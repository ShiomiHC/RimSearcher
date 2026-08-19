namespace RimSearcher.Storage;

/// <summary>
/// 噪声字段清单 —— **唯一产地**。
///
/// 游戏侧根本不过滤,清单只在这里存在一份,判据只有一个:**按路径末段匹配**。
/// 策略要改就改这里,改完重跑 import,不进游戏重导。
/// </summary>
public static class NoiseFilter
{
    /// <summary>按路径末段匹配的噪声字段名。</summary>
    public static readonly IReadOnlySet<string> NoiseLeaves = new HashSet<string>(StringComparer.Ordinal)
    {
        "debugRandomId",
        "defNameHash",
        "shortHash",
        "index",
        "ignoreConfigErrors",
        "ignoreIllegalLabelCharacterConfigError",
        // Unity 原生对象的内存地址(UnityEngine.Object.m_CachedPtr)。名字是 Unity 专有的,
        // 而值每次进程启动都不同 —— 一份 1.6 全家桶里就有 684 个,两次导出必然「有差异」。
        "m_CachedPtr",
        // ThingSetMaker_MarketValue / _Nutrition 的私有 RNG 状态,加载时 = Rand.Int。
        "nextSeed",
        // 导出跑的那个临时 savedata 目录,每次一个新 GUID。
        "BackupPath",
        // 注意:generated 不在清单里 —— 它是 ImpliedDefs 的判据,是有用信号而非噪声。
    };

    /// <summary>
    /// Mono 的 <c>System.Delegate</c> 内部字段。走到一个委托字段(<c>wanderDestValidator</c>、
    /// <c>qualityToValue</c>)时会被下钻出来,值是函数指针。
    ///
    /// **只能连值一起判**:<c>method</c> 这个名字被真实字段占着 ——
    /// <c>ScenPart.method</c> 的值是 <c>DropPods</c>,按名字拦会把它一起吃掉。
    /// </summary>
    public static readonly IReadOnlySet<string> PointerLeaves = new HashSet<string>(StringComparer.Ordinal)
    {
        "method",
        "method_ptr",
        "method_code",
        "invoke_impl",
    };

    /// <summary>整段丢弃的路径前缀。</summary>
    public static readonly IReadOnlyList<string> NoisePrefixes =
    [
        "modContentPack.",
    ];

    /// <summary>取路径末段(<c>comps[0].compClass</c> → <c>compClass</c>)。</summary>
    public static string Leaf(string path)
    {
        var cut = path.Length;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c == '.') return path[(i + 1)..cut];
            if (c == ']')
            {
                // comps[0] 这种下标不算末段的一部分
                var open = path.LastIndexOf('[', i);
                if (open >= 0) cut = open;
                i = open < 0 ? -1 : open;
            }
        }
        return path[..cut];
    }

    /// <summary>
    /// <paramref name="value"/> 只给按值判的那一类用。不传就等于「这一类不判」——
    /// 于是漏进去几个函数指针,而不是把同名的真实字段吃掉。
    /// </summary>
    public static bool IsNoise(string path, string? value = null)
    {
        foreach (var p in NoisePrefixes)
            if (path.StartsWith(p, StringComparison.Ordinal)) return true;

        var leaf = Leaf(path);
        if (NoiseLeaves.Contains(leaf)) return true;
        return PointerLeaves.Contains(leaf) && IsPointerValue(value);
    }

    /// <summary>
    /// 指针长这样:十进制、十位以上、没有别的字符。<c>DropPods</c> 这样的枚举名过不了,
    /// 而真要有个十位数的合法取值被误当指针,它也已经不是人能读的那种值了。
    /// </summary>
    private static bool IsPointerValue(string? value)
    {
        if (value is not { Length: >= 10 }) return false;
        foreach (var c in value)
            if (c is < '0' or > '9') return false;
        return true;
    }
}
