namespace RimSearcher.Search;

public static class PathSegments
{
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

    private static bool SameSegment(string segment, string wanted)
    {
        var w = wanted.IndexOf('[', StringComparison.Ordinal);
        if (w < 0)
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0) segment = segment[..bracket];
            return string.Equals(segment, wanted, StringComparison.OrdinalIgnoreCase);
        }

        var s = segment.IndexOf('[', StringComparison.Ordinal);
        if (s < 0) return false;

        return segment.EndsWith("[]", StringComparison.Ordinal) || wanted.EndsWith("[]", StringComparison.Ordinal)
            ? string.Equals(segment[..s], wanted[..w], StringComparison.OrdinalIgnoreCase)
            : string.Equals(segment, wanted, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ContainerPrefix(string path)
    {
        var i = path.LastIndexOf("].", StringComparison.Ordinal);
        return i < 0 ? null : path[..(i + 2)];
    }

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

    public static bool IsWholeSegment(string path, IReadOnlyList<string> texts)
    {
        for (var i = 0; i < texts.Count; i++)
            if (IsWholeSegment(path, texts[i])) return true;
        return false;
    }

    public static IEnumerable<string> IndexPrefixes(string path)
    {
        for (var i = 0; i < path.Length; i++)
            if (path[i] == ']') yield return path[..(i + 1)];
    }
}
