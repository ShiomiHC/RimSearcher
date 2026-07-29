using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 把 InheritorsOutput 印成文本。
//
// 这一形与 ScanOutput 共用 Row 与 Footnote 两个原语，但 Header 与 Section 的形状不同：扫盘的
// 表头数的是「行」、正文分的是「文件」，继承树的表头数的是「整棵树」、正文一行一个类型。两者
// 硬合成一个 renderer 只会做出一个满是分支的空壳，故各自成形、共用原语（见指导文档 §4）。
//
// 逐条对应 InheritorsOutput 头部列的三条耦合：
//   1. Shape（域内整树）与切片深度分别由 Headline 的两个位置读，切片深度只在这里数一次；
//   2. Listed()/Fold 同判据（`HiddenCount > 0`），故「列了几个」与折叠行同生同死；
//   3. 深度图例与覆盖说明都从切片最深层派生，且各带自己的在场条件。
public static class InheritorsRenderer
{
    public static string Render(InheritorsOutput output)
        => output.Inheritors.Items.Count == 0
            ? Empty(output)
            : Listing(output);

    // 零命中形。三句话，选哪一句要同时看两件事：这个名字在索引里有没有、以及 scope 外还有没有
    // 派生类。后者的载体就是那条越界脚注，故这两个判断必须在同一处做——「这是答案」这句背书
    // 只在**真的是完整答案**时给。
    //
    // scope 外还有派生类时，背书下面跟着的是一行小字斜体的越界计数；盲测里调用方把整份返回压缩
    // 成了「没有子类」，而那个被丢掉的 1 足以让「可以安全改签名」这类结论翻车：语气最重的那句
    // 和唯一的反证放在一起，读者只会记住前者。
    private static string Empty(InheritorsOutput output)
    {
        var footer = OutOfScopeFooter(output, withTreeShape: false);

        var message = output.TypeIsIndexed
            ? footer != null
                ? $"'{output.Symbol}' is indexed, and nothing in scope '{output.Scope.Expression}' "
                  + "derives from it — but it does have subclasses outside that scope, so this is not "
                  + "the whole answer."
                : $"'{output.Symbol}' is indexed, and nothing in scope '{output.Scope.Expression}' "
                  + "derives from it (this is an answer, not a lookup failure)."
            : $"No type named '{output.Symbol}' is in the index, so this is not evidence that it has no "
              + "subclasses. Check the spelling with rimworld-searcher__locate, and note that "
              + "inheritors resolves C# type names only.";

        // 这里**不挂** ScopeNotices.RetryWider。那句「retry with scope:'all' before concluding it
        // does not exist」在本分支的三种情形下全是错的或白跑的：
        //   - 已知类型 + 有越界子类  → footer 已经逐源报了数，且上一句刚说过「这不是完整答案」；
        //   - 已知类型 + 无越界子类  → 继承闭包是全域算的，scope:'all' 一条也加不出来；
        //   - 索引里没这个名字      → IsKnownType 本就与 scope 无关，换 scope 返回逐字相同。
        // 实测第三种给出的是 "…Check the spelling… Only sources in scope 'base' were searched —
        // retry with scope:'all'"：两句语气相反，而后一句保证白跑一轮。
        return FootnoteBlock.After(message, footer, output.ScopeNotice);
    }

