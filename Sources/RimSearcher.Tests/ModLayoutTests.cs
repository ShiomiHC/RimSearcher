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
