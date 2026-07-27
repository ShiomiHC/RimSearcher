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

    public string Describe() => Blocker != null
        ? $"{SourceName}: 无法同步 — {Blocker}"
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

    public bool AnyChanges => Changes.Any(c => c.HasChanges);
    public long ElapsedMs { get; init; }
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

    public SourceSyncService(AppConfig config, ResolvedSources sources, string cacheDirectory)
    {
        _sources = sources;
        _statePath = Path.Combine(cacheDirectory, "assembly-state.json");
        _gameVersion = config.GameVersion ?? DetectGameVersion(sources);
        _history = new SourceHistoryStore(cacheDirectory, config.SourceHistoryDepth);
    }

    public SourceHistoryStore History => _history;

    public string? GameVersion => _gameVersion;

    public IReadOnlyList<SourcePathEntry> FollowableSources => _sources.Followable;

    // 只读检查：扫描程序集、对比上次状态，不做任何反编译。实测 475 个 dll 约 235 ms。
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

    public SyncReport Sync(IReadOnlyCollection<string>? onlySources, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = LoadState();
        var changes = new List<SourceChange>();
        var outcomes = new List<DecompileOutcome>();
        var changeSets = new List<SourceChangeSet>();
        var stagingPaths = new List<string>();

        foreach (var entry in FollowableSources)
        {
            if (onlySources != null && onlySources.Count > 0
                && !onlySources.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            var change = Inspect(entry, state);
            changes.Add(change);

            if (change.Blocker != null || !change.HasChanges) continue;

            var entries = ScanSource(entry);
            var unique = DeduplicateByContent(entries);

            // 引用集跨源取并：mod 普遍引用 Assembly-CSharp，只给本源的目录会解析失败
            var referenceRoots = FollowableSources
                .SelectMany(e => e.AssemblyPaths)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 先全部反编译到暂存区，成功后才替换。中途失败/取消不会留下半份源码，
            // 也让历史归档能在替换前拿到完整的新旧两版做比较。
            //
            // 暂存区必须与目标同卷：放 cache 下时若两者跨盘，Directory.Move 会失败并退化成
            // 逐文件复制（实测 10222 个文件多花约 30 秒）。同级目录既同卷，又不在被索引的路径内。
            var staging = GetStagingPath(entry.Path);
            ResetDirectory(staging);

            var sourceOutcomes = new List<DecompileOutcome>();
            foreach (var (outputName, assembly) in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var references = DecompileService.ResolveReferencePaths(
                    assembly.Path, entries, referenceRoots);

                sourceOutcomes.Add(DecompileService.Decompile(new DecompileRequest
                {
                    AssemblyPath = assembly.Path,
                    OutputDirectory = Path.Combine(staging, outputName),
                    ReferencePaths = references
                }, cancellationToken));
            }

            outcomes.AddRange(sourceOutcomes);

            if (sourceOutcomes.All(o => !o.Success))
            {
                TryDelete(staging);
                continue;
            }

            stagingPaths.Add(staging);

            var changeSet = _history.Capture(entry.Name, entry.Path, staging);
            changeSets.Add(changeSet);

            Promote(staging, entry.Path);
            File.WriteAllText(Path.Combine(entry.Path, OwnershipMarker),
                $"generated by RimSearcher {UpdateChecker.CurrentVersion}\n");

            state.Sources[entry.Name] = new SyncSourceState
            {
                Name = entry.Name,
                CatalogDigest = AssemblyScanner.ComputeCatalogDigest(entries),
                SyncedAtUtc = DateTime.UtcNow,
                Assemblies = entries
                    .Where(e => e.Sha256 != null)
                    .ToDictionary(e => e.Path, e => e.Sha256!, StringComparer.OrdinalIgnoreCase)
            };
        }

        SaveState(state);
        foreach (var path in stagingPaths) TryDelete(path);
        stopwatch.Stop();

        return new SyncReport
        {
            Changes = changes,
            Outcomes = outcomes,
            FileChanges = changeSets,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
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
    private static string GetStagingPath(string targetPath)
    {
        var trimmed = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + ".rimsearcher-staging";
    }

    private List<AssemblyEntry> ScanSource(SourcePathEntry entry)
    {
        var scanned = AssemblyScanner.Enumerate(entry.AssemblyPaths, _gameVersion);
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

    private static void ResetDirectory(string path)
    {
        TryDelete(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    // 暂存区转正。同卷时 Move 是原子的；跨卷（cache 与源码目录不同盘）退化为复制。
    private static void Promote(string staging, string target)
    {
        var marker = Path.Combine(target, OwnershipMarker);
        var hadMarker = File.Exists(marker);

        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return;
        }
        catch (IOException)
        {
            // 跨卷或目标被占用，退回逐文件复制
        }
        catch (UnauthorizedAccessException) { }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            var destination = Path.Combine(target, relative);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            try { File.Copy(file, destination, overwrite: true); }
            catch { }
        }

        if (hadMarker && !File.Exists(marker))
        {
            try { File.WriteAllText(marker, "generated by RimSearcher\n"); } catch { }
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
                    catch { }
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
        catch { }

        return new SyncState();
    }

    private void SaveState(SyncState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch { }
    }
}
