using System.Text;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Contract;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 「这份快照还等于磁盘吗」的两条判据。
///
/// 两条都是**反向**闸:它们守的不是「说得对」,而是「该说的时候不许沉默」——
/// 过期而不发声,与答案本身错的代价一模一样,而且更隐蔽(输出看着完全正常)。
/// </summary>
public class StalenessTests
{
    // ---- 游戏版本:产地是 dll,不是 ModsConfig.xml ----

    /// <summary>
    /// 换算逐字对得上游戏自己算的那个数。基线取自本机实测:装机的 Assembly-CSharp.dll
    /// 是 1.6.9676.17735,而游戏里显示、导出器写进快照的是 1.6.4871 rev591。
    /// </summary>
    [Fact]
    public void 程序集版本换算成游戏自报的版本串()
        => Assert.Equal("1.6.4871 rev591", GameBuild.Format(new Version(1, 6, 9676, 17735)));

    /// <summary>rev 那一位是**整除**出来的 —— 少一次截断就会造出一个游戏从没显示过的数。</summary>
    [Theory]
    [InlineData(0, "1.6.4871 rev0")]
    [InlineData(29, "1.6.4871 rev0")]
    [InlineData(30, "1.6.4871 rev1")]
    [InlineData(17735, "1.6.4871 rev591")]
    public void 修订位按三十秒一格截断(int revision, string expected)
        => Assert.Equal(expected, GameBuild.Format(new Version(1, 6, 9676, revision)));

    /// <summary>
    /// 读不到就是 <c>null</c>,不是抛,也不是猜一个。三种读不到都要走同一条路 ——
    /// 上层靠这个 null 决定退回问 ModsConfig,一次异常会让整条查询死在寻址上。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Z:\\no\\such\\game")]
    public void 读不到版本就是空(string? gameDir)
        => Assert.Null(GameBuild.Installed(gameDir));

