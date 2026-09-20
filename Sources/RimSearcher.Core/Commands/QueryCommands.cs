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
            "Matching runs in stages and stops at the first one that finds anything: full-text search, " +
            "which sees only letters and digits and splits a name at its word starts; a substring pass " +
            "over names, which reaches a query sitting in the middle of a compound name; the " +
            "pre-translation original text of translations; then fuzzy identifier matching that tolerates " +
            "typos and CamelCase initials. You never need to add '*' yourself; a query with no letter or " +
            "digit in it skips the full-text stage. Translated text is in the full-text index, so a " +
            "Chinese label finds the def; the " +
            "English wording it replaced is not, and is reached only by that later pass — which is why an " +
            "English query against a translated snapshot can come back with rows whose label column is not " +
            "English. Each result says in 'matched_on' which of these it was.\n\n" + NameLookup.Help,
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
            new() { Key = "defs", Rows = true, What = "one row per matching def: def_name, def_type, label, matched_on, declared_in." },
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
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

        // 同一个查询换个筛子能命中几个 —— 与下面的主查询同一套两段(FTS 接子串补扫)。
        int Hits(ScopeFilter s, string? t)
            => ctx.Db.SearchFts(query, s, t, 1, 0).Total +
               (IsCompoundToken(query) ? ctx.Db.NamesContainingUnmatched(query, s, t).Count : 0);

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
        //
        // 空串单独一档:它是每一行的子串,扫出来的是整张表,而那不是一个结果 —— 用法错。
        // 别的标点(`.`)只是命中得宽,不是必然命中全体,照常扫。
        if (query.Length == 0)
            throw new CliUsageException(
                "'search' needs a query with a letter or digit in it. 'rimsearcher list <DefType>' enumerates a type.");
        var nothingToMatch = FtsText.HasNothingToMatch(query);
        if (nothingToMatch)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{query}' holds no letter or digit, so the rows below come from scanning text for it as a " +
                "plain substring.");

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
                    "in the original text a translation replaced.");
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
                    $"'{query}' as a substring.");
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

            // 自己给的两个筛子各算一次「单独拿掉能回来几个」(Docs/25 乙1)。查询词没内容时
            // FTS 没被问过,这两个数就不成立,不算。
            if (!nothingToMatch)
            {
                var causes = new List<EmptyCause>();
                if (!scope.IsAll)
                    causes.Add(new(ctx.FilterAsGiven("scope"), Hits(ctx.Unscoped(), type), ctx.Without("scope")));
                if (type is not null)
                    causes.Add(new(ctx.FilterAsGiven("type"), Hits(scope, null), ctx.Without("type")));
                ctx.Report.EmptyBecause(causes);
            }

            // 名字的真实落点当场算得出来:算得出就印 found_as 的一行,算不出才退回按形状猜。
            var sighting = NameLookup.Locate(ctx, query, scope);
            var looksLikeClass = ClassNameShape.Looks(query);
            if (sighting is not null) NameLookup.Say(ctx, sighting);
            else ctx.Report.Notice(NoticeKind.NextStep,
                (looksLikeClass
                    // 嵌套 `Class=` **是**被索引的,所以不许说「索引不到」——
                    // 那句话会把 `where Class` 的零判成「工具看不见」,而不是「确实没有」。
                    // 念 NestedClassLine 那个唯一产地,不在这里另写一句。
                    ? $"Nothing in this snapshot is called that under any other guise either — no def type, " +
                      $"no class, no mod. " + Completeness.NestedClassLine(ctx) +
                      // 两条出路各标自己列出什么;「类可以不经过 def 被 C# 直接 new」那半是情景假设,
                      // 2026-09-18 删(Docs/25 §16 / §18)—— 出路的标签已经把两种落点分开。
                      $" 'rimsearcher code-search \"class {ClassNameShape.Tail(query)}\\b\"' finds the class " +
                      $"declaration; 'rimsearcher code-search \"{ClassNameShape.Tail(query)}\"' lists who uses it."
                    : "'rimsearcher list' with no def type lists what kinds of def this snapshot holds, " +
                      "and 'rimsearcher mods' lists which mods it covers."));
        }

        // 表上方 —— 理由同 list 那处。主结果退到模糊候选时这句更要紧:表里印的是
        // 「拼写最接近的」,而被排除的那半边可能有真命中 —— 两者在表上长得一样,
        // 只有这个数分得开。
        ctx.AnnounceExcluded(scope, rest => CountIn(ctx, query, rest, type), "def");

        // 「靠什么命中的」必须在表里:命中可以来自子结构(如 TraitDef 某一档 degreeData 的
        // label),而那一行自己的 label 是空的 —— 不说清就会被归到邻行上。
        ctx.Report.Table("defs", ["def_name", "def_type", "label", "matched_on", "declared_in"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["label"] = r.Label,
                ["matched_on"] = how.StartsWith("fuzzy", StringComparison.Ordinal)
                                 ? "closest spelling"
                                 : MatchedOn(ctx, r, query),
                ["declared_in"] = r.SourceMod,
            }).ToList());

        Advisory.NoteSameLabel(ctx, rows);
        return rows.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// 同一个查询换个 scope 命中多少 —— 只出数,不取行。
    ///
    /// **口径必须与 <see cref="Run"/> 里那三段一致**:FTS、复合词的子串补扫、
    /// 零命中时的译文原文回退。三段各自的理由见 Run 里的注释。少一段就会把
    /// 「被排除的那半边有」少报成 0,而这句话一旦报 0 就整个沉默 —— 那正是它要修的东西。
    ///
    /// 模糊回退不进这个数:那一段产出的是「拼写最接近的」候选,不是命中。
    /// </summary>
    private static int CountIn(CommandContext ctx, string query, ScopeFilter scope, string? type)
    {
        var (_, total) = ctx.Db.SearchFts(query, scope, type, 0, 0);
        if (IsCompoundToken(query)) total += ctx.Db.NamesContainingUnmatched(query, scope, type).Count;
        if (total == 0) total = ctx.Db.NamesByTranslationOriginal(query, scope, type).Count;
        return total;
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
        // 命中的是没启用的 mod 的语言文件时,这一格自己说(与 get 的 origin 格同一个词);
        // 此前是表外一句「N 个 def 也命中了未启用 mod 的语言文件」,而它数的是有没有那种译文,
        // 不是命中的是不是那一条。
        if (t is not null) return GetCommand.OutsideSnapshot(t) ? $"{t.Path} (file{GetCommand.NotEnabled})" : t.Path;

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
        Summary = "Show a def in full: its identity, its fields, and any translations of it. " +
                  "Several names, or --type on its own, print one such block each.",
        Remarks =
            "Field paths are the merged, post-patch shape the game actually had in memory when the snapshot was " +
            "taken, so PatchOperations and inheritance are already applied. A def created in code rather than XML " +
            "says so on its source line.\n\n" +
            // source 这一列一直在印文件名,把它是什么、不是什么一次说完。
            "The 'source' line is the bare file name the game reported for that def — no directory, because " +
            "the game does not keep one. It names the file inside that mod's Defs folder ('declared_in' " +
            "above says which mod); it is not a path, and nothing here reads the file system to confirm the file is " +
            "still there. Defs the game builds in code carry a placeholder there instead.\n\n" +
            "When present, the 'xml' column says whether this def's own XML wrote the path (here), only an " +
            "ancestor did (parent), or neither of them did (not-written) — the fact PatchOperationReplace " +
            "vs Add turns on. " +
            "Index paths such as costList[0].thingDef are joined back to def-name tags such as costList.Steel " +
            "from a sibling value on the same list entry, including two-level tags (things.AncientAmmoStack.chance), " +
            "and using XML lines written by this def or by an ancestor. After that join, here/parent means the " +
            "line is there, and not-written means the XML read here does not write it. The output says which XML it " +
            "read. 'read after every patch ran' is the merged XML after every PatchOperation ran, and a line " +
            "another mod's patch put there reads as here+patch or parent+patch: Replace still finds that node, " +
            "but your patch now depends on that mod staying loaded. 'read before patches ran' is the XML as " +
            "written on disk, so there a patched-in node reads as not-written instead, and 'rimsearcher inherit " +
            "<defName>' reports how many patch xpaths name this def. A further value, 'under <container>', means the XML wrote that container but " +
            "this row still cannot be pinned to a line in it: the entry did not join, or it joined to a " +
            "short-form tag such as <Steel>75</Steel> whose inline text matches none of the remaining fields, " +
            "or more than one of them — or the snapshot did not record that text. Neither answer is " +
            "available there. A snapshot without the column says so.\n\n" +
            // 身份行不进字段表,理由在 SnapshotDb.Fields。说一句,免得拿字段数对 values/where
            // 那边的计数时差一行没人解释。
            "defName is not listed as a field: the def_name line above the table is that value, and the " +
            "counts here leave it out. --path-contains naming it brings that row back; 'where' and 'values' " +
            "see it as a path either way.\n\n" +
            "The translations table's 'origin' column says what became of each row. 'in effect' is what the " +
            "game displays. 'in pack, not applied' is a record the language pack carries that the game did " +
            "not inject: '(key on no slot)' when the key names no injectable slot of this def — usually a " +
            "handle taken from a label the author has since changed; the row's 'path' is that key rewritten, " +
            "so it lines up with nothing in the field table — or '(slot not translatable)' when the slot is " +
            "real but the game marks it as not translatable. 'in pack' alone is a snapshot that predates the " +
            "game's verdict. 'file (<mod>)' was read from that mod's language files on disk, ', not enabled' " +
            "when the mod is installed but not enabled, so the game never read it, ', N files' when the same " +
            "key sits in more than one of its files. The two slot suffixes attach to a file row the same " +
            "way. Rows that carry no def " +
            "type come from language files, whose keys are '<defName>.<field>'; the game injects those by " +
            "name, so on a name shared by several defs they are listed under each.\n\n" +
            Advisory.SiblingHelp + "\n\n" + IndexGap.Help,
        // 一个名字与一批名字走同一条路:多名只是把「撞名连印」那套块格式的入口从
        // 「同名跨类型」放宽到「调用方点了几个名字」。不给名字、只给 --type 是第三个入口,
        // 同一套块。三个入口一份渲染,单名那条路的输出一个字节不动 —— 下游脚本与 skill
        // 都照着它写。
        //
        // 不给名字就整类,而不是要一个显式 --all:2026-09-07 六份盲测里,两个没见过任何方案
        // 的被试凭直觉敲的都是这一形;读了两版文案的四人各自嫌自己那版(隔壁草更绿),
        // 而「忘了名字想先看看」这一题六人全走 list,误触没测出来。定的是隐式,并留了一段
        // 观察期(tools/scan-get-batch.py 读会话记录),误触真发生再补 --all。
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defName",
                Required = false,
                Variadic = true,
                Help = "The exact def name. 'search' finds it if you only know part of it. Several names " +
                       "print one block each, in the order given; a name that matches nothing is reported " +
                       "in a note and the others still print. Leave the names out and give --type to " +
                       "print every def of that type.",
            },
        ],
        Options =
        [
            // 多块输出里 --limit 截的是每块的字段,不是 def 个数 —— 不说的话
            // 「--type GeneDef --limit 10」会被读成「只出 10 个 def」,而两种读法的输出
            // 都是一份看着正常的表。
            CommonOptions.Limit("fields") with
            {
                Help = CommonOptions.Limit("fields").Help +
                       " It counts fields inside each block; the number of blocks printed is set by the " +
                       "names you give, or by --type.",
            },
            new OptionSpec
            {
                // 没有它,在几百字段的 def 里找一条路径只能整份打出来再 grep 输出。
                Name = "path-contains",
                Arity = Arity.Multi,
                // 主名与别名各由一头的实测定:识别测 path-contains 10/10(危险的两种误读
                // ——读成文件系统路径、读成按值匹配——各零例),而产出式里没人写得出它,
                // 24/24 伸手去抓的是 filter。所以 filter 留作别名接住伸手。
                // 光叫 path 两头都不占:产出式 0/24,而它与文件路径撞词。
                //
                // path 后来还是收进别名了,而那条实测没被推翻 —— 换的是证据来源:
                // 真实调用里(Vethara 那批会话)`get … --path` 打了 112 次,一次都没成。
                // 产出式盲测问「你会怎么写」,真实调用记的是「先写了什么」,而这个选项的
                // 主名恰好长得像它的一个前缀,伸手去抓 path 是很自然的第一下。
                // 撞词那半仍然成立,所以它只是别名:`docs --path` 是 --out 的别名,而
                // get / inherit 这边一个文件路径选项都没有,同一条命令里不产生歧义。
                // "only" 不在这里:sources sync 有一个真的 --only。
                //
                // `values` 同样由真实调用定:伸手写它的有 15 次 / 13 会话,实参给的全是
                // 字段路径(statBases / workerClass / ingestible),而被拒之后的重写十之
                // 八九就是 --path-contains 同一个词。
                //
                // 单数的 `--value` 不收 —— 那 14 次实参是值或 stat 名(MarketValue /
                // MaxNutrition),与这个选项要的东西是两类。于是要问:写错单复数的人
                // 落到哪。实测两支,别按「探针总会出声」记:
                //   · 那个值就在这个 def 上 → 点名并指去 `where --value`
                //     (`get AIPersonaCore --values MarketValue`)。
                //   · 不在 → 换成「同类别的 def 有这条路径」那一支,而它可能指向一条只是
                //     **含**这段文本的无关路径(MarketValue → race.meatMarketValue)。
                // 两支都不给假表:开头那句「No field path contains 'X'」在哪一支下都成立。
                // 按 Docs/12 的判据(会静默给出错答案的旧名不留作别名),第二支只是没帮上忙,
                // 不是给了个错答案,所以这个别名收得下。
                //
                // 删掉的四个 —— field / field-path / path-filter / field-contains ——
                //
                // 删掉的四个 —— field / field-path / path-filter / field-contains ——
                // 在 get / inherit / fields 三条命令上逐字**各 0 次**(28377 次真实调用)。
                // 留着的代价不是零:它们进「Did you mean」的候选池,也占着帮助行。
                Aliases = ["filter", "grep", "values", "path"],
                Placeholder = "<text>",
                Help = "Only show field paths containing this text. Repeat it to widen the selection. " +
                       "The translation table is filtered by it too, on the path and on the key alike, with a " +
                       "list subscript tried in both spellings ('[0]' and '.0.'). " +
                       CommonOptions.AnyIndexNote,
                Narrows = true,
            },
            CommonOptions.ExactPathFilter(),
            // 同名跨 def 类型是 RimWorld 常态(PsychicSensitivity 既是 StatDef 又是 TraitDef)。
            // `get` 的 --type 挑的是**哪个 def**,不是从这个 def 的字段里筛,所以计数句里
            // 不念它 —— 念了会被读成「去掉它还有更多字段」,而去掉它得到的是另一个 def。
            CommonOptions.Type with
            {
                Narrows = false,
                Help = CommonOptions.Type.Help + " Given with no def name at all, it selects every def of " +
                       "that type instead, one block each in def-name order.",
            },
            new OptionSpec
            {
                Name = "defaults",
                Arity = Arity.Flag,
                Aliases = ["with-defaults"],
                // 「快照判不了」从 when 从句提到主句,内容一个字没加也没减 —— 只换语法位置。
                // 旧版把描述性的那半句(「值等于新实例已有的值」)放主句、把规定性的那半句
                // 放从句,而实测的失败推理链正是从主句那半截推出来的:「值等于类默认 ⇒ 这个
                // def 没设过它」。SKILL.md 里同一件事是主句(`yes` = the snapshot cannot
                // tell whether anyone set it),两处产地此前强度不同。
                //
                // 那句「照着默认值写一遍与根本没写完全同形」此前压着没搬进来 —— 等的是信道
                // 复验。复验跑完了:抽象地说「工具区分不了」实测 0/10,
                // 把两个 def 各自点名才 4/10,两次复制合并 8/20 对 0/30、p=0.0002。于是搬进来。
                // 「因为它们最常被读成作者选的」这半句 2026-09-07 删掉:它是个理由,而它
                // 想防的那次误读由后面「照着默认值写一遍与根本没写完全同形」那句直接点名 ——
                // 上面那轮实测(0/10 对 4/10)量的正是后面那句,不是这个理由。
                // 「默认不列出」这个事实留着,输出里的「Not listed: N fields…」也照旧印。
                Help = "Also list fields whose value is the one a fresh instance of the declaring type already "
                     + "carries. They are left out by default. "
                     + "The 'xml' column on those rows says whether this def's own XML wrote the "
                     + "path (here), only an ancestor did (parent), neither of them did (not-written), or that the row cannot be "
                     + "pinned to a line inside a container the XML did write (under <container>). The table "
                     + "says beside it which XML that was: 'read after every patch ran' or "
                     + "'read before patches ran'. "
                     + "A yes with xml=here is an explicit write of "
                     + "the default. Without the xml column, a def whose XML "
                     + "writes that same value and a def that never mentions the field look the same. "
                     + "How many were left out is always printed, and --path-contains shows a named field either way.",
            },
        ],
        Examples =
        [
            "rimsearcher get Apparel_ShieldBelt",
            "rimsearcher get Apparel_ShieldBelt --path-contains statBases",
            "rimsearcher get Bullet_Revolver",
            "rimsearcher get Bullet_Revolver --defaults",
            "rimsearcher get Gun_Autopistol Gun_Revolver --type ThingDef",
            "rimsearcher get --type GeneDef --defaults --json",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                Rows = true,
                What = "one object per def carrying the name — each with 'def' (identity), 'fields' " +
                       "(path/value/code_default rows, plus 'xml' when the snapshot recorded which XML lines " +
                       "were written) and 'translations'. Both inner tables are always there, empty array " +
                       "and all. 'defs' stays an array even for a single def, because a name can belong to " +
                       "several def types at once. With several names the objects come in the order the " +
                       "names were given, and with --type alone in def-name order; a name that matched " +
                       "nothing has no object here and one note in 'notes' that quotes it. With a path " +
                       "filter that matched nothing on that def, the object also carries 'index_gap' (asked, " +
                       "state, next) when the cause could be told — see the fields command for the states — " +
                       "and, when the filter emptied the translation table, 'empty_because' for that table.",
            },
            new()
            {
                Key = "absent",
                Rows = true,
                What = "one row per layer these defs would draw on that is short in this snapshot — layer, " +
                       "state, next; empty when every such layer is complete. Today that is " +
                       "'economy' on a ThingDef when prices were not measured (state pre-measure / skipped / " +
                       "unavailable), 'disk_translations' when the import did not scan the language files " +
                       "on disk (skipped / unconfigured / unmeasured), and 'injection_keys' with --path-contains " +
                       "on a snapshot whose translation table has no 'key' column (pre-measure); next is the " +
                       "command that fills the layer.",
            },
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var wantType = ctx.Args.Value("type");
        var given = ctx.Args.Positionals;
        if (given.Count == 0 && wantType is not { Length: > 0 })
            throw new CliUsageException(
                "get needs at least one <defName>, or --type <DefType> on its own to print every def of that type.");

        // 同一个名字给了两遍只印一遍 —— 两块逐字相同,而第二块会被读成「另一个同名 def」。
        // 名字按 NOCASE 比,与 GetDefsNamed 的判据一致。
        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in given)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"Given more than once, printed once: {NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}.");

        // 三个入口(一个名字 / 几个名字 / --type 整类)在这里汇成一列 def,下面一套块渲染。
        // 单名那条路上每一句话、每个分支都与多名之前一字不差 —— 只把 return 1 换成
        // 「单名才 return」。缺席的名字只在 notes 里留一句,其余照印;一个都没落到就 1。
        var single = names.Count == 1;
        var blocks = new List<DefRow>();
        string? whatFollows = null;

        if (names.Count == 0)
        {
            var all = ctx.Db.DefsOfType(wantType!);
            if (all.Count == 0)
            {
                // 「不是分桶键」不等于「不存在」:同 list 的那一档,子类型的 def 躺在基类的桶里。
                // 名字是 class 不是 def 类型:found_as 一行(is = class,next 指 list --class),
                // 「子类共用基类的桶」是机制,住 NameLookup.Help。
                if (NameLookup.AsClass(ctx, wantType!, ctx.Unscoped()) is { } asClass)
                    NameLookup.Say(ctx, asClass);
                else
                    ctx.Report.Notice(NoticeKind.NextStep,
                        DefTypeMiss.Say(wantType!, ctx.Db.Types(ctx.Unscoped()).Select(t => t.Type), "get --type"));
                return 1;
            }
            blocks.AddRange(all);
            // 整类的分界词是 def_name:同一类型下 def_type 行每块都一样,分不开块。
            whatFollows = $"Every {all[0].DefType} in this snapshot: {Tally.Complete(all.Count).Render("def")} follow, " +
                          "one block each, in def-name order; every count and note below belongs to the block " +
                          "it sits in, and the def_name line at the top of a block says which.";
        }

        foreach (var name in names)
        {
            // 过滤前的全量要留住:同名提示按**这个名字一共有几个 def** 说话,不是按这次显示了几个。
            var allMatches = ctx.Db.GetDefsNamed(name);
            var matches = allMatches;

            if (wantType is { Length: > 0 } && matches.Count > 0)
            {
                var kept = matches.Where(d => string.Equals(d.DefType, wantType, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0)
                {
                    // 单名时整份结果是空的,成因是 empty_because 的一行(Docs/25 乙1);几个名字时
                    // 这一句只关于其中一个,而别的名字可能有块,那时还是一句点名的话。
                    if (single)
                    {
                        ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("type"), matches.Count, ctx.Without("type")));
                        return 1;
                    }
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"'{name}' exists in this snapshot but not as a {wantType}. It is " +
                        $"{string.Join(" and ", matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal))}. " +
                        "Drop --type to see it.");
                    continue;
                }
                matches = kept;
            }

            if (matches.Count == 0)
            {
                // 「名字在哪儿」的六种落点统一由 NameLookup 判,抽象父节点是其中一种。
                var sighting = NameLookup.Locate(ctx, name);
                if (sighting is not null)
                {
                    NameLookup.Say(ctx, sighting);
                    if (single) return 1;
                    continue;
                }

                var known = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
                var close = Suggestion.Closest(known, name);

                // 六种落点全在**快照**里,而快照只装 def 侧:句中的「a class」指的是某个 def 的
                // class 列,不是代码树里的 C# 类型(`MapPortal` 就是这样一个类)。代码树上万个
                // 文件,不为每次落空去扫,只把没查的那一半说出来并指名能查的那条命令。
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def is named '{name}' in this snapshot, and it is not a def type, a class, a mod, " +
                    "an abstract XML parent, or a name held by any other registered snapshot." +
                    Suggestion.Say(close, " 'rimsearcher search' matches on labels and translations too.") +
                    " C# type names that no def references live only in the decompiled trees: " +
                    $"'rimsearcher code-search \"class {name}\"'.");
                if (single) return 1;
                continue;
            }

            // 撞名这件事排在**全部段落之前**。此前它在最后:六个同名 def 各带一张完整字段表,
            // 两百行之后才说「这里其实有六个」,而第一段开口可以是一句否定
            // (「No field path of Chimera (PawnKindDef) contains ...」),读的人已经拿它当答案走了。
            // 按全量说话,不按过滤后的集合 —— --type 在场时最需要这句:调用方主动收窄了,
            // 恰恰说明它知道有歧义、并打算只读一个。
            if (allMatches.Count > 1)
            {
                // 不去重:NameCollision 数的是被挡在外面的 def 有几个,不是几种类型。
                var others = allMatches.Where(d => !matches.Contains(d))
                                       .Select(d => d.DefType)
                                       .ToList();
                ctx.Report.Notice(NoticeKind.Boundary, NameCollision.Say(
                    name, allMatches.Count,
                    matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal).ToList(),
                    others));
            }

            blocks.AddRange(matches);
        }

        if (blocks.Count == 0) return 1;

        // 每段各带自己的脚注与截断警告,而那些话只差一个数字 —— 段与段的分界得看得见,
        // 否则「at least 23 fields were dropped」与「at least 174」并排出现时,没人知道
        // 哪条管哪个 def。分界由每段自己的 identity 行给出,这里只说破要按哪一行读:
        // 一个名字撞出几块时按 def_type(名字都一样),几个名字时按 def_name。
        if (blocks.Count > 1)
            ctx.Report.Notice(NoticeKind.Boundary, whatFollows ?? (single
                ? $"{Tally.Complete(blocks.Count).Render("def")} follow, one block each; every count and note " +
                  "below belongs to the block it sits in, and the def_type line at the top of a block says which."
                : $"{Tally.Complete(blocks.Count).Render("def")} follow, one block each, in the order the names " +
                  "were given; every count and note below belongs to the block it sits in, and the def_name " +
                  "line at the top of a block says which."), teach: true);

        var limit = ctx.Limit();
        var (paths, exactPath) = ctx.Args.PathFilters();
        // 报错句里点的旗必须是读者自己敲的那个 —— 同 read 的 --lines / --start。
        var pathSpelling = exactPath ? "--exact-path" : "--path-contains";
        var pathOption = exactPath ? "exact-path" : "path-contains";
        // 字段索引存的是方括号式(stages[0].label),而拿语言文件来查的人手上是点下标式
        // (stages.0.label)—— 后者在字段表里一条都不中,且那与「这个字段不存在」同形。
        // 只把**查询**归一;所有报错句仍引读者自己敲的那一串。
        var fieldPaths = paths.Select(InjectionKey.ToFieldPath).ToList();

        // 下面凡是「只有一块才这么说 / 排」的判断都看 alone,不看某个名字命中了几个。
        var alone = blocks.Count == 1;

        // 这些 def 会用到、而这份库里不完整的层,攒到最后印成一张 absent 表(层 / 状态 / 出路)。
        // 层是库的性质,不是 def 的,所以一次输出只印一张、挂在根上;各块只决定哪些层与自己有关。
        var shortLayers = new List<LayerRow>();
        void Short(LayerRow row)
        {
            if (!row.Complete && shortLayers.All(r => r.Layer != row.Layer)) shortLayers.Add(row);
        }

        foreach (var def in blocks)
        {
            // 这个名字一共挂着几个 def(不论 --type 挡掉了谁)。整类那一路没先查过,
            // 这里统一再查一次 —— 单名路上结果与前面那次相同。
            var namesakes = ctx.Db.GetDefsNamed(def.DefName).Count;
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
                new("declared_in", def.SourceMod),
                // 括号里只说它是什么;「not from an XML file」那半是对着「把 ImpliedDefs 当文件名」
                // 这个假想误读写的,2026-09-20 剪掉。
                new("source", def.Generated
                    ? $"{def.SourceFile} (created in code)"
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
                       ?? (named.Count == 1 && namesakes == 1 ? named[0] : null);
            if (xmlNode?.ParentName is { Length: > 0 } parentName)
                pairs.Add(new("inherits_from", $"{parentName} (see 'rimsearcher inherit {def.DefName}')"));

            // 只有一个 def 时,identity 块**不**排在最前:它是一叠名字,而 line 1 是管道下
            // 唯一的幸存者,那个位置得留给「几条、全不全」。名字是调用方自己敲进来的,
            // 少看一眼不会把截断读成完整。块改挂在字段表正上方(见下面那句 Detail)。
            //
            // 撞名连印时**不动**:那时 line 1 已经是撞名那句,而各段的计数一旦提到自己的
            // identity 块之前,就会紧贴着上一段的表尾,读成上一段的数 —— 上面那句
            // 「读每块顶部的 def_type 行」正是拿这个当分界的。
            if (!alone) ctx.Report.Detail("def", pairs);

            // 默认不列「与 C# 声明默认值无从区分」的那些行。两个例外都指向同一条:
            // **调用方点了名的东西不许消失** —— --path-contains 已经点名了要哪些路径,--defaults
            // 是明说要全量。于是过滤只发生在什么都没点名的那一次。
            var withDefaults = ctx.Args.Flag("defaults") || paths.Count > 0;
            var (fields, matched, total, defaulted, matchedPaths) =
                ctx.Db.Fields(def.Id, limit.Effective, fieldPaths, includeDefaults: withDefaults,
                              exactPath: exactPath);

            // 表在这一段的**末尾**才挂上去(渲染顺序 = Add 顺序)。分界与折叠行那条同理:
            // 数得清多少、全不全,读到行的时候得已经知道 —— 所以计数、过滤、截断在表之前。
            // 读完之后才成立的注解(哪些值是同类型大多数都有的)排在表之后,它们的措辞本来
            // 就写着「above」。
            // 多个 def 同名时,截断声明必须指名道姓 —— 否则两条「Showing 5 of N fields」
            // 并排出现,读者无从知道哪条管哪个 def。
            var whose = alone ? "" : $" of {def.DefName} ({def.DefType})";
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
                        : ctx.Db.TypeDefsWithPath(def.DefType, fieldPaths, exactPath);

                    // 成因算出来的两档各是 index_gap 的一行(Docs/25 §20):状态词 + 填好的命令。
                    // 「null 不进索引」是机制,住 IndexGap.Help。句子里只剩数。
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"No field path{whose} {(exactPath ? "is exactly" : "contains")} {PathFilterText.Say(paths)}; the def does have " +
                        $"{Tally.Complete(total).Render("field")}. Drop {pathSpelling} to see them." +
                        (kin > 0
                            ? $" Other defs of this type do have it: {Tally.Complete(kin).Render("def")} across " +
                              $"{Tally.Complete(kinPaths).Render("field path")}."
                            : ""));
                    foreach (var t in asValue)
                        IndexGap.Say(ctx, t, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {t}");
                    if (kin > 0)
                        IndexGap.Say(ctx, string.Join(", ", paths), IndexGap.NullOnThisDef,
                            $"{CommandRegistry.ExeName} fields {def.DefType} " +
                            string.Join(" ", paths.Select(p => $"{pathSpelling} {p}")));

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
                    var whole = matchedPaths.Count(x => PathSegments.IsWholeSegment(x, fieldPaths));

                    // 第三种可能,而那两句只穷举了两种:名值对结构(statBases[N].stat = MarketValue)
                    // 把**字段的名字搬进了值那一列**,--path-contains 结构上够不着它。于是「命中了几条、
                    // 但没一条是整段」这张表干净、完整,答的却是另一个问题。这一档查得出来,
                    // 就查出来 —— 与完全没命中那支同一个探针。
                    var alsoValue = whole == 0
                        ? paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList()
                        : [];

                    // 名值对的机制住 IndexGap.Help;这里一句事实 + 一行 index_gap。
                    ctx.Report.Notice(NoticeKind.Filter,
                        PathFilterSummary.Say(paths, "on the def", "field", matched, total, whole, whose) +
                        // 「not in any path」2026-09-20 删:上一句已经说了路径上匹配了几条;
                        // 30 次实印,--value 那条出路被照做 33%(基线 6%),半句否定没人读。
                        (alsoValue.Count > 0
                            ? " This def also carries " +
                              $"{PathFilterText.Say(alsoValue)} as a field's value."
                            : ""));
                    foreach (var t in alsoValue)
                        IndexGap.Say(ctx, t, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {t}");
                    if (fields.Count < matched)
                        // 同 ReadCommand 那处:自己拼句子,于是没跟上 ece5f54 换的文法。
                        // "Showing" 前缀跟着去掉 —— 新文法里 "showing" 已经在句中了。
                        ctx.Report.Notice(NoticeKind.Truncation,
                            $"{Tally.Of(fields.Count, matched).RenderTotalFirst("field")}; " +
                            "raise --limit for the rest.", count: Tally.Of(fields.Count, matched));

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
                    alone ? "" : $"this is {def.DefName} ({def.DefType}).");

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
                    // 所以藏了哪些算得出来。
                    var shownIdx = fields.SelectMany(f => PathSegments.IndexPrefixes(f.Path))
                                         .ToHashSet(StringComparer.Ordinal);
                    var hiddenIdx = matchedPaths.SelectMany(PathSegments.IndexPrefixes)
                                                .Distinct(StringComparer.Ordinal)
                                                .Where(x => !shownIdx.Contains(x))
                                                .ToList();
                    // 2026-09-18 压成只剩事实。此前这段带着五截辩护(「on those rows and on the
                    // ones below alike」/「not as not-written」/「is not evidence that nothing wrote」/
                    // 「is in neither count」/ 没藏时的「Every list index … appears below」),每截
                    // 都是某次盲测误读的反面;口径是:辩护句是错误的表现面而非错误本身。
                    // 留下的四件各是一个能核对的事实:没列出几条、出路是 --defaults、索引里
                    // 有几条路径(null 字段不在内)、xml 列读的是哪份 XML。「under <container>」
                    // 的含义与 here/parent/not-written 一起在 get --help 与 SKILL 里,这里不重复。
                    // `--defaults` 仍贴着它召回的那个数:它是这条声明唯一的出路。
                    // 没有 xml 列的老库改说事实:那一列不在时,两种 def 在这里同形 ——
                    // 点名那对(实测 4/10 对 0/10 的正是「点名」,不是那截「不构成证据」)。
                    var readWhen = ctx.Db.Meta.IndexesPostPatchXml
                        ? "read after every patch ran"
                        : "read before patches ran";
                    var xmlLayer = $"The '{XmlOrigin.Column}' column refers to the XML {readWhen}.";
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"Not listed: {Tally.Complete(defaulted).Render("field")} whose value matches the " +
                        "declaring type's own default (--defaults lists them). The snapshot holds " +
                        $"{Tally.Complete(total).Render("field path")} for this " +
                        "def; a null-valued field never entered the index. " + xmlLayer +
                        // 藏了就点名;没藏不另说一句。
                        (hiddenIdx.Count > 0
                            ? " List entries with none of their fields shown below: " +
                              $"{NameList.Render(hiddenIdx, Limits.MaxSuggestions)}."
                            : ""));
                }
            }

            // 「字段被截」与「没有该字段」必须可区分。这一句自己的措辞就把位置钉死了 ——
            // 它说的是 the list below。
            //
            // 加法由这里做完,不留给读者:两个加数此前分在两句里(索引数在上一段、丢掉数在
            // 这一段,中间还常隔着 hiddenIdx 那一大截),而「这个 def 一共有多少字段」的正解
            // 只有那个和。实测六个样本全都认出「索引数是下界」,只有两个把 +N 做完 ——
            // 而两个加数从头到尾都在 CLI 手上。
            if (def.FieldsTruncated > 0)
            {
                var by = ctx.Db.TruncationCausesFor(def.Id);
                var said = ExportCap.OnDef(by, def.FieldsTruncated);
                // 加数是「没进索引的条数」,不是那个总数:值长度那一类的路径就在下面这张表里,
                // 把它加进去等于把已经数过的行再数一遍。成因没量过的库退回总数(旧行为)。
                var missing = by is null || by.Total == 0 ? def.FieldsTruncated : by.Missing;
                ctx.Report.Notice(NoticeKind.Boundary,
                    missing > 0
                        // 「so a path missing from the list below is not evidence that the def lacks it」
                        // 2026-09-18 删掉(Docs/23 第十六轮):带它与不带它,弱档 0/4 对 0/4 都把
                        // 下界当确答,常见档 4/4 对 4/4 都说破了截断 —— 说破靠的是前半句那个数
                        // 与 snapshot truncated,不是这半句。
                        ? "The exporter stopped short on this def: " + said + ". " +
                          // 主语自带,不靠上文:--path-contains 那一支的上文说的是「matching N, out of
                          // M on the def」,而这一句在两支下逐字相同。
                          $"Added to the {total} paths that did get indexed, that is " +
                          $"{Tally.AtLeast(total + missing).Render("field path")} on this def."
                        // 只有值被切时,缺的不是行而是那几格里的字。上面那句会让人去找不存在的缺行。
                        //
                        // 「没缺行」只说一次,而且是**带着那个数**说的:此前它散成三处
                        // (先一句 No field path is missing、再一句 those rows carry the front、
                        // 最后一句 the N paths above are all of them),读者要把三句拼起来才
                        // 拿到「250 就是全部」。数与断言分家时,那个数就只是个可加的量 ——
                        // 而另一支恰恰教人去加。
                        // 「counted above」不行:--path-contains 那一支的上文是「matching N, out of
                        // M on the def」,同一个词在两支下指着不同的数。这一句与另一支一样自带主语。
                        : (total == 1
                              ? "The only field path this def has is indexed"
                              : $"All {Tally.Complete(total).Render("field path")} this def has are indexed") +
                          " — what the exporter cut is text, not rows: " + said +
                          ", so those rows show the front of their value and not the rest.");
            }

            if (alone) ctx.Report.Detail("def", pairs);

            // xml 列的取值只从 XmlOrigin 出。值回连要用同一元素的其它格,所以全量取一次
            // 再按元素前缀分组 —— 不按格查库。
            var xmlMarks = ctx.Db.XmlWrittenMarks(def.DefType, def.DefName, out var xmlContainers, out var xmlTexts,
                                                  out var xmlPatched);
            var cellsByElement = XmlOrigin.CellsByElement(ctx.Db.AllFieldCells(def.Id));

            ctx.Report.Table("fields", ["path", "value", FieldDefault.Column, XmlOrigin.Column],
                fields.Select(f =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["path"] = f.Path,
                        ["value"] = f.Value,
                        // 这一列恒在,不随「本次有没有默认值行」出现或消失:表的形状随数据变,
                        // 照着一次输出写的解析器下一次就取不到键。unknown 也必须能与 no 分开 ——
                        // 「没比成」不是「有人改过」。
                        [FieldDefault.Column] = FieldDefault.Render(f.Default),
                    };
                    row[XmlOrigin.Column] = XmlOrigin.Resolve(
                        f.Path, xmlMarks, xmlContainers, cellsByElement, xmlTexts, xmlPatched);
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList());

            // 表之后:这句讲的是刚读过的那些行(措辞里就是 above)。
            // --path-contains 那条分支同样要说 —— 按 path 收窄恰恰是最容易只盯着一行读的用法。
            // 后两个参数只服务句尾那半句「yes 行没参与比较」:它得知道本次取景里到底有没有
            // yes 行,以及这条否定是不是已经由上面的 Not listed 那句承住了(见那边的注释)。
            Completeness.NoteWidelySharedValues(ctx, def, fields, withDefaults, defaulted);

            // 导出时拿不到打完补丁的文档(PatchRoute=none)的快照,xml 列读的是磁盘上的 XML 原文、
            // PatchOperation 还没跑,于是别的 mod 用 PatchOperationAdd 加进来的一行在那里报 no,
            // 而 no 的出路是 Add —— 会插出第二份。06「patch 溯源」给这份时间差定的处置是逐条报数
            // 而不是写一句常驻免责声明:0 不说,非 0 报出数字。
            //
            // 这个数**只会低估**,两头都漏,所以它非 0 时是硬信号、为 0 时什么都不是:
            // xpath 按 thingClass 或通配符寻址的不留痕迹;既没有 Name= 也没有 ParentName、
            // 又不 abstract 的普通 def 根本不进 xml_nodes,连计数都没有。为 0 与查不到都沉默,
            // 常驻的那半句话在 --help 与 --defaults 的说明里。
            if (!ctx.Db.Meta.IndexesPostPatchXml)
            {
                var node = ctx.Db.NodesNamed(def.DefName)
                    .FirstOrDefault(n => string.Equals(n.DefType, def.DefType, StringComparison.Ordinal)
                                      && string.Equals(n.DefName, def.DefName, StringComparison.Ordinal));
                var xpaths = node is null
                    ? 0
                    : (string.IsNullOrEmpty(node.Name) ? 0 : node.PatchOps)
                      + node.PatchOpsDefName + node.PatchOpsLabel;
                // 收了打完补丁的 XML 之后这句话的前提就没了 —— 补丁加的行在列里自带
                // '+patch',逐行说,比整段告诫准。剩下的只有「补丁改了值」那一路,
                // 而值本来就印在 value 列里。
                if (xpaths > 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"{xpaths} patch xpath{(xpaths == 1 ? "" : "s")} name this def; the " +
                        $"'{XmlOrigin.Column}' column above was read before patches ran. " +
                        $"'rimsearcher inherit {def.DefName}' breaks the count down by how each xpath names it.");
            }

            // 经济面的指路。**这句必须长在 get 上,不能只长在 economy 上**:`economy --help` 里那句
            // 「asking 'get' for a cost returns nothing」印在门的另一侧,要读到它得先找到这扇门。
            //
            // 真实语料(2026-08-04 → 09-20,排掉本仓、盲测仓 rsblind 与审计本 CLI 的会话):带这句的
            // get 222 次,之后三条内敲 economy 24 次(10.8%),不带的 5268 次里 1.8%。但 24 次里 9 次
            // 本会话早用过 economy、7 次 get 与 economy 写在同一个命令块 —— 句子可归因的约 10 次
            // (≈4.5%),一条有证词(「rimsearcher 自己提示了一条我不知道的路」)。承重的是那条可粘贴的
            // 命令(24 次里 13 次逐字照抄)。第十五轮盲测的 38 次里 15 次是 rsblind 的被试,
            // 「11 倍」是那次污染算出来的数。产地 tools/audit-blindtest-prose.py。
            //
            // 只指路、不搬数:handoff §4 明令经济语义不进 get 的 def 查询含义,而且那些数
            // 不是字段,混进字段表会正好造成这一层要防的那个误读。
            // 在场判据是**四态 meta**,不是经济表的行数。
            //
            // 头一版按行数判,并把「没量过经济面的快照上这句自动消失」当成了特性写进注释 ——
            // 那正是本仓拿命防的那件事:它把「这个 def 没被定价」与「这份快照根本没量过定价」
            // 压成同一个沉默。第十五轮第二轮实证:races2(--no-economy)上问「最赚钱的是什么」,
            // 受测档一次 economy 都没跑,`get` 也不吭声,于是它拿 statBases 的价当利润交了卷。
            //
            // 没量过的时候恰恰**更要说**。那时无从知道这个 def 会不会被定价,所以判据退到
            // ThingDef —— 经济面只覆盖 ThingDef,而这一句宁可在没被定价的 ThingDef 上多出一次,
            // 也不能在被定价的那个上沉默。噪声只落在明确降级过的快照上,那是划算的。
            if (ctx.Db.EconomyState == Contract.IntermediateFormat.EconomyStateOk)
            {
                // 判据是**名字加类型**,不能只有名字。经济面只收 ThingDef,而 get 撞名时
                // 一次输出里有好几个块 —— 只按名字问的话,ResearchProjectDef 那一块底下也会
                // 印这句,而它指的 economy 行是另一个 def(实测 HospitalBed:科研项目那块下
                // 推荐 'economy HospitalBed',回来的是建筑医疗床的市价与钢材)。
                // 与下面没量过那一档同一个判据,那一档从一开始就带着它。
                if (DefTypes.Same(def.DefType, "ThingDef") && ctx.Db.EconomyByName(def.DefName).Count > 0)
                    // 「computed, not stored」是机制,r17 量到常见档把机制句读成规格(自己把定价复现一遍);
                    // 这里只剩「不是字段」这个事实与出路。
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"Market value, cost to make and work amount are not fields: " +
                        $"'rimsearcher economy {def.DefName}' has them.", teach: true);
            }
            else if (DefTypes.Same(def.DefType, "ThingDef"))
                // 三态(没到那版 / 跳过 / 量不成)各是一个状态词,与 economy 自己拒绝时印的是同一张表 ——
                // 此前这里把三态一律说成「从未量过」,与 economy 那边的 skipped / unavailable 矛盾。
                Short(DataLayers.EconomyRow(ctx.Db, ctx.SnapshotName ?? ""));

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
            var beforePathFilter = allTranslations.Count;
            // 过滤词落在**两种拼法上都算数**。归一给了 path 一套与字段表可比的坐标,可游戏
            // 认的那一串仍是另一种写法,而拿着语言文件来查的人手上只有后者。同一个坐标的
            // 三种写法(stages[0].label / stages.0.label / stages.observed_corpse.label)于是
            // 都能在**这张表**里选中同一行 —— 读者不必先知道自己手上是哪一种。字段表那侧
            // 只认第一种,所以口径不能写成「一个词两表同中」。
            if (paths.Count > 0)
            {
                // 点下标式两列都不存(path 是方括号,key 是译者写的那串),所以它得先过一道
                // 归一才有得比 —— 只比原样的话,「stages.0.label」筛空,而筛空与「没有译文」同形。
                var needles = paths
                    .SelectMany(p => new[] { p, InjectionKey.ToFieldPath(p) })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                allTranslations = allTranslations
                    .Where(t => needles.Any(p => t.Path.Contains(p, StringComparison.OrdinalIgnoreCase)
                                              || (t.Key?.Contains(p, StringComparison.OrdinalIgnoreCase) ?? false)))
                    .ToList();
            }
            var translations = limit.IsAll
                ? allTranslations
                : allTranslations.Take(limit.Effective).ToList();

            // 数量在表之前,同字段表那条分界。
            if (translations.Count > 0)
                ctx.Report.CountNotice(Tally.Of(translations.Count, allTranslations.Count),
                    "translation",
                    alone ? "" : $"this is {def.DefName} ({def.DefType}).");

            // 筛空的那一次是这个 def 的 empty_because 一行(筛子 / 拿掉它回来几条 / 同一条命令)。
            // 两种拼法都试过是机制,住 --path-contains 的 help;没归一的老快照里译文那栏是
            // **注入键**原样(`stages.0.label` 或 `stages.observed_corpse.label`),拿字段表的
            // `stages[0].label` 贴回来一条都不会中 —— 那是缺层,absent 表给它一行。
            if (paths.Count > 0 && translations.Count == 0 && beforePathFilter > 0)
            {
                ctx.Report.EmptyBecause(
                    new EmptyCause(ctx.FilterAsGiven(pathOption), beforePathFilter, ctx.Without(pathOption)),
                    "translation");
                if (!ctx.Db.HasInjectionKeyRows)
                    Short(DataLayers.InjectionKeysRow(ctx.Db, ctx.SnapshotName ?? ""));
            }

            // 表恒在场,空着也在场。--json 的自述契约是「表键恒在,没命中就是空数组」,而
            // 此前它被 translations.Count > 0 挡在外面,于是「筛空了」与「这个 def 一条译文
            // 都没有」在 JSON 面逐字同形(键都不见)。文本面不变 —— 零行的表渲染出来是零字节。
            ctx.Report.Table("translations",
                ["path", "key", "translated", "original", "language", "origin"],
                translations.Select(t =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["path"] = t.Path,
                        ["translated"] = t.Translated,
                        ["original"] = t.Original,
                        ["language"] = t.Language,
                        ["origin"] = OriginCell(t),
                    };
                    // 归一之后 path 与游戏认的那一串不再逐字相同,而写语言文件的人要的是后者。
                    // 相同时留空:两栏一模一样只会让读者以为它们是两件事。
                    row["key"] = t.Key is { } k && k != t.Path ? k : null;
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList());

            if (translations.Count > 0)
            {
                // origin 那一列印着「in effect」,读的人自然读出「另有 on disk 的没印出来」;
                // 这份库要是没量过磁盘,那个对照根本不存在 —— absent 表里给它一行。
                Short(DataLayers.DiskTranslationsRow(ctx.Db, ctx.Config, ctx.SnapshotName ?? ""));

                // 缺层要宣布:老快照上 `key` 那一列压根不摆,而不说破就与「这些译文没有
                // 另一个键」同形。**只宣布缺层,不预言选不中** —— 这条脚注此前还带一句
                // 「同一个词在两张表里选不到同一处」,而它发在「给了过滤器**且选中了**」的
                // 调用上:那次调用里那个词恰恰两张表都中了,一句现在时的「选不中」对它是假的。
                // 真会踩的人(过滤词带 `[]`)筛出来是空的,走上面那条 Filter,到不了这里。
                //
                // 仍旧只在**给了过滤器**时发。无条件发过一版,`StalenessTests` 立刻红:
                // 干净的一次普通查询要求声明区零字节,而两套文法这件事只在按坐标找东西的人
                // 身上兑现 —— 不按坐标找的人拿到的是「每条命令 6 行」里少掉的一行。
                if (!ctx.Db.HasInjectionKeyRows && paths.Count > 0)
                    Short(DataLayers.InjectionKeysRow(ctx.Db, ctx.SnapshotName ?? ""));

                // 配不上槽位(no-slot)与槽位不许译(refused)两档 2026-09-18 起折进 origin 格
                // (OriginCell:「in pack, not applied (key on no slot)」),此前各是表下一句;
                // 「outside this snapshot」那句脚注同理(origin 格写 not enabled)。机制住 Remarks。

                if (byNameOnly && namesakes > 1)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"These rows carry no def type — language-file keys are '{def.DefName}.<field>' — and " +
                        $"{Tally.Complete(namesakes).Render("def")} share this name; the game injects them by name too.");
            }
        }

        ctx.Report.EndItems();
        ctx.Report.Absent(shortLayers);

        return 0;
    }


    /// <summary>
    /// 译文那张表的 origin 格。
    ///
    /// original 是被替换掉的原文:导出时刻 def 上留的是译文,原文只在注入记录里 ——
    /// 两者同时在场是运行时导出独有的便宜,所以只有 in effect 那些行有它。
    ///
    /// 收割行带上「几个文件」:一个 mod 常同时铺 1.4/ 1.5/ 1.6/ 三套 Languages,同一句话
    /// 逐列全同地存三份。入库时折成一行,而**不说破就等于把「三份同文」印成「一份」**;
    /// 说破了,读者也不会再把它当成三种不同的说法。
    /// </summary>
    private static string OriginCell(TranslationRow t)
    {
        // 运行时那一档三个取值,不是两个。**「语言包里有这条记录」不等于「它生效了」** ——
        // 键配不上槽位、或槽位不许译时,游戏照旧把记录留在包里,只是不注。游戏自己的判决
        // 随行带出;没带判决的行(磁盘语言文件收割来的)落到 in pack,那一格说的是**我们只
        // 知道它在包里**,不许写成 in effect(那是替游戏担保一件没测过的事)。
        // 名册那一档(键配不上槽位 / 槽位不许译)贴在同一格里:两档出路不同(改键能救 / 改了
        // 也没用),而它们与一条正常的「in pack, not applied」此前逐字同形,靠表下两句分。
        var slot = t.KeyState switch
        {
            InjectionKey.State.NoSlot => " (key on no slot)",
            InjectionKey.State.Refused => " (slot not translatable)",
            _ => "",
        };
        if (t.Origin == TranslationOrigin.Runtime)
            return (t.Applied switch { true => "in effect", false => "in pack, not applied", _ => "in pack" }) + slot;
        // 装着、没启用的 mod 的文件:游戏没注它,只够召回。取值自陈,不另挂脚注解释。
        var files = t.SourceFileCount is > 1 ? $", {t.SourceFileCount} files" : "";
        return $"file ({t.SourceMod}{(OutsideSnapshot(t) ? NotEnabled : "")}{files})" + slot;
    }

    public const string NotEnabled = ", not enabled";
    public static bool OutsideSnapshot(TranslationRow t) => t.Origin == TranslationOrigin.HarvestedOutside;

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
            // 「后缀是纯文本、不停在 `.` 上,所以 graphicData.shaderType 也命中
            // swimmingGraphicData.shaderType」这句 2026-09-09 删掉。它是判据的辩护 ——
            // 而判据已经改成段对齐,那句现在是假的。--exact-path 那半句留着:后缀与整条
            // 仍是两件事(compClass 命中每一条以它结尾的路径)。
            // 「whole segments」改「a segment at a time」:whole 这个词此前同时在指整段名字、
            // 整条路径、整个值三件事,而这一句正好挨着 --exact-path 那两行。
            "The field path is matched from the end, a segment at a time, so 'compClass' finds " +
            "'comps[3].compClass' without you knowing the index, and 'graphicData.shaderType' does not reach " +
            // 段对齐落地后,`; --exact-path pins the whole path` 删掉:它紧跟在
            // swimmingGraphicData 那个例子后面,读起来像是为那件事备的 —— 而那件事现在
            // 由后缀规则自己挡住了。它真正还挡的(后缀上面多出来的段)这句没说,而选项表
            // 两行后的「Match the field path given as an argument end to end」已经说完。
            "'swimmingGraphicData.shaderType'. This replaces grepping " +
            "the XML: the values here are the merged, post-patch ones, and a class reference is an exact match " +
            "rather than a text hit.\n\n" +
            "When the rows include defs the game builds in code at load time (Blueprint_*, Frame_*, meat, " +
            "corpses, and the like) a written_in column says which (code / xml). A def written in code has " +
            "no XML node, so a PatchOperation addressed by its defName has nothing to match; the column is " +
            "absent when no such def is among the rows.\n\n" + Advisory.SiblingHelp + "\n\n" + NameLookup.Help,
        Positionals =
        [
            new PositionalSpec { Name = "fieldPath", Option = "field", Help = "A field path or just its last segment, such as compClass or defaultProjectile. " + CommonOptions.AnyIndexNote + " '--field' spells out this same argument. Omit it to search every field instead.", Required = false },
            // value 不挂 Option:--value 与这一格的「给了两遍」由命令自己判 —— 它那句能说出两种写法等价,
            // 解析层那句说不出;而 --value 独自在场(没字段)是一条真的分支,不是这一格的另一种拼法。
            new PositionalSpec { Name = "value", Help = "The value to look for. '--value' spells out this same argument, so give it one way or the other. Omit it to list every def that has the field at all.", Required = false },
        ],
        Options =
        [
            // 这两处数的是**行**,不是 def:一行是一个 (def, 路径) 对,而后缀匹配一放开,
            // 同一个 def 就常在多条路径上命中。41f9a3a 把输出侧的四处口径改到了行,
            // 漏了这里 —— 全套命令里只有 where 一行不等于一个 def,那个 "defs" 在别处都是真话。
            // 名词恒定叫 match:help 是静态的,算不出输出那边「两数相等就叫 def」的判据,
            // 而 match 在两种情形下都是真话。
            CommonOptions.Limit("matches"),
            CommonOptions.Offset("matches"),
            CommonOptions.Scope,
            // 全套查询命令里 where 是最后一个拿到类型面的。它此前没有,而 --scope 按 mod 收、
            // list 按类型列却给不出「值等于 X」—— 于是「哪些 HediffDef 把它设成了这个」
            // 在这套命令里没有写法,而消费侧照 list 的形状把类型写在第一个位置上,那里
            // 正好是 <fieldPath>:`HediffDef` 按后缀命中真实存在的 comps[].hediffDef,
            // 于是拿到的不是报错,是一张答着另一个问题的干净的表。
            CommonOptions.Type,
            new OptionSpec
            {
                // <fieldPath> 的选项拼法。`--field` 在这条命令上被拒了六周、每周仍出现,报错句
                // 08-01 起就给了改正后的整条命令 —— 读者的形状是「类型在前、字段用选项」
                // (`where ThingDef --field defName`,60 条历史样本里 38 条),与 <value>/--value
                // 对称。解析层把它落进同一格(PositionalSpec.Option),命令侧仍只读 Positional(0)。
                Name = "field",
                Placeholder = "<path>",
                Help = "The field path. 'rimsearcher where ThingDef --field compClass --value CompShield' is " +
                       "'rimsearcher where compClass CompShield --type ThingDef'.",
                // 不算收窄:它是问题本身,位置上的同一个词也不进「within …」那句。
            },
            new OptionSpec
            {
                Name = "exact",
                Arity = Arity.Flag,
                Aliases = ["exact-match", "whole"],
                Help = "Require the whole value to match, with either a field path or --value. " +
                       "Without it, the value is matched as a substring.",
                Narrows = true,
            },
            CommonOptions.ExactPath(),
            CommonOptions.PathContainsBeside(),
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
                // 「every field」横跨了它够不到的那部分:导出器有三个上限(每 def 字段数 /
                // 嵌套深度 / 集合项数),索引 ⊊ 数据。这一格的全称落在**搜法**上而不是答案上,
                // 本来就比上面那两处轻,但 every 不带限定时读者读到的仍是「全部字段」。
                // indexed 是本仓对这件事的既定词(见 NoteIndexHoldsValuesOnly)。
                Help = "The value to look for. Without a field path, every indexed field is searched and the " +
                       "report names which paths hold it.",
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
                What = "with a field path: one row per def that has it — def_name, def_type, path, value, " +
                       "code_default, declared_in (the mod whose XML declares the def; a comp another mod " +
                       "bolts onto a vanilla def still reads as the vanilla mod, and --scope filters that " +
                       "same column), plus written_in (xml / code) when a def built in code is among the rows.",
            },
            new()
            {
                Key = "paths",
                What = "without a field path: one row per field path that holds the value — path, def_type, " +
                       "example_value, and the def count split in two: defs_exact (the value is exactly the one " +
                       "asked for) and defs_other (it is inside a longer value). With --exact there is one " +
                       "meaning, so the column is a single 'defs'. This is the key that question produces; " +
                       "'matches' is absent then.",
            },
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
            Completeness.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");

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
            // --exact-path 钉的是位置参数,而这一支没有位置参数。此前它被静默接受,
            // 表头还照印「N field paths within --exact-path」—— 一句声称筛过了的话,
            // 而它一条都没筛。收窄声明只要出现就得对应一次真的收窄。
            if (ctx.Args.Flag("exact-path"))
                throw new CliUsageException(
                    // 不用 "pins":段对齐落地后它全 CLI 只剩这一处,读者在 --help 上学到的
                    // 是 "Match the field path given as an argument end to end",对不上。
                    "--exact-path changes how the field path given as an argument is matched, and this " +
                    "call does not give one. " +
                    "Pass the path ('rimsearcher where statBases[].stat MarketValue --exact-path'), or " +
                    "drop the switch. To narrow which paths are searched without naming one, " +
                    "--path-contains takes a fragment.");

            if (named is null)
            {
                ctx.Report.Promises("matches");
                ctx.Report.Notice(NoticeKind.NextStep,
                    "'where' needs either a field path ('rimsearcher where compClass CompShield') or " +
                    "--value to search every field ('rimsearcher where --value CompShield').");
                return 2;
            }
            ctx.Report.Promises("paths");
            return ByValue(ctx, named, scope, limit, ctx.Args.Flag("exact"), offset, type,
                           ctx.Args.Values("path-contains"));
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
        var pq = new PathQuery(path, ctx.Args.Flag("exact-path"), Contains: ctx.Args.Values("path-contains"));

        var (rows, total, defs) = ctx.Db.FindByField(pq, value, exact, scope, limit.Effective, offset, type);

        // 少写下标那一档。索引里存的是 `statBases[0].stat`,而后缀匹配是纯文本、不认段边界,
        // 于是 `statBases.stat` —— C# 字段名连起来最自然的写法 —— 恒为空,且空得与
        // 「这个字段不存在」逐字同形。会话语料里 166 次「路径不存在」有 33 次就是这个。
        //
        // 只在**查空之后**试,不改默认语义:无条件放开等于把后缀匹配再宽一级,而那会把
        // 原本各自成立的两条路径悄悄并进一张表。判据取 total 不取 rows.Count ——
        // 后者在翻过头时也是 0,而那一档路径是存在的,救援进去会把「翻过头」说成「改写了」。
        //
        // 句子攒着不发 —— 它得排在计数之后(line 1 归「一共几条」,那是管道下唯一的
        // 幸存者),而计数要等这里改写完才算得准。
        string? rewritten = null;
        if (total == 0 && pq.CanTolerateIndex)
        {
            var tolerant = pq with { IndexTolerant = true };
            // 先问形状,再决定要不要跑完整查询。两者的 WHERE 逐条相同,于是「有形状」
            // 与「有行」是同一件事;而形状一次扫描就答得出来,完整查询要扫两遍(计数
            // 一遍、取页一遍)。语料里点分路径查空过半是真没有,那条路上两遍全省掉。
            var shapes = ctx.Db.FindPathShapes(tolerant, value, exact, scope, type);
            if (shapes.Count > 0)
            {
                var retry = ctx.Db.FindByField(tolerant, value, exact, scope, limit.Effective, offset, type);
                // 改写必须说,而且在有结果时最要说:零至少还会让人再看一眼,一张表不会。
                // 说的是**实际命中的形状**而不是「补了下标」—— 形状原样粘回 --exact-path
                // 就是收窄后的查询,而「补了下标」还得读者自己再推一次补在哪儿。
                var shown = shapes.Take(Limits.MaxSuggestions).ToList();
                rewritten =
                    $"Nothing sits at '{path}' as written — indexed paths carry the list index, so this ran as " +
                    string.Join(" / ", shown.Select(x => $"{x.Shape} ({x.Count})")) +
                    (shapes.Count > shown.Count
                        ? $", plus {Tally.Complete(shapes.Count - shown.Count).Render("path shape")} not shown"
                        : "") +
                    // 一条形状与多条形状要的下一步不同:多条时得先挑一条,一条时那句
                    // 「挑一条」是空话,而 --exact-path 照样是把它钉死的那条命令。
                    (shapes.Count > 1
                        ? ". Any one of those goes back in with --exact-path to pool that one alone."
                        : ". That shape goes back in with --exact-path to pin it.");
                pq = tolerant;
                (rows, total, defs) = retry;
            }
        }

        // 数的是**行**,不是 def:一行是一个(def, 路径)对,同一个 def 在多条路径上取到
        // 同一个值就有几行(`where capacity Consciousness` 是 155 行 / 80 个 def,首页
        // 二十五行里 `AlcoholHigh` 一个就占四行)。此前这里印的是「155 defs」,而翻页、
        // --limit、offset 管的自始至终都是行 —— 那个数没错,错的是它叫什么。
        // 名词跟着这个数**真正数的东西**走,而那是算得出来的:两数相等时它同时是行数与
        // def 数,叫 def 是真话;不等时只有 match 说得住。
        //
        // 不一律叫 match:盲测三臂里有两臂在 `where graphicData.shaderType Cutout
        // --exact-path`(1492 行 = 1492 个 def)上取了「25 of 1492」的第一个数,
        // 而问的是「多少个 def」—— 恒定叫 match 时,读者还得自己把 match 折算成 def,
        // 那一步是白加的。
        var noun = defs == total ? "def" : "match";

        // 一个发射口,两条到达路径都从这儿过 —— 「有表」与「翻过头」各写一遍的话,
        // 后者迟早会被漏掉:那条路径 return 得早,而它恰恰是最难想起来的那条。
        // 排在计数之后(line 1 归计数),置空让重复调用无害。
        void NoteRewrite()
        {
            if (rewritten is null) return;
            ctx.Report.Notice(NoticeKind.NextStep, rewritten);
            rewritten = null;
        }

        if (rows.Count > 0)
        {
            ctx.Report.PageNotice(noun, rows.Count, offset, total);
            // 两个数不等时才说:相等时它只是把上一句用另一个词再念一遍。
            // 而不等时非说不可 —— `where` 是这套命令里用来做集合运算的那一个,
            // 「有多少个 def 用着这个值」正是拿它做集合差的人要的那个数。
            if (defs < total)
                ctx.Report.Notice(NoticeKind.Count,
                    // 不说「holds this value」:`where stat` 这种只给路径不给值的问法
                    // 一样会走到这里,那时句子里的「这个值」指不到任何东西。
                    $"Those {Tally.Complete(total).Render("match")} come from " +
                    $"{Tally.Complete(defs).Render("def")}: a def that matches on more than one path has " +
                    "a row for each, so the def_name column repeats.");
        }
        else if (offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render(noun)} in all.");
            NoteRewrite();
            return 1;
        }

        // `where ThingDef Bloomstone` 两个位置参数正好填满,解析层一个字都说不出来;而路径
        // 按后缀匹配,`ThingDef` 命中的是真实存在的 costList[].thingDef —— 于是问「哪些
        // ThingDef 用了它」的人拿到一张干净的、答着另一个问题的表。
        //
        // 重解释在这里**不许发生**:`thingDef` 是真字段名,路径匹配又是 NOCASE,
        // 抢走它等于拿一个静默错答换另一个。但「这个词同时是这份快照里的一个 def 类型」
        // 是当场算得出来的,算得出来就得说。
        //
        // 判据**连大小写一起比**,而这条命令自己的路径匹配是 NOCASE —— 两处口径不同是有意的:
        // 字段名 camelCase、类型名 PascalCase 是 RimWorld 侧的铁律,而它是这里唯一能把两种
        // 意图分开的信号。NOCASE 比的话,`where thingDef Bloomstone` 这条完全合法的查询
        // 每次都要挨一句与它无关的话 —— 实测语料里 82 次误形全是 PascalCase。
        //
        // 有结果时**照说** —— 那正是最贵的一档:零至少还会让人再看一眼,一张六行的表不会。
        // 位置排在计数之后:line 1 是管道下唯一的幸存者,那格归「一共几条」。
        //
        // **只留两样:算出来的那个数,和一条能跑的命令。** 三轮盲测(共 44 次派发,两次改了
        // 设计)都没测出这句话有正面效应:文档在场时,说与不说的被试都是 6/6;把陷阱埋进长
        // 上下文、只要结论不要审计,是 2/8 对 2/8。点破陷阱的四个被试引的都是那条命令,没有
        // 一个引「按后缀匹配」那半句 —— 而那半句在两条路上都是重复的:零那档紧接着的分流
        // 自己会说「没有哪个 def 的字段路径以 X 结尾」,非零那档表里的 path 列直接把
        // costList[0].thingDef 摆在眼前。既然效应测不出来,就不该再占四行。
        NoteRewrite();

        if (type is not { Length: > 0 } &&
            ctx.Db.Types(ctx.Unscoped())
                  .FirstOrDefault(t => string.Equals(t.Type, path, StringComparison.Ordinal))
                is { Type: not null } asType)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{path}' is also a def type here ({Tally.Complete(asType.Count).Render("def")}), and this query " +
                "reads it as a field path, not as a type. For the type: 'rimsearcher where <fieldPath>" +
                (value is null ? "" : $" {value}") + $" --type {asType.Type}'.");


        if (rows.Count == 0)
        {
            // 别的快照里有没有是**算得出来**的,叠加不替换:成因分流照说,这一句排在它后面。
            // 声明在分流之前 —— 它对四条分支一视同仁,而每条分支各自 return。
            void NoteElsewhere()
            {
                // 问「那边有几个 def」而不是「几行」:这句话是拿去跟本快照的零比的,
                // 而零那一侧说的是 def。
                if (NameLookup.Elsewhere(ctx, db => db.FindByField(
                        pq, value, exact,
                        Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0, 0, type).Defs, "def")
                    is { } line)
                    ctx.Report.Notice(NoticeKind.NextStep, line);
            }

            // --type 是第三种**自己施加的**过滤,而它落空时下面那三条成因一句都不成立:
            // 路径在、值也在,只是不坐在这个类型上。判据与 --scope 那条同源 —— 同一条 SQL,
            // type 去掉重跑一次,那次重查是白拿的。
            //
            // 两件事分开:类型压根不在这份快照里(要核对的是拼写),与类型在、只是这个值
            // 不落在它上面(要做的是把 --type 去掉)。合成一句的话,写错类型名的人会拿到
            // 一句「这个类型上没有」,而那句预设了那个类型存在。
            if (type is { Length: > 0 })
            {
                if (UnknownType(ctx, type) is { } unknown)
                {
                    ctx.Report.Notice(NoticeKind.Filter, unknown);
                    return 1;
                }

                // 取 Defs 不取 Total,理由同 --scope 那处:这句的谓语是「有 N 个 def 把它
                // 设成了这个」,而 Total 数的是(def, 路径)行。
                // 成因是一行 empty_because(筛子 / 它挡掉的 def 数 / 拿掉它的同一条命令),
                // 此前是一句「--type X is what emptied this: N defs … Drop --type」(Docs/25 乙1)。
                var hiddenByType = ctx.Db.FindByField(pq, value, exact, scope, 0, 0).Defs;
                if (hiddenByType > 0)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("type"), hiddenByType, ctx.Without("type")));
                    NoteElsewhere();
                    return 1;
                }
            }

            // --exact-path 自己把结果筛空,与「这个字段不存在」是两件事,而下面那套分流
            // 会把它说成后者 —— 它问的是「路径存在吗」,而此时那条路径确实不以整段存在。
            // 排在最前面:这条成因一旦成立,后面三条都不适用。
            //
            // 「不以整段存在」得**自己问一遍**(2026-09-09 补)。此前只问了「作为后缀在吗」,
            // 于是 `where texPath zzznope --exact-path` 也进这一支 —— 而 texPath 本来就是
            // 整条路径,空是那个值造成的。那次读者拿到的是一句假话,外加一条走不通的下一步:
            // 真正该答他的是下面「值不在值域里」那一支。
            if (pq.Exact && !ctx.Db.FieldPathExists(pq, scope) && ctx.Db.FieldPathExists(path, scope))
            {
                // 列出形状而不是报个数:那批形状本身就是下一条查询,而它们带的 `[]`
                // 原样粘回 --exact-path 就走得通。
                //
                // 带值查形状可能一条都不剩(路径在、没有一条带这个值)。那时不许把空表
                // 印成一串形状 —— 此前印出来的是「Matched as a suffix instead: 」后面什么
                // 都没有,紧跟着「Any one of those」指着一个空集。退回不带值的那批形状,
                // 并把「值也得换」说出来:这一格的落空是两件事叠出来的,只说一件都不够。
                var shapes = ctx.Db.FindPathShapes(path, value, exact, scope, type);
                var valueMissedThemAll = shapes.Count == 0;
                if (valueMissedThemAll) shapes = ctx.Db.FindPathShapes(path, null, false, scope, type);
                var shown = shapes.Take(Limits.MaxSuggestions).ToList();
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No field path is exactly '{path}'" +
                    (valueMissedThemAll
                        ? $", and no path ending in it is set to '{value}' either. Those paths: "
                        : ". Matched as a suffix instead: ") +
                    string.Join(", ", shown.Select(x => $"{x.Shape} ({x.Count})")) +
                    (shapes.Count > shown.Count
                        ? $", plus {Tally.Complete(shapes.Count - shown.Count).Render("path shape")} not shown"
                        : "") +
                    ". Any one of those goes straight back into --exact-path, where '[]' stands for any index" +
                    (valueMissedThemAll ? ", but the value has to change too." : "."));
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
                    ["class"] = "'rimsearcher list <DefType> --class <ClassName>' filters by the def's own class",
                    ["def_type"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["deftype"] = "'rimsearcher list <DefType>' lists a whole type",
                    ["mod"] = "'--scope <packageId>' restricts any query to one mod",
                    ["declared_in"] = "'--scope <packageId>' restricts any query to one mod",
                    ["source"] = "the source file is shown by 'rimsearcher get', but is not searchable",
                    ["parent"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                    ["parentname"] = "abstract XML parents are not in a runtime snapshot at all; see 'rimsearcher get --help'",
                };

                // 第四种成因,排在其余三种之前:路径在快照里有,只是被 --scope 圈掉了。
                // 这一档一旦成立,下面那整套(找字段叫什么 / 读声明 / 索引边界)全不适用 ——
                // 字段就在索引里。产地与 `values` 共用,见 OutsideScope。
                //
                // identity 那几个名字不进来:`class` 之类既是身份又可能是真路径,
                // 而「它是 def 的身份」那句答的是更前面的问题。
                if (!identity.ContainsKey(path) && !scope.IsAll &&
                    ctx.Db.FieldPathExists(pq, ctx.Unscoped()))
                {
                    OutsideScope.Say(ctx, pq, type);
                    NoteElsewhere();
                    return 1;
                }

                // 只给了一个词的人给的多半不是字段路径而是一个**值** —— 这条命令的正脸就是
                // 「从一个类名或一个值反查 def」。落点当场算得出来,就说算出来的那一条。
                // 值也给了的那一支不进来:下面那句已经拿着那个值点名了 --value。
                var placed = value is null && !identity.ContainsKey(path) ? Placed(ctx, path, scope) : null;
                // 点分路径专属的一档,与 placed 互斥:placed 认的是**整串**是个名字,
                // 而带点的串在 def 名表与落点表里都查不到,于是那一支恒空。
                var tailValue = value is null && placed is null && !identity.ContainsKey(path)
                    ? TailIsValue(ctx, path, scope, type) : null;
                // 与 values 那一支同一条:算出来「值在更深一层」就不再发带占位符的通用指路。
                var deeper = identity.ContainsKey(path) ? null : Completeness.ValuesLiveDeeper(ctx, path, scope);

                ctx.Report.Notice(NoticeKind.NextStep,
                    // 上面那一档已经把「只是被圈掉了」拿走,走到这里就是**域外也没有** ——
                    // 所以限定语说的是这个,不是 `within --scope X`。后者会被读成
                    // 「放宽也许有」,而这一支恰恰是放宽也没有。与 `values` 逐字同形。
                    $"No def in this snapshot has a field path ending in '{path}'" +
                    (scope.IsAll ? "" : $" (nor anywhere outside --scope {scope.Expression})") + "." +
                    // 尾巴撤掉时那个句点后面不许留空格 —— 基线闸按行尾空白判红。
                    (identity.TryGetValue(path, out var hint)
                        ? $" '{path}' is part of a def's identity rather than one of its fields: {hint}."
                        : deeper is not null
                            ? ""
                            : " 'rimsearcher fields <DefType> --path-contains <text>' lists the paths that a def type actually has" +
                              (value is not null
                                  ? $", and 'rimsearcher where --value {value}' finds which field holds that value."
                                  // 落点算出来了就不给这半句:下一句里同一条命令的参数是**填好的**。
                                  // 末段是取值那一档同理 —— 它那句里的 --value 也是填好的。
                                  : placed is not null || tailValue is not null
                                      ? "."
                                      : ", and 'rimsearcher where --value <text>' finds which path holds a value " +
                                        "you already know.")));

                // 算得出来的结论排在索引边界那句之前;边界那句照旧挂 —— 「它是个值」并不
                // 证明「它不同时是一个没进索引的字段」,两件事正交,叠加不替换。
                if (placed is not null) NameLookup.Say(ctx, placed);
                // 同一格上的另一档:整串不是名字,但**末段**是个取值。理由与 placed 同源 ——
                // 算得出来的结论排在索引边界那句之前,而边界那句照旧挂:「末段是取值」并不
                // 证明「它不同时是一个没进索引的字段」。
                if (tailValue is not null) ctx.Report.Notice(NoticeKind.NextStep, tailValue);
                // 「值住在更深一层」把边界那句**顶掉**,不是叠加:后者列的两条成因
                // (每个 def 都是 null / 不存盘的运行时缓存)在这种局面下一条都不成立。
                if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
                // identity 那一档不说:那时候答案已经给全了,再挂一句索引边界是纯噪音。
                // `class` 是**唯一**的例外:`<path>.Class` 是一条真路径,
                // 敲 `where Class X` 的人问的多半是嵌套子对象的类型,而 identity 那句只答了
                // 「def 自己的 class」。
                else if (!identity.ContainsKey(path) || string.Equals(path, "class", StringComparison.OrdinalIgnoreCase))
                    Completeness.NoteIndexHoldsValuesOnly(ctx, path);
                NoteElsewhere();
                return 1;
            }

            if (value is null)
            {
                OutsideScope.Say(ctx, pq, type);
                NoteElsewhere();
                return 1;
            }

            // 直接把真实值域里的近似项端出来:一条字段的取值动辄上百(compClass 有 175 个),
            // 指一条「去跑 values」的路照样看不见答案。
            var space = ctx.Db.DistinctValues(pq, scope, Limits.ValueSpaceSample, type).Rows.Select(v => v.Value).ToList();

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
            var cov = space.Count > 0 ? ctx.Db.ValueCoverage(pq, scope, 3, type) : default;
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
            // 本次查询**自己施加的过滤**是算得出来的成因,而抽象基类只是个猜测。算得出来的
            // 排在最前,并让猜测退场:两句并排摆着,读的人会挑更具体的那句,然后去查一批
            // 根本不存在的子类。
            //
            // scope 只在第一行被回显过,而回显不是成因 —— 「我圈了这几个 mod」与
            // 「零是这个圈造成的」差着一次重查,而这次重查是白拿的:同一条 SQL,scope 换成 all。
            // 取 Defs 不取 Total:这句话的谓语是「有 N 个 def 把这个字段设成了它」,
            // 而 Total 数的是(def, 路径)行。
            var hiddenByScope = scope.IsAll
                ? 0
                : ctx.Db.FindByField(pq, value, exact,
                                     Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config),
                                     0, 0, type).Defs;
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
                          : $" 'rimsearcher values {path}' lists the whole value domain.")
                    // 曾经这里只写「X 大概是个抽象基类」—— 一句**未经验证的猜测摆在输出
                    // 位置**,读的人会当结论用。`GenStep_ScatterLumpsMineable` 是个被 C# 直接
                    // new 出来的**具体类**,而那句话把人推去查一批不存在的子类,第九轮盲测 S1
                    // 正是这么走完全程的。
                    //
                    // 之后写成「这个零有两种成因并列」,2026-09-18 再压成两条出路(Docs/25 §18):
                    // 每条出路的标签就是它列出什么(派生类 / 使用者),两种落点靠标签分开,
                    // 不再把成因当情景写在前面。判据从严(ClassNameShape 把 `True`、`.ogg`、`1.5` 挡在外面)。
                    : $" 'rimsearcher values {path}' lists them." +
                      (ClassNameShape.Looks(value) && hiddenByScope == 0
                          ? $" 'rimsearcher code-search \"class \\w+ : {ClassNameShape.Tail(value)}\\b\"' lists " +
                            $"the classes deriving from '{ClassNameShape.Tail(value)}'; " +
                            $"'rimsearcher code-search \"{ClassNameShape.Tail(value)}\"' lists who constructs it."
                          : "")));

            // 成因是一行 empty_because,排在落空句后面;此前是「--scope X is what emptied this:
            // N defs … Drop --scope」(Docs/25 乙1)。
            if (hiddenByScope > 0)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("scope"), hiddenByScope, ctx.Without("scope")));

            // 边界排在建议**之后**:它限定的是上面那整段,而不是其中某一条。
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
        /// <summary>
        /// 点分路径的末段其实是一个**取值**:<c>skillRequirements.Crafting</c> 里
        /// <c>Crafting</c> 不是 <c>skillRequirements</c> 下的一个键,它是
        /// <c>skillRequirements[].skill</c> 取到的值 —— 字典式的问法套在列表上。
        ///
        /// 这一档得与「末段哪儿都不是」分开。两者都是「没有这条路径」,而前者的答案就在
        /// 同一个库里、连命令带参数都填得满;合成一句的话,能答的那次和真答不出的那次
        /// 印出来一模一样。
        ///
        /// 只在没给值时算:给了值的人把末段当字段用,而那时推荐命令里的 --value 该填哪个
        /// 是两可的 —— 两可的指路比不指路贵。
        /// </summary>
        static string? TailIsValue(CommandContext ctx, string path, Snapshot.ScopeFilter scope, string? type)
        {
            var cut = path.LastIndexOf('.');
            if (cut <= 0 || cut == path.Length - 1) return null;
            var head = path[..cut];
            var tail = path[(cut + 1)..];

            // Identifier 而不是子串:这里问的是「末段**就是**那个值」。子串会让
            // `foo.Bullet` 认领 `Bullet_Revolver`,而那是另一个答案。也不用 Exact ——
            // 类名在索引里存的是限定形态(`RimWorld.CompShield`),而人敲的是末段。
            var rows = ctx.Db.PathsWithValue(tail, scope, 64, Storage.ValueMatch.Identifier, 0, type).Rows;
            if (rows.Count == 0) return null;

            // 前半截仍是**归属判据**,不是装饰:`statBases.MarketValue` 问的是 statBases
            // 底下那个,而 MarketValue 同时坐在别的路径上。对得上的排前面,一条都对不上
            // 时照样报 —— 那时说的是「它是个值,只是不在你说的那一层」。
            var shapes = rows.Select(r => Search.PathSegments.Shape(r.Path))
                             .Distinct(StringComparer.Ordinal)
                             .OrderByDescending(s => Search.PathSegments.IsWholeSegment(s, head))
                             .ToList();
            var under = shapes.Where(s => Search.PathSegments.IsWholeSegment(s, head)).ToList();
            var pick = under.Count > 0 ? under : shapes;
            var shown = pick.Take(Limits.MaxSuggestions).ToList();

            return $"'{tail}' is a value in this snapshot, not a field under '{head}': it sits on " +
                   string.Join(" / ", shown) +
                   (pick.Count > shown.Count
                       ? $", plus {Tally.Complete(pick.Count - shown.Count).Render("path shape")} more"
                       : "") +
                   (under.Count > 0
                       ? ""
                       // 一条都不在那一层底下是**另一个结论**,不能沉默地混进上一句 ——
                       // 读的人问的是「statBases 底下的 MarketValue」,而答案是
                       // 「它是个值,但不在 statBases 底下」。
                       : $" — none of those is under '{head}'") +
                   $". The query that asks it is 'rimsearcher where {shown[0]} --value {tail}'.";
        }

        // 名字落在别处时给出 found_as 的行;落不到就 null,调用方接着按形状猜。
        static NameLookup.Sighting[]? Placed(CommandContext ctx, string name, Snapshot.ScopeFilter scope)
        {
            var named = ctx.Db.GetDefsNamed(name);
            if (named.Count == 0)
                return NameLookup.Locate(ctx, name, scope) is { } sighting ? [sighting] : null;

            // 指向 --value 之前先探一次,判据与那条命令自己的默认完全一致(子串)——
            // 一个字段都没指向它时那句话是死路,而「没有谁按名字引用它」本身就是个答案。
            // defName 那条路径不算数:它装的是这个 def 自己的名字,不是谁指向它。
            var referenced = ctx.Db.PathsWithValue(name, scope, Limits.MaxSuggestions).Rows
                                .Any(r => !string.Equals(r.Path, "defName", StringComparison.Ordinal));

            // 是个 def 名:一行 is = def;有字段指着它时再一行 is = field value(那才是敲
            // `where <defName>` 的人多半在问的事)。此前是一句「is a def name, not a field path …」。
            var asDef = NameLookup.AsDef(name, named);
            return referenced
                ? [asDef, new NameLookup.Sighting(NameLookup.Where.FieldValue, name, "field value",
                                                  "fields that point at it", $"{CommandRegistry.ExeName} where --value {name}")]
                : [asDef];
        }

        // 这里不像 get 那样把默认值行滤掉:调用方点名了一个字段与一个值,「哪些 def 取到过它」
        // 的答案里就该有它们。但**为什么取到**要分得开 —— comps[N].compClass 一整批
        // 等于 CompShield,多半是 CompProperties_Shield 的声明里写死的,不是谁在 XML 里挑的。
        // 代码造出来的 def 混在结果里时,那件事必须落在**行上**,不能只落在声明里 ——
        // 这份结果最常见的下游是「--json 灌进脚本批量生成补丁」,而脚本不读 notes。
        //
        // 句子数整个结果集(与上面两句同口径),列跟着这一页 —— 于是首页一个 ImpliedDef
        // 都没碰上时,句子照样出声,而那句会自己说清楚「不都在这一页上」。
        var generated = ctx.Db.FindGeneratedDefs(pq, value, exact, scope, type);
        Advisory.NoteGeneratedDefs(ctx, generated, defs,
            rows.Count(r => r.Def.Generated));

        // 表上方 —— 理由同 list 那处。数 def 而不是 matches:这条命令一行是一个
        // (def, 路径)对,而「被排除的那半边还有多少」问的是有多少个 def 落在外面。
        // 排在这一组的最前面:另外三句说的是「你手上这张表不是全集」,而这一句说的是
        // 「这张表里的行未必是你问的那个值」—— 后者改变的是眼前这些行怎么读,得先于
        // 「外面还有什么」。
        Advisory.NoteSubstringWidened(ctx, pq, value, exact, scope, defs, type);

        // 紧跟其后,理由同上一条:它改变的也是**眼前这些行怎么读**(mod 列答的是哪个问题),
        // 而下面几句说的是「这张表不是全集」。两类之间的界不能被穿插。
        Advisory.NoteValueAuthorship(ctx, pq, value, exact, scope, type);

        ctx.AnnounceExcluded(scope, rest => ctx.Db.FindByField(pq, value, exact, rest, 0, 0, type).Defs, "def");

        // 同样在表上方,且排在 scope 补集那句之后:两句都在说「你手上这张表不是全集」,
        // 而 scope 那条是调用方自己划的界,这条是他没意识到自己划了的界。
        Advisory.NoteValueElsewhere(ctx, pq, value, exact, scope, defs);

        // 跨形状那句紧贴着它:两条是同一件事的两个维度 —— 路径维度的外延(你这一堆行
        // 其实不是同一个字段)与值维度的外延(同一个值还坐在别的字段上),本来就该挨着读。
        // 它此前是 footnote,于是**同类的两条外延警告分居数据两侧**,而按位置纪律
        // 下方那条正是会被 head/sed 截掉的那条。
        Advisory.NoteMixedPathShapes(ctx, ctx.Db.FindPathShapes(pq, value, exact, scope, type));

        // 说明区之后、表之前 —— 它自成一块,插在说明中间会把连着的那几句劈成两段,
        // 而那几句靠「连着」才读得出是 N 件互不相干的事。
        // 跟着 --type 一起收:表已经滤成一个类型了,这块不能还在说别的类型。
        // 范围措辞与 values 那处同源 —— 两条命令圈的是同一批。
        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(pq, scope, type),
            type is { Length: > 0 }
                ? $"all of {type}"
                : "every def type that uses this path at all");

        ctx.Report.Table("matches",
            generated > 0
                ? ["def_name", "def_type", "path", "value", FieldDefault.Column, WrittenIn.Column, "declared_in"]
                : new[] { "def_name", "def_type", "path", "value", FieldDefault.Column, "declared_in" },
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.Def.DefName,
                ["def_type"] = r.Def.DefType,
                ["path"] = r.Path,
                ["value"] = r.Value,
                [FieldDefault.Column] = FieldDefault.Render(r.Default),
                [WrittenIn.Column] = generated > 0 ? WrittenIn.Render(r.Def.Generated) : null,
                ["declared_in"] = r.Def.SourceMod,
            }).ToList());

        Advisory.NoteAuthoredSiblings(ctx, rows.Where(r => r.Default != Contract.DefaultState.Same)
                                                .Select(r => (r.Def.DefName, r.Def.Id, r.Path)));
        return 0;
    }

    // --type 指的类型不在这份快照里 —— 那时整条查询必然空手,而空手与「查过了、没有」
    // 逐字同形。两条问法(点名字段 / 只给值)都会撞上它,所以判据只此一处。
    private static string? UnknownType(CommandContext ctx, string? type) =>
        type is { Length: > 0 } &&
        !ctx.Db.Types(ctx.Unscoped()).Any(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))
            ? $"'{type}' is not a def type in this snapshot. --type {type} therefore selects nothing at " +
              "all, whatever the rest of the query asks. 'rimsearcher list' names every def type this " +
              "snapshot does have."
            : null;

    /// <summary>--value:不指名字段,直接问「哪个字段装着这段文本」。</summary>
    private static int ByValue(CommandContext ctx, string value, Snapshot.ScopeFilter scope, LimitValue limit,
                               bool exact, int offset, string? type = null,
                               IReadOnlyList<string>? pathContains = null)
    {
        var (rows, total, exactTotal, defsTotal) = ctx.Db.PathsWithValue(value, scope, limit.Effective,
            exact ? ValueMatch.Exact : ValueMatch.Substring, offset, type, pathContains);

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
            // 类型名写错时,下面那句「这份快照里没有哪个字段装着这个值」是假话 ——
            // 它成立与否根本没被问过。
            if (UnknownType(ctx, type) is { } unknown)
            {
                ctx.Report.Notice(NoticeKind.Filter, unknown);
                return 1;
            }

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
                    exact ? ValueMatch.Exact : ValueMatch.Substring, defType: type).Total, "field path")
                is { } line)
                ctx.Report.Notice(NoticeKind.NextStep, line);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total);

        // 一共牵动多少个 def。逐行相加得不到这个数 —— 同一个 def 常在好几条路径上都持有
        // 这个值(`stuffProps.categories[0]` 与 `[1]`),而读者要的正是去重后的那个总数,
        // 此前只能估(真实会话里:「可能存在重叠,所以总共约 6 个」)。
        ctx.Report.Notice(NoticeKind.Count,
            $"{Tally.Complete(defsTotal).Render("def")} hold it altogether, counting each def once.",
            data: new Dictionary<string, object?> { ["defs"] = defsTotal });

        // 子串命中不留痕,与 `--path-contains` 是同一条纪律的值侧:`where --value Bullet` 命中每一个
        // `Bullet_*`,而问的人多半只想要「值就是 Bullet 的那些」。
        //
        // 「哪些行会被 --exact 砍掉」不在这里说 —— 那是**每一行**的属性,它坐在
        // defs_exact / defs_other 两列里(见下面建表处)。这句只报路径侧的两个数与那个动作。
        if (!exact && exactTotal < total)
            ctx.Report.Notice(NoticeKind.Filter,
                exactTotal == 0
                    ? $"No value here is exactly '{value}'; each match has it inside a longer value — see " +
                      "example_value. --exact would return nothing."
                    : $"{Tally.Complete(exactTotal).Render("field path")} hold it exactly; --exact keeps those.",
                data: new Dictionary<string, object?> { ["paths_exact"] = exactTotal });

        // 表上方 —— 理由同 list 那处。名词用 field path 而不是 path:NounRegistry 只认
        // 前者,而这张表一行就是一条字段路径。
        ctx.AnnounceExcluded(scope, rest => ctx.Db.PathsWithValue(
            value, rest, 0, exact ? ValueMatch.Exact : ValueMatch.Substring, defType: type).Total, "field path");

        // 说明区之后、表之前:它自成一块,插在说明中间会把连着的那几句劈成两段。
        //
        // 按值一次问清,不按结果里的每条路径各查一次再求和:求和会把同一个被砍的 def 按它
        // 出现在几条路径上重复计数,而路径 defName(`where --value` 命中 def 名时必然有)的
        // 「同类型」等于全体 def 类型,单这一项就等于全库 —— 于是子集计数会大于全集。
        // 表里那批 def 是「取到过这个值」选出来的,这一块圈的也必须是同一批。
        //
        // 跟着 --type 一起收:表已经滤成一个类型了,这块不能还在说别的类型 —— 读者对
        // 「另一个类型有 def 被砍过」无事可做,它答的是另一个问题。
        Completeness.NoteIndexedPathsOnly(ctx,
            ctx.Db.TruncatedDefsSharingValue(value, exact ? ValueMatch.Exact : ValueMatch.Substring,
                                             scope, type),
            type is { Length: > 0 }
                ? $"all of {type}"
                : "every def type that holds this value anywhere");

        // 子串态下 defs 拆成两列,各自答一个问题:「值就是它的 def 有几个」与「值只是含着
        // 它的有几个」。合成一列的时候这两个数没有任何东西能把它们分开 —— 一行里两态并存
        // 在真快照上占 28%(194 个真实查询值里 55 个),而那一列印的是两者之和,读者逐行
        // 引用它(「出现在 10 个建筑的造价里」)时拿到的是被污染的数。
        //
        // --exact 那条路只有一个口径,列名回到裸 defs:一列名字答的是它自己数的那批,
        // 这时没有第二批。
        ctx.Report.Table("paths",
            exact
                ? ["path", "def_type", "defs", "example_value"]
                : ["path", "def_type", "defs_exact", "defs_other", "example_value"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)(exact
                ? new Dictionary<string, object?>
                {
                    ["path"] = r.Path,
                    ["def_type"] = r.DefType,
                    ["defs"] = r.Defs,
                    ["example_value"] = r.Sample,
                }
                : new Dictionary<string, object?>
                {
                    ["path"] = r.Path,
                    ["def_type"] = r.DefType,
                    ["defs_exact"] = r.DefsExact,
                    ["defs_other"] = r.Defs - r.DefsExact,
                    ["example_value"] = r.Sample,
                })).ToList());
        return 0;
    }
}

