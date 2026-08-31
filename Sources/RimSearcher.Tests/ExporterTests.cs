using System;
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
}
