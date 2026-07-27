using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Server;

public sealed record PendingSourceUpdate
{
    public DateTime DetectedAtUtc { get; init; }
    public IReadOnlyList<string> ChangedAssemblySources { get; init; } = [];
    public IReadOnlyList<string> ChangedXmlSources { get; init; } = [];

    // 变更涉及的文件名/类型名，用于判断某个会话查过的东西是否受影响
    public IReadOnlyList<string> Hints { get; init; } = [];

    // 变更源对应的源码根目录。会话查过的类型只要不落在这些目录下，这次变更就与它无关。
    public IReadOnlyList<string> ChangedRoots { get; init; } = [];

    public bool RequiresDecompile => ChangedAssemblySources.Count > 0;
    public bool Any => ChangedAssemblySources.Count > 0 || ChangedXmlSources.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (ChangedAssemblySources.Count > 0)
            parts.Add($"assemblies changed in: {string.Join(", ", ChangedAssemblySources)}");
        if (ChangedXmlSources.Count > 0)
            parts.Add($"XML defs changed in: {string.Join(", ", ChangedXmlSources)}");
        return string.Join("; ", parts);
    }
}

// 进程级的源变更观察者。dll 与 xml 两侧并行探测——前者要算 sha256（实测 475 个约 235 ms），
// 后者只比大小与修改时间（1672 个约 10 ms），串行跑没有意义。
public static class SourceWatcher
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

            var (assemblySources, assemblyHints) = assemblyTask.Result;
            var (xmlSources, xmlHints) = xmlTask.Result;

            if (assemblySources.Count == 0 && xmlSources.Count == 0)
            {
                await ServerLogger.Info("SourceWatcher", "No source changes detected");
                return;
            }

            var changedNames = new HashSet<string>(
                assemblySources.Concat(xmlSources), StringComparer.OrdinalIgnoreCase);

            _pending = new PendingSourceUpdate
            {
                DetectedAtUtc = DateTime.UtcNow,
                ChangedAssemblySources = assemblySources,
                ChangedXmlSources = xmlSources,
                Hints = assemblyHints.Concat(xmlHints).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ChangedRoots = RootsOf(_sources!.Csharp, changedNames)
                    .Concat(RootsOf(_sources!.Xml, changedNames))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            await ServerLogger.Warning("SourceWatcher", "Source changes detected",
                ("assemblies", string.Join(",", assemblySources)),
                ("xml", string.Join(",", xmlSources)),
                ("hints", _pending.Hints.Count));
        }
        catch (Exception ex)
        {
            await ServerLogger.Warning("SourceWatcher", "Detection failed", ("reason", ex.Message));
        }
    }

    private static (List<string> Sources, List<string> Hints) DetectAssemblyChanges()
    {
        var sources = new List<string>();
        var hints = new List<string>();

        var report = _syncService!.Check();
        foreach (var change in report.Changes)
        {
            if (!change.HasChanges || change.Blocker != null) continue;
            sources.Add(change.SourceName);
        }

        // 程序集级变更给不出类型名，退而用源名做提示词
        hints.AddRange(sources);
        return (sources, hints);
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
}

// 每个会话一份（IndexHost 为每条管道连接各建一个 RimSearcher）。记录本会话问过什么，
// 只有变更确实碰到这些东西时才打断用户；否则写日志了事。
public sealed class SessionUpdateNotice
{
    private static readonly string[] QueryArgumentNames =
        ["query", "name", "symbol", "pattern", "path", "type", "def"];

    private readonly HashSet<string> _askedAbout = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _notifiedFor = DateTime.MinValue;

    public string? Consume(string? toolName, JsonElement arguments, string resultContent)
    {
        RecordQuery(arguments);

        var pending = SourceWatcher.Pending;
        if (pending == null || !pending.Any) return null;

        // 同一批变更只打断一次
        if (_notifiedFor >= pending.DetectedAtUtc) return null;

        if (!IsRelevant(pending))
        {
            _ = ServerLogger.Info("SourceWatcher", "Change not related to this session's queries",
                ("tool", toolName ?? "?"));
            _notifiedFor = pending.DetectedAtUtc;
            return null;
        }

        _notifiedFor = pending.DetectedAtUtc;

        var action = pending.RequiresDecompile
            ? "Run rimworld-searcher__sync_sources with action='sync' to re-decompile and rebuild the index "
              + "(no restart needed), then action='diff' to see what changed."
            : "Run rimworld-searcher__sync_sources with action='check' for details.";

        return $"\n\n---\n**Note: the indexed sources changed since this session started.** "
             + $"{pending.Describe()}. Results above may be stale. {action}";
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
                if (token.Length >= 3) _askedAbout.Add(token);
            }
        }
    }

    private bool IsRelevant(PendingSourceUpdate pending)
    {
        // 还没问过任何具体东西时不打扰
        if (_askedAbout.Count == 0) return false;

        // 主判据：把会话问过的名字反查回文件，看它们是否落在变更的源里。
        // 变的是某个 mod 而这个会话一直在看 vanilla 时，这一步就把提示挡掉了。
        var indexer = SourceWatcher.Indexer;
        if (indexer != null && pending.ChangedRoots.Count > 0)
        {
            foreach (var term in _askedAbout)
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
            foreach (var term in _askedAbout)
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
