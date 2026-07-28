using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 呈现层重构期间的**字节级**闸。
//
// 与隔壁两层的分工：OutputReadabilityTests 钉「这个工具的这句话该长什么样」，GrammarRules 钉
// 「任何返回都不许违反的文法」，两者都是 Contains / DoesNotContain——它们判得出「这句话还在」，
// 判不出「整份输出一个字没变」。而 renderer 重构的约束恰恰是后者：每个工具各自手拼的文本要挪进
// 一份共用 renderer，挪的过程里最容易掉的不是某句话，是句与句之间的空行数、脚注的先后、
// 某个分支下多出来的一个空格。这些全都能在 828 个断言全绿的前提下发生。
//
// 故这里存整份 ToolResult.Content 的逐字基线。判据只有一条：diff 为空。
//
// 基线不存在时**判红**而不是静默生成。一道在基线被删掉之后照样绿的闸比没有闸更糟——它绿的时候
// 没人知道那是「输出没变」还是「没有东西可比」。首次落地与故意改文案时的流程都是：
//   RIMSEARCHER_SNAPSHOTS=update dotnet test --filter OutputSnapshotTests
// 生成/更新基线，人工核对 diff，再重跑一次拿全绿。
[Collection("PathSecurity")]
public class OutputSnapshotTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public OutputSnapshotTests() => PathSecurity.ResetForTests();

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // ---- 闸本体 ----

    private static bool UpdateMode =>
        string.Equals(
            Environment.GetEnvironmentVariable("RIMSEARCHER_SNAPSHOTS"), "update", StringComparison.OrdinalIgnoreCase);

    // 基线跟着**测试源文件**放，不进构建输出：它是要被人读、被 git diff 审的东西，
    // 拷进 bin/ 只会让「改了输出」这件事在 review 里看不见。
    private static string SnapshotPath(string name, [CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Snapshots", $"{name}.txt");

    private void Verify(string name, string content)
    {
        var path = SnapshotPath(name);
        var actual = Normalize(content);

        if (UpdateMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            Assert.Fail(
                $"基线 Snapshots/{name}.txt 不存在，已按本次输出生成。人工核对后重跑；"
                + "本次判红是故意的——缺基线时判绿的闸没有判据。");
        }

        var expected = File.ReadAllText(path);
        if (expected == actual) return;

        Assert.Fail($"Snapshots/{name}.txt 与本次输出不一致：\n{Diff(expected, actual)}");
    }

    // 输出里唯一随环境变的东西是路径与耗时，两者都归一化掉；其余一律逐字比。
    private string Normalize(string content)
    {
        var root = _workspace.Root;
        content = content
            .Replace(root, "<ROOT>", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/'), "<ROOT>", StringComparison.OrdinalIgnoreCase);

        // <ROOT> 之后那一段的分隔符归一。只动这一段——正则回显与代码正文里的反斜杠不是路径，
        // 全局替换会把它们一起改掉，那就不再是「归一化」而是篡改被测文本了。
        content = Regex.Replace(content, @"<ROOT>[^\s`)\]]*", m => m.Value.Replace('\\', '/'));

        // sync_sources 的 `Source check (N ms, …)`
        content = Regex.Replace(content, @"\((?<n>\d+) ms,", "(<MS> ms,");

        return content;
    }

    // 逐行 diff。整份贴出来的话，一个空行的差异要在几十行里靠肉眼找。
    private static string Diff(string expected, string actual)
    {
        var want = expected.Split('\n');
        var got = actual.Split('\n');
        var lines = new List<string>();

        for (var i = 0; i < Math.Max(want.Length, got.Length); i++)
        {
            var a = i < want.Length ? want[i] : "<无此行>";
            var b = i < got.Length ? got[i] : "<无此行>";
            if (a == b) continue;

            lines.Add($"  第 {i + 1} 行\n    基线: {Quote(a)}\n    本次: {Quote(b)}");
            if (lines.Count >= 12) { lines.Add("  …（差异过多，只列前 12 处）"); break; }
        }

        return lines.Count == 0
            // 逐行相同却整体不等 = 差在行尾空白或结尾换行上，那正是最该被这道闸抓住的一类
            ? $"  逐行相同但整体不等（行尾空白或收尾差异）：基线 {expected.Length} 字符、本次 {actual.Length} 字符"
            : string.Join("\n", lines);
    }

    private static string Quote(string s) => "\"" + s.Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    // ---- 语料与调用 ----

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
        defs.Scan(root);
        defs.FreezeIndex();

        return (indexer, defs, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // 两个源：来源标签的三态、fileScopeTotals、越界计数都要它。
    private (SourceIndexer Indexer, DefIndexer Defs, ScopeCatalog Catalog) BuildTwoSourceIndex(
        (string RelPath, string Body)[] vanilla, (string RelPath, string Body)[] milira)
    {
        var vanillaRoot = _workspace.Dir("Vanilla");
        foreach (var (relPath, body) in vanilla)
            _workspace.WriteFile(Path.Combine("Vanilla", relPath), body);

        var miliraRoot = _workspace.Dir("Milira");
        foreach (var (relPath, body) in milira)
            _workspace.WriteFile(Path.Combine("Milira", relPath), body);

        var indexer = new SourceIndexer();
        indexer.Scan(vanillaRoot);
        indexer.Scan(miliraRoot);
        indexer.FreezeIndex();

        var defs = new DefIndexer();
        defs.Scan(vanillaRoot);
        defs.Scan(miliraRoot);
        defs.FreezeIndex();

        return (indexer, defs,
            ScopeCatalog.Build([("vanilla", vanillaRoot), ("milira", miliraRoot)], null, null));
    }

    private static async Task<string> Run(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    private static (string RelPath, string Body)[] ManyFiles(int count, int matchesPerFile = 1)
        => Enumerable.Range(0, count)
            .Select(i => ($"ZzFile{i:D3}.cs",
                string.Concat(Enumerable.Repeat("// ZzNeedle\n", matchesPerFile))))
            .ToArray();

    // ================= search_regex（§6 的形态清单） =================

    // 零命中形：fileFilter 回显 + 过滤后候选文件数 + casing + RetryWiderNotice。
    // 后者要两个源才在场——只有一个源时 scope 'vanilla' 就是全域（IncludesEverything），
    // 那时「retry with scope:'all'」保证白跑一轮，按设计不印。
    [Fact]
    public async Task SearchRegex_ZeroHits_FilteredToNothing()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzOne.cs", "// ZzNeedle\n")], [("ZzTwo.cs", "// ZzNeedle\n")]);

        var content = await Run(
            new SearchRegexTool(indexer, catalog),
            new { pattern = "ZzNeedle", fileFilter = ".txt", scope = "vanilla" });

        Verify("search_regex/zero-hits-file-filter", content);
    }

    // 拼错的 scope 被静默退回全域，零命中路径也要带 scopeNotice
    [Fact]
    public async Task SearchRegex_ZeroHits_WithUnresolvedScope()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(3));
        var content = await Run(
            new SearchRegexTool(indexer, catalog),
            new { pattern = "ZzAbsent", scope = "nosuchmod" });

        Verify("search_regex/zero-hits-unresolved-scope", content);
    }

    // 正常形表头（FoundCount 确定值）+ 单源钉死故一个来源标签都不印
    [Fact]
    public async Task SearchRegex_PlainHeader()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(3));
        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle" });

        Verify("search_regex/plain-header", content);
    }

    // 表头第二形：有文件读不开 → `at least` + LowerBoundReason 就地引用 + 尾注点名
    [Fact]
    public async Task SearchRegex_AtLeastHeader_WithUnreadableFile()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzOpen.cs", "// ZzNeedle\n"),
            ("ZzLocked.cs", "// ZzNeedle\n"));

        var locked = Directory.GetFiles(_workspace.Root, "ZzLocked.cs", SearchOption.AllDirectories)[0];
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle" });

        Verify("search_regex/at-least-unreadable", content);
    }

    // 第三种成因：单文件行闸。行闸在第 20000 行停，故语料要跨过它。
    [Fact]
    public async Task SearchRegex_AtLeastHeader_WithLineCappedFile()
    {
        var lines = Enumerable.Range(0, 25_000).Select(i => i == 0 ? "// ZzNeedle" : "// filler");
        var (indexer, _, catalog) = BuildIndex(("ZzHuge.cs", string.Join("\n", lines)));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle" });

        Verify("search_regex/at-least-line-capped", content);
    }

    // 第三种成因的最后一支：pattern 在某个文件上灾难性回溯、该文件被中途放弃。
    // `(a+)+b` 对一行纯 a 是指数级，索引层的 1 秒超时必中；另给一个真命中的文件，
    // 否则整份返回走零命中路径，而那条路径**一个字都不提**被放弃的文件（见 §7 的第一条）。
    [Fact]
    public async Task SearchRegex_AtLeastHeader_WithTimedOutFile()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBacktrack.cs", new string('a', 40) + "\n"),
            ("ZzQuick.cs", "aaab\n"));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "(a+)+b" });

        Verify("search_regex/at-least-timed-out", content);
    }

    // 同一个 pattern、只是没有任何文件命中：整份返回退化成一句 "No matches"，而扫描确实
    // 在一个文件上被放弃了。这份基线钉的是**现状**，§7 第一条修好之后它会作为 diff 出现。
    [Fact]
    public async Task SearchRegex_ZeroHits_SwallowsTheTimedOutFile()
    {
        var (indexer, _, catalog) = BuildIndex(("ZzBacktrack.cs", new string('a', 40) + "\n"));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "(a+)+b" });

        Verify("search_regex/zero-hits-swallows-timeout", content);
    }

    // 表头第三形：limit 咬人 → `first N preview lines` + scan-stopped 尾注
    [Fact]
    public async Task SearchRegex_FirstNPreviewLinesHeader()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(400));
        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = 100 });

        Verify("search_regex/first-n-preview-lines", content);
    }

    // 每文件折叠（`+N more of M matching lines in this file`）+ preview-cap 脚注整份一次
    [Fact]
    public async Task SearchRegex_PerFileFold_AndPreviewCapFootnote()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzDense.cs", string.Join("\n", Enumerable.Range(0, 9).Select(i => $"// ZzNeedle {i}"))));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle" });

        Verify("search_regex/per-file-fold", content);
    }

    // 文件数超限、扫描**没停**那一形：`... +N more of M matching files (50 listed; …)`
    [Fact]
    public async Task SearchRegex_FileCountOverCap_ScanNotStopped()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(60));
        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = "all" });

        Verify("search_regex/file-count-over-cap", content);
    }

    // 重名文件消歧：基名在本次返回里不唯一时补到刚好能分开的那几级目录
    [Fact]
    public async Task SearchRegex_DisambiguatesDuplicateFileNames()
    {
        var (indexer, _, catalog) = BuildIndex(
            (Path.Combine("A", "ZzSame.cs"), "// ZzNeedle\n"),
            (Path.Combine("B", "ZzSame.cs"), "// ZzNeedle\n"));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle" });

        Verify("search_regex/duplicate-file-names", content);
    }

    // 来源标签混源形（逐行印）+ HardScopeFilterNotice 缺席（scope 'all' 时本来就没有「外面」）
    [Fact]
    public async Task SearchRegex_MixedSources_LabelsPerRow()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzOne.cs", "// ZzNeedle\n")],
            [("ZzTwo.cs", "// ZzNeedle\n")]);

        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", scope = "all" });

        Verify("search_regex/mixed-sources", content);
    }

    // 同源形（标签提到表头印一次）+ 窄 scope 故 HardScopeFilterNotice 在场
    [Fact]
    public async Task SearchRegex_SingleSourceUnderNarrowScope_HoistsTheLabel()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzOne.cs", "// ZzNeedle\n"), ("ZzThree.cs", "// ZzNeedle\n")],
            [("ZzTwo.cs", "// ZzNeedle\n")]);

        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", scope = "vanilla" });

        Verify("search_regex/hard-scope-filter", content);
    }

    // 大小写开关的回显（默认 true，故这一形要显式关掉才看得见）
    [Fact]
    public async Task SearchRegex_CaseSensitiveEcho()
    {
        var (indexer, _, catalog) = BuildIndex(("ZzCase.cs", "// ZzNeedle\n// zzneedle\n"));
        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", ignoreCase = false });

        Verify("search_regex/case-sensitive", content);
    }

    // ================= trace =================

    [Fact]
    public async Task TraceInheritors_Tree()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzMid.cs", "namespace Zz { public class ZzMid : ZzBase { } }"),
            ("ZzLeaf.cs", "namespace Zz { public class ZzLeaf : ZzMid { } }"),
            ("ZzOther.cs", "namespace Zz { public class ZzOther : ZzBase { } }"));

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzBase", mode = "inheritors" });

        Verify("trace/inheritors-tree", content);
    }

    // 被 limit 截断：折叠行走 FoldLine 的 truncatedByLimit 分支 + capAction 只在顶到硬上限时印
    [Fact]
    public async Task TraceInheritors_TruncatedByLimit()
    {
        var files = new List<(string, string)> { ("ZzBase.cs", "namespace Zz { public class ZzBase { } }") };
        for (var i = 0; i < 12; i++)
            files.Add(($"ZzSub{i:D2}.cs", $"namespace Zz {{ public class ZzSub{i:D2} : ZzBase {{ }} }}"));

        var (indexer, _, catalog) = BuildIndex(files.ToArray());
        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzBase", mode = "inheritors", limit = 5 });

        Verify("trace/inheritors-truncated", content);
    }

    // 「索引里有这个类型、只是没人继承它」与「索引里没这个名字」是两条不同的话
    [Fact]
    public async Task TraceInheritors_KnownTypeWithNoSubclasses()
    {
        var (indexer, _, catalog) = BuildIndex(("ZzLone.cs", "namespace Zz { public class ZzLone { } }"));
        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzLone", mode = "inheritors" });

        Verify("trace/inheritors-known-but-childless", content);
    }

    // 切片浅于整树：截断留下的恒是最浅的那一批，故必须说清「更深的没列出来」。
    // 这一形与 inheritors-truncated 的差别只在 shape.Deepest > 切片最深层，而它换的是表头
    // 那半句覆盖说明——两句共用同一个「切片最深层」，任何一处算错都在这里显形。
    [Fact]
    public async Task TraceInheritors_SliceShallowerThanTheTree()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzA.cs", "namespace Zz { public class ZzA : ZzBase { } }"),
            ("ZzB.cs", "namespace Zz { public class ZzB : ZzBase { } }"),
            ("ZzC.cs", "namespace Zz { public class ZzC : ZzBase { } }"),
            ("ZzDeep.cs", "namespace Zz { public class ZzDeep : ZzA { } }"));

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzBase", mode = "inheritors", limit = 2 });

        Verify("trace/inheritors-slice-shallower", content);
    }

    // 混源：来源标签逐行印（未截断，故段头不印构成）
    [Fact]
    public async Task TraceInheritors_MixedSources()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
             ("ZzHome.cs", "namespace Zz { public class ZzHome : ZzBase { } }")],
            [("ZzGuest.cs", "namespace Zz { public class ZzGuest : ZzBase { } }")]);

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzBase", mode = "inheritors" });

        Verify("trace/inheritors-mixed-sources", content);
    }

    // 截断 + 总数跨源：段头改印全树的来源构成（切片全是 vanilla，正是那个结构性偏置）
    [Fact]
    public async Task TraceInheritors_TruncatedSpanningSources()
    {
        var vanilla = new List<(string, string)> { ("ZzBase.cs", "namespace Zz { public class ZzBase { } }") };
        for (var i = 0; i < 6; i++)
            vanilla.Add(($"ZzHome{i}.cs", $"namespace Zz {{ public class ZzHome{i} : ZzBase {{ }} }}"));

        var (indexer, _, catalog) = BuildTwoSourceIndex(
            vanilla.ToArray(),
            [("ZzGuest.cs", "namespace Zz { public class ZzGuest : ZzBase { } }")]);

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzBase", mode = "inheritors", limit = 3 });

        Verify("trace/inheritors-truncated-spanning-sources", content);
    }

    // 越界脚注 + 「把落选那批算进来整棵树是什么形状」。后者用的 depths 是全域 BFS 的产物，
    // 与逐行的 [depth N] 同源，故这份基线同时钉住「两处对不对得上」。
    [Fact]
    public async Task TraceInheritors_OutOfScopeFooter()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
             ("ZzHome.cs", "namespace Zz { public class ZzHome : ZzBase { } }")],
            [("ZzGuest.cs", "namespace Zz { public class ZzGuest : ZzBase { } }"),
             ("ZzGuestDeep.cs", "namespace Zz { public class ZzGuestDeep : ZzGuest { } }")]);

        var content = await Run(
            new TraceTool(indexer, catalog),
            new { symbol = "ZzBase", mode = "inheritors", scope = "vanilla" });

        Verify("trace/inheritors-out-of-scope", content);
    }

    // 零命中 + scope 外有派生类：那句「这是答案」的背书必须换成「这不是完整答案」
    [Fact]
    public async Task TraceInheritors_ZeroHitsWithSubclassesOutOfScope()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzBase.cs", "namespace Zz { public class ZzBase { } }")],
            [("ZzGuest.cs", "namespace Zz { public class ZzGuest : ZzBase { } }")]);

        var content = await Run(
            new TraceTool(indexer, catalog),
            new { symbol = "ZzBase", mode = "inheritors", scope = "vanilla" });

        Verify("trace/inheritors-zero-hits-out-of-scope", content);
    }

    [Fact]
    public async Task TraceInheritors_UnknownType()
    {
        var (indexer, _, catalog) = BuildIndex(("ZzLone.cs", "namespace Zz { public class ZzLone { } }"));
        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzNoSuchType", mode = "inheritors" });

        Verify("trace/inheritors-unknown-type", content);
    }

    // usages 主形态：与 search_regex 共享文件块、每文件折叠、preview-cap 三条句子
    [Fact]
    public async Task TraceUsages_WithPerFileFold()
    {
        var body = string.Join("\n", Enumerable.Range(0, 9).Select(i => $"    // ZzMark {i}"));
        var (indexer, _, catalog) = BuildIndex(
            ("ZzHost.cs", $"namespace Zz {{ public class ZzHost {{\n{body}\n}} }}"),
            ("ZzGuest.cs", "namespace Zz { public class ZzGuest { void M() { ZzMark(); } } }"));

        var content = await Run(new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages" });

        Verify("trace/usages-per-file-fold", content);
    }

    [Fact]
    public async Task TraceUsages_TruncatedByLimit()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(40).Select(f => (f.RelPath,
            f.Body.Replace("ZzNeedle", "ZzMark"))).ToArray());

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzMark", mode = "usages", limit = 10 });

        Verify("trace/usages-truncated", content);
    }

    // 零命中 + 窄 scope：RetryWiderNotice 是这条路径上唯一的「别处也许有」的痕迹
    [Fact]
    public async Task TraceUsages_ZeroHits()
    {
        var (indexer, _, catalog) = BuildTwoSourceIndex(
            [("ZzHost.cs", "namespace Zz { public class ZzHost { } }")],
            [("ZzGuest.cs", "namespace Zz { public class ZzGuest { void M() { ZzAbsent(); } } }")]);

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzAbsent", mode = "usages", scope = "vanilla" });

        Verify("trace/usages-zero-hits", content);
    }

    // ---- 条件目录：行内的键与整份的成因脚注 ----
    //
    // 这两形此前一份基线都没有，而行内标记与尾注是**同一个持有者**（ConditionalReport）在正文
    // 两端各印一半：标记在每一行上打，成因隔着几十行正文在末尾兑换。两端之间那条「指认得上」
    // 的线是 F33 规则甲，没有字节级基线时它只被 Contains 断言拦着半边。
    private (SourceIndexer Indexer, ScopeCatalog Catalog, ConditionalFolders Folders) BuildGatedIndex(
        params (string RelPath, string Body)[] files)
    {
        var conditionalDir = _workspace.Dir("Core", "1.6", "CE");
        foreach (var (relPath, body) in files)
            _workspace.WriteFile(Path.Combine("Core", relPath), body);

        var root = _workspace.Dir("Core");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer,
            ScopeCatalog.Build([("vanilla", root)], null, null),
            ConditionalFolders.Build([
                new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "vanilla")
            ]));
    }

    [Fact]
    public async Task SearchRegex_ConditionalFolderTagAndFootnote()
    {
        var (indexer, catalog, folders) = BuildGatedIndex(
            ("ZzPlain.cs", "// ZzNeedle\n"),
            (Path.Combine("1.6", "CE", "ZzGated.cs"), "// ZzNeedle\n"));

        var content = await Run(
            new SearchRegexTool(indexer, catalog, folders), new { pattern = "ZzNeedle" });

        Verify("search_regex/conditional-folder", content);
    }

    [Fact]
    public async Task TraceInheritors_ConditionalFolderTagAndFootnote()
    {
        var (indexer, catalog, folders) = BuildGatedIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { } }"),
            ("ZzPlain.cs", "namespace Zz { public class ZzPlain : ZzBase { } }"),
            (Path.Combine("1.6", "CE", "ZzGated.cs"),
                "namespace Zz { public class ZzGated : ZzBase { } }"));

        var content = await Run(
            new TraceTool(indexer, catalog, folders), new { symbol = "ZzBase", mode = "inheritors" });

        Verify("trace/inheritors-conditional", content);
    }

    // ================= locate =================

    // C# Types / Members / XML Defs / Content Matches 四段齐。
    // 第四段要**另一条** def 的字段值里出现整词 ZzWidget——内容索引按整词建键、查询走全等
    // 查表，故 `ZzWidgetKeyword` 这样的值命不中；宿主 def 自己名字不含查询词，那一段才不会
    // 被前一段吃掉。
    [Fact]
    public async Task Locate_AllSections()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { public void ZzWidgetTick() { } } }"),
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n"
                + "    <label>alpha widget</label>\n  </ZzWidgetDef>\n"
                + "  <ZzWidgetDef>\n    <defName>ZzUnrelatedBeta</defName>\n"
                + "    <ZzNote>ZzWidget</ZzNote>\n  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Verify("locate/all-sections", content);
    }

    // Files 段的精确补充那一支：显式带扩展名 = 在问文件，索引里有那一份就只补它
    [Fact]
    public async Task Locate_FilesSection_ExactFileName()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidgets.xml" });

        Verify("locate/files-exact-name", content);
    }

    // Tally 的 `N of M` 与 `(K at 100%)` 两个记号
    [Fact]
    public async Task Locate_TallyWithFoldAndFullScore()
    {
        var files = Enumerable.Range(0, 20)
            .Select(i => ($"ZzThing{i:D2}.cs",
                $"namespace Zz {{ public class ZzThing{i:D2} {{ public int ZzThingField{i:D2}; }} }}"))
            .Append(("ZzThing.cs", "namespace Zz { public class ZzThing { } }"))
            .ToArray();

        var (indexer, defs, catalog) = BuildIndex(files);
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzThing" });

        Verify("locate/tally-fold-and-full-score", content);
    }

    // 零命中 + 窄 scope。这里的 RetryWiderNotice 与越界脚注互斥（两句并排会让同一个
    // 「改用 scope:'all'」被两套措辞各说一遍），故语料里另一个源不含近名项。
    [Fact]
    public async Task Locate_ZeroHits()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }")],
            [("ZzOther.cs", "namespace Zz { public class ZzOther { } }")]);

        var content = await Run(
            new LocateTool(indexer, defs, catalog), new { query = "ZzAbsentThing", scope = "vanilla" });

        Verify("locate/zero-hits", content);
    }

    // 认不出的过滤前缀被当成普通搜索词
    [Fact]
    public async Task Locate_UnknownPrefixNotice()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { public void ZzTick() { } } }"));

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "member:ZzTick" });

        Verify("locate/unknown-prefix", content);
    }

    // 短词不进内容索引：Content Matches 段整段缺席与「查过了、零命中」在版面上同形，故要明说
    [Fact]
    public async Task Locate_ShortTokenNotice()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"),
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n    <ZzCount>20</ZzCount>\n"
                + "  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget 20" });

        Verify("locate/short-token", content);
    }

    // 显式带扩展名 = 在问文件，而索引里没有叫这个名字的文件时必须说
    [Fact]
    public async Task Locate_MissingExactFileNotice()
    {
        var (indexer, defs, catalog) = BuildIndex(("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"));
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidgets.xml" });

        Verify("locate/missing-exact-file", content);
    }

    // 越界脚注（合计 + 构成 + 逐源）：locate 是唯一逐源报 out-of-scope 的那一类
    [Fact]
    public async Task Locate_OutOfScopeFooter()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }")],
            [("ZzWidgetTwo.cs", "namespace Zz { public class ZzWidgetTwo { } }")]);

        var content = await Run(
            new LocateTool(indexer, defs, catalog), new { query = "ZzWidget", scope = "vanilla" });

        Verify("locate/out-of-scope-footer", content);
    }

    // ================= read_code =================

    // 行区间形 + 折叠行（`+N more of M lines (pass startLine=N)`）
    [Fact]
    public async Task ReadCode_LineRangeWithFold()
    {
        var body = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"// line {i}"));
        var (indexer, _, catalog) = BuildIndex(("ZzLong.cs", body + "\n"));
        PathSecurity.Initialize([Path.Combine(_workspace.Root, "Core")]);

        var content = await Run(
            new ReadCodeTool(indexer, catalog), new { fileName = "ZzLong.cs", startLine = 0, lineCount = 10 });

        Verify("read_code/line-range-fold", content);
    }

    [Fact]
    public async Task ReadCode_SingleMember()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz\n{\n    public class ZzWidget\n    {\n        public void ZzTick()\n        {\n            var x = 1;\n        }\n    }\n}\n"));
        PathSecurity.Initialize([Path.Combine(_workspace.Root, "Core")]);

        var content = await Run(
            new ReadCodeTool(indexer, catalog), new { fileName = "ZzWidget.cs", methodName = "ZzTick" });

        Verify("read_code/single-member", content);
    }

    // XML 不该被套进 csharp 围栏
    [Fact]
    public async Task ReadCode_XmlFence()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzDefs.xml", "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n  </ZzWidgetDef>\n</Defs>\n"));
        PathSecurity.Initialize([Path.Combine(_workspace.Root, "Core")]);

        var content = await Run(new ReadCodeTool(indexer, catalog), new { fileName = "ZzDefs.xml" });

        Verify("read_code/xml-fence", content);
    }

    // ================= list_directory =================

    [Fact]
    public async Task ListDirectory_PlainListing()
    {
        var (_, _, catalog) = BuildIndex(
            ("ZzOne.cs", "// one\n"), ("ZzTwo.cs", "// two\n"), (Path.Combine("Sub", "ZzThree.cs"), "// three\n"));
        var core = Path.Combine(_workspace.Root, "Core");
        PathSecurity.Initialize([core]);

        var content = await Run(new ListDirectoryTool(catalog), new { path = core });

        Verify("list_directory/plain-listing", content);
    }

    // 分页折叠行：下一步是 `pass offset=N`，落不进 FoldLine 现有三分支（步 4 要加的那一形）
    [Fact]
    public async Task ListDirectory_PageFold()
    {
        var files = Enumerable.Range(0, 12).Select(i => ($"ZzEntry{i:D2}.cs", "// x\n")).ToArray();
        var (_, _, catalog) = BuildIndex(files);
        var core = Path.Combine(_workspace.Root, "Core");
        PathSecurity.Initialize([core]);

        var content = await Run(new ListDirectoryTool(catalog), new { path = core, limit = 5 });

        Verify("list_directory/page-fold", content);
    }

    // ================= inspect =================

    [Fact]
    public async Task Inspect_TypeOutline()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzBase.cs", "namespace Zz { public class ZzBase { public virtual void ZzTick() { } } }"),
            ("ZzWidget.cs",
                "namespace Zz { public class ZzWidget : ZzBase { public int ZzEnergy; "
                + "public override void ZzTick() { } } }"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzWidget" });

        Verify("inspect/type-outline", content);
    }

    [Fact]
    public async Task Inspect_Def()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidgetDef.cs", "namespace Zz { public class ZzWidgetDef { } }"),
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n"
                + "    <label>alpha widget</label>\n  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzWidgetAlpha" });

        Verify("inspect/def", content);
    }

    // ================= sync_sources =================

    [Fact]
    public async Task SyncSources_Check()
    {
        var sourceDirectory = _workspace.Dir("src");
        _workspace.WriteFile(Path.Combine("src", "ZzWidget.cs"), "// current\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = sourceDirectory,
            AssemblyPaths = [_workspace.Dir("assemblies")]
        };

        var service = new SourceSyncService(
            config, new ResolvedSources([entry], []), _workspace.Dir("cache"));

        PathSecurity.Initialize([sourceDirectory]);

        var content = await Run(new SyncSourcesTool(service), new { action = "check" });

        Verify("sync_sources/check", content);
    }
}
