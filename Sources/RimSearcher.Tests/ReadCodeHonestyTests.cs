using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：read_code 的四条「说不清自己做了什么」的路径。
// 共同点是返回读起来完全成立，只是说的不是实际发生的事——调用方据此下的结论会是反的。
public class ReadCodeHonestyTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string TwoClasses = """
        namespace Zz
        {
            public class ZzAlpha
            {
                public void ZzShared() { }
                public int ZzOnlyAlpha;
            }
            public class ZzBeta
            {
                public void ZzShared() { }
            }
        }
        """;

    private (ReadCodeTool Tool, string Root) Build(string subdir = "Core")
    {
        var root = _workspace.Dir(subdir);
        _workspace.WriteFile(Path.Combine(subdir, "ZzTwo.cs"), TwoClasses);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null)), root);
    }

    private static async Task<ToolResult> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    // className 只是过滤器：过滤后归零不等于「这个文件里没有这个成员」
    [Fact]
    public async Task WrongClassName_SaysWhereTheMemberActuallyIs()
    {
        var (tool, _) = Build();

        var result = await Run(tool, """{"path":"ZzTwo.cs","methodName":"ZzShared","className":"ZzNoSuchClass"}""");

        Assert.True(result.IsError);
        Assert.Contains("does exist", result.Content);
        Assert.Contains("ZzNoSuchClass", result.Content);
        Assert.Contains("ZzAlpha", result.Content);
        Assert.Contains("ZzBeta", result.Content);
    }

    // 真的没有这个成员时仍要说 not found，不能被上一条的回退路径吞掉
    [Fact]
    public async Task GenuinelyAbsentMember_StillReportsNotFound()
    {
        var (tool, _) = Build();

        var result = await Run(tool, """{"path":"ZzTwo.cs","methodName":"ZzNeverDeclared","className":"ZzAlpha"}""");

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
        Assert.DoesNotContain("does exist", result.Content);
    }

    // 传目录时唯一正确的下一步是 list_directory，而原先回的是「文件不存在，去 locate」
    [Fact]
    public async Task DirectoryPath_IsNamedAsADirectory()
    {
        var (tool, root) = Build();

        var result = await Run(tool, $$"""{"path":{{JsonSerializer.Serialize(root)}},"startLine":0,"lineCount":5}""");

        Assert.True(result.IsError);
        Assert.Contains("is a directory", result.Content);
        Assert.Contains("list_directory", result.Content);
        Assert.DoesNotContain("File not found", result.Content);
    }

    // 报错回显整条路径：只印基名时「路径写错」和「没进索引」长得一模一样
    [Fact]
    public async Task NotFound_EchoesTheWholePathTheCallerGave()
    {
        var (tool, _) = Build();

        var result = await Run(tool, """{"path":"/zz/nowhere/ZzGhost.cs","startLine":0,"lineCount":5}""");

        Assert.True(result.IsError);
        Assert.Contains("/zz/nowhere/ZzGhost.cs", result.Content);
        Assert.Contains("does not exist on disk", result.Content);
    }

    // 裸行模式的头部要给绝对路径，与 methodName / extractClass 两个模式对齐
    [Fact]
    public async Task RawLineMode_PrintsTheResolvedAbsolutePath()
    {
        var (tool, root) = Build();

        var result = await Run(tool, """{"path":"ZzTwo","startLine":0,"lineCount":3}""");

        Assert.False(result.IsError);
        Assert.Contains(Path.Combine(root, "ZzTwo.cs"), result.Content);
    }

    // scope 内有多份同名文件时，返回必须说清读的是几选一
    [Fact]
    public async Task SameNameInTwoSources_SaysItPickedOne()
    {
        var vanilla = _workspace.Dir("V");
        var modded = _workspace.Dir("M");
        _workspace.WriteFile(Path.Combine("V", "ZzDup.cs"), "namespace Zz { public class ZzDup { } }");
        _workspace.WriteFile(Path.Combine("M", "ZzDup.cs"), "namespace Zz { public class ZzDup { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(vanilla);
        indexer.Scan(modded);
        indexer.FreezeIndex();

        var tool = new ReadCodeTool(
            indexer, ScopeCatalog.Build([("vanilla", vanilla), ("modded", modded)], null, null));

        var result = await Run(tool, """{"path":"ZzDup","startLine":0,"lineCount":2}""");

        Assert.False(result.IsError);
        Assert.Contains("files share this name", result.Content);
    }

    // 只有一份时不能凭空冒出「几选一」的提示
    [Fact]
    public async Task SingleMatch_HasNoAmbiguityNote()
    {
        var (tool, _) = Build();

        var result = await Run(tool, """{"path":"ZzTwo","startLine":0,"lineCount":2}""");

        Assert.False(result.IsError);
        Assert.DoesNotContain("files share this name", result.Content);
    }

    // extractClass 这个名字本身就像个开关，而它要的是类名。传 true 时原先走 CoerceToString
    // 变成一次「找不到名叫 true 的类」的查找失败——返回读起来是「这个文件里没有这个类，
    // 去 inspect 核对名字」，方向完全相反，照做只会再确认一遍那个类确实存在。
    [Theory]
    [InlineData("extractClass")]
    [InlineData("methodName")]
    [InlineData("className")]
    public void BooleanInANameSlot_IsRejectedAsAWrongType_NotReportedAsANotFoundName(string parameter)
    {
        var (tool, _) = Build();

        // className 要配 methodName 才走得到，一并传上不影响本条断言
        using var args = JsonDocument.Parse($$"""{"path":"ZzTwo.cs","methodName":"ZzShared","{{parameter}}":true}""");
        var ex = Assert.Throws<ToolArgumentException>(
            () => tool.ExecuteAsync(args.RootElement, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains(parameter, ex.Message);
        Assert.Contains("not a boolean", ex.Message);
        Assert.Contains("not a switch", ex.Message);
        // 「没找到」是下一步完全不同的一类结论，这里一个字都不该出现
        Assert.DoesNotContain("not found", ex.Message);
    }

    // 名字位收严之后，"true" 作为**字符串**仍是一个合法（虽然找不到）的类名——
    // 拒绝的是类型不对，不是这个字面量
    [Fact]
    public async Task StringNamedTrue_IsStillTreatedAsAName()
    {
        var (tool, _) = Build();

        var result = await Run(tool, """{"path":"ZzTwo.cs","extractClass":"true"}""");

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
    }

    // 出错当场给出的参数清单是调用方唯一看得到的一份，漏 scope 会让它以为不支持
    [Fact]
    public void UsageLine_ListsScope()
    {
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var tool = new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", _workspace.Dir("Empty"))], null, null));

        using var args = JsonDocument.Parse("""{"path":""}""");
        var ex = Assert.Throws<ToolArgumentException>(
            () => tool.ExecuteAsync(args.RootElement, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Contains("scope", ex.Message);
    }
}
