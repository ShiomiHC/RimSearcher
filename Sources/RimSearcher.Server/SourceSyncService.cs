using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server;

// 反编译产物目录的归属标记。没有它就说明目录是用户手工维护的，
// 同步流程绝不覆盖——否则一次配置笔误就会抹掉别人的源码副本。
public sealed record SyncSourceState
{
    public required string Name { get; init; }
    public required string CatalogDigest { get; init; }
    public DateTime SyncedAtUtc { get; init; }
    public Dictionary<string, string> Assemblies { get; init; } = new();
}

public sealed record SyncState
{
    public Dictionary<string, SyncSourceState> Sources { get; init; } = new();
}

public sealed record SourceChange
{
    public required string SourceName { get; init; }
    public required bool HasChanges { get; init; }
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Removed { get; init; }
    public int TotalAssemblies { get; init; }
    public string? Blocker { get; init; }

    // 本源开始同步之后整体失败并回滚了。与 Blocker 的区别是时机：Blocker 是「压根没能开始」
    // （目录不存在、输出目录不归我们管），这个是「跑起来了但没能完整落地」。
    // 两者都必须让调用方看见——失败被吞掉的后果是用户以为同步过了。
    public string? Failure { get; init; }

    public string Describe() => Blocker != null
        ? $"{SourceName}: 无法同步 — {Blocker}"
        : Failure != null
            ? $"{SourceName}: 同步失败，已整体回滚（源码目录仍是上一版）— {Failure}"
            : HasChanges
                ? $"{SourceName}: +{Added} 修改 {Modified} 移除 {Removed} (共 {TotalAssemblies} 个程序集)"
                : $"{SourceName}: 无变更 ({TotalAssemblies} 个程序集)";
}

public sealed record SyncReport
{
    public required IReadOnlyList<SourceChange> Changes { get; init; }
    public IReadOnlyList<DecompileOutcome> Outcomes { get; init; } = [];

    // 源码文件级增删改，与上面按程序集统计的 Changes 是两个粒度
    public IReadOnlyList<SourceChangeSet> FileChanges { get; init; } = [];

    // 进程内已有一次同步在跑，这次请求什么都没做。刻意不排队，理由见 SourceSyncService.SyncAsync。
    public bool AlreadyRunning { get; init; }

    // 真正完成转正（磁盘上的源码确实换了）的源名。索引重建应当以此为条件：
    // 只看「有程序集反编译成功」的话，某个源整体失败回滚后磁盘其实没变，
    // 重建就只是白清空一次索引再重扫（vanilla 实测约 4 秒，期间所有查询挂起）。
    public IReadOnlyList<string> PromotedSources { get; init; } = [];

    public bool AnyPromoted => PromotedSources.Count > 0;

    public bool AnyChanges => Changes.Any(c => c.HasChanges);

    public IReadOnlyList<SourceChange> Failures => Changes.Where(c => c.Failure != null).ToList();

    public long ElapsedMs { get; init; }

    public static SyncReport Busy() => new() { Changes = [], AlreadyRunning = true };
}

public sealed class SourceSyncService
{
    public const string OwnershipMarker = ".rimsearcher-decompiled";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ResolvedSources _sources;
    private readonly string _statePath;
    private readonly string? _gameVersion;
    private readonly SourceHistoryStore _history;

    // 进程级单写者。同一会话能并发提交多个 sync（协议层并发闸放 10 个，且这个工具
    // BypassIndexGate），共享宿主模式下不同客户端还共享同一个 SourceSyncService 实例。
    // 暂存目录、目标目录、历史版本号、状态文件全都没有各自的互斥，两次同步交错跑的结果是
    // 互删暂存区、历史版本号撞车、目标目录文件混合、后写的状态覆盖先写的。
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // 反编译动作。默认就是 DecompileService.Decompile；留成可注入是为了让事务性回归测试
    // 不必真跑 ILSpy（单个程序集实测数秒到数十秒，而「第三个程序集失败」这种场景根本没法按需复现）。
    internal Func<DecompileRequest, CancellationToken, DecompileOutcome> Decompiler { get; init; }
        = DecompileService.Decompile;

    // 目录改名与单文件复制。真实失败条件（跨卷、文件被别的进程打开、磁盘满）在测试里没法
    // 稳定复现，而「失败后必须回滚」恰恰是这里最要紧的行为，故同样留接缝。
    internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;

