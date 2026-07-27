using System.Text;
using RimSearcher.Server.Tools;
using RimSearcher.Core;
using RimSearcher.Server;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

// stdout 是协议通道，此后任何顺手的 Console.WriteLine 都会污染它，故整体改指 stderr
var protocolOut = Console.Out;
Console.SetOut(Console.Error);

// Core 层的降级提示接到 Server 的日志出口上（Core 不依赖 Server，故用钩子）
SourceHistoryStore.OnDiagnostic = (message, level) => _ = ServerLogger.LogAsync(message, level);

var (appConfig, configPath, isLoaded, configError) = AppConfig.Load();
await ServerLogger.Info("Program", "Configuration source", ("path", configPath));

var resolvedSources = appConfig.ResolveSources();
var hasPaths = resolvedSources.HasAny;

if (!isLoaded)
    await ServerLogger.Error("Program", "Failed to load configuration", ("path", configPath), ("reason", configError ?? "file not found"));
else if (!hasPaths)
    await ServerLogger.Warning("Program", "No source paths defined", ("path", configPath));

// mod 根展开的结果必须可见：被丢掉的旧版本文件是「搜不到某个 def」最可能的解释，
// 而它是工具替用户做的决定，不写日志就只能靠猜
if (appConfig.Sources.Any(definition => definition.Mods.Count > 0))
{
    await ServerLogger.Info("Program", "Mod folders resolved",
        ("gameVersion", resolvedSources.GameVersion ?? "unknown"),
        ("xmlDirs", resolvedSources.Xml.Count),
        ("shadowedFiles", resolvedSources.Shadowed.Count));
}

foreach (var note in resolvedSources.Notes)
{
    await ServerLogger.Warning("Program", "Mod layout note", ("detail", note));
}

var cacheDirectory = IndexCacheService.GetDefaultCacheDirectory();
var cacheDirectoryUsable = IndexCacheService.EnsureCacheDirectory(cacheDirectory, out var cacheInitError);
await ServerLogger.Info("Program", "Index cache directory", ("path", cacheDirectory));

if (!cacheDirectoryUsable)
    await ServerLogger.Warning("Program", "Index cache disabled", ("path", cacheDirectory), ("reason", cacheInitError ?? "unknown"));

PathSecurity.Initialize(resolvedSources.AllPaths, enabled: !appConfig.SkipPathSecurity);

var scopeCatalog = ScopeCatalog.Build(resolvedSources.AllSources, appConfig.ScopeGroups, appConfig.DefaultScope);
if (scopeCatalog.HasSources)
{
    await ServerLogger.Info("Program", "Scope catalog ready",
        ("sources", scopeCatalog.Sources.Count),
        ("groups", scopeCatalog.GroupNames.Count),
        ("default", string.IsNullOrWhiteSpace(appConfig.DefaultScope) ? ScopeCatalog.EverythingKeyword : appConfig.DefaultScope));
}

// 席位竞选必须先于建索引：挂上已有宿主的进程不该再花 4 秒和 1 GB 建第二份
var hostFingerprint = IndexFingerprints.ForHost(appConfig, resolvedSources);
var election = await HostElection.ElectAsync(appConfig, hasPaths, hostFingerprint, protocolOut);
if (election.ShouldExitImmediately) return;

var indexer = new SourceIndexer();
var defIndexer = new DefIndexer();
var localization = new LocalizationIndex();

var prepared = await SourceLayout.PrepareAsync(resolvedSources);

// 语言解析放在竞选之后：ForHost 只需要语言名（它自己算），而挑出实际语言包要读磁盘
var language = appConfig.ResolveLanguage();
var localizationSources = LocalizationLayout.Resolve(resolvedSources, language);

if (language != null)
{
    await ServerLogger.Info("Startup", "Localization resolved",
        ("language", language),
        ("langDirs", resolvedSources.Languages.Count),
        ("packs", localizationSources.Count));
}

// 缓存指纹放在竞选之后算：它要枚举几万条元数据（约 100~300ms），
// 而挂上宿主就直接退出的进程根本用不到这份缓存
await IndexBootstrapper.PopulateAsync(
    indexer,
    defIndexer,
    localization,
    prepared,
    localizationSources,
    appConfig.LocalizationDescription,
    cacheDirectory,
    cacheDirectoryUsable,
    IndexFingerprints.ForCache(resolvedSources, appConfig.VerifySourceFreshness, localizationSources));

var syncService = new SourceSyncService(appConfig, resolvedSources, cacheDirectory);
var indexRebuilder = new IndexRebuilder(
    indexer, defIndexer, localization, resolvedSources, localizationSources, appConfig.LocalizationDescription);

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
    new LocateTool(indexer, defIndexer, scopeCatalog, localization),
    new InspectTool(indexer, defIndexer, scopeCatalog, localization),
    new TraceTool(indexer, scopeCatalog),
    new ReadCodeTool(indexer, scopeCatalog),
    new SearchRegexTool(indexer, scopeCatalog),
    new SyncSourcesTool(syncService, indexRebuilder)
};

foreach (var tool in tools) server.RegisterTool(tool);

if (election.Slot != null)
{
    HostElection.StartServing(hostFingerprint, tools);
}
else if (!HostElection.IsSharingPossible(appConfig, hasPaths))
{
    // 竞选路径没走到时 watchdog 还没起过。走到了但没抢到席位的，ElectAsync 里已经起过了。
    ProcessGuard.Start(appConfig.IdleTimeoutMinutes);
}

if (isLoaded && hasPaths)
    await ServerLogger.Info("Program", "RimSearcher MCP server started", ("role", election.Role.ToString()));

if (appConfig.CheckUpdates)
    _ = Task.Run(UpdateChecker.CheckAsync);

await server.RunAsync();

if (election.Slot != null) await HostElection.DrainAsync(election.Slot);
