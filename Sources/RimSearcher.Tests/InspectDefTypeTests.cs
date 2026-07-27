using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// defType 只影响过表头：正文那次 ResolveDefXmlElementAsync 传的是 name，内部又按名字查了
// 一遍、落回默认胜者，于是 `Type: ThingDef` 底下摆的是 BodyDef 的 XML。
// 加上确定性排序之后这还成了必现——vanilla 的 ThingDef Human 无论传不传 defType 都拿不到
// 自己的 XML。表头与正文必须来自同一条 def。
public class InspectDefTypeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private InspectTool BuildTool(params (string File, string Xml)[] files)
    {
        var root = _workspace.Dir("Defs");
        foreach (var (file, xml) in files)
            _workspace.WriteFile(Path.Combine("Defs", file), xml);

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        defIndexer.FreezeIndex();

        var sourceIndexer = new SourceIndexer();
        sourceIndexer.FreezeIndex();

        return new InspectTool(sourceIndexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<ToolResult> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    private static (string File, string Xml)[] ThreeTypes =>
    [
        ("Races.xml", "<Defs>\n  <ThingDef>\n    <defName>Human</defName>\n    <label>human</label>\n  </ThingDef>\n</Defs>\n"),
        ("Bodies.xml", "<Defs>\n  <BodyDef>\n    <defName>Human</defName>\n    <corePart>torso</corePart>\n  </BodyDef>\n</Defs>\n"),
        ("Hediffs.xml", "<Defs>\n  <HediffGiverSetDef>\n    <defName>Human</defName>\n  </HediffGiverSetDef>\n</Defs>\n")
    ];

    [Fact]
    public async Task DefType_PicksTheXmlBodyToo_NotJustTheHeader()
    {
        var tool = BuildTool(ThreeTypes);

        var result = await Run(tool, """{"name":"Human","defType":"ThingDef"}""");

        Assert.False(result.IsError);
        Assert.Contains("Type: ThingDef", result.Content);
        Assert.Contains("Races.xml", result.Content);

        // 正文必须是同一条：这三条 def 的标签互不相同，串了一眼就能看出来
        Assert.Contains("<ThingDef>", result.Content);
        Assert.DoesNotContain("<BodyDef>", result.Content);
        Assert.DoesNotContain("<HediffGiverSetDef>", result.Content);
    }

    [Fact]
    public async Task WithoutDefType_HeaderAndBodyStillAgree()
    {
        var tool = BuildTool(ThreeTypes);

        var result = await Run(tool, """{"name":"Human"}""");

        Assert.False(result.IsError);
        Assert.Contains("Type: BodyDef", result.Content);
        Assert.Contains("<BodyDef>", result.Content);
        Assert.DoesNotContain("<ThingDef>", result.Content);
    }

    [Fact]
    public async Task AmbiguousAcrossTypes_TellsYouToPassDefType()
    {
        var tool = BuildTool(ThreeTypes);

        var result = await Run(tool, """{"name":"Human"}""");

        Assert.Contains("(BodyDef, HediffGiverSetDef, ThingDef)", result.Content);
        Assert.Contains("pass defType to pick another", result.Content);
    }

    // 撞名的几条恰好同类型时 defType 分不开它们，照着提示传回来会拿到逐字相同的结果
    [Fact]
    public async Task AmbiguousWithinOneType_DoesNotSuggestDefType()
    {
        var tool = BuildTool(
            ("A.xml", "<Defs>\n  <ThingDef>\n    <defName>Human</defName>\n  </ThingDef>\n</Defs>\n"),
            ("B.xml", "<Defs>\n  <ThingDef>\n    <defName>Human</defName>\n  </ThingDef>\n</Defs>\n"));

        var result = await Run(tool, """{"name":"Human"}""");

        Assert.Contains("2 defs share this name", result.Content);
        Assert.DoesNotContain("pass defType to pick another", result.Content);
        Assert.Contains("narrow scope", result.Content);
    }

    // 'type' 在本服务器别处是 C# 类型名（read_code 的 className 别名、inspect 自己 name
    // 支持的 'type:' 前缀）。收作 defType 别名的话，一次很自然的误用会凭空多出一条告警。
    [Fact]
    public async Task TypeIsNotAnAliasForDefType()
    {
        var tool = BuildTool(ThreeTypes);

        var result = await Run(tool, """{"name":"Human","type":"CompShield"}""");

        Assert.False(result.IsError);
        Assert.DoesNotContain("no 'CompShield' named", result.Content);
    }
}
