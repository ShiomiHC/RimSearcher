using System.Text;

namespace RimSearcher.Search;

internal static class CsHeader
{
    internal const string Extension = "extension";

    internal static CsDecl? EnumMember(string raw, CsDecl owner)
    {
        var header = Normalize(raw);
        if (header.Length == 0) return null;
        if (header.Count(c => c == '[') != header.Count(c => c == ']')) return null;
        header = StripAttributes(header);
        header = StripInitializer(header);
        var name = LastIdentifier(header);
        if (name is null) return null;
        return new CsDecl("enum-member", name, owner.Name, 0, 0)
            { OwnerTypeParams = owner.TypeParams ?? "" };
    }

    internal static string Normalize(string header)
    {
        var sb = new StringBuilder(header.Length);
        var space = true;
        foreach (var ch in header)
        {
            if (char.IsWhiteSpace(ch)) { if (!space) { sb.Append(' '); space = true; } continue; }
            sb.Append(ch);
            space = false;
        }
        return sb.ToString().Trim();
    }

    internal static CsDecl? Classify(string header, CsDecl? owner, bool bodyless = false)
    {
        var decl = ClassifyCore(header, owner, bodyless);
        return decl is null ? null : decl with { Modifiers = LeadingModifiers(header) };
    }

    private static CsDecl? ClassifyCore(string header, CsDecl? owner, bool bodyless = false)
    {
        if (header.Length == 0) return null;

        var ownerName = owner?.Name;
        var ownerParams = owner?.TypeParams ?? "";

        header = StripAttributes(header);
        if (header.StartsWith('#')) return null;

        if (bodyless && RightSideAt(header) is var at and >= 0)
        {
            if (header.AsSpan(at).StartsWith("=>")) bodyless = false;
            header = header[..at].TrimEnd();
        }

        header = StripConstraints(header);

        var head = SkipModifiers(header);

        if (head.StartsWith('~'))
        {
            var dtor = head[1..].TrimStart();
            var len = 0;
            while (len < dtor.Length && IsIdentChar(dtor[len])) len++;
            if (len > 0)
                return new CsDecl("destructor", "~" + dtor[..len], ownerName, 0, 0)
                    { OwnerTypeParams = ownerParams };
        }

        if (head.StartsWith("delegate ", StringComparison.Ordinal) || head.StartsWith("delegate(", StringComparison.Ordinal))
        {
            var dp = TopLevelParen(head);
            if (dp > 0 && IdentifierBefore(head, dp) is { } dname)
                return new CsDecl("delegate", dname, ownerName, 0, 0) { OwnerTypeParams = ownerParams };
        }

        if (head.StartsWith("extension(", StringComparison.Ordinal) || head.StartsWith("extension<", StringComparison.Ordinal))
            return new CsDecl(Extension, Extension, ownerName, 0, 0) { OwnerTypeParams = ownerParams };

        foreach (var kw in TypeKeywords)
        {
            var name = AfterKeyword(header, kw);
            if (name is not null)
                return new CsDecl(kw, name, ownerName, 0, 0)
                {
                    TypeParams = TypeParamsAfter(header, kw, name),
                    OwnerTypeParams = ownerParams,
                };
        }

        if (header.StartsWith("namespace ", StringComparison.Ordinal))
            return new CsDecl("namespace", header["namespace ".Length..].Trim().TrimEnd(';'), ownerName, 0, 0)
                { OwnerTypeParams = ownerParams };

        var op = TryOperator(header);
        if (op is not null)
            return new CsDecl("operator", op, ownerName, 0, 0) { OwnerTypeParams = ownerParams };

        var paren = TopLevelParen(header);
        if (paren > 0)
        {
            var name = IdentifierBefore(header, paren);
            if (name is null) return null;
            var kind = name == ownerName ? "constructor" : "method";
            return new CsDecl(kind, name, ownerName, 0, 0) { OwnerTypeParams = ownerParams };
        }

        if (IsIndexer(header))
            return new CsDecl("indexer", "this", ownerName, 0, 0) { OwnerTypeParams = ownerParams };

        var named = StripFixedSize(head);
        var last = LastIdentifier(named);
        if (last is null || named.TrimEnd().Length == last.Length) return null;
        return new CsDecl(bodyless ? "field" : "property", last, ownerName, 0, 0)
            { OwnerTypeParams = ownerParams };
    }

