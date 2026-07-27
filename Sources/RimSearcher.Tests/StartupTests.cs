using RimSearcher.Server;

namespace RimSearcher.Tests;

public class IndexFingerprintsTests
{
    private static ResolvedSources SourcesAt(params string[] csharpRoots)
        => new(csharpRoots.Select(path => new SourcePathEntry { Name = "s", Path = path }).ToList(), []);

    private static AppConfig Config() => new();

    // #5 的回归，钉在做决定的那一层：宿主指纹必须对内容免疫，否则源一变新进程就
    // 算出另一个管道名，连不上正在跑的宿主，转头自建第二份 1 GB 索引。
    [Fact]
    public void ForHost_IgnoresContentChanges()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var sources = SourcesAt(root);
        var before = IndexFingerprints.ForHost(Config(), sources);

        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");
        workspace.WriteFile("src/New.cs", "public class New { }");

        Assert.Equal(before, IndexFingerprints.ForHost(Config(), sources));
    }

    // 「一份配置两个指纹、要求正好相反」这条分界线，在同一次内容变化上一起看才看得清
    [Fact]
    public void AContentChange_MovesTheCacheKeyOnly_NeverTheHostName()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");
        workspace.WriteFile("src/Thing.cs", "public class Thing { }");

        var sources = SourcesAt(root);
        var hostBefore = IndexFingerprints.ForHost(Config(), sources);
        var cacheBefore = IndexFingerprints.ForCache(sources, verifySourceFreshness: true);

        workspace.WriteFile("src/Thing.cs", "public class Thing { public int Added; }");

        Assert.Equal(hostBefore, IndexFingerprints.ForHost(Config(), sources));
        Assert.NotEqual(cacheBefore, IndexFingerprints.ForCache(sources, verifySourceFreshness: true));
    }

    // 以下一组钉的是同一件事：路径相同但配置不同的两个 client 绝不能算出同一个管道名。
    // 会合点撞上之后，后启动的那个进程整场会话都在用宿主的工具实例和宿主的配置——
    // 它自己写的那份 config.toml 静默失效，而两边都表现正常。
    private static void AssertHostsAreSeparated(AppConfig left, AppConfig right)
    {
        using var workspace = new TempWorkspace();
        var sources = SourcesAt(workspace.Dir("src"));

        Assert.NotEqual(IndexFingerprints.ForHost(left, sources), IndexFingerprints.ForHost(right, sources));
    }

    // 最狠的一条：关掉了路径校验的宿主会把「已关闭」传染给明确要求开启的 client，
    // 于是后者的 read_code 能读到自己配置本来禁止的目录。
    [Fact]
    public void ForHost_SeparatesHosts_WhenPathSecurityDiffers()
        => AssertHostsAreSeparated(new AppConfig { SkipPathSecurity = false }, new AppConfig { SkipPathSecurity = true });

    [Fact]
    public void ForHost_SeparatesHosts_WhenDefaultScopeDiffers()
        => AssertHostsAreSeparated(new AppConfig { DefaultScope = "vanilla" }, new AppConfig { DefaultScope = "mods" });

    [Fact]
    public void ForHost_SeparatesHosts_WhenScopeGroupsDiffer()
        => AssertHostsAreSeparated(
            new AppConfig { ScopeGroups = new() { ["mods"] = ["har"] } },
            new AppConfig { ScopeGroups = new() { ["mods"] = ["har", "vanilla"] } });

    // 组内成员顺序不是同义写法：ScopeCatalog 按书写顺序发 rank，也就是同分命中的排序
    [Fact]
    public void ForHost_SeparatesHosts_WhenScopeGroupMemberOrderDiffers()
        => AssertHostsAreSeparated(
            new AppConfig { ScopeGroups = new() { ["mods"] = ["har", "vanilla"] } },
            new AppConfig { ScopeGroups = new() { ["mods"] = ["vanilla", "har"] } });

    [Fact]
    public void ForHost_SeparatesHosts_WhenGameVersionDiffers()
        => AssertHostsAreSeparated(new AppConfig { GameVersion = "1.5" }, new AppConfig { GameVersion = "1.6" });

    [Fact]
    public void ForHost_SeparatesHosts_WhenHistoryDepthDiffers()
        => AssertHostsAreSeparated(new AppConfig { SourceHistoryDepth = 0 }, new AppConfig { SourceHistoryDepth = 3 });

    [Fact]
    public void ForHost_SeparatesHosts_WhenSourceUpdateChecksDiffer()
        => AssertHostsAreSeparated(
            new AppConfig { CheckSourceUpdates = true }, new AppConfig { CheckSourceUpdates = false });

    // 源名就是 scope 表达式里能写的词，也是 sync_sources 的选择单位
    [Fact]
    public void ForHost_SeparatesHosts_WhenTheSamePathIsNamedDifferently()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");

        ResolvedSources Named(string name) =>
            new([new SourcePathEntry { Name = name, Path = root }], []);

        Assert.NotEqual(
            IndexFingerprints.ForHost(Config(), Named("vanilla")),
            IndexFingerprints.ForHost(Config(), Named("har")));
    }

    // 程序集路径决定 sync_sources 从哪些 dll 反编译进这个源码目录：共用宿主的话，
    // 一次 sync 就用别人的 dll 改写了这份源码
    [Fact]
    public void ForHost_SeparatesHosts_WhenFollowedAssembliesDiffer()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");

        ResolvedSources Following(params string[] assemblies) =>
            new([new SourcePathEntry { Name = "s", Path = root, AssemblyPaths = assemblies }], []);

        Assert.NotEqual(
            IndexFingerprints.ForHost(Config(), Following(workspace.Dir("dlls-a"))),
            IndexFingerprints.ForHost(Config(), Following(workspace.Dir("dlls-b"))));

        Assert.NotEqual(
            IndexFingerprints.ForHost(Config(), Following()),
            IndexFingerprints.ForHost(Config(), Following(workspace.Dir("dlls-a"))));
    }

    // 反过来的失效同样静默且更贵：同义写法算出两个管道名，两个本该共用的进程各建一份 1 GB
    [Fact]
    public void ForHost_IgnoresSynonymousConfigSpellings()
    {
        using var workspace = new TempWorkspace();
        var sources = SourcesAt(workspace.Dir("src"));

        var canonical = new AppConfig
        {
            DefaultScope = "vanilla,-har",
            ScopeGroups = new() { ["mods"] = ["har"], ["engine"] = ["vanilla"] },
            GameVersion = "1.6"
        };

        var sameThingWrittenDifferently = new AppConfig
        {
            // 分隔符 , ; | 等价，排除前缀 - 与 ! 等价，token 大小写与空白无意义
            DefaultScope = "  Vanilla ; ! HAR ",
            // 组的书写顺序只影响工具说明里罗列组名的次序，不影响任何一次解析
            ScopeGroups = new() { ["Engine"] = ["Vanilla"], ["MODS"] = [" har "] },
            GameVersion = " 1.6 "
        };

        Assert.Equal(
            IndexFingerprints.ForHost(canonical, sources),
            IndexFingerprints.ForHost(sameThingWrittenDifferently, sources));
    }

    // 「没写」和「写了空值」是同一个意思：都落到全域 scope、都让 GameVersion 退回自动探测
    [Fact]
    public void ForHost_TreatsEmptyValuesAsUnset()
    {
        using var workspace = new TempWorkspace();
        var sources = SourcesAt(workspace.Dir("src"));

        var unset = new AppConfig();
        var explicitlyEmpty = new AppConfig
        {
            DefaultScope = "   ",
            GameVersion = "",
            ScopeGroups = new() { ["empty"] = [] }
        };

        Assert.Equal(
            IndexFingerprints.ForHost(unset, sources),
            IndexFingerprints.ForHost(explicitlyEmpty, sources));
    }

    // 同一个目录的三种写法（大小写、/ 与 \、尾分隔符）在 Windows 上是同一份源码。
    // 不收敛的症状是两个 client 明明指着同一批目录却各建一份索引。
    [Fact]
    public void ForHost_IgnoresWindowsPathSpellings()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TempWorkspace();
        var root = workspace.Dir("src");

        var spelled = root.Replace('\\', '/').ToUpperInvariant() + "/";

        Assert.Equal(
            IndexFingerprints.ForHost(Config(), SourcesAt(root)),
            IndexFingerprints.ForHost(Config(), SourcesAt(spelled)));
    }

    // 只影响缓存键与启动耗时的开关不该劈开会合点：宿主给出的回答与它无关，
    // 为它多建一份 1 GB 索引是纯亏。
    [Fact]
    public void ForHost_IgnoresCacheOnlyKnobs()
    {
        using var workspace = new TempWorkspace();
        var sources = SourcesAt(workspace.Dir("src"));

        Assert.Equal(
            IndexFingerprints.ForHost(new AppConfig { VerifySourceFreshness = true }, sources),
            IndexFingerprints.ForHost(new AppConfig { VerifySourceFreshness = false }, sources));

        // 进程寿命是各进程自己的事：代理在挂上宿主之前就起了自己的 ProcessGuard
        Assert.Equal(
            IndexFingerprints.ForHost(new AppConfig { IdleTimeoutMinutes = 0 }, sources),
            IndexFingerprints.ForHost(new AppConfig { IdleTimeoutMinutes = 30 }, sources));
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

        Assert.NotEqual(
            IndexFingerprints.ForHost(Config(), SourcesAt(a)),
            IndexFingerprints.ForHost(Config(), SourcesAt(b)));
        Assert.NotEqual(
            IndexFingerprints.ForHost(Config(), SourcesAt(a)),
            IndexFingerprints.ForHost(Config(), SourcesAt(a, b)));
        Assert.Equal(
            IndexFingerprints.ForHost(Config(), SourcesAt(a)),
            IndexFingerprints.ForHost(Config(), SourcesAt(a)));
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

    // 席位用的是全机命名内核对象，故每个测试各用一个随机指纹，免得撞上真在跑的服务器
    private static string NewFingerprint() => $"rimsearcher-test-{Guid.NewGuid():N}";

    private Task<HostElectionResult> Elect(string fingerprint, Func<Task<bool>> attach) =>
        HostElection.ElectAsync(
            Config(shareIndexHost: true), hasPaths: true, fingerprint, TextWriter.Null, (_, _) => attach());

    // #2 的回归。两个进程同时冷启动时，双方的第一次代理探测都落在「还没有宿主」上并立即
    // 返回，随后一个抢到席位、另一个抢不到。旧实现在这里直接降级 standalone，于是在最该
    // 共享的那一刻多建了一份约 1 GB 的索引——必须再探一次赢家的管道。
    [Fact]
    public async Task LosingTheSlotRace_TriesToAttachAgainBeforeGivingUp()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();
        using var winner = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(winner);

        var attempts = 0;
        var result = await Elect(fingerprint, () =>
        {
            attempts++;
            return Task.FromResult(false);
        });

        Assert.Equal(2, attempts);
        Assert.Equal(ServerRole.Standalone, result.Role);
        Assert.Null(result.Slot);
    }

    [Fact]
    public async Task LosingTheSlotRace_AttachesOnceTheWinnersPipeIsUp()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();
        using var winner = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(winner);

        var attempts = 0;
        var result = await Elect(fingerprint, () => Task.FromResult(++attempts == 2));

        Assert.Equal(2, attempts);
        Assert.Equal(ServerRole.ProxyFinished, result.Role);
        Assert.True(result.ShouldExitImmediately);
    }

    // 没人占席位时不该多绕一圈：一次探测失败就当宿主
    [Fact]
    public async Task WithNoHostAround_BecomesHostAfterASingleAttachAttempt()
    {
        if (!IndexHost.IsSupported) return;

        var attempts = 0;
        var result = await Elect(NewFingerprint(), () =>
        {
            attempts++;
            return Task.FromResult(false);
        });

        Assert.Equal(1, attempts);
        Assert.Equal(ServerRole.Host, result.Role);
        result.Slot!.Dispose();
    }

    // 赢家在建索引途中死掉时，席位随它的句柄一起消失。此时顶上去当宿主，
    // 后来的进程才仍有可挂靠的对象——否则一屋子进程各建一份索引。
    [Fact]
    public async Task WhenTheWinnerVanishesDuringTheRetry_TakesOverAsHost()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();
        using var winner = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(winner);

        var attempts = 0;
        var result = await Elect(fingerprint, () =>
        {
            // 赢家在第二次探测期间退出。第一次探测时它还占着席位，故本进程确实是
            // 抢位失败、走到重试之后才顶上来的。
            if (++attempts == 2) winner!.Dispose();
            return Task.FromResult(false);
        });

        Assert.Equal(2, attempts);
        Assert.Equal(ServerRole.Host, result.Role);
        result.Slot!.Dispose();
    }

    // 降级必须有界，且这一条要用真实的 TryRunAsProxyAsync 走完：席位被占、管道永远不来
    // （宿主卡在建索引、或管道权限不对）时它必须自己收工。多一份索引可以忍，把 client
    // 挂在这里等不行。窗口缩到毫秒级只为让测试跑得完——按默认窗口真等两轮要 20 秒，
    // 而这里要验的是「会返回」，不是等多久。
    [Fact]
    public async Task WithASlotHeldButNoPipe_FallsBackToStandaloneInsteadOfWaitingForever()
    {
        if (!IndexHost.IsSupported) return;

        var fingerprint = NewFingerprint();
        using var winner = IndexHost.TryBecomeHost(fingerprint);
        Assert.NotNull(winner);

        var tightWindow = new ProxyRetryWindow(
            Attempts: 2, FirstConnectTimeoutMs: 50, LaterConnectTimeoutMs: 50, RetryDelayMs: 10);

        var attempts = 0;
        var result = await HostElection.ElectAsync(
            Config(shareIndexHost: true), hasPaths: true, fingerprint, TextWriter.Null,
            (candidate, output) =>
            {
                attempts++;
                return IndexHost.TryRunAsProxyAsync(candidate, output, tightWindow);
            });

        Assert.Equal(2, attempts);
        Assert.Equal(ServerRole.Standalone, result.Role);
        Assert.Null(result.Slot);
    }
}
