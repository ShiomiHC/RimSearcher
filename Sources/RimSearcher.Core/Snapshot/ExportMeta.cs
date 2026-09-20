using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimSearcher.Contract;

namespace RimSearcher.Snapshot;

public sealed record ModRef(string PackageId, string? Name, string? Version);

/// <summary>
/// 快照身份。指纹 = **有序** packageId + 各 mod 版本 + 游戏 build + 语言。
///
/// 顺序必须入指纹:激活顺序就是 patch 应用顺序,同一批 mod 换个顺序得到的是另一份数据。
/// 语言入指纹是因为 label 列存的是该语言下的运行时值。
/// </summary>
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

    /// <summary>
    /// 读得进来的最老导出器。<see cref="IntermediateFormat.FormatVersion"/> 答「文件能不能读」,
    /// 这一档答「层齐不齐」:0.5–0.10 逐层加进来的 xml_written / type_fields / injection_keys
    /// 都没涨格式号,靠这里拒收。地板之下的文件与库一个都不在盘上了,每层的世代分支随之删除
    /// (2026-09-20);再往上抬时只改这两个数。
    /// </summary>
    public const int FloorMajor = 0, FloorMinor = 12;

    /// <summary>
    /// 这份快照的 xml_written 是**打完补丁**的路径全集吗 —— 当次真拿到了那份文档
    /// (<see cref="PatchRoute"/> 不是 <c>none</c>)。
    ///
    /// 拿不到时收的是磁盘上的原文,于是别的 mod 用 PatchOperationAdd 加进来的一行,在
    /// <c>xml</c> 列上报 <c>no</c> —— 与「谁都没写过、该 Add」逐字同形,而出路正相反。
    /// </summary>
    public bool IndexesPostPatchXml =>
        !string.IsNullOrEmpty(PatchRoute) && PatchRoute != IntermediateFormat.PatchRouteNone;

    private static bool AtLeast(string version, int major, int minor)
    {
        var parts = (version ?? "").Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var ma) || !int.TryParse(parts[1], out var mi))
            return false;   // "unknown" / "test" 一律当成没量过 —— 少说一件事比多担保一件强
        return ma > major || (ma == major && mi >= minor);
    }

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

        // 拒收双原因和应对:文件旧 = 重导
        // 文件新 = 当前 CLI 落后于 mod
        var version = root.TryGetProperty(IntermediateFormat.KeyFormatVersion, out var fv) ? fv.GetInt32() : 0;
        if (version < IntermediateFormat.FormatVersion)
            throw new SnapshotFormatError(
                $"This export is format version {version} and this build reads {IntermediateFormat.FormatVersion}. " +
                "It is refused rather than imported in part: the sections it is missing would answer as " +
                "'nothing found' instead of 'not in this file', which reads exactly like the game not having " +
                "the thing you asked about. Export again from the game ('rimsearcher export') — re-importing " +
                "this file cannot add what was never written into it.");
        if (version > IntermediateFormat.FormatVersion)
            throw new SnapshotFormatError(
                $"This export is format version {version}, which is newer than the {IntermediateFormat.FormatVersion} " +
                "this build reads — the in-game exporter is ahead of the CLI. Update the CLI (rebuild and " +
                "re-publish it); exporting again would produce the same file.");

        var exporter = Str(IntermediateFormat.KeyExporterVersion, "unknown");
        if (!AtLeast(exporter, FloorMajor, FloorMinor))
            throw new SnapshotFormatError(
                $"This export was written by exporter {exporter}, and this build reads {FloorMajor}.{FloorMinor} or later. " +
                "It is refused rather than imported in part: the layers that exporter did not write would answer as " +
                "'nothing found' instead of 'not in this file'. Export again from the game ('rimsearcher export') — " +
                "re-importing this file cannot add what was never written into it.");

        var mods = new List<ModRef>();
        if (root.TryGetProperty(IntermediateFormat.KeyMods, out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
            foreach (var m in modsEl.EnumerateArray())
                mods.Add(new ModRef(
                    m.TryGetProperty(IntermediateFormat.KeyPackageId, out var p) ? p.GetString() ?? "" : "",
                    m.TryGetProperty(IntermediateFormat.KeyName, out var n) ? n.GetString() : null,
                    m.TryGetProperty(IntermediateFormat.KeyVersion, out var v2) ? v2.GetString() : null));

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
