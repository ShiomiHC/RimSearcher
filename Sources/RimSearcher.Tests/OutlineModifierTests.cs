using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：大纲丢弃全部成员修饰符，private 与 public、static 与实例、const 与可写字段
// 渲染成逐字相同的一行。大纲的用途就是「照着它写调用或写 Harmony patch」
// （RoslynHelper.FormatParameter 上方的注释已经写明这条判据），修饰符缺失等于给出错的
// 抄写样本：会写出 `comp.PrivateProp`（编译不过）、`instance.StaticMethod()`（编译不过），
// 或对一个 const 做 AccessTools.FieldRefAccess（const 没有字段槽，运行期炸）。
public class OutlineModifierTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private async Task<string> Outline()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzShapes.cs"), """
            namespace Zz
            {
                public class ZzShapes
                {
                    public float PublicProp { get; set; }
                    private float PrivateProp { get; set; }
                    private const float ZzConstField = 0.05f;
                    private static readonly string ZzStaticField = "x";
                    public int ZzPlainField;
                    public static ZzShapes ZzFromValue(int v) { return null; }
                    public override string ToString() { return null; }
                    protected virtual void ZzHook() { }
                }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new InspectTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        using var args = JsonDocument.Parse("""{"name":"ZzShapes"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    [Fact]
    public async Task Properties_CarryTheirVisibility()
    {
        var content = await Outline();

        // 种类由 `  Properties:` 表头说一次，行内只剩签名本身
        Assert.Contains("\n  Properties:\n", content);
        Assert.Contains("\n    public float PublicProp", content);
        Assert.Contains("\n    private float PrivateProp", content);
    }

    // const 与 static readonly 与普通字段：三者的取用方式完全不同
    [Fact]
    public async Task Fields_CarryConstAndStaticReadonly()
    {
        var content = await Outline();

        Assert.Contains("\n  Fields:\n", content);
        Assert.Contains("\n    private const float ZzConstField", content);
        Assert.Contains("\n    private static readonly string ZzStaticField", content);
        Assert.Contains("\n    public int ZzPlainField", content);
    }

    // static 与否决定写 Harmony patch 时要不要 __instance 形参
    [Fact]
    public async Task Methods_CarryStaticVirtualAndOverride()
    {
        var content = await Outline();

        Assert.Contains("\n  Methods:\n", content);
        Assert.Contains("\n    public static ZzShapes ZzFromValue(int v)", content);
        Assert.Contains("\n    public override string ToString()", content);
        Assert.Contains("\n    protected virtual void ZzHook()", content);
    }

    // 修饰符是前缀，不能把类型和名字挤掉。种类前缀去掉之后，「没有修饰符」的行会以
    // 缩进后的第一个字符开头——多一个空格就说明前缀渲染成了空串却仍留着分隔空格。
    [Fact]
    public async Task ModifiersDoNotDisplaceTypeOrName()
    {
        var content = await Outline();

        Assert.DoesNotContain("\n     ", content);
    }

    // 逐行的 `Property: ` / `Field: ` / `Method: ` 已由每块的表头取代
    [Fact]
    public async Task OutlineRows_DoNotRepeatTheKindOnEveryLine()
    {
        var content = await Outline();

        Assert.DoesNotContain("  Property: ", content);
        Assert.DoesNotContain("  Field: ", content);
        Assert.DoesNotContain("  Method: ", content);
    }
}
