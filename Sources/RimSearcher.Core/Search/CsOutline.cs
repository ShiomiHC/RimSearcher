using System.Text;

namespace RimSearcher.Search;

/// <summary>
/// 一段声明在文件里占的行。行号 1 起,两端都含。
///
/// <paramref name="Owner"/> 是最近的**类型**祖先(namespace 不算),用来把同名成员分开。
/// </summary>
public sealed record CsDecl(string Kind, string Name, string? Owner, int StartLine, int EndLine)
{
    /// <summary>
    /// 自己的泛型参数原文(<c>&lt;T&gt;</c>);非泛型是空串。
    ///
    /// 存原文而不是个数,因为 <c>ThingOwner&lt;T&gt;</c> 比 <c>ThingOwner`1</c> 认得出来,
    /// 而反编译产物的参数名一律很短(T / TKey / TElement)。
    /// </summary>
    public string TypeParams { get; init; } = "";

    /// <summary>
    /// <see cref="Owner"/> 的泛型参数原文。成员**必须**带着它 —— 一个文件里可以同时住着
    /// <c>ThingOwner&lt;T&gt;</c> 与 <c>ThingOwner</c>(vanilla 真有),而两边的成员
    /// 光看 Owner 名字一模一样。
    /// </summary>
    public string OwnerTypeParams { get; init; } = "";

    /// <summary>
    /// 声明头最前面那串修饰符原文,空格分隔;一个都没有就是空串。
    ///
    /// 存在的理由是 <c>override</c> 与 <c>virtual</c> 的区别 —— 少了它,
    /// 「这个类型覆写了基类的某个成员」与「这个类型自己新引入了一个可覆写成员」
    /// 在轮廓里逐字同形,而两者对「该去基类找什么」给出相反的下一步。
    /// </summary>
    public string Modifiers { get; init; } = "";

    /// <summary>带元数的自己。<see cref="Name"/> 保持裸名 —— 那是**匹配**用的。</summary>
    public string Display => Name + TypeParams;

    /// <summary>`ThingOwner&lt;T&gt;.Count` 这样的显示名;顶层类型就是它自己。</summary>
    public string Qualified => Owner is { Length: > 0 } ? $"{Owner}{OwnerTypeParams}.{Display}" : Display;

    public int Lines => EndLine - StartLine + 1;
}

