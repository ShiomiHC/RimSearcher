using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RimSearcher.Core;

// 一段「加载条件未被判定」的路径：mod 的某个内容目录（`1.6/CE/Patches`），或它的程序集
// 反编译出来的那棵源码树（`Decompiled/Cinders/EmbergardenCE`）。
//
// Folder 取 loadFolders.xml 里那条 li 的写法（`1.6/CE`），而不是绝对路径：它同时是返回里
// 那个行内标记的取值，标记与脚注必须共用同一个键，否则读者拿着标记到脚注里对不上号
// ——第九轮把「同现」升格成「可指认」正是为了这件事（F33 规则甲）。
//
// Condition 是给人读的那一形（`CETeam.CombatExtended active`），不是 packageId 表达式：
// 这一行最终落在返回文本里，读它的是调用方而不是解析器。
public sealed record ConditionalArea(string Path, string Folder, string Condition, string Source = "")
{
    // 脚注里的一行。Source 为空（Core 侧还没贴源名）时只给目录。
    public string Describe()
        => Source.Length == 0
            ? $"`{Folder}` needs {Condition}"
            : $"`{Folder}` [{Source}] needs {Condition}";

    // 去重键。同一个 Folder 名在两个 mod 里都可能出现（`1.6/Mods/Royalty` 有五个源都有），
    // 故要带上源名——但**不带 Path**：loadFolders.xml 里的一条 li 会展开成 Defs / Patches /
    // Assemblies 好几条 area，路径各不相同，而说给调用方听的是同一句话。带上 Path 的话，
    // 一次 Cinders 的搜索会把「`1.6/CE` [Cinders] needs CETeam.CombatExtended active」
    // 在脚注里原样印两遍——真语料上就是这么印的。键取「呈现成同一句话」这个等价类。
    public string Key => $"{Source} {Folder} {Condition}";
}

// 路径 → 条件区域的前缀查表。
//
// 判据与 ScopeCatalog.ResolveSourceIndex 同源（最长前缀胜出、按路径缓存），只是问的问题不同：
// 那边问「这个文件属于哪个源」，这边问「它的加载条件判过没有」。两者都被逐行调用，故都缓存。
//
// 注意收进来的只有**没判过条件**的目录：config 里给了 active_mods 的源，条件已经按那份白名单
// 判定过了，此时再打标就是把一个已经有答案的问题重新说成悬案。
public sealed class ConditionalFolders
{
    private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly ConcurrentDictionary<string, ConditionalArea?> _byPath = new(PathComparer);

    public static readonly ConditionalFolders None = new([]);

    private ConditionalFolders(IReadOnlyList<ConditionalArea> areas) => Areas = areas;

    public IReadOnlyList<ConditionalArea> Areas { get; }

    public bool IsEmpty => Areas.Count == 0;

    public static ConditionalFolders Build(IEnumerable<ConditionalArea>? areas)
    {
        if (areas == null) return None;

        var normalized = new List<ConditionalArea>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var area in areas)
        {
            var path = NormalizeRoot(area.Path);
            if (path == null || !seen.Add(path)) continue;
            normalized.Add(area with { Path = path });
        }

        return normalized.Count == 0 ? None : new ConditionalFolders(normalized);
    }

    public ConditionalArea? Of(string? filePath)
    {
        if (Areas.Count == 0 || string.IsNullOrEmpty(filePath)) return null;

        return _byPath.GetOrAdd(filePath, path =>
        {
            var normalized = NormalizeRoot(path);
            if (normalized == null) return null;

            ConditionalArea? best = null;
            foreach (var area in Areas)
            {
                if (best != null && area.Path.Length <= best.Path.Length) continue;
                if (!IsUnderRoot(normalized, area.Path)) continue;
                best = area;
            }

            return best;
        });
    }

    // 一个符号散在多份文件里时（同名类型的两份声明）只有**全部**落在条件区里才算条件性存在：
    // 有一份是无条件的，那这个符号在任何实机上都在，打标反而是假警报。
    // 命名取第一条命中的区域——多条时它们的成因未必相同，而行内标记只放得下一个键，
    // 脚注里那几行会把参与的区域都列出来。
    public ConditionalArea? OfAll(IEnumerable<string>? paths)
    {
        if (Areas.Count == 0 || paths == null) return null;

        ConditionalArea? first = null;
        var any = false;

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            any = true;

            var area = Of(path);
            if (area == null) return null;
            first ??= area;
        }

        return any ? first : null;
    }

    private static bool IsUnderRoot(string normalizedPath, string root)
    {
        if (!normalizedPath.StartsWith(root, PathComparison)) return false;
        if (normalizedPath.Length == root.Length) return true;

        var separator = normalizedPath[root.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static string? NormalizeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return full;
        }
        catch
        {
            return null;
        }
    }
}
