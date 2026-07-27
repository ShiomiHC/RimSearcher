using System.Text;
using System.Diagnostics;
using RimSearcher.Server.Tools;
using RimSearcher.Core;
using RimSearcher.Server;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var protocolOut = Console.Out;
Console.SetOut(Console.Error);

// Core 层的降级提示接到 Server 的日志出口上（Core 不依赖 Server，故用钩子）
SourceHistoryStore.OnDiagnostic = (message, level) => _ = ServerLogger.LogAsync(message, level);

var (appConfig, configPath, isLoaded) = AppConfig.Load();
await ServerLogger.Info("Program", "Configuration source", ("path", configPath));

var resolvedSources = appConfig.ResolveSources();
bool hasPaths = resolvedSources.HasAny;
var cacheDirectory = IndexCacheService.GetDefaultCacheDirectory();
var canUseCache = IndexCacheService.EnsureCacheDirectory(cacheDirectory, out var cacheInitError);
await ServerLogger.Info("Program", "Index cache directory", ("path", cacheDirectory));

if (!canUseCache)
{
    await ServerLogger.Warning("Program", "Index cache disabled", ("path", cacheDirectory), ("reason", cacheInitError ?? "unknown"));
}

if (!isLoaded)
{
    await ServerLogger.Error("Program", "Failed to load configuration", ("path", configPath), ("reason", "file missing or JSON parse error"));
}
else if (!hasPaths)
{
    await ServerLogger.Warning("Program", "No source paths defined", ("path", configPath));
}

PathSecurity.Initialize(resolvedSources.AllPaths, enabled: !appConfig.SkipPathSecurity);

var scopeCatalog = ScopeCatalog.Build(resolvedSources.AllSources, appConfig.ScopeGroups, appConfig.DefaultScope);
if (scopeCatalog.HasSources)
{
    await ServerLogger.Info("Program", "Scope catalog ready",
        ("sources", scopeCatalog.Sources.Count),
        ("groups", scopeCatalog.GroupNames.Count),
        ("default", string.IsNullOrWhiteSpace(appConfig.DefaultScope) ? ScopeCatalog.EverythingKeyword : appConfig.DefaultScope));
}

// 宿主指纹要在建索引前算出来：共享宿主按它分组（不同 config 不共用一份索引）。
//
// 它只能认路径，绝不能掺内容摘要。管道名是进程间的会合点，而宿主的名字在启动时
// 算一次就冻住了；掺进内容之后，源一变（Steam 更新、编辑器保存一下、乃至
// sync_sources 自己重写反编译产物）新进程就会算出另一个名字，找不到正在跑的宿主，
// 转头再建一份 1 GB 索引——共享机制恰好在最该生效的时候失效。
// 索引是否陈旧由另一条链路负责：SourceChangeProbe 探到变化并提示，sync_sources 原地重建。
var hostFingerprint = IndexCacheService.ComputeConfigFingerprint(
    resolvedSources.Csharp.Select(entry => entry.Path),
    resolvedSources.Xml.Select(entry => entry.Path),
    includeContentDigest: false);

// 代理路径必须先于建索引：连上已有宿主的进程不该再花 4 秒和 1 GB 建第二份索引
HostSlot? hostSlot = null;
if (appConfig.ShareIndexHost && hasPaths)
{
    if (!IndexHost.IsSupported)
    {
        await ServerLogger.Info("Program", "Index host sharing unavailable on this platform, running standalone");
    }
    else
    {
        ProcessGuard.Start(appConfig.IdleTimeoutMinutes);

        if (await IndexHost.TryRunAsProxyAsync(hostFingerprint, protocolOut))
        {
            await ServerLogger.Info("Program", "Proxy session ended");
            return;
        }

        hostSlot = IndexHost.TryBecomeHost(hostFingerprint);
        if (hostSlot == null)
            await ServerLogger.Info("Program", "Could not claim host slot, running standalone");
    }
}

