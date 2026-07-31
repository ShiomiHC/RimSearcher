namespace RimSearcher.Output;

/// <summary>
/// 「举几个例子」的唯一产地 —— 取前几条、逗号连起来、**说清没举出来的还有几条**。
/// 尾巴省掉数量的写法(裸 <c>", …"</c>)与「一共就这几个」逐字同形,必须避免。
/// 三态文法(<see cref="Tally"/>)管的是结果集,管不到举例子这一层。
///
/// 上限不给默认值:举几条是**每处自己的判断**(mod 名单 5 条、packageId 8 条、近似候选
/// <c>Limits.MaxSuggestions</c> 条)。
/// </summary>
public static class NameList
{
    /// <summary>
    /// 渲染成「a, b, c」或「a, b, c, and 4 more」。名词不进这句 —— 上文一律已经点过
    /// (「This snapshot contains:」「It covers:」)。要带名词请在调用点自己接。
    /// </summary>
    public static string Render(IReadOnlyCollection<string> items, int max)
        => Render(items, max, items.Count);

    /// <summary>
    /// 同上,但**总数另给** —— 名单在到这里之前已被查询的 LIMIT 截过一次,
    /// 此时 <c>items.Count</c> 不是真总数。
    /// </summary>
    public static string Render(IReadOnlyCollection<string> items, int max, int total)
    {
        var shown = items.Take(max).ToList();
        var hidden = Math.Max(total, items.Count) - shown.Count;
        return string.Join(", ", shown) + (hidden > 0 ? $", and {hidden} more" : "");
    }
}
