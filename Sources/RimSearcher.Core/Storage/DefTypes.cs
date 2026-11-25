namespace RimSearcher.Storage;

public static class DefTypes
{
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string Leaf(string s) => s[(s.LastIndexOf('.') + 1)..];
        return string.Equals(Leaf(a), Leaf(b), StringComparison.OrdinalIgnoreCase);
    }
}
