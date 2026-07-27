using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：归档里读不到旧内容有两种相反的成因——「这一版新增」和「这一版没动过」。
// 反向增量只归档被改写/删除的旧文件，所以未变更的文件同样读不到归档，旧实现把它们
// 一律说成 "added in this version"。sync_sources 的主用途正是回答「更新后改了什么」，
// 于是一个从头到尾没动过的文件会被报成本次新增——事实上的反向答案。
[Collection("PathSecurity")]
public class SyncSourcesUntouchedFileTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly string _sourceDirectory;
    private readonly SyncSourcesTool _tool;

    public SyncSourcesUntouchedFileTests()
    {
        _sourceDirectory = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");

        // 旧树：两个文件。新树：其中一个改了、另一个原样、再多出一个新文件
        _workspace.WriteFile(Path.Combine("src", "Untouched.cs"), "same-line\n");
        _workspace.WriteFile(Path.Combine("src", "Modified.cs"), "old-line\n");
        _workspace.WriteFile(Path.Combine("staging", "Untouched.cs"), "same-line\n");
        _workspace.WriteFile(Path.Combine("staging", "Modified.cs"), "new-line\n");
        _workspace.WriteFile(Path.Combine("staging", "Added.cs"), "brand-new\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = _sourceDirectory,
            AssemblyPaths = [_workspace.Dir("assemblies")]
        };

        var service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));
        service.History.Capture("Core", _sourceDirectory, staging);

        // 转正：把源目录改成新树的样子
        _workspace.WriteFile(Path.Combine("src", "Modified.cs"), "new-line\n");
        _workspace.WriteFile(Path.Combine("src", "Added.cs"), "brand-new\n");

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_sourceDirectory]);

        _tool = new SyncSourcesTool(service);
    }

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private async Task<ToolResult> Diff(string file)
    {
        using var args = JsonDocument.Parse(
            JsonSerializer.Serialize(new { action = "diff", file, limit = 200 }));

        return await _tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task FileUntouchedByTheSync_IsNotReportedAsAdded()
    {
        var result = await Diff("Untouched.cs");

        Assert.False(result.IsError);
        Assert.DoesNotContain("added in this version", result.Content);
        Assert.Contains("unchanged in this version", result.Content);
    }

    [Fact]
    public async Task FileActuallyAddedByTheSync_IsStillReportedAsAdded()
    {
        var result = await Diff("Added.cs");

        Assert.False(result.IsError);
        Assert.Contains("added in this version", result.Content);
        Assert.DoesNotContain("unchanged in this version", result.Content);
    }

    // 正常路径钉住，否则上面两条可以靠「一律说 unchanged」蒙过去
    [Fact]
    public async Task ModifiedFile_StillProducesLineLevelDiff()
    {
        var result = await Diff("Modified.cs");

        Assert.False(result.IsError);
        Assert.Contains("-old-line", result.Content);
        Assert.Contains("+new-line", result.Content);
    }

    // 分隔符归一：diff 列表在 Windows 上印 '\'，调用方常回填 '/'，判定不该因此翻转
    [Fact]
    public async Task SeparatorForm_DoesNotFlipTheVerdict()
    {
        var store = new SourceHistoryStore(_workspace.Dir("cache"), 2);
        var versions = store.ListVersions("Core");
        var versionId = versions[^1].Id;

        Assert.True(store.WasPresentAt("Core", versionId, "Untouched.cs"));
        Assert.True(store.WasPresentAt("Core", versionId, "./Untouched.cs".Replace("./", string.Empty)));
        Assert.False(store.WasPresentAt("Core", versionId, "Added.cs"));
    }
}
