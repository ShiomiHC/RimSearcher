using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：inheritors 只返回**直接**子类，而工具描述和输出表头都称其为「子类树」。
// RimWorld 的层级普遍三四层深（ThingComp → CompApparelVerbOwner →
// CompApparelVerbOwner_Charged → CompApparelReloadable），于是
// 「X 是不是 Y 的子类」——本 mode 最主要的用途——对任何间接后代都被答成「不是」。
public class TraceInheritorsDepthTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 一条四层链 + 一条从中分叉的旁支，用来同时验证「走得够深」与「深度标得对」
    private TraceTool BuildTool()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Chain.cs"), """
            namespace Zz
            {
                public class ZzBase { }
                public class ZzChild : ZzBase { }
                public class ZzGrandchild : ZzChild { }
                public class ZzGreatGrandchild : ZzGrandchild { }
                public class ZzSecondChild : ZzBase { }
                public class ZzUnrelated { }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(TraceTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    [Fact]
    public async Task Inheritors_ReachesIndirectDescendants_NotJustDirectOnes()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors"}""");

        Assert.Contains("ZzChild", content);
        Assert.Contains("ZzSecondChild", content);
        // 这两条是修复前完全拿不到的
        Assert.Contains("ZzGrandchild", content);
        Assert.Contains("ZzGreatGrandchild", content);
        Assert.DoesNotContain("ZzUnrelated", content);
    }

    // 拍平成一列返回时，「直接子类」与「曾孙」在决定覆写哪一层方法时含义完全不同，
    // 不逐条标深度就分不出来
    [Fact]
    public async Task EachInheritor_IsTaggedWithItsDepth()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors"}""");

        Assert.Matches(@"ZzChild`\s*\[direct\]", content);
        Assert.Matches(@"ZzGrandchild`\s*\[depth 2\]", content);
        Assert.Matches(@"ZzGreatGrandchild`\s*\[depth 3\]", content);
    }

    // 截断时留下的必须是直接子类：调用方要的先是「谁直接继承了它」
    [Fact]
    public async Task ShallowestInheritors_SurviveTruncation()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors","limit":2}""");

        Assert.Contains("ZzChild", content);
        Assert.Contains("ZzSecondChild", content);
        Assert.DoesNotContain("ZzGreatGrandchild", content);
    }

    // 表头的两个数只描述「列出来的这些」。拿展示切片当整棵树的统计量，
    // 或反过来，都会造出一个看起来像结论的假数字。
    [Fact]
    public async Task HeaderCounts_DescribeTheListedSlice_NotTheWholeTree()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors","limit":2}""");

        Assert.Contains("4 in scope", content);          // 树的总量
        Assert.Contains("Listed below: 2", content);     // 切片的量
        Assert.Contains("2 direct", content);
        Assert.Contains("deepest 1 level(s) down", content);
    }

    // 「索引里没有这个名字」与「有，但没人继承它」下一步完全不同：前者要去核对拼写，
    // 后者已经是答案。两者说成同一句话时，调用方会拿着一个不存在的名字继续往下查。
    [Fact]
    public async Task UnknownSymbol_IsNotReportedAsHavingNoSubclasses()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzNoSuchType","mode":"inheritors"}""");

        Assert.Contains("No type named 'ZzNoSuchType' is in the index", content);
        Assert.Contains("not evidence", content);
        Assert.Contains("locate", content);
    }

    [Fact]
    public async Task KnownLeafType_IsReportedAsAnAnswer_NotALookupFailure()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzGreatGrandchild","mode":"inheritors"}""");

        Assert.Contains("is indexed", content);
        Assert.Contains("this is an answer", content);
        Assert.DoesNotContain("is in the index", content);
    }

    // 短名归并后同名类型跨命名空间互指会成环，BFS 必须自带环保护而不是空转
    [Fact]
    public async Task CyclicInheritance_DoesNotHang()
    {
        var root = _workspace.Dir("Cyclic");
        _workspace.WriteFile(Path.Combine("Cyclic", "Cycle.cs"), """
            namespace Zc
            {
                public class ZcA : ZcB { }
                public class ZcB : ZcA { }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, """{"symbol":"ZcA","mode":"inheritors"}""");

        Assert.Contains("ZcB", content);
        // 环里的每个类型只应出现一次，不该被反复展开
        Assert.Equal(1, content.Split("`Zc.ZcB`").Length - 1);
    }
}
