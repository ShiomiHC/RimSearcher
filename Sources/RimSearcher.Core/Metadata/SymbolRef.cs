namespace RimSearcher.Metadata;

public sealed record SymbolRef
{
    public required string TypeName { get; init; }

    public string? MemberName { get; init; }

    private string? Tail { get; init; }

    public string? Parameters { get; init; }

    public required string Raw { get; init; }

    public bool HasMember => MemberName is { Length: > 0 };

    public static bool IsConstructorName(string name)
        => name is ".ctor" or "#ctor" or "ctor" or ".cctor" or "#cctor";

    public static SymbolRef Parse(string raw)
    {
        var s = raw.Trim();

        if (s.Length > 2 && s[1] == ':' && "MTPFEN".Contains(char.ToUpperInvariant(s[0])))
            s = s[2..];

        string? parameters = null;
        var paren = s.IndexOf('(');
        if (paren >= 0)
        {
            var close = s.LastIndexOf(')');
            parameters = close > paren ? s[(paren + 1)..close] : s[(paren + 1)..];
            s = s[..paren];
        }

        s = Search.GenericName.Canonical(s.Trim());

        var dc = s.IndexOf("::", StringComparison.Ordinal);
        if (dc >= 0)
            return new SymbolRef
            {
                TypeName = Normalize(s[..dc]),
                MemberName = EmptyToNull(Search.GenericName.Strip(s[(dc + 2)..])),
                Parameters = parameters,
                Raw = raw,
            };

        foreach (var suffix in new[] { "..ctor", ".#ctor", "..cctor", ".#cctor" })
            if (s.EndsWith(suffix, StringComparison.Ordinal))
                return new SymbolRef
                {
                    TypeName = Normalize(s[..^suffix.Length]),
                    MemberName = suffix.Contains("cctor") ? ".cctor" : ".ctor",
                    Parameters = parameters,
                    Raw = raw,
                };

        var lastDot = s.LastIndexOf('.');
        if (lastDot < 0)
            return new SymbolRef { TypeName = Normalize(s), Parameters = parameters, Raw = raw };

        var head = s[..lastDot];
        var tail = s[(lastDot + 1)..];

        return new SymbolRef
        {
            TypeName = Normalize(head),
            MemberName = EmptyToNull(Search.GenericName.Strip(tail)),
            Tail = tail,
            Parameters = parameters,
            Raw = raw,
        };
    }

    private static string Normalize(string s) => s.Replace('/', '+').Trim();

    private static string? EmptyToNull(string s) => s.Trim() is { Length: > 0 } t ? t : null;

    public SymbolRef AsWholeType()
    {
        if (!HasMember) return this;
        return new SymbolRef { TypeName = $"{TypeName}.{Tail ?? MemberName}", Parameters = Parameters, Raw = Raw };
    }

    public SymbolRef AsBareMember()
        => new() { TypeName = "", MemberName = HasMember ? MemberName : Search.GenericName.Strip(TypeName), Parameters = Parameters, Raw = Raw };
}
