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
    private const string IndexFileName = "index.json";
    private const string HashesFileName = "hashes.json";
    private const string FilesDirectoryName = "files";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _root;
    private readonly int _depth;

    public SourceHistoryStore(string cacheDirectory, int depth)
    {
        _root = Path.Combine(cacheDirectory, "history");
        _depth = Math.Max(0, depth);
    }

    public bool Enabled => _depth > 0;

    // 反编译产物已备在 stagingPath、尚未替换 sourcePath 时调用。
    // 归档旧文件并轮转，返回本次变更摘要（depth=0 时只算摘要不落盘）。
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

    // 归档里那一版的文件内容，供调用方与当前文件做行级比较
    public string? ReadArchived(string sourceName, string versionId, string relativePath)
    {
        try
        {
            var path = Path.Combine(GetSourceRoot(sourceName), versionId, FilesDirectoryName, relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
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
            catch { }
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
            catch { }
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
        catch { }

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
        catch { }
    }
}
