using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// Header 的多段形：主题 + 逐段计数列表 + scope 标注。locate 的头一行是
// `## 'query' — <各段计数> _(scope: X)_`，不是单个 Header 装得下的形状（见指导文档 §4）。
//
// 一格描述一段，且**恒与那一段同生同死**：格子不由工具另攒一张列表，而是从段自己算出来
// （见 LocateSection / LocateRenderer）。原先两者是并行维护的两个 list，Members 段还得先占位
// 再回填——那格子要等分组配额切完才算得出来，于是次序靠手工保。
public static class Tally
{
    // 一格：**列出了几条，以及这个 scope 里一共有几条**。
    //
    // 原先只有前一个数（`— 5 members`），而 `method:CompTick` 的真实命中是 144——总数在整份
    // 返回里一次都没出现过，要靠折叠行的 `+139 more` 自己做加法。表头是最显眼的位置，
    // 盲测里两个调用方都差点把它当结论直接报出去，其中一个原话是「会把 144 报成 5，错 28 倍」。
    //
    // 同一批工具里 trace 的表头给的是**总数**（`(381 in scope 'base' …) Listed below: 200`），
    // locate 给的是**显示数**，句式却一样——两个口径撞在同一个位置上，这才是要害。故这里改成
    // 两个数都给，且沿用 `<数> of <数>` 那条读法：没被截时不写 `of N`，那时显示即全部。
    //
    // 那次只改了 locate 一边，碰撞从「一边缺一个数」降级成「两边顺序相反」而没有消掉。
    // N5 之后 trace inheritors 的表头也走这一格（见 InheritorsRenderer.Headline），两个工具
    // 说同一对数用的是同一个写法，这条才算完。
    //
    // 读法的辖域是这**一个计数惯用法**，不是 `of` 这个词——语料里还有改不掉的普通介词
    // （`lines of a N-line file`、`tokens of that length`）。判据见 GrammarRules 规则三。
    //
    // 名词跟总数走（"1 of 768 C# types" 是属格复数，"5 C# types" 跟 5），与 R30 判据一致。
    //
    // totalIsLowerBound 时改口成 `at least N`。文法与 search_regex / trace 的表头共用
    // （见 ScanReport.FoundCount），那边是「有文件没扫全所以总数只是下界」，这边是「候选池
    // 装不下所以总数只是下界」——两处成因不同，而调用方要学的读法是同一条：出现 at least
    // 就说明这个数只是地板。
    //
    // fullScore：这个总数里名字逐字相同的有几条（ScopedResult.FullScoreCount），-1 = 不适用。
    // 表头的 `N of M` / bare `N` 说的是**完整性**（这一段有没有被截断），而调用方拿它当
    // **精确性**读：`method:Draw` 的 `10 of 1591 members` 印出来的 10 条全是 100%，
    // 真正叫 Draw 的只有 35——第十轮盲测两条链各自差点把 1591 与 4 当成答案交出去，两次都
    // 是自费多跑一轮才刹住。这里补的就是那个推不出来的数，且只在它与总数不等时才印：
    // 相等时（全集本来就都是精确命中）一个字都不多，不会退化成常亮。
    public static string Cell(
        int shown, int total, CountedNoun plural, bool totalIsLowerBound = false, int fullScore = -1)
    {
        // 记号取 ScanReport 那一处，不在这里再写一遍字面量。上面那段注释本就写着「文法与
        // search_regex / trace 的表头共用（见 ScanReport.FoundCount）」，而共用此前只是两处
        // 各写一个 "at least " 恰好写成了同一个词——注释说的是事实，不是构造。
        var floor = totalIsLowerBound ? ScanReport.FloorMark : string.Empty;
        var head = total > shown
            ? $"{shown} of {floor}{plural.Quantity(total)}"
            : $"{floor}{plural.Quantity(shown)}";

        // 下界形不带这个限定：那时总数自己都不准，再挂一个「其中几条精确」会被读成两处
        // 独立的不确定性（同折叠行只在表头限定一次那条判据）。
        return fullScore >= 0 && fullScore < total && !totalIsLowerBound
            ? $"{head} ({fullScore} at 100%)"
            : head;
    }

    // 区间形：**取的是哪一段，以及一共有多少**。`lines 2-30 of 30`。
    //
    // 与上面那格长得像而读法相反：这里的 of 说的是「取自」，`30 of 30` 完全正常（第 2 到第 30
    // 行、全文共 30 行）；Cell 的 of 说的是「没给全」，`30 of 30` 在那边是自相矛盾。名词按英文
    // 语序落在区间**前面**，两形只在这一点上分得开。
    //
    // 此前两个工具各手拼一遍同一句（ReadCodeTool 的位置行、InspectTool 的 XML 表头），
    // 谁也不是产地。代价直接落在闸上：GrammarRules 的 `N of M` 只好挂一条 `(?<![-\d])` 的
    // 区间豁免，而那条豁免认的是「N 前面有没有连字符」这个**文本特征**，不是「这一形出自哪里」
    // ——read_code 哪天把 `lines 2-30` 改成 `L2–L30`，豁免立刻失效，闸会把一条正常的行判红。
    // 有了产地，闸问产地，豁免整条不必存在。
    public static string Window(CountedNoun noun, int from, int to, int total)
        => $"{noun.Plural} {from}-{to} of {total}";
}
