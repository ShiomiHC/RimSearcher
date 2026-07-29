using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// trace inheritors 的输出模型：一棵拍平成一列的子类树。
//
// 与 ScanOutput 同一条分界（说「有什么」，不说「怎么印」），但这里的耦合是另外三条，且都在
// 第九、十三轮盲测上各自出过事：
//
//   1. **表头的每个数各自说清数的是哪一批**。`200 of 381 subclasses` 里前一个数是展示切片、
//      后一个是整棵树，`306 direct, deepest 4 levels down` 又回到整棵树——三个口径句法对称地
//      并排。R42 修的是「切片的 direct/deepest 排在描述全树的总数之后」。
//      故模型里 Shape（域内整树）与 Items（切片）是两个字段，切片的深度由 renderer 自己数，
//      工具没有机会把它们混起来。
//   2. **「列了几个」与折叠行是同一件事的两半**。出现 `N of M` 这个记号本身就是「被截了」的
//      信号（R33），而折叠行是那个信号的收尾；两处此前是两个独立的 if，判据各写一遍
//      （`Items.Count < TotalInScope` 与 `Fold.Line` 内部的 `HiddenCount <= 0`）。
//      现在只判一次：`Tally.Cell` 的 `total > shown` 与 `Fold.Line` 的 `hidden > 0` 是同一件事，
//      而 hidden 就是 total - shown（见 InheritorsRenderer，与 LocateRenderer 同一条）。
//   3. **深度标记的图例与覆盖说明只在真印了标记时才说**。一个 `[depth N]` 都没有时讲解一套
//      不存在的记法，反而会让读者去找它（同 R9）；而切片浅于整树时必须说清「更深的没列出来」
//      ——第十三轮盲测里 depth 4 的那批名字被读成了 depth 6 的成员。两句都从「切片里最深的
//      那一层」派生，那个量只有拿到整批 Items 才数得出来，故不在模型里。
public sealed record InheritorsOutput
{
    public required string Symbol { get; init; }

    public required ScopeSelection Scope { get; init; }

    // scope 过滤后的展示切片 + 域内总数 + 越界计数 + 域内来源构成。折叠行、来源标签、
    // 越界脚注三处都从它派生（见 Fold.Line / SourceLabeling.Of<T> / ScopeReport.Add<T>）。
    public required ScopedResult<string> Inheritors { get; init; }

    // 每个类型到 Symbol 的距离，**全域** BFS 的产物（scope 过滤发生在它之后）。
    // 故它同时供两处用：切片里逐行的 `[depth N]`，与越界脚注里「把落选那批算进来整棵树
    // 是什么形状」。后者不必重算，也正因为不重算才不会与前者对不上。
    public required IReadOnlyDictionary<string, int> Depths { get; init; }

    // 每个类型的声明文件。行末的文件注记与条件标记都要它（见 SymbolRow.FileNote）。
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Paths { get; init; }

    // scope **内**整棵树的形状。与 Items 是两个量：后者是被 limit 截断后的展示切片。
    public required InheritorTreeShape Shape { get; init; }

    public required ResultLimit Limit { get; init; }

    public required ConditionalReport Conditional { get; init; }

    // 索引里到底有没有这个类型。零命中时「索引里没有这个名字」和「有，但没人继承它」是两件事，
    // 下一步也完全不同：前者要去确认名字，后者已经是答案。两者此前同一句话，调用方读到的都是
    // 「没有子类」，于是拿着一个根本不存在的名字继续往下查。
    //
    // 与 scope 无关（IsKnownType 不看 scope），故它是事实而不是措辞；而「这是答案」那句背书
    // 该不该给，还要看越界脚注在不在场——那个判断在 renderer 里，见 InheritorsRenderer.Empty。
    public required bool TypeIsIndexed { get; init; }

    public string? ScopeNotice { get; init; }
}
