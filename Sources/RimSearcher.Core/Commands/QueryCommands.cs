using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 一个名字下挂着的东西归不归这个 def —— 同名跨 def 类型是 RimWorld 常态,而快照里
/// 有三张表按 <c>def_name</c> 存东西(译文、继承层、defs 自己),按名字关联就会串味(R2)。
/// </summary>
internal static class DefTypes
{
    /// <summary>
    /// 两个 <c>def_type</c> 是否指同一个类型。
    ///
    /// 需要这个判断而不是直接 <c>==</c>,是因为两者不同源:继承层的是 XML 根元素名,
    /// defs 表的是 <c>AllDefTypesWithDatabases</c> 的桶名(只产出「祖先链上没有非抽象 Def」
    /// 的类型)。实测本机 modded 快照,在**没有同名歧义**的 def 里有 26 个对不上,三种形状:
    ///   Blindhealer             CreepJoinerFormKindDef          → PawnKindDef        (子类落进基类桶)
    ///   AncientComplex_Loot     ComplexLayoutDef                → LayoutDef          (同上)
    ///   DefaultCareForColonist  Defaults.Defs.DefaultSettingDef → DefaultSettingDef  (带命名空间)
    /// 前两种由调用点的「无歧义时回退到唯一候选」兜住;第三种在**同时有同名歧义**时连回退都
    /// 走不到,所以这里补一层:全等优先,再退到去掉命名空间后相等。
    ///
    /// 次选那一层理论上能把两个 mod 各自的 <c>A.FooDef</c> / <c>B.FooDef</c> 配到一起,
    /// 但调用点只在候选里挑,配错的前提是同一个 defName 下同时存在这两个类型 —— 比
    /// 「该显示的东西不显示」罕见得多,而后者正是这一轮反复在修的那类错(缺席被读成事实)。
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string Leaf(string s) => s[(s.LastIndexOf('.') + 1)..];
        return string.Equals(Leaf(a), Leaf(b), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SearchCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "search",
        Aliases = ["find-def", "s"],
        Summary = "Find defs by name, label, description, or translated text.",
        Remarks =
            "Matching is in three stages and stops at the first one that finds anything: full-text search, " +
            "then a prefix pass, then fuzzy identifier matching that tolerates typos and CamelCase initials. " +
            "You never need to add '*' yourself. Translated text is indexed alongside the English, so a " +
            "Chinese label finds the def even though the label column keeps the value the game had at export time.",
        Positionals = [new PositionalSpec { Name = "query", Help = "Words, a def name, or part of one." }],
        Options = [CommonOptions.Limit("defs"), CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher search shield",
            "rimsearcher search \"psychic shock\" --type ThingDef",
            "rimsearcher search CompShield --scope all,-vanilla",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var query = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");

        var (rows, total) = ctx.Db.SearchFts(query, scope, type, limit.Effective);
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
                    var (more, _) = ctx.Db.ByNames([.. extra.Take(room)], room);
                    rows = [.. rows, .. more];
                    addedBySubstring = more.Count;
                }
            }
        }

        if (rows.Count == 0)
        {
            // 02-7 的对策:调用方不该需要知道 '*' 才搜得到复合名,更不该知道打错一个字母就归零。
            var names = ctx.Db.AllDefNames(scope);
            var (bare, kind) = FuzzyMatcher.StripKindPrefix(query);
            var ranked = FuzzyMatcher.Rank(names, bare).Take(limit.Effective).Select(t => t.Text).ToList();
            if (ranked.Count > 0)
            {
                (rows, total) = ctx.Db.ByNames(ranked, limit.Effective);
                total = ranked.Count;
                how = kind is null ? "fuzzy" : $"fuzzy (ignoring the '{kind}:' prefix, which defs do not use)";
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No def matched '{query}' as written; these are the closest names by spelling.");
            }
        }

        var tally = Tally.Of(rows.Count, Math.Max(total, rows.Count));
        if (rows.Count > 0)
        {
            ctx.Report.CountNotice(tally, "def",
                limit.IsAll ? "narrow the query." : "raise --limit or narrow the query.");

            if (addedBySubstring > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"That includes {Tally.Complete(addedBySubstring).Render("def")} found by scanning names for " +
                    $"'{query}' as a substring; full-text matching alone splits names at word starts, so it misses " +
                    "the query in the middle of a compound name.");
        }
        if (rows.Count == 0)
        {
            // 值域必须说清。search 覆盖的是 defName / label / description / 译文 ——
            // **不含** C# 类名。实测里有人拿 CompShield 来搜,零结果被读成「模糊匹配坏了」,
            // 而错误消息当时把他指向 code-search:那条路找得到类,却永远找不到用它的 def。
            // 「像个类名」= 带命名空间点号,或者驼峰(首字母大写且内部还有大写)。
            var looksLikeClass = query.Contains('.') ||
                                 (query.Length > 2 && char.IsUpper(query[0]) && query.Skip(1).Any(char.IsUpper));
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing matched '{query}' in this snapshot" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                ". This command covers def names, labels, descriptions and translations, not C# class names." +
                (looksLikeClass
                    ? $" That looks like a class: 'rimsearcher find compClass {query}' (or thingClass, workerClass) " +
                      "finds the defs that use it, and 'rimsearcher code-search' searches the source text."
                    : " 'rimsearcher types' lists what kinds of def this snapshot holds."));
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
        var t = ctx.Db.Translations(r.DefName)
                      .Where(x => x.DefType is null || DefTypes.Same(x.DefType, r.DefType))
                      .FirstOrDefault(x => Has(x.Translated) || Has(x.Original));
        return t is not null ? t.Path : "indexed text";
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
            // 这件事变成可判定的:名字在 xml_nodes 里就点名说它是什么、去哪儿看。
            var node = ctx.Db.NodesNamed(name).FirstOrDefault();
            if (node is not null)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{name}' is an XML node in {node.SourceMod} ({node.SourceFile}) but never becomes a def" +
                    (node.Abstract ? " — it is Abstract=\"True\"" : "") +
                    ", so 'get' cannot show it. 'rimsearcher inherit " + name + "' shows what it inherits from, " +
                    "what inherits from it, and which concrete child to read the merged values off.");
                return 1;
            }

            var names = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            var close = FuzzyMatcher.Rank(names, name).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def is named '{name}' in this snapshot." +
                (close.Count > 0 ? $" Closest names: {string.Join(", ", close)}." : " Try 'rimsearcher search' instead."));
            return 1;
        }

        var limit = ctx.Args.Limit(fallback: Limits.DefaultFieldsPerDef);
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

            var (fields, matched, total) = ctx.Db.Fields(def.Id, limit.Effective, paths);
            ctx.Report.Table("fields", ["path", "value"],
                fields.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                }).ToList());

            // 多个 def 同名时,截断声明必须指名道姓 —— 否则两条「Showing 5 of N fields」
            // 并排出现,读者无从知道哪条管哪个 def。
            var whose = matches.Count == 1 ? "" : $" of {def.DefName} ({def.DefType})";
            if (paths.Count > 0)
            {
                // 过滤后为空**不等于** def 没有这些字段,只等于没有路径含这段文本。
                // 这两件事在输出上长得一样,所以必须由声明区把它们分开。
                if (matched == 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"No field path{whose} contains {Join(paths)}; the def does have " +
                        $"{Tally.Complete(total).Render("field")}. Drop --path to see them.");
                else
                {
                    // 这是调用方自己要的过滤,不是截断。机器侧靠 kind 分类,混用会让
                    // 「我主动只要 driverClass」被扫 notes 的下一位读成「结果不完整」。
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"{Tally.Complete(matched).Render("field")}{whose} " +
                        $"match {Join(paths)}, out of {total} on the def.");
                    if (fields.Count < matched)
                        ctx.Report.Notice(NoticeKind.Truncation,
                            $"Showing {Tally.Of(fields.Count, matched).Render("field")}; raise --limit for the rest.");
                }
            }
            else
            {
                ctx.Report.CountNotice(Tally.Of(fields.Count, total), "field",
                    $"pass --limit all{(matches.Count == 1 ? "" : $" (this is {def.DefName})")} " +
                    "for the rest, or --path <text> to pick out the ones you want.");
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
                        $"'{def.DefName}.<field>' with no def type, and {allMatches.Count} defs share this name. " +
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
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();

        if (ctx.Args.Value("value") is { Length: > 0 } anyValue)
            return ByValue(ctx, anyValue, scope, limit, ctx.Args.Flag("exact"));

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

        var (rows, total) = ctx.Db.FindByField(path, value, exact, scope, limit.Effective);

        if (rows.Count > 0)
            ctx.Report.CountNotice(Tally.Of(rows.Count, total), "def", "raise --limit to see the rest.");

        if (rows.Count == 0)
        {
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
                return 1;
            }

            if (value is null)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{path}' exists in this snapshot but no def has it within --scope {scope.Expression}. " +
                    "Widen the scope, or pass a value to look for.");
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
                      (alt is not null && close.Any(c => Tail(c).Equals(alt, StringComparison.OrdinalIgnoreCase))
                          // 说破规律,不只是给一个名字 —— 否则同一个人下一个 comp 还会再敲错一次。
                          ? " The XML writes Class=\"CompProperties_X\"; this field holds the resolved CompX."
                          : "")
                    : $" 'rimsearcher values {path} --limit all' lists them. If '{value}' is an abstract base " +
                      "class, no def names it directly: get its subclasses from the decompiler first, then " +
                      "look each one up."));
            return 1;
        }

        static string Tail(string v)
        {
            var i = v.LastIndexOf('.');
            return i < 0 ? v : v[(i + 1)..];
        }

        ctx.Report.Table("matches", ["def_name", "def_type", "path", "value", "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.Def.DefName,
                ["def_type"] = r.Def.DefType,
                ["path"] = r.Path,
                ["value"] = r.Value,
                ["mod"] = r.Def.SourceMod,
            }).ToList());

        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(path, scope));
        return 0;
    }

    /// <summary>--value:不指名字段,直接问「哪个字段装着这段文本」。</summary>
    private static int ByValue(CommandContext ctx, string value, Snapshot.ScopeFilter scope, LimitValue limit,
                               bool exact)
    {
        var (rows, total) = ctx.Db.PathsWithValue(value, scope, limit.Effective, exact);

        if (rows.Count == 0)
        {
            // R9:原先这句以「so this means the text is absent, not misspelt」收尾 —— 它特意
            // 堵掉了「你拼错了」这条退路,可快照索引的是叶子标量与 comps 的 compClass,
            // **嵌套 li / 多态子对象的运行时类型不在其中**(modExtensions[0] 的 Class=、
            // paramMappings[0].inParam 的 Class=)。于是「类真实存在且正在被这个 def 使用」
            // 与「这个类根本不存在」在输出上完全一样,而它只报了后者。
            // 类名形状的查询词最容易撞这一条,所以这时候必须把索引边界说出来。
            var looksLikeType = value.Length > 2 && !value.Any(char.IsWhiteSpace) &&
                                (char.IsUpper(value[0]) || value.Contains('.'));
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No field in this snapshot holds a value {(exact ? "equal to" : "containing")} '{value}'" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                (exact ? ". Drop --exact to match it as a substring." : "") +
                (looksLikeType
                    ? " If that is a class name: the snapshot indexes leaf scalars and a comp's compClass, but " +
                      "not the runtime type of nested <li Class=\"...\"> objects (modExtensions, sub-object " +
                      "parameters), so a class can be in use by a def and still be absent here. " +
                      "'rimsearcher code-search' finds the class itself."
                    : ""));
            return 1;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "field path", "raise --limit to see the rest.");
        ctx.Report.Table("paths", ["path", "def_type", "defs", "example_value"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["def_type"] = r.DefType,
                ["defs"] = r.Defs,
                ["example_value"] = r.Sample,
            }).ToList());

        Completeness.NoteIndexedPathsOnly(ctx,
            rows.Select(r => r.Path).Distinct(StringComparer.Ordinal)
                .Sum(pth => ctx.Db.TruncatedDefsSharingPath(pth, scope)));
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
            new OptionSpec
            {
                Name = "offset",
                Aliases = ["skip", "start"],
                Placeholder = "<n>",
                Help = "Skip this many defs before listing. The total is always reported, so you can tell when you have reached the end.",
                Default = "0",
            },
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
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var offset = ctx.Args.Int("offset", 0);
        var wantClass = ctx.Args.Value("class");
        var scope = ctx.Scope();

        var (rows, total) = ctx.Db.ListByType(type, scope, limit.Effective, offset, wantClass);

        if (rows.Count == 0)
        {
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
                          string.Join(", ", present.Take(Limits.MaxSuggestions).Select(c => $"{c.Class} ({c.Count})")) +
                          (present.Count > Limits.MaxSuggestions ? $", and {present.Count - Limits.MaxSuggestions} more" : "") + ".");
                return 1;
            }

            var types = ctx.Db.Types(scope).Select(t => t.Type).ToList();
            var close = FuzzyMatcher.Rank(types, type).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                offset > 0 && total > 0
                    ? $"--offset {offset} is past the end; this snapshot has {Tally.Complete(total).Render("def")} of type {type}."
                    : $"No def type named '{type}' in this snapshot." +
                      (close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : " 'rimsearcher types' lists them all."));
            return 1;
        }

        // 上游有 --offset 分页却拿不到总数,不知道翻到哪算到头(02-1)。这里总数恒在。
        var shownSoFar = offset + rows.Count;
        ctx.Report.CountNotice(
            shownSoFar < total ? Tally.Of(rows.Count, total) : Tally.Complete(rows.Count),
            "def",
            $"{shownSoFar} of {total} listed so far; pass --offset {shownSoFar} for the next page.");

        // 桶里只有一种 class 时不平白多一列(ThingDef 一万多个 def 都是 Verse.ThingDef);
        // 异构时这一列是唯一能把子类型区分开的东西。
        var classes = ctx.Db.ClassesInType(type, scope);
        var heterogeneous = wantClass is null && classes.Count > 1;
        if (heterogeneous)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Type {type} holds {Tally.Complete(classes.Count).Render("def type")} of def class: " +
                string.Join(", ", classes.Take(Limits.MaxSuggestions).Select(c => $"{Tail(c.Class)} ({c.Count})")) +
                (classes.Count > Limits.MaxSuggestions ? $", and {classes.Count - Limits.MaxSuggestions} more" : "") +
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
            "path is universal for the type or only present on a handful of defs.",
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
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path comps",
            "rimsearcher fields HediffDef --limit all",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var filters = ctx.Args.Values("path");
        var (rows, total) = ctx.Db.FieldPathsForType(type, limit.Effective, filters.FirstOrDefault());

        if (rows.Count == 0)
        {
            if (filters.Count > 0 && ctx.Db.FieldPathsForType(type, 1).Rows.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{type}' has field paths, but none contains '{filters[0]}'. Drop --path to see them all.");
                return 1;
            }
            var types = ctx.Db.Types(ctx.Scope()).Select(t => t.Type).ToList();
            var close = FuzzyMatcher.Rank(types, type).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def type named '{type}' in this snapshot." +
                (close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : " 'rimsearcher types' lists them all."));
            return 1;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "field path",
            "raise --limit, or narrow with --path <text>.");
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
        Options = [CommonOptions.Limit("values"), CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher values compClass",
            "rimsearcher values expandingIconTexture --type WorldObjectDef",
            "rimsearcher values thingClass --scope vanilla",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var path = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");
        var (rows, total) = ctx.Db.DistinctValues(path, scope, limit.Effective, type);

        if (rows.Count == 0)
        {
            var withoutType = type is not null && ctx.Db.FieldPathExists(path, scope);
            ctx.Report.Notice(NoticeKind.NextStep,
                withoutType
                    ? $"'{path}' exists in this snapshot but not on any {type}. Drop --type to see which def types have it."
                    : $"No def in this snapshot has a field path ending in '{path}'. " +
                      $"'rimsearcher fields <DefType>' lists the paths a type actually has, and " +
                      $"'rimsearcher find --value <text>' finds which path holds a value you already know.");
            return 1;
        }

        // 值的产地。后缀匹配天然会把语义不同的路径并进一张表,不说清就会被读成
        // 「这个字段到处都是这个值」—— 实测里 `values damageAmountBase` 正是这样险些骗到人。
        var cov = ctx.Db.ValueCoverage(path, scope, Limits.MaxSuggestions, type);

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

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "value", "raise --limit to see the rest.");
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
    };

    public override int Run(CommandContext ctx)
    {
        var scope = ctx.Scope();
        var all = ctx.Db.Types(scope);
        var limit = ctx.Args.Value("limit") is null ? LimitValue.All : ctx.Args.Limit();
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
    };

    public override int Run(CommandContext ctx)
    {
        var all = ctx.Db.Mods;
        var limit = ctx.Args.Value("limit") is null ? LimitValue.All : ctx.Args.Limit();
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
    public static void NoteIndexedPathsOnly(CommandContext ctx, int affected)
    {
        if (affected == 0) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            $"Counted over indexed field paths only: {Tally.Complete(affected).Render("def")} of the same def types " +
            "lost fields at export time and could belong here without showing up. " +
            "'rimsearcher snapshot truncated' lists them.", footnote: true);
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
}
