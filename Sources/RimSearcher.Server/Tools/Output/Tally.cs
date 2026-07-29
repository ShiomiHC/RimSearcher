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
    // 读法的辖域是这**一个计数惯用法**，不是 `of` 这个词——全语料里一半的 `of` 是改不掉的普通
    // 介词（`Subclasses of 'X'`、`lines of a N-line file`）。判据见 GrammarRules 规则三。
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
        int shown, int total, string plural, bool totalIsLowerBound = false, int fullScore = -1)
    {
        var floor = totalIsLowerBound ? "at least " : string.Empty;
        var head = total > shown
            ? $"{shown} of {floor}{OutputText.Quantity(total, plural)}"
            : $"{floor}{OutputText.Quantity(shown, plural)}";

        // 下界形不带这个限定：那时总数自己都不准，再挂一个「其中几条精确」会被读成两处
        // 独立的不确定性（同折叠行只在表头限定一次那条判据）。
        return fullScore >= 0 && fullScore < total && !totalIsLowerBound
            ? $"{head} ({fullScore} at 100%)"
            : head;
    }
}
