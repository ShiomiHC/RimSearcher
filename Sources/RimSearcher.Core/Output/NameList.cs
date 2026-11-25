namespace RimSearcher.Output;

public static class NameList
{
    public static string Render(IReadOnlyCollection<string> items, int max)
        => Render(items, max, items.Count);

    public static string Render(IReadOnlyCollection<string> items, int max, int total)
    {
        var shown = items.Take(max).ToList();
        var hidden = Math.Max(total, items.Count) - shown.Count;
        return string.Join("、", shown) + (hidden > 0 ? $"，其余 {hidden} 个" : "");
    }
}
