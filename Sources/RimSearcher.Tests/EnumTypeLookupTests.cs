using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// enum 与 delegate 不是 TypeDeclarationSyntax，索引原先整类不收：inspect('ShieldState')
// 回「不存在，用 locate 查确切名字」（名字本来就是对的），read_code(extractClass) 回
// 「不是类，用 inspect 核对类型名」——两条提示互相指，而文件就在索引里躺着。
public class EnumTypeLookupTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string EnumSource = """
        namespace RimWorld
        {
            public enum ShieldState : byte
            {
                Active,
                Resetting = 7,
                Disabled
            }

            public delegate void ShieldBrokenHandler(int energy);

            public delegate ref F FieldRef<in T, F>(T instance = default(T)) where T : class;

            public delegate ref F FieldRef<F>();
        }
        """;

    private (SourceIndexer Indexer, ScopeCatalog Catalog) BuildIndex()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ShieldState.cs"), EnumSource);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<ToolResult> Run(ITool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public void EnumAndDelegate_AreIndexedAsTypes()
    {
        var (indexer, _) = BuildIndex();

        Assert.NotEmpty(indexer.GetPathsByType("ShieldState"));
        Assert.NotEmpty(indexer.GetPathsByType("RimWorld.ShieldState"));
        Assert.NotEmpty(indexer.GetPathsByType("ShieldBrokenHandler"));
    }

    [Fact]
    public async Task Inspect_OutlinesEnumValues()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new InspectTool(indexer, new DefIndexer(), catalog);

        var result = await Run(tool, """{"name":"ShieldState"}""");

        Assert.False(result.IsError);
        Assert.Contains("Enum: RimWorld.ShieldState : byte", result.Content);
        // 取值不逐行挂 `Value: `：上一行的 `Enum:` 已经说完下面每行是什么
        Assert.Contains("\n  Active", result.Content);
        // 显式赋值要跟着一起出来：调用方查 enum 多半就是为了那个数值
        Assert.Contains("\n  Resetting = 7", result.Content);
        Assert.Contains("\n  Disabled", result.Content);
        Assert.DoesNotContain("Value: ", result.Content);
    }

    [Fact]
    public async Task Inspect_OutlinesDelegateSignature()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new InspectTool(indexer, new DefIndexer(), catalog);

        var result = await Run(tool, """{"name":"ShieldBrokenHandler"}""");

        Assert.False(result.IsError);
        Assert.Contains("Delegate: void RimWorld.ShieldBrokenHandler(int energy)", result.Content);
    }

    [Fact]
    public async Task ReadCode_ExtractClass_ReturnsEnumBody()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new ReadCodeTool(indexer, catalog);

        var result = await Run(tool, """{"path":"ShieldState","extractClass":"ShieldState"}""");

        Assert.False(result.IsError);
        Assert.Contains("enum ShieldState", result.Content);
        Assert.Contains("Resetting = 7", result.Content);
    }

    // 类型参数表丢了的话，签名里的 T/F 在整行里没有声明处（照抄编译不过），而且只有
    // arity 不同的两个重载会渲染成一模一样的一行，调用方分不出自己要的是哪个。
    [Fact]
    public async Task Inspect_KeepsDelegateTypeParametersAndConstraints()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new InspectTool(indexer, new DefIndexer(), catalog);

        var result = await Run(tool, """{"name":"FieldRef"}""");

        Assert.False(result.IsError);
        Assert.Contains(
            "Delegate: ref F RimWorld.FieldRef<in T, F>(T instance = default(T)) where T : class",
            result.Content);
        Assert.Contains("Delegate: ref F RimWorld.FieldRef<F>()", result.Content);
    }

    [Fact]
    public async Task ReadCode_ExtractClass_ReturnsDelegateDeclaration()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new ReadCodeTool(indexer, catalog);

        var result = await Run(tool, """{"path":"ShieldState","extractClass":"ShieldBrokenHandler"}""");

        Assert.False(result.IsError);
        Assert.Contains("delegate void ShieldBrokenHandler(int energy)", result.Content);
    }

    // locate 现在会推荐 `EnumMembers: RimWorld.ShieldState.Resetting`，成员级 diff 也会列出
    // 它并明说「pass 'method' with one of these names」——两处指的下一步都是这里。
    [Fact]
    public async Task ReadCode_MethodName_FindsEnumValue()
    {
        var (indexer, catalog) = BuildIndex();
        var tool = new ReadCodeTool(indexer, catalog);

        var result = await Run(tool, """{"path":"ShieldState","methodName":"Resetting"}""");

        Assert.False(result.IsError);
        Assert.Contains("Resetting = 7", result.Content);
    }

    [Fact]
    public void ExtractMemberText_FindsEnumValue()
    {
        var extracted = RoslynHelper.ExtractMemberText(EnumSource, "Resetting", "ShieldState");

        Assert.True(extracted.IsOk);
        Assert.Contains("Resetting = 7", extracted.Content);
    }

    // 字段与事件在 locate 和成员级 diff 里一直都列着，两处给的下一步都是 read_code(methodName)
    private const string MembersSource = """
        namespace RimWorld
        {
            public class CompShield
            {
                public float energy;

                public int a, b;

                public event System.Action ShieldBroken;

                public event System.Action Reset
                {
                    add { }
                    remove { }
                }
            }
        }
        """;

    [Theory]
    [InlineData("energy", "public float energy;")]
    [InlineData("b", "public int a, b;")]
    [InlineData("ShieldBroken", "event System.Action ShieldBroken;")]
    [InlineData("Reset", "event System.Action Reset")]
    public void ExtractMemberText_FindsFieldsAndEvents(string memberName, string expected)
    {
        var extracted = RoslynHelper.ExtractMemberText(MembersSource, memberName, "CompShield");

        Assert.True(extracted.IsOk);
        Assert.Contains(expected, extracted.Content);
    }

    [Fact]
    public void FormatMemberBody_LabelsFieldsAsFieldsNotMethods()
    {
        var body = RoslynHelper.FormatMemberBody(MembersSource, "energy", "CompShield", "CompShield.cs");

        Assert.True(body.IsOk);
        Assert.Matches(@"^// Field energy — CompShield\.cs:\d+$", body.Content.Split('\n')[0].TrimEnd());
    }

    [Fact]
    public void EnumValues_AreSearchableAsMembers()
    {
        var (indexer, catalog) = BuildIndex();

        var hits = indexer.SearchMembersByKeywords(["Resetting"], catalog.Everything, 10);

        Assert.Contains(hits.Items, entry =>
            entry.Item.TypeName == "RimWorld.ShieldState" && entry.Item.MemberName == "Resetting");
    }

    [Fact]
    public void MemberDiff_SeesEnumValues()
    {
        const string changed = """
            namespace RimWorld
            {
                public enum ShieldState : byte
                {
                    Active,
                    Resetting = 9,
                    Disabled
                }
            }
            """;

        var before = RoslynHelper.ListMemberTexts(EnumSource);
        var after = RoslynHelper.ListMemberTexts(changed);

        // 不收 enum 取值时，只改了一个枚举值的文件会被报成「改在任何成员声明之外」
        Assert.Contains("RimWorld.ShieldState.Resetting", before.Keys);
        Assert.NotEqual(before["RimWorld.ShieldState.Resetting"], after["RimWorld.ShieldState.Resetting"]);
    }
}
