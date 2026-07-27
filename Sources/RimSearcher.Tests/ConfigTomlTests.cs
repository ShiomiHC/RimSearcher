using RimSearcher.Server;

namespace RimSearcher.Tests;

// 三处「宽松地认 key」曾各写各的，规则并不一致：SourcePathEntry 那份只去下划线不去连字符，
// 于是 assembly-paths 在 sources 里认得、在 csharpSourcePaths 里被静默忽略。
public class ConfigTomlTests
{
    [Theory]
    [InlineData("assemblyPaths", "assemblypaths")]
    [InlineData("assembly_paths", "assemblypaths")]
    [InlineData("assembly-paths", "assemblypaths")]
    [InlineData("ASSEMBLY_PATHS", "assemblypaths")]
    [InlineData("Path", "path")]
    [InlineData("", "")]
    public void NormalizeKey_FoldsCaseAndSeparators(string input, string expected)
        => Assert.Equal(expected, ConfigToml.NormalizeKey(input));

    private static AppConfig Parse(string toml) => AppConfig.Parse(toml)!;

    [Theory]
    [InlineData("assemblies")]
    [InlineData("assembly")]
    [InlineData("assemblyPath")]
    [InlineData("assemblyPaths")]
    [InlineData("assembly_paths")]
    [InlineData("assembly-paths")]
    [InlineData("dll")]
    [InlineData("dlls")]
    public void AssemblyPaths_AcceptEverySpelling(string key)
    {
        var config = Parse($$"""
        [[sources]]
        name = "Core"
        csharp = "C:/src"
        "{{key}}" = [ "C:/dlls" ]
        """);

        var definition = Assert.Single(config.Sources);
        Assert.Equal("C:/dlls", Assert.Single(definition.Assemblies));
        Assert.True(definition.CanFollow);
    }

    // 顶层 key 同样宽松：snake_case 是 TOML 的习惯，PascalCase 是从字段说明里照抄的
    [Theory]
    [InlineData("default_scope")]
    [InlineData("defaultScope")]
    [InlineData("DefaultScope")]
    public void TopLevelKeys_AcceptEveryCasing(string key)
        => Assert.Equal("base", Parse($"""{key} = "base" """).DefaultScope);

    // 单值写裸字符串、多值写数组，两种手写形态都要认
    [Fact]
    public void AssemblyPaths_AcceptBothScalarAndArray()
    {
        var scalar = Parse("""sources = [ { csharp = "C:/src", assemblyPath = "C:/a" } ]""");
        var array = Parse("""sources = [ { csharp = "C:/src", assemblyPaths = [ "C:/a", "C:/b" ] } ]""");

        Assert.Single(scalar.Sources[0].Assemblies);
        Assert.Equal(2, array.Sources[0].Assemblies.Count);
    }

    // 类型写错不该带偏同一张表里其余 key 的读取——忽略这一个值，name/csharp 仍要落位
    [Fact]
    public void MalformedValue_DoesNotDerailTheRestOfTheTable()
    {
        var config = Parse("""
        [[sources]]
        assemblies = 42
        name = "Core"
        csharp = "C:/src"
        """);

        var definition = Assert.Single(config.Sources);
        Assert.Equal("Core", definition.Name);
        Assert.Equal("C:/src", Assert.Single(definition.Csharp));
        Assert.Empty(definition.Assemblies);
    }

    // 换成 TOML 的头一个理由：配置能自带说明。注释不该影响任何字段的落位
    [Fact]
    public void Comments_AreIgnored()
    {
        var config = Parse("""
        # 这一行是整份配置的说明
        default_scope = "base"   # 行尾注释

        [[sources]]
        name = "Core"            # 源名
        csharp = "C:/src/Core"
        """);

        Assert.Equal("base", config.DefaultScope);
        Assert.Equal("Core", Assert.Single(config.Sources).Name);
    }

