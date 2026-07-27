using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：服务端对参数名极宽容（别名 + 大小写/下划线归一），调用方由此学到「这台服务器
// 对名字不挑」，于是把别的工具的参数类推过来是必然行为——locate 传 defType（inspect 才有）、
// trace 传 fileFilter（search_regex 才有）。这些键一律被丢弃，而返回是一份逐字正常、
// 看不出任何异常的结果，调用方据此得出的结论是「我按 X 过滤后就这些」。
public class UnknownParameterNoticeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private LocateTool BuildLocate()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzThing.cs"), "namespace Zz { public class ZzThing { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static string? Notice(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return ToolArgs.UnknownKeyNotice(tool, args.RootElement);
    }

    [Fact]
    public void UnknownKey_IsCalledOut()
    {
        var notice = Notice(BuildLocate(), """{"query":"ZzThing","bogusParam":1}""");

        Assert.NotNull(notice);
        Assert.Contains("bogusParam", notice);
        Assert.Contains("rimworld-searcher__locate accepts", notice);
    }

    // 最常见的真实误用：把 inspect 的 defType 类推到 locate 上
    [Fact]
    public void ParameterBorrowedFromAnotherTool_IsCalledOut()
    {
        var notice = Notice(BuildLocate(), """{"query":"ZzThing","defType":"ThingDef"}""");

        Assert.NotNull(notice);
        Assert.Contains("defType", notice);
    }

    // 健康调用零开销、零额外 token
    [Fact]
    public void DeclaredParameters_ProduceNoNotice()
    {
        Assert.Null(Notice(BuildLocate(), """{"query":"ZzThing","scope":"all","limit":5}"""));
    }

    // 合法别名不能被误报成被忽略——那比不提示更糟
    [Theory]
    [InlineData("""{"name":"ZzThing"}""")]
    [InlineData("""{"symbol":"ZzThing"}""")]
    [InlineData("""{"query":"ZzThing","maxResults":5}""")]
    [InlineData("""{"query":"ZzThing","sources":"vanilla"}""")]
    public void AcceptedAliases_ProduceNoNotice(string json)
    {
        Assert.Null(Notice(BuildLocate(), json));
    }

    // 大小写/下划线归一后仍算命中：max_results 与 maxResults 是同一个键
    [Fact]
    public void NormalisedKeySpelling_ProducesNoNotice()
    {
        Assert.Null(Notice(BuildLocate(), """{"query":"ZzThing","max_results":5}"""));
    }

    // 每个工具都要声明得住自己的别名，否则上线即误报
    [Fact]
    public void EveryToolAcceptsItsOwnAliases()
    {
        var root = _workspace.Dir("Core2");
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var cases = new (ITool Tool, string Json)[]
        {
            (new InspectTool(indexer, defIndexer, catalog), """{"defName":"Zz","defTypeName":"ThingDef","scope":"all"}"""),
            (new TraceTool(indexer, catalog), """{"symbolName":"Zz","traceMode":"usages","limit":5}"""),
            (new ReadCodeTool(indexer, catalog), """{"filePath":"Zz.cs","member":"Tick","typeName":"Zz"}"""),
            (new SearchRegexTool(indexer, catalog), """{"regex":"Zz","extension":".cs","caseInsensitive":true}"""),
            (new ListDirectoryTool(), """{"dir":"/tmp","count":5,"skip":0}"""),
        };

        foreach (var (tool, json) in cases)
            Assert.Null(Notice(tool, json));
    }
}
