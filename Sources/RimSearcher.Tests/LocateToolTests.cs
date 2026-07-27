using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

public class LocateToolTests
{
    private static SourceIndexer EmptySourceIndexer()
    {
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        return indexer;
    }

    private static DefIndexer EmptyDefIndexer()
    {
        var indexer = new DefIndexer();
        indexer.FreezeIndex();
        return indexer;
    }

    // 回归：曾用 `sb.Length > rawQuery.Length + 10 + scope.Expression.Length` 推断有没有命中。
    // 窄 scope 的表头是 `## 'q' _(scope: e)_` + 换行 = q + e + 19，恒大于阈值 q + e + 10，
    // 于是零命中被判成有结果——「换个 scope 再试」的提示因此永远发不出来。
    [Fact]
    public async Task NarrowScope_WithNoMatches_ReportsNoResults()
    {
        var catalog = ScopeCatalog.Build(
            [("vanilla", @"C:\game\Core"), ("milira", @"C:\mods\Milira")], null, null);

        var tool = new LocateTool(EmptySourceIndexer(), EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"ZzNoSuchSymbolZz","scope":"vanilla"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        // 搜索没命中不是工具执行失败，isError 留给「调用方给错了参数」——
        // trace 与 search_regex 的零命中一直是 false，locate 曾是 true。
        Assert.False(result.IsError);
        Assert.Contains("No results", result.Content);
        Assert.Contains("vanilla", result.Content);
    }

    // 全域路径下旧实现恰好判对，这里钉住它别被改回去
    [Fact]
    public async Task EverythingScope_WithNoMatches_ReportsNoResults()
    {
        var catalog = ScopeCatalog.Build([("vanilla", @"C:\game\Core")], null, null);
        var tool = new LocateTool(EmptySourceIndexer(), EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"ZzNoSuchSymbolZz"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("No results", result.Content);
    }

    // 反向保险：命中时不得被误判成空，否则「修好空判定」会变成「永远说没结果」
    [Fact]
    public async Task NarrowScope_WithMatch_ReportsTheMatch()
    {
        using var workspace = new TempWorkspace();
        var coreDirectory = workspace.Dir("Core");
        workspace.WriteFile(
            Path.Combine("Core", "CompTestShield.cs"),
            "namespace RimWorld { public class CompTestShield { public void CompTick() { } } }");

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.Scan(coreDirectory);
        sourceIndexer.FreezeIndex();

        var catalog = ScopeCatalog.Build(
            [("vanilla", coreDirectory), ("milira", @"C:\mods\Milira")], null, null);

        var tool = new LocateTool(sourceIndexer, EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"CompTestShield","scope":"vanilla"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("CompTestShield", result.Content);
        Assert.DoesNotContain("No results", result.Content);
    }

    // query 里写 'scope:xxx' 应被当作 scope 参数吸收，而不是变成一个搜不到东西的关键词
    [Fact]
    public async Task ScopePrefixInQuery_IsTreatedAsScope()
    {
        var catalog = ScopeCatalog.Build(
            [("vanilla", @"C:\game\Core"), ("milira", @"C:\mods\Milira")], null, null);

        var tool = new LocateTool(EmptySourceIndexer(), EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"scope:milira ZzNoSuchSymbolZz"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("milira", result.Content);
    }

    // 缺必填参数必须是可自我纠正的工具错误，不能冒泡成 -32603
    [Fact]
    public async Task MissingQuery_ThrowsToolArgumentException()
    {
        var catalog = ScopeCatalog.Build([("vanilla", @"C:\game\Core")], null, null);
        var tool = new LocateTool(EmptySourceIndexer(), EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"scope":"vanilla"}""");

        var exception = await Assert.ThrowsAsync<ToolArgumentException>(
            () => tool.ExecuteAsync(args.RootElement, CancellationToken.None));

        Assert.Contains("query", exception.Message);
        Assert.Contains("scope", exception.Message);
    }
}