/// <summary>
/// 大括号配平出来的 C# 轮廓 —— **不是**语法分析。
///
/// 不接 Roslyn:为了「读一个方法体」把整个编译器前端拉进一个查快照的 CLI,代价与收益差着
/// 量级。反编译产物(ILSpy 生成)的格式最规整,配平括号足够把方法体的边界找准。
///
/// 三件事这里做不到,<see cref="ReadCommand"/> 会在用到时说:
///   · 预处理指令里的括号照样算数 —— <c>#if</c> 两侧各带半个 <c>{</c> 的写法会配歪;
///   · 泛型约束、`where T : class` 这类文本里的关键字靠位置规避,不靠语义;
///   · 找不到一个名字,只说明**文本扫描**没看见它,不等于它不在文件里。
///
/// 字符串、字符、注释里的括号一律不算。
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
                        // 以及带主构造函数的 `record Foo(int X);`。同样只认声明能住的地方。
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

                // 声明从第一个**非空白**字符开始:空白要在记行号**之前**跳掉,否则行首缩进
                // 就把 pending 变成非空,起始行号再也不更新。
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

    /// <summary>
    /// 最近的类型祖先本身,不只是它的名字 —— 成员要从它身上取走泛型参数,
    /// 否则同名不同元数的两个类型下的成员在输出里逐字相同。
    /// </summary>
    private static CsDecl? OwnerOf(Stack<Frame> open)
        => open.FirstOrDefault(f => f.Decl is not null && IsType(f.Decl.Kind))?.Decl;

    /// <summary>当前位置能不能住一个声明:根、namespace 里、类型里三处。</summary>
    private static bool Declarable(Stack<Frame> open)
        => open.Count == 0 ||
           (open.Peek().Decl is { } p && (p.Kind == "namespace" || IsType(p.Kind)));

    internal static bool IsType(string kind)
        => kind is "class" or "struct" or "interface" or "record" or "enum";

    /// <summary>
    /// 声明行往上收编紧挨着的注释与特性行。文档注释与 <c>[Attribute]</c> 是声明的一部分,
    /// 而词法器把注释整个吞掉了,特性又常常自成一行。
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
    private static CsDecl? Classify(string header, CsDecl? owner, bool bodyless = false)
    {
        var decl = ClassifyCore(header, owner, bodyless);
        return decl is null ? null : decl with { Modifiers = LeadingModifiers(header) };
    }

    private static CsDecl? ClassifyCore(string header, CsDecl? owner, bool bodyless = false)
    {
        if (header.Length == 0) return null;

        // 成员一律带着 owner 的泛型参数走。少了它,`ThingOwner<T>.Count` 与
        // `ThingOwner.Count`(同一个文件里的两个类,vanilla 真有)在输出里逐字相同,
        // --member 给出两条无从区分的候选,--type 也收敛不了。
        var ownerName = owner?.Name;
        var ownerParams = owner?.TypeParams ?? "";

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
        // 认成方法:`readonly Material BubbleMat = MaterialPool.MatFrom(…)` 会报成 MatFrom。
        // 先切,再判。
        if (bodyless) header = StripInitializer(header);

        // 泛型约束同理:那一段里没有正在被声明的名字。而它不只是「多认一个」——
        // `where T : class where U : struct` 里的 `class where` 正好是
        // 「class 关键字 + 空格 + 标识符」,AfterKeyword 会认成一个叫 where 的类型,
        // 于是这个**方法**以类型身份压栈,方法体里的每条语句都变成声明。
        // 必须在找 TypeKeywords 之前切。
        header = StripConstraints(header);

        // 类型。关键字必须后接一个标识符,于是 `where T : class` 这种约束文本不会命中
        // (它后面是 `,` 或行尾)。取第一处,因为 `class Foo : IEnumerable<Bar>` 里
        // 后面的都不是本体。
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

        // 方法一族:看第一个顶层 '('。lambda、强制转换与调用都在语句里,而这里只在
        // 「直接住在类型里」的位置被调用,所以不必再防它们。
        var paren = TopLevelParen(header);
        if (paren > 0)
        {
            var name = IdentifierBefore(header, paren);
            if (name is null) return null;
            var kind = name == ownerName ? "constructor" : "method";
            return new CsDecl(kind, name, ownerName, 0, 0) { OwnerTypeParams = ownerParams };
        }

        // 剩下的:属性 / 事件 / 索引器 / 字段。索引器的名字就是 `this`,与 C# 说法一致。
        if (header.EndsWith("this", StringComparison.Ordinal) || header.Contains("this["))
            return new CsDecl("indexer", "this", ownerName, 0, 0) { OwnerTypeParams = ownerParams };

        var last = LastIdentifier(header);
        if (last is null || Keywords.Contains(last)) return null;
        return new CsDecl(bodyless ? "field" : "property", last, ownerName, 0, 0)
            { OwnerTypeParams = ownerParams };
    }

    /// <summary>
    /// 声明头开头那一串修饰符,取到第一个不是修饰符的词为止。
    ///
    /// 只认前缀,不满文件搜:C# 的修饰符一律写在类型/返回类型左边,而同样这些词在右边
    /// 是别的东西(<c>Func&lt;int&gt; New</c> 里的 new、参数表里的 ref)。取前缀就不必分辨。
    /// </summary>
    private static string LeadingModifiers(string header)
    {
        // 特性自成一段、又常与声明挤在同一个 pending 里,先跳过。
        while (header.StartsWith('[') && header.IndexOf(']') > 0)
            header = header[(header.IndexOf(']') + 1)..].TrimStart();

        var taken = new List<string>();
        foreach (var word in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ModifierWords.Contains(word)) break;
            taken.Add(word);
        }
        return string.Join(' ', taken);
    }

    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "file",
        "static", "readonly", "const", "volatile", "required", "ref",
        "abstract", "virtual", "override", "sealed", "new",
        "extern", "partial", "async", "unsafe",
    };

    private static readonly string[] TypeKeywords = ["class", "struct", "interface", "record", "enum"];

    /// <summary>C# 关键字里会单独结尾的那些。它们不是名字。</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "get", "set", "init", "add", "remove", "return", "else", "do", "try", "finally",
        "unsafe", "checked", "unchecked", "fixed", "lock", "using", "switch", "base", "this",
    };

    /// <summary>
    /// 类型名后面紧跟的 <c>&lt;…&gt;</c> 原文;非泛型回空串。
    ///
    /// 取原文而不是数个数:同名不同元数的类型要靠这段区分开,而
    /// <c>Row&lt;T1, T2&gt;</c> 与 <c>Row&lt;T1, T2, T3&gt;</c> 一眼分得出,
    /// 元数 2 与 3 还要读的人自己换算。
    /// </summary>
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

    /// <summary>
    /// **参数表**那个 '('。尖括号里的不算(泛型实参可以带元组)。
    ///
    /// 第一个顶层 '(' 不一定就是它 —— 元组类型自带一对括号,而它写在名字**左边**:
    /// <c>internal (int left, int right) Split(int at)</c> 的第一个 '(' 是返回类型。
    /// 取错的代价不是少认一个成员:<see cref="IdentifierBefore"/> 会往左取到修饰符,
    /// 这个方法在轮廓里就叫 <c>internal</c>,而行号仍然是对的 —— 错答案穿着对答案的衣服。
    /// 字段同理(<c>private (int lo, int hi) bounds;</c>)。
    ///
    /// 判据:一个顶层 '(' 若其配对 ')' 后面还跟着标识符,它就是类型不是参数表 ——
    /// 参数表右边只可能是泛型约束、基构造调用、'=>' 或行尾,都不以标识符打头。
    /// </summary>
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
                    if (close < 0) return i;          // 没配上,按参数表处理
                    // ')' 右边还可能挂类型后缀:数组 `[]`、多维 `[,]`、可空 `?`,以及它们的
                    // 组合(`(int, int)?[] xs`)。跳过这些再看是不是标识符。
                    // **冒号不跳**:`Foo(int a) : base(a)` 的右边是基构造调用,
                    // 那个 '(' 属于 base,跳过去就把构造函数认成了 base。
                    var j = close + 1;
                    while (j < header.Length && header[j] is ' ' or '[' or ']' or '?' or ',') j++;
                    if (j < header.Length && IsIdentChar(header[j])) { i = close; continue; }
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>'(' 的配对 ')';没配上回 -1。</summary>
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

    /// <summary>
    /// 切掉第一个顶层 <c>where </c> 起的全部文本。约束子句里只有类型参数和它被约束到的
    /// 类型,正在声明的那个名字一定在它左边。
    ///
    /// 顶层判定按尖括号深度:泛型实参里可以出现任意标识符,不该被当成约束的起点。
    /// <c>where</c> 是上下文关键字、理论上能做标识符,但要在声明头里后接一个空格才会
    /// 命中这里,那个形状不存在于反编译产物。
    /// </summary>
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
