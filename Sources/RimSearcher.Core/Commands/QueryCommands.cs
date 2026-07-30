using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

public sealed class SearchCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "search",
        Aliases = ["find-def", "s"],
        Summary = "Find defs by name, label, description, or translated text.",
        Remarks =
            "Matching runs in stages and stops at the first one that finds anything: full-text search, a " +
            "substring pass over names, the pre-translation original text of translations, then fuzzy " +
            "identifier matching that tolerates typos and CamelCase initials. You never need to add '*' " +
            "yourself. Translated text is in the full-text index, so a Chinese label finds the def; the " +
            "English wording it replaced is not, and is reached only by that later pass — which is why an " +
            "English query against a translated snapshot can come back with rows whose label column is not " +
            "English. Each result says in 'matched_on' which of these it was.",
        Positionals = [new PositionalSpec { Name = "query", Help = "Words, a def name, or part of one." }],
        Options = [CommonOptions.Limit("defs"), CommonOptions.Offset("defs"), CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher search shield",
            "rimsearcher search \"psychic shock\" --type ThingDef",
            "rimsearcher search CompShield --scope all,-vanilla",
        ],
        JsonKeys =
        [
            new() { Key = "defs", What = "one row per matching def: def_name, def_type, label, matched_on, mod." },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var query = ctx.Args.Positional(0)!;
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");

        var offset = ctx.Args.Offset();

        var (rows, total) = ctx.Db.SearchFts(query, scope, type, limit.Effective, offset);
        var ftsTotal = total;
        var how = "full-text";
        var addedBySubstring = 0;

        // 三级匹配「命中第一级就停」曾把答案漏在第二级里:FTS 分词按分隔符与驼峰**词首**切,
        // `VoidNode` 于是找不到 `MonolithGleamingVoidNode` —— 查询词落在名字中段。
        // 实测里这条漏洞的后果不是「少一行」,而是调用方拿 `--limit all` 复跑、看到逐字相同的
        // 输出,反而**二次确认**了「22 条即全集」这个错结论。补一遍子串扫描,别让人自己去拆词。
        if (IsCompoundToken(query))
        {
            // 去重在 SQL 侧对**整个 FTS 命中集**做,不是对已显示的行做;全量算进 total,
            // 只取得下的进 rows。两条都不能省:按已显示的行去重会把没显示出来的 FTS 命中
            // 当成新增(`--limit 3` 报「3 of 41」),先 Take 再累加则让 M 跟着 --limit 缩
            // (报「3 of 22」而真值 23)—— 两种都是这条补丁本身要修的那个错结论的翻版。
            var extra = ctx.Db.NamesContainingUnmatched(query, scope, type);
            if (extra.Count > 0)
            {
                total += extra.Count;
                var room = Math.Max(0, limit.Effective - rows.Count);
                if (room > 0)
                {
                    // 结果集是「FTS 命中」接着「子串补扫」两段拼起来的,翻页要在**拼好的那一条
                    // 序列**上走。FTS 段已经在 SQL 里跳过了 offset;跳完还有剩,就说明这一页
                    // 落在了第二段里,余下的偏移量从这里接着扣。两段各自跳一次 offset 是最容易
                    // 犯的那个错 —— 第二页会把第一页的补扫结果原样再印一遍。
                    var skipHere = Math.Max(0, offset - ftsTotal);
                    var (more, _) = ctx.Db.ByNames([.. extra.Skip(skipHere).Take(room)], room);
                    rows = [.. rows, .. more];
                    addedBySubstring = more.Count;
                }
            }
        }

        // 译文原文那一侧的兜底。FTS 只索引 translated,于是一份中文快照上,每个 def 的英文
        // 原名都在库里躺着却一个也搜不到 —— 而落空那句话还写着「covers … and translations」。
        //
        // **必须排在模糊回退之前**:反过来的话,英文查询会先撞上一批「拼写相近的中文名」,
        // 而那份输出读起来就是「这东西没有」。真答案被拼写噪声挤掉,是同形错答案的又一种。
        if (rows.Count == 0 && offset == 0)
        {
            var byOriginal = ctx.Db.NamesByTranslationOriginal(query, scope, type);
            if (byOriginal.Count > 0)
            {
                (rows, total) = ctx.Db.ByNames(byOriginal, limit.Effective);
                how = "translation original";
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No name, label or translated text in this snapshot contains '{query}'; these defs have it " +
                    "in the original text a translation replaced. This snapshot's language is " +
                    $"{ctx.Db.Meta.Language}, so the English wording survives only where a translation " +
                    "recorded what it was translated from.");
            }
        }

        // 模糊回退只在**第一页**做。翻到末页之后 rows 自然为空,那时改口给一批「拼写相近的
        // 名字」,读起来就像前面那些命中不作数了 —— 而它们明明是同一次查询的前几页。
        if (rows.Count == 0 && offset == 0)
        {
            // 02-7 的对策:调用方不该需要知道 '*' 才搜得到复合名,更不该知道打错一个字母就归零。
            // 候选先去重再打分:AllDefNames 是**按 def 一行**给的,而 Firefoam 那样一名两 def
            // 的名字在候选集里会出现两次,于是 `--limit 5` 里有一格花在同一个名字上。
            var names = ctx.Db.AllDefNames(scope).Distinct(StringComparer.Ordinal).ToList();
            var (bare, kind) = FuzzyMatcher.StripKindPrefix(query);
            var ranked = FuzzyMatcher.Rank(names, bare).Take(limit.Effective).Select(t => t.Text).ToList();
            if (ranked.Count > 0)
            {
                // total 用 ByNames 报的**行数**,不是名字数 —— 一个名字带两行时,
                // 按名字数报会让页脚的 M 比表里的行还少。
                (rows, total) = ctx.Db.ByNames(ranked, limit.Effective);
                how = kind is null ? "fuzzy" : $"fuzzy (ignoring the '{kind}:' prefix, which defs do not use)";
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No def matched '{query}' as written; these are the closest names by spelling.");
            }
        }

        if (rows.Count > 0)
        {
            ctx.Report.PageNotice("def", rows.Count, offset, Math.Max(total, offset + rows.Count));

            if (addedBySubstring > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"That includes {Tally.Complete(addedBySubstring).Render("def")} found by scanning names for " +
                    $"'{query}' as a substring; full-text matching alone splits names at word starts, so it misses " +
                    "the query in the middle of a compound name.");
        }
        if (rows.Count == 0 && offset > 0)
        {
            // 翻过了头不是「没有这个东西」。分开说,否则一次翻页会被读成一次否定 ——
            // 这正是 R8 那批误诊的形状换个位置再来一遍。
            ctx.Report.PastEnd(offset, $"'{query}' matched {Tally.Complete(total).Render("def")} in all.");
        }
        else if (rows.Count == 0)
        {
            // 值域必须说清。search 覆盖的是 defName / label / description / 译文 ——
            // **不含** C# 类名。实测里有人拿 CompShield 来搜,零结果被读成「模糊匹配坏了」,
            // 而错误消息当时把他指向 code-search:那条路找得到类,却永远找不到用它的 def。
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing matched '{query}' in this snapshot" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                ". This command covers def names, labels, descriptions and translations, not C# class names.");

            // R8:剩下那半句原先是**猜**的 —— 「像个类名」就指向 find/code-search,
            // 否则指向 types。两条猜法各自造出一种误诊,而名字的真实落点是可以当场算的。
            // 算得出来就说算出来的那一条,算不出来才退回猜(而猜也只在真像类名时才猜)。
            var sighting = NameLookup.Locate(ctx, query, scope);
            var looksLikeClass = ClassNameShape.Looks(query);
            ctx.Report.Notice(NoticeKind.NextStep,
                sighting?.Sentence
                ?? (looksLikeClass
                    ? $"Nothing in this snapshot is called that under any other guise either — no def type, " +
                      $"no class, no mod. If '{query}' is a class that only appears inside nested " +
                      "<li Class=\"...\"> objects, the snapshot does not index those: 'rimsearcher code-search' " +
                      "finds the class itself."
                    : "'rimsearcher types' lists what kinds of def this snapshot holds, and " +
                      "'rimsearcher mods' lists which mods it covers."));
        }

        // 「靠什么命中的」必须在表里。实测:`search 心灵迟钝` 命中 TraitDef PsychicSensitivity,
        // 是因为它某一档 degreeData 的 label 就叫这个 —— 但 TraitDef 自己没有 label,那一行
        // 于是一片空白,而排在它上面的 GeneDef 的 label 恰恰就是「心灵迟钝」。读到这一屏的
        // 第一反应是「心灵迟钝是个基因,不是特性」,而那正是本题的错误答案。
        ctx.Report.Table("defs", ["def_name", "def_type", "label", "matched_on", "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["label"] = r.Label,
                ["matched_on"] = how.StartsWith("fuzzy", StringComparison.Ordinal)
                                 ? "closest spelling"
                                 : MatchedOn(ctx, r, query),
                ["mod"] = r.SourceMod,
            }).ToList());

        Advisory.NoteOutsideTranslations(ctx, rows.Select(r => r.DefName));
        return rows.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// 这一行靠什么命中。判据只认「肉眼能在这一行上验证的」:名字、label、描述里含查询词。
    /// 都不含时说明命中来自不在表里的东西(译文,或 label 挂在子结构上),那正是最需要说的
    /// 一种 —— 不说,这一行就是没有解释的空白。
    /// </summary>
    private static string MatchedOn(CommandContext ctx, DefRow r, string query)
    {
        bool Has(string? s) => s is { Length: > 0 } && s.Contains(query, StringComparison.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (Has(r.DefName)) parts.Add("def_name");
        if (Has(r.Label)) parts.Add("label");
        if (Has(r.Description)) parts.Add("description");
        if (parts.Count > 0) return string.Join("+", parts);

        // R2 的同族:这里也只按 defName 取过译文,于是同名跨 def 类型时会把**别人的**译文
        // 路径报成「这一行靠什么命中」。F18 加这一列正是为了让 label 空的行有个解释,
        // 给错的解释比不给更坏。def_type 为空的(语言文件收割,注入 key 不带类型)仍算,
        // 因为游戏也是按名字注入的 —— 那条译文确实作用在这个 def 上。
        var all = ctx.Db.Translations(r.DefName);
        var t = all.Where(x => x.DefType is null || DefTypes.Same(x.DefType, r.DefType))
                   .FirstOrDefault(x => Has(x.Translated) || Has(x.Original));
        if (t is not null) return t.Path;

        // 命中来自**另一个同名 def** 的译文 —— 上一句的 def_type 过滤刚把它挡掉。这一行
        // 自己没有任何东西含查询词,说成 "indexed text" 就是给它一个它验证不了的解释,
        // 而它与真·靠索引文本命中的行在这一列上逐字同形。
        if (all.Any(x => Has(x.Translated) || Has(x.Original))) return "same def_name";

        return "indexed text";
    }

    /// <summary>单个复合标识符(无空格、内部有大写或下划线)—— 只有这种查询词会落进名字中段。</summary>
    private static bool IsCompoundToken(string q)
        => q.Length > 2 && !q.Any(char.IsWhiteSpace) &&
           (q.Contains('_') || q.Skip(1).Any(char.IsUpper));
}

