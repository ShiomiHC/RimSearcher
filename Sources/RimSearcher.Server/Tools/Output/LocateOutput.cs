using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// locate 的输出模型：一个 Tally 表头 + 若干段 + 若干工具自备脚注。
//
// 这一形的耦合密度是全服最高的，且大部分是**同一件事在五个地方各写一遍**：
//
//   1. 「这一段有行」⇔「Tally 里有它那一格」⇔「越界合计里记了它一笔」⇔「不走零命中路径」。
//      四件事此前在每一段里各写一遍（5 段 × 4 = 20 处），漏任何一处都是静默的：少一格 Tally
//      则表头与正文对不上，少一笔合计则脚注的数字不对应正文，忘置 hasResults 则整份返回退化
//      成 "No results"。现在一段就是一个 LocateSection，前三件事全从它派生（见 LocateRenderer），
//      第四件是 `Sections.Count == 0`。Members 段原先还得先往 tally 塞个空格子占位、等分组配额
//      切完再回填——那种保序全靠手工。
//   2. 「段头的方括号按**全集**判，未截断不印构成」。五段此前各写一遍判据，且写法不同：三段
//      用 `SourceLabeling.Of(result)`，Members 与 Files 显式传 scopeTotals。现在统一成
//      `Shown < Total` 一处（Files 那条额外条件由工具决定往 SourcesInScope 里放不放，
//      见那里的注释——加起来对不上的构成不如不印）。
//   3. 「折叠行按总量计」。五段的 hidden 都恰好是 `Total - Shown`（含 Files 兜底支：
//      fileTotal = 列出的 + 被砍掉的），故折叠行不再由工具各算一遍。
//   4. **脚注的排序，两条路径各一份**。有结果时是「conditional → 越界 → 缺文件 → 下界成因
//      → 短词 → scopeNotice → 前缀」，零命中时是「RetryWider → 越界 → scopeNotice → 前缀
//      → Try」。此前两份顺序只活在 ExecuteAsync 的代码顺序里，中间还隔着一个 early return。
//
// 工具还留着的措辞只有各行的正文与四条自备整句——前者五段各不相同（`(N%)`、`- DefType`、
// `字段路径 in \`宿主\``…），后者是 locate 独有的能力边界声明。即便这些，**它们挂在哪、
// 什么顺序**仍归 renderer。
public sealed record LocateOutput
{
    // 表头 `## '...'` 里的查询串，原样回显（与零命中那句里的 ForEcho 形不同，故两处分开给）。
    public required string Query { get; init; }

    public required ScopeSelection Scope { get; init; }

    // 只放**真有行**的段，按印出来的先后。空列表 = 零命中形。
    public required IReadOnlyList<LocateSection> Sections { get; init; }

    // 零命中时的第一句（含句末句号）。
    public required string EmptyLine { get; init; }

    public required ResultLimit Limit { get; init; }

    // 跨段累加的越界计数。两条路径都要它，且零命中路径还要拿「它在不在场」去决定
    // RetryWider 说不说话（两句并排会把同一个「改用 scope:'all'」用两套措辞各说一遍）。
    public required ScopeReport OutOfScope { get; init; }

    // 五段共用一份：行内只放键，成因整份说一次。
    public required ConditionalReport Conditional { get; init; }

    // ---- 工具自备的整句脚注。四条各有自己的在场条件，措辞是 locate 独有的能力边界声明 ----

    // 显式带扩展名却没有这个文件。零命中路径不挂：那时第一句已经是 "No results for 'X'"。
    public string? MissingFile { get; init; }

    // 查询词里有短于内容索引建键下限的词——那些词**没被查过**，而 Content Matches 段整段
    // 缺席与「查过了、零命中」在版面上逐字同形。
    public string? ShortTokens { get; init; }

    // 认不出的前缀被当普通搜索词用了 / 前缀后面什么都没给。两条路径都挂。
    public string? PrefixNotice { get; init; }

