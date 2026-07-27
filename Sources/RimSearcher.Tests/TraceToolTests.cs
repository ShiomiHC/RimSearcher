using System.Text.Json;
using System.Text.RegularExpressions;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

public class TraceToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string Symbol = "ZzTracedSymbol";

    // 每个文件最多贡献 3 条（TraceTool.MaxMatchesPerFile），故 100 个文件 ≈ 300 条潜在命中，
    // 足以压过 50 这个旧魔数、也压过 200 的硬上限。
    private TraceTool BuildTool(int fileCount)
    {
        var root = _workspace.Dir("Core");
        for (var i = 0; i < fileCount; i++)
        {
            _workspace.WriteFile(
                Path.Combine("Core", $"File{i}.cs"),
                $"// {Symbol}\n// {Symbol}\n// {Symbol}\n");
        }

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static int ReportedMatches(string content)
    {
        var match = Regex.Match(content, @"\((\d+) found");
        Assert.True(match.Success, $"unexpected header: {content.Split('\n')[0]}");
        return int.Parse(match.Groups[1].Value);
    }

    private static async Task<string> Run(TraceTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // 回归：曾写成 `limit == 0 ? 50 : Math.Max(limit, 50)`，显式的 limit:5 被抬到 50
    [Fact]
    public async Task Usages_HonorsAnExplicitSmallLimit()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages","limit":5}""");

        Assert.Equal(5, ReportedMatches(content));
    }

    // 回归：'all' 曾同样被压在 50
    [Fact]
    public async Task Usages_AllExpandsBeyondTheOldFiftyAndStopsAtTheServerCap()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages","limit":"all"}""");

        var matches = ReportedMatches(content);
        Assert.True(matches > 50, $"expected more than the old hard-coded 50, got {matches}");
        Assert.Equal(ScopeArgs.HardLimit, matches);
        Assert.Contains("server cap", content);
    }

    // schema 的 maximum 只是提示，越界请求必须由服务端夹到硬上限
    [Fact]
    public async Task Usages_ClampsRequestsAboveTheServerCap()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages","limit":100000}""");

        Assert.Equal(ScopeArgs.HardLimit, ReportedMatches(content));
    }

    // 缺省仍是 50，不因硬上限抬到 200（扫盘结果一条一行，默认就给满会吃掉上下文）
    [Fact]
    public async Task Usages_DefaultsToFifty()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages"}""");

        Assert.Equal(50, ReportedMatches(content));
    }

    [Fact]
    public async Task Inheritors_HonorsAnExplicitLimit()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Tree.cs"), """
            namespace RimWorld
            {
                public class ThingComp { }
                public class CompA : ThingComp { }
                public class CompB : ThingComp { }
                public class CompC : ThingComp { }
                public class CompD : ThingComp { }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var limited = await Run(tool, """{"symbol":"ThingComp","mode":"inheritors","limit":2}""");
        Assert.Equal(2, limited.Split('\n').Count(line => line.StartsWith("- `")));
        Assert.Contains("+2 more", limited);

        // 缺省即展开到硬上限：继承树本身就是要看全的
        var all = await Run(tool, """{"symbol":"ThingComp","mode":"inheritors"}""");
        Assert.Equal(4, all.Split('\n').Count(line => line.StartsWith("- `")));
    }

    // 接口实现者也要能通过 trace 查到（索引层的回归见 SourceIndexerInheritanceTests）
    [Fact]
    public async Task Inheritors_IncludeInterfaceImplementors()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Worker.cs"), """
            namespace RimWorld
            {
                public class BaseWorker { }
                public class Worker : BaseWorker, IExposable { }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        Assert.Contains("RimWorld.Worker", await Run(tool, """{"symbol":"IExposable","mode":"inheritors"}"""));
    }
}
