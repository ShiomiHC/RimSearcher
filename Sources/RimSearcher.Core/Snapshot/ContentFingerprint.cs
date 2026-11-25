using System.Text.Json;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Sources;
using RimTooling.IO;
using RimTooling.RimWorld;

namespace RimSearcher.Snapshot;

public sealed record ModContent(string PackageId, int Files, string Hash, string? Root = null,
                                int? LangFiles = null, string? LangHash = null,
                                int? AsmFiles = null, string? AsmHash = null,
                                IReadOnlyList<string>? AsmElsewhere = null);

public sealed record ContentScan(IReadOnlyList<ModContent> Mods)
{
    public int Files => Mods.Sum(m => m.Files);

    public int XmlMods => Mods.Count(m => m.Files > 0);

    public int LangFiles => Mods.Sum(m => m.LangFiles ?? 0);

    public int LangMods => Mods.Count(m => m.LangFiles > 0);

    public int AsmMods => Mods.Count(m => m.AsmFiles > 0);

    public string ToJson()
        => JsonSerializer.Serialize(Mods.Select(m => new Dto(m.PackageId, m.Files, m.Hash, m.Root,
                                                             m.LangFiles, m.LangHash, m.AsmFiles, m.AsmHash,
                                                             m.AsmElsewhere)).ToList(),
                                    ContentScanJson.Default.ListDto);

    public static ContentScan? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize(json, ContentScanJson.Default.ListDto);
            return dto is null
                ? null
                : new ContentScan(dto.Select(d => new ModContent(d.id, d.files, d.hash, d.root,
                                                                 d.lang_files, d.lang_hash,
                                                                 d.asm_files, d.asm_hash, d.asm_elsewhere)).ToList());
        }
        catch (JsonException) { return null; }
    }

    internal sealed record Dto(string id, int files, string hash, string? root,
                              int? lang_files = null, string? lang_hash = null,
                              int? asm_files = null, string? asm_hash = null,
                              IReadOnlyList<string>? asm_elsewhere = null);
}

public sealed record ContentComparison(IReadOnlyList<string> Changed, IReadOnlyList<string> Missing, int Scanned,
                                       IReadOnlyList<string>? AssembliesChanged = null)
{
    public IReadOnlyList<string> Assemblies => AssembliesChanged ?? [];

    public bool Drifted => Changed.Count > 0 || Missing.Count > 0 || Assemblies.Count > 0;
}

public static class ContentFingerprint
{
    public static readonly string[] Subdirs = ["Defs", "Patches"];

    public static readonly string[] LanguageSubdirs = ["Keyed", "DefInjected"];

    public const string FallbackLanguage = "English";

    public static ContentScan? Scan(RimConfig config, IEnumerable<string> packageIds, string gameVersion,
                                    string? language = null,
                                    IReadOnlyDictionary<string, IReadOnlyList<LoadedAssembly>>? loaded = null)
        => Scan(config, packageIds, gameVersion, language,
                id => (loaded is not null && loaded.TryGetValue(id, out var l) ? l : null, null));

    private static ContentScan? Scan(RimConfig config, IEnumerable<string> packageIds, string gameVersion,
                                     string? language, Func<string, Elsewhere> elsewhere)
    {
        var installed = InstalledMods.Scan(config);
        if (installed.Count == 0) return null;

        var version = SourcePlanner.NormalizeGameVersion(gameVersion);
        var ids = packageIds
            .Where(id => !Contract.IntermediateFormat.IsExporterPackageId(id))
            .ToList();
        var active = ModFolders.NormalizeActive(ids);

        var targets = ids.Where(installed.ContainsKey)
                         .Select(id => (Id: id, Root: installed[id].Directory, Elsewhere: elsewhere(id)))
                         .ToList();

        return Assemble(targets, version, active, language, xml: true, keepEmpty: false);
    }

    public static ContentScan? Rescan(RimConfig config, ContentScan recorded, IEnumerable<string> packageIds,
                                      string gameVersion)
    {
        if (recorded.Mods.Any(m => m.Root is not { Length: > 0 } r || !Directory.Exists(r)))
        {
            var byId = recorded.Mods.GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First().AsmElsewhere, StringComparer.OrdinalIgnoreCase);
            return Scan(config, packageIds, gameVersion, null,
                        id => (null, byId.GetValueOrDefault(id)));
        }

