using System.Text.Json;
using RimSearcher.Server;

namespace RimSearcher.Tests;

// 三处「宽松地认 key」曾各写各的，规则并不一致：SourcePathEntry 那份只去下划线不去连字符，
// 于是 assembly-paths 在 sources 里认得、在 csharpSourcePaths 里被静默忽略。
public class ConfigJsonTests
{
    [Theory]
    [InlineData("assemblyPaths", "assemblypaths")]
    [InlineData("assembly_paths", "assemblypaths")]
    [InlineData("assembly-paths", "assemblypaths")]
    [InlineData("ASSEMBLY_PATHS", "assemblypaths")]
    [InlineData("Path", "path")]
    [InlineData("", "")]
    public void NormalizeKey_FoldsCaseAndSeparators(string input, string expected)
        => Assert.Equal(expected, ConfigJson.NormalizeKey(input));

    private static AppConfig Parse(string json)
        => JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Theory]
    [InlineData("assemblyPaths")]
    [InlineData("assembly_paths")]
    [InlineData("assembly-paths")]
    [InlineData("assemblies")]
    public void CsharpSourcePathEntry_AcceptsEverySpellingOfAssemblyPaths(string key)
    {
        var config = Parse($$"""
        { "csharpSourcePaths": [ { "name": "Core", "path": "C:/src", "{{key}}": "C:/dlls" } ] }
        """);

        var entry = Assert.Single(config.CsharpSourcePaths);
        Assert.Equal("C:/dlls", Assert.Single(entry.AssemblyPaths));
        Assert.True(entry.CanFollow);
    }

    [Theory]
    [InlineData("assembly-paths")]
    [InlineData("assembly_paths")]
    public void SourceDefinition_AcceptsTheSameSpellings(string key)
    {
        var config = Parse($$"""
        { "sources": [ { "name": "Core", "csharp": "C:/src", "{{key}}": [ "C:/dlls" ] } ] }
        """);

        var definition = Assert.Single(config.Sources);
        Assert.Equal("C:/dlls", Assert.Single(definition.Assemblies));
    }

    // 单值写裸字符串、多值写数组，两种手写形态都要认
    [Fact]
    public void AssemblyPaths_AcceptBothScalarAndArray()
    {
        var scalar = Parse("""{ "csharpSourcePaths": [ { "path": "C:/src", "assemblyPath": "C:/a" } ] }""");
        var array = Parse("""{ "csharpSourcePaths": [ { "path": "C:/src", "assemblyPaths": [ "C:/a", "C:/b" ] } ] }""");

        Assert.Single(scalar.CsharpSourcePaths[0].AssemblyPaths);
        Assert.Equal(2, array.CsharpSourcePaths[0].AssemblyPaths.Count);
    }

    // 类型写错不该带偏后续 key 的读取——跳过整棵子树，name/path 仍要落位
    [Fact]
    public void MalformedValue_DoesNotDerailTheRestOfTheObject()
    {
        var config = Parse("""
        { "csharpSourcePaths": [ { "assemblyPaths": { "oops": 1 }, "name": "Core", "path": "C:/src" } ] }
        """);

        var entry = Assert.Single(config.CsharpSourcePaths);
        Assert.Equal("Core", entry.Name);
        Assert.Equal("C:/src", entry.Path);
        Assert.Empty(entry.AssemblyPaths);
    }
}
