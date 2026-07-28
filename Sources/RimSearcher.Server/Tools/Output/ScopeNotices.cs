using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// scope 相关的三条脚注。共同点是它们全都在说**缺席的含义**：拼错的 scope 被静默退回全域、
// 窄 scope 下「搜不到」不等于「不存在」、扫盘工具没有越界计数不等于外面没有。
//
// 三条互斥关系要一并读：RetryWider 与 ScopeReport 的越界脚注同现时只留后者（它说得更全），
// 而 HardScopeFilter 恰恰是「压根不会有越界脚注」的那两个工具用的。谁在场谁不在场本身就是
// 契约的一部分——这也是它们必须住在一起的理由。
public static class ScopeNotices
{
    // 拼错的 scope 会被 ScopeCatalog 静默退回全域（空集合会更糟，见那里的注释）。
    // 退回本身没问题，无声才是问题：调用方拿着全域结果，会以为自己限定过范围。
    public static string? Unresolved(ScopeCatalog catalog, ScopeSelection scope)
    {
        if (scope.UnresolvedTokens.Count == 0) return null;

        var names = string.Join(", ", scope.UnresolvedTokens.Select(t => $"'{t}'"));
        var fellBack = scope.IncludesEverything && scope.UnresolvedTokens.Count > 0;

        return $"\n_Scope {names} matched no configured group or source and was ignored"
             + (fellBack ? $" — searched everything instead" : $"; searched '{scope.Expression}'")
             + $". Available — {catalog.DescribeAvailable()}._";
    }

    // 零命中 + 窄 scope 是「搜不到」被读成「不存在」的高发点：那一刻返回里通常连一条
    // out-of-scope 计数都没有（扫盘类工具本就不统计，模糊搜索也可能真的一条落选都没有），
    // 于是全篇没有任何痕迹提示还有别的地方没找过。默认 scope 来自 config，调用方多半
    // 根本不知道自己被限定在了哪几个源里。
    // hasOutOfScopeFooter：ScopeReport 的脚注已经把这件事说得更全（它点明限制、逐源给出
    // 落选命中数、并给同一条出路）。两句并排时同一个 scope 表达式在两行里出现三次、
    // 同一个「改用 scope:'all'」被两套措辞各说一遍，读者以为是两条不同的提示。
    public static string? RetryWider(ScopeSelection scope, bool hasOutOfScopeFooter = false)
        => scope.IncludesEverything || hasOutOfScopeFooter
            ? null
            : $" Only sources in scope '{scope.Expression}' were searched — "
              + $"retry with scope:'{ScopeCatalog.EverythingKeyword}' before concluding it does not exist.";

    // 有结果时的对应件：说清「这里为什么没有 out-of-scope 那一行」。
    //
    // 同一批工具里 locate 与 trace inheritors 会逐源报出 scope 外还有多少命中，而两个扫盘类
    // 工具（trace usages / search_regex）不报——它们是硬 scope 过滤，落选文件根本没被打开，
    // 要统计就得再读一遍，代价与全域搜索相同（见 TraceTool 扫盘处的注释）。问题在于返回里
    // 同样写着 `in scope 'X'`，于是「没有那一行」会被读成「scope 外没有」。盲测里这一条被
    // 单列为「最容易造成静默漏检的缺口」：缺席不等于没有，而缺席本身不留痕迹。
    //
    // 全域时不印——那时本来就没有「外面」。
    public static string? HardScopeFilter(ScopeSelection scope)
        => scope.IncludesEverything
            ? null
            // 括号里那半句原先是「the absence of such a line is not evidence of absence」——双重否定
            // 套 absence，而它要说的事第一句已经正面说过了（"cannot tell you whether there are matches
            // there"）。同一件事说两遍，第二遍还更难读。收成一句陈述。
            : $"\n\n_Files outside scope '{scope.Expression}' were never opened, so this tool cannot tell you "
              + $"whether there are matches there; pass scope:'{ScopeCatalog.EverythingKeyword}' to include them. "
              + "(locate and trace inheritors do count out-of-scope hits; this tool never prints such a line.)_";
}