public sealed class GetCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "get",
        Aliases = ["show", "inspect", "def"],
        Summary = "Show one def in full: its identity, its fields, and any translations of it.",
        Remarks =
            "Field paths are the merged, post-patch shape the game actually had in memory when the snapshot was " +
            "taken, so PatchOperations and inheritance are already applied. A def created in code rather than XML " +
            "says so on its source line.",
        Positionals = [new PositionalSpec { Name = "defName", Help = "The exact def name. 'search' finds it if you only know part of it." }],
        Options =
        [
            CommonOptions.Limit("fields") with { Default = Limits.DefaultFieldsPerDef.ToString() },
            new OptionSpec
            {
                // 实测:一个 295 字段的 def 里找 statBases,唯一的办法是 --limit all 再 grep 输出。
                // 那既烧上下文,又正好是这套工具劝人别做的事。
                Name = "path",
                Arity = Arity.Multi,
                Aliases = ["paths", "field", "field-path", "only", "filter", "grep"],
                Placeholder = "<text>",
                Help = "Only show field paths containing this text. Repeat it to widen the selection.",
            },
            // 同名跨 def 类型是 RimWorld 常态(PsychicSensitivity 既是 StatDef 又是 TraitDef)。
            // 工具自己都在输出「N defs share the name」,却没有任何开关能挑一个 —— 实测里
            // 四个 agent 各自敲了 `--type` 然后吃同一句「Unknown option」。
            CommonOptions.Type,
            new OptionSpec
            {
                Name = "defaults",
                Arity = Arity.Flag,
                Aliases = ["with-defaults", "all-fields"],
                Help = "Also list fields whose value is the one a fresh instance of the declaring type already "
                     + "carries. Those rows are left out by default because they are the ones most often read as "
                     + "something an author chose, when the snapshot cannot tell whether anything set them at all. "
                     + "How many were left out is always printed, and --path shows a named field either way.",
            },
            new OptionSpec
            {
                Name = "fields",
                Arity = Arity.Flag,
                Aliases = ["with-fields", "show-fields"],
                Help = "Deprecated no-op: fields are always shown. Kept so that scripts that pass it keep working.",
            },
        ],
        Examples =
        [
            "rimsearcher get Apparel_ShieldBelt",
            "rimsearcher get Apparel_ShieldBelt --path statBases",
            "rimsearcher get Bullet_Revolver --limit all",
            "rimsearcher get Bullet_Revolver --defaults",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                What = "one object per def carrying the name — each with 'def' (identity), 'fields' " +
                       "(path/value rows) and, when there are any, 'translations'. It stays an array even " +
                       "for a single def, because a name can belong to several def types at once.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        var wantType = ctx.Args.Value("type");
        // 过滤前的全量要留住:同名提示必须按**这个名字一共有几个 def** 说话,而不是按
        // 这次显示了几个。R2 最恶劣的一半正是这里 —— 提示原先挂在过滤后的集合上,
        // 于是按 SKILL 教的加了 --type 之后提示消失、串味的行留下,对冲归零。
        var allMatches = ctx.Db.GetDefsNamed(name);
        var matches = allMatches;

        if (wantType is { Length: > 0 } && matches.Count > 0)
        {
            var kept = matches.Where(d => string.Equals(d.DefType, wantType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{name}' exists in this snapshot but not as a {wantType}. It is " +
                    $"{string.Join(" and ", matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal))}. " +
                    "Drop --type to see it.");
                return 1;
            }
            matches = kept;
        }

        if (matches.Count == 0)
        {
            // 抽象父节点原先只能靠一句**无条件**的边界句糊过去,因为快照里一点痕迹都没有 ——
            // 那句话每次 get 落空都要说一遍,而十有八九问的根本不是抽象节点。继承层落地之后
            // 这件事变成可判定的,而它只是「名字在哪儿」的一种落点:判据搬进 NameLookup,
            // 六种落点一起判(R8/R10),这里不再自己只查一张表。
            var sighting = NameLookup.Locate(ctx, name);
            if (sighting is not null)
            {
                ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
                return 1;
            }

            var names = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            var close = Suggestion.Closest(names, name);

            // 走到这里,六种落点都算过了,别的快照也问过了 —— 这时候「没有」才是个结论,
            // 而不是「我只查了一张表」。把这层意思说出来,否则读的人无从判断该不该相信它。
            //
            // 但六种落点全在**快照**里,而快照只装 def 侧。第四轮回归实测(B6):
            // `get MapPortal` 走到这一句,而 MapPortal 是 RimWorld 的一个 C# 类,
            // 就在 vanilla 树的 MapPortal.cs 里。「a class」指的是「某个 def 的 class 列」,
            // 读的人不会这么读 —— 这一句听上去穷尽了,于是归属被判成「不存在」,
            // 正是 B6 那道题的靶子。不去扫代码树(一万多个文件,每次落空都扫太贵),
            // 而是把没查的那一半说出来,并指名唯一那条能查的命令。
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def is named '{name}' in this snapshot, and it is not a def type, a class, a mod, " +
                "an abstract XML parent, or a name held by any other registered snapshot." +
                Suggestion.Say(close, " 'rimsearcher search' matches on labels and translations too.") +
                " All of that is the def side; C# type names that no def references live only in the " +
                $"decompiled trees, which this lookup never reads: 'rimsearcher code-search \"class {name}\"'.");
            return 1;
        }

        var limit = ctx.Limit(fallback: Limits.DefaultFieldsPerDef);
        var paths = ctx.Args.Values("path");

        foreach (var def in matches)
        {
            // 恒定形状:即使只有一个 def,JSON 里也是 defs[0]。单数/复数两种形状会让
            // 照着一次输出写的解析器在下一次撞名时静默拿到别的东西。
            ctx.Report.Item("defs");

            // --path 说的是「这次我只要这些」。description 动辄几百字,精确提问反而被它
            // 淹掉 —— 实测里 7 个 def 一批投影出 36KB 落盘,前 2KB 预览一个目标值都没有。
            var pairs = new List<KeyValuePair<string, object?>>
            {
                new("def_name", def.DefName),
                new("def_type", def.DefType),
                new("label", def.Label),
                new("description", paths.Count > 0 ? Clip(def.Description) : def.Description),
                new("class", def.Class),
                new("mod", def.SourceMod),
                new("source", def.Generated
                    ? $"{def.SourceFile} (created in code, not from an XML file)"
                    : def.SourceFile),
            };

            // 有父节点才出这一行。没有的那九成 def 平白多一行空值,就是把上下文预算
            // 花在「这里什么也没有」上 —— 而恒 null 的 parent 字段正是 F13 删掉的那个东西。
            //
            // R2:原先只按 defName 取,于是同名跨 def 类型时把**别人的**父节点印在自己的
            // 标题块下(实测 def_type MentalStateDef 底下印出 inherits_from PsycastBase)。
            // 但不能改成「def_type 必须相等」就完事:`xml_nodes.def_type` 是 XML 根元素名,
            // `defs.def_type` 是 AllDefTypesWithDatabases 的桶名,F5 已经证明这两者会不一致
            // (CreepJoinerAggressiveDef 的 def 落在 CreepJoinerBaseDef 桶里)。硬要求相等
            // 会把「串味」换成「丢数据」—— 正是它要修的那类错。
            // 收法:先要相等的;没有相等的,只在这个名字**没有同名歧义**时才回退到唯一候选,
            // 有歧义就不显示(不显示比显示错的强,而 `inherit` 那条路照样答得出)。
            var named = ctx.Db.NodesNamed(def.DefName)
                              .Where(n => string.Equals(n.DefName, def.DefName, StringComparison.OrdinalIgnoreCase))
                              .ToList();
            var xmlNode = named.FirstOrDefault(n => DefTypes.Same(n.DefType, def.DefType))
                       ?? (named.Count == 1 && allMatches.Count == 1 ? named[0] : null);
            if (xmlNode?.ParentName is { Length: > 0 } parentName)
                pairs.Add(new("inherits_from", $"{parentName} (see 'rimsearcher inherit {def.DefName}')"));

            ctx.Report.Detail("def", pairs);

            // R1:默认不列「与 C# 声明默认值无从区分」的那些行 —— 四个错结论全是从它们
            // 生成的,而且每次错的都恰好是「字段名与提问一字不差」的那一行。
            //
            // 两个例外,都指向同一条:**调用方点了名的东西不许消失**。
            //   --path <text> 已经点名了要哪些路径:此时把其中一条藏起来,回答会变成
            //     「没有路径含 burstCount」—— 比印错值更坏,因为它是一句彻底的假话。
            //   --defaults 是明说要全量。
            // 于是过滤只发生在「什么都没点名」的那一次,而那一次省下的正是纯篇幅。
            var withDefaults = ctx.Args.Flag("defaults") || paths.Count > 0;
            var (fields, matched, total, defaulted, matchedPaths) =
                ctx.Db.Fields(def.Id, limit.Effective, paths, includeDefaults: withDefaults);
            ctx.Report.Table("fields", ["path", "value", FieldDefault.Column],
                fields.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                    // 这一列恒在,不随「本次有没有默认值行」出现或消失:表的形状随数据变,
                    // 照着一次输出写的解析器下一次就取不到键。unknown 也必须能与 no 分开 ——
                    // 把「没比成」印成「有人改过」正是 R1 本身。
                    [FieldDefault.Column] = FieldDefault.Render(f.Default),
                }).ToList());

            // 多个 def 同名时,截断声明必须指名道姓 —— 否则两条「Showing 5 of N fields」
            // 并排出现,读者无从知道哪条管哪个 def。
            var whose = matches.Count == 1 ? "" : $" of {def.DefName} ({def.DefType})";
            if (paths.Count > 0)
            {
                // 过滤后为空**不等于** def 没有这些字段,只等于没有路径含这段文本。
                // 这两件事在输出上长得一样,所以必须由声明区把它们分开。
                if (matched == 0)
                {
                    // 第二种成因,第四轮回归实测撞到的:给进来的文本不是路径,是**值**
                    // (`--path TrapSpringChance` —— 它是 statBases[6].stat 装着的那个值)。
                    // 「这个 def 没有这条路径」与「你把值当成了路径」的输出此前逐字同形,
                    // 而后者算得出来,所以算出来再说 —— 猜出来的下一步正是 R8 那批误诊。
                    var asValue = paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList();
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"No field path{whose} contains {Join(paths)}; the def does have " +
                        $"{Tally.Complete(total).Render("field")}. Drop --path to see them." +
                        // 动词不进登记处,所以句子里不能有随 asValue 数量变形的成分 ——
                        // 冒号在前、名单在后,主句就没有跟着计数走的动词(R6 的同一条教训)。
                        (asValue.Count > 0
                            ? " Found on this def as a field's value rather than anywhere in a path: " +
                              $"{Join(asValue)}. 'rimsearcher find --value {asValue[0]}' names every path holding it."
                            : ""));
                }
                else
                {
                    // 这是调用方自己要的过滤,不是截断。机器侧靠 kind 分类,混用会让
                    // 「我主动只要 driverClass」被扫 notes 的下一位读成「结果不完整」。
                    // 动词不进登记处,所以句子不能让动词跟着计数走 —— 原先是
                    // 「{N} fields{whose} match …」,N 为 1 时读出「1 field match」。
                    // 把计数挪到冒号后,主句就不再有随数量变形的成分(R6 的同一条教训)。
                    // 子串匹配不留痕:`--path soundImpact` 只回一行 `soundImpactDefault` ——
                    // 语义相反的另一个字段,`code_default=no` 让它看着像作者刻意设的,而输出里
                    // 没有一处说过「你打的这个词作为完整的一段一次都没命中」。第五轮盲测里
                    // 这一条直接产出了错结论。整段命中的数在**截断之前**数(matchedPaths 不受
                    // --limit 影响),否则同一个 --path 换个 --limit 就换一句结论。
                    var whole = matchedPaths.Count(x => PathSegments.IsWholeSegment(x, paths));
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"Matching {Join(paths)}{whose}: " +
                        $"{Tally.Complete(matched).Render("field")}, out of " +
                        $"{Tally.Complete(total).Render("field")} on the def." +
                        (whole == 0
                            ? $" None of those has {Join(paths)} as a whole path segment — each contains it " +
                              "inside a longer name, so a field by exactly that name may not exist here."
                            : whole < matched
                                ? $" Whole path segment: {Tally.Complete(whole).Render("field")}; " +
                                  $"inside a longer name: {Tally.Complete(matched - whole).Render("field")}."
                                : ""));
                    if (fields.Count < matched)
                        ctx.Report.Notice(NoticeKind.Truncation,
                            $"Showing {Tally.Of(fields.Count, matched).Render("field")}; raise --limit for the rest.");

                    // --path 是调用方自己收窄的,而收窄之后同一块里的其它字段就看不见了。
                    Advisory.NoteAuthoredSiblings(ctx, fields.Where(f => f.Default != Contract.DefaultState.Same)
                                                             .Select(f => (def.Id, f.Path)));
                }
            }
            else
            {
                // 分母是**列出来的那一群**的总数,不是 def 的字段总数 —— 否则「3 of 120」里
                // 那 117 条既包含被 limit 截的、也包含被默认值过滤掉的,读者无从拆开。
                // 两者各自一句,再由 total 把账对上。
                var listable = withDefaults ? total : total - defaulted;
                ctx.Report.CountNotice(Tally.Of(fields.Count, listable), "field",
                    $"pass --limit all{(matches.Count == 1 ? "" : $" (this is {def.DefName})")} " +
                    "for the rest, or --path <text> to pick out the ones you want.");

                // 措辞不许滑成「没人设过它」:XML 里照着默认值写一遍是常事,快照里那两种
                // 情形完全同形。这一列能证的只有「与声明默认值无从区分」,句子就只说这个。
                // 同理句中不出现任何与数量一致的动词或代词(名词才有登记处)。
                // 五轮 F4:第二个分句原先说「The def has N fields in all; pass --defaults to list
                // every one」—— 两处超发。N 是**索引到的路径数**而不是 def 的字段数;
                // 而「list every one」在值为 null 的字段上做不到:导出器见 null 直接 return
                // (DefExporter:284),那条路径从来没进过索引,--defaults 也召不回来。
                // 于是「这个字段不存在」与「它的值是 null」在输出上完全同形,而实测里有人
                // 跨三份列表交叉验证,只是把错结论**加固**了 —— 缺的是同一批字段。
                // 第一分句逐字不动(它是被反复点名的那句认识论诚实),--path 那半句也不动
                // (那是唯一的逃生指令),只换中间那一段。
                if (!withDefaults && defaulted > 0)
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"Not listed: {Tally.Complete(defaulted).Render("field")} whose value is the one a fresh " +
                        "instance of the declaring type already carries, so this snapshot cannot tell whether " +
                        $"anything set it. The snapshot holds {Tally.Complete(total).Render("field path")} for this " +
                        "def; --defaults lists the rest of those, and --path <text> sees a named one either way. " +
                        "A field whose value was null is in none of them: it never entered the index, so its " +
                        "absence here is not evidence that the field does not exist.");
            }

            // 02-3:「字段被截」与「没有该字段」必须可区分。上游把这件事整个略过了,
            // 于是深层字段查不到时调用方会得出「没有这个字段」的错误结论。
            if (def.FieldsTruncated > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The exporter stopped short on this def: {Tally.AtLeast(def.FieldsTruncated).Render("field")} " +
                    "were dropped at export time for depth or size, so a path missing from the list below is not " +
                    "proof that the def lacks it.");

            // --limit 说的是「这次调用我要多少行」,不是「字段表要多少行」。译文表不听它的话,
            // 就出现过 `get Muffalo --limit 5` 吐出八十行的实测 —— 限额说了不算,预算就是空话。
            // --path 同理:实测里字段表正确地报了「一个都没匹配上」,紧接着三份内容相同的
            // 译文块把这个真结论淹掉,第一眼读成「找到了一堆东西」。精确提问不该更吵。
            // R2 的另一半:译文原先也只按 defName 取,于是字段表刚说完「这个 def 没有
            // description」,紧接着就印出同名**别的 def type** 的 description 译文并标
            // 「origin: in effect」—— 读者只能读成「描述由翻译文件注入」。
            // 策略与 inherits_from 同源:def_type 对得上的归自己;对不上的一律不要;
            // def_type 为空的(语言文件收割,注入 key 不带类型)留着,但要自证它是按名字匹配的。
            var allTranslations = (IReadOnlyList<TranslationRow>)ctx.Db.Translations(def.DefName)
                .Where(t => t.DefType is null || DefTypes.Same(t.DefType, def.DefType))
                .ToList();
            // 一个 def_type 对得上的都没有、却有一批不带类型的,且名字还有歧义 —— 此时
            // 「这批译文归谁」纯属未知,说清比默默端出去强。
            var byNameOnly = allTranslations.Count > 0 && allTranslations.All(t => t.DefType is null);
            if (paths.Count > 0)
                allTranslations = allTranslations
                    .Where(t => paths.Any(p => t.Path.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            var translations = limit.IsAll
                ? allTranslations
                : allTranslations.Take(limit.Effective).ToList();
            if (translations.Count > 0)
            {
                // original 是被替换掉的原文。它值得占一列:导出时刻 def 上留的是译文,
                // 原文只在注入记录里 —— 两者同时在场是运行时导出独有的便宜(06 层 2 翻译节)。
                ctx.Report.Table("translations",
                    ["path", "translated", "original", "language", "origin"],
                    translations.Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["path"] = t.Path,
                        ["translated"] = t.Translated,
                        ["original"] = t.Original,
                        ["language"] = t.Language,
                        ["origin"] = t.Origin == TranslationOrigin.Runtime ? "in effect"
                                   : t.Origin == TranslationOrigin.Harvested ? $"file ({t.SourceMod})"
                                   : $"file ({t.SourceMod}, outside this snapshot)",
                    }).ToList());

                ctx.Report.CountNotice(Tally.Of(translations.Count, allTranslations.Count),
                    "translation", $"pass --limit all to see the rest{whose}.");

                if (translations.Any(t => t.Origin == TranslationOrigin.HarvestedOutside))
                    ctx.Report.Notice(NoticeKind.Advisory,
                        "Rows marked 'outside this snapshot' come from language files of mods that were installed " +
                        "but not enabled when the snapshot was taken. They are searchable, but the game did not " +
                        "apply them.", footnote: true);

                if (byNameOnly && allMatches.Count > 1)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"These rows were matched by defName alone: they come from language files, whose keys are " +
                        $"'{def.DefName}.<field>' with no def type, and " +
                        $"{Tally.Complete(allMatches.Count).Render("def")} share this name. " +
                        "The game injects them by name too, so which of the same-named defs they belong to is not " +
                        "recorded anywhere.");
            }
        }

        ctx.Report.EndItems();

        // R2:这句原先挂在**过滤后**的集合上,于是 --type 一给就消失 —— 而那正是最需要它的
        // 时候:调用方主动收窄了,恰恰说明它知道有歧义、并打算只读一个。提示走了,读者就
        // 没有任何迹象去怀疑标题块里那些按名字关联来的行。改成按全量说话,两种情形各自措辞。
        if (allMatches.Count > 1)
        {
            var others = allMatches.Where(d => !matches.Contains(d))
                                   .Select(d => d.DefType)
                                   .Distinct(StringComparer.Ordinal)
                                   .ToList();
            ctx.Report.Notice(NoticeKind.Boundary, others.Count == 0
                ? $"{Tally.Complete(allMatches.Count).Render("def")} share the name '{name}' across different def " +
                  "types; all of them are shown. Pass --type <DefType> for just one."
                : $"{Tally.Complete(allMatches.Count).Render("def")} share the name '{name}': this is the " +
                  $"{string.Join(" and ", matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal))} one. " +
                  $"The other{(others.Count == 1 ? " is a" : "s are")} {string.Join(" and ", others)}" +
                  $"{(others.Count == 1 ? "" : " def")}, shown only without --type. Fields, parent node and " +
                  "translations above are this def's own.");
        }

        return 0;
    }

    private static string Join(IReadOnlyList<string> parts)
        => parts.Count == 1 ? $"'{parts[0]}'" : string.Join(" or ", parts.Select(p => $"'{p}'"));

    /// <summary>--path 在场时把 description 压成一行:它不是被要的东西,却最占地方。</summary>
    private static string? Clip(string? text)
    {
        if (text is null || text.Length <= 80) return text;
        return text[..77].TrimEnd() + "...";
    }
}

