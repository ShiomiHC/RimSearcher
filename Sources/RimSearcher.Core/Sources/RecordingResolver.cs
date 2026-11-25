using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Sources;

internal sealed class RecordingResolver(IAssemblyResolver inner) : IAssemblyResolver, IDisposable
{
    private readonly Dictionary<string, Guid> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<MetadataFile> _opened = new(ReferenceEqualityComparer.Instance);

    public void Dispose()
    {
        lock (_seen)
        {
            foreach (var file in _opened) (file as IDisposable)?.Dispose();
            _opened.Clear();
        }
    }

    public IReadOnlyDictionary<string, Guid> Resolved()
    {
        lock (_seen) return new Dictionary<string, Guid>(_seen, StringComparer.OrdinalIgnoreCase);
    }

    private MetadataFile? Note(MetadataFile? file)
    {
        if (file is null) return null;
        lock (_seen)
        {
            _opened.Add(file);
            if (file is { IsAssembly: true } && file.Metadata is var md)
                _seen.TryAdd(md.GetString(md.GetAssemblyDefinition().Name), md.GetGuid(md.GetModuleDefinition().Mvid));
        }
        return file;
    }

    public MetadataFile? Resolve(IAssemblyReference reference) => Note(inner.Resolve(reference));

    public async Task<MetadataFile?> ResolveAsync(IAssemblyReference reference)
        => Note(await inner.ResolveAsync(reference).ConfigureAwait(false));

    public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
    {
        var file = inner.ResolveModule(mainModule, moduleName);
        if (file is not null) lock (_seen) _opened.Add(file);
        return file;
    }

    public async Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
    {
        var file = await inner.ResolveModuleAsync(mainModule, moduleName).ConfigureAwait(false);
        if (file is not null) lock (_seen) _opened.Add(file);
        return file;
    }

    public IDisposable? BeginSnapshot() => inner.BeginSnapshot();
}
