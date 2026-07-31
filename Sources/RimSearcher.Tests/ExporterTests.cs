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
}
