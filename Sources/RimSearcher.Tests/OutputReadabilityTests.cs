using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 可读性回归。与前一轮的「说错了」不同，这里守的是「说对了但难读」：同一件事印两遍、
// 分隔线分隔的是空气、一维的链画成二维的图、同一个概念每个工具一套写法。
// 这些缺陷单看每一条都不致命，但它们按结果行数线性放大，且会把真正有信息的那几行淹掉。
// PathSecurity.Initialize 是静态状态，几个用例要用它，故与其余同类用例串行
[Collection("PathSecurity")]
public class OutputReadabilityTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private (SourceIndexer Indexer, DefIndexer Defs, ScopeCatalog Catalog) BuildIndex(
        params (string RelPath, string Body)[] files)
    {
        var root = _workspace.Dir("Core");
        foreach (var (relPath, body) in files)
            _workspace.WriteFile(Path.Combine("Core", relPath), body);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defs = new DefIndexer();
        defs.FreezeIndex();

        return (indexer, defs, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> RunAsync(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // ---- R1：结果行末尾那个能从符号名逐字推出来的文件名 ----

    // 语料实测：2610 条 locate 结果行里 2489 条（95%）的文件名就是 `<符号名>.cs`。
    // 印它等于把同一个词说两遍，还把剩下 5% 真正有信息的文件名淹了。
    [Fact]
    public async Task Locate_OmitsTheFileNameWhenItIsJustTheTypeNameDotCs()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"));

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Assert.Contains("`ZzWidget` (100%)", content);
        Assert.DoesNotContain("ZzWidget.cs", content);
    }

    // 反面：文件名推不出来时**必须**印，那才是这条信息存在的意义
    [Fact]
    public async Task Locate_KeepsTheFileNameWhenItIsNotDerivable()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzHost.cs", "namespace Zz { public class ZzGuest { } }"));

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzGuest" });

        Assert.Contains("ZzHost.cs", content);
    }

    // 成员行同理：宿主类型名已经在反引号里了，`ZzWidget.Tick` 后面再挂 ZzWidget.cs 没有增量
    [Fact]
    public async Task Locate_MemberRows_OmitTheDerivableFileName()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { public void ZzTick() { } } }"));

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "method:ZzTick" });

        Assert.Contains("ZzWidget.ZzTick", content);
        Assert.DoesNotContain("ZzWidget.cs", content);
    }

    // Files 段原先是「基名 - 全路径」，而基名逐字就是全路径的末段
    [Fact]
    public async Task Locate_FileRows_DoNotPrintTheBaseNameBesideTheFullPath()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzLoose.xml", "<Defs></Defs>"));

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzLoose" });

        var fileRows = content.Split('\n').Where(l => l.Contains("ZzLoose.xml")).ToList();
        Assert.NotEmpty(fileRows);
        foreach (var row in fileRows)
            Assert.Equal(1, row.Split("ZzLoose.xml").Length - 1);
    }

    // ---- R5：表头要说「什么 + 多少条」，与 trace / search_regex / list_directory 一致 ----

    [Fact]
    public async Task Locate_Header_StatesHowManyOfEachKind()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { public void ZzWidgetPump() { } } }"));

        var header = (await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" }))
            .Split('\n')[0];

        Assert.StartsWith("## 'ZzWidget'", header);
        Assert.Contains("1 C# type", header);
    }

    // 单数不写成 "1 C# types"——表头是每次调用都读的那一行
    [Fact]
    public async Task Locate_Header_UsesSingularForOne()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzSolo.cs", "namespace Zz { public class ZzSolo { } }"));

        var header = (await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzSolo" }))
            .Split('\n')[0];

        Assert.Contains("1 C# type", header);
        Assert.DoesNotContain("1 C# types", header);
    }

    // ---- R2：线性的基类链不画成 mermaid ----

    // C# 的基类链恒为线性。三层链的 `graph TD` 要 7 行 ~150 字符，一行式说同一件事只要 ~45。
    [Fact]
    public async Task Inspect_RendersTheBaseChainOnOneLine_NotAsAMermaidGraph()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzMid.cs", "namespace Zz { public class ZzMid : ZzBase { } }"),
            ("ZzLeaf.cs", "namespace Zz { public class ZzLeaf : ZzMid { } }"));

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzLeaf" });

        Assert.DoesNotContain("mermaid", content);
        Assert.DoesNotContain("graph TD", content);
        Assert.Contains("Inheritance chain: ZzLeaf <- ZzMid <- ZzBase", content);
    }

    // 链上每个中间类型只出现一次——(child, parent) 对直接 join 会把它们各印两遍
    [Fact]
    public async Task Inspect_ChainLine_DoesNotRepeatIntermediateTypes()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzMid.cs", "namespace Zz { public class ZzMid : ZzBase { } }"),
            ("ZzLeaf.cs", "namespace Zz { public class ZzLeaf : ZzMid { } }"));

        var line = (await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzLeaf" }))
            .Split('\n').First(l => l.StartsWith("Inheritance chain:", StringComparison.Ordinal));

        Assert.Equal(1, line.Split("ZzMid").Length - 1);
    }

    // def 模式的链早就是一行式（F14）。同一个工具的两个模式渲染同一个概念不该有两套写法。
    [Fact]
    public async Task Inspect_TypeModeAndDefMode_UseTheSameChainWording()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzLeaf.cs", "namespace Zz { public class ZzLeaf : ZzBase { } }"));

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzLeaf" });

        Assert.Contains("Inheritance chain: ", content);
    }

    // ---- R3：分隔线画在两份大纲之间，不是每份之后 ----

    // 只有一份大纲时（绝大多数调用）结尾那道 `---` 分隔的是空气，读者会读成「被截断了」。
    [Fact]
    public async Task Inspect_SingleOutline_DoesNotEndWithADanglingSeparator()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzOnly.cs", "namespace Zz { public class ZzOnly { public void Go() { } } }"));

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzOnly" });

        Assert.DoesNotContain("---", content);
        Assert.False(content.TrimEnd().EndsWith("---", StringComparison.Ordinal));
    }

    // 结尾空行同理：各工具是「表头 → 若干可选段 → 若干可选脚注」的拼装，缺段就在结尾留下
    // 一到三个空行。对 LLM 调用方那是一个「后面本来还有、被截断了」的信号，会引出多余的重查。
    [Fact]
    public async Task NoToolResponse_EndsWithBlankLines()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzOnly.cs", "namespace Zz { public class ZzOnly { public void Go() { } } }"));
        var root = _workspace.Dir("Core");
        PathSecurity.Initialize([root]);

        var responses = new[]
        {
            await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzOnly" }),
            await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzOnly" }),
            await RunAsync(new TraceTool(indexer, catalog), new { name = "ZzOnly", mode = "usages" }),
            await RunAsync(new ReadCodeTool(indexer, catalog), new { path = "ZzOnly.cs" }),
            await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzOnly" }),
            await RunAsync(new ListDirectoryTool(), new { path = root }),
        };

        foreach (var response in responses)
            Assert.Equal(response.TrimEnd(), response);
    }

    // ---- R7：表头与 **Outline** 之间的空行，有没有基类链都一样 ----

    // interface 走的是「没有基类链」那一支，原先表头下一行直接就是 **Outline**，
    // 而 class 中间隔着链那行，于是同一个工具两种间距。
    [Fact]
    public async Task Inspect_HeaderToOutlineSpacing_IsTheSameWithOrWithoutAChain()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("IZzThing.cs", "namespace Zz { public interface IZzThing { void Go(); } }"),
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzLeaf.cs", "namespace Zz { public class ZzLeaf : ZzBase { public void Go() { } } }"));

        var tool = new InspectTool(indexer, defs, catalog);

        static int BlankLinesBeforeOutline(string content)
        {
            var lines = content.Split('\n');
            var idx = Array.FindIndex(lines, l => l.StartsWith("**Outline**", StringComparison.Ordinal));
            Assert.True(idx > 0, "no **Outline** section in the response");
            var blanks = 0;
            for (var i = idx - 1; i >= 0 && lines[i].Trim().Length == 0; i--) blanks++;
            return blanks;
        }

        Assert.Equal(
            BlankLinesBeforeOutline(await RunAsync(tool, new { name = "ZzLeaf" })),
            BlankLinesBeforeOutline(await RunAsync(tool, new { name = "IZzThing" })));
    }

    // ---- 同一条查询必须给同一份答案 ----

    // 缺陷回归：正则扫描原先是整张候选表满盘并发 + 命中上限一到就从委托头部 return，于是
    // **哪些文件赶在上限前被扫到**取决于线程调度；ConcurrentBag 的枚举序又是第二层不确定。
    // 实测同一条查询连跑 6 次给出 3 种不同的文件集。返回里那句 "showing the first N" 因此没有
    // 定义——调用方复查一遍拿到另一批文件，只能推断索引变了或自己上次读错了。
    [Fact]
    public async Task SearchRegex_SameQueryTwice_ReturnsTheSameAnswer()
    {
        var files = Enumerable.Range(0, 900)
            .Select(i => ($"Zz_{i:D4}.cs", $"namespace Zz {{ public class Zz_{i:D4} {{ /* ZzNeedle */ }} }}"))
            .ToArray();
        var (indexer, _, catalog) = BuildIndex(files);

        var tool = new SearchRegexTool(indexer, catalog);
        var runs = new List<string>();
        for (var i = 0; i < 5; i++)
            runs.Add(await RunAsync(tool, new { pattern = "ZzNeedle", limit = 20 }));

        Assert.Single(runs.Distinct());
    }

    // 截断选的是候选表的**前缀**（按索引序），故放大 limit 只会补进更多文件，
    // 不会把上一次给过的换掉。原先随线程调度取任意子集时这条不成立：调用方把 limit
    // 从 5 调到 40 复查，先前那几条可能整批消失，读起来像索引在自己变。
    // 展示层另按文件名排序，所以这里断言的是集合包含而不是行序。
    [Fact]
    public async Task SearchRegex_RaisingTheLimit_OnlyAddsFiles_NeverSwapsThemOut()
    {
        var files = Enumerable.Range(0, 900)
            .Select(i => ($"Zz_{i:D4}.cs", $"namespace Zz {{ public class Zz_{i:D4} {{ /* ZzNeedle */ }} }}"))
            .ToArray();
        var (indexer, _, catalog) = BuildIndex(files);

        var small = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = 5 });
        var large = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = 40 });

        static HashSet<string> Hits(string content) => content.Split('\n')
            .Where(l => l.StartsWith("`Zz_", StringComparison.Ordinal))
            .Select(l => l.Split('`')[1]).ToHashSet();

        var smallHits = Hits(small);
        Assert.NotEmpty(smallHits);
        Assert.Subset(Hits(large), smallHits);
    }

    // 展示顺序必须是调用方能自己复现的那个（文件名序），否则「这个文件不在结果里」推不出来
    [Fact]
    public async Task SearchRegex_FilesAreListedInNameOrder()
    {
        var files = Enumerable.Range(0, 40)
            .Select(i => ($"Zz_{i:D4}.cs", $"namespace Zz {{ public class Zz_{i:D4} {{ /* ZzNeedle */ }} }}"))
            .ToArray();
        var (indexer, _, catalog) = BuildIndex(files);

        var content = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = 30 });

        var shown = content.Split('\n')
            .Where(l => l.StartsWith("`Zz_", StringComparison.Ordinal))
            .Select(l => l.Split('`')[1]).ToList();

        Assert.NotEmpty(shown);
        Assert.Equal(shown.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), shown);
    }

    // ---- R4：全服一套截断脚注文法 `... +N more <什么> (<怎么拿到>)` ----

    // 原先五个工具五种写法（`... +N more (…)` / `... [N more; …]` / `[N more lines available, …]` /
    // `[Truncated: … ]` / `... +N more X not shown (…)`），调用方每换一个工具就得重新认一次。
    [Fact]
    public async Task ReadCode_TruncationFootnote_FollowsTheSharedGrammar()
    {
        var body = new StringBuilder("namespace Zz { public class ZzBig {\n");
        for (var i = 0; i < 2500; i++) body.Append($"    public void M{i}() {{ int x = {i}; }}\n");
        body.Append("} }\n");

        var (indexer, _, catalog) = BuildIndex(("ZzBig.cs", body.ToString()));
        PathSecurity.Initialize([_workspace.Dir("Core")]);

        var content = await RunAsync(new ReadCodeTool(indexer, catalog), new { path = "ZzBig.cs" });

        AssertSharedFootnoteGrammar(content);
    }

    [Fact]
    public async Task ListDirectory_TruncationFootnote_FollowsTheSharedGrammar()
    {
        var root = _workspace.Dir("Many");
        for (var i = 0; i < 30; i++) _workspace.WriteFile(Path.Combine("Many", $"Zz_{i:D3}.cs"), "// x");
        PathSecurity.Initialize([root]);

        var content = await RunAsync(new ListDirectoryTool(), new { path = root, limit = 5 });

        AssertSharedFootnoteGrammar(content);
    }

    [Fact]
    public async Task InspectOutline_TruncationFootnote_FollowsTheSharedGrammar()
    {
        var body = new StringBuilder("namespace Zz { public class ZzWide {\n");
        for (var i = 0; i < 80; i++) body.Append($"    public void M{i}() {{ }}\n");
        body.Append("} }\n");

        var (indexer, defs, catalog) = BuildIndex(("ZzWide.cs", body.ToString()));

        var content = await RunAsync(
            new InspectTool(indexer, defs, catalog), new { name = "ZzWide", limit = 5 });

        AssertSharedFootnoteGrammar(content);
    }

    // 文法：以 `... +N more` 开头，随后一个名词说清「更多的是什么」，括号里给出下一步。
    // 方括号、"available"、"not shown"、"Truncated:" 都是被统一掉的旧写法。
    private static void AssertSharedFootnoteGrammar(string content)
    {
        var fold = content.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("... +", StringComparison.Ordinal));

        Assert.NotNull(fold);
        Assert.Contains("(", fold);
        Assert.EndsWith(")", fold.TrimEnd());
        Assert.DoesNotContain("[", fold);
        Assert.DoesNotContain("not shown", fold);
        Assert.DoesNotContain("available", fold);
        Assert.DoesNotContain("Truncated", content);
    }
}
