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

    // 缺陷回归：模糊匹配参数过去没有长度闸。实测一条 100 KB 的 query 让服务端 210% CPU
    // 烧了 77 秒，再把那 100 KB 原样回显进 "No results for '…'"——拖垮进程之余，
    // 还把等量垃圾塞回调用方上下文。语料里最长的符号名远在 256 以内，越界一定是误用。
    [Fact]
    public void FuzzyString_OverTheLengthCap_IsRefusedWithAnActionableMessage()
    {
        var oversized = new string('A', ToolArgs.MaxFuzzyQueryLength + 1);
        var args = Args(JsonSerializer.Serialize(new { query = oversized }));

        var ex = Assert.Throws<ToolArgumentException>(
            () => ToolArgs.GetRequiredFuzzyString(args, Spec, "query", "name"));

        Assert.Contains(ToolArgs.MaxFuzzyQueryLength.ToString(), ex.Message);
        Assert.Contains("search_regex", ex.Message);
        // 拒绝消息本身不得成为放大器：原样回显那 100 KB 与直接返回它没有区别
        Assert.DoesNotContain(oversized, ex.Message);
        Assert.True(ex.Message.Length < 1000);
    }

    [Fact]
    public void FuzzyString_AtExactlyTheCap_IsAccepted()
    {
        var exact = new string('A', ToolArgs.MaxFuzzyQueryLength);
        var args = Args(JsonSerializer.Serialize(new { query = exact }));

        Assert.Equal(exact, ToolArgs.GetRequiredFuzzyString(args, Spec, "query", "name"));
    }

    [Fact]
    public void ForEcho_TruncatesAndSaysHowLongTheOriginalWas()
    {
        var echoed = ToolArgs.ForEcho(new string('A', 5000));

        Assert.True(echoed.Length < 200);
        Assert.Contains("5000 chars total", echoed);
    }

    [Fact]
    public void ForEcho_LeavesOrdinaryQueriesAlone()
        => Assert.Equal("type:CompShield", ToolArgs.ForEcho("type:CompShield"));
}

