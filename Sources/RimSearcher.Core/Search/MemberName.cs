namespace RimSearcher.Search;

public static class MemberName
{
    public static List<CsDecl> Hits(IEnumerable<CsDecl> decls, string member, string? type = null)
    {
        var (owner, name) = Split(member);
        var inType = decls.Where(d => d.Kind != "namespace" && Within(d, type) && Within(d, owner)).ToList();
        var exact = inType.Where(d => d.Name == name).ToList();
        if (exact.Count > 0) return exact;
        var compiled = inType.Where(d => Compiled(d, name)).ToList();
        if (compiled.Count > 0) return compiled;

        var byCase = inType.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return byCase.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() == 1 ? byCase : [];

        static bool Within(CsDecl d, string? t) => t is not { Length: > 0 } || GenericName.Is(t, d.Owner ?? "", d.OwnerTypeParams);
    }

    public static bool MatchedByCase(IReadOnlyList<CsDecl> hits, string member)
    {
        var name = Split(member).Name;
        return hits.Count > 0 && hits[0].Name != name && string.Equals(hits[0].Name, name, StringComparison.OrdinalIgnoreCase);
    }

    public static (string? Owner, string Name) Split(string member)
    {
        if (member.Contains("operator ", StringComparison.Ordinal)) return (null, member);
        foreach (var compiled in new[] { ".ctor", ".cctor" })
            if (member.EndsWith(compiled, StringComparison.Ordinal))
                return (member.Length > compiled.Length + 1 ? member[..^(compiled.Length + 1)] : null, compiled);
        return member.LastIndexOf('.') is var dot and > 0 ? (member[..dot], member[(dot + 1)..]) : (null, member);
    }

    public static IReadOnlyList<string> Plain(string raw)
    {
        if (raw.Contains("operator ", StringComparison.Ordinal) || raw.StartsWith('~'))
            return [raw];

        var text = GenericName.Canonical(raw);
        if (text.Contains("<>", StringComparison.Ordinal)) return [raw];
        if (text.IndexOf(" --", StringComparison.Ordinal) is var option and >= 0) text = text[..option];

        if (Generated.Match(text) is { Success: true } generated)
        {
            var host = Tail(text[..generated.Index]);
            return [IsName(host) ? $"{host}.{generated.Groups[1].Value}" : generated.Groups[1].Value];
        }

        var kept = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch is '(' or '<') depth++;
            else if (ch is ')' or '>') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && ch is not ('\\' or '$' or '^')) kept.Append(ch);
        }

        var names = kept.ToString().Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(Tail).Where(IsName).Distinct(StringComparer.Ordinal).ToList();
        return names.Count > 0 ? names : [raw];
    }

    private static readonly System.Text.RegularExpressions.Regex Generated =
        new(@"(?:^|[.+])<(\w+)>\w*(?:\(.*\))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Tail(string piece)
    {
        var (owner, name) = Split(piece.Replace('+', '.'));
        name = GenericName.Strip(name);
        return owner is null ? name : $"{owner[(owner.LastIndexOf('.') + 1)..]}.{name}";
    }

    private static bool IsName(string piece)
    {
        var (owner, name) = Split(piece);
        return (owner is null || IsIdentifier(GenericName.Strip(owner))) && (name is ".ctor" or ".cctor" || IsIdentifier(name));
    }

    private static bool IsIdentifier(string piece)
        => piece.Length > 0 && piece.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '@');

    public static string Source(string member)
    {
        var bare = Split(member).Name;
        var name = Metadata.MetadataLookup.SourceNameOf(bare);
        return name == "Item" ? bare : name;
    }

    private static bool Compiled(CsDecl d, string member) => member switch
    {
        ".ctor" => d.Kind == "constructor" && !IsStatic(d),
        ".cctor" => d.Kind == "constructor" && IsStatic(d),
        "Finalize" => d.Kind == "destructor",
        "get_Item" or "set_Item" => d.Kind == "indexer",
        "op_Implicit" => d.Kind == "operator" && d.Name.StartsWith("implicit ", StringComparison.Ordinal),
        "op_Explicit" => d.Kind == "operator" && d.Name.StartsWith("explicit ", StringComparison.Ordinal),
        _ when Operators.TryGetValue(member, out var op) => d.Kind == "operator" && d.Name == "operator " + op,
        _ when member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal)
            => d.Kind == "property" && d.Name == member[4..],
        _ when member.StartsWith("add_", StringComparison.Ordinal)
            => d.Kind is "field" or "property" && d.Name == member[4..],
        _ when member.StartsWith("remove_", StringComparison.Ordinal)
            => d.Kind is "field" or "property" && d.Name == member[7..],
        _ => false,
    };

    private static bool IsStatic(CsDecl d) => d.Modifiers.Split(' ').Contains("static");

    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["op_Equality"] = "==", ["op_Inequality"] = "!=",
        ["op_LessThan"] = "<", ["op_GreaterThan"] = ">",
        ["op_LessThanOrEqual"] = "<=", ["op_GreaterThanOrEqual"] = ">=",
        ["op_Addition"] = "+", ["op_Subtraction"] = "-", ["op_Multiply"] = "*", ["op_Division"] = "/", ["op_Modulus"] = "%",
        ["op_UnaryPlus"] = "+", ["op_UnaryNegation"] = "-", ["op_LogicalNot"] = "!", ["op_OnesComplement"] = "~",
        ["op_Increment"] = "++", ["op_Decrement"] = "--", ["op_True"] = "true", ["op_False"] = "false",
        ["op_BitwiseAnd"] = "&", ["op_BitwiseOr"] = "|", ["op_ExclusiveOr"] = "^",
        ["op_LeftShift"] = "<<", ["op_RightShift"] = ">>", ["op_UnsignedRightShift"] = ">>>",
    };
}
