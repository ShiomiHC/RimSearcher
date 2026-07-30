using ICSharpCode.Decompiler.CSharp;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Sources;

namespace RimSearcher.Tests;

/// <summary>
/// 反编译谁、放在哪、叫什么名字。
///
/// 这一批闸看的是**选择**,不是反编译本身 —— 后者是 ILSpy 的活儿,跑一次几十秒。
/// 而选错的代价恰恰更大:多反编译一份旧版本的 dll,<c>code-search</c> 就会拿出根本没在跑的
/// 代码,而它长得跟真答案一模一样。实测代价已经付过:HAR 装了六年,20 个 dll 里在用的只有 2 个。
/// </summary>
public class SourcesTests
{
    private const string GameVersion = "1.6.4871 rev591";

    // ---- 运行时程序集 ----

    [Theory]
    [InlineData("mscorlib.dll")]
    [InlineData("netstandard.dll")]
    [InlineData("System.dll")]
    [InlineData("System.Xml.dll")]
    [InlineData("UnityEngine.dll")]
    [InlineData("UnityEngine.CoreModule.dll")]
    [InlineData("Mono.Security.dll")]
    [InlineData("Microsoft.CSharp.dll")]
    [InlineData("Newtonsoft.Json.dll")]
    [InlineData("websocket-sharp.dll")]
    public void 运行时程序集不反编译(string name)
        => Assert.True(AssemblyFilter.IsRuntimeAssembly(name));

    /// <summary>
    /// 裸前缀 StartsWith 会把这些正常 mod 程序集整批当成运行时库排掉,它们的源码就永远
    /// 进不了树 —— 而查不到的人看不出原因。所以精确名与「点分家族」是两档,不是一档。
    /// </summary>
    [Theory]
    [InlineData("SystematicWeapons.dll")]
    [InlineData("UnityEngineTweaks.dll")]
    [InlineData("I18NPlus.dll")]
    [InlineData("Monolith.dll")]
    [InlineData("Systemic.dll")]
    [InlineData("AlienRace.dll")]
    [InlineData("0Harmony.dll")]
    public void 正常mod程序集不许被当成运行时库(string name)
        => Assert.False(AssemblyFilter.IsRuntimeAssembly(name),
            $"'{name}' would never appear in any source tree, and nothing would say why.");

    // ---- 版本目录与遮蔽 ----

