using System;
using System.Collections.Generic;
using System.Linq;
using RimSearcher.DataMod;

namespace RimSearcher.Tests;

/// <summary>
/// 导出器那一层的闸。它跑在游戏进程里,能进这里的只有**不碰 RimWorld 类型**的部分
/// (见 csproj 里那段 Compile Link 的说明)。
/// </summary>
public class ExporterTests
{
    // 基类声明私有字段而 def 的运行时类是子类 —— 对应游戏里的
    // CreepJoinerBaseDef(private float weight = 1f)与它的四个子类。
    private class BaseDefShape
    {
        private float weight = 1f;
        private System.Collections.Generic.List<string> excludes = new();
        public string label = "";

        public float Weight => weight;
        public System.Collections.Generic.List<string> Excludes => excludes;
    }

    private class DerivedDefShape : BaseDefShape
    {
        public int degree;
    }

    private class ShadowingDefShape : BaseDefShape
    {
        public new string label = "";
    }

    /// <summary>
    /// 基类声明的**私有**字段也要被枚举到。
    ///
    /// <c>GetFields(NonPublic | Instance)</c> 在派生类上拿不到它们(反射规则,不是 bug),
    /// 于是基类私有字段会在导出里整条消失。
    /// </summary>
    [Fact]
    public void 基类声明的私有字段也要进字段枚举()
    {
        var names = FieldWalk.InstanceFields(typeof(DerivedDefShape)).Select(f => f.Name).ToList();

        Assert.Contains("weight", names);    // 基类私有 —— 反射默认拿不到的那两个
        Assert.Contains("excludes", names);
        Assert.Contains("label", names);     // 基类公开,一直拿得到
        Assert.Contains("degree", names);    // 子类自己的
    }

    /// <summary>
    /// 子类用 <c>new</c> 遮住同名字段时只出一次,且是近的那个 —— 与运行时真正读到的一致。
    /// </summary>
    [Fact]
    public void 被子类遮住的同名字段只算近的那一个()
    {
        var fields = FieldWalk.InstanceFields(typeof(ShadowingDefShape))
                              .Where(f => f.Name == "label").ToList();

        Assert.Single(fields);
        Assert.Equal(typeof(ShadowingDefShape), fields[0].DeclaringType);
    }

    // ---- 嵌套 Class= 那一维 ----
    //
    // 形状对应游戏里的两处:GenStepDef.genStep 是**单字段**上的 Class=(0.2 的
    // 「路径以 ] 收尾」判据一条都发不出),ThingDef.comps 是列表(0.2 一律发,
    // 其中运行时正好等于声明的那些是在报告作者没做的事)。

    private class GenStep { }
    private class GenStep_RocksFromGrid : GenStep { }
    private class StatModifier { }

    private class GenStepDefShape
    {
        public GenStep genStep = new GenStep_RocksFromGrid();
    }

    /// <summary>
    /// 单字段上的多态要发得出来 —— 这是 167 个 GenStepDef 一个字都说不出「跑哪段代码」
    /// 的那条路。旧判据挂在路径形状上(以 <c>]</c> 收尾),它连测都测不到。
    /// </summary>
    [Fact]
    public void 单字段上的多态要发出Class这一条()
    {
        var field = FieldWalk.InstanceFields(typeof(GenStepDefShape)).Single(f => f.Name == "genStep");
        var value = new GenStepDefShape().genStep;

        Assert.True(NestedClass.ShouldEmit(value.GetType(), field.FieldType));
    }

    /// <summary>
    /// 运行时类型正好是声明的那个 = 作者没写 <c>Class=</c>,不发。
    ///
    /// 这一条是换判据的**净收益**所在:实测一份 15964 个 def 的快照里 53509 条 .Class,
    /// 单是 <c>RimWorld.StatModifier</c>(<c>List&lt;StatModifier&gt;</c> 的元素,无子类)
    /// 就占 14729 条。旧判据把它们全发了。
    /// </summary>
    [Fact]
    public void 运行时类型等于声明类型时不发Class()
    {
        Assert.False(NestedClass.ShouldEmit(typeof(StatModifier), typeof(StatModifier)));
        Assert.False(NestedClass.ShouldEmit(typeof(GenStep), typeof(GenStep)));
    }