var indexer = new SourceIndexer();
var defIndexer = new DefIndexer();

var failedPaths = new List<string>();
var existingCsharpPaths = new List<string>();
var existingXmlPaths = new List<string>();

// 可跟随源的 csharp[0] 就是反编译输出目标，首次 sync 前它本来就不存在——那是待办状态，
// 不是配置错误。不先建出来的话它会落进 failedPaths，而 failedPaths 非空会整体禁掉索引缓存，
// 于是「配好了但还没 sync」的用户每次启动都要重建一份 1 GB 索引。
foreach (var entry in resolvedSources.Followable)
{
    if (Directory.Exists(entry.Path)) continue;

    try
    {
        Directory.CreateDirectory(entry.Path);
        await ServerLogger.Info("Program", "Created decompile output directory",
            ("source", entry.Name), ("path", entry.Path));
    }
    catch (Exception ex)
    {
        // 建不出来（多见于装在 Program Files 下）就照常走下面的 failedPaths 分支
        await ServerLogger.Warning("Program", "Could not create decompile output directory",
            ("source", entry.Name), ("path", entry.Path), ("reason", ex.Message));
    }
}

foreach (var entry in resolvedSources.Csharp)
{
    if (Directory.Exists(entry.Path)) existingCsharpPaths.Add(entry.Path);
    else failedPaths.Add($"C# source '{entry.Name}': {entry.Path}");
}

foreach (var entry in resolvedSources.Xml)
{
    if (Directory.Exists(entry.Path)) existingXmlPaths.Add(entry.Path);
    else failedPaths.Add($"XML source '{entry.Name}': {entry.Path}");
}

var totalCsharpPaths = 0;
var totalXmlPaths = 0;
var cacheLoaded = false;

// 缓存键则相反，必须对内容敏感：mod 更新不改路径集合，纯路径键会让磁盘上那份
// 陈旧索引一直命中且毫无提示。这一步要枚举几万条元数据（约 100~300ms），放在
// 代理路径之后算——挂上宿主就直接退出的进程不该为一份自己不会用的缓存买单。
var cacheFingerprint = IndexCacheService.ComputeConfigFingerprint(
    resolvedSources.Csharp.Select(entry => entry.Path),
    resolvedSources.Xml.Select(entry => entry.Path),
    appConfig.VerifySourceFreshness);

