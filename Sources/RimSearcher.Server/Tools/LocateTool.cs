using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class LocateTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;
    private readonly ScopeCatalog _scopeCatalog;
    private readonly LocalizationIndex? _localization;

    public LocateTool(
        SourceIndexer sourceIndexer,
        DefIndexer defIndexer,
        ScopeCatalog scopeCatalog,
        LocalizationIndex? localization = null)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
        _localization = localization;
    }

    public string Name => "rimworld-searcher__locate";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "name", "symbol", "search", "term", "maxResults", "count", "scopes", "source", "sources", "mod", "mods", "in"];

    public string Description =>
        "Fuzzy name lookup: turns a partial or misspelled name into the exact C# type / member / XML def / file " +
        "name that other tools require — the only tool that accepts approximate input. " +
        "Results are split into C# Types, Members, XML Defs and Content Matches (defs matched on a field value rather " +
        "than on their name), each section capped by limit and folded independently, plus a Files section of indexed " +
        "paths — fuzzy when the other four come back empty, otherwise just the file whose name matches the query " +
        "exactly. " +
        "A section header reading 'N of M' means the listing was cut and M is the scope's total; a bare 'N' means " +
        "the listing is that section's complete set. " +
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
                    "Search text or filtered query. Examples: 'Apparel_ShieldBelt', 'RimWorld.Pawn', 'def:Apparel_ShieldBelt', 'method:CompTick'."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeArgs.LimitSchemaProperty()
        },
        required = new[] { "query" }
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__locate",
        "query (search text, optionally filtered: 'def:Apparel_ShieldBelt', 'method:CompTick'). Aliases accepted: name, symbol, pattern, search.",
        "query (required), scope, limit.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var rawQuery = ToolArgs.GetRequiredFuzzyString(args, ArgSpec, "query", "name", "symbol", "pattern", "search");

        cancellationToken.ThrowIfCancellationRequested();

        var query = QueryParser.Parse(rawQuery);

        // 'scope:xxx' 混进 query 是必然会发生的（调用方已经在用 type:/def: 前缀），
        // 这里把它当作 scope 参数吸收，而不是让它变成一个搜不到东西的关键词。
        var scope = query.ScopeFilter != null
            ? _scopeCatalog.Resolve(query.ScopeFilter)
            : ScopeArgs.Resolve(_scopeCatalog, args);
        var limit = ScopeArgs.GetDisplayLimit(args);

        // 拼错的 scope 被静默退回全域，每条返回路径都要带上这行，
        // 否则调用方拿着全域结果却以为自己限定过范围。表头在全域时不打 scope 标注，
        // 正是这种情况下最没痕迹的地方。
        var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

        var report = new ScopeReport();

        // 表头要报出「各段各几条」，而那要等各段都跑完才知道，所以正文先攒在 sb 里、
        // 表头最后再拼到前面。trace / search_regex / list_directory 的头一行都是
        // 「什么 + 多少条 + 什么 scope」，locate 此前只有「什么」，读者得自己数行。
        var sb = new StringBuilder();
        var tally = new List<string>();

        // 各段落自己置位。曾用 sb.Length 与表头长度比大小来推断，窄 scope 下表头恰好比
        // 阈值长，零命中也会被判成有结果——「查不到就提示换 scope」那条路径因此永远走不到。
        var hasResults = false;

        // C# Types 段列过的名字。文件段用它去重：类型 `CompShield` 与文件 CompShield.cs 是
        // 同一个东西的两种写法，两段各列一次只是把同一条结果说两遍。
        var shownTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (query.TypeFilter != null || (string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter) && string.IsNullOrEmpty(query.DefFilter)))
        {
            var typeSearchTerm = query.TypeFilter ?? QueryParser.GetCombinedSearchTerm(query);
            // 短名/全名的合并在索引层完成（见 SourceIndexer.CollapseNameAliases）——那里是截断
            // 之前，计数才对得上；在这里折叠只会把已经被 limit 砍过的一批再去一次重。
            var types = _sourceIndexer.FuzzySearchTypes(typeSearchTerm, scope, limit.Count);
            report.Add(types);

            if (types.Items.Count > 0)
            {
                hasResults = true;
                tally.Add(Count(types.Items.Count, types.TotalInScope, "C# types"));
                var typeLabels = ScopeArgs.SourceLabeling.Of(types.Items.Select(e => e.SourceName));
                sb.AppendLine($"\n**C# Types**{typeLabels.Header}:");
                foreach (var entry in types.Items)
                {
                    var paths = _sourceIndexer.GetPathsByType(entry.Item);
                    shownTypeNames.Add(entry.Item);
                    sb.AppendLine(
                        $"- `{entry.Item}` ({entry.Score:F0}%){FileNote(entry.Item, paths)}{typeLabels.Row(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(types, "C# types", limit: limit);
                if (fold != null) sb.AppendLine(fold);
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
            report.Add(members);

            if (members.Items.Count > 0)
            {
                hasResults = true;
                var tallySlot = tally.Count;
                tally.Add("");

                // limit 是**这一段的上限**（schema 写的 result cap per section），故按总量切。
                // 原先是 perGroup = max(3, limit/2)、每个种类各切一份，于是同一个 limit 既不是
                // 上限也不是下限：`energy` limit:10 列出 13 条（Properties 5 + Fields 5 +
                // Methods 3），而 `method:CompTick` limit:10 只列 5 条——单一种类只拿得到一份配额。
                var groupedMembers = members.Items.GroupBy(m => m.Item.MemberType).ToList();

                // 来源标签按**实际列出的那些行**判，故先把分组配额切出来再写表头
                var shownGroups = TakeRoundRobin(groupedMembers, limit.Unlimited ? int.MaxValue : limit.Count);
                var shown = shownGroups.Sum(g => g.Items.Count);

                var memberLabels = ScopeArgs.SourceLabeling.Of(
                    shownGroups.SelectMany(g => g.Items).Select(e => e.SourceName));
                sb.AppendLine($"\n**Members**{memberLabels.Header}:");

                foreach (var (kind, groupItems) in shownGroups)
                {
                    sb.AppendLine($"  {Plural(kind)}:");
                    foreach (var entry in groupItems)
                    {
                        var (typeName, memberName, _, filePath) = entry.Item;
                        sb.AppendLine(
                            $"  - `{typeName}.{memberName}` ({entry.Score:F0}%)"
                            + $"{FileNote(typeName, [filePath])}{memberLabels.Row(entry.SourceName)}");
                    }
                }

                tally[tallySlot] = Count(shown, members.TotalInScope, "members");

                // 折叠行放在整段末尾、按 TotalInScope 计数。原先每组各打一行、只数「取回的这批里
                // 还剩几条」，而取回本身已被 limit.Scale(3) 砍过：method:CompTick 因此报 +25，
                // 实际有 186 条。组内那行还漏了「怎么拿到更多」，调用方连能展开都不知道。
                var memberFold = ScopeArgs.FoldLine(
                    Math.Max(0, members.TotalInScope - shown),
                    shown,
                    members.TruncatedByScoreGap,
                    truncatedByLimit: true,
                    // 「members」而非某一类：这行数的是 method/property/field 三类之和
                    noun: "members",
                    indent: "  ",
                    limit: limit);
                if (memberFold != null) sb.AppendLine(memberFold);
            }
        }

        if (query.DefFilter != null || (string.IsNullOrEmpty(query.TypeFilter) && string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter)))
        {
            var defSearchTerm = query.DefFilter ?? QueryParser.GetCombinedSearchTerm(query);
            var defs = _defIndexer.FuzzySearch(defSearchTerm, scope, limit.Count);
            report.Add(defs);

            if (defs.Items.Count > 0)
            {
                hasResults = true;
                tally.Add(Count(defs.Items.Count, defs.TotalInScope, "XML defs"));
                var defLabels = ScopeArgs.SourceLabeling.Of(defs.Items.Select(e => e.SourceName));
                sb.AppendLine($"\n**XML Defs**{defLabels.Header}:");
                foreach (var entry in defs.Items)
                {
                    var def = entry.Item;
                    var abstractTag = def.IsAbstract ? " [Abstract]" : "";
                    var label = !string.IsNullOrEmpty(def.Label) ? $" \"{def.Label}\"" : "";

                    // 译名接在英文 label 后面。locate 只给 label——description 长一到两个数量级，
                    // 一屏几十条结果每条都带上就没法看了，那是 inspect 的事。
                    var localized = _localization?.Lookup(def.DefType, def.DefName)?.Label;
                    var localizedTag = !string.IsNullOrEmpty(localized) ? $" / {localized}" : "";

                    sb.AppendLine(
                        $"- `{def.DefName}` ({entry.Score:F0}%) - {def.DefType}{abstractTag}{label}{localizedTag}{defLabels.Row(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(defs, "XML defs", indent: "  ", limit: limit);
                if (fold != null) sb.AppendLine(fold);
            }

            if (query.Keywords.Count > 0)
            {
                var defsByContent = _defIndexer.SearchByContent(query.Keywords.ToArray(), scope, limit.Count);
                report.Add(defsByContent);

                if (defsByContent.Items.Count > 0)
                {
                    hasResults = true;
                    tally.Add(Count(defsByContent.Items.Count, defsByContent.TotalInScope, "content matches"));
                    var contentLabels = ScopeArgs.SourceLabeling.Of(
                        defsByContent.Items.Select(e => e.SourceName));
                    sb.AppendLine($"\n**Content Matches**{contentLabels.Header}:");

                    foreach (var entry in defsByContent.Items)
                    {
                        var (location, matchedFields) = entry.Item;
                        var fieldSummary = string.Join(", ", matchedFields.Take(3));
                        var moreFields = matchedFields.Count > 3 ? $" +{matchedFields.Count - 3}" : "";
                        sb.AppendLine($"- `{location.DefName}` - {fieldSummary}{moreFields}{contentLabels.Row(entry.SourceName)}");
                    }

                    var fold = ScopeArgs.FoldLine(defsByContent, "content matches", indent: "  ", limit: limit);
                    if (fold != null) sb.AppendLine(fold);
                }
            }
        }

        // 文件名是 locate 的一等查询目标（README 的「支持内容」头一条就列着它），但这一段原先
        // 只在其余四段全部零命中时才跑。于是查一个确实在索引里的文件名——'Bodies_Humanlike'——
        // 只要顺带蹭到一条 38 分的无关 def，整段就被吞掉，返回读起来是「索引里没有这个文件」。
        // 现在分两种触发：零命中时它仍是兜底（模糊列出若干条），有命中时只补名字完全一致的
        // 那一份，不把每次查询都拖长一段模糊文件名。
        var wantsFileFallback = !hasResults;
        var hasFilterPrefix = query.TypeFilter != null || query.MethodFilter != null
                              || query.FieldFilter != null || query.DefFilter != null;

        if (wantsFileFallback || !hasFilterPrefix)
        {
            var files = _sourceIndexer.Search(rawQuery, scope, limit.Count);

            // 精确补充时只留「基名与查询词逐字相同」的那些，并去掉已在 C# Types 段出现过的
            // 同名项（类型 CompShield 与文件 CompShield.cs 是同一件事的两种写法）。
            var items = wantsFileFallback
                ? files.Items.ToList()
                : files.Items
                    .Where(entry => string.Equals(
                        Path.GetFileNameWithoutExtension(entry.Item), rawQuery, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => !shownTypeNames.Contains(Path.GetFileNameWithoutExtension(entry.Item)))
                    .ToList();

            if (items.Count > 0)
            {
                // footer 的落选计数只在真的列出这一段时才计入，否则「补一条精确文件」会顺带
                // 把几十条模糊文件命中的 out-of-scope 计数灌进去，脚注的数字就不再对应正文。
                report.Add(files);

                tally.Add(Count(items.Count, items.Count, "files"));
                var fileLabels = ScopeArgs.SourceLabeling.Of(items.Select(e => e.SourceName));
                sb.AppendLine($"\n**Files**{fileLabels.Header}:");
                foreach (var entry in items)
                {
                    // 原先是「基名 - 全路径」，而基名逐字包含在全路径的末尾，说的是同一件事。
                    sb.AppendLine($"- {entry.Item}{fileLabels.Row(entry.SourceName)}");
                }

                // 折叠行只对兜底那一支有意义：精确补充本来就只列同名的那几条，没有「还有更多」。
                if (wantsFileFallback)
                {
                    var fold = ScopeArgs.FoldLine(files, "files", limit: limit);
                    if (fold != null) sb.AppendLine(fold);
                }

                hasResults = true;
            }
        }

        var footer = report.Render(scope);

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

        if (!hasResults)
        {
            var message = new StringBuilder(
                $"No results for '{ToolArgs.ForEcho(rawQuery)}' in scope '{scope.Expression}'.");
            message.Append(ScopeArgs.RetryWiderNotice(scope, footer != null));
            if (footer != null) message.Append(footer);
            message.Append(scopeNotice);
            message.Append(prefixNotice);

            // 过滤器清单只列一次。上面的 prefixNotice 在「前缀没被识别」时已经列过一遍
            // （那正是最该看到它的场合），这里再列就是同一行字紧挨着说两遍。
            message.Append(query.UnknownPrefixes.Count > 0
                ? "\n\nTry: partial names, or search_regex for patterns."
                : "\n\nTry: partial names, query filters (type:, method:, field:, def:), or search_regex for patterns.");

            // 零命中是一个正常结果，不是调用失败。isError 留给「工具没能执行」，置 true 会让
            // client 把这次搜索当成故障去重试或上报；同一个服务器里 trace 查不到子类、
            // search_regex 零命中都是 false，locate 独自为 true 只会让调用方两套判据。
            return Task.FromResult(new ToolResult(message.ToString()));
        }

        if (footer != null) sb.Append(footer);
        sb.Append(scopeNotice);
        sb.Append(prefixNotice);

        var header = new StringBuilder($"## '{rawQuery}'");
        if (tally.Count > 0) header.Append($" — {string.Join(", ", tally)}");
        if (!scope.IncludesEverything) header.Append($" _(scope: {scope.Expression})_");

        return Task.FromResult(new ToolResult(header.AppendLine().Append(sb).ToString()));
    }

    // 表头的每一格：**列出了几条，以及这个 scope 里一共有几条**。
    //
    // 原先只有前一个数（`— 5 members`），而 `method:CompTick` 的真实命中是 144——总数在整份
    // 返回里一次都没出现过，要靠折叠行的 `+139 more` 自己做加法。表头是最显眼的位置，
    // 盲测里两个调用方都差点把它当结论直接报出去，其中一个原话是「会把 144 报成 5，错 28 倍」。
    //
    // 同一批工具里 trace 的表头给的是**总数**（`(381 in scope 'base' …) Listed below: 200`），
    // locate 给的是**显示数**，句式却一样——两个口径撞在同一个位置上，这才是要害。故这里改成
    // 两个数都给，且沿用「看到 of 就是被截了」这条读法：没被截时不写 `of N`，那时显示即全部。
    //
    // 名词跟总数走（"1 of 768 C# types" 是属格复数，"5 C# types" 跟 5），与 R30 判据一致。
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

    private static string Count(int shown, int total, string plural) =>
        total > shown
            ? $"{shown} of {OutputText.Quantity(total, plural)}"
            : OutputText.Quantity(shown, plural);

    // 判据与 trace 共用（见 SymbolRow）：文件名推得出来就不印。locate 用 ` - 名字` 的破折号写法，
    // trace 用括号写法，共享的是「什么时候印」而不是「怎么排版」。
    private static string FileNote(string typeName, IReadOnlyList<string> paths)
    {
        var note = SymbolRow.FileNote(typeName, paths);
        return note.Length == 0 ? string.Empty : $" - {note.Trim().Trim('(', ')')}";
    }

    // MemberType 来自索引层，取值是 Method / Property / Field。直接加 's' 会写出 'Propertys'。
    private static string Plural(string memberType) =>
        memberType.EndsWith('y') ? $"{memberType[..^1]}ies" : $"{memberType}s";

}
