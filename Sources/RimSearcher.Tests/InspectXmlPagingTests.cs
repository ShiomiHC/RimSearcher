using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：合并 XML 超长被截断后，提示写的是「use read_code on file path above」。
// 但被截断的是**沿 ParentName 链合并后**的 XML，它不对应磁盘上任何一个文件——上面那行
// `File:` 指的是子 def 自己那份未合并的源文件，里面恰恰没有继承来的字段。照着提示走，
// 拿回来的是另一份文档，且缺的正是 inspect def 模式唯一的存在理由。
// 续读只能由 inspect 自己提供，于是有了 xmlStartLine。
public class InspectXmlPagingTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 父 def 摆一大堆字段，子 def 只有 defName + ParentName：
    // 合并结果远长于源文件，两者不可互相替代这一点因此可断言。
    private InspectTool BuildTool(int parentFieldCount)
    {
        var root = _workspace.Dir("Defs");

        var fields = string.Join("\n", Enumerable.Range(0, parentFieldCount)
            .Select(i => $"    <zzField{i}>{i}</zzField{i}>"));

        _workspace.WriteFile(Path.Combine("Defs", "Base.xml"),
            $"<Defs>\n  <ThingDef Name=\"ZzBase\" Abstract=\"True\">\n{fields}\n  </ThingDef>\n</Defs>\n");

        _workspace.WriteFile(Path.Combine("Defs", "Child.xml"),
            "<Defs>\n  <ThingDef ParentName=\"ZzBase\">\n    <defName>ZzChild</defName>\n  </ThingDef>\n</Defs>\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        defIndexer.FreezeIndex();

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.FreezeIndex();

        return new InspectTool(sourceIndexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    [Fact]
    public async Task ShortMergedXml_IsShownWholeWithNoContinuationHint()
    {
        var content = await Run(BuildTool(5), """{"name":"ZzChild"}""");

        Assert.Contains("zzField0", content);
        Assert.Contains("zzField4", content);
        Assert.DoesNotContain("xmlStartLine", content);
    }

    // 截断提示必须指回 inspect 自己，且说清 File: 那一行不是这份 XML 的来源
    [Fact]
    public async Task TruncatedMergedXml_PointsBackAtInspect_NotAtTheFile()
    {
        var content = await Run(BuildTool(400), """{"name":"ZzChild"}""");

        Assert.Contains("Truncated", content);
        Assert.Contains("call inspect again with xmlStartLine:", content);
        Assert.Contains("un-merged", content);
        Assert.DoesNotContain("use read_code on file path above", content);
    }

    // 续读拿到的必须是被截掉的那一段，而不是又一次头尾
    [Fact]
    public async Task ContinuingWithXmlStartLine_ReturnsTheSkippedMiddle()
    {
        var tool = BuildTool(400);

        var first = await Run(tool, """{"name":"ZzChild"}""");
        Assert.DoesNotContain("<zzField300>", first);

        var second = await Run(tool, """{"name":"ZzChild","xmlStartLine":201}""");

        Assert.Contains("<zzField300>", second);
        Assert.Contains("lines 201-", second);
    }

    // 走到尾就说走到尾，不要再给一个指向空白的续读值
    [Fact]
    public async Task ReachingTheEnd_SaysSoInsteadOfOfferingAnotherPage()
    {
        var content = await Run(BuildTool(400), """{"name":"ZzChild","xmlStartLine":300}""");

        Assert.Contains("End of the merged XML", content);
        Assert.DoesNotContain("call inspect again with xmlStartLine:", content);
    }

    // 越界起点不该炸，也不该回一段空白
    [Fact]
    public async Task StartLineBeyondTheEnd_ClampsToTheLastLine()
    {
        var content = await Run(BuildTool(20), """{"name":"ZzChild","xmlStartLine":99999}""");

        Assert.Contains("End of the merged XML", content);
    }
}
