namespace RimSearcher.Search;

public sealed record EntrySelector(string Before, string Key, string Value, string After)
{
    public string BaseText => Before + "[]" + After;

    public string KeyShape => Before + "[]." + Key;

    public static EntrySelector? Parse(string text, out string? problem)
    {
        problem = null;
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0) return null;

        var open = text.LastIndexOf('[', eq);
        var close = text.IndexOf(']', eq);
        if (open < 0 || close < 0 || text.IndexOf(']', open) < eq ||
            text.IndexOf('[', eq) is var inner && inner >= 0 && inner < close)
        {
            problem = "「=」只能写在列表下标里，如 statBases[stat=Mass].value。";
            return null;
        }
        if (text.IndexOf('=', close) >= 0)
        {
            problem = "一条路径只接受一个 [field=value]，此路径写有不止一个。";
            return null;
        }

        var before = text[..open];
        var key = text[(open + 1)..eq];
        var value = text[(eq + 1)..close];
        var after = text[(close + 1)..];

        if (before.Length == 0 || before.EndsWith('.') || before.EndsWith(']'))
            problem = "方括号前须写出列表名，如 statBases[stat=Mass].value。";
        else if (key.Length == 0 || !key.Split('.').All(IsIdentifier))
            problem = $"「=」前的 '{key}' 须为列表条目的字段，如 stat。";
        else if (value.Length == 0)
            problem = "方括号内「=」后须有值，如 statBases[stat=Mass]。";
        else if (!after.StartsWith('.') || after.Length == 1)
            problem = $"方括号后须写明读取条目的哪个字段，如 {before}[{key}={value}].value。";

        return problem is null ? new EntrySelector(before, key, value, after) : null;
    }

    public string Reinsert(string shape)
    {
        var end = shape.Length;
        for (var i = 0; i <= After.Count(c => c == '[') && end > 0; i++)
            end = shape.LastIndexOf("[]", end - 1, StringComparison.Ordinal);
        return end < 0 ? shape : shape[..end] + $"[{Key}={Value}]" + shape[(end + 2)..];
    }

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
