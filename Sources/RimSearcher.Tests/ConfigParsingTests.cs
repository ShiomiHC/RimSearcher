using RimSearcher.Server;

namespace RimSearcher.Tests;

public class ConfigParsingTests
{
    private static AppConfig Parse(string toml) => AppConfig.Parse(toml)!;

    // 目录末段常是版本号或内容类型，拿它当源名毫无信息量
    [Theory]
    [InlineData("C:/mods/HAR/1.6/Defs", "HAR")]
    [InlineData("C:/mods/HAR/Defs", "HAR")]
    [InlineData("C:/mods/HAR/Assemblies", "HAR")]
    [InlineData("C:/mods/Milira", "Milira")]
    public void UninformativeSegments_AreSkippedWhenInferringNames(string path, string expected)
    {
        var config = Parse($"""
            [[sources]]
            xml = ["{path}"]
            """);

        Assert.Equal(expected, Assert.Single(config.ResolveSources().Xml).Name);
    }

    [Fact]
    public void ExplicitName_WinsOverInference()
    {
        var config = Parse("""
            [[sources]]
            name = "vanilla"
            csharp = "C:/src/Core"
            """);

        Assert.Equal("vanilla", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    // assemblyPath 单数写字符串、复数写数组都要认
    [Theory]
    [InlineData("""assemblyPath = "C:/game/Managed" """)]
    [InlineData("""assemblyPaths = ["C:/game/Managed"] """)]
    [InlineData("""assembly_path = "C:/game/Managed" """)]
    [InlineData("""assemblies = ["C:/game/Managed"] """)]
    public void AssemblyPaths_AcceptSingularAndPluralForms(string assemblyLine)
    {
        var config = Parse($"""
            [[sources]]
            name = "Core"
            csharp = "C:/src/Core"
            {assemblyLine}
            """);
        var source = Assert.Single(config.ResolveSources().Csharp);

        Assert.Equal(@"C:/game/Managed", Assert.Single(source.AssemblyPaths));
        Assert.True(source.CanFollow);
    }

    [Fact]
    public void WithoutAssemblies_SourceIsNotFollowable()
    {
        var config = Parse("""
            [[sources]]
            csharp = "C:/src/Core"
            """);

        Assert.False(Assert.Single(config.ResolveSources().Csharp).CanFollow);
        Assert.Empty(config.ResolveSources().Followable);
    }

    // 新格式：一个 [[sources]] 块声明一个逻辑源的全部路径
    [Fact]
    public void UnifiedSourceDefinition_FansOutToBothLists()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            csharp = "C:/src/Core"
            xml = ["C:/game/Data/Core/Defs", "C:/game/Data/Royalty/Defs"]
            assemblies = "C:/game/Managed"
            """);

        var sources = config.ResolveSources();

        Assert.Equal("Core", Assert.Single(sources.Csharp).Name);
        Assert.Equal(2, sources.Xml.Count);
        Assert.All(sources.Xml, entry => Assert.Equal("Core", entry.Name));
        Assert.Single(sources.Followable);
    }

    // 反编译产物只写第一个 csharp 路径，其余是附加只读源码目录；
    // 否则同一批程序集会被多条源码路径重复扫描
    [Fact]
    public void OnlyTheFirstCsharpPath_CarriesTheAssemblies()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            csharp = ["C:/src/Decompiled", "C:/src/OfficialSource"]
            assemblies = "C:/game/Managed"
            """);

        var csharp = config.ResolveSources().Csharp;

        Assert.Equal(2, csharp.Count);
        Assert.True(csharp[0].CanFollow);
        Assert.False(csharp[1].CanFollow);
    }

