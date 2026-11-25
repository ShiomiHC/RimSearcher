namespace RimSearcher.Search;

internal sealed class CsCodeReader(IReadOnlyList<string> lines)
{
    private enum Lex { Code, LineComment, BlockComment, String, InterpolatedString, InterpolatedVerbatim, VerbatimString, RawString, Char }

    private Lex _state = Lex.Code;
    private int _quoteRun;
    private int _row;
    private int _col;

    private readonly Stack<(Lex Text, int Braces)> _holes = new();

    public int Line { get; private set; }

    public bool Next(out char ch)
    {
        while (_row < lines.Count)
        {
            var line = lines[_row];
            if (_col >= line.Length)
            {
                Line = ++_row;
                _col = 0;
                if (_state == Lex.LineComment) _state = Lex.Code;
                ch = '\n';
                return true;
            }

            var c = _col++;
            var cur = line[c];
            var next = c + 1 < line.Length ? line[c + 1] : '\0';
            if (_state != Lex.Code)
            {
                Inside(line, cur, next);
                continue;
            }
            if (EntersLiteral(line, c, cur, next)) continue;

            if (_holes.Count > 0)
            {
                var hole = _holes.Pop();
                if (cur == '{') hole.Braces++;
                else if (cur == '}' && hole.Braces-- == 0) { _state = hole.Text; continue; }
                _holes.Push(hole);
                continue;
            }

            Line = _row + 1;
            ch = cur;
            return true;
        }
        ch = '\0';
        return false;
    }

    private void Inside(string line, char cur, char next)
    {
        switch (_state)
        {
            case Lex.BlockComment:
                if (cur == '*' && next == '/') { _state = Lex.Code; _col++; }
                break;

            case Lex.String:
                if (cur == '\\') _col++;
                else if (cur == '"') _state = Lex.Code;
                break;

            case Lex.InterpolatedString:
            case Lex.InterpolatedVerbatim:
                if (_state == Lex.InterpolatedString && cur == '\\') { _col++; break; }
                if (_state == Lex.InterpolatedVerbatim && cur == '"' && next == '"') { _col++; break; }
                if (cur == '"') { _state = Lex.Code; break; }
                if (cur == '{' && next == '{') { _col++; break; }
                if (cur == '{') { _holes.Push((_state, 0)); _state = Lex.Code; }
                break;

            case Lex.VerbatimString:
                if (cur == '"' && next == '"') _col++;
                else if (cur == '"') _state = Lex.Code;
                break;

            case Lex.RawString:
                if (cur != '"') break;
                var run = 1;
                while (_col - 1 + run < line.Length && line[_col - 1 + run] == '"') run++;
                _col += run - 1;
                if (run >= _quoteRun) _state = Lex.Code;
                break;

            case Lex.Char:
                if (cur == '\\') _col++;
                else if (cur == '\'') _state = Lex.Code;
                break;
        }
    }

    private bool EntersLiteral(string line, int c, char cur, char next)
    {
        if (cur == '/' && next == '/') { _state = Lex.LineComment; _col = line.Length; return true; }
        if (cur == '/' && next == '*') { _state = Lex.BlockComment; _col++; return true; }
        if (cur == '\'') { _state = Lex.Char; return true; }
        if (cur == '$' && next == '"' && !(c + 3 < line.Length && line[c + 2] == '"' && line[c + 3] == '"'))
        {
            _state = Lex.InterpolatedString;
            _col++;
            return true;
        }
        if ((cur == '$' && next == '@' || cur == '@' && next == '$') && c + 2 < line.Length && line[c + 2] == '"')
        {
            _state = Lex.InterpolatedVerbatim;
            _col += 2;
            return true;
        }
        if (cur == '@' && next == '"') { _state = Lex.VerbatimString; _col++; return true; }
        if (cur == '"')
        {
            var run = 1;
            while (c + run < line.Length && line[c + run] == '"') run++;
            if (run >= 3) { _state = Lex.RawString; _quoteRun = run; _col += run - 1; }
            else _state = Lex.String;
            return true;
        }
        return false;
    }
}