    private static string Listing(InheritorsOutput output)
    {
        var items = output.Inheritors.Items;

        // 列出来的类型全同源时标签只印一次；被上限截断时表头改印全树的来源构成
        // （见 SourceLabeling）。这里的截断是结构性偏置：候选按 depth 再按字母序排，
        // vanilla 的直接子类挤满前 200 条，于是「切片全是 vanilla、全树横跨五个源」是常态
        // 而非巧合。
        var labels = SourceLabeling.Of(output.Inheritors);

        var rows = items.Select(entry => Row(output, entry, labels));

        // 行内标记是上面那个 Select 打的，而它是惰性的——必须先把行文本落定，才读得到
        // Conditional 收下的键。这也正是「打标记」与「收脚注」不该分居两个方法的理由。
        var body = $"{Headline(output, labels)}\n{string.Join("\n", rows)}\n";

        // 顶到硬上限时「narrow the query」在继承树上不是个可执行动作：查询词就是那个类名，
        // 没得再窄，而这个模式既没有 offset 也没有任何参数抬得动上限。唯一的出路是从列表里挑一个
        // 子树根重跑——这个动作在 schema、Description、返回里此前一处都没写。
        //
        // 只给这一条出路时它的语气太足：第十三轮盲测里被测方据此断定「要拼出全树得对 306 个直接
        // 子类逐个盲试」，转而去跑正则补料，多花四次调用，产出一份自己都标注为「非完整」的名单。
        // 而按源切片一次就能把某个源的整棵子树完整列出来（实测 scope:'Milira' 回 41 条，含全部
        // depth 2/3/4，无折叠）。越界脚注只在窄 scope 时出现且只劝人放宽，方向恰好相反，
        // 故这条得写在这里。
        var fold = Fold.Line(
            output.Inheritors, "subclasses", indent: "", limit: output.Limit,
            capAction: "re-trace a listed type as its own root (depths then restart from it), "
                       + "or narrow scope to one source — a per-source subtree is listed in full "
                       + "whenever it fits under the cap",
            hiddenBatch: HiddenBatchOf(output, ShownDeepest(output)));

        // 这里**不要**加「limit 在本模式无效」那类注文。第十三轮照盲测的结论加过一条，实测当场
        // 自打嘴巴：`limit:20` 明明就列了 20 条。盲测证到的只是「limit:'all' 拿不到更多」——那是
        // 抬不高（缺省已经顶在 200），不是不起作用。两个方向别混为一谈。天花板本身由 Fold.Line 的
        // `server cap 200 reached` 分支说，那条是对的。

        return FootnoteBlock.After(
            body + (fold != null ? fold + "\n" : string.Empty), Footnotes(output));
    }

    // 表头。三组数并排，每组各自说清数的是哪一批：
    //   Tally.Cell                → 「列了几条 / 这个 scope 里一共几条」，与 locate 同一格；
    //   Shape.Direct / .Deepest   → 同一棵树的形状（不是切片的！见 R42）；
    //   labels.Header             → 那个总数的来源构成（见 SourceLabeling）。
    //
    // 这一行此前是自己一套写法：`Subclasses of 'X' (12 in scope 'all', …). Listed below: 5`。
    // 同一对数（总数 / 显示数）在这套工具里有四个表头各说一遍，而这一个是唯一把两者拆到句子
    // 两端、中间隔着整棵树形状的——`12` 在最前、`5` 在句末，读者要跨过一整段限定语才拼得起来，
    // 且「Listed below」在版面上还很像「下面就是那 5 个」的引导语而不是一个计数。
    //
    // 改走 Tally.Cell 之后三件事一起归位：`N of M` 那条读法（没被截时不写 of，见 GrammarRules
    // 规则三）不必在这里重写一遍；`at least` 改口与 `(K at 100%)` 这两个槽将来接上时是共用的；
    // 「表头 N of M 与折叠行 +K 必须自洽」那条常驻判据（规则八按名词配对）在这个工具上从此
    // 真的生效——此前它因为表头没有 `N of M subclasses` 而整条落空。
    private static string Headline(InheritorsOutput output, SourceLabeling labels)
    {
        // 深度标记的约定只在**这次真的印出了标记**时才需要说明；一个标记都没有时讲解一套不存在
        // 的记法，反而会让读者去找它（同 R9：表头说过的话不逐行重复，没发生的事不说）。
        //
        // 「direct = depth 1」必须点破：整份返回里 depth 的原点从没写过，于是表头的
        // `deepest 6 levels down` 该对应 `[depth 6]` 还是 `[depth 5]` 无从判断，而这两种读法在
        // 「要覆写哪一层」上给出不同答案。
        var shownDeepest = ShownDeepest(output);
        var depthLegend = shownDeepest > 1 ? ", untagged = direct (depth 1)" : "";

        // 构成紧跟在它限定的那个总数后面。方括号描述的是**全集**的来源构成，而括号里那串是树的
        // 形状——中间隔着一整段形状描述时，`[vanilla 6, milira 1]` 会被读成「列出来这几行的来源」。
        var cell = Tally.Cell(
            output.Inheritors.Items.Count, output.Inheritors.TotalInScope, "subclasses");

        return $"## '{output.Symbol}' — {cell}{labels.Header} "
               + $"(in scope '{output.Scope.Expression}', transitive — indirect descendants "
               + $"included; {output.Shape.Direct} direct, deepest "
               + $"{OutputText.Quantity(output.Shape.Deepest, "levels")} down{depthLegend}):";
    }

    // 切片里最深的那一层。表头的图例与覆盖说明都从它派生，故只数一次。
    private static int ShownDeepest(InheritorsOutput output)
        => output.Inheritors.Items
            .Select(e => output.Depths.TryGetValue(e.Item, out var d) ? d : 1)
            .DefaultIfEmpty(1).Max();

