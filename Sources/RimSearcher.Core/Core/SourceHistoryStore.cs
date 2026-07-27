using System.Security.Cryptography;
using System.Text.Json;

namespace RimSearcher.Core;

public enum FileChangeKind { Added, Modified, Removed }

public readonly record struct FileChange(string RelativePath, FileChangeKind Kind);

public sealed record SourceChangeSet
{
    public required string SourceName { get; init; }
    public required IReadOnlyList<FileChange> Changes { get; init; }

    public int Added => Changes.Count(c => c.Kind == FileChangeKind.Added);
    public int Modified => Changes.Count(c => c.Kind == FileChangeKind.Modified);
    public int Removed => Changes.Count(c => c.Kind == FileChangeKind.Removed);
    public bool Any => Changes.Count > 0;
}

public sealed record HistoryVersion
{
    public required string Id { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Removed { get; init; }
    public long ArchivedBytes { get; init; }
}

public sealed record HistoryIndex
{
    public List<HistoryVersion> Versions { get; init; } = new();
}

// 反向增量历史：每个版本目录只存「本次同步中被覆盖或删除掉的旧文件」，而非完整快照。
// 一次 RimWorld 更新通常只动 5–20% 的文件，故稳态占用远低于留整份副本。
public sealed class SourceHistoryStore
{
    // 单侧文件的读取上限，量级取自 RoslynHelper 对源文件的同类限制。diff 要把新旧两份
    // 内容整份读进内存再逐行比，没有上限时一个被指向的巨大文件就能把进程拖死。
    public const long MaxComparableFileSize = 10 * 1024 * 1024;

    private const string IndexFileName = "index.json";
    private const string HashesFileName = "hashes.json";
    private const string FilesDirectoryName = "files";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Core 不认识 Server 的 ServerLogger，又不该为了几条降级提示反向依赖它。
    // 留个钩子由宿主进程接上（见 Program.cs）；没接就等同于原来的静默。
    public static Action<string, string>? OnDiagnostic;

    private static void Warn(string message) => OnDiagnostic?.Invoke(message, "warning");

    private readonly string _root;
    private readonly int _depth;

    public SourceHistoryStore(string cacheDirectory, int depth)
    {
        _root = Path.Combine(cacheDirectory, "history");
        _depth = Math.Max(0, depth);
    }

    public bool Enabled => _depth > 0;

    // 归档旧文件并轮转，返回本次变更摘要（depth=0 时只算摘要不落盘）。
    // 两个路径就是「旧的那棵树」和「新的那棵树」，归档的是旧树里被改写/删除的文件。
    // 调用方可以在转正前传 (源码目录, 暂存区)，也可以在转正后传 (留底目录, 源码目录)；
    // SourceSyncService 选的是后者——转正失败要回滚，先归档就会在历史里留下一版
    // 根本没发生过的同步。
    public SourceChangeSet Capture(string sourceName, string sourcePath, string stagingPath)
    {
        var current = HashDirectory(sourcePath);
        var staged = HashDirectory(stagingPath);
        var changes = Compare(current, staged);

        if (_depth == 0 || changes.Count == 0)
            return new SourceChangeSet { SourceName = sourceName, Changes = changes };

        try
        {
            var sourceRoot = GetSourceRoot(sourceName);
            var index = LoadIndex(sourceName);
            var versionId = NextVersionId(index);
            var versionDirectory = Path.Combine(sourceRoot, versionId);
            var filesDirectory = Path.Combine(versionDirectory, FilesDirectoryName);
            Directory.CreateDirectory(filesDirectory);

            long archivedBytes = 0;

            // 只归档「被改写」和「被删除」的旧文件；新增文件在旧版本里本就不存在
            foreach (var change in changes)
            {
                if (change.Kind == FileChangeKind.Added) continue;

                var origin = Path.Combine(sourcePath, change.RelativePath);
                if (!File.Exists(origin)) continue;

                var destination = Path.Combine(filesDirectory, change.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

                File.Copy(origin, destination, overwrite: true);
                archivedBytes += new FileInfo(destination).Length;
            }

            File.WriteAllText(Path.Combine(versionDirectory, HashesFileName),
                JsonSerializer.Serialize(current, JsonOptions));

            index.Versions.Add(new HistoryVersion
            {
                Id = versionId,
                CapturedAtUtc = DateTime.UtcNow,
                Added = changes.Count(c => c.Kind == FileChangeKind.Added),
                Modified = changes.Count(c => c.Kind == FileChangeKind.Modified),
                Removed = changes.Count(c => c.Kind == FileChangeKind.Removed),
                ArchivedBytes = archivedBytes
            });

            Rotate(sourceRoot, index);
            SaveIndex(sourceName, index);
        }
        catch
        {
            // 历史是辅助功能，落盘失败不该让同步本身失败
        }

        return new SourceChangeSet { SourceName = sourceName, Changes = changes };
    }

    public IReadOnlyList<HistoryVersion> ListVersions(string sourceName)
        => LoadIndex(sourceName).Versions;

    // 某个历史版本与当前磁盘状态的差异。versionId 省略时取最近一版。
    public SourceChangeSet? DiffAgainst(string sourceName, string sourcePath, string? versionId = null)
    {
        var index = LoadIndex(sourceName);
        if (index.Versions.Count == 0) return null;

        var version = versionId == null
            ? index.Versions[^1]
            : index.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        if (version == null) return null;

        var hashesPath = Path.Combine(GetSourceRoot(sourceName), version.Id, HashesFileName);
        if (!File.Exists(hashesPath)) return null;

        try
        {
            var old = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(hashesPath), JsonOptions) ?? new();

            return new SourceChangeSet
            {
                SourceName = sourceName,
                Changes = Compare(old, HashDirectory(sourcePath))
            };
        }
        catch
        {
            return null;
        }
    }

