using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Server;

// 一次已完成同步的精确结果，与 PendingSourceUpdate（尚未同步、只知道哪个源变了）相对
public sealed record SyncedChanges
{
    public DateTime SyncedAtUtc { get; init; }
    public IReadOnlySet<string> ChangedNames { get; init; } = new HashSet<string>();
}

public sealed record PendingSourceUpdate
{
    public DateTime DetectedAtUtc { get; init; }
    public IReadOnlyList<string> ChangedAssemblySources { get; init; } = [];
    public IReadOnlyList<string> ChangedXmlSources { get; init; } = [];

    // 记录丢了、产物还在的那些源（SourceChange.IsLostRecordOnly）。刻意与 Changed 分开存：
    // 这批源没有任何内容差异被观察到，把它们并进 ChangedAssemblySources 就等于让这条挂在
    // 每次查询末尾的提示宣布「源变了、结果可能过时」——两句都是假的，而它给出的补救
    // （全量重反编译）代价是分钟级且换不来一处变化。
    public IReadOnlyList<string> UnverifiedAssemblySources { get; init; } = [];

    // 变更涉及的文件名/类型名，用于判断某个会话查过的东西是否受影响
    public IReadOnlyList<string> Hints { get; init; } = [];

    // 变更源对应的源码根目录。会话查过的类型只要不落在这些目录下，这次变更就与它无关。
    public IReadOnlyList<string> ChangedRoots { get; init; } = [];

    public bool RequiresDecompile => ChangedAssemblySources.Count > 0;

    // 观察到的**差异**。只有无记录可比的源不算——那是「没验证」，见 UnverifiedAssemblySources。
    public bool AnyChanged => ChangedAssemblySources.Count > 0 || ChangedXmlSources.Count > 0;

    public bool Any => AnyChanged || UnverifiedAssemblySources.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (ChangedAssemblySources.Count > 0)
            parts.Add($"assemblies changed in: {string.Join(", ", ChangedAssemblySources)}");
        if (ChangedXmlSources.Count > 0)
            parts.Add($"XML defs changed in: {string.Join(", ", ChangedXmlSources)}");
        if (UnverifiedAssemblySources.Count > 0)
            parts.Add($"no sync record to compare against in: {string.Join(", ", UnverifiedAssemblySources)}");
        return string.Join("; ", parts);
    }
}

