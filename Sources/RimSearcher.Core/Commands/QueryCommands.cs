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
            new() { Key = "defs", Rows = true, What = "one row per matching def: def_name, def_type, label, matched_on, mod." },
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

        // FTS 分词按分隔符与驼峰**词首**切,查询词落在名字中段时命中不了
        // (`VoidNode` 找不到 `MonolithGleamingVoidNode`),所以补一遍子串扫描。
        if (IsCompoundToken(query))
        {
            // 去重在 SQL 侧对**整个 FTS 命中集**做,不是对已显示的行做;全量算进 total,
            // 只取得下的进 rows —— 否则 total 会跟着 --limit 变形。
            var extra = ctx.Db.NamesContainingUnmatched(query, scope, type);
            if (extra.Count > 0)
            {
                total += extra.Count;
                var room = Math.Max(0, limit.Effective - rows.Count);
                if (room > 0)
                {
                    // 结果集是「FTS 命中」接「子串补扫」两段拼起来的,翻页走的是拼好的那一条
                    // 序列:FTS 段已经在 SQL 里跳过 offset,余下的偏移量从这里接着扣。
                    var skipHere = Math.Max(0, offset - ftsTotal);
                    var (more, _) = ctx.Db.ByNames([.. extra.Skip(skipHere).Take(room)], room);
                    rows = [.. rows, .. more];
                    addedBySubstring = more.Count;
                }
            }
        }

        // 查询词里一个字母数字都没有:FTS 侧**根本没被问过**,它给的零是关于查询词的。
        // 不先说破的话,下面那条兜底会拿着这个零去断言「什么都没匹配到」,再用
        // `LIKE '%<原串>%'` 去扫 —— 空串是每一行的子串,于是 `search ''` 顶着一句
        // 「没有」印出装机库上的 9963 条。零结果与「问都没问」在这里必须不同形。
        var nothingToMatch = FtsText.HasNothingToMatch(query);
        if (nothingToMatch)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{query}' holds no letter or digit, and only those take part in full-text matching, so the " +
                "index was never asked about it: whatever appears below came from scanning text for " +
                $"'{query}' as a plain substring." +
                // 空串这一档单说:它是每一行的子串,那个计数是被扫的那张表的大小,
                // 不是一个结果。别的标点(`.`)只是命中得宽,不是必然命中全体。
                (query.Length == 0
                    ? " An empty query is a substring of every row, so any count below is the size of what was " +
                      "scanned rather than an answer. 'rimsearcher list <DefType>' enumerates on purpose."
                    : ""));

        // 译文原文那一侧的兜底:FTS 只索引 translated,中文快照上英文原名一个也搜不到。
        // **必须排在模糊回退之前** —— 否则英文查询会先被一批拼写相近的中文名挤掉真答案。
        if (rows.Count == 0 && offset == 0)
        {
            var byOriginal = ctx.Db.NamesByTranslationOriginal(query, scope, type);
            if (byOriginal.Count > 0)
            {
                (rows, total) = ctx.Db.ByNames(byOriginal, limit.Effective);
                how = "translation original";
                ctx.Report.Notice(NoticeKind.Boundary,
                    // 首句是一个**断言**,而它只在 FTS 真被问过时才成立。查询词没内容时
                    // FTS 的零什么也没证明,照印就是拿没查过的事当查过的结论。
                    (nothingToMatch
                        ? "These defs have it "
                        : $"No name, label or translated text in this snapshot contains '{query}'; these defs have it ") +
                    "in the original text a translation replaced. This snapshot's language is " +
                    $"{ctx.Db.Meta.Language}, so the English wording survives only where a translation " +
                    "recorded what it was translated from.");
            }
        }

        // 模糊回退只在**第一页**做:末页之后 rows 也为空,那时给一批「拼写相近的名字」
        // 会读成前面那些命中不作数。
        if (rows.Count == 0 && offset == 0)
        {
            // 候选先去重再打分:AllDefNames 是**按 def 一行**给的,一名两 def 的名字
            // (如 Firefoam)在候选集里会出现两次。
            var names = ctx.Db.AllDefNames(scope).Distinct(StringComparer.Ordinal).ToList();
            var (bare, kind) = FuzzyMatcher.StripKindPrefix(query);
            var ranked = FuzzyMatcher.Rank(names, bare).Take(limit.Effective).Select(t => t.Text).ToList();
            if (ranked.Count > 0)
            {
                // total 用 ByNames 报的**行数**而不是名字数 —— 一个名字可以带两行。
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
            // 翻过了头不是「没有这个东西」,分开说,否则一次翻页会被读成一次否定。
            ctx.Report.PastEnd(offset, $"'{query}' matched {Tally.Complete(total).Render("def")} in all.");
        }
        else if (rows.Count == 0)
        {
            // 值域(覆盖 defName / label / description / def 侧译文,**不含** C# 类名与
            // Languages/*/Keyed)是恒定知识,归 SKILL.md;这里只说本次的事实。
            //
            // 此前这里整段复读那份清单,而下面那句在算得出落点时正要说同一件事、还带真参数:
            // 「not the UI strings under Languages/*/Keyed … that 'rimsearcher keyed' reads」
            // 后面紧跟着「'没有电力' is interface text … 'rimsearcher keyed 没有电力' shows
            // the full row」。占位符版本在前、实参版本在后,量过 227 字节。
            //
            // 算不出落点时也不该复读:NameLookup.Locate 查的正是那几层(def 类型、类、mod、
            // keyed、字段值、XML 节点、别的快照),它返回 null 就意味着**那几层都查过且都空** ——
            // 那时再指过去是条假线索。
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing matched '{query}' in this snapshot" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") + ".");

            // 名字的真实落点当场算得出来:算得出就说算出来的那一条,算不出才退回按形状猜。
            var sighting = NameLookup.Locate(ctx, query, scope);
            var looksLikeClass = ClassNameShape.Looks(query);
            ctx.Report.Notice(NoticeKind.NextStep,
                sighting?.Sentence
                ?? (looksLikeClass
                    // 嵌套 `Class=` **是**被索引的(导出器 0.2.0 起),所以不许说「索引不到」——
                    // 那句话会把 `where Class` 的零判成「工具看不见」,而不是「确实没有」。
                    // 覆盖到哪一层随快照的导出器版本变,所以念 NestedClassLine 那个唯一产地,
                    // 不在这里另写一句会过时的。
                    ? $"Nothing in this snapshot is called that under any other guise either — no def type, " +
                      $"no class, no mod. " + Completeness.NestedClassLine(ctx) +
                      // 反方向那半:类可以完全不经过 def 被使用,那时候两条查询都该是零。
                      $" A zero from that one too means no def names '{query}' — the class can still exist and " +
                      $"be constructed in C#: 'rimsearcher code-search \"class {ClassNameShape.Tail(query)}\\b\"' " +
                      "settles whether it exists, and a bare 'rimsearcher code-search' on the name shows who uses it."
                    : "'rimsearcher list' with no def type lists what kinds of def this snapshot holds, " +
                      "and 'rimsearcher mods' lists which mods it covers."));
        }

        // 「靠什么命中的」必须在表里:命中可以来自子结构(如 TraitDef 某一档 degreeData 的
        // label),而那一行自己的 label 是空的 —— 不说清就会被归到邻行上。
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
        Advisory.NoteSameLabel(ctx, rows);
        return rows.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// 这一行靠什么命中。判据只认「肉眼能在这一行上验证的」:名字、label、描述里含查询词;
    /// 都不含时命中来自不在表里的东西(译文,或挂在子结构上的 label)。
    /// </summary>
    private static string MatchedOn(CommandContext ctx, DefRow r, string query)
    {
        bool Has(string? s) => s is { Length: > 0 } && s.Contains(query, StringComparison.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (Has(r.DefName)) parts.Add("def_name");
        if (Has(r.Label)) parts.Add("label");
        if (Has(r.Description)) parts.Add("description");
        if (parts.Count > 0) return string.Join("+", parts);

        // 译文要按 def_type 过滤,否则同名跨 def 类型时会把**别人的**译文路径报成
        // 「这一行靠什么命中」。def_type 为空的(语言文件收割,注入 key 不带类型)仍算 ——
        // 游戏也是按名字注入的,那条译文确实作用在这个 def 上。
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
            "says so on its source line.\n\n" +
            // source 这一列一直在印文件名,把它是什么、不是什么一次说完。
            "The 'source' line is the bare file name the game reported for that def — no directory, because " +
            "the game does not keep one. It names the file inside that mod's Defs folder ('mod' above says " +
            "which mod); it is not a path, and nothing here reads the file system to confirm the file is " +
            "still there. Defs the game builds in code carry a placeholder there instead.",
        Positionals = [new PositionalSpec { Name = "defName", Help = "The exact def name. 'search' finds it if you only know part of it." }],
        Options =
        [
            CommonOptions.Limit("fields") with { Default = Limits.DefaultFieldsPerDef.ToString() },
            new OptionSpec
            {
                // 没有它,在几百字段的 def 里找一条路径只能 --limit all 再 grep 输出。
                Name = "path-contains",
                Arity = Arity.Multi,
                // 主名与别名各由一头的实测定:识别测 path-contains 10/10(危险的两种误读
                // ——读成文件系统路径、读成按值匹配——各零例),而产出式里没人写得出它,
                // 24/24 伸手去抓的是 filter。所以 filter 留作别名接住伸手。
                // 光叫 path 两头都不占:产出式 0/24,而它与文件路径撞词。
                Aliases = ["filter", "grep", "field-contains", "path-filter", "field", "field-path", "only"],
                Placeholder = "<text>",
                Help = "Only show field paths containing this text. Repeat it to widen the selection.",
                Narrows = true,
            },
            // 同名跨 def 类型是 RimWorld 常态(PsychicSensitivity 既是 StatDef 又是 TraitDef)。
            // `get` 的 --type 挑的是**哪个 def**,不是从这个 def 的字段里筛,所以计数句里
            // 不念它 —— 念了会被读成「去掉它还有更多字段」,而去掉它得到的是另一个 def。
            CommonOptions.Type with { Narrows = false },
            new OptionSpec
            {
                Name = "defaults",
                Arity = Arity.Flag,
                Aliases = ["with-defaults", "all-fields"],
                Help = "Also list fields whose value is the one a fresh instance of the declaring type already "
                     + "carries. Those rows are left out by default because they are the ones most often read as "
                     + "something an author chose, when the snapshot cannot tell whether anything set them at all. "
                     + "How many were left out is always printed, and --path-contains shows a named field either way.",
            },
        ],
        Examples =
        [
            "rimsearcher get Apparel_ShieldBelt",
            "rimsearcher get Apparel_ShieldBelt --path-contains statBases",
            "rimsearcher get Bullet_Revolver --limit all",
            "rimsearcher get Bullet_Revolver --defaults",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                Rows = true,
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
        // 过滤前的全量要留住:同名提示按**这个名字一共有几个 def** 说话,不是按这次显示了几个。
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
            // 「名字在哪儿」的六种落点统一由 NameLookup 判,抽象父节点是其中一种。
            var sighting = NameLookup.Locate(ctx, name);
            if (sighting is not null)
            {
                ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
                return 1;
            }

            var names = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            var close = Suggestion.Closest(names, name);

            // 六种落点全在**快照**里,而快照只装 def 侧:句中的「a class」指的是某个 def 的
            // class 列,不是代码树里的 C# 类型(`MapPortal` 就是这样一个类)。代码树上万个
            // 文件,不为每次落空去扫,只把没查的那一半说出来并指名能查的那条命令。
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def is named '{name}' in this snapshot, and it is not a def type, a class, a mod, " +
                "an abstract XML parent, or a name held by any other registered snapshot." +
                Suggestion.Say(close, " 'rimsearcher search' matches on labels and translations too.") +
                " All of that is the def side; C# type names that no def references live only in the " +
                $"decompiled trees, which this lookup never reads: 'rimsearcher code-search \"class {name}\"'.");
            return 1;
        }

        var limit = ctx.Limit(fallback: Limits.DefaultFieldsPerDef);
        var paths = ctx.Args.Values("path-contains");

        // 撞名这件事排在**全部段落之前**。此前它在最后:六个同名 def 各带一张完整字段表,
        // 两百行之后才说「这里其实有六个」,而第一段开口可以是一句否定
        // (「No field path of Chimera (PawnKindDef) contains ...」),读的人已经拿它当答案走了。
        // 按全量说话,不按过滤后的集合 —— --type 在场时最需要这句:调用方主动收窄了,
        // 恰恰说明它知道有歧义、并打算只读一个。
        if (allMatches.Count > 1)
        {
            var others = allMatches.Where(d => !matches.Contains(d))
                                   .Select(d => d.DefType)
                                   .Distinct(StringComparer.Ordinal)
                                   .ToList();
            ctx.Report.Notice(NoticeKind.Boundary, NameCollision.Say(
                name, allMatches.Count,
                matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal).ToList(),
                others));

            // 每段各带自己的脚注与截断警告,而那些话只差一个数字 —— 段与段的分界得看得见,
            // 否则「at least 23 fields were dropped」与「at least 174」并排出现时,没人知道
            // 哪条管哪个 def。分界由每段自己的 def_type 行给出,这里只说破要按它读。
            if (matches.Count > 1)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"What follows is {Tally.Complete(matches.Count).Render("def")} in a row, one block each. " +
                    "Every count, footnote and truncation warning below belongs to the block it sits in — " +
                    "read the def_type line at the top of a block to know which def the lines under it are about.");
        }

        foreach (var def in matches)
        {
            // 恒定形状:即使只有一个 def,JSON 里也是 defs[0] —— 形状随数据变会让照着一次
            // 输出写的解析器在下一次撞名时静默拿到别的东西。
            ctx.Report.Item("defs");

            // --path-contains 说的是「这次我只要这些」,而 description 动辄几百字,会把它淹掉。
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

            // 有父节点才出这一行,没有的不平白多一行空值。
            //
            // 取父节点要按 def_type 收,否则同名跨 def 类型时会印**别人的**父节点。但不能
            // 硬要求相等:`xml_nodes.def_type` 是 XML 根元素名,`defs.def_type` 是
            // AllDefTypesWithDatabases 的桶名,两者会不一致(CreepJoinerAggressiveDef 的 def
            // 落在 CreepJoinerBaseDef 桶里),硬要求相等会把「串味」换成「丢数据」。
            // 收法:先要相等的;没有相等的,只在这个名字**没有同名歧义**时才回退到唯一候选。
            var named = ctx.Db.NodesNamed(def.DefName)
                              .Where(n => string.Equals(n.DefName, def.DefName, StringComparison.OrdinalIgnoreCase))
                              .ToList();
            var xmlNode = named.FirstOrDefault(n => DefTypes.Same(n.DefType, def.DefType))
                       ?? (named.Count == 1 && allMatches.Count == 1 ? named[0] : null);
            if (xmlNode?.ParentName is { Length: > 0 } parentName)
                pairs.Add(new("inherits_from", $"{parentName} (see 'rimsearcher inherit {def.DefName}')"));

            // 只有一个 def 时,identity 块**不**排在最前:它是一叠名字,而 line 1 是管道下
            // 唯一的幸存者,那个位置得留给「几条、全不全」。名字是调用方自己敲进来的,
            // 少看一眼不会把截断读成完整。块改挂在字段表正上方(见下面那句 Detail)。
            //
            // 撞名连印时**不动**:那时 line 1 已经是撞名那句,而各段的计数一旦提到自己的
            // identity 块之前,就会紧贴着上一段的表尾,读成上一段的数 —— 上面那句
            // 「读每块顶部的 def_type 行」正是拿这个当分界的。
            if (matches.Count > 1) ctx.Report.Detail("def", pairs);

            // 默认不列「与 C# 声明默认值无从区分」的那些行。两个例外都指向同一条:
            // **调用方点了名的东西不许消失** —— --path-contains 已经点名了要哪些路径,--defaults
            // 是明说要全量。于是过滤只发生在什么都没点名的那一次。
            var withDefaults = ctx.Args.Flag("defaults") || paths.Count > 0;
            var (fields, matched, total, defaulted, matchedPaths) =
                ctx.Db.Fields(def.Id, limit.Effective, paths, includeDefaults: withDefaults);

            // 表在这一段的**末尾**才挂上去(渲染顺序 = Add 顺序)。分界与折叠行那条同理:
            // 数得清多少、全不全,读到行的时候得已经知道 —— 所以计数、过滤、截断在表之前。
            // 读完之后才成立的注解(哪些值是同类型大多数都有的)排在表之后,它们的措辞本来
            // 就写着「above」。
            // 多个 def 同名时,截断声明必须指名道姓 —— 否则两条「Showing 5 of N fields」
            // 并排出现,读者无从知道哪条管哪个 def。
            var whose = matches.Count == 1 ? "" : $" of {def.DefName} ({def.DefType})";
            if (paths.Count > 0)
            {
                // 过滤后为空**不等于** def 没有这些字段,只等于没有路径含这段文本。
                // 这两件事在输出上长得一样,所以必须由声明区把它们分开。
                if (matched == 0)
                {
                    // 第二种成因:给进来的文本不是路径而是**值**(`--path-contains TrapSpringChance`
                    // 是 statBases[6].stat 装着的那个值)。这一档算得出来,就算出来再说。
                    var asValue = paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList();

                    // 第三种成因,也是最容易被读反的那种:字段在同类型别的 def 上有,只是
                    // 这个 def 上是 null 而 null 不进索引。「这个 def 没有」与「这个类型
                    // 没有」在输出上同形,而这个数当场查得出来 —— 不报,前者就会被当后者用。
                    var (kin, kinPaths) = asValue.Count > 0
                        ? (0, 0)
                        : ctx.Db.TypeDefsWithPath(def.DefType, paths);

                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"No field path{whose} contains {PathFilterText.Say(paths)}; the def does have " +
                        $"{Tally.Complete(total).Render("field")}. Drop --path-contains to see them." +
                        // 动词不进登记处:冒号在前、名单在后,主句就没有随数量变形的成分。
                        (asValue.Count > 0
                            ? " Found on this def as a field's value rather than anywhere in a path: " +
                              $"{PathFilterText.Say(asValue)}. 'rimsearcher where --value {asValue[0]}' names every path holding it."
                            : "") +
                        (kin > 0
                            ? $" Other defs of this type do have it: {Tally.Complete(kin).Render("def")} across " +
                              $"{Tally.Complete(kinPaths).Render("field path")}. So it is missing from this def, " +
                              $"not from {def.DefType} — a field that is null on a def never entered the index. " +
                              $"'rimsearcher fields {def.DefType} --path-contains {paths[0]}' names those paths."
                            : ""));

                    // 一个同类都没有,而那段文本也不是个值:此时「索引里没有」与「字段不存在」
                    // 真的分不开,得由那段话去分。上面两支各自已经解释过了,不再挂一遍。
                    if (kin == 0 && asValue.Count == 0)
                        Completeness.NoteIndexHoldsValuesOnly(ctx, paths[0]);
                }
                else
                {
                    // 这是调用方自己要的过滤,不是截断:机器侧靠 kind 分类,混用会让
                    // 「我主动只要 driverClass」被扫 notes 的下一位读成「结果不完整」。
                    // 动词不进登记处,计数一律挪到冒号后。
                    // 子串匹配不留痕:`--path-contains soundImpact` 只回 `soundImpactDefault` 这个语义
                    // 相反的字段,所以要说破「整段一次都没命中」。整段命中的数在**截断之前**
                    // 数(matchedPaths 不受 --limit 影响),否则换个 --limit 就换一句结论。
                    var whole = matchedPaths.Count(x => PathSegments.IsWholeSegment(x, paths));

                    // 第三种可能,而那两句只穷举了两种:名值对结构(statBases[N].stat = MarketValue)
                    // 把**字段的名字搬进了值那一列**,--path-contains 结构上够不着它。于是「命中了几条、
                    // 但没一条是整段」这张表干净、完整,答的却是另一个问题。这一档查得出来,
                    // 就查出来 —— 与完全没命中那支同一个探针。
                    var alsoValue = whole == 0
                        ? paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList()
                        : [];

                    ctx.Report.Notice(NoticeKind.Filter,
                        $"Matching {PathFilterText.Say(paths)}{whose}: " +
                        $"{Tally.Complete(matched).Render("field")}, out of " +
                        $"{Tally.Complete(total).Render("field")} on the def." +
                        (whole == 0
                            // 这里不能下存在性的强断言:「前缀式列举」是正常用法,而要找的
                            // 字段往往就在这句话下面那张表里。只摆事实、两种读法都点出来,
                            // 并说破这句话**一行都没滤掉**。
                            ? $" None of those has {PathFilterText.Say(paths)} as a whole path segment: each match contains " +
                              "it inside a longer name. Either those longer names are the fields you meant, or " +
                              "nothing here is called exactly that — this line removes none of the matched " +
                              "fields, so read them before deciding which." +
                              (alsoValue.Count > 0
                                  ? " A third reading is in play here: this def also carries " +
                                    $"{PathFilterText.Say(alsoValue)} as a field's *value*, not in any path. " +
                                    "A name/value pair puts the field's own name in the value column " +
                                    "(statBases[N].stat = MarketValue), where --path-contains cannot reach it — " +
                                    $"'rimsearcher where --value {alsoValue[0]}' goes at it from that side."
                                  : "")
                            : whole < matched
                                ? $" Whole path segment: {Tally.Complete(whole).Render("field")}; " +
                                  $"inside a longer name: {Tally.Complete(matched - whole).Render("field")}."
                                : ""));
                    if (fields.Count < matched)
                        ctx.Report.Notice(NoticeKind.Truncation,
                            $"Showing {Tally.Of(fields.Count, matched).Render("field")}; raise --limit for the rest.");

                    // --path-contains 是调用方自己收窄的,而收窄之后同一块里的其它字段就看不见了。
                    Advisory.NoteAuthoredSiblings(ctx, fields.Where(f => f.Default != Contract.DefaultState.Same)
                                                             .Select(f => (def.DefName, def.Id, f.Path)));
                }
            }
            else
            {
                // 分母是**列出来的那一群**的总数,不是 def 的字段总数 —— 否则被 limit 截的
                // 与被默认值过滤掉的混在同一个差额里,拆不开。两者各自一句,再由 total 对账。
                var listable = withDefaults ? total : total - defaulted;
                // 撞名连印时才点名这一块是谁 —— 别处那个 def_name 行就在几行之上,
                // 而多块连印时读者手里有好几个。出路本身不说(见 CountNotice)。
                ctx.Report.CountNotice(Tally.Of(fields.Count, listable), "field",
                    matches.Count == 1 ? "" : $"this is {def.DefName} ({def.DefType}).");

                // 措辞不许滑成「没人设过它」:XML 里照着默认值写一遍是常事,快照里那两种
                // 情形完全同形。这一列能证的只有「与声明默认值无从区分」,句子就只说这个,
                // 且句中不出现任何随数量变形的动词或代词(名词才有登记处)。
                // 数字说的是**索引到的路径数**,不是 def 的字段数:导出器见 null 直接 return,
                // 那条路径从来没进过索引,--defaults 也召不回来 —— 于是「这个字段不存在」
                // 与「它的值是 null」在输出上完全同形。
                if (!withDefaults && defaulted > 0)
                {
                    // 折叠按「谁设的值」筛,而提问常常是「列表多长」—— 两个维度正交却归同一个
                    // 开关管,一整个列表项被折光时列表看着就变短了。下标前缀不受折叠影响,
                    // 所以两边都说破:藏了就点名,没藏也把那句正面的话给出来。
                    var shownIdx = fields.SelectMany(f => PathSegments.IndexPrefixes(f.Path))
                                         .ToHashSet(StringComparer.Ordinal);
                    var hiddenIdx = matchedPaths.SelectMany(PathSegments.IndexPrefixes)
                                                .Distinct(StringComparer.Ordinal)
                                                .Where(x => !shownIdx.Contains(x))
                                                .ToList();
                    // 「code_default 这一列是什么意思」与「--path-contains 指名字段两边都看得见」
                    // 都搬进了 SKILL.md —— 逐字不随查询变。**`--defaults` 不能跟着走**:
                    // 它是这条声明唯一的出路,少了它这句就只说「有 N 条你看不到」而不说
                    // 怎么看到。出路紧贴它召回的那个数,不隔一句挂在总数后面。
                    // 第四态那半句同样不能走:上一句刚给出「这个 def 有 M 条字段路径」,
                    // 不接着说破 null 字段从没进过索引,那个 M 就会被读成「这个 def 的全部
                    // 字段」,而「字段不存在」与「值是 null」在这里同形。它数的是那两个数
                    // (没列出的、索引里的),不是那两张表 —— neither 得自己带上主语。
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"Not listed: {Tally.Complete(defaulted).Render("field")} carrying the declaring type's " +
                        "own default; --defaults lists them. The snapshot holds " +
                        $"{Tally.Complete(total).Render("field path")} for this " +
                        "def; a null-valued field never entered the index and is in neither count." +
                        // 方位词指的是这句话下面那张表。此前渲染器无条件把声明全提到最前,
                        // 于是这两句写着 above 却印在表前,指的是一片不存在的上文。
                        (hiddenIdx.Count > 0
                            ? " Nothing below shows any field of these list entries, which the def has all the " +
                              $"same: {NameList.Render(hiddenIdx, Limits.MaxSuggestions)} — so the lists run " +
                              "longer than they look here."
                            // 正面态只报事实,不把事实翻译成读法 —— 「每个下标都在下面」到
                            // 「列表长度如实」只隔一步,而藏了的那一态才是坑,解释留在那边。
                            : " Every list index the def has appears below."));
                }
            }

            // 「字段被截」与「没有该字段」必须可区分。这一句自己的措辞就把位置钉死了 ——
            // 它说的是 the list below。
            if (def.FieldsTruncated > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The exporter stopped short on this def: {ExportCap.OnDef(def.FieldsTruncated)}, " +
                    "so a path missing from the list below is not proof that the def lacks it.");

            if (matches.Count == 1) ctx.Report.Detail("def", pairs);

            if (FieldDefault.Legend(fields.Select(f => (f.Path, f.Default))) is { } legend)
                ctx.Report.Notice(NoticeKind.Boundary, legend);

            ctx.Report.Table("fields", ["path", "value", FieldDefault.Column],
                fields.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                    // 这一列恒在,不随「本次有没有默认值行」出现或消失:表的形状随数据变,
                    // 照着一次输出写的解析器下一次就取不到键。unknown 也必须能与 no 分开 ——
                    // 「没比成」不是「有人改过」。
                    [FieldDefault.Column] = FieldDefault.Render(f.Default),
                }).ToList());

            // 表之后:这句讲的是刚读过的那些行(措辞里就是 above)。
            // --path-contains 那条分支同样要说 —— 按 path 收窄恰恰是最容易只盯着一行读的用法。
            Completeness.NoteWidelySharedValues(ctx, def, fields);

            // --limit 与 --path-contains 同样管译文表:不管的话,`get Muffalo --limit 5` 会吐出八十行,
            // 而字段表刚报的「一个都没匹配上」会被一批译文块淹掉。
            // 归属策略与 inherits_from 同源:def_type 对得上的归自己;对不上的一律不要;
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
                // 数量在表之前,同字段表那条分界。
                ctx.Report.CountNotice(Tally.Of(translations.Count, allTranslations.Count),
                    "translation",
                    matches.Count == 1 ? "" : $"this is {def.DefName} ({def.DefType}).");

                // original 是被替换掉的原文:导出时刻 def 上留的是译文,原文只在注入记录里 ——
                // 两者同时在场是运行时导出独有的便宜。
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

                DiskLayer.NoteIfUnmeasured(ctx);

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

        return 0;
    }


    /// <summary>--path-contains 在场时把 description 压成一行:它不是被要的东西,却最占地方。</summary>
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
        // 旧主名 find 不留作别名。它与 search 在英语里几乎同义,而两条命令做的是相反方向的
        // 事(search 从名字找 def,这条从字段值反查 def),盲测里 33–42% 的动词误选就落在这
        // 一对上。留着别名等于把那道选择题留着 —— 而 where 与 search 谁也不像谁。
        Name = "where",
        Aliases = ["by-field"],
        Summary = "Find defs by the value of a field. This is the reverse lookup: from a C# class or a value back to the defs that use it.",
        Remarks =
            "The field path is matched from the end, so 'compClass' finds 'comps[3].compClass' without you knowing " +
            "the index. That suffix is plain text and does not stop at a '.', so 'graphicData.shaderType' also " +
            "matches 'swimmingGraphicData.shaderType'; --exact-path pins the whole path. This replaces grepping " +
            "the XML: the values here are the merged, post-patch ones, and a class reference is an exact match " +
            "rather than a text hit.",
        Positionals =
        [
            new PositionalSpec { Name = "fieldPath", Help = "A field path or just its last segment, such as compClass or defaultProjectile. Omit it to search every field instead.", Required = false },
            new PositionalSpec { Name = "value", Help = "The value to look for. '--value' spells out this same argument, so give it one way or the other. Omit it to list every def that has the field at all.", Required = false },
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
                Narrows = true,
            },
            CommonOptions.ExactPath,
            new OptionSpec
            {
                // 「别 grep XML」拿走了一种能力,就得给回等价的一种:不知道字段叫什么时
                // 靠猜会拿到一个语法正常、语义全错的结果集。
                //
                // 这个名字与位置参数 <value> 撞名是**有意的** —— 两处说的就是同一件事,
                // 于是 `--field X --value Y` 这种从 get / inherit / read 那边带过来的写法,
                // 去掉 --field 之后剩下的半条命令仍然在答同一个问题。
                Name = "value",
                Aliases = ["any-field", "search-values", "holding"],
                Placeholder = "<text>",
                Help = "The value to look for, same as giving it as an argument. Without a field path, every field " +
                       "is searched and the report names which paths hold it.",
            },
        ],
        Examples =
        [
            "rimsearcher where compClass RimWorld.CompShield",
            "rimsearcher where compClass --value RimWorld.CompShield",
            "rimsearcher where defaultProjectile Bullet_Revolver",
            "rimsearcher where --value World/WorldObjects/Expanding",
        ],
        // 这条命令的两种问法产出两种行,所以键名也是两个 —— 同一个键装两种形状,
        // 消费方读到的字段会随它没传过的参数变,比多一个键危险得多。
        JsonKeys =
        [
            new()
            {
                Key = "matches",
                What = "with a field path: one row per def that has it — def_name, def_type, value, mod. " +
                       "'mod' is where the def was declared, not who wrote the value: a comp another mod " +
                       "bolts onto a vanilla def still reads as the vanilla mod, and --scope filters that " +
                       "same column.",
            },
            new()
            {
                Key = "paths",
                What = "without a field path: one row per field path that holds the value — path, def_type, defs, " +
                       "example_value. This is the key that question produces; 'matches' is absent then.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();

        var offset = ctx.Args.Offset();

        var path = ctx.Args.Positional(0);

        // 空串**不是**一个值。此前它被当成「没给 --value」,于是 `where <path> --value ""`
        // 静默退化成「列出所有带这个字段的 def」—— 一张长得和合法答案一模一样的表,
        // 而读的人问的是「哪些 def 把它设成了空」。两种写法各自的含义当场说清:
        // 「没设过」在这套索引里不是一个可查的值(null 根本不进索引)。
        if (ctx.Args.Value("value") is { Length: 0 })
            throw new CliUsageException(
                "--value was given as an empty string, which is not a value to look for. " +
                (path is null
                    ? "Pass the text to look for ('rimsearcher where --value CompShield')."
                    : $"To list every def that has the field, drop it ('rimsearcher where {path}'); " +
                      $"to look for a value, pass one ('rimsearcher where {path} <value>'). ") +
                "A field nobody set is not in the index at all, so no query here returns it.");

        var named = ctx.Args.Value("value") is { Length: > 0 } v ? v : null;

        // 分支判据是**给没给字段**,不是给没给 --value。--value 一律读作「要找的值」:
        // 有字段就是那个字段的值,没字段才退回搜遍所有字段。此前判据挂在 --value 上,于是
        // `where --field X --value Y` 被拒掉 --field 之后,剩下的半条命令照样跑得通、
        // 答的却是另一个问题 —— 一个语法正常、语义全错、还长得像正常结果的东西。
        //
        // 两张表互斥,按分支认领 —— 声明在命令头上的话,`where compClass X` 会白发一个
        // 空的 paths,而空数组在机器侧读作「查过了,没有」。
        if (path is null)
        {
            if (named is null)
            {
                ctx.Report.Promises("matches");
                ctx.Report.Notice(NoticeKind.NextStep,
                    "'where' needs either a field path ('rimsearcher where compClass CompShield') or " +
                    "--value to search every field ('rimsearcher where --value CompShield').");
                return 2;
            }
            ctx.Report.Promises("paths");
            return ByValue(ctx, named, scope, limit, ctx.Args.Flag("exact"), offset);
        }
        ctx.Report.Promises("matches");

        // 两处都给了值:它们说的是同一件事,取哪个都可能不是想要的那个,而挑一个跑下去
        // 之后输出里看不出另一个被丢了。
        if (ctx.Args.Positional(1) is { } inline && named is not null && !string.Equals(inline, named, StringComparison.Ordinal))
            throw new CliUsageException(
                $"The value is given twice and the two differ: '{inline}' as an argument and '{named}' as --value. " +
                $"With a field path they mean the same thing — 'rimsearcher where {path} {inline}' is " +
                $"'rimsearcher where {path} --value {inline}'. Drop one.");

        var value = ctx.Args.Positional(1) ?? named;
        var exact = ctx.Args.Flag("exact");
        var pq = new PathQuery(path, ctx.Args.Flag("exact-path"));

        var (rows, total) = ctx.Db.FindByField(pq, value, exact, scope, limit.Effective, offset);

        if (rows.Count > 0)
            ctx.Report.PageNotice("def", rows.Count, offset, total);
        else if (offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("def")} match in all.");
            return 1;
        }

        if (rows.Count == 0)
        {
            // 别的快照里有没有是**算得出来**的,叠加不替换:成因分流照说,这一句排在它后面。
            // 声明在分流之前 —— 它对四条分支一视同仁,而每条分支各自 return。
            void NoteElsewhere()
            {
                if (NameLookup.Elsewhere(ctx, db => db.FindByField(
                        pq, value, exact,
                        Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0, 0).Total, "def")
                    is { } line)
                    ctx.Report.Notice(NoticeKind.NextStep, line);
            }

            // --exact-path 自己把结果筛空,与「这个字段不存在」是两件事,而下面那套分流
            // 会把它说成后者 —— 它问的是「路径存在吗」,而此时那条路径确实不以整段存在。
            // 排在最前面:这条成因一旦成立,后面三条都不适用。
            if (pq.Exact && ctx.Db.FieldPathExists(path, scope))
            {
                // 列出形状而不是报个数:那批形状本身就是下一条查询,而它们带的 `[]`
                // 原样粘回 --exact-path 就走得通。
                var shapes = ctx.Db.FindPathShapes(path, value, exact, scope);
                var shown = shapes.Take(Limits.MaxSuggestions).ToList();
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No field path is exactly '{path}'. Matched as a suffix instead: " +
                    string.Join(", ", shown.Select(x => $"{x.Shape} ({x.Count})")) +
                    (shapes.Count > shown.Count ? $", and {shapes.Count - shown.Count} more shapes" : "") +
                    ". Any one of those goes straight back into --exact-path, where '[]' stands for any index.");
                NoteElsewhere();
                return 1;
            }

            // 零结果有三种互斥成因,它们要的下一步完全不同:
            //   (1) 这个字段路径根本不存在 → 该去找字段叫什么
            //   (2) 字段存在,但这个值不在它的值域里 → 该去看值域
            //   (3) 名字是 def 的身份而不是字段(class / def_type / mod / source)→ 该换命令
            // (1) 要先于近似项算:跳过它,`where zzznotafield somevalue` 会报
            // 「No def has 'zzznotafield' set to ...」,一句预设了字段存在的话。
            var fieldExists = ctx.Db.FieldPathExists(pq, scope);

            if (!fieldExists)
            {
                // identity 级的名字不是字段,却是最自然的猜法 —— 它们在 get 的输出里就摆着。
                var identity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["class"] = "'rimsearcher list <DefType> --own-class <ClassName>' filters by the def's own class",
                    ["def_type"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["deftype"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["mod"] = "'--scope <packageId>' restricts any query to one mod",
                    ["source"] = "the source file is shown by 'rimsearcher get', but is not searchable",
                    ["parent"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                    ["parentname"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                };

                // 只给了一个词的人给的多半不是字段路径而是一个**值** —— 这条命令的正脸就是
                // 「从一个类名或一个值反查 def」。落点当场算得出来,就说算出来的那一条。
                // 值也给了的那一支不进来:下面那句已经拿着那个值点名了 --value。
                var placed = value is null && !identity.ContainsKey(path) ? Placed(ctx, path, scope) : null;
                // 与 values 那一支同一条:算出来「值在更深一层」就不再发带占位符的通用指路。
                var deeper = identity.ContainsKey(path) ? null : Completeness.ValuesLiveDeeper(ctx, path, scope);

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def in this snapshot has a field path ending in '{path}'" +
                    (scope.IsAll ? "" : $" within --scope {scope.Expression}") + "." +
                    // 尾巴撤掉时那个句点后面不许留空格 —— 基线闸按行尾空白判红。
                    (identity.TryGetValue(path, out var hint)
                        ? $" '{path}' is part of a def's identity rather than one of its fields: {hint}."
                        : deeper is not null
                            ? ""
                            : " 'rimsearcher fields <DefType> --path-contains <text>' lists the paths that a def type actually has" +
                              (value is not null
                                  ? $", and 'rimsearcher where --value {value}' finds which field holds that value."
                                  // 落点算出来了就不给这半句:下一句里同一条命令的参数是**填好的**。
                                  : placed is not null
                                      ? "."
                                      : ", and 'rimsearcher where --value <text>' finds which path holds a value " +
                                        "you already know.")));

                // 算得出来的结论排在索引边界那句之前;边界那句照旧挂 —— 「它是个值」并不
                // 证明「它不同时是一个没进索引的字段」,两件事正交,叠加不替换。
                if (placed is not null) ctx.Report.Notice(NoticeKind.NextStep, placed);
                // 「值住在更深一层」把边界那句**顶掉**,不是叠加:后者列的两条成因
                // (每个 def 都是 null / 不存盘的运行时缓存)在这种局面下一条都不成立。
                if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
                // identity 那一档不说:那时候答案已经给全了,再挂一句索引边界是纯噪音。
                // `class` 是**唯一**的例外:导出器 0.2.0 起 `<path>.Class` 是一条真路径,
                // 敲 `where Class X` 的人问的多半是嵌套子对象的类型,而 identity 那句只答了
                // 「def 自己的 class」。
                else if (!identity.ContainsKey(path) || string.Equals(path, "class", StringComparison.OrdinalIgnoreCase))
                    Completeness.NoteIndexHoldsValuesOnly(ctx, path);
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

            // 直接把真实值域里的近似项端出来:一条字段的取值动辄上百(compClass 有 175 个),
            // 指一条「去跑 values」的路照样看不见答案。
            var space = ctx.Db.DistinctValues(pq, scope, Limits.MaxLimit).Rows.Select(v => v.Value).ToList();

            // RimWorld 的约定:XML 里写的是 `Class="CompProperties_X"`,而落到 def 上的
            // comps[N].compClass 存的是被解析出来的 `CompX` —— 照 XML 抄的名字必然查不到。
            var alt = value.Contains("CompProperties_") ? value.Replace("CompProperties_", "Comp") : null;

            // 先做精确关系:值域里存的是全限定名,调用方给的常是末段。这是**同一个名字**,
            // 不是「长得像」—— 交给模糊打分会低于阈值而漏掉(CompAmbientSound 对
            // RimWorld.CompAmbientSound 就是如此)。
            var close = space.Where(v => Tail(v).Equals(alt ?? value, StringComparison.OrdinalIgnoreCase))
                             .Concat(space.Where(v => alt is not null &&
                                                      Tail(v).Contains(Tail(alt), StringComparison.OrdinalIgnoreCase)))
                             .Distinct(StringComparer.Ordinal)
                             .Take(Limits.MaxSuggestions)
                             .ToList();

            if (close.Count == 0)
                close = FuzzyMatcher.Rank(space, alt ?? value).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            // 值域计数没有产地就是负资产:「out of 207 values」会被读成「值的形态有讲究」。
            // 数字得连着「这些值来自哪些路径 / 哪些 def 类型」一起说。
            var cov = space.Count > 0 ? ctx.Db.ValueCoverage(pq, scope, 3) : default;
            var provenance = space.Count == 0 ? "" :
                $", out of the {Tally.Complete(space.Count).Render("value")} found under " +
                (cov.Paths.Count > 0
                    ? string.Join(" / ", cov.Paths.Select(x => x.Path)) +
                      (cov.PathTotal > cov.Paths.Count ? $" (and {cov.PathTotal - cov.Paths.Count} more paths)" : "")
                    : $"'{path}'");

            // 值域计数是拿来给「找遍了都没有」背书的,所以它自己的边界必须跟着说 ——
            // li-only 那档快照的 Class 值域里,单字段多态**结构性地不可能**出现,
            // 而「1397 个值里没有」读起来正是「找遍了」。
            var isClassPath = path.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".Class", StringComparison.Ordinal);
            // 索引缺口是**算出来的**成因,抽象基类只是个猜测。缺口在场时猜测让位 ——
            // 两句并排摆着,读的人会挑后者(它更具体),然后去查一批根本不存在的子类。
            var indexGap = isClassPath && !ctx.Db.Meta.IndexesAllNestedClass;

            // 本次查询**自己施加的过滤**是算得出来的成因,而抽象基类只是个猜测。算得出来的
            // 排在最前,并让猜测退场 —— 与 indexGap 同一条纪律:两句并排摆着,读的人会挑
            // 更具体的那句,然后去查一批根本不存在的子类。
            //
            // scope 只在第一行被回显过,而回显不是成因 —— 「我圈了这几个 mod」与
            // 「零是这个圈造成的」差着一次重查,而这次重查是白拿的:同一条 SQL,scope 换成 all。
            var hiddenByScope = scope.IsAll
                ? 0
                : ctx.Db.FindByField(pq, value, exact,
                                     Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config),
                                     0, 0).Total;
            if (hiddenByScope > 0)
                ctx.Report.Notice(NoticeKind.Filter,
                    $"--scope {scope.Expression} is what emptied this: " +
                    $"{Tally.Complete(hiddenByScope).Render("def")} in this snapshot " +
                    $"{(hiddenByScope == 1 ? "has" : "have")} '{path}' set to " +
                    $"{(exact ? "exactly " : "")}'{value}'. Drop --scope to see {(hiddenByScope == 1 ? "it" : "them")}.");

            ctx.Report.Notice(NoticeKind.NextStep,
                // 落空句自己要带上收窄条件,否则它与上面那句「--scope 把 N 行滤掉了」
                // 并排摆着就是一对矛盾话。措辞与 search 的落空句同源。
                $"No def{(scope.IsAll ? "" : $" within --scope {scope.Expression}")} has '{path}' set to " +
                $"{(exact ? "exactly " : "")}'{value}'{provenance}." +
                (close.Count > 0
                    ? $" Closest: {string.Join(", ", close)}." +
                      (alt is not null && close.FirstOrDefault(c => Tail(c).Equals(alt, StringComparison.OrdinalIgnoreCase)) is { } resolved
                          // 说破规律,并把**那条命令**一起给出来,不只是给一个名字。
                          ? " The XML writes Class=\"CompProperties_X\"; this field holds the resolved CompX — " +
                            $"'rimsearcher where {path} {resolved}' is the query you meant."
                          // 「给了个名字」不等于「说了下一步」:那几条只是最近的,真值域没看过。
                          : $" 'rimsearcher values {path} --limit all' lists the whole value domain.")
                    // 「如果 X 是抽象基类」是一句**未经验证的猜测摆在输出位置**,读的人会当
                    // 结论用。判据从严(ClassNameShape 把 `True`、`.ogg`、`1.5` 挡在外面),
                    // 并指向能当场证实或证伪它的 code-search。
                    //
                    // 两种成因并列,不许只说抽象基类那一种:`GenStep_ScatterLumpsMineable` 是个
                    // 被 C# 直接 new 出来的**具体类**,而单说抽象基类会把人推去查一批不存在的
                    // 子类 —— 第九轮盲测 S1 正是这么走完全程的。
                    : $" 'rimsearcher values {path} --limit all' lists them." +
                      (ClassNameShape.Looks(value) && !indexGap && hiddenByScope == 0
                          ? $" Two things look like this zero when '{value}' is a class: it is an abstract base " +
                            "and defs name its subclasses instead " +
                            $"('rimsearcher code-search \"class \\w+ : {ClassNameShape.Tail(value)}\\b\"' names " +
                            "them), or no def drives it at all and C# constructs it directly " +
                            $"('rimsearcher code-search \"{ClassNameShape.Tail(value)}\"' shows who does). " +
                            "Neither is evidence that the class itself does not exist."
                          : "")));

            // 边界排在建议**之后**:它限定的是上面那整段,而不是其中某一条。
            //
            // 量全了的那一档在这里**不发声**:它说的是「'find Class <ClassName>' 才是查得到
            // 它的那条查询」,而 isClassPath 的意思正是调用方刚跑完那条 —— 把人指回他站着的
            // 地方。留下的两档说的是另一回事:这份快照没量到那里,所以你手上这个零是假的。
            if (isClassPath && !ctx.Db.Meta.IndexesAllNestedClass)
                ctx.Report.Notice(NoticeKind.Boundary, Completeness.NestedClassLine(ctx));
            // 值侧是单语的 —— `where label "shield belt"` 在中文快照上必然空手,
            // 而那个 def 就在文本索引里躺着。与上面的近似候选叠加,不替换。
            if (value is { Length: > 0 }) Advisory.NoteTextIndexHasIt(ctx, value);
            NoteElsewhere();
            return 1;
        }

        static string Tail(string v)
        {
            var i = v.LastIndexOf('.');
            return i < 0 ? v : v[(i + 1)..];
        }

        // 落点分流借 search 那一份产地(NameLookup),**除了 def 名这一档**:那九档的措辞
        // 是给「这个名字不是 def」写的,而 `where Bullet_Revolver` 里它就是 def 名 ——
        // 照借会把一句假话摆在输出位置。def 名自己说,剩下八档原样复用。
        static string? Placed(CommandContext ctx, string name, Snapshot.ScopeFilter scope)
        {
            if (ctx.Db.GetDefsNamed(name).Count == 0) return NameLookup.Locate(ctx, name, scope)?.Sentence;

            // 指向 --value 之前先探一次,判据与那条命令自己的默认完全一致(子串)——
            // 一个字段都没指向它时那句话是死路,而「没有谁按名字引用它」本身就是个答案。
            // defName 那条路径不算数:它装的是这个 def 自己的名字,不是谁指向它。
            var referenced = ctx.Db.PathsWithValue(name, scope, Limits.MaxSuggestions).Rows
                                .Any(r => !string.Equals(r.Path, "defName", StringComparison.Ordinal));

            return $"'{name}' is a def name in this snapshot, not a field path. 'rimsearcher get {name}' shows " +
                   "what is in it" +
                   (referenced
                       ? $", and 'rimsearcher where --value {name}' names the fields that point at it."
                       : ", and no indexed field value points at it — nothing in this snapshot refers to it " +
                         "by name.");
        }

        // 一次命中横跨几种路径形状 —— 拿 where 的结果做集合差的人,少的正是这一句。
        Advisory.NoteMixedPathShapes(ctx, ctx.Db.FindPathShapes(pq, value, exact, scope));

        // 这里不像 get 那样把默认值行滤掉:调用方点名了一个字段与一个值,「哪些 def 取到过它」
        // 的答案里就该有它们。但**为什么取到**要分得开 —— comps[N].compClass 一整批
        // 等于 CompShield,多半是 CompProperties_Shield 的声明里写死的,不是谁在 XML 里挑的。
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(pq, scope),
            "every def type that uses this path at all, not just the ones in the rows above");

        // where 的这一列比 get 更容易被整列折叠掉 —— 全 yes 时折叠行只剩 `code_default=yes`
        // 一句,而那正是 C1 两组栽进去的形状。
        if (FieldDefault.Legend(rows.Select(r => (r.Path, r.Default))) is { } legend)
            ctx.Report.Notice(NoticeKind.Boundary, legend);

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
                                                .Select(r => (r.Def.DefName, r.Def.Id, r.Path)));
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
            // 快照索引的是叶子标量与 comps 的 compClass,**嵌套 li / 多态子对象的运行时类型
            // 不在其中**(modExtensions[0] 的 Class=、paramMappings[0].inParam 的 Class=)。
            // 于是「类真实存在且正在被这个 def 使用」与「这个类根本不存在」在输出上完全一样,
            // 而类名形状的查询词最容易撞这一条,所以这时候必须把索引边界说出来。
            // 判据归一到 ClassNameShape,它把 `True`、`.ogg`、`1.5` 挡在外面。
            var looksLikeType = ClassNameShape.Looks(value);
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No field in this snapshot holds a value {(exact ? "equal to" : "containing")} '{value}'" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") + "." +
                (exact ? " Drop --exact to match it as a substring." : "") +
                (looksLikeType
                    ? " If that is a class name: the snapshot indexes leaf scalars and a comp's compClass. " +
                      Completeness.NestedClassLine(ctx) +
                      $" 'rimsearcher code-search \"class {ClassNameShape.Tail(value)}\\b\"' finds the class itself."
                    : ""));

            // 值侧是单语的:同一个词在文本索引里可能好端端地在(译文的另一侧)。
            Advisory.NoteTextIndexHasIt(ctx, value);

            // 叠加不替换:上面那句说的是「这份快照里没有」,而别的快照里有没有算得出来。
            if (NameLookup.Elsewhere(ctx, db => db.PathsWithValue(
                    value, Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0,
                    exact ? ValueMatch.Exact : ValueMatch.Substring).Total, "field path")
                is { } line)
                ctx.Report.Notice(NoticeKind.NextStep, line);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total);

        // 子串命中不留痕,与 `--path-contains` 是同一条纪律的值侧:`where --value Bullet` 命中每一个
        // `Bullet_*`,而问的人多半只想要「值就是 Bullet 的那些」。
        if (!exact && exactTotal < total)
            ctx.Report.Notice(NoticeKind.Filter,
                exactTotal == 0
                    ? $"No value here is exactly '{value}'; each match has it inside a longer value — see " +
                      "example_value. --exact would return nothing."
                    // 拆的是**路径**,而右边那列数的是 def,两个口径叠在一张表里。不说破的话
                    // 「56 精确」会把整张表连同 defs 列一起读成精确数。
                    : $"Value exactly '{value}': {Tally.Complete(exactTotal).Render("field path")}; " +
                      $"containing it: {Tally.Complete(total - exactTotal).Render("field path")}. " +
                      "--exact keeps the first group only — and also narrows the defs column, which here " +
                      $"counts every def whose value contains '{value}', both groups together.");

        // 按值一次问清,不按结果里的每条路径各查一次再求和:求和会把同一个被砍的 def 按它
        // 出现在几条路径上重复计数,而路径 defName(`where --value` 命中 def 名时必然有)的
        // 「同类型」等于全体 def 类型,单这一项就等于全库 —— 于是子集计数会大于全集。
        // 表里那批 def 是「取到过这个值」选出来的,这句担保的也必须是同一批。
        Completeness.NoteIndexedPathsOnly(ctx,
            ctx.Db.TruncatedDefsSharingValue(value, exact ? ValueMatch.Exact : ValueMatch.Substring, scope),
            "every def type that holds this value anywhere, not just the ones in the rows above");

        ctx.Report.Table("paths", ["path", "def_type", "defs", "example_value"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["def_type"] = r.DefType,
                ["defs"] = r.Defs,
                ["example_value"] = r.Sample,
            }).ToList());
        return 0;
    }
}

