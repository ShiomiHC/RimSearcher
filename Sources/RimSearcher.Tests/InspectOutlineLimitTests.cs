using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：大纲每类折叠到 40 条，而折叠行给的两条出路在触发折叠的大类型上都走不通——
// locate 只能按已知名字找（可调用方正是不知道剩下的叫什么才来看大纲），
// read_code extractClass 到 2000 行就二次截断。于是被折叠的成员在整套 API 下无路可取，
// 输出却写得像有。这里给 inspect 补 limit，并守住 'all' 是真无限（不受 HardLimit=200 夹）。
public class InspectOutlineLimitTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const int PropertyCount = 260;

    private InspectTool BuildTool()
    {
        var root = _workspace.Dir("Core");

        var sb = new StringBuilder();
        sb.AppendLine("namespace Zz");
        sb.AppendLine("{");
        sb.AppendLine("    public class ZzWide");
        sb.AppendLine("    {");
        for (var i = 0; i < PropertyCount; i++)
            sb.AppendLine($"        public int ZzProp{i:D3} {{ get; set; }}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        _workspace.WriteFile(Path.Combine("Core", "ZzWide.cs"), sb.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new InspectTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private async Task<string> Run(string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await BuildTool().ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    [Fact]
    public async Task DefaultOutline_StillFoldsAtFortyPerKind()
    {
        var content = await Run("""{"name":"ZzWide"}""");

        Assert.Contains("ZzProp039", content);
        Assert.DoesNotContain("ZzProp040", content);
        Assert.Contains($"+{PropertyCount - 40} more properties", content);
    }

    // 折叠行必须给一条真的走得通的路，而不是两条走不通的
    [Fact]
    public async Task FoldLine_PointsAtLimitAll()
    {
        var content = await Run("""{"name":"ZzWide"}""");

        Assert.Contains("limit:'all'", content);
        Assert.DoesNotContain("read_code extractClass)", content);
    }

    // 'all' 必须是真无限：单个类型成员数超过 HardLimit=200 是常态
    [Fact]
    public async Task LimitAll_ListsEveryMemberPastTheHardLimit()
    {
        var content = await Run("""{"name":"ZzWide","limit":"all"}""");

        Assert.Contains("ZzProp000", content);
        Assert.Contains($"ZzProp{PropertyCount - 1:D3}", content);
        Assert.DoesNotContain("more properties", content);
    }

    [Fact]
    public async Task NumericLimit_IsHonoured()
    {
        var content = await Run("""{"name":"ZzWide","limit":5}""");

        Assert.Contains("ZzProp004", content);
        Assert.DoesNotContain("ZzProp005", content);
        Assert.Contains($"+{PropertyCount - 5} more properties", content);
    }

    // limit 与其余工具共用同一套解析：不可解释的值要报错而不是静默退回默认
    [Fact]
    public async Task UninterpretableLimit_IsRejected()
    {
        using var args = JsonDocument.Parse("""{"name":"ZzWide","limit":"lots"}""");
        var tool = BuildTool();

        await Assert.ThrowsAsync<ToolArgumentException>(
            () => tool.ExecuteAsync(args.RootElement, CancellationToken.None));
    }
}
