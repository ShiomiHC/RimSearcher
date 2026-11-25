using System.Xml.Linq;
using RimTooling.IO;

namespace RimSearcher.Commands;

public sealed class TreeAssemblies(string root)
{
    private readonly Dictionary<string, List<Project>> _trees = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Project(string Name, string Part, HashSet<string> Files);

    public IReadOnlyList<string> Names(string tree) => [.. Projects(tree).Select(p => p.Name)];

    public IReadOnlyList<string> Owners(string tree, string relInTree)
    {
        var projects = Projects(tree);
        var owners = projects.Where(p => p.Files.Contains(relInTree)).Select(p => p.Name).ToList();
        if (owners.Count > 0) return owners;
        var slash = relInTree.IndexOf('/');
        if (slash <= 0) return owners;
        var head = relInTree[..slash];
        var rest = relInTree[(slash + 1)..];
        return [.. projects.Where(p => (head.Equals(p.Part, StringComparison.OrdinalIgnoreCase)
                                        || head.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                                       && p.Files.Contains(rest))
                           .Select(p => p.Name)];
    }

    private List<Project> Projects(string tree)
    {
        if (_trees.TryGetValue(tree, out var known)) return known;
        var dir = Path.Combine(root, tree);
        var result = new List<Project>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly)
                             .Concat(Directory.EnumerateDirectories(dir)
                                              .Where(d => !Path.GetFileName(d).StartsWith('.'))
                                              .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.TopDirectoryOnly)))
                             .ToList();
        }
        catch { files = []; }
        foreach (var file in files)
        {
            try
            {
                var doc = XDocument.Load(file);
                var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var compiled = doc.Descendants().Where(e => e.Name.LocalName == "Compile")
                                  .Select(e => (string?)e.Attribute("Include"))
                                  .OfType<string>()
                                  .Select(f => f.Replace('\\', '/'))
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var parent = Path.GetDirectoryName(file)!;
                var top = PathIdentity.Same(parent, dir);
                var part = top ? Path.GetFileNameWithoutExtension(file) : Path.GetFileName(parent);
                if (!top) compiled = compiled.Select(f => part + "/" + f).ToHashSet(StringComparer.OrdinalIgnoreCase);
                result.Add(new Project(name, part, compiled));
            }
            catch { }
        }
        return _trees[tree] = result;
    }
}
