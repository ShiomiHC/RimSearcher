using RimSearcher.Metadata;

namespace RimSearcher.Sources;

public enum CsFreshness
{
    Unbuilt,

    AssembliesChanged,

    ReferencesChanged,

    OldDecompiler,

    NoSource,

    Current,
}

public sealed record CsVerdict(
    CsFreshness State, SourceTreeState? Existing, IReadOnlyList<string> ChangedReferences);

public static class TreeFreshness
{
    public static CsVerdict Cs(string treeDir, SourceTreeState? existing, SourceTreeState planned,
                               Func<string, IReadOnlyCollection<Guid>?> modulesNow)
    {
        if (existing is null) return new(CsFreshness.Unbuilt, null, []);
        if (!existing.SameAssemblies(planned) ||
            existing.Kept(planned).Any(k => !File.Exists(Path.Combine(planned.Root, k.Path))) ||
            KeptAssemblies.Split(treeDir, existing, planned).Replaced.Count > 0)
            return new(CsFreshness.AssembliesChanged, existing, []);

        var refs = existing.ChangedReferences(planned, modulesNow);
        if (refs.Count > 0) return new(CsFreshness.ReferencesChanged, existing, refs);
        if (!existing.HasReferences(planned) || !existing.SameDecompiler(planned)) return new(CsFreshness.OldDecompiler, existing, []);
        if (!HasSource(treeDir)) return new(CsFreshness.NoSource, existing, []);
        return new(CsFreshness.Current, existing, []);
    }

    private static bool HasSource(string treeDir)
    {
        try { return Directory.EnumerateFiles(treeDir, "*.cs", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    public static bool CopiesMissing(string treeDir, SourceTreePlan plan)
        => plan.Assemblies.Any(d => !File.Exists(AssemblyStore.CopyPath(treeDir, AssemblyStore.Relative(plan.Root, d))));
}
