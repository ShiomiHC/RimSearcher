namespace RimSearcher.Search;

public static class ClassNameShape
{
    public static bool Looks(string? value)
    {
        if (value is not { Length: > 2 }) return false;

        var segments = value.Split('.');
        foreach (var s in segments)
        {
            if (s.Length == 0 || !char.IsUpper(s[0])) return false;
            if (!s.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
        }

        if (segments.Length == 1)
            return segments[0].Skip(1).Any(c => char.IsUpper(c) || c == '_');

        return true;
    }

    public static string Tail(string value)
    {
        var i = value.LastIndexOf('.');
        return i < 0 ? value : value[(i + 1)..];
    }

    public static bool FromGameCode(string? value)
    {
        if (value is not { Length: > 0 }) return true;
        var i = value.IndexOf('.');
        if (i < 0) return true;
        var root = value[..i];
        return root is "RimWorld" or "Verse" or "LudeonTK";
    }
}
