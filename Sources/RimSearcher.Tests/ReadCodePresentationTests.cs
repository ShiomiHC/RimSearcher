using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// read_code 的两处「说的和实际不符」：白名单外的绝对路径被报成「文件不存在」，
// 以及 XML 一律被套进 ```csharp 围栏。两者都让调用方照着错误的前提继续动作。
[Collection("PathSecurity")]
public class ReadCodePresentationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ReadCodePresentationTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private (ReadCodeTool Tool, string Root, string Outside) Build()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "CompShield.cs"), "namespace RimWorld { public class CompShield { } }\n");
        _workspace.WriteFile(
            Path.Combine("Core", "Apparel_Belts.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>Apparel_ShieldBelt</defName>\n  </ThingDef>\n</Defs>\n");

        var outside = _workspace.WriteFile(Path.Combine("elsewhere", "config.toml"), "secret = 1\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        PathSecurity.Initialize([root]);

        return (new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null)), root, outside);
    }

    private static async Task<ToolResult> Run(ReadCodeTool tool, object arguments)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    // 文件确实存在、只是不在白名单内。回「File not found，去 locate 找找」会让调用方
    // 反复去 locate 试——而 locate 永远不会返回一个不在索引根下的文件。
    [Fact]
    public async Task AbsolutePathOutsideTheWhitelist_SaysItIsOutsideNotMissing()
    {
        var (tool, _, outside) = Build();

        var result = await Run(tool, new { path = outside, startLine = 0, lineCount = 5 });

        Assert.True(result.IsError);
        Assert.Contains("outside allowed directories", result.Content);
        Assert.DoesNotContain("File not found", result.Content);
        // 正文一个字都不能漏出去
        Assert.DoesNotContain("secret", result.Content);
    }

    // 反向保险：真的不存在的路径仍是 not found，而不是被新分支吞成越权
    [Fact]
    public async Task GenuinelyMissingFile_StillReportsNotFound()
    {
        var (tool, _, _) = Build();

        var result = await Run(tool, new { path = "ZzNoSuchFile.cs" });

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content);
    }

    // 绝对路径打错（目录不对、文件不存在）时仍会按文件名在索引里另找一份同名文件返回，
    // 而头部注释打印的是解析后的名字——不说一句的话，返回里没有任何线索表明读的不是
    // 调用方点名的那条路径。
    [Fact]
    public async Task MistypedAbsolutePath_SaysItReadTheIndexedFileInstead()
    {
        var (tool, root, _) = Build();
        var wrong = Path.Combine(root, "NoSuchDir", "CompShield.cs");

        var result = await Run(tool, new { path = wrong, startLine = 0, lineCount = 5 });

        Assert.False(result.IsError);
        Assert.Contains("does not exist", result.Content);
        Assert.Contains("CompShield.cs", result.Content);
    }

    // 反向保险：路径没打错时不该凭空多出这句
    [Fact]
    public async Task CorrectAbsolutePath_HasNoRedirectNotice()
    {
        var (tool, root, _) = Build();

        var result = await Run(tool, new { path = Path.Combine(root, "CompShield.cs"), startLine = 0, lineCount = 5 });

        Assert.False(result.IsError);
        Assert.DoesNotContain("does not exist", result.Content);
    }

    [Fact]
    public async Task RawRead_OfXml_UsesAnXmlFenceAndXmlComments()
    {
        var (tool, _, _) = Build();

        var result = await Run(tool, new { path = "Apparel_Belts.xml", startLine = 0, lineCount = 5 });

        Assert.False(result.IsError);
        Assert.Contains("```xml", result.Content);
        Assert.DoesNotContain("```csharp", result.Content);
        // `// ...` 留在 xml 块里就是一行非法内容，整块复制出去直接解析失败
        Assert.Contains("<!-- Apparel_Belts.xml", result.Content);
        Assert.DoesNotContain("// Apparel_Belts.xml", result.Content);
    }

    [Fact]
    public async Task RawRead_OfCsharp_KeepsTheCsharpFence()
    {
        var (tool, _, _) = Build();

        var result = await Run(tool, new { path = "CompShield", startLine = 0, lineCount = 5 });

        Assert.False(result.IsError);
        Assert.Contains("```csharp", result.Content);
        Assert.Contains("// CompShield.cs", result.Content);
    }
}
