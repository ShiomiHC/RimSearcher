namespace RimSearcher.Output;

public static class Cjk
{
    public static string Join(params string[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            if (sb.Length > 0 && NeedsSpace(sb[^1], part[0])) sb.Append(' ');
            sb.Append(part);
        }
        return sb.ToString();
    }

    private static bool NeedsSpace(char left, char right)
        => (IsIdeograph(left) && IsWesternStart(right)) || (IsWesternEnd(left) && IsIdeograph(right));

    private static bool IsIdeograph(char c)
        => c is (>= '⺀' and <= '⿟') or (>= '぀' and <= 'ヿ')
             or (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿') or (>= '豈' and <= '﫿');

    private static bool IsWesternEnd(char c) => char.IsAsciiLetterOrDigit(c) || c is '#' or ')' or ']' or '\'' or '"' or '`' or '%';

    private static bool IsWesternStart(char c) => char.IsAsciiLetterOrDigit(c) || c is '(' or '[' or '\'' or '"' or '`' or '-' or '<';
}
