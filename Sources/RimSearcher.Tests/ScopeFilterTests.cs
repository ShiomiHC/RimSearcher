using RimSearcher.Core;

namespace RimSearcher.Tests;

public class ScopeFilterTests
{
    private const string VanillaRoot = @"C:\game\Data\Core";
    private const string HarRoot = @"C:\mods\HAR";
    private const string MiliraRoot = @"C:\mods\Milira";

    private static ScopeCatalog Catalog() => ScopeCatalog.Build(
        [("vanilla", VanillaRoot), ("har", HarRoot), ("milira", MiliraRoot)], null, null);

    private static ScoredCandidate<string> Candidate(string name, double score, string root)
        => new(name, score, Path.Combine(root, name + ".cs"));

    [Fact]
    public void OutOfScopeCandidates_AreCountedNotReturned()
    {
        var scope = Catalog().Resolve("vanilla,har");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, VanillaRoot),
            Candidate("b", 95, HarRoot),
            Candidate("d", 99, MiliraRoot)
        ], scope, limit: 10);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.TotalInScope);
        Assert.Equal(("milira", 1), result.OutOfScope.Single());
        Assert.Equal(1, result.OutOfScopeTotal);
    }

    // 相对首条掉 40 分即视为断层：低于它的多是纯子串噪音
    [Fact]
    public void ScoreGap_TruncatesLowRelevanceTail()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, VanillaRoot),
            Candidate("b", 95, VanillaRoot),
            Candidate("c", 20, VanillaRoot)
        ], scope, limit: 10);

        Assert.Equal(2, result.Items.Count);
        Assert.True(result.TruncatedByScoreGap);
        Assert.Equal(3, result.TotalInScope);
        Assert.Equal(1, result.HiddenCount);
    }

    [Fact]
    public void ScoreGap_CanBeDisabled()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, VanillaRoot),
            Candidate("c", 20, VanillaRoot)
        ], scope, limit: 10, scoreGap: null);

        Assert.Equal(2, result.Items.Count);
        Assert.False(result.TruncatedByScoreGap);
    }

    // 「全是弱匹配」的查询不该被砍到只剩一条，故断层收口要求首条足够强（>= 70）
    [Fact]
    public void ScoreGap_RequiresAStrongTopHit()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 50, VanillaRoot),
            Candidate("b", 5, VanillaRoot)
        ], scope, limit: 10);

        Assert.Equal(2, result.Items.Count);
        Assert.False(result.TruncatedByScoreGap);
    }

    [Fact]
    public void Limit_CapsItemsButNotTotalInScope()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, VanillaRoot),
            Candidate("b", 99, VanillaRoot),
            Candidate("c", 98, VanillaRoot)
        ], scope, limit: 1);

        Assert.Single(result.Items);
        Assert.Equal("a", result.Items[0].Item);
        Assert.Equal(3, result.TotalInScope);
        Assert.Equal(2, result.HiddenCount);
    }

    [Fact]
    public void ZeroLimit_MeansUnlimited()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, VanillaRoot),
            Candidate("b", 99, VanillaRoot),
            Candidate("c", 98, VanillaRoot)
        ], scope, limit: 0);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(0, result.HiddenCount);
    }

    // 同分时按 scope 表达式里的书写顺序排
    [Fact]
    public void EqualScores_AreOrderedByScopeRank()
    {
        var scope = Catalog().Resolve("milira,vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("fromVanilla", 90, VanillaRoot),
            Candidate("fromMilira", 90, MiliraRoot)
        ], scope, limit: 10);

        Assert.Equal("fromMilira", result.Items[0].Item);
        Assert.Equal("fromVanilla", result.Items[1].Item);
    }

    [Fact]
    public void SourceLabels_AppearOnlyWhenMultipleSourcesSelected()
    {
        var multi = ScopeFilter.Apply(
            [Candidate("a", 100, VanillaRoot)], Catalog().Resolve("vanilla,har"), limit: 10);
        Assert.Equal("vanilla", multi.Items[0].SourceName);

        var single = ScopeFilter.Apply(
            [Candidate("a", 100, VanillaRoot)], Catalog().Resolve("vanilla"), limit: 10);
        Assert.Null(single.Items[0].SourceName);
    }

    [Fact]
    public void EmptyInput_ProducesEmptyResult()
    {
        var result = ScopeFilter.Apply(
            Array.Empty<ScoredCandidate<string>>(), Catalog().Resolve("vanilla"), limit: 10);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalInScope);
        Assert.Empty(result.OutOfScope);
    }

    [Fact]
    public void OutOfScopeCounts_AreOrderedByFrequency()
    {
        var scope = Catalog().Resolve("vanilla");

        var result = ScopeFilter.Apply(
        [
            Candidate("a", 100, MiliraRoot),
            Candidate("b", 99, MiliraRoot),
            Candidate("c", 98, HarRoot)
        ], scope, limit: 10);

        Assert.Equal(("milira", 2), result.OutOfScope[0]);
        Assert.Equal(("har", 1), result.OutOfScope[1]);
    }
}
