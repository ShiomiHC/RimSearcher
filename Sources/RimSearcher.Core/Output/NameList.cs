namespace RimSearcher.Output;

/// <summary>
/// 「举几个例子」的唯一产地 —— 取前几条、逗号连起来、**说清没举出来的还有几条**。
///
/// 抽出来的理由不是 DRY。这个动作原先散在十几处,尾巴长出四种写法:四处写
/// <c>", and N more"</c>,三处写裸 <c>", …"</c>,其余不加。而裸的那个省掉的正是数量 ——
///
///   This snapshot contains: brrainz.harmony, …(8 个), …. 'rimsearcher mods' lists them all.
///
/// 读出来是「大概就这些」,而真值是 22 个里的 8 个。**它与「一共就这 8 个」逐字同形**,
/// 正是这套输出从第一轮起一直在清的那个形状,只不过这次犯在举例子上而不是计数上。
/// 三态文法(<see cref="Tally"/>)管的是结果集,举例子这一层此前没有产地,于是各写各的。
///
/// 上限不给默认值:举几条是**每处自己的判断**(mod 名单 5 条、packageId 8 条、近似候选
/// <c>Limits.MaxSuggestions</c> 条),给了默认值就会有人顺手用默认值而不想这件事。
/// </summary>
public static class NameList
{
    /// <summary>
    /// 渲染成「a, b, c」或「a, b, c, and 4 more」。名词不进这句 —— 上文一律已经点过
    /// (「This snapshot contains:」「It covers:」),再带一遍就是复述,而多带的那个词
    /// 还得进登记处。要带名词的场合请在调用点自己接,别在这里开第二种形态。
    /// </summary>
    public static string Render(IReadOnlyCollection<string> items, int max)
    {
        var shown = items.Take(max).ToList();
        var hidden = items.Count - shown.Count;
        return string.Join(", ", shown) + (hidden > 0 ? $", and {hidden} more" : "");
    }
}