    /// <summary>
    /// 同一个 dll 在根 <c>Assemblies/</c> 与版本目录里各一份时,游戏用版本目录那份
    /// (实测 HAR 就是这样)。取错的后果是反编译出好几年前的代码。
    /// </summary>
    [Fact]
    public void 版本目录里的dll顶掉根目录的同名dll()
    {
        using var mod = new TempMod();
        mod.Dll("Assemblies/AlienRace.dll");
        mod.Dll("1.6/Assemblies/AlienRace.dll");

        var got = ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([]));
        Assert.Equal([mod.Path("1.6/Assemblies/AlienRace.dll")], got);
    }

    /// <summary>旧游戏版本的目录整支不进。它们是历史死代码,游戏一个字都不加载。</summary>
    [Fact]
    public void 旧版本目录整支不进()
    {
        using var mod = new TempMod();
        mod.Dll("1.0/Assemblies/Old.dll");
        mod.Dll("1.5/Assemblies/Old.dll");
        mod.Dll("1.6/Assemblies/New.dll");

        var got = ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([]));
        Assert.Equal([mod.Path("1.6/Assemblies/New.dll")], got);
    }

    /// <summary>
    /// <c>1.6_unofficial</c> 不是「1.6 的另一种写法」,而是一条靠 <c>IfModActive</c> 开关的
    /// 互斥分支(实测:RatkinGene)。把它当版本目录会让两套代码同时进树。
    /// </summary>
    [Fact]
    public void 带后缀的目录名不算版本目录()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/Official.dll");
        mod.Dll("1.6_unofficial/Assemblies/Unofficial.dll");

        var got = ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([]));
        Assert.Equal([mod.Path("1.6/Assemblies/Official.dll")], got);
    }

    /// <summary>没有任何 1.6 目录时退到「小于等于当前版本的最高一个」,与游戏一致。</summary>
    [Fact]
    public void 没有当前版本目录时退到最接近的旧版本()
    {
        using var mod = new TempMod();
        mod.Dll("1.4/Assemblies/A.dll");
        mod.Dll("1.5/Assemblies/A.dll");

        var got = ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([]));
        Assert.Equal([mod.Path("1.5/Assemblies/A.dll")], got);
    }

    // ---- loadFolders.xml ----

    /// <summary>
    /// 互斥分支由**启用了哪些 mod** 裁定,而那件事快照里已经记着。旧世系靠 config 里手写一条
    /// <c>active_mods</c>,而手写清单会漂 —— 这个错刚在 export 上付过一次代价。
    /// </summary>
    [Fact]
    public void 互斥分支按启用的mod裁()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/Official.dll");
        mod.Dll("1.6_unofficial/Assemblies/Unofficial.dll");
        mod.LoadFolders("""
            <loadFolders>
              <v1.6>
                <li IfModActive="fxz.other">1.6_unofficial</li>
                <li IfModActive="solaris.ratkinracemod">1.6</li>
              </v1.6>
            </loadFolders>
            """);

        var official = ModFolders.Assemblies(mod.Root, "1.6.4871",
            ModFolders.NormalizeActive(["Solaris.RatkinRaceMod"]));
        Assert.Equal([mod.Path("1.6/Assemblies/Official.dll")], official);

        var unofficial = ModFolders.Assemblies(mod.Root, "1.6.4871",
            ModFolders.NormalizeActive(["fxz.other"]));
        Assert.Equal([mod.Path("1.6_unofficial/Assemblies/Unofficial.dll")], unofficial);
    }

    /// <summary>
    /// Steam 订阅那份 id 尾巴上挂 <c>_steam</c>,而 loadFolders 里写的是裸 id。
    /// 游戏比对时忽略这个后缀(<c>ignorePostfix</c>),不忽略就等于整条分支永远关着。
    /// </summary>
    [Fact]
    public void steam后缀不影响启用判定()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/A.dll");
        mod.LoadFolders("""
            <loadFolders>
              <v1.6><li IfModActive="some.mod">1.6</li></v1.6>
            </loadFolders>
            """);

        Assert.Single(ModFolders.Assemblies(mod.Root, "1.6.4871",
            ModFolders.NormalizeActive(["some.mod_steam"])));
    }

    /// <summary>
    /// 声明了本版本的目录列表,就**只**用它 —— 根目录、Common、版本目录一概不再自动补。
    /// 这一条最容易想当然地补上,而补了就等于让被条件关掉的那套又漏进来。
    /// </summary>
    [Fact]
    public void loadFolders声明过就不再自动补根目录()
    {
        using var mod = new TempMod();
        mod.Dll("Assemblies/Root.dll");
        mod.Dll("1.6/Assemblies/Versioned.dll");
        mod.LoadFolders("""
            <loadFolders>
              <v1.6><li>1.6</li></v1.6>
            </loadFolders>
            """);

        Assert.Equal([mod.Path("1.6/Assemblies/Versioned.dll")],
            ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([])));
    }

    /// <summary>
    /// 游戏的完整版本号是 <c>1.6.4871</c>,而 mod 写的键几乎总是 <c>1.6</c>。
    /// 只做精确匹配的话,**每一个** loadFolders.xml 都会被判成不适用 —— 于是这个回退不是花活。
    /// </summary>
    [Fact]
    public void 版本键回退到小于等于当前的最高一个()
    {
        using var mod = new TempMod();
        mod.Dll("1.5/Assemblies/Old.dll");
        mod.Dll("1.6/Assemblies/New.dll");
        mod.LoadFolders("""
            <loadFolders>
              <v1.5><li>1.5</li></v1.5>
              <v1.6><li>1.6</li></v1.6>
            </loadFolders>
            """);

        Assert.Equal([mod.Path("1.6/Assemblies/New.dll")],
            ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([])));
    }

    /// <summary>坏 loadFolders.xml 退回默认布局,不是「一个目录都不加载」。</summary>
    [Fact]
    public void 坏的loadFolders退回默认布局()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/A.dll");
        mod.LoadFolders("<loadFolders><v1.6>这不是合法 xml");

        Assert.Equal([mod.Path("1.6/Assemblies/A.dll")],
            ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([])));
    }

    /// <summary>
    /// 去重的键是 <c>Assemblies/</c> 起算的**相对路径**,不是文件名:游戏眼里
    /// <c>Assemblies/a/x.dll</c> 与 <c>Assemblies/b/x.dll</c> 是两个文件,都会加载。
    /// </summary>
    [Fact]
    public void 子目录里的同名dll是两个文件()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/a/X.dll");
        mod.Dll("1.6/Assemblies/b/X.dll");

        Assert.Equal(2, ModFolders.Assemblies(mod.Root, "1.6.4871", ModFolders.NormalizeActive([])).Count);
    }

    // ---- 树名 ----

    /// <summary>
    /// 树名就是 packageId —— <c>rimsearcher mods</c> 第二列那个,<c>--scope</c> 认的那个。
    /// 另起一套短名字等于给同一件事造第二个产地,而两套名字迟早对不上。
    /// </summary>
    [Fact]
    public void 树名取packageId()
    {
        using var har = new TempMod();
        har.Dll("1.6/Assemblies/AlienRace.dll");

        var plans = SourcePlanner.Plan(
            new RimConfig(), ["erdelf.humanoidalienraces"], GameVersion,
            Installed(("erdelf.humanoidalienraces", har.Root)), out _);

        Assert.Equal(["erdelf.humanoidalienraces"], plans.Select(p => p.Name));
    }

    /// <summary>
    /// 本体与五个 DLC 合成一棵 <c>vanilla</c>。游戏代码就是一套程序集,DLC 只加数据 ——
    /// 摊成六棵会把同一份 Assembly-CSharp 反编译六遍。
    /// </summary>
    [Fact]
    public void 本体与DLC合成一棵vanilla()
    {
        using var game = new TempMod();
        game.Dll("RimWorldWin64_Data/Managed/Assembly-CSharp.dll");
        game.Dll("RimWorldWin64_Data/Managed/mscorlib.dll");

        var plans = SourcePlanner.Plan(
            new RimConfig { GameDir = game.Root },
            ["ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.anomaly"],
            GameVersion, Installed(), out _);

        var vanilla = Assert.Single(plans);
        Assert.Equal(SourcePlanner.VanillaTree, vanilla.Name);
        // 运行时库不进,连游戏目录也不例外。
        Assert.Equal([game.Path("RimWorldWin64_Data/Managed/Assembly-CSharp.dll")], vanilla.Assemblies);
    }

    /// <summary>纯 XML mod 不建树。一棵空目录只是噪音,而 <c>sources list</c> 还得为它编一行状态。</summary>
    [Fact]
    public void 不加载程序集的mod不建树()
    {
        using var xmlOnly = new TempMod();
        xmlOnly.File("1.6/Defs/Things.xml", "<Defs />");

        var plans = SourcePlanner.Plan(new RimConfig(), ["some.xmlmod"], GameVersion,
            Installed(("some.xmlmod", xmlOnly.Root)), out _);
        Assert.Empty(plans);
    }

    /// <summary>导出器自己不建树:它的源码就在本仓,反编译自己的产物没有意义。</summary>
    [Fact]
    public void 导出器自己不建树()
    {
        using var exporter = new TempMod();
        exporter.Dll("Assemblies/RimSearcher.DataMod.dll");

        var plans = SourcePlanner.Plan(new RimConfig(),
            [RimSearcher.Contract.IntermediateFormat.ExporterPackageId], GameVersion,
            Installed((RimSearcher.Contract.IntermediateFormat.ExporterPackageId, exporter.Root)), out _);
        Assert.Empty(plans);
    }

    /// <summary>没装的 mod 单独报,不能默默漏掉 —— 漏掉就是一棵本该有的树无声地不存在。</summary>
    [Fact]
    public void 没装的mod单独报()
    {
        SourcePlanner.Plan(new RimConfig(), ["not.installed"], GameVersion, Installed(), out var missing);
        Assert.Equal(["not.installed"], missing);
    }

    /// <summary><c>1.6.4871 rev591</c> → <c>1.6.4871</c>。loadFolders 比的是不带 rev 的那截。</summary>
    [Fact]
    public void 版本号去掉rev后缀()
        => Assert.Equal("1.6.4871", SourcePlanner.NormalizeGameVersion("1.6.4871 rev591"));

    // ---- 产物 ----

    /// <summary>
    /// 语言档位锁在 C# 9:RimWorld 跑在 Unity 2022.3,这是 Ludeon 真能写出的形态。
    /// 它还是字节级稳定的前提 —— 档位一变,一万四千个文件全重排,真正的改动就淹了。
    /// </summary>
    [Fact]
    public void 反编译语言档位锁在csharp9()
        => Assert.Equal(LanguageVersion.CSharp9_0, Decompiler.CreateSettings().GetMinimumRequiredVersion());

    /// <summary>
    /// 产出必须逐次相同。反编译器给每个 .csproj 生成一个新的随机 ProjectGuid,而那是整棵树里
    /// 唯一不确定的东西 —— 实测一次「什么都没变」的重跑:一万四千个 .cs 逐字节相同,
    /// 29 个 .csproj 全红,红的只有那一行。二十九条假改动会把真改动埋掉,而这个仓只为真改动存在。
    /// </summary>
    [Fact]
    public void 项目GUID由项目名定而不是每次随机()
    {
        Assert.Equal(Decompiler.StableProjectGuid("NVorbis"), Decompiler.StableProjectGuid("NVorbis"));
        Assert.NotEqual(Decompiler.StableProjectGuid("NVorbis"), Decompiler.StableProjectGuid("NAudio"));
        // 形状要还是 GUID:它照样得能被 msbuild 读。
        Assert.True(Guid.TryParse(Decompiler.StableProjectGuid("NVorbis"), out _));
    }

    /// <summary>
    /// 清单里的路径相对 mod 根,而且**没有时间戳**。绝对路径会让库一搬家每棵树都变红;
    /// 时间戳会让每次同步都无端改一行 —— 而那件事 git 的提交时间已经记着了。
    /// </summary>
    [Fact]
    public void 清单不含绝对路径与时间戳()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/A.dll");

        var plan = new SourceTreePlan
        {
            Name = "some.mod",
            PackageId = "some.mod",
            Root = mod.Root,
            Assemblies = [mod.Path("1.6/Assemblies/A.dll")],
        };
        var manifest = SourcePlanner.Manifest(plan, GameVersion);

        Assert.Equal("1.6/Assemblies/A.dll", Assert.Single(manifest.Assemblies).Path);

        using var tree = new TempMod();
        manifest.Write(tree.Root);
        var text = System.IO.File.ReadAllText(System.IO.Path.Combine(tree.Root, SourceTreeState.FileName));
        Assert.DoesNotContain(mod.Root, text);
        foreach (var word in new[] { "utc", "time", "date", "synced" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 同一批 dll 再算一遍,清单必须逐字相同 —— 否则「没变就不重跑」这条判据就是假的,
    /// 每次同步都会重建整棵树,而 git diff 里全是噪音。
    /// </summary>
    [Fact]
    public void 同一批dll算出同一份清单()
    {
        using var mod = new TempMod();
        mod.Dll("1.6/Assemblies/A.dll");
        var plan = new SourceTreePlan
        {
            Name = "some.mod", PackageId = "some.mod", Root = mod.Root,
            Assemblies = [mod.Path("1.6/Assemblies/A.dll")],
        };

        Assert.True(SourcePlanner.Manifest(plan, GameVersion)
                        .SameSources(SourcePlanner.Manifest(plan, GameVersion)));
    }

    /// <summary>
    /// 别人手工维护的源码副本不许被覆盖:一次配置笔误的代价不该是抹掉它。
    /// 空目录与带标记的目录算我们的,其余不算。
    /// </summary>
    [Fact]
    public void 非空且无标记的目录不算我们的()
    {
        using var dir = new TempMod();
        Assert.True(SourceTreeState.IsOurs(dir.Root));           // 空的

        dir.File("SomeoneElse.cs", "class X {}");
        Assert.False(SourceTreeState.IsOurs(dir.Root));

        dir.File(SourceTreeState.LegacyMarker, "");
        Assert.True(SourceTreeState.IsOurs(dir.Root));           // 旧世系建的也认
    }

    /// <summary>
    /// 树里不再写一个 <c>*</c> 的 .gitignore。旧世系写它是为了防止使用者的 mod 工程仓库
    /// 顺手把产物一起提交 —— 那个顾虑是对的,但现在这棵树**自己就是一个 git 仓**,
    /// 嵌套的 .git 本来就让外层仓库看不进来,而且比 ignore 规则更硬(外层改不掉它)。
    /// 留着那个文件反而会屏蔽掉这棵树自己的版本控制,也就屏蔽掉唯一的 diff 能力。
    /// </summary>
    [Fact]
    public void 清单落地时不写屏蔽自己的gitignore()
    {
        using var tree = new TempMod();
        SourcePlanner.Manifest(
            new SourceTreePlan { Name = "x", PackageId = "x", Root = tree.Root, Assemblies = [] },
            GameVersion).Write(tree.Root);

        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(tree.Root, ".gitignore")),
            "A '*' .gitignore in the tree would hide the tree from its own repository — " +
            "and that repository is the only thing that can answer 'what changed'.");
    }

    /// <summary>
    /// <c>--source</c> 打不中时不许只给一个「看起来像」的答案。树名是 packageId(全名),
    /// 而人记得的往往是外号 —— 外号不在任何数据里,打分器只能给出看似合理却错的那一个。
    /// 实测:<c>--source HAR</c> 换来 <c>brrainz.harmony</c>,而真正要的是
    /// <c>erdelf.humanoidalienraces</c>。错的独家建议比没有建议更坏:它看着像答案,
    /// 于是没人再去看名单。
    /// </summary>
    [Fact]
    public void 树名打不中时要指向完整名单而不是只给一个看起来像的()
    {
        var trees = new[] { "brrainz.harmony", "erdelf.humanoidalienraces", "vanilla" };
        var said = CodeSearchCommand.NoSuchTree("HAR", trees);

        Assert.Contains("brrainz.harmony", said);              // 打分器给的那个,允许出现
        Assert.Contains("nickname matches nothing", said);     // 但必须说破它为什么可能是错的
        Assert.Contains("sources list", said);                 // 且必须指向完整名单
    }

    private static Dictionary<string, InstalledMod> Installed(params (string Id, string Dir)[] mods)
        => mods.ToDictionary(m => m.Id, m => new InstalledMod(m.Id, m.Id, m.Dir),
                             StringComparer.OrdinalIgnoreCase);

    /// <summary>一次性的假 mod 目录。真 mod 目录随订阅内容变,拿它当输入闸会天天红。</summary>
    private sealed class TempMod : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rimsearcher-sources-tests", Guid.NewGuid().ToString("N"));

        public TempMod() => Directory.CreateDirectory(Root);

        public string Path(string relative)
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        public void File(string relative, string content)
        {
            var full = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
        }

        /// <summary>内容不重要:这一批闸判的是「选了哪些文件」,不是反编译结果。</summary>
        public void Dll(string relative) => File(relative, "not a real assembly");

        public void LoadFolders(string xml) => File("loadFolders.xml", xml);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