    private static int RightSideAt(string header)
    {
        var depth = 0;
        for (var i = 0; i < header.Length; i++)
            switch (header[i])
            {
                case '(' or '[': depth++; break;
                case ')' or ']': if (depth > 0) depth--; break;
                case '=' when depth == 0 && !InOperatorToken(header[..i]): return i;
            }
        return -1;
    }

    internal static bool InOperatorToken(string text)
    {
        var at = LastWordBefore(text, "operator", text.Length);
        return at >= 0 && (at == 0 || text[at - 1] != '@') && text.IndexOf('(', at) < 0;
    }

    private static string StripAttributes(string header)
    {
        while (header.StartsWith('['))
        {
            var depth = 0;
            var end = -1;
            for (var i = 0; i < header.Length && end < 0; i++)
            {
                if (header[i] == '[') depth++;
                else if (header[i] == ']' && --depth == 0) end = i;
            }
            if (end < 0) return header;
            header = header[(end + 1)..].TrimStart();
        }
        return header;
    }

    private static bool IsIndexer(string header)
    {
        var at = LastWordBefore(header, "this", header.Length);
        if (at < 0 || (at > 0 && header[at - 1] == '@')) return false;
        var after = header.AsSpan(at + 4).TrimStart();
        return after.IsEmpty || after[0] == '[';
    }

    private static string StripFixedSize(string head)
    {
        var trimmed = head.TrimEnd();
        if (!trimmed.EndsWith(']')) return trimmed;
        var open = trimmed.LastIndexOf('[');
        return open > 0 ? trimmed[..open].TrimEnd() : trimmed;
    }

