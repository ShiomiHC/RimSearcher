using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 三处此前没有体积上限的输出。inspect 的 C# 大纲是 locate 之后的必经一站，成员数以百计的
// 巨型类型每次查询都要全量渲染一遍，同名类型散在多个源里时还会按文件数线性翻倍；read_code
// 的 extractClass 提一个几千行的类就返回几千行；search_regex 的预览行整条链路一次都没截，
// 而 ScopeArgs.HardLimit 的体积账正是按「每行 ≤100 字符」算出来的。
//
// 三处都按「指回按名精取」的方向收口，而不是默默砍掉尾巴——只截不说，调用方会把半份结果
// 当成全部。
[Collection("PathSecurity")]
public class OutputVolumeCapTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public OutputVolumeCapTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    private static string TypeWithMembers(string typeName, int perKind)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace RimWorld {{ public class {typeName} {{");
        for (int i = 0; i < perKind; i++) sb.AppendLine($"    public int Prop{i} {{ get; set; }}");
        for (int i = 0; i < perKind; i++) sb.AppendLine($"    public string field{i};");
        for (int i = 0; i < perKind; i++) sb.AppendLine($"    public void Method{i}(int a) {{ }}");
        sb.AppendLine("} }");
        return sb.ToString();
    }

    private static int CountLines(string text, string prefix)
        => text.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    // 配额按类别各给一份，而不是一个总数顺序截断：一个有两百个字段的类会把 Method 整段挤掉，
    // 而方法签名恰恰是大纲最常被用到的部分（照着写调用、写 Harmony patch）。
    [Fact]
    public async Task Outline_CapsEachMemberKindIndependently()
    {
        var path = _workspace.WriteFile(Path.Combine("Core", "Huge.cs"), TypeWithMembers("Huge", 60));

        var outline = await RoslynHelper.GetClassOutlineAsync(path, "Huge");

        Assert.True(outline.IsOk);
        var cap = RoslynHelper.DefaultMaxOutlineMembersPerKind;
        Assert.Equal(cap, CountLines(outline.Content, "  Property: "));
        Assert.Equal(cap, CountLines(outline.Content, "  Field: "));
        Assert.Equal(cap, CountLines(outline.Content, "  Method: "));

        // 三类各自报出自己还剩多少，而不是合并成一条含混的总数
        Assert.Contains($"+{60 - cap} more properties", outline.Content);
        Assert.Contains($"+{60 - cap} more fields", outline.Content);
        Assert.Contains($"+{60 - cap} more methods", outline.Content);
    }

    // 折叠行必须给出下一步。只写 +N 的话，调用方唯一想得到的动作是把整个文件读出来——
    // 那正是大纲想省掉的开销。给的下一步还必须真的走得通：原先指的 locate（要先知道名字）
    // 与 read_code extractClass（2000 行二次截断）在触发折叠的大类型上都取不到被折叠的成员。
    [Fact]
    public async Task Outline_FoldLineNamesTheWayToGetTheRest()
    {
        var path = _workspace.WriteFile(Path.Combine("Core", "Huge.cs"), TypeWithMembers("Huge", 60));

        var outline = await RoslynHelper.GetClassOutlineAsync(path, "Huge");

        Assert.Contains("limit:'all'", outline.Content);
        Assert.DoesNotContain("extractClass", outline.Content);
    }

    // 反向保险：寻常大小的类型一条都不能少，也不该出现任何折叠痕迹
    [Fact]
    public async Task Outline_SmallTypeIsNotFolded()
    {
        var path = _workspace.WriteFile(Path.Combine("Core", "Small.cs"), TypeWithMembers("Small", 3));

        var outline = await RoslynHelper.GetClassOutlineAsync(path, "Small");

        Assert.Equal(3, CountLines(outline.Content, "  Method: "));
        Assert.DoesNotContain("not shown", outline.Content);
    }