// 进程级的源变更探测。刻意是一次性的：DetectAsync 只在启动时跑一趟，此后不再复查，
// 结果一直挂在 Pending 上直到 sync_sources 把它清掉。之所以不做成常驻监视，是因为
// 唯一的消费路径就是「在工具返回里附一句提示」，而那句提示对一次会话说一遍就够了；
// 常驻轮询要么反复打扰，要么就得再记一层「哪些提示已经发过」的状态。
//
// dll 与 xml 两侧并行探测——前者要算 sha256（实测 475 个约 235 ms），
// 后者只比大小与修改时间（1672 个约 10 ms），串行跑没有意义。
public static class SourceChangeProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static SourceSyncService? _syncService;
    private static ResolvedSources? _sources;
    private static string _statePath = string.Empty;
    private static volatile PendingSourceUpdate? _pending;

    public static bool Enabled { get; private set; }

    public static PendingSourceUpdate? Pending => _pending;

    // 会话据此把「查过的类型」反查到文件路径，再判断是否落在变更源里
    public static SourceIndexer? Indexer { get; private set; }

    public static void Configure(
        SourceSyncService syncService,
        ResolvedSources sources,
        string cacheDirectory,
        SourceIndexer indexer)
    {
        _syncService = syncService;
        _sources = sources;
        _statePath = Path.Combine(cacheDirectory, "xml-state.json");
        Indexer = indexer;
        Enabled = true;
    }

    private static List<string> RootsOf(IEnumerable<SourcePathEntry> entries, ICollection<string> sourceNames)
    {
        var roots = new List<string>();
        foreach (var entry in entries)
        {
            if (!sourceNames.Contains(entry.Name)) continue;
            try { roots.Add(Path.GetFullPath(entry.Path)); }
            catch { roots.Add(entry.Path); }
        }
        return roots;
    }

    public static SessionUpdateNotice? CreateSessionNotice()
        => Enabled ? new SessionUpdateNotice() : null;

    // 后台探测。绝不触发反编译或重建——那是 sync_sources 的活，这里只负责发现并记录。
    public static async Task DetectAsync()
    {
        if (!Enabled || _syncService == null || _sources == null) return;

        try
        {
            var assemblyTask = Task.Run(DetectAssemblyChanges);
            var xmlTask = Task.Run(DetectXmlChanges);

            await Task.WhenAll(assemblyTask, xmlTask);

            var (assemblySources, unverifiedSources, assemblyHints) = assemblyTask.Result;
            var (xmlSources, xmlHints) = xmlTask.Result;

            if (assemblySources.Count == 0 && unverifiedSources.Count == 0 && xmlSources.Count == 0)
            {
                await ServerLogger.Info("SourceChangeProbe", "No source changes detected");
                return;
            }

            var changedNames = new HashSet<string>(
                assemblySources.Concat(unverifiedSources).Concat(xmlSources), StringComparer.OrdinalIgnoreCase);

            _pending = new PendingSourceUpdate
            {
                DetectedAtUtc = DateTime.UtcNow,
                ChangedAssemblySources = assemblySources,
                UnverifiedAssemblySources = unverifiedSources,
                ChangedXmlSources = xmlSources,
                Hints = assemblyHints.Concat(xmlHints).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ChangedRoots = RootsOf(_sources!.Csharp, changedNames)
                    .Concat(RootsOf(_sources!.Xml, changedNames))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            await ServerLogger.Warning("SourceChangeProbe", "Source changes detected",
                ("assemblies", string.Join(",", assemblySources)),
                ("unverified", string.Join(",", unverifiedSources)),
                ("xml", string.Join(",", xmlSources)),
                ("hints", _pending.Hints.Count));
        }
        catch (Exception ex)
        {
            await ServerLogger.Warning("SourceChangeProbe", "Detection failed", ("reason", ex.Message));
        }
    }

    private static (List<string> Sources, List<string> Unverified, List<string> Hints) DetectAssemblyChanges()
    {
        var sources = new List<string>();
        var unverified = new List<string>();
        var hints = new List<string>();

        var report = _syncService!.Check();
        foreach (var change in report.Changes)
        {
            if (!change.HasChanges || change.Blocker != null) continue;
            // 记录丢了、产物还在的源分到另一桶：这条提示会宣布「结果可能过时」，
            // 而那批源一处差异都没被观察到，说成变更就是把「验不了」印成「变了」。
            if (change.IsLostRecordOnly) unverified.Add(change.SourceName);
            else sources.Add(change.SourceName);
        }

        // 程序集级变更给不出类型名，退而用源名做提示词。两桶都要——相关性判定问的是
        // 「这个会话查过的东西落在这些源里吗」，与该源是变了还是验不了无关。
        hints.AddRange(sources);
        hints.AddRange(unverified);
        return (sources, unverified, hints);
    }

    private static (List<string> Sources, List<string> Hints) DetectXmlChanges()
    {
        var sources = new List<string>();
        var hints = new List<string>();
        var previous = LoadXmlState();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _sources!.Xml.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var digest = ComputeXmlDigest(group.Select(e => e.Path), out var sampleNames);
            current[group.Key] = digest;

            if (previous.TryGetValue(group.Key, out var old) && !string.Equals(old, digest, StringComparison.Ordinal))
            {
                sources.Add(group.Key);
                hints.AddRange(sampleNames);
            }
        }

        SaveXmlState(current);
        return (sources, hints);
    }

    // XML 是文本且数量上万，算内容哈希不划算；大小+修改时间足以发现 Steam 更新
    private static string ComputeXmlDigest(IEnumerable<string> roots, out List<string> sampleNames)
    {
        var entries = new List<string>();
        sampleNames = new List<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);

                    // 被遮蔽的文件不进索引，它变了也不影响任何查询结果——算进摘要只会误报一次
                    // 「源已更新」，而用户去看的时候什么都没变
                    if (_sources?.Shadowed.Contains(info.FullName) == true) continue;

                    entries.Add($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                }
                catch { }
            }
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);

        // 只留少量文件名做相关性提示，全量会让 pending 记录膨胀
        foreach (var entry in entries.Take(200))
        {
            var separator = entry.IndexOf('|');
            if (separator <= 0) continue;
            var name = Path.GetFileNameWithoutExtension(entry[..separator]);
            if (!string.IsNullOrWhiteSpace(name)) sampleNames.Add(name);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static Dictionary<string, string> LoadXmlState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_statePath), JsonOptions);
                if (loaded != null) return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveXmlState(Dictionary<string, string> state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch { }
    }

    internal static void ClearPending() => _pending = null;

    // 探测本身要跑一遍真实反编译目录才有 Pending，而需要回归的是「同一份 Pending 渲染成
    // 哪句话」。测试直接放一份进来，避免为一句措辞搭出整套同步服务。
    internal static void SetPendingForTests(PendingSourceUpdate? pending) => _pending = pending;

    // 同步完成后才有文件级 diff，判定精度从「源里的东西」升到「这个类型本身」。
    // 记在进程级：触发同步的只是某一个会话，别的会话同样需要知道自己看过的东西过时了。
    internal static void RecordSync(IEnumerable<SourceChangeSet> changeSets)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var changeSet in changeSets)
        {
            foreach (var change in changeSet.Changes)
            {
                // 反编译产物一类一文件，裸文件名就是类型名
                var name = Path.GetFileNameWithoutExtension(change.RelativePath);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }

        LastSync = new SyncedChanges { SyncedAtUtc = DateTime.UtcNow, ChangedNames = names };
        _pending = null;
    }

    public static SyncedChanges? LastSync { get; private set; }
}

