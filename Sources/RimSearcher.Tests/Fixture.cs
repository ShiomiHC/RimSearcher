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

    /// <summary>
    /// 两份快照住在一起的目录。<c>snapshot_dir</c> 指着它 ——
    /// 不指,零结果的跨快照分流(R10)就会去开**本机真实**的快照,测试从此依赖这台机器。
    /// </summary>
    public static string SnapshotDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

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
                var db = Path.Combine(SnapshotDir, "fixture.db");
                new SnapshotImporter().Import(export, db);

                // 第二份快照:R10 的落点。它有一个 fixture 里没有的 def,
                // 于是「这个名字不在你问的这份里,但在 other 那份里」这句话有地方可验。
                var otherExport = Path.Combine(dir, "other" + IntermediateFormat.FileExtension);
                WriteOtherExport(otherExport);
                new SnapshotImporter().Import(otherExport, Path.Combine(SnapshotDir, "other.db"));

                return _dbPath = db;
            }
        }
    }

    /// <summary>
    /// 另一份快照的语料。刻意只有一个 def,而且是 fixture 里没有的名字 ——
    /// 它存在的唯一理由是让「换一份快照就能拿到」这句话可判定。
    /// </summary>
    private static void WriteOtherExport(string path)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "test")
            .Str(IntermediateFormat.KeyExportedAtUtc, "2026-01-02T00:00:00.0000000Z")
            .Str(IntermediateFormat.KeyGameVersion, GameVersion)
            .Str(IntermediateFormat.KeyLanguage, Language)
            .Raw(IntermediateFormat.KeyMods,
                "[" + new JsonLine().Str("package_id", "ludeon.rimworld").Str("name", "Core").Str("version", "1.6") + "," +
                      new JsonLine().Str("package_id", "other.mod").Str("name", "Other Mod").Str("version", "0.1") + "]")
            .Raw(IntermediateFormat.KeyLimits, new JsonLine().Int("max_field_depth", 6).ToString())
            .Str(IntermediateFormat.KeyModSettingsHash, "")
            .ToString());

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindDef)
            .Str(IntermediateFormat.KeyDefType, "ThingDef")
            .Str(IntermediateFormat.KeyDefName, "OnlyInOtherSnapshot")
            .Str(IntermediateFormat.KeyLabel, "only in other")
            .Str(IntermediateFormat.KeyDescription, "")
            .Str(IntermediateFormat.KeySourceMod, "other.mod")
            .Str(IntermediateFormat.KeySourceFile, "Other.xml")
            .Bool(IntermediateFormat.KeyGenerated, false)
            .Str(IntermediateFormat.KeyClass, "Verse.ThingDef")
            .Pairs(IntermediateFormat.KeyFields, [new KeyValuePair<string, string>("thingClass", "Verse.ThingWithComps")])
            .Int(IntermediateFormat.KeyFieldsTruncated, 0)
            .ToString());

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
            .Int(IntermediateFormat.KeyRecords, 3)
            .Int(IntermediateFormat.KeyDefs, 1)
            .Int(IntermediateFormat.KeyInjections, 0)
            .Int(IntermediateFormat.KeyXmlNodes, 0)
            .ToString());

        w.Flush();
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
        //
        // **这两行的先后是有承重的**:两条译文的 def_type 是 ThingDef,而 ThingDef 写在前面。
        // 导入侧原先按 defName 建「名字 → 单个 id」的表,后写的顶掉先写的,于是译文会绑到
        // 后写的 StatDef 上 —— 反过来写,同一份错代码就碰巧绑对了,闸也就白立了。
        Def("ThingDef", "Firefoam", "firefoam", "ludeon.rimworld", "Buildings_Special.xml", false, 0,
            ("thingClass", "RimWorld.Building"),
            ("statBases[0].stat", "MarketValue"));

        Def("StatDef", "Firefoam", null, "ludeon.rimworld", "Stats_Basics.xml", false, 0);

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

    /// <summary>
    /// 跑一次 CLI(进程内),返回 stdout / stderr / 退出码。
    ///
    /// <c>--db</c> 与 <c>--config</c> 追加在**后面**,而用例自己写的同名参数在前面 ——
    /// 于是「这条用例要一份别的配置」不必新开一个跑法,写进 argv 就行,而且基线里的
    /// 命令行回显是逐字的那一份(追加的绝对路径含机器名,不能进基线)。
    /// </summary>
    public static (string Stdout, string Stderr, int Code) Run(params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var all = new List<string>(argv);
        if (!argv.Contains("--db")) { all.Add("--db"); all.Add(Db); }
        if (!argv.Contains("--config")) { all.Add("--config"); all.Add(SourcesConfigPath); }
        var code = RimSearcher.Cli.Runner.Run(all, stdout, stderr);
        return (stdout.ToString(), stderr.ToString(), code);
    }

    /// <summary>指向一个不存在的配置文件 —— 测试不许读本机 config。</summary>
    public static string NoConfigPath => Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "no-such-config.toml");

    private static string? _sourcesConfig;

    /// <summary>
    /// 默认配置:只写 <c>decompiled_dir</c>,指向下面那棵造出来的反编译树。
    /// 别的键一律不写 —— 写了就会有基线依赖本机游戏目录。
    /// </summary>
    public static string SourcesConfigPath
    {
        get
        {
            lock (Gate)
            {
                if (_sourcesConfig is not null && File.Exists(_sourcesConfig)) return _sourcesConfig;
                var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests");
                Directory.CreateDirectory(dir);
                WriteSourceTree(Path.Combine(dir, "sources"));
                _ = Db;   // 先把两份快照造出来,snapshot_dir 才指得到东西
                var path = Path.Combine(dir, "sources-config.toml");
                File.WriteAllText(path,
                    "decompiled_dir = '" + Path.Combine(dir, "sources") + "'\n" +
                    // 不写这一行,零结果的跨快照分流会去开 ~/.rimsearcher/snapshots 下的真快照。
                    "snapshot_dir = '" + SnapshotDir + "'\n",
                    new UTF8Encoding(false));
                return _sourcesConfig = path;
            }
        }
    }

    /// <summary>
    /// <c>code-search</c> 的语料:一棵小反编译树。四道闸各要一个落点,所以形状是刻意的:
    ///
    ///   vanilla/          三个文件,排在最前(它是问题的默认语境,被截掉代价最大)
    ///     Verse/ThingComp.cs      连着好几行命中 —— 上下文窗口重叠合并的落点
    ///     Verse/Widgets.cs        同一文件里多条命中 —— 单文件上限的落点
    ///     RimWorld/CompShield.cs
    ///   .git/objects/Sneaky.cs   **必须一次都不被读到**:它匹配 *.cs,却不是源码树
    ///   zz.emptytree/            一个文件都没有 —— 不许被点名成「没读到的树」
    ///   zz.othermod/Patches.cs   排在 vanilla 之后 —— 文件数上限咬下去时它是「没读到」的那棵
    /// </summary>
    private static void WriteSourceTree(string root)
    {
        void File_(string rel, params string[] lines)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        // 第 4~8 行连着命中 public,-C 1 时五个窗口两两重叠 —— 不合并就会把每行印三遍。
        File_("vanilla/Verse/ThingComp.cs",
            "namespace Verse",
            "{",
            "\tpublic class ThingComp",
            "\t{",
            "\t\tpublic ThingWithComps parent;",
            "\t\tpublic CompProperties props;",
            "\t\tpublic virtual void PostSpawnSetup(bool respawningAfterLoad)",
            "\t\t{",
            "\t\t}",
            "\t}",
            "}");

        File_("vanilla/Verse/Widgets.cs",
            "namespace Verse",
            "{",
            "\tpublic static class Widgets",
            "\t{",
            "\t\tpublic static void Label(Rect rect, string label)",
            "\t\t{",
            "\t\t}",
            "\t}",
            "}");

        File_("vanilla/RimWorld/CompShield.cs",
            "namespace RimWorld",
            "{",
            "\tpublic class CompShield : ThingComp",
            "\t{",
            "\t}",
            "}");

        File_(".git/objects/Sneaky.cs",
            "public class Sneaky : ThingComp");

        File_("zz.othermod/Patches.cs",
            "namespace OtherMod",
            "{",
            "\tpublic class MyComp : ThingComp",
            "\t{",
            "\t}",
            "}");

        // read 的语料。轮廓靠配平大括号,于是每一种「括号看起来在那儿其实不在」的写法
        // 都要有一份:字符串里的 }、注释里的 {、字符字面量、逐字字符串里的双写引号。
        // 另外三件事各要一个落点:方法体里的 if(…){ 不许变成一个叫 if 的成员;
        // 带初值的字段不许被初值里的括号认成方法(`= Make(…)` 曾经报出 Make);
        // 同名成员分属两个类型 —— --type 就是为分开它们存在的。
        File_("vanilla/Verse/Outline.cs",
            "using System;",
            "",
            "namespace Verse",
            "{",
            "\t// A brace in a comment: {",
            "\t[StaticConstructorOnStartup]",
            "\tpublic class Outer",
            "\t{",
            "\t\tprivate static readonly string Marker = Make(\"} not a brace {\");",
            "",
            "\t\tprivate char Open = '{';",
            "",
            "\t\tpublic string Verbatim => @\"he said \"\"} \"\" and left\";",
            "",
            "\t\tpublic void Shared(int n)",
            "\t\t{",
            "\t\t\tif (n > 0)",
            "\t\t\t{",
            "\t\t\t\tConsole.WriteLine(Marker);",
            "\t\t\t}",
            "\t\t}",
            "",
            "\t\tpublic class Inner",
            "\t\t{",
            "\t\t\tpublic void Shared(int n)",
            "\t\t\t{",
            "\t\t\t}",
            "\t\t}",
            "\t}",
            "}");

        // 同名文件的第二份。read 收基名,而基名在两棵树里撞车时选错的代价是整条结论作废
        // (mod 的覆盖版被当成原版读下去,输出里逐字看不出区别)—— 所以那条路不选,只列。
        // 正文刻意不含 "public" 与 ": ThingComp":它进了 code-search 的候选集,内容一撞
        // 那几份基线就不只是数字变化了。
        File_("zz.othermod/Outline.cs",
            "namespace OtherMod",
            "{",
            "\tclass Outer",
            "\t{",
            "\t}",
            "}");

        Directory.CreateDirectory(Path.Combine(root, "zz.emptytree"));
    }
}
