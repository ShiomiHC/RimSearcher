using RimSearcher.Contract;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 译文那一层的七道闸:同一句话不许因磁盘布局重复、归属不许靠 defName 猜、
/// 过滤到零不许让整张表从输出里消失、两个注入键串各自入库、没量过注入键层的快照回空
/// 而不是空表、归一的三档各自落在自己那一格、
/// 界面文案那一层的重复同样折起来且份数出声。
///
/// 条条各自钉住一种「印出来与真相同形」的错法,成因都在注入键的形状里:
/// mod 常同时铺 <c>1.5/</c> 与 <c>1.6/</c> 两套 Languages(同一句话两行,逐列全同),
/// 注入 key 是 <c>DefName.field</c> 不带类型(同名异型 def 的译文互相串门),
/// 而同一个槽位在游戏里同时认下标式与把手式两种键串。
/// </summary>
public class TranslationLayerTests
{
    /// <summary>
    /// 造一棵假 mod 树并按它导入一次。<paramref name="files"/> 的键是相对 mod 目录的路径。
    /// </summary>
    private static SnapshotDb ImportWithModTree(string caseName, params (string Rel, string Body)[] files)
    {
        var dir = Path.Combine(TestTemp.Root, "translayer", caseName);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var modDir = Path.Combine(dir, "mods", "TestLangMod");
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(modDir, "About"));
        File.WriteAllText(Path.Combine(modDir, "About", "About.xml"),
            "<ModMetaData><packageId>test.langmod</packageId></ModMetaData>");

        foreach (var (rel, body) in files)
        {
            var full = Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        var config = Path.Combine(dir, "config.toml");
        File.WriteAllText(config,
            "mod_roots = ['" + Path.Combine(dir, "mods").Replace("\\", "\\\\") + "']\n" +
            "snapshot_dir = '" + dir.Replace("\\", "\\\\") + "'\n");

        var (_, err, code) = Fixture.Run("snapshot", "import", Fixture.ExportPath,
                                         "--name", caseName, "--json", "--config", config);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        return SnapshotDb.Open(Path.Combine(dir, caseName + ".db"));
    }

    private static string Injected(string defName, string field, string text) =>
        $"<LanguageData><{defName}.{field}>{text}</{defName}.{field}></LanguageData>";

    /// <summary>
    /// 同一个 mod 把同一句话铺在几套版本目录里,是磁盘布局,不是几条不同的译文。
    ///
    /// 实测成因(races 快照,Ancot.MiliraRace 等):mod 目录下 <c>1.4/</c>~<c>1.6/</c> 各有一份
    /// Languages,收割器逐个扫、逐个入库,于是 <c>get</c> 的译文表上同一句话连画两到四遍。
    /// 消费方看到的是「这条路径有四条译文」,而真相是「一条译文,存了四份」——
    /// 逐列全同,库里除 rowid 无从区分,所以折叠不丢任何信息。
    /// </summary>
    [Fact]
    public void 同一句话铺在几套版本目录里只算一条()
    {
        var body = Injected("Apparel_ShieldBelt", "description", "护盾腰带的说明");
        using var db = ImportWithModTree("dupversions",
            ($"1.4/Languages/{Fixture.Language}/DefInjected/ThingDef/Apparel.xml", body),
            ($"1.5/Languages/{Fixture.Language}/DefInjected/ThingDef/Apparel.xml", body),
            ($"1.6/Languages/{Fixture.Language}/DefInjected/ThingDef/Apparel.xml", body));

        var rows = db.Translations("Apparel_ShieldBelt")
                     .Where(t => t.Path == "description" && t.Origin != TranslationOrigin.Runtime)
                     .ToList();
        Assert.Single(rows);
    }

