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
    /// <param name="others">被 <c>--type</c> 挡在外面的类型;为空表示全都在场。</param>
    public static string Say(string name, int total, IReadOnlyList<string> mine, IReadOnlyList<string> others)
    {
        var head = $"{Tally.Complete(total).Render("def")} share the name '{name}'";
        if (others.Count == 0)
            return $"{head} across different def types; all of them are shown. Pass --type <DefType> for just one.";

        // 尾缀的名词两支都不带 —— 类型名本身就以 Def 收尾,再接一个 "defs" 是
        // 「WorldObjectDef defs」。单数那支本来就没有,复数跟着它对齐。
        return $"{head}: this is the {NameList.Render(mine, mine.Count)} one. " +
               (others.Count == 1
                   ? $"The other is a {others[0]}"
                   : $"The others are {NameList.Render(others, others.Count)}") +
               // 方位词说的是这句话下面那几段。这句永远排在 def 循环之前,所以它上面
               // 一个字都没有 —— 原先写的 above 指着一片不存在的上文。
               ", shown only without --type. Fields, parent node and translations below are this def's own.";
    }
}