// 每个会话一份（IndexHost 为每条管道连接各建一个 RimSearcher）。记录本会话问过什么，
// 只有变更确实碰到这些东西时才打断用户；否则写日志了事。
//
// 线程安全是必需项而非可选项：RimSearcher 每条协议消息各起一个任务，同一会话最多有
// _concurrencyLimit 个工具调用同时落到 Consume 上。裸 HashSet 并发 Add 会破坏内部桶结构。
public sealed class SessionUpdateNotice
{
    private static readonly string[] QueryArgumentNames =
        ["query", "name", "symbol", "pattern", "path", "type", "def"];

    // 问过的词只增不减，给个上限免得长会话无界增长。到顶后停止收新词而不是清空，
    // 早期问过的东西仍在集合里，相关性判定不会突然整体失效。
    private const int MaxTrackedTerms = 512;

    private readonly ConcurrentDictionary<string, byte> _askedAbout = new(StringComparer.OrdinalIgnoreCase);

    // 存 Ticks 而非 DateTime，以便用 Interlocked 原子推进：只有把时间戳推进成功的那个
    // 线程才发提示，并发调用因此不会对同一批变更重复打断。
    private long _notifiedForTicks = DateTime.MinValue.Ticks;
    private long _notifiedSyncTicks = DateTime.MinValue.Ticks;

    internal int TrackedTermCount => _askedAbout.Count;

