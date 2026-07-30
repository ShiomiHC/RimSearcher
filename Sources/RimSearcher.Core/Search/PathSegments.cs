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

    /// <summary>这条路径的某一段与**任意一个**查询词整体相等。</summary>
    public static bool IsWholeSegment(string path, IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
            if (IsWholeSegment(path, texts[i])) return true;
        return false;
    }
}
