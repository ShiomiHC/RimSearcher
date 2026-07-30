using System.Text;

namespace RimSearcher.Search;

/// <summary>
/// 一段声明在文件里占的行。行号 1 起,两端都含。
///
/// <paramref name="Owner"/> 是最近的**类型**祖先(namespace 不算),用来把同名成员分开。
/// </summary>
public sealed record CsDecl(string Kind, string Name, string? Owner, int StartLine, int EndLine)
{
    /// <summary>`Pawn.Kill` 这样的显示名;顶层类型就是它自己的名字。</summary>
    public string Qualified => Owner is { Length: > 0 } ? $"{Owner}.{Name}" : Name;

    public int Lines => EndLine - StartLine + 1;
}

/// <summary>
/// 大括号配平出来的 C# 轮廓 —— **不是**语法分析。
///
/// 为什么不接 Roslyn:这一支从头到尾没有它,而为了「读一个方法体」把整个编译器前端拉进
/// 一个查快照的 CLI,代价与收益差着量级。反编译产物的形状又恰好是所有 C# 里最规整的一种
/// (ILSpy 生成,没有奇诡的格式化),配平括号足够把方法体的边界找准。
///
/// 但它的**边界必须说出去**,不能让调用方以为拿到的是一次解析(R51:能力边界写进它作用的
/// 那个块)。三件事这里做不到,<see cref="ReadCommand"/> 会在用到时说:
///   · 预处理指令里的括号照样算数 —— <c>#if</c> 两侧各带半个 <c>{</c> 的写法会配歪;
///   · 泛型约束、`where T : class` 这类文本里的关键字靠位置规避,不靠语义;
///   · 找不到一个名字,只说明**文本扫描**没看见它,不等于它不在文件里。
///
/// 字符串、字符、注释里的括号一律不算 —— 逐字符的状态机就是为这件事存在的。
/// </summary>
public static class CsOutline
{
    /// <summary>整份文件的声明,按出现顺序。</summary>
    public static IReadOnlyList<CsDecl> Scan(IReadOnlyList<string> lines)
    {
        var found = new List<CsDecl>();
        var open = new Stack<Frame>();
        var pending = new StringBuilder();
        var pendingStart = 0;

        var state = Lex.Code;
        var quoteRun = 0;   // 原始字符串字面量("""…""")的引号数

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (state == Lex.LineComment) state = Lex.Code;

            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                var next = c + 1 < line.Length ? line[c + 1] : '\0';

                switch (state)
                {
                    case Lex.LineComment:
                        continue;

                    case Lex.BlockComment:
                        if (ch == '*' && next == '/') { state = Lex.Code; c++; }
                        continue;

                    case Lex.String:
                        if (ch == '\\') { c++; continue; }
                        if (ch == '"') state = Lex.Code;
                        continue;

                    case Lex.VerbatimString:
                        // @"…""…" —— 两个引号是一个转义,不是收尾
                        if (ch == '"' && next == '"') { c++; continue; }
                        if (ch == '"') state = Lex.Code;
                        continue;

                    case Lex.RawString:
                        if (ch != '"') continue;
                        var run = 1;
                        while (c + run < line.Length && line[c + run] == '"') run++;
                        c += run - 1;
                        if (run >= quoteRun) state = Lex.Code;
                        continue;

                    case Lex.Char:
                        if (ch == '\\') { c++; continue; }
                        if (ch == '\'') state = Lex.Code;
                        continue;
                }

                // —— 这里开始是 Lex.Code ——
                if (ch == '/' && next == '/') { state = Lex.LineComment; continue; }
                if (ch == '/' && next == '*') { state = Lex.BlockComment; c++; continue; }
                if (ch == '\'') { state = Lex.Char; continue; }
                if (ch == '@' && next == '"') { state = Lex.VerbatimString; c++; continue; }
                if (ch == '"')
                {
                    var run2 = 1;
                    while (c + run2 < line.Length && line[c + run2] == '"') run2++;
                    if (run2 >= 3) { state = Lex.RawString; quoteRun = run2; c += run2 - 1; }
                    else state = Lex.String;
                    continue;
                }

                switch (ch)
                {
                    case '{':
                    {
                        // 只在**声明能住的地方**才认头:根、namespace 里、类型里。方法体内部
                        // 的 `if (x) {` 同样是「标识符 + 圆括号 + 大括号」,认下去就会多出一个
                        // 叫 if 的方法 —— 轮廓里混进语句碎片,比少认几个还坏。
                        var decl = Declarable(open) ? Classify(Normalize(pending.ToString()), OwnerOf(open)) : null;
                        open.Push(new Frame(decl, decl is null ? 0 : pendingStart));
                        pending.Clear();
                        continue;
                    }

                    case '}':
                    {
                        if (open.Count > 0)
                        {
                            var frame = open.Pop();
                            if (frame.Decl is { } d)
                                found.Add(d with { StartLine = Backfill(lines, frame.Start), EndLine = i + 1 });
                        }
                        pending.Clear();
                        continue;
                    }

                    case ';':
                    {
                        // 无体成员:字段、常量、abstract/extern 方法、`=> expr;` 的属性,
                        // 以及带主构造函数的 `record Foo(int X);`。同样只认声明能住的地方 ——
                        // 否则方法体里的每一条语句都会被当成一个字段。
                        if (Declarable(open))
                        {
                            var decl = Classify(Normalize(pending.ToString()), OwnerOf(open), bodyless: true);
                            // 根与 namespace 下只可能是类型;类型里才轮得到成员。
                            var inType = open.Count > 0 && open.Peek().Decl is { } p && IsType(p.Kind);
                            if (decl is not null && (inType || IsType(decl.Kind)))
                                found.Add(decl with { StartLine = Backfill(lines, pendingStart), EndLine = i + 1 });
                        }
                        pending.Clear();
                        continue;
                    }
                }

                // 声明从第一个**非空白**字符开始。原先这里先记行号再无条件 append,于是
                // 每行开头那个缩进 tab 会把 pending 变成非空,起始行号从此再也不更新 ——
                // 整份文件的声明全部报成同一个起点。
                if (pending.Length == 0)
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    pendingStart = i + 1;
                }
                pending.Append(ch);
            }

            if (pending.Length > 0) pending.Append(' ');
        }

        return found;
    }

    private enum Lex { Code, LineComment, BlockComment, String, VerbatimString, RawString, Char }

    private sealed record Frame(CsDecl? Decl, int Start);

    private static string? OwnerOf(Stack<Frame> open)
        => open.FirstOrDefault(f => f.Decl is not null && IsType(f.Decl.Kind))?.Decl?.Name;

    /// <summary>当前位置能不能住一个声明:根、namespace 里、类型里三处。</summary>
    private static bool Declarable(Stack<Frame> open)
        => open.Count == 0 ||
           (open.Peek().Decl is { } p && (p.Kind == "namespace" || IsType(p.Kind)));

    internal static bool IsType(string kind)
        => kind is "class" or "struct" or "interface" or "record" or "enum";

    /// <summary>
    /// 声明行往上收编紧挨着的注释与特性行。文档注释与 <c>[Attribute]</c> 是声明的一部分,
    /// 而词法器把注释整个吞掉了,特性又常常自成一行 —— 不收编,读到的方法体会缺掉
    /// 「这个方法是干什么的」那几行,而那正是读它的理由。
    /// </summary>
    private static int Backfill(IReadOnlyList<string> lines, int start)
    {
        var i = start;   // 1 起
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

    /// <summary>把跨行的声明头压成一行,便于按位置判断。</summary>
    private static string Normalize(string header)
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

    /// <summary>
    /// 一段声明头是什么。判不出来就回 null —— 判不出来的东西不该编一个名字给它,
    /// 那会让 <c>--member</c> 命中一堆语句碎片。
    /// </summary>
    private static CsDecl? Classify(string header, string? owner, bool bodyless = false)
    {
        if (header.Length == 0) return null;

        // `=> expr;` 的成员没有大括号,走的是无体那条路,可它是属性/方法而不是字段。
        // 箭头右边是实现,与「这是什么」无关,先切掉。
        var arrow = header.IndexOf("=>", StringComparison.Ordinal);
        if (bodyless && arrow > 0)
        {
            header = header[..arrow].TrimEnd();
            bodyless = false;
        }

        // 特性与预处理指令自己不是声明,但它们常常挂在声明头前面,先剥掉。
        while (header.StartsWith('[') && header.IndexOf(']') > 0)
            header = header[(header.IndexOf(']') + 1)..].TrimStart();
        if (header.StartsWith('#')) return null;

        // 无体声明的等号右边是初值,不是声明的一部分 —— 而初值里的圆括号会把一个字段
        // 认成方法:`readonly Material BubbleMat = MaterialPool.MatFrom(…)` 报出来的名字
        // 曾经是 MatFrom。先切,再判。
        if (bodyless) header = StripInitializer(header);

        // 类型。关键字必须后接一个标识符,于是 `where T : class` 这种约束文本不会命中
        // (它后面是 `,` 或行尾)。取第一处,因为 `class Foo : IEnumerable<Bar>` 里
        // 后面的都不是本体。
        foreach (var kw in TypeKeywords)
        {
            var name = AfterKeyword(header, kw);
            if (name is not null) return new CsDecl(kw, name, owner, 0, 0);
        }

        if (header.StartsWith("namespace ", StringComparison.Ordinal))
            return new CsDecl("namespace", header["namespace ".Length..].Trim().TrimEnd(';'), owner, 0, 0);

        // 方法一族:看第一个顶层 '('。lambda、强制转换与调用都在语句里,而这里只在
        // 「直接住在类型里」的位置被调用,所以不必再防它们。
        var paren = TopLevelParen(header);
        if (paren > 0)
        {
            var name = IdentifierBefore(header, paren);
            if (name is null) return null;
            var kind = name == owner ? "constructor" : "method";
            return new CsDecl(kind, name, owner, 0, 0);
        }

        // 剩下的:属性 / 事件 / 索引器 / 字段。索引器的名字就是 `this`,与 C# 说法一致。
        if (header.EndsWith("this", StringComparison.Ordinal) || header.Contains("this["))
            return new CsDecl("indexer", "this", owner, 0, 0);

        var last = LastIdentifier(header);
        if (last is null || Keywords.Contains(last)) return null;
        return new CsDecl(bodyless ? "field" : "property", last, owner, 0, 0);
    }

    private static readonly string[] TypeKeywords = ["class", "struct", "interface", "record", "enum"];

    /// <summary>C# 关键字里会单独结尾的那些。它们不是名字。</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "get", "set", "init", "add", "remove", "return", "else", "do", "try", "finally",
        "unsafe", "checked", "unchecked", "fixed", "lock", "using", "switch", "base", "this",
    };

    /// <summary>`class Foo` → `Foo`。关键字必须是独立的词,后面必须真的跟一个标识符。</summary>
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

    /// <summary>圆括号嵌套为 0 时的第一个 '('。尖括号里的不算(泛型实参可以带元组)。</summary>
    private static int TopLevelParen(string header)
    {
        var angle = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '(' when angle == 0: return i;
            }
        }
        return -1;
    }

    /// <summary>'(' 往左跳过泛型形参,取那个标识符。<c>operator +</c> 这类连同关键字一起取。</summary>
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

    /// <summary>`int hitPoints = 30` → `int hitPoints`。初值里的标识符不是字段名。</summary>
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
        return end > start ? header[start..end] : null;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
