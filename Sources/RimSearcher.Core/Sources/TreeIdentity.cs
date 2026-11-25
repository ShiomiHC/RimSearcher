using RimSearcher.Metadata;

namespace RimSearcher.Sources;

public static class TreeIdentity
{
    public static string Part(string path) => Path.GetFileNameWithoutExtension(path);

    public sealed class Claims
    {
        private readonly Dictionary<string, string> _parts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public string? Claim(string path, string assemblyName)
        {
            var part = Part(path);
            if (_parts.TryGetValue(part, out var by) || _names.TryGetValue(assemblyName, out by)) return by;
            _parts[part] = path;
            _names[assemblyName] = path;
            return null;
        }
    }

    public static List<(string Path, string By)> Clashes(string root, IEnumerable<string> assemblies)
    {
        var claims = new Claims();
        var clashes = new List<(string, string)>();
        foreach (var dll in assemblies)
        {
            var rel = AssemblyStore.Relative(root, dll).Replace('\\', '/');
            if (claims.Claim(rel, AssemblyFilter.AssemblyName(dll)) is { } by) clashes.Add((rel, by));
        }
        return clashes;
    }
}