    internal Action<string, string> CopyFile { get; init; }
        = static (from, to) => File.Copy(from, to, overwrite: true);

    public SourceSyncService(AppConfig config, ResolvedSources sources, string cacheDirectory)
    {
        _sources = sources;
        _statePath = Path.Combine(cacheDirectory, "assembly-state.json");
        // 版本判定在 ResolveSources 时就做过了（mod 展开要用它），这里沿用同一个结论：
        // 两处各探一次的话，「索引按 1.6 展开、同步按别的版本筛 dll」这种错位无从察觉。
        _gameVersion = sources.GameVersion ?? config.GameVersion ?? DetectGameVersion(sources);
        _history = new SourceHistoryStore(cacheDirectory, config.SourceHistoryDepth);
    }

    public SourceHistoryStore History => _history;

    public string? GameVersion => _gameVersion;

    public IReadOnlyList<SourcePathEntry> FollowableSources => _sources.Followable;

    // 只读检查：扫描程序集、对比上次状态，不做任何反编译。实测 475 个 dll 约 235 ms。
    // 刻意不参与 _syncLock：它是启动探测的入口，被一次正在跑的同步挡住只会让启动变慢，
    // 而它读到的「旧状态 + 新磁盘」最坏也就是多报一次有变更。
    public SyncReport Check()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = LoadState();
        var changes = new List<SourceChange>();

        foreach (var entry in FollowableSources)
        {
            changes.Add(Inspect(entry, state));
        }