    // 零命中那句 Try 的两形：过滤器清单已经在 PrefixNotice 里列过一遍时不再列第二遍
    // （那正是最该看到它的场合，紧挨着说两遍是噪音）。与 PrefixNotice 非空**不是**同一个
    // 条件——「前缀后面没给值」那一条不列清单。
    public required bool FilterListAlreadyShown { get; init; }

    public string? ScopeNotice { get; init; }
}

// 一段。Tally 那一格、段头的来源构成、段末的折叠行三者全从这里派生，故它们不可能与段本身
// 对不上——那正是此前五段各写一遍时唯一防不住的事。
public sealed record LocateSection
{
    // 段头，如 "C# Types"（renderer 加 `**` 与冒号）。
    public required string Name { get; init; }

    // Tally 那一格与折叠行共用的名词，如 "C# types"。共用是判据而不是巧合：两处数的是
    // 同一批东西，措辞分家就会长出两个名词指同一类东西（见 ScopeReport.Composition 里同型的坑）。
    public required string Noun { get; init; }

    // 行文本，**不含**行尾的来源标签（那由 renderer 按整段判完再挂，见 LocateRow）。
    public required IReadOnlyList<LocateRow> Rows { get; init; }

    // 列出了几条。与 Rows.Count 不是同一个数：Members 段的 Rows 里混着 `  Methods:` 这类
    // 子组标题，它们不是结果行。
    public required int Shown { get; init; }

    // 这个 scope 里一共有几条。`Total > Shown` 是**唯一**的截断判据，Tally 的 `of`、
    // 段头的构成、段末的折叠行三处同出于它。
    public required int Total { get; init; }

    // Total 自己只是下界（候选池装不下）。
    //
    // 与 LowerBoundNotice 是一件事的两半，故住在同一个对象里：Tally 那一格改口成 `at least N`
    // 的同时必须给出成因。两个扫描类工具的 `at least` 恒与「有文件没扫全」那条尾注同现，调用方
    // 从那里学到的读法就是「看到 at least 去找成因」；locate 此前只改表头、一句成因都不给，
    // 于是同一个记号在两个工具上要各学一遍，而这边那一次还无从判断「narrow the query」到底要
    // 窄到什么程度（见 ScanReport.LowerBoundReason 里同型的判据）。
    public bool TotalIsLowerBound { get; init; }

    // 上一格为真时的成因整句（含前导空行与斜体）。措辞是本段独有的（成员搜索的服务端展开上限），
    // 但**它挂在哪、什么顺序**归 renderer。
    public string? LowerBoundNotice { get; init; }

    // Total 里名字逐字相同的有几条；-1 = 这一段算不出（分值不可比，或多关键词把前缀命中
    // 也推到了 100 分，那时这个数是假的）。
    public int FullScoreCount { get; init; } = -1;

    // Total 那一批的来源构成。renderer 只在 `Total > Shown` 时用它；工具往里放什么由
    // 「各源之和加不加得起来」决定——构成自证的本事全在「恰好等于 Total」上。
    public IReadOnlyList<(string Source, int Count)> SourcesInScope { get; init; } = [];

    // 折叠行要靠这两个才分得清「limit 砍的」与「断层收口砍的」：后者调多大的 limit 都拿不回来。
    public bool TruncatedByScoreGap { get; init; }
    public bool TruncatedByLimit { get; init; }

    // 这一段有没有「还有更多」这回事。Files 段的精确补充支没有——它本来就只列同名的那几条。
    public bool Foldable { get; init; } = true;
}

// 一行。SourceName 交给 renderer，是因为「同源就提到段头印一次、混源才逐行印」这条判据
// 要看**整段实际列出的那些行**，单行答不出来。
//
// 五段的行各不相同，但来源标签在五段里都落在**行尾**——故它是 renderer 挂得上的唯一记号，
// 其余（分数、文件注记、conditional 键）都在段自己的措辞里。
//
// IsGroupHeader：Members 段的 `  Methods:` 这类子组标题。它们既不参与「这批行是不是同源」
// 的判定（参与了会因为 SourceName 为 null 而让整段一个标签都不印），也不该挂标签。
public sealed record LocateRow(string Text, string? SourceName = null, bool IsGroupHeader = false);