    private static string LeadingModifiers(string header)
    {
        header = StripAttributes(header);

        var taken = new List<string>();
        foreach (var word in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ModifierWords.Contains(word)) break;
            taken.Add(word);
        }
        return string.Join(' ', taken);
    }

    private static string SkipModifiers(string header)
    {
        var i = 0;
        while (true)
        {
            var end = header.IndexOf(' ', i);
            if (end < 0 || !ModifierWords.Contains(header[i..end])) return header[i..];
            i = end + 1;
        }
    }

    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "file",
        "static", "readonly", "const", "volatile", "required", "ref",
        "abstract", "virtual", "override", "sealed", "new",
        "extern", "partial", "async", "unsafe", "fixed",
    };

    private static readonly string[] TypeKeywords = ["class", "struct", "interface", "record", "enum"];

    private static string TypeParamsAfter(string header, string keyword, string name)
    {
        var at = header.IndexOf($"{keyword} {name}", StringComparison.Ordinal);
        if (at < 0) return "";
        var i = at + keyword.Length + 1 + name.Length;
        if (i >= header.Length || header[i] != '<') return "";

        var angle = 0;
        for (var j = i; j < header.Length; j++)
        {
            if (header[j] == '<') angle++;
            else if (header[j] == '>' && --angle == 0) return header[i..(j + 1)];
        }
        return "";
    }

    private static string? AfterKeyword(string header, string keyword)
    {
        var from = 0;
        while (true)
        {
            var at = header.IndexOf(keyword, from, StringComparison.Ordinal);
            if (at < 0) return null;
            from = at + keyword.Length;

            var before = at == 0 || !IsIdentChar(header[at - 1]);
            if (!before) continue;
            if (from >= header.Length || header[from] != ' ') continue;

            var rest = header[(from + 1)..];
            var len = 0;
            while (len < rest.Length && IsIdentChar(rest[len])) len++;
            if (len == 0) continue;
            return rest[..len];
        }
    }

    private static int TopLevelParen(string header)
    {
        var angle = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '(' when angle == 0:
                {
                    var close = MatchParen(header, i);
                    if (close < 0) return i;
                    var j = close + 1;
                    while (j < header.Length && header[j] is ' ' or '[' or ']' or '?' or ',') j++;
                    if (j < header.Length && IsIdentChar(header[j])) { i = close; continue; }
                    return i;
                }
            }
        }
        return -1;
    }

    private static int MatchParen(string header, int open)
    {
        var depth = 0;
        for (var i = open; i < header.Length; i++)
        {
            if (header[i] == '(') depth++;
            else if (header[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static string? TryOperator(string header)
    {
        var at = LastWordBefore(header, "operator", header.Length);
        if (at < 0) return null;

        var start = at;
        var left = at - 1;
        while (left >= 0 && header[left] == ' ') left--;
        if (left >= 0)
        {
            var wordEnd = left + 1;
            var wordStart = wordEnd;
            while (wordStart > 0 && IsIdentChar(header[wordStart - 1])) wordStart--;
            var word = header[wordStart..wordEnd];
            if (word is "implicit" or "explicit") start = wordStart;
        }

        var i = at + "operator".Length;
        while (i < header.Length && header[i] == ' ') i++;

        var isChecked = false;
        if (header.AsSpan(i).StartsWith("checked") &&
            (i + 7 >= header.Length || !IsIdentChar(header[i + 7])))
        {
            isChecked = true;
            i += 7;
            while (i < header.Length && header[i] == ' ') i++;
        }

        if (i >= header.Length) return null;

        var tokenStart = i;
        if (header[i] == '(')
        {
            var close = MatchParen(header, i);
            if (close < 0) return null;
            i = close + 1;
        }
        else if (IsOpSymbol(header[i]))
        {
            while (i < header.Length && IsOpSymbol(header[i])) i++;
        }
        else if (IsIdentChar(header[i]))
        {
            while (i < header.Length)
            {
                while (i < header.Length && (IsIdentChar(header[i]) || header[i] is '.' or ':')) i++;
                if (i < header.Length && header[i] == '<')
                {
                    var angle = 0;
                    for (; i < header.Length; i++)
                    {
                        if (header[i] == '<') angle++;
                        else if (header[i] == '>' && --angle == 0) { i++; break; }
                    }
                    if (i < header.Length && header[i] == '.') continue;
                }
                break;
            }
            while (i < header.Length && header[i] is ' ') i++;
            while (i < header.Length && header[i] is '?' or '[' or ']' or '*' or ',') i++;
        }
        else return null;

        var token = header[tokenStart..i].Trim();
        if (token.Length == 0) return null;

        while (i < header.Length && header[i] == ' ') i++;
        if (i >= header.Length || header[i] != '(') return null;

        var sb = new StringBuilder();
        if (start < at)
        {
            sb.Append(header[start..at].Trim());
            sb.Append(' ');
        }
        sb.Append("operator");
        if (isChecked) sb.Append(" checked");
        sb.Append(' ');
        sb.Append(token);
        return sb.ToString();
    }

    private static bool IsOpSymbol(char c)
        => c is '+' or '-' or '!' or '~' or '*' or '/' or '%' or '&' or '|' or '^' or '<' or '>' or '=';

    private static int LastWordBefore(string header, string word, int end)
    {
        var found = -1;
        var from = 0;
        while (from < end)
        {
            var at = header.IndexOf(word, from, end - from, StringComparison.Ordinal);
            if (at < 0) break;
            var after = at + word.Length;
            var okBefore = at == 0 || !IsIdentChar(header[at - 1]);
            var okAfter = after >= end || !IsIdentChar(header[after]);
            if (okBefore && okAfter) found = at;
            from = at + 1;
        }
        return found;
    }

    private static string? IdentifierBefore(string header, int paren)
    {
        var i = paren - 1;
        while (i >= 0 && header[i] == ' ') i--;
        if (i >= 0 && header[i] == '>')
        {
            var angle = 0;
            while (i >= 0)
            {
                if (header[i] == '>') angle++;
                else if (header[i] == '<') { angle--; if (angle == 0) { i--; break; } }
                i--;
            }
            while (i >= 0 && header[i] == ' ') i--;
        }

        var end = i + 1;
        while (i >= 0 && IsIdentChar(header[i])) i--;
        var name = header[(i + 1)..end];
        return name.Length == 0 ? null : name;
    }

    private static string StripConstraints(string header)
    {
        var angle = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': angle++; continue;
                case '>': if (angle > 0) angle--; continue;
            }
            if (angle != 0 || header[i] != 'w') continue;
            if (i > 0 && IsIdentChar(header[i - 1])) continue;
            if (header.AsSpan(i).StartsWith("where ")) return header[..i].TrimEnd();
        }
        return header;
    }

    private static string StripInitializer(string header)
    {
        var eq = header.IndexOf('=');
        return eq < 0 ? header : header[..eq].TrimEnd();
    }

    private static string? LastIdentifier(string header)
    {
        var end = header.Length;
        while (end > 0 && !IsIdentChar(header[end - 1])) end--;
        var start = end;
        while (start > 0 && IsIdentChar(header[start - 1])) start--;
        return end > start && !char.IsDigit(header[start]) ? header[start..end] : null;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