public sealed class ListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "list",
        Aliases = ["ls", "types", "def-types"],
        Summary = "List every def of one type — or, with no type given, every def type in the snapshot.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defType",
                Required = false,
                Help = "A def type such as ThingDef. Leave it out and this lists the def types themselves, " +
                       "with how many defs each holds — all of them, unless you pass --limit. " +
                       "--own-class and --offset need a def type and are refused without one.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Scope,
            CommonOptions.Offset("defs"),
            new OptionSpec
            {
                Name = "own-class",
                // n=10 的识别测把三个候选分开了:own-class 10/10 且零靶心误读;
                // def-class 9/10 但那一份正是「子字段里的 compClass 也算」;
                // root-class 只有 1/10 —— 9 份把等值读成「及其派生类」的 is-a 匹配,丢掉。
                // 旧主名 class 留作别名:它是产出式赢家,但单说 class 不带「谁的」。
                Aliases = ["def-class", "class", "runtime-class"],
                Placeholder = "<ClassName>",
                // 「own」在做功,但光靠它不够:很多 def 类型的 class 是恒定量,多态全在嵌套
                // 字段上,而这个选项够不着那里。不说破的话,它在那些类型上回的零读起来
                // 就是「没有 def 用这个类」。
                Help = "Only defs whose own class is this. Def types that hold several classes list them below " +
                       "the count. Many def types hold just one class and pick their behaviour in a nested " +
                       "field instead — GenStepDef is all Verse.GenStepDef, with the GenStep subclass on " +
                       "'genStep' — and this option cannot see that. 'rimsearcher where Class <ClassName>' can.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "find",
                Aliases = ["filter", "grep", "search", "match"],
                Placeholder = "<text>",
                // 存在的理由是把 `list X | grep y` 挤掉:那条管道筛在 --limit 之后,
                // 默认 25 行外的东西压根到不了 grep,而计数句也一起被吃掉,于是空结果
                // 读起来就是「快照里没有」。
                Help = "Only defs whose name or label contains this. The filter runs before --limit, so a " +
                       "count of what matched is always reported — unlike piping to grep, which only ever " +
                       "sees the current page.",
                Narrows = true,
            },
        ],
        Examples =
        [
            "rimsearcher list",
            "rimsearcher list HediffDef",
            "rimsearcher list GenStepDef --find scatter",
            "rimsearcher list CreepJoinerBaseDef --own-class CreepJoinerAggressiveDef",
            "rimsearcher list ThingDef --scope all,-vanilla --limit all",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                What = "with a def type: one row per def — def_name, label, mod, plus 'class' when the " +
                       "bucket holds more than one def class. 'mod' is where the def was declared, not who " +
                       "last changed it: a def another mod patched still reads as its original mod, and " +
                       "--scope filters that same column.",
            },
            new()
            {
                Key = "types",
                What = "without one: one row per def type — def_type, defs. Which of the two keys is " +
                       "present follows the def type, so a caller that passed one never has to guess.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        // 分模式的判据是**给没给 def 类型**,而不是一个开关参数 —— 落空分流里那句
        // 「没有这个 def 类型」于是指得回同一条命令。
        var type = ctx.Args.Positional(0);
        return type is null ? RunTypes(ctx) : RunDefs(ctx, type);
    }

    /// <summary>
    /// 不给 def 类型的那一半:列出这份快照有哪些 def 类型。
    ///
    /// <c>--own-class</c> 与 <c>--offset</c> 只对另一半有意义,却仍然声明在这条命令上 ——
    /// 照单收下、悄悄不生效是个沉默口子(与 <see cref="CommandContext.Limit"/> 记的
    /// <c>--limit</c> 被静默夹紧同形),所以当场退 2 并把该走的那条路说出来。
    /// </summary>
    private static int RunTypes(CommandContext ctx)
    {
        // 指的那条路只在名字**真是** def class 时才落到桶上(`list CompShield` 回的是
        // 「No def type named 'CompShield'」),所以两种落点都说出来。
        if (ctx.Args.Value("own-class") is { } cls)
            throw new CliUsageException(
                $"--own-class needs a def type to narrow inside. If you do not know which def type holds " +
                $"the class '{cls}', run 'rimsearcher list {cls}': it names the def type when '{cls}' " +
                $"is a def class, and reports no such def type when it is not — a class that defs only " +
                $"reference in a field is found by 'rimsearcher where <field> {cls}' instead.");

        // 数量不许猜:一份快照能有两百多个 def 类型。
        if (ctx.Args.Offset() > 0)
            throw new CliUsageException(
                "--offset needs a def type to page through. Without one this lists the def types " +
                "themselves, and they all come out at once unless you pass --limit.");

        var scope = ctx.Scope();
        ctx.Report.Promises("types");
        var everything = ctx.Db.Types(scope);

        // --find 在这一半筛的是**类型名**。这条路不像 --own-class 那样退 2:它在这里
        // 真的生效,而「哪些 def 类型名字里带 Gen」是个答得出来的问题。
        var typeFind = ctx.Args.Value("find");
        var all = typeFind is null
            ? everything
            : everything.Where(t => t.Type.Contains(typeFind, StringComparison.OrdinalIgnoreCase)).ToList();

        // 筛空与快照空是两回事,分母就在手边。
        if (all.Count == 0 && everything.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def type in this snapshot has '{typeFind}' in its name, out of " +
                $"{Tally.Complete(everything.Count).Render("def type")}. Drop --find to see them all; " +
                "to filter the defs inside one type instead, name the type: " +
                $"'rimsearcher list <DefType> --find {typeFind}'.");
            return 1;
        }

        // 零行一律 exit 1,否则按退出码分流的脚本会把「0 def types.」读成「查到了」。
        // 句子也不许把 scope 造成的空说成快照的空 —— 整份快照的数就在手边。
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

        // 缺省全给:问的是「一共有哪些」,截一刀就答不完整。
        var limit = ctx.LimitOrAll();
        var rows = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        // 筛过就把分母也说出来:一个不带出处的「12 def types」读起来是整份快照的全部。
        if (typeFind is not null)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Filtered by --find '{typeFind}'; this snapshot holds " +
                $"{Tally.Complete(everything.Count).Render("def type")} in all.");

        ctx.Report.CountNotice(Tally.Of(rows.Count, all.Count), "def type");
        ctx.Report.Table("types", ["def_type", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_type"] = r.Type,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }

    private static int RunDefs(CommandContext ctx, string type)
    {
        var limit = ctx.Limit();
        var offset = ctx.Args.Offset();
        var wantClass = ctx.Args.Value("own-class");
        var find = ctx.Args.Value("find");
        var scope = ctx.Scope();

        // 认领要赶在查询**之前**:下面每一条零行分流都是提前 return。`defs` 与 `types`
        // 互斥,不能靠声明层的 Rows 统一认领(空数组在机器侧读作「这一路也查过了」),
        // 只能由两支各自认领 —— 另一支在 RunTypes 里。
        ctx.Report.Promises("defs");

        var (rows, total) = ctx.Db.ListByType(type, scope, limit.Effective, offset, wantClass, find);

        if (rows.Count == 0)
        {
            // 翻过头**先**判:下面每一条分流问的都是「这个名字是什么」,而翻过头时
            // 那个名字查得好好的。
            // 筛条件一律进句子:少写一个,那个条件造成的空就会被说成「快照里没有」。
            var narrowed = (wantClass is null ? "" : $" with class '{wantClass}'") +
                           (find is null ? "" : $" whose name or label contains '{find}'");

            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset,
                    $"this snapshot has {Tally.Complete(total).Render("def")} of type {type}{narrowed}.");
                return 1;
            }

            // 「这个 scope 里没有」不等于「快照里没有」:下面每一条判据都是 scope 过滤过的,
            // 而 `--scope zh`(汉化包,一个 def 都不加)会让 `list ThingDef` 报成
            // 「No def type named 'ThingDef' in this snapshot」。
            if (!scope.IsAll && offset == 0)
            {
                var (_, everywhere) = ctx.Db.ListByType(type, ctx.Unscoped(), 1, 0, wantClass, find);
                if (everywhere > 0)
                {
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"No def of type {type}{narrowed} is in scope '{scope.Expression}'" +
                        $", but this snapshot has {Tally.Complete(everywhere).Render("def")} of it overall. " +
                        $"Drop --scope, or run 'rimsearcher mods' to see which mods the scope selects.");
                    return 1;
                }
            }

            // --find 筛空的,与「这个类型压根不存在」是两回事。数就在手边,不说破的话
            // 这个空与 `list NoSuchDef` 的空读起来一模一样 —— 而后者才该去改类型名。
            if (find is not null && offset == 0)
            {
                var (_, unfiltered) = ctx.Db.ListByType(type, scope, 1, 0, wantClass);
                if (unfiltered > 0)
                {
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"No def of type {type}{(wantClass is null ? "" : $" with class '{wantClass}'")} has " +
                        $"'{find}' in its name or label, out of {Tally.Complete(unfiltered).Render("def")}. " +
                        "The filter reads def names and labels only — a def that merely holds this as a field " +
                        "value is 'rimsearcher where --value', and label text in another language is " +
                        "'rimsearcher search'.");
                    Advisory.NoteTextIndexHasIt(ctx, find, "--find");
                    return 1;
                }
            }

            // 「不是分桶键」不等于「不存在」:游戏只给「祖先链上没有非抽象 Def」的类型建库,
            // 于是 CreepJoinerAggressiveDef 的 def 全躺在 CreepJoinerBaseDef 桶里。
            var holders = ctx.Db.TypesHoldingClass(type, scope);
            if (wantClass is null && holders.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{type}' is not a def type in this snapshot, but it is the class of " +
                    $"{Tally.Complete(holders.Sum(h => h.Count)).Render("def")}: " +
                    string.Join(", ", holders.Select(h => $"{h.Count} under {h.DefType}")) + ". " +
                    $"The game only gives a def database to types with no concrete Def ancestor, so subclasses " +
                    $"share their base's bucket. 'rimsearcher list {holders[0].DefType} --own-class {type}' lists them.");
                return 1;
            }

            if (wantClass is not null)
            {
                var present = ctx.Db.ClassesInType(type, scope);
                if (present.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.NextStep,
                        DefTypeMiss.Say(type, ctx.Db.Types(scope).Select(t => t.Type), "list"));
                    return 1;
                }

                // 只有一个 class 时,这个选项在这个类型上区分不了任何东西 —— 而不说破的话,
                // 「确实没有 def 用这个类」与「--own-class 问的根本不是这件事」逐字同形。
                // 那类型真正的多态在嵌套字段上(GenStepDef 的 167 个 def 全是 Verse.GenStepDef,
                // 各自跑的 GenStep 子类写在 genStep 那一个字段的 Class= 里),所以转向要指到
                // 那条路上去,并按快照量到哪一步说话。
                if (present.Count == 1)
                {
                    // 「类恒定的桶把多态放在嵌套字段上」这条通则是 --own-class 自己的 help 文本
                    // (OptionSpec 里那段,cli-reference 的 --own-class 行就是它渲染出来的),
                    // 不在这里再讲一遍;这句只留本次的事实与填好参数的转向。
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"Every one of the {Tally.Complete(present[0].Count).Render("def")} of type {type} has the " +
                        $"same class, {present[0].Class}, so --own-class cannot tell them apart and this is not " +
                        $"evidence about '{wantClass}'. The behaviour lives on a nested field instead: " +
                        $"'rimsearcher where Class {wantClass}'.");
                    // 量全了的快照上这一条只会把上面那句用占位符再说一遍。留着的是它另外两档
                    // 携带的免责:那条转向在这份快照上会回一个**假零**,不说破就是个闭环。
                    if (!ctx.Db.Meta.IndexesAllNestedClass)
                        ctx.Report.Notice(NoticeKind.Boundary, Completeness.NestedClassLine(ctx));
                    return 1;
                }

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def of type {type} has class '{wantClass}'. That type holds " +
                    NameList.Render([.. present.Select(c => $"{c.Class} ({c.Count})")], Limits.MaxSuggestions) + ".");
                return 1;
            }

            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(scope).Select(t => t.Type), "list"));
            return 1;
        }

        // 筛过就把分母说出来:PageNotice 的 total 是**筛之后**的数,不带出处的话
        // 「3 of 3 defs」读起来就是这个类型总共只有三个。
        if (find is not null)
        {
            var (_, unfiltered) = ctx.Db.ListByType(type, scope, 1, 0, wantClass);
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Filtered by --find '{find}'; this type holds {Tally.Complete(unfiltered).Render("def")} " +
                (wantClass is null ? "in all." : $"with class '{wantClass}' in all."));
        }

        // 分页必须给总数,否则不知道翻到哪算到头。
        ctx.Report.PageNotice("def", rows.Count, offset, total);

        // 桶里只有一种 class 时不平白多一列(ThingDef 一万多个 def 都是 Verse.ThingDef);
        // 异构时这一列是唯一能把子类型区分开的东西。
        var classes = ctx.Db.ClassesInType(type, scope);
        var heterogeneous = wantClass is null && classes.Count > 1;
        if (heterogeneous)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 数的是 class,名词就得写「def class」—— 这一句正长在
                // 「def 类型不等于运行时 class」那条区分上。
                $"Type {type} holds {Tally.Complete(classes.Count).Render("def class")}: " +
                NameList.Render([.. classes.Select(c => $"{Tail(c.Class)} ({c.Count})")], Limits.MaxSuggestions) +
                ". Pass --own-class to pick one.");

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
            "Use this before 'where' when you are not sure what a field is called. The counts tell you whether a " +
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
                // ThingDef 有近三千条路径,默认只出 25 条。没有这个开关只能
                // `fields ThingDef | grep comps`,而管道会把截断声明一起滤掉,
                // 于是「被截了」变成「没有」—— 筛选必须在工具里做。
                Name = "path-contains",
                Arity = Arity.Multi,
                Aliases = ["filter", "grep", "field-contains", "path-filter", "contains", "match", "only"],
                Placeholder = "<text>",
                Help = "Only list paths containing this text. Repeat it to widen the selection.",
                Narrows = true,
            },
            CommonOptions.Offset("field paths"),
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path-contains comps",
            "rimsearcher fields HediffDef --limit all",
        ],
        JsonKeys =
        [
            new() { Key = "fields", Rows = true, What = "one row per field path: path, defs (how many defs use it)." },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Limit();
        var filters = ctx.Args.Values("path-contains");
        var offset = ctx.Args.Offset();
        var (rows, total, whole) = ctx.Db.FieldPathsForType(type, limit.Effective, filters, offset);

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
                    $"'{type}' has field paths, but none contains {PathFilterText.Say(filters)}. Drop --path-contains to see them all.");
                Completeness.NoteIndexHoldsValuesOnly(ctx, filters[0]);
                return 1;
            }
            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(ctx.Scope()).Select(t => t.Type), "fields"));
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total);

        // 与 `get --path-contains` 同一条纪律:子串匹配不留痕。这里的代价更大 —— 这条命令是
        // 「这个类型有没有这个字段」的正式问法,而「一条都不是整段」正是「没有」的形状。
        if (filters.Count > 0 && whole < total)
            ctx.Report.Notice(NoticeKind.Filter,
                whole == 0
                    ? $"None of those has {PathFilterText.Say(filters)} as a whole path segment: each match contains it " +
                      $"inside a longer name. Either those longer names are the paths you meant, or '{type}' " +
                      $"has no field called exactly {PathFilterText.Say(filters)} — this line removes none of the " +
                      $"{Tally.Complete(total).Render("field path")} that matched, so read them before deciding which."
                    : $"Whole path segment: {Tally.Complete(whole).Render("field path")}; " +
                      $"inside a longer name: {Tally.Complete(total - whole).Render("field path")}.");

        // 这一处圈的是整个 def 类型,与 --path-contains 无关 —— 表已经按 --path-contains 滤过,而被砍掉的
        // 字段本来就不在表里,按 --path-contains 收窄这个数就是拿看得见的东西去限定看不见的东西。
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsOfType(type),
            $"all of {type}, whatever --path-contains says");

        ctx.Report.Table("fields", ["path", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["defs"] = r.Count,
            }).ToList());
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
        Options =
        [
            CommonOptions.Limit("values"), CommonOptions.Offset("values"), CommonOptions.Scope,
            CommonOptions.Type, CommonOptions.ExactPath,
        ],
        Examples =
        [
            "rimsearcher values compClass",
            "rimsearcher values expandingIconTexture --type WorldObjectDef",
            "rimsearcher values thingClass --scope vanilla",
        ],
        JsonKeys =
        [
            new() { Key = "values", Rows = true, What = "one row per distinct value: value, defs." },
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
        var pq = new PathQuery(path, ctx.Args.Flag("exact-path"));
        var (rows, total) = ctx.Db.DistinctValues(pq, scope, limit.Effective, type, offset);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"'{path}' takes {Tally.Complete(total).Render("value")} in all.");
                return 1;
            }

            // 三种成因,要的下一步不同 —— 与 `where` 的分流同形:字段不存在 / 不在这个
            // --type 上 / 不在这个 --scope 里。空是 scope 造的就不能记在快照头上。
            var withoutType = type is not null && ctx.Db.FieldPathExists(pq, scope);
            var wideScope = ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config);
            var outsideScope = !withoutType && !scope.IsAll && ctx.Db.FieldPathExists(pq, wideScope);

            // --exact-path 自己筛空,与「这个字段不存在」是两件事。它排在三条成因之前:
            // 一旦成立,那三句都在描述另一个集合。
            var loosely = pq.Exact && !withoutType && !outsideScope && ctx.Db.FieldPathExists(path, scope);

            // 第四种成因:敲的是上一层。它要先算出来 —— 它一旦成立,下面那句通用指路里
            // 带占位符的两条命令就不发了,填好参数的路在它自己那句里。
            var deeper = !withoutType && !outsideScope && !loosely
                ? Completeness.ValuesLiveDeeper(ctx, path, scope) : null;

            ctx.Report.Notice(NoticeKind.NextStep,
                loosely
                    ? $"No field path is exactly '{path}', though some path ends in it. Drop --exact-path " +
                      $"to pool them, and read matched_paths for the real shapes — one of those pasted back " +
                      "with --exact-path is the narrow query."
                    : withoutType
                        ? $"'{path}' exists in this snapshot but not on any {type}. Drop --type to see which def types have it."
                        : outsideScope
                            ? $"'{path}' exists in this snapshot but no def has it within --scope {scope.Expression}. " +
                              "Widen the scope, or run 'rimsearcher mods' to see what this scope could have matched."
                            : $"No def in this snapshot has a field path ending in '{path}'" +
                              (scope.IsAll ? "" : $" (nor anywhere outside --scope {scope.Expression})") + "." +
                              // 尾巴撤掉时那个句点后面不许留空格 —— 基线闸按行尾空白判红。
                              (deeper is not null
                                  ? ""
                                  : " 'rimsearcher fields <DefType>' lists the paths a type actually has, and " +
                                    "'rimsearcher where --value <text>' finds which path holds a value you already know."));
            if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
            else if (!withoutType && !outsideScope && !loosely) Completeness.NoteIndexHoldsValuesOnly(ctx, path);
            return 1;
        }

        var cov = ctx.Db.ValueCoverage(pq, scope, Limits.MaxSuggestions, type);

        // 这里的省略不是 NameList 那种「我取了前几条」—— cov.Paths 已经在 SQL 侧截过,
        // 手上根本没有第 4 条起的名字。分母只有 cov.PathTotal 知道,所以照实拼。
        var pathList = string.Join(", ", cov.Paths.Select(x => $"{x.Path} ({x.Count})"));
        if (cov.PathTotal > cov.Paths.Count) pathList += $", and {cov.PathTotal - cov.Paths.Count} more";

        // 「N of M」是覆盖率的分母:少了它,一条 `Verse.Thing (7)` 分不清是「只有 7 个 def
        // 这么写」还是「导出漏了一千多个」。
        var typeList = string.Join(", ", cov.DefTypes.Select(x =>
            $"{x.DefType} ({x.Count} of {ctx.Db.CountDefsOfType(x.DefType, scope)})"));

        // 计数行走在产地块前面,而不是跟着它下面那张表。line 1 是管道下唯一的幸存者,
        // 那个位置得留给「一共几个、看到了几个」;产地三行是口径,少看一眼不会把
        // 截断读成完整。
        ctx.Report.PageNotice("value", rows.Count, offset, total);

        // 值的产地:后缀匹配天然会把语义不同的路径并进一张表,不说清就会被读成
        // 「这个字段到处都是这个值」。
        ctx.Report.Detail("field", [
            new("matched_paths", pathList),
            new("def_types", typeList),
            new("defs_with_field", (object)cov.DefsCovered),
        ]);

        // 这张表把几条路径的值**并成了一池**,而 matched_paths 只列得下前几条。不指出
        // 收窄的办法,读的人手上就只有一个没法拆开的池子。
        if (cov.PathTotal > 1 && !pq.Exact)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"These values come from {Tally.Complete(cov.PathTotal).Render("field path")} pooled together, " +
                "not from one field. Any path named above goes back in with --exact-path to pool that one " +
                "alone; '[]' there stands for any index.",
                footnote: true);

        // 跟着 --type 一起收:表已经滤成一个类型了,这句不能还在说别的类型。
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(pq, scope, type),
            type is { Length: > 0 }
                ? $"all of {type}, the type this table is already filtered to"
                : "every def type that uses this path at all, not just the ones in the rows above");

        ctx.Report.Table("values", ["value", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["value"] = r.Value,
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
        // 对一条列举命令拒绝它,读起来像「这里不能限量」,而实际只是「这里不需要」。
        // 严格模式该拦的是拼错的名字,不是合理的期待。
        Options = [CommonOptions.Limit("mods") with { Default = "all" }],
        Examples = ["rimsearcher mods"],
        JsonKeys = [new() { Key = "mods", Rows = true, What = "one row per mod, in load order: order, package_id, name, version." }],
    };

    public override int Run(CommandContext ctx)
    {
        var all = ctx.Db.Mods;
        var limit = ctx.LimitOrAll();
        var mods = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        ctx.Report.CountNotice(Tally.Of(mods.Count, all.Count), "mod");
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

/// <summary>
/// 一串 <c>--path-contains</c> 念回给读的人时的写法。产地唯一 —— `get` 与 `fields` 说的是同一件事。
/// </summary>
internal static class PathFilterText
{
    public static string Say(IReadOnlyList<string> parts)
        => parts.Count == 1 ? $"'{parts[0]}'" : string.Join(" or ", parts.Select(p => $"'{p}'"));
}

internal static class DefTypeMiss
{
    /// <summary>
    /// 「这个快照里没有这个 def 类型」的唯一产地:<c>list</c> 与 <c>fields</c> 共用一份措辞,
    /// 两条命令回答同一个问题时口径不许不一致,否则读的人会以为差别有意义。
    ///
    /// 近似候选**不能**把出路顶掉:<see cref="Suggestion.Say"/> 是二选一的,于是「打错了」
    /// 这条路上以前只剩一串名字,一条能敲的命令都没有 —— 而拿到名字之后要敲什么,恰好是
    /// 打错的人不知道的。所以头一个候选带着命令一起给。
    ///
    /// <paramref name="verb"/> 跟着调用方走:同一句话在 <c>fields</c> 上指 <c>list</c>
    /// 就把人从他要问的问题上带走了。
    /// </summary>
    public static string Say(string typed, IEnumerable<string> known, string verb)
    {
        var close = Suggestion.Closest(known, typed);
        return $"No def type named '{typed}' in this snapshot." +
               (close.Count == 0
                   ? " 'rimsearcher list' with no def type lists them all."
                   : Suggestion.Say(close) + $" 'rimsearcher {verb} {close[0]}' if that is the one.");
    }
}

internal static class Completeness
{
    /// <summary>
    /// 反查类命令的完整性尾注。
    ///
    /// 快照里有两套「完整」在互相打架:get 会为**单个 def** 声明「导出时砍掉了 N 个字段」,
    /// 而 where / values / fields 的计数以**已索引路径**为界 —— 某个 def 的 comps 在导出时被砍,
    /// 它就从 find 的结果里静默消失,而这恰恰是「一共有哪些」这类问题的致命伤。
    ///
    /// 但尾注本身也不能变成新的免责声明,所以收窄到「与本次结果**同类型**的 def 里真有被
    /// 砍的」才出声:不出声时,「完整」就是无条件的,而不是「大概吧」。
    /// </summary>
    /// <summary>
    /// 末尾那条命令要**走得到刚才说的那批**:裸命令列的是全库,而尾注说的只是其中几个类型
    /// 的一小批,两者输出形状一模一样。类型不多时逐个带上 --type;多到列不下就说清列不下。
    /// </summary>
    /// <summary>
    /// 「这条路径不在索引里」有几种互不相同的成因,句子要把它们列全 —— 它本身就是
    /// 「为什么是零」的答案。
    ///
    /// 导出器见 null 直接 return,那条路径从来没进过索引;嵌套 <c>&lt;li Class="…"&gt;</c>
    /// 的运行时类型也一样不进;基类上声明的字段(如 <c>weight = 1f</c>)在 def 的运行时类
    /// 是子类时反射默认拿不到(见 <c>FieldWalk</c>)。于是「这个字段不存在」与「它在,
    /// 只是每个 def 上都是 null」在输出上完全同形。
    ///
    /// 一件事一个产地:where / values / fields 三处调同一份措辞,不各写各的。
    /// </summary>
    /// <summary>
    /// 结论与那条能敲的命令排在成因分类**之前**。依据是采纳率实测:这句话到达读者 19 次,
    /// 只被照做 1 次,而它点名的 <c>code-search</c> 自然跟随率是 57% —— 读者不是没采纳,
    /// 是反着走的(19 次没有一次是会话末条,不是「聊完了」造成的)。
    ///
    /// 两个成因都在句子自己身上:唯一的出路埋在 300 多字符的分类之后;而它印的
    /// <c>'rimsearcher code-search'</c> 不带参数 —— 那不是一条命令,是一个名词,抄不走。
    /// 本仓别处的纪律是**命令填好了再印**(<c>snapshot truncated</c> 的脚注就是填好的)。
    /// </summary>
    public static void NoteIndexHoldsValuesOnly(CommandContext ctx, string? path = null)
    {
        // 字段声明的行长这样:`public List<ThingDefCountRangeClass> killedLeavingsRanges;`。
        // 类型里带 <> 和 [],所以字符类不能只有 \w —— `\w+ <name>;` 实测零命中。
        var leaf = Leaf(path);
        var how = leaf is null
            ? "'rimsearcher code-search' reads the class declaration, which does say."
            : $"'rimsearcher code-search \"[\\w<>,\\[\\] ]+ {leaf};\"' finds the declaration, which does say.";

        ctx.Report.Notice(NoticeKind.Boundary,
            $"This says no indexed value sits at that path — not that no such field exists. {how} " +
            "Two things keep a field out of this index without any sign here: a value that was null " +
            "on every def, and a field the game marks as an unsaved runtime cache. " +
            // 第三种成因只在**这份快照真有被砍的 def** 时存在。没有的时候那 105 字节讲的是一件
            // 这里不可能发生的事,而它还带着一条会回空表的指路 —— 与 NoteIndexedPathsOnly
            // 同一条纪律(圈住的类型里没有被砍的就一个字不发)。
            (ctx.Db.TruncatedDefCount() > 0
                ? "A third, hitting the per-def field cap, does leave a sign — " +
                  "'rimsearcher snapshot truncated' lists those defs. "
                : "") +
            NestedClassLine(ctx));
    }

    /// <summary>
    /// 敲的那个名字是**上一层**,值住在更深的叶子上 —— 这是零结果里最常见的一种成因,
    /// 而在补上这句之前它一次都没被说出来过。
    ///
    /// 索引只存叶子:<c>List&lt;StatModifier&gt; statBases</c> 自己不落行,值在
    /// <c>statBases[0].stat</c> 上。于是按后缀问 <c>statBases</c> 恒空 —— 而 C# 字段名就长这样,
    /// 是最容易敲进去的那个词。实测 ThingDef 上 <c>statBases</c>(44 条路径 / 1967 个 def)、
    /// <c>comps</c>、<c>costList</c>、<c>verbs</c> 全部落进这个洞,输出与「快照里真没有这个字段」
    /// 逐字同形,而 <see cref="NoteIndexHoldsValuesOnly"/> 随后列的两条成因一条都不适用,
    /// 把读者按在「这字段是空的」上。
    ///
    /// 一旦这句出声,那条免责就**不许再跟**:它讲的是另一种局面。
    /// </summary>
    /// <returns>该说的那句话,不适用就是 null。调用方拿它做两件事:掐掉那条免责,
    /// 以及把前一句里带 &lt;DefType&gt; / &lt;text&gt; 占位的通用指路一并撤掉 —— 参数填好的路
    /// 就在下一句,占位的那两条排在它前面只会先被照做。</returns>
    public static string? ValuesLiveDeeper(CommandContext ctx, string path, ScopeFilter scope)
    {
        // 自己带下标或点号的路径不进来:那时敲的已经是一条具体路径,空就是真的空。
        if (path.Contains('.') || path.Contains('[')) return null;
        var (samples, total, topType) = ctx.Db.PathsBelow(path, scope, 1);
        if (total == 0) return null;

        // 试过把打头那条从 `values` 换成 `where`(理由:`where` 带 def 名一列,`values` 只出汇总),
        // 5 个盲测 + 回访之后**改回来了**:细节错不是「路指错表」造成的。四个受测者一致自判
        // 「错在自己」,机制是不对称查询(查了 [0] 不查 [1])、抽样撞上恰好只有一条的 def、
        // 以及把 `values` 的分布表当逐 def 对照表用。联表本来就有(`get <def> --path-contains`),
        // 是没人敲,不是没有。别再为这个改措辞。
        var top = samples[0];
        return $"'{path}' holds objects rather than a value, so nothing is indexed under that name by itself — " +
               $"the values sit one level down, on {Tally.Complete(total).Render("field path")}. The widest is " +
               $"'{top.Path}' ({Tally.Complete(top.Defs).Render("def")}): 'rimsearcher values {top.Path}' reads it" +
               (topType is null
                   ? "."
                   : $", and 'rimsearcher fields {topType} --path-contains {path}' lists the rest.");
    }

    /// <summary>
    /// 路径的末段,即 C# 里那个字段自己的名字(<c>graphicData.shaderType</c> → <c>shaderType</c>,
    /// <c>statBases[3].stat</c> → <c>stat</c>)。拿不出一个纯标识符就返回 null ——
    /// 那时宁可不给命令,也不给一条敲了会报错的。
    /// </summary>
    private static string? Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var last = path.Split('.')[^1];
        var bracket = last.IndexOf('[');
        if (bracket >= 0) last = last[..bracket];
        return last.Length > 0 && last.All(c => char.IsLetterOrDigit(c) || c == '_') ? last : null;
    }

    /// <summary>
    /// 上面这些行里,哪些的值是**同类型大多数 def 都有的那个**。
    ///
    /// 引擎会成批塞值(<c>ThingDef.ResolveReferences</c> 给**每一个** ThingDef 都塞了
    /// <c>soundImpactDefault</c>,而那个字段的语义还是反的),而这一列能证的只有
    /// 「与刚 new 出来的实例不同」,读的人却一律读成「有人挑了它」。分辨「XML 写的」与
    /// 「引擎填的」在这份快照里没有产地(见 shared_values 建表注释),所以**不猜成因,
    /// 只报可核对的事实**,读的人自己判。
    ///
    /// 两边都说 —— 不靠沉默承载「都是这个 def 自己的」。
    /// </summary>
    public static void NoteWidelySharedValues(
        CommandContext ctx, DefRow def, IReadOnlyList<FieldRow> fields)
    {
        if (fields.Count == 0) return;
        var shared = ctx.Db.SharedValues(def.DefType, fields.Select(f => (f.Path, f.Value)));
        // 这张名单**不许截**:读的人是拿着某一行来对的,而截了之后「没共享」与
        // 「被 and N more 吃掉了」在这一行上完全同形。
        // 共享数大的排前面:越接近全类型,越像引擎填的。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listed = fields
            .Where(f => f.Value is not null && shared.ContainsKey((f.Path, f.Value)) && seen.Add(f.Path))
            .Select(f => (f.Path, N: shared[(f.Path, f.Value!)]))
            .OrderByDescending(x => x.N).ThenBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => $"{x.Path} ({x.N})")
            .ToList();
        // 分母必须与 shared_values 同口径 —— 那张表是全库算的,所以这里写死 all。
        var total = ctx.Db.CountDefsOfType(
            def.DefType, Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));

        // 「code_default 这一列是什么意思」搬进了 SKILL.md —— 它逐字不随 def 变。
        // 但「这不是这个 def 挑的」那半句得留:名单本身只是几个路径加数字,不说破的话
        // 一行 code_default=no 就会被当成「作者在这里做了个决定」读走。
        //
        // 指列名而不是指那一列的取值:整列同值时渲染器会把它折进表上方那行
        // (`Same in every row: code_default=no`),于是「上面的一个 no」在表里根本不存在,
        // 这句话就指了个空。列名在折叠行里照样出现,指它才两种排布都成立。
        //
        // 范围也得写进句子:shared_values 建表时就把「与声明默认值相同」的行整批排除了
        // (见那张表的建表注释),于是一行 yes 从来没进过候选。不说破的话,「上面没有一个」
        // 印在一张只有 yes 行的表下面会被读成结论 —— 而那是没比过,不是比过了没有。
        ctx.Report.Notice(NoticeKind.Advisory, listed.Count > 0
            ? $"Values that most of the {total} {def.DefType}s in this snapshot also carry, so their " +
              $"'{FieldDefault.Column}' is not this def having made a choice — the count in brackets: " +
              $"{NameList.Render(listed, listed.Count)}. " +
              $"Only rows whose '{FieldDefault.Column}' is no were compared: a yes is a declared default already."
            // 否定支砍掉「所以没有一个是透过那一列显出来的全类默认值」:那是前半句的改写,
            // 而 SKILL.md 讲过这条线是干什么的。「yes 没参与比较」那半句一个字不动 ——
            // 它守的是「没比过」被读成「比过了没有」,而那正是这一支印在一张全 yes 表下面时的样子。
            : $"No value above with '{FieldDefault.Column}'=no is one that most of the {total} " +
              $"{def.DefType}s in this snapshot also carry. " +
              $"Rows marked yes were not compared: a yes is a declared default already.");
    }

    /// <summary>
    /// 嵌套 <c>Class="…"</c> 的运行时类型这一维,手上这份快照量到哪一步。
    ///
    /// **三档,不是两档。** 0.2.0 只发列表元素(判据是「路径以 ] 收尾」),0.4.0 起单字段
    /// 上的 <c>Class=</c> 才一并发。中间那一档最险:<c>where Class X</c> 照样回零,而那个零
    /// 与「量过了、确实没人用它」逐字同形 —— 一句 "is the query that reaches it" 会把人
    /// 送去查一条对 <c>&lt;genStep Class="…"&gt;</c> 根本不存在的路径,走空了再照这句
    /// 试一遍,闭环。所以中间档必须点名它够不着的是哪一类。
    /// </summary>
    public static string NestedClassLine(CommandContext ctx)
    {
        if (ctx.Db.Meta.IndexesAllNestedClass)
            return "The runtime type of a nested Class=\"...\" object — in a list or on a single field — is " +
                   "indexed as '<path>.Class', so 'rimsearcher where Class <ClassName>' is the query that reaches it.";

        if (ctx.Db.Meta.IndexesNestedClass)
            return "The runtime type of a nested Class=\"...\" object is indexed as '<path>.Class' for list " +
                   "elements only (<li Class=\"...\">), so 'rimsearcher where Class <ClassName>' reaches those. " +
                   // 句尾不重复指 code-search:三个调用点各自都已经指过了,
                   // 而 NoteIndexHoldsValuesOnly 的结尾正好就是那一句。
                   "A single field that picks a class — GenStepDef.genStep, ThinkTreeDef.thinkRoot — is not in " +
                   $"this snapshot at all: it was written by exporter {ctx.Db.Meta.ExporterVersion}, before that " +
                   "case entered the index, and a zero from that query is not evidence about it. Re-export to reach it.";

        return "The runtime type of a nested Class=\"...\" object is not in this snapshot at all: it was " +
               $"written by exporter {ctx.Db.Meta.ExporterVersion}, before that type entered the index, so no " +
               "query here reaches it — re-export to get 'rimsearcher where Class <ClassName>'.";
    }

    /// <summary>
    /// 摆在表**上面**,不进脚注区 —— 与 <see cref="Report.DeferredNotice"/> 那条「表头留给随查询
    /// 变化的东西」一致:它只在真有 def 被砍过时才出声(<c>affected.Count == 0</c> 直接返回),
    /// 不是每次都在同一位置说同一句的横幅。
    ///
    /// 沉在脚注区时它是可以被无声切掉的:`| head` 或 `Select-Object -First N` 砍掉尾巴之后,
    /// 剩下的输出与完整输出**逐字相同** —— line 1 的计数只担保表,对脚注一个字都没说,
    /// 于是没有任何信号提示「这里少了一句『这答案可能缺东西』」。
    /// </summary>
    /// <param name="basis">
    /// 这批类型是**怎么圈出来的**,整句写全。三条调用路各不相同(用得到这条路径 /
    /// 取到过这个值 / 就是这一个类型),而句子此前一律写死成 "carrying this path" ——
    /// 按值那条与按类型那条上,那句话说的不是它做的事。
    ///
    /// 范围本身也得由调用方说破「比上面那张表宽」:一个被砍过的 def 丢掉的可能正是本次
    /// 问的那个字段,所以担保必须按类型给,给不了「只看表里这几行」那么窄 —— 而读的人
    /// 默认按表读,于是名单里冒出表里没有的类型时,整条脚注会被当成虚警。
    /// </param>
    public static void NoteIndexedPathsOnly(CommandContext ctx, TruncationScope affected, string basis)
    {
        if (affected.Count == 0) return;

        var types = affected.Types;
        var shown = types.Take(Limits.MaxSuggestions).ToList();
        var cmd = "rimsearcher snapshot truncated" +
                  string.Concat(shown.Select(t => $" --type {t}"));
        // 类型当场点名,不写「the same def types」。主语固定,计数进从句:名词有登记处,
        // 动词没有。
        // 「计数只覆盖索引到的路径」这条规则搬进了 SKILL.md。这里留的是它在**这一次**
        // 的具体后果:范围圈住了哪几个 def 类型、其中多少个 def 在导出时掉过字段、
        // 以及那条已经填好参数的交叉验证命令 —— 三样都是查一次才知道的。
        // basis 自己已经点了那个类型的名时,名单就是同一个词再说一遍。
        var alreadyNamed = types.Count == 1 && basis.Contains(types[0], StringComparison.Ordinal);
        var tally = Tally.Complete(affected.Count).Render("def");

        ctx.Report.Notice(NoticeKind.Boundary,
            "Defs whose export was cut short can be missing from this answer: the field asked about may be " +
            "one of the ones they lost. " +
            (alreadyNamed
                ? $"That risk spans {basis}, holding {tally} cut short. "
                : $"That risk spans {basis} — {NameList.Render(types, Limits.MaxSuggestions)}, " +
                  $"holding {tally} cut short between them. ") +
            $"'{cmd}' lists " +
            (shown.Count == types.Count
                ? "them."
                : $"the ones of the {shown.Count} biggest of those types; for the rest, the bare " +
                  "'rimsearcher snapshot truncated' covers every type at once."));
    }
}

