using System.Text;

namespace RimSearcher.Search;

public sealed record CsDecl(string Kind, string Name, string? Owner, int StartLine, int EndLine)
{
    public string TypeParams { get; init; } = "";

    public string OwnerTypeParams { get; init; } = "";

    public string Modifiers { get; init; } = "";

    public string Display => Name + TypeParams;

    public string Qualified => Owner is { Length: > 0 } ? $"{Owner}{OwnerTypeParams}.{Display}" : Display;

    public int Lines => EndLine - StartLine + 1;
}

public static class CsOutline
{
    public static IReadOnlyList<CsDecl> Scan(IReadOnlyList<string> lines) => new Scanner(lines).Run();

    internal static bool IsType(string kind)
        => kind is "class" or "struct" or "interface" or "record" or "enum";

    private sealed record Frame(CsDecl? Decl, int Start);

    private sealed class Scanner(IReadOnlyList<string> lines)
    {
        private readonly List<CsDecl> _found = [];
        private readonly Stack<Frame> _open = new();
        private readonly StringBuilder _pending = new();
        private int _pendingStart;
        private int _pendingEnd;
        private int _brackets;
        private bool _rightSide;
        private int _exprBraces;
        private int _lastProperty = -1;
        private bool _inInitializer;

        public IReadOnlyList<CsDecl> Run()
        {
            var code = new CsCodeReader(lines);
            while (code.Next(out var ch))
            {
                if (_exprBraces > 0)
                {
                    if (ch == '{') _exprBraces++;
                    else if (ch == '}') _exprBraces--;
                    continue;
                }

                switch (ch)
                {
                    case '{': OpenBrace(); break;
                    case '}': CloseBrace(code.Line); break;
                    case ';': Semicolon(code.Line); break;
                    case ',' when EnumComma(): break;
                    default: Append(ch, code.Line); break;
                }
            }
            return _found;
        }

        private void OpenBrace()
        {
            var declarable = Declarable();
            if (declarable && (_brackets > 0 || _rightSide))
            {
                _exprBraces = 1;
                return;
            }

            var decl = declarable ? CsHeader.Classify(CsHeader.Normalize(_pending.ToString()), OwnerOf()) : null;
            _open.Push(new Frame(decl, decl is null ? 0 : _pendingStart));
            Restart();
            _lastProperty = -1;
        }

        private void CloseBrace(int line)
        {
            if (InEnum() && CsHeader.EnumMember(_pending.ToString(), _open.Peek().Decl!) is { } last)
                _found.Add(last with { StartLine = _pendingStart, EndLine = _pendingEnd });

            _lastProperty = -1;
            if (_open.Count > 0)
            {
                var frame = _open.Pop();
                if (frame.Decl is { } d)
                {
                    _found.Add(d with { StartLine = Backfill(lines, frame.Start), EndLine = line });
                    if (d.Kind == "property") _lastProperty = _found.Count - 1;
                }
            }
            Restart();
        }

        private bool EnumComma()
        {
            if (!InEnum() || CsHeader.EnumMember(_pending.ToString(), _open.Peek().Decl!) is not { } member) return false;
            _found.Add(member with { StartLine = _pendingStart, EndLine = _pendingEnd });
            Restart();
            return true;
        }

        private void Semicolon(int line)
        {
            if (_inInitializer)
            {
                _found[_lastProperty] = _found[_lastProperty] with { EndLine = line };
            }
            else if (Declarable())
            {
                var decl = CsHeader.Classify(CsHeader.Normalize(_pending.ToString()), OwnerOf(), bodyless: true);
                var inType = _open.Count > 0 && _open.Peek().Decl is { } p && (IsType(p.Kind) || p.Kind == CsHeader.Extension);
                if (decl is not null && (inType || IsType(decl.Kind) || decl.Kind == "delegate"))
                    _found.Add(decl with { StartLine = Backfill(lines, _pendingStart), EndLine = line });
            }
            Restart();
            _lastProperty = -1;
        }

        private void Append(char ch, int line)
        {
            if (_pending.Length == 0)
            {
                if (char.IsWhiteSpace(ch)) return;
                _pendingStart = line;
                if (_lastProperty >= 0 && ch == '=') _inInitializer = true;
                else _lastProperty = -1;
            }
            if (!char.IsWhiteSpace(ch)) _pendingEnd = line;

            switch (ch)
            {
                case '(' or '[': _brackets++; break;
                case ')' or ']': if (_brackets > 0) _brackets--; break;
                case '=' when _brackets == 0 && !_rightSide && !CsHeader.InOperatorToken(_pending.ToString()):
                    _rightSide = true;
                    break;
            }
            _pending.Append(ch);
        }

        private void Restart()
        {
            _pending.Clear();
            _brackets = 0;
            _rightSide = false;
            _inInitializer = false;
        }

        private CsDecl? OwnerOf()
            => _open.FirstOrDefault(f => f.Decl is not null && IsType(f.Decl.Kind))?.Decl;

        private bool Declarable()
            => _open.Count == 0 ||
               (_open.Peek().Decl is { } p && (p.Kind == "namespace" || p.Kind == CsHeader.Extension || IsType(p.Kind)));

        private bool InEnum()
            => _open.Count > 0 && _open.Peek().Decl is { Kind: "enum" };

        private static int Backfill(IReadOnlyList<string> lines, int start)
        {
            var i = start;
            while (i > 1)
            {
                var above = lines[i - 2].Trim();
                if (above.StartsWith("//", StringComparison.Ordinal) ||
                    (above.StartsWith('[') && above.EndsWith(']')))
                    i--;
                else break;
            }
            return i;
        }
    }
}