    // 第二个理由：Windows 路径可以整条粘进单引号字面串，不必把每个反斜杠敲两遍
    [Fact]
    public void LiteralStrings_KeepBackslashesVerbatim()
    {
        var config = Parse("""
        [[sources]]
        name = "Core"
        csharp = 'C:\RimWorldSource\1.6\Core'
        """);

        Assert.Equal(@"C:\RimWorldSource\1.6\Core", Assert.Single(Assert.Single(config.Sources).Csharp));
    }

    // [[sources]] 与 sources = [{...}] 都是合法 TOML，也都会被手写出来
    [Fact]
    public void InlineTableArray_IsEquivalentToTableArraySyntax()
    {
        var block = Parse("""
        [[sources]]
        name = "Core"
        csharp = "C:/src/Core"
        """);
        var inline = Parse("""sources = [ { name = "Core", csharp = "C:/src/Core" } ]""");

        Assert.Equal(
            Assert.Single(block.ResolveSources().Csharp).Path,
            Assert.Single(inline.ResolveSources().Csharp).Path);
    }

    // 语法错误必须与「空配置」区分开：前者要让 Load 报「没加载成功」，
    // 静默当成空配置会让用户对着一份写错的 config 找不到任何源
    [Fact]
    public void SyntaxError_ReturnsNull()
    {
        Assert.Null(AppConfig.Parse("""
        [[sources
        name = "Core"
        """));
    }

    // 手写配置漏个括号是常事。诊断里必须带行号，否则用户只能拿肉眼扫整个文件
    [Fact]
    public void SyntaxError_ReportsLineNumber()
    {
        var config = AppConfig.Parse("""
        default_scope = "base"

        [[sources
        name = "Core"
        """, out var error);

        Assert.Null(config);
        Assert.NotNull(error);
        // Tomlyn 的 DiagnosticMessage 形如 "(3,10) : error : ..."：出错的是第 3 行
        Assert.Contains("(3,", error);
    }

    // 一处笔误常连带报出好几条诊断，全塞进日志会淹掉第一现场
    [Fact]
    public void ManyDiagnostics_AreTruncated()
    {
        AppConfig.Parse(string.Join('\n', Enumerable.Repeat("= = =", 12)), out var error);

        Assert.NotNull(error);
        Assert.Contains("more)", error);
    }

    // 解析成功时不该留下错误信息
    [Fact]
    public void ValidDocument_ReportsNoError()
    {
        var config = AppConfig.Parse("""default_scope = "base" """, out var error);

        Assert.NotNull(config);
        Assert.Null(error);
    }

    [Fact]
    public void EmptyDocument_ParsesToDefaults()
    {
        var config = AppConfig.Parse(string.Empty);

        Assert.NotNull(config);
        Assert.False(config.ResolveSources().HasAny);
    }

    // 「还没配」和「刚把配置改坏了」是两回事，启动日志要能分开说：
    // 前者没什么可报的，后者得把行号摆到脸上
    [Fact]
    public void MissingFile_LoadsWithoutAnError()
    {
        using var workspace = new TempWorkspace();

        var loaded = AppConfig.TryLoad(Path.Combine(workspace.Root, "absent.toml"), out _, out var error);

        Assert.False(loaded);
        Assert.Null(error);
    }

    [Fact]
    public void BrokenFile_LoadsWithADiagnostic()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile("config.toml", """
            [[sources]]
            name = "Core
            """);

        var loaded = AppConfig.TryLoad(path, out _, out var error);

        Assert.False(loaded);
        Assert.NotNull(error);
        Assert.Contains("(2,", error);
    }

    [Fact]
    public void ValidFile_LoadsTheSources()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile("config.toml", """
            [[sources]]
            name = "Core"
            csharp = 'C:\src\Core'
            """);

        var loaded = AppConfig.TryLoad(path, out var config, out var error);

        Assert.True(loaded);
        Assert.Null(error);
        Assert.Equal("Core", Assert.Single(config.Sources).Name);
    }
}
