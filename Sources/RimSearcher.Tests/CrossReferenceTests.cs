using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 第九轮的主题：同一份返回里**两条信息之间的可指认引用**。
//
// 前八轮把「每个数字配一个正确的量纲和名词」这一路做到了见底——第九轮盲测在文法层面零缺陷。
// 翻车全部发生在别处：记号在场、成因也在场，但两者之间没有一条读者能顺着走的线，于是读者
// 就近抓一个看起来能解释它的东西（`at least 105` 抓 `limit` 的 default 100，截断后的
// `[depth 4]` 抓表头的 `deepest 6`，`87 个根` 抓 scope 的 11 个源）。
//
// 这一组守的就是那些线：下界↔成因、表头↔列表排序、脚注合计↔构成、索引口径↔运行时口径。
public class CrossReferenceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static async Task<string> Run(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // ---- 下界记号 ↔ 成因 ----

    // 盲测三条互不相干的任务链在**成因确实同现**的返回上各自独立误读了同一个 `at least`：
    // schema 里唯一带上限语义的是 `limit` 的 default 100，而命中 105 与它只差 5，算术上太顺。
    // 真成因（有文件没扫全）写在整份结果之后、中间还隔着预览上限那一行。记号自己必须带引用。
    [Fact]
    public async Task LowerBoundHeader_PointsAtItsCause_AndRulesOutLimit()
    {
        var root = _workspace.Dir("Core");
        // 越过单文件行闸（20000 行）的文件把总数降格成下界
        var giant = new System.Text.StringBuilder();
        for (var i = 0; i < 20050; i++) giant.AppendLine("// filler");
        giant.AppendLine("// ZzNeedle");
        _workspace.WriteFile(Path.Combine("Core", "Giant.cs"), giant.ToString());
        _workspace.WriteFile(Path.Combine("Core", "Small.cs"), "// ZzNeedle\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        Assert.Contains("at least ", content);
        Assert.Contains("were not scanned in full", content);
        // 记号旁边就说清成因在哪、以及 limit 与它无关
        Assert.Contains("'at least' comes from the trailing 'not scanned in full' note, not from limit", content);
    }

    // 反面：没有下界时不许凭空挂这句引用
    [Fact]
    public async Task CompleteHeader_CarriesNoLowerBoundPointer()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "Small.cs"), "// ZzNeedle\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { pattern = "ZzNeedle" });

        Assert.DoesNotContain("at least ", content);
        Assert.DoesNotContain("'at least' comes from", content);
    }

    // ---- 表头 ↔ 列表排序 ----

    private TraceTool BuildDeepTree(int wideAtDepthOne)
    {
        var root = _workspace.Dir("Core");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Zz {");
        sb.AppendLine("public class ZzBase { }");
        // 第一层足够宽，把 200 的配额吃满，深层项因此全部落在被截的那批里
        for (var i = 0; i < wideAtDepthOne; i++)
            sb.AppendLine($"public class ZzWide{i:D4} : ZzBase {{ }}");
        // 一条走到第 4 层的细链
        sb.AppendLine("public class ZzD2 : ZzWide0000 { }");
        sb.AppendLine("public class ZzD3 : ZzD2 { }");
        sb.AppendLine("public class ZzD4 : ZzD3 { }");
        sb.AppendLine("}");
        _workspace.WriteFile(Path.Combine("Core", "Tree.cs"), sb.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        return new TraceTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // R42 让表头描述整棵树（真值），而列表按深度升序截断。两者并排、句法对称，于是
    // 「样本里最深的一层」被当成「树最深的一层」——盲测里 depth 4 的那批名字被报成了
    // depth 6 的成员。截断先吃掉最深层这件事，返回里此前一个字都没有。
    [Fact]
    public async Task TruncatedInheritors_StateWhichDepthsAreStillMissing()
    {
        var content = await Run(BuildDeepTree(250), new { symbol = "ZzBase", mode = "inheritors" });

        Assert.Contains("Listed below: 200", content);
        Assert.Contains("shallowest first", content);
        Assert.Contains("nothing below depth 1 is listed", content);
        // 表头仍报整棵树的真深度——这一条是 R42 的产物，不许回退
        Assert.Contains("deepest 4 levels down", content);
    }

    // 没被截断时不谈「哪一层没列」——那时列表就是整棵树，说了反而让读者去找不存在的缺口
    [Fact]
    public async Task CompleteInheritors_SayNothingAboutDepthCoverage()
    {
        var content = await Run(BuildDeepTree(3), new { symbol = "ZzBase", mode = "inheritors" });

        Assert.DoesNotContain("Listed below", content);
        Assert.DoesNotContain("shallowest first", content);
    }

    // 顶到硬上限时「narrow the query」在继承树上不是可执行动作：查询词就是那个类名，没得再窄，
    // 而这个 mode 既没有 offset 也没有参数抬得动 200。唯一的出路此前一处没写，盲测为此拿
    // 9 次 trace 盲探。
    [Fact]
    public async Task InheritorsCapLine_GivesTheOnlyActionThatExists()
    {
        var content = await Run(BuildDeepTree(250), new { symbol = "ZzBase", mode = "inheritors" });

        Assert.Contains("server cap 200 reached", content);
        Assert.Contains("re-trace a listed type as its own root", content);
        Assert.Contains("depths then restart from it", content);
    }

    // depth 的原点从没写过：表头的 `deepest N levels down` 该对 `[depth N]` 还是 `[depth N-1]`
    // 无从判断，而这两种读法在「要覆写哪一层」上给出不同答案。
    [Fact]
    public async Task DepthLegend_PinsTheOrigin()
    {
        var content = await Run(BuildDeepTree(3), new { symbol = "ZzBase", mode = "inheritors" });

        Assert.Contains("untagged = direct (depth 1)", content);
    }

    // ---- 域内形状 ↔ 全域形状 ----

    // 表头的 `N direct, deepest M levels down` 描述的是 scope **内**的树，而越界脚注只报
    // 「外面还有几个」。于是「换个 scope 会不会改变深度」完全不可判定，盲测里调用方猜错并
    // 写进了答案正文。与 R42 同形，轴从「整树 vs 截断切片」换成「域内树 vs 全域树」。
    [Fact]
    public async Task OutOfScopeFootnote_GivesTheWholeDomainShape()
    {
        var inScope = _workspace.Dir("Core");
        var outScope = _workspace.Dir("Mod");
        _workspace.WriteFile(Path.Combine("Core", "Tree.cs"), """
            namespace Zz
            {
                public class ZzBase { }
                public class ZzChild : ZzBase { }
            }
            """);
        // scope 外多一个直接子类，并把树多加一层
        _workspace.WriteFile(Path.Combine("Mod", "Ext.cs"), """
            namespace Zz
            {
                public class ZzModChild : ZzBase { }
                public class ZzModGrandchild : ZzChild { }
            }
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(inScope);
        indexer.Scan(outScope);
        indexer.FreezeIndex();
        var tool = new TraceTool(
            indexer, ScopeCatalog.Build([("vanilla", inScope), ("mod", outScope)], null, "vanilla"));

        var content = await Run(tool, new { symbol = "ZzBase", mode = "inheritors", scope = "vanilla" });

        // 域内：1 个直接子类、只有一层
        Assert.Contains("1 direct, deepest 1 level down", content);
        // 全域：2 个直接子类、两层——这两个数此前在返回里完全拿不到
        Assert.Contains("including them the tree is 2 direct, deepest 2 levels down", content);
    }

    // ---- 脚注合计 ↔ 构成 ----

    // R48 的合计跨段累加，而**哪几段参与**跟着命中形态变：同一条 `method:X` 换个 scope，
    // Members 段空了会触发 Files 段，同一个源的计数就多一。调用方看到同一个源两次数不一致，
    // 只能对整份脚注打折使用。
    [Fact]
    public async Task OutOfScopeTotal_NamesWhatItIsMadeOf()
    {
        var inScope = _workspace.Dir("Core");
        var modA = _workspace.Dir("ModA");
        var modB = _workspace.Dir("ModB");
        _workspace.WriteFile(Path.Combine("Core", "Placeholder.cs"), "namespace Zz { public class ZzOther { } }");
        // 落选的两条来自**不同段**：一条 C# 类型、一条 XML def。合计说 2 matches，
        // 而这 2 是「1 个类型 + 1 个 def」还是「2 个类型」，此前没有任何一处说得出来。
        _workspace.WriteFile(Path.Combine("ModA", "ZzNeedle.cs"), "namespace Zz { public class ZzNeedle { } }");
        _workspace.WriteFile(Path.Combine("ModB", "Things.xml"), """
            <Defs>
              <ThingDef>
                <defName>ZzNeedle</defName>
              </ThingDef>
            </Defs>
            """);

        var indexer = new SourceIndexer();
        indexer.Scan(inScope);
        indexer.Scan(modA);
        indexer.FreezeIndex();
        var defIndexer = new DefIndexer();
        defIndexer.Scan(modB);

        var catalog = ScopeCatalog.Build(
            [("vanilla", inScope), ("modA", modA), ("modB", modB)], null, "vanilla");
        var tool = new LocateTool(indexer, defIndexer, catalog);

        var content = await Run(tool, new { query = "ZzNeedle", scope = "vanilla" });

        // 合计 3，构成是「1 类型 + 1 def + 1 内容命中」——三段各出一条。此前只有那个 3，
        // 而它由哪几段凑成跟着命中形态变（同一条查询换个 scope，参与的段就换一批）。
        Assert.Contains(
            "Outside scope 'vanilla': 3 matches (1 C# type + 1 XML def + 1 content match) — modB 2, modA 1.",
            content);
    }

    // ---- 索引口径 ↔ 运行时口径 ----

    // 「显式带扩展名 = 在问文件」（F31）只做了命中那一半。零命中时 Files 段整个不印，返回里
    // 只剩别的段落对同一个查询串做的模糊命中——实测 `LoadFolders.xml` 回的是
    // 「1 C# type: LoadFolder (37%)」，全篇没有一个字说索引里没有这个文件。
    [Fact]
    public async Task FileNameQueryWithNoSuchFile_SaysSo_EvenWhenOtherSectionsHit()
    {
        var root = _workspace.Dir("Core");
        // 类型名与查询词高度相似，会占住 C# Types 段
        _workspace.WriteFile(Path.Combine("Core", "ZzLoadFolder.cs"), "namespace Zz { public class ZzLoadFolder { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new LocateTool(indexer, new DefIndexer(), ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { query = "ZzLoadFolders.xml" });

        Assert.Contains("No indexed file is named 'ZzLoadFolders.xml'", content);
        Assert.Contains("matched the query as a name, not as that file", content);
    }

    // 索引里确实有那个文件时不许挂这句
    [Fact]
    public async Task FileNameQueryThatResolves_CarriesNoMissingFileNotice()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzThings.xml"), "<Defs />");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new LocateTool(indexer, new DefIndexer(), ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { query = "ZzThings.xml" });

        Assert.DoesNotContain("No indexed file is named", content);
    }

    // 「命中文件总数」与「列出了几个」两形要对齐。同一个工具的 scan-stopped 那一形明写
    // `only the first 50 files are listed`，这一形此前不写，读者只能做减法——盲测据此断言
    // 「97 个文件的来源标签清一色落在那 11 个源内」，而它只见过 50 个。
    [Fact]
    public async Task FileFoldLine_PrintsHowManyWereListed()
    {
        var root = _workspace.Dir("Core");
        for (var i = 0; i < 60; i++)
            _workspace.WriteFile(Path.Combine("Core", $"File{i:D3}.cs"), "// ZzNeedle\n");

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var tool = new SearchRegexTool(indexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { pattern = "ZzNeedle", limit = "all" });

        Assert.Contains("... +10 more of 60 matching files (50 listed;", content);
    }

    // R51 那句「PatchOperations 从不被应用」此前只在 tools/list 里。它两次是整条链的转折点，
    // 但两次都靠调用方通读了 schema——而返回里唯一会被当作「游戏里的那个 def」读的就是这一块。
    [Fact]
    public async Task ResolvedXmlHeading_SaysPatchesAreNotApplied()
    {
        var root = _workspace.Dir("Defs");
        _workspace.WriteFile(Path.Combine("Defs", "Things.xml"), """
            <Defs>
              <ThingDef>
                <defName>ZzThing</defName>
                <label>zz thing</label>
              </ThingDef>
            </Defs>
            """);

        var defIndexer = new DefIndexer();
        defIndexer.Scan(root);
        var indexer = new SourceIndexer();
        indexer.FreezeIndex();
        var tool = new InspectTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));

        var content = await Run(tool, new { name = "ZzThing" });

        Assert.Contains("**Resolved XML** (mod PatchOperations are not applied, so a mod patch against "
                        + "this def is not reflected below):", content);
    }

    // 露出来的前几个根形如 `<反编译根>\<源名>`，逐一对应 scope 里的源名——于是「根 ≈ 源」被
    // 坐实，87 个根读成 87 个源，而 scope 只枚举 11 个。两条链各自撞上这道粒度差。
    [Fact]
    public void RootsSentence_TiesRootCountToSourceCount()
    {
        var a = _workspace.Dir("SrcA");
        var b = _workspace.Dir("SrcB");
        PathSecurity.Initialize([a, b]);

        // 一个源跨两个根：正是让「根数 ≠ 源数」的那种配置
        var catalog = ScopeCatalog.Build([("vanilla", a), ("vanilla", b)], null, null);
        var description = new ListDirectoryTool(catalog).Description;

        Assert.Contains("These 2 roots are the indexed folders of the 1 configured sources", description);
        Assert.Contains("one source usually spans several roots", description);
    }
}
