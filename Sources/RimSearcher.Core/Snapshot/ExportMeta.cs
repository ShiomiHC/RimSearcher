using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Contract;

namespace RimSearcher.Snapshot;

public sealed record ModRef(string PackageId, string? Name, string? Version,
                            IReadOnlyList<LoadedAssembly>? Assemblies = null);

public sealed record LoadedAssembly(string Name, Guid Mvid);

public sealed record ExportMeta(
    int FormatVersion,
    string ExporterVersion,
    string ExportedAtUtc,
    string GameVersion,
    string Language,
    IReadOnlyList<ModRef> Mods,
    string? ModSettingsHash,
    string RawJson,
    string? PatchRoute = null)
{
    public string Fingerprint => ComputeFingerprint(GameVersion, Language, Mods);

    public const int FloorMajor = 0, FloorMinor = 26;

    public bool IndexesPostPatchXml =>
        !string.IsNullOrEmpty(PatchRoute) && PatchRoute != IntermediateFormat.PatchRouteNone;

    private static bool AtLeast(string version, int major, int minor)
    {
        var parts = (version ?? "").Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var ma) || !int.TryParse(parts[1], out var mi))
            return false;
        return ma > major || (ma == major && mi >= minor);
    }

    public string ModlistFingerprint => ComputeModlistFingerprint(Mods.Select(m => m.PackageId));

    public static string ComputeFingerprint(string gameVersion, string language, IEnumerable<ModRef> mods)
    {
        var sb = new StringBuilder();
        sb.Append(gameVersion).Append('\u0001').Append(language);
        foreach (var m in mods)
            sb.Append('\u0001').Append(m.PackageId.ToLowerInvariant()).Append('@').Append(m.Version ?? "");
        return Hash(sb.ToString());
    }

    public static string ComputeModlistFingerprint(IEnumerable<string> packageIds)
        => Hash(string.Join("\u0001", packageIds.Select(p => p.ToLowerInvariant())));

    private static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    public static ExportMeta Parse(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        string Str(string key, string fallback = "") =>
            root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

        var kind = Str(IntermediateFormat.KeyKind);
        if (kind != IntermediateFormat.KindMeta)
            throw new SnapshotFormatError(
                $"导出文件不以 {IntermediateFormat.KindMeta} 行开头。须重新导出。");

        var version = root.TryGetProperty(IntermediateFormat.KeyFormatVersion, out var fv) ? fv.GetInt32() : 0;
        if (version < IntermediateFormat.FormatVersion)
            throw new SnapshotFormatError(
                $"该导出文件的格式版本为 {version}，本构建读取 {IntermediateFormat.FormatVersion}，因此不导入。" +
                "须从游戏重新导出（'rimsearcher export'）。");
        if (version > IntermediateFormat.FormatVersion)
            throw new SnapshotFormatError(
                $"该导出文件的格式版本 {version} 比本构建读取的 {IntermediateFormat.FormatVersion} 新，因此不导入。" +
                "须更新 CLI，或从游戏重新导出（'rimsearcher export'）。");

        var exporter = Str(IntermediateFormat.KeyExporterVersion, "unknown");
        if (!AtLeast(exporter, FloorMajor, FloorMinor))
            throw new SnapshotFormatError(
                $"该导出文件由导出器 {exporter} 写出，本构建读取 {FloorMajor}.{FloorMinor} 或更新的版本，因此不导入。" +
                "须从游戏重新导出（'rimsearcher export'）。");

        var mods = new List<ModRef>();
        if (root.TryGetProperty(IntermediateFormat.KeyMods, out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
            foreach (var m in modsEl.EnumerateArray())
                mods.Add(new ModRef(
                    m.TryGetProperty(IntermediateFormat.KeyPackageId, out var p) ? p.GetString() ?? "" : "",
                    m.TryGetProperty(IntermediateFormat.KeyName, out var n) ? n.GetString() : null,
                    m.TryGetProperty(IntermediateFormat.KeyVersion, out var v2) ? v2.GetString() : null,
                    !m.TryGetProperty(IntermediateFormat.KeyAssemblies, out var a) ? []
                    : a.ValueKind != JsonValueKind.Array ? null
                        : a.EnumerateArray()
                           .Select(e => (Name: e.TryGetProperty(IntermediateFormat.KeyName, out var an) ? an.GetString() : null,
                                         Mvid: e.TryGetProperty(IntermediateFormat.KeyMvid, out var am) &&
                                               Guid.TryParse(am.GetString(), out var g) ? g : Guid.Empty))
                           .Where(e => e.Name is { Length: > 0 })
                           .Select(e => new LoadedAssembly(e.Name!, e.Mvid))
                           .ToList()));

        return new ExportMeta(
            version,
            exporter,
            Str(IntermediateFormat.KeyExportedAtUtc),
            Str(IntermediateFormat.KeyGameVersion, "unknown"),
            Str(IntermediateFormat.KeyLanguage, "unknown"),
            mods,
            root.TryGetProperty(IntermediateFormat.KeyModSettingsHash, out var h) ? h.GetString() : null,
            jsonLine,
            root.TryGetProperty(IntermediateFormat.KeyPatchRoute, out var pr) ? pr.GetString() : null);
    }
}

public sealed class SnapshotFormatError(string message) : Exception(message);