    /// <summary>
    /// 界面文案那一层同样折,而且折掉的份数要在输出里出声。
    ///
    /// 「同 key 多来源不挑一个」照旧成立 —— 跨 mod 的同名 key 真有几种说法。折的只是
    /// **同一个 mod 里逐列全同**的那几行,它们是版本目录的副本。不说破份数就等于把
    /// 「三份同文」印成「一份」,而读的人会据此去数这句话有几种说法。
    /// </summary>
    [Fact]
    public void 界面文案铺在几套版本目录里折成一条并报出份数()
    {
        const string body = "<LanguageData><TestKeyedLine>三份同文</TestKeyedLine></LanguageData>";
        using var db = ImportWithModTree("dupkeyed",
            ($"1.4/Languages/{Fixture.Language}/Keyed/Ui.xml", body),
            ($"1.5/Languages/{Fixture.Language}/Keyed/Ui.xml", body),
            ($"1.6/Languages/{Fixture.Language}/Keyed/Ui.xml", body));

        var rows = db.KeyedByKey("TestKeyedLine")
                     .Where(r => r.Origin != TranslationOrigin.Runtime).ToList();
        Assert.Single(rows);
        Assert.Equal(3, rows[0].SourceFileCount);

        var (text, _, code) = Fixture.Run("keyed", "TestKeyedLine", "--db", db.Path);
        Assert.Equal(0, code);
        Assert.Contains("(+2 same)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 收割行的归属靠语言文件的目录名,不靠 defName 猜。
    ///
    /// 注入 key 是 <c>DefName.field</c>,不带类型 —— 但**它所在的目录带**:语言文件
    /// 恒住在 <c>DefInjected/&lt;DefType&gt;/</c> 底下。不认这一级,同名异型 def 的译文
    /// 就会互相串门,而 <c>get</c> 的头注还写着「translations below are this def's own」。
    ///
    /// 语料里 Firefoam 同时是 ThingDef 与 StatDef,这条只给 StatDef 那个铺译文。
    /// </summary>
    [Fact]
    public void 收割行的类型取自DefInjected下那一级目录名()
    {
        using var db = ImportWithModTree("owntype",
            ($"Languages/{Fixture.Language}/DefInjected/StatDef/Stats.xml",
             Injected("Firefoam", "description", "只属于 StatDef 那一个")));

        var harvested = db.Translations("Firefoam")
                          .Where(t => t.Origin != TranslationOrigin.Runtime)
                          .ToList();
        Assert.All(harvested, t => Assert.Equal("StatDef", t.DefType));

        // ThingDef 那张卡上不许出现它 —— get 按 def_type 筛,而筛得动的前提是这一列不为空。
        var (thing, _, thingCode) = Fixture.Run("get", "Firefoam", "--type", "ThingDef",
                                                "--db", db.Path);
        Assert.Equal(0, thingCode);
        Assert.DoesNotContain("只属于 StatDef 那一个", thing, StringComparison.Ordinal);
    }

    /// <summary>
    /// 造一份只有 meta + 给定行 + 尾行的合成导出并导入。<paramref name="exporterVersion"/> 上
    /// 场是因为注入键层归它的能力位管 —— 「这一档没导」与「导了、没有行」得分得开。
    /// </summary>
    private static SnapshotDb ImportLines(string caseName, string exporterVersion,
                                          params string[] lines)
    {
        var dir = Path.Combine(TestTemp.Root, "translayer", caseName);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var export = Path.Combine(dir, caseName + IntermediateFormat.FileExtension);

        using (var fs = File.Create(export))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindMeta)
                .Int(IntermediateFormat.KeyFormatVersion, IntermediateFormat.FormatVersion)
                .Str(IntermediateFormat.KeyExporterVersion, exporterVersion)
                .Str(IntermediateFormat.KeyExportedAtUtc, "2026-09-05T00:00:00.0000000Z")
                .Str(IntermediateFormat.KeyGameVersion, Fixture.GameVersion)
                .Str(IntermediateFormat.KeyLanguage, Fixture.Language)
                .Raw(IntermediateFormat.KeyMods, "[]")
                .Raw(IntermediateFormat.KeyLimits, "{}")
                .ToString());
            foreach (var line in lines) w.WriteLine(line);
            w.WriteLine(new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEnd)
                .Int(IntermediateFormat.KeyRecords, lines.Length + 2)
                .ToString());
        }

        var db = Path.Combine(dir, caseName + ".db");
        new SnapshotImporter().Import(export, db);
        return SnapshotDb.Open(db);
    }

    private static string InjKeyLine(string defName, string path, string suggested,
                                     bool allowed = true) =>
        new JsonLine()
            .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindInjKey)
            .Str(IntermediateFormat.KeyDefType, "HediffDef")
            .Str(IntermediateFormat.KeyDefName, defName)
            .Str(IntermediateFormat.KeyPath, path)
            .Str(IntermediateFormat.KeySuggestedPath, suggested)
            .Bool(IntermediateFormat.KeyIsCollection, false)
            .Bool(IntermediateFormat.KeyTranslationAllowed, allowed)
            .Bool(IntermediateFormat.KeyFullListTranslationAllowed, false)
            .ToString();

    private sealed record RosterRow(string DefName, string? DefType, string Path, string SuggestedPath,
                                    bool TranslationAllowed);

    /// <summary>名册按 Core 同一条 JOIN 读回 —— Core 自己没有读名册整行的入口(get 只按行数判层在不在)。</summary>
    private static List<RosterRow> Roster(string path, string defName)
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT n.def_name, t.def_type, p.path, q.path, k.translation_allowed FROM injection_keys k "
                        + "JOIN injection_key_names n ON n.id = k.def_name_id "
                        + "LEFT JOIN injection_key_types t ON t.id = k.def_type_id "
                        + "JOIN injection_key_paths p ON p.id = k.path_id "
                        + "JOIN injection_key_paths q ON q.id = k.suggested_path_id "
                        + "WHERE n.def_name = $n ORDER BY p.path";
        cmd.Parameters.AddWithValue("$n", defName);
        var rows = new List<RosterRow>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new RosterRow(rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1),
                                   rd.GetString(2), rd.GetString(3), rd.GetInt32(4) != 0));
        return rows;
    }

    /// <summary>
    /// 注入键层进得了库,两个键串各占一列。
    ///
    /// 这一层的用途就是拿把手式换下标式:游戏对同一个槽位同时认
    /// <c>stages.0.label</c> 与 <c>stages.observed_corpse.label</c>,而译文表里存的是译者
    /// 写的那一串 —— 少了这张对照表,<c>--path</c> 只匹配得上其中一种,另一种回的零与
    /// 「这个 def 没这条译文」逐字同形。
    /// </summary>
    [Fact]
    public void 注入键层的两个键串各自入库()
    {
        using var db = ImportLines("injkeys", Fixture.DefExporterVersion,
            InjKeyLine("ObservedLayingCorpse", "stages.0.label", "stages.observed_corpse.label"),
            InjKeyLine("ObservedLayingCorpse", "description", "description", allowed: false));

        var rows = Roster(db.Path, "ObservedLayingCorpse");
        Assert.Equal(2, rows.Count);

        var handled = rows.Single(r => r.Path == "stages.0.label");
        Assert.Equal("stages.observed_corpse.label", handled.SuggestedPath);
        Assert.True(handled.TranslationAllowed);

        // 「不许译」那一格必须活着进库 —— 它是「谁都没译」与「白译也没用」的唯一分界。
        Assert.False(rows.Single(r => r.Path == "description").TranslationAllowed);
    }

    /// <summary>
    /// 名册四列全进字典之后,读回来的必须与写进去的逐字相同 —— 字典化是编码,不是取舍。
    ///
    /// 特别钉 <c>def_type</c> 那一列:它可空,而空的那一档**不发号**。给空串发个号会让
    /// 「没有类型」与「类型是空串」在库里同形,而那正是这一列要分开的两件事。
    /// </summary>
    [Fact]
    public void 名册字典化之后每一列都原样读得回来()
    {
        using var db = ImportLines("injkeydict", Fixture.DefExporterVersion,
            InjKeyLine("ObservedLayingCorpse", "stages.0.label", "stages.observed_corpse.label"),
            // 同一条路径在两行里出现 —— 字典跨行共用,那正是省下来的东西。
            InjKeyLine("SecondHediff", "stages.0.label", "stages.observed_corpse.label"),
            InjKeyLine("ObservedLayingCorpse", "description", "description", allowed: false));

        var rows = Roster(db.Path, "ObservedLayingCorpse");
        Assert.Equal(2, rows.Count);
        var handled = rows.Single(r => r.Path == "stages.0.label");
        Assert.Equal("ObservedLayingCorpse", handled.DefName);
        Assert.Equal("HediffDef", handled.DefType);
        Assert.Equal("stages.observed_corpse.label", handled.SuggestedPath);

        using var raw = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={db.Path};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();

        // 三行两个不同的 def_name、两条不同的路径 —— 字典真去重了才是这两个数。
        cmd.CommandText = "SELECT COUNT(*) FROM injection_key_names";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM injection_key_paths";
        Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM injection_key_types";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        // 主表存的是号,不是字符串 —— 不然上面那些计数一样绿,而库没省一个字节。
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('injection_keys') "
                        + "WHERE name IN ('def_type','def_name','path','suggested_path')";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// 三个键态各自落在自己那一格,而且 <c>key</c> 一字不改。
    ///
    /// 判据是**槽位名册**(injection_keys),不是字段表。这条闸盯的正是拿字段表当替身的
    /// 那次错判:整表注入的键不带元素下标(<c>descriptionRules.rulesStrings</c>),字段表里
    /// 那条路径带(<c>…rulesStrings[0]</c>),逐字比一次不中 —— 实测 1348 条判「配不上」
    /// 里 956 条是这么来的,而输出还配着一句「游戏那边同样注入不上」的假话。
    ///
    /// 三档合并任何两个都会让一种「印出来与真相同形」回来:把 no-slot 并进 resolved →
    /// 一条游戏也注入不上的坏译文印成好的;把 refused 并进 no-slot → 出路从「改键没用」
    /// 变成「改键能救」。
    /// </summary>
    [Fact]
    public void 三个键态各自落在自己那一格()
    {
        using var db = SnapshotDb.Open(Fixture.InjKeyDb);
        var rows = db.Translations("ObservedLayingCorpse")
                     .ToDictionary(t => t.Key!, t => t);

        // 把手式:名册给出的下标式改写成字段表文法。
        var handled = rows["stages.observed_corpse.label"];
        Assert.Equal("stages[0].label", handled.Path);
        Assert.Equal(InjectionKey.State.Resolved, handled.KeyState);

        // 下标式:本来就是字段路径。
        Assert.Equal("label", rows["label"].Path);
        Assert.Equal(InjectionKey.State.Resolved, rows["label"].KeyState);

        // 整表注入:字段表里没有这条裸路径,名册里有 —— 这一条必须是 resolved。
        Assert.Equal(InjectionKey.State.Resolved,
                     rows["descriptionRules.rulesStrings"].KeyState);

        // 名册上没有 —— 游戏那边同样注入不上,所以它不许并进上面任何一档。
        Assert.Equal(InjectionKey.State.NoSlot, rows["stages.corpse_seen.label"].KeyState);

        // 名册上有、但不许译:键没写错,与「键写错了」出路不同。
        Assert.Equal(InjectionKey.State.Refused, rows["stages.0.minSeverity"].KeyState);
    }

    /// <summary>
    /// 「在语言包里」与「生效了」是两件事,origin 那一格分得开。
    ///
    /// 此前导出侧把包里每一条 defInjection 都当生效,一律印 <c>in effect</c> —— 而键配不上
    /// 槽位、或槽位不许译的那些,游戏照旧把记录留在包里、只是不注。baseline 上光前一种
    /// 就有 1348 行。判据是游戏自己的 <c>DefInjection.injected</c>,不是本项目推的:
    /// 名册那条推算路只对得起收割来的行(那些行游戏根本没读过)。
    /// </summary>
    [Fact]
    public void 在包里与生效了在origin那格分得开()
    {
        using var db = SnapshotDb.Open(Fixture.InjKeyDb);
        var rows = db.Translations("ObservedLayingCorpse").ToDictionary(t => t.Key!, t => t);

        Assert.True(rows["label"].Applied);
        Assert.False(rows["stages.corpse_seen.label"].Applied);
        Assert.False(rows["stages.0.minSeverity"].Applied);

        // 运行时那一档此前 source_file 恒空 —— 游戏其实一直知道译文出自哪个文件。
        Assert.Equal("DefInjected/HediffDef/Hediffs.xml", rows["label"].SourceFile);
    }

    /// <summary>
    /// <c>--path-contains</c> 筛掉全部译文时,那张表要以空表在场,不是从输出里消失。
    ///
    /// 两件事同时错:<c>--json</c> 的自述契约写着「表键恒在,没命中就是空数组」,而
    /// 译文表被筛空时整个键不见了;头注仍在说「translations below are this def's own」,
    /// 底下却一条都没有。于是「这个字段没有译文」与「你的过滤器文法和译文那栏对不上」
    /// 印出来一模一样 —— 而后者恰恰是真事:字段表写 <c>stages[0].label</c>,
    /// 译文那栏是注入键,写 <c>stages.0.label</c> 或 <c>stages.observed_corpse.label</c>。
    /// </summary>
    [Fact]
    public void 译文表被过滤到零时以空表在场()
    {
        var (json, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains",
                                          "soundDrop", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var def = doc.RootElement.GetProperty("defs")[0];
        Assert.True(def.TryGetProperty("translations", out var trans),
                    "译文表被筛空后整个键消失了 —— 与「这个 def 一条译文都没有」同形。");
        Assert.Equal(0, trans.GetArrayLength());
    }

    /// <summary>
    /// 快照外 mod(装着、没启用)的语言文件收割进来只够召回,游戏没注它。此前 get 的 origin 格写
    /// 「outside this snapshot」再挂一句脚注解释这四个字;search 则在表外数「N 个 def 也命中了
    /// 未启用 mod 的语言文件」。2026-09-18 起取值自陈(Docs/25 丁2):origin 写 not enabled,
    /// search 的 matched_on 在命中的正是那条译文时带同一个标注,两句散文都不印。
    /// </summary>
    [Fact]
    public void 快照外mod的译文在格里自陈not_enabled_不再挂脚注()
    {
        var body = Injected("Apparel_ShieldBelt", "description", "护盾腰带的说明");
        using var db = ImportWithModTree("outsideenabled",
            ($"1.6/Languages/{Fixture.Language}/DefInjected/ThingDef/Apparel.xml", body));
        var row = db.Translations("Apparel_ShieldBelt").Single(t => t.Origin != TranslationOrigin.Runtime);
        Assert.Equal(TranslationOrigin.HarvestedOutside, row.Origin);

        var (get, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "description", "--db", db.Path);
        Assert.Contains("file (test.langmod, not enabled)", get, StringComparison.Ordinal);
        Assert.DoesNotContain("outside this snapshot", get, StringComparison.Ordinal);
        Assert.DoesNotContain("not enabled when the snapshot was taken", get, StringComparison.Ordinal);

        var (search, _, _) = Fixture.Run("search", "护盾腰带的说明", "--db", db.Path);
        Assert.Contains("description (file, not enabled)", search, StringComparison.Ordinal);
        Assert.DoesNotContain("also matched language files", search, StringComparison.Ordinal);
    }
}
