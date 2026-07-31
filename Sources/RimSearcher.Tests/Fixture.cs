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

    /// <summary>
    /// 只拼路径,不建库。<see cref="Db"/> 的实现体自己要用它 —— 走公开的
    /// <see cref="CoreDb"/> 会重入 <c>Db</c>(<c>lock</c> 对同一线程放行,而
    /// <c>_dbPath</c> 到最后一行才赋值),于是同一份 <c>fixture.db.tmp</c> 被两层
    /// <c>Import</c> 抢着写。
    /// </summary>
    private static string CoreDbPath => Path.Combine(SnapshotDir, "core.db");

    /// <summary>
    /// 名字本身就是一个 scope 组名的那份快照 —— 撞名提示的落点。
    ///
    /// 取路径就把库建出来。原先它只拼路径,靠调用方经由 <see cref="Run"/> 缺省的
    /// <c>--config</c> 走到 <see cref="SourcesConfigPath"/> 里那句 <c>_ = Db</c> 才有库 ——
    /// 一条同时显式传 <c>--db</c> 与 <c>--config</c> 的用例就会拿到一个不存在的文件,
    /// 而那看起来会像一次偶发。
    /// </summary>
    public static string CoreDb { get { _ = Db; return CoreDbPath; } }

    /// <summary>语料的导出文件本身 —— 要自己跑一遍 `snapshot import` 的用例用它当输入。</summary>
    public static string ExportPath
    {
        get { _ = Db; return Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "fixture" + IntermediateFormat.FileExtension); }
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

                // 第三份:文件名就是一个 scope 组名。五轮 F3 的落点 —— 快照叫 core/vanilla
                // 与 `--scope core` 是两回事,而两者在句子里长得一模一样。内容刻意与 other
                // 不同名,免得跨快照点名那句话多出一个落点。
                var coreExport = Path.Combine(dir, "core" + IntermediateFormat.FileExtension);
                WriteOtherExport(coreExport, "OnlyInCoreSnapshot", "CoreMod.CompOnlyInCore");
                new SnapshotImporter().Import(coreExport, CoreDbPath);

                return _dbPath = db;
            }
        }
    }

    /// <summary>
    /// 另一份快照的语料。刻意只有一个 def,而且是 fixture 里没有的名字 ——
    /// 它存在的唯一理由是让「换一份快照就能拿到」这句话可判定。
    /// </summary>
    private static void WriteOtherExport(string path, string defName = "OnlyInOtherSnapshot",
                                         string compClass = "OtherMod.CompOnlyElsewhere")
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "0.1.0")
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
            .Str(IntermediateFormat.KeyDefName, defName)
            .Str(IntermediateFormat.KeyLabel, "only in other")
            .Str(IntermediateFormat.KeyDescription, "")
            .Str(IntermediateFormat.KeySourceMod, "other.mod")
            .Str(IntermediateFormat.KeySourceFile, "Other.xml")
            .Bool(IntermediateFormat.KeyGenerated, false)
            .Str(IntermediateFormat.KeyClass, "Verse.ThingDef")
            // 第二个字段是跨快照兜底的落点:这个值在 fixture 那份里一次都不出现,于是
            // 「本快照没有」与「哪儿都没有」在 find 的输出上分不分得开,有地方可验。
            .Fields(IntermediateFormat.KeyFields,
                [new ExportedField("thingClass", "Verse.ThingWithComps", DefaultState.Differs),
                 new ExportedField("comps[0].compClass", compClass, DefaultState.Differs)])
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

    public static void WriteExport(string path, bool omitEndMarker = false, long? wrongRecordCount = null,
                                   int? formatVersion = null)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };

        long records = 0;

        w.WriteLine(new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
            .Int(IntermediateFormat.KeyFormatVersion, formatVersion ?? IntermediateFormat.FormatVersion)
            .Str(IntermediateFormat.KeyExporterVersion, "0.2.0")
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
                 int truncated, params (string Path, string Value, int Default)[] fields)
            => DefAs(type, "Verse." + type, name, label, mod, file, generated, truncated, fields);

        // 运行时 class 与 def_type 不是一回事:游戏只给「祖先链上没有非抽象 Def」的类型建库,
        // 所以子类型的 def 全落在基类桶里。语料里必须有这么一桶,否则 list --class 那条路没人守。
        void DefAs(string type, string cls, string name, string? label, string mod, string file, bool generated,
                   int truncated, params (string Path, string Value, int Default)[] fields)
        {
            var pairs = fields.Select(f => new ExportedField(f.Path, f.Value, f.Default)).ToList();
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
                .Fields(IntermediateFormat.KeyFields, pairs)
                .Int(IntermediateFormat.KeyFieldsTruncated, truncated)
                .ToString());
            records++;
        }

        // 第三位是 R1 的语料:一条值与「这个类型刚 new 出来时」的关系。
        //   compClass 取 Same —— CompProperties_Shield 的声明里就写着 typeof(CompShield),
        //   没有任何人在 XML 里挑过它;而 energyMax 是作者写的。两条挨在一起、在旧输出里
        //   逐字同形,正是四个错结论的产地。
        Def("ThingDef", "Apparel_ShieldBelt", "shield belt", "ludeon.rimworld", "Apparel_Belts.xml", false, 0,
            // 引擎级默认:ThingDef.ResolveReferences 给**每一个** ThingDef 都塞这个值,
            // 于是 code_default 印 no 而没有任何人挑过它。第八轮 ep34 全场最贵的一次
            // 险出错就在这上面 —— 九个 ThingDef 全都带着它,shared_values 才有落点。
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "RimWorld.Apparel", DefaultState.Differs),
            ("comps[0].compClass", "RimWorld.CompShield", DefaultState.Same),
            ("comps[0].props.energyMax", "0.5", DefaultState.Differs),
            // 同块的第二个「有人设过」的字段 —— 兄弟提示的落点。实测里
            // minFuelCost=50 盖掉同块的 fuelPerTile=3(16 倍),而只列出后者的那张表
            // 干净、计数明确、一条警告都没有。
            ("comps[0].props.energyLossPerDamage", "0.033", DefaultState.Differs),
            // 第二个**不叫 comps** 的块,同样有两个有人设过的字段 —— 兄弟提示的措辞
            // 原先把块名写死成 comps[N],而 ContainerPrefix 对任何带下标的层都成立。
            ("statBases[0].stat", "MarketValue", DefaultState.Differs),
            ("statBases[0].value", "120", DefaultState.Differs),
            // 噪声:末段匹配应把这两条挡掉(02-2 的唯一产地在 import 侧)
            ("shortHash", "12345", DefaultState.Differs),
            ("comps[0].index", "0", DefaultState.Same),
            ("modContentPack.name", "Core", DefaultState.Differs));

        // burstCount 是 R1 报告里那一行的形状:**字段名与提问一字不差,值却是代码默认值**。
        // 它必须在语料里,否则「--path 点了名的东西不许被过滤掉」那道闸没有落点。
        // speed 取 Unknown —— 三态里最容易被顺手并进某一边的那个。
        Def("ThingDef", "Bullet_Revolver", "revolver bullet", "ludeon.rimworld", "Projectiles_Guns.xml", false, 3,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "RimWorld.Bullet", DefaultState.Differs),
            ("projectile.damageAmountBase", "12", DefaultState.Differs),
            ("projectile.burstCount", "1", DefaultState.Same),
            ("projectile.speed", "70", DefaultState.Unknown));

        Def("ThingDef", "Meat_Muffalo", "muffalo meat", "ludeon.rimworld",
            IntermediateFormat.ImpliedDefsSourceFile, true, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "Verse.ThingWithComps", DefaultState.Differs),
            ("ingestible.foodType", "Meat", DefaultState.Differs));

        // comps[0].compClass 与上面那个 ThingDef 同路径,而**只有 ThingDef 那边有被截过的 def**
        // (Bullet_Revolver)。于是 `values compClass --type HediffDef` 是「表已经滤干净、
        // 脚注还在说别的类型」那道闸唯一的落点(第七轮 T6)。
        Def("HediffDef", "Anesthetic", "anesthetic", "ludeon.rimworld", "Hediffs_Local.xml", false, 0,
            ("hediffClass", "Verse.HediffWithComps", DefaultState.Differs),
            ("comps[0].compClass", "Verse.HediffComp_Disappears", DefaultState.Differs));

        Def("ThingDef", "TestModGun", "test gun", "test.mod", "Guns.xml", false, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "RimWorld.Apparel", DefaultState.Differs),
            // 列表元素的运行时类型(导出器 0.2.0 起发的那一维,第七轮 T1)。这是主快照里
            // 唯一一条 `.Class`,而 other 那份标着 0.1.0 —— 于是「量过了、没人用」与
            // 「这份快照根本没量」两个世界各有一个落点,不然那道闸只守得住一半。
            ("comps[0].Class", "RimWorld.CompProperties_Shield", DefaultState.Differs),
            ("comps[0].compClass", "RimWorld.CompShield", DefaultState.Same));

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
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "RimWorld.Building", DefaultState.Differs),
            ("statBases[0].stat", "MarketValue", DefaultState.Differs));

        Def("StatDef", "Firefoam", null, "ludeon.rimworld", "Stats_Basics.xml", false, 0);

        // 两件事一份语料,而两件事都是「表看着齐全,分不开的那一维不在表里」:
        //
        // ① label 与上面那个 ThingDef Firefoam **逐字相同、def 类型也相同** —— 第六轮 C42
        //    的形状(TrapSpringChance 与 PawnTrapSpringChance 的简中 label 都是「陷阱触发率」)。
        //    同名跨类型的那一对(Firefoam 自己)在表里当场分得开,不是同一件事,所以要各一份。
        // ② statFactors 这一条让 `find stat MarketValue` 横跨两种路径形状 —— C31 的
        //    静默假阴性:1229 行里混着 1 行 statFactors,而拿它做集合差的人不会逐行核对 path。
        Def("ThingDef", "FoamPopper", "firefoam", "ludeon.rimworld", "Buildings_Special.xml", false, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs),
            ("thingClass", "RimWorld.Building", DefaultState.Differs),
            ("statFactors[0].stat", "MarketValue", DefaultState.Differs));

        // 三级匹配的语料:查 "VoidNode" 时 FTS 命中前两个(词首对齐),第三个只有子串扫描找得到。
        // 混合命中是「N of M 的 M 不许随 --limit 变」那道闸唯一的落点 —— 少了它,
        // 「把没显示出来的 FTS 命中当成新增」与「先截断再累加」两个方向的错都没人守。
        Def("ThingDef", "VoidNode", "void node", "test.mod", "Anomaly.xml", false, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs));
        Def("ThingDef", "VoidNodeShard", "void node shard", "test.mod", "Anomaly.xml", false, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs));
        Def("ThingDef", "GleamingVoidNode", "gleaming void node", "test.mod", "Anomaly.xml", false, 0,
            ("soundImpactDefault", "BulletImpact_Ground", DefaultState.Differs),
            ("soundDrop", "Standard_Drop", DefaultState.Differs),
            ("soundPickup", "Standard_Pickup", DefaultState.Differs),
            ("soundInteract", "Standard_Pickup", DefaultState.Differs));

        // 异构桶:两个 def 的 def_type 都是 TestBaseDef,运行时 class 却不同。
        DefAs("TestBaseDef", "Verse.TestVariantDef", "VariantOne", "variant one", "test.mod", "Variants.xml", false, 0,
            ("workerClass", "Verse.TestWorker", DefaultState.Differs));
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

        void Keyed(string key, string translated, string original, bool placeholder, string file, int line)
        {
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindKeyed)
                .Str(IntermediateFormat.KeyKeyedKey, key)
                .Str(IntermediateFormat.KeyTranslated, translated)
                .Str(IntermediateFormat.KeyOriginal, original)
                .Str(IntermediateFormat.KeySourceFile, file)
                .Int(IntermediateFormat.KeySourceLine, line)
                .Bool(IntermediateFormat.KeyPlaceholder, placeholder)
                .ToString());
            records++;
        }

        // 界面文案语料。四种形态,因为四条判据各有各的错法:
        //   CannotUseNoPower  正常双语 —— 代码里那一行 .Translate() 附译文的落点
        //   TodoKey           占位 —— 「有这个 key 但没译」不许与「有译文」同形
        //   OnlyEnglishKey    没有英文那一侧(original 空)—— 双语表里缺一列不许看起来像缺数据
        //   JumpToLocation / ClickToJump  同一句中文由两个 key 各自承载。这不是边角:
        //     真数据(vanilla 1.6)里「转至事件发生地点」就同时是 JumpToLocation 与
        //     ClickToJumpToProblem。少了它,「按文案反查」那两条路都会挑第一个说成
        //     「就是这个 key」—— 而表里另一行长得一模一样,挑错了看不出来。
        Keyed("CannotUseNoPower", "没有电力", "No power", false, "Misc.xml", 12);
        Keyed("TodoKey", "TODO", "Not translated yet", true, "Misc.xml", 34);
        Keyed("OnlyEnglishKey", "只有中文这一侧", "", false, "Gui.xml", 7);
        Keyed("JumpToLocation", "转至此处", "Jump to location", false, "Letters.xml", 6);
        Keyed("ClickToJump", "转至此处", "Click to jump", false, "Alerts.xml", 5);

        // 过线的填充批。两道闸共用,而两道都**必须**有一批过 Limits.MaxLimit(2000)的语料:
        //   `--limit all` 解除行上限 —— 语料不过线,「夹到 2000」与「全给」印出来一模一样;
        //   `--placeholders` 在 SQL 里筛 —— 占位排在这批的最末一条,于是「取完这一页再筛」
        //     会拿着第一页的零去否定全部 2100 条,而那正是这个开关唯一用途上的最强否定句。
        // 全部共用 original 里的 filler 一词,与上面五条的查询词不相交(基线不受牵连)。
        for (var i = 0; i < 2100; i++)
            Keyed($"FillerKey{i:0000}", $"填充{i}", "filler line", i == 2099, "Filler.xml", i + 1);

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
                .Int(IntermediateFormat.KeyKeyedCount, 5)
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
                // 一份与快照逐字对得上的 ModsConfig.xml —— 于是「快照与当前游戏一致吗」
                // 那条分支在测试里到得了。到不了的分支上挂的话红不了(第七轮 T7)。
                var modsConfig = Path.Combine(dir, "ModsConfig.xml");
                File.WriteAllText(modsConfig,
                    "<ModsConfigData><version>1.6.0000</version><activeMods>" +
                    "<li>ludeon.rimworld</li><li>test.mod</li>" +
                    "</activeMods></ModsConfigData>\n", new UTF8Encoding(false));

                var path = Path.Combine(dir, "sources-config.toml");
                File.WriteAllText(path,
                    "decompiled_dir = '" + Path.Combine(dir, "sources") + "'\n" +
                    "mods_config = '" + modsConfig + "'\n" +
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

        // 末尾三行是 keyed 那一层的落点,而它们**刻意不含 `public` 或 `: ThingComp`** ——
        // 现有 12 份 code-search 基线的 pattern 只有这两个和一个必然零命中的词,
        // 于是这三行不改动任何一份既有基线,只给新的 ui_text 一节提供语料。
        // 三行各是一种形态:字面量能查到 / 字面量查不到 / 运行时拼出来的 key。
        File_("vanilla/Verse/Widgets.cs",
            "namespace Verse",
            "{",
            "\tpublic static class Widgets",
            "\t{",
            "\t\tpublic static void Label(Rect rect, string label)",
            "\t\t{",
            "\t\t\tDraw(rect, \"CannotUseNoPower\".Translate());",
            "\t\t\tDraw(rect, \"NoSuchUiKey\".Translate());",
            "\t\t\tDraw(rect, (\"Stat_\" + label).Translate());",
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