public sealed class ListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "list",
        // "types" 归 C# 侧那条命令。这里让出来一次真实用法都没牺牲:995 份会话里敲过的
        // 3786 次 rimsearcher 中,`list` 119 次、`types` 1 次 —— 而那个词在 C# 侧指的是
        // 类型本身,两义并存会让 `types ThingComp` 体面地回答另一个问题。
        Aliases = ["ls", "def-types"],
        Summary = "List every def of one type — or, with no type given, every def type in the snapshot.\n\n" + NameLookup.Help,
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defType",
                Required = false,
                Variadic = true,
                Help = "A def type such as ThingDef. Several go in one call; --limit and --offset apply to " +
                       "each on its own, each gets its own count line, and the def_type column says which " +
                       "type a row came from. Leave them all out and this lists the def types themselves, " +
                       "with how many defs each holds — all of them, unless you pass --limit. " +
                       "--class and --offset need a def type and are refused without one.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Scope,
            CommonOptions.Offset("defs"),
            new OptionSpec
            {
                Name = "class",
                // 主名按产出定,别名按识别定,两轮量的不是同一件事:
                // 2026-08-04 的 n=10 识别测里 own-class 10/10 且零靶心误读、def-class 9/10、
                // root-class 1/10(9 份读成 is-a),于是主名改成 own-class、class 降为别名。
                // 改名之后的六周,消费方敲 --class 76 次、--own-class 8 次 —— help 里第一眼
                // 看到的名字十次里九次不是人敲的那个。2026-09-18 换回来:class 当主名,
                // own-class 留作别名;「谁的类」那半句由 Help 第一句承担,不再压在名字上。
                Aliases = ["own-class", "def-class", "runtime-class"],
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
            "rimsearcher list CreepJoinerBaseDef --class CreepJoinerAggressiveDef",
            "rimsearcher list ThingDef --scope all,-vanilla",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "defs",
                What = "with a def type: one row per def — def_name, label, declared_in, def_type (which of the " +
                       "types asked for the row came from, present on a single-type call too), plus 'class' when " +
                       "one of the buckets holds more than one def class. A def another mod patched still reads " +
                       "as its original mod in declared_in, and --scope filters that same column.",
            },
            new()
            {
                Key = "types",
                What = "without one: one row per def type — def_type, defs. Which of the two keys is " +
                       "present follows the def type, so a caller that passed one never has to guess.",
            },
            EmptyCause.JsonKeyCounting("row"),
            NameLookup.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        // 分模式的判据是**给没给 def 类型**,而不是一个开关参数 —— 落空分流里那句
        // 「没有这个 def 类型」于是指得回同一条命令。
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ctx.Args.Positionals)
            if (seen.Add(t)) asked.Add(t);

        if (asked.Count == 0) return RunTypes(ctx);

        // 认领要赶在查询**之前**:下面每一条零行分流都是提前 return。`defs` 与 `types`
        // 互斥,不能靠声明层的 Rows 统一认领(空数组在机器侧读作「这一路也查过了」),
        // 只能由两支各自认领 —— 另一支在 RunTypes 里。几个类型只认领一次。
        ctx.Report.Promises("defs");

        var table = new List<IReadOnlyDictionary<string, object?>>();
        // class 一列的有无本来就跟着数据走(桶里只有一个 class 就不印)。几个类型一次问时
        // 取并集:一个类型异构就印,同质那几个的行照填真值,不留空。
        var showClass = false;
        var listed = 0;
        foreach (var type in asked)
            if (RunDefs(ctx, type, table, ref showClass) == 0) listed++;

        // 一个类型都没列出来才算失败 —— 与 read / fields / values / keyed 同一条。
        if (listed == 0) return 1;

        ctx.Report.Table("defs",
            showClass
                ? ["def_name", "class", "label", "declared_in", "def_type"]
                : ["def_name", "label", "declared_in", "def_type"],
            table);
        return 0;
    }

    /// <summary>
    /// 不给 def 类型的那一半:列出这份快照有哪些 def 类型。
    ///
    /// <c>--class</c> 与 <c>--offset</c> 只对另一半有意义,却仍然声明在这条命令上 ——
    /// 照单收下、悄悄不生效是个沉默口子(与 <see cref="CommandContext.Limit"/> 记的
    /// <c>--limit</c> 被静默夹紧同形),所以当场退 2 并把该走的那条路说出来。
    /// </summary>
    private static int RunTypes(CommandContext ctx)
    {
        // 指的那条路只在名字**真是** def class 时才落到桶上(`list CompShield` 回的是
        // 「No def type named 'CompShield'」),所以两种落点都说出来。
        if (ctx.Args.Value("class") is { } cls)
            throw new CliUsageException(
                $"--class needs a def type to narrow inside. If you do not know which def type holds " +
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

        // --find 在这一半筛的是**类型名**。这条路不像 --class 那样退 2:它在这里
        // 真的生效,而「哪些 def 类型名字里带 Gen」是个答得出来的问题。
        var typeFind = ctx.Args.Value("find");
        var all = typeFind is null
            ? everything
            : everything.Where(t => t.Type.Contains(typeFind, StringComparison.OrdinalIgnoreCase)).ToList();

        // 筛空与快照空是两回事,分母就在手边。
        if (all.Count == 0 && everything.Count > 0)
        {
            // 一行 empty_because(Docs/25 乙1);此前是「No def type … has 'x' in its name, out of N.
            // Drop --find …; to filter the defs inside one type instead, name the type」。
            ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("find"), everything.Count, ctx.Without("find")), "def type");
            return 1;
        }

        // 零行一律 exit 1,否则按退出码分流的脚本会把「0 def types.」读成「查到了」。
        // 句子也不许把 scope 造成的空说成快照的空 —— 整份快照的数就在手边。
        if (all.Count == 0)
        {
            if (scope.IsAll)
                // 整份库空这一态没有夹具,所以 absent 那张表在 list 上没有探针可守;这一句留作散文。
                ctx.Report.Notice(NoticeKind.NextStep,
                    "This snapshot holds no defs at all. 'rimsearcher snapshot list' shows when it was taken, " +
                    "and 'rimsearcher export' rebuilds it.");
            else
                // 此前是「No def in this snapshot comes from --scope X. Snapshot-wide the figure is N def types.
                // 'rimsearcher mods' lists what this snapshot actually has」。
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("scope"),
                    ctx.Db.Types(ctx.Unscoped()).Count, ctx.Without("scope")), "def type");
            return 1;
        }

        // 缺省全给:问的是「一共有哪些」,截一刀就答不完整。
        var limit = ctx.Limit();
        var rows = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        // 筛过就把分母也说出来:一个不带出处的「12 def types」读起来是整份快照的全部。
        if (typeFind is not null)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Filtered by --find '{typeFind}'; this snapshot holds " +
                $"{Tally.Complete(everything.Count).Render("def type")} in all.");

        ctx.Report.CountNotice(Tally.Of(rows.Count, all.Count), "def type");

        // 表上方 —— 理由同 RunDefs 那处。--find 也要跟着过去,否则补集那一半按更宽的
        // 口径数,两个数不可比。
        ctx.AnnounceExcluded(scope, rest =>
        {
            var there = ctx.Db.Types(rest);
            return typeFind is null
                ? there.Count
                : there.Count(t => t.Type.Contains(typeFind, StringComparison.OrdinalIgnoreCase));
        }, "def type");

        ctx.Report.Table("types", ["def_type", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_type"] = r.Type,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }

    /// <summary>
    /// 一个 def 类型的那一份。行不自己发,追加进 <paramref name="table"/> —— 几个类型共用一张表。
    /// </summary>
    private static int RunDefs(CommandContext ctx, string type,
                               List<IReadOnlyDictionary<string, object?>> table, ref bool showClass)
    {
        var limit = ctx.Limit();
        var offset = ctx.Args.Offset();
        var wantClass = ctx.Args.Value("class");
        var find = ctx.Args.Value("find");
        var scope = ctx.Scope();

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
                    // 此前是「No def of type T is in scope 'X', but this snapshot has N of it overall.
                    // Drop --scope, or run 'rimsearcher mods' …」。
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("scope"), everywhere, ctx.Without("scope")));
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
                    // 此前是「No def of type T has 'x' in its name or label, out of N. The filter reads def
                    // names and labels only — … 'where --value' … 'search'」;筛子读什么住 --find 的 help。
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("find"), unfiltered, ctx.Without("find")));
                    Advisory.NoteTextIndexHasIt(ctx, find, "--find");
                    return 1;
                }
            }

            // 「不是分桶键」不等于「不存在」:游戏只给「祖先链上没有非抽象 Def」的类型建库,
            // 于是 CreepJoinerAggressiveDef 的 def 全躺在 CreepJoinerBaseDef 桶里。
            if (wantClass is null && NameLookup.AsClass(ctx, type, scope) is { } asClass)
            {
                NameLookup.Say(ctx, asClass);
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
                // 「确实没有 def 用这个类」与「--class 问的根本不是这件事」逐字同形。
                // 那类型真正的多态在嵌套字段上(GenStepDef 的 167 个 def 全是 Verse.GenStepDef,
                // 各自跑的 GenStep 子类写在 genStep 那一个字段的 Class= 里),所以转向要指到
                // 那条路上去,并按快照量到哪一步说话。
                if (present.Count == 1)
                {
                    // 「类恒定的桶把多态放在嵌套字段上」这条通则是 --class 自己的 help 文本
                    // (OptionSpec 里那段,cli-reference 的 --class 行就是它渲染出来的),
                    // 不在这里再讲一遍;这句只留本次的事实与填好参数的转向。
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"Every one of the {Tally.Complete(present[0].Count).Render("def")} of type {type} has the " +
                        $"same class, {present[0].Class}, so --class cannot tell them apart: " +
                        $"'rimsearcher where Class {wantClass}'.");
                    // 与下面「类型里有几种 class、没有你要的那种」那支同一条纪律:--class 单独拿掉能回来多少。
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("class"), present[0].Count, ctx.Without("class")));
                    return 1;
                }

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def of type {type} has class '{wantClass}'. That type holds " +
                    NameList.Render([.. present.Select(c => $"{c.Class} ({c.Count})")], Limits.MaxSuggestions) + ".");
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("class"), present.Sum(c => c.Count), ctx.Without("class")));
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
                $"Filtered by --find '{find}'; {type} holds {Tally.Complete(unfiltered).Render("def")} " +
                (wantClass is null ? "in all." : $"with class '{wantClass}' in all."));
        }

        // 分页必须给总数,否则不知道翻到哪算到头。限定语说破这一句数的是哪个类型 ——
        // 几个类型一次问时,两句「25 of 79 defs」分不出谁是谁。
        ctx.Report.PageNotice("def", rows.Count, offset, total, $" of type {type}");

        // 排在表**上方**,与「计数在它数的那张表上方」同一条纪律:这句说的是
        // 「这张表全不全」,读到行的时候得已经知道。位置对这一条格外要紧 ——
        // 它的受众定义上就是拿到一张长表的人,而那种人最可能 head/sed 截一段就走,
        // 末尾的脚注正好被切掉(本项目实测踩过:`sed -n '9,20p'` 两头一起切,
        // 把确实存在的 comp 读成了空)。
        ctx.AnnounceExcluded(scope, rest => ctx.Db.ListByType(type, rest, 0, 0, wantClass, find).Total,
                             "def", $" of type {type}");

        // 桶里只有一种 class 时不平白多一列(ThingDef 一万多个 def 都是 Verse.ThingDef);
        // 异构时这一列是唯一能把子类型区分开的东西。
        var classes = ctx.Db.ClassesInType(type, scope);
        var heterogeneous = wantClass is null && classes.Count > 1;
        showClass |= heterogeneous;
        if (heterogeneous)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 数的是 class,名词就得写「def class」—— 这一句正长在
                // 「def 类型不等于运行时 class」那条区分上。
                $"Type {type} holds {Tally.Complete(classes.Count).Render("def class")}: " +
                NameList.Render([.. classes.Select(c => $"{Tail(c.Class)} ({c.Count})")], Limits.MaxSuggestions) +
                ". Pass --class to pick one.");

        // 同质时不印 class 一列(见上),但**值照填** —— 不印是排版,而 JSON 里的 null 在这套
        // 输出里恒读作「查不出来」。同质恰恰是查得最清楚的那种,而 `--class` 点了名的
        // 那次更刺眼:用户敲的就是这个 class,回答里它却是 null。
        // 数据里真没有 class 时才是 null,而那时它是实话。
        //
        // def_type 排在末列,不排首列:文本渲染器折不掉第 0 列,而单类型调用里这一列
        // 每行都一样 —— 摆在末列它自己折进表头。
        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["class"] = r.Class is { } c ? Tail(c) : null,
                ["label"] = r.Label,
                ["declared_in"] = r.SourceMod,
                ["def_type"] = type,
            });

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
            "What is listed is every path the exporter recorded a value for. When the snapshot has the type's " +
            "declared field set, a miss that is in that set means the field exists and is null on every def; a " +
            "miss that is not means the type has no such field. That set is walked to a bounded nesting depth, " +
            "and the answer says how deep it reached: a field nested past that is outside what it measured. A " +
            "snapshot without that set says so. The shape of a nested object is in its class: 'code-search' and " +
            "'read'.\n\n" +
            IndexGap.Help,
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defType",
                Variadic = true,
                Help = "A def type such as ThingDef. Several types go in one call; --limit and --offset apply " +
                       "to each one on its own, each gets its own count line, and the def_type column says " +
                       "which type a row came from.",
            },
        ],
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
                // "only" 不在这里:sources sync 有一个真的 --only(只同步这几棵树)。
                // 删掉的四个(field-contains / path-filter / contains / match)在 get /
                // inherit / fields 上逐字各 0 次,理由记在 get 那一处。
                //
                // "path" 是 2026-09-09 补的,而它此前不在这里**不是**一个决定 ——
                // get / inherit 收了它,fields 漏了,于是三条同族命令里这一条独自
                // 扛下全部误敲:实测 15 次 / 8 份会话,逐条读过,15 条的意图无一例外
                // 就是这个选项。主名把「路径」和「怎么比」压进一个词,读者只说前半截。
                Aliases = ["filter", "grep", "path"],
                Placeholder = "<text>",
                Help = "Only list paths containing this text. Repeat it to widen the selection. " +
                       CommonOptions.AnyIndexNote,
                Narrows = true,
            },
            CommonOptions.ExactPathFilter(),
            CommonOptions.Offset("field paths"),
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path-contains comps",
            "rimsearcher fields HediffDef",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "fields",
                Rows = true,
                What = "one row per field path: path, defs (how many defs use it), def_type (which type the " +
                       "row was counted under — present on a single-type call too).",
            },
            Completeness.JsonKey,
            IndexGap.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var (filters, exactPath) = ctx.Args.PathFilters();
        var pathSpelling = exactPath ? "--exact-path" : "--path-contains";
        var offset = ctx.Args.Offset();

        // 同一个类型给两遍只查一遍:行会重,而那两句计数会一字不差地说两次 —— 读起来
        // 像两个类型碰巧同样大。
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ctx.Args.Positionals)
            if (seen.Add(t)) asked.Add(t);

        var table = new List<IReadOnlyDictionary<string, object?>>();
        var listed = new List<string>();
        foreach (var type in asked)
            if (RunOne(ctx, type, limit, filters, offset, exactPath, table) == 0) listed.Add(type);

        // 部分命中仍是结果,全空才 1。
        if (listed.Count == 0) return 1;

        // 截断声明合成一块,不按类型各发一块 —— `completeness` 是个具名块,发两次会撞键。
        // 圈的范围跟着实际列出来的那几个类型走。
        var cut = listed.Select(ctx.Db.TruncatedDefsOfType).ToList();
        var byType = cut.SelectMany(s => s.ByType).ToList();
        if (byType.Count > 0)
            Completeness.NoteIndexedPathsOnly(ctx, new TruncationScope(cut.Sum(s => s.Count), byType),
                $"all of {NameList.Render(listed, listed.Count)}, whatever {pathSpelling} says");

        ctx.Report.Table("fields", ["path", "defs", "def_type"], table);
        return 0;
    }

    /// <summary>
    /// 一个类型的那一份。行追加进 <paramref name="table"/> —— 分开发就成了几张
    /// 同名表,而键名撞车会静默覆盖。
    /// </summary>
    private static int RunOne(CommandContext ctx, string type, LimitValue limit,
                              IReadOnlyList<string> filters, int offset, bool exactPath,
                              List<IReadOnlyDictionary<string, object?>> table)
    {
        var (rows, total, whole) = ctx.Db.FieldPathsForType(type, limit.Effective, filters, offset, exactPath);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("field path")} match in all.");
                return 1;
            }

            if (filters.Count > 0 && ctx.Db.FieldPathsForType(type, 1).Rows.Count > 0)
            {
                // --exact-path 自己把结果筛空,与「这个类型没有这个字段」是两件事。
                // 排在最前:这条成因一旦成立,后面三条都不适用 —— 而它们的句子模板全都
                // 写着 contains,在整条相等的语义下那是**假话**:`fields ThingDef
                // --exact-path MarketValue` 落空,而 race.meatMarketValue 含着它。
                //
                // 口径与 where 那侧的同一条诊断一致(先问自己是不是成因,再列形状),
                // 只是那边按后缀问、这边按子串问 —— 两条命令的 --exact-path 各自
                // 放宽回它自己的默认谓词。
                if (exactPath)
                {
                    var relaxed = ctx.Db.FieldPathsForType(type, Limits.MaxSuggestions, filters);
                    if (relaxed.Rows.Count > 0)
                    {
                        var shown = relaxed.Rows.Select(r => r.Path).ToList();
                        // 动词不进随数量变形的位置:分词短语当标签,冒号后是名单 ——
                        // 与 where 那侧「Matched as a suffix instead:」同一个形态,
                        // 谓词换成这条命令自己的默认(子串)。
                        ctx.Report.Notice(NoticeKind.NextStep,
                            $"No field path on '{type}' is exactly {PathFilterText.Say(filters)}. " +
                            "Matched as a substring instead: " +
                            string.Join(", ", shown) +
                            (relaxed.Total > shown.Count
                                ? $", plus {Tally.Complete(relaxed.Total - shown.Count).Render("field path")} not shown"
                                : "") +
                            ". Any one of those goes straight back into --exact-path, or drop the flag to " +
                            "see them all.");
                        return 1;
                    }
                }

                var declared = ctx.Db.TypeDeclaredPaths(type, filters, exactPath);
                if (declared.Count > 0)
                {
                    // 事实句 + index_gap 一行;「没进值索引」「类型有、def 没有」是同一件事的反面与重述。
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"'{type}' declares {Tally.Complete(declared.Count).Render("field path")} matching " +
                        $"{PathFilterText.Say(filters)}; every def of the type has them as null.");
                    IndexGap.Say(ctx, string.Join(", ", filters), IndexGap.NullOnType,
                                 Completeness.DeclarationSearch(filters[0]));
                    return 1;
                }
                // 打进来的文本是这个类型的一个**取值**时,答案就在同一个库里 —— 先算,
                // 算出来了就不必再走下面那两条「路径面为什么可能漏」的解释。
                var (valueAxisChecked, asValue) = FilterIsValue(ctx, type, filters, exactPath);

                // 问过而没命中,就把这个否定也说出来 —— 沉默会让它与「压根没问值面」同形。
                // 只在问过时出声:filters 不止一条那次确实没问,那时说什么都是假的。
                var notAValue = valueAxisChecked && asValue is null
                    ? $" No def of '{type}' holds {PathFilterText.Say(filters)} as a value either."
                    : "";

                {
                    // 量程跟着这句否定一起说。声明集是按递归深度收的,而类型图有环 ——
                    // 「展开完」不存在(Docs/22 第 12.2 节:471 个类型的强连通分量)。
                    // 不说深度的话,一个嵌套过深的字段与一个根本不存在的字段印出来同形。
                    //
                    // 量程那半句在 asValue 出声时撤掉:它解释的是「路径面可能没测到那么深」,
                    // 而这次落空的成因已经查明是**轴选错了**。两句并排会让读者以为还得
                    // 再往深里找一次。
                    var reach = ctx.Db.TypeDeclaredPathMaxSegments(type);
                    ctx.Report.Notice(NoticeKind.Boundary,
                        // 「declared-path list」是个要外部定义才读得懂的名词 —— 它的定义
                        // 只在 SKILL 里。换成自陈的关系从句:与前半句「有值的那些路径」
                        // 正好成对照,也与代码侧 members / read 的 declares 同一个对立面
                        // (类型自己声明的 vs 实际到手的),同一个词在两侧不再需要各学一遍。
                        // 量程是数,留在句里;「嵌套过深就在量程外」是机制,住 Remarks。
                        $"'{type}' has field paths, but none contains {PathFilterText.Say(filters)}, and " +
                        "none of the fields the type itself declares has it either" +
                        (asValue is null && reach is { } n ? $" (that declared set reaches {n} segments deep)." : ".") +
                        notAValue);
                    if (asValue is not null) SayValueNotPath(ctx, type, filters[0], asValue);
                    else IndexGap.Say(ctx, string.Join(", ", filters), IndexGap.Undeclared,
                                      Completeness.DeclarationSearch(filters[0]));
                    return 1;
                }
            }
            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(ctx.Scope()).Select(t => t.Type), "fields"));
            // 叠加不替换:上面那句说的是「def 类型里没有它」,这条说的是「它在别处,而且
            // 那儿正好答得出你问的这件事」。两句都真,少了后一句人就留在 def 那一层了。
            if (DefTypeMiss.InSourceInstead(ctx, type) is { } inSource)
                ctx.Report.Notice(NoticeKind.NextStep, inSource);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total, $" on {type}");

        // 与 `get --path-contains` 同一条纪律,而且是同一句话(产地 PathFilterSummary):
        // 子串匹配不留痕,这里的代价更大 —— 这条命令是「这个类型有没有这个字段」的正式
        // 问法,而「一条都不是整段」正是「没有」的形状。
        //
        // 分母另查一次:上面那个 total 已经被 --path-contains 滤过,而分页那句
        // 「N field paths on {type}」与不加过滤时逐字同形 —— 不把分母说进来,读者
        // 拿不到「滤掉了多少」。
        if (filters.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                PathFilterSummary.Say(filters, $"on {type}", "field path",
                                      total, ctx.Db.FieldPathsForType(type, 1).Total, whole));

        // 截断声明不在这儿发:它圈的是整个 def 类型(与 --path-contains 无关 —— 表已经按
        // --path-contains 滤过,而被砍掉的字段本来就不在表里,按 --path-contains 收窄这个数
        // 就是拿看得见的东西去限定看不见的东西),几个类型合成一块由 Run 发。

        // def_type 排在末列,不排首列:文本渲染器折不掉第 0 列,而单类型调用里这一列
        // 每行都一样 —— 摆在末列它自己折进表头,摆在首列就是一整条冗余。
        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["defs"] = r.Count,
                ["def_type"] = type,
            });
        return 0;
    }

    /// <summary>
    /// 打进 <c>--path-contains</c> 的文本其实是这个类型的一个**取值**,不是字段名。
    ///
    /// <c>fields GeneDef --path-contains hunger</c> 这类调用问的是「这个类型跟这个词
    /// 有没有关系」,而问的人**不知道**这个词会落在路径位还是值位 —— 那正是他要查的东西。
    /// 路径面查空之后,现有的两句都在解释「路径面为什么可能漏」(声明集有多深 /
    /// 字段可能没进索引),整段假定问的是个字段名,于是把读者按在「没有」上。
    ///
    /// 实测:三次同型调用(<c>hunger</c> / <c>WorkSpeedGlobal</c> / <c>EquipDelay</c>)
    /// 全部当场交卷,一次都没转去值面。而后两个在值面上分别是 10 和 107 个 def。
    /// hunger 那次值面**也是空的**,于是这里一个字都不说 —— 判据当场算得出来,
    /// 算不出就沉默(纪律同 <see cref="DefTypeMiss.InSourceInstead"/>)。
    ///
    /// 限定 <paramref name="type"/> 查,不查全库:问的是「这个类型上」,而
    /// <c>WorkSpeedGlobal</c> 在别的类型上也坐着值,不限定就会把别人的答案报成他的。
    /// </summary>
    /// <returns>
    /// <c>Checked</c>:值面到底问没问过。<c>Hit</c>:问到了该说的那句话,问过但没有就是 null。
    ///
    /// 这两件事必须分开报。只回一个 null 的话,「没问值面」与「问了、确实不是个值」
    /// 印出来同形 —— 而读者问的正是「这个词跟这个类型有没有关系」,值面是答案的一半。
    /// 复查时这条缝就出在相邻的两条输出上:一条大声说值面命中了,另一条对未命中沉默。
    /// </returns>
    private static (bool Checked, string? Hit) FilterIsValue(CommandContext ctx, string type,
                                                             IReadOnlyList<string> filters, bool exactPath)
    {
        // 给了几个筛子时,「哪一个是值」本身要先答一次,而那是两可的指路。
        if (filters.Count != 1) return (false, null);
        var text = filters[0];

        // Identifier 而不是子串:问的是「值**就是**这个词」。子串会让 Beauty 认领
        // BeautyOutdoors,而那是另一个答案 —— 口径同 TailIsValue。
        var rows = ctx.Db.PathsWithValue(text, ctx.Scope(), 64, Storage.ValueMatch.Identifier, 0, type).Rows;
        if (rows.Count == 0) return (true, null);

        var shapes = rows.Select(r => Search.PathSegments.Shape(r.Path))
                         .Distinct(StringComparer.Ordinal)
                         .ToList();
        var shown = shapes.Take(Limits.MaxSuggestions).ToList();
        var defs = rows.Sum(r => r.Defs);

        return (true, $"'{text}' is a value on '{type}', not a field name: it sits at " +
               string.Join(" / ", shown) +
               (shapes.Count > shown.Count
                   ? $", plus {Tally.Complete(shapes.Count - shown.Count).Render("path shape")} more"
                   : "") +
               $" on {Tally.Complete(defs).Render("def")}.");
    }

    /// <summary>
    /// 「那段文本是个值」的事实句 + index_gap 一行。next 里**不填路径**:路径不止一条时,
    /// 填第一条就把答案窄成了它的一支,而那一支与整个答案印出来同形。
    /// </summary>
    private static void SayValueNotPath(CommandContext ctx, string type, string text, string fact)
    {
        ctx.Report.Notice(NoticeKind.NextStep, fact);
        IndexGap.Say(ctx, text, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {text} --type {type}");
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
        Positionals =
        [
            new PositionalSpec
            {
                Name = "fieldPath",
                Variadic = true,
                Option = "field",
                Help = "A field path or its last segment, such as compClass. " + CommonOptions.AnyIndexNote +
                       " '--field' spells out this same argument. Several paths go in one call; " +
                       "--limit and --offset apply to each one on its own, each gets its own count line and " +
                       "its own entry in the 'field' block, and the field_path column says which one a row " +
                       "came from.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("values"), CommonOptions.Offset("values"), CommonOptions.Scope,
            CommonOptions.Type,
            new OptionSpec
            {
                // 同 where 的 --field:`values ThingDef --field techLevel` 是同一个「类型在前、字段用选项」的形状。
                // 示例用叶子字段:statBases 是容器,索引里没有它自己的取值,照抄示例会得到一张空表。
                Name = "field",
                Placeholder = "<path>",
                Arity = Arity.Multi,
                Help = "A field path. 'rimsearcher values ThingDef --field techLevel' is " +
                       "'rimsearcher values techLevel --type ThingDef'.",
            },
            CommonOptions.ExactPath(),
            CommonOptions.PathContainsBeside(),
        ],
        Examples =
        [
            "rimsearcher values compClass",
            "rimsearcher values expandingIconTexture --type WorldObjectDef",
            "rimsearcher values thingClass --scope vanilla",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "values",
                Rows = true,
                What = "one row per distinct value: value, defs, field_path (which of the paths asked for the " +
                       "row was counted under — present on a single-path call too).",
            },
            new()
            {
                Key = "field",
                What = "an array with one entry per field path asked for, in the order given; each entry " +
                       "holds a 'field' object (field[0].field), the same nesting 'get' uses for defs[]. " +
                       "That object says which path was asked for and which full paths and def types its " +
                       "values came from: asked, matched_paths, def_types, defs_with_field. A bare name " +
                       "matches by suffix, so this says what was actually pooled. Always an array, including " +
                       "when one path was asked for. Always " +
                       "present: on an empty result that object's three members are empty and " +
                       "defs_with_field is 0.",
            },
            EmptyCause.JsonKey,
            Completeness.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");
        var offset = ctx.Args.Offset();

        // 同一条路径给两遍只查一遍:行会重,而那几句计数会一字不差地说两次。
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ctx.Args.Positionals)
            if (seen.Add(p)) asked.Add(p);

        var table = new List<IReadOnlyDictionary<string, object?>>();
        // 实际查出了值的那几条路径,各自贡献了哪些 def 类型。截断声明要圈的是它们。
        var listed = new List<(string Asked, IReadOnlyList<(string DefType, int Count)> DefTypes)>();
        foreach (var one in asked)
            RunOne(ctx, one, limit, scope, type, offset, table, listed);
        // 集合到这里关掉:开着的话下面那两块也会被归进 field。
        ctx.Report.EndItems();

        // 部分命中仍是结果,全空才 1。
        if (listed.Count == 0) return 1;

        // `completeness` 是具名块,每条路径发一块会撞键 —— 只发一块。
        //
        // 数按 **def 类型**取,不按路径取:几条路径各问一次「被砍过、且有这条路径的 def」
        // 再加起来,同一个 def 会被数几遍(那正是 TruncatedDefsSharingValue 头上那段注释
        // 记着的坑:子集计数能大过全集)。而这个数按类型取还更贴切 —— 被砍在这条路径
        // **之前**的 def 根本没留下这条路径,按路径问的那一版恰好看不见它们,
        // 而它们正是这句话要警告的那批。按类型取是个真上界,也正是下面 verify: 那条
        // 命令印出来的数。
        var types = listed.SelectMany(l => l.DefTypes.Select(d => d.DefType))
                          .Distinct(StringComparer.Ordinal).ToList();
        var byType = types.SelectMany(t => ctx.Db.TruncatedDefsOfType(t).ByType)
                          .GroupBy(t => t.Type, StringComparer.Ordinal)
                          .Select(g => (Type: g.Key, Defs: g.Max(t => t.Defs)))
                          .ToList();
        if (byType.Count > 0)
            Completeness.NoteIndexedPathsOnly(ctx, new TruncationScope(byType.Sum(t => t.Defs), byType),
                type is { Length: > 0 }
                    ? $"all of {type}"
                    : "every def type that uses " + (listed.Count > 1 ? "any of these paths" : "this path") +
                      " at all");

        // field_path 排在末列,不排首列:文本渲染器折不掉第 0 列,而单条路径的调用里这一列
        // 每行都一样 —— 摆在末列它自己折进表头,摆在首列就是一整条冗余。
        ctx.Report.Table("values", ["value", "defs", "field_path"], table);
        return 0;
    }

    /// <summary>
    /// 一条路径的那一份。行不自己发,追加进 <paramref name="table"/> —— 几条路径共用一张表。
    /// 查出了值就把自己记进 <paramref name="listed"/>,截断声明由 <see cref="Run"/> 合起来发。
    /// </summary>
    private static void RunOne(CommandContext ctx, string path, LimitValue limit, ScopeFilter scope,
                               string? type, int offset,
                               List<IReadOnlyDictionary<string, object?>> table,
                               List<(string Asked, IReadOnlyList<(string DefType, int Count)> DefTypes)> listed)
    {
        var pq = new PathQuery(path, ctx.Args.Flag("exact-path"), Contains: ctx.Args.Values("path-contains"));
        var (rows, total) = ctx.Db.DistinctValues(pq, scope, limit.Effective, type, offset);

        // 少写下标那一档,判据与 `where` 那处同源(见那边的长注释):索引里存的是
        // `statBases[0].stat`,而 `statBases.stat` 恒空、且空得与「没有这个字段」同形。
        // 同样只在查空之后试,同样取 total 不取 rows.Count —— 翻过头那一档路径是存在的。
        string? rewritten = null;
        if (total == 0 && pq.CanTolerateIndex)
        {
            var tolerant = pq with { IndexTolerant = true };
            // 先问「这条路径存不存在」再跑完整查询:EXISTS 命中即停,而完整查询要把
            // 整张表数完再取一页。真没有的那一档(语料里过半)因此少扫一遍。
            // 判据仍是 retry.Total —— EXISTS 不认 --type,它只当挡在前面的快门。
            var retry = ctx.Db.FieldPathExists(tolerant, scope)
                ? ctx.Db.DistinctValues(tolerant, scope, limit.Effective, type, offset)
                : ([], 0);
            if (retry.Total > 0)
            {
                // 这条命令的表下方本来就有一句「这些值来自几条路径」(EmitFieldCoverage),
                // 那句会把实际路径摆出来 —— 所以这里只说改写这件事本身,不重复列形状。
                rewritten = $"Nothing sits at '{path}' as written — indexed paths carry the list index, " +
                            "so this ran with that index left open. The 'field' block below names the paths " +
                            "actually pooled, and any one of them goes back in with --exact-path.";
                pq = tolerant;
                (rows, total) = retry;
            }
        }

        // 一个发射口,每条到达输出的路径都从这儿过 —— 理由与 `where` 那处逐字相同:
        // 分开写的话,return 得早的那条路径迟早被漏掉。置空让重复调用无害。
        void NoteRewrite()
        {
            if (rewritten is null) return;
            ctx.Report.Notice(NoticeKind.NextStep, rewritten);
            rewritten = null;
        }

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"'{path}' takes {Tally.Complete(total).Render("value")} in all.");
                // 翻过头这条路径 return 得早,而改写这件事对它一样成立 —— 说
                // 「一共 N 个」而不说这 N 个从哪条路径来,那个 N 就挂在一条调用方
                // 没写过的路径上。
                NoteRewrite();
                // 翻过头**不是**「没有这个字段」—— 这个字段存在,它的产地也是量得出来的。
                // 这一格照旧摆真数(不是零),两个面都摆:它与正常列表是同一件事,
                // 只是这一页恰好没有行。下面那一支才是空的那种,那边三格才为空。
                EmitFieldCoverage(ctx, path, pq, scope, type);
                return;
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

            // 三种自己给的筛子各是一行 empty_because(Docs/25 乙1);此前各是一句「Drop --exact-path /
            // --type / Widen the scope」。--exact-path 那一行的 hidden 是放开整段之后能回来的 def 数。
            if (loosely)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("exact-path"),
                    ctx.Db.FindByField(pq with { Exact = false }, null, false, scope, 0, 0, type).Defs, ctx.Without("exact-path")));
            else if (withoutType)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("type"),
                    ctx.Db.FindByField(pq, null, false, scope, 0, 0).Defs, ctx.Without("type")));
            else if (outsideScope)
                OutsideScope.Say(ctx, pq, type);
            else
                ctx.Report.Notice(NoticeKind.NextStep,
                              $"No def in this snapshot has a field path ending in '{path}'" +
                              (scope.IsAll ? "" : $" (nor anywhere outside --scope {scope.Expression})") + "." +
                              // 尾巴撤掉时那个句点后面不许留空格 —— 基线闸按行尾空白判红。
                              (deeper is not null
                                  ? ""
                                  : " 'rimsearcher fields <DefType>' lists the paths a type actually has, and " +
                                    "'rimsearcher where --value <text>' finds which path holds a value you already know."));
            if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
            else if (!withoutType && !outsideScope && !loosely) Completeness.NoteIndexHoldsValuesOnly(ctx, path);

            // 空结果上这一格照样摆,**但只在 --json 面**。此前它只在有值时才出现,于是
            // 机器侧「这次一个值都没有」与「你把键名问错了」逐字节同形 —— 而 skill 正教读者
            // 「缺键 = 问错了键」,照着走会去改键名,方向就反了。
            // 文本面不摆:那边上面那句话已经把话说完了,再来一行 `defs_with_field 0` 是噪声。
            if (ctx.Json) ctx.Report.Item("field").Detail("field", [
                new("asked", path),
                new("matched_paths", ""),
                new("def_types", ""),
                new("defs_with_field", (object)0),
            ]);
            return;
        }

        // 计数行走在产地块前面,而不是跟着它下面那张表。line 1 是管道下唯一的幸存者,
        // 那个位置得留给「一共几个、看到了几个」;产地三行是口径,少看一眼不会把
        // 截断读成完整。
        ctx.Report.PageNotice("value", rows.Count, offset, total, $" of '{path}'");

        // 排在计数之后,理由同 `where` 那处:line 1 归「一共几条」。
        NoteRewrite();

        var cov = EmitFieldCoverage(ctx, path, pq, scope, type);

        // 这张表把几条路径的值**并成了一池**,而 matched_paths 只列得下前几条。不指出
        // 收窄的办法,读的人手上就只有一个没法拆开的池子。
        // 「not from one field」2026-09-20 删:148 次实印,没人把它读成单个字段的值域,而 --exact-path
        // 那条出路被照做 19%(基线 2%)—— 承重的是出路,不是那半句否定。
        if (cov.PathTotal > 1 && !pq.Exact)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The values of '{path}' come from {Tally.Complete(cov.PathTotal).Render("field path")} " +
                "pooled together. Any path named for it above goes back in with " +
                "--exact-path to pool that one alone; '[]' there stands for any index.",
                footnote: true);

        // 表上方 —— 理由同 list 那处。
        ctx.AnnounceExcluded(scope, rest => ctx.Db.DistinctValues(pq, rest, 0, type, 0).Total,
                             "value", $" of '{path}'");

        // **这张表的轴是字段,不是值。** 两个闭卷样本把它读反了,而且在两个不同的时点:
        // 一个在读到 where 的外延提示**之前**跑它探路,拿到取值域就认定范围锁定;
        // 一个在读到提示**之后**跑它去证伪那条提示,拿到一个真的否定读数就写下
        // 「不存在其他路径」—— 比不复核时更自信。时序不同只是表象,混淆是同一个:
        // `values <字段>` 给的是**这个字段的取值域**,`where --value <值>` 给的才是
        // **这个值的字段分布**,方向反了。
        //
        // 零结果那一支早就有这条互指了(「finds which path holds a value you already
        // know」),有结果时反而沉默 —— 而踩坑的人拿到的正是有结果的那一份。
        // **话早写好了,只是没长在读者会走的那条路上。**
        //
        // 不写成光秃秃的免责句:把**这张表自己第一行那个值**填进反方向那条命令,
        // 于是它是一条能直接敲的下一步,而不是一句每次都一样的提醒。
        //
        // **试过带一个数(「这个值坐在 N 条 field path 上」),那是个更坏的东西**:
        // N 常常就是 1,而那 1 条正是被问的这个字段自己 —— 句子于是在最该警醒的场合
        // 读成「这个值确实只在这儿」,把结尾那句否定当场抵消。与「报最大的那个」同型:
        // 一个从数据算出来的量,在常见情形下恰好说「你没事」。
        // 数交给那条命令自己去报 —— 它报得对,而且带着它自己那一套边界说明。
        if (rows.Count > 0 && rows[0].Value is { Length: > 0 and <= 40 } top)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 改写发生过时主语要跟着改:同一页上既说「'statBases.stat' 什么都没有」
                // 又说「'statBases.stat' 取这些值」,两句对着干。
                "This table's axis is the field: " +
                (rewritten is null
                    ? $"it lists the values '{path}' takes"
                    : $"it lists the values pooled under '{path}', with the list index left open") +
                ". Which fields hold a " +
                "value you already have is the inverse question and a different command — " +
                $"'rimsearcher where --value {Advisory.Quote(top)} --exact' asks it for '{top}', the first " +
                "row here.");

        // 截断声明不在这儿发:它自成一块,几条路径合成一块由 Run 在说明区之后、表之前发。
        listed.Add((path, cov.DefTypes));

        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["value"] = r.Value,
                ["defs"] = r.Count,
                ["field_path"] = path,
            });
    }

    /// <summary>
    /// 值的产地:后缀匹配天然会把语义不同的路径并进一张表,不说清就会被读成
    /// 「这个字段到处都是这个值」。
    ///
    /// 拆成方法是因为它有**两个**调用点:正常列表,和翻过头的那一页。后者此前直接
    /// return,于是 `--json` 上少一个 `field` 键 —— 而那个键的声明写着 Always present,
    /// skill 又教读者「缺键 = 你把键名问错了」。翻页越界与问错键名于是同形。
    /// </summary>
    private static (IReadOnlyList<(string Path, int Count)> Paths, int PathTotal,
                    IReadOnlyList<(string DefType, int Count)> DefTypes, int DefsCovered)
        EmitFieldCoverage(CommandContext ctx, string asked, PathQuery pq, ScopeFilter scope, string? type)
    {
        var cov = ctx.Db.ValueCoverage(pq, scope, Limits.MaxSuggestions, type);

        // 这里的省略不是 NameList 那种「我取了前几条」—— cov.Paths 已经在 SQL 侧截过,
        // 手上根本没有第 4 条起的名字。分母只有 cov.PathTotal 知道,所以照实拼。
        var pathList = string.Join(", ", cov.Paths.Select(x => $"{x.Path} ({x.Count})"));
        if (cov.PathTotal > cov.Paths.Count) pathList += $", and {cov.PathTotal - cov.Paths.Count} more";

        // 「N of M」是覆盖率的分母:少了它,一条 `Verse.Thing (7)` 分不清是「只有 7 个 def
        // 这么写」还是「导出漏了一千多个」。
        var typeList = string.Join(", ", cov.DefTypes.Select(x =>
            $"{x.DefType} ({x.Count} of {ctx.Db.CountDefsOfType(x.DefType, scope)})"));

        // 走集合而不是裸键:几条路径各有一份产地,写同一个键后写的会静默盖掉先写的。
        // 单条路径的调用也走集合 —— 形状随路径条数变的话,消费方得先数参数才知道
        // `field` 是对象还是数组。
        ctx.Report.Item("field").Detail("field", [
            new("asked", asked),
            new("matched_paths", pathList),
            new("def_types", typeList),
            new("defs_with_field", (object)cov.DefsCovered),
        ]);
        return cov;
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
        Options = [CommonOptions.Limit("mods")],
        Examples = ["rimsearcher mods"],
        JsonKeys = [new() { Key = "mods", Rows = true, What = "one row per mod, in load order: order, package_id, name, version." }],
    };

    public override int Run(CommandContext ctx)
    {
        var all = ctx.Db.Mods;
        var limit = ctx.Limit();
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

/// <summary>
/// 「这条路径在这份快照里有,只是不在这个 <c>--scope</c> 里」的唯一产地。
///
/// 这一档与「快照里根本没有这条路径」在结果上逐字同形(两边都是零行),而**出路相反**:
/// 前者把 <c>--scope</c> 放宽就有,后者放宽也没有、该去问这个字段到底叫什么。
/// 判据是白拿的 —— 同一条存在性查询,作用域换成 all 再问一次。
///
/// <c>where</c> 那侧此前没有这一档:它的零结果只分三种成因(路径不存在 / 值不在值域里 /
/// 名字是 def 的身份),作用域不在其中,而它的存在性判据**是带作用域的**。于是
/// 「只是被圈掉了」落进了「路径不存在」那一支,读者被指去 <c>fields --path-contains</c>
/// 找字段叫什么、去 code-search 读声明 —— 出路配错了状态。
/// (`values` 那段注释写着「与 `where` 的分流同形」,那句对称性一直不成立。)
/// </summary>
internal static class OutsideScope
{
    /// <summary>
    /// 「这条路径在快照里有,只是不在这个 --scope 里」:一行 empty_because(--scope / 拿掉它能回来几个 def /
    /// 拿掉它的同一条命令)。此前 where 与 values 共用一句「'{path}' exists in this snapshot but no def
    /// has it within --scope X. Widen the scope…」(Docs/25 乙1)。
    /// </summary>
    public static void Say(CommandContext ctx, PathQuery pq, string? type)
    {
        var hidden = ctx.Db.FindByField(pq, null, false, ctx.Unscoped(), 0, 0, type).Defs;
        ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("scope"), hidden, ctx.Without("scope")));
    }
}

