using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：ParentName 链合并是**静默**失败的——父 def 查不到时循环直接结束，
// CleanupMetadata 又把 ParentName 属性删掉，于是三种情形渲染得逐字同形：
//   ① 这个 def 本来就没有父；② 父已经合进来了；③ 父找不到，所以少了半份。
// 而工具描述向调用方承诺的是 "the complete effective definition"，
// 它会把 ③ 的半成品当完整定义，据此断定某个 hediff 没有 hediffClass、
// 不关联任何 C# 类，然后去补一个根本不缺的字段。
public class InspectInheritanceChainTests : IDisposable
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

    private static async Task<string> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // 父声明了却不在索引里（那个源没配进 config，或 scope 把它挡在外面）
    private static (string, string)[] OrphanChild =>
    [
        ("Child.xml",
         "<Defs>\n  <HediffDef ParentName=\"ZzMissingBase\">\n    <defName>ZzOrphan</defName>\n"
         + "    <label>orphan</label>\n  </HediffDef>\n</Defs>\n")
    ];

    private static (string, string)[] ResolvedChain =>
    [
        ("Base.xml",
         "<Defs>\n  <HediffDef Name=\"ZzBenignBase\" Abstract=\"True\">\n"
         + "    <hediffClass>HediffWithComps</hediffClass>\n    <isBad>false</isBad>\n  </HediffDef>\n</Defs>\n"),
        ("Child.xml",
         "<Defs>\n  <HediffDef ParentName=\"ZzBenignBase\">\n    <defName>ZzResolved</defName>\n"
         + "    <label>resolved</label>\n  </HediffDef>\n</Defs>\n")
    ];

    private static (string, string)[] NoParent =>
    [
        ("Solo.xml",
         "<Defs>\n  <HediffDef>\n    <defName>ZzSolo</defName>\n    <label>solo</label>\n"
         + "    <hediffClass>HediffWithComps</hediffClass>\n  </HediffDef>\n</Defs>\n")
    ];

    [Fact]
    public async Task MissingParent_IsWarnedAboutLoudly()
    {
        var content = await Run(BuildTool(OrphanChild), """{"name":"ZzOrphan"}""");

        Assert.Contains("ZzMissingBase", content);
        Assert.Contains("NOT the complete effective definition", content);
        Assert.Contains("scope:'all'", content);
    }

    // 合并成功时要说清字段是从哪一条继承来的——这正是「本来就没有父」的反面
    [Fact]
    public async Task ResolvedChain_IsSpelledOut()
    {
        var content = await Run(BuildTool(ResolvedChain), """{"name":"ZzResolved"}""");

        Assert.Contains("Inheritance chain: ZzResolved <- ZzBenignBase", content);
        Assert.Contains("HediffWithComps", content);
        Assert.DoesNotContain("NOT the complete effective definition", content);
    }

    // 真的没有父时也要明说，否则它和「父找不到」还是分不开
    [Fact]
    public async Task DefWithNoParent_SaysSoExplicitly()
    {
        var content = await Run(BuildTool(NoParent), """{"name":"ZzSolo"}""");

        Assert.Contains("declares no ParentName", content);
        Assert.DoesNotContain("NOT the complete effective definition", content);
    }

    // 三种情形必须两两可区分——这条就是本缺陷的本体
    [Fact]
    public async Task TheThreeCases_AreDistinguishable()
    {
        var missing = await Run(BuildTool(OrphanChild), """{"name":"ZzOrphan"}""");
        var resolved = await Run(BuildTool(ResolvedChain), """{"name":"ZzResolved"}""");
        var none = await Run(BuildTool(NoParent), """{"name":"ZzSolo"}""");

        Assert.True(missing.Contains("NOT the complete") && !resolved.Contains("NOT the complete"));
        Assert.True(resolved.Contains("<- ZzBenignBase") && !none.Contains("<-"));
        Assert.True(none.Contains("declares no ParentName") && !resolved.Contains("declares no ParentName"));
    }

    // ParentName 成环时合并同样会少字段，不能当成正常完成
    [Fact]
    public async Task CyclicParentName_IsWarnedAboutToo()
    {
        var tool = BuildTool(
            ("A.xml", "<Defs>\n  <HediffDef Name=\"ZzA\" ParentName=\"ZzB\">\n    <defName>ZzA</defName>\n  </HediffDef>\n</Defs>\n"),
            ("B.xml", "<Defs>\n  <HediffDef Name=\"ZzB\" ParentName=\"ZzA\">\n    <isBad>false</isBad>\n  </HediffDef>\n</Defs>\n"));

        var content = await Run(tool, """{"name":"ZzA"}""");

        Assert.Contains("Warning", content);
        Assert.DoesNotContain("declares no ParentName", content);
    }
}
