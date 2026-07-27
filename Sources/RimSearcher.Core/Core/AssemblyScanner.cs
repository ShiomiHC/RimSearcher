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
    //
    // 判定分「精确名」与「点分家族」两档，不能一律 StartsWith：裸前缀 StartsWith 会把
    // SystematicWeapons.dll / UnityEngineTweaks.dll / I18NPlus.dll 这类正常 mod 程序集
    // 整批当成运行时库排掉，它们的源码永远进不了索引，而用户查不到还看不出原因。
    // 精确名只匹配整个程序集名；家族前缀带结尾的点，只吃 "Foo." 开头的真·子命名空间。

    // 逐条判断依据：
    //   mscorlib        只有 mscorlib.dll 这一个，没有 mscorlib.* 家族 → 精确
    //   netstandard     同上，只有 netstandard.dll → 精确
    //   System          System.dll 存在，System.Xml/System.Core/... 也存在 → 精确 + 家族
    //   UnityEngine     UnityEngine.dll 存在，UnityEngine.CoreModule 等模块化拆分也存在 → 精确 + 家族
    //   I18N            I18N.dll 存在（Mono 的字符集库），I18N.West/I18N.CJK 也存在 → 精确 + 家族
    //   Newtonsoft.Json Newtonsoft.Json.dll 是本体，Newtonsoft.Json.Bson 之类是同厂扩展；
    //                   这个命名空间不可能是 mod 名 → 精确 + 家族
    //   Microsoft.      没有裸 Microsoft.dll，只有 Microsoft.CSharp 这类 → 只家族
    //   Unity.          没有裸 Unity.dll（引擎本体叫 UnityEngine），只有 Unity.TextMeshPro 这类 → 只家族
    //   Mono.           没有裸 Mono.dll，只有 Mono.Security / Mono.Posix 这类 → 只家族
    //   websocket-sharp 游戏 Managed 下的单个第三方库，无家族 → 精确
    private static readonly string[] ExcludedExactNames =
    [
        "mscorlib", "netstandard", "System", "UnityEngine",
        "I18N", "Newtonsoft.Json", "websocket-sharp"
    ];

    private static readonly string[] ExcludedFamilyPrefixes =
    [
        "System.", "UnityEngine.", "I18N.", "Newtonsoft.Json.",
        "Microsoft.", "Unity.", "Mono."
    ];

    public static bool IsRuntimeAssembly(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

        foreach (var exact in ExcludedExactNames)
        {
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var family in ExcludedFamilyPrefixes)
        {
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // gameVersion 为 null 时不做版本过滤（全部保留）。
    // excludedPaths 是 mod 展开时算出的遮蔽集合：同相对路径的 dll 在高优先级文件夹里已有一份，
    // 这里的这份游戏不会加载。它比 gameVersion 那条路径正则准——loadFolders.xml 可以把内容
    // 放在 Common/ 或 1.6/Mods/Odyssey 这种正则匹配不到的地方。
    public static List<AssemblyEntry> Enumerate(
        IEnumerable<string> roots,
        string? gameVersion,
        bool includeRuntimeAssemblies = false,
        IReadOnlySet<string>? excludedPaths = null)
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
                if (excludedPaths != null && excludedPaths.Contains(System.IO.Path.GetFullPath(file))) continue;

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