public sealed class FindCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "find",
        Aliases = ["by-field", "where"],
        Summary = "Find defs by the value of a field. This is the reverse lookup: from a C# class or a value back to the defs that use it.",
        Remarks =
            "The field path is matched from the end, so 'compClass' finds 'comps[3].compClass' without you knowing " +
            "the index. This replaces grepping the XML: the values here are the merged, post-patch ones, and a " +
            "class reference is an exact match rather than a text hit.",
        Positionals =
        [
            new PositionalSpec { Name = "fieldPath", Help = "A field path or just its last segment, such as compClass or defaultProjectile. Omit it when you pass --value.", Required = false },
            new PositionalSpec { Name = "value", Help = "The value to look for. Omit it to list every def that has the field at all.", Required = false },
        ],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Offset("defs"),
            CommonOptions.Scope,
            new OptionSpec
            {
                Name = "exact",
                Arity = Arity.Flag,
                Aliases = ["exact-match", "whole"],
                Help = "Require the whole value to match, with either a field path or --value. " +
                       "Without it, the value is matched as a substring.",
            },
            new OptionSpec
            {
                // 「别 grep XML」拿走了一种能力,就得给回等价的一种。没有它,不知道字段
                // 叫什么的人只能猜 —— 而猜偏了会拿到一个语法正常、语义全错的结果集。
                Name = "value",
                Aliases = ["any-field", "search-values", "holding"],
                Placeholder = "<text>",
                Help = "Search every field for this value and report which paths hold it, instead of naming a field yourself.",
            },
        ],
        Examples =
        [
            "rimsearcher find compClass RimWorld.CompShield",
            "rimsearcher find defaultProjectile Bullet_Revolver",
            "rimsearcher find --value World/WorldObjects/Expanding",
        ],
        // 这条命令的两种问法产出两种行,所以键名也是两个 —— 同一个键装两种形状,
        // 消费方读到的字段会随它没传过的参数变,比多一个键危险得多。
        JsonKeys =
        [
            new()
            {
                Key = "matches",
                What = "with a field path: one row per def that has it — def_name, def_type, value, mod.",
            },
            new()
            {
                Key = "paths",
                What = "with --value: one row per field path that holds the value — path, def_type, defs, " +
                       "example_value. This is the key --value produces; 'matches' is absent then.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();

        var offset = ctx.Args.Offset();

        if (ctx.Args.Value("value") is { Length: > 0 } anyValue)
            return ByValue(ctx, anyValue, scope, limit, ctx.Args.Flag("exact"), offset);

        var path = ctx.Args.Positional(0);
        if (path is null)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                "'find' needs either a field path ('rimsearcher find compClass CompShield') or " +
                "--value to search every field ('rimsearcher find --value CompShield').");
            return 2;
        }
        var value = ctx.Args.Positional(1);
        var exact = ctx.Args.Flag("exact");

        var (rows, total) = ctx.Db.FindByField(path, value, exact, scope, limit.Effective, offset);

        if (rows.Count > 0)
            ctx.Report.PageNotice("def", rows.Count, offset, total);
        else if (offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("def")} match in all.");
            return 1;
        }

        if (rows.Count == 0)
        {
            // 别的快照里有没有,是**算得出来**的,而本快照那句「没有」在读的人眼里就是
            // 「这东西不存在」。叠加不替换:成因分流照说,这一句排在它后面。
            // 放在成因分流之前算、之后印 —— 它对四条分支一视同仁,而每条分支各自 return。
            void NoteElsewhere()
            {
                if (NameLookup.Elsewhere(ctx, db => db.FindByField(
                        path, value, exact,
                        Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0, 0).Total, "def")
                    is { } line)
                    ctx.Report.Notice(NoticeKind.NextStep, line);
            }

            // 零结果有三种互斥成因,它们要的下一步完全不同:
            //   (1) 这个字段路径根本不存在 → 该去找字段叫什么
            //   (2) 字段存在,但这个值不在它的值域里 → 该去看值域
            //   (3) 名字是 def 的身份而不是字段(class / def_type / mod / source)→ 该换命令
            // 原先只有 value 为 null 时才查(1),带 value 的分支直接去算近似项,于是
            // `find zzznotafield somevalue` 会报「No def has 'zzznotafield' set to ...」——
            // 这句话预设了字段存在,而下一条 `values zzznotafield` 立刻说它不存在,自相矛盾。
            var fieldExists = ctx.Db.FieldPathExists(path, scope);

            if (!fieldExists)
            {
                // identity 级的名字不是字段,却是最自然的猜法 —— 它们在 get 的输出里就摆着。
                var identity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["class"] = "'rimsearcher list <DefType> --class <ClassName>' filters by the def's own class",
                    ["def_type"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["deftype"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["mod"] = "'--scope <packageId>' restricts any query to one mod",
                    ["source"] = "the source file is shown by 'rimsearcher get', but is not searchable",
                    ["parent"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                    ["parentname"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                };

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def in this snapshot has a field path ending in '{path}'" +
                    (scope.IsAll ? "" : $" within --scope {scope.Expression}") + ". " +
                    (identity.TryGetValue(path, out var hint)
                        ? $"'{path}' is part of a def's identity rather than one of its fields: {hint}."
                        : "'rimsearcher fields <DefType> --path <text>' lists the paths that a def type actually has" +
                          (value is null ? "." : $", and 'rimsearcher find --value {value}' finds which field holds that value.")));
                NoteElsewhere();
                return 1;
            }

            if (value is null)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{path}' exists in this snapshot but no def has it within --scope {scope.Expression}. " +
                    "Widen the scope, or pass a value to look for.");
                NoteElsewhere();
                return 1;
            }

            // 直接把真实值域里的近似项端出来。指一条「去跑 values」的路是不够的:
            // 实测里 compClass 有 175 个取值,默认只出 25 个,照着建议跑一遍照样看不见答案,
            // 只能 --limit all 再自己 grep。近似项本来就在库里,算一次比让人跑两趟便宜。
            var space = ctx.Db.DistinctValues(path, scope, Limits.MaxLimit).Rows.Select(v => v.Value).ToList();

            // RimWorld 的约定:XML 里写的是 `Class="CompProperties_X"`,而落到 def 上的
            // comps[N].compClass 存的是被解析出来的 `CompX`。照 XML 抄过来的名字必然查不到,
            // 而这恰恰是从 grep XML 迁过来的人最先敲的那一条 —— skill 自己的示范就栽在这。
            var alt = value.Contains("CompProperties_") ? value.Replace("CompProperties_", "Comp") : null;

            // 先做精确关系:值域里存的是全限定名,调用方给的常是末段。这是**同一个名字**,
            // 不是「长得像」,所以不该交给模糊打分 —— 实测里 CompAmbientSound 对
            // RimWorld.CompAmbientSound 打分低于阈值,近似项一条都没出来。
            var close = space.Where(v => Tail(v).Equals(alt ?? value, StringComparison.OrdinalIgnoreCase))
                             .Concat(space.Where(v => alt is not null &&
                                                      Tail(v).Contains(Tail(alt), StringComparison.OrdinalIgnoreCase)))
                             .Distinct(StringComparer.Ordinal)
                             .Take(Limits.MaxSuggestions)
                             .ToList();

            if (close.Count == 0)
                close = FuzzyMatcher.Rank(space, alt ?? value).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            // 值域计数没有产地就是负资产:「out of 207 values」被读成「值的形态有讲究」,
            // 于是有人去试全限定名,而真因是 MapPortal 是**抽象基类**,6 个 def 用的是它的
            // 5 个子类。数字得连着「这些值来自哪些路径/哪些 def 类型」一起说。
            var cov = space.Count > 0 ? ctx.Db.ValueCoverage(path, scope, 3) : default;
            var provenance = space.Count == 0 ? "" :
                $", out of the {Tally.Complete(space.Count).Render("value")} found under " +
                (cov.Paths.Count > 0
                    ? string.Join(" / ", cov.Paths.Select(x => x.Path)) +
                      (cov.PathTotal > cov.Paths.Count ? $" (and {cov.PathTotal - cov.Paths.Count} more paths)" : "")
                    : $"'{path}'");

            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def has '{path}' set to {(exact ? "exactly " : "")}'{value}'{provenance}." +
                (close.Count > 0
                    ? $" Closest: {string.Join(", ", close)}." +
                      (alt is not null && close.FirstOrDefault(c => Tail(c).Equals(alt, StringComparison.OrdinalIgnoreCase)) is { } resolved
                          // 说破规律,不只是给一个名字 —— 否则同一个人下一个 comp 还会再敲错一次。
                          // 规律之外还得把**那条命令**给出来:读的人手上有了正确的名字,却还要
                          // 自己把它拼回一条命令行,而这一步正是「指了路却没给路」的老形状。
                          ? " The XML writes Class=\"CompProperties_X\"; this field holds the resolved CompX — " +
                            $"'rimsearcher find {path} {resolved}' is the query you meant."
                          // 有近似项时原先就到此为止,而没有近似项的那一支反倒指了路 ——
                          // 「给了个名字」不等于「说了下一步」:那三条只是最近的,真值域没看过。
                          : $" 'rimsearcher values {path} --limit all' lists the whole value domain.")
                    // 「如果 X 是抽象基类」原先无条件说,而它是一句**未经验证的猜测摆在
                    // 输出位置**,读的人会当结论用。判据从严(ClassNameShape:`True`、
                    // `.ogg`、`1.5` 全挡在外面),而且指的路换成本工具自己那条 ——
                    // 一条 code-search 就能证实或证伪,不必外包给别的东西。
                    : $" 'rimsearcher values {path} --limit all' lists them." +
                      (ClassNameShape.Looks(value)
                          ? $" If '{value}' is an abstract base class, no def names it directly, and its " +
                            "subclasses are what to look up instead: " +
                            $"'rimsearcher code-search \"class \\w+ : {ClassNameShape.Tail(value)}\\b\"' " +
                            "names them, and settles whether such a class exists at all."
                          : "")));
            NoteElsewhere();
            return 1;
        }

        static string Tail(string v)
        {
            var i = v.LastIndexOf('.');
            return i < 0 ? v : v[(i + 1)..];
        }

        // 这里不像 get 那样把默认值行滤掉:调用方点名了一个字段与一个值,「哪些 def 取到过它」
        // 的答案里就该有它们。但**为什么取到**要分得开 —— comps[N].compClass 一整批
        // 等于 CompShield,多半是 CompProperties_Shield 的声明里写死的,不是谁在 XML 里挑的。
        ctx.Report.Table("matches", ["def_name", "def_type", "path", "value", FieldDefault.Column, "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.Def.DefName,
                ["def_type"] = r.Def.DefType,
                ["path"] = r.Path,
                ["value"] = r.Value,
                [FieldDefault.Column] = FieldDefault.Render(r.Default),
                ["mod"] = r.Def.SourceMod,
            }).ToList());

        Advisory.NoteAuthoredSiblings(ctx, rows.Where(r => r.Default != Contract.DefaultState.Same)
                                                .Select(r => (r.Def.Id, r.Path)));
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(path, scope));
        return 0;
    }

    /// <summary>--value:不指名字段,直接问「哪个字段装着这段文本」。</summary>
    private static int ByValue(CommandContext ctx, string value, Snapshot.ScopeFilter scope, LimitValue limit,
                               bool exact, int offset)
    {
        var (rows, total, exactTotal) = ctx.Db.PathsWithValue(value, scope, limit.Effective,
            exact ? ValueMatch.Exact : ValueMatch.Substring, offset);

        if (rows.Count == 0 && offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("field path")} hold '{value}'.");
            return 1;
        }

        if (rows.Count == 0)
        {
            // R9:原先这句以「so this means the text is absent, not misspelt」收尾 —— 它特意
            // 堵掉了「你拼错了」这条退路,可快照索引的是叶子标量与 comps 的 compClass,
            // **嵌套 li / 多态子对象的运行时类型不在其中**(modExtensions[0] 的 Class=、
            // paramMappings[0].inParam 的 Class=)。于是「类真实存在且正在被这个 def 使用」
            // 与「这个类根本不存在」在输出上完全一样,而它只报了后者。
            // 类名形状的查询词最容易撞这一条,所以这时候必须把索引边界说出来。
            // 判据归一到 ClassNameShape:旧写法 `IsUpper(v[0]) || Contains('.')` 会把
            // `True`、`.ogg`、`1.5` 一并算成类名,于是这段索引边界跑到值查询上去说。
            var looksLikeType = ClassNameShape.Looks(value);
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No field in this snapshot holds a value {(exact ? "equal to" : "containing")} '{value}'" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") + "." +
                (exact ? " Drop --exact to match it as a substring." : "") +
                (looksLikeType
                    ? " If that is a class name: the snapshot indexes leaf scalars and a comp's compClass, but " +
                      "not the runtime type of nested <li Class=\"...\"> objects (modExtensions, sub-object " +
                      "parameters), so a class can be in use by a def and still be absent here. " +
                      $"'rimsearcher code-search \"class {ClassNameShape.Tail(value)}\\b\"' finds the class itself."
                    : ""));

            // 叠加不替换:上面那句说的是「这份快照里没有」,而别的快照里有没有算得出来。
            if (NameLookup.Elsewhere(ctx, db => db.PathsWithValue(
                    value, Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0,
                    exact ? ValueMatch.Exact : ValueMatch.Substring).Total, "field path")
                is { } line)
                ctx.Report.Notice(NoticeKind.NextStep, line);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total);

        // 子串命中不留痕,与 `--path` 是同一条纪律的值侧。`find --value Bullet` 命中
        // 每一个 `Bullet_*` 的字符串,而问的人多半只想要「值就是 Bullet 的那些」——
        // 拆不开这两档,「有一个字段的值就是它」与「有一堆值里碰巧含这几个字母」逐字同形。
        if (!exact && exactTotal < total)
            ctx.Report.Notice(NoticeKind.Filter,
                exactTotal == 0
                    ? $"No value here is exactly '{value}'; each match has it inside a longer value — see " +
                      "example_value. --exact would return nothing."
                    : $"Value exactly '{value}': {Tally.Complete(exactTotal).Render("field path")}; " +
                      $"containing it: {Tally.Complete(total - exactTotal).Render("field path")}. " +
                      "--exact keeps the first group only.");

        ctx.Report.Table("paths", ["path", "def_type", "defs", "example_value"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["def_type"] = r.DefType,
                ["defs"] = r.Defs,
                ["example_value"] = r.Sample,
            }).ToList());

        // 五轮 F1:这里原先按**结果里的每条路径**各查一次再求和。两处都错。
        // 一是求和把同一个被砍的 def 按它出现在几条路径上重复计数;二是只要结果里有一条
        // 路径叫 defName —— 而 `find --value` 命中一个 def 名时必然有 —— 那条路径的
        // 「同类型」就退化成全体 def 类型,这一项独自等于全库。实测报出 251 与 242,
        // 而快照总共 239:**子集计数大于全集**,却与一个正常计数逐字同形。
        // 按值一次问清,不按路径拆:表里那批 def 是「取到过这个值」选出来的,
        // 尾注担保的也必须是同一批。
        Completeness.NoteIndexedPathsOnly(ctx,
            ctx.Db.TruncatedDefsSharingValue(value, exact ? ValueMatch.Exact : ValueMatch.Substring, scope));
        return 0;
    }
}

