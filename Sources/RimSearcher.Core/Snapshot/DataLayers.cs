using RimSearcher.Config;
using RimSearcher.Contract;
using RimSearcher.Storage;

namespace RimSearcher.Snapshot;

/// <summary>
/// 一层数据在这份快照里的在场状态。封闭词表:查询面印的是它的小写拼法(<see cref="LayerStateText"/>),
/// 消费方按字面匹配,所以加一个词就是改契约。
///
/// 词各答一个不同的问题 —— 同一个「空」在这里必须分得开,因为它们的出路不同:
/// <list type="bullet">
/// <item><see cref="PreMeasure"/>:这份库建于该层进导出器之前 —— 出路是重导。</item>
/// <item><see cref="Skipped"/>:导出被明确告知跳过这一层 —— 出路是不带那个开关重导。</item>
/// <item><see cref="Unavailable"/>:导出器想量、量不成(游戏侧签名对不上等)—— 出路不在这台机器的命令行上。</item>
/// <item><see cref="Unmeasured"/>:这份库本可以量、这一次没量(导入时关了 / 那一格没记)—— 出路是重导入。</item>
/// <item><see cref="Unconfigured"/>:量的前提没配(<c>mod_roots</c> 等)—— 出路是先配再导入。</item>
/// <item><see cref="Partial"/>:量了一部分(老导出器只量列表元素的 Class)—— 出路是重导。</item>
/// <item><see cref="Empty"/>:量过了,一行都没有;成因分不出来 —— 出路仍是重导,但可能重导之后照旧。</item>
/// <item><see cref="Missing"/>:磁盘上没有(源码树 / 表)—— 出路是建它。</item>
/// </list>
/// </summary>
public enum LayerState
{
    Ok,
    PreMeasure,
    Skipped,
    Unavailable,
    Unmeasured,
    Unconfigured,
    Partial,
    Empty,
    Missing,
}

