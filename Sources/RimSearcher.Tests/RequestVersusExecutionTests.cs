using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 第十三轮：请求与实际执行之间的差。六个工具里有一批参数在特定模式下被静默忽略或择一，
// 而这件事此前只写在 tools/list 的参数说明里，返回里一个字都没有。
// 第十二轮立的判据是「一句话的辖域是本次调用还是这台机器，必须由这句话自己说出来」；
// 这一批是它在**参数**上的对应物——服务端做了与请求不同的事，返回得说得出来。
//
// 加字一律**条件触发**：没多传参数的调用一个字都不该多出来（R19）。每条正例都配一条
// 「不许改口」的反例，这是本轮的主要回归风险。
//
// 几个用例要 PathSecurity.Initialize，那是进程级静态；不并入这个 collection 的话，
// 会把并行跑的 SyncSources* 的白名单冲掉，表现为间歇性失败。
[Collection("PathSecurity")]
public class RequestVersusExecutionTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // ── read_code：三模互斥的静默择一 ────────────────────────────────

    private ReadCodeTool ReadCodeTool()
    {
        var root = _workspace.Dir("Core");

        var sb = new StringBuilder();
        sb.AppendLine("namespace Zz");
        sb.AppendLine("{");
        sb.AppendLine("    public class ZzWide");
        sb.AppendLine("    {");
        // 刻意写成多行：单行成员的起止行相同，那时不印区间才是对的（见下面两条用例）
        sb.AppendLine("        public void ZzTarget()");
        sb.AppendLine("        {");
        sb.AppendLine("            var zz = 1;");
        sb.AppendLine("        }");
        // 类体行数压过 read_code 的 2000 行上限，逼出截断脚注
        for (var i = 0; i < 2200; i++) sb.AppendLine($"        public int ZzProp{i:D4} {{ get; set; }}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        _workspace.WriteFile(Path.Combine("Core", "ZzWide.cs"), sb.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([root]);

        return new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(ITool tool, object arguments)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // 拿回的不是「少了点什么」而是**完全另一块代码**：调用方要的成员可能根本不在
    // 被交付的那 2000 行里。首行注释在陈述交付物，不是在报告丢弃。
    [Fact]
    public async Task ExtractClass_NamesTheParametersItOutranked()
    {
        var content = await Run(ReadCodeTool(),
            new { path = "ZzWide.cs", extractClass = "ZzWide", methodName = "ZzTarget", startLine = 100 });

        Assert.Contains("'extractClass' takes precedence", content);
        Assert.Contains("methodName:'ZzTarget'", content);
        Assert.Contains("startLine/lineCount", content);
        Assert.Contains("were not applied", content);
    }

    [Fact]
    public async Task MethodName_NamesTheLineRangeItOutranked()
    {
        var content = await Run(ReadCodeTool(),
            new { path = "ZzWide.cs", methodName = "ZzTarget", startLine = 100, lineCount = 5 });

        Assert.Contains("'methodName' takes precedence", content);
        Assert.Contains("startLine/lineCount was not applied", content);
    }

    // 反例：只传一条模式的调用一个字都不该多出来
    [Fact]
    public async Task SingleMode_SaysNothingAboutPrecedence()
    {
        var content = await Run(ReadCodeTool(), new { path = "ZzWide.cs", extractClass = "ZzWide" });

        Assert.DoesNotContain("takes precedence", content);
        Assert.DoesNotContain("not applied", content);
    }

    // 脚注数的是**类体自己的行**，而同一工具的裸行模式对同一个文件报的是文件行数。
    // 两个数同源不同量纲，且都不说自己量的是什么——出题的主会话自己就读错过一次。
    [Fact]
    public async Task ExtractClassTruncation_SaysWhatItsLineCountMeasures()
    {
        var content = await Run(ReadCodeTool(), new { path = "ZzWide.cs", extractClass = "ZzWide" });

        Assert.Contains("more lines", content);
        Assert.Contains("-line file", content);
    }

    // 调用方已经传了 methodName、被 extractClass 静默压掉时，原先照样劝他「pass methodName」
    // ——照做拿回逐字相同的返回。一条会把人绕回原地的建议。
    [Fact]
    public async Task ExtractClassTruncation_DoesNotAdviseTheParameterItJustDropped()
    {
        var content = await Run(ReadCodeTool(),
            new { path = "ZzWide.cs", extractClass = "ZzWide", methodName = "ZzTarget" });

        Assert.Contains("drop extractClass to get just 'ZzTarget'", content);
        Assert.DoesNotContain("pass methodName for one member", content);
    }

    [Fact]
    public async Task ExtractClassTruncation_StillAdvisesMethodName_WhenNoneWasPassed()
    {
        var content = await Run(ReadCodeTool(), new { path = "ZzWide.cs", extractClass = "ZzWide" });

        Assert.Contains("pass methodName for one member", content);
    }

    // 「这段代码在第几行到第几行」是本工具最常被追问的一件事，而唯一直接回答它的模式
    // 恰好是唯一不报区间的那个。起止行在同一个 LineSpan 上，零成本。
    [Fact]
    public async Task MemberLocationLine_GivesTheWholeRange_NotJustTheStart()
    {
        var content = await Run(ReadCodeTool(), new { path = "ZzWide.cs", methodName = "ZzTarget" });

        Assert.Matches(@"ZzWide\.cs:\d+-\d+", content);
    }

    // 起止行相同的成员不印区间——`:12-12` 是纯噪音，且会让「有横杠 = 跨多行」失效
    [Fact]
    public async Task MemberLocationLine_PrintsNoRange_WhenTheMemberIsOneLine()
    {
        var root = _workspace.Dir("OneLiner");
        _workspace.WriteFile(Path.Combine("OneLiner", "ZzTiny.cs"),
            "namespace Zz\n{\n    public class ZzTiny\n    {\n        public int ZzOne => 1;\n    }\n}\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        PathSecurity.ResetForTests();
        PathSecurity.Initialize([root]);

        var content = await Run(
            new ReadCodeTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null)),
            new { path = "ZzTiny.cs", methodName = "ZzOne" });

        Assert.Matches(@"ZzTiny\.cs:\d+\b", content);
        Assert.DoesNotMatch(@"ZzTiny\.cs:\d+-", content);
    }

    // ── inspect：def 模式忽略 limit、且无条件压过同名 C# 类型 ─────────

    private InspectTool InspectTool()
    {
        var defRoot = _workspace.Dir("Defs");
        _workspace.WriteFile(Path.Combine("Defs", "Things.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>ZzTwin</defName>\n    <label>zz twin</label>\n"
            + "  </ThingDef>\n</Defs>\n");

        var csRoot = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzTwin.cs"),
            "namespace Zz\n{\n    public class ZzTwin\n    {\n        public int ZzField;\n    }\n}\n");
        _workspace.WriteFile(Path.Combine("Core", "ZzSolo.cs"),
            "namespace Zz\n{\n    public class ZzSolo\n    {\n        public int ZzField;\n    }\n}\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(defRoot);
        defIndexer.FreezeIndex();

        var indexer = new SourceIndexer();
        indexer.Scan(csRoot);
        indexer.FreezeIndex();

        return new InspectTool(indexer, defIndexer,
            ScopeCatalog.Build([("vanilla", defRoot), ("vanilla", csRoot)], null, null));
    }

    // def 与 C# 类型撞名时 def 无条件胜出并 return，类型索引从没被查过。而整份返回里唯一的
    // 同名披露只枚举 def——盲测里被测方据此把这份沉默当成了「不存在同名 C# 类型」的证据。
    [Fact]
    public async Task DefMode_NamesTheSameNamedCSharpTypeItDidNotShow()
    {
        var content = await Run(InspectTool(), new { name = "ZzTwin" });

        Assert.Contains("## Def: ZzTwin", content);
        Assert.Contains("a C# type named 'ZzTwin' also exists", content);
        Assert.Contains("resolves def before type", content);
        // 出路得自带参数值：ZzTwin 的文件名从类型名就推得出来，于是 FileNote 返回空串，
        // 一句「on that path」会指向前面根本没印出来的东西。
        Assert.Contains("read_code path:'ZzTwin.cs' extractClass:'ZzTwin'", content);
    }

    // 反例——**本轮最重要的一条**：查不到同名类型时一个字都不印。
    // 无条件挂一句「本次按 def 解析」是常亮噪音，而那时的沉默才真正代表「没有」。
    [Fact]
    public async Task DefMode_SaysNothing_WhenNoSameNamedTypeExists()
    {
        var defRoot = _workspace.Dir("OnlyDefs");
        _workspace.WriteFile(Path.Combine("OnlyDefs", "Things.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>ZzLonely</defName>\n    <label>zz</label>\n"
            + "  </ThingDef>\n</Defs>\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(defRoot);
        defIndexer.FreezeIndex();
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();

        var tool = new InspectTool(indexer, defIndexer,
            ScopeCatalog.Build([("vanilla", defRoot)], null, null));

        var content = await Run(tool, new { name = "ZzLonely" });

        Assert.DoesNotContain("also exists", content);
        Assert.DoesNotContain("resolves def before type", content);
    }

    // limit 在 def 模式从不被读，而调用方传它时指望的正是「别截断」——def 模式确实会截断，
    // 只是换了个参数。指望的那件事恰好是任务成败的关键，故值得加字。
    [Fact]
    public async Task DefMode_SaysLimitWasIgnored_WhenItWasPassed()
    {
        var content = await Run(InspectTool(), new { name = "ZzTwin", limit = "all" });

        Assert.Contains("'limit' applies to the C# type outline only and was ignored here", content);
        Assert.Contains("xmlStartLine", content);
    }

    [Fact]
    public async Task DefMode_SaysNothingAboutLimit_WhenItWasNotPassed()
    {
        var content = await Run(InspectTool(), new { name = "ZzTwin" });

        Assert.DoesNotContain("was ignored here", content);
    }

    // F39 给类型模式的同名行补了辖域，而 def 模式的同名行语义正好相反（下面那份 XML 恰恰是
    // 合并过的）却仍是裸的。两处同形而义反，第十二轮只修了一半。
    [Fact]
    public async Task DefMode_InheritanceChainSaysTheXmlBelowIsTheMerge()
    {
        var root = _workspace.Dir("Chain");
        _workspace.WriteFile(Path.Combine("Chain", "Defs.xml"),
            "<Defs>\n  <ThingDef Name=\"ZzBaseThing\" Abstract=\"True\">\n    <label>base</label>\n"
            + "  </ThingDef>\n  <ThingDef ParentName=\"ZzBaseThing\">\n    <defName>ZzChild</defName>\n"
            + "  </ThingDef>\n</Defs>\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        defIndexer.FreezeIndex();
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();

        var content = await Run(
            new InspectTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null)),
            new { name = "ZzChild" });

        Assert.Contains("Inheritance chain:", content);
        Assert.Contains("the XML below is these merged together", content);
        Assert.Contains("not the content of the `File:` path above", content);
    }

    // 没有父链就没有合并，那时印这句是假陈述
    [Fact]
    public async Task DefMode_SaysNothingAboutMerging_WhenThereIsNoChain()
    {
        var content = await Run(InspectTool(), new { name = "ZzTwin" });

        Assert.DoesNotContain("merged together", content);
    }

    // ── search_regex：fileFilter 的回显 ──────────────────────────────

    private SearchRegexTool SearchTool()
    {
        var root = _workspace.Dir("Src");
        _workspace.WriteFile(Path.Combine("Src", "ZzOne.cs"), "class ZzOne { } // ZzNeedle\n");
        _workspace.WriteFile(Path.Combine("Src", "ZzTwo.xml"), "<Defs><!-- ZzNeedle --></Defs>\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([root]);

        return new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // 服务端确实照做了，问题是调用方没有观察点——而 scope 与 casing 都被回显这件事
    // 反过来教出「没回显 = 没生效」。三个参数要么都回显，不该只差这一个。
    [Fact]
    public async Task SuccessHeader_EchoesTheFileFilterThatWasApplied()
    {
        var content = await Run(SearchTool(), new { pattern = "ZzNeedle", fileFilter = ".cs" });

        Assert.Contains("files filtered to '.cs'", content);
        Assert.Contains("ZzOne.cs", content);
        Assert.DoesNotContain("ZzTwo.xml", content);
    }

    [Fact]
    public async Task SuccessHeader_SaysNothingAboutFiltering_WhenNoneWasPassed()
    {
        var content = await Run(SearchTool(), new { pattern = "ZzNeedle" });

        Assert.DoesNotContain("files filtered", content);
    }
}