public sealed class ListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "list",
        Aliases = ["ls"],
        Summary = "List every def of one type.",
        Positionals = [new PositionalSpec { Name = "defType", Help = "A def type such as ThingDef. 'rimsearcher types' lists them." }],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Scope,
            CommonOptions.Offset("defs"),
            new OptionSpec
            {
                Name = "class",
                Aliases = ["def-class", "runtime-class"],
                Placeholder = "<ClassName>",
                Help = "Only defs whose own class is this. Def types that hold several classes list them below the count.",
            },
        ],
        Examples =
        [
            "rimsearcher list HediffDef",
            "rimsearcher list CreepJoinerBaseDef --class CreepJoinerAggressiveDef",
            "rimsearcher list ThingDef --scope all,-vanilla --limit all",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                What = "one row per def: def_name, label, mod, plus 'class' when the bucket holds more " +
                       "than one def class.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Limit();
        var offset = ctx.Args.Offset();
        var wantClass = ctx.Args.Value("class");
        var scope = ctx.Scope();

        var (rows, total) = ctx.Db.ListByType(type, scope, limit.Effective, offset, wantClass);

        if (rows.Count == 0)
        {
            // 翻过头**先**判。它下面每一条分流问的都是「这个名字是什么」,而翻过头时
            // 那个名字明明查得好好的 —— 实测 `list ThingDef --offset 900` 从这里掉进
            // 「ThingDef 不是 def 类型」那一条,把一次翻页答成了一句彻头彻尾的假话。
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset,
                    $"this snapshot has {Tally.Complete(total).Render("def")} of type {type}" +
                    (wantClass is null ? "" : $" with class '{wantClass}'") + ".");
                return 1;
            }

            // 「这个 scope 里没有」不等于「快照里没有」。下面每一条判据都是 scope 过滤过的,
            // 所以 `--scope zh`(汉化包,一个 def 都不加)会让 `list ThingDef` 报出
            // 「No def type named 'ThingDef' in this snapshot」—— 一句彻头彻尾的假话。
            // 这与 CreepJoinerAggressiveDef 那条同形:分不清缺席的成因就报最强的那种。
            if (!scope.IsAll && offset == 0)
            {
                var (_, everywhere) = ctx.Db.ListByType(type, ctx.Unscoped(), 1, 0, wantClass);
                if (everywhere > 0)
                {
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"No def of type {type} is in scope '{scope.Expression}'" +
                        (wantClass is null ? "" : $" with class '{wantClass}'") +
                        $", but this snapshot has {Tally.Complete(everywhere).Render("def")} of it overall. " +
                        $"Drop --scope, or run 'rimsearcher mods' to see which mods the scope selects.");
                    return 1;
                }
            }

            // 「不是分桶键」不等于「不存在」。游戏只给「祖先链上没有非抽象 Def」的类型建库,
            // 于是 CreepJoinerAggressiveDef 的 def 全躺在 CreepJoinerBaseDef 桶里 —— 照直报
            // 「No def type named ...」就是把缺席说成了事实,而调用方没有任何办法看出区别。
            var holders = ctx.Db.TypesHoldingClass(type, scope);
            if (wantClass is null && holders.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{type}' is not a def type in this snapshot, but it is the class of " +
                    $"{Tally.Complete(holders.Sum(h => h.Count)).Render("def")}: " +
                    string.Join(", ", holders.Select(h => $"{h.Count} under {h.DefType}")) + ". " +
                    $"The game only gives a def database to types with no concrete Def ancestor, so subclasses " +
                    $"share their base's bucket. 'rimsearcher list {holders[0].DefType} --class {type}' lists them.");
                return 1;
            }

            if (wantClass is not null)
            {
                var present = ctx.Db.ClassesInType(type, scope);
                ctx.Report.Notice(NoticeKind.NextStep,
                    present.Count == 0
                        ? $"No def type named '{type}' in this snapshot. 'rimsearcher types' lists them all."
                        : $"No def of type {type} has class '{wantClass}'. That type holds " +
                          NameList.Render([.. present.Select(c => $"{c.Class} ({c.Count})")], Limits.MaxSuggestions) + ".");
                return 1;
            }

            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(scope).Select(t => t.Type)));
            return 1;
        }

        // 上游有 --offset 分页却拿不到总数,不知道翻到哪算到头(02-1)。这里总数恒在。
        ctx.Report.PageNotice("def", rows.Count, offset, total);

        // 桶里只有一种 class 时不平白多一列(ThingDef 一万多个 def 都是 Verse.ThingDef);
        // 异构时这一列是唯一能把子类型区分开的东西。
        var classes = ctx.Db.ClassesInType(type, scope);
        var heterogeneous = wantClass is null && classes.Count > 1;
        if (heterogeneous)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 数的是 class,名词原先写的是「def type」—— R7 的形状:计数的名词
                // 与实际所数的东西不是一回事,而这一句正长在「def 类型不等于运行时 class」
                // 那条区分上,说反了等于把要讲清的两件事又搅回一起。
                $"Type {type} holds {Tally.Complete(classes.Count).Render("def class")}: " +
                NameList.Render([.. classes.Select(c => $"{Tail(c.Class)} ({c.Count})")], Limits.MaxSuggestions) +
                ". Pass --class to pick one.");

        var columns = heterogeneous
            ? new[] { "def_name", "class", "label", "mod" }
            : ["def_name", "label", "mod"];

        ctx.Report.Table("defs", columns,
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["class"] = heterogeneous ? Tail(r.Class ?? "") : null,
                ["label"] = r.Label,
                ["mod"] = r.SourceMod,
            }).ToList());

        return 0;
    }

    private static string Tail(string v)
    {
        var i = v.LastIndexOf('.');
        return i < 0 ? v : v[(i + 1)..];
    }
}

