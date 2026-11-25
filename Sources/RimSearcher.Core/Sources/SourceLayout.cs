using RimTooling.IO;

namespace RimSearcher.Sources;

public static class SourceLayout
{
    public const string PartsDir = ".parts";

    private const string AssemblyInfo = "Properties/AssemblyInfo.cs";

    public static void Merge(string partsDir, string treeDir)
    {
        var byPath = new Dictionary<string, List<(string Assembly, string File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Directory.EnumerateDirectories(partsDir).Order(StringComparer.Ordinal))
        {
            var assembly = Path.GetFileName(part);
            foreach (var file in Directory.EnumerateFiles(part, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(part, file).Replace('\\', '/');
                if (rel.Equals(AssemblyInfo, StringComparison.OrdinalIgnoreCase)) continue;
                if (!byPath.TryGetValue(rel, out var copies)) byPath[rel] = copies = [];
                copies.Add((assembly, file));
            }
        }

        foreach (var (rel, copies) in byPath)
        {
            if (copies.Skip(1).All(c => SameBytes(c.File, copies[0].File)))
                Place(copies[0].File, Path.Combine(treeDir, rel));
            else
                foreach (var (assembly, file) in copies)
                    Place(file, Path.Combine(treeDir, assembly, rel));
        }

        FileSystemSafety.DeleteTreeJunctionSafe(partsDir);
    }

    private static void Place(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
    }

    private static bool SameBytes(string a, string b)
        => new FileInfo(a).Length == new FileInfo(b).Length
        && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
