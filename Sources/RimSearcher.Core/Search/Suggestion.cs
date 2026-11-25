using RimSearcher.Cli;

namespace RimSearcher.Search;

public static class Suggestion
{
    public static IReadOnlyList<string> Closest(IEnumerable<string> pool, string? typed)
        => [.. FuzzyMatcher.Rank(pool, typed ?? "").Take(Limits.MaxSuggestions).Select(t => t.Text)];

    public static string Say(IReadOnlyCollection<string> closest, string whenNone = "")
        => closest.Count == 0 ? whenNone : $"拼写最接近的：{string.Join("、", closest)}。";
}