    public string? Consume(string? toolName, JsonElement arguments, string resultContent)
    {
        RecordQuery(arguments);

        // 同步后的精确 diff 优先：此刻能指名道姓说哪个类型变了，而不是笼统说源变了
        var synced = DescribeSyncedChanges();
        if (synced != null) return synced;

        var pending = SourceChangeProbe.Pending;
        if (pending == null || !pending.Any) return null;

        // 同一批变更只打断一次。抢不到即别的并发调用已经处理过这批。
        if (!TryClaim(ref _notifiedForTicks, pending.DetectedAtUtc)) return null;

        if (!IsRelevant(pending))
        {
            _ = ServerLogger.Info("SourceChangeProbe", "Change not related to this session's queries",
                ("tool", toolName ?? "?"));
            return null;
        }

        // 一处差异都没观察到、只是无记录可比时，这条提示原先照样说「源变了、结果可能过时、
        // 去跑一次 sync」。三句全是假的：sync 记录空了而反编译产物还在磁盘上，本次比对
        // 根本没有可比的旧哈希。它挂在**每一次**查询返回的末尾，比 sync_sources 自己那条
        // 更容易把调用方推向那次白跑的全量重反编译（第十二轮盲测里正是这么发生的）。
        if (!pending.AnyChanged)
            return $"\n\n---\n**Note: this session cannot confirm the indexed C# is current.** "
                 + $"{pending.Describe()} — the decompiled output those results came from is on disk, "
                 + "there is just no record of what it was built from, so nothing here is known to have "
                 + "changed. Run rimworld-searcher__sync_sources with action='check' for details.";

        var action = pending.RequiresDecompile
            ? "Run rimworld-searcher__sync_sources with action='sync' to re-decompile and rebuild the index "
              + "(no restart needed), then action='diff' to see what changed."
            : "Run rimworld-searcher__sync_sources with action='check' for details.";

        return $"\n\n---\n**Note: the indexed sources changed since this session started.** "
             + $"{pending.Describe()}. Results above may be stale. {action}";
    }

    // 只报「这个会话确实问过、且这次同步确实改了」的那些名字。
    // 问过的东西一个都没变时返回 null——沉默才是对的输出。
    private string? DescribeSyncedChanges()
    {
        var lastSync = SourceChangeProbe.LastSync;
        if (lastSync == null) return null;

        // 无论最终报不报，这一版同步都算已消费；抢不到即别的并发调用已经消费过它
        if (!TryClaim(ref _notifiedSyncTicks, lastSync.SyncedAtUtc)) return null;
        if (_askedAbout.IsEmpty) return null;

        var affected = _askedAbout.Keys
            .Where(term => lastSync.ChangedNames.Contains(term))
            .OrderBy(term => term, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (affected.Count == 0) return null;

        return $"\n\n---\n**Note: {string.Join(", ", affected)} changed in the latest sync.** "
             + "Anything said about them earlier in this conversation is now out of date; "
             + "use rimworld-searcher__sync_sources with action='diff' and a 'file' to see the line-level changes.";
    }

    private void RecordQuery(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return;

        foreach (var name in QueryArgumentNames)
        {
            if (!ToolArgs.TryGetElement(arguments, out var value, name)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 'def:Foo' / 'RimWorld.Pawn' 这类要拆开，否则和变更提示里的裸类型名对不上
            foreach (var token in text.Split([':', '.', '/', '\\', ' ', ','],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Length < 3) continue;
                if (_askedAbout.Count >= MaxTrackedTerms && !_askedAbout.ContainsKey(token)) continue;
                _askedAbout.TryAdd(token, 0);
            }
        }
    }

    // 把时间戳单调推进到 stamp；返回 true 表示本次调用抢到了「为这批变更发一次提示」的资格
    private static bool TryClaim(ref long slot, DateTime stamp)
    {
        var target = stamp.Ticks;

        while (true)
        {
            var current = Interlocked.Read(ref slot);
            if (current >= target) return false;
            if (Interlocked.CompareExchange(ref slot, target, current) == current) return true;
        }
    }

    private bool IsRelevant(PendingSourceUpdate pending)
    {
        // 还没问过任何具体东西时不打扰
        if (_askedAbout.IsEmpty) return false;

        // 主判据：把会话问过的名字反查回文件，看它们是否落在变更的源里。
        // 变的是某个 mod 而这个会话一直在看 vanilla 时，这一步就把提示挡掉了。
        var indexer = SourceChangeProbe.Indexer;
        if (indexer != null && pending.ChangedRoots.Count > 0)
        {
            foreach (var term in _askedAbout.Keys)
            {
                foreach (var path in indexer.GetPathsByType(term))
                {
                    if (IsUnderChangedRoot(path, pending.ChangedRoots)) return true;
                }
            }
        }

        // def 不在 C# 类型索引里，退回按 XML 文件名匹配
        foreach (var hint in pending.Hints)
        {
            foreach (var term in _askedAbout.Keys)
            {
                if (hint.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || term.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool IsUnderChangedRoot(string path, IReadOnlyList<string> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path; }

        foreach (var root in roots)
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
