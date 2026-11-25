using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Metadata;
using RimSearcher.Snapshot;
using RimTooling.IO;

namespace RimSearcher.Sources;

public sealed record SourceTreePlan
{
    public required string Name { get; init; }

    public required string PackageId { get; init; }

    public required string Root { get; init; }

    public required IReadOnlyList<string> Assemblies { get; init; }
}

public static class SourcePlanner
{
    public const string VanillaTree = "vanilla";

    public const string UnityTree = "unitystubs";

    public const string EngineTree = "waylith";

    public const string CoreLibTree = "corelib";

    public const string LibraryTree = "gamelibs";

    private enum NameReading { Default, OnMiss, LastResort }

    private sealed record GameDirTree(string Name, string Identity, string Missing, NameReading Reading,
                                      Func<GameLayout, List<string>> Assemblies);

    private static readonly GameDirTree[] GameDirTable =
    [
        new(UnityTree, "游戏用来替代 Unity 的程序集", "没有 UnityStubs.dll", NameReading.Default,
            l => l.UnityStubsPath is { } p ? [p] : []),
        new(EngineTree, "游戏引擎", "没有 Waylith*.dll 与 Brutal.*.dll", NameReading.OnMiss,
            l => l.EnginePaths()),
        new(CoreLibTree, "游戏 .NET 运行时的核心库", "没有 System.Private.CoreLib.dll", NameReading.LastResort,
            l => l.CoreLibPath is { } p ? [p] : []),
        new(LibraryTree, "游戏随附的其他库", "没有其他托管程序集", NameReading.LastResort,
            l => l.LibraryPaths()),
    ];

    public static readonly IReadOnlyList<string> GameDirTrees = [.. GameDirTable.Select(t => t.Name)];

    public static readonly IReadOnlyList<string> LastResortTrees =
        [.. GameDirTable.Where(t => t.Reading == NameReading.LastResort).Select(t => t.Name)];

    public const int NameTiers = 3;

    private const string LudeonPrefix = "ludeon.rimworld";

    private static GameDirTree? Find(string name)
        => GameDirTable.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsReserved(string name)
        => string.Equals(name, VanillaTree, StringComparison.OrdinalIgnoreCase) || IsGameDirTree(name);

    public static bool IsGameDirTree(string name) => Find(name) is not null;

    public static bool IsSearchedOnlyWhenNamed(string name) => IsGameDirTree(name);

    public static int NameTier(string name) => (int)(Find(name)?.Reading ?? NameReading.Default);

    public static bool IsLastResort(string name) => Find(name)?.Reading == NameReading.LastResort;

    public static string Describe(string tree) => Find(tree)?.Identity ?? "";

    public static string Listed(IEnumerable<string> trees, bool describe = false)
    {
        var items = trees.Select(t => describe ? $"{t}（{Describe(t)}）" : t).ToList();
        return items.Count <= 1 ? string.Concat(items)
             : string.Join("、", items.Take(items.Count - 1)) + (items[^2].EndsWith('）') ? "和 " : " 和 ") + items[^1];
    }

    public static string NormalizeGameVersion(string raw)
    {
        var space = raw.IndexOf(' ');
        return (space < 0 ? raw : raw[..space]).Trim();
    }

    public static bool IsVanilla(string packageId)
        => packageId.StartsWith(LudeonPrefix, StringComparison.OrdinalIgnoreCase);

    public static List<SourceTreePlan> Plan(
        RimConfig config,
        IReadOnlyList<string> packageIds,
        string gameVersion,
        IReadOnlyDictionary<string, InstalledMod> installed,
        out List<string> notInstalled)
        => Plan(config, packageIds, gameVersion, installed, out notInstalled, out _);