if (hasPaths && existingCsharpPaths.Count + existingXmlPaths.Count > 0)
{
    if (canUseCache && failedPaths.Count == 0)
    {
        var loadResult = IndexCacheService.TryLoad(cacheDirectory, cacheFingerprint);
        if (loadResult.Success && loadResult.Snapshot != null)
        {
            indexer.ImportSnapshot(loadResult.Snapshot.Source);
            defIndexer.ImportSnapshot(loadResult.Snapshot.Def);
            indexer.FreezeIndex();
            defIndexer.FreezeIndex();
            cacheLoaded = true;
            await ServerLogger.Info("Program", "Index loaded from cache");
        }
        else
        {
            await ServerLogger.Info("Program", "Cache unavailable, rebuilding index", ("reason", loadResult.Reason));
        }
    }

    if (!cacheLoaded)
    {
        var buildStopwatch = Stopwatch.StartNew();

        foreach (var path in existingCsharpPaths)
        {
            indexer.Scan(path);
            totalCsharpPaths++;
        }

        foreach (var path in existingXmlPaths)
        {
            defIndexer.Scan(path);
            indexer.Scan(path);
            totalXmlPaths++;
        }

        if (totalCsharpPaths > 0 || totalXmlPaths > 0)
        {
            indexer.FreezeIndex();
            defIndexer.FreezeIndex();
            await ServerLogger.Info("Program", "Index build completed",
                ("csPaths", totalCsharpPaths),
                ("xmlPaths", totalXmlPaths),
                ("durationMs", buildStopwatch.ElapsedMilliseconds));

            if (canUseCache && failedPaths.Count == 0)
            {
                var snapshot = new IndexCacheSnapshot
                {
                    Source = indexer.ExportSnapshot(),
                    Def = defIndexer.ExportSnapshot()
                };

                var indexedCsharpFileCount = snapshot.Source.ProcessedFiles.Count(path =>
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                var indexedXmlFileCount = snapshot.Def.ProcessedFiles.Length;

                var saveResult = IndexCacheService.Save(
                    cacheDirectory,
                    cacheFingerprint,
                    snapshot,
                    buildStopwatch.Elapsed,
                    indexedCsharpFileCount,
                    indexedXmlFileCount);

                if (saveResult.Success)
                {
                    await ServerLogger.Info("Program", "Index cache saved", ("path", cacheDirectory));
                }
                else
                {
                    await ServerLogger.Warning("Program", "Failed to save index cache", ("reason", saveResult.Reason));
                }
            }
        }
    }
}

if (failedPaths.Count > 0)
{
    await ServerLogger.Warning("Program", "Some configured paths are unavailable", ("count", failedPaths.Count), ("paths", string.Join("; ", failedPaths)));
}

var syncService = new SourceSyncService(appConfig, resolvedSources, cacheDirectory);
var indexRebuilder = new IndexRebuilder(indexer, defIndexer, resolvedSources);

// 必须早于任何 RimSearcher 实例化：会话的通知器是在字段初始化时向 SourceChangeProbe 要的，
// 那时若还没 Configure，拿到的就是 null，本会话此后再也不会提示。
if (appConfig.CheckSourceUpdates && syncService.FollowableSources.Count > 0)
{
    SourceChangeProbe.Configure(syncService, resolvedSources, cacheDirectory, indexer);
    _ = Task.Run(SourceChangeProbe.DetectAsync);
}

var server = new RimSearcher.Server.RimSearcher(protocolOut);

// tool 实例无会话状态（只持索引引用），故宿主的各管道会话直接共享同一批
var tools = new ITool[]
{
    new ListDirectoryTool(),
    new LocateTool(indexer, defIndexer, scopeCatalog),
    new InspectTool(indexer, defIndexer, scopeCatalog),
    new TraceTool(indexer, scopeCatalog),
    new ReadCodeTool(indexer, scopeCatalog),
    new SearchRegexTool(indexer, scopeCatalog),
    new SyncSourcesTool(syncService, indexRebuilder)
};

foreach (var tool in tools) server.RegisterTool(tool);

if (hostSlot != null)
{
    // 宿主寿命与第一个 client 解绑：只要还有别的连接就不退
    ProcessGuard.ShouldStayAlive = () => IndexHost.ShouldStayAliveForConnections(TimeSpan.FromSeconds(60));
    // 必须与 TryBecomeHost 用同一个指纹：在一个名字上占席位、却在另一个名字上开管道，
    // 等于谁也连不上，且席位还被占着
    IndexHost.StartAcceptLoop(hostFingerprint, tools);
}
else if (!appConfig.ShareIndexHost || !IndexHost.IsSupported || !hasPaths)
{
    // 共享路径没走到时，watchdog 仍须启动
    ProcessGuard.Start(appConfig.IdleTimeoutMinutes);
}

if (isLoaded && hasPaths)
{
    await ServerLogger.Info("Program", "RimSearcher MCP server started",
        ("role", hostSlot != null ? "host" : "standalone"));
}

if (appConfig.CheckUpdates)
{
    _ = Task.Run(UpdateChecker.CheckAsync);
}

await server.RunAsync();

// 本地 stdio 结束（自己的 client 走了）但仍有管道连接时，宿主继续服务到最后一个断开
if (hostSlot != null)
{
    while (IndexHost.ShouldStayAliveForConnections(TimeSpan.FromSeconds(60)))
        await Task.Delay(TimeSpan.FromSeconds(15));

    await ServerLogger.Info("Program", "Index host shutting down");
    hostSlot.Dispose();
}
