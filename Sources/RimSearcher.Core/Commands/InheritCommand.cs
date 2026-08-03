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
    public override CommandSpec Spec => new()
    {
        Name = "inherit",
        Aliases = ["inheritance", "parent", "parents", "children", "tree"],
        Summary = "Show what an XML node inherits from and what inherits from it, including abstract parents.",
        Remarks =
            "This is the one part of a snapshot that is read from the mods' XML rather than from the objects the " +
            "game had in memory, because the game resolves inheritance while loading and then discards it. " +
            "Abstract parents exist only here: they never become defs, so 'get' will not find them.\n\n" +
            "What is shown is the XML before PatchOperations are applied. Each node that declares Name= reports how " +
            "many patch operations target it by name, so 0 there means what you see is exactly what the game read. " +
            "A node without a Name= reports 'n/a' rather than 0: patches that reach a def by defName are counted " +
            "nowhere in this layer, so for those defs the question stays unanswered. " +
            "For the merged, post-patch values, read any concrete child with 'get' — everything a parent " +
            "contributes is already in each of its children.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "name",
                Help = "A Name= of an XML node, or the defName of a def. Both are looked up.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("children"),
            new OptionSpec
            {
                Name = "path",
                Aliases = ["field", "fieldPath"],
                Placeholder = "<text>",
                Help = "Ask which layer a field comes from. For every layer in the chain, count the other defs " +
                       "descending from it that carry a field path containing this text, and how many of those " +
                       "carry the same value. Matching is the substring match 'get --path' uses, so the same " +
                       "word selects the same fields in both commands.",
            },
        ],
        Examples =
        [
            "rimsearcher inherit BaseBullet",
            "rimsearcher inherit Bullet_Revolver",
            "rimsearcher inherit BaseHumanlike --limit all",
            "rimsearcher inherit Bullet_Revolver --path damageAmountBase",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "nodes",
                Rows = true,
                What = "one object per XML node answering to the name — each with 'node' (identity and patch " +
                       "count), 'ancestors', 'children' when it has any, and 'witnesses' when --path is given.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;

        var nodes = ctx.Db.NodesNamed(name);
        if (nodes.Count == 0)
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

        var limit = ctx.Limit();
        var pathFilter = ctx.Args.Value("path");

        foreach (var node in nodes)
        {
            ctx.Report.Item("nodes");

            var named = node.Name is { Length: > 0 };
            var label = named ? node.Name : node.DefName ?? "";
            ctx.Report.Detail("node",
            [
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
                // 需要警示的后果由下面那句边界话说,只在非零时出现。
                new("patch_ops", named ? node.PatchOps : "n/a"),
            ]);

            // 紧跟着 identity 块 —— 这两句解释的就是它上面那一行 patch_ops。此前它们排在
            // 全部块之后,离所解释的那一格隔着两张表。
            //
            // 逐条申报,不是一句总的免责声明。计数恒在 identity 块的 patch_ops 上,
            // 这里只在非零时补说后果 —— 0 就是游戏读到的原样,不需要解释。
            if (!named)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{label}' declares no Name=, so patch_ops is not measured for it: only xpaths naming a " +
                    "node with @Name= are counted, and a patch that reaches this def by defName leaves no " +
                    "trace here.");
            else if (node.PatchOps > 0)
                // 主语放到句尾,免得动词跟着计数变单复数 —— NounRegistry 管名词,不管动词。
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{label}' is targeted by name by " +
                    $"{Tally.Complete(node.PatchOps).Render("patch operation")} in this snapshot. " +
                    "This layer is the XML before patches, so what the game finally used differs from it " +
                    "by whatever those operations did.");

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

            if (chain.Count > 0)
                ctx.Report.Table("ancestors", ["name", "def_type", "abstract", "mod", "source"],
                    chain.Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["name"] = n.Name,
                        ["def_type"] = n.DefType,
                        ["abstract"] = n.Abstract,
                        ["mod"] = n.SourceMod,
                        ["source"] = n.SourceFile,
                    }).ToList());

            // 断链要说破:ParentName 指着一个本快照里没有的名字,意思是那个 mod 没启用,
            // 而不是「到根了」。两者在表格上长得一模一样。
            if (unresolved is not null)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The chain stops at '{unresolved}', which no mod in this snapshot declares. " +
                    "The mod that defines it was not enabled when the snapshot was taken, so what it " +
                    "contributed is not visible here.");

            if (pathFilter is { Length: > 0 }) Witnesses(ctx, node, chain, pathFilter);

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

        if (nodes.Count > 1)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(nodes.Count).Render("XML node")} answer to '{name}'; all of them are shown.");

        // 这个数与 `get` 那边的**必然**对不上,而两条命令都用绝对语气报自己那个 —— 差额
        // 不解释的话,读的人只能自己编一个理由(盲测里编的是「那几个是 code-generated」,
        // 而它们明明来自具名 XML 文件,于是那个解释当场被自己推翻)。
        // 差额的真实成因只有一个:这一层只装声明了 Name= / ParentName= / Abstract= 的节点。
        var sameName = ctx.Db.GetDefsNamed(name);
        var inLayer = nodes.Count(n => string.Equals(n.DefName, name, StringComparison.OrdinalIgnoreCase));
        var outside = sameName.Count - inLayer;
        if (outside > 0)
        {
            var types = sameName
                .Where(d => !nodes.Any(n => string.Equals(n.DefName, name, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(n.DefType, d.DefType, StringComparison.OrdinalIgnoreCase)))
                .Select(d => d.DefType)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Outside this layer: {Tally.Complete(outside).Render("def")} also named '{name}', declaring no " +
                $"Name=, ParentName= or Abstract= ({NameList.Render(types, Limits.MaxSuggestions)}). An ordinary " +
                $"def takes part in no inheritance, which is why 'rimsearcher get {name}' counts " +
                $"{sameName.Count} where this command counts {nodes.Count}.");
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
                                  IReadOnlyList<XmlNodeRow> chain, string pathFilter)
    {
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
                var fields = ctx.Db.Fields(self.Id, int.MaxValue, [pathFilter]);
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
                        $"'{self.DefName}' itself carries no field path containing '{pathFilter}', so there is no " +
                        "value of its own to compare against; the counts below say which layers' descendants " +
                        "carry one at all.");
                }
                else
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"'{self.DefName}' carries {Tally.Complete(values.Count).Render("value")} matching " +
                        $"'{pathFilter}' ({NameList.Render(values.Select(Quote).ToList(), 4)}), so there is no " +
                        "single value to compare against. Narrow --path until one is left.");
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
            var dom = ctx.Db.DominantValue(node.Name, pathFilter);
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

        var columns = reference is null
            ? new List<string> { "layer", "other_defs", "with_path" }
            : ["layer", "other_defs", "with_path", "same_value"];

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = 0;
        foreach (var layer in layers)
        {
            var w = ctx.Db.Witnesses(layer.Name!, pathFilter, reference, exclude);
            truncated = Math.Max(truncated, w.Truncated);
            var row = new Dictionary<string, object?>
            {
                ["layer"] = layer.Name,
                ["other_defs"] = w.Descendants,
                ["with_path"] = w.WithPath,
            };
            if (reference is not null) row["same_value"] = w.SameValue;
            rows.Add(row);
        }

        ctx.Report.Table("witnesses", columns, rows);

        // 数怎么读,说一次。不说的话这张表就是三四列没有单位的整数。
        ctx.Report.Notice(NoticeKind.NextStep,
            $"Each row counts the other defs descending from that layer: how many carry a field path containing " +
            $"'{pathFilter}'" + (reference is null ? "" : ", and how many of those read the same value") +
            ". A layer that declares a field passes it to every descendant, so a layer whose with_path falls " +
            "short of other_defs is not the one declaring this field. The snapshot stores no 'declared here' " +
            "fact — the game resolves inheritance while loading and then discards it — so these counts are what " +
            "the answer has to be read off.");

        // 逆命题不成立,而这张表长得很像在给逆命题作证:「61 of 61」与「每个后代各写各的一份」
        // 在数上无法分辨。
        ctx.Report.Notice(NoticeKind.Boundary,
            "The converse does not hold: with_path reaching other_defs is equally consistent with every " +
            "descendant writing the field separately" +
            (reference is null
                ? ", which no count here tells apart. No single value could be fixed to compare against, so " +
                  "the same_value column — the one that does tell them apart — is not in this table."
                : ". The same_value column is what tells the two apart — one shared value points at the layer, " +
                  "a spread of values points at each def writing its own." +
                  (byMode
                      ? " It is the most common value under this node, not one the node declares: the node " +
                        "declares nothing, and a majority is what stands in for it."
                      : "")));

        ctx.Report.Notice(NoticeKind.Boundary,
            "A descendant that overrides the field still counts in with_path" +
            (reference is null ? "" : " but not in same_value") + ", so the columns differing means overriding, " +
            "not absence. And field values here are the merged, post-patch ones, so a PatchOperation that added " +
            "this field to many defs is indistinguishable from a layer declaring it.");

        // with_path 追平 other_defs,对一个**整个类型都带**的字段是恒真的 —— 而恒真的东西
        // 长得与铁证一模一样。第九轮盲测的落点:`inherit BasePawn --path tickerType` 回
        // 165 of 165,而全快照三千多个 ThingDef 每一个都带这条路径。分母摆出来,那 165
        // 才有得比。只在真追平了的时候说 —— 没追平的表本来就没在暗示什么。
        if (node.DefType is { Length: > 0 } &&
            rows.Any(r => r["with_path"] is int w && w > 0 && Equals(r["other_defs"], r["with_path"])))
        {
            var wide = ctx.Db.TypeDefsWithPath(node.DefType, [pathFilter]);
            var all = ctx.Db.CountDefsOfType(node.DefType,
                Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            if (wide.Defs > 0 && all > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The denominator for a full row: across the whole snapshot, {wide.Defs} of the {all} " +
                    $"{node.DefType}s carry a path containing '{pathFilter}', layer or no layer. A row where " +
                    "with_path equals other_defs is evidence only to the extent that fraction is smaller.");
        }

        // 导出时被截字段表的 def 会「没有这条路径」而其实有 —— 那正好是让一层被误判成
        // 「没声明」的方向。数的是分母里的那些,不是整库:整库的数恒为非零,恒真的
        // 免责声明会被学着跳过。
        if (truncated > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(truncated).Render("def")} counted in other_defs had the field list cut short " +
                "at export, so any of those can miss with_path for that reason alone — the direction that makes " +
                "a layer look innocent.");
    }

    private static string Quote(string v) => v.Length == 0 ? "an empty value" : $"'{v}'";
}
