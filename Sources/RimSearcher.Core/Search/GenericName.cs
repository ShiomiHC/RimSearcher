namespace RimSearcher.Search;

public static class GenericName
{
    public static string Canonical(string name)
    {
        var kept = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '<' && i > 0 && IsNameChar(name[i - 1]) && Close(name, i) is { } close)
            {
                kept.Append('`').Append(Count(name.AsSpan(i + 1, close - i - 1)));
                i = close;
            }
            else kept.Append(name[i]);
        }
        return kept.ToString();

        static int? Close(string s, int open)
        {
            var depth = 0;
            for (var i = open; i < s.Length; i++)
                if (s[i] == '<') depth++;
                else if (s[i] == '>' && --depth == 0) return i;
            return null;
        }
    }

    public static string Strip(string name) => Marker.Replace(Canonical(name), "");

    public static (string Name, int? Arity) Split(string name)
    {
        var canonical = Canonical(name);
        return canonical.LastIndexOf('`') is var at and > 0 && int.TryParse(canonical[(at + 1)..], out var arity)
            ? (canonical[..at], arity)
            : (canonical, null);
    }

    public static bool Is(string asked, string name, string typeParams)
    {
        var (bare, arity) = Split(asked);
        return bare == name && (arity is null || arity == Arity(typeParams));
    }

    public static int Arity(string typeParams)
        => typeParams.Length < 2 ? 0 : Count(typeParams.AsSpan(1, typeParams.Length - 2));

    private static int Count(ReadOnlySpan<char> inside)
    {
        var (count, depth) = (1, 0);
        foreach (var ch in inside)
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == ',' && depth == 0) count++;
        return count;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static readonly System.Text.RegularExpressions.Regex Marker =
        new(@"(?<=\w)`\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
