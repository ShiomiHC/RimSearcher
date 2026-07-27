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

    // 回归：折叠行承诺的条数必须等于 limit:'all' 真能拿到的增量。
    // 索引里一个类型有短名与全名两条记录，同一查询两条都命中；折叠曾经发生在 limit 截断之后，
    // 于是「+N more」把重复的那一半也算进去——实测 locate 'shield' 报 +83，展开后总共只有 51 条。
    [Fact]
    public async Task TypeFoldCount_MatchesWhatExpandingActuallyReturns()
    {
        using var workspace = new TempWorkspace();
        var coreDirectory = workspace.Dir("Core");

        for (var i = 1; i <= 12; i++)
        {
            workspace.WriteFile(
                Path.Combine("Core", $"CompFold{i:00}.cs"),
                $"namespace RimWorld {{ public class CompFold{i:00} {{ }} }}");
        }

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.Scan(coreDirectory);
        sourceIndexer.FreezeIndex();

        var catalog = ScopeCatalog.Build([("vanilla", coreDirectory)], null, null);
        var tool = new LocateTool(sourceIndexer, EmptyDefIndexer(), catalog);

        using var capped = JsonDocument.Parse("""{"query":"type:CompFold","limit":4}""");
        var cappedResult = await tool.ExecuteAsync(capped.RootElement, CancellationToken.None);

        using var expanded = JsonDocument.Parse("""{"query":"type:CompFold","limit":"all"}""");
        var expandedResult = await tool.ExecuteAsync(expanded.RootElement, CancellationToken.None);

        var shownWhenCapped = CountTypeLines(cappedResult.Content);
        var shownWhenExpanded = CountTypeLines(expandedResult.Content);
        var promised = FoldCount(cappedResult.Content);

        Assert.Equal(shownWhenExpanded, shownWhenCapped + promised);
    }

    // 回归：Members 段的折叠行曾只数「取回的那批里还剩几条」，而取回本身已被 limit.Scale(3)
    // 砍过一道——method:CompTick 因此报 +25，limit:'all' 实际能给出 186 条。
    // 它还漏了「怎么才能拿到更多」，调用方连能展开都不知道。
    [Fact]
    public async Task MemberFoldLine_CountsEverythingHiddenAndSaysHowToExpand()
    {
        using var workspace = new TempWorkspace();
        var coreDirectory = workspace.Dir("Core");

        for (var i = 1; i <= 60; i++)
        {
            workspace.WriteFile(
                Path.Combine("Core", $"Holder{i:00}.cs"),
                $"namespace RimWorld {{ public class Holder{i:00} {{ public void FoldTick() {{ }} }} }}");
        }

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.Scan(coreDirectory);
        sourceIndexer.FreezeIndex();

        var catalog = ScopeCatalog.Build([("vanilla", coreDirectory)], null, null);
        var tool = new LocateTool(sourceIndexer, EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"method:FoldTick","limit":5}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        var shown = result.Content.Split('\n').Count(line => line.Contains(".FoldTick`"));
        Assert.Equal(60, shown + FoldCount(result.Content));
        Assert.Contains("limit:'all'", result.Content);
    }

    // Property 的复数是 Properties。分组标题曾是 $"{MemberType}s"，写出 'Propertys'。
    [Fact]
    public async Task MemberGroupHeadings_UseCorrectPlurals()
    {
        using var workspace = new TempWorkspace();
        var coreDirectory = workspace.Dir("Core");
        workspace.WriteFile(
            Path.Combine("Core", "Holder.cs"),
            "namespace RimWorld { public class Holder { public int FoldValue { get; set; } } }");

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.Scan(coreDirectory);
        sourceIndexer.FreezeIndex();

        var catalog = ScopeCatalog.Build([("vanilla", coreDirectory)], null, null);
        var tool = new LocateTool(sourceIndexer, EmptyDefIndexer(), catalog);

        using var args = JsonDocument.Parse("""{"query":"FoldValue"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Properties:", result.Content);
        Assert.DoesNotContain("Propertys", result.Content);
    }

    private static int CountTypeLines(string content) =>
        content.Split('\n').Count(line => line.TrimStart().StartsWith("- `CompFold"));

    private static int FoldCount(string content)
    {
        var line = content.Split('\n').FirstOrDefault(l => l.Contains("... +"));
        if (line == null) return 0;

        var start = line.IndexOf("... +", StringComparison.Ordinal) + 5;
        var digits = new string(line[start..].TakeWhile(char.IsDigit).ToArray());
        return int.Parse(digits);
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