public sealed class FieldsCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "fields",
        Aliases = ["paths", "schema"],
        Summary = "List the field paths that a def type actually uses, with how often each occurs.",
        Remarks =
            "Use this before 'find' when you are not sure what a field is called. The counts tell you whether a " +
            "path is universal for the type or only present on a handful of defs.\n\n" +
            "What is listed is every path the exporter recorded a value for. A field whose value was null on " +
            "every def of the type is in none of them, so a path missing here is not proof that the field does " +
            "not exist — for the shape of a nested object, read its class with 'code-search' and 'read'.",
        Positionals = [new PositionalSpec { Name = "defType", Help = "A def type such as ThingDef." }],
        Options =
        [
            CommonOptions.Limit("field paths"),
            new OptionSpec
            {
                // ThingDef 有 2973 条路径,默认只出 25 条。没有这个开关,调用方只能
                // `fields ThingDef | grep comps` —— 而 grep 会连同截断声明一起滤掉,
                // 于是「被截了」变成「没有」。管道会把声明区吃掉,所以筛选必须在工具里做。
                Name = "path",
                Arity = Arity.Multi,
                Aliases = ["paths", "contains", "match", "filter", "grep", "only"],
                Placeholder = "<text>",
                Help = "Only list paths containing this text. Repeat it to widen the selection.",
            },
            CommonOptions.Offset("field paths"),
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path comps",
            "rimsearcher fields HediffDef --limit all",
        ],
        JsonKeys =
        [
            new() { Key = "fields", What = "one row per field path: path, defs (how many defs use it)." },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Limit();
        var filters = ctx.Args.Values("path");
        var offset = ctx.Args.Offset();
        var (rows, total, whole) = ctx.Db.FieldPathsForType(type, limit.Effective, filters.FirstOrDefault(), offset);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("field path")} match in all.");
                return 1;
            }

            if (filters.Count > 0 && ctx.Db.FieldPathsForType(type, 1).Rows.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{type}' has field paths, but none contains '{filters[0]}'. Drop --path to see them all.");
                return 1;
            }
            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(ctx.Scope()).Select(t => t.Type)));
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total, "narrow with --path <text>.");

        // 与 `get --path` 同一条纪律:子串匹配不留痕。这里的代价更大 —— 这条命令是
        // 「这个类型有没有这个字段」的正式问法,而「一条都不是整段」正是「没有」的形状。
        if (filters.Count > 0 && whole < total)
            ctx.Report.Notice(NoticeKind.Filter,
                whole == 0
                    ? $"None of those has '{filters[0]}' as a whole path segment — each contains it inside a " +
                      $"longer name, so '{filters[0]}' may not be a field of '{type}' at all."
                    : $"Whole path segment: {Tally.Complete(whole).Render("field path")}; " +
                      $"inside a longer name: {Tally.Complete(total - whole).Render("field path")}.");

        ctx.Report.Table("fields", ["path", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["defs"] = r.Count,
            }).ToList());
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsOfType(type));
        return 0;
    }
}