    /// <summary>
    /// 声明类型不可知时一律发:少一条 Class 是整条反查断掉,多一条只是一行冗余。
    /// </summary>
    [Fact]
    public void 声明类型不可知时宁可多发()
    {
        Assert.True(NestedClass.ShouldEmit(typeof(GenStep_RocksFromGrid), null));
        Assert.False(NestedClass.ShouldEmit(null, typeof(GenStep)));
    }

    /// <summary>
    /// 列表的元素声明类型 —— 判据靠它才知道「这个 li 写没写 Class=」。
    /// 拿不到的形状(非泛型集合)回 null,于是退回「一律发」,与 0.2 的行为一致。
    /// </summary>
    [Fact]
    public void 集合的元素声明类型取得出来()
    {
        Assert.Equal(typeof(GenStep), NestedClass.ElementType(typeof(System.Collections.Generic.List<GenStep>)));
        Assert.Equal(typeof(GenStep), NestedClass.ElementType(typeof(GenStep[])));
        Assert.Equal(typeof(GenStep),
                     NestedClass.ElementType(typeof(System.Collections.Generic.IEnumerable<GenStep>)));
        Assert.Null(NestedClass.ElementType(typeof(System.Collections.ArrayList)));
        Assert.Null(NestedClass.ElementType(null));
    }

    // ---- xpath 定位 / XML 写成的路径 / 类型字段全集 ----

    [Fact]
    public void xpath三种文本定位都能数到()
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byDefName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLabel = new Dictionary<string, int>(StringComparer.Ordinal);

        PatchXPath.AddMatches("/Defs/ThingDef[@Name=\"BaseBullet\"]/comps", byName, byDefName, byLabel);
        PatchXPath.AddMatches("/Defs/ThingDef[defName=\"Bullet_Revolver\"]/projectile", byName, byDefName, byLabel);
        PatchXPath.AddMatches("/Defs/ThingDef[@defName='Gun']", byName, byDefName, byLabel);
        PatchXPath.AddMatches("/Defs/ThingDef[label=\"revolver bullet\"]", byName, byDefName, byLabel);