    // 归档文件应当所在的绝对路径；versionId 或 relativePath 会穿出历史根时返回 null。
    // 单独暴露是为了让调用方在读之前能自己判存在性和大小——ReadArchived 把这些都吞成 null，
    // 分不清「没这个文件」和「路径非法」，而这两者该给出完全不同的提示。
    public string? ResolveArchivedPath(string sourceName, string versionId, string relativePath)
    {
        // versionId 同样是外部输入且参与拼接，不校验的话它自己就是一条穿越通道
        var versionDirectory = PathSecurity.ResolveInsideRoot(GetSourceRoot(sourceName), versionId);
        if (versionDirectory == null) return null;

        return PathSecurity.ResolveInsideRoot(Path.Combine(versionDirectory, FilesDirectoryName), relativePath);
    }

    // 归档里那一版的文件内容，供调用方与当前文件做行级比较
    public string? ReadArchived(string sourceName, string versionId, string relativePath)
    {
        try
        {
            var path = ResolveArchivedPath(sourceName, versionId, relativePath);
            if (path == null || !File.Exists(path)) return null;

            // 整份内容要进内存再送进 diff，异常大的文件足以把进程顶爆。
            // 调用方应先用 ResolveArchivedPath 自查大小并给出明确错误，这里只是最后一道闸。
            return new FileInfo(path).Length > MaxComparableFileSize ? null : File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    private static List<FileChange> Compare(
        Dictionary<string, string> before,
        Dictionary<string, string> after)
    {
        var changes = new List<FileChange>();

        foreach (var (path, hash) in after)
        {
            if (!before.TryGetValue(path, out var oldHash))
                changes.Add(new FileChange(path, FileChangeKind.Added));
            else if (!string.Equals(oldHash, hash, StringComparison.OrdinalIgnoreCase))
                changes.Add(new FileChange(path, FileChangeKind.Modified));
        }

        foreach (var path in before.Keys)
        {
            if (!after.ContainsKey(path)) changes.Add(new FileChange(path, FileChangeKind.Removed));
        }

        changes.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return changes;
    }

    // 反编译产物的 mtime 每次都是新的，快速指纹无效，只能算内容哈希
    private static Dictionary<string, string> HashDirectory(string root)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return results;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);
        }
        catch
        {
            return results;
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                results[Path.GetRelativePath(root, file)] = hash;
            }
            catch (Exception ex)
            {
                // 读不到的文件在哈希表里缺席，下次 diff 会把它当成「新增」——是噪音不是丢数据
                Warn($"SourceHistory: could not hash '{file}' | reason={ex.Message}");
            }
        }

        return results;
    }

    private void Rotate(string sourceRoot, HistoryIndex index)
    {
        while (index.Versions.Count > _depth)
        {
            var oldest = index.Versions[0];
            index.Versions.RemoveAt(0);

            try
            {
                var directory = Path.Combine(sourceRoot, oldest.Id);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                // 索引里已经把它移出去了，磁盘上却还在——history 目录会无声地持续膨胀
                Warn($"SourceHistory: could not delete rotated version '{oldest.Id}' | reason={ex.Message}");
            }
        }
    }

    private static string NextVersionId(HistoryIndex index)
    {
        var maximum = 0;
        foreach (var version in index.Versions)
        {
            if (version.Id.Length > 1 && int.TryParse(version.Id[1..], out var n) && n > maximum) maximum = n;
        }
        return $"v{maximum + 1:D4}";
    }

    // 源名直接做目录名不安全（可含 / 或 :），用不可逆但稳定的短哈希兜底
    private string GetSourceRoot(string sourceName)
    {
        var safe = string.Join("_", sourceName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe)) safe = "unnamed";
        return Path.Combine(_root, safe);
    }

    private HistoryIndex LoadIndex(string sourceName)
    {
        try
        {
            var path = Path.Combine(GetSourceRoot(sourceName), IndexFileName);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<HistoryIndex>(File.ReadAllText(path), JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // 空索引意味着已归档的那几代版本从此不可见，磁盘上却还占着地方
            Warn($"SourceHistory: could not read history index for '{sourceName}' | reason={ex.Message}");
        }

        return new HistoryIndex();
    }

    private void SaveIndex(string sourceName, HistoryIndex index)
    {
        try
        {
            var root = GetSourceRoot(sourceName);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, IndexFileName), JsonSerializer.Serialize(index, JsonOptions));
        }
        catch (Exception ex)
        {
            // 版本目录写好了但索引没更新，等于这一代白存——diff 拿不到它
            Warn($"SourceHistory: could not persist history index for '{sourceName}' | reason={ex.Message}");
        }
    }
}