public sealed class ValuesCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "values",
        Aliases = ["distinct"],
        Summary = "List the distinct values a field takes, most common first.",
        Remarks =
            "Answers 'what am I allowed to put here' and 'which classes are actually in use' without reading any XML. " +
            "A bare name such as compClass matches every path ending in it, so the table above the values tells you " +
            "which full paths and which def types actually contributed, and how many defs are covered.",
        Positionals = [new PositionalSpec { Name = "fieldPath", Help = "A field path or its last segment, such as compClass." }],
        Options = [CommonOptions.Limit("values"), CommonOptions.Offset("values"), CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher values compClass",
            "rimsearcher values expandingIconTexture --type WorldObjectDef",
            "rimsearcher values thingClass --scope vanilla",
        ],
        JsonKeys =
        [
            new() { Key = "values", What = "one row per distinct value: value, defs." },
            new()
            {
                Key = "field",
                What = "an object, not an array: which full paths and def types the values came from " +
                       "(matched_paths, def_types, defs_with_field). A bare name matches by suffix, so this " +
                       "says what was actually pooled.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var path = ctx.Args.Positional(0)!;
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");
        var offset = ctx.Args.Offset();
        var (rows, total) = ctx.Db.DistinctValues(path, scope, limit.Effective, type, offset);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"'{path}' takes {Tally.Complete(total).Render("value")} in all.");
                return 1;
            }

            // 三种成因,要的下一步不同 —— 与 `find` 的分流同形。原先只分了 --type 那一档,
            // 于是 `values defName --scope <没有 def 的 mod>` 回「本快照没有 defName」,
            // 而不带 scope 时每个 def 都有它:空是 scope 造的,句子却记在了快照头上。
            var withoutType = type is not null && ctx.Db.FieldPathExists(path, scope);
            var wideScope = ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config);
            var outsideScope = !withoutType && !scope.IsAll && ctx.Db.FieldPathExists(path, wideScope);

            ctx.Report.Notice(NoticeKind.NextStep,
                withoutType
                    ? $"'{path}' exists in this snapshot but not on any {type}. Drop --type to see which def types have it."
                    : outsideScope
                        ? $"'{path}' exists in this snapshot but no def has it within --scope {scope.Expression}. " +
                          "Widen the scope, or run 'rimsearcher mods' to see what this scope could have matched."
                        : $"No def in this snapshot has a field path ending in '{path}'" +
                          (scope.IsAll ? "" : $" (nor anywhere outside --scope {scope.Expression})") + ". " +
                          $"'rimsearcher fields <DefType>' lists the paths a type actually has, and " +
                          $"'rimsearcher find --value <text>' finds which path holds a value you already know.");
            return 1;
        }

        // 值的产地。后缀匹配天然会把语义不同的路径并进一张表,不说清就会被读成
        // 「这个字段到处都是这个值」—— 实测里 `values damageAmountBase` 正是这样险些骗到人。
        var cov = ctx.Db.ValueCoverage(path, scope, Limits.MaxSuggestions, type);

        // 这里的省略不是 NameList 那种「我取了前几条」—— cov.Paths 已经在 SQL 侧截过,
        // 手上根本没有第 4 条起的名字。分母只有 cov.PathTotal 知道,所以照实拼。
        var pathList = string.Join(", ", cov.Paths.Select(x => $"{x.Path} ({x.Count})"));
        if (cov.PathTotal > cov.Paths.Count) pathList += $", and {cov.PathTotal - cov.Paths.Count} more";

        // 「N of M」是覆盖率的分母。少了它,一条 `Verse.Thing (7)` 分不清是「只有 7 个 def
        // 这么写」还是「导出漏了一千多个」—— 实测里有人靠手工把 110 行加起来才敢下结论。
        var typeList = string.Join(", ", cov.DefTypes.Select(x =>
            $"{x.DefType} ({x.Count} of {ctx.Db.CountDefsOfType(x.DefType, scope)})"));

        ctx.Report.Detail("field", [
            new("matched_paths", pathList),
            new("def_types", typeList),
            new("defs_with_field", (object)cov.DefsCovered),
        ]);

        ctx.Report.PageNotice("value", rows.Count, offset, total);
        ctx.Report.Table("values", ["value", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["value"] = r.Value,
                ["defs"] = r.Count,
            }).ToList());
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(path, scope));
        return 0;
    }
}