    // 同名类型在 vanilla 与各 mod 里各有一份是常态。几份大纲通常高度重合，而体积是实打实
    // 地按文件数翻倍，所以第二份起只报路径。
    [Fact]
    public async Task Inspect_SecondDeclarationReportsPathWithoutOutline()
    {
        var coreRoot = _workspace.Dir("Core");
        _workspace.WriteFile(
            Path.Combine("Core", "CompShield.cs"),
            "namespace RimWorld { public class CompShield { public void CoreOnly() { } } }\n");

        var modRoot = _workspace.Dir("Mod");
        _workspace.WriteFile(
            Path.Combine("Mod", "CompShield.cs"),
            "namespace RimWorld { public class CompShield { public void ModOnly() { } } }\n");

        var indexer = new SourceIndexer();
        indexer.Scan(coreRoot);
        indexer.Scan(modRoot);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.Scan(_workspace.Dir("Defs"));
        defIndexer.FreezeIndex();

        var tool = new InspectTool(
            indexer, defIndexer, ScopeCatalog.Build([("vanilla", coreRoot), ("mod", modRoot)], null, null));

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { name = "CompShield" }));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("**Outline**", result.Content);
        Assert.Contains("Also declared in", result.Content);
        Assert.Contains("outline omitted", result.Content);

        // 两份大纲都渲染的话，两个独有方法会同时出现
        Assert.False(
            result.Content.Contains("CoreOnly") && result.Content.Contains("ModOnly"),
            "only the highest-priority declaration should be outlined");
    }

    // extractClass 提的是整个类型的实现体，反编译产物里这动辄几千行，一次就能吃掉整个上下文预算
    [Fact]
    public async Task ExtractClass_TruncatesAndPointsAtMethodName()
    {
        var root = _workspace.Dir("Core");

        var body = new StringBuilder();
        body.AppendLine("namespace RimWorld { public class Giant {");
        for (int i = 0; i < 2500; i++) body.AppendLine($"    public void M{i}() {{ int x = {i}; }}");
        body.AppendLine("} }");
        _workspace.WriteFile(Path.Combine("Core", "Giant.cs"), body.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        PathSecurity.Initialize([root]);

        var tool = new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        using var args = JsonDocument.Parse(
            JsonSerializer.Serialize(new { path = "Giant.cs", extractClass = "Giant" }));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Truncated", result.Content);
        Assert.Contains("methodName", result.Content);

        // 截断说明必须落在围栏之外：混进 ``` 块里就成了源码的一部分，整块复制出去编译不过
        Assert.EndsWith("]", result.Content.TrimEnd());
    }

    private async Task<ToolResult> SearchRegex(string fileName, string content, string pattern)
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", fileName), content);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { pattern }));
        return await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    // XML 里一行写完的 <li> 列表、反编译产物里的长泛型签名都能把单行拉到几百字符。
    // 不截的话 150 行预览就是 HardLimit 那笔体积账的几倍，且随 pattern 与 scope 浮动。
    [Fact]
    public async Task SearchRegex_LongPreviewLineIsTruncated()
    {
        var longLine = "public void Marker(" + new string('x', 400) + ") { }";
        var result = await SearchRegex(
            "Long.cs", $"namespace RimWorld {{ public class Long {{\n    {longLine}\n}} }}\n", "Marker");

        Assert.False(result.IsError);
        Assert.Contains("Marker", result.Content);
        Assert.Contains("...", result.Content);

        // 与 trace usages 同一个数：截到 97 字符再补 "..."，整行预览正好 100
        var previewLine = result.Content
            .Split('\n')
            .First(line => line.TrimStart().StartsWith("L", StringComparison.Ordinal) && line.Contains("Marker"));
        var preview = previewLine[(previewLine.IndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd();
        Assert.Equal(100, preview.Length);
        Assert.EndsWith("...", preview);

        // 整条原始长行一个字都不该漏出去
        Assert.DoesNotContain(new string('x', 200), result.Content);
    }

    // 反向保险：寻常长度的行原样给出，末尾不该凭空多出省略号
    [Fact]
    public async Task SearchRegex_ShortPreviewLineIsUntouched()
    {
        var result = await SearchRegex(
            "Short.cs", "namespace RimWorld { public class Short {\n    public void Marker() { }\n} }\n", "Marker");

        Assert.False(result.IsError);
        Assert.Contains("public void Marker() { }", result.Content);
        Assert.DoesNotContain("...", result.Content);
    }

    // 反向保险：寻常大小的类不该被截，也不该多出那句说明
    [Fact]
    public async Task ExtractClass_SmallClassIsReturnedWhole()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(
            Path.Combine("Core", "CompShield.cs"),
            "namespace RimWorld { public class CompShield { public void CompTick() { } } }\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        PathSecurity.Initialize([root]);

        var tool = new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        using var args = JsonDocument.Parse(
            JsonSerializer.Serialize(new { path = "CompShield.cs", extractClass = "CompShield" }));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("CompTick", result.Content);
        Assert.DoesNotContain("Truncated", result.Content);
    }
}
