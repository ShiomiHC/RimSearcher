using System.Text.Json;
using RimSearcher.Server;

namespace RimSearcher.Tests;

public class ConfigParsingTests
{
    private static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // 旧格式的裸字符串仍须可用，名字从路径推断
    [Fact]
    public void LegacyStringPaths_InferNameFromPath()
    {
        var config = Parse("""{"CsharpSourcePaths":["C:/src/Core"]}""");
        var sources = config.ResolveSources();

        Assert.Equal("Core", Assert.Single(sources.Csharp).Name);
    }

    // 目录末段常是版本号或内容类型，拿它当源名毫无信息量
    [Theory]
    [InlineData("C:/mods/HAR/1.6/Defs", "HAR")]
    [InlineData("C:/mods/HAR/Defs", "HAR")]
    [InlineData("C:/mods/HAR/Assemblies", "HAR")]
    [InlineData("C:/mods/Milira", "Milira")]
    public void UninformativeSegments_AreSkippedWhenInferringNames(string path, string expected)
    {
        var config = Parse($$"""{"XmlSourcePaths":["{{path}}"]}""");

        Assert.Equal(expected, Assert.Single(config.ResolveSources().Xml).Name);
    }

    [Fact]
    public void ExplicitName_WinsOverInference()
    {
        var config = Parse("""{"CsharpSourcePaths":[{"name":"vanilla","path":"C:/src/Core"}]}""");

        Assert.Equal("vanilla", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    // assemblyPath 单数写字符串、复数写数组都要认
    [Theory]
    [InlineData("""{"name":"Core","path":"C:/src/Core","assemblyPath":"C:/game/Managed"}""")]
    [InlineData("""{"name":"Core","path":"C:/src/Core","assemblyPaths":["C:/game/Managed"]}""")]
    [InlineData("""{"name":"Core","path":"C:/src/Core","assembly_path":"C:/game/Managed"}""")]
    [InlineData("""{"name":"Core","path":"C:/src/Core","assemblies":["C:/game/Managed"]}""")]
    public void AssemblyPaths_AcceptSingularAndPluralForms(string entry)
    {
        var config = Parse($$"""{"CsharpSourcePaths":[{{entry}}]}""");
        var source = Assert.Single(config.ResolveSources().Csharp);

        Assert.Equal(@"C:/game/Managed", Assert.Single(source.AssemblyPaths));
        Assert.True(source.CanFollow);
    }

    [Fact]
    public void WithoutAssemblyPaths_SourceIsNotFollowable()
    {
        var config = Parse("""{"CsharpSourcePaths":["C:/src/Core"]}""");

        Assert.False(Assert.Single(config.ResolveSources().Csharp).CanFollow);
        Assert.Empty(config.ResolveSources().Followable);
    }

    // 新格式：一行声明一个逻辑源的全部路径
    [Fact]
    public void UnifiedSourceDefinition_FansOutToBothLists()
    {
        var config = Parse("""
            {"Sources":[{
                "name":"Core",
                "csharp":"C:/src/Core",
                "xml":["C:/game/Data/Core/Defs","C:/game/Data/Royalty/Defs"],
                "assemblies":"C:/game/Managed"
            }]}
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
            {"Sources":[{
                "name":"Core",
                "csharp":["C:/src/Decompiled","C:/src/OfficialSource"],
                "assemblies":"C:/game/Managed"
            }]}
            """);

        var csharp = config.ResolveSources().Csharp;

        Assert.Equal(2, csharp.Count);
        Assert.True(csharp[0].CanFollow);
        Assert.False(csharp[1].CanFollow);
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("csharp_paths")]
    [InlineData("source")]
    public void SourceDefinitionKeys_AcceptAliases(string key)
    {
        var config = Parse($$"""{"Sources":[{"name":"Core","{{key}}":"C:/src/Core"}]}""");

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("defs")]
    [InlineData("xml_paths")]
    public void XmlKeys_AcceptAliases(string key)
    {
        var config = Parse($$"""{"Sources":[{"name":"Core","{{key}}":"C:/game/Defs"}]}""");

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Xml).Name);
    }

    [Fact]
    public void UnifiedDefinition_InfersNameWhenOmitted()
    {
        var config = Parse("""{"Sources":[{"csharp":"C:/src/Milira"}]}""");

        Assert.Equal("Milira", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    // 回归：转换器对「什么路径都没写」的条目返回 null，ResolveSources 直接解引用会 NRE，
    // 而这一步在 TryLoad 的 catch 之外——config 里多打一个 {} 会让整个进程起不来。
    [Fact]
    public void EmptySourceDefinition_DoesNotCrashResolution()
    {
        var config = Parse("""{"Sources":[{},{"name":"Core","csharp":"C:/src/Core"}]}""");

        var sources = config.ResolveSources();

        Assert.Equal("Core", Assert.Single(sources.Csharp).Name);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var config = Parse("""
            {"Sources":[{"name":"Core","csharp":"C:/src/Core","futureOption":{"nested":true}}],
             "SomethingElse":123}
            """);

        Assert.Equal("Core", Assert.Single(config.ResolveSources().Csharp).Name);
    }

    [Fact]
    public void LegacyAndUnifiedFormats_Coexist()
    {
        var config = Parse("""
            {"CsharpSourcePaths":["C:/src/Core"],
             "Sources":[{"name":"Milira","csharp":"C:/src/Milira"}]}
            """);

        var names = config.ResolveSources().Csharp.Select(entry => entry.Name).ToList();

        Assert.Contains("Core", names);
        Assert.Contains("Milira", names);
    }

    [Fact]
    public void Defaults_MatchDocumentedBehaviour()
    {
        var config = Parse("{}");

        Assert.True(config.CheckUpdates);
        Assert.True(config.CheckSourceUpdates);
        Assert.True(config.ShareIndexHost);
        Assert.True(config.VerifySourceFreshness);
        Assert.False(config.SkipPathSecurity);
        Assert.Equal(0, config.SourceHistoryDepth);
        Assert.Equal(0, config.IdleTimeoutMinutes);
        Assert.False(config.ResolveSources().HasAny);
    }

    [Fact]
    public void ScopeGroupsAndDefaultScope_RoundTrip()
    {
        var config = Parse("""
            {"ScopeGroups":{"addons":["har","milira"]},"DefaultScope":"addons"}
            """);

        Assert.Equal(["har", "milira"], config.ScopeGroups["addons"]);
        Assert.Equal("addons", config.DefaultScope);
    }
}
