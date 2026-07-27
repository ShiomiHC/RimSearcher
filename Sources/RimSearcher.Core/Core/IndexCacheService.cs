using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimSearcher.Core;

public static class IndexCacheService
{
    // 缓存结构版本号（2 = def 索引一名多值；3 = inheritors 收全部直接超类型，含接口；
    // 4 = 快照多带一份 DefInjected 译文）。
    // 字段形状没变但内容语义变了：旧缓存里的 InheritorsMap 只有基类型列表第一项，
    // 直接复用会让「按接口查实现」继续返回空，故必须让旧缓存失效重建。
    public const int SchemaVersion = 4;

    private const string ManifestFileName = "manifest.json";
    private const string IndexFileName = "index.bin";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetDefaultCacheDirectory()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".cache", "index");
    }

    public static bool EnsureCacheDirectory(string cacheDirectory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // includeContentDigest：把各根目录下源文件的「大小 + 修改时间」也纳入指纹。
    // 源指向 Steam workshop 时，mod 更新不改路径集合，纯路径指纹会让陈旧索引一直命中且毫无提示。
    // 只 stat 不读内容，故成本是几万次元数据枚举（约 100~300ms），远低于内容哈希要读的几百 MB。
    // excludedPaths：mod 展开时被遮蔽、故不进索引的文件。它随 loadFolders.xml 与版本目录的
    // 增删而变，而这两者都不改路径集合——不入指纹的话，换了游戏版本后那份按旧规则建的索引
    // 会继续命中。
    // localizationPaths：本轮实际选中的语言包（目录或 tar）。必须进内容摘要——汉化包更新既不改
    // 路径集合也不动 Defs，不纳入的话磁盘上那份带旧译文的索引会一直命中且毫无提示。
    //
    // localizationVariant：从同一批语言包里取了哪些字段。它决定快照的内容而不只是显示方式——
    // 只收 label 的那次建出来的快照里根本没有 description，不区分的话，把
    // localization_description 从 false 改成 true 会命中那份缓存，于是描述永远不出现。
    public static string ComputeConfigFingerprint(
        IEnumerable<string> csharpPaths,
        IEnumerable<string> xmlPaths,
        bool includeContentDigest = true,
        IEnumerable<string>? excludedPaths = null,
        IEnumerable<string>? localizationPaths = null,
        string? localizationVariant = null)
    {
        var normalizedCsharp = NormalizePaths(csharpPaths);
        var normalizedXml = NormalizePaths(xmlPaths);
        var normalizedLocalization = localizationPaths == null ? [] : NormalizePaths(localizationPaths);

        var builder = new StringBuilder();
        builder.AppendLine($"schema:{SchemaVersion}");
        builder.AppendLine("[csharp]");
        foreach (var path in normalizedCsharp)
        {
            builder.AppendLine(path);
        }

        builder.AppendLine("[xml]");
        foreach (var path in normalizedXml)
        {
            builder.AppendLine(path);
        }

        if (excludedPaths != null)
        {
            var normalizedExcluded = NormalizePaths(excludedPaths);
            if (normalizedExcluded.Count > 0)
            {
                builder.AppendLine("[excluded]");
                foreach (var path in normalizedExcluded)
                {
                    builder.AppendLine(path);
                }
            }
        }

        if (normalizedLocalization.Count > 0 || !string.IsNullOrEmpty(localizationVariant))
        {
            builder.AppendLine("[localization]");

            if (!string.IsNullOrEmpty(localizationVariant))
                builder.AppendLine($"variant:{localizationVariant}");

            foreach (var path in normalizedLocalization)
            {
                builder.AppendLine(path);
            }
        }

        if (includeContentDigest)
        {
            builder.AppendLine("[content]");
            builder.AppendLine(ComputeContentDigest(
                normalizedCsharp.Concat(normalizedXml).Concat(normalizedLocalization)));
        }

        return $"sha256:{ComputeSha256(Encoding.UTF8.GetBytes(builder.ToString()))}";
    }

    private static readonly HashSet<string> DigestBlacklistedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".idea", ".build", "temp" };

    private static string ComputeContentDigest(IEnumerable<string> roots)
    {
        var perRoot = new List<string>();

        // 各根独立摘要后再合并：单个根内部要有序才稳定，根之间的顺序由上游已排序的路径列表定
        foreach (var root in roots)
        {
            // 语言包可以是单个 tar 文件而不是目录（本体的官方语言就是），故 root 也允许是文件
            if (File.Exists(root))
            {
                try
                {
                    var file = new FileInfo(root);
                    perRoot.Add($"{root}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                }
                catch
                {
                    perRoot.Add($"{root}|unreadable");
                }

                continue;
            }

            if (!Directory.Exists(root))
            {
                perRoot.Add($"{root}|missing");
                continue;
            }

            var entries = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                try
                {
                    var directory = new DirectoryInfo(current);
                    foreach (var file in directory.EnumerateFiles())
                    {
                        if (!file.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            && !file.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                            continue;

                        entries.Add($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                    }

                    foreach (var sub in directory.EnumerateDirectories())
                    {
                        if (!DigestBlacklistedDirs.Contains(sub.Name)) stack.Push(sub.FullName);
                    }
                }
                catch { }
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            var rootDigest = ComputeSha256(Encoding.UTF8.GetBytes(string.Join("\n", entries)));
            perRoot.Add($"{root}|{entries.Count}|{rootDigest}");
        }

        return ComputeSha256(Encoding.UTF8.GetBytes(string.Join("\n", perRoot)));
    }

    public static (bool Success, string Reason, IndexCacheSnapshot? Snapshot, IndexCacheManifest? Manifest) TryLoad(
        string cacheDirectory,
        string expectedConfigFingerprint)
    {
        try
        {
            var manifestPath = Path.Combine(cacheDirectory, ManifestFileName);
            if (!File.Exists(manifestPath))
                return (false, "manifest missing", null, null);

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<IndexCacheManifest>(manifestJson, ManifestJsonOptions);
            if (manifest == null)
                return (false, "manifest parse failed", null, null);

            if (manifest.SchemaVersion != SchemaVersion)
                return (false, $"schema mismatch (expected {SchemaVersion}, got {manifest.SchemaVersion})", null, manifest);

            if (!string.Equals(manifest.ConfigFingerprint, expectedConfigFingerprint, StringComparison.Ordinal))
                return (false, "config fingerprint mismatch", null, manifest);

            var indexFile = string.IsNullOrWhiteSpace(manifest.IndexFile) ? IndexFileName : manifest.IndexFile;
            var indexPath = Path.Combine(cacheDirectory, indexFile);
            if (!File.Exists(indexPath))
                return (false, "index file missing", null, manifest);

            var compressedBytes = File.ReadAllBytes(indexPath);
            if (manifest.IndexFileSize > 0 && compressedBytes.LongLength != manifest.IndexFileSize)
                return (false, "index file size mismatch", null, manifest);

            if (!string.IsNullOrWhiteSpace(manifest.IndexFileSha256))
            {
                var actualHash = ComputeSha256(compressedBytes);
                if (!string.Equals(actualHash, manifest.IndexFileSha256, StringComparison.OrdinalIgnoreCase))
                    return (false, "index file hash mismatch", null, manifest);
            }

            var snapshotBytes = string.Equals(manifest.Compression, "gzip", StringComparison.OrdinalIgnoreCase)
                ? Decompress(compressedBytes)
                : compressedBytes;

            var snapshot = JsonSerializer.Deserialize<IndexCacheSnapshot>(snapshotBytes, SnapshotJsonOptions);
            if (snapshot == null)
                return (false, "snapshot parse failed", null, manifest);

            return (true, "cache loaded", snapshot, manifest);
        }
        catch (Exception ex)
        {
            return (false, $"cache load exception: {ex.Message}", null, null);
        }
    }

    public static (bool Success, string Reason, IndexCacheManifest? Manifest) Save(
        string cacheDirectory,
        string configFingerprint,
        IndexCacheSnapshot snapshot,
        TimeSpan buildDuration,
        int indexedCsharpFileCount,
        int indexedXmlFileCount)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);

            var snapshotJsonBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);
            var compressedBytes = Compress(snapshotJsonBytes);
            var compressedHash = ComputeSha256(compressedBytes);

            var manifest = new IndexCacheManifest
            {
                SchemaVersion = SchemaVersion,
                ConfigFingerprint = configFingerprint,
                IndexFile = IndexFileName,
                Compression = "gzip",
                IndexFileSize = compressedBytes.LongLength,
                IndexFileSha256 = compressedHash,
                BuiltAtUtc = DateTime.UtcNow,
                BuildDurationMs = (long)buildDuration.TotalMilliseconds,
                IndexedCsharpFileCount = indexedCsharpFileCount,
                IndexedXmlFileCount = indexedXmlFileCount
            };

            var indexPath = Path.Combine(cacheDirectory, IndexFileName);
            var manifestPath = Path.Combine(cacheDirectory, ManifestFileName);

            WriteBytesAtomic(indexPath, compressedBytes);
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            WriteTextAtomic(manifestPath, manifestJson);

            return (true, "cache saved", manifest);
        }
        catch (Exception ex)
        {
            return (false, $"cache save exception: {ex.Message}", null);
        }
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths)
    {
        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var set = new HashSet<string>(comparer);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            try
            {
                var full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                }
                set.Add(full);
            }
            catch
            {
            }
        }

        return set.OrderBy(x => x, comparer).ToList();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static void WriteBytesAtomic(string targetPath, byte[] bytes)
    {
        // 临时名带 PID：多实例并发保存时固定名会互相截断对方写入中的文件
        var tempPath = $"{targetPath}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        ReplaceFile(tempPath, targetPath);
    }

    private static void WriteTextAtomic(string targetPath, string content)
    {
        // 临时名带 PID：多实例并发保存时固定名会互相截断对方写入中的文件
        var tempPath = $"{targetPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tempPath, content);
        ReplaceFile(tempPath, targetPath);
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
            File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }
    }
}
