using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

public class LocateTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;
    private readonly ScopeCatalog _scopeCatalog;
    private readonly LocalizationIndex? _localization;
    private readonly ConditionalFolders _conditional;

    public LocateTool(
        SourceIndexer sourceIndexer,
        DefIndexer defIndexer,
        ScopeCatalog scopeCatalog,
        LocalizationIndex? localization = null,
        ConditionalFolders? conditional = null)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
        _localization = localization;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__locate";

    // scope / limit 两族取 ScopeAndLimitArgs 的名单，不再在这里各抄一遍：抄漏的 `max` 与 `top`
    // 读得进来却被报成被忽略，同一份返回自相矛盾。
    public IEnumerable<string> ExtraAcceptedKeys =>
        [.. ScopeAndLimitArgs.ScopeKeys, .. ScopeAndLimitArgs.LimitKeys, "query", "name", "symbol", "pattern", "search", "term"];

    public string Description =>
        "Fuzzy name lookup: turns a partial or misspelled name into the exact C# type / member / XML def / file " +
        "name that other tools require — the only tool that accepts approximate input. " +
        "Results are split into C# Types, Members, XML Defs and Content Matches (defs matched on a field value rather " +
        "than on their name), each section capped by limit and folded independently, plus a Files section of indexed " +
        "paths — fuzzy when the other four come back empty, otherwise just the file whose name matches the query " +
        "exactly. " +
        "A section header reading 'N of M' means the listing was cut and M is the scope's total; a bare 'N' means " +
        "the listing is that section's complete set; 'at least M' means M is a floor rather than the total, which " +
        "only happens when the member search matches more name keys than the server expands in one call — a trailing " +
        "note then states that cap, and narrowing the query gives an exact count. " +
        // 每行尾巴那个 (N%) 此前一处没成文，而 F32 之后它是唯一能把「叫这个名字」和「像这个
        // 名字」分开的记号：`method:CompTick` 的 200 里有 56 条是 90 分前缀命中
        // （CompTickRare / CompTickInterval / CompTickLong），叫 CompTick 的方法只有 144，
        // 而默认 limit 印出来的前 10 条恰好全是 100%——混合在默认返回里零痕迹。
        // 判官还提议把 Members 表头拆成 `144 exact-name, 56 approximate`。不做：那个拆分只能按
        // score==100 算，而成员分是 baseScore + keywordBonus 封顶 100（SourceIndexer 的
        // `Math.Min(matchCount - 1, 5) * 10`），两个以上关键词时一个 90 分的前缀命中也能被推到
        // 100——拆出来的「exact-name」于是是个假数。写进返回里的读法只能取恒真的那一半方向。
        // 限定必须前置。原先是「100% 以下都是近名」立完规则、下一句才用从句把它推翻，而那个
        // 限定条件（两个以上关键词）藏在从句里——读者已经先把规则收下了。
        // 「怎么判」也要就地给：只说 method:/field: 不保证名字精确、不说拿什么判，等于把一个
        // 已经答得出的问题留成悬案，第十轮盲测据此多绕了一轮。
        // 「每一行都带 (N%)」是个全称承诺，而 Content Matches 是它唯一的反例：那一段的排序键是
        // 关键词命中计数（DefIndexer 传 scoreGap: null），不是 0~100 的相似度，故整段无分数、
        // 表头也永远没有 (K at 100%)。第十一轮盲测里调用方据此答不出「几条是逐字精确的」——
        // 而这个问题服务端本来就答得出：内容索引按整词建键（WordSplitRegex `\W+`，下划线属 \w，
        // 故 Apparel_ShieldBelt 保持为一个 token），查询走全等查表，**每一条内容命中按构造就是
        // 整词、不区分大小写的逐字命中**。不是答不了，是从没写出来过。
        // 这句恒真，故只能进描述——印进返回就是常亮。
        "Every row in the C# Types, Members, XML Defs and Files sections carries its match score as (N%); " +
        "Content Matches rows carry none and that section never reports '(K at 100%)' — a content match is " +
        "a whole-word, case-insensitive hit on a field name or on one word of a field value, so every one " +
        "listed is already literal, and the section is ranked by how many of the query's keywords a def " +
        "matched rather than by name similarity. " +
        "For a single-keyword query 100% does mean an " +
        "identical name; only a query of two or more keywords can push a near-name up to 100% in the member " +
        "sections, where 100% is then the top score rather than a guarantee of identical spelling. " +
        "Anything below 100% is a near-name match and not the name that was asked for, so a section total " +
        "counts those too — when it does, the header says how many of that total are exact, as '(K at 100%)'. " +
        "This holds for method: and field: as well — they restrict the search to members, they do not make " +
        "the name match exact; read the (N%) on each row to tell an exact name from a near one. " +
        // 「有几个方法叫 X」是这一段最典型的用途，而行数答的不是那个问题：第十轮盲测里
        // FleckStatic 的两个 Draw 重载在返回里是一行，调用方多跑一次 inspect 才发现，并因此
        // 对自己已经闭合的证据失去信心。这件事在 schema、README、返回三处都没有写过。
        "Member rows are deduplicated by declaring type + member name + kind + file, so same-named overloads " +
        "collapse into one row: the count is of member names, not of declarations — inspect the type to see " +
        "how many overloads a row stands for. " +
        SourceLabeling.Contract + " " +
        ConditionalReport.Contract + " " +
        "Filters go inside the query: type:, method:, field:, def:, and scope: as an alias for the scope parameter.";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                minLength = 1,
                description =
                    // 四个过滤前缀在参数说明里各出一次。原先只举了 def: 与 method:，于是 type:
                    // 只活在长描述靠后的一句里——第十三轮盲测里被测方写下「'type:' 是我照猫画虎
                    // 试的」，并为此多跑了一次对照调用。举例本身就是接口（R77 那条的同一判据）。
                    "Search text or filtered query. Examples: 'Apparel_ShieldBelt', 'RimWorld.Pawn', "
                    + "'def:Apparel_ShieldBelt', 'type:CompShield', 'method:CompTick', 'field:energy'."
            },
            scope = ScopeAndLimitArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeAndLimitArgs.LimitSchemaProperty()
        },
        required = new[] { "query" }
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__locate",
        "query (search text, optionally filtered: 'def:Apparel_ShieldBelt', 'method:CompTick'). Aliases accepted: name, symbol, pattern, search.",
        "query (required), scope, limit.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var rawQuery = ToolArgs.GetRequiredFuzzyString(
            args, ArgSpec, "query", "name", "symbol", "pattern", "search", "term");

        cancellationToken.ThrowIfCancellationRequested();

        var query = QueryParser.Parse(rawQuery);

        // 'scope:xxx' 混进 query 是必然会发生的（调用方已经在用 type:/def: 前缀），
        // 这里把它当作 scope 参数吸收，而不是让它变成一个搜不到东西的关键词。
        var scope = query.ScopeFilter != null
            ? _scopeCatalog.Resolve(query.ScopeFilter)
            : ScopeAndLimitArgs.Resolve(_scopeCatalog, args);
        var limit = ScopeAndLimitArgs.GetDisplayLimit(args);

        var report = new ScopeReport();

        // 一段就是一个 LocateSection。Tally 那一格、段头的来源构成、段末的折叠行、以及
        // 「走不走零命中路径」全从这张表派生——此前它们是四件在每段里各写一遍的事。
        var sections = new List<LocateSection>();

        // C# Types 段列过的名字。文件段用它去重：类型 `CompShield` 与文件 CompShield.cs 是
        // 同一个东西的两种写法，两段各列一次只是把同一条结果说两遍。
        var shownTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 条件加载目录：五段共用一份，成因在末尾统一说（见 ConditionalReport）
        var conditional = new ConditionalReport(_conditional);

        if (query.TypeFilter != null || (string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter) && string.IsNullOrEmpty(query.DefFilter)))
        {
            var typeSearchTerm = query.TypeFilter ?? QueryParser.GetCombinedSearchTerm(query);
            // 短名/全名的合并在索引层完成（见 SourceIndexer.CollapseNameAliases）——那里是截断
            // 之前，计数才对得上；在这里折叠只会把已经被 limit 砍过的一批再去一次重。
            var types = _sourceIndexer.FuzzySearchTypes(typeSearchTerm, scope, limit.Count);
            report.Add(types, CountedNoun.CSharpTypes);

            if (types.Items.Count > 0)
            {
                var rows = new List<LocateRow>();
                foreach (var entry in types.Items)
                {
                    var paths = _sourceIndexer.GetPathsByType(entry.Item);
                    shownTypeNames.Add(entry.Item);
                    rows.Add(new LocateRow(
                        $"- `{entry.Item}` ({entry.Score:F0}%){FileNote(entry.Item, paths)}"
                        + conditional.TagAll(paths),
                        entry.SourceName));
                }

                sections.Add(Section(types, CountedNoun.CSharpTypes, rows));
            }
        }

        if (query.MethodFilter != null || query.FieldFilter != null || query.Keywords.Count > 0)
        {
            var keywords = new List<string>();
            if (query.MethodFilter != null) keywords.Add(query.MethodFilter);
            if (query.FieldFilter != null) keywords.Add(query.FieldFilter);
            keywords.AddRange(query.Keywords);

            // 前缀承诺的种类必须一路带到取回层。README 写着 field: =「只搜字段/属性」、
            // method: =「只搜方法」，而这两个前缀此前只起到「关掉 C# Types / XML Defs 两段」
            // 的作用——取回不分种类，于是 field:Tick 的结果里方法把配额吃光，字段只剩 1 条。
            var memberKinds = new List<string>();
            if (query.MethodFilter != null) memberKinds.Add("Method");
            if (query.FieldFilter != null) { memberKinds.Add("Field"); memberKinds.Add("Property"); }

            // 成员按 method/property/field 分组显示、各组轮流占配额，故取回要比 limit 多一些：
            // 只取回 limit 条的话，分数最高的那一批可能全是同一类，轮流也就无从轮起。
            // Scale 放大后仍夹在服务端硬上限内
            var members = _sourceIndexer.SearchMembersByKeywords(
                keywords.ToArray(), scope, limit.Scale(3).Count, memberKinds);
            report.Add(members, CountedNoun.Members);

            if (members.Items.Count > 0)
            {
                // limit 是**这一段的上限**（schema 写的 result cap per section），故按总量切。
                // 原先是 perGroup = max(3, limit/2)、每个种类各切一份，于是同一个 limit 既不是
                // 上限也不是下限：`energy` limit:10 列出 13 条（Properties 5 + Fields 5 +
                // Methods 3），而 `method:CompTick` limit:10 只列 5 条——单一种类只拿得到一份配额。
                var groupedMembers = members.Items.GroupBy(m => m.Item.MemberType).ToList();
                var shownGroups = TakeRoundRobin(groupedMembers, limit.Unlimited ? int.MaxValue : limit.Count);

                var rows = new List<LocateRow>();
                foreach (var (kind, groupItems) in shownGroups)
                {
                    // 子组标题不是结果行：它不挂来源标签，也不参与「这批行是不是同源」的判定
                    rows.Add(new LocateRow($"  {Plural(kind)}:", IsGroupHeader: true));
                    foreach (var entry in groupItems)
                    {
                        var (typeName, memberName, _, filePath) = entry.Item;
                        rows.Add(new LocateRow(
                            $"  - `{typeName}.{memberName}` ({entry.Score:F0}%)"
                            + $"{FileNote(typeName, [filePath])}{conditional.Tag(filePath)}",
                            entry.SourceName));
                    }
                }

                // 成员分是 baseScore + keywordBonus 封顶 100（SourceIndexer 里那个
                // `Math.Min(matchCount - 1, 5) * 10`），故**多关键词**查询里一条 90 分的前缀
                // 命中也能被推到 100——那时「100% 的有几条」是个假数，第九轮正是据此驳回了
                // 「把表头拆成 exact-name / approximate」的提议。单关键词时 matchCount 恒为 1、
                // bonus 恒为 0，100 分就真的是逐字相同，这个数才敢印。
                // 描述里那句「两个以上关键词时 100% 只是最高分」说的是同一件事，两处判据同源。
                var memberFullScore = keywords.Count == 1 ? members.FullScoreCount : -1;

                sections.Add(new LocateSection
                {
                    Noun = CountedNoun.Members,
                    Rows = rows,
                    // 这一段的截断是两层的（ScopeFilter 的 limit 加每组配额），故显示数要另数：
                    // Rows 里还混着子组标题，ScopedResult 也只看得见第一层。
                    Shown = shownGroups.Sum(g => g.Items.Count),
                    Total = members.TotalInScope,
                    TotalIsLowerBound = members.TotalIsLowerBound,
                    LowerBoundNotice =
                        $"\n\n_The member search matched more than {SourceIndexer.MemberQualifiedKeyCap} name keys and "
                        + "expanded only that many, so the member total above is a floor rather than the total "
                        + "(server expansion cap; no parameter widens it). Narrow the query for an exact count._",
                    FullScoreCount = memberFullScore,
                    SourcesInScope = members.SourcesInScope,
                    TruncatedByScoreGap = members.TruncatedByScoreGap,
                    // 折叠行按 TotalInScope 计数。原先每组各打一行、只数「取回的这批里还剩几条」，
                    // 而取回本身已被 limit.Scale(3) 砍过：method:CompTick 因此报 +25，实际有 186 条。
                    TruncatedByLimit = true,
                });
            }
        }

        if (query.DefFilter != null || (string.IsNullOrEmpty(query.TypeFilter) && string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter)))
        {
            var defSearchTerm = query.DefFilter ?? QueryParser.GetCombinedSearchTerm(query);
            var defs = _defIndexer.FuzzySearch(defSearchTerm, scope, limit.Count);
            report.Add(defs, CountedNoun.XmlDefs);

            if (defs.Items.Count > 0)
            {
                var rows = new List<LocateRow>();
                foreach (var entry in defs.Items)
                {
                    var def = entry.Item;
                    var abstractTag = def.IsAbstract ? " [Abstract]" : "";
                    var label = !string.IsNullOrEmpty(def.Label) ? $" \"{def.Label}\"" : "";

                    // 译名接在英文 label 后面。locate 只给 label——description 长一到两个数量级，
                    // 一屏几十条结果每条都带上就没法看了，那是 inspect 的事。
                    var localized = _localization?.Lookup(def.DefType, def.DefName)?.Label;
                    var localizedTag = !string.IsNullOrEmpty(localized) ? $" / {localized}" : "";

                    // def 行不印文件路径（R20），故这个标记是整段里唯一能看出「这条 def 来自
                    // 一个条件目录」的地方——而 vanilla 的 def 与条件补丁包里的 def 在这一行上
                    // 逐字同形（HAR 的 1.6/Mods/Ideology 就落在默认 scope 'base' 里）。
                    rows.Add(new LocateRow(
                        $"- `{def.DefName}` ({entry.Score:F0}%) - {def.DefType}{abstractTag}{label}{localizedTag}"
                        + conditional.Tag(def.FilePath),
                        entry.SourceName));
                }

                sections.Add(Section(defs, CountedNoun.XmlDefs, rows));
            }

            if (query.Keywords.Count > 0)
            {
                var defsByContent = _defIndexer.SearchByContent(query.Keywords.ToArray(), scope, limit.Count);
                report.Add(defsByContent, CountedNoun.ContentMatches);

                if (defsByContent.Items.Count > 0)
                {
                    var rows = new List<LocateRow>();
                    foreach (var entry in defsByContent.Items)
                    {
                        var (location, matchedFields) = entry.Item;
                        var fieldSummary = string.Join(", ", matchedFields.Take(3));
                        var moreFields = matchedFields.Count > 3 ? $" +{matchedFields.Count - 3}" : "";
                        // 语序而非记号：原先是 `- \`名字\` - 字段路径`，与其余四段的
                        // `- \`命中项\` (N%) - 附注` 逐字同形。但那四段行首是**被查中的东西**，
                        // 这一段行首是**装着那个字段值的宿主 def**——同一处版面位置在同一份返回里
                        // 表示两种关系，返回里没有任何记号区分。第十一轮盲测里被测方是靠
                        // tools/list 的描述补出这层语义的，它甚至把出处记成了「返回开头」；
                        // 只盯返回的调用方拿不到这条，最自然的读法就是「一个名字近似命中的 def」。
                        // 改成 `字段路径 in \`名字\``，让语序自己说清谁装着谁，净增一个字符，
                        // 名字仍是行内唯一的反引号项，复制给 inspect 照样能取。
                        rows.Add(new LocateRow(
                            $"- {fieldSummary}{moreFields} in `{location.DefName}`"
                            + conditional.Tag(location.FilePath),
                            entry.SourceName));
                    }

                    sections.Add(Section(defsByContent, CountedNoun.ContentMatches, rows));
                }
            }
        }

        // 文件名是 locate 的一等查询目标（README 的「支持内容」头一条就列着它），但这一段原先
        // 只在其余四段全部零命中时才跑。于是查一个确实在索引里的文件名——'Bodies_Humanlike'——
        // 只要顺带蹭到一条 38 分的无关 def，整段就被吞掉，返回读起来是「索引里没有这个文件」。
        // 现在分两种触发：零命中时它仍是兜底（模糊列出若干条），有命中时只补名字完全一致的
        // 那一份，不把每次查询都拖长一段模糊文件名。
        //
        // 「其余四段全空」判据只有一处：sections 是不是还空着。
        var wantsFileFallback = sections.Count == 0;
        var hasFilterPrefix = query.TypeFilter != null || query.MethodFilter != null
                              || query.FieldFilter != null || query.DefFilter != null;

        // 调用方**显式打了扩展名**，那就是在问文件、不是在问类型。
        var queryIsFileName = LooksLikeIndexedFileName(rawQuery);

        // F31 立的判据是「显式带扩展名 = 在问文件」，但只做了命中那一半。零命中时这一段
        // **整个不印**，返回里只剩别的段落对同一个查询串做的模糊命中——实测
        // `locate 'LoadFolders.xml' scope:'all'` 回的是「1 C# type: LoadFolder (37%)」，
        // 全篇没有一个字说索引里没有叫这个名字的文件。名字几乎相同的那条被读成「找到了」，
        // 调用方据此以为条件加载信息可查（真值：loadFolders.xml 不是 .cs/.xml 内容文件，不进索引）。
        // 兜底那一支同样要说：它列出来的是模糊文件名命中，而 Files 段的行**不带分数**，
        // 于是一条 40 分的近名文件与一条精确命中在版面上逐字同形。
        var exactFileMissing = false;

        if (wantsFileFallback || !hasFilterPrefix)
        {
            var files = _sourceIndexer.Search(rawQuery, scope, limit.Count);

            // 打了扩展名就走精确查表。模糊那条路是拿查询串跟**去掉扩展名**的基名比分的，
            // 于是 `Pawn.cs` 对 `Pawn` 编辑距离恒为 3、短名直接 0 分出局
            // （判据与实测见 SourceIndexer.GetPathsByFileName）。
            var exactFiles = queryIsFileName
                ? _sourceIndexer.GetPathsByFileName(rawQuery, scope, limit.Count)
                : ScopedResult<string>.Empty;

            exactFileMissing = queryIsFileName && exactFiles.Items.Count == 0;

            // 精确补充时只留「基名与查询词逐字相同」的那些，并去掉已在 C# Types 段出现过的
            // 同名项（类型 CompShield 与文件 CompShield.cs 是同一件事的两种写法）。
            //
            // 两道都只在调用方**没**说清要哪一个时才成立：
            //   比较层——带扩展名时左边去了扩展名、右边没去，恒不等；
            //   去重层——反编译产物的文件名逐个对应类型名，故查一个 .cs 文件名时同名类型
            //           必然已在 C# Types 段列过，去重必然命中。
            // 两道叠加，`.cs` 文件名查询的 Files 段在结构上永远不可达（`.xml` 之所以正常，
            // 只是因为 XML 文件名通常不是类型名，走的是下面零命中兜底那一支）。
            var items = wantsFileFallback
                ? WithExactFilesFirst(files.Items, exactFiles.Items)
                : queryIsFileName
                    ? exactFiles.Items.ToList()
                    : files.Items
                        .Where(entry => string.Equals(
                            Path.GetFileNameWithoutExtension(entry.Item), rawQuery, StringComparison.OrdinalIgnoreCase))
                        .Where(entry => !shownTypeNames.Contains(Path.GetFileNameWithoutExtension(entry.Item)))
                        .ToList();

            if (items.Count > 0)
            {
                // footer 的落选计数只在真的列出这一段时才计入，否则「补一条精确文件」会顺带
                // 把几十条模糊文件命中的 out-of-scope 计数灌进去，脚注的数字就不再对应正文。
                // 精确补充那一支计的是精确查表自己的落选数，两份不相加——相加会把同一条路径
                // 数两遍。
                report.Add(!wantsFileFallback && queryIsFileName ? exactFiles : files, CountedNoun.Files);

                // 这一段的总数：兜底那一支列出来的是模糊结果（可能被 limit 砍过），故总数是
                // 「列出的 + 被砍掉的」；精确补充那一支本来就只列同名的那几条，没有被砍的。
                //
                // 原先两个位置都传 items.Count，于是 total == shown 恒成立，表头**永远**写不出
                // `of`——而同一段下面照样印着 `... +43 more files`。README 把 `<数> of <数>` 定成
                // 截断记号，调用方读到的却是「5 files」加一行「还有 43 条」，两句在同一屏里
                // 互相否定。
                var fileTotal = wantsFileFallback ? items.Count + files.HiddenCount : items.Count;

                // 这一段里名字逐字相同的有几条。带扩展名的查询走精确查表，那份结果**整份**都是
                // 逐字同名（GetPathsByFileName 用 Path.GetFileName 比较、分数恒 100），故直接取
                // 它的在域总数；模糊那支的 100 分同样只在基名与查询串逐字相同时给出。
                // 不这么算的话 `RangedIndustrial.xml` 的表头是干净的 `4 files`——按 F30 的契约
                // 读作「完整集」，而它确实是完整集，只是其中一半根本不叫这个名字。
                var fileFullScore = queryIsFileName ? exactFiles.TotalInScope : files.FullScoreCount;

                var rows = items.Select(entry => new LocateRow(
                    // 原先是「基名 - 全路径」，而基名逐字包含在全路径的末尾，说的是同一件事。
                    // 分数不能省：描述里立着「每一行都带 (N%)」这条契约，而这一段此前整段没有，
                    // 于是调用方按契约去找判别器、找不到，最省事的反推就是「这段都是 100%」——
                    // 一条 40 分的近名文件与一条精确命中在版面上逐字同形，正好推向那个错数。
                    $"- {entry.Item} ({entry.Score:F0}%)" + conditional.Tag(entry.Item),
                    entry.SourceName)).ToList();

                sections.Add(new LocateSection
                {
                    Noun = CountedNoun.Files,
                    Rows = rows,
                    Shown = items.Count,
                    Total = fileTotal,
                    FullScoreCount = fileFullScore,
                    // 段头的构成数的是 files.TotalInScope——WithExactFilesFirst 往 items 里补进过
                    // 模糊结果之外的精确命中时，构成会比 fileTotal 少那几条。加起来对不上的构成
                    // 不如不印：它自证的本事全在「各源之和恰好等于表头那个总数」上。
                    SourcesInScope = wantsFileFallback && items.Count == files.Items.Count
                        ? files.SourcesInScope
                        : [],
                    TruncatedByScoreGap = files.TruncatedByScoreGap,
                    TruncatedByLimit = files.TruncatedByLimit,
                    // 折叠行只对兜底那一支有意义：精确补充本来就只列同名的那几条，没有「还有更多」。
                    Foldable = wantsFileFallback,
                });
            }
        }

        // 查询串里那些没被当成过滤器用的前缀必须说出来。'member:CompTick' 回一句 "No results"
        // 而 'method:CompTick' 有 144 条——同一个符号，一个说不存在、一个说有一百多处。
        // 差别全在那个没被识别的前缀上，而调用方在返回里看不到任何线索。
        var prefixNotice = new StringBuilder();
        if (query.UnknownPrefixes.Count > 0)
        {
            var names = string.Join(", ", query.UnknownPrefixes.Distinct().Select(p => $"'{p}:'"));
            prefixNotice.Append(
                $"\n\n_{names} is not a query filter, so it was matched as ordinary search text. "
                + "Known filters: type:, method:, field:, def:, scope:._");
        }
        if (query.HadEmptyFilterValue)
        {
            prefixNotice.Append(
                "\n\n_A filter prefix was given with nothing after it and was ignored. "
                + "Write the term right after the colon (type:CompShield); a space after the colon is fine too._");
        }

        // 字段内容索引有一条建键下限，低于它的词从没进过索引，故用它查内容命中恒为空——
        // 而 Content Matches 段是**整段不出现**，与「查过了、零命中」在版面上逐字同形。
        // 第十二轮盲测：`Plants_Wild.xml` 里实打实有六处 `<li>20</li>`，`locate('20')` 的
        // 返回里连那个段头都没有；被测方是自费跑了一整套对照实验（查 '22'、'14'、'200'、
        // '1200'，再 inspect 一个 def 拿真值回查）才判出这是盲区而不是空集。
        // 这一句只声明「没查」，不声明「有没有」——后者要 search_regex，返回给出出路即可。
        // 不能改成让 Content Matches 恒印 `0 content matches`：那恰好把这个错误结论固化成契约，
        // 而且是真正的常亮。只在调用方真传了短词时印。
        var shortKeywords = query.Keywords
            .Where(k => k.Length < DefIndexer.MinContentTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 零命中是一个正常结果，不是调用失败。isError 留给「工具没能执行」，置 true 会让 client
        // 把这次搜索当成故障去重试或上报；同一个服务器里 trace 查不到子类、search_regex 零命中
        // 都是 false，locate 独自为 true 只会让调用方两套判据。
        return Task.FromResult(new ToolResult(LocateRenderer.Render(new LocateOutput
        {
            Query = rawQuery,
            Scope = scope,
            Sections = sections,
            EmptyLine = $"No results for '{ToolArgs.ForEcho(rawQuery)}' in scope '{scope.Expression}'.",
            Limit = limit,
            OutOfScope = report,
            Conditional = conditional,
            // 措辞点明「上面那些是按查询串比名字比出来的」，因为这一句唯一要防的误读就是
            // 把近名结果当成那个文件本身。
            MissingFile = exactFileMissing
                ? $"\n\n_No indexed file is named '{ToolArgs.ForEcho(rawQuery)}' in scope '{scope.Expression}'; "
                  + "anything listed above matched the query as a name, not as that file._"
                : null,
            ShortTokens = shortKeywords.Count > 0
                ? $"\n\n_{string.Join(", ", shortKeywords.Select(k => $"'{ToolArgs.ForEcho(k)}'"))} "
                  + $"{(shortKeywords.Count == 1 ? "is" : "are")} shorter than "
                  + $"{DefIndexer.MinContentTokenLength} characters, and the field-value index only holds tokens "
                  + "of that length or more — no def field was searched for "
                  + $"{(shortKeywords.Count == 1 ? "it" : "them")}. A missing Content Matches section here means "
                  + "'not searched', not 'not present'; search_regex matches short literals._"
                : null,
            PrefixNotice = prefixNotice.Length > 0 ? prefixNotice.ToString() : null,
            FilterListAlreadyShown = query.UnknownPrefixes.Count > 0,
            // 拼错的 scope 被静默退回全域，每条返回路径都要带上这行，否则调用方拿着全域结果
            // 却以为自己限定过范围。表头在全域时不打 scope 标注，正是这种情况下最没痕迹的地方。
            ScopeNotice = ScopeNotices.Unresolved(_scopeCatalog, scope),
        })));
    }

    // 五段里有四段的形状是一样的：显示数、总数、满分数、来源构成、两种截断标记全取自
    // 同一份 ScopedResult。只有 Members（两层截断）与 Files（两支不同的总数算法）要自己拼。
    private static LocateSection Section<T>(
        ScopedResult<T> result, CountedNoun noun, IReadOnlyList<LocateRow> rows)
        => new()
        {
            Noun = noun,
            Rows = rows,
            Shown = result.Items.Count,
            Total = result.TotalInScope,
            TotalIsLowerBound = result.TotalIsLowerBound,
            FullScoreCount = result.FullScoreCount,
            SourcesInScope = result.SourcesInScope,
            TruncatedByScoreGap = result.TruncatedByScoreGap,
            TruncatedByLimit = result.TruncatedByLimit,
        };

    // 各组轮流取一条，直到取满 budget 或全部取完。组的先后与组内次序都保持传入时的样子——
    // 那是取回层排好的（分数 → 名字长度 → 宿主类型 → 成员名 → 文件），这里只负责切配额。
    //
    // 轮流而不是「按顺序装满一组再装下一组」：后者会让第一类把配额吃光，而那正是 F10 当初
    // 把 kind 过滤推到取回层要防的事。带前缀的查询只有一类，轮流退化成顺序取，正好拿满 limit。
    // 取空的组不返回，否则会印出一个底下一条都没有的 `Fields:` 标题。
    private static List<(string Kind, List<T> Items)> TakeRoundRobin<T>(
        List<IGrouping<string, T>> groups,
        int budget)
    {
        var pools = groups.Select(group => group.ToList()).ToList();
        var taken = groups.Select(group => (Kind: group.Key, Items: new List<T>())).ToList();

        var remaining = budget;
        for (var round = 0; remaining > 0; round++)
        {
            var progressed = false;
            for (var i = 0; i < pools.Count && remaining > 0; i++)
            {
                if (round >= pools[i].Count) continue;
                taken[i].Items.Add(pools[i][round]);
                remaining--;
                progressed = true;
            }

            if (!progressed) break;
        }

        return taken.Where(group => group.Items.Count > 0).ToList();
    }

    // 索引只收 .cs / .xml（SourceIndexer.CollectFilesIterative 扫描时的判据），故只认这两种扩展名。
    // 不能用 Path.GetExtension 泛判——`Verse.AI.Pawn` 的「扩展名」会被算成 `.Pawn`，
    // 而带命名空间的全名查询是 locate 的一等输入。
    private static bool LooksLikeIndexedFileName(string query) =>
        query.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || query.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    // 零命中兜底那一支**不改**：模糊列出若干条正是它的用途（查 `Bodies_Humanlike.xml` 会顺带
    // 给出 `Races_Humanlike.xml`，那是有用的）。这里只把精确命中补进最前面——它可能因为 0 分
    // 根本没进模糊结果，而「索引里到底有没有这个文件」是这一段唯一必须答对的事。
    // 折叠行仍按模糊那份的 HiddenCount 算：补进来的是模糊结果**之外**的条目，总数与已列出数
    // 同增，差值不变。
    //
    // 去重保留的必须是**精确**那一份。原先反过来（在模糊结果里出现过就跳过精确那条），于是
    // 一条真正逐字同名的文件在列表里带的是模糊分——`RangedIndustrial.xml` 的两条精确命中都在
    // 模糊结果里，两条都会印成 40% 上下，而表头写着「2 at 100%」，同一份返回里两处互相否定。
    // 行不带分数时这道错误看不出来（两支的行文本逐字相同），补上 (N%) 之后它就成了硬伤。
    private static List<ScopedEntry<string>> WithExactFilesFirst(
        IReadOnlyList<ScopedEntry<string>> fuzzy, IReadOnlyList<ScopedEntry<string>> exact)
    {
        if (exact.Count == 0) return fuzzy.ToList();

        var exactPaths = new HashSet<string>(exact.Select(e => e.Item), StringComparer.OrdinalIgnoreCase);
        var items = exact.ToList();
        items.AddRange(fuzzy.Where(e => !exactPaths.Contains(e.Item)));
        return items;
    }

    // 判据与 trace 共用（见 SymbolRow）：文件名推得出来就不印。locate 用 ` - 名字` 的破折号写法，
    // trace 用括号写法，共享的是「什么时候印」而不是「怎么排版」。
    private static string FileNote(string typeName, IReadOnlyList<string> paths)
    {
        var note = SymbolRow.FileNote(typeName, paths);
        return note.Length == 0 ? string.Empty : $" - {note.Trim().Trim('(', ')')}";
    }

    // MemberType 来自索引层，取值是 Method / Property / Field / Event / EnumMember。
    // 直接加 's' 会写出 'Propertys'。
    private static string Plural(string memberType) =>
        memberType.EndsWith('y') ? $"{memberType[..^1]}ies" : $"{memberType}s";

}
