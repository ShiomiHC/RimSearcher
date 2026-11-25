using System.Text;

namespace RimSearcher.Storage;

public static class FtsText
{
    public static bool IsCjk(char c) =>
        (c >= '㐀' && c <= '䶿') ||
        (c >= '一' && c <= '鿿') ||
        (c >= '豈' && c <= '﫿') ||
        (c >= '぀' && c <= 'ヿ');

    public static string ExpandCjkBigrams(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var extra = new StringBuilder();
        for (var i = 0; i + 1 < text.Length; i++)
            if (IsCjk(text[i]) && IsCjk(text[i + 1]))
                extra.Append(' ').Append(text[i]).Append(text[i + 1]);
        return extra.Length == 0 ? text : text + extra.ToString();
    }

    public static string SplitIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length * 2);
        sb.Append(name);
        var part = new StringBuilder();
        void Flush()
        {
            if (part.Length > 1) sb.Append(' ').Append(part);
            part.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-' or '.' or ' ') { Flush(); continue; }
            if (char.IsUpper(c) && part.Length > 0 && !char.IsUpper(name[i - 1])) Flush();
            part.Append(c);
        }
        Flush();
        return sb.ToString();
    }

    public static string ForIndex(string? text, bool identifier = false)
    {
        var s = identifier ? SplitIdentifier(text) : text ?? string.Empty;
        return ExpandCjkBigrams(s);
    }

    private static readonly char[] FtsSpecials = ['"', '*', ':', '(', ')', '^', '-', '+', ',', '\''];

    public static string BuildMatchQuery(string userQuery, bool prefix = true)
    {
        var terms = Tokenize(userQuery);
        if (terms.Count == 0) return "\"\"";
        var parts = new List<string>(terms.Count);
        foreach (var t in terms)
        {
            var clean = new string(t.Where(c => !FtsSpecials.Contains(c)).ToArray());
            if (clean.Length == 0) continue;
            if (clean.Length >= 2 && clean.All(IsCjk))
            {
                for (var i = 0; i + 1 < clean.Length; i++)
                    parts.Add($"\"{clean[i]}{clean[i + 1]}\"");
                if (clean.Length == 1) parts.Add($"\"{clean}\"");
            }
            else parts.Add(prefix ? $"\"{clean}\"*" : $"\"{clean}\"");
        }
        return parts.Count == 0 ? "\"\"" : string.Join(" ", parts);
    }

    public static IReadOnlyList<string> EffectiveTerms(string userQuery)
    {
        var result = new List<string>();
        foreach (var raw in Tokenize(userQuery))
        {
            var clean = new string(raw.Where(c => !FtsSpecials.Contains(c)).ToArray());
            var sb = new StringBuilder();
            foreach (var c in clean)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) result.Add(sb.ToString());
        }
        return result;
    }

    public static bool HasNothingToMatch(string userQuery) => EffectiveTerms(userQuery).Count == 0;

    private static List<string> Tokenize(string q)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in q)
        {
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
