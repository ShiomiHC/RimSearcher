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

    // R8：trace inheritors 的行末文件名走的是与 locate 同一条判据（SymbolRow.FileNote）。
    // 真实语料实测 601 行里 589 行（98%）可推；剩下的 12 行全是嵌套类型，按外层段同样可推。
    [Fact]
    public async Task TraceInheritors_OmitsTheDerivableFileName()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzKid.cs", "namespace Zz { public class ZzKid : ZzBase { } }"));

        var content = await RunAsync(new TraceTool(indexer, catalog),
            new { symbol = "ZzBase", mode = "inheritors" });

        Assert.Contains("`Zz.ZzKid`", content);
        Assert.DoesNotContain("ZzKid.cs", content);
    }

    // 嵌套类型声明在外层类型的文件里，同样推得出来
    [Fact]
    public async Task TraceInheritors_TreatsANestedTypesOuterFileAsDerivable()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzOuter.cs", "namespace Zz { public class ZzOuter { public class ZzInner : ZzBase { } } }"));

        var content = await RunAsync(new TraceTool(indexer, catalog),
            new { symbol = "ZzBase", mode = "inheritors" });

        Assert.Contains("ZzInner", content);
        Assert.DoesNotContain("ZzOuter.cs", content);
    }

    // R10：`[C#]` / `[XML]` 前缀与紧跟其后的 .cs / .xml 后缀说的是同一件事；
    // search_regex 同样按文件分组，从来没有这个前缀。
    [Fact]
    public async Task TraceUsages_DoesNotTagFilesWithALanguageTheExtensionAlreadyGives()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzUser.cs", "namespace Zz { public class ZzUser { void Go() { ZzTarget.Run(); } } }"),
            ("ZzTarget.cs", "namespace Zz { public static class ZzTarget { public static void Run() { } } }"));

        var content = await RunAsync(new TraceTool(indexer, catalog),
            new { symbol = "ZzTarget", mode = "usages" });

        Assert.Contains("`ZzUser.cs`", content);
        Assert.DoesNotContain("[C#]", content);
        Assert.DoesNotContain("[XML]", content);
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

    // trace usages 走的是自己那套扫盘，F21 修的只是 search_regex：这里原先仍是整张文件表
    // 满盘并发抢配额，`limit:1` 返回哪个文件取决于线程调度——实测同一条查询在不同轮次里
    // 给出过 `Stance_Warmup.cs` 与 `ThingDef.cs` 两个答案，而返回里那句 "first 1" 于是没有定义。
    [Fact]
    public async Task TraceUsages_TruncatedResult_IsTheSameEveryTime()
    {
        var files = Enumerable.Range(0, 900)
            .Select(i => ($"Zz_{i:D4}.cs", $"namespace Zz {{ public class Zz_{i:D4} {{ void M() {{ ZzMark.Go(); }} }} }}"))
            .ToArray();
        var (indexer, _, catalog) = BuildIndex(files);
        var tool = new TraceTool(indexer, catalog);

        var runs = new List<string>();
        for (var i = 0; i < 5; i++)
            runs.Add(await RunAsync(tool, new { symbol = "ZzMark", mode = "usages", limit = 20 }));

        Assert.Single(runs.Distinct());
    }

    // 与 search_regex 同款：扫过的恒是文件表的前缀，故放大 limit 只补不换
    [Fact]
    public async Task TraceUsages_RaisingTheLimit_OnlyAddsFiles_NeverSwapsThemOut()
    {
        var files = Enumerable.Range(0, 900)
            .Select(i => ($"Zz_{i:D4}.cs", $"namespace Zz {{ public class Zz_{i:D4} {{ void M() {{ ZzMark.Go(); }} }} }}"))
            .ToArray();
        var (indexer, _, catalog) = BuildIndex(files);

        var small = await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages", limit = 5 });
        var large = await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages", limit = 40 });

        static HashSet<string> Hits(string content) => content.Split('\n')
            .Where(l => l.StartsWith("`Zz_", StringComparison.Ordinal))
            .Select(l => l.Split('`')[1]).ToHashSet();

        Assert.NotEmpty(Hits(small));
        Assert.Subset(Hits(large), Hits(small));
    }

    // 排序键与印出来的东西必须是同一个。两个工具都只印文件名却按完整路径排，读者看到的
    // 是「每进一个目录字母序就重来一遍」，无从判断「这个文件不在结果里」是真没有还是被截了。
    [Fact]
    public async Task GroupedHitTools_ListFilesInTheOrderTheyArePrinted()
    {
        var files = new[]
        {
            (Path.Combine("Aaa", "ZzZeta.cs"), "namespace Zz { public class ZzZeta { void M() { ZzMark.Go(); } } }"),
            (Path.Combine("Bbb", "ZzAlpha.cs"), "namespace Zz { public class ZzAlpha { void M() { ZzMark.Go(); } } }"),
        };
        var (indexer, _, catalog) = BuildIndex(files);

        static List<string> Shown(string content) => content.Split('\n')
            .Where(l => l.StartsWith('`')).Select(l => l.Split('`')[1]).ToList();

        var trace = Shown(await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" }));
        var regex = Shown(await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark" }));

        // 按完整路径排是 Aaa/ZzZeta 在前；按印出来的文件名排才是 ZzAlpha 在前
        Assert.Equal(["ZzAlpha.cs", "ZzZeta.cs"], trace);
        Assert.Equal(trace, regex);
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

    // ---- R16/R17：read_code 正文之前的位置注释 ----

    // 原先三行说同一件事：read_code 回显目标名、RoslynHelper 印 `// File: <路径>`、
    // 再印 `// Method, starts at line: N`。`path:line` 是一行就说完的通用写法。
    [Fact]
    public async Task ReadCode_Member_LeadsWithExactlyOneLocationLine()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzComp.cs", "namespace Zz\n{\n    public class ZzComp\n    {\n        public void ZzTick() { }\n    }\n}\n"));
        PathSecurity.Initialize([_workspace.Dir("Core")]);

        var content = await RunAsync(
            new ReadCodeTool(indexer, catalog), new { path = "ZzComp.cs", methodName = "ZzTick" });

        var lines = content.Split('\n');
        Assert.StartsWith("```csharp", lines[0]);
        Assert.Matches(@"^// Method ZzTick — .*ZzComp\.cs:5$", lines[1].TrimEnd());
        // 位置行只此一行：旧的三行头里那两句已经没有了
        Assert.DoesNotContain("// File:", content);
        Assert.DoesNotContain("starts at line", content);
        Assert.Equal(1, content.Split('\n').Count(l => l.StartsWith("// ", StringComparison.Ordinal)));
    }

    // extractClass 与 member 走同一条文法，且印全限定名——同名类型分属不同命名空间时，
    // 返回里必须能看出取的是哪一个。
    [Fact]
    public async Task ReadCode_ExtractClass_UsesTheSameLocationLineGrammar()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzComp.cs", "namespace Zz\n{\n    public class ZzComp\n    {\n        public void ZzTick() { }\n    }\n}\n"));
        PathSecurity.Initialize([_workspace.Dir("Core")]);

        var content = await RunAsync(
            new ReadCodeTool(indexer, catalog), new { path = "ZzComp.cs", extractClass = "ZzComp" });

        Assert.Matches(@"^// Class Zz\.ZzComp — .*ZzComp\.cs:3$", content.Split('\n')[1].TrimEnd());
        Assert.DoesNotContain("// File:", content);
        Assert.DoesNotContain("Starts at line", content);
    }

    // 多命中：每条正文之前恰好一行，`[i/n]` 同时承担分段与进度。原先的
    // `// --- NEXT MATCH ---` 只说「后面还有」，说不出还有几条，末尾又悬空。
    [Fact]
    public async Task ReadCode_MultipleMatches_ReplaceTheSeparatorWithNumberedLocationLines()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzPair.cs", "namespace Zz\n{\n    public class ZzA { public void ZzTick() { } }\n"
                          + "    public class ZzB { public void ZzTick() { } }\n}\n"));
        PathSecurity.Initialize([_workspace.Dir("Core")]);

        var content = await RunAsync(
            new ReadCodeTool(indexer, catalog), new { path = "ZzPair.cs", methodName = "ZzTick" });

        Assert.Matches(@"^// \[1/2\] Method ZzTick in Zz\.ZzA — .*ZzPair\.cs:3$",
            content.Split('\n').First(l => l.Contains("[1/2]")).TrimEnd());
        Assert.Matches(@"^// \[2/2\] Method ZzTick in Zz\.ZzB — .*ZzPair\.cs:4$",
            content.Split('\n').First(l => l.Contains("[2/2]")).TrimEnd());
        Assert.DoesNotContain("NEXT MATCH", content);
        Assert.DoesNotContain("matching members", content);
    }

    // 构造函数的名字就是它所属类型的短名，`Constructor ZzA in Zz.ZzA` 里后半截一个字都没多说
    [Fact]
    public async Task ReadCode_Constructors_DoNotRepeatTheTypeNameAsTheOwner()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzCtor.cs", "namespace Zz\n{\n    public class ZzA\n    {\n        public ZzA() { }\n"
                          + "        public ZzA(int x) { }\n    }\n}\n"));
        PathSecurity.Initialize([_workspace.Dir("Core")]);

        var content = await RunAsync(
            new ReadCodeTool(indexer, catalog), new { path = "ZzCtor.cs", methodName = ".ctor" });

        // `.ctor` 是约定写法，位置行要还原成源码里真正写着的名字，否则它在正文里找不到
        Assert.Contains("[1/2] Constructor ZzA — ", content);
        Assert.DoesNotContain(".ctor", content);
        Assert.DoesNotContain("in Zz.ZzA", content);
    }

    // ---- R19：结果全同源时，每行末尾那个一模一样的 ` [来源]` ----

    // 两个源在 scope 里，但某一段的结果全落在一个源上时，逐行标签每行都一样。
    // 实测 locate 一次 200 条的返回里 412 个标签约 4120 字，占正文 14%。
    [Fact]
    public async Task Locate_UniformSourceSection_StatesTheSourceOnceOnTheHeader()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }")],
            [("ZzUnrelated.cs", "namespace Zz { public class ZzUnrelated { } }")]);

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Assert.Contains("**C# Types** [vanilla]:", content);
        Assert.DoesNotContain("`ZzWidget` (100%) [vanilla]", content);
        // 标签是移位不是删除：读者仍要能一眼看出这批结果来自哪儿
        Assert.Contains("[vanilla]", content);
    }

    // 反面：真的混源时逐行印才是唯一说得清的写法，表头不能替它做主
    [Fact]
    public async Task Locate_MixedSourceSection_KeepsThePerRowLabels()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }")],
            [("ZzWidgetPatch.cs", "namespace Zz { public class ZzWidgetPatch { } }")]);

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Assert.Contains("**C# Types**:", content);
        Assert.Contains("[vanilla]", content);
        Assert.Contains("[Vethara]", content);
        Assert.DoesNotContain("**C# Types** [", content);
    }

    // scope 已经把源钉死时一个标签都不该出现——这条判据原先就在，别被表头那次印破坏
    [Fact]
    public async Task Locate_SingleSourceScope_PrintsNoLabelAtAll()
    {
        var (indexer, defs, catalog) = BuildIndex(("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"));

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Assert.Contains("**C# Types**:", content);
        Assert.DoesNotContain("[vanilla]", content);
    }

    // 同一条判据要覆盖所有列结果行的工具，否则「同一个概念每个工具一套写法」原样回来
    [Fact]
    public async Task TraceAndSearchRegex_AlsoHoistTheUniformSourceLabel()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzOne.cs", "namespace Zz { public class ZzOne { void M() { ZzMark.Go(); } } }")],
            [("ZzUnrelated.cs", "namespace Zz { public class ZzUnrelated { } }")]);

        var usages = await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" });
        var regex = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark" });

        Assert.Contains("[vanilla]:", usages);
        Assert.DoesNotContain("`ZzOne.cs` [vanilla]", usages);
        Assert.Contains("[vanilla]:", regex);
        Assert.DoesNotContain("`ZzOne.cs` [vanilla]", regex);
    }

    private (SourceIndexer Indexer, DefIndexer Defs, ScopeCatalog Catalog) BuildTwoSourceIndex(
        (string RelPath, string Body)[] vanilla, (string RelPath, string Body)[] vethara)
    {
        var vanillaRoot = _workspace.Dir("Vanilla");
        var vetharaRoot = _workspace.Dir("Vethara");
        foreach (var (relPath, body) in vanilla) _workspace.WriteFile(Path.Combine("Vanilla", relPath), body);
        foreach (var (relPath, body) in vethara) _workspace.WriteFile(Path.Combine("Vethara", relPath), body);

        var indexer = new SourceIndexer();
        indexer.Scan(vanillaRoot);
        indexer.Scan(vetharaRoot);
        indexer.FreezeIndex();

        var defs = new DefIndexer();
        defs.FreezeIndex();

        return (indexer, defs,
            ScopeCatalog.Build([("vanilla", vanillaRoot), ("Vethara", vetharaRoot)], null, null));
    }

    // ---- R11/R13：trace usages 与 search_regex 是同一个结构，却长着两副样子 ----

    // 两者都输出「文件名一行 + 缩进的预览行若干」。search_regex 组间空行、trace 不空，
    // 于是 trace 里上一组的最后一条预览与下一组的组名贴在一起，组的边界只剩缩进能看。
    [Fact]
    public async Task TraceUsages_SeparatesFileGroups_TheSameWaySearchRegexDoes()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzOne.cs", "namespace Zz { public class ZzOne { void M() { ZzMark.Go(); } } }"),
            ("ZzTwo.cs", "namespace Zz { public class ZzTwo { void M() { ZzMark.Go(); } } }"));

        var trace = await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" });
        var regex = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark" });

        Assert.Equal(GroupLayout(trace), GroupLayout(regex));
        // 具体是哪一种：组名之前恒有一个空行（首组除外，表头后面本就空着）
        Assert.Contains("\n\n`ZzTwo.cs`", trace);
    }

    // 同一个事件（预览行扫到上限、扫描就地停下）原先两个工具各写一句：
    // `[Preview lines truncated at limit 1 and scanning stopped there, raise limit …]` 对
    // `[scanning stopped at the 1-preview cap — pass limit:'all' …]`。
    [Fact]
    public async Task TraceAndSearchRegex_ReportTheSameScanStopWithTheSameSentence()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzOne.cs", "namespace Zz { public class ZzOne { void M() { ZzMark.Go(); ZzMark.Go(); } } }"),
            ("ZzTwo.cs", "namespace Zz { public class ZzTwo { void M() { ZzMark.Go(); ZzMark.Go(); } } }"));

        var trace = await RunAsync(
            new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages", limit = 1 });
        var regex = await RunAsync(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark", limit = 1 });

        var expected = "... more matches exist (scan stopped at the 1-preview cap; "
                       + "pass limit:'all' to raise the cap to 200, or narrow the query or the scope)";
        Assert.Contains(expected, trace);
        Assert.Contains(expected, regex);

        // 方括号是被统一掉的旧写法——其余各工具的折叠脚注早已不用它
        Assert.DoesNotContain("[Preview lines", trace);
        Assert.DoesNotContain("[scanning stopped", regex);
    }

    // ---- R24：inspect 大纲逐行的种类前缀 ----

    // 三类成员本就是分块连续印的，每行再挂一次 `Property: ` / `Field: ` / `Method: ` 是把
    // 表头说过的话在下面每一行重说（与 enum 的 `Value: ` 同型）。locate 的 Members 段一直
    // 就是「组表头 + 裸行」，两处至此同形。
    [Fact]
    public async Task InspectOutline_NamesEachKindOnceAsAGroupHeader()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzBox.cs", "namespace Zz\n{\n    public class ZzBox\n    {\n"
                         + "        public int ZzProp { get; set; }\n"
                         + "        public string zzField;\n"
                         + "        public void ZzGo() { }\n    }\n}\n"));

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzBox" });

        Assert.Contains("\n  Properties:\n    public int ZzProp", content);
        Assert.Contains("\n  Fields:\n    public string zzField", content);
        Assert.Contains("\n  Methods:\n    public void ZzGo()", content);
        Assert.DoesNotContain("  Property: ", content);
        Assert.DoesNotContain("  Field: ", content);
        Assert.DoesNotContain("  Method: ", content);
    }

    // ---- R25：折叠行的名词槽 ----

    // 全服文法是 `... +N more <什么> (<怎么拿到>)`，而 locate 五段一直把 <什么> 留空。
    // Members 段最要命：它是唯一有种类子组的段，折叠行又与组内条目同缩进，于是
    // `... +1938 more` 紧跟在 Properties 组末尾时读起来像「还有 1938 个 property」，
    // 而它数的是 method/property/field 三类之和。
    [Fact]
    public async Task Locate_FoldLines_NameWhatTheMoreIs()
    {
        var files = Enumerable.Range(0, 40)
            .Select(i => ($"ZzThing{i}.cs",
                $"namespace Zz {{ public class ZzThing{i} {{ public int ZzThingField{i}; }} }}"))
            .ToArray();
        var (indexer, defs, catalog) = BuildIndex(files);

        var content = await RunAsync(new LocateTool(indexer, defs, catalog), new { query = "ZzThing" });

        foreach (var fold in content.Split('\n').Where(l => l.TrimStart().StartsWith("... +")))
            Assert.Matches(@"^\s*\.\.\. \+\d+ more [a-zA-Z# ]+ \(", fold);
        Assert.Contains("more C# types", content);
        // 三类之和，故是 members 而不是紧邻其上那个子组的种类
        Assert.Contains("more members", content);
    }

    // ---- F23：trace usages 的静默削减 ----

    // 同一份命中集，search_regex 附「有文件没扫全」尾注而 trace usages 不附。调用方从
    // search_regex 学到的是「没有尾注即完整命中集」，套到 trace 上就会把一份漏了内容的
    // 结果当成穷尽结论。两处的削减是一模一样的两条：单文件行闸、读不开就跳过。
    [Fact]
    public async Task TraceUsages_ReportsUnreadableFiles_TheSameWaySearchRegexDoes()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzOne.cs", "namespace Zz { public class ZzOne { void M() { ZzMark.Go(); } } }"),
            ("ZzLocked.cs", "namespace Zz { public class ZzLocked { void M() { ZzMark.Go(); } } }"));

        var locked = Directory.GetFiles(_workspace.Root, "ZzLocked.cs", SearchOption.AllDirectories)[0];
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var trace = await RunAsync(
                new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" });
            var regex = await RunAsync(
                new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark" });

            const string expected = "... some files were not scanned in full (1 file could not be read "
                                    + "and was skipped entirely; matches in the unscanned parts would not be listed)";
            Assert.Contains(expected, trace);
            Assert.Contains(expected, regex);

            // 表头同时改口：有文件没扫全时那个总数只是下界，不能再说 "N found"。
            // 名词也补上（R30）：数的是命中行不是命中次数，且 N=1 时收单数。
            Assert.Contains("at least 1 matching line in scope", trace);
            Assert.DoesNotContain("1 found in scope", trace);
            Assert.DoesNotContain("1 matching lines", trace);
        }
    }

    // ---- R26：inspect def 头部的 `C# Class:` 行 ----

    // DefType 就是 def 的 C# 类名，`C# Class:` 行在文件名可推时整行零新增事实——
    // 同一个词在相邻两行里说了两遍。合并进 Type 行之后三态仍各自可分：可推 → 光名字，
    // 不可推/多文件 → 带文件注，类没进索引 → 明说（原先靠整行缺席表达，得先知道规则才读得出来）。
    [Fact]
    public async Task InspectDef_FoldsTheClassLineIntoTheTypeLine()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzGadgetDef.cs"),
            "namespace Zz { public class ZzGadgetDef { } }");
        _workspace.WriteFile(Path.Combine("Core", "Gadgets.xml"),
            "<Defs>\n  <ZzGadgetDef>\n    <defName>ZzWidget</defName>\n  </ZzGadgetDef>\n</Defs>\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var defs = new DefIndexer();
        defs.Scan(root);
        defs.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzWidget" });

        Assert.Contains("Type: ZzGadgetDef", content);
        Assert.DoesNotContain("C# Class:", content);
    }

    // 类没进索引时不再靠「整行缺席」表达
    [Fact]
    public async Task InspectDef_SaysSoWhenTheDefTypeClassIsNotIndexed()
    {
        var root = _workspace.Dir("Defs");
        _workspace.WriteFile(Path.Combine("Defs", "Gadgets.xml"),
            "<Defs>\n  <ZzUnindexedDef>\n    <defName>ZzWidget</defName>\n  </ZzUnindexedDef>\n</Defs>\n");

        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defs = new DefIndexer();
        defs.Scan(root);
        defs.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var content = await RunAsync(new InspectTool(indexer, defs, catalog), new { name = "ZzWidget" });

        Assert.Contains("Type: ZzUnindexedDef (C# class not indexed)", content);
    }

    // 组名行与预览行的排布骨架，逐字内容无关
    // ---- R29：每文件折叠行的两个空槽 ----

    // `... +N more in this file` 是全语料里出现最频的一条折叠行（92/181），也是唯一
    // 名词槽与提示槽都空着的一条。名词非补不可：两个工具数的都是 `regex.IsMatch(line)`
    // 逐行累加，一行里命中两次仍只算一行，不写名词读者会按「命中次数」读。
    [Fact]
    public async Task PerFileFold_NamesWhatItCounts_InBothScanningTools()
    {
        var body = string.Join("\n", Enumerable.Range(0, 9).Select(i => $"    // ZzMark {i}"));
        var (indexer, defs, catalog) = BuildIndex(("ZzDense.cs", $"namespace Zz {{ public class ZzDense {{\n{body}\n}} }}"));

        var regex = await RunAsync(new SearchRegexTool(indexer, catalog), new { pattern = "ZzMark" });
        var trace = await RunAsync(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" });

        foreach (var content in new[] { regex, trace })
        {
            Assert.Contains("more matching lines in this file", content);
            Assert.DoesNotContain("more in this file", content);
        }
    }

    // 其余每一种折叠行都以 `(pass … )` 之类的下一步收尾，于是这条把提示槽留空会被读成
    // 「漏印了参数名」。而每文件预览条数是常数、没有参数放得宽——这件事推不出来，必须明说；
    // 且按 R19 的判据只在整份返回里说一次，不逐文件重复。
    [Fact]
    public async Task PerFilePreviewCap_IsStatedOnce_AndOnlyWhenSomethingWasFolded()
    {
        var body = string.Join("\n", Enumerable.Range(0, 9).Select(i => $"    // ZzMark {i}"));
        var (dense, defs, catalog) = BuildIndex(("ZzDense.cs", $"namespace Zz {{ public class ZzDense {{\n{body}\n}} }}"));

        var folded = await RunAsync(new SearchRegexTool(dense, catalog), new { pattern = "ZzMark" });
        Assert.Equal(1, CountOccurrences(folded, "no parameter widens that"));
        Assert.Contains("read_code", folded);

        // 没有任何文件被折叠时这条整句不出现——「没有这行」即「每个文件都印全了」。
        // 同一份索引换一条只命中一行的 pattern，把「有没有折叠」隔离成唯一变量。
        var whole = await RunAsync(new SearchRegexTool(dense, catalog), new { pattern = "class ZzDense" });
        Assert.DoesNotContain("no parameter widens that", whole);
        Assert.DoesNotContain("more matching lines in this file", whole);
    }

    // ---- R30：名词槽的单复数 ----

    // R5 已经为 locate 表头定过「不写 `1 C# types`」，而折叠行与 FoundCount 一直漏着。
    // 全语料里 `... +1 more C# types` 出现在 locate / inspect / trace 三个工具上。
    [Theory]
    [InlineData(1, "C# type")]
    [InlineData(2, "C# types")]
    public void FoldLine_AgreesInNumberWithItsCount(int hidden, string expected)
    {
        var fold = ScopeArgs.FoldLine(hidden, 1, false, true, "C# types");

        Assert.NotNull(fold);
        Assert.Contains($"+{hidden} more {expected} (", fold);
    }

    // 裸去 's' 会写出 entrie / content matche / propertie
    [Theory]
    [InlineData("entries", "entry")]
    [InlineData("content matches", "content match")]
    [InlineData("properties", "property")]
    [InlineData("subclasses", "subclass")]
    [InlineData("matching lines", "matching line")]
    [InlineData("XML defs", "XML def")]
    [InlineData("matching files", "matching file")]
    public void Singular_IsBuiltBackFromEachPluralActuallyInUse(string plural, string singular)
        => Assert.Equal($"1 {singular}", OutputText.Quantity(1, plural));

    // R30 的第二半：`(s)` 那类偷懒写法。它比 `1 C# types` 更难看出来，因为读者会自动补全，
    // 但补全不了动词——`1 file(s) were only scanned` 展开成单数就是病句。故 N=1 的两条尾注
    // 各钉一遍：名词收单数，跟着它的那个动词也得收。
    [Fact]
    public async Task ScanTailNote_TakesASingularVerbForOneFile()
    {
        var root = _workspace.Dir("Big");
        var lines = Enumerable.Range(0, 25_000).Select(i => i == 0 ? "// ZzNeedle" : "// filler");
        _workspace.WriteFile(Path.Combine("Big", "ZzHuge.cs"), string.Join("\n", lines));

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await RunAsync(tool, new { pattern = "ZzNeedle" });

        Assert.Contains("1 file was only scanned to line", content);
        Assert.DoesNotContain("1 files", content);
        Assert.DoesNotContain("file(s)", content);
    }

    // trace inheritors 表头的 `deepest N level(s) down`：N=1 是最常见的一档
    // （被查的类只有直接子类时恒是 1），却恰好是 `(s)` 读起来最别扭的那一档。
    [Fact]
    public async Task TraceInheritorsHeader_SaysOneLevelNotOneLevels()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzRoot.cs", "namespace Zz { public class ZzRoot { } }"),
            ("ZzOnlyChild.cs", "namespace Zz { public class ZzOnlyChild : ZzRoot { } }"));

        var content = await RunAsync(
            new TraceTool(indexer, catalog), new { symbol = "ZzRoot", mode = "inheritors" });

        Assert.Contains("deepest 1 level down", content);
        Assert.DoesNotContain("1 levels", content);
        Assert.DoesNotContain("level(s)", content);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- F25：locate 的同分并列结果在进程之间不可复现 ----

    // F21 修的是 search_regex、F22 修的是 trace usages，而 locate——三个工具里被调用得最多的
    // 那个——一直漏着。分数与名字长度并列之后，次序落回 matchedMembers / def 索引的枚举顺序，
    // 而它跟着索引期的并发写入走。实测两轮全量转储之间，`method:CompTick` 的前十条整批换过，
    // `Vethara_Head_0` 换成了 `Vethara_Head_3`——调用方据此得出的「这个符号不在结果里」不可复现。
    //
    // 这里不靠「重跑五次」断言（同一进程内枚举顺序本就稳定，复现不出来），而是直接断言
    // 并列组按声明的末级键有序——那才是与插入顺序无关的不变量。
    [Fact]
    public async Task Locate_TiedMembers_ComeBackInADeclaredOrder()
    {
        // 30 个同名同长的成员：分数、名字长度两级全并列，只剩末级键定序
        var files = Enumerable.Range(0, 30)
            .Select(i => ($"ZzHost{i:D2}.cs",
                $"namespace Zz {{ public class ZzHost{i:D2} {{ public void ZzTick() {{ }} }} }}"))
            .ToArray();
        var (indexer, defs, catalog) = BuildIndex(files);

        var content = await RunAsync(
            new LocateTool(indexer, defs, catalog), new { query = "method:ZzTick", limit = "all" });

        var hosts = content.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"`Zz\.(ZzHost\d+)\.ZzTick`"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(30, hosts.Count);
        Assert.Equal(hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList(), hosts);
    }

    // def 侧同理：`Vethara_Head_0` 与 `Vethara_Head_3` 同分同长，谁进前十全看 def 索引写入顺序
    [Fact]
    public async Task Locate_TiedDefs_ComeBackInADeclaredOrder()
    {
        var root = _workspace.Dir("Defs");
        var body = string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"<ZzGadgetDef><defName>ZzGadget_{i:D2}</defName><label>zz</label></ZzGadgetDef>"));
        _workspace.WriteFile(Path.Combine("Defs", "ZzGadgets.xml"), $"<Defs>{body}</Defs>");

        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var defs = new DefIndexer();
        defs.Scan(root);
        defs.FreezeIndex();

        var content = await RunAsync(
            new LocateTool(indexer, defs, ScopeCatalog.Build([("vanilla", root)], null, null)),
            new { query = "ZzGadget", limit = "all" });

        var names = content.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"`(ZzGadget_\d+)`"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(20, names.Count);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    private static string GroupLayout(string content) =>
        string.Concat(content.Split('\n').SkipWhile(l => !l.StartsWith('`')).Select(l =>
            l.Length == 0 ? "_" : l.StartsWith('`') ? "G" : l.StartsWith("  ") ? "p" : "?"));

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
