using System.IO.Compression;
using System.Text;
using RimSearcher.Contract;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 确定性语料。字节级基线要有对照物,就不能拿真快照当输入 —— 真快照随游戏与 mod 更新而变,
/// 基线会天天红。这里用中间格式手工造一份小语料,走的是与真导出**完全同一条 import 路径**,
/// 所以建库逻辑本身照样被闸住(B 案把建库搬到 CLI 侧才有的便宜,06 分工一节)。
/// </summary>
public static class Fixture
{
    public const string Language = "ChineseSimplified";
    public const string GameVersion = "1.6.0000 rev1";

    private static readonly object Gate = new();
    private static string? _dbPath;

    /// <summary>造好一次,整个测试进程共用。</summary>
    public static string Db
    {
        get
        {
            lock (Gate)
            {
                if (_dbPath is not null && File.Exists(_dbPath)) return _dbPath;
                var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests");
                Directory.CreateDirectory(dir);
                var export = Path.Combine(dir, "fixture" + IntermediateFormat.FileExtension);
                WriteExport(export);
                var db = Path.Combine(dir, "fixture.db");
                new SnapshotImporter().Import(export, db);
                return _dbPath = db;
            }
        }
    }

    public static void WriteExport(string path, bool omitEndMarker = false, long? wrongRecordCount = null)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        long records = 0;

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "test")
            .Str(IntermediateFormat.KeyExportedAtUtc, "2026-01-01T00:00:00.0000000Z")
            .Str(IntermediateFormat.KeyGameVersion, GameVersion)
            .Str(IntermediateFormat.KeyLanguage, Language)
            .Raw(IntermediateFormat.KeyMods,
                "[" + new JsonLine().Str("package_id", "ludeon.rimworld").Str("name", "Core").Str("version", "1.6") + "," +
                      new JsonLine().Str("package_id", "test.mod").Str("name", "Test Mod").Str("version", "0.1") + "]")
            .Raw(IntermediateFormat.KeyLimits, new JsonLine().Int("max_field_depth", 6).ToString())
            .Str(IntermediateFormat.KeyModSettingsHash, "")
            .ToString());
        records++;

        void Def(string type, string name, string? label, string mod, string file, bool generated,
                 int truncated, params (string Path, string Value)[] fields)
        {
            var pairs = fields.Select(f => new KeyValuePair<string, string>(f.Path, f.Value)).ToList();
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
                .Str(IntermediateFormat.KeyDefType, type)
                .Str(IntermediateFormat.KeyDefName, name)
                .Str(IntermediateFormat.KeyLabel, label ?? "")
                .Str(IntermediateFormat.KeyDescription, "")
                .Str(IntermediateFormat.KeySourceMod, mod)
                .Str(IntermediateFormat.KeySourceFile, file)
                .Bool(IntermediateFormat.KeyGenerated, generated)
                .Str(IntermediateFormat.KeyClass, "Verse." + type)
                .Pairs(IntermediateFormat.KeyFields, pairs)
                .Int(IntermediateFormat.KeyFieldsTruncated, truncated)
                .ToString());
            records++;
        }

        Def("ThingDef", "Apparel_ShieldBelt", "shield belt", "ludeon.rimworld", "Apparel_Belts.xml", false, 0,
            ("thingClass", "RimWorld.Apparel"),
            ("comps[0].compClass", "RimWorld.CompShield"),
            ("comps[0].props.energyMax", "0.5"),
            ("statBases[0].stat", "MarketValue"),
            // 噪声:末段匹配应把这两条挡掉(02-2 的唯一产地在 import 侧)
            ("shortHash", "12345"),
            ("comps[0].index", "0"),
            ("modContentPack.name", "Core"));

        Def("ThingDef", "Bullet_Revolver", "revolver bullet", "ludeon.rimworld", "Projectiles_Guns.xml", false, 3,
            ("thingClass", "RimWorld.Bullet"),
            ("projectile.damageAmountBase", "12"));

        Def("ThingDef", "Meat_Muffalo", "muffalo meat", "ludeon.rimworld",
            IntermediateFormat.ImpliedDefsSourceFile, true, 0,
            ("thingClass", "Verse.ThingWithComps"),
            ("ingestible.foodType", "Meat"));

        Def("HediffDef", "Anesthetic", "anesthetic", "ludeon.rimworld", "Hediffs_Local.xml", false, 0,
            ("hediffClass", "Verse.HediffWithComps"));

        Def("ThingDef", "TestModGun", "test gun", "test.mod", "Guns.xml", false, 0,
            ("thingClass", "RimWorld.Apparel"),
            ("comps[0].compClass", "RimWorld.CompShield"));

        void Injection(string defName, string path, string translated, string original)
        {
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDefInjection)
                .Str(IntermediateFormat.KeyDefType, "ThingDef")
                .Str(IntermediateFormat.KeyDefName, defName)
                .Str(IntermediateFormat.KeyPath, path)
                .Str(IntermediateFormat.KeyTranslated, translated)
                .Str(IntermediateFormat.KeyOriginal, original)
                .ToString());
            records++;
        }

        Injection("Apparel_ShieldBelt", "label", "护盾腰带", "shield belt");

        if (!omitEndMarker)
        {
            records++;
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                .Int(IntermediateFormat.KeyRecords, wrongRecordCount ?? records)
                .Int(IntermediateFormat.KeyDefs, 5)
                .Int(IntermediateFormat.KeyInjections, 1)
                .ToString());
        }

        w.Flush();
    }

    /// <summary>跑一次 CLI(进程内),返回 stdout / stderr / 退出码。</summary>
    public static (string Stdout, string Stderr, int Code) Run(params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var all = new List<string>(argv) { "--db", Db, "--config", NoConfigPath };
        var code = RimSearcher.Cli.Runner.Run(all, stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), code);
    }

    /// <summary>指向一个不存在的配置文件 —— 测试不许读本机 config。</summary>
    public static string NoConfigPath => Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "no-such-config.toml");
}
