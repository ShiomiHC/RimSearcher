using RimTooling.RimWorld;

namespace RimSearcher.Sources;

public static class ModFolders
{
    public static List<string> LoadFolders(string rootDir, string gameVersion, IReadOnlySet<string> activeIds)
        => [.. ModLoadFolders.Resolve(rootDir, gameVersion, activeIds, existingOnly: true)];

    public static List<string> Assemblies(string rootDir, string gameVersion, IReadOnlySet<string> activeIds)
        => AssembliesIn(LoadFolders(rootDir, gameVersion, activeIds));

    public static List<string> AssembliesIn(IEnumerable<string> folders)
    {
        var byRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var dir = Path.Combine(folder, "Assemblies");
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                var key = Path.GetRelativePath(folder, file).Replace('\\', '/');
                byRelative.TryAdd(key, Path.GetFullPath(file));
            }
        }

        var result = byRelative.Values.Where(p => !AssemblyFilter.IsRuntimeAssembly(p)).ToList();
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static HashSet<string> NormalizeActive(IEnumerable<string> packageIds)
        => ModLoadFolders.NormalizeActiveIds(packageIds);
}
