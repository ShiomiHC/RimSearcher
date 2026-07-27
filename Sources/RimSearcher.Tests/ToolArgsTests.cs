using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

public class ToolArgsTests
{
    private static readonly ToolArgSpec Spec = new(
        "rimworld-searcher__locate",
        "query (search text).",
        "query (required), scope, limit.");

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void GetRequiredString_AcceptsCanonicalName()
        => Assert.Equal("Pawn", ToolArgs.GetRequiredString(Args("""{"query":"Pawn"}"""), Spec, "query", "name"));

    [Fact]
    public void GetRequiredString_AcceptsAlias()
        => Assert.Equal("Pawn", ToolArgs.GetRequiredString(Args("""{"name":"Pawn"}"""), Spec, "query", "name"));

    // max_results 与 maxResults 的差异必须吸收，否则调用方从一个工具类推到另一个就报错
    [Fact]
    public void KeyMatching_IgnoresCaseAndSeparators()
    {
        Assert.Equal(7, ToolArgs.GetInt(Args("""{"max_results":7}"""), 1, "maxResults"));
        Assert.Equal(7, ToolArgs.GetInt(Args("""{"Max-Results":7}"""), 1, "maxResults"));
    }

    // null 值等同于没写，应继续往后找别名
    [Fact]
    public void NullValues_AreSkipped()
        => Assert.Equal("Pawn", ToolArgs.GetRequiredString(Args("""{"query":null,"name":"Pawn"}"""), Spec, "query", "name"));

    [Fact]
    public void MissingRequired_ThrowsWithCorrectionHints()
    {
        var exception = Assert.Throws<ToolArgumentException>(
            () => ToolArgs.GetRequiredString(Args("""{"scope":"vanilla"}"""), Spec, "query", "name"));

        Assert.Contains("query", exception.Message);
        Assert.Contains("scope", exception.Message);          // 收到了什么键
        Assert.Contains("All parameters", exception.Message); // 该怎么改
    }

    [Fact]
    public void BlankRequired_IsRejected()
        => Assert.Throws<ToolArgumentException>(
            () => ToolArgs.GetRequiredString(Args("""{"query":"   "}"""), Spec, "query"));

    // 单值位上收到数组时取首元素——调用方偶发把标量包成数组
    [Fact]
    public void ScalarSlot_UnwrapsSingleElementArray()
        => Assert.Equal("Pawn", ToolArgs.GetRequiredString(Args("""{"query":["Pawn"]}"""), Spec, "query"));

    [Theory]
    [InlineData("""{"limit":5}""", 5)]
    [InlineData("""{"limit":"5"}""", 5)]
    [InlineData("""{"limit":"5.9"}""", 5)]
    [InlineData("""{"limit":"abc"}""", 42)]
    [InlineData("""{}""", 42)]
    public void GetInt_CoercesLoosely(string json, int expected)
        => Assert.Equal(expected, ToolArgs.GetInt(Args(json), 42, "limit"));

    [Theory]
    [InlineData("""{"ignoreCase":true}""", true)]
    [InlineData("""{"ignoreCase":false}""", false)]
    [InlineData("""{"ignoreCase":"yes"}""", true)]
    [InlineData("""{"ignoreCase":"off"}""", false)]
    [InlineData("""{"ignoreCase":1}""", true)]
    [InlineData("""{"ignoreCase":0}""", false)]
    [InlineData("""{"ignoreCase":"maybe"}""", true)]
    [InlineData("""{}""", true)]
    public void GetBool_CoercesLoosely(string json, bool expected)
        => Assert.Equal(expected, ToolArgs.GetBool(Args(json), true, "ignoreCase"));

    // locate 的过滤前缀会被带到只认裸名的工具上
    [Theory]
    [InlineData("def:VoidNode", "VoidNode")]
    [InlineData("type:CompVoidNode", "CompVoidNode")]
    [InlineData("DEF:VoidNode", "VoidNode")]
    [InlineData("  method:CompTick  ", "CompTick")]
    [InlineData("Pawn", "Pawn")]
    [InlineData("RimWorld.Pawn", "RimWorld.Pawn")]
    public void StripLocateFilterPrefix_RemovesOnlyKnownPrefixes(string input, string expected)
        => Assert.Equal(expected, ToolArgs.StripLocateFilterPrefix(input));

    [Fact]
    public void ReceivedKeys_ListsWhatArrived()
    {
        var keys = ToolArgs.ReceivedKeys(Args("""{"query":"a","scope":"b"}"""));

        Assert.Equal(["query", "scope"], keys);
    }

    [Fact]
    public void NonObjectArguments_AreHandledGracefully()
    {
        Assert.Empty(ToolArgs.ReceivedKeys(Args("\"scalar\"")));
        Assert.Null(ToolArgs.GetOptionalString(Args("\"scalar\""), "query"));
    }
}

public class ScopeArgsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{}""", ScopeArgs.DefaultDisplayLimit)]
    [InlineData("""{"limit":5}""", 5)]
    [InlineData("""{"limit":"5"}""", 5)]
    [InlineData("""{"maxResults":7}""", 7)]
    [InlineData("""{"top":3}""", 3)]
    [InlineData("""{"limit":"all"}""", 0)]
    [InlineData("""{"limit":"*"}""", 0)]
    [InlineData("""{"limit":"everything"}""", 0)]
    [InlineData("""{"limit":-1}""", 0)]
    [InlineData("""{"limit":0}""", 0)]
    public void GetDisplayLimit_UnderstandsNumbersAndExpandKeywords(string json, int expected)
        => Assert.Equal(expected, ScopeArgs.GetDisplayLimit(Args(json)));

    [Fact]
    public void Resolve_AcceptsScopeAliases()
    {
        var catalog = ScopeCatalog.Build([("vanilla", @"C:\a"), ("milira", @"C:\b")], null, null);

        foreach (var key in new[] { "scope", "source", "mod", "in" })
        {
            var selection = ScopeArgs.Resolve(catalog, Args($$"""{"{{key}}":"milira"}"""));
            Assert.Equal(1, selection.SelectedCount);
            Assert.Equal("milira", selection.Expression);
        }
    }

    [Fact]
    public void FoldLine_ExplainsWhyItemsAreHidden()
    {
        var withGap = new ScopedResult<string>(
            [new ScopedEntry<string>("a", 100, null)], totalInScope: 5,
            outOfScope: [], truncatedByScoreGap: true);
        Assert.Contains("lower relevance", ScopeArgs.FoldLine(withGap));

        var withoutGap = new ScopedResult<string>(
            [new ScopedEntry<string>("a", 100, null)], totalInScope: 5,
            outOfScope: [], truncatedByScoreGap: false);
        Assert.DoesNotContain("lower relevance", ScopeArgs.FoldLine(withoutGap));

        var complete = new ScopedResult<string>(
            [new ScopedEntry<string>("a", 100, null)], totalInScope: 1,
            outOfScope: [], truncatedByScoreGap: false);
        Assert.Null(ScopeArgs.FoldLine(complete));
    }

    [Fact]
    public void ScopeReport_AggregatesAcrossSections()
    {
        var catalog = ScopeCatalog.Build([("vanilla", @"C:\a"), ("milira", @"C:\b")], null, null);
        var scope = catalog.Resolve("vanilla");

        var report = new ScopeReport();
        report.Add("milira", 2);
        report.Add("milira", 3);

        Assert.True(report.HasOutOfScope);

        var rendered = report.Render(scope);
        Assert.Contains("milira 5", rendered);
        Assert.Contains("vanilla", rendered);
    }
}
