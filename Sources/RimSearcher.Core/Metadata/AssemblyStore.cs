using RimSearcher.Sources;
using RimTooling.IO;

namespace RimSearcher.Metadata;

public enum AssemblyOrigin
{
    Copy,

    Installed,
}

public sealed record ResolvedAssembly
{
    public required string Tree { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required AssemblyOrigin Origin { get; init; }

    public string? InstalledPath { get; init; }

    public bool? OriginalChanged => OriginalCheck?.Value;

    internal Lazy<bool>? OriginalCheck { get; init; }

    public bool OriginalMissing { get; init; }
}

public static class AssemblyStore
{
    public const string CopyDir = ".assemblies";

    public static readonly IReadOnlyList<string> IgnoreEntries = [CopyDir + "/", CallGraphStore.FileName];

    public static string CopyDirectory(string treeDir) => Path.Combine(treeDir, CopyDir);

    public static string CopyPath(string treeDir, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
            rel = Path.GetFileName(rel);
        return Path.Combine(CopyDirectory(treeDir), rel);
    }

    public static int CopyInto(string treeDir, string root, IEnumerable<string> assemblies)
    {
        Directory.CreateDirectory(CopyDirectory(treeDir));
        var n = 0;
        foreach (var dll in assemblies)
        {
            try
            {
                var target = CopyPath(treeDir, Relative(root, dll));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(dll, target, overwrite: true);
                n++;
            }
            catch
            {
            }
        }
        return n;
    }

    public static string Relative(string root, string assemblyPath)
    {
        try { return Path.GetRelativePath(root, assemblyPath); }
        catch { return Path.GetFileName(assemblyPath); }
    }

    public static void EnsureIgnored(string root)
        => EnsureIgnored(root, IgnoreEntries, "# 抄进来的来源程序集与它们的调用图 —— 可重建的派生物,不进历史。");

    public static void EnsureIgnored(string root, IReadOnlyList<string> entries, string comment)
    {
        var path = Path.Combine(root, ".gitignore");
        List<string> lines;
        try { lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : []; }
        catch { return; }

        var missing = entries.Where(e => !lines.Any(l => l.Trim() == e)).ToList();
        if (missing.Count == 0) return;

        try
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add(comment);
            lines.AddRange(missing);
            File.WriteAllLines(path, lines);
        }
        catch {  }
    }

    public static IReadOnlyList<ResolvedAssembly> Resolve(string treeDir, IReadOnlyCollection<string> installRoots)
    {
        var tree = Path.GetFileName(treeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        var state = SourceTreeState.Read(treeDir);
        var result = new List<ResolvedAssembly>();
        var reachable = state is not null && UnderAny(state.Root, installRoots);

        if (state is null)
        {
            foreach (var f in SafeFiles(CopyDirectory(treeDir)))
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = AssemblyFilter.AssemblyName(f),
                    Path = f,
                    Origin = AssemblyOrigin.Copy,
                });
            return result;
        }

        foreach (var entry in state.Assemblies)
        {
            var rel = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var installed = Path.GetFullPath(Path.Combine(state.Root, rel));

            var copy = CopyPath(treeDir, entry.Path);
            if (!File.Exists(copy))
                copy = Path.Combine(CopyDirectory(treeDir), Path.GetFileName(rel));

            if (File.Exists(copy))
            {
                var originalHere = File.Exists(installed);
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = AssemblyFilter.AssemblyName(copy),
                    Path = copy,
                    Origin = AssemblyOrigin.Copy,
                    InstalledPath = installed,
                    OriginalMissing = !originalHere && reachable,
                    OriginalCheck = originalHere ? new(() => !SameHash(installed, entry.Sha256)) : null,
                });
            }
            else if (File.Exists(installed))
            {
                result.Add(new ResolvedAssembly
                {
                    Tree = tree,
                    Name = AssemblyFilter.AssemblyName(installed),
                    Path = installed,
                    Origin = AssemblyOrigin.Installed,
                    InstalledPath = installed,
                    OriginalCheck = new(() => !SameHash(installed, entry.Sha256)),
                });
            }
        }

        return result;
    }

    public static IReadOnlyList<string> InstallRoots(RimSearcher.Config.RimConfig config)
        => [.. new[] { config.GameDir }.Concat(config.ModRoots).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!)];

    private static bool UnderAny(string path, IReadOnlyCollection<string> roots)
        => !string.IsNullOrWhiteSpace(path) && roots.Any(r => PathIdentity.IsUnder(path, r, includeSelf: true));

    public static IReadOnlyList<ResolvedAssembly> ResolveAll(
        string root, IReadOnlyCollection<string> installRoots, IReadOnlyCollection<string>? only = null)
    {
        var all = new List<ResolvedAssembly>();
        if (!Directory.Exists(root)) return all;

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir)!;
            if (name.Length == 0 || name.StartsWith('.')) continue;
            if (only is { Count: > 0 } && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            all.AddRange(Resolve(dir, installRoots));
        }
        return all;
    }

    private static bool SameHash(string path, string recorded)
    {
        if (recorded.Length == 0) return false;
        try { return string.Equals(AssemblyFilter.Sha256(path), recorded, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : []; }
        catch { return []; }
    }
}