/// <summary>
/// 「<c>--path-contains</c> 命中了几条、总共几条、其中整段的几条」这一句。产地唯一 ——
/// <c>get</c> 问一个 def、<c>fields</c> 问一个类型,问的是同一件事。
///
/// **分母与命中数同句**。`fields` 那侧此前只印命中数,而句式与不加过滤时逐字同形
/// (「2863 field paths on QuestScriptDef」,不加过滤是「3094 field paths on
/// QuestScriptDef」),于是一个已经被滤过的数读起来像这个类型的全部,而滤掉了多少
/// 一个字都没有。整段/子串那半句原先另发一条,得读者自己把 2854 + 9 加回 2863。
///
/// 整段那半句不许收在存在性的强断言上:「前缀式列举」是正常用法,要找的字段往往就在
/// 这句话下面那张表里。只摆事实,并说破这一行**一条都没滤掉** —— 前半句的
/// 「N out of M」会被读成表已经被剔过。
/// </summary>
internal static class PathFilterSummary
{
    /// <summary>
    /// 出路**只挂 <c>whole == 0</c> 那一支**,不是两支共用。
    ///
    /// 2026-09-09 补出路时两支都挂了,同日复查改掉。错在出路答的不是它挂着的那个态:
    /// 两个数分的是「整段命中 / 嵌在更长名字里」,而 <c>--exact-path</c> 钉的是整条路径,
    /// 它给不出「只要那 6 条整段的」。两支于是相反 ——
    ///
    /// <list type="bullet">
    /// <item><c>whole == 0</c>:根本不存在「整段那一档」可给,把其中一条整条报出来是此处
    /// 唯一的收窄。实测有人照着 where 学会这个词、带到 <c>get</c> 上敲了 7 次一次没成,
    /// 缺的正是这句。留。</item>
    /// <item><c>0 &lt; whole &lt; matched</c>:存在一个读者要得起的子集,而这条尾巴把 7 条
    /// 收成 1 条 —— 拿一个够不着的开关顶替一个没造的开关。删。想按整段筛得先有那个开关,
    /// 而它目前没有任何实测需求。</item>
    /// </list>
    ///
    /// 「什么都没被滤掉」那句**不能**被这句顶掉,两句管的是两件事:那句说的是这一行
    /// 自己没滤掉任何东西(要的字段就在下面表里),这句说的是想滤该敲什么。删掉前者,
    /// 读者会把「不是整段」读成「你要的东西被藏起来了」。
    ///
    /// 2026-09-09 改掉那句里的 this line:它指的是这条注意行自己,而读者手边正有一张表,
    /// 「line」先被读成表里的一行。改成直说没滤掉、并把落点指到表上。
    /// </summary>
    private const string Pin = " Passing one path from the column back as " +
                               "'--exact-path <that path>' keeps that one alone.";

