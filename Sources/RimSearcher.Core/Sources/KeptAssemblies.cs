using System.Xml.Linq;
using RimSearcher.Metadata;

namespace RimSearcher.Sources;

public static class KeptAssemblies
{
    public static (List<SourceAssembly> Kept, List<(SourceAssembly Old, string By)> Replaced) Split(
        string treeDir, SourceTreeState existing, SourceTreeState planned)
    {
        var extras = existing.Kept(planned);
        if (extras.Count == 0) return ([], []);

        var claims = new TreeIdentity.Claims();
        foreach (var p in planned.Assemblies)
            claims.Claim(p.Path, AssemblyFilter.AssemblyName(Path.Combine(planned.Root, p.Path)));

        var kept = new List<SourceAssembly>();
        var replaced = new List<(SourceAssembly, string)>();
        foreach (var k in extras)
            if (claims.Claim(k.Path, AssemblyName(treeDir, k)) is { } by) replaced.Add((k, by));
            else kept.Add(k);
        return (kept, replaced);
    }

    public static bool Carry(string treeDir, SourceAssembly kept, string partsDir)
    {
        var part = PartName(kept);
        if (Project(treeDir, part, AssemblyName(treeDir, kept)) is not { } project) return false;

        XDocument doc;
        try { doc = XDocument.Load(project); }
        catch { return false; }

        var target = Path.Combine(partsDir, part);
        if (Directory.Exists(target))
            throw new InvalidOperationException($"{target} 已存有另一个程序集的输出；{kept.Path} 与该程序集同名。");
        Place(project, Path.Combine(target, Path.GetFileName(project)));
        foreach (var include in doc.Descendants().Select(e => (string?)e.Attribute("Include")).OfType<string>())
        {
            var rel = include.Replace('\\', '/');
            var own = Path.Combine(treeDir, part, rel);
            var shared = Path.Combine(treeDir, rel);
            var from = File.Exists(own) ? own : File.Exists(shared) ? shared : null;
            if (from is not null) Place(from, Path.Combine(target, rel));
        }
        return true;
    }

    public static void CopyCopies(string treeDir, IEnumerable<SourceAssembly> kept, string intoTreeDir)
    {
        foreach (var k in kept)
        {
            var copy = AssemblyStore.CopyPath(treeDir, k.Path);
            if (File.Exists(copy)) Place(copy, AssemblyStore.CopyPath(intoTreeDir, k.Path));
        }
    }

    public static string AssemblyName(string treeDir, SourceAssembly assembly)
    {
        var copy = AssemblyStore.CopyPath(treeDir, assembly.Path);
        return File.Exists(copy) ? AssemblyFilter.AssemblyName(copy) : PartName(assembly);
    }

    public static CallGraph Merge(CallGraph built, CallGraph? previous, IReadOnlySet<string> keptNames)
    {
        if (previous is null || keptNames.Count == 0) return built;
        bool Kept(string from) => keptNames.Contains(from);

        var targets = new Dictionary<(string, int), (string, string)>(built.Targets);
        foreach (var (key, name) in previous.Targets) targets.TryAdd(key, name);

        return new CallGraph
        {
            Tree = built.Tree,
            Edges = [.. built.Edges, .. previous.Edges.Where(e => Kept(e.FromAssembly))],
            Unresolved = built.Unresolved,
            Accesses = built.Accesses is null ? null
                     : [.. built.Accesses, .. (previous.Accesses ?? []).Where(a => Kept(a.FromAssembly))],
            AccessUnresolved = built.AccessUnresolved,
            Targets = targets,
            SourceHashes = built.SourceHashes,
        };
    }

    private static string PartName(SourceAssembly assembly) => TreeIdentity.Part(assembly.Path);

    private static string? Project(string treeDir, string part, string assemblyName)
    {
        foreach (var dir in new[] { treeDir, Path.Combine(treeDir, part) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var project in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var name = XDocument.Load(project).Descendants()
                                        .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
                    if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase)) return project;
                }
                catch { }
            }
        }
        return null;
    }

    private static void Place(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
}