public class ScopeArgsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 显式数字原样生效；'all' 与 0/负数一律解析成「展开到服务端硬上限」，
    // 不再是一个交给各工具自己翻译的 0 —— TraceTool 曾把它翻译成 50。
    [Theory]
    [InlineData("""{}""", ScopeArgs.DefaultDisplayLimit, false)]
    [InlineData("""{"limit":5}""", 5, false)]
    [InlineData("""{"limit":"5"}""", 5, false)]
    [InlineData("""{"maxResults":7}""", 7, false)]
    [InlineData("""{"top":3}""", 3, false)]
    [InlineData("""{"limit":"all"}""", ScopeArgs.HardLimit, true)]
    [InlineData("""{"limit":"*"}""", ScopeArgs.HardLimit, true)]
    [InlineData("""{"limit":"everything"}""", ScopeArgs.HardLimit, true)]
    [InlineData("""{"limit":-1}""", ScopeArgs.HardLimit, true)]
    [InlineData("""{"limit":0}""", ScopeArgs.HardLimit, true)]
    public void GetDisplayLimit_UnderstandsNumbersAndExpandKeywords(string json, int expectedCount, bool expectedUnlimited)
    {
        var limit = ScopeArgs.GetDisplayLimit(Args(json));
        Assert.Equal(expectedCount, limit.Count);
        Assert.Equal(expectedUnlimited, limit.Unlimited);
    }

    // schema 里的 maximum 只是给 client 的提示，夹紧必须由服务端做
    [Theory]
    [InlineData("""{"limit":5000}""")]
    [InlineData("""{"limit":"5000"}""")]
    [InlineData("""{"maxResults":100000}""")]
    public void GetDisplayLimit_ClampsRequestsAboveTheServerCap(string json)
    {
        var limit = ScopeArgs.GetDisplayLimit(Args(json));
        Assert.Equal(ScopeArgs.HardLimit, limit.Count);
        Assert.True(limit.Unlimited);
    }

    // 缺陷回归：解释不了的 limit 过去被静默换成默认值。
    //
    // 与拼错的 scope 不对称，因为两者退回的方向相反：scope 退回全域给的是**超集**，
    // 调用方少不了东西；limit 退回默认给的是**子集**——要 100 条、拿到 10 条、
    // 而 10 这个数它自己从没写过。这种「静默给少」在只读文本的调用方那里会直接
    // 沉淀成「一共就这么多」。
    [Theory]
    [InlineData("""{"limit":"many"}""")]
    [InlineData("""{"limit":"a lot"}""")]
    [InlineData("""{"limit":true}""")]
    [InlineData("""{"limit":{}}""")]
    [InlineData("""{"limit":["5","7"]}""")]
    public void GetDisplayLimit_RefusesValuesItCannotInterpret(string json)
    {
        var ex = Assert.Throws<ToolArgumentException>(() => ScopeArgs.GetDisplayLimit(Args(json)));

        Assert.Contains("'all'", ex.Message);
        Assert.Contains(ScopeArgs.HardLimit.ToString(), ex.Message);
    }

    // 标量位收到单元素数组是客户端序列化的常见抖动，仍按 ToolArgs 的口径认下来
    [Fact]
    public void GetDisplayLimit_StillAcceptsASingleElementArray()
        => Assert.Equal(7, ScopeArgs.GetDisplayLimit(Args("""{"limit":[7]}""")).Count);

    // 拒绝消息本身不得成为放大器
    [Fact]
    public void GetDisplayLimit_RefusalDoesNotEchoAHugeString()
    {
        var huge = new string('x', 5000);
        var ex = Assert.Throws<ToolArgumentException>(
            () => ScopeArgs.GetDisplayLimit(Args(JsonSerializer.Serialize(new { limit = huge }))));

        Assert.DoesNotContain(huge, ex.Message);
        Assert.True(ex.Message.Length < 500);
    }

    // 显式的小 limit 不得被任何下限抬高（TraceTool 曾写 Math.Max(limit, 50)）
    [Fact]
    public void GetDisplayLimit_NeverRaisesAnExplicitSmallLimit()
    {
        var limit = ScopeArgs.GetDisplayLimit(Args("""{"limit":5}"""), fallback: 100);
        Assert.Equal(5, limit.Count);
        Assert.False(limit.Unlimited);
    }

    // 放大分组配额时仍不得越过硬上限
    [Fact]
    public void ResultLimit_ScaleStaysWithinTheServerCap()
    {
        Assert.Equal(30, ScopeArgs.GetDisplayLimit(Args("""{"limit":10}""")).Scale(3).Count);
        Assert.Equal(ScopeArgs.HardLimit, ScopeArgs.GetDisplayLimit(Args("""{"limit":"all"}""")).Scale(3).Count);
    }

    // 已经展开到硬上限时不能再劝 'all'——那是让调用方原地重试同一个请求
    [Fact]
    public void FoldLine_DoesNotSuggestAllWhenAlreadyExpanded()
    {
        var expanded = ScopeArgs.GetDisplayLimit(Args("""{"limit":"all"}"""));

        // 真的顶到了硬上限：这时才该说出那个数字
        var atCap = new ScopedResult<string>(
            Enumerable.Range(0, ScopeArgs.HardLimit)
                .Select(i => new ScopedEntry<string>($"a{i}", 100, null)).ToList(),
            totalInScope: ScopeArgs.HardLimit + 5,
            outOfScope: [], truncatedByScoreGap: false, truncatedByLimit: true);

        var atCapLine = ScopeArgs.FoldLine(atCap, limit: expanded);
        Assert.DoesNotContain("limit:'all'", atCapLine);
        Assert.Contains($"server cap {ScopeArgs.HardLimit}", atCapLine);

        // 要过 'all' 但只回了 1 条：远没到上限，说「server cap 200 reached」是假话
        var wellUnderCap = new ScopedResult<string>(
            [new ScopedEntry<string>("a", 100, null)], totalInScope: 5,
            outOfScope: [], truncatedByScoreGap: false, truncatedByLimit: true);

        var underCapLine = ScopeArgs.FoldLine(wellUnderCap, limit: expanded);
        Assert.DoesNotContain("limit:'all'", underCapLine);
        Assert.DoesNotContain("server cap", underCapLine);
    }

    // 断层收口砍掉的那部分与 limit 无关（ScopeFilter 的 effectiveLimit = Min(limit, cutoff)）。
    // 劝调用方 'all' 会让它照做一次，然后一条也多不出来。
    [Fact]
    public void FoldLine_DoesNotSuggestLimitWhenLimitCannotExpand()
    {
        var gapOnly = new ScopedResult<string>(
            [new ScopedEntry<string>("a", 100, null)], totalInScope: 5,
            outOfScope: [], truncatedByScoreGap: true, truncatedByLimit: false);

        var line = ScopeArgs.FoldLine(gapOnly);

        Assert.Contains("lower relevance", line);
        Assert.DoesNotContain("limit:'all'", line);

        // 断层收口砍掉的是低相关结果，够到它们要放宽查询而不是收窄——建议方向写反了照做也拿不到
        Assert.Contains("broaden", line);
        Assert.DoesNotContain("refine", line);
    }

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
