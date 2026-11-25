using RimSearcher.Config;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Snapshot;

public enum LayerState
{
    Ok,
    Skipped,
    Unavailable,
    Unmeasured,
    Unconfigured,
    Empty,
    Missing,
}

public static class LayerStateText
{
    public static string Render(LayerState state) => state switch
    {
        LayerState.Ok => "齐备",
        LayerState.Skipped => "已跳过",
        LayerState.Unavailable => "无法采集",
        LayerState.Unmeasured => "未采集",
        LayerState.Unconfigured => "未配置",
        LayerState.Empty => "空",
        LayerState.Missing => "未建",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

public sealed record LayerRow(string Layer, LayerState State, string? Next, string? Why)
{
    public bool Complete => State == LayerState.Ok;
}

public static class DataLayers
{
    public const string Defs = "defs";
    public const string Economy = "economy";
    public const string Keyed = "keyed";
    public const string DiskTranslations = "disk_translations";
    public const string InjectionKeys = "injection_keys";
    public const string InjectionApplied = "injection_applied";
    public const string XmlFingerprint = "xml_fingerprint";
    public const string LanguagesFingerprint = "languages_fingerprint";
    public const string AssembliesFingerprint = "assemblies_fingerprint";
    public const string XmlWritten = "xml_written";
    public const string XmlWrittenText = "xml_written_text";
    public const string PostPatchXml = "post_patch_xml";
    public const string PostPatchInheritance = "post_patch_inheritance";
    public const string TypeFields = "type_fields";
    public const string TruncationCauses = "truncation_causes";

    public static IReadOnlyList<LayerRow> Ledger(SnapshotDb db, RimConfig config, string snapshotName)
    {
        var export = ExportCommand(snapshotName);
        var m = db.Meta;
        return
        [
            DefsRow(db, snapshotName),
            EconomyRow(db, snapshotName),
            KeyedRow(db, snapshotName),
            DiskTranslationsRow(db, config, snapshotName),
            InjectionKeysRow(db, snapshotName),
            Bit(InjectionApplied, db.HasInjectionApplied, export,
                "没有译文行标有「已应用」：语言包里的每一行都算作生效"),
            new(XmlFingerprint, db.Content is null ? LayerState.Unmeasured : LayerState.Ok,
                db.Content is null ? export : null,
                db.Content is null ? "导出时未记录 Defs/Patches 指纹" : null),
            new(LanguagesFingerprint, db.Content is null ? LayerState.Unmeasured : LayerState.Ok,
                db.Content is null ? export : null,
                db.Content is null ? "导出时未记录 Languages/ 指纹" : null),
            new(AssembliesFingerprint, db.Content is null ? LayerState.Unmeasured : LayerState.Ok,
                db.Content is null ? export : null,
                db.Content is null ? "导出时未记录 mod 程序集的指纹" : null),
            XmlWrittenRow(db, snapshotName),
            Bit(XmlWrittenText, db.HasXmlWrittenText, export, "XML 叶节点上没有内联文本"),
            PostPatchXmlRow(db, snapshotName),
            PostPatchInheritanceRow(db, snapshotName),
            Bit(TypeFields, db.HasTypeFieldRows, export, "没有按类型的字段路径集"),
            TruncationCausesRow(db, snapshotName),
        ];
    }

    public static string ExportCommand(string snapshotName) => $"rimsearcher export --modlist {snapshotName}";

    public static string ImportCommand(SnapshotDb db, string snapshotName)
        => $"rimsearcher snapshot import {db.SourceFile ?? snapshotName + IntermediateFormat.FileExtension} --name {snapshotName}";

    public static LayerRow DefsRow(SnapshotDb db, string snapshotName)
        => db.DefCount() > 0
            ? new(Defs, LayerState.Ok, null, null)
            : new(Defs, LayerState.Empty, ExportCommand(snapshotName), "没有 def 行");

    public static LayerRow EconomyRow(SnapshotDb db, string snapshotName) => db.EconomyState switch
    {
        IntermediateFormat.EconomyStateOk => new(Economy, LayerState.Ok, null, null),
        IntermediateFormat.EconomyStateSkipped => new(Economy, LayerState.Skipped,
                    ExportCommand(snapshotName), "导出时带 --no-economy"),
        _ => new(Economy, LayerState.Unavailable,
                 db.EconomyError ?? "导出未记录原因",
                 "导出器在这一游戏构建上无法测量价格；其余各层齐备"),
    };

    public static LayerRow KeyedRow(SnapshotDb db, string snapshotName)
        => db.KeyedCount() > 0
            ? new(Keyed, LayerState.Ok, null, null)
            : new(Keyed, LayerState.Empty, ExportCommand(snapshotName),
                  "没有 Keyed 行：导出早于这一层，或导出时游戏未加载语言数据");

    public static LayerRow DiskTranslationsRow(SnapshotDb db, RimConfig config, string snapshotName)
    {
        if (db.Harvested) return new(DiskTranslations, LayerState.Ok, null, null);
        var import = ImportCommand(db, snapshotName);
        var next = config.ModRoots.Count > 0 ? import : ConfigureModRootsThen + import;
        return db.TranslationsHarvest == SnapshotSchema.TranslationsHarvestOff
            ? new(DiskTranslations, LayerState.Skipped, next, "导入时带 --no-harvest-translations")
            : new(DiskTranslations, LayerState.Unconfigured, next,
                  "导入时未配置 'mod_roots'，无处可扫");
    }

    public const string ConfigureModRootsThen = "在配置文件里设置 'mod_roots'，然后 ";

    public static LayerRow InjectionKeysRow(SnapshotDb db, string snapshotName)
        => Bit(InjectionKeys, db.HasInjectionKeyRows, ExportCommand(snapshotName),
               "没有注入键名单：译文表的键列无法填写");

    public static LayerRow XmlWrittenRow(SnapshotDb db, string snapshotName)
        => Bit(XmlWritten, db.HasXmlWrittenRows, ExportCommand(snapshotName), "没有 xml_written 行：「取自」列无法区分 XML 与运行时");

    public static LayerRow AssemblyCopyRow(string tree, string assembly)
        => new(AssemblyCopyPrefix + tree + "/" + assembly, LayerState.Missing, SyncCommand,
               "源码树旁没有程序集副本；元数据从已安装的程序集读取，该程序集的构建未必与 C# 源码出自同一次构建");

    public const string AssemblyCopyPrefix = "assembly_copy:";

    public static readonly CliJsonKey AssemblyCopyJsonKey = new("absent",
        "因源码树未留存副本而从已安装程序集读取元数据的程序集，每个一行：layer（'assembly_copy:' + 树/程序集）、" +
        "state 未建、next（在源码树旁留存副本的 sync 命令）；读到的每个程序集都有副本时为空。",
        Rows: true);

    public static LayerRow PostPatchXmlRow(SnapshotDb db, string snapshotName)
        => db.Meta.IndexesPostPatchXml
            ? new(PostPatchXml, LayerState.Ok, null, null)
            : new(PostPatchXml, LayerState.Unavailable, ExportCommand(snapshotName),
                  "导出器没有找到可读的 patch 后 XML 文档，因此 XML 反映的是 mod 的 XML 文件");

    public static LayerRow PostPatchInheritanceRow(SnapshotDb db, string snapshotName)
        => db.Meta.IndexesPostPatchXml ? new(PostPatchInheritance, LayerState.Ok, null, null)
         : new(PostPatchInheritance, LayerState.Unavailable, ExportCommand(snapshotName),
               "导出器没有找到可读的 patch 后 XML 文档，因此继承关系反映的是 mod 的 XML 文件");

    public static LayerRow TruncationCausesRow(SnapshotDb db, string snapshotName)
        => new(TruncationCauses, LayerState.Ok, null, null);

    public const string CallGraphPrefix = "call_graph:";
    public const string SourceTreePrefix = "source_tree:";
    public const string SyncCommand = "rimsearcher sources sync";

    public static IReadOnlyList<LayerRow> CallGraphRows(IReadOnlyList<string> treesWithout, int cap = 6)
    {
        const string why = "该树没有调用点表，或调用点表早于保留字段与类型访问的版本；sync 无需重新反编译即可建出调用点表";
        var rows = treesWithout.Take(cap)
            .Select(t => new LayerRow(CallGraphPrefix + t, LayerState.Missing, SyncCommand, why))
            .ToList();
        if (treesWithout.Count > cap)
            rows.Add(new LayerRow(CallGraphPrefix + Cjk.Join("另有", Tally.Complete(treesWithout.Count - cap).Render("source tree")), LayerState.Missing,
                                  "rimsearcher sources list", why));
        return rows;
    }

    public static LayerRow EmptySourceTreeRow(string tree)
        => new(SourceTreePrefix + tree, LayerState.Empty, SyncCommand,
               "目录存在但没有反编译文件；sync 按快照中 mod 加载的内容重建");

    private static LayerRow Bit(string layer, bool present, string next, string why)
        => present ? new(layer, LayerState.Ok, null, null) : new(layer, LayerState.Empty, next, why);

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
