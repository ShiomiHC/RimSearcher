using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 「以 Class / Worker 结尾的标签装的是类型名」是启发式，而 RimWorld 的 XML 里一样有
// 以 Worker 结尾却装着数字的标签：技能需求 `<BasicWorker>3</BasicWorker>` 让 Human 的
// Linked C# Types 里凭空多出一条 `3 (not indexed)`。
public class InspectLinkedTypesTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string Def = """
        <Defs>
          <ThingDef>
            <defName>TestPawn</defName>
            <thingClass>Pawn</thingClass>
            <skillGains>
              <BasicWorker>3</BasicWorker>
            </skillGains>
            <statBases>
              <MeleeWorker>-2.5</MeleeWorker>
            </statBases>
            <comps>
              <li Class="CompProperties_Shield" />
            </comps>
          </ThingDef>
        </Defs>
        """;

    private async Task<ToolResult> Inspect(string name)
    {
        var xmlRoot = _workspace.Dir("Defs");
        _workspace.WriteFile(Path.Combine("Defs", "Things.xml"), Def);

        var csRoot = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Pawn.cs"), "namespace Verse { public class Pawn { } }\n");

        var indexer = new SourceIndexer();
        indexer.Scan(csRoot);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.Scan(xmlRoot);
        defIndexer.FreezeIndex();

        var tool = new InspectTool(
            indexer, defIndexer, ScopeCatalog.Build([("vanilla", csRoot), ("defs", xmlRoot)], null, null));

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { name }));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task LinkedTypes_SkipValuesThatCannotBeTypeNames()
    {
        var result = await Inspect("TestPawn");

        Assert.False(result.IsError);
        Assert.Contains("**Linked C# Types:**", result.Content);

        // 真正的类型引用照常列出
        Assert.Contains("`Pawn`", result.Content);
        Assert.Contains("`CompProperties_Shield`", result.Content);

        // 数字与负小数都不是标识符，不该出现在类型列表里
        Assert.DoesNotContain("`3`", result.Content);
        Assert.DoesNotContain("`-2.5`", result.Content);
    }

    // 上面两个值在首字符判断就被挡掉了，逐字符白名单那个循环一次都没走到 return false
    [Fact]
    public void LooksLikeTypeName_RejectsLetterLedValuesThatAreNotIdentifiers()
    {
        Assert.True(InspectTool.LooksLikeTypeNameForTests("CompProperties_Shield"));
        Assert.True(InspectTool.LooksLikeTypeNameForTests("RimWorld.CompShield"));
        Assert.True(InspectTool.LooksLikeTypeNameForTests("Verse.Outer+Inner"));
        Assert.True(InspectTool.LooksLikeTypeNameForTests("List<ThingDef>"));

        Assert.False(InspectTool.LooksLikeTypeNameForTests("Melee(2)"));
        Assert.False(InspectTool.LooksLikeTypeNameForTests("a-1.5"));
        Assert.False(InspectTool.LooksLikeTypeNameForTests("x%"));
        Assert.False(InspectTool.LooksLikeTypeNameForTests("3"));
        Assert.False(InspectTool.LooksLikeTypeNameForTests("-2.5"));
    }
}
