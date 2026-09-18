using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 继承层的出口。
///
/// 这是快照里唯一**不是**「游戏内存里的对象」的一层:游戏在 <c>LoadAllActiveMods</c> 末尾就
/// <c>XmlInheritance.Clear()</c>,而导出跑在 <c>StaticConstructorOnStartup</c>,那时继承关系
/// 已经应用完并丢弃;抽象父节点更是从头到尾没有 Def 实例,所以 <c>get</c> 永远找不到 BaseBullet。
///
/// 边界写在每一条结果上而不是挂一段总的免责声明:这一层是**打补丁之前**的 XML,
/// 而每个具名节点带着「有多少条 xpath 点了它的名」。
/// </summary>
public sealed class InheritCommand : Command
{
    /// <summary>见证表里「有几个后代读到参照值」那一列的两个名字 —— 参照值是问的 def 自己的值 / 子树众数。</summary>
    public const string SameValue = "same_value";
    public const string SameAsMode = "same_as_mode";

    public override CommandSpec Spec => new()
    {
        Name = "inherit",
        Aliases = ["inheritance", "parent", "parents", "children", "tree"],
        Summary = "Show what an XML node inherits from and what inherits from it, including abstract parents.",
        Remarks =
            "This is the one part of a snapshot that is read from the mods' XML rather than from the objects the " +
            "game had in memory, because the game resolves inheritance while loading and then discards it. " +
            "Abstract parents exist only here: they never become defs, so 'get' will not find them.\n\n" +
            // 与 identity 块那三支同口径 —— 它们说的是同一个计数,而 r14 抓到一个受测者
            // 读了输出的新句、再引这里的旧句把它降格成「通用免责措辞」驳回。旧句三处错:
            // ① 「0 there means what you see is exactly what the game read」是假话,正是
            // 7065749 从代码注释里推翻的那一句;② 「by defName」把遗漏面窄化成一条,而计数
            // 的正则只认 @Name=,漏的是所有其他定位方式(852e863 已在输出侧修掉);
            // ③ 只把 unanswered 给了无 Name= 的节点 —— 那正好暗示有 Name= 的是 answered,
            // 而那是受测者驳回新句时踩的那级台阶。
            "What is shown is the XML before PatchOperations are applied. patch_ops counts xpaths that name the " +
            "node with @Name=; patch_ops_defname and patch_ops_label count xpaths that name it by defName= and " +
            "by label=, and a snapshot exported before those were measured has neither column. " +
            "An xpath that reaches a node by thingClass or by a wildcard is counted nowhere in this layer, " +
            "so a 0 is not evidence that the node reached the game unpatched. A node without a Name= reports " +
            "patch_ops as 'n/a' rather than 0 because that count was never taken; the defName and label counts are " +
            "still taken. " +
            "For the merged, post-patch values, read any concrete child with 'get' — everything a parent " +
            "contributes is already in each of its children.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "name",
                Variadic = true,
                Help = "A Name= of an XML node, or the defName of a def. Both are looked up. Several names " +
                       "print one block each, in the order given; a name that matches nothing is reported " +
                       "in a note and the others still print.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("children"),
            new OptionSpec
            {
                // 与 get/fields 同名同义:这个开关的「限定到含该文本的字段路径」这一半,
                // R11 的 12 份识别测全对。错的是另一半 —— 它拿这个筛选去做什么(横向数
                // 兄弟 def,不是纵向追溯本 def 的来源)。那一半是操作本身要重新设计,
                // 不在本批,所以这里只跟着改名。
                Name = "path-contains",
                // path 收进别名的理由在 get 那一处的注释里(真实调用 112 次全打空);
                // 同一处也记着为什么删掉 field / fieldPath / path-filter / field-contains
                // (三条命令上逐字各 0 次)。`values` 不跟着收:那 15 次伸手全打在 get 上,
                // 这里逐字 0 次,按同一条判据不该凭对称加。
                Aliases = ["filter", "grep", "path"],
                Placeholder = "<text>",
                Help = "Count, for every layer in the chain, the other defs descending from it that carry a " +
                       "field path containing this text, and how many of those carry the same value. This is a " +
                       "witness count, not a record of where the field was declared — the snapshot holds no such " +
                       "record, and the output below the table says what the count does and does not settle. " +
                       // 不在这里补一句 `[]` —— 上面那句是一条完整的等同(与 get 同一种匹配),
                       // 再点名其中一项等于把全称说成部分。产地在 CommonOptions.AnyIndexNote。
                       // 「word」换成「text」:上面那句是一条完整的等同,而 `[]` 形状
                       // (comps[].props.energyMax)不是一个 word —— 措辞把成立的全称
                       // 窄化成「一个词」,只读这一屏的人会把印出来的形状改写成 [0]。
                       // 不在这里补一句「也包括 []」:那才是把全称说成部分。
                       "Matching is the substring match 'get --path-contains' uses, so the same " +
                       "text selects the same fields in both commands.",
            },
            CommonOptions.ExactPathFilter() with { Arity = Arity.Single },
        ],
        Examples =
        [
            "rimsearcher inherit BaseBullet",
            "rimsearcher inherit Bullet_Revolver",
            "rimsearcher inherit BaseHumanlike",
            "rimsearcher inherit Bullet_Revolver --path-contains damageAmountBase",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "nodes",
                Rows = true,
                What = "one object per XML node answering to the names — each with 'node' (identity and patch " +
                       // 本轮把 --exact-path 补到 inherit 之后这句就少说了一个开关 ——
                       // 两个都出见证表。只写 --path-contains 的话,要整条路径的读者会
                       // 以为那个键拿不到,改敲子串。
                       "count), 'ancestors', 'children' when it has any, and 'witnesses' when --path-contains " +
                       "or --exact-path is given. " +
                       "With several names the objects come in the order the names were given; a name that " +
                       "matched nothing has no object here and one note in 'notes' that quotes it.",
            },
        ],
    };

    /// <summary>
    /// 一个名字在继承层落空。三种互斥成因各说各的 —— 名字错了 / 这个 def 不参与继承 /
    /// 它根本不在快照里,报成同一句「没有」会让前两种被读成第三种。
    ///
    /// 只有单名调用走这里 —— 多名那一路只报一句名单。
    /// </summary>
    private static int Miss(CommandContext ctx, string name)
    {
        var close = Suggestion.Closest(ctx.Db.AllXmlNodeNames(), name);

        // 三种互斥成因分清楚:名字错了 / 这个 def 不参与继承 / 它根本不在快照里。
        // 报成同一句「没有」会让前两种被读成第三种,而第三种是最强的那个结论。
        var isDef = ctx.Db.GetDefsNamed(name).Count > 0;
        if (isDef)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{name}' is a def in this snapshot but its XML declares no Name=, ParentName= or " +
                "Abstract=, so it takes part in no inheritance. 'rimsearcher get " + name + "' shows it.");
            // 「有没有 mod 用 PatchOperation 改过这个 def」是另一个问题:本命令别处报的
            // patch 计数只盖得住声明了 Name= 的节点。不说破,这一格空着而看起来像填了。
            ctx.Report.Notice(NoticeKind.Boundary,
                "Whether a PatchOperation edits it is a separate question this snapshot does not answer: " +
                "the patch counts reported elsewhere here cover only nodes that declare Name=.");
            return 1;
        }

        // 第三种成因:别的已注册快照里有没有它、它是不是个 def 类型 / class / mod,
        // 都是当场问得出的。
        var sighting = NameLookup.Locate(ctx, name);
        ctx.Report.Notice(NoticeKind.NextStep,
            $"No XML node named '{name}' is in this snapshot." +
            Suggestion.Say(close) +
            // 继承层只装带 Name=/ParentName=/Abstract= 的节点,**普通 def 不在里面** ——
            // 这是敲 inherit 落空最常见的成因。
            (sighting is null
                ? " Only nodes that declare Name=, ParentName= or Abstract= are in this layer, so a def " +
                  "that inherits from nothing never shows up here: 'rimsearcher get " + name + "' looks it " +
                  "up as a def, and 'rimsearcher search' matches on labels and translations too."
                : ""));
        if (sighting is not null) ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
        return 1;
    }

    public override int Run(CommandContext ctx)
    {
        // 同一个名字给两遍只查一遍 —— 两块逐字相同,而第二块会被读成另一个同名节点。
        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in ctx.Args.Positionals)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"Given more than once, printed once: {NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}.");

        var single = names.Count == 1;
        var found = new List<(string Name, IReadOnlyList<XmlNodeRow> Nodes)>();
        var missing = new List<string>();
        foreach (var n in names)
        {
            var hit = ctx.Db.NodesNamed(n);
            if (hit.Count == 0) { missing.Add(n); if (single) return Miss(ctx, n); continue; }
            found.Add((n, hit));
        }
        var nodes = found.SelectMany(f => f.Nodes).ToList();

        if (nodes.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No XML node answers to any of these: {NameList.Render(missing, Limits.MaxSuggestions)}. " +
                "Only nodes that declare Name=, ParentName= or Abstract= are in this layer, so a def that " +
                "inherits from nothing never shows up here; 'rimsearcher get <defName>' looks one up as a def.");
            foreach (var n in missing)
                if (NameLookup.Locate(ctx, n) is { } sighting)
                    ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
            return 1;
        }

        if (missing.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No XML node answers to {NameList.Render(missing, Limits.MaxSuggestions)}, so nothing below " +
                "is about it. The blocks that did come back are unaffected.");
            // 落空的名字里那些**在快照里另有落点**的,各自说一句。`inherit ThingDef
            // Bullet_Revolver` 里的 `ThingDef` 落在名字格上,是个查不到的名字,而「查不到」
            // 与「你把类型写在了名字格上」是两件事。库在这一层,判得出来。
            foreach (var n in missing)
                if (NameLookup.Locate(ctx, n) is { } sighting)
                    ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
        }

        var limit = ctx.Limit();
        var (pathFilters, exactPath) = ctx.Args.PathFilters();
        var pathFilter = pathFilters.Count > 0 ? pathFilters[0] : null;

        foreach (var node in nodes)
        {
            ctx.Report.Item("nodes");

            var named = node.Name is { Length: > 0 };
            var label = named ? node.Name : node.DefName ?? "";
            var countedExtra = ctx.Db.Meta.IndexesPatchOpsByDefNameLabel;
            var identity = new List<KeyValuePair<string, object?>>
            {
                new("name", node.Name),
                new("def_name", node.DefName),
                new("def_type", node.DefType),
                new("abstract", node.Abstract),
                new("inherits_from", node.ParentName),
                new("mod", node.SourceMod),
                new("source", node.SourceFile),
                // 数字每次都在场且可机读,于是 0 = 「量过、没人 patch」,与「这一格没量」分得开:
                // 导出器对无 Name= 的节点硬写 0(计数正则只认 `@Name=`),所以那种情况印 n/a。
                // 印 n/a 而不是留空 —— 留空会让整行在文本面消失(Renderers 跳过空值)。
                new("patch_ops", named ? node.PatchOps : "n/a"),
            };
            if (countedExtra)
            {
                identity.Add(new("patch_ops_defname", node.PatchOpsDefName));
                identity.Add(new("patch_ops_label", node.PatchOpsLabel));
            }
            ctx.Report.Detail("node", identity);

            // 紧跟着 identity 块 —— 这两句解释的就是它上面那一行 patch_ops。此前它们排在
            // 全部块之后,离所解释的那一格隔着两张表。
            //
            // 逐条申报,不是一句总的免责声明。计数恒在 identity 块的 patch_ops 上,
            // 三支各说一件不同的事,**包括 0 那一支**:此前这里写着「0 就是游戏读到的原样,
            // 不需要解释」,而 Human 是这句的反例 —— 它声明了 Name=、patch_ops 是 0,
            // 同时被 HAR 换掉了 class、插进两个 comp,那些补丁按 defName 定位,不点 Name=。
            // 一个沉默的 0 于是断言了一件假事,而这一支覆盖的是 named 节点里的多数。
            //
            // 说破它省不掉下游那次双快照 diff —— 「这个 def 被 patch 改过没有」在单快照里
            // 本就不可判定(存的是 pre-patch 的节点身份 + post-patch 的字段值,没有 pre-patch
            // 的值可比)。省掉的是另一件事:不必先把 0 当答案用一遍,再从别处撞见反证。
            var oldLayer = countedExtra
                ? ""
                : $" This snapshot (exporter {ctx.Db.Meta.ExporterVersion}) only counted @Name=; " +
                  "re-export to also count xpaths by defName= and by label=.";
            if (!named)
                ctx.Report.Notice(NoticeKind.Boundary,
                    countedExtra
                        ? $"'{label}' declares no Name=, so patch_ops is not measured for it. " +
                          "patch_ops_defname and patch_ops_label above count xpaths that name it by defName= " +
                          "and by label=; an xpath that reaches it by thingClass or by a wildcard still " +
                          "leaves no trace here."
                        : $"'{label}' declares no Name=, so patch_ops is not measured for it: only xpaths naming a " +
                          "node with @Name= are counted, and a patch that reaches this def by defName leaves no " +
                          "trace here." + oldLayer);
            else if (node.PatchOps > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{label}' is targeted by name by " +
                    $"{Tally.Complete(node.PatchOps).Render("patch operation")} in this snapshot. " +
                    "This layer is the XML before patches, so what the game finally used differs from it " +
                    "by whatever those operations did." + oldLayer);
            else if (countedExtra)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No patch operation's xpath names '{label}' with @Name= in this snapshot — that is " +
                    "what patch_ops=0 counts. patch_ops_defname and patch_ops_label count xpaths that name " +
                    "it by defName= and by label=. An xpath that reaches it by thingClass or by a wildcard " +
                    // 「so a 0 is not evidence that the game read this node unpatched」2026-09-18 删掉:
                    // 0 数的是什么、什么留不下痕迹,上面两句已经是全部机制(Docs/23 第八节)。
                    "leaves no trace here.");
            else
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No patch operation's xpath names '{label}' with @Name= in this snapshot — that is " +
                    "what the 0 counts. An xpath that reaches it any other way — by defName, by label, " +
                    "by thingClass, by a wildcard — leaves no trace here." + oldLayer);

            // 往上走到根。带环保护是必要的:XML 里写得出环,游戏在这一层之后才检出来,
            // 快照存的正是检出之前的原文。
            var chain = new List<XmlNodeRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? unresolved = null;
            var cursor = node.ParentName;
            while (cursor is { Length: > 0 })
            {
                if (!seen.Add(cursor)) break;
                var up = ctx.Db.NodesNamed(cursor)
                               .FirstOrDefault(n => string.Equals(n.Name, cursor, StringComparison.OrdinalIgnoreCase));
                if (up is null) { unresolved = cursor; break; }
                chain.Add(up);
                cursor = up.ParentName;
            }

            // 这条链不受 --limit 管(往上走到根就是全部),所以计数恒为完整式。它一直缺着 ——
            // 此前渲染器把全部声明提到最前,子节点那条计数顶在第一行,看着就像整份输出都有数了。
            if (chain.Count > 0)
                ctx.Report.CountNotice(Tally.Complete(chain.Count), "ancestor", "");

            // 祖先被点名 = 这个节点从它继承来的字段跟着变,而 identity 块那一格看不见这件事:
            // 它只数点名本节点自己的 xpath。实测里 BaseMechanoid 自己 0、父 BasePawn 是 1
            // (全部机械族的 comps 都被那条改了),六个受测者里四个就此答「节点自身未被改」——
            // 字面不错,漏掉的是真正生效的那条。这些数在上面那趟往上走里已经查出来了。
            //
            // 列按 list --class 的先例条件化,但理由不是省地方:全零时渲染器会把它折进
            // 「Same in every row」那句、印成 patch_ops=0,而那正是这条命令改了三轮的形态 ——
            // 一个沉默的 0 断言假事。条件化之后沉默只发生在祖先侧确实没有已知 patch 时。
            var patchedUp = chain.Where(n => n.PatchOps > 0).ToList();
            if (chain.Count > 0)
                ctx.Report.Table("ancestors",
                    patchedUp.Count > 0
                        ? ["name", "def_type", "abstract", "patch_ops", "mod", "source"]
                        : ["name", "def_type", "abstract", "mod", "source"],
                    chain.Select(n =>
                    {
                        var row = new Dictionary<string, object?>
                        {
                            ["name"] = n.Name,
                            ["def_type"] = n.DefType,
                            ["abstract"] = n.Abstract,
                            ["mod"] = n.SourceMod,
                            ["source"] = n.SourceFile,
                        };
                        if (patchedUp.Count > 0) row["patch_ops"] = n.PatchOps;
                        return (IReadOnlyDictionary<string, object?>)row;
                    }).ToList());

            // 带上数字才省得掉「再往上跑一次 inherit」那个动作;只说「祖先里有被改的」
            // 等于把活推回去。计数放句尾,免得动词跟着单复数变 —— NounRegistry 不管动词。
            if (patchedUp.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"A def inherits its ancestors' fields, so a patch that rewrites an ancestor changes " +
                    $"what the game read for '{label}' as well — and the patch_ops on '{label}' itself " +
                    $"does not count that. Above, patch_ops is not zero for " +
                    $"{Tally.Complete(patchedUp.Count).Render("ancestor")}.");

            // 断链要说破:ParentName 指着一个本快照里没有的名字,意思是那个 mod 没启用,
            // 而不是「到根了」。两者在表格上长得一模一样。
            if (unresolved is not null)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The chain stops at '{unresolved}', which no mod in this snapshot declares. " +
                    "The mod that defines it was not enabled when the snapshot was taken, so what it " +
                    "contributed is not visible here.");

            if (pathFilter is { Length: > 0 }) Witnesses(ctx, node, chain, pathFilter, exactPath);

            if (node.Name is { Length: > 0 })
            {
                var children = ctx.Db.NodesInheritingFrom(node.Name);
                var shown = limit.IsAll ? children : children.Take(limit.Effective).ToList();

                // 计数在它数的那张表**上方** —— 数得清几条、全不全,读到行的时候得已经知道。
                ctx.Report.CountNotice(Tally.Of(shown.Count, children.Count), "direct child");

                if (shown.Count > 0)
                    ctx.Report.Table("children", ["name", "def_name", "def_type", "abstract", "mod"],
                        shown.Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["name"] = n.Name,
                            ["def_name"] = n.DefName,
                            ["def_type"] = n.DefType,
                            ["abstract"] = n.Abstract,
                            ["mod"] = n.SourceMod,
                        }).ToList());

                // 抽象节点没有自己的字段表:它写的每一条都已合并进每个子节点,且那一份是
                // patch 之后的。
                if (node.Abstract && children.Count > 0)
                {
                    var concrete = children.FirstOrDefault(c => !c.Abstract && c.DefName is { Length: > 0 });
                    if (concrete is not null)
                        ctx.Report.Notice(NoticeKind.NextStep,
                            $"An abstract node has no fields of its own in a snapshot. Everything it declares is " +
                            $"already merged, post-patch, into each child: 'rimsearcher get {concrete.DefName}'.");
                }
            }
        }

        ctx.Report.EndItems();

        // 「几个节点答应同一个名字」是按**名字**说的话。几个名字一起给时,块总数大于 1
        // 是理所当然的,而它与「这一个名字底下有两个节点」是两件事 —— 合起来数会把前者
        // 报成后者,而后者才是读的人要当心的那个(两块看着像重复,其实是不同节点)。
        foreach (var (askedFor, hit) in found.Where(f => f.Nodes.Count > 1))
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(hit.Count).Render("XML node")} answer to '{askedFor}'; all of them are shown.");

        // 这个数与 `get` 那边的**必然**对不上,而两条命令都用绝对语气报自己那个 —— 差额
        // 不解释的话,读的人只能自己编一个理由(盲测里编的是「那几个是 code-generated」,
        // 而它们明明来自具名 XML 文件,于是那个解释当场被自己推翻)。
        // 差额的真实成因只有一个:这一层只装声明了 Name= / ParentName= / Abstract= 的节点。
        foreach (var (askedFor, hit) in found)
        {
            var sameName = ctx.Db.GetDefsNamed(askedFor);
            var inLayer = hit.Count(n => string.Equals(n.DefName, askedFor, StringComparison.OrdinalIgnoreCase));
            var outside = sameName.Count - inLayer;
            if (outside == 0) continue;

            var types = sameName
                .Where(d => !hit.Any(n => string.Equals(n.DefName, askedFor, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(n.DefType, d.DefType, StringComparison.OrdinalIgnoreCase)))
                .Select(d => d.DefType)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // 两边的数都按**这一个名字**取:拿本次印出来的块总数去比,几个名字一起给时
            // 那两个数会各自跨名字相加,而句子说的是同一个名字下的差额。
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Outside this layer: {Tally.Complete(outside).Render("def")} also named '{askedFor}', declaring no " +
                $"Name=, ParentName= or Abstract= ({NameList.Render(types, Limits.MaxSuggestions)}). An ordinary " +
                $"def takes part in no inheritance, which is why 'rimsearcher get {askedFor}' counts " +
                $"{sameName.Count} where this command counts {hit.Count}.");
        }

        return 0;
    }

    /// <summary>
    /// 「这个值是哪一层写的」—— 证人兄弟法。
    ///
    /// 快照里没有「哪一层声明了它」这条事实,但它推得出来:某一层若真声明了这个字段,
    /// 它的后代应当**都**带着;后代里有一条不带,那一层就没声明。这里出数,不下结论 ——
    /// 这条推论有洞(子节点可以覆写,导出时字段表可能被截),洞逐条说破,判断权留在外面。
    /// </summary>
    private static void Witnesses(CommandContext ctx, XmlNodeRow node,
                                  IReadOnlyList<XmlNodeRow> chain, string pathFilter, bool exactPath)
    {
        // 「含这段文本」在 --exact-path 那一档是假的:那一档比的是整条。同一句话
        // 两档共用时,说错的是判据本身,而读者拿它当口径去解释下面每一个数。
        var holding = exactPath ? "exactly at" : "containing";

        // 只有具名节点才有「后代」这回事 —— 无 Name= 的节点谁也继承不了它。
        var layers = new List<XmlNodeRow>();
        if (node.Name is { Length: > 0 }) layers.Add(node);
        layers.AddRange(chain.Where(n => n.Name is { Length: > 0 }));

        if (layers.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{node.DefName ?? node.Name}' declares no Name= and inherits from no node that does, so there " +
                "is no layer above it to test: whatever it carries, it carries by itself.");
            return;
        }

        // 参照值 —— 问的那个 def 自己在这条路径上装着什么。没有参照值时 same_value 这一列
        // 整个不出:印一列恒为 0 的数比不印更坏,它看起来像「一个兄弟都不同意」。
        string? reference = null;
        (string DefName, string DefType)? exclude = null;
        if (node.DefName is { Length: > 0 })
        {
            var self = ctx.Db.GetDefsNamed(node.DefName)
                          .FirstOrDefault(d => string.Equals(d.DefType, node.DefType, StringComparison.OrdinalIgnoreCase));
            if (self is not null)
            {
                exclude = (self.DefName, self.DefType);
                var fields = ctx.Db.Fields(self.Id, int.MaxValue, [pathFilter], exactPath: exactPath);
                var values = fields.Rows.Select(r => r.Value ?? "").Distinct(StringComparer.Ordinal).ToList();

                if (values.Count == 1)
                {
                    reference = values[0];
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"'{self.DefName}' carries {Tally.Complete(fields.Rows.Count).Render("field")} matching " +
                        $"'{pathFilter}', all reading {Quote(reference)} — that is the value the counts below " +
                        "are compared against.");
                }
                else if (values.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"'{self.DefName}' itself carries no field path {holding} '{pathFilter}', so there is no " +
                        "value of its own to compare against; the counts below say which layers' descendants " +
                        "carry one at all.");
                }
                else
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"'{self.DefName}' carries {Tally.Complete(values.Count).Render("value")} matching " +
                        $"'{pathFilter}' ({NameList.Render(values.Select(Quote).ToList(), 4)}), so there is no " +
                        "single value to compare against. Narrow the path filter until one is left.");
                }
            }
        }

        // 抽象节点自己没有值,而那一列此前就整个不出 —— 于是 `165 of 165` 看着像铁证,
        // 实际上「这一层声明了它」与「三千个 def 各写各的」在数上无法分辨,唯一分得开的
        // 那一列不在场。参照值改从子树的众数取:占满就是共享,散开就是各写各的。
        //
        // 口径与「问的那个 def 自己装着什么」不同,所以表头必须说破参照值是哪儿来的 ——
        // 两种口径下这一列都读作「有几个后代读到参照值」,变的是参照值的产地。
        var byMode = false;
        if (reference is null && exclude is null && node.Name is { Length: > 0 })
        {
            var dom = ctx.Db.DominantValue(node.Name, pathFilter, exactPath);
            if (dom.Value is not null)
            {
                reference = dom.Value;
                byMode = true;
                ctx.Report.Notice(NoticeKind.Filter,
                    $"'{node.Name}' is a node, not a def, so it carries no value of its own. The counts below " +
                    $"are compared against the most common value under it instead: {Quote(dom.Value)}, on " +
                    $"{Tally.Complete(dom.Defs).Render("def")}" +
                    (dom.Distinct > 1
                        ? $", out of {Tally.Complete(dom.Distinct).Render("value")} that appear there."
                        : " — the only value that appears there."));
            }
        }

        // 列名自陈参照值的产地(Docs/25 丁2):问的 def 自己的值 → same_value,子树众数 → same_as_mode。
        // 此前两种口径共用一个列名,靠一句脚注说破「这一列比的是众数,节点自己什么都没声明」。
        var sameColumn = byMode ? SameAsMode : SameValue;
        var columns = reference is null
            ? new List<string> { "layer", "other_defs", "with_path" }
            : ["layer", "other_defs", "with_path", sameColumn];

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = 0;
        foreach (var layer in layers)
        {
            var w = ctx.Db.Witnesses(layer.Name!, pathFilter, reference, exclude, exactPath);
            truncated = Math.Max(truncated, w.Truncated);
            var row = new Dictionary<string, object?>
            {
                ["layer"] = layer.Name,
                ["other_defs"] = w.Descendants,
                ["with_path"] = w.WithPath,
            };
            if (reference is not null) row[sameColumn] = w.SameValue;
            rows.Add(row);
        }

        ctx.Report.Table("witnesses", columns, rows);

        // 数怎么读,说一次。不说的话这张表就是三四列没有单位的整数。
        ctx.Report.Notice(NoticeKind.NextStep,
            $"Each row counts the other defs descending from that layer: how many carry a field path {holding} " +
            $"'{pathFilter}'" + (reference is null ? "" : ", and how many of those read the same value") +
            ". A layer that declares a field passes it to every descendant, so a layer whose with_path falls " +
            "short of other_defs is not the one declaring this field, and one that reaches it may still not " +
            "be: every descendant writing the field separately counts the same. " +
            "The snapshot stores no 'declared here' " +
            "fact — the game resolves inheritance while loading and then discards it.");

        // 逆命题那半句已经并进上一条(「追平不能反推」),这里只剩「靠哪一列分」。参照值定不下来
        // 那一支不再说「那一列不在表里」—— 表里没有它,而为什么没有,上面挑参照值的那句已说。
        // 众数口径也不再补半句:列名 same_as_mode 自己说了,挑参照值那句说了众数是多少。
        if (reference is not null)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The {sameColumn} column is what tells the two apart — one shared value points at the layer, " +
                "a spread of values points at each def writing its own.");

        ctx.Report.Notice(NoticeKind.Boundary,
            "A descendant that overrides the field still counts in with_path" +
            (reference is null ? "" : $" but not in {sameColumn}") + ", so the columns differing means overriding, " +
            "not absence. And field values here are the merged, post-patch ones, so a PatchOperation that added " +
            "this field to many defs is indistinguishable from a layer declaring it.");

        // with_path 追平 other_defs,对一个**整个类型都带**的字段是恒真的 —— 而恒真的东西
        // 长得与铁证一模一样。第九轮盲测的落点:`inherit BasePawn --path-contains tickerType` 回
        // 165 of 165,而全快照三千多个 ThingDef 每一个都带这条路径。分母摆出来,那 165
        // 才有得比。只在真追平了的时候说 —— 没追平的表本来就没在暗示什么。
        if (node.DefType is { Length: > 0 } &&
            rows.Any(r => r["with_path"] is int w && w > 0 && Equals(r["other_defs"], r["with_path"])))
        {
            var wide = ctx.Db.TypeDefsWithPath(node.DefType, [pathFilter], exactPath);
            var all = ctx.Db.CountDefsOfType(node.DefType,
                Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            if (wide.Defs > 0 && all > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The denominator for a full row: across the whole snapshot, {wide.Defs} of the {all} " +
                    $"{node.DefType}s carry a path {holding} '{pathFilter}', layer or no layer. A row where " +
                    "with_path equals other_defs is evidence only to the extent that fraction is smaller.");
        }

        // 导出时被截字段表的 def 会「没有这条路径」而其实有 —— 那正好是让一层被误判成
        // 「没声明」的方向。数的是分母里的那些,不是整库:整库的数恒为非零,恒真的
        // 免责声明会被学着跳过。
        if (truncated > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(truncated).Render("def")} counted in other_defs had the field list cut short " +
                "at export, so any of those can miss with_path for that reason alone.");
    }

    private static string Quote(string v) => v.Length == 0 ? "an empty value" : $"'{v}'";
}