public sealed class TypesCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "types",
        Aliases = ["def-types"],
        Summary = "List every def type in the snapshot with how many defs it has.",
        Options = [CommonOptions.Limit("def types") with { Default = "all" }, CommonOptions.Scope],
        Examples = ["rimsearcher types", "rimsearcher types --scope all,-vanilla"],
        JsonKeys = [new() { Key = "types", What = "one row per def type: def_type, defs." }],
    };

    public override int Run(CommandContext ctx)
    {
        var scope = ctx.Scope();
        var all = ctx.Db.Types(scope);

        // 零行是 exit 1(R12 约定),`types` 原先无条件 return 0 —— 按退出码分流的脚本
        // 会把「0 def types.」读成「查到了」。而那句话本身也不许把 scope 造成的空
        // 说成快照的空:整份快照的数就在手边,算一次比让人再跑一趟便宜。
        if (all.Count == 0)
        {
            if (scope.IsAll)
                ctx.Report.Notice(NoticeKind.NextStep,
                    "This snapshot holds no defs at all. 'rimsearcher snapshot list' shows when it was taken, " +
                    "and 'rimsearcher export' rebuilds it.");
            else
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def in this snapshot comes from --scope {scope.Expression}. Snapshot-wide the figure is " +
                    Tally.Complete(ctx.Db.Types(ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config)).Count)
                         .Render("def type") + ". 'rimsearcher mods' lists what this snapshot actually has.");
            return 1;
        }

        var limit = ctx.LimitOrAll();
        var rows = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        ctx.Report.CountNotice(Tally.Of(rows.Count, all.Count), "def type", "pass --limit all for the rest.");
        ctx.Report.Table("types", ["def_type", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_type"] = r.Type,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }
}

public sealed class ModsCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "mods",
        Summary = "List the mods that were active when the snapshot was taken, in load order.",
        Remarks = "Load order matters: it is the order in which PatchOperations were applied, so it is part of the snapshot's identity.",
        // 默认全出:装了什么 mod 是快照身份的一部分,截一半没有意义。但 --limit 还是收 ——
        // 07 实证里它是被发明得最多的参数,而对一条列举命令拒绝它,读起来像「这里不能限量」,
        // 实际只是「这里不需要」。严格模式该拦的是拼错的名字,不是合理的期待。
        Options = [CommonOptions.Limit("mods") with { Default = "all" }],
        Examples = ["rimsearcher mods"],
        JsonKeys = [new() { Key = "mods", What = "one row per mod, in load order: order, package_id, name, version." }],
    };

    public override int Run(CommandContext ctx)
    {
        var all = ctx.Db.Mods;
        var limit = ctx.LimitOrAll();
        var mods = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        ctx.Report.CountNotice(Tally.Of(mods.Count, all.Count), "mod",
            "pass --limit all to see the whole load order.");
        ctx.Report.Table("mods", ["order", "package_id", "name", "version"],
            mods.Select((m, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["order"] = i,
                ["package_id"] = m.PackageId,
                ["name"] = m.Name,
                ["version"] = m.Version,
            }).ToList());
        return 0;
    }
}