        var version = SourcePlanner.NormalizeGameVersion(gameVersion);
        var active = ModFolders.NormalizeActive(
            packageIds.Where(id => !Contract.IntermediateFormat.IsExporterPackageId(id)));

        return Assemble(recorded.Mods.Select(m => (m.PackageId, m.Root!, new Elsewhere(null, m.AsmElsewhere))).ToList(),
                        version, active, language: null, xml: true, keepEmpty: true);
    }

    public static ContentScan? RescanLanguages(RimConfig config, ContentScan recorded, IEnumerable<string> packageIds,
                                               string gameVersion, string language)
    {
        if (recorded.Mods.Any(m => m.Root is not { Length: > 0 } r || !Directory.Exists(r)))
            return Scan(config, packageIds, gameVersion, language, loaded: null);

        var version = SourcePlanner.NormalizeGameVersion(gameVersion);
        var active = ModFolders.NormalizeActive(
            packageIds.Where(id => !Contract.IntermediateFormat.IsExporterPackageId(id)));

        return Assemble(recorded.Mods.Select(m => (m.PackageId, m.Root!, new Elsewhere(null, null))).ToList(),
                        version, active, language, xml: false, keepEmpty: true);
    }

    private static ContentScan? Assemble(IReadOnlyList<(string Id, string Root, Elsewhere Elsewhere)> targets, string version,
                                         IReadOnlySet<string> active, string? language, bool xml, bool keepEmpty)
    {
        var slots = new ModContent?[targets.Count];
        Parallel.For(0, targets.Count, i => slots[i] = ScanOne(targets[i].Id, targets[i].Root, targets[i].Elsewhere,
                                                                version, active, language, xml, keepEmpty));

        var mods = slots.OfType<ModContent>().ToList();
        return mods.Count == 0 ? null : new ContentScan(mods);
    }

    private readonly record struct Elsewhere(IReadOnlyList<LoadedAssembly>? Loaded, IReadOnlyList<string>? Known)
    {
        public static implicit operator Elsewhere((IReadOnlyList<LoadedAssembly>? Loaded, IReadOnlyList<string>? Known) t)
            => new(t.Loaded, t.Known);
    }

    private static ModContent? ScanOne(string packageId, string rootDir, Elsewhere elsewhere, string version,
                                       IReadOnlySet<string> active, string? language, bool xml, bool keepEmpty)
    {
        List<string> folders;
        try { folders = ModFolders.LoadFolders(rootDir, version, active); }
        catch { return null; }

        var xmlScan = xml ? BuildXmlFingerprint(rootDir, folders) : ((int Count, string Hash)?)null;
        (int Count, string Hash)? langScan = language is null ? null : BuildLanguageFingerprint(rootDir, folders, language);
        List<string>? found = null;
        var asmScan = xml ? BuildAssemblyFingerprint(rootDir, folders, elsewhere, out found) : ((int Count, string Hash)?)null;

        if (!keepEmpty && (xmlScan?.Count ?? 0) == 0 && (langScan?.Count ?? 0) == 0 &&
            (asmScan?.Count ?? 0) == 0) return null;

        return new ModContent(packageId, xmlScan?.Count ?? 0, xmlScan is null ? "" : xmlScan.Value.Hash,
                              PathIdentity.Normalize(rootDir), langScan?.Count, langScan?.Hash,
                              asmScan?.Count, asmScan?.Hash,
                              found is { Count: > 0 } ? found : null);
    }

    private static (int Count, string Hash) BuildAssemblyFingerprint(string rootDir, List<string> folders,
                                                                     Elsewhere elsewhere, out List<string>? found)
    {
        var builder = new ContentFingerprintBuilder(PathComparison.DefaultComparer);
        var seen = new HashSet<string>(PathComparison.DefaultComparer);
        var planned = ModFolders.AssembliesIn(folders);
        foreach (var path in planned)
        {
            var rel = Relative(rootDir, path);
            seen.Add(rel);
            builder.AddFile(path, rel, trackMissing: true);
        }

        found = elsewhere.Known?.ToList()
             ?? (elsewhere.Loaded is { } loaded
                 ? SourcePlanner.LoadedElsewhere(rootDir, planned, loaded).Select(p => Relative(rootDir, p)).ToList()
                 : null);
        foreach (var rel in found ?? [])
        {
            var normalizedRel = rel.Replace('\\', '/');
            if (!seen.Add(normalizedRel)) continue;
            builder.AddFile(Path.Combine(rootDir, normalizedRel), normalizedRel, trackMissing: true);
        }

        return (builder.Snapshot().Count, builder.ComputeDigestHex());
    }

    private static (int Count, string Hash) BuildXmlFingerprint(string rootDir, List<string> folders)
    {
        var builder = new ContentFingerprintBuilder(PathComparison.DefaultComparer);
        var opts = new FingerprintWalkOptions { SearchPattern = "*.xml" };

        foreach (var folder in folders)
        {
            foreach (var sub in Subdirs)
            {
                var dir = Path.Combine(folder, sub);
                builder.AddDirectory(dir, Relative(rootDir, dir), opts);
            }
        }

        return (builder.Snapshot().Count, builder.ComputeDigestHex());
    }

    private static (int Count, string Hash) BuildLanguageFingerprint(string rootDir, List<string> folders, string language)
    {
        var builder = new ContentFingerprintBuilder(PathComparison.DefaultComparer);
        var opts = new FingerprintWalkOptions { SearchPattern = "*.xml" };
        var languages = string.Equals(language, FallbackLanguage, StringComparison.OrdinalIgnoreCase)
            ? [language]
            : new[] { language, FallbackLanguage };

        foreach (var folder in folders)
        {
            var langRoot = Path.Combine(folder, "Languages");
            if (!Directory.Exists(langRoot)) continue;

            foreach (var lang in languages)
            {
                foreach (var name in new[] { lang, LegacyFolderName(lang) }.Distinct(StringComparer.Ordinal))
                {
                    var dir = Path.Combine(langRoot, name);
                    if (Directory.Exists(dir))
                    {
                        foreach (var sub in LanguageSubdirs)
                        {
                            var subDir = Path.Combine(dir, sub);
                            builder.AddDirectory(subDir, Relative(rootDir, subDir), opts);
                        }
                        break;
                    }

                    var tar = dir + ".tar";
                    if (File.Exists(tar))
                    {
                        builder.AddFile(tar, Relative(rootDir, tar), trackMissing: false);
                        break;
                    }
                }
            }
        }

        return (builder.Snapshot().Count, builder.ComputeDigestHex());
    }

    public static string LegacyFolderName(string folderName)
        => (folderName.Contains('(') ? folderName[..Math.Max(0, folderName.IndexOf('(') - 1)] : folderName).Trim();

    private static string Relative(string rootDir, string fullPath)
        => Path.GetRelativePath(rootDir, Path.GetFullPath(fullPath)).Replace('\\', '/');

    public static ContentComparison Compare(ContentScan recorded, ContentScan current)
    {
        var now = current.Mods.ToDictionary(m => m.PackageId, StringComparer.OrdinalIgnoreCase);

        var changed = new List<string>();
        var missing = new List<string>();
        var assemblies = new List<string>();

        foreach (var m in recorded.Mods)
        {
            if (!now.TryGetValue(m.PackageId, out var cur)) { missing.Add(m.PackageId); continue; }
            if (!string.Equals(cur.Hash, m.Hash, StringComparison.Ordinal)) changed.Add(m.PackageId);
            if (m.AsmHash is not null && cur.AsmHash is not null &&
                !string.Equals(cur.AsmHash, m.AsmHash, StringComparison.Ordinal)) assemblies.Add(m.PackageId);
        }

        return new ContentComparison(changed, missing, recorded.XmlMods, assemblies);
    }

    public static IReadOnlyList<string> CompareLanguages(ContentScan recorded, ContentScan current)
    {
        var now = current.Mods.ToDictionary(m => m.PackageId, StringComparer.OrdinalIgnoreCase);
        return recorded.Mods
            .Where(m => m.LangHash is not null && now.TryGetValue(m.PackageId, out var cur) && cur.LangHash is not null
                        && !string.Equals(cur.LangHash, m.LangHash, StringComparison.Ordinal))
            .Select(m => m.PackageId)
            .ToList();
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ContentScan.Dto>), TypeInfoPropertyName = "ListDto")]
internal sealed partial class ContentScanJson : System.Text.Json.Serialization.JsonSerializerContext;
