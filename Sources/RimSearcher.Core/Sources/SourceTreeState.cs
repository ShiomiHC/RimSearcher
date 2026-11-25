using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RimTooling.IO;

namespace RimSearcher.Sources;

public sealed record SourceAssembly
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? References { get; init; }
}

public sealed record SourceTreeState
{
    public const string FileName = ".rimsearcher-source.json";

    [JsonPropertyName("package_id")] public required string PackageId { get; init; }

    [JsonPropertyName("game_version")] public required string GameVersion { get; init; }
    [JsonPropertyName("root")] public required string Root { get; init; }
    [JsonPropertyName("assemblies")] public required IReadOnlyList<SourceAssembly> Assemblies { get; init; }

    [JsonPropertyName("decompiler")] public string? Decompiler { get; init; }

    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? References { get; init; }

    private static readonly JsonTypeInfo<SourceTreeState> Json =
        (JsonTypeInfo<SourceTreeState>)new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = SourceTreeStateJson.Default,
        }.GetTypeInfo(typeof(SourceTreeState));

    public static SourceTreeState? Read(string treeDir)
    {
        var path = System.IO.Path.Combine(treeDir, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public void Write(string treeDir)
        => AtomicFile.Write(System.IO.Path.Combine(treeDir, FileName),
                            JsonSerializer.Serialize(this, Json) + "\n");

    public static bool IsOurs(string treeDir)
    {
        if (!Directory.Exists(treeDir)) return true;
        if (File.Exists(System.IO.Path.Combine(treeDir, FileName))) return true;
        try { return !Directory.EnumerateFileSystemEntries(treeDir).Any(); }
        catch { return false; }
    }

    public SourceAssembly? Entry(string path)
        => Assemblies.FirstOrDefault(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public bool SameAssemblies(SourceTreeState planned)
        => planned.Assemblies.All(p => Entry(p.Path) is { } e && e.Sha256 == p.Sha256);

    public IReadOnlyList<SourceAssembly> Kept(SourceTreeState planned)
        => [.. Assemblies.Where(a => planned.Entry(a.Path) is null)];

    public IReadOnlyDictionary<string, Guid>? ReferencesOf(SourceAssembly assembly)
        => (assembly.References ?? References) is { } raw
            ? raw.Where(r => Guid.TryParse(r.Value, out _))
                 .ToDictionary(r => r.Key, r => Guid.Parse(r.Value), StringComparer.OrdinalIgnoreCase)
            : null;

    public bool HasReferences(SourceTreeState planned)
        => planned.Assemblies.All(p => Entry(p.Path) is { } e && (e.References ?? References) is not null);

    public IReadOnlyList<string> ChangedReferences(SourceTreeState planned, Func<string, IReadOnlyCollection<Guid>?> current)
        => [.. planned.Assemblies
                      .Select(p => Entry(p.Path) is { } e ? ReferencesOf(e) : null)
                      .OfType<IReadOnlyDictionary<string, Guid>>()
                      .SelectMany(r => Metadata.CallGraphFreshness.Changed(r, current))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .Order(StringComparer.OrdinalIgnoreCase)];

    public bool SameDecompiler(SourceTreeState planned)
        => Decompiler == planned.Decompiler
        && planned.Assemblies.All(p => Entry(p.Path) is { } e && e.Language == p.Language);
}

[JsonSerializable(typeof(SourceTreeState))]
internal sealed partial class SourceTreeStateJson : JsonSerializerContext;