    public static string Say(IReadOnlyList<string> filters, string subject, string noun,
                             int matched, int total, int whole, string whose = "")
        => $"Matching {PathFilterText.Say(filters)}{whose}: {Tally.Complete(matched).Render(noun)}, " +
           $"out of {Tally.Complete(total).Render(noun)} {subject}." +
           (whole == 0
               ? $" None of those has {PathFilterText.Say(filters)} as a whole path segment: each match " +
                 "contains it inside a longer name. Nothing is filtered out for that — every match is " +
                 "in the table below." + Pin
               : whole < matched
                   ? $" Whole path segment: {Tally.Complete(whole).Render(noun)}; " +
                     $"inside a longer name: {Tally.Complete(matched - whole).Render(noun)}."
                   : "");
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

    /// <summary>
    /// 打进 <c>fields</c> 的名字不是 def 类型,而反编译树里有一个同名类型 —— 那个类型
    /// 就是问的人要的东西,只是它没有自己的 def 数据库。
    ///
    /// 第十二轮盲测:<c>fields ThingDefCountClass</c> 落空后,四格里有三格改去从
    /// <c>costList[0].*</c> 的样本里凑字段,交出的名单是六个里的三个 —— 而权威名单
    /// 就在 <c>ThingDefCountClass.cs</c> 里,ILSpy 按类型名给每个文件命名,当场找得到。
    /// 上面那句「没有这个 def 类型」是真话,可它把人留在了 def 那一层。
    ///
    /// 判据必须**当场算得出来**(NameLookup 的纪律):真去找那个文件,找不到就一个字不说。
    /// 只认纯标识符,既挡住通配符进 <c>EnumerateFiles</c>,也挡住路径分隔符。
    /// 这一句只给 <c>fields</c>,不给 <c>list</c>:「这个类的字段有哪些」与「这个类的 def
    /// 有哪些」是两个问题,后者的答案是 <c>list &lt;DefType&gt; --class</c>。
    /// </summary>
    public static string? InSourceInstead(CommandContext ctx, string typed)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        if (!typed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return null;

        var hits = new List<string>();
        try
        {
            foreach (var tree in SourcesShared.TreeNames(root))
                hits.AddRange(Directory
                    .EnumerateFiles(Path.Combine(root, tree), typed + ".cs", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (hits.Count == 0) return null;
        hits.Sort(StringComparer.OrdinalIgnoreCase);

        // 几棵树里各有一个同名文件是常态(mod 重打包了游戏的类)。只报头一条会把一个
        // 挑选说成一个事实,而下一步那条命令自己会在这上面停下来问 —— 先说清楚。
        // 不写「它只出现在别的 def 的字段里,所以够不着」:那是**猜**这个名字在数据里扮
        // 什么角色,而这里一个字都没查过它。说得住的只有机制本身 —— 只有 def 类型才有
        // def 数据库,这条命令读的正是那个。
        return $"'{typed}' is a type in the decompiled source all the same — " +
               $"{NameList.Render(hits, Limits.MaxSuggestions)}. Only a def type gets a def database, so this " +
               "command has nothing of its own to read for it, while the source does: " +
               // 不许写 "every":--outline 匹配的是花括号,RegionProcessor.cs 整文件一份
               // `delegate` 声明,轮廓 0 条。口径与 ReadCommand.SayNoDeclaration 同。
               $"'rimsearcher read {(hits.Count > 1 ? hits[0] : typed + ".cs")} --outline' lists what brace " +
               "matching finds there, each with its line range.";
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
    ///
    /// **「两个无声成因」那句(null on every def / unsaved runtime cache)2026-09-20 删。** 它曾靠盲测留下
    /// (两臂各 10 次,带它的 10/10 用「缓存」措辞,不带的 0/10,p=1.1e-05),但那道题面是「零就是答案,
    /// 解释成因」。真实语料里它印了 490 次,随后文字 41 条里用缓存 / null 措辞的 4 条(9%),低于全语料
    /// 基线 14%;读者对零的反应是「管道有问题 / 路径写错了」—— 下一步 114 次原样重跑、137 次换个
    /// where、89 次 get。真实的零绝大多数是查询打错,成因句对着一个几乎不出现的场景写。
    /// 产地 tools/audit-blindtest-prose.py。
    /// </summary>
    public static void NoteIndexHoldsValuesOnly(CommandContext ctx, string? path = null)
    {
        // 字段声明的行长这样:`public List<ThingDefCountRangeClass> killedLeavingsRanges;`。
        // 类型里带 <> 和 [],所以字符类不能只有 \w —— `\w+ <name>;` 实测零命中。
        var leaf = Leaf(path);
        var how = leaf is null
            ? "'rimsearcher code-search' reads the class declaration, which does say."
            : $"'{DeclarationSearch(path)}' finds the declaration, which does say.";

        ctx.Report.Notice(NoticeKind.Boundary,
            // 「导出器在某个 def 上停短,那条路径也不在索引里 → 'snapshot truncated'」那段 2026-09-20 删:
            // 40 次实印,`snapshot truncated` 被照做 0 次(基线 1%)。截断那一层的读法住
            // snapshot status 的 Remarks 与 get 的截断行。
            $"No indexed value sits at that path. {how}");
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
    /// <summary>找这条路径末段那个字段的声明行的 code-search 命令;末段不像标识符时退回裸 code-search。</summary>
    public static string DeclarationSearch(string? path)
        => Leaf(path) is { } leaf
            ? $"{CommandRegistry.ExeName} code-search \"[\\w<>,\\[\\] ]+ {leaf};\""
            : $"{CommandRegistry.ExeName} code-search";

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
        CommandContext ctx, DefRow def, IReadOnlyList<FieldRow> fields,
        bool defaultRowsListed, int defaulted)
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
        //
        // 「为什么没比」这个理由不许写成身份断言。此前两支共用的后半句是
        // 「a yes is a declared default already」,而 yes 的真实含义只是**值与新 new 的
        // 一模一样**(产地注释 FieldDefault.Render 写得很准:「这不等于没人设过它 ——
        // XML 里照着默认值再写一遍是常事,能证的只有『无从区分』」)。
        // 那句话把「值相等」说成了「它就是那个默认值」,读者顺着推就得出「所以作者没写」——
        // 正是 r13 题 2 那个错答的方向。输出与它自己的产地注释矛盾,是假话,不是天花板。
        // **否定放主句,匹配事实放从句。** 此前是反过来的(前半句陈述、破折号后免责),
        // 而实测有人复述了前半句、接着自己接上「所以是没写」—— 前半句可独立成立时,
        // 只读前半句反而显得更完整。inherit 那句同型的话就是这么排的
        // (`A 0 is therefore not evidence that…` 是主句)。
        // 外层已经有一个冒号,这里用破折号。破折号本身不是问题 —— 问题是此前**否定在
        // 破折号后**,于是只读前半句的人读到的是一句可独立成立的陈述。
        //
        // 后半句此前解释的是「yes 是什么意思」(值与新 new 的一样),那是准确的,但盲测里
        // 0/10 —— 读者拿它接着推「所以没人写」。换成**把两个分不开的 def 各自点名**之后
        // 是 4/10;这是 get 那句同一手法的复制(那边 4/10 对 0/20),两次合并 8/20 对 0/30,
        // p=0.0002。要点是两边都得是读者叫得出、能去核对的东西 —— 抽象地说「工具区分不了」
        // 在 read --outline 那条上实测仍是 0/10。
        // 这两者不同形:xml 列就在同一行上,here 是「写了同样的值」,no 是「从没提过这个字段」。
        // 「yes 留下的问题由同一行的 xml 列定」是**列义**,住 help
        // (「the 'xml' column says whether this def's own XML wrote the path …」)。2026-09-20 删:
        // 那半句印了 276 次,随后文字提到 xml 列的 0/12,与 bfd0ec7 立的「列义住 help」同一口径。

        // 这半句只在**本次取景里真有 yes 行**时才拼。两条被砍掉的路径各有各的毛病:
        //   不加 --defaults 时,yes 行被整批滤走、表折成 `code_default=no`,而
        //     「Not listed: N fields…」那句已经带着逐字同义的否定(`That match is not evidence
        //     that nothing wrote them`)。两句同屏说同一件事,后一句还指着一个屏幕上不出现的
        //     取值 —— 读的人得先假设有 yes 行才读得懂它;
        //   defaulted 为 0 时(MoteGlow --defaults 是实例),整个 def 一行 yes 都没有,
        //     这句指的是一个读者当场能验证为空的集合,只会制造「是不是还藏着几行」的疑问。
        // **否定没有跟着分支消失**:它与上面那条 Filter notice 的条件恰好互补
        //   (那边是 `!withDefaults && defaulted > 0`),于是「存在一个 yes」的每条路径上,
        //   这条否定都还在,只是换了承载者 —— 分支的是位置,不是有无。
        // 判据用 defaulted 而不是「渲染出来的行里有没有 Same」:defaulted 是过滤后、
        //   **截断前**的数(见 SnapshotDb.Fields),否则同一个 def 换个 --limit 就换一句结论。
        var carryYesMeans = defaultRowsListed && defaulted > 0;
        ctx.Report.Notice(NoticeKind.Advisory, listed.Count > 0
            // 「so their code_default is not this def having made a choice」2026-09-20 删:733 次实印、
            // 随后文字 78 条,没有一条写「它自己选的」;那半句防的读法在真实使用里不发生。
            ? $"Values that most of the {total} {def.DefType}s in this snapshot also carry — the count in " +
              $"brackets: {NameList.Render(listed, listed.Count)}." +
              (carryYesMeans
                  ? $" Only rows whose '{FieldDefault.Column}' is no were compared."
                  : "")
            // 否定支砍掉「所以没有一个是透过那一列显出来的全类默认值」:那是前半句的改写,
            // 而 SKILL.md 讲过这条线是干什么的。「yes 没参与比较」那半句留着 ——
            // 它守的是「没比过」被读成「比过了没有」,而那正是这一支印在一张全 yes 表下面时的样子。
            : $"No value above with '{FieldDefault.Column}'=no is one that most of the {total} " +
              $"{def.DefType}s in this snapshot also carry." +
              (carryYesMeans
                  ? " Rows marked yes were not compared."
                  : ""));
    }

    /// <summary>
    /// 嵌套 <c>Class="…"</c> 的运行时类型住在哪条路径下 —— 两个「像类名」的落空处念的同一句。
    /// </summary>
    public static string NestedClassLine(CommandContext ctx)
    {
        return "The runtime type of a nested Class=\"...\" object — in a list or on a single field — is " +
               "indexed as '<path>.Class', so 'rimsearcher where Class <ClassName>' is the query that reaches it.";
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
    /// <summary>
    /// 四条命令共用同一份声明 —— 键的形状是这里定的,各命令只是把它列进自己的键表。
    /// 抄四遍的话,改一处措辞会留下三份说的是旧形状。
    /// </summary>
    public static readonly JsonKeySpec JsonKey = new()
    {
        Key = "completeness",
        What = "an object, present only when some def in scope had its export cut short: scope (which def " +
               "types this covers, in words), defs_cut_short (how many), " +
               "types (one row per def type with its own count), verify (a ready command that lists them). " +
               "Absent means no def in that scope lost fields at export.",
    };

    public static void NoteIndexedPathsOnly(CommandContext ctx, TruncationScope affected, string basis)
    {
        if (affected.Count == 0) return;

        var shown = affected.ByType.Take(Limits.MaxSuggestions).ToList();
        // 名单长过上限时那条命令只走得到印出来的这几行 —— 退回裸命令,它覆盖每个类型。
        // 填好参数的那条更好用,但一条只查了一半的命令看着与查全了的一模一样。
        var cmd = shown.Count < affected.ByType.Count
            ? "rimsearcher snapshot truncated"
            : "rimsearcher snapshot truncated" + string.Concat(shown.Select(t => $" --type {t.Type}"));

        ctx.Report.Add(new CompletenessBlock("completeness", basis, shown, affected.Count, cmd));
    }
}

internal static class Advisory
{
    /// <summary>
    /// 一个**外来的类**坐在官方 def 上时,说破 <c>mod</c> 列答的不是「谁把它挂上去的」。
    ///
    /// 第三轮盲测(2026-09-01,A/B 各 3 人)的实测:六个人都能正确说出 mod 列是声明处 ——
    /// 那半句 <c>where --help</c> 里本来就有 —— 然后**六个人全都接着断言了「谁挂的」**,
    /// 5/6 说成「通过补丁」(真值是 C# 运行时装的),把握一律写 100%,而
    /// <c>--defaults</c> 零人使用。SKILL.md 里那条同批下架:A 组读过它的
    /// 「Nothing records which mod authored a value」,照样断言 —— 文档上零采纳。
    ///
    /// 所以这句不是重复 <c>--help</c> 的那半句,它补的是后半件事:**谁挂的查不出,
    /// 但「是不是 XML 写的」查得出**,而那正是那五个人答错的地方。
    ///
    /// 判据窄成这样是量过的:「结果跨多个 mod」在 baseline 上 11/12、races 上 12/12 命中,
    /// 那是纯上下文税;收成「第三方命名空间的类 + 落在官方 def 上」之后,races 的
    /// compClass 取值里只有 28/1025(2.7%)会触发,且那 28 条正是这一类。
    ///
    /// 出路跟着能力位分三档 —— <c>xml</c> 那一列读的是哪份 XML 决定了 <c>no</c> 能排除什么。
    /// 补丁前读的快照上 <c>no</c> 分不开 patch 与 C#,那时把话说满就是给一个错判据。
    /// </summary>
    public static void NoteValueAuthorship(CommandContext ctx, PathQuery own, string? value, bool exact,
                                           Snapshot.ScopeFilter scope, string? defType)
    {
        // 没给值时这条命令答的是「谁有这个字段」,不存在「谁写了这个值」这回事。
        if (value is not { Length: > 0 }) return;
        // **不按 --exact 门控。** 一度这么写过,而盲测里那六个人敲的正是不带 --exact 的
        // 默认形态 —— 那道门会让这句话恰好在唯一测到过它该出声的场合哑火。
        // 子串匹配也不需要那道门:不带命名空间的片段落在 FromGameCode 的单段那一档上被挡住,
        // 而带命名空间的片段(`ASEL.` 开头)本身就说明了命中的那批值归谁。
        if (!ClassNameShape.Looks(value) || ClassNameShape.FromGameCode(value)) return;

        var mods = ctx.Db.FindModsHolding(own, value, exact, scope, defType);
        if (mods.Count == 0) return;

        var official = ScopeFilter.Parse("vanilla", ctx.Db.PackageIds(), ctx.Config);
        var onOfficial = mods.Where(m => official.Includes(m.Mod)).ToList();
        // 全落在第三方 def 上时不出声:那种表里 mod 列与值的来源常常真的是一家,
        // 而这句话的整个由头是「看着像一家,其实不是」。
        if (onOfficial.Count == 0) return;

        // 「What is settled is whether…」那个框架句 2026-09-01 压掉了:它只给下半句搭台,
        // 而下半句自己就带着主语。同批压掉的还有开头那句「X 不是游戏自己的类」——
        // 那是在解释这条 notice 为什么出现,对读者要做的判断不提供任何输入。
        // xml 列读的是哪份 XML、no 能排除什么,是 get 的 Remarks 里的机制;这里只指过去。
        ctx.Report.Notice(NoticeKind.Boundary,
            // 主语固定成 it,计数全在介词短语里 —— 计数放主语位时动词得跟着单复数变,
            // 而 NounRegistry 管名词不管动词(同一条纪律在 AnnounceExcluded 上也写着)。
            // 「'mod' is where each def was declared, not who put this value on it」那半句随列名
            // 改成 declared_in 一起退场(Docs/25 丁2):列自己说了它是谁声明的。
            // 「Nothing in the snapshot records who put it there.」2026-09-01 压掉:它与
            // 那半句是同一个命题的两遍,而它自己不给下一步。三轮盲测对这一句零信息量。
            $"'{value}' sits on {Tally.Complete(onOfficial.Sum(m => m.Defs)).Render("def")} declared in " +
            "official mods; the 'xml' column of 'rimsearcher get <defName> --defaults' says whether their " +
            "XML wrote it.");
    }

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

    // internal:values 那句互指要拼一条能直接敲的命令,而引号规则不该有第二份。
    internal static string Quote(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

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
    /// <summary>
    /// 点名了字段的那次查询,**外延不足**的那一半。
    ///
    /// `where` 对「结果比预期多」已经处理得较齐(跨形状那句、子串那句、scope 补集那句);
    /// (**这行原先写的是「很齐」,而「子串那句」当时只长在 `--value` 不点名字段那条路上** ——
    /// 点名字段这条路一个字都没有。注释断言了一个不存在的覆盖,见 <see cref="NoteSubstringWidened"/>。)
    /// 这一句管的是反向:`where explosionRadius --value 3.9 --exact` 返回的两行每一行都对,
    /// 只是**不是全部** —— 同一个值另有 <c>comps[].explosiveRadius</c> 九个 ThingDef,
    /// 而这张表计数准确、路径明确、code_default=no、mod 归属清楚,**没有一处看得出问题**。
    /// 闭卷实测四个样本零反查,其中一个带着「字段名一律反查、不许猜」的文档照样踩,
    /// 还主动加了 --exact 与 --limit all(两个已知陷阱都躲了,栽在没被提示的这个上)。
    ///
    /// **不设阈值**:常见值上它每次都出声(值 1 落在 1800 条路径上),吵是吵,但沉默一旦
    /// 挂上条件就承重了 —— 没提示会被读成「我的外延是全的」,而那正是本仓修过五次的形态。
    /// 噪声改由数值本身承担:按命中行的 def_type 收窄之后,3.9 那句给出「9 对 2」,
    /// 而值 1 那句给出的大数当场说明「筛选全靠字段名在做」,那是校准不是噪声。
    ///
    /// 口径跟随查询:带 --exact 就精确反查,否则子串 —— 两处不同口径会让这个数与表打架。
    /// </summary>
    public static void NoteValueElsewhere(CommandContext ctx, PathQuery own, string? value, bool exact,
                                          Snapshot.ScopeFilter scope, int here)
    {
        // 没给值时这条命令答的是「谁有这个字段」,不存在「同一个值还在别处」这回事。
        if (value is not { Length: > 0 }) return;
        if (ctx.Db.ValueElsewhere(own, value, exact ? ValueMatch.Exact : ValueMatch.Substring,
                                  scope) is not { } el) return;
        // **加法在这里不做**,而且要说破为什么不做 —— 这是别处那批「CLI 把两个加数加完」
        // 的句子的例外:哪些形状跟提问的是同一件事,判据不在值里(靶题里
        // comps[].explosiveRadius 算,statBases[].value 不算),CLI 判不出来。
        // 试过给并集,那是个看着像答案的错数(17,真值 11)。所以给形状自己的数,
        // 并把「相关不相关得你判」写在句子里,而不是让读者自己想到这一层。
        //
        // 也不写成「N 对 M」那种比较:自己就是最大项时那场比较赢了,于是这句话在
        // 最该警醒的场合读成了「你没事」。
        // **列若干条,不报「最大的那个」。** 只报最大项时,起手字段一换,指向就从
        // 「正是要找的另一半」变成「一个无关字段」—— 而选样标准(def 数)与相关性无关,
        // 恰恰是这句话自己在说的那件事。列出来交给读者判,与紧邻的跨形状那句同形。
        // **数在主语位,不在从句里。** 原先的主句是「Naming a field narrows to that field」——
        // 讲的是读者做了什么,而出路挂在句尾从句上。实测过的形状(`N of M` → `M defs,
        // showing the first N`,haiku 闭卷 0/7 → 7/7)说的正是这件事:换个说法是措辞,
        // 换哪个数当主语是**结构**,而弱档只吃后者。
        //
        // 跨类型那一半单独报数。它是这句话现在最要紧的一件事:起手字段决定了 def_type,
        // 而答案常常整个坐在别的类型上(消费侧实例里 82% 如此),读者手上那张表却
        // 一行都看不出这回事。
        //
        // **这句话到主句+枚举为止,没有尾巴。** 原先句尾挂着「相关性从值里推不出来 +
        // `where --value` 兜底」那 184 字符,2026-08-14 的开卷盲测(152 个受试、四臂
        // × 三模型档)把它删了:
        //   · 尾巴喂的是检索行为,而检索行为 agent 自己就有 —— **只留主句的 D 臂输出里
        //     一个命令字都没有,Sonnet 兜底率仍是 8/8**,四臂兜底率齐平(8/8/7/8);
        //   · 枚举段喂的是答案本身:note 印的 `costList[].thingDef (14, ThingDef)` 里
        //     那个 14 会被**直接搬进答案** —— 带枚举 22/32 答出 14,不带 14/32,p≈0.023。
        // 三档排序 C ≥ A > B > D,C(主句+枚举)在 Sonnet 与 Opus 上均分都是第一。
        //
        // 留档以免重犯:先前依据 Sonnet 单档 n=8(7/8、轮数最低)得出过相反的
        // 「砍枚举」建议,被扩样 + Haiku 复现失败 + D 臂一起推翻。
        // **单档、小 n、且判据已饱和时的优势,不是优势。**
        var others = el.CrossTypeShapes > 0
            ? $", {el.CrossTypeShapes} of them on def types other than " +
              $"{string.Join("/", el.Types.OrderBy(t => t, StringComparer.Ordinal))}"
            : "";
        // 每条带上自己的 def_type —— 少了它,一条跨类型的形状与一条同类型的在这行里同形。
        // **但全都落在同一个类型上时,那一列逐条相同**,此时它不再区分任何东西,只是
        // 每条重复一遍同一个词:提到前面说一次。判据是「这段字每次出现是否都一样」,
        // 一样就等于背景噪声 —— 与 crossType=0 时不出那句从句同一条理由。
        var lone = el.Shown.Select(s => s.Types).Distinct(StringComparer.Ordinal).ToList();
        var oneType = lone.Count == 1 && el.OtherShapes == el.Shown.Count;
        ctx.Report.Notice(NoticeKind.Boundary,
            $"'{Quote(value)}' also sits on {Tally.Complete(el.OtherShapes).Render("path shape")} " +
            "beyond this one" + others + (oneType ? $", all on {lone[0]}" : "") + ": " +
            string.Join(", ", el.Shown.Select(s => oneType ? $"{s.Shape} ({s.Defs})" : $"{s.Shape} ({s.Defs}, {s.Types})")) +
            (el.OtherShapes > el.Shown.Count
                ? $", plus {Tally.Complete(el.OtherShapes - el.Shown.Count).Render("path shape")} not shown"
                : "") + ".");
    }

    /// <summary>
    /// 不带 <c>--exact</c> 时值是**子串**匹配,而点名字段这条路此前一句话都不说。
    ///
    /// `where explosionRadius --value 2` 回 11 行,其中只有 2 行真的等于 2 ——
    /// 其余是 <c>2.49</c> / <c>2.4</c> / <c>2.9</c>,还有一行是 <c>7.2</c>(它也含 "2")。
    /// 表本身没撒谎(value 列就印着),但首行只说「11 defs」,而读者问的是 2。
    ///
    /// **这是一处跨产地口径不一致**:同一件事在 `where --value 2`(不点名字段)那条路上
    /// 一直说着(「Value exactly '2': 530 field paths; containing it: 3072」),点名字段
    /// 这条路上零。两条路答的是同一个问题的两种问法,不许一条说一条不说。
    ///
    /// 数取 def 而不是行,并且**它就是读者眼前那张表的行数来源**——句子里引用的量必须
    /// 是同屏看得见的那个,否则读者核不动。
    ///
    /// 沉默有两种,都不是「没算」:带了 <c>--exact</c>(那时子串根本没参与),
    /// 以及**算过了、这次子串一条都没多收**(每一行都精确相等)。后者尤其要留着不说 ——
    /// 常见值上这句话会次次出声,而它每次给的数都随查询变,不是同一句免责声明重播。
    /// </summary>
    public static void NoteSubstringWidened(CommandContext ctx, PathQuery own, string? value, bool exact,
                                            Snapshot.ScopeFilter scope, int here, string? defType = null)
    {
        if (exact || value is not { Length: > 0 } || here == 0) return;
        // defType 跟着传:here 是**表上看得见的那个 def 数**,而 strict 拿来跟它比。
        // 一侧收窄一侧不收,strict 会大过 here,于是句子里那个减法印出负数。
        var strict = ctx.Db.FindByField(own, value, true, scope, 0, 0, defType).Defs;
        if (strict == here) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            strict == 0
                ? $"Nothing here holds exactly '{Quote(value)}': every row has it inside a longer value — " +
                  "see the value column. --exact would return nothing at all."
                : $"Of the {Tally.Complete(here).Render("def")} here, {strict} hold exactly '{Quote(value)}' and " +
                  $"{here - strict} hold it inside a longer value (the value column says which); " +
                  "--exact keeps the first group.");
    }

    public static void NoteMixedPathShapes(CommandContext ctx, IReadOnlyList<(string Shape, int Count)> shapes)
    {
        if (shapes.Count < 2) return;
        // 恰好只多出一条时全列 —— 与相邻那句同一条:那句「plus 1 not shown」占的字
        // 比那一项本身还多。两句现在挨着读,列表长度的规矩不许两样。
        var shown = shapes.Take(shapes.Count == Limits.MaxSuggestions + 1
                                    ? shapes.Count : Limits.MaxSuggestions).ToList();
        ctx.Report.Notice(NoticeKind.Boundary,
            "These rows span more than one path shape: " +
            string.Join(", ", shown.Select(x => $"{x.Shape} ({x.Count})")) +
            // 「and 1 more shapes」——名词随数量变形只能走登记处。挪到表上方之后
            // 这句是人人都会读到的了,那个复数错也跟着从脚注区走到了台前。
            (shapes.Count > shown.Count
                ? $", plus {Tally.Complete(shapes.Count - shown.Count).Render("path shape")} not shown"
                : "") +
            // 旗名点上命令名。实证:这句话是 where 印的,而它最常见的下一步是拿着
            // def_name 走 get —— 于是形状和旗一起搬过去,而 get 上没有这个旗
            // (它的路径筛选叫 --path-contains)。一次真实调用为此花掉了两轮 exit 2。
            ". The suffix matched them all. Pasting one of those shapes back into this command with " +
            "--exact-path keeps that one alone.");
    }

    /// <summary>
    /// 这一屏里混着加载期由 C# 造出来的 def。
    ///
    /// 盲测实证:`where` 的结果最常见的下游是 `--json` 灌进脚本批量生成
    /// PatchOperation,而按 defName 寻址的补丁**打不到**这批 def —— 满配文档的四个臂里
    /// 有三个照样交出了一串够不着的 `Blueprint_*`。文档在场没能挡住,所以这件事得落在输出上。
    ///
    /// 光有这一句还不够,真正管用的是同时出现的 <see cref="WrittenIn.Column"/> 列:
    /// 出事的那次是 `--json` 进脚本,而**脚本不读 notes**。这一句是给人看的入口,
    /// 说清楚那一列是干什么的;判定要落在行上。
    ///
    /// 因此它不是 footnote —— 它改的是每一行怎么读,不是给某一行加注。
    /// </summary>
    public static void NoteGeneratedDefs(CommandContext ctx, int generated, int defs, int onThisPage)
    {
        if (generated == 0) return;
        // 分母取 def 数不取行数:两边都按 DISTINCT def 数,才是一个比例。表头那个数
        // 数的是(def, 路径)行,拿它当分母会把比例算小(`capacity Consciousness`
        // 是 155 行 / 80 个 def)。
        // 「from no XML node at all」「A PatchOperation addressed by defName cannot reach them — patches
        // run on the XML, where these do not exist」与三个例子名 2026-09-18 删掉:列值 code 自己
        // 说了它是哪一类,补丁够不着是机制,住 --help;名单就是那一列。留的是整集里有几个
        // (页内读不出)与它们在不在这一页。
        ctx.Report.Notice(NoticeKind.Boundary,
            $"{generated} of the {Tally.Complete(defs).Render("def")} matched here " +
            $"{(generated == 1 ? "is" : "are")} built in code at load time ({WrittenIn.Column} = code). " +
            // 页内一个都没有时不许沉默:名字扎堆,首页排序上常常一个都碰不到,而这句话
            // 说的是整个结果集。不点破的话读的人会拿这一页当全集,而列在这里一格都不动。
            (onThisPage == 0 ? "None of them are on this page." : ""));
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
            "; 'rimsearcher get <defName> --path-contains description' tells them apart.",
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

        // 同块字段互相覆盖是机制,住 SiblingHelp(get / where 的 Remarks);这里点名 + 出路。
        ctx.Report.Notice(NoticeKind.Advisory,
            $"Set by hand in the same block as the rows above: {NameList.Render(names, Limits.MaxSuggestions)}." +
            (block is null
                ? ""
                : $" 'rimsearcher get {first.DefName} --path-contains {block}' lists the whole {block} entry."),
            footnote: true);
    }

    /// <summary>同块兄弟那条 Advisory 的机制,get 与 where 的 Remarks 各引一份。</summary>
    public const string SiblingHelp =
        "Fields in one indexed block — a comps[N] or statBases[N] entry — bind and override each other, " +
        "and a path filter shows only the one asked for; when the row shown is a hand-set value, the answer " +
        "names the other hand-set fields of the same block.";
}
