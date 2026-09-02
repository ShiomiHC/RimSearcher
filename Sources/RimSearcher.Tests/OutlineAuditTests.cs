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
        {
            "比较运算符 —— 左括号左边是 == 不是标识符,原先整条丢掉",
            """
            internal struct T
            {
            	public static bool operator ==(T a, T b)
            	{
            		return true;
            	}
            }
            """,
            ["operator ==", "T"]
        },
        {
            "转换运算符 —— 左括号左边是目标类型名,原先被收成普通方法",
            """
            internal struct T
            {
            	public static implicit operator Foo(T a)
            	{
            		return default;
            	}
            }
            """,
            ["implicit operator Foo", "T"]
        },
        {
            "类型内部的委托 —— 左括号左边是委托名,原先被收成普通方法",
            """
            internal class T
            {
            	public delegate void Nested(string s);
            }
            """,
            ["Nested", "T"]
        },
        {
            "析构函数 —— 名字与宿主同名,原先被收成构造函数",
            """
            internal class T
            {
            	~T()
            	{
            	}
            }
            """,
            ["~T", "T"]
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
    /// 运算符是声明,kind 是 operator,名字能让人认出是哪一个。
    ///
    /// 比较/算术那种左括号左边不是标识符,原先整条丢掉;转换运算符左边是目标类型名,
    /// 原先被收成普通方法 —— 若目标类型恰好就是宿主名,还会被判成 constructor。
    /// 两种都比「少认一个」更坏:一个是沉默的缺席,一个是看起来完全正常、实际不存在的成员。
    /// </summary>
    [Fact]
    public void 运算符声明记成operator且名字能认出是哪一个()
    {
        var decls = Scan("""
            internal struct Host
            {
            	public static bool operator ==(Host a, Host b)
            	{
            		return true;
            	}

            	public static bool operator !=(Host a, Host b)
            	{
            		return false;
            	}

            	public static Host operator +(Host a, Host b)
            	{
            		return a;
            	}

            	public static bool operator <=(Host a, Host b)
            	{
            		return true;
            	}

            	public static bool operator true(Host a)
            	{
            		return true;
            	}

            	public static bool operator false(Host a)
            	{
            		return false;
            	}

            	public static Host operator checked +(Host a, Host b)
            	{
            		return a;
            	}

            	public static implicit operator Foo(Host a)
            	{
            		return default;
            	}

            	public static explicit operator Foo(Host a)
            	{
            		return default;
            	}

            	public static implicit operator Host(int n)
            	{
            		return default;
            	}

            	public static bool operator ==(Host a, Host b) => true;
            }
            """);

        var ops = decls.Where(d => d.Kind == "operator").Select(d => d.Name).ToArray();
        Assert.Equal(
        [
            "operator ==",
            "operator !=",
            "operator +",
            "operator <=",
            "operator true",
            "operator false",
            "operator checked +",
            "implicit operator Foo",
            "explicit operator Foo",
            "implicit operator Host",
            "operator ==",
        ], ops);

        // 转换目标恰好是宿主名时不许落成 constructor;目标是别的名字时不许落成 method。
        Assert.DoesNotContain(decls, d => d.Kind == "constructor");
        Assert.DoesNotContain(decls, d => d.Kind == "method");
    }

    /// <summary>
    /// 委托类型两档都在场、kind 都是 delegate:namespace 下的(RegionProcessor.cs /
    /// PanCompletionCallback.cs 整文件就是一份)Owner 为空,类型内部的 Owner 是宿主。
    /// 文件级那档曾被「根与 namespace 下只可能是类型」那道闸挡在外面,整文件轮廓 0 条。
    /// </summary>
    [Fact]
    public void 命名空间下的委托类型进轮廓且嵌套委托记成delegate()
    {
        var fileLevel = Scan("""
            namespace Verse
            {
            	public delegate bool RegionProcessor(Region reg);
            }
            """);
        var top = Assert.Single(fileLevel.Where(d => d.Kind != "namespace"));
        Assert.Equal(("delegate", "RegionProcessor", (string?)null), (top.Kind, top.Name, top.Owner));
        Assert.Equal((3, 3), (top.StartLine, top.EndLine));

        // 文件级命名空间(`namespace Verse;`)下同样要认:那时栈是空的,委托直接住在根上。
        var fileScoped = Scan("""
            namespace Verse;

            public delegate bool RegionProcessor(Region reg);
            """);
        Assert.Contains(fileScoped, d => d is { Kind: "delegate", Name: "RegionProcessor", Owner: null });

        var nested = Scan("""
            internal class T
            {
            	public delegate void Nested(int n);

            	internal void Real(int n)
            	{
            	}
            }
            """);
        // 委托与普通方法必须分开:两者都是「名字 + 参数表」,标成同一个 kind 时,
        // 一份轮廓能让人得出「这个类有个叫 Nested 的方法可以调」。
        Assert.Equal("delegate", nested.Single(d => d.Name == "Nested").Kind);
        Assert.Equal("method", nested.Single(d => d.Name == "Real").Kind);
    }

    /// <summary>
    /// enum 成员进轮廓,kind 是 enum-member,Owner 是那个 enum:成员之间是逗号而不是分号,
    /// 逗号只在 enum 帧里当分隔符;最后一个成员没有逗号,由收尾花括号收。
    /// 形状对齐真实树的 RegionType.cs:带 <c>[Flags]</c>、初值有十进制也有十六进制。
    /// 特性实参里的逗号不许切成两个成员;尾随逗号不许多出一个空成员。
    /// </summary>
    [Fact]
    public void enum成员进轮廓且逗号只在enum帧里当分隔符()
    {
        var decls = Scan("""
            namespace Verse
            {
            	[Flags]
            	public enum RegionType
            	{
            		None = 0,
            		Portal = 1,
            		[Obsolete("use Portal", true)]
            		Set_All = 0xF,
            		Plain
            	}

            	public enum Trailing
            	{
            		A,
            		B,
            	}

            	public class T
            	{
            		public void M(int a, int b)
            		{
            		}
            	}
            }
            """);
        var members = decls.Where(d => d is { Kind: "enum-member", Owner: "RegionType" }).ToList();
        Assert.Equal(["None", "Portal", "Set_All", "Plain"], members.Select(d => d.Name));
        Assert.Equal((6, 6), (members[0].StartLine, members[0].EndLine));
        Assert.Equal((8, 9), (members[2].StartLine, members[2].EndLine));
        // 最后一个成员由 `}` 收,行号仍是它自己那行,不是 `}` 那行。
        Assert.Equal((10, 10), (members[3].StartLine, members[3].EndLine));
        // 尾随逗号之后不许多出一个空成员。
        Assert.Equal(["A", "B"], decls.Where(d => d.Owner == "Trailing").Select(d => d.Name));
        // 方法参数表里的逗号不是分隔符:M 仍是一个 method,没有叫 a 或 b 的东西。
        Assert.Single(decls, d => d.Name == "M" && d.Kind == "method");
        Assert.DoesNotContain(decls, d => d.Name is "a" or "b");
    }

    /// <summary>
    /// 方法体里的本地函数进不了轮廓:扫描只在根、namespace、类型三处认声明,方法体内一律
    /// 当语句(否则 <c>if (x) {</c> 会变成一个叫 if 的方法)。
    /// 这是免责句此刻点名的「认不出的一类」—— 哪天修进来了,那句话得跟着换例子,
    /// 这条闸就是提醒它换的。形状对齐真实树 AttackTargetFinder.cs 第 297 行。
    /// </summary>
    [Fact]
    public void 方法体里的本地函数进不了轮廓()
    {
        var decls = Scan("""
            internal class AttackTargetFinder
            {
            	public static IAttackTarget BestAttackTarget(IntVec3 c)
            	{
            		return BestTargetOnCell(c);
            		IAttackTarget BestTargetOnCell(IntVec3 x)
            		{
            			return null;
            		}
            	}
            }
            """);
        Assert.Single(decls, d => d.Name == "BestAttackTarget");
        Assert.DoesNotContain(decls, d => d.Name == "BestTargetOnCell");
    }

    /// <summary>
    /// 析构函数不是构造函数。两者名字同形(都是宿主名),标成 constructor 时轮廓里就多出
    /// 一个「第二个无参构造」—— 行号是对的,整行是假的。
    /// 名字带上 <c>~</c>,与运算符同理:按源码形状写,自己就与构造函数分得开。
    /// </summary>
    [Fact]
    public void 析构函数记成destructor且与构造函数分得开()
    {
        var decls = Scan("""
            internal class Holder
            {
            	internal Holder()
            	{
            	}

            	~Holder()
            	{
            	}
            }
            """);

        Assert.Equal("constructor", decls.Single(d => d.Name == "Holder" && d.Kind != "class").Kind);
        Assert.Equal("destructor", decls.Single(d => d.Name == "~Holder").Kind);
        Assert.Equal("Holder.~Holder", decls.Single(d => d.Name == "~Holder").Qualified);
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
