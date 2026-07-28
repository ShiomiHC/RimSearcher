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
    // 不标深度就分不出来。只标非直接的：直接子类占绝大多数（真实语料 601 行全是），
    // 每行挂一个 `[direct]` 是把表头已经说过的话再说 601 遍。
    [Fact]
    public async Task NonDirectInheritors_AreTaggedWithTheirDepth()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors"}""");

        Assert.Matches(@"ZzGrandchild`\s*\[depth 2\]", content);
        Assert.Matches(@"ZzGreatGrandchild`\s*\[depth 3\]", content);
    }

    // 直接子类不挂标记，而「无标记 = 直接」这条约定必须在有深层项时由表头说出来
    [Fact]
    public async Task DirectInheritors_CarryNoTag_AndTheHeaderSaysSo()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors"}""");

        Assert.DoesNotContain("[direct]", content);
        Assert.Contains("untagged = direct", content);
    }

    // 一个 `[depth N]` 都没印出来时，不讲解那套记法——讲了反而会让读者去找它。
    // 判据是「这次真的印了标记吗」，与整棵树有多深无关（表头另说那件事）。
    [Fact]
    public async Task AllDirect_NeedsNoLegend()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors","limit":2}""");

        Assert.DoesNotContain("[direct]", content);
        Assert.DoesNotContain("untagged = direct", content);
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

    // 表头括号里的 direct / deepest 描述的是 **scope 内的整棵树**，不是截断后展示的那一段。
    //
    // 这条推翻了上一轮的写法（当时断言的是「两个数只描述列出来的这些」）。理由：那两个数
    // 紧跟在描述全树的总数后面、句法完全对称，读者不会把它们分开当两批。实测 ThingComp 的
    // 前 200 条恰好全是直接子类，于是表头写出「381 … Listed below: 200 (200 direct,
    // deepest 1 level down)」——调用方据此断定这棵树只有一层，而它真有四层。两个数各自
    // 都没算错，错在它们描述的是切片。现在只剩「Listed below」一格描述切片。
    [Fact]
    public async Task HeaderCounts_DescribeTheWholeTree_NotTheListedSlice()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors","limit":2}""");

        Assert.Contains("4 in scope", content);            // 树的总量
        Assert.Contains("2 direct", content);              // 整棵树的直接子类数
        Assert.Contains("deepest 3 levels down", content); // 整棵树的深度
        Assert.Contains("Listed below: 2", content);       // 只有这一格描述切片

        // 切片里两条都是直接子类，旧写法会据此报 1 层——那正是要挡住的假数字
        Assert.DoesNotContain("deepest 1 level down", content);
    }

    // 反面：没被截断时不印「列了多少」——那时它逐字等于前面那个总数。
    // 沿用 R33 的读法：出现「列了多少」这一格本身就是「被截了」的信号。
    [Fact]
    public async Task Header_OmitsTheListedCount_WhenNothingWasCutOff()
    {
        var content = await Run(BuildTool(), """{"symbol":"ZzBase","mode":"inheritors"}""");

        Assert.Contains("4 in scope", content);
        Assert.Contains("2 direct", content);
        Assert.DoesNotContain("Listed below", content);
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

    // 窄 scope 下的零结果分支：不再追加「retry with scope:'all' before concluding it does not
    // exist」。那句在本分支的三种情形下全是错的或白跑的——继承闭包是全域算的，而 IsKnownType
    // 也与 scope 无关，换 scope 返回逐字相同。实测它紧跟在语气最重的「this is an answer」
    // 后面，两句正相反；而「拼写待核」那一支后面跟着它，则保证白跑一轮。
    [Theory]
    [InlineData("ZzGreatGrandchild")]  // 已知类型、确实没人继承
    [InlineData("ZzNoSuchType")]       // 索引里根本没这个名字
    public async Task ZeroInheritors_DoesNotAdviseRetryingWithAWiderScope(string symbol)
    {
        var (tool, _) = BuildNarrowScopedTool();

        var content = await Run(tool, $$"""{"symbol":"{{symbol}}","mode":"inheritors","scope":"vanilla"}""");

        Assert.DoesNotContain("retry with scope", content);
        Assert.DoesNotContain("before concluding it does not exist", content);
    }

    // 反面：scope 外真有派生类时，那条逐源计数仍要在，且背书要撤——R38 的判据不受本轮影响
    [Fact]
    public async Task ZeroInheritorsInScope_StillReportsSubclassesFoundOutsideIt()
    {
        var (tool, _) = BuildNarrowScopedTool();

        var content = await Run(tool, """{"symbol":"ZzChild","mode":"inheritors","scope":"vanilla"}""");

        Assert.Contains("not the whole answer", content);
        Assert.Contains("Outside scope 'vanilla'", content);
        Assert.DoesNotContain("this is an answer", content);
    }

    // 两个源：vanilla 里放基类与一条链，mod 里放一个 scope 外的派生类，
    // 这样 scope:'vanilla' 才是真正的窄 scope（单源目录会被判成全域）。
    private (TraceTool Tool, string Root) BuildNarrowScopedTool()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Chain.cs"), """
            namespace Zz
            {
                public class ZzBase { }
                public class ZzChild : ZzBase { }
                public class ZzGreatGrandchild { }
            }
            """);

        var other = _workspace.Dir("Other");
        _workspace.WriteFile(Path.Combine("Other", "Ext.cs"), """
            namespace Zm { public class ZmChildOfChild : ZzChild { } }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.Scan(other);
        indexer.FreezeIndex();

        return (new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root), ("mod", other)], null, null)), root);
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
