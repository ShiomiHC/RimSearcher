using System.Text;
using System.Diagnostics;
using RimSearcher.Server.Tools;
using RimSearcher.Core;
using RimSearcher.Server;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var protocolOut = Console.Out;
Console.SetOut(Console.Error);

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

// 索引指纹要在建索引前算出来：共享宿主按它分组（不同 config 不共用一份索引）
var earlyFingerprint = IndexCacheService.ComputeConfigFingerprint(
    resolvedSources.Csharp.Select(entry => entry.Path),
    resolvedSources.Xml.Select(entry => entry.Path),
    appConfig.VerifySourceFreshness);

// 代理路径必须先于建索引：连上已有宿主的进程不该再花 4 秒和 1 GB 建第二份索引
Mutex? hostMutex = null;
if (appConfig.ShareIndexHost && hasPaths)
{
    if (!IndexHost.IsSupported)
    {
        await ServerLogger.Info("Program", "Index host sharing unavailable on this platform, running standalone");
    }
    else
    {
        ProcessGuard.Start(appConfig.IdleTimeoutMinutes);

        if (await IndexHost.TryRunAsProxyAsync(earlyFingerprint, protocolOut))
        {
            await ServerLogger.Info("Program", "Proxy session ended");
            return;
        }

        hostMutex = IndexHost.TryBecomeHost(earlyFingerprint);
        if (hostMutex == null)
            await ServerLogger.Info("Program", "Could not claim host slot, running standalone");
    }
}

var indexer = new SourceIndexer();
var defIndexer = new DefIndexer();

var failedPaths = new List<string>();
var existingCsharpPaths = new List<string>();
var existingXmlPaths = new List<string>();

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
var configFingerprint = earlyFingerprint;

if (hasPaths && existingCsharpPaths.Count + existingXmlPaths.Count > 0)
{
    if (canUseCache && failedPaths.Count == 0)
    {
        var loadResult = IndexCacheService.TryLoad(cacheDirectory, configFingerprint);
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
                    configFingerprint,
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

// 必须早于任何 RimSearcher 实例化：会话的通知器是在字段初始化时向 SourceWatcher 要的，
// 那时若还没 Configure，拿到的就是 null，本会话此后再也不会提示。
if (appConfig.CheckSourceUpdates && syncService.FollowableSources.Count > 0)
{
    SourceWatcher.Configure(syncService, resolvedSources, cacheDirectory, indexer);
    _ = Task.Run(SourceWatcher.DetectAsync);
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

if (hostMutex != null)
{
    // 宿主寿命与第一个 client 解绑：只要还有别的连接就不退
    ProcessGuard.ShouldStayAlive = () => IndexHost.ShouldStayAliveForConnections(TimeSpan.FromSeconds(60));
    IndexHost.StartAcceptLoop(configFingerprint, tools);
}
else if (!appConfig.ShareIndexHost || !IndexHost.IsSupported || !hasPaths)
{
    // 共享路径没走到时，watchdog 仍须启动
    ProcessGuard.Start(appConfig.IdleTimeoutMinutes);
}

if (isLoaded && hasPaths)
{
    await ServerLogger.Info("Program", "RimSearcher MCP server started",
        ("role", hostMutex != null ? "host" : "standalone"));
}

if (appConfig.CheckUpdates)
{
    _ = Task.Run(UpdateChecker.CheckAsync);
}

await server.RunAsync();

// 本地 stdio 结束（自己的 client 走了）但仍有管道连接时，宿主继续服务到最后一个断开
if (hostMutex != null)
{
    while (IndexHost.ShouldStayAliveForConnections(TimeSpan.FromSeconds(60)))
        await Task.Delay(TimeSpan.FromSeconds(15));

    await ServerLogger.Info("Program", "Index host shutting down");
    hostMutex.ReleaseMutex();
    hostMutex.Dispose();
}
