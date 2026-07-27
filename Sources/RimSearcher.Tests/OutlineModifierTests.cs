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

        Assert.Contains("Property: public float PublicProp", content);
        Assert.Contains("Property: private float PrivateProp", content);
    }

    // const 与 static readonly 与普通字段：三者的取用方式完全不同
    [Fact]
    public async Task Fields_CarryConstAndStaticReadonly()
    {
        var content = await Outline();

        Assert.Contains("Field: private const float ZzConstField", content);
        Assert.Contains("Field: private static readonly string ZzStaticField", content);
        Assert.Contains("Field: public int ZzPlainField", content);
    }

    // static 与否决定写 Harmony patch 时要不要 __instance 形参
    [Fact]
    public async Task Methods_CarryStaticVirtualAndOverride()
    {
        var content = await Outline();

        Assert.Contains("Method: public static ZzShapes ZzFromValue(int v)", content);
        Assert.Contains("Method: public override string ToString()", content);
        Assert.Contains("Method: protected virtual void ZzHook()", content);
    }

    // 修饰符是前缀，不能把类型和名字挤掉
    [Fact]
    public async Task ModifiersDoNotDisplaceTypeOrName()
    {
        var content = await Outline();

        Assert.DoesNotContain("Field:  ", content);
        Assert.DoesNotContain("Method:  ", content);
        Assert.DoesNotContain("Property:  ", content);
    }
}