        stopwatch.Stop();
        return new SyncReport { Changes = changes, ElapsedMs = stopwatch.ElapsedMilliseconds };
    }

    // 整段「检查 → 反编译 → 转正 → 记历史 → 写状态」必须是单写者。
    // 第二个请求刻意不排队：排队等于让调用方以为自己那次真的跑了，而它拿到的其实是
    // 别人那次的结果（甚至是别人筛掉了它要的源之后的结果）。直接说「已有同步在跑」，
    // 由调用方决定是等还是放弃。
    public async Task<SyncReport> SyncAsync(
        IReadOnlyCollection<string>? onlySources,
        CancellationToken cancellationToken = default)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return SyncReport.Busy();
        }

        try
        {
            return SyncCore(onlySources, cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // 仅供测试：产品代码一律走 SyncAsync（SyncSourcesTool 已改成 await）。
    // 留成 internal 而不是删掉，是因为事务测试要在一次断言里同步观察「跑完之后磁盘长什么样」，
    // 而这里没有同步上下文，sync-over-async 不会死锁。
    internal SyncReport Sync(
        IReadOnlyCollection<string>? onlySources,
        CancellationToken cancellationToken = default)
        => SyncAsync(onlySources, cancellationToken).GetAwaiter().GetResult();

    private SyncReport SyncCore(IReadOnlyCollection<string>? onlySources, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = LoadState();
        var changes = new List<SourceChange>();
        var outcomes = new List<DecompileOutcome>();
        var changeSets = new List<SourceChangeSet>();
        var promoted = new List<string>();
        var stateDirty = false;

        try
        {
            foreach (var entry in FollowableSources)
            {
                if (onlySources != null && onlySources.Count > 0
                    && !onlySources.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                RecoverAbandonedBackup(entry.Path);

                var change = Inspect(entry, state);

                if (change.Blocker != null || !change.HasChanges)
                {
                    changes.Add(change);
                    continue;
                }

                var attempt = SyncOne(entry, change, cancellationToken);

                changes.Add(attempt.Change);
                outcomes.AddRange(attempt.Outcomes);
                if (attempt.FileChanges != null) changeSets.Add(attempt.FileChanges);

                // 状态只在整源转正成功后才记。以前是「有一个程序集成功就记下整批哈希」，
                // 于是失败那几个的哈希也进了状态，下次 Check() 报「无变更」——缺失的类型
                // 或上一版留下的幽灵代码从此不会再被自动修正。
                if (attempt.State != null)
                {
                    state.Sources[entry.Name] = attempt.State;
                    promoted.Add(entry.Name);
                    stateDirty = true;
                }
            }
        }
        finally
        {
            // 取消或异常时也要落盘：已经转正的那几个源磁盘上确实换过内容了，不记状态的话
            // 下次同步会把它们整份重来，还会在历史里多出一版「什么都没变」的版本。
            if (stateDirty) SaveState(state);
        }

        stopwatch.Stop();

        return new SyncReport
        {
            Changes = changes,
            Outcomes = outcomes,
            FileChanges = changeSets,
            PromotedSources = promoted,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    // 一个源的完整事务结果。State 非 null 即「已转正、可以记状态」——三者要么一起生效，
    // 要么一个都不生效，故用同一个返回值捆在一起，而不是让调用方各自判断。
    private sealed record SourceAttempt
    {
        public required SourceChange Change { get; init; }
        public IReadOnlyList<DecompileOutcome> Outcomes { get; init; } = [];
        public SourceChangeSet? FileChanges { get; init; }
        public SyncSourceState? State { get; init; }

        public static SourceAttempt Failed(
            SourceChange change, string? reason, IReadOnlyList<DecompileOutcome>? outcomes = null)
            => new()
            {
                Change = change with { Failure = reason ?? "未知原因" },
                Outcomes = outcomes ?? []
            };
    }

    // 逐源独立的事务：反编译到暂存区 → 转正（旧目录先改名留底）→ 归档历史 → 记状态。
    // 任何一步失败都让本源整体退回上一版，且不影响别的源。
    private SourceAttempt SyncOne(SourcePathEntry entry, SourceChange change, CancellationToken cancellationToken)
    {
        var entries = ScanSource(entry);
        var unique = DeduplicateByContent(entries);

        // 引用集跨源取并：mod 普遍引用 Assembly-CSharp，只给本源的目录会解析失败
        var referenceRoots = FollowableSources
            .SelectMany(e => e.AssemblyPaths)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 先全部反编译到暂存区，成功后才替换。中途失败/取消不会留下半份源码，
        // 也让历史归档能拿到完整的新旧两版做比较。
        //
        // 暂存区必须与目标同卷：放 cache 下时若两者跨盘，Directory.Move 会失败并退化成
        // 逐文件复制（实测 10222 个文件多花约 30 秒）。同级目录既同卷，又不在被索引的路径内。
        var staging = GetStagingPath(entry.Path);
        if (!TryPrepareStaging(staging, out var stagingError))
        {
            return SourceAttempt.Failed(change, stagingError);
        }

        try
        {
            var sourceOutcomes = new List<DecompileOutcome>();
            foreach (var (outputName, assembly) in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var references = DecompileService.ResolveReferencePaths(
                    assembly.Path, entries, referenceRoots);

                sourceOutcomes.Add(Decompiler(new DecompileRequest
                {
                    AssemblyPath = assembly.Path,
                    OutputDirectory = Path.Combine(staging, outputName),
                    ReferencePaths = references
                }, cancellationToken));
            }

            // 任一程序集失败即本源整体失败。以前只要有一个成功就照样转正：旧源码被残缺目录
            // 换掉、索引基于残缺源码重建、失败那几个的哈希还一并记进了状态——于是这份残缺
            // 既不会被察觉（下次 Check 报无变更），也永远不会自动重试。
            var failures = sourceOutcomes.Where(o => !o.Success).ToList();
            if (failures.Count > 0)
            {
                return SourceAttempt.Failed(
                    change, DescribeDecompileFailures(failures, sourceOutcomes.Count), sourceOutcomes);
            }

            var promotion = Promote(staging, entry.Path);
            if (!promotion.Success)
            {
                return SourceAttempt.Failed(change, promotion.Error, sourceOutcomes);
            }

            // 历史归档比的是「旧树 vs 新树」，而旧树此刻就是转正留下的留底目录。
            // 放在转正之后做，才不会在转正失败回滚时留下一版根本没发生过的历史。
            // 首次同步没有留底目录，传一个不存在的路径进去即可——整棵树会被算成新增。
            var previousTree = promotion.BackupPath ?? GetBackupPath(entry.Path);
            var changeSet = _history.Capture(entry.Name, previousTree, entry.Path);

            if (promotion.BackupPath != null) TryDelete(promotion.BackupPath);

            return new SourceAttempt
            {
                Change = change,
                Outcomes = sourceOutcomes,
                FileChanges = changeSet,
                State = new SyncSourceState
                {
                    Name = entry.Name,
                    CatalogDigest = AssemblyScanner.ComputeCatalogDigest(entries),
                    SyncedAtUtc = DateTime.UtcNow,
                    Assemblies = entries
                        .Where(e => e.Sha256 != null)
                        .ToDictionary(e => e.Path, e => e.Sha256!, StringComparer.OrdinalIgnoreCase)
                }
            };
        }
        finally
        {
            // 转正成功走的是 rename，此刻 staging 已经不存在；这里清的是失败、取消，
            // 以及退化成逐文件复制之后留下的那份残余。
            TryDelete(staging);
        }
    }

    private static string DescribeDecompileFailures(IReadOnlyList<DecompileOutcome> failures, int total)
    {
        var names = failures
            .Take(3)
            .Select(o => $"{Path.GetFileName(o.AssemblyPath)}（{o.Error}）");

        var suffix = failures.Count > 3 ? $" 等 {failures.Count} 个" : string.Empty;
        return $"{failures.Count}/{total} 个程序集反编译失败: {string.Join("; ", names)}{suffix}";
    }

    private SourceChange Inspect(SourcePathEntry entry, SyncState state)
    {
        var missing = entry.AssemblyPaths.Where(p => !Directory.Exists(p)).ToList();
        if (missing.Count == entry.AssemblyPaths.Count)
        {
            return new SourceChange
            {
                SourceName = entry.Name,
                HasChanges = false,
                Blocker = $"程序集目录均不存在: {string.Join("; ", missing)}"
            };
        }

        if (!IsWritableOutput(entry.Path, out var reason))
        {
            return new SourceChange { SourceName = entry.Name, HasChanges = false, Blocker = reason };
        }

        var entries = ScanSource(entry);
        state.Sources.TryGetValue(entry.Name, out var previous);

        var previousMap = previous?.Assemblies ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentMap = entries
            .Where(e => e.Sha256 != null)
            .ToDictionary(e => e.Path, e => e.Sha256!, StringComparer.OrdinalIgnoreCase);

        var added = currentMap.Keys.Count(k => !previousMap.ContainsKey(k));
        var modified = currentMap.Count(kv => previousMap.TryGetValue(kv.Key, out var old) && old != kv.Value);
        var removed = previousMap.Keys.Count(k => !currentMap.ContainsKey(k));

        return new SourceChange
        {
            SourceName = entry.Name,
            HasChanges = added + modified + removed > 0,
            Added = added,
            Modified = modified,
            Removed = removed,
            TotalAssemblies = entries.Count
        };
    }

    // 与目标同级、同卷，故转正走的是原子 rename 而非跨盘复制
    private static string GetStagingPath(string targetPath) => Sibling(targetPath, ".rimsearcher-staging");

    // 转正前旧目录改名到这里。同样要同级同卷，否则「留个底」本身就变成一次整树复制。
    private static string GetBackupPath(string targetPath) => Sibling(targetPath, ".rimsearcher-backup");

    private static string Sibling(string targetPath, string suffix)
    {
        var trimmed = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + suffix;
    }

    private List<AssemblyEntry> ScanSource(SourcePathEntry entry)
    {
        var scanned = AssemblyScanner.Enumerate(
            entry.AssemblyPaths, _gameVersion, excludedPaths: _sources.Shadowed);
        return AssemblyScanner.FillHashes(scanned);
    }

    // 同一个 0Harmony.dll 会散落在几百个 mod 目录里（实测 475 → 403 唯一），按内容去重只反编译一次。
    // 同名不同内容时补 sha 短前缀，避免两份产物互相覆盖。
    private static List<(string OutputName, AssemblyEntry Assembly)> DeduplicateByContent(
        IReadOnlyList<AssemblyEntry> entries)
    {
        var results = new List<(string, AssemblyEntry)>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.Sha256 == null || !seenHashes.Add(entry.Sha256)) continue;

            var baseName = Path.GetFileNameWithoutExtension(entry.Path);
            var outputName = usedNames.Add(baseName)
                ? baseName
                : $"{baseName}.{entry.Sha256[..8]}";

            usedNames.Add(outputName);
            results.Add((outputName, entry));
        }

        return results;
    }

    // 目录为空、不存在、或带有归属标记时才允许写入
    private static bool IsWritableOutput(string path, out string? reason)
    {
        reason = null;
        if (!Directory.Exists(path)) return true;
        if (File.Exists(Path.Combine(path, OwnershipMarker))) return true;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any()) return true;
        }
        catch (Exception ex)
        {
            reason = $"无法读取输出目录: {ex.Message}";
            return false;
        }

        reason = $"输出目录 '{path}' 非空且不是 RimSearcher 生成的（缺 {OwnershipMarker} 标记），"
               + "拒绝覆盖。请改用空目录，或确认无误后手工放置该标记文件。";
        return false;
    }

    // 暂存/留底目录的路径是从用户配置的输出目录推算出来的（同级加后缀），完全可能撞上
    // 用户自己的目录，而下一步就是递归删除。所以这里要走和正式输出目录同一套归属判定：
    // 不存在、空、或带 RimSearcher 标记，三者之一才算「归我们管」。
    // 缺了这道检查，一个恰好同名的目录会被静默抹掉，且没有任何回退余地。
    private static bool IsScratchClaimable(string path, out string? reason)
    {
        reason = null;
        if (!Directory.Exists(path)) return true;
        if (File.Exists(Path.Combine(path, OwnershipMarker))) return true;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any()) return true;
        }
        catch (Exception ex)
        {
            reason = $"无法读取 '{path}': {ex.Message}";
            return false;
        }

        reason = $"'{path}' 已存在、非空且缺 {OwnershipMarker} 标记，看起来是你自己的目录，"
               + "拒绝删除。请把它挪走或改名后重试。";
        return false;
    }

    // 归属校验 + 确保删干净。删不掉就算失败：残留文件会和新产物混在一起，
    // 「全量重出一份」实际变成了增量覆盖，上一版删掉的类型会以幽灵代码的形式留在索引里。
    private static bool TryClearScratch(string path, out string? reason)
    {
        if (!IsScratchClaimable(path, out reason)) return false;

        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            reason = $"清理 '{path}' 失败: {ex.Message}";
            return false;
        }

        // Windows 上 Directory.Delete 可能在目录项真正消失之前就返回（句柄尚未完全释放），
        // 紧接着的 rename 会撞上「目录已存在」。短暂等一下比直接报错更贴近现实。
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++) Thread.Sleep(20);

        if (Directory.Exists(path))
        {
            reason = $"'{path}' 删除后仍然存在（多半是有进程正打开着里面的文件）";
            return false;
        }

        return true;
    }

    private bool TryPrepareStaging(string staging, out string? reason)
    {
        if (!TryClearScratch(staging, out reason))
        {
            reason = $"暂存目录不可用: {reason}";
            return false;
        }

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex)
        {
            reason = $"创建暂存目录 '{staging}' 失败: {ex.Message}";
            return false;
        }

        // 标记先写、反编译后跑：崩在反编译中途留下的暂存目录靠这个标记才能被下次同步
        // 认作自己的东西并安全回收，否则它会以「非空又没标记」的身份把后续同步全部挡住。
        // 另外标记随暂存区一起 rename 就位，不会出现「目录已换、标记还没写上」的中间态。
        if (!TryWriteMarker(staging, out reason)) return false;

        WriteGitIgnore(staging);
        return true;
    }

    private static bool TryWriteMarker(string directory, out string? reason)
    {
        reason = null;

        try
        {
            File.WriteAllText(Path.Combine(directory, OwnershipMarker),
                $"generated by RimSearcher {UpdateChecker.CurrentVersion}\n");
            return true;
        }
        catch (Exception ex)
        {
            // 标记是「这个目录归我管」的唯一凭据，写不上就不能转正：目录一旦非空又没标记，
            // 下次同步只会拒绝覆盖，用户从此卡在一棵永远不再更新的源码树上。
            reason = $"写入归属标记 '{OwnershipMarker}' 失败: {ex.Message}";
            return false;
        }
    }

    // 反编译产物是游戏/mod 代码的衍生物：本地留着属于互操作用途，提交进版本库再公开就不是了。
    // 使用者把 mod 工程和这个目录放在同一个 git 仓库下是很自然的事，靠自觉不如靠机制。
    // cargo / npm 对自己的 cache 目录也是这么做的。
    private static void WriteGitIgnore(string path)
    {
        try
        {
            File.WriteAllText(Path.Combine(path, ".gitignore"),
                "# RimSearcher 反编译产物：请勿提交或再分发\n*\n");
        }
        catch (Exception ex)
        {
            // 写不进去意味着这道防线没建起来，产物可能被一起提交——不阻断同步，但必须留痕。
            // 与归属标记刻意不同级：标记丢了会让后续同步彻底卡死，这个只是少了一层保险。
            _ = ServerLogger.Warning("SourceSync", "Could not write .gitignore into decompile output",
                ("path", path), ("reason", ex.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // 删不掉时残留文件会和新产物混在一起，「全量重出」实际变成了增量覆盖
            _ = ServerLogger.Warning("SourceSync", "Could not clear directory", ("path", path), ("reason", ex.Message));
        }
    }

    private readonly record struct Promotion(bool Success, string? BackupPath, string? Error);

    // 暂存区转正，带留底与回滚。
    //
    // 原先是「先 Directory.Delete(target) 再 Directory.Move(staging, target)」：删完之后
    // Move 只要失败（跨卷、目标被占用、进程被杀），旧源码就彻底没了——而它可能是用户当下
    // 唯一能看的那一份，而且不是所有人都还留着能重新反编译出它的那个 dll 版本。
    //
    // 现在是：target 改名留底 → Move 暂存区到位 → 成功后由调用方删留底；任何一步失败都把
    // 留底改回原位。同卷 rename 既快又原子，留底的代价接近零。
    private Promotion Promote(string staging, string target)
    {
        string? backup = null;

        if (Directory.Exists(target))
        {
            backup = GetBackupPath(target);

            // 留底目录同样可能撞上用户自己的目录，删之前先验归属
            if (!TryClearScratch(backup, out var backupError))
            {
                return new Promotion(false, null, $"留底目录不可用: {backupError}");
            }

            try
            {
                MoveDirectory(target, backup);
            }
            catch (Exception ex)
            {
                // 留底失败就什么都不做。宁可这次同步不成，也不能在没有退路的前提下动旧目录。
                return new Promotion(false, null,
                    $"旧源码目录改名留底失败（多半是有进程正打开着里面的文件）: {ex.Message}");
            }
        }

        try
        {
            MoveDirectory(staging, target);
            return new Promotion(true, backup, null);
        }
        // 跨卷或目标被占用，退回逐文件复制。慢很多，所以记一笔说明原因
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ServerLogger.Info("SourceSync", "Directory move failed, falling back to per-file copy",
                ("target", target), ("reason", ex.Message));
        }
        catch (Exception ex)
        {
            return Rollback(backup, target, $"暂存区转正失败: {ex.Message}");
        }

        if (TryCopyTree(staging, target, out var copyError))
        {
            return new Promotion(true, backup, null);
        }

        return Rollback(backup, target, copyError!);
    }

    private Promotion Rollback(string? backup, string target, string error)
    {
        // 半成品必须先清掉：它是一棵残缺的源码树，留着还会让 rename 撞上「目录已存在」
        TryDelete(target);

        if (backup == null) return new Promotion(false, null, error);

        try
        {
            MoveDirectory(backup, target);
            return new Promotion(false, null, $"{error}（旧源码已回滚）");
        }
        catch (Exception ex)
        {
            // 最坏情况：旧源码还在，但没能回到原位。必须把两个路径都喊出来——
            // 用户手工改个名就能救回来，而闷声不响的话他只会看到源码目录凭空消失。
            _ = ServerLogger.Error("SourceSync",
                "Rollback failed, previous sources are left in the backup directory",
                ("backup", backup), ("target", target), ("reason", ex.Message));

            return new Promotion(false, null,
                $"{error}；回滚同样失败（{ex.Message}）——旧源码仍在 '{backup}'，请手工改名回 '{target}'");
        }
    }

    // 跨卷或目标被占用时的退路（代码注释里实测的那 30 秒就是它）。
    // 任何一次复制失败都算整体失败：以前只累加计数记一条 warning 就继续，结果是磁盘上一棵
    // 残缺的源码树 + 一次报「成功」的同步 + 一份已经记下的哈希，缺的东西从此不会自动补回来。
    private bool TryCopyTree(string staging, string target, out string? reason)
    {
        reason = null;

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            reason = $"创建目标目录 '{target}' 失败: {ex.Message}";
            return false;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, file);
                var destination = Path.Combine(target, relative);
                var directory = Path.GetDirectoryName(destination);

                try
                {
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    CopyFile(file, destination);
                }
                catch (Exception ex)
                {
                    reason = $"复制 '{relative}' 失败: {ex.Message}";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            reason = $"遍历暂存目录 '{staging}' 失败: {ex.Message}";
            return false;
        }

        return true;
    }

    // 上一次转正在「旧目录已改名留底」和「暂存区搬到位」之间断掉了（进程被杀、断电）：
    // 源码目录此刻只存在于留底目录里。先把它挪回原位再走正常流程——否则这一版旧内容会被
    // 当成垃圾清掉，历史 diff 也就失去了比较基线。
    private void RecoverAbandonedBackup(string target)
    {
        var backup = GetBackupPath(target);

        if (Directory.Exists(target) || !Directory.Exists(backup)) return;

        // 没有归属标记就不是我们留下的，绝不搬动
        if (!File.Exists(Path.Combine(backup, OwnershipMarker))) return;

        try
        {
            MoveDirectory(backup, target);
            _ = ServerLogger.Warning("SourceSync",
                "Recovered sources left in the backup directory by an interrupted sync",
                ("backup", backup), ("target", target));
        }
        catch (Exception ex)
        {
            _ = ServerLogger.Warning("SourceSync", "Could not recover abandoned backup directory",
                ("backup", backup), ("reason", ex.Message));
        }
    }

    // Version.txt 首行形如 "1.6.4871 rev590"，取前两段作为 mod 版本目录的匹配键
    private static string? DetectGameVersion(ResolvedSources sources)
    {
        foreach (var assemblyPath in sources.Followable.SelectMany(e => e.AssemblyPaths))
        {
            var directory = new DirectoryInfo(assemblyPath);
            while (directory != null)
            {
                var versionFile = Path.Combine(directory.FullName, "Version.txt");
                if (File.Exists(versionFile))
                {
                    try
                    {
                        var first = File.ReadLines(versionFile).FirstOrDefault()?.Trim();
                        var parts = first?.Split('.', StringSplitOptions.RemoveEmptyEntries);
                        if (parts is { Length: >= 2 }) return $"{parts[0]}.{parts[1]}";
                    }
                    catch (Exception ex)
                    {
                        // 读不出版本号 → GameVersion 为 unknown → mod 的多版本目录无法筛选，
                        // 历史版本的死代码会被一起索引。是降级不是失败，但要说出来
                        _ = ServerLogger.Warning("SourceSync", "Could not read game version",
                            ("path", versionFile), ("reason", ex.Message));
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private SyncState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var json = File.ReadAllText(_statePath);
                var loaded = JsonSerializer.Deserialize<SyncState>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // 落回空状态意味着「所有源都没同步过」，下一次 sync 会全量重来。
            // 不记的话，用户只会看到反编译莫名其妙又跑了一整遍
            _ = ServerLogger.Warning("SourceSync", "Could not read sync state, treating every source as never synced",
                ("path", _statePath), ("reason", ex.Message));
        }

        return new SyncState();
    }

    private void SaveState(SyncState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            // 先写临时文件再原子替换。直接 WriteAllText 的话，进程在写一半时被杀就留下半截
            // JSON：LoadState 解析失败会把所有源都当成从未同步过，下一次 sync 整份重来
            // （vanilla + 400 个 mod 实测数分钟），历史里还会多出一版什么都没变的版本。
            // 临时名带 PID：多实例并发保存时固定名会互相截断（与 IndexCacheService 同一套做法）。
            var tempPath = $"{_statePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            ReplaceFile(tempPath, _statePath);
        }
        catch (Exception ex)
        {
            // 存不下就等于这次同步没发生过，下次启动仍报「有变更」
            _ = ServerLogger.Warning("SourceSync", "Could not persist sync state",
                ("path", _statePath), ("reason", ex.Message));
        }
    }

    private static void ReplaceFile(string tempPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(tempPath, targetPath);
            return;
        }

        try
        {
            File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            // File.Replace 在部分文件系统（网络盘、部分容器挂载）上不支持，退回删除 + 改名。
            // 这一小段窗口里目标文件不存在，但比整段写入过程都在裸奔要窄得多。
            File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }
    }
}
