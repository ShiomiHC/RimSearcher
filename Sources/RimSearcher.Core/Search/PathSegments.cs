namespace RimSearcher.Search;

/// <summary>
/// 字段路径的分段。
///
/// `--path` 与 `--value` 都是**子串**匹配,而子串匹配不留痕:
/// `get Bullet_BeamRepeater --path soundImpact` 只回一行 `soundImpactDefault` —— 语义相反的
/// 另一个字段,而 `code_default=no` 让它看着像作者刻意设的。输出里没有任何一处说过
/// 「你打的这个词,作为一个完整的段,一次都没命中」。第五轮盲测里这一条直接产出了错结论。
///
/// 判据只认「肉眼在这一行上验证得了的」:把路径按 `.` 切开,每段去掉 `[N]` 下标,
/// 与查询词整体比一次。下标不算段的一部分 —— `comps[3]` 里那个 `comps` 就是完整的一段。
/// </summary>
public static class PathSegments
{
    /// <summary><paramref name="text"/> 是不是 <paramref name="path"/> 的某一个完整段。</summary>
    public static bool IsWholeSegment(string path, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var start = 0;
        while (start <= path.Length)
        {
            var dot = path.IndexOf('.', start);
            var end = dot < 0 ? path.Length : dot;

            var segEnd = end;
            var bracket = path.IndexOf('[', start);
            if (bracket >= 0 && bracket < end) segEnd = bracket;

            if (segEnd - start == text.Length &&
                string.Compare(path, start, text, 0, text.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;

            if (dot < 0) break;
            start = dot + 1;
        }
        return false;
    }

    /// <summary>
    /// 这条路径所在的**带下标容器**的前缀,含末尾的点;不在这种容器里就回 null。
    ///
    /// <c>comps[1].minFuelCost</c> → <c>comps[1].</c>。判据只认 <c>].</c>:
    /// 只有带下标的那一层才是「一个可以整块换掉的东西」(一个 comp、一个 li),
    /// 而同一块里的字段互相约束 —— 实测里 <c>minFuelCost=50</c> 盖掉了同块的
    /// <c>fuelPerTile=3</c>,差 16 倍,而只看后者的输出一个字都没提前者。
    /// 不带下标的层(<c>projectile.</c>)不算:那是分类,不是实例,兄弟太多且不成组。
    /// </summary>
    public static string? ContainerPrefix(string path)
    {
        var i = path.LastIndexOf("].", StringComparison.Ordinal);
        return i < 0 ? null : path[..(i + 2)];
    }

    /// <summary>这条路径的某一段与**任意一个**查询词整体相等。</summary>
    public static bool IsWholeSegment(string path, IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
            if (IsWholeSegment(path, texts[i])) return true;
        return false;
    }
}