internal static class Advisory
{
    /// <summary>
    /// 值侧是**单语**的:field_values 存的是游戏加载时的那一份文本(这份快照的语言),
    /// 而另一侧只活在 translations 表里,只有 `search` 看得见。
    ///
    /// 于是中文快照上 `where --value "shield belt"` 回「本快照没有任何字段装着这段文本」,
    /// 而 `search "shield belt"` 当场命中 —— 两句话都对,而前者与「这东西真不存在」逐字同形。
    ///
    /// 只在文本索引**真的命中**时出声,并且点名命中了谁 —— 一句无条件的「值侧是单语的」
    /// 就是免责声明,而这一句自带可验证的下一步。
    /// </summary>
    /// <param name="reachedBy">
    /// 够不着它的是哪个旋钮。<c>where --value</c> 与 <c>list --find</c> 撞的是同一堵墙
    /// (库里存的是加载后的那一份文本),但句尾若写死 <c>--value</c>,另一条路上的读者
    /// 会去查一个自己没用过的参数。
    /// </param>
    public static void NoteTextIndexHasIt(CommandContext ctx, string value, string reachedBy = "--value")
    {
        var wide = ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config);
        var (rows, total) = ctx.Db.SearchFts(value, wide, null, Limits.MaxSuggestions);
        if (rows.Count == 0) return;