    public static List<SourceTreePlan> Plan(
        RimConfig config,
        IReadOnlyList<string> packageIds,
        string gameVersion,
        IReadOnlyDictionary<string, InstalledMod> installed,
        out List<string> notInstalled,
        out List<string> reserved,
        IReadOnlyDictionary<string, IReadOnlyList<LoadedAssembly>>? loaded = null)
    {
        var version = NormalizeGameVersion(gameVersion);
        var active = ModFolders.NormalizeActive(packageIds);
        var plans = new List<SourceTreePlan>();
        notInstalled = [];
        reserved = [];

        if (packageIds.Any(IsVanilla) && config.GameDir is { Length: > 0 } gameDir
            && GameLayout.Of(gameDir).GameAssemblies() is { Count: > 0 } gameDlls)
            plans.Add(new SourceTreePlan
            {
                Name = VanillaTree,
                PackageId = LudeonPrefix,
                Root = Path.GetFullPath(gameDir),
                Assemblies = gameDlls,
            });

        foreach (var tree in GameDirTrees)
            if (GameDirAssemblies(config, tree) is { Count: > 0 } dlls)
                plans.Add(new SourceTreePlan
                {
                    Name = tree,
                    PackageId = tree,
                    Root = Path.GetFullPath(config.GameDir!),
                    Assemblies = dlls,
                });

        foreach (var id in packageIds)
        {
            if (IsVanilla(id)) continue;
            if (Contract.IntermediateFormat.IsExporterPackageId(id))
                continue;
            if (IsReserved(id)) { reserved.Add(id); continue; }

            if (!installed.TryGetValue(id, out var mod)) { notInstalled.Add(id); continue; }

            var dlls = ModFolders.Assemblies(mod.Directory, version, active)
                .Where(d => !AssemblyFilter.IsRuntimeAssemblyFile(d)).ToList();
            if (loaded is not null && loaded.TryGetValue(id, out var recorded))
            {
                dlls.AddRange(LoadedElsewhere(mod.Directory, dlls, recorded));
                if (config.DecompiledDir is { Length: > 0 } decompiled)
                    dlls.AddRange(EmbeddedPayloads.Extract(dlls.ToList(), Unplanned(dlls, recorded),
                                                           EmbeddedPayloads.Directory(decompiled)));
                dlls.Sort(StringComparer.OrdinalIgnoreCase);
            }
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

    public static List<string> LoadedElsewhere(string modDir, IReadOnlyList<string> planned, IReadOnlyList<LoadedAssembly> recorded)
    {
        var wanted = Unplanned(planned, recorded);
        if (wanted.Count == 0) return [];

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories)
                             .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || AssemblyFilter.HasPeSignature(f))
                             .ToList();
        }
        catch { return []; }

        return files.Select(f => (Path: Path.GetFullPath(f), Mvid: CallGraphFreshness.Mvid(f)))
                    .Where(t => t.Mvid is { } m && wanted.Contains(m))
                    .GroupBy(t => t.Mvid)
                    .Select(g => g.Select(t => t.Path).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).First())
                    .ToList();
    }

    private static HashSet<Guid> Unplanned(IReadOnlyList<string> planned, IReadOnlyList<LoadedAssembly> recorded)
    {
        var have = planned.Select(AssemblyFilter.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return recorded.Where(a => !have.Contains(a.Name) && !AssemblyFilter.IsRuntimeAssembly(a.Name + ".dll"))
                       .Select(a => a.Mvid).ToHashSet();
    }

    public static List<string> LooseAssemblies(string modDir, string gameVersion, IReadOnlyList<string> packageIds)
    {
        var folders = ModFolders.LoadFolders(modDir, NormalizeGameVersion(gameVersion), ModFolders.NormalizeActive(packageIds));
        var assemblyDirs = folders.Select(f => Path.Combine(f, "Assemblies")).ToList();
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).ToList(); }
            catch { continue; }
            foreach (var file in files.Where(f => !AssemblyFilter.IsRuntimeAssemblyFile(f)))
            {
                var full = Path.GetFullPath(file);
                if (assemblyDirs.Any(d => PathIdentity.IsUnder(full, d))) continue;
                result.Add(Path.GetRelativePath(modDir, full).Replace('\\', '/'));
            }
        }
        return [.. result];
    }

    private static List<string> GameDirAssemblies(RimConfig config, string tree)
        => config.GameDir is { Length: > 0 } gameDir && Find(tree) is { } t ? t.Assemblies(GameLayout.Of(gameDir)) : [];

    public const string GameDirNotSet = "未设 game_dir";
    public const string NotInGameDir = "不在 game_dir";

    public static string? GameDirUnplanned(RimConfig config, string tree)
        => config.GameDir is not { Length: > 0 } ? GameDirNotSet
         : GameDirAssemblies(config, tree).Count == 0 ? NotInGameDir
         : null;

    public static string GameDirSource(string tree) => Find(tree)?.Missing ?? "";

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static SourceTreeState Manifest(SourceTreePlan plan, string gameVersion) => new()
    {
        PackageId = plan.PackageId,
        GameVersion = IsGameDirTree(plan.Name) ? "" : NormalizeGameVersion(gameVersion),
        Root = plan.Root,
        Assemblies = plan.Assemblies
            .Select(a => new SourceAssembly
            {
                Path = Relative(plan.Root, a),
                Sha256 = SafeHash(a),
                Language = SafeLanguage(a),
            })
            .ToList(),
        Decompiler = Sources.Decompiler.Identity,
    };

    private static string? SafeLanguage(string path)
    {
        try { return LanguageLevel.Render(LanguageLevel.Of(path)); }
        catch { return null; }
    }

    private static string SafeHash(string path)
    {
        try { return AssemblyFilter.Sha256(path); }
        catch { return ""; }
    }
}
