using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 继承层的出口。
///
/// 这是快照里唯一**不是**「游戏内存里的对象」的一层,而它非存不可的理由恰恰是别处存不下:
/// 游戏在 <c>LoadAllActiveMods</c> 末尾就 <c>XmlInheritance.Clear()</c>,导出跑在
/// <c>StaticConstructorOnStartup</c>,那时「谁继承谁」已经应用完并丢弃;抽象父节点更是
/// 从头到尾没有 Def 实例。所以 <c>get</c> 永远找不到 BaseBullet 不是工具的缺陷,
/// 是它问错了层 —— 本命令就是那另一层。
///
/// 边界写在每一条结果上而不是挂一段总的免责声明:这一层是**打补丁之前**的 XML,
/// 而每个具名节点带着「有多少条 xpath 点了它的名」,读的人自己判断这一条准不准。
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
            "What is shown is the XML before PatchOperations are applied. Each named node reports how many patch " +
            "operations target it by name, so a node with 0 of them is exactly what the game read. " +
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
        Options = [CommonOptions.Limit("children")],
        Examples =
        [
            "rimsearcher inherit BaseBullet",
            "rimsearcher inherit Bullet_Revolver",
            "rimsearcher inherit BaseHumanlike --limit all",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "nodes",
                What = "one object per XML node answering to the name — each with 'node' (identity and patch " +
                       "count), 'ancestors' and, when it has any, 'children'.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;

        var nodes = ctx.Db.NodesNamed(name);
        if (nodes.Count == 0)
        {
            var close = FuzzyMatcher.Rank(ctx.Db.AllXmlNodeNames(), name)
                                    .Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            // 三种互斥成因,分清楚再说。名字错了 / 这个 def 不参与继承 / 它根本不在快照里 ——
            // 报成同一句「没有」会让前两种被读成第三种,而第三种是最强的那个结论。
            var isDef = ctx.Db.GetDefsNamed(name).Count > 0;
            if (isDef)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{name}' is a def in this snapshot but its XML declares no Name=, ParentName= or " +
                    "Abstract=, so it takes part in no inheritance. 'rimsearcher get " + name + "' shows it.");
                return 1;
            }

            // 第三种成因原先到「本快照里没有」为止,而那正是 R10 说的可算而未算:
            // 别的已注册快照里有没有它、它是不是个 def 类型 / class / mod,都是当场问得出的。
            var sighting = NameLookup.Locate(ctx, name);
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No XML node named '{name}' is in this snapshot." +
                (close.Count > 0 ? $" Closest names: {string.Join(", ", close)}." : ""));
            if (sighting is not null) ctx.Report.Notice(NoticeKind.NextStep, sighting.Sentence);
            return 1;
        }

        var limit = ctx.Args.Limit();

        foreach (var node in nodes)
        {
            ctx.Report.Item("nodes");

            var label = node.Name is { Length: > 0 } ? node.Name : node.DefName ?? "";
            ctx.Report.Detail("node",
            [
                new("name", node.Name),
                new("def_name", node.DefName),
                new("def_type", node.DefType),
                new("abstract", node.Abstract),
                new("inherits_from", node.ParentName),
                new("mod", node.SourceMod),
                new("source", node.SourceFile),
                // R6:这个数原先只在非零时以一句散文出现,而文档承诺的是「每个具名节点都报
                // 一个数,0 就意味着你看到的正是游戏读到的」。于是「零」和「这件事没做」
                // 分不开 —— 那个承诺给出的保证在实现里根本不存在。
                // 二轮 F2(三态文法的裸 N 从未渲染出来过)是同一个形状,**这是第二次犯**。
                // 放进 identity 块而不是补一句散文:数字每次都在场且可机读,占一行;
                // 需要警示的后果仍由下面那句边界话说,只在非零时出现。
                new("patch_ops", node.PatchOps),
            ]);

            // 往上走到根。带环保护不是防御性编程 —— XML 里写出环是可能的,而游戏自己
            // 是在这一层之后才检出来的,快照存的正是检出之前的原文。
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

            if (node.Name is { Length: > 0 })
            {
                var children = ctx.Db.NodesInheritingFrom(node.Name);
                var shown = limit.IsAll ? children : children.Take(limit.Effective).ToList();
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

                ctx.Report.CountNotice(Tally.Of(shown.Count, children.Count), "direct child",
                    "pass --limit all for the rest.");

                // 抽象节点没有自己的字段表,而这不是缺陷:它写的每一条都已经合并进每个子节点,
                // 并且那一份是 patch 之后的。指路到子节点比在这里复制一份 patch 前的原文强。
                if (node.Abstract && children.Count > 0)
                {
                    var concrete = children.FirstOrDefault(c => !c.Abstract && c.DefName is { Length: > 0 });
                    if (concrete is not null)
                        ctx.Report.Notice(NoticeKind.NextStep,
                            $"An abstract node has no fields of its own in a snapshot. Everything it declares is " +
                            $"already merged, post-patch, into each child: 'rimsearcher get {concrete.DefName}'.");
                }
            }

            // 逐条申报,不是一句总的免责声明。计数本身现在恒在 identity 块的 patch_ops 上
            // (R6),这里只在非零时补说后果 —— 0 的那一条不需要解释,它就是游戏读到的原样。
            if (node.PatchOps > 0)
                // 主语放到句尾,免得动词跟着计数变单复数 —— NounRegistry 管名词,不管动词,
                // 「1 patch operation … target」这种主谓不一致靠加登记项是修不掉的。
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{label}' is targeted by name by " +
                    $"{Tally.Complete(node.PatchOps).Render("patch operation")} in this snapshot. " +
                    "This layer is the XML before patches, so what the game finally used differs from it " +
                    "by whatever those operations did.");
        }

        ctx.Report.EndItems();

        if (nodes.Count > 1)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(nodes.Count).Render("XML node")} answer to '{name}'; all of them are shown.");

        return 0;
    }
}
