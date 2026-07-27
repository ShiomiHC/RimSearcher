using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// granularity 的两处「看着正常、读出来是错的」：
//   - 拼错的值被静默当成 'files'，返回是一份逐字正常的文件列表，调用方看不出自己要的
//     成员粒度压根没被识别——与已经修过的 action / sources 是同一个坑的第三个入口；
//   - 成员级差异只对 modified 文件存在，于是首次 sync 之后的第一次 diff（整片都是新增）
//     输出与 granularity='files' 一字不差，读起来像「这些文件里没有成员变化」。
[Collection("PathSecurity")]
public class SyncSourcesGranularityTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly SyncSourcesTool _tool;
    private readonly string _core;

    public SyncSourcesGranularityTests()
    {
        _core = _workspace.Dir("core-src");
        var staging = _workspace.Dir("staging");

        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "old-line\n");
        _workspace.WriteFile(Path.Combine("staging", "RimWorld", "CompShield.cs"), "new-line\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entries = new List<SourcePathEntry>
        {
            new SourcePathEntry { Name = "Core", Path = _core, AssemblyPaths = [_workspace.Dir("core-asm")] }
        };

        var service = new SourceSyncService(config, new ResolvedSources(entries, []), _workspace.Dir("cache"));
        service.History.Capture("Core", _core, staging);

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_core]);

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
    public async Task UnknownGranularity_IsAnError_NotASilentFallbackToFiles()
    {
        var result = await Run(new { action = "diff", granularity = "typo" });

        Assert.True(result.IsError);
        Assert.Contains("Unknown granularity 'typo'", result.Content);
        Assert.Contains("members", result.Content);
    }

    [Theory]
    [InlineData("files")]
    [InlineData("members")]
    [InlineData("FILES")]
    [InlineData("member")]
    public async Task AcceptedGranularities_AreNotRejected(string granularity)
    {
        var result = await Run(new { action = "diff", granularity });

        Assert.False(result.IsError);
    }

    // 首次 sync 之后的第一次 diff 必然走到这里：整片变更都是新增，成员级差异对它们不存在，
    // 于是这份返回与 granularity='files' 一字不差。不说一句的话，调用方要么以为参数没生效，
    // 要么把它读成「这些文件里没有成员变化」。
    [Fact]
    public async Task MembersGranularity_WithOnlyAddedFiles_SaysWhyNothingExpanded()
    {
        // 归档的那一版里 CompShield.cs 就是这个内容，故它不算改动；新文件才是本次唯一的变更
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "old-line\n");
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompNew.cs"), "namespace RimWorld { }\n");

        var result = await Run(new { action = "diff", granularity = "members" });

        Assert.False(result.IsError);
        Assert.Contains("CompNew.cs", result.Content);
        Assert.Contains("expands modified files only", result.Content);
    }

    // 反向保险：真的有 modified 文件时不该多出这句，它会把一份正常的成员列表说成空的
    [Fact]
    public async Task MembersGranularity_WithAModifiedFile_HasNoSuchNote()
    {
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "new-line\n");

        var result = await Run(new { action = "diff", granularity = "members" });

        Assert.False(result.IsError);
        Assert.Contains("CompShield.cs", result.Content);
        Assert.DoesNotContain("expands modified files only", result.Content);
    }

    // 只列出新增文件的那一页仍要指路：本源确实有 modified，只是不在这一页上
    [Fact]
    public async Task MembersGranularity_PageWithoutModified_PointsAtTheOnesThatHaveIt()
    {
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "CompShield.cs"), "new-line\n");
        _workspace.WriteFile(Path.Combine("core-src", "RimWorld", "AaaCompNew.cs"), "namespace RimWorld { }\n");

        // 变更按相对路径排序，AaaCompNew.cs 在前，limit:1 于是恰好只列到那条新增
        var result = await Run(new { action = "diff", granularity = "members", limit = 1 });

        Assert.Contains("AaaCompNew.cs", result.Content);
        Assert.Contains("expands modified files only", result.Content);
        Assert.Contains("page to them with offset", result.Content);
    }
}