public static class LayerStateText
{
    public static string Render(LayerState state) => state switch
    {
        LayerState.Ok => "ok",
        LayerState.PreMeasure => "pre-measure",
        LayerState.Skipped => "skipped",
        LayerState.Unavailable => "unavailable",
        LayerState.Unmeasured => "unmeasured",
        LayerState.Unconfigured => "unconfigured",
        LayerState.Partial => "partial",
        LayerState.Empty => "empty",
        LayerState.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>
/// 一层的账:名字、状态、下一步(填好参数的命令;<c>ok</c> 行为空)、成因(只进
/// <c>snapshot status</c> 的全账,查询面不印 —— 机制句会被读成规格)。
/// </summary>
public sealed record LayerRow(string Layer, LayerState State, string? Next, string? Why)
{
    public bool Complete => State == LayerState.Ok;
}

/// <summary>
/// 层在场性的唯一产地。此前 21 处各自手写「这份库没有这一层,因为 X,出路 Y」,
/// 判据散在 <see cref="ExportMeta"/> 的版本位、<see cref="SnapshotDb"/> 的 meta 键与几张表的行数上;
/// 这里把它们收成一张 (层, 状态, 出路, 成因) 的账,查询面只印碰到的那几行。
/// </summary>
public static class DataLayers
{
    public const string Defs = "defs";
    public const string Economy = "economy";
    public const string Keyed = "keyed";
    public const string DiskTranslations = "disk_translations";
    public const string InjectionKeys = "injection_keys";
    public const string InjectionApplied = "injection_applied";
    public const string XmlFingerprint = "xml_fingerprint";
    public const string XmlWritten = "xml_written";
    public const string XmlWrittenText = "xml_written_text";
    public const string PostPatchXml = "post_patch_xml";
    public const string TypeFields = "type_fields";
    public const string NestedClass = "nested_class";
    public const string PatchOpsByName = "patch_ops_by_name";
    public const string TruncationCauses = "truncation_causes";
    public const string ExportTimings = "export_timings";
    public const string ImportTimings = "import_timings";

    /// <summary>整张账,<c>snapshot status</c> 用;顺序固定,消费方可按位读。</summary>
    public static IReadOnlyList<LayerRow> Ledger(SnapshotDb db, RimConfig config, string snapshotName)
    {
        var export = ExportCommand(snapshotName);
        var import = ImportCommand(db, snapshotName);
        var m = db.Meta;
        return
        [
            DefsRow(db, snapshotName),
            EconomyRow(db, snapshotName),
            KeyedRow(db, snapshotName),
            DiskTranslationsRow(db, config, snapshotName),
            Bit(InjectionKeys, db.InjectionKeysIndexed, export,
                "exporter before 0.9.0: translation paths stored as raw injection keys"),
            Bit(InjectionApplied, m.RecordsInjectionApplied, export,
                "exporter before 0.10.0: every language-pack row counted as in effect"),
            new(XmlFingerprint, db.Content is null ? LayerState.Unmeasured : LayerState.Ok,
                db.Content is null ? export : null,
                db.Content is null ? "no Defs/Patches fingerprint recorded at export" : null),
            Bit(XmlWritten, m.IndexesXmlWritten, export, "exporter before 0.5.0: no 'xml' column"),
            Bit(XmlWrittenText, m.IndexesXmlWrittenText, export, "exporter before 0.6.0: no inline text per XML leaf"),
            PostPatchXmlRow(db, snapshotName),
            Bit(TypeFields, m.IndexesTypeFields, export, "exporter before 0.5.0: no per-type field path set"),
            NestedClassRow(db, snapshotName),
            Bit(PatchOpsByName, m.IndexesPatchOpsByDefNameLabel, export,
                "exporter before 0.5.0: patch_ops counts @Name= targets only"),
            TruncationCausesRow(db, snapshotName),
            Bit(ExportTimings, db.ExportTimings is { Count: > 0 }, export, "exporter before 0.11.0: no per-layer timing"),
            Bit(ImportTimings, db.ImportTimings is { Count: > 0 }, import, "imported before per-stage timing was kept"),
        ];
    }

    public static string ExportCommand(string snapshotName) => $"rimsearcher export --modlist {snapshotName}";

    /// <summary>
    /// 重导入的命令。文件名取建库时记下的那一个(<see cref="SnapshotSchema.MetaKeySourcePath"/>),
    /// 没记就按「导出文件与快照同名」的约定拼 —— <c>snapshot import</c> 对裸文件名会去导出目录找。
    /// </summary>
    public static string ImportCommand(SnapshotDb db, string snapshotName)
        => $"rimsearcher snapshot import {db.SourceFile ?? snapshotName + IntermediateFormat.FileExtension} --name {snapshotName}";

    public static LayerRow DefsRow(SnapshotDb db, string snapshotName)
        => db.DefCount() > 0
            ? new(Defs, LayerState.Ok, null, null)
            : new(Defs, LayerState.Empty, ExportCommand(snapshotName), "no def rows");

    /// <summary>
    /// 经济层四态。<c>null</c> 是第四态(建于经济面进导出之前),不是「没成功」;
    /// 三种缺席的下一步各不相同,合成一句会让最刺眼的那种(签名对不上)被读成最无害的那种。
    /// </summary>
    public static LayerRow EconomyRow(SnapshotDb db, string snapshotName) => db.EconomyState switch
    {
        IntermediateFormat.EconomyStateOk => new(Economy, LayerState.Ok, null, null),
        null => new(Economy, LayerState.PreMeasure, ExportCommand(snapshotName),
                    "exported before prices were measured"),
        IntermediateFormat.EconomyStateSkipped => new(Economy, LayerState.Skipped,
                    ExportCommand(snapshotName), "export ran with --no-economy"),
        // 导出器点名的那句原样进 next,不概括 —— 它是唯一的下一步(去比 vanilla 现在的签名),
        // 而这一层不回退到自写实现:回退能出数,出的却是与游戏内表格不一致的数。
        _ => new(Economy, LayerState.Unavailable,
                 db.EconomyError ?? "the export recorded no reason, which should not happen",
                 "the exporter could not measure prices on this game build; every other layer is complete"),
    };

    /// <summary>
    /// 两种成因(层未量 / 语言数据没加载)在位上分不开 —— 导出器没写态,账上如实 <c>empty</c>。
    /// </summary>
    public static LayerRow KeyedRow(SnapshotDb db, string snapshotName)
        => db.KeyedCount() > 0
            ? new(Keyed, LayerState.Ok, null, null)
            : new(Keyed, LayerState.Empty, ExportCommand(snapshotName),
                  "no keyed rows: exported before this layer, or from a game whose language data was not loaded");

    /// <summary>
    /// 磁盘语言文件那一层。成因优先读建库时记下的(<see cref="SnapshotDb.TranslationsHarvest"/>);
    /// 老库没记,只能按**现机**的 <c>mod_roots</c> 猜 —— 库是 <c>--no-harvest</c> 建的时候会指错方向,
    /// 所以 why 里写明是猜的。
    /// </summary>
    public static LayerRow DiskTranslationsRow(SnapshotDb db, RimConfig config, string snapshotName)
    {
        if (db.Harvested) return new(DiskTranslations, LayerState.Ok, null, null);
        var import = ImportCommand(db, snapshotName);
        return db.TranslationsHarvest switch
        {
            SnapshotSchema.TranslationsHarvestOff => new(DiskTranslations, LayerState.Unmeasured, import,
                "imported with --no-harvest-translations"),
            SnapshotSchema.TranslationsHarvestNoRoots => new(DiskTranslations, LayerState.Unconfigured,
                "set 'mod_roots' in the config file, then " + import,
                "imported with no 'mod_roots' configured, so there was nowhere to scan"),
            _ => config.ModRoots.Count > 0
                ? new(DiskTranslations, LayerState.Unmeasured, import,
                      "imported before the reason was recorded; 'mod_roots' is configured now")
                : new(DiskTranslations, LayerState.Unconfigured,
                      "set 'mod_roots' in the config file, then " + import,
                      "imported before the reason was recorded; no 'mod_roots' is configured now"),
        };
    }

    public static LayerRow PostPatchXmlRow(SnapshotDb db, string snapshotName)
    {
        var m = db.Meta;
        if (m.IndexesPostPatchXml) return new(PostPatchXml, LayerState.Ok, null, null);
        return m.ExporterAtLeast(0, 7)
            ? new(PostPatchXml, LayerState.Unavailable, ExportCommand(snapshotName),
                  "the exporter found no patched XML document to read, so 'xml' reflects the files on disk")
            : new(PostPatchXml, LayerState.PreMeasure, ExportCommand(snapshotName),
                  "exporter before 0.7.0: 'xml' reflects the files on disk, before patches");
    }

    public static LayerRow NestedClassRow(SnapshotDb db, string snapshotName)
    {
        var m = db.Meta;
        if (m.IndexesAllNestedClass) return new(NestedClass, LayerState.Ok, null, null);
        return m.IndexesNestedClass
            ? new(NestedClass, LayerState.Partial, ExportCommand(snapshotName),
                  "exporter before 0.4.0: Class= measured on list elements only")
            : new(NestedClass, LayerState.PreMeasure, ExportCommand(snapshotName),
                  "exporter before 0.2.0: Class= not measured");
    }

    /// <summary>
    /// 「分得清为什么被截」这一层。列在不在与列里有没有数是两件事:0.13.0 之前的文件进了新库,
    /// 四列拿的是 DEFAULT 0;而一个 def 也没被截过的库,四个零是真的 —— 那时这一层是 ok。
    /// </summary>
    public static LayerRow TruncationCausesRow(SnapshotDb db, string snapshotName)
    {
        if (!db.DefsHaveTruncationBreakdown)
            return new(TruncationCauses, LayerState.PreMeasure, ExportCommand(snapshotName),
                       "exporter before 0.13.0: truncation kept as one total");
        if (db.TruncationCausesMeasured || db.TruncatedDefCount() == 0)
            return new(TruncationCauses, LayerState.Ok, null, null);
        return new(TruncationCauses, LayerState.PreMeasure, ExportCommand(snapshotName),
                   "exporter before 0.13.0: defs were truncated but no cause was recorded");
    }

    private static LayerRow Bit(string layer, bool present, string next, string why)
        => present ? new(layer, LayerState.Ok, null, null) : new(layer, LayerState.PreMeasure, next, why);

    /// <summary>渲染成表行。<c>why</c> 只在全账里要。</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(IEnumerable<LayerRow> rows, bool withWhy)
        => rows.Select(r =>
        {
            var d = new Dictionary<string, object?>
            {
                ["layer"] = r.Layer,
                ["state"] = LayerStateText.Render(r.State),
                ["next"] = r.Next,
            };
            if (withWhy) d["why"] = r.Why;
            return (IReadOnlyDictionary<string, object?>)d;
        }).ToList();
}
