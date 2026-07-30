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
            => DefAs(type, "Verse." + type, name, label, mod, file, generated, truncated, fields);

        // 运行时 class 与 def_type 不是一回事:游戏只给「祖先链上没有非抽象 Def」的类型建库,
        // 所以子类型的 def 全落在基类桶里。语料里必须有这么一桶,否则 list --class 那条路没人守。
        void DefAs(string type, string cls, string name, string? label, string mod, string file, bool generated,
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
                .Str(IntermediateFormat.KeyClass, cls)
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

        // 同名跨 def 类型 —— RimWorld 常态,也是 JSON 撞键静默丢数据那条的唯一语料。
        // 一个有字段一个没有,是为了让「后写的把先写的盖成空」当场暴露。
        //
        // 三轮 R2 还要求这两边**资产不对称**:下面的 XML 节点与两条译文都只挂在 ThingDef
        // 那一边,StatDef 这边一无所有。于是 `get Firefoam --type StatDef` 一旦按 defName
        // 关联,就会在 StatDef 的标题块下印出 ThingDef 的父节点与描述译文 —— 那正是
        // S8 险些交出的错答案(字段表刚说完「没有 description」,紧接着一条 description 译文)。
        Def("StatDef", "Firefoam", null, "ludeon.rimworld", "Stats_Basics.xml", false, 0);

        Def("ThingDef", "Firefoam", "firefoam", "ludeon.rimworld", "Buildings_Special.xml", false, 0,
            ("thingClass", "RimWorld.Building"),
            ("statBases[0].stat", "MarketValue"));

        // 三级匹配的语料:查 "VoidNode" 时 FTS 命中前两个(词首对齐),第三个只有子串扫描找得到。
        // 混合命中是「N of M 的 M 不许随 --limit 变」那道闸唯一的落点 —— 少了它,
        // 「把没显示出来的 FTS 命中当成新增」与「先截断再累加」两个方向的错都没人守。
        Def("ThingDef", "VoidNode", "void node", "test.mod", "Anomaly.xml", false, 0);
        Def("ThingDef", "VoidNodeShard", "void node shard", "test.mod", "Anomaly.xml", false, 0);
        Def("ThingDef", "GleamingVoidNode", "gleaming void node", "test.mod", "Anomaly.xml", false, 0);

        // 异构桶:两个 def 的 def_type 都是 TestBaseDef,运行时 class 却不同。
        DefAs("TestBaseDef", "Verse.TestVariantDef", "VariantOne", "variant one", "test.mod", "Variants.xml", false, 0,
            ("workerClass", "Verse.TestWorker"));
        DefAs("TestBaseDef", "Verse.TestBaseDef", "PlainOne", "plain one", "test.mod", "Variants.xml", false, 0);

        void XmlNode(string defType, string name, string parentName, bool isAbstract,
                     string defName, string mod, string file, int patchOps)
        {
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindXmlNode)
                .Str(IntermediateFormat.KeyDefType, defType)
                .Str(IntermediateFormat.KeyName, name)
                .Str(IntermediateFormat.KeyParentName, parentName)
                .Bool(IntermediateFormat.KeyAbstract, isAbstract)
                .Str(IntermediateFormat.KeyDefName, defName)
                .Str(IntermediateFormat.KeyLabel, "")
                .Str(IntermediateFormat.KeySourceMod, mod)
                .Str(IntermediateFormat.KeySourceFile, file)
                .Int(IntermediateFormat.KeyPatchOps, patchOps)
                .ToString());
            records++;
        }

        // 继承层语料。四种形态各一条,因为四条判据各有各的错法:
        //   BaseBullet    抽象、有子、被 patch 点名 —— 逐条申报那条闸的落点
        //   BaseProjectile 抽象、是 BaseBullet 的父 —— 往上走的链
        //   Bullet_Revolver 具体 def 且有父 —— get 的 inherits_from 那一行
        //   OrphanChild   父节点不在本快照里 —— 「断链」与「到根了」必须分得开
        XmlNode("ThingDef", "BaseProjectile", "", true, "", "ludeon.rimworld", "Projectiles_Guns.xml", 0);
        XmlNode("ThingDef", "BaseBullet", "BaseProjectile", true, "", "ludeon.rimworld", "Projectiles_Guns.xml", 2);
        XmlNode("ThingDef", "", "BaseBullet", false, "Bullet_Revolver", "ludeon.rimworld", "Projectiles_Guns.xml", 0);
        XmlNode("ThingDef", "", "BaseFromSomeDisabledMod", false, "TestModGun", "test.mod", "Guns.xml", 0);

        // R2 的语料另一半:只有 ThingDef 那个 Firefoam 有父节点,StatDef 那个没有。
        XmlNode("ThingDef", "", "BaseProjectile", false, "Firefoam", "ludeon.rimworld", "Buildings_Special.xml", 0);

        // 桶名不一致:VariantOne 的 def 落在 TestBaseDef 桶(异构桶语料),而它的 XML 根元素
        // 是 TestVariantDef。实测本机快照有 26 个这种 def(Blindhealer 的
        // CreepJoinerFormKindDef → PawnKindDef 等),R2 若改成「def_type 必须相等」就会
        // 把它们的 inherits_from 整批弄丢 —— 串味换成丢数据,正是它要修的那类错。
        XmlNode("TestVariantDef", "", "BaseProjectile", false, "VariantOne", "test.mod", "Variants.xml", 0);

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

        // 这两条的 def_type 是 ThingDef(Injection 写死的),而同名的 StatDef Firefoam
        // 连 description 都没有 —— 按 defName 关联时它们会跑到 StatDef 的输出里去。
        Injection("Firefoam", "label", "灭火泡沫", "firefoam");
        Injection("Firefoam", "description", "一团灭火泡沫。", "A blob of firefoam.");

        if (!omitEndMarker)
        {
            records++;
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                .Int(IntermediateFormat.KeyRecords, wrongRecordCount ?? records)
                .Int(IntermediateFormat.KeyDefs, 5)
                .Int(IntermediateFormat.KeyInjections, 3)
                .Int(IntermediateFormat.KeyXmlNodes, 6)
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
