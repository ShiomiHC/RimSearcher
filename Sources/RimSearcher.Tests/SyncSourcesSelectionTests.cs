using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 'sources' 里的名字原先没人核对：check 干脆忽略这个参数报全量，diff 走到「一个源都没
// 轮到」那支，回的是「还没有历史，先跑 action='sync'」——而 action='sync' 是全服务器
// 唯一会写盘的操作，照着这句提示做就是一次几分钟的重反编译，起因只是一个拼错的名字。
[Collection("PathSecurity")]
public class SyncSourcesSelectionTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly SyncSourcesTool _tool;

    public SyncSourcesSelectionTests()
    {
        var core = _workspace.Dir("core-src");
        var mod = _workspace.Dir("mod-src");
        var staging = _workspace.Dir("staging");

        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "old-line\n");
        _workspace.WriteFile(Path.Combine("staging", "RimWorld", "CompShield.cs"), "new-line\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entries = new List<SourcePathEntry>
        {
            new SourcePathEntry { Name = "Core", Path = core, AssemblyPaths = [_workspace.Dir("core-asm")] },
            new SourcePathEntry { Name = "Milira", Path = mod, AssemblyPaths = [_workspace.Dir("mod-asm")] }
        };

        var service = new SourceSyncService(config, new ResolvedSources(entries, []), _workspace.Dir("cache"));
        service.History.Capture("Core", core, staging);
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "new-line\n");

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([core, mod]);

        _tool = new SyncSourcesTool(service);
    }

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private async Task<ToolResult> Run(object arguments)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return await _tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task Check_HonoursSourcesFilter()
    {
        var result = await Run(new { action = "check", sources = "Core" });

        Assert.False(result.IsError);
        Assert.Contains("Core:", result.Content);
        Assert.DoesNotContain("Milira:", result.Content);
    }

    [Fact]
    public async Task Check_WithoutFilter_StillCoversEverySource()
    {
        var result = await Run(new { action = "check" });

        Assert.Contains("Core:", result.Content);
        Assert.Contains("Milira:", result.Content);
    }

    [Fact]
    public async Task UnknownSourceName_IsAnError_NotASilentFullRun()
    {
        var result = await Run(new { action = "check", sources = "nosuchsource" });

        Assert.True(result.IsError);
        Assert.Contains("'nosuchsource'", result.Content);
        // 可用名字要一并给出，否则调用方只能猜
        Assert.Contains("Core", result.Content);
        Assert.Contains("Milira", result.Content);
    }

    // 最有害的一条：拼错名字换来一句「先跑 sync」，而 sync 会写盘
    [Fact]
    public async Task Diff_WithUnknownSource_DoesNotSuggestRunningSync()
    {
        var result = await Run(new { action = "diff", sources = "nosuchsource" });

        Assert.True(result.IsError);
        Assert.DoesNotContain("action='sync'", result.Content);
        Assert.Contains("nosuchsource", result.Content);
    }

    [Fact]
    public async Task PartiallyUnknownSources_RunTheKnownOnesAndSayWhatWasIgnored()
    {
        var result = await Run(new { action = "check", sources = "Core,nosuchsource" });

        Assert.False(result.IsError);
        Assert.Contains("Core:", result.Content);
        Assert.DoesNotContain("Milira:", result.Content);
        Assert.Contains("Ignored 'nosuchsource'", result.Content);
    }

    // 过滤生效之后「全部可跟随的源都是最新的」就成了拿 1/2 抽样下的全称断言：
    // 没被扫到的那个源变了也不会有人发现，而调用方读到这句就不会再查了。
    [Fact]
    public async Task Check_WithFilter_DoesNotClaimEverySourceIsUpToDate()
    {
        var result = await Run(new { action = "check", sources = "Milira" });

        Assert.False(result.IsError);
        Assert.DoesNotContain("All followable sources are up to date", result.Content);
        Assert.Contains("not checked", result.Content);
    }

    [Fact]
    public async Task Check_WithoutFilter_KeepsTheGlobalWording()
    {
        var result = await Run(new { action = "check" });

        Assert.Contains("All followable sources are up to date", result.Content);
    }

    // 'sources' 语义上就是列表，客户端把它序列化成数组是很自然的写法。单值位的
    // 数组取首元素规则用在这里，会静默地只查一半并且一句都不说。
    [Fact]
    public async Task Check_AcceptsSourcesAsAnArray_WithoutDroppingElements()
    {
        var result = await Run(new { action = "check", sources = new[] { "Core", "Milira" } });

        Assert.False(result.IsError);
        Assert.Contains("Core:", result.Content);
        Assert.Contains("Milira:", result.Content);
    }

    // sync_sources 按源名工作，没有 scope 概念，而 ServerInstructions 在教别的工具用
    // scope:'all'。收作别名的话，scope:'all' 会被当成一个不存在的源名而硬报错。
    [Fact]
    public async Task ScopeIsNotAnAliasForSources()
    {
        var result = await Run(new { action = "check", scope = "all" });

        Assert.False(result.IsError);
        Assert.Contains("Core:", result.Content);
        Assert.Contains("Milira:", result.Content);
    }

    [Fact]
    public async Task Diff_OffsetPastTheEnd_SaysSo()
    {
        var result = await Run(new { action = "diff", sources = "Core", offset = 9999, limit = 5 });

        Assert.False(result.IsError);
        // 表头照常写着「N modified」，却一条也列不出来——不说一句就会被读成「这段没有变更」
        Assert.Contains("past the end", result.Content);
        Assert.Contains("offset=0", result.Content);
    }
}