        Assert.Equal(1, byName["BaseBullet"]);
        Assert.Equal(1, byDefName["Bullet_Revolver"]);
        Assert.Equal(1, byDefName["Gun"]);
        Assert.Equal(1, byLabel["revolver bullet"]);
        Assert.False(byName.ContainsKey("Bullet_Revolver"));
        Assert.False(byDefName.ContainsKey("BaseBullet"));
    }

    [Fact]
    public void XML写成的路径与字段路径同形()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml("""
            <ThingDef ParentName="BaseBullet">
              <defName>Bullet_Revolver</defName>
              <projectile>
                <damageAmountBase>12</damageAmountBase>
              </projectile>
              <comps>
                <li Class="CompProperties_Shield">
                  <energyMax>0.5</energyMax>
                </li>
              </comps>
              <thingCategories>
                <li>Foods</li>
              </thingCategories>
              <genStep Class="GenStep_Scatter" />
            </ThingDef>
            """);

        var paths = XmlFieldPaths.Collect(doc.DocumentElement!, 6, 200, out var texts);
        Assert.Equal(paths.Count, texts.Count);
        Assert.Contains("defName", paths);
        Assert.Contains("projectile.damageAmountBase", paths);
        Assert.DoesNotContain("projectile", paths);
        Assert.Contains("comps[0].Class", paths);
        Assert.Contains("comps[0].energyMax", paths);
        Assert.Contains("thingCategories[0]", paths);
        Assert.Contains("genStep.Class", paths);

        string TextOf(string path)
        {
            var i = paths.IndexOf(path);
            Assert.True(i >= 0, $"no path {path}");
            return texts[i];
        }
        Assert.Equal("Bullet_Revolver", TextOf("defName"));
        Assert.Equal("12", TextOf("projectile.damageAmountBase"));
        Assert.Equal("CompProperties_Shield", TextOf("comps[0].Class"));
        Assert.Equal("0.5", TextOf("comps[0].energyMax"));
        Assert.Equal("Foods", TextOf("thingCategories[0]"));
        Assert.Equal("GenStep_Scatter", TextOf("genStep.Class"));
        Assert.Equal("", TextOf("genStep"));
    }

    [Fact]
    public void XML写成的路径去重取第一次的文本()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml("""
            <ThingDef>
              <label>first</label>
              <label>second</label>
            </ThingDef>
            """);
        var paths = XmlFieldPaths.Collect(doc.DocumentElement!, 6, 200, out var texts);
        Assert.Single(paths);
        Assert.Equal("label", paths[0]);
        Assert.Equal("first", texts[0]);
    }

    private class TypeWalkShape
    {
        public string label = "";
        public int? maybe;
        public List<CompShape> comps = new();
        public NestedShape nested = new();
        public string? neverSet;
    }

    private class CompShape
    {
        public string compClass = "";
        public float energyMax;
    }

    private class NestedShape
    {
        public int speed;
    }

    [Fact]
    public void 类型字段全集含null字段且集合只用下标零()
    {
        var paths = TypeFieldWalk.Collect(typeof(TypeWalkShape), 6, _ => false, TypeFieldWalk.DefaultIsLeaf);
        Assert.Contains("label", paths);
        Assert.Contains("maybe", paths);
        Assert.Contains("neverSet", paths);
        Assert.Contains("comps[0].compClass", paths);
        Assert.Contains("comps[0].energyMax", paths);
        Assert.Contains("comps[0].Class", paths);
        Assert.Contains("nested.speed", paths);
        Assert.Contains("nested.Class", paths);
        Assert.DoesNotContain("comps", paths);
        Assert.DoesNotContain("nested", paths);
        Assert.DoesNotContain("comps[1].compClass", paths);
    }

    // ---- 类型全集的记忆化:必须与朴素展开逐条相同 ----

    private class TW_Root { public TW_A a = new(); public TW_B b = new(); }
    private class TW_A { public TW_B b = new(); public int x; }
    private class TW_B { public TW_A a = new(); public TW_C c = new(); public int y; }
    private class TW_C { public int z; }
    private class TW_List { public List<TW_A> items = new(); public TW_A one = new(); }

    // 两条**等长**的路径通到同一个类型 —— 等长是关键:预算一样,(类型, 预算) 这个键
    // 才会在两条祖先链上同时出现,错误的缓存复用才有机会发生。第一版闸的类型图两条路
    // 一长一短,键永远不撞,于是注入了错误也照样全绿。
    private class TW_Shared { public TW_W2 w2 = new(); public TW_W1 w1 = new(); }
    private class TW_W1 { public TW_Deep d = new(); public int p; }
    private class TW_W2 { public TW_Deep d = new(); public int q; }
    private class TW_Deep { public TW_Back back = new(); public int y; }
    private class TW_Back { public TW_W1 w1 = new(); public int z; }

    /// <summary>
    /// 改造前那版的逐行复刻,只用来当参照物。**不许拿被测代码去验被测代码** ——
    /// 记忆化要证的正是「换了实现,吐出来的还是同一批」。
    /// </summary>
    private static HashSet<string> NaiveCollect(Type type, int maxDepth)
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        var stack = new HashSet<Type>();
        Walk(type, "", 0);
        return into;

        void Walk(Type t, string prefix, int depth)
        {
            if (t == null || !stack.Add(t)) return;
            try
            {
                foreach (var f in FieldWalk.InstanceFields(t))
                {
                    var ft = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
                    var path = prefix.Length == 0 ? f.Name : prefix + "." + f.Name;
                    if (TypeFieldWalk.DefaultIsLeaf(ft)) { into.Add(path); continue; }
                    if (ft != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(ft))
                    {
                        if (depth >= maxDepth) continue;
                        var elem = NestedClass.ElementType(ft);
                        if (elem == null) continue;
                        elem = Nullable.GetUnderlyingType(elem) ?? elem;
                        var ix = path + "[0]";
                        if (TypeFieldWalk.DefaultIsLeaf(elem)) { into.Add(ix); continue; }
                        if (elem.IsClass && elem != typeof(string)) into.Add(ix + ".Class");
                        Walk(elem, ix, depth + 1);
                        continue;
                    }
                    if (depth >= maxDepth) continue;
                    if (ft.IsClass && ft != typeof(string)) into.Add(path + ".Class");
                    Walk(ft, path, depth + 1);
                }
            }
            finally { stack.Remove(t); }
        }
    }

    /// <summary>
    /// 这个类型图是照着记忆化**会出错的那一种形状**造的:B 在 `root.b` 下面展开得到
    /// `b.a.x`,在 `root.a.b` 下面却因为 A 已在祖先链上而被截断。两处的 (B, 预算) 完全
    /// 相同,结果却不同 —— 缓存条目一旦跨祖先链复用,`a.b.a.x` 就会凭空冒出来。
    ///
    /// 这不是假想:实测里第一版记忆化正是这么错的,而它跑得更快、行数更多,
    /// 从外面看像是「捡回了更多路径」。
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)] [InlineData(9)]
    public void 类型全集记忆化与朴素展开逐条相同(int depth)
    {
        foreach (var root in new[] { typeof(TW_Root), typeof(TW_A), typeof(TW_B), typeof(TW_List),
                                     typeof(TW_Shared), typeof(TW_W1), typeof(TW_Deep) })
        {
            var fast = TypeFieldWalk.Collect(root, depth, _ => false, TypeFieldWalk.DefaultIsLeaf);
            var naive = NaiveCollect(root, depth);
            Assert.Equal(naive.OrderBy(x => x, StringComparer.Ordinal),
                         fast.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void 同一类型在不同祖先链下的截断不许互相污染()
    {
        var paths = TypeFieldWalk.Collect(typeof(TW_Root), 4, _ => false, TypeFieldWalk.DefaultIsLeaf);
        // root.b 那一支:B 之上没有 A,所以 A 展得开。
        Assert.Contains("b.a.x", paths);
        // root.a 那一支:A 已在祖先链上,b.a 必须停在这里 —— 它自己那条 .Class 照发
        // (那一句在递归**之前**,报的是「这个位置是多态的」,不是「底下展开了」)。
        Assert.DoesNotContain("a.b.a.x", paths);
        Assert.Contains("a.b.a.Class", paths);
        // 两支都够得着的那个,两边都在。
        Assert.Contains("a.b.c.z", paths);
        Assert.Contains("b.c.z", paths);
    }

    // ---- 打完补丁的合并文档 ----

    private static System.Xml.XmlDocument Merged(string inner)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml("<Defs>" + inner + "</Defs>");
        return doc;
    }

    /// <summary>
    /// 身份真撞上(同类型、同键、键来源一样、抽象与否一样)时**前者胜**。
    ///
    /// 依据是 DefDatabase.Add:同名的第二份要么在同 mod 内被跳过,要么跨 mod 时被改名 ——
    /// 活下来的叫这个名字的始终是第一份。逐 mod 遍历那一路给的是并集,于是路径表里会混进
    /// 那份根本没生效的,而混入在输出上与「作者两处都写了」逐字同形。
    ///
    /// 官方 Data 里 Mercenary_Slasher 就是同一个文件里两份 PawnKindDef。
    /// </summary>
    [Fact]
    public void 身份撞车时前者胜而不是并集()
    {
        var doc = Merged("""
            <ThingDef><defName>Gun</defName><damage>10</damage><liveOnly>x</liveOnly></ThingDef>
            <ThingDef><defName>Gun</defName><damage>99</damage><deadOnly>y</deadOnly></ThingDef>
            """);
        var nodes = PatchedXmlNodes.Extract(doc, 6, 64);

        var gun = Assert.Single(nodes);
        Assert.Contains("liveOnly", gun.Paths);
        Assert.DoesNotContain("deadOnly", gun.Paths);
        Assert.Equal("10", gun.Texts[gun.Paths.IndexOf("damage")]);
    }

    /// <summary>
    /// 抽象父节点与同名的具体 def 是**两个身份**,一份都不许塌缩掉。
    ///
    /// 官方 Data 里三种写法都有:Name= 抽象节点与具体 def 同名(JobDef BabyPlay);
    /// 抽象节点同时带 Name= 和 defName(PawnKindDef Mercenary_Slasher);
    /// 抽象节点只有 Name=(SoundDef Designate_DragStandard_Changed)。
    /// 这里父子两份路径**都算数** —— 子 def 继承了父节点写的那些行,塌缩掉一份就会让
    /// 它们在 xml 列上变成确定的 no。第一版把这三处全判错了。
    /// </summary>
    [Fact]
    public void 抽象父节点与同名具体def各占一条()
    {
        var byName = Merged("""
            <JobDef Abstract="True" Name="BabyPlay"><driverClass>D</driverClass></JobDef>
            <JobDef ParentName="BabyPlay"><defName>BabyPlay</defName><reportString>r</reportString></JobDef>
            """);
        Assert.Equal(2, PatchedXmlNodes.Extract(byName, 6, 64).Count);

        // 抽象节点同时带 Name= 和 defName:两边都按 defName 立键,只有抽象与否分得开。
        var bothKeys = Merged("""
            <PawnKindDef Name="SlasherBase" Abstract="True"><defName>Slasher</defName><apparelMoney>1</apparelMoney></PawnKindDef>
            <PawnKindDef ParentName="SlasherBase"><defName>Slasher</defName><combatPower>2</combatPower></PawnKindDef>
            """);
        var pair = PatchedXmlNodes.Extract(bothKeys, 6, 64);
        Assert.Equal(2, pair.Count);
        Assert.All(pair, n => Assert.Equal("Slasher", n.NodeKey));
        Assert.Contains(pair, n => n.Paths.Contains("apparelMoney"));
        Assert.Contains(pair, n => n.Paths.Contains("combatPower"));
    }

    /// <summary>def 类型不同就是两个节点,哪怕 defName 一样。</summary>
    [Fact]
    public void 同名但类型不同的def各占一条()
    {
        var doc = Merged("""
            <ThingDef><defName>Gun</defName><damage>10</damage></ThingDef>
            <RecipeDef><defName>Gun</defName><workAmount>5</workAmount></RecipeDef>
            """);
        Assert.Equal(2, PatchedXmlNodes.Extract(doc, 6, 64).Count);
    }

    /// <summary>抽象节点没有 defName,按 Name= 立键 —— 祖先那一路的 parent 全靠它。</summary>
    [Fact]
    public void 抽象节点按Name立键()
    {
        var doc = Merged("""<ThingDef Name="BaseGun" Abstract="True"><speed>70</speed></ThingDef>""");
        var node = Assert.Single(PatchedXmlNodes.Extract(doc, 6, 64));
        Assert.Equal("BaseGun", node.NodeKey);
        Assert.True(node.KeyIsName);
    }

    /// <summary>
    /// 原文里没出现过的路径才算补丁加的。整个节点在原文里都不存在(<c>before</c> 为
    /// <c>null</c>)时全算 —— 那种节点确实整个是补丁造的。
    /// </summary>
    [Fact]
    public void 补丁标记只认原文里没出现过的路径()
    {
        var paths = new List<string> { "defName", "damage", "recipeMaker.researchPrerequisite" };
        var before = new HashSet<string>(StringComparer.Ordinal) { "defName", "damage" };
        Assert.Equal([false, false, true], PatchedXmlNodes.PatchedFlags(paths, before));
        Assert.Equal([true, true, true], PatchedXmlNodes.PatchedFlags(paths, null));
    }
}
