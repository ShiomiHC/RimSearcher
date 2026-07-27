using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RimSearcher.Core;

// 一个候选程序集。Sha256 惰性填充：全量哈希 1145 个 dll 实测 4.7 秒，
// 故先用 Length+LastWrite 预筛，只对疑似变动项算内容哈希。
public sealed record AssemblyEntry
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required long LastWriteUtcTicks { get; init; }

    // 路径中推断出的游戏版本目录（"1.6" 等）；null 表示不在版本目录下
    public string? GameVersion { get; init; }

    public string? Sha256 { get; init; }

    // 同一路径下「大小 + 修改时间」都没变就认为内容没变，省掉一次全文件哈希
    public string QuickDigest => $"{Length}|{LastWriteUtcTicks}";
}

public readonly record struct AssemblyReferenceInfo(string Name, string Version);

public sealed record AssemblyMetadata
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required ImmutableArray<AssemblyReferenceInfo> References { get; init; }
}

public static class AssemblyScanner
{
    // RimWorld mod 的多版本布局是 ModRoot/1.6/Assemblies/*.dll，游戏只加载当前版本那一份，
    // 其余都是历史死代码（实测 1145 个 dll 中 678 个属于 1.0–1.5）。
    private static readonly Regex VersionDirPattern =
        new(@"[\\/](1\.[0-9]+)[\\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 运行时与引擎程序集：反编译它们既无意义又会让产物膨胀十倍。
    // 前缀匹配，覆盖 UnityEngine.*Module.dll / System.*.dll 这类族。
    private static readonly string[] ExcludedPrefixes =
    [
        "mscorlib", "netstandard", "System", "Microsoft.",
        "UnityEngine", "Unity.", "Mono.", "I18N",
        "Newtonsoft.Json", "websocket-sharp"
    ];

    public static bool IsRuntimeAssembly(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        foreach (var prefix in ExcludedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // gameVersion 为 null 时不做版本过滤（全部保留）
    public static List<AssemblyEntry> Enumerate(
        IEnumerable<string> roots,
        string? gameVersion,
        bool includeRuntimeAssemblies = false)
    {
        var results = new List<AssemblyEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories);
            }
            catch { continue; }

            foreach (var file in files)
            {
                if (!seenPaths.Add(file)) continue;
                if (!includeRuntimeAssemblies && IsRuntimeAssembly(file)) continue;

                var versionDir = ExtractGameVersion(file);
                if (gameVersion != null
                    && versionDir != null
                    && !string.Equals(versionDir, gameVersion, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    results.Add(new AssemblyEntry
                    {
                        Path = info.FullName,
                        Length = info.Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                        GameVersion = versionDir
                    });
                }
                catch { }
            }
        }

        results.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return results;
    }

    // 取最后一个匹配段：mod 根自身若带 "1.x" 目录名会误伤，但版本目录总在更深处
    public static string? ExtractGameVersion(string path)
    {
        var matches = VersionDirPattern.Matches(path);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // 只对 previous 中缺失或快速指纹已变的条目计算 sha256，其余沿用旧值
    public static List<AssemblyEntry> FillHashes(
        IReadOnlyList<AssemblyEntry> entries,
        IReadOnlyDictionary<string, AssemblyEntry>? previous = null)
    {
        var results = new List<AssemblyEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (previous != null
                && previous.TryGetValue(entry.Path, out var old)
                && old.Sha256 != null
                && old.QuickDigest == entry.QuickDigest)
            {
                results.Add(entry with { Sha256 = old.Sha256 });
                continue;
            }

            try
            {
                results.Add(entry with { Sha256 = ComputeSha256(entry.Path) });
            }
            catch
            {
                results.Add(entry);
            }
        }

        return results;
    }

    // 反编译只需要编译期类型解析，而 AssemblyRef 恰好就是编译期确定的引用集合，
    // 比 About.xml 的 modDependencies（含纯 XML patch 依赖）更贴合这个用途。
    public static AssemblyMetadata? ReadMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return null;

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly) return null;

            var definition = reader.GetAssemblyDefinition();
            var references = ImmutableArray.CreateBuilder<AssemblyReferenceInfo>();

            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                references.Add(new AssemblyReferenceInfo(
                    reader.GetString(reference.Name),
                    reference.Version?.ToString() ?? string.Empty));
            }

            return new AssemblyMetadata
            {
                Name = reader.GetString(definition.Name),
                Version = definition.Version?.ToString() ?? string.Empty,
                References = references.ToImmutable()
            };
        }
        catch
        {
            return null;
        }
    }

    // 整个候选集合的指纹，用于「有没有任何程序集变过」这一个判断。
    // 与 IndexCacheService.ComputeConfigFingerprint 刻意保持独立：那个决定进程身份，这个只描述内容。
    public static string ComputeCatalogDigest(IReadOnlyList<AssemblyEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Path).Append('|')
                   .Append(entry.Sha256 ?? entry.QuickDigest).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
