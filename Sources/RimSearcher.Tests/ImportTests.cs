using RimSearcher.Contract;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 建库侧的闸:「一份中间格式进去,库里应该长什么样」在测试里跑,不用启动游戏。
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

    /// <summary>
    /// 同上,但写的是 <c>Keyed</c> 那一半:界面文案不挂在任何 def 上,目录与表都不同。
    /// packageId 显式传进来,因为「在不在快照的 mod 列表里」正是 origin 的判据。
    /// </summary>
    private static string ModRootWithKeyed(string tag, string packageId,
                                           params (string Key, string Text)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "import", tag + "-keyedmods");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var mod = Path.Combine(root, "SomeUiMod");
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(Path.Combine(mod, "About", "About.xml"),
            $"<ModMetaData><packageId>{packageId}</packageId></ModMetaData>");
        var keyed = Path.Combine(mod, "Languages", Fixture.Language, "Keyed");
        Directory.CreateDirectory(keyed);
        File.WriteAllText(Path.Combine(keyed, "Ui.xml"),
            "<LanguageData>" + string.Concat(entries.Select(e => $"<{e.Key}>{e.Text}</{e.Key}>")) + "</LanguageData>");
        return root;
    }

    // ---- 完整性:半份文件必须被拒,不许静默建成一个缺行的库 ----

    /// <summary>
    /// 游戏中途崩了、磁盘写满了,产出的就是一份没有结束标记的文件。导进去得到的库
    /// 看起来正常但内容不全 —— 「没查到」再也分不出是真没有还是被截断。宁可拒绝。
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
    /// 旧版导出器写的文件必须被拒,而不是当成「这些字段都不是默认值」导进来 ——
    /// 那样每个 def 都长得像处处被作者改过,与真的处处被改过逐字同形。
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

    // ---- 噪声过滤(唯一产地在 import 侧)----

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
    /// generated 是 ImpliedDefs 的信号,**不是**噪声 —— 滤掉它,代码生成的 def
    /// 就再也说不清自己从哪来。
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
        Assert.Equal(13, db.AllDefNames(ScopeFilter.Parse("all", db.PackageIds(), NoConfig)).Count);
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
    /// 译文带着被替换掉的原文一起进库 —— 这是运行时导出独有的便宜,
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

    /// <summary>导出时被截的 def 要留下记号,否则「字段被截」与「没有该字段」分不开。</summary>
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

    /// <summary>调用方不该需要知道 '*'。</summary>
    [Fact]
    public void 复合名的中间段不加星号也搜得到()
    {
        using var db = Build("fts");
        var scope = ScopeFilter.Parse("all", db.PackageIds(), NoConfig);
        var (rows, _) = db.SearchFts("shield", scope, null, 25);
        Assert.Contains(rows, r => r.DefName == "Apparel_ShieldBelt");
    }

    /// <summary>CJK 双字切分必须在建库侧到位。</summary>
    [Fact]
    public void 中文标签搜得到()
    {
        using var db = Build("cjk");
        var scope = ScopeFilter.Parse("all", db.PackageIds(), NoConfig);
        var (rows, _) = db.SearchFts("护盾", scope, null, 25);
        Assert.Contains(rows, r => r.DefName == "Apparel_ShieldBelt");
    }

    // ---- 同名 def 的译文归属 ----

    /// <summary>
    /// 一个 defName 下挂着几个 def 时,带类型的运行时注入要绑到**类型对得上**的那个。
    /// 语料特意把 ThingDef 写在前面,让「按名字取最后写的那个」当场绑错。
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

    /// <summary>
    /// 兄弟字段那条尾注每行挂一个 OR,而 SQLite 的表达式树深度上限是 1000 —— 超了整条
    /// 命令崩在一句可有可无的提示上,而 `--limit` 的文档反向担保了 2000 以内安全。
    /// 闸给 1200 行(过阈值),同时核对答案与一行时逐字相同 —— 分批不许换答案。
    /// </summary>
    [Fact]
    public void 兄弟字段的查询不许被行数撑爆表达式树()
    {
        using var db = Build("batch");
        var def = db.GetDefsNamed("Apparel_ShieldBelt").Single();
        var one = db.AuthoredSiblings([(def.Id, "comps[0].compClass")]);
        Assert.NotEmpty(one);

        var many = Enumerable.Repeat((def.Id, "comps[0].compClass"), 1200).ToList();
        Assert.Equal(one, db.AuthoredSiblings(many));
    }

    // ---- keyed(界面文案):与 def 无关的那一层 ----

    /// <summary>
    /// 运行时 <c>keyedReplacements</c> 的五条都进库,且双语两侧、占位标记、源文件行号
    /// 都到位 —— 少了原文那一侧,「中文快照上按英文找」整个不通;
    /// 少了占位标记,「有 key 但没译」与「译了」在库里同形。
    /// </summary>
    [Fact]
    public void 运行时keyed五条都进库且双语两侧都在()
    {
        using var db = Build("keyed");
        // 五条形态语料 + 2100 条过 MaxLimit 的填充批(上限与占位两道闸要的,见 Fixture)。
        Assert.Equal(5 + 2100, db.KeyedCount());

        var row = db.KeyedByKey("CannotUseNoPower").Single();
        Assert.Equal("没有电力", row.Translated);
        Assert.Equal("No power", row.Original);
        Assert.False(row.Placeholder);
        Assert.Equal(TranslationOrigin.Runtime, row.Origin);
        Assert.Equal("Misc.xml", row.SourceFile);
        Assert.Equal(12, row.SourceLine);

        var todo = db.KeyedByKey("TodoKey").Single();
        Assert.True(todo.Placeholder);

        // 英文那一侧可以缺(mod 只提供了译文),缺的时候不许把整条丢掉 ——
        // 丢掉与「这个 key 真不存在」同形。
        var oneSided = db.KeyedByKey("OnlyEnglishKey").Single();
        Assert.Equal("只有中文这一侧", oneSided.Translated);
        Assert.True(string.IsNullOrEmpty(oneSided.Original));
    }

    /// <summary>
    /// 磁盘收割来的 keyed 标成非生效,而且**同 key 不去重**:这一层说的是「磁盘上存在」,
    /// 不是「哪一句会显示」—— 挑一个就是凭目录枚举顺序发一张证不了的赢家证书。
    /// 「仅赢家」是运行时那一层的口径(keyedReplacements 本身已经是合并后的最终值)。
    /// </summary>
    [Fact]
    public void 收割的keyed标成非生效且同key不去重()
    {
        var roots = ModRootWithKeyed("keyedharvest", "test.mod",
            ("CannotUseNoPower", "电力不足"),
            ("ModOnlyKey", "只在磁盘上"));
        using var db = BuildAt("keyedharvest", modRoots: [roots]).Db;

        var both = db.KeyedByKey("CannotUseNoPower");
        Assert.Equal(2, both.Count);
        Assert.Single(both, r => r.Origin == TranslationOrigin.Runtime);
        var harvested = both.Single(r => r.Origin == TranslationOrigin.Harvested);
        Assert.Equal("电力不足", harvested.Translated);
        Assert.Equal("test.mod", harvested.SourceMod);

        // 生效的那一句唯一 —— 两条同 key 的记录不许让 KeyedInEffect 变成二选一。
        var inEffect = db.KeyedInEffect(["CannotUseNoPower", "ModOnlyKey"]);
        Assert.Equal("没有电力", inEffect["CannotUseNoPower"].Translated);
        Assert.False(inEffect.ContainsKey("ModOnlyKey"));
    }

    /// <summary>
    /// 收割源在不在快照的 mod 列表里,决定的是 harvested 还是 harvested_outside ——
    /// 后者连「这个环境里装着」都不成立,只够召回。
    /// </summary>
    [Fact]
    public void 快照外mod的keyed标成环境外收割()
    {
        var roots = ModRootWithKeyed("keyedoutside", "not.loaded", ("OutsideKey", "快照外"));
        using var db = BuildAt("keyedoutside", modRoots: [roots]).Db;
        var row = db.KeyedByKey("OutsideKey").Single();
        Assert.Equal(TranslationOrigin.HarvestedOutside, row.Origin);
    }

    /// <summary>
    /// 收割层要和运行时层存下**同一个字符串**。游戏读语言文件时把字面 <c>\n</c> 换成真换行
    /// (Keyed 走 DirectXmlLoaderSimple、DefInjected 走 DefInjectionPackage),收割不跟着换,
    /// 「两层不一致」这个信号里就混进一批纯表示差异。
    /// </summary>
    [Fact]
    public void 收割把字面n还原成换行和运行时一致()
    {
        var roots = ModRootWithKeyed("keyedescape", "test.mod",
            ("CannotUseNoPower", "上一行\\n下一行"));
        using var db = BuildAt("keyedescape", modRoots: [roots]).Db;

        var harvested = db.KeyedByKey("CannotUseNoPower")
                          .Single(r => r.Origin == TranslationOrigin.Harvested);
        Assert.Equal("上一行\n下一行", harvested.Translated);
        Assert.DoesNotContain("\\n", harvested.Translated);
    }

    /// <summary>
    /// keyed 走自己的 FTS 表,而不是蹭 <c>defs_fts</c>(那张表的 rowid 是 def id)。
    /// 两侧文本都要能召回:中文快照上按英文原文找是这一层最主要的用法之一。
    /// </summary>
    [Fact]
    public void keyed的双语文本都能召回()
    {
        using var db = Build("keyedfts");
        Assert.Equal("CannotUseNoPower", db.KeyedSearch("没有电力", 25).Rows.Single().Key);
        Assert.Equal("CannotUseNoPower", db.KeyedSearch("No power", 25).Rows.Single().Key);
    }
}
