namespace RimSearcher.Output;

/// <summary>
/// 「这个名字被几个 def 类型共用」的唯一产地。
///
/// 抽出来是因为它的三档(全都在场 / 只剩一个别的 / 剩好几个别的)在真语料里凑不齐 ——
/// 手上这份 fixture 里同名的只有 Firefoam 一对,永远只走得到「剩一个」那档,
/// 而多出来的那档正是复数形态出错的地方。产地独立,三档就都能单独验。
/// </summary>
public static class NameCollision
{
    /// <param name="total">这个名字一共挂着几个 def。</param>
    /// <param name="mine">本次输出里那些 def 的类型。</param>
    /// <param name="others">被 <c>--type</c> 挡在外面的**每个 def** 的类型,不去重;为空表示全都在场。
    /// 只剩一个别的时点它的名;剩好几个折成个数 —— 那张名单拿掉 --type 就能看到,
    /// 而它在 Nociosphere 这种名字上有六个类型长。</param>
    public static string Say(string name, int total, IReadOnlyList<string> mine, IReadOnlyList<string> others)
    {
        var head = $"{Tally.Complete(total).Render("def")} share the name '{name}'";
        if (others.Count == 0)
            return $"{head} across different def types; all of them are shown. Pass --type <DefType> for just one.";

        // 尾缀的名词两支都不带 —— 类型名本身就以 Def 收尾,再接一个 "defs" 是
        // 「WorldObjectDef defs」。
        return $"{head}: this is the {NameList.Render(mine, mine.Count)} one; " +
               (others.Count == 1
                   ? $"the other is a {others[0]}"
                   : $"the {others.Count} others are of other def types") +
               ", shown only without --type.";
    }
}
