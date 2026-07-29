using System.Text;

namespace RimSearcher.Config;

/// <summary>
/// TOML 子集读写器。只支持本项目 config.toml 用到的形状:顶层键、<c>[表]</c>、字符串、
/// 整数、布尔、字符串数组、行注释。
///
/// 为什么不引第三方库:CLI 侧的运行时依赖目前只有 SQLite 一项,而配置解析的错误消息是要
/// 直接给调用方看的(06「错误消息是一等公民」)——自己解析才能把行号和期望形状写进消息里。
/// 遇到本子集之外的语法一律显式报错,不静默忽略。
/// </summary>
public static class Toml
{
    public sealed class Table
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Table> Tables { get; } = new(StringComparer.Ordinal);

        public string? String(string key) => Values.TryGetValue(key, out var v) ? v as string : null;
        public bool Bool(string key, bool fallback = false) => Values.TryGetValue(key, out var v) && v is bool b ? b : fallback;
        public int Int(string key, int fallback) => Values.TryGetValue(key, out var v) && v is long l ? (int)l : fallback;
        public IReadOnlyList<string> Strings(string key)
            => Values.TryGetValue(key, out var v) && v is List<string> l ? l : [];
        public Table Sub(string key) => Tables.TryGetValue(key, out var t) ? t : new Table();
    }

    public static Table Parse(string text, string originForErrors)
    {
        var root = new Table();
        var current = root;
        var rawLines = text.Split('\n');

        for (var index = 0; index < rawLines.Length; index++)
        {
            var lineNo = index + 1;
            var line = StripComment(rawLines[index]).Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                    throw new TomlError($"{originForErrors}:{lineNo}: table header must end with ']'.");
                var name = line[1..^1].Trim();
                if (name.Length == 0)
                    throw new TomlError($"{originForErrors}:{lineNo}: empty table name.");
                current = root;
                foreach (var part in name.Split('.'))
                {
                    var key = Unquote(part.Trim());
                    if (!current.Tables.TryGetValue(key, out var next))
                        current.Tables[key] = next = new Table();
                    current = next;
                }
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new TomlError($"{originForErrors}:{lineNo}: expected 'key = value'.");

            var k = Unquote(line[..eq].Trim());
            var v = line[(eq + 1)..].Trim();

            // 数组可以跨行。一串路径写成竖排是最自然的形态,强迫它挤成一行只是解析器偷懒。
            if (v.StartsWith('[') && !v.EndsWith(']'))
            {
                var depth = Depth(v);
                while (depth > 0 && index + 1 < rawLines.Length)
                {
                    index++;
                    var more = StripComment(rawLines[index]).Trim();
                    v += more;
                    depth += Depth(more);
                }
                if (depth > 0)
                    throw new TomlError($"{originForErrors}:{lineNo}: the array opened here is never closed with ']'.");
            }

            current.Values[k] = ParseValue(v, originForErrors, lineNo);
        }

        return root;
    }

    public static Table Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path), Path.GetFileName(path)) : new Table();

    private static object ParseValue(string v, string origin, int lineNo)
    {
        if (v.Length == 0) throw new TomlError($"{origin}:{lineNo}: missing value.");

        if (v[0] == '[')
        {
            if (v[^1] != ']')
                // 跨行数组是支持的(上面已经把续行接了起来),所以这里**不能**说
                // 「数组必须写在一行」—— 那条规则早就不存在了,照着改只会更糊涂。
                throw new TomlError($"{origin}:{lineNo}: there is text after the array's closing ']'.");
            var items = new List<string>();
            foreach (var part in SplitTopLevel(v[1..^1]))
            {
                var t = part.Trim();
                if (t.Length == 0) continue;
                items.Add(Unquote(t));
            }
            return items;
        }

        if (v is "true" or "false") return v == "true";
        if (long.TryParse(v, out var n)) return n;
        if (v[0] is '"' or '\'')
        {
            // 引号没闭合过去是静默接受的:Unquote 只剥掉开头那个引号,于是
            // `a = "D:/games` 会被当成合法值 D:/games 存下来。用户手误漏一个引号,
            // 得到的不是报错而是一个**看起来对**的配置,后面所有找不到目录都指着它。
            // 引号必须成对,而且要在同一行 —— 本子集不支持多行字符串,不如直接说破。
            if (v.Length < 2 || v[^1] != v[0])
                throw new TomlError(
                    $"{origin}:{lineNo}: the string opened with {v[0]} is never closed on this line.");
            return Unquote(v);
        }

        throw new TomlError($"{origin}:{lineNo}: value '{v}' is not a quoted string, number, boolean, or array. " +
                            "Quote it if it is meant to be text.");
    }

    /// <summary>方括号净深度,引号内的不算。</summary>
    private static int Depth(string s)
    {
        var depth = 0;
        var inQuote = '\0';
        foreach (var c in s)
        {
            if (inQuote != '\0') { if (c == inQuote) inQuote = '\0'; continue; }
            if (c is '"' or '\'') { inQuote = c; continue; }
            if (c == '[') depth++;
            else if (c == ']') depth--;
        }
        return depth;
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var sb = new StringBuilder();
        var inQuote = '\0';
        foreach (var c in s)
        {
            if (inQuote != '\0') { if (c == inQuote) inQuote = '\0'; sb.Append(c); continue; }
            if (c is '"' or '\'') { inQuote = c; sb.Append(c); continue; }
            if (c == ',') { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string StripComment(string line)
    {
        var inQuote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuote != '\0') { if (c == inQuote) inQuote = '\0'; continue; }
            if (c is '"' or '\'') { inQuote = c; continue; }
            if (c == '#') return line[..i];
        }
        return line;
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2) return s;
        if (s[0] == '\'' && s[^1] == '\'') return s[1..^1];   // literal string:不处理转义
        if (s[0] != '"' || s[^1] != '"') return s;

        var body = s[1..^1];
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length) { sb.Append(body[i]); continue; }
            var next = body[i + 1];
            switch (next)
            {
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case '"': sb.Append('"'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                // 未知转义原样保留反斜杠。严格 TOML 会报错,但配置里最常见的字符串就是
                // Windows 路径("D:\SteamLibrary\..."),把 \S 悄悄吃成 S 是最坏的一种结果。
                default: sb.Append('\\'); break;
            }
        }
        return sb.ToString();
    }

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

public sealed class TomlError(string message) : Exception(message);
