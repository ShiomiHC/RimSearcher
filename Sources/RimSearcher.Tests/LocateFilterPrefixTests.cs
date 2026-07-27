using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 过滤前缀这一层此前有三个各自独立的缺口，共同点是：调用方写了一个前缀，
// 服务端做了别的事，而返回里看不出来。
public class LocateFilterPrefixTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private LocateTool BuildTool()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzHolder.cs"), """
            namespace Zz
            {
                public class ZzHolder
                {
                    public int zzPulse;
                    public int ZzPulseCount { get; set; }
                    public void ZzPulseNow() { }
                    public void ZzPulseLater() { }
                    public void ZzPulseAgain() { }
                }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(LocateTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // 缺陷回归：'type: X'（冒号后带空格）是人写查询时极常见的写法，分词后是两个 token。
    // 空的 filter 值此前照样被当成「用户指定了过滤词」，把该段搜索词覆盖成空串，
    // 于是 C# Types 段整段消失——读起来就是「这个类型不存在」。
    [Fact]
    public async Task FilterPrefixFollowedByASpace_BehavesLikeTheNoSpaceForm()
    {
        var tool = BuildTool();

        var spaced = await Run(tool, """{"query":"type: ZzHolder"}""");
        var tight = await Run(tool, """{"query":"type:ZzHolder"}""");

        Assert.Contains("ZzHolder", spaced);
        Assert.Contains("C# Types", spaced);

        // 首行回显的是调用方原样传进来的查询串（那是对的），故只比对其后的正文
        static string Body(string content) => content[(content.IndexOf('\n') + 1)..];
        Assert.Equal(Body(tight), Body(spaced));
    }

    // 光杆前缀恒零命中；此前连「我把它忽略了」都不说
    [Fact]
    public async Task BareFilterPrefix_SaysItWasIgnored()
    {
        var content = await Run(BuildTool(), """{"query":"type:"}""");

        Assert.Contains("was ignored", content);
        Assert.Contains("type:CompShield", content);
    }

    // 缺陷回归：认不出的前缀连同前缀一起当关键词（这是既定行为），于是
    // 'member:ZzPulseNow' 零命中而 'method:ZzPulseNow' 有命中——同一个符号，
    // 一个说不存在、一个说有。差别全在那个没被识别的前缀，返回里却毫无线索。
    [Fact]
    public async Task UnknownFilterPrefix_IsCalledOut()
    {
        var tool = BuildTool();

        var unknown = await Run(tool, """{"query":"member:ZzPulseNow"}""");
        var known = await Run(tool, """{"query":"method:ZzPulseNow"}""");

        Assert.Contains("ZzPulseNow", known);
        Assert.Contains("'member:' is not a query filter", unknown);
        Assert.Contains("Known filters:", unknown);
    }

    [Fact]
    public async Task KnownFilterPrefix_ProducesNoSuchNotice()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzPulseNow"}""");

        Assert.DoesNotContain("is not a query filter", content);
        Assert.DoesNotContain("was ignored", content);
    }

    // 缺陷回归：README 写着 field: =「只搜字段/属性」、method: =「只搜方法」，
    // 但取回层不分种类——候选按分数取 limit 条，方法数量压倒性多于字段时
    // 把配额吃光，field: 查询于是几乎拿不到字段。过滤必须发生在取回层。
    [Fact]
    public async Task FieldPrefix_ReturnsOnlyFieldsAndProperties()
    {
        var content = await Run(BuildTool(), """{"query":"field:ZzPulse"}""");

        Assert.Contains("zzPulse", content);
        Assert.Contains("ZzPulseCount", content);
        Assert.DoesNotContain("ZzPulseNow", content);
        Assert.DoesNotContain("ZzPulseLater", content);
    }

    [Fact]
    public async Task MethodPrefix_ReturnsOnlyMethods()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzPulse"}""");

        Assert.Contains("ZzPulseNow", content);
        Assert.DoesNotContain("ZzPulseCount", content);
    }

    // 不带前缀时不筛种类，三类都该在
    [Fact]
    public async Task NoPrefix_StillReturnsEveryMemberKind()
    {
        var content = await Run(BuildTool(), """{"query":"ZzPulse","limit":"all"}""");

        Assert.Contains("zzPulse", content);
        Assert.Contains("ZzPulseCount", content);
        Assert.Contains("ZzPulseNow", content);
    }
}
