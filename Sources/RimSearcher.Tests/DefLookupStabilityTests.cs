using RimSearcher.Core;

namespace RimSearcher.Tests;

// 同一个源里的同名 def（vanilla 的 Human 就是 ThingDef / BodyDef / HediffGiverSetDef
// 各一条）Rank 相同，胜负原先落在并发扫描写入 ConcurrentBag 的顺序上：重建一次索引，
// inspect('Human') 就换一条 def 返回——同一个问题在不同时刻给出不同答案。
public class DefLookupStabilityTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string BuildDefs()
    {
        var root = _workspace.Dir("Defs");

        _workspace.WriteFile(Path.Combine("Defs", "Races.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>Human</defName>\n    <label>human</label>\n  </ThingDef>\n</Defs>\n");
        _workspace.WriteFile(Path.Combine("Defs", "Bodies.xml"),
            "<Defs>\n  <BodyDef>\n    <defName>Human</defName>\n  </BodyDef>\n</Defs>\n");
        _workspace.WriteFile(Path.Combine("Defs", "HediffGiverSets.xml"),
            "<Defs>\n  <HediffGiverSetDef>\n    <defName>Human</defName>\n  </HediffGiverSetDef>\n</Defs>\n");

        return root;
    }

    private (DefIndexer Indexer, ScopeCatalog Catalog) Index(string root)
    {
        var indexer = new DefIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    [Fact]
    public void SameNameInOneSource_ResolvesToTheSameDefEveryBuild()
    {
        var root = BuildDefs();

        var picks = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var (indexer, catalog) = Index(root);
            var lookup = indexer.Lookup("Human", catalog.Everything);

            Assert.NotNull(lookup.Location);
            picks.Add($"{lookup.Location!.DefType}|{lookup.Location.FilePath}");
        }

        Assert.Single(picks.Distinct());
    }

    [Fact]
    public void AmbiguousLookup_ReportsWhichTypesShareTheName()
    {
        var (indexer, catalog) = Index(BuildDefs());

        var lookup = indexer.Lookup("Human", catalog.Everything);

        Assert.True(lookup.AmbiguousInScope);
        Assert.Equal(3, lookup.InScopeCount);
        Assert.Equal(["BodyDef", "HediffGiverSetDef", "ThingDef"], lookup.InScopeDefTypes);
    }

    [Fact]
    public void DefType_PicksThatOne()
    {
        var (indexer, catalog) = Index(BuildDefs());

        var lookup = indexer.Lookup("Human", catalog.Everything, defType: "ThingDef");

        Assert.Equal("ThingDef", lookup.Location!.DefType);
        Assert.False(lookup.RequestedDefTypeUnavailable);
        // 收窄只影响挑哪一条：「有几条同名、都是些什么」仍按 scope 内的全部算
        Assert.Equal(3, lookup.InScopeCount);
        Assert.Equal(3, lookup.InScopeDefTypes.Count);
    }

    // 上面那组三条 def 分别在三个文件里，ThenBy(FilePath) 一步就定了序，DefType/DefName
    // 两条 tiebreak 永远排不到。三条写进同一个文件才轮得到它们。
    [Fact]
    public void SameFile_SameNameDifferentTypes_TieBreaksOnDefType()
    {
        var root = _workspace.Dir("AllDefs");
        _workspace.WriteFile(Path.Combine("AllDefs", "All.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>Human</defName>\n  </ThingDef>\n"
            + "  <HediffGiverSetDef>\n    <defName>Human</defName>\n  </HediffGiverSetDef>\n"
            + "  <BodyDef>\n    <defName>Human</defName>\n  </BodyDef>\n</Defs>\n");

        var indexer = new DefIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var lookup = indexer.Lookup("Human", catalog.Everything);

        Assert.Equal("BodyDef", lookup.Location!.DefType);
    }

    [Fact]
    public void DefType_ThatDoesNotExist_StillAnswersButSaysSo()
    {
        var (indexer, catalog) = Index(BuildDefs());

        var lookup = indexer.Lookup("Human", catalog.Everything, defType: "PawnKindDef");

        Assert.NotNull(lookup.Location);
        Assert.True(lookup.RequestedDefTypeUnavailable);
    }
}
