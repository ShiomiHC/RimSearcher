namespace RimSearcher.Storage;

/// <summary>
/// 噪声字段清单 —— **唯一产地**。
///
/// 02-2 是这个项目里被治过的同一种病的原样重演:上游把清单写了两份(导出侧按全路径匹配、
/// 查询侧按末段匹配),内容今天恰好相同而判据已经不同,于是嵌套噪声全部入库、只有顶层被拦。
/// B 案把它结构性消解掉:游戏侧根本不过滤,清单只在这里存在一份,判据也只有一个 ——
/// **按路径末段匹配**。策略要改就改这里,改完重跑 import(秒级),不进游戏重导。
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
        // 注意:generated 不在清单里 —— 它是 ImpliedDefs 的判据,是有用信号而非噪声(03 甲)。
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

    public static bool IsNoise(string path)
    {
        foreach (var p in NoisePrefixes)
            if (path.StartsWith(p, StringComparison.Ordinal)) return true;
        return NoiseLeaves.Contains(Leaf(path));
    }
}
