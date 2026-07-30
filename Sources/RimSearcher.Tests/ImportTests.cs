using RimSearcher.Contract;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 建库侧的闸。B 案把建库从游戏里搬到 CLI 侧,换来的正是这个:
/// 「一份中间格式进去,库里应该长什么样」可以在测试里跑,不用启动游戏。
/// </summary>
public class ImportTests
{
    /// <summary>测试不许读本机 config —— 指向一个不存在的路径,拿到的就是纯默认值。</summary>
    private static readonly Config.RimConfig NoConfig = Config.RimConfig.Load(Fixture.NoConfigPath);

    private static string Temp(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "import");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        if (File.Exists(p)) File.Delete(p);
        return p;
    }

    private static SnapshotDb Build(string tag, bool omitEnd = false, long? wrongCount = null)
        => BuildAt(tag, omitEnd, wrongCount).Db;

    /// <summary>
    /// 同 <see cref="Build"/>,外加库文件路径 —— 有些字段没有任何命令读它
    /// (<c>translations.def_id</c> 就是),要立闸只能自己开库看。
    /// </summary>
    private static (SnapshotDb Db, string Path) BuildAt(string tag, bool omitEnd = false, long? wrongCount = null,
                                                        IReadOnlyList<string>? modRoots = null)
    {
        var export = Temp(tag + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export, omitEnd, wrongCount);
        var db = Temp(tag + ".db");
        new SnapshotImporter { ModRoots = modRoots ?? [] }.Import(export, db);
        return (SnapshotDb.Open(db), db);
    }

    /// <summary>直接读 <c>translations</c>,包括查询侧不返回的 <c>def_id</c>。</summary>
    private static List<(string Path, long? DefId)> RawTranslations(string dbPath, string defName)
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT path, def_id FROM translations WHERE def_name = $n ORDER BY path";
        cmd.Parameters.AddWithValue("$n", defName);
        using var rd = cmd.ExecuteReader();
        var rows = new List<(string, long?)>();
        while (rd.Read()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetInt64(1)));
        return rows;
    }

    /// <summary>造一个只有语言文件的 mod 目录,给静态收割那条路当输入。</summary>
    private static string ModRootWith(string tag, params (string Key, string Text)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "import", tag + "-mods");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var mod = Path.Combine(root, "SomeTranslationMod");
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"),
            "<ModMetaData><packageId>test.mod</packageId></ModMetaData>");
        var inj = Path.Combine(mod, "Languages", Fixture.Language, "DefInjected");
        Directory.CreateDirectory(inj);
        File.WriteAllText(Path.Combine(inj, "Injected.xml"),
            "<LanguageData>" + string.Concat(entries.Select(e => $"<{e.Key}>{e.Text}</{e.Key}>")) + "</LanguageData>");
        return root;
    }

    // ---- 完整性:半份文件必须被拒,不许静默建成一个缺行的库 ----

    /// <summary>
    /// 游戏中途崩了、磁盘写满了,产出的就是一份没有结束标记的文件。把它导进去会得到一个
    /// **看起来正常但内容不全**的库 —— 后面每一次「没查到」都无从判断是真没有还是被截断了。
    /// 宁可拒绝。
    /// </summary>
    [Fact]
    public void 缺结束标记的导出文件被拒绝()
    {
        var export = Temp("noend" + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export, omitEndMarker: true);
        var ex = Assert.ThrowsAny<Exception>(() => new SnapshotImporter().Import(export, Temp("noend.db")));
        Assert.Contains("end", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>结束标记里的条数与实际读到的对不上,同样说明文件不完整。</summary>
    [Fact]
    public void 条数对不上的导出文件被拒绝()
    {
        var export = Temp("badcount" + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export, wrongRecordCount: 999);
        var ex = Assert.ThrowsAny<Exception>(() => new SnapshotImporter().Import(export, Temp("badcount.db")));
        Assert.Contains("999", ex.Message);
    }

    /// <summary>
    /// 上一版导出器写的文件必须被拒,而不是当成「这些字段都不是默认值」导进来 ——
    /// 那样建出来的库,每个 def 都长得像「处处被作者改过」,与真的处处被改过逐字同形(R1)。
    /// 消息要同时带两个版本号,否则读的人不知道该更新哪一边。
    /// </summary>
    [Fact]
    public void 上一版格式的导出文件被拒绝()
    {
        var export = Temp("oldformat" + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export, formatVersion: IntermediateFormat.FormatVersion - 1);
        var ex = Assert.ThrowsAny<Exception>(() => new SnapshotImporter().Import(export, Temp("oldformat.db")));
        Assert.Contains((IntermediateFormat.FormatVersion - 1).ToString(), ex.Message);
        Assert.Contains(IntermediateFormat.FormatVersion.ToString(), ex.Message);
    }

    /// <summary>被拒的导入不许留下半个库文件 —— 否则下次打开的是一份垃圾。</summary>
    [Fact]
    public void 导入失败不留下半成品库()
    {
        var export = Temp("noend2" + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export, omitEndMarker: true);
        var db = Temp("noend2.db");
        Assert.ThrowsAny<Exception>(() => new SnapshotImporter().Import(export, db));
        Assert.False(File.Exists(db), "A rejected import left a database behind.");
    }

    // ---- 噪声过滤(02-2:唯一产地在 import 侧)----

    [Fact]
    public void 噪声字段不进库()
    {
        using var db = Build("noise");
        var def = db.GetDefsNamed("Apparel_ShieldBelt").Single();
        var paths = db.Fields(def.Id, int.MaxValue).Rows.Select(f => f.Path).ToList();

        Assert.DoesNotContain("shortHash", paths);
        Assert.DoesNotContain("comps[0].index", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("modContentPack.", StringComparison.Ordinal));
    }

    [Fact]
    public void 真实字段照常进库()
    {
        using var db = Build("keep");
        var def = db.GetDefsNamed("Apparel_ShieldBelt").Single();
        var fields = db.Fields(def.Id, int.MaxValue).Rows.ToDictionary(f => f.Path, f => f.Value);

        Assert.Equal("RimWorld.Apparel", fields["thingClass"]);
        Assert.Equal("RimWorld.CompShield", fields["comps[0].compClass"]);
        Assert.Equal("0.5", fields["comps[0].props.energyMax"]);
    }

    /// <summary>
    /// generated 是 ImpliedDefs 的信号,**不是**噪声。滤掉它,00 论据 1 的那批
    /// 代码生成 def 就再也说不清自己从哪来。
    /// </summary>
    [Fact]
    public void 代码生成的def保留其来源标记()
    {
        using var db = Build("gen");
        var def = db.GetDefsNamed("Meat_Muffalo").Single();
        Assert.True(def.Generated);
        Assert.Equal(IntermediateFormat.ImpliedDefsSourceFile, def.SourceFile);
    }

    // ---- 往返 ----

    [Fact]
    public void 导入后def数量与结束标记一致()
    {
        using var db = Build("count");
        Assert.Equal(12, db.AllDefNames(ScopeFilter.Parse("all", db.PackageIds(), NoConfig)).Count);
    }

    [Fact]
    public void 元信息往返不丢()
    {
        using var db = Build("meta");
        Assert.Equal(Fixture.GameVersion, db.Meta.GameVersion);
        Assert.Equal(Fixture.Language, db.Meta.Language);
        Assert.Equal(["ludeon.rimworld", "test.mod"], db.Meta.Mods.Select(m => m.PackageId).ToArray());
    }

    /// <summary>
    /// 译文带着被替换掉的原文一起进库 —— 这是运行时导出独有的便宜(06 层 2),
    /// 丢了它就只剩译文,反查英文名要另起一套。
    /// </summary>
    [Fact]
    public void 译文与原文成对入库()
    {
        using var db = Build("tr");
        var t = db.Translations("Apparel_ShieldBelt").Single();
        Assert.Equal("护盾腰带", t.Translated);
        Assert.Equal("shield belt", t.Original);
        Assert.Equal(TranslationOrigin.Runtime, t.Origin);
    }

    /// <summary>导出时被截的 def 要留下记号,否则 02-3 那条区分无从谈起。</summary>
    [Fact]
    public void 导出侧截断标记随def入库()
    {
        using var db = Build("trunc");
        Assert.Equal(3, db.GetDefsNamed("Bullet_Revolver").Single().FieldsTruncated);
        Assert.Equal(0, db.GetDefsNamed("Apparel_ShieldBelt").Single().FieldsTruncated);
    }

    // ---- 版本 ----

    /// <summary>
    /// schema 版本对不上时要给一条能照做的消息,而不是让 SQLite 抛一个
    /// 「no such column」出来 —— 后者会被读成「数据坏了」。
    /// </summary>
    [Fact]
    public void 打开陌生schema版本的库时消息可照做()
    {
        var path = Temp("wrongver.db");
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); " +
                              $"INSERT INTO meta VALUES ('schema_version', '{SnapshotSchema.Version + 99}');";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.ThrowsAny<Exception>(() => SnapshotDb.Open(path));
        Assert.Contains((SnapshotSchema.Version + 99).ToString(), ex.Message);
        Assert.Contains("export", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 打开根本不是快照的文件时说得清楚()
    {
        var path = Temp("notadb.db");
        File.WriteAllText(path, "this is not a database");
        var ex = Assert.ThrowsAny<Exception>(() => SnapshotDb.Open(path));
        Assert.NotEmpty(ex.Message);
    }

    // ---- FTS ----

    /// <summary>02-7:调用方不该需要知道 '*'。这条是它在建库侧的落点。</summary>
    [Fact]
    public void 复合名的中间段不加星号也搜得到()
    {
        using var db = Build("fts");
        var scope = ScopeFilter.Parse("all", db.PackageIds(), NoConfig);
        var (rows, _) = db.SearchFts("shield", scope, null, 25);
        Assert.Contains(rows, r => r.DefName == "Apparel_ShieldBelt");
    }

    /// <summary>02-8:CJK 双字切分不许在重建里被顺手丢掉。</summary>
    [Fact]
    public void 中文标签搜得到()
    {
        using var db = Build("cjk");
        var scope = ScopeFilter.Parse("all", db.PackageIds(), NoConfig);
        var (rows, _) = db.SearchFts("护盾", scope, null, 25);
        Assert.Contains(rows, r => r.DefName == "Apparel_ShieldBelt");
    }

    // ---- 同名 def 的译文归属(R2 的建库侧;盲测没碰到,是修 R2 时实测查出的)----

    /// <summary>
    /// 一个 defName 下挂着几个 def 时,带类型的运行时注入要绑到**类型对得上**的那个。
    /// 原先这里是「名字 → 最后写的那个 id」,绑对绑错全看导出顺序 —— 语料特意把
    /// ThingDef 写在前面,让「后写的赢」当场赢错。
    /// </summary>
    [Fact]
    public void 同名def的译文按类型绑到对的那个()
    {
        var (db, path) = BuildAt("owner");
        using var _ = db;
        var named = db.GetDefsNamed("Firefoam");
        var thing = named.Single(d => d.DefType == "ThingDef");
        var stat = named.Single(d => d.DefType == "StatDef");
        Assert.NotEqual(thing.Id, stat.Id);

        var rows = RawTranslations(path, "Firefoam");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(thing.Id, r.DefId));
    }

    /// <summary>
    /// 静态收割来的 key 是 `DefName.field`,一个字的类型信息都没有。同名歧义下**写 null**,
    /// 不挑一个 —— 挑错的那一行与挑对的长得一模一样,而 null 至少是一句真话。
    /// 同一份收割里名字唯一的那条照常绑上,免得「判不出来」被写成「一律不绑」。
    /// </summary>
    [Fact]
    public void 收割译文判不出归属时写空而不是挑一个()
    {
        var roots = ModRootWith("harvest",
            ("Firefoam.label", "赛博泡沫"),
            ("Apparel_ShieldBelt.description", "一条护盾腰带。"));
        var (db, path) = BuildAt("harvest", modRoots: [roots]);
        using var _ = db;

        var ambiguous = RawTranslations(path, "Firefoam")
            .Where(r => r.Path == "label")
            .ToList();
        Assert.Contains(ambiguous, r => r.DefId is null);

        var unique = RawTranslations(path, "Apparel_ShieldBelt").Single(r => r.Path == "description");
        Assert.Equal(db.GetDefsNamed("Apparel_ShieldBelt").Single().Id, unique.DefId);
    }

    /// <summary>
    /// FTS 的 translated 列是召回用的,判据与 def_id 不同:归属判得出来就只挂那一个
    /// (中文名搜出来的不该是同名的另一个类型),判不出来就每个同名 def 都挂 ——
    /// 漏掉一个的后果是「用中文名搜不到那个 def」,比多召回一个同名的贵得多。
    /// </summary>
    [Fact]
    public void 中文名召回到判得出归属的那个否则全都召回()
    {
        var roots = ModRootWith("recall", ("Firefoam.label", "赛博泡沫"));
        var (db, _path) = BuildAt("recall", modRoots: [roots]);
        using var _ = db;
        var scope = ScopeFilter.Parse("all", db.PackageIds(), NoConfig);
        var named = db.GetDefsNamed("Firefoam");
        var thing = named.Single(d => d.DefType == "ThingDef");
        var stat = named.Single(d => d.DefType == "StatDef");

        var typed = db.SearchFts("灭火泡沫", scope, null, 25).Rows.Select(r => r.Id).ToList();
        Assert.Contains(thing.Id, typed);
        Assert.DoesNotContain(stat.Id, typed);

        var untyped = db.SearchFts("赛博", scope, null, 25).Rows.Select(r => r.Id).ToList();
        Assert.Contains(thing.Id, untyped);
        Assert.Contains(stat.Id, untyped);
    }
}
