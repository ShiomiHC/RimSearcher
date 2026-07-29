using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Contract;

namespace RimSearcher.Snapshot;

public sealed record ModRef(string PackageId, string? Name, string? Version);

/// <summary>
/// 快照身份。指纹 = **有序** packageId + 各 mod 版本 + 游戏 build + 语言。
///
/// 顺序必须入指纹:激活顺序就是 patch 应用顺序(03 甲),同一批 mod 换个顺序得到的是
/// 另一份数据。语言入指纹是因为 label 列存的是该语言下的运行时值。
/// </summary>
public sealed record ExportMeta(
    int FormatVersion,
    string ExporterVersion,
    string ExportedAtUtc,
    string GameVersion,
    string Language,
    IReadOnlyList<ModRef> Mods,
    string? ModSettingsHash,
    string RawJson)
{
    public string Fingerprint => ComputeFingerprint(GameVersion, Language, Mods);

    /// <summary>只看有序 packageId 的短指纹 —— 用来回答「同一套 modlist 吗」。</summary>
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
                $"The export file does not start with a {IntermediateFormat.KindMeta} line. " +
                "Re-run the export; a file that starts mid-stream cannot be trusted.");

        var version = root.TryGetProperty(IntermediateFormat.KeyFormatVersion, out var fv) ? fv.GetInt32() : 0;
        if (version != IntermediateFormat.FormatVersion)
            throw new SnapshotFormatError(
                $"Export format version {version} was produced by a different version of the in-game exporter " +
                $"(this build reads version {IntermediateFormat.FormatVersion}). Update the mod and export again.");

        var mods = new List<ModRef>();
        if (root.TryGetProperty(IntermediateFormat.KeyMods, out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
            foreach (var m in modsEl.EnumerateArray())
                mods.Add(new ModRef(
                    m.TryGetProperty(IntermediateFormat.KeyPackageId, out var p) ? p.GetString() ?? "" : "",
                    m.TryGetProperty(IntermediateFormat.KeyName, out var n) ? n.GetString() : null,
                    m.TryGetProperty(IntermediateFormat.KeyVersion, out var v2) ? v2.GetString() : null));

        return new ExportMeta(
            version,
            Str(IntermediateFormat.KeyExporterVersion, "unknown"),
            Str(IntermediateFormat.KeyExportedAtUtc),
            Str(IntermediateFormat.KeyGameVersion, "unknown"),
            Str(IntermediateFormat.KeyLanguage, "unknown"),
            mods,
            root.TryGetProperty(IntermediateFormat.KeyModSettingsHash, out var h) ? h.GetString() : null,
            jsonLine);
    }
}

public sealed class SnapshotFormatError(string message) : Exception(message);