    /// <summary>
    /// 目录布局对上就真的读得出来。语料是测试程序集自己 —— 换个名字放进
    /// <c>RimWorldWin64_Data/Managed/</c> 就够,这一步验的是「找得到并读得动」。
    /// </summary>
    [Fact]
    public void 版本从游戏目录的程序集读出来()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "fake-game");
        var managed = SourcePlanner.ManagedPath(gameDir);
        Directory.CreateDirectory(managed);
        var self = typeof(StalenessTests).Assembly;
        File.Copy(self.Location, Path.Combine(managed, "Assembly-CSharp.dll"), overwrite: true);

        var expected = GameBuild.Format(self.GetName().Version!);
        Assert.Equal(expected, GameBuild.Installed(gameDir));
    }

    // ---- 参考侧 XML:mtime 那一层 ----

    /// <summary>
    /// 一棵最小 mod 树。形状是刻意的,每个目录都在守一条射程边界:
    ///
    ///   About/About.xml       packageId 的产地,InstalledMods 靠它认领这个目录
    ///   Defs/Things.xml       在射程内
    ///   Patches/Fix.xml       在射程内
    ///   1.6/Defs/New.xml      在射程内 —— 当前版本目录,它的优先级还高于根目录
    ///   1.5/Defs/Old.xml      **不**在射程内:1.6 那份在场时游戏不看它
    ///   Languages/…           不在射程内(翻译层改了这条判据不响,得说得出口)
    ///   Textures/…            同上
    ///
    /// 1.6 那个目录不是摆设:去掉它,游戏就会回退到「小于等于当前版本的最高一个」,
    /// 于是 1.5 反倒**在**射程内 —— 那是游戏自己的算法,不是漏扫。
    /// </summary>
    private static string WriteModTree(string root, string packageId)
    {
        void W(string rel, string text)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        W("About/About.xml", $"<ModMetaData><packageId>{packageId}</packageId><name>{packageId}</name></ModMetaData>");
        W("Defs/Things.xml", "<Defs><ThingDef><defName>A</defName></ThingDef></Defs>");
        W("Patches/Fix.xml", "<Patch><Operation Class=\"PatchOperationAdd\" /></Patch>");
        W("1.6/Defs/New.xml", "<Defs><ThingDef><defName>New</defName></ThingDef></Defs>");
        W("Languages/ChineseSimplified/Keyed/UI.xml", "<LanguageData><Some>x</Some></LanguageData>");
        W("Textures/note.xml", "<x />");
        W("1.5/Defs/Old.xml", "<Defs><ThingDef><defName>Old</defName></ThingDef></Defs>");
        return root;
    }

    private const string TestGameVersion = "1.6.4871 rev591";

    private static (RimConfig Config, string ModDir) Environment(string name, string packageId = "test.contentmod")
    {
        var root = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "content", name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var modDir = WriteModTree(Path.Combine(root, "mods", packageId), packageId);
        return (new RimConfig { ModRoots = [Path.Combine(root, "mods")] }, modDir);
    }

    private static string? HashOf(RimConfig config, string packageId)
        => ContentFingerprint.Scan(config, [packageId], TestGameVersion)?.Mods.SingleOrDefault()?.Hash;

    [Fact]
    public void 扫得到的只有当前加载目录下的Defs与Patches()
    {
        var (config, _) = Environment("scope");
        var scan = ContentFingerprint.Scan(config, ["test.contentmod"], TestGameVersion);

        // 三个文件,不是七个:Languages / Textures / 1.5 都在射程外。
        Assert.NotNull(scan);
        Assert.Equal(3, scan!.Files);
    }

    /// <summary>这条是整件事的理由:内容变了,而 About.xml 一个字没动。</summary>
    [Fact]
    public void 改动Defs里的文件会让指纹变()
    {
        var (config, modDir) = Environment("edit");
        var before = HashOf(config, "test.contentmod");

        File.WriteAllText(Path.Combine(modDir, "Defs", "Things.xml"),
                          "<Defs><ThingDef><defName>A</defName><label>changed</label></ThingDef></Defs>",
                          new UTF8Encoding(false));

        Assert.NotEqual(before, HashOf(config, "test.contentmod"));
    }

    /// <summary>
    /// 长度不变也要变 —— Steam 更新最常见的形态就是同长度改写(改一个数字、换一个类名)。
    /// 只比长度的话这一格整个漏掉。
    /// </summary>
    [Fact]
    public void 长度不变的改写照样被抓到()
    {
        var (config, modDir) = Environment("same-length");
        var file = Path.Combine(modDir, "Defs", "Things.xml");
        var original = File.ReadAllText(file);
        var before = HashOf(config, "test.contentmod");

        var rewritten = original.Replace("<defName>A</defName>", "<defName>B</defName>");
        Assert.Equal(original.Length, rewritten.Length);
        File.WriteAllText(file, rewritten, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(1));

        Assert.NotEqual(before, HashOf(config, "test.contentmod"));
    }

    [Fact]
    public void 新增与删除文件都让指纹变()
    {
        var (config, modDir) = Environment("added");
        var before = HashOf(config, "test.contentmod");

        var extra = Path.Combine(modDir, "Defs", "More.xml");
        File.WriteAllText(extra, "<Defs />", new UTF8Encoding(false));
        var withExtra = HashOf(config, "test.contentmod");
        Assert.NotEqual(before, withExtra);

        File.Delete(extra);
        Assert.Equal(before, HashOf(config, "test.contentmod"));
    }

    /// <summary>
    /// 射程外的三处动了,指纹不许变。假阳性在这条判据上比漏报更贵:
    /// 一条天天响的过期警告等于没有过期警告。
    /// </summary>
    [Theory]
    [InlineData("Languages/ChineseSimplified/Keyed/UI.xml")]
    [InlineData("Textures/note.xml")]
    [InlineData("1.5/Defs/Old.xml")]
    [InlineData("About/About.xml")]
    public void 射程外的改动不让指纹变(string relative)
    {
        var (config, modDir) = Environment("outside-" + relative.Replace('/', '-'));
        var before = HashOf(config, "test.contentmod");

        var path = Path.Combine(modDir, relative.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(path, "<!-- touched -->");

        Assert.Equal(before, HashOf(config, "test.contentmod"));
    }

    /// <summary>
    /// 目录搬家不算内容变化 —— 键是相对 mod 根的路径,与 SourceTreeState 同一条口径。
    /// 不这么定的话,把库从 D: 挪到 E: 会让每个 mod 同时报「变了」。
    /// </summary>
    [Fact]
    public void 换个目录放着指纹不变()
    {
        var (config, _) = Environment("home");
        var before = HashOf(config, "test.contentmod");

        var moved = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "content", "moved", "mods");
        if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
        Directory.CreateDirectory(moved);
        CopyTree(config.ModRoots[0], moved);

        Assert.Equal(before, HashOf(new RimConfig { ModRoots = [moved] }, "test.contentmod"));
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(from, to);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    /// <summary>扫不到任何 mod 目录 = 这次没量,不是「什么都没变」。</summary>
    [Fact]
    public void 没有mod根目录时不作答()
        => Assert.Null(ContentFingerprint.Scan(new RimConfig(), ["test.contentmod"], TestGameVersion));

    /// <summary>
    /// 导出时扫得到、现在找不到的 mod 与「内容改了」分开报:前者的下一步是把它装回来,
    /// 后者是重导,混成一句会指错路。
    /// </summary>
    [Fact]
    public void 消失的mod与改动的mod分开记()
    {
        var recorded = new ContentScan([
            new ModContent("a.mod", 3, "aaaa"),
            new ModContent("b.mod", 3, "bbbb"),
            new ModContent("c.mod", 3, "cccc"),
        ]);
        var current = new ContentScan([
            new ModContent("a.mod", 3, "aaaa"),
            new ModContent("b.mod", 4, "zzzz"),
        ]);

        var diff = ContentFingerprint.Compare(recorded, current);
        Assert.Equal(["b.mod"], diff.Changed);
        Assert.Equal(["c.mod"], diff.Missing);
        Assert.True(diff.Drifted);
        Assert.Equal(3, diff.Scanned);
    }

    /// <summary>快照里没有、现在多出来的 mod 不在这条判据里报 —— 那是 modlist 那条的活儿。</summary>
    [Fact]
    public void 新多出来的mod不由这条判据报()
    {
        var diff = ContentFingerprint.Compare(
            new ContentScan([new ModContent("a.mod", 3, "aaaa")]),
            new ContentScan([new ModContent("a.mod", 3, "aaaa"), new ModContent("new.mod", 9, "nnnn")]));

        Assert.False(diff.Drifted);
    }

    // ---- 端到端:建一份带指纹的库,改一个文件,看它说不说话 ----

    /// <summary>
    /// 走真的 import 路径建库,于是「记进 meta」与「读出来比对」两侧同时被闸住。
    /// </summary>
    /// <summary>快照自己那两个 mod。<see cref="SnapshotOfModTree"/> 的 activeMods 默认值。</summary>
    private static readonly string[] SnapshotMods = ["ludeon.rimworld", "test.mod"];

    private static (string Db, RimConfig Config, string ModDir, string ConfigPath) SnapshotOfModTree(
        string name, string[]? activeMods = null, string? gameVersion = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "content", name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

        const string PackageId = "test.mod";
        var modDir = WriteModTree(Path.Combine(root, "mods", PackageId), PackageId);
        // 快照的 mod 列表里还有 Core,给它一个目录,免得「装不到」把两件事搅在一起。
        WriteModTree(Path.Combine(root, "mods", "ludeon.rimworld"), "ludeon.rimworld");

        // ModsConfig.xml 要自己造一份并指过去。不指的话读的是**本机真实的**那一份,
        // 于是这几条闸的成败取决于跑测试的人现在开着哪些 mod。
        var modsConfig = Path.Combine(root, "ModsConfig.xml");
        File.WriteAllText(modsConfig,
            $"<ModsConfigData><version>{gameVersion ?? Fixture.GameVersion}</version><activeMods>" +
            string.Concat((activeMods ?? SnapshotMods).Select(id => $"<li>{id}</li>")) +
            "</activeMods></ModsConfigData>\n", new UTF8Encoding(false));

        var config = new RimConfig { ModRoots = [Path.Combine(root, "mods")], ModsConfig = modsConfig };

        var export = Path.Combine(root, "export" + IntermediateFormat.FileExtension);
        Fixture.WriteExport(export);
        var db = Path.Combine(root, name + ".db");
        new SnapshotImporter { Environment = config }.Import(export, db);

        // 同一套事实的 toml 形态 —— 端到端那几条要走真的命令行入口。
        var configPath = Path.Combine(root, "config.toml");
        File.WriteAllText(configPath,
            $"mod_roots = ['{Path.Combine(root, "mods")}']\n" +
            $"mods_config = '{modsConfig}'\n" +
            $"snapshot_dir = '{root}'\n",
            new UTF8Encoding(false));

        return (db, config, modDir, configPath);
    }

    /// <summary>
    /// <paramref name="db"/> 传 null 就是**不寻址**:让 <c>SnapshotCatalog</c> 自己挑。
    /// 「选了哪一份」那句话只在这条路上出得来 —— 显式 <c>--db</c> 一律静音。
    /// </summary>
    private static string Run(string configPath, string? db, params string[] argv)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        List<string> all = [.. argv];
        if (db is not null) { all.Add("--db"); all.Add(db); }
        all.Add("--config"); all.Add(configPath);
        RimSearcher.Cli.Runner.Run(all, stdout, stderr);
        return stdout.ToString();
    }

    [Fact]
    public void 导入时把参考侧XML指纹记进库()
    {
        var (dbPath, _, _, _) = SnapshotOfModTree("recorded");
        using var db = SnapshotDb.Open(dbPath);

        Assert.NotNull(db.Content);
        Assert.Equal(2, db.Content!.Mods.Count);          // test.mod 与 ludeon.rimworld
        Assert.Equal(6, db.Content.Files);                // 每个 mod 三个文件在射程内
    }

    /// <summary>没配环境时不记 —— 那样的库对这个问题没有资格回答,不许装作比过了。</summary>
    [Fact]
    public void 没有环境时库里不留指纹()
    {
        using var db = SnapshotDb.Open(Fixture.Db);
        Assert.Null(db.Content);
    }

    [Fact]
    public void 改了Defs之后查询当场说破()
    {
        var (dbPath, config, modDir, _) = SnapshotOfModTree("drifted");
        File.AppendAllText(Path.Combine(modDir, "Defs", "Things.xml"), "<!-- edited after the export -->");

        using var db = SnapshotDb.Open(dbPath);
        var report = SnapshotCatalog.Compare(db, config);

        Assert.Equal(EnvironmentMatch.ContentDrift, report.Match);
        Assert.Equal(["test.mod"], report.Content!.Changed);
    }

    [Fact]
    public void 没改动时这条判据一个字都不说()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree("quiet");
        using var db = SnapshotDb.Open(dbPath);
        var report = SnapshotCatalog.Compare(db, config);

        Assert.Equal(EnvironmentMatch.Same, report.Match);
        Assert.False(report.Content!.Drifted);
    }

    // ---- mod 列表:集合差不算过期,次序差算 ----

    /// <summary>
    /// 游戏多开了几个 mod,不是漂移。快照覆盖到哪儿为止是**它存在的理由**(一份刻意精简的
    /// 基线快照,pin 上之后这句话每次查询都成立、且永远不会「修好」),而恒真的警告不携带
    /// 信息,却与真过期同形同位。
    /// </summary>
    [Fact]
    public void 游戏多开了mod不算漂移()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree(
            "superset", ["ludeon.rimworld", "test.mod", "extra.mod"]);
        using var db = SnapshotDb.Open(dbPath);
        var report = SnapshotCatalog.Compare(db, config);

        Assert.Equal(EnvironmentMatch.Same, report.Match);
        Assert.False(report.Reordered);
        // 数字照旧算出来 —— `snapshot status` 要逐条讲,只是不进每次查询。
        Assert.Equal(1, report.Added);
        Assert.Equal(0, report.Removed);
    }

    /// <summary>禁用一个 mod 是同一件事的另一半:也是环境选择,也不发声。</summary>
    [Fact]
    public void 游戏禁用了mod也不算漂移()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree("subset", ["ludeon.rimworld"]);
        using var db = SnapshotDb.Open(dbPath);
        var report = SnapshotCatalog.Compare(db, config);

        Assert.Equal(EnvironmentMatch.Same, report.Match);
        Assert.False(report.Reordered);
        Assert.Equal(1, report.Removed);
    }

    /// <summary>
    /// 次序变了是漂移。没人挑得出一个「次序变体」环境 —— 而加载顺序决定同名 patch 谁赢,
    /// 于是快照里的值不是「不全」,是错的。
    /// </summary>
    [Fact]
    public void 同一批mod换了次序算漂移()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree("reordered", ["test.mod", "ludeon.rimworld"]);
        using var db = SnapshotDb.Open(dbPath);

        Assert.True(SnapshotCatalog.Compare(db, config).Reordered);
    }

    /// <summary>
    /// 次序只在**两边都在**的那些 mod 之间判。拿全表去判的话,多开一个就会让它响 ——
    /// 而那一格按上面的口径不发声,于是集合差会从这条判据的后门漏回来。
    /// </summary>
    [Fact]
    public void 多开的mod插在中间不算换次序()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree(
            "interleaved", ["ludeon.rimworld", "extra.mod", "test.mod"]);
        using var db = SnapshotDb.Open(dbPath);

        Assert.False(SnapshotCatalog.Compare(db, config).Reordered);
    }

    /// <summary>
    /// **这条是整件事的理由**:此前 mod 列表不一致会让 <c>Compare</c> 当场 return,
    /// 于是版本与 XML 那两层根本跑不到 —— 一份 pin 着的精简快照碰上 build 升级,一个字都不报。
    /// 那条恒真的列表警告不只是稀释信号,它在顶替信号。
    /// </summary>
    [Fact]
    public void 多开了mod也照样报版本漂移()
    {
        var (dbPath, config, _, _) = SnapshotOfModTree(
            "superset-version", ["ludeon.rimworld", "test.mod", "extra.mod"], gameVersion: "1.7.0000");
        using var db = SnapshotDb.Open(dbPath);

        Assert.Equal(EnvironmentMatch.VersionDrift, SnapshotCatalog.Compare(db, config).Match);
    }

    /// <summary>同上,XML 那一层 —— 两层各有各的 return,漏一个就漏一整类。</summary>
    [Fact]
    public void 多开了mod也照样报XML漂移()
    {
        var (dbPath, config, modDir, _) = SnapshotOfModTree(
            "superset-content", ["ludeon.rimworld", "test.mod", "extra.mod"]);
        File.AppendAllText(Path.Combine(modDir, "Defs", "Things.xml"), "<!-- edited -->");

        using var db = SnapshotDb.Open(dbPath);
        var report = SnapshotCatalog.Compare(db, config);

        Assert.Equal(EnvironmentMatch.ContentDrift, report.Match);
        Assert.Equal(["test.mod"], report.Content!.Changed);
    }

    /// <summary>
    /// 声明句要点出**是哪几个 mod** —— 只说「有东西变了」的话,下一步(是重导还是不管)
    /// 没有依据,而重导一次要开一遍游戏。
    /// </summary>
    [Fact]
    public void 漂移声明点名到mod()
    {
        var sentence = ContentDrift.Sentence(
            "modded", new ContentComparison(["erdelf.humanoidalienraces"], [], 23));

        Assert.Contains("erdelf.humanoidalienraces", sentence, StringComparison.Ordinal);
        Assert.Contains("Re-export", sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// 措辞不许把判据说大。比的是尺寸与时间戳,而 Steam 重下一份逐字节相同的文件
    /// 也会让它响 —— 说成「被编辑过」就是拿一句证不了的话去指挥下一步。
    /// </summary>
    [Fact]
    public void 漂移声明不声称文件被编辑过()
    {
        var sentence = ContentDrift.Sentence(
            "modded", new ContentComparison(["a.mod"], [], 5));

        Assert.Contains("changed on disk", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("edited", sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// 主谓要跟着数走。第一次跑真数据就撞上「1 mod … have」—— 计数本身是文法系统渲染的,
    /// 而句子后半段的动词不是,两半各说各的。
    /// </summary>
    [Fact]
    public void 漂移声明的主谓跟着数走()
    {
        var one = ContentDrift.Sentence("s", new ContentComparison(["a.mod"], [], 5));
        Assert.Contains("1 mod", one, StringComparison.Ordinal);
        Assert.Contains("has Defs or Patches", one, StringComparison.Ordinal);

        var many = ContentDrift.Sentence("s", new ContentComparison(["a.mod", "b.mod"], [], 5));
        Assert.Contains("2 mods", many, StringComparison.Ordinal);
        Assert.Contains("have Defs or Patches", many, StringComparison.Ordinal);

        Assert.Contains("its files", ContentDrift.Sentence("s", new ContentComparison([], ["a.mod"], 5)),
                        StringComparison.Ordinal);
        Assert.Contains("their files", ContentDrift.Sentence("s", new ContentComparison([], ["a.mod", "b.mod"], 5)),
                        StringComparison.Ordinal);
    }

    /// <summary>找不到的 mod 那一半自己有一句话,而且不说「重导就好了」。</summary>
    [Fact]
    public void 消失的mod有自己的说法()
    {
        var sentence = ContentDrift.Sentence(
            "modded", new ContentComparison([], ["gone.mod"], 5));

        Assert.Contains("gone.mod", sentence, StringComparison.Ordinal);
        Assert.Contains("cannot be found on disk", sentence, StringComparison.Ordinal);
    }

    // ---- 端到端:真的命令行入口说了什么 ----

    /// <summary>
    /// 普通查询在漂移时**当场**说一句 —— 详情分流到 <c>snapshot status</c> 是可以的,
    /// 沉默不行:调用方不会为每次提问先去查一遍状态。
    /// </summary>
    [Fact]
    public void 漂移时普通查询也发声()
    {
        var (db, _, modDir, configPath) = SnapshotOfModTree("e2e-drift");
        File.AppendAllText(Path.Combine(modDir, "Defs", "Things.xml"), "<!-- edited -->");

        var stdout = Run(configPath, db, "get", "Apparel_ShieldBelt");

        Assert.Contains("changed on disk", stdout, StringComparison.Ordinal);
        Assert.Contains("test.mod", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 漂移横幅的**位置**跟着结果走:点到了那个 mod 就在最上面,没点到就沉到表下。
    ///
    /// 一次都不抑制 —— 「结果里没有那个 mod」不等于「答案没受它影响」,那个 mod 可能正是
    /// 把某一行改没了的那个。调的只是位置:表头留给随查询变化的东西(scope 展开、
    /// 精确/包含拆分、截断脚注),而一条每次都在同一行说同样话的横幅,读到第五遍之后
    /// 会把整个表头区一起训练成盲区。
    /// </summary>
    [Fact]
    public void 漂移横幅点到那个mod时才占表头()
    {
        var (db, _, modDir, configPath) = SnapshotOfModTree("e2e-drift-place");
        File.AppendAllText(Path.Combine(modDir, "Defs", "Things.xml"), "<!-- edited -->");
        const string Banner = "changed on disk";

        // 答案就出自那个 mod:第一行。
        var hit = Run(configPath, db, "get", "TestModGun");
        Assert.StartsWith("1 mod in snapshot", hit, StringComparison.Ordinal);

        // 答案与它无关:话照说,位置在表下面。
        var other = Run(configPath, db, "get", "Apparel_ShieldBelt");
        Assert.Contains(Banner, other, StringComparison.Ordinal);
        Assert.False(other.StartsWith("1 mod in snapshot", StringComparison.Ordinal));
        Assert.True(other.IndexOf(Banner, StringComparison.Ordinal) >
                    other.IndexOf("ludeon.rimworld", StringComparison.Ordinal));

        // 零结果最需要它:被漂移改没的那一行,长得就是这个样子。
        var miss = Run(configPath, db, "get", "zzznosuchdef");
        Assert.StartsWith("1 mod in snapshot", miss, StringComparison.Ordinal);

        // 输出里根本没有 mod 这一维时,证不出无关 —— 一律当有关。
        var agg = Run(configPath, db, "fields", "ThingDef");
        Assert.StartsWith("1 mod in snapshot", agg, StringComparison.Ordinal);

        // 位置会变,于是句子里一个方位词都不许有 —— 沉到表下时 "answers below" 指的是
        // 一片不存在的下文。
        foreach (var stdout in new[] { hit, other, miss, agg })
        {
            var line = stdout[stdout.IndexOf("1 mod in snapshot", StringComparison.Ordinal)..].Split('\n')[0];
            Assert.DoesNotContain("below", line, StringComparison.Ordinal);
            Assert.DoesNotContain("above", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 没漂移时一个字都不说。这条与上一条是同一件事的两半 —— 一条天天响的过期警告
    /// 会被训练成噪声,那时真漂移的那次也就白说了。
    /// </summary>
    [Fact]
    public void 没漂移时普通查询零声明字节()
    {
        var (db, _, _, configPath) = SnapshotOfModTree("e2e-quiet");
        var stdout = Run(configPath, db, "get", "Apparel_ShieldBelt");

        Assert.DoesNotContain("changed on disk", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-export", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 量过了、没变 —— 这一支也要说清没比的是什么。判据是尺寸与时间戳,
    /// 说成「文件一致」就是把一句证不了的话当成背书发出去。
    /// </summary>
    [Fact]
    public void 量过了也要说清比的只是尺寸与时间戳()
    {
        var (db, _, _, configPath) = SnapshotOfModTree("e2e-status");
        var stdout = Run(configPath, db, "snapshot", "status");

        Assert.Contains("file size and timestamp", stdout, StringComparison.Ordinal);
        // 射程外那几处要点名,否则「一致」会被读成整个 mod 目录都比过了。
        Assert.Contains("Languages/", stdout, StringComparison.Ordinal);
        // 假阳性那一面也要说 —— 不说的话,一次 Steam 校验引发的告警会被当成工具坏了。
        Assert.Contains("identical bytes", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// pin 着一份覆盖面与游戏不同的快照时,每次查询**一个字都不提 mod 列表**。
    /// 这是这轮改动的正面:头两行恒定横幅是在不需要时的泛泛提醒,而需要时的精确提醒
    /// (零结果点名哪份快照有那个 def)另有产地。
    /// </summary>
    [Fact]
    public void 多开了mod时查询不提mod列表()
    {
        var (db, _, _, configPath) = SnapshotOfModTree(
            "e2e-superset", ["ludeon.rimworld", "test.mod", "extra.mod"]);
        var stdout = Run(configPath, db, "get", "Apparel_ShieldBelt");

        Assert.DoesNotContain("mod list", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer enabled", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("load order", stdout, StringComparison.Ordinal);
    }

    /// <summary>次序变了照旧当场说 —— 这一条是上一条的反面,少了它「省略」就成了「全静默」。</summary>
    [Fact]
    public void 次序变了查询当场说破()
    {
        var (db, _, _, configPath) = SnapshotOfModTree("e2e-reorder", ["test.mod", "ludeon.rimworld"]);
        var stdout = Run(configPath, db, "get", "Apparel_ShieldBelt");

        Assert.Contains("different load order", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 集合差在 <c>snapshot status</c> 里照旧逐条讲 —— 它是被显式问的那一处。
    /// 还要说破「查询不会提这件事」,否则那份沉默会被当成没差异。
    /// </summary>
    [Fact]
    public void 集合差在status里照旧逐条讲()
    {
        var (db, _, _, configPath) = SnapshotOfModTree(
            "e2e-superset-status", ["ludeon.rimworld", "test.mod", "extra.mod"]);
        var stdout = Run(configPath, db, "snapshot", "status");

        Assert.Contains("1 enabled that this snapshot lacks", stdout, StringComparison.Ordinal);
        Assert.Contains("Ordinary queries stay silent", stdout, StringComparison.Ordinal);
        // 版本与文件那两层的背书不许把 mod 列表也一起背下去。
        Assert.DoesNotContain("same mods", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 多份快照并存时说破这一次选了哪个 —— 选错快照就是答案错,而调用方没说过话。
    ///
    /// 但**只说这一次的选择结果**:「还注册了哪几个」与「用 snapshot list 看」逐字不随
    /// 查询变,产地在 SKILL.md 的 Snapshots 一节,在每次查询上重念是拿上下文交税。
    /// </summary>
    [Fact]
    public void 自动选中的快照报出名字而不附带指路()
    {
        var (db, _, _, configPath) = SnapshotOfModTree("e2e-choice");
        // 只有一份时不存在选错,那时这句话本来就不出;第二份的内容不论,它只负责让
        // 「不止一份」成立。名字排在后面,于是被自动检测挑中的确定是 e2e-choice。
        File.Copy(db, Path.Combine(Path.GetDirectoryName(db)!, "zz-decoy.db"));

        var stdout = Run(configPath, null, "list", "ThingDef");

        Assert.Contains("Using snapshot 'e2e-choice' (auto-detected).", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot list", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 版本这句话是从 ModsConfig.xml 来的时候要说破它弱在哪 —— 那个数只在玩家保存过
    /// mod 列表之后才更新,而「same game build」看上去与读 dll 得到的一模一样。
    /// </summary>
    [Fact]
    public void 版本来自ModsConfig时说破它会落后()
    {
        var (db, _, _, configPath) = SnapshotOfModTree("e2e-weak-version");
        var stdout = Run(configPath, db, "snapshot", "status");

        Assert.Contains("ModsConfig.xml", stdout, StringComparison.Ordinal);
        Assert.Contains("can lag behind what is installed", stdout, StringComparison.Ordinal);
        Assert.Contains("game_dir", stdout, StringComparison.Ordinal);
    }
}