internal static class DefTypeMiss
{
    /// <summary>
    /// 「这个快照里没有这个 def 类型」的唯一产地。<c>list</c> 与 <c>fields</c> 原先各写一份,
    /// 逐字相同 —— 两份逐字相同的句子只有一个结局:改一处、忘一处,而两条命令回答同一个
    /// 问题时口径不一致,读的人会以为差别有意义。
    /// </summary>
    public static string Say(string typed, IEnumerable<string> known)
        => $"No def type named '{typed}' in this snapshot." +
           Suggestion.Say(Suggestion.Closest(known, typed), " 'rimsearcher types' lists them all.");
}

internal static class Completeness
{
    /// <summary>
    /// 反查类命令的完整性尾注。
    ///
    /// 快照里有两套「完整」在互相打架:get 会为**单个 def** 声明「导出时砍掉了 N 个字段」,
    /// 而 find / values / fields 的计数以**已索引路径**为界 —— 某个 def 的 comps 在导出时被砍,
    /// 它就从 find 的结果里静默消失,而这恰恰是「一共有哪些」这类问题的致命伤。
    /// 实测里五条轨迹各自带着一句消不掉的免责声明交了答案。
    ///
    /// 但尾注本身也不能变成新的免责声明(00 论据 3)。所以它收窄到「与本次结果**同类型**的 def
    /// 里真有被砍的」才出声:不出声时,「完整」就是无条件的,而不是「大概吧」。
    /// </summary>
    /// <summary>
    /// 末尾那条命令要**走得到刚才说的那批**。原先一律给裸命令,而它列的是全库 239 条,
    /// 尾注刚说的却是其中某几个类型的一小批 —— 照着跑一遍拿到的是另一个集合,
    /// 而两者的输出形状一模一样。类型不多时逐个带上 --type;多到列不下就说清列不下。
    /// </summary>
    public static void NoteIndexedPathsOnly(CommandContext ctx, TruncationScope affected)
    {
        if (affected.Count == 0) return;

        var types = affected.Types;
        var shown = types.Take(Limits.MaxSuggestions).ToList();
        var cmd = "rimsearcher snapshot truncated" +
                  string.Concat(shown.Select(t => $" --type {t}"));
        ctx.Report.Notice(NoticeKind.Boundary,
            $"Counted over indexed field paths only: {Tally.Complete(affected.Count).Render("def")} of the same " +
            "def types lost fields at export time and could belong here without showing up. " +
            $"'{cmd}' lists " +
            (shown.Count == types.Count
                ? "them."
                : $"the biggest {shown.Count} of those types; still in the same position — " +
                  $"{Tally.Complete(types.Count - shown.Count).Render("def type")}, which the bare " +
                  "'rimsearcher snapshot truncated' covers along with everything else."),
            footnote: true);
    }
}

internal static class Advisory
{
    /// <summary>
    /// 环境外翻译的聚合尾注(06 上下文预算:逐条标注聚合成一行,不是每行挂一句)。
    /// </summary>
    public static void NoteOutsideTranslations(CommandContext ctx, IEnumerable<string> defNames)
    {
        var n = ctx.Db.CountTranslationsOutside(defNames);
        if (n == 0) return;
        ctx.Report.Notice(NoticeKind.Advisory,
            $"{Tally.Complete(n).Render("def")} above also matched language files from mods that are installed " +
            "but were not enabled in this snapshot; those translations are searchable but were not in effect. " +
            "'rimsearcher get <defName>' shows which.", footnote: true);
    }

    /// <summary>
    /// 同一块 <c>comps[N]</c> 里、有人设过的兄弟字段(同样是聚合成一行,不是每行挂一句)。
    ///
    /// 第五轮实测:`minFuelCost=50` 盖掉同块的 `fuelPerTile=3`,差 16 倍,而只列出后者的
    /// 那张表干净、计数明确、一条警告都没有 —— 错结论就是从那张表上读出来的。
    /// 一句话只做一件事:**点名**,不解释谁盖谁。工具证得了「这几个字段有人设过、
    /// 而且与你看的这个同处一块」,证不了「它们的关系是什么」,后者要读源码。
    ///
    /// 第三道收窄在调用侧(<paramref name="shown"/> 已经筛过):只有当**你看的这一行自己**
    /// 是有人设过的值时才提示。判别字段(compClass / thingClass / workerClass)按定义
    /// 就是声明默认值,而 `find compClass CompShield` 恰恰是文档推荐的那条主查询 ——
    /// 在它上面挂一句「同块还有 energyMax」是纯噪音,而噪音要在所有调用上收税。
    /// </summary>
    public static void NoteAuthoredSiblings(CommandContext ctx, IEnumerable<(long DefId, string Path)> shown)
    {
        var names = ctx.Db.AuthoredSiblings(shown);
        if (names.Count == 0) return;
        ctx.Report.Notice(NoticeKind.Advisory,
            $"Set by hand in the same block as the rows above: {NameList.Render(names, Limits.MaxSuggestions)}. " +
            "Fields in one comps[N] entry bound and override each other, and this table shows only the one " +
            "asked for. 'rimsearcher get <defName> --path <block>' lists the whole block.", footnote: true);
    }
}
