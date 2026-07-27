using RimSearcher.Server;

namespace RimSearcher.Tests;

public class IndexFingerprintsTests
{
    private static ResolvedSources SourcesAt(params string[] csharpRoots)
        => new(csharpRoots.Select(path => new SourcePathEntry { Name = "s", Path = path }).ToList(), []);

    // #5 的回归，钉在做决定的那一层：宿主指纹必须对内容免疫，否则源一变新进程就
    // 算出另一个管道名，连不上正在跑的宿主，转头自建第二份 1 GB 索引。
    [Fact]
    public void ForHost_IgnoresContentChanges()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var sources = SourcesAt(root);
        var before = IndexFingerprints.ForHost(sources);

        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");
        workspace.WriteFile("src/New.cs", "public class New { }");

        Assert.Equal(before, IndexFingerprints.ForHost(sources));
    }

    [Fact]
    public void ForCache_FollowsContentChanges_WhenFreshnessVerificationIsOn()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var sources = SourcesAt(root);
        var before = IndexFingerprints.ForCache(sources, verifySourceFreshness: true);

        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");

        Assert.NotEqual(before, IndexFingerprints.ForCache(sources, verifySourceFreshness: true));
    }

    // 关掉 freshness 校验就是用户明确接受「陈旧缓存可能一直命中」，换启动少几百毫秒
    [Fact]
    public void ForCache_IgnoresContentChanges_WhenFreshnessVerificationIsOff()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var sources = SourcesAt(root);
        var before = IndexFingerprints.ForCache(sources, verifySourceFreshness: false);

        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");

        Assert.Equal(before, IndexFingerprints.ForCache(sources, verifySourceFreshness: false));
    }

    [Fact]
    public void ForHost_StillSeparatesDifferentPathSets()
    {
        using var workspace = new TempWorkspace();
        var a = workspace.Dir("a");
        var b = workspace.Dir("b");

        Assert.NotEqual(IndexFingerprints.ForHost(SourcesAt(a)), IndexFingerprints.ForHost(SourcesAt(b)));
        Assert.Equal(IndexFingerprints.ForHost(SourcesAt(a)), IndexFingerprints.ForHost(SourcesAt(a)));
    }
}

public class SourceLayoutTests
{
    private static ResolvedSources Build(
        IEnumerable<(string Name, string Path, string[] Assemblies)> csharp,
        IEnumerable<(string Name, string Path)>? xml = null)
        => new(
            csharp.Select(e => new SourcePathEntry { Name = e.Name, Path = e.Path, AssemblyPaths = e.Assemblies }).ToList(),
            (xml ?? []).Select(e => new SourcePathEntry { Name = e.Name, Path = e.Path }).ToList());

    [Fact]
    public async Task Prepare_SplitsExistingFromMissing()
    {
        using var workspace = new TempWorkspace();
        var present = workspace.Dir("present");
        var absent = Path.Combine(workspace.Root, "absent");
        var xml = workspace.Dir("defs");

        var prepared = await SourceLayout.PrepareAsync(Build(
            [("A", present, []), ("B", absent, [])],
            [("C", xml)]));

        Assert.Equal([present], prepared.ExistingCsharp);
        Assert.Equal([xml], prepared.ExistingXml);
        Assert.Contains("B", Assert.Single(prepared.Missing));
        Assert.False(prepared.CacheIsTrustworthy);
        Assert.True(prepared.HasAnyExisting);
    }

    // 可跟随源的输出目录在首次 sync 前本来就不存在，那是待办状态不是配置错误。
    // 不建出来的话它落进 Missing，缓存被整体禁掉，用户每次启动都要重建 1 GB 索引。
    [Fact]
    public async Task Prepare_CreatesMissingDecompileTargets_KeepingTheCacheUsable()
    {
        using var workspace = new TempWorkspace();
        var target = Path.Combine(workspace.Root, "decompiled");
        var assemblies = workspace.Dir("dlls");

        var prepared = await SourceLayout.PrepareAsync(Build([("Core", target, [assemblies])]));

        Assert.True(Directory.Exists(target));
        Assert.Equal([target], prepared.ExistingCsharp);
        Assert.Empty(prepared.Missing);
        Assert.True(prepared.CacheIsTrustworthy);
    }

    // 没配 assemblyPath 的源是手工副本，缺了就是缺了，不该替用户凭空建一个空目录
    [Fact]
    public async Task Prepare_DoesNotCreateDirectoriesForNonFollowableSources()
    {
        using var workspace = new TempWorkspace();
        var target = Path.Combine(workspace.Root, "manual");

        var prepared = await SourceLayout.PrepareAsync(Build([("Manual", target, [])]));

        Assert.False(Directory.Exists(target));
        Assert.Single(prepared.Missing);
    }

    [Fact]
    public async Task Prepare_WithNothingConfigured_ReportsNothingToIndex()
    {
        var prepared = await SourceLayout.PrepareAsync(new ResolvedSources([], []));

        Assert.False(prepared.HasAnyExisting);
        Assert.True(prepared.CacheIsTrustworthy);
    }
}

public class HostElectionTests
{
    private static AppConfig Config(bool shareIndexHost) => new() { ShareIndexHost = shareIndexHost };

    [Fact]
    public void SharingRequires_TheFlag_PathsAndPlatformSupport()
    {
        Assert.False(HostElection.IsSharingPossible(Config(shareIndexHost: false), hasPaths: true));
        Assert.False(HostElection.IsSharingPossible(Config(shareIndexHost: true), hasPaths: false));

        // 三个条件都满足时结论就是平台是否支持——不能各处再各写一遍这个判断
        Assert.Equal(IndexHost.IsSupported, HostElection.IsSharingPossible(Config(shareIndexHost: true), hasPaths: true));
    }

    [Fact]
    public async Task WithSharingDisabled_ElectsStandaloneWithoutTouchingAnySlot()
    {
        var result = await HostElection.ElectAsync(
            Config(shareIndexHost: false), hasPaths: true, "fingerprint", TextWriter.Null);

        Assert.Equal(ServerRole.Standalone, result.Role);
        Assert.Null(result.Slot);
        Assert.False(result.ShouldExitImmediately);
    }

    [Fact]
    public async Task WithNoSources_ElectsStandalone()
    {
        var result = await HostElection.ElectAsync(
            Config(shareIndexHost: true), hasPaths: false, "fingerprint", TextWriter.Null);

        Assert.Equal(ServerRole.Standalone, result.Role);
        Assert.Null(result.Slot);
    }
}
