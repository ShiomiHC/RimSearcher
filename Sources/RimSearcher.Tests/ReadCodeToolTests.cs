using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 「取到了没有」原先靠 body.Contains("not found") 判断，而反编译产物里
// Log.Error("... not found") / throw new Exception("def not found") 这类字面量遍地都是：
// 正文一含这段文本就被误报成「类不存在」，用户看到的结论与事实相反。
public class ReadCodeToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string Source = """
        namespace RimWorld
        {
            public class CompVoidNode
            {
                public void Resolve()
                {
                    Log.Error("linked def not found, skipping");
                }

                public string Label
                {
                    get { return "not found"; }
                }
            }

            public class Sibling { }
        }
        """;

    private ReadCodeTool BuildTool()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "CompVoidNode.cs"), Source);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<ToolResult> Run(ReadCodeTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task ExtractClass_ReturnsBodyEvenWhenItContainsNotFoundText()
    {
        var result = await Run(BuildTool(), """{"path":"CompVoidNode","extractClass":"CompVoidNode"}""");

        Assert.False(result.IsError);
        Assert.Contains("class CompVoidNode", result.Content);
        Assert.Contains("linked def not found", result.Content);
    }

    [Fact]
    public async Task Member_ReturnsBodyEvenWhenItContainsNotFoundText()
    {
        var result = await Run(BuildTool(), """{"path":"CompVoidNode","methodName":"Resolve"}""");

        Assert.False(result.IsError);
        Assert.Contains("linked def not found", result.Content);
    }

    // 属性正文整个就是 "not found" 这个字符串字面量，最刁钻的一种
    [Fact]
    public async Task Property_WhoseBodyIsLiterallyNotFound_IsStillReturned()
    {
        var result = await Run(BuildTool(), """{"path":"CompVoidNode","methodName":"Label"}""");

        Assert.False(result.IsError);
        Assert.Contains("Label", result.Content);
    }

    // 反向保险：真正不存在的目标仍要报错，且提示可自纠
    [Fact]
    public async Task MissingClass_StillReportsAnError()
    {
        var result = await Run(BuildTool(), """{"path":"CompVoidNode","extractClass":"ZzNoSuchClass"}""");

        Assert.True(result.IsError);
        Assert.Contains("ZzNoSuchClass", result.Content);
        Assert.Contains("inspect", result.Content);
    }

    [Fact]
    public async Task MissingMember_StillReportsAnError()
    {
        var result = await Run(BuildTool(), """{"path":"CompVoidNode","methodName":"ZzNoSuchMember"}""");

        Assert.True(result.IsError);
        Assert.Contains("ZzNoSuchMember", result.Content);
        Assert.Contains("inspect", result.Content);
    }

    // schema 的 maximum 只是提示：越界的 lineCount 必须由服务端夹住
    [Fact]
    public async Task RawRead_ClampsLineCountToTheServerMaximum()
    {
        var root = _workspace.Dir("Big");
        _workspace.WriteFile(
            Path.Combine("Big", "Big.cs"),
            string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"// line {i}")));

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
        var result = await Run(tool, """{"path":"Big","lineCount":100000}""");

        Assert.False(result.IsError);
        Assert.Equal(2000, result.Content.Split('\n').Count(line => line.StartsWith("L")));
        // 夹住之后还剩内容，必须照常给出续读提示
        Assert.Contains("more lines available", result.Content);
    }
}
