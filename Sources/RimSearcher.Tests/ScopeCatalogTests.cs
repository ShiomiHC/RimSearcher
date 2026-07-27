using RimSearcher.Core;

namespace RimSearcher.Tests;

public class ScopeCatalogTests
{
    private const string VanillaRoot = @"C:\game\Data\Core";
    private const string HarRoot = @"C:\mods\HAR";
    private const string MiliraRoot = @"C:\mods\Milira";

    private static ScopeCatalog Build(string? defaultScope = null) => ScopeCatalog.Build(
        [("vanilla", VanillaRoot), ("har", HarRoot), ("milira", MiliraRoot)],
        new Dictionary<string, List<string>>
        {
            ["framework"] = ["har"],
            ["addons"] = ["har", "milira"]
        },
        defaultScope);

    [Fact]
    public void NoExpression_SelectsEverything()
    {
        var selection = Build().Resolve(null);

        Assert.True(selection.IncludesEverything);
        Assert.Equal(3, selection.SelectedCount);
        Assert.Equal(ScopeCatalog.EverythingKeyword, selection.Expression);
    }

    [Fact]
    public void SourceName_SelectsThatSourceOnly()
    {
        var selection = Build().Resolve("har");

        Assert.Equal(1, selection.SelectedCount);
        Assert.True(selection.Contains(Path.Combine(HarRoot, "Comp.cs")));
        Assert.False(selection.Contains(Path.Combine(VanillaRoot, "Pawn.cs")));

        // 单源时来源标签是纯噪音，不该标
        Assert.False(selection.ShowLabels);
    }

    [Fact]
    public void GroupName_ExpandsToItsMembers()
    {
        var selection = Build().Resolve("addons");

        Assert.Equal(2, selection.SelectedCount);
        Assert.True(selection.Contains(Path.Combine(HarRoot, "a.cs")));
        Assert.True(selection.Contains(Path.Combine(MiliraRoot, "b.cs")));
        Assert.False(selection.Contains(Path.Combine(VanillaRoot, "c.cs")));
        Assert.True(selection.ShowLabels);
    }

    [Fact]
    public void ExclusionOnly_SelectsEverythingElse()
    {
        var selection = Build().Resolve("-vanilla");

        Assert.Equal(2, selection.SelectedCount);
        Assert.False(selection.Contains(Path.Combine(VanillaRoot, "Pawn.cs")));
        Assert.True(selection.Contains(Path.Combine(HarRoot, "a.cs")));
    }

    // 回归：includesEverything 曾按 nextRank 算，而 'all' 先把 vanilla 计入、'-vanilla' 再排除，
    // 于是排除了源却仍自称全域——未落在任何源里的路径会被 RankOf 当成命中收进来。
    [Fact]
    public void AllMinusOneSource_IsNotEverything()
    {
        var selection = Build().Resolve("all,-vanilla");

        Assert.Equal(2, selection.SelectedCount);
        Assert.False(selection.IncludesEverything);
        Assert.False(selection.Contains(Path.Combine(VanillaRoot, "Pawn.cs")));

        // 不属于任何已配置源的路径，在非全域选择下必须落在 scope 外
        Assert.False(selection.Contains(@"C:\somewhere\else\x.cs"));
    }

    [Fact]
    public void Exclusion_WinsRegardlessOfOrder()
    {
        var selection = Build().Resolve("-vanilla,all");

        Assert.False(selection.Contains(Path.Combine(VanillaRoot, "Pawn.cs")));
        Assert.True(selection.Contains(Path.Combine(HarRoot, "a.cs")));
    }

    // 全是拼错的名字时退回全域：空集合会让调用方收到「没有结果」并误判成「不存在」
    [Fact]
    public void UnknownTokens_FallBackToEverything()
    {
        var selection = Build().Resolve("typo-nonexistent");

        Assert.True(selection.IncludesEverything);
        Assert.Equal(3, selection.SelectedCount);
    }

    [Fact]
    public void WritingOrder_DecidesRank()
    {
        var selection = Build().Resolve("milira,vanilla");

        Assert.True(selection.RankOf(Path.Combine(MiliraRoot, "a.cs"))
                  < selection.RankOf(Path.Combine(VanillaRoot, "b.cs")));
    }

