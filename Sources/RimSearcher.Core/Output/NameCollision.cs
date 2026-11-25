namespace RimSearcher.Output;

public static class NameCollision
{
    public sealed record Entry(string Name, int Total, IReadOnlyList<string> Mine, IReadOnlyList<string> Others,
                               IReadOnlyList<string>? Spellings = null);

    public static IEnumerable<string> Say(IReadOnlyList<Entry> entries, int asked)
    {
        var folded = entries.Where(e => e.Spellings is { Count: > 1 }).ToList();
        if (folded.Count > 0)
            return [.. Say([.. entries.Except(folded)], asked),
                    .. folded.Select(e => Say(e.Name, e.Total, e.Mine, e.Others, e.Spellings))];

        var allShown = entries.All(e => e.Others.Count == 0);
        if (entries.Count < 2 || !(allShown || entries.All(e => e.Others.Count > 0)))
            return entries.Select(e => Say(e.Name, e.Total, e.Mine, e.Others));

        var which = entries.Count == asked
            ? (asked == 2 ? "两个名字" : Cjk.Join("全部", Tally.Complete(asked).Render("name")))
            : Tally.Of(entries.Count, asked).Render("name");
        return
        [
            allShown
                ? $"{which}各自由不同类型的 def 共用，已全部列出；括号内为 def 数：{List(entries, e => e.Total)}。"
                : $"{which}也由其他类型的 def 使用，那些 def 未列出；括号内为这些 def 的个数：" +
                  $"{List(entries, e => e.Others.Count)}。",
        ];
    }

    private static string List(IReadOnlyList<Entry> entries, Func<Entry, int> n)
        => NameList.Render([.. entries.Select(e => $"{e.Name}（{n(e)}）")], entries.Count);

    public static string Say(string name, int total, IReadOnlyList<string> mine, IReadOnlyList<string> others,
                             IReadOnlyList<string>? spellings = null)
    {
        if (spellings is { Count: > 1 })
            return $"没有 def 恰好名为 '{name}'；忽略大小写时匹配 {Tally.Complete(total).Render("def")}，" +
                   $"名为 {NameList.Render([.. spellings], spellings.Count)}" +
                   (others.Count == 0 ? "，已全部列出。"
                    : others.Count == 1 ? $"；其中一个为 {others[0]}，未列出。"
                    : $"；其中 {Tally.Complete(others.Count).Render("def")} 属其他 def 类型，未列出。");

        var head = $"{Tally.Complete(total).Render("def")} 同名 '{name}'";
        if (others.Count == 0)
            return $"{head}，分属不同 def 类型，已全部列出。";

        return $"{head}：这里列出的是 {NameList.Render(mine, mine.Count)} 类型下的 def；" +
               (others.Count == 1
                   ? $"另一个为 {others[0]}"
                   : $"其余 {Tally.Complete(others.Count).Render("def")} 属其他 def 类型") +
               "，未列出。";
    }
}