    // 「藏起来的是哪一批」。截断留下的**恒是最浅的那一批**（GetInheritors 按 depth 升序排候选），
    // 而返回里此前只在表头顺带提了一句。默认读法是「列表是这棵树的一个样本」，于是「样本里最深的
    // 一层」被当成「树最深的一层」——R42 治好的是表头报错深度，这里复发成**把 depth 4 的那批名字
    // 报成 depth 6 的成员**。
    //
    // 两支是两件不同的事实，不是一件事的两个精度，故不叠着说：切片没触到最深层时，藏起来的里面
    // **有更深的**（这套工具给不出「第 5、6 层有谁」——没有 offset、也没有参数抬得动 200 这个顶；
    // 答不了不是缺陷，不说自己答不了才是）；触到了，藏的就是同深度里的其余。
    private static string HiddenBatchOf(InheritorsOutput output, int shownDeepest)
        => shownDeepest < output.Shape.Deepest
            ? Fold.HiddenBatch.NothingDeeperThan(shownDeepest)
            : Fold.HiddenBatch.ShallowestFirst;

    // 一行：类型全名 + 深度标记 + 文件注记 + 条件标记 + 来源标签。
    // 后两个的**相对次序**是全服判据：行尾的 `[x]` 是来源标签位（见 SourceLabeling 与文法闸
    // 规则六），别的记号挤进去会让「同源就提到表头」那条判据在这一行上读不出来。
    private static string Row(InheritorsOutput output, ScopedEntry<string> entry, SourceLabeling labels)
    {
        var paths = output.Paths.TryGetValue(entry.Item, out var p) ? p : Array.Empty<string>();

        // 深度必须逐条标出来：树是拍平成一列返回的，不标就分不出「直接子类」和「曾孙」，
        // 而这两者在判断「要覆写哪个方法」时含义完全不同。但**只标非直接的**：直接子类占绝大
        // 多数（本转储 601 行全是），每行挂一个 `[direct]` 是把表头已经说过的话再说 601 遍。
        var depth = output.Depths.TryGetValue(entry.Item, out var d) ? d : 1;
        var depthLabel = depth == 1 ? "" : $" [depth {depth}]";

        // 声明散在多份文件里时只有全部落在条件目录里才打标——有一份无条件的，这个类型在任何
        // 实机上都在（见 ConditionalFolders.OfAll）。
        return $"- `{entry.Item}`{depthLabel}{SymbolRow.FileNote(entry.Item, paths)}"
               + $"{output.Conditional.TagAll(paths)}{labels.Row(entry.SourceName)}";
    }

    // 尾注顺序，与 ScanOutputRenderer.Footnotes 同一条规则（由近及远）。这一形没有前两档
    // （本段的量、整份结果的完整性）——继承闭包是精确的，没有「扫不全」这回事。
    //   3. 行内记号的成因（conditional）
    //   4. scope 相关（越界报告 / scopeNotice）
    private static string?[] Footnotes(InheritorsOutput output) =>
    [
        output.Conditional.Render(),
        OutOfScopeFooter(output, withTreeShape: true),
        output.ScopeNotice,
    ];

    // 越界脚注。原先只报「外面还有 91 个」，而表头那句 `23 direct, deepest 6 levels down` 是
    // scope 内的形状——「换个 scope 会不会改变深度」在返回里完全不可判定，调用方只能猜。盲测里
    // 它猜错并写进了答案正文（实测 scope:'all' 仍是 deepest 6）。与 R42 同形，轴从「整树 vs
    // 截断切片」换成「域内树 vs 全域树」。
    //
    // 这两个数不必重算：Depths 是在**全域**上跑完 BFS 的产物，scope 过滤发生在它之后。不重算
    // 也正是它们不会与逐行的 `[depth N]` 对不上的原因。
    //
    // 零命中形不带这句形状补充：那时表头压根不存在，没有要被限定的数。
    private static string? OutOfScopeFooter(InheritorsOutput output, bool withTreeShape)
    {
        var report = new ScopeReport();
        report.Add(output.Inheritors);

        if (!withTreeShape) return report.Render(output.Scope, "subclasses");

        var directEverywhere = output.Depths.Values.Count(d => d == 1);
        var deepestEverywhere = output.Depths.Values.DefaultIfEmpty(1).Max();

        return report.Render(
            output.Scope, "subclasses",
            extra: $"including them the tree is {directEverywhere} direct, deepest "
                   + $"{OutputText.Quantity(deepestEverywhere, "levels")} down");
    }
}
