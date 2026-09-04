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
    /// 这份快照量过**列表元素**(<c>&lt;li Class="…"&gt;</c>)的运行时类型吗(导出器 0.2.0 起)。
    ///
    /// 老快照对 <c>where Class X</c> 回零,而那与「量过了、确实没人用它」同形。
    /// </summary>
    public bool IndexesNestedClass => AtLeast(ExporterVersion, 0, 2);

    /// <summary>
    /// 单字段上的 <c>Class="…"</c> 也量过了吗(导出器 0.4.0 起)。
    ///
    /// 0.2 那一档的判据是「路径以 ] 收尾」,于是 <c>&lt;genStep Class="GenStep_RocksFromGrid"&gt;</c>
    /// 这种**不在列表里**的多态一条都没进索引 —— 而 <c>where Class X</c> 对它回的零,
    /// 与「列表里量过了、确实没人用」逐字同形。三种世界各说各的话,判据在这里分档。
    /// </summary>
    public bool IndexesAllNestedClass => AtLeast(ExporterVersion, 0, 4);

    /// <summary>
    /// 这份快照记下了每个 XML 节点实际写出来的字段路径吗(导出器 0.5.0 起)。
    /// 老快照的 <c>code_default=yes</c> 分不开「XML 写过默认值」与「根本没写」。
    /// </summary>
    public bool IndexesXmlWritten => AtLeast(ExporterVersion, 0, 5);

    /// <summary>
    /// 这份快照除了 <c>@Name=</c> 还数过按 defName / label 定位的 xpath 吗(导出器 0.5.0 起)。
    /// 老快照的 <c>patch_ops=0</c> 把那两种定位与「没被改过」压成同一个零。
    /// </summary>
    public bool IndexesPatchOpsByDefNameLabel => AtLeast(ExporterVersion, 0, 5);

    /// <summary>
    /// 这份快照记下了每个 def 类型能有的字段路径全集吗(导出器 0.5.0 起)。
    /// 老快照里「这个类型有这个字段但全是 null」与「类型根本没有这个字段」同形。
    /// </summary>
    public bool IndexesTypeFields => AtLeast(ExporterVersion, 0, 5);

    /// <summary>
    /// 这份快照记下了每条 xml_written 叶子路径的行内文本吗(导出器 0.6.0 起)。
    ///
    /// 老快照上短形式标签底下的候选格多于一个时,「这段文本就是这一格」与
    /// 「这段文本落在别的格 / 对不上」同形 —— 一律 under,分不开。
    /// </summary>
    public bool IndexesXmlWrittenText => AtLeast(ExporterVersion, 0, 6);

    /// <summary>
    /// 这份快照的 xml_written 是**打完补丁**的路径全集吗(导出器 0.7.0 起,且当次真拿到了
    /// 那份文档 —— <see cref="PatchRoute"/> 不是 <c>none</c>)。
    ///
    /// 老快照收的是磁盘上的原文,于是别的 mod 用 PatchOperationAdd 加进来的一行,在
    /// <c>xml</c> 列上报 <c>no</c> —— 与「谁都没写过、该 Add」逐字同形,而出路正相反。
    /// </summary>
    public bool IndexesPostPatchXml =>
        AtLeast(ExporterVersion, 0, 7)
        && !string.IsNullOrEmpty(PatchRoute)
        && PatchRoute != IntermediateFormat.PatchRouteNone;

    /// <summary>
    /// 这份快照记下了注入键层吗(导出器 0.8.0 起)。
    ///
    /// 老快照的译文表存的是**译者写的那一串**:同一个槽位在把手式与下标式两种键下各存
    /// 一份,而 <c>--path</c> 只能匹配上其中一种 —— 另一种回的零与「这个 def 没这条译文」
    /// 逐字同形。这一档之后两种键归一,且「这个字段不许译」不再与「谁都没译」同形。
    /// </summary>
    public bool IndexesInjectionKeys => AtLeast(ExporterVersion, 0, 8);

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
            jsonLine,
            root.TryGetProperty(IntermediateFormat.KeyPatchRoute, out var pr) ? pr.GetString() : null);
    }
}

public sealed class SnapshotFormatError(string message) : Exception(message);
