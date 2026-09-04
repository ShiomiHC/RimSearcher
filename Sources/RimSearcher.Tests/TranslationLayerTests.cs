using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 收割来的译文行的三道闸:同一句话不许因磁盘布局重复、归属不许靠 defName 猜、
/// 过滤到零不许让整张表从输出里消失。
///
/// 三条各自钉住一种「印出来与真相同形」的错法,成因都在语言文件的形状里:
/// mod 常同时铺 <c>1.5/</c> 与 <c>1.6/</c> 两套 Languages(同一句话两行,逐列全同),
/// 而注入 key 是 <c>DefName.field</c> 不带类型(同名异型 def 的译文互相串门)。
/// </summary>
public class TranslationLayerTests
{
    /// <summary>
    /// 造一棵假 mod 树并按它导入一次。<paramref name="files"/> 的键是相对 mod 目录的路径。
    /// </summary>
    private static SnapshotDb ImportWithModTree(string caseName, params (string Rel, string Body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "translayer", caseName);
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
}
