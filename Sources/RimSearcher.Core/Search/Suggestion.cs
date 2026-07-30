using RimSearcher.Cli;

namespace RimSearcher.Search;

/// <summary>
/// 「你是不是想打这个」的产地 —— 取候选 + 说那句话。
///
/// 抽出来是因为措辞长出了三种:<c>Closest:</c> / <c>Closest names:</c> /
/// <c>Closest by spelling:</c>,同一件事三种说法。**统一成 by spelling**,因为它说清了
/// 「凭什么近」:七处里有几处的池子是 def 名、有几处是文件名、有几处是树名,读的人
/// 需要知道这三个字是拼写打分打出来的,而不是语义上相关。
///
/// **一处故意不用这里**:<c>find</c> 的值域近似先做末段精确匹配(<c>CompAmbientSound</c>
/// 对 <c>RimWorld.CompAmbientSound</c> 是**同一个名字**,不是「长得像」),模糊打分只是
/// 兜底。那里说 by spelling 就是假话,所以它留着自己的 <c>Closest:</c>。
///
/// 候选数被 <see cref="Limits.MaxSuggestions"/> 截掉的那些**不报数量**,这与
/// <see cref="Output.NameList"/> 的规矩相反,是有意的:排在第 4 位往后的近似项按定义
/// 就不是「最近的」,补一句「还有 37 个」会让人以为答案可能在那 37 个里。
/// 「Closest」这个词本身已经声明了它是个 top-N。
/// </summary>
public static class Suggestion
{
    /// <summary>按拼写打分取前几个候选。空池子、空输入都安全。</summary>
    public static IReadOnlyList<string> Closest(IEnumerable<string> pool, string? typed)
        => [.. FuzzyMatcher.Rank(pool, typed ?? "").Take(Limits.MaxSuggestions).Select(t => t.Text)];

    /// <summary>
    /// 标准那句话,前面自带一个空格,一个候选都没有时给 <paramref name="whenNone"/>。
    /// 「一条都没有」的场合通常要说点别的(「'rimsearcher types' lists them all」),
    /// 所以那句也从这里过 —— 否则调用点还得自己写一遍三元判断,产地就又劈成两份。
    /// </summary>
    public static string Say(IReadOnlyCollection<string> closest, string whenNone = "")
        => closest.Count == 0 ? whenNone : $" Closest by spelling: {string.Join(", ", closest)}.";
}
