using System;
using System.Linq;
using RimSearcher.DataMod;

namespace RimSearcher.Tests;

/// <summary>
/// 导出器那一层的闸。这一层跑在游戏进程里,长期没有任何测试 —— 而第八轮盲测的归因里
/// <c>data_side</c> 的摩擦单价最高(1.00,cli_output 是 0.71),最贵的东西恰恰落在没闸的地方。
///
/// 能进这里的只有**不碰 RimWorld 类型**的部分(见 csproj 里那段 Compile Link 的说明)。
/// </summary>
public class ExporterTests
{
    // 故意长成实测出问题的那个形状:基类声明私有字段,而 def 的运行时类是子类。
    // 对应真实的 CreepJoinerBaseDef(private float weight = 1f)与它的四个子类。
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
    /// <c>GetFields(NonPublic | Instance)</c> 在派生类上拿不到它们 —— 这是反射规则不是 bug,
    /// 但它让 <c>CreepJoinerBaseDef</c> 的五个字段在 24 个 def 上整条消失,而落空那句话把
    /// 成因说成「每个 def 上都是 null」(<c>weight = 1f</c> 一个都不是 null)。
    /// **说错成因比说不出更坏**,所以这一条守的是枚举本身,不是措辞。
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
    /// 少了这条,走基类链的写法会把同一个名字发两遍,导出侧变成同名两行、值还可能不同。
    /// </summary>
    [Fact]
    public void 被子类遮住的同名字段只算近的那一个()
    {
        var fields = FieldWalk.InstanceFields(typeof(ShadowingDefShape))
                              .Where(f => f.Name == "label").ToList();

        Assert.Single(fields);
        Assert.Equal(typeof(ShadowingDefShape), fields[0].DeclaringType);
    }
}