        ctx.Report.Notice(NoticeKind.NextStep,
            $"The text index does have '{value}' though — " +
            NameList.Render([.. rows.Select(r => $"{r.DefName} ({r.DefType})")], Limits.MaxSuggestions,
                            total: total) +
            $". Names and values alike are stored as the game loaded them, in this snapshot's language " +
            $"({ctx.Db.Meta.Language}); the other side of a translated label or description lives only in " +
            $"the text index, so 'rimsearcher search {Quote(value)}' reaches it and {reachedBy} cannot.");
    }

    private static string Quote(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

    /// <summary>
    /// 这一屏里有两行的 label **逐字相同**。
    ///
    /// 同 label 同 def_type 同 mod 是常态(`TrapSpringChance` 与 `PawnTrapSpringChance` 的
    /// 简中 label 都是「陷阱触发率」),此时表里没有任何一列分得开它们,而问的人只想要
    /// 其中一个。这一类不能靠「多说一句边界」修:查询技术上成功了,表是完整的,
    /// 没有任何异常信号,只是**看得见的那几列不足以判**。
    ///
    /// 所以这一句只做一件事:把撞在一起的那几组点出来,并指向真正分得开它们的东西
    /// (description 不在表里)。没撞就一个字不说。
    /// </summary>
    /// <summary>
    /// 这一次命中横跨了不止一种路径形状。
    ///
    /// `where stat Mass` 的上千行里会混进几行 <c>statFactors[].stat</c>,拿它做集合差时
    /// 那几行是**静默假阴性** —— 表里确实印了 path 列,但默认 25 行的视图下没人会逐行核对
    /// 路径形状,而 `where` 恰恰是这套命令里用来做集合运算的那一个。
    ///
    /// 只有一种形状时一个字不说 —— 那时「结果集是齐的」是无条件的。
    /// </summary>
    public static void NoteMixedPathShapes(CommandContext ctx, IReadOnlyList<(string Shape, int Count)> shapes)
    {
        if (shapes.Count < 2) return;
        var shown = shapes.Take(Limits.MaxSuggestions).ToList();
        ctx.Report.Notice(NoticeKind.Boundary,
            "These rows span more than one path shape: " +
            string.Join(", ", shown.Select(x => $"{x.Shape} ({x.Count})")) +
            (shapes.Count > shown.Count ? $", and {shapes.Count - shown.Count} more shapes" : "") +
            ". The suffix matched them all; a set operation over this result treats them as one field " +
            "unless the path column is read row by row. Pasting one of those shapes back with " +
            "--exact-path keeps that one alone.",
            footnote: true);
    }

    private static readonly IEqualityComparer<(string, string DefType)> TupleComparer =
        EqualityComparer<(string, string DefType)>.Default;

    public static void NoteSameLabel(CommandContext ctx, IReadOnlyList<DefRow> rows)
    {
        // 同 label **且同 def 类型**才算撞。类型不同的那种(ConceptDef 与 ThingDef 都叫
        // 「护盾腰带」)表里 def_type 列当场分得开,而句中「表里没有列分得开」那半句
        // 在那种情形下是**假的**。
        var clashes = rows.Where(r => r.Label is { Length: > 0 })
                          .GroupBy(r => (r.Label!, r.DefType), TupleComparer)
                          .Where(g => g.Count() > 1)
                          .ToList();
        if (clashes.Count == 0) return;

        var shown = clashes.Take(Limits.MaxSuggestions)
                           .Select(g => $"'{g.Key.Item1}' ({g.Key.DefType}: " +
                                        $"{NameList.Render([.. g.Select(r => r.DefName)], Limits.MaxSuggestions)})")
                           .ToList();
        ctx.Report.Notice(NoticeKind.Advisory,
            "Rows above carry the same label and the same def type: " + string.Join("; ", shown) +
            (clashes.Count > shown.Count ? $", and {clashes.Count - shown.Count} more such labels" : "") +
            ". The defName is the only column that tells them apart; the description, which is not in this " +
            "table, says which is which — 'rimsearcher get <defName> --path-contains description'.",
            footnote: true);
    }

    /// <summary>
    /// 环境外翻译的聚合尾注:逐条标注聚合成一行,不是每行挂一句。
    /// </summary>
    public static void NoteOutsideTranslations(CommandContext ctx, IEnumerable<string> defNames)
    {
        var n = ctx.Db.CountTranslationsOutside(defNames);
        if (n == 0) return;
        ctx.Report.Notice(NoticeKind.Advisory,
            // 尾句砍掉:`get <defName>` 那个占位符贴不回去 —— 要敲它得先知道是上面哪几个 def,
            // 而这句话正是那个不知道。它是指路的形状,不是指路。
            $"{Tally.Complete(n).Render("def")} above also matched language files from mods that are installed " +
            "but were not enabled in this snapshot; those translations are searchable but were not in effect.",
            footnote: true);
    }

    /// <summary>
    /// 同一块 <c>comps[N]</c> 里、有人设过的兄弟字段(同样是聚合成一行,不是每行挂一句)。
    ///
    /// 同块字段会互相覆盖(`minFuelCost=50` 盖掉同块的 `fuelPerTile=3`),而只列出后者的
    /// 那张表干净、计数明确、一条警告都没有。一句话只做一件事:**点名**,不解释谁盖谁 ——
    /// 工具证得了「这几个字段有人设过、而且与你看的这个同处一块」,证不了它们的关系。
    ///
    /// 收窄在调用侧(<paramref name="shown"/> 已经筛过):只有当**你看的这一行自己**是有人
    /// 设过的值时才提示。判别字段(compClass / thingClass / workerClass)按定义就是声明
    /// 默认值,而 `where compClass CompShield` 恰恰是推荐的那条主查询 —— 在它上面挂一句
    /// 「同块还有 energyMax」是纯噪音,而噪音要在所有调用上收税。
    /// </summary>
    public static void NoteAuthoredSiblings(CommandContext ctx,
                                            IEnumerable<(string DefName, long DefId, string Path)> shown)
    {
        var rows = shown.ToList();
        var names = ctx.Db.AuthoredSiblings(rows.Select(r => (r.DefId, r.Path)));
        if (names.Count == 0) return;

        // 块名不能写死成 `comps[N]`:ContainerPrefix 对**任何**带下标的层都成立
        // (`statBases[8]`、`corePart.parts[6]`、`degreeDatas[0].statFactors[0]`)。
        // 块名与 defName 都当场算得出来,句中的占位符一个都不许留。
        var first = rows.FirstOrDefault(r => PathSegments.ContainerPrefix(r.Path) is not null);
        var block = first.Path is null ? null : PathSegments.ContainerPrefix(first.Path)?.TrimEnd('.');

        ctx.Report.Notice(NoticeKind.Advisory,
            $"Set by hand in the same block as the rows above: {NameList.Render(names, Limits.MaxSuggestions)}. " +
            (block is null
                ? "Fields in one indexed block bind and override each other, and this table shows only the one asked for."
                : $"Fields in one {block} entry bind and override each other, and this table shows only the one " +
                  $"asked for. 'rimsearcher get {first.DefName} --path-contains {block}' lists the whole block."),
            footnote: true);
    }
}
