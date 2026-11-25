using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Sources;

public sealed record ReferenceRecipe
{
    public IReadOnlyList<string> SearchDirectories { get; init; } = [];

    public AssemblyRedirects? Redirects { get; init; }

    public RenamedAssemblies? Renamed { get; init; }

    public string? MainAssembly { get; init; }

    internal RecordingResolver Open(PEFile file)
    {
        var inner = new UniversalAssemblyResolver(MainAssembly ?? file.FileName, throwOnError: false,
                                                  targetFramework: file.DetectTargetFrameworkId());
        foreach (var dir in SearchDirectories)
            if (Directory.Exists(dir)) inner.AddSearchDirectory(dir);
        var named = Renamed?.Wrap(inner) ?? inner;
        return new RecordingResolver(Redirects?.Wrap(named) ?? named);
    }
}

public sealed class ReferencePlanner
{
    private readonly string? _gameAssemblyDir;
    private readonly IReadOnlyList<SourceTreePlan> _plans;
    private readonly AssemblyRedirects? _redirects;
    private readonly RenamedAssemblies _renamed;

    public ReferencePlanner(string? gameDir, IReadOnlyList<SourceTreePlan> plans)
    {
        _gameAssemblyDir = gameDir is { Length: > 0 } ? GameLayout.Of(gameDir).AssemblyDir : null;
        _plans = plans;
        _redirects = AssemblyRedirects.Read(gameDir);
        _renamed = RenamedAssemblies.Of(plans.SelectMany(p => p.Assemblies));
    }

    public ReferenceRecipe ForAssembly(SourceTreePlan plan, string dll)
        => new() { SearchDirectories = SearchDirectories(plan, dll), Redirects = _redirects, Renamed = _renamed, MainAssembly = dll };

    private List<string> SearchDirectories(SourceTreePlan plan, string dll)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (seen.Add(dir)) paths.Add(dir);
        }

        Add(_gameAssemblyDir);
        Add(Path.GetDirectoryName(dll));
        foreach (var sibling in plan.Assemblies) Add(Path.GetDirectoryName(sibling));

        var wanted = AssemblyFilter.References(dll);
        if (wanted.Count == 0) return paths;

        foreach (var other in _plans)
            foreach (var path in other.Assemblies)
                if (wanted.Contains(Path.GetFileNameWithoutExtension(path)))
                    Add(Path.GetDirectoryName(path));

        return paths;
    }
}
