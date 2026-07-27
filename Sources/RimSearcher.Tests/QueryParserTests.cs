using RimSearcher.Core;

namespace RimSearcher.Tests;

public class QueryParserTests
{
    // 'scope:xxx' 混进 query 是必然会发生的（调用方已经在用 type:/def: 前缀），
    // 必须被当成 scope 而不是一个搜不到东西的关键词
    [Theory]
    [InlineData("scope:milira")]
    [InlineData("s:milira")]
    [InlineData("source:milira")]
    [InlineData("in:milira")]
    public void ScopeFilter_AcceptsItsAliases(string token)
    {
        var parsed = QueryParser.Parse($"{token} CompShield");

        Assert.Equal("milira", parsed.ScopeFilter);
        Assert.Equal(["CompShield"], parsed.Keywords);
    }

    [Theory]
    [InlineData("type:Pawn", "TypeFilter", "Pawn")]
    [InlineData("t:Pawn", "TypeFilter", "Pawn")]
    [InlineData("class:Pawn", "TypeFilter", "Pawn")]
    [InlineData("method:CompTick", "MethodFilter", "CompTick")]
    [InlineData("m:CompTick", "MethodFilter", "CompTick")]
    [InlineData("field:hitPoints", "FieldFilter", "hitPoints")]
    [InlineData("property:Label", "FieldFilter", "Label")]
    [InlineData("def:Apparel_ShieldBelt", "DefFilter", "Apparel_ShieldBelt")]
    [InlineData("d:Apparel_ShieldBelt", "DefFilter", "Apparel_ShieldBelt")]
    public void KnownPrefixes_LandInTheRightSlot(string query, string slot, string expected)
    {
        var parsed = QueryParser.Parse(query);

        var actual = slot switch
        {
            "TypeFilter" => parsed.TypeFilter,
            "MethodFilter" => parsed.MethodFilter,
            "FieldFilter" => parsed.FieldFilter,
            _ => parsed.DefFilter
        };

        Assert.Equal(expected, actual);
        Assert.Empty(parsed.Keywords);
    }

    // 未知前缀原样留作关键词，别把 'RimWorld:Pawn' 这类写法吃掉
    [Fact]
    public void UnknownPrefix_StaysAKeyword()
    {
        var parsed = QueryParser.Parse("whatever:Pawn");

        Assert.Equal(["whatever:Pawn"], parsed.Keywords);
        Assert.Null(parsed.TypeFilter);
    }

    [Fact]
    public void PlainText_BecomesKeywords()
    {
        var parsed = QueryParser.Parse("shield belt");

        Assert.Equal(["shield", "belt"], parsed.Keywords);
    }

    [Fact]
    public void QuotedSegments_StayTogether()
    {
        var parsed = QueryParser.Parse("\"shield belt\" armor");

        Assert.Equal(["shield belt", "armor"], parsed.Keywords);
    }

    [Fact]
    public void EmptyQuery_ProducesEmptyResult()
    {
        var parsed = QueryParser.Parse("   ");

        Assert.Empty(parsed.Keywords);
        Assert.Null(parsed.ScopeFilter);
    }

    [Fact]
    public void PrefixesAreCaseInsensitive()
        => Assert.Equal("Pawn", QueryParser.Parse("TYPE:Pawn").TypeFilter);

    // scope 不该混进检索词，否则会被当成待搜索的内容
    [Fact]
    public void CombinedSearchTerm_ExcludesScope()
    {
        var parsed = QueryParser.Parse("scope:milira type:Comp shield");

        var combined = QueryParser.GetCombinedSearchTerm(parsed);

        Assert.Contains("Comp", combined);
        Assert.Contains("shield", combined);
        Assert.DoesNotContain("milira", combined);
    }
}
