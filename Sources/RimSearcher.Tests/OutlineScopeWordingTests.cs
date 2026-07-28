using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 第十二轮盲测：调用方要在 `Pawn` 上找取地图的成员，大纲折叠行写着「pass limit:'all' to
// expand the whole list」，照做展开全部 118 条属性也拿不到 `Map`——它声明在基类
// `Verse.Thing` 上。`the whole list` 是个没有辖域的全称承诺，而这份大纲只列本类型自己
// 声明的成员。同一份返回里另有两处同类的辖域缺口：继承链那行不说「基类成员不在下面」，
// 同名类型被别的源盖住时只说「outline omitted」而不说怎么才能看到它。
public class OutlineScopeWordingTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const int PropertyCount = 60;

    // 基类带一个派生类没有的成员：正是「照折叠行展开也拿不到」的那种成员
    private const string BaseSource = """
        namespace Zz
        {
            public class ZzBase
            {
                public int ZzOnlyOnTheBase { get; set; }
            }
        }
        """;

    private InspectTool BuildTool(bool withSecondSource = false)
    {
        var root = _workspace.Dir("Core");

        var sb = new StringBuilder();
        sb.AppendLine("namespace Zz");
        sb.AppendLine("{");
        sb.AppendLine("    public class ZzDerived : ZzBase");
        sb.AppendLine("    {");
        for (var i = 0; i < PropertyCount; i++)
            sb.AppendLine($"        public int ZzProp{i:D3} {{ get; set; }}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        _workspace.WriteFile(Path.Combine("Core", "ZzDerived.cs"), sb.ToString());
        _workspace.WriteFile(Path.Combine("Core", "ZzBase.cs"), BaseSource);

        var sources = new List<(string, string)> { ("vanilla", root) };
        var indexer = new SourceIndexer();
        indexer.Scan(root);

        if (withSecondSource)
        {
            // 同名类型的第二份声明：低优先级源的那份不会被列出来
            var modRoot = _workspace.Dir("Mod");
            _workspace.WriteFile(Path.Combine("Mod", "ZzDerived.cs"),
                "namespace Zz\n{\n    public class ZzDerived\n    {\n        public int ZzModOnly { get; set; }\n    }\n}\n");
            indexer.Scan(modRoot);
            sources.Add(("Milira", modRoot));
        }

        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new InspectTool(indexer, defIndexer, ScopeCatalog.Build(sources, null, null));
    }

    private async Task<string> Run(string json, bool withSecondSource = false)
    {
        using var args = JsonDocument.Parse(json);
        var result = await BuildTool(withSecondSource).ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // F38：折叠行只能担保「这一类里本类型自己声明的那些」，不能承诺 the whole list
    [Fact]
    public async Task FoldLine_DoesNotPromiseAListItCannotDeliver()
    {
        var content = await Run("""{"name":"ZzDerived"}""");

        Assert.Contains("more properties", content);
        Assert.DoesNotContain("whole list", content);
        Assert.Contains("limit:'all'", content);
    }

    // F39：辖域那半句由继承链那行承担——照 limit:'all' 展开也拿不到基类成员这件事，
    // 必须在同一屏里说出来，否则折叠行读起来仍像是「全部成员」。
    [Fact]
    public async Task InheritanceChain_SaysInheritedMembersAreNotInTheOutline()
    {
        var content = await Run("""{"name":"ZzDerived"}""");

        Assert.Contains("ZzDerived <- ZzBase", content);
        Assert.Contains("not in the outline below at any limit", content);
        Assert.Contains("inspect a base name", content);
    }

    // 反向守住：limit:'all' 确实展开了本类型的全部成员，却依然没有基类那个成员。
    // 这条是上一条那句话的事实基础，两者必须一起成立。
    [Fact]
    public async Task LimitAll_ExpandsEverythingDeclaredHere_AndStillLacksTheBaseMember()
    {
        var content = await Run("""{"name":"ZzDerived","limit":"all"}""");

        Assert.Contains($"ZzProp{PropertyCount - 1:D3}", content);
        Assert.DoesNotContain("more properties", content);
        Assert.DoesNotContain("ZzOnlyOnTheBase", content);
    }

    // F44：被高优先级源盖住的那份只说「outline omitted」，等于给了个没有下文的死路。
    // 两条真的走得通的出路必须写出来。
    [Fact]
    public async Task ShadowedDeclaration_SaysHowToActuallySeeIt()
    {
        var content = await Run("""{"name":"ZzDerived","scope":"all"}""", withSecondSource: true);

        Assert.Contains("Also declared in", content);
        Assert.Contains("outline omitted", content);
        Assert.Contains("narrow scope to this source", content);
        Assert.Contains("extractClass", content);
    }
}