    [Fact]
    public void DefaultExpression_AppliesWhenNoneGiven()
    {
        var selection = Build(defaultScope: "har").Resolve(null);

        Assert.Equal(1, selection.SelectedCount);
        Assert.True(selection.Contains(Path.Combine(HarRoot, "a.cs")));
    }

    [Fact]
    public void ExplicitExpression_OverridesDefault()
    {
        var selection = Build(defaultScope: "har").Resolve("vanilla");

        Assert.True(selection.Contains(Path.Combine(VanillaRoot, "a.cs")));
        Assert.False(selection.Contains(Path.Combine(HarRoot, "b.cs")));
    }

    // 嵌套配置（<mod>/Defs 与 <mod>/1.6/Defs 同时在册）时最长根前缀胜出
    [WindowsFact("盘符路径在 Unix 上不是路径，`C:\\mods\\X\\1.6` 只是个普通文件名，分不出前缀层级")]
    public void LongestRootPrefix_Wins()
    {
        var catalog = ScopeCatalog.Build(
            [("mod", @"C:\mods\X"), ("mod-current", @"C:\mods\X\1.6")], null, null);

        var selection = catalog.Everything;

        Assert.Equal("mod-current", selection.SourceNameOf(@"C:\mods\X\1.6\Defs\a.xml"));
        Assert.Equal("mod", selection.SourceNameOf(@"C:\mods\X\Defs\a.xml"));
    }

    // 前缀相同但不在目录边界上的路径不算命中：C:\mods\X 不该收下 C:\mods\XY
    [WindowsFact("目录边界由 `\\` 划定，Unix 上它是普通字符，构不成边界之分")]
    public void RootMatching_RespectsDirectoryBoundaries()
    {
        var catalog = ScopeCatalog.Build([("mod", @"C:\mods\X")], null, null);

        Assert.Equal("mod", catalog.Everything.SourceNameOf(@"C:\mods\X\a.cs"));
        Assert.Null(catalog.Everything.SourceNameOf(@"C:\mods\XY\a.cs"));
    }

    [Fact]
    public void SourceNames_AreCaseInsensitive()
    {
        var selection = Build().Resolve("VANILLA");

        Assert.Equal(1, selection.SelectedCount);
        Assert.True(selection.Contains(Path.Combine(VanillaRoot, "a.cs")));
    }

    // config 手误不该让服务器起不来
    [Fact]
    public void GroupsReferencingUnknownSources_AreDropped()
    {
        var catalog = ScopeCatalog.Build(
            [("vanilla", VanillaRoot)],
            new Dictionary<string, List<string>> { ["ghost"] = ["does-not-exist"] },
            null);

        Assert.DoesNotContain("ghost", catalog.GroupNames);
        Assert.True(catalog.Resolve("ghost").IncludesEverything);
    }

    // 同名条目跨 C#/XML 两侧归为一个源，多个根都算它的
    [Fact]
    public void SameName_MergesIntoOneSourceWithMultipleRoots()
    {
        var catalog = ScopeCatalog.Build(
            [("har", HarRoot), ("har", @"C:\mods\HAR-Defs")], null, null);

        Assert.Single(catalog.Sources);

        var selection = catalog.Resolve("har");
        Assert.True(selection.Contains(Path.Combine(HarRoot, "a.cs")));
        Assert.True(selection.Contains(@"C:\mods\HAR-Defs\b.xml"));
    }

    [Fact]
    public void OutOfScopeLabel_NamesTheOwningSource()
    {
        var selection = Build().Resolve("vanilla");

        Assert.Equal("milira", selection.OutOfScopeLabel(Path.Combine(MiliraRoot, "a.cs")));
        Assert.Equal("unindexed", selection.OutOfScopeLabel(@"C:\nowhere\a.cs"));
    }

    [Fact]
    public void DescribeAvailable_ListsGroupsAndSources()
    {
        var description = Build().DescribeAvailable();

        Assert.Contains("framework", description);
        Assert.Contains("vanilla", description);
        Assert.Contains(ScopeCatalog.EverythingKeyword, description);
    }
}
