using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// action='diff' + file 是唯一一条「调用方给的路径直接落到 File.ReadAllText」的读路径。
// 与 PathSecurityTests 同一 collection：两边都要动 PathSecurity 的静态白名单。
[Collection("PathSecurity")]
public class SyncSourcesDiffTests : IDisposable
{
    private const string Sentinel = "SECRET-OUTSIDE-THE-SOURCE-ROOT";

    private readonly TempWorkspace _workspace = new();
    private readonly string _sourceDirectory;
    private readonly string _outsideFile;
    private readonly SyncSourcesTool _tool;

    public SyncSourcesDiffTests()
    {
        _sourceDirectory = _workspace.Dir("src");
        _outsideFile = _workspace.WriteFile(Path.Combine("outside", "secret.txt"), Sentinel);

        // 归档一版：src 里是旧内容、staging 里是新内容，Capture 后把 src 改成新内容当作转正
        var staging = _workspace.Dir("staging");
        _workspace.WriteFile(Path.Combine("src", "RimWorld", "CompShield.cs"), "old-line\n");
        _workspace.WriteFile(Path.Combine("staging", "RimWorld", "CompShield.cs"), "new-line\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = _sourceDirectory,
            AssemblyPaths = [_workspace.Dir("assemblies")]
        };

        var service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));
        service.History.Capture("Core", _sourceDirectory, staging);
        _workspace.WriteFile(Path.Combine("src", "RimWorld", "CompShield.cs"), "new-line\n");

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_sourceDirectory]);

        _tool = new SyncSourcesTool(service);
    }

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private async Task<ToolResult> Diff(string file, string? version = null)
    {
        using var args = JsonDocument.Parse(
            JsonSerializer.Serialize(new { action = "diff", file, version, limit = 200 }));

        return await _tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    // 正常路径先钉住，否则下面几条「必须拒绝」全部可以靠「一律拒绝」蒙过去
    [Fact]
    public async Task RelativePathInsideSource_StillProducesDiff()
    {
        var result = await Diff(Path.Combine("RimWorld", "CompShield.cs"));

        Assert.False(result.IsError);
        Assert.Contains("-old-line", result.Content);
        Assert.Contains("+new-line", result.Content);
    }

    // 归档目录里的相对路径用的是 '\'，但调用方（和 diff 列表的复制粘贴）常给 '/'
    [Fact]
    public async Task ForwardSlashSeparators_AreAccepted()
    {
        var result = await Diff("RimWorld/CompShield.cs");

        Assert.False(result.IsError);
        Assert.Contains("+new-line", result.Content);
    }

    // 缺陷回归：旧实现只 TrimStart 掉开头的分隔符，'..' 一路放行
    [Fact]
    public async Task ParentTraversalInFile_IsRefusedAndReadsNothing()
    {
        var result = await Diff(Path.Combine("..", "outside", "secret.txt"));

        Assert.True(result.IsError);
        Assert.DoesNotContain(Sentinel, result.Content);
        Assert.DoesNotContain("added in this version", result.Content);
    }

    // 缺陷回归：Path.Combine 遇到 rooted 的第二段会丢掉源根，绝对路径原样直通。
    // 稳定可达的后果是任意绝对路径的存在性探测（走到「added in this version」那一支）。
    [Fact]
    public async Task AbsoluteFile_IsRefusedAndReadsNothing()
    {
        var result = await Diff(_outsideFile);

        Assert.True(result.IsError);
        Assert.DoesNotContain(Sentinel, result.Content);
        Assert.DoesNotContain("added in this version", result.Content);
    }

    // 缺陷回归：versionId 也参与历史目录拼接，从不与索引核对
    [Fact]
    public async Task UnknownVersion_IsRefusedAndListsTheRealOnes()
    {
        var result = await Diff(Path.Combine("RimWorld", "CompShield.cs"), "v9999");

        Assert.True(result.IsError);
        Assert.Contains("v9999", result.Content);
        Assert.Contains("v0001", result.Content);
    }

    [Fact]
    public async Task VersionWithParentTraversal_IsRefused()
    {
        var result = await Diff(
            Path.Combine("RimWorld", "CompShield.cs"),
            Path.Combine("..", "..", "..", "outside"));

        Assert.True(result.IsError);
        Assert.DoesNotContain(Sentinel, result.Content);
    }

    // 源根之内、但白名单之外的文件也不能读：这条读路径此前完全绕过 PathSecurity
    [Fact]
    public async Task FileInsideSourceButOutsideAllowedRoots_IsRefused()
    {
        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_workspace.Dir("somewhere-else")]);

        var result = await Diff(Path.Combine("RimWorld", "CompShield.cs"));

        Assert.True(result.IsError);
        Assert.Contains("outside the allowed directories", result.Content);
        Assert.DoesNotContain("new-line", result.Content);
    }
}
