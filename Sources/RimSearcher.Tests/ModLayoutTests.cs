using RimSearcher.Core;

namespace RimSearcher.Tests;

public class ModLayoutTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 没有 loadFolders.xml 时的默认布局：版本目录压过根目录
    [Fact]
    public void Resolve_DefaultLayout_PrefersVersionFolderOverRoot()
    {
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Traits.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Traits.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal("1.6", layout.Version);
        Assert.Equal(
            [Path.Combine(_workspace.Root, "Mod", "1.6"), Path.Combine(_workspace.Root, "Mod")],
            layout.Folders);
    }

    // 覆盖是文件级的：根目录那份 Traits.xml 整个不被解析，而根目录独有的文件照常收
    [Fact]
    public void Resolve_ShadowsRootFilesWithTheSameRelativePath()
    {
        var shadowed = _workspace.WriteFile(Path.Combine("Mod", "Defs", "Traits.xml"), "<Defs />");
        var survivor = _workspace.WriteFile(Path.Combine("Mod", "Defs", "Legacy.xml"), "<Defs />");
        var winner = _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Traits.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Contains(shadowed, layout.Shadowed);
        Assert.DoesNotContain(survivor, layout.Shadowed);
        Assert.DoesNotContain(winner, layout.Shadowed);
    }

    // 相对路径是相对于 mod 文件夹根算的，故子目录不同就不算同一个文件
    [Fact]
    public void Resolve_ComparesPathsRelativeToTheModFolder()
    {
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Races", "Alien.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Alien.xml"), "<Defs />");

        Assert.Empty(Resolve("Mod", "1.6").Shadowed);
    }

    // dll 与 xml 同一套规则
    [Fact]
    public void Resolve_ShadowsAssembliesToo()
    {
        var old = _workspace.WriteFile(Path.Combine("Mod", "Assemblies", "Mod.dll"), "x");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Assemblies", "Mod.dll"), "x");

        var layout = Resolve("Mod", "1.6");

        Assert.Contains(old, layout.Shadowed);
        Assert.Equal(2, layout.AssemblyDirs.Count);
    }

    // 旧版本目录一份都不进：它们才是这个功能要清掉的东西
    [Fact]
    public void Resolve_IgnoresOlderVersionFolders()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.4", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.0", "Assemblies", "Old.dll"), "x");

        var layout = Resolve("Mod", "1.6");

        Assert.All(layout.XmlDirs, path => Assert.DoesNotContain("1.4", path));
        Assert.Empty(layout.AssemblyDirs);
    }

    // 比当前版本新的目录同样不进——游戏自己也不加载它们
    [Fact]
    public void Resolve_IgnoresNewerVersionFolders()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.7", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "Defs"), Assert.Single(layout.XmlDirs));
    }

    // loadFolders.xml 说了算：列表里越靠后优先级越高
    [Fact]
    public void Resolve_LoadFolders_LastEntryWins()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>/</li>
                    <li>Common</li>
                    <li>1.6</li>
                </v1.6>
            </loadFolders>
            """);
        var rootFile = _workspace.WriteFile(Path.Combine("Mod", "Defs", "A.xml"), "<Defs />");
        var commonFile = _workspace.WriteFile(Path.Combine("Mod", "Common", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(
            [
                Path.Combine(_workspace.Root, "Mod", "1.6"),
                Path.Combine(_workspace.Root, "Mod", "Common"),
                Path.Combine(_workspace.Root, "Mod")
            ],
            layout.Folders);
        Assert.Contains(rootFile, layout.Shadowed);
        Assert.Contains(commonFile, layout.Shadowed);
    }

    // ModContentPack.InitLoadFolders 的默认布局里有 Common，排在版本目录之后、根之前
    [Fact]
    public void Resolve_DefaultLayout_IncludesCommonFolder()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Common", "Defs", "B.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "C.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(
            [
                Path.Combine(_workspace.Root, "Mod", "1.6"),
                Path.Combine(_workspace.Root, "Mod", "Common"),
                Path.Combine(_workspace.Root, "Mod")
            ],
            layout.Folders);
    }

    // 没有当前版本的节点时用 ≤当前版本的最高版本节点（InitLoadFolders 的第二步）。
    // 节点声明的版本未必有同名目录，故这一步不能靠按目录名建的版本链。
    [Fact]
    public void Resolve_LoadFolders_UsesHighestOlderVersionNode()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.3><li>Legacy</li></v1.3>
                <v1.5><li>Modern</li></v1.5>
                <v1.7><li>Future</li></v1.7>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "Legacy", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Modern", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Future", "Defs", "A.xml"), "<Defs />");

        Assert.Equal(
            Path.Combine(_workspace.Root, "Mod", "Modern", "Defs"),
            Assert.Single(Resolve("Mod", "1.6").XmlDirs));
    }

    // 版本节点都对不上时还有 <default>（已废弃但游戏仍认）
    [Fact]
    public void Resolve_LoadFolders_FallsBackToDefaultNode()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.7><li>Future</li></v1.7>
                <default><li>Fallback</li></default>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "Future", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Fallback", "Defs", "A.xml"), "<Defs />");

        Assert.Equal(
            Path.Combine(_workspace.Root, "Mod", "Fallback", "Defs"),
            Assert.Single(Resolve("Mod", "1.6").XmlDirs));
    }

    // loadFolders 里没有当前版本的节点时退回默认布局，而不是什么都不加载
    [Fact]
    public void Resolve_LoadFolders_FallsBackWhenVersionNodeMissing()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.4><li>1.4</li></v1.4>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");

        Assert.Equal(
            Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"),
            Assert.Single(Resolve("Mod", "1.6").XmlDirs));
    }

    // IfModActive 指向的补丁目录全收：手动指 mod 根时无从判断哪些 mod 处于启用状态
    [Fact]
    public void Resolve_LoadFolders_IncludesConditionalFolders()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>/</li>
                    <li IfModActive="Ludeon.RimWorld.Odyssey">1.6/Mods/Odyssey</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Mods", "Odyssey", "Patches", "B.xml"), "<Patch />");

        var layout = Resolve("Mod", "1.6");

        Assert.Contains(
            Path.Combine(_workspace.Root, "Mod", "1.6", "Mods", "Odyssey", "Patches"),
            layout.XmlDirs);
        Assert.Contains(layout.Notes, note => note.Contains("conditional"));
    }

    // 一个 mod 用两组互斥条件挂两套内容（RatkinGene 的 1.6 与 1.6_unofficial）。没给
    // active_mods 时两套全收，谁遮蔽谁由 loadFolders 的书写顺序决定——那未必是实际生效的
    // 那套，所以必须提示。
    [Fact]
    public void Resolve_ReportsMutuallyExclusiveConditionalFolders()
    {
        WriteExclusiveBranches();

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(3, layout.XmlDirs.Count);
        Assert.Contains(layout.Notes, note => note.Contains("mutually exclusive"));
        Assert.Contains(layout.Notes, note => note.Contains("active_mods"));
    }

    // 给了 active_mods，落选的那套分支根本不进目录表——不是「进了再被遮蔽」
    [Fact]
    public void Resolve_ActiveMods_KeepsOnlyTheMatchingBranch()
    {
        WriteExclusiveBranches();

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["official.mod"]);

        Assert.NotNull(layout);
        Assert.Equal(
            [
                Path.Combine(_workspace.Root, "Mod", "official", "Defs"),
                Path.Combine(_workspace.Root, "Mod", "Defs")
            ],
            layout.XmlDirs);
        Assert.Empty(layout.Shadowed);
        Assert.DoesNotContain(layout.Notes, note => note.Contains("mutually exclusive"));
        Assert.Contains(layout.Notes, note => note.Contains("skipped by active_mods"));
    }

    // packageId 比对不分大小写：ModsConfig.xml 里全是小写，loadFolders 里常写驼峰
    [Fact]
    public void Resolve_ActiveMods_MatchesCaseInsensitively()
    {
        WriteExclusiveBranches();

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["OFFICIAL.MOD"]);

        Assert.Contains(
            Path.Combine(_workspace.Root, "Mod", "official", "Defs"),
            layout!.XmlDirs);
    }

    // 白名单谁都没匹配上、又没有无条件内容兜着时，整个 mod 会变空——config 里明确指了它，
    // 此时回退全收。（无条件目录仍有内容的话不回退：那本就是「前置都没装」的正确结果）
    [Fact]
    public void Resolve_ActiveMods_FallsBackWhenTheModWouldBeEmpty()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li IfModActive="unofficial.mod">unofficial</li>
                    <li IfModActive="official.mod">official</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "official", "Defs", "Genes.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "unofficial", "Defs", "Genes.xml"), "<Defs />");

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["someone.else"]);

        Assert.NotNull(layout);
        Assert.Equal(2, layout.XmlDirs.Count);
        Assert.Contains(layout.Notes, note => note.Contains("fell back to including all"));
    }

    // 反过来：条件目录全被筛掉但基础内容还在，那就是「前置都没装」的正常结果，不该回退
    [Fact]
    public void Resolve_ActiveMods_DoesNotFallBackWhenBaseContentRemains()
    {
        WriteExclusiveBranches();

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["someone.else"]);

        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "Defs"), Assert.Single(layout!.XmlDirs));
        Assert.DoesNotContain(layout.Notes, note => note.Contains("fell back to including all"));
    }

    // 「DLC 装了就替换基础定义」是常规覆盖，不是互斥分支，不该报冲突
    [Fact]
    public void Resolve_ConditionalOverridingUnconditional_IsNotAConflict()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>1.6</li>
                    <li IfModActive="Ludeon.RimWorld.Ideology">1.6/Mods/Ideology</li>
                </v1.6>
            </loadFolders>
            """);
        var overridden = _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Mods", "Ideology", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Contains(overridden, layout.Shadowed);
        Assert.DoesNotContain(layout.Notes, note => note.Contains("mutually exclusive"));
    }

    [Fact]
    public void Resolve_ActiveMods_HonorsIfModNotActive()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>1.6</li>
                    <li IfModNotActive="some.patch">Fallback</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Fallback", "Defs", "B.xml"), "<Defs />");

        var withPatch = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["some.patch"]);
        var withoutPatch = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["other.mod"]);

        Assert.DoesNotContain(withPatch!.XmlDirs, path => path.Contains("Fallback"));
        Assert.Contains(withoutPatch!.XmlDirs, path => path.Contains("Fallback"));
    }

    // IfModActive="A, B" 是「任一启用」（LoadFolder.requiredAnyOfPackageIds + AnyModActiveNoSuffix）。
    // 作者常拿它写「CE 的 steam 版或非 steam 版装了任一个」，判成「都要装」会整块漏掉。
    [Fact]
    public void Resolve_ActiveMods_IfModActiveIsAnyOf()
    {
        WriteConditionFixture("IfModActive=\"mod.a, mod.b\"");

        var one = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a"]);
        var none = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.c"]);

        Assert.Contains(one!.XmlDirs, path => path.Contains("Conditional"));
        Assert.DoesNotContain(none!.XmlDirs, path => path.Contains("Conditional"));
    }

    // IfModActiveAll 才是「全部启用」
    [Fact]
    public void Resolve_ActiveMods_IfModActiveAllRequiresEveryId()
    {
        WriteConditionFixture("IfModActiveAll=\"mod.a, mod.b\"");

        var partial = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a"]);
        var complete = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a", "mod.b"]);

        Assert.DoesNotContain(partial!.XmlDirs, path => path.Contains("Conditional"));
        Assert.Contains(complete!.XmlDirs, path => path.Contains("Conditional"));
    }

    // 三个属性可以挂在同一个 li 上，取合取
    [Fact]
    public void Resolve_ActiveMods_CombinesConditionsOnOneItem()
    {
        WriteConditionFixture("IfModActive=\"mod.a\" IfModNotActive=\"mod.blocker\"");

        var allowed = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a"]);
        var blocked = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a", "mod.blocker"]);

        Assert.Contains(allowed!.XmlDirs, path => path.Contains("Conditional"));
        Assert.DoesNotContain(blocked!.XmlDirs, path => path.Contains("Conditional"));
    }

    // ModsConfig.xml 里 steam 版 mod 的 packageId 带 _steam 后缀，而条件判定走的是
    // AnyModActiveNoSuffix——两边都要脱掉后缀再比
    [Fact]
    public void Resolve_ActiveMods_IgnoresSteamPostfix()
    {
        WriteConditionFixture("IfModActive=\"mod.a\"");

        var layout = ModLayoutResolver.Resolve(
            Path.Combine(_workspace.Root, "Mod"), "1.6", ["mod.a_steam"]);

        Assert.Contains(layout!.XmlDirs, path => path.Contains("Conditional"));
    }

    private void WriteConditionFixture(string attributes)
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), $"""
            <loadFolders>
                <v1.6>
                    <li>1.6</li>
                    <li {attributes}>Conditional</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Conditional", "Defs", "B.xml"), "<Defs />");
    }

    // RatkinGene 的形态：两个分支各挂一个前置，内容文件同名
    private void WriteExclusiveBranches()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), """
            <loadFolders>
                <v1.6>
                    <li>/</li>
                    <li IfModActive="unofficial.mod">unofficial</li>
                    <li IfModActive="official.mod">official</li>
                </v1.6>
            </loadFolders>
            """);
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Base.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "official", "Defs", "Genes.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "unofficial", "Defs", "Genes.xml"), "<Defs />");
    }

    // 只支持到旧版本的 mod：按规则本该什么都不加载，但用户手动指了它就是想搜它。
    // 回退并在 Notes 里说清楚，而不是静默返回空。
    [Fact]
    public void Resolve_FallsBackToOlderVersionWhenNothingMatches()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.4", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal("1.4", layout.Version);
        Assert.Contains(layout.Notes, note => note.Contains("fell back"));
    }

    // 版本未知（Version.txt 读不到）时取目录里最高的那个，而不是把七个版本全收
    [Fact]
    public void Resolve_WithoutGameVersion_UsesHighestVersionFolder()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.4", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", null);

        Assert.Equal("1.6", layout.Version);
        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"), Assert.Single(layout.XmlDirs));
    }

    // "1.10" > "1.6"：逐段按数值比，别落回字符串序
    [Fact]
    public void Resolve_ComparesVersionSegmentsNumerically()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.10", "Defs", "A.xml"), "<Defs />");

        Assert.Equal("1.10", Resolve("Mod", null).Version);
    }

    // Languages/Textures 不进索引
    [Fact]
    public void Resolve_CollectsOnlyDefsPatchesAndAssemblies()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Patches", "B.xml"), "<Patch />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Languages", "ChineseSimplified", "C.xml"), "<x />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Textures", "d.png"), "x");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(
            [
                Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"),
                Path.Combine(_workspace.Root, "Mod", "1.6", "Patches")
            ],
            layout.XmlDirs);
    }

    // workshop 的目录名是纯数字 ID，源名得从 About.xml 里取
    [Fact]
    public void Resolve_ReadsModNameFromAbout()
    {
        _workspace.WriteFile(Path.Combine("839005762", "About", "About.xml"), """
            <ModMetaData>
                <name>Humanoid Alien Races</name>
                <author>erdelf</author>
            </ModMetaData>
            """);
        _workspace.WriteFile(Path.Combine("839005762", "Defs", "A.xml"), "<Defs />");

        Assert.Equal("Humanoid Alien Races", Resolve("839005762", "1.6").Name);
    }

    [Fact]
    public void Resolve_ReturnsNullForMissingRoot()
        => Assert.Null(ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, "nope"), "1.6"));

    // 内容目录一个都没有的 mod（纯贴图包）不该被当成解析失败，但也不该产出路径
    [Fact]
    public void Resolve_ReportsNoContentInsteadOfFailing()
    {
        _workspace.WriteFile(Path.Combine("Mod", "Textures", "a.png"), "x");

        var layout = Resolve("Mod", "1.6");

        Assert.NotNull(layout);
        Assert.False(layout.HasContent);
    }

    // 回归：纯汉化包（各版本目录下只有 Languages）会把整条版本链试完，报出来的必须仍是首选
    // 那份布局。留下链尾那次尝试的残留，日志里就会显示一个这个 mod 根本没走的版本。
    [Fact]
    public void Resolve_WithoutContent_StillReportsThePreferredLayout()
    {
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Languages", "ChineseSimplified", "a.xml"), "<x />");
        _workspace.WriteFile(Path.Combine("Mod", "1.5", "Languages", "ChineseSimplified", "a.xml"), "<x />");

        var layout = Resolve("Mod", "1.6");

        Assert.False(layout.HasContent);
        Assert.Equal("1.6", layout.Version);
        Assert.Equal(
            [Path.Combine(_workspace.Root, "Mod", "1.6"), Path.Combine(_workspace.Root, "Mod")],
            layout.Folders);
        Assert.Empty(layout.Notes);
    }

    // 坏掉的 loadFolders.xml 退到默认布局，而不是让整个 mod 消失
    [Fact]
    public void Resolve_MalformedLoadFolders_FallsBackToDefaultLayout()
    {
        _workspace.WriteFile(Path.Combine("Mod", "loadFolders.xml"), "<loadFolders><v1.6>");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");

        var layout = Resolve("Mod", "1.6");

        Assert.Equal(Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"), Assert.Single(layout.XmlDirs));
        Assert.Contains(layout.Notes, note => note.Contains("unreadable"));
    }

    private ModLayout Resolve(string relativeRoot, string? gameVersion)
    {
        var layout = ModLayoutResolver.Resolve(Path.Combine(_workspace.Root, relativeRoot), gameVersion);
        Assert.NotNull(layout);
        return layout;
    }
}
