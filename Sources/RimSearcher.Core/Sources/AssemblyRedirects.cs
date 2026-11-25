using System.Reflection.PortableExecutable;
using System.Text.Json;
using ICSharpCode.Decompiler.Metadata;
using RimTooling.RimWorld;

namespace RimSearcher.Sources;

public sealed class AssemblyRedirects
{
    public const string Property = "WaylithClr.AssemblyNameRedirects";

    private readonly IReadOnlyList<(string Pattern, string Target)> _rules;
    private readonly string _dir;

    private AssemblyRedirects(IReadOnlyList<(string, string)> rules, string dir) => (_rules, _dir) = (rules, dir);

    public static AssemblyRedirects? Read(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return null;
        var layout = GameLayout.Of(gameDir);
        if (layout.Engine != GameEngine.Waylith) return null;

        var path = Path.ChangeExtension(layout.Exe, ".runtimeconfig.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var options)
                || !options.TryGetProperty("configProperties", out var props)
                || !props.TryGetProperty(Property, out var spec)
                || spec.ValueKind != JsonValueKind.String)
                return null;
            var rules = Parse(spec.GetString()!);
            return rules.Count == 0 ? null : new AssemblyRedirects(rules, layout.AssemblyDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Pattern, string Target)> Parse(string spec)
        => spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(r => r.Split('=', 2))
               .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
               .Select(p => (p[0].Trim(), p[1].Trim()))
               .ToList();

    public string? TargetOf(string assemblyName)
    {
        foreach (var (pattern, target) in _rules)
        {
            var hit = pattern.EndsWith('*')
                ? assemblyName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : assemblyName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            if (hit) return target.Length == 0 ? null : target;
        }
        return null;
    }

    public IAssemblyResolver Wrap(IAssemblyResolver inner) => new Resolver(this, inner);

    private sealed class Resolver(AssemblyRedirects redirects, IAssemblyResolver inner) : IAssemblyResolver
    {
        private readonly Dictionary<string, MetadataFile?> _opened = new(StringComparer.OrdinalIgnoreCase);

        private MetadataFile? Redirected(IAssemblyReference reference)
        {
            if (redirects.TargetOf(reference.Name) is not { } target) return null;
            var path = Path.Combine(redirects._dir, target + ".dll");
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
            => Redirected(reference) ?? inner.Resolve(reference);

        public async Task<MetadataFile?> ResolveAsync(IAssemblyReference reference)
            => Redirected(reference) ?? await inner.ResolveAsync(reference).ConfigureAwait(false);

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
            => inner.ResolveModule(mainModule, moduleName);

        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
            => inner.ResolveModuleAsync(mainModule, moduleName);

        public IDisposable? BeginSnapshot() => inner.BeginSnapshot();
    }
}
