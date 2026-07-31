namespace RimSearcher.Search;

/// <summary>
/// 字段路径的分段。
///
/// `--path` 与 `--value` 都是**子串**匹配,而子串匹配不留痕:
/// `--path soundImpact` 会命中语义相反的 `soundImpactDefault`。所以输出里必须有一处说
/// 「你打的这个词,作为一个完整的段,一次都没命中」。
///
/// 判据:把路径按 `.` 切开,每段去掉 `[N]` 下标,与查询词整体比一次。
/// 下标不算段的一部分 —— `comps[3]` 里那个 `comps` 就是完整的一段。
/// </summary>
public static class PathSegments
{
    /// <summary>
    /// <paramref name="text"/> 是不是 <paramref name="path"/> 里一段**完整的、连着的**路径。
    ///
    /// 两边都按 <c>.</c> 切成段;查询词写了下标就带下标比,没写就把路径那一侧的下标剥掉再比。
    /// 于是三种写法都成立,而且各自的语义不串:
    ///   <c>comps</c> 命中 <c>comps[0].props.energyMax</c>(下标无关的问法)
    ///   <c>comps[0]</c> 也命中它(**块级问法**。下标不能无条件剥 —— 那样带下标的写法
    ///     永远不可能等于任何一段)
    ///   <c>props.energyMax</c> 同样命中(多段问法)
    /// </summary>
    public static bool IsWholeSegment(string path, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var want = text.Split('.');
        var have = path.Split('.');
        if (want.Length > have.Length) return false;

        for (var at = 0; at + want.Length <= have.Length; at++)
        {
            var all = true;
            for (var i = 0; i < want.Length && all; i++)
                all = SameSegment(have[at + i], want[i]);
            if (all) return true;
        }
        return false;
    }

    /// <summary>查询词带下标就连下标一起比,不带就把路径那一侧的下标剥掉。</summary>
    private static bool SameSegment(string segment, string wanted)
    {
        if (!wanted.Contains('[', StringComparison.Ordinal))
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0) segment = segment[..bracket];
        }
        return string.Equals(segment, wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这条路径所在的**带下标容器**的前缀,含末尾的点;不在这种容器里就回 null。
    ///
    /// <c>comps[1].minFuelCost</c> → <c>comps[1].</c>。判据只认 <c>].</c>:
    /// 只有带下标的那一层才是「一个可以整块换掉的东西」(一个 comp、一个 li),
    /// 而同一块里的字段互相约束(实测 <c>minFuelCost=50</c> 盖掉了同块的 <c>fuelPerTile=3</c>)。
    /// 不带下标的层(<c>projectile.</c>)不算:那是分类,不是实例,兄弟太多且不成组。
    /// </summary>
    public static string? ContainerPrefix(string path)
    {
        var i = path.LastIndexOf("].", StringComparison.Ordinal);
        return i < 0 ? null : path[..(i + 2)];
    }

    /// <summary>
    /// 路径的**形状** —— 每个下标里的数字抹掉,<c>statBases[7].stat</c> → <c>statBases[].stat</c>。
    ///
    /// 「一次查询命中了几种东西」问的是形状,不是路径:一百多条 <c>statBases[N].stat</c>
    /// 原样列出来是噪音,而「statBases 与 statFactors 两种」才是做集合运算的人要判的那件事。
    /// </summary>
    public static string Shape(string path)
    {
        if (!path.Contains('[', StringComparison.Ordinal)) return path;
        var sb = new System.Text.StringBuilder(path.Length);
        var inIndex = false;
        foreach (var c in path)
        {
            if (c == '[') { inIndex = true; sb.Append(c); continue; }
            if (c == ']') { inIndex = false; sb.Append(c); continue; }
            if (!inIndex) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>这条路径的某一段与**任意一个**查询词整体相等。</summary>
    public static bool IsWholeSegment(string path, IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
            if (IsWholeSegment(path, texts[i])) return true;
        return false;
    }

    /// <summary>
    /// 路径里每一层带下标的前缀:<c>a[0].b[1].c</c> → <c>a[0]</c>、<c>a[0].b[1]</c>。
    ///
    /// `get` 折叠掉默认值行之后,一整个列表项可能一条不剩,于是「这个列表只有一项」
    /// 成了看得见的形状,而真值更长。下标前缀取自折叠前的 matchedPaths,
    /// 与印出来的一比就算得出「有没有整项消失」。
    /// </summary>
    public static IEnumerable<string> IndexPrefixes(string path)
    {
        for (var i = 0; i < path.Length; i++)
            if (path[i] == ']') yield return path[..(i + 1)];
    }
}
