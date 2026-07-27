using RimSearcher.Server;

namespace RimSearcher.Tests;

// [[sources]] 里写 mod 根之后，config 层展开出来的东西
public class ModSourceExpansionTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Mod_ExpandsToEffectiveDefAndAssemblyDirectories()
    {
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Traits.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Traits.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Patches", "P.xml"), "<Patch />");
        _workspace.WriteFile(Path.Combine("Mod", "1.4", "Defs", "Old.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Assemblies", "Mod.dll"), "x");

        var sources = Resolve("Mod");

        Assert.Equal(
            [
                Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"),
                Path.Combine(_workspace.Root, "Mod", "1.6", "Patches"),
                Path.Combine(_workspace.Root, "Mod", "Defs")
            ],
            sources.Xml.Select(entry => entry.Path));

        Assert.Equal(
            Path.Combine(_workspace.Root, "Mod", "1.6", "Assemblies"),
            Assert.Single(Assert.Single(sources.Csharp).AssemblyPaths));
    }

    // 根 Defs 目录仍在路径表里（它可能有独有文件），被顶掉的那几个文件靠 Shadowed 剔除
    [Fact]
    public void Mod_CollectsShadowedFilesForTheIndexer()
    {
        var shadowed = _workspace.WriteFile(Path.Combine("Mod", "Defs", "Traits.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "Defs", "Only.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "Traits.xml"), "<Defs />");

        Assert.Equal(shadowed, Assert.Single(Resolve("Mod").Shadowed));
    }

    [Fact]
    public void Mod_TakesSourceNameFromAboutXml()
    {
        _workspace.WriteFile(Path.Combine("839005762", "About", "About.xml"),
            "<ModMetaData><name>Humanoid Alien Races</name></ModMetaData>");
        _workspace.WriteFile(Path.Combine("839005762", "1.6", "Defs", "A.xml"), "<Defs />");

        Assert.Equal("Humanoid Alien Races", Assert.Single(Resolve("839005762").Xml).Name);
    }

    // 显式写的 name 是用户的决定，About.xml 不该顶掉它——scope 表达式和 scope_groups 都指着它
    [Fact]
    public void Mod_KeepsExplicitName()
    {
        _workspace.WriteFile(Path.Combine("839005762", "About", "About.xml"),
            "<ModMetaData><name>Humanoid Alien Races</name></ModMetaData>");
        _workspace.WriteFile(Path.Combine("839005762", "1.6", "Defs", "A.xml"), "<Defs />");

        var config = Parse($"""
            game_version = "1.6"

            [[sources]]
            name = "HAR"
            mod  = '{Path.Combine(_workspace.Root, "839005762")}'
            """);

        Assert.Equal("HAR", Assert.Single(config.ResolveSources().Xml).Name);
    }

    // mod 与手写 xml 并存：手写的那条在前，展开结果追加在后
    [Fact]
    public void Mod_AppendsToExplicitlyConfiguredPaths()
    {
        var manual = _workspace.Dir("Manual");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");

        var config = Parse($"""
            game_version = "1.6"

            [[sources]]
            name = "X"
            mod  = '{Path.Combine(_workspace.Root, "Mod")}'
            xml  = '{manual}'
            """);

        Assert.Equal(
            [manual, Path.Combine(_workspace.Root, "Mod", "1.6", "Defs")],
            config.ResolveSources().Xml.Select(entry => entry.Path));
    }

    // 一个 [[sources]] 可以指多个 mod 根，展开结果归到同一个源
    [Fact]
    public void Mod_AcceptsMultipleRoots()
    {
        _workspace.WriteFile(Path.Combine("A", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("B", "1.6", "Defs", "B.xml"), "<Defs />");

        var config = Parse($"""
            game_version = "1.6"

            [[sources]]
            name = "pack"
            mods = [ '{Path.Combine(_workspace.Root, "A")}', '{Path.Combine(_workspace.Root, "B")}' ]
            """);

        var xml = config.ResolveSources().Xml;

        Assert.Equal(2, xml.Count);
        Assert.All(xml, entry => Assert.Equal("pack", entry.Name));
    }

    // 路径没了（退订、移库）时记一条说明，而不是让这个源无声消失
    [Fact]
    public void Mod_ReportsMissingRoot()
    {
        var config = Parse($"""
            game_version = "1.6"

            [[sources]]
            name = "gone"
            mod  = '{Path.Combine(_workspace.Root, "nope")}'
            """);

        var sources = config.ResolveSources();

        Assert.Empty(sources.Xml);
        Assert.Contains(sources.Notes, note => note.Contains("unavailable"));
    }

    // 没写 game_version 时从 Version.txt 探：它就在 vanilla 那条源的 assemblies 目录上面
    [Fact]
    public void GameVersion_DetectedFromVersionFileNextToAssemblies()
    {
        _workspace.WriteFile(Path.Combine("Game", "Version.txt"), "1.6.4871 rev590");
        _workspace.Dir("Game", "RimWorldWin64_Data", "Managed");
        _workspace.WriteFile(Path.Combine("Mod", "1.6", "Defs", "A.xml"), "<Defs />");
        _workspace.WriteFile(Path.Combine("Mod", "1.5", "Defs", "A.xml"), "<Defs />");

        var config = Parse($"""
            [[sources]]
            name       = "vanilla"
            assemblies = '{Path.Combine(_workspace.Root, "Game", "RimWorldWin64_Data", "Managed")}'

            [[sources]]
            name = "mod"
            mod  = '{Path.Combine(_workspace.Root, "Mod")}'
            """);

        var sources = config.ResolveSources();

        Assert.Equal("1.6", sources.GameVersion);
        Assert.Equal(
            Path.Combine(_workspace.Root, "Mod", "1.6", "Defs"),
            Assert.Single(sources.Xml).Path);
    }

    private ResolvedSources Resolve(string relativeModRoot)
        => Parse($"""
            game_version = "1.6"

            [[sources]]
            mod = '{Path.Combine(_workspace.Root, relativeModRoot)}'
            """).ResolveSources();

    private static AppConfig Parse(string toml)
    {
        var config = AppConfig.Parse(toml, out var error);
        Assert.Null(error);
        Assert.NotNull(config);
        return config;
    }
}
