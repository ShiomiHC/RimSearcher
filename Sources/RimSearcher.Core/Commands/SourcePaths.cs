using RimSearcher.Search;
using RimSearcher.Sources;

namespace RimSearcher.Commands;

internal static class SourcePaths
{
    internal static IReadOnlyList<string> Files(string root, string wanted, string? sourceName)
    {
        var norm = Norm(wanted);

        var direct = Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct) &&
            (sourceName is not { Length: > 0 } || norm.StartsWith(sourceName + "/", StringComparison.OrdinalIgnoreCase)))
            return [norm];

        norm = GenericName.Strip(norm);
        var withCs = norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? null : norm + ".cs";
        return Tiered(sourceName, tier => Listing(root, sourceName, tier)
            .Where(rel => Tail(rel, norm) || (withCs is not null && Tail(rel, withCs)))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    internal static IReadOnlyList<string> Dirs(string root, string wanted, string? sourceName)
    {
        var norm = Norm(wanted);
        if (norm.Length == 0) return [];
        var slash = norm.IndexOf('/');
        if (slash > 0 && Directory.Exists(Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar))) &&
            SourcesShared.TreeNames(root).Contains(norm[..slash], StringComparer.OrdinalIgnoreCase) &&
            (sourceName is not { Length: > 0 } || norm.StartsWith(sourceName + "/", StringComparison.OrdinalIgnoreCase)))
            return [norm];
        return Tiered(sourceName, tier => Trees(root, sourceName, tier)
            .Select(t => Path.Combine(root, t))
            .SelectMany(t => Directory.EnumerateDirectories(t, "*", SearchOption.AllDirectories).Prepend(t))
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
            .Where(rel => !rel.Split('/').Any(s => s.StartsWith('.')) && Tail(rel, norm))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static List<string> Listing(string root, string? sourceName, int tier)
    {
        var key = (root, sourceName ?? "", tier);
        if (listings is not null && listings.TryGetValue(key, out var kept)) return kept;
        var listed = FilesIn(root, Trees(root, sourceName, tier))
            .Concat(tier > 0 || sourceName is { Length: > 0 } ? [] : Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        listings?.Add(key, listed);
        return listed;
    }

    [ThreadStatic] private static Dictionary<(string, string, int), List<string>>? listings;

    internal static IDisposable OneListing() => new ListingScope();

    private sealed class ListingScope : IDisposable
    {
        private readonly bool owner = listings is null;
        public ListingScope() { listings ??= []; }
        public void Dispose() { if (owner) listings = null; }
    }

    private static IReadOnlyList<string> Tiered(string? sourceName, Func<int, List<string>> find)
    {
        for (var tier = 0; tier < (sourceName is { Length: > 0 } ? 1 : SourcePlanner.NameTiers); tier++)
            if (find(tier) is { Count: > 0 } found) return found;
        return [];
    }

    private static IEnumerable<string> Trees(string root, string? sourceName, int tier)
        => sourceName is { Length: > 0 } ? (tier > 0 ? [] : [sourceName]) : SourcesShared.TreesOfTier(root, tier);

    private static IEnumerable<string> FilesIn(string root, IEnumerable<string> trees)
        => trees.SelectMany(t => Directory.EnumerateFiles(Path.Combine(root, t), "*", SearchOption.AllDirectories));

    internal static IEnumerable<string> AllFiles(string root, string? sourceName)
    {
        if (sourceName is { Length: > 0 })
            return Directory.EnumerateFiles(Path.Combine(root, sourceName), "*", SearchOption.AllDirectories);

        return FilesIn(root, SourcesShared.TreeNames(root))
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly));
    }

    private static string Norm(string wanted) => wanted.Replace('\\', '/').Trim('/');

    private static bool Tail(string rel, string want)
        => string.Equals(rel, want, StringComparison.OrdinalIgnoreCase) ||
           rel.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase);
}
