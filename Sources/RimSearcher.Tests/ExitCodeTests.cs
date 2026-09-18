using RimSearcher.Cli;
using RimSearcher.Output;
using Xunit;

namespace RimSearcher.Tests;

/// <summary>
/// 零行那个退出码按印出的表细分(Docs/25 丙):1 = 量了是空,3 = absent 表在场(没量),
/// 4 = found_as 表在场(名字在别处)。码由表决定,产地 <see cref="Runner.Refine"/>。
/// 下游此前得读 notes 原文才分得开「量了是空」与「查错了维度」。
/// </summary>
public class ExitCodeTests
{
    // NameLookup 是 internal;表名与列名在 GateTests 里同样按字面钉。
    private const string FoundAs = "found_as";
    private static readonly string[] FoundAsColumns = ["name", "is", "in", "next"];

    [Theory]
    // 量了是空:三张表一张都没印,或只印了 empty_because(自己的筛子挡的)。
    [InlineData(Runner.ExitNoResults, "get", "NoSuchDefAnywhere")]
    [InlineData(Runner.ExitNoResults, "keyed", "NoSuchKeyAnywhere")]
    [InlineData(Runner.ExitNoResults, "where", "compClass", "RimWorld.CompNoSuchThing")]
    // 名字在别处:found_as 表在场。
    [InlineData(Runner.ExitFoundElsewhere, "get", "BaseBullet")]
    [InlineData(Runner.ExitFoundElsewhere, "keyed", "Bullet_Revolver")]
    [InlineData(Runner.ExitFoundElsewhere, "inherit", "ThingDef")]
    // 自己的筛子与名字在别处同时在场:found_as 说的是名字的落点,比「筛空了」多一件事。
    [InlineData(Runner.ExitFoundElsewhere, "search", "TestModGun", "--scope", "ludeon.rimworld")]
    // 没量:absent 表在场。
    [InlineData(Runner.ExitLayerAbsent, "code-search", "public", "--source", "zz.emptytree")]
    public void 零行的退出码由印出的表决定(int expected, params string[] argv)
    {
        var (_, _, code) = Fixture.Run(argv);
        Assert.Equal(expected, code);
    }

    [Theory]
    // 缺层的库上零行:3。
    [InlineData(Runner.ExitLayerAbsent, "economy")]
    [InlineData(Runner.ExitLayerAbsent, "keyed", "Bullet_Revolver")]
    // 同一份库上,名字在别的快照里:4 —— 层在,名字不在。
    [InlineData(Runner.ExitFoundElsewhere, "get", "BaseBullet")]
    public void 缺层的库上零行是3不是1(int expected, params string[] argv)
    {
        var (_, _, code) = Fixture.Run([.. argv, "--db", Fixture.OtherDb]);
        Assert.Equal(expected, code);
    }

    /// <summary>有行就是 0:absent / found_as 只补充,不改「答了」这件事。</summary>
    [Theory]
    [InlineData("get", "BaseBullet", "Bullet_Revolver")]
    [InlineData("keyed", "CannotUseNoPower")]
    [InlineData("callers", "Verse.Widgets.Label")]
    [InlineData("inherit", "BaseBullet")]
    public void 有行时表在场也是0(params string[] argv)
    {
        var (stdout, _, code) = Fixture.Run([.. argv, "--json"]);
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var supplemented =
            (root.TryGetProperty(Report.AbsentTable, out var a) && a.GetArrayLength() > 0) ||
            (root.TryGetProperty(FoundAs, out var f) && f.GetArrayLength() > 0);
        Assert.True(supplemented, "用例选错了:这份输出既没有 absent 也没有 found_as,证不了「表在场也是 0」");
    }

    /// <summary>两张表都在时缺层优先:没量过的零,说它在别处也不成立。</summary>
    [Fact]
    public void 两张表都在时缺层优先()
    {
        var report = new Report();
        report.Table(FoundAs, FoundAsColumns,
            [new Dictionary<string, object?> { ["name"] = "X", ["is"] = "class", ["in"] = "a", ["next"] = "b" }]);
        Assert.Equal(Runner.ExitFoundElsewhere, Runner.Refine(Runner.ExitNoResults, report));
        report.Table(Report.AbsentTable, ["layer", "state", "next"],
            [new Dictionary<string, object?> { ["layer"] = "economy", ["state"] = "empty", ["next"] = "c" }]);
        Assert.Equal(Runner.ExitLayerAbsent, Runner.Refine(Runner.ExitNoResults, report));
        // 只对 1 细分:0 / 2 / 70 原样。
        Assert.Equal(0, Runner.Refine(0, report));
        Assert.Equal(Runner.ExitUsage, Runner.Refine(Runner.ExitUsage, report));
        // 空表不算在场:键恒在而行为零是「没这回事」。
        var empty = new Report();
        empty.Table(FoundAs, FoundAsColumns, []);
        Assert.Equal(Runner.ExitNoResults, Runner.Refine(Runner.ExitNoResults, empty));
    }

    /// <summary>参考页与 SKILL 说的码要与产地一致:四个数都在表里。</summary>
    [Fact]
    public void 参考页列出全部退出码()
    {
        var stdout = File.ReadAllText(GateTests.ReferencePath);
        foreach (var code in new[] { 0, Runner.ExitNoResults, Runner.ExitLayerAbsent, Runner.ExitFoundElsewhere, Runner.ExitUsage, Runner.ExitInternal })
            Assert.Contains($"| `{code}` |", stdout, StringComparison.Ordinal);
    }
}