    // 回归：只写 assemblies 不写 csharp 的源，以前在 ResolveSources 里一条路径都不产出，
    // 既不索引也不反编译——静默消失。现在补默认输出目录 <base>/Decompiled/<源名>。
    [Fact]
    public void AssembliesWithoutCsharp_FallsBackToDefaultOutputDirectory()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            assemblies = "C:/game/Managed"
            """);

        var source = Assert.Single(config.ResolveSources(@"C:/app").Csharp);

        Assert.Equal(Path.Combine(@"C:/app", "Decompiled", "Core"), source.Path);
        Assert.True(source.CanFollow);
    }

    [Fact]
    public void ExplicitCsharpPath_WinsOverDefaultOutputDirectory()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            csharp = "D:/src/Core"
            assemblies = "C:/game/Managed"
            """);

        Assert.Equal("D:/src/Core", Assert.Single(config.ResolveSources(@"C:/app").Csharp).Path);
    }

    [Fact]
    public void DecompileOutputRoot_OverridesTheDefaultFolder()
    {
        var config = Parse("""
            decompile_output_root = "D:/decompiled"

            [[sources]]
            name = "Core"
            assemblies = "C:/game/Managed"
            """);

        // DecompileOutputRoot 走 ResolvePath，故期望值也要按同一套规则规范化（D:/ → D:\）
        Assert.Equal(Path.Combine(Path.GetFullPath(@"D:/decompiled"), "Core"),
            Assert.Single(config.ResolveSources(@"C:/app").Csharp).Path);
    }

    // 相对路径按 exe 目录解析，与 config.toml / RIMSEARCHER_CONFIG 的既有规则一致
    [Fact]
    public void RelativeDecompileOutputRoot_ResolvesAgainstBaseDirectory()
    {
        var config = Parse("""
            decompile_output_root = "../shared"

            [[sources]]
            name = "Core"
            assemblies = "C:/game/Managed"
            """);

        var path = Assert.Single(config.ResolveSources(@"C:/app/bin").Csharp).Path;

        Assert.Equal(Path.GetFullPath(Path.Combine(@"C:/app", "shared", "Core")), path);
    }

    // 源名会直接进路径。它可能是用户显式给的，也可能是从路径末段推断的，都不保证是合法目录名
    [Theory]
    [InlineData("Vanilla Expanded", "Vanilla Expanded")]
    [InlineData("Core/Sub", "Core_Sub")]
    [InlineData(@"a:b*c", "a_b_c")]
    public void DefaultOutputDirectory_SanitizesSourceName(string name, string expected)
    {
        var config = Parse($"""
            [[sources]]
            name = "{name}"
            assemblies = "C:/game/Managed"
            """);

        Assert.Equal(Path.Combine(@"C:/app", "Decompiled", expected),
            Assert.Single(config.ResolveSources(@"C:/app").Csharp).Path);
    }

    // 没有 assemblies 就没有反编译，也就不该凭空造出一个输出目录
    [Fact]
    public void SourceWithNeitherCsharpNorAssemblies_ProducesNoCsharpPath()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            xml = "C:/game/Defs"
            """);

        var sources = config.ResolveSources(@"C:/app");

        Assert.Empty(sources.Csharp);
        Assert.Single(sources.Xml);
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("csharp_paths")]
    [InlineData("source")]
    public void SourceDefinitionKeys_AcceptAliases(string key)
    {
        var config = Parse($"""
            [[sources]]
            name = "Core"
            {key} = "C:/src/Core"
            """);

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("defs")]
    [InlineData("xml_paths")]
    public void XmlKeys_AcceptAliases(string key)
    {
        var config = Parse($"""
            [[sources]]
            name = "Core"
            {key} = "C:/game/Defs"
            """);

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Xml).Name);
    }

    [Fact]
    public void UnifiedDefinition_InfersNameWhenOmitted()
    {
        var config = Parse("""
            [[sources]]
            csharp = "C:/src/Milira"
            """);

        Assert.Equal("Milira", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    // 回归：解析对「什么路径都没写」的条目返回 null，ResolveSources 直接解引用会 NRE，
    // 而这一步在 TryLoad 的 catch 之外——config 里多打一个空的 [[sources]] 会让整个进程起不来。
    [Fact]
    public void EmptySourceDefinition_DoesNotCrashResolution()
    {
        var config = Parse("""
            [[sources]]

            [[sources]]
            name = "Core"
            csharp = "C:/src/Core"
            """);

        var sources = config.ResolveSources();

        Assert.Equal("Core", Assert.Single(sources.Csharp).Name);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var config = Parse("""
            something_else = 123

            [[sources]]
            name = "Core"
            csharp = "C:/src/Core"
            future_option = { nested = true }
            """);

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    // 同名的多个块归为同一个源：ScopeCatalog 按 name 归组，所以一个逻辑源可以拆成
    // 几个块写（例如源码目录与 Def 目录来自完全不同的盘）
    [Fact]
    public void BlocksSharingAName_StayOneSource()
    {
        var config = Parse("""
            [[sources]]
            name = "Core"
            csharp = "C:/src/Core"

            [[sources]]
            name = "Core"
            xml = "D:/game/Data/Core/Defs"
            """);

        var sources = config.ResolveSources();

        Assert.Equal("Core", Assert.Single(sources.Csharp).Name);
        Assert.Equal("Core", Assert.Single(sources.Xml).Name);
    }

    [Fact]
    public void Defaults_MatchDocumentedBehaviour()
    {
        var config = Parse(string.Empty);

        Assert.True(config.CheckUpdates);
        Assert.True(config.CheckSourceUpdates);
        Assert.True(config.ShareIndexHost);
        Assert.True(config.VerifySourceFreshness);
        Assert.False(config.SkipPathSecurity);
        Assert.Equal(0, config.SourceHistoryDepth);
        Assert.Equal(0, config.IdleTimeoutMinutes);
        Assert.Null(config.DecompileOutputRoot);
        Assert.False(config.ResolveSources().HasAny);
    }

    [Fact]
    public void ScopeGroupsAndDefaultScope_RoundTrip()
    {
        var config = Parse("""
            default_scope = "addons"

            [scope_groups]
            addons = ["har", "milira"]
            """);

        Assert.Equal(["har", "milira"], config.ScopeGroups["addons"]);
        Assert.Equal("addons", config.DefaultScope);
    }

    // 布尔与整数从 TOML 的原生标量读出，不再经过字符串
    [Fact]
    public void ScalarOptions_ReadFromNativeTomlTypes()
    {
        var config = Parse("""
            skip_path_security = true
            check_updates = false
            source_history_depth = 3
            idle_timeout_minutes = 30
            game_version = "1.6"
            """);

        Assert.True(config.SkipPathSecurity);
        Assert.False(config.CheckUpdates);
        Assert.Equal(3, config.SourceHistoryDepth);
        Assert.Equal(30, config.IdleTimeoutMinutes);
        Assert.Equal("1.6", config.GameVersion);
    }
}
