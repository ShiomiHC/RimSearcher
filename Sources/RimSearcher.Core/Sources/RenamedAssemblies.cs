using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Metadata;

namespace RimSearcher.Sources;

public sealed class RenamedAssemblies
{
    private readonly Dictionary<string, string> _byName;

    private RenamedAssemblies(Dictionary<string, string> byName) => _byName = byName;

    public int Count => _byName.Count;

    public static RenamedAssemblies Of(IEnumerable<string> dlls)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dlls.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = AssemblyFilter.AssemblyName(dll);
            if (!string.Equals(name + ".dll", Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase))
                byName.TryAdd(name, dll);
        }
        return new RenamedAssemblies(byName);
    }

    public IAssemblyResolver Wrap(IAssemblyResolver inner) => _byName.Count == 0 ? inner : new Resolver(this, inner);

    private sealed class Resolver(RenamedAssemblies table, IAssemblyResolver inner) : IAssemblyResolver
    {
        private readonly Dictionary<string, MetadataFile?> _opened = new(StringComparer.OrdinalIgnoreCase);

        private MetadataFile? ByName(IAssemblyReference reference)
        {
            if (!table._byName.TryGetValue(reference.Name, out var path)) return null;
            lock (_opened)
            {
                if (!_opened.TryGetValue(path, out var file))
                {
                    try { file = new PEFile(path, File.OpenRead(path), PEStreamOptions.PrefetchEntireImage); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
                    { file = null; }
                    _opened[path] = file;
                }
                return file;
            }
        }

        public MetadataFile? Resolve(IAssemblyReference reference)
            => inner.Resolve(reference) ?? ByName(reference);

        public async Task<MetadataFile?> ResolveAsync(IAssemblyReference reference)
            => await inner.ResolveAsync(reference).ConfigureAwait(false) ?? ByName(reference);

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
            => inner.ResolveModule(mainModule, moduleName);

        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
            => inner.ResolveModuleAsync(mainModule, moduleName);

        public IDisposable? BeginSnapshot() => inner.BeginSnapshot();
    }
}
