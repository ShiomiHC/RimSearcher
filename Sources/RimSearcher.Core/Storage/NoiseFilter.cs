namespace RimSearcher.Storage;

public static class NoiseFilter
{
    public static readonly IReadOnlySet<string> NoiseLeaves = new HashSet<string>(StringComparer.Ordinal)
    {
        "debugRandomId",
        "defNameHash",
        "shortHash",
        "index",
        "ignoreConfigErrors",
        "ignoreIllegalLabelCharacterConfigError",
        "m_CachedPtr",
        "nextSeed",
        "BackupPath",
    };

    public static readonly IReadOnlySet<string> PointerLeaves = new HashSet<string>(StringComparer.Ordinal)
    {
        "method",
        "method_ptr",
        "method_code",
        "invoke_impl",
    };

    public static readonly IReadOnlyList<string> NoisePrefixes =
    [
        "modContentPack.",
    ];

    public static string Leaf(string path)
    {
        var cut = path.Length;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c == '.') return path[(i + 1)..cut];
            if (c == ']')
            {
                var open = path.LastIndexOf('[', i);
                if (open >= 0) cut = open;
                i = open < 0 ? -1 : open;
            }
        }
        return path[..cut];
    }

    public static bool IsNoise(string path, string? value = null)
    {
        foreach (var p in NoisePrefixes)
            if (path.StartsWith(p, StringComparison.Ordinal)) return true;

        var leaf = Leaf(path);
        if (NoiseLeaves.Contains(leaf)) return true;
        return PointerLeaves.Contains(leaf) && IsPointerValue(value);
    }

    private static bool IsPointerValue(string? value)
    {
        if (value is not { Length: >= 10 }) return false;
        foreach (var c in value)
            if (c is < '0' or > '9') return false;
        return true;
    }
}
