using RimSearcher.Search;

namespace RimSearcher.Tests;

/// <summary>
/// <see cref="CsOutline"/> 的形态闸。
///
/// 两类形态的产物都是**带着正确行号的错名字**,即错答案穿着对答案的衣服:
///
///   · 元组类型。声明头里第一个顶层 '(' 是**类型**不是参数表,取它左边的标识符
///     就取到修饰符 —— `CellRect.SplitVertical` 会整个消失,列里留下一个叫 public 的方法。
///   · 泛型约束连写。`where T : class where U : struct` 里的 `class where`
///     是「关键字 + 空格 + 标识符」,被认成类型声明后以类型身份压栈,
///     <c>Declarable</c> 跟着放行 —— 整个方法体的语句都变成声明。
///
/// 这里不扫真实树:那棵树在哪、装了哪些 mod 因机器而异。语料把这些形态逐条内联,
/// 判据是关键字不许出现在名字位置。该判据是**反向**的(不许有什么),所以每条语料
/// 同时配一份正向名单 —— 只判反向的话,一个把所有声明都丢掉的实现同样能过。
/// </summary>
public class OutlineAuditTests
{
    /// <summary>
    /// 一条语料 = 一种写法 + 它应当产出的**全部**声明名字(按出现顺序)。
    ///
    /// 期望值写成全集而不是「至少包含」:多认一个语句碎片与少认一个成员同等严重,
    /// 而「至少包含」对前者一个字都说不出来。
    /// </summary>
    public static TheoryData<string, string, string[]> Shapes => new()
    {
        {
            "元组返回类型 —— 第一个顶层 ( 是返回类型,不是参数表",
            """
            internal class T
            {
            	internal (int left, int right) Split(int at)
            	{
            	}
            }
            """,
            ["Split", "T"]
        },
        {
            "元组字段",
            """
            internal class T
            {
            	private (int lo, int hi) bounds;
            }
            """,
            ["bounds", "T"]
        },
        {
            "元组数组 —— ')' 右边是 '[',不是标识符",
            """
            internal class T
            {
            	private (int lo, int hi)[] spans;
            }
            """,
            ["spans", "T"]
        },
        {
            "可空元组 —— ')' 右边是 '?'",
            """
            internal class T
            {
            	internal (int lo, int hi)? Maybe(int at)
            	{
            	}
            }
            """,
            ["Maybe", "T"]
        },
        {
            "元组数组带初值 —— StripInitializer 与元组判据同时在场",
            """
            internal class T
            {
            	private static (float, string)[] Labels = new(float, string)[2];
            }
            """,
            ["Labels", "T"]
        },
        {
            "构造函数带基构造调用 —— 反向落点:跳过冒号就会把它认成 base",
            """
            internal class T
            {
            	internal T(int at) : base(at)
            	{
            	}
            }
            """,
            ["T", "T"]
        },
        {
            "泛型约束连写(class)—— 崩塌型:误判成类型后整个方法体变成声明面",
            """
            internal class T
            {
            	internal void Both<A, B>(A a, B b) where A : class where B : struct
            	{
            		if (a != null)
            		{
            		}
            	}
            }
            """,
            ["Both", "T"]
        },
        {
            "泛型约束连写(struct)",
            """
            internal class T
            {
            	internal void Cast<A, B>(A a) where A : struct where B : struct
            	{
            	}
            }
            """,
            ["Cast", "T"]
        },
        {
            "类型自己带约束连写",
            """
            internal class Holder<A, B> where A : class where B : class
            {
            	private int n;
            }
            """,
            ["n", "Holder"]
        },
        {
            "单条约束 —— 切在 where 上,名字仍要完整",
            """
            internal class T
            {
            	internal void One<A>(A a) where A : class
            	{
            	}
            }
            """,
            ["One", "T"]
        },
        {
            "方法体里的 if 不许变成成员",
            """
            internal class T
            {
            	internal void M(int n)
            	{
            		if (n > 0)
            		{
            		}
            	}
            }
            """,
            ["M", "T"]
        },
        {
            "带初值的字段不许被初值里的括号认成方法",
            """
            internal class T
            {
            	private static readonly string Marker = Make("x");
            }
            """,
            ["Marker", "T"]
        },
        {
            "=> 属性是属性,不是字段",
            """
            internal class T
            {
            	internal string Label => "x";
            }
            """,
            ["Label", "T"]
        },
        {
            // 访问器不是声明:Classify 的 Keywords 表把 get/set/init/add/remove 挡在外面,
            // 否则每个属性都会带出两个叫 get 与 set 的「成员」。
            "索引器的名字就是 this,访问器不算声明",
            """
            internal class T
            {
            	internal int this[int i]
            	{
            		get
            		{
            		}
            	}
            }
            """,
            ["this", "T"]
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void 每种写法认出的声明与预期逐条相同(string what, string code, string[] expected)
    {
        var decls = Scan(code);
        Assert.Equal(expected, decls.Select(d => d.Name).ToArray());
        Assert.NotEmpty(what);
    }

    /// <summary>
    /// 元数进显示,不进匹配。
    ///
    /// <see cref="CsDecl.Name"/> 一旦跟着带上 <c>&lt;T&gt;</c>,`--type ThingOwner`
    /// 就再也命中不了泛型那一个,而调用方**没有地方**能知道该写几个类型参数:
    /// 反编译树按类型名建文件,文件名不带元数。
    /// </summary>
    [Fact]
    public void 同名不同元数的类型显示分得开而裸名仍然相同()
    {
        var decls = Scan("""
            internal class Box
            {
            	private int a;
            }

            internal class Box<T>
            {
            	private int b;
            }
            """);

        var types = decls.Where(d => d.Kind == "class").ToList();
        Assert.Equal(2, types.Count);

        // 匹配侧:两个都叫 Box,--type Box 因此同时命中 —— 这是有意的。
        Assert.All(types, d => Assert.Equal("Box", d.Name));
        // 显示侧:分得开。
        Assert.Equal(["Box", "Box<T>"], types.Select(d => d.Display).ToArray());

        // 成员必须带着 owner 的元数走,否则两个 Box 下的字段归属逐字相同。
        var fields = decls.Where(d => d.Kind == "field").ToList();
        Assert.Equal(["Box.a", "Box<T>.b"], fields.Select(d => d.Qualified).ToArray());
        Assert.All(fields, d => Assert.Equal("Box", d.Owner));
    }

    /// <summary>多个类型参数照抄原文,读的人不必把元数换算回参数表。</summary>
    [Fact]
    public void 多参数泛型的显示名保留参数原文()
    {
        var decls = Scan("""
            internal class Row<T1, T2, T3>
            {
            	private int n;
            }
            """);

        Assert.Equal("Row<T1, T2, T3>", decls.Single(d => d.Kind == "class").Display);
        Assert.Equal("Row<T1, T2, T3>.n", decls.Single(d => d.Kind == "field").Qualified);
    }

    /// <summary>
    /// 带约束的泛型类型:StripConstraints 切在 where 上,而元数取的是名字后面那一段,
    /// 两者不许互相踩。
    /// </summary>
    [Fact]
    public void 泛型类型带约束时元数仍然取得到()
    {
        var decls = Scan("""
            internal class Holder<A, B> where A : class where B : class
            {
            	private int n;
            }
            """);

        Assert.Equal("Holder<A, B>", decls.Single(d => d.Kind == "class").Display);
    }

    /// <summary>
    /// 关键字不许落在名字位置:一条语句被当成声明,产出的名字必然是个关键字
    /// (if / while / return);一个声明头被切错,产出的名字必然是个修饰符
    /// (public / private / static)。这一条对没见过的新形态同样有效,
    /// 而上面那张表只认得已经撞过的那些。
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void 关键字不出现在名字位置(string what, string code, string[] expected)
    {
        foreach (var d in Scan(code))
            Assert.False(Reserved.Contains(d.Name),
                $"{what}: 轮廓里出现了 '{d.Kind} {d.Name}' @{d.StartLine}-{d.EndLine} —— " +
                "关键字落在名字位置,说明这一段被当成了它不是的东西。");
        Assert.NotEmpty(expected);
    }

    /// <summary>
    /// C# 里不可能做声明名字的词。<c>record</c> **不在**这张表里 ——
    /// 它是上下文关键字,`public RelationshipRecord record;` 在 vanilla 里真实存在,
    /// 把它算进来会让这道闸对着一个合法字段名报红。
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "switch", "lock", "try", "catch", "finally",
        "else", "do", "return", "throw", "using", "checked", "unchecked", "fixed", "unsafe",
        "where", "class", "struct", "interface", "enum", "new", "base",
        "public", "private", "protected", "internal", "static", "readonly", "const",
        "virtual", "override", "abstract", "sealed", "partial", "extern", "async",
    };

    private static IReadOnlyList<CsDecl> Scan(string code)
        // ReadCommand.DeclarationsIn 滤掉 namespace,闸跟着滤 —— 判的是它实际读到的那份。
        => CsOutline.Scan(code.Replace("\r\n", "\n").Split('\n'))
                    .Where(d => d.Kind != "namespace").ToList();
}
