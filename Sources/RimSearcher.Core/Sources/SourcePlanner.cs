using RimSearcher.Commands;
using RimSearcher.Config;

namespace RimSearcher.Sources;

/// <summary>要建(或已经建好)的一棵反编译树。</summary>
public sealed record SourceTreePlan
{
    /// <summary>树的目录名。</summary>
    public required string Name { get; init; }

    /// <summary>packageId。<see cref="Name"/> 与它只在 vanilla 那一棵上不同。</summary>
    public required string PackageId { get; init; }

    /// <summary>mod 根目录(vanilla 是游戏目录)。清单里的路径相对它。</summary>
    public required string Root { get; init; }

    /// <summary>游戏真会加载的 dll,绝对路径。</summary>
    public required IReadOnlyList<string> Assemblies { get; init; }
}

/// <summary>
/// 「该建哪些树」的答案来自**快照**(游戏亲自答的 mod 列表),不来自手写清单。
/// 树名就是 <c>rimsearcher mods</c> 第二列那个 packageId,也是 <c>--scope</c> 认的那个。
/// </summary>
public static class SourcePlanner
{
    /// <summary>游戏本体那棵树的名字。它不是某个 mod,五个 DLC 也与它共用同一批程序集。</summary>
    public const string VanillaTree = "vanilla";

    private const string LudeonPrefix = "ludeon.rimworld";

    /// <summary>游戏程序集所在(相对游戏目录)。</summary>
    private const string ManagedDir = "RimWorldWin64_Data/Managed";

    /// <summary>
    /// <c>1.6.4871 rev591</c> → <c>1.6.4871</c>。loadFolders 的版本比对用的是不带 rev 的那截
    /// (游戏侧 <c>VersionControl.CurrentVersionString</c>)。
    /// </summary>
    public static string NormalizeGameVersion(string raw)
    {
        var space = raw.IndexOf(' ');
        return (space < 0 ? raw : raw[..space]).Trim();
    }

    public static bool IsVanilla(string packageId)
        => packageId.StartsWith(LudeonPrefix, StringComparison.OrdinalIgnoreCase);

    public static string ManagedPath(string gameDir) => Path.Combine(gameDir, ManagedDir.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 快照里的 mod 列表 → 树计划。
    ///
    /// 三类被摘掉:
    ///   - Ludeon 系(本体与五个 DLC)合成一棵 <c>vanilla</c> —— 游戏代码就是一套程序集,DLC 只加数据;
    ///   - 导出器自己 —— 源码就在本仓;
    ///   - 一个 dll 都不加载的 mod(纯 XML mod)。
    /// </summary>
    public static List<SourceTreePlan> Plan(
        RimConfig config,
        IReadOnlyList<string> packageIds,
        string gameVersion,
        IReadOnlyDictionary<string, InstalledMod> installed,
        out List<string> notInstalled)
    {
        var version = NormalizeGameVersion(gameVersion);
        var active = ModFolders.NormalizeActive(packageIds);
        var plans = new List<SourceTreePlan>();
        notInstalled = [];

        if (packageIds.Any(IsVanilla) && config.GameDir is { Length: > 0 } gameDir)
        {
            var managed = ManagedPath(gameDir);
            if (Directory.Exists(managed))
            {
                var dlls = Directory.EnumerateFiles(managed, "*.dll", SearchOption.TopDirectoryOnly)
                                    .Where(f => !AssemblyFilter.IsRuntimeAssembly(f))
                                    .Select(Path.GetFullPath)
                                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                if (dlls.Count > 0)
                    plans.Add(new SourceTreePlan
                    {
                        Name = VanillaTree,
                        PackageId = LudeonPrefix,
                        Root = Path.GetFullPath(gameDir),
                        Assemblies = dlls,
                    });
            }
        }

        foreach (var id in packageIds)
        {
            if (IsVanilla(id)) continue;
            if (string.Equals(id, Contract.IntermediateFormat.ExporterPackageId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!installed.TryGetValue(id, out var mod)) { notInstalled.Add(id); continue; }

            var dlls = ModFolders.Assemblies(mod.Directory, version, active);
            if (dlls.Count == 0) continue;

            plans.Add(new SourceTreePlan
            {
                Name = id,
                PackageId = id,
                Root = Path.GetFullPath(mod.Directory),
                Assemblies = dlls,
            });
        }

        return plans;
    }

    /// <summary>
    /// 计划 → 清单。哈希在这里算,换来的是「没变就不重跑」。
    /// </summary>
    public static SourceTreeState Manifest(SourceTreePlan plan, string gameVersion) => new()
    {
        PackageId = plan.PackageId,
        GameVersion = NormalizeGameVersion(gameVersion),
        Root = plan.Root,
        Assemblies = plan.Assemblies
            .Select(a => new SourceAssembly
            {
                // 相对 mod 根。绝对路径会让库一搬家每棵树都变红,而那不是内容变化。
                Path = System.IO.Path.GetRelativePath(plan.Root, a).Replace('\\', '/'),
                Sha256 = SafeHash(a),
            })
            .ToList(),
    };

    private static string SafeHash(string path)
    {
        // 读不动一个 dll 不该让整次同步失败;空哈希只是让这棵树每次都被判成变了。
        try { return AssemblyFilter.Sha256(path); }
        catch { return ""; }
    }

    /// <summary>
    /// 类型解析的搜索目录。<paramref name="plan"/> 引用到的程序集在哪个目录里,就把那个目录加进来,
    /// 再无条件带上游戏的 Managed —— 每个 mod 都引用 Assembly-CSharp,少了它整棵树的类型全退化。
    /// </summary>
    public static List<string> ReferencePaths(
        SourceTreePlan plan, string assemblyPath, IReadOnlyList<SourceTreePlan> allPlans, RimConfig config)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (seen.Add(dir)) paths.Add(dir);
        }

        if (config.GameDir is { Length: > 0 } gameDir) Add(ManagedPath(gameDir));
        Add(Path.GetDirectoryName(assemblyPath));
        foreach (var sibling in plan.Assemblies) Add(Path.GetDirectoryName(sibling));

        var wanted = AssemblyFilter.References(assemblyPath);
        if (wanted.Count == 0) return paths;

        foreach (var other in allPlans)
            foreach (var dll in other.Assemblies)
                if (wanted.Contains(Path.GetFileNameWithoutExtension(dll)))
                    Add(Path.GetDirectoryName(dll));

        return paths;
    }
}
