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

    // limit 约束的是预览行数，故观测点就是预览行本身。
    // 曾改成读表头的数字，但表头在截断时说的是「真实命中总数」——配额一满就不再打开新文件，
    // 那个数只反映恰好扫到了哪些文件，随线程调度浮动，拿它断言 limit 会随机失败。
    private static int PreviewLines(string content)
        => Regex.Matches(content, @"(?m)^  L\d+: ").Count;

    private static async Task<string> Run(TraceTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // 配额用尽后剩下的文件从 Parallel 委托头部直接 return，不走 finally 里的计数：
    // 进度于是永远停在半路（实测 limit:5 时停在 1.3%），客户端的进度条挂在原地不动。
    [Fact]
    public async Task Usages_ReportsFullProgress_EvenWhenTheQuotaCutsTheScanShort()
    {
        var tool = BuildTool(200);
        var reported = new List<double>();

        using var args = JsonDocument.Parse("""{"symbol":"ZzTracedSymbol","mode":"usages","limit":5}""");
        var result = await tool.ExecuteAsync(
            args.RootElement, CancellationToken.None, new Progress<double>(value => reported.Add(value)));

        Assert.False(result.IsError);
        // Progress<double> 是异步投递的，末值可能还在路上——等它落地再断言。
        // 上限给到 2 秒而不是 0.5 秒：投递走的是线程池，整个测试集并行跑起来时
        // 线程池被占满，末值迟到几百毫秒是常态，卡在 0.5 秒会让这条随机变红。
        for (var i = 0; i < 200 && (reported.Count == 0 || reported[^1] < 1.0); i++)
            await Task.Delay(10);

        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1]);
    }

    // 回归：曾写成 `limit == 0 ? 50 : Math.Max(limit, 50)`，显式的 limit:5 被抬到 50
    [Fact]
    public async Task Usages_HonorsAnExplicitSmallLimit()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages","limit":5}""");

        Assert.Equal(5, PreviewLines(content));
    }

    // 回归：'all' 曾同样被压在 50
    [Fact]
    public async Task Usages_AllExpandsBeyondTheOldFiftyAndStopsAtTheServerCap()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages","limit":"all"}""");

        var matches = PreviewLines(content);
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

        Assert.Equal(ScopeArgs.HardLimit, PreviewLines(content));
    }

    // 缺省仍是 50，不因硬上限抬到 200（扫盘结果一条一行，默认就给满会吃掉上下文）
    [Fact]
    public async Task Usages_DefaultsToFifty()
    {
        var tool = BuildTool(fileCount: 100);
        var content = await Run(tool, $$"""{"symbol":"{{Symbol}}","mode":"usages"}""");

        Assert.Equal(50, PreviewLines(content));
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
