using System.Runtime.CompilerServices;
using System.Text;
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

        // sync_sources 的 diff 表头带归档时刻 `since v0001 (2026-07-29 02:37 UTC)`
        content = Regex.Replace(content, @"\(\d{4}-\d{2}-\d{2} \d{2}:\d{2} UTC\)", "(<UTC>)");

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
    // 才走得到表头那一路（`at least` + 就地成因）。零命中那一形是下一个用例。
    [Fact]
    public async Task SearchRegex_AtLeastHeader_WithTimedOutFile()
    {
        var (indexer, _, catalog) = BuildIndex(
            ("ZzBacktrack.cs", new string('a', 40) + "\n"),
            ("ZzQuick.cs", "aaab\n"));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "(a+)+b" });

        Verify("search_regex/at-least-timed-out", content);
    }

    // 同一个 pattern、只是没有任何文件命中。这一路此前退化成一句 "No matches"，而扫描确实
    // 在一个文件上被放弃了——一个字都不提（§7 第一条）。已修（e388328），此处钉的是修好那一形：
    // 表头那半边在这一路无处可挂（没有表头，也没有要降格的总数），故成因整句落在尾注上。
    //
    // 名字里是 StillNames 而不是 Swallows：`Swallows` 正是那个已修的缺陷，而这个用例钉住的
    // 恰恰是**不再吞掉**（快照里明明白白印着 `1 file was abandoned mid-scan … (ZzBacktrack.cs)`）。
    // 方法名比注释显眼得多，读者第一眼看到的就是它命名的那个行为。
    [Fact]
    public async Task SearchRegex_ZeroHits_StillNamesTheTimedOutFile()
    {
        var (indexer, _, catalog) = BuildIndex(("ZzBacktrack.cs", new string('a', 40) + "\n"));

        var content = await Run(new SearchRegexTool(indexer, catalog), new { pattern = "(a+)+b" });

        Verify("search_regex/zero-hits-names-timeout", content);
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

    // 同一形的 **N==1** 那一侧。分开立一份是因为它是全服唯一一处「计数恰好为 1」还没有判据的
    // 表头：`first 1 preview lines` 是线上正在输出的话，而它本该是 `first 1 preview line`。
    //
    // 要紧的是抓不到它的原因**不在闸的规则**——OutputGrammarGateTests 的规则二甲是纯结构判定
    // （`1 preview lines` 里 lines 以 s 结尾、不在 NotNouns 里 → 判违规），压根不查词表，本来
    // 就抓得住。漏掉它的是矩阵少了「计数恰好为 1」这一维：两个 ScanStopped 格用的都是 limit = 4。
    //
    // 这份基线立在改动之前，钉的是当时的现状（表头由 ScanOutputRenderer 手拼、不走构词）。
    // 补维度并改产地的那个 commit（086acc3）让它作为 diff 出现了一次，现在钉的是修好那一形。
    [Fact]
    public async Task SearchRegex_FirstOnePreviewLineHeader()
    {
        var (indexer, _, catalog) = BuildIndex(ManyFiles(400));
        var content = await Run(
            new SearchRegexTool(indexer, catalog), new { pattern = "ZzNeedle", limit = 1 });

        Verify("search_regex/scan-stopped-single", content);
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

    // 零命中 + 有文件撞了行闸。§7 第一条同型的另一半：那条尾注此前只挂在有结果那一路，
    // 而两个扫盘工具共用同一个 renderer，故修在 ScanOutputRenderer.Empty 一处、trace 这边跟着好，
    // 这一份钉的就是「跟着好了」。
    [Fact]
    public async Task TraceUsages_ZeroHitsWithLineCappedFile()
    {
        var lines = Enumerable.Range(0, 25_000).Select(_ => "// filler");
        var (indexer, _, catalog) = BuildIndex(("ZzHuge.cs", string.Join("\n", lines)));

        var content = await Run(
            new TraceTool(indexer, catalog), new { symbol = "ZzAbsent", mode = "usages" });

        Verify("trace/usages-zero-hits-line-capped", content);
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

    // 断层收口那一形：`... +N more C# types (lower relevance, …)`。
    //
    // 补这一份是 N1 的前置。`lower relevance` 此前在整批基线里**一处都没有**（`grep -rl` 零命中）
    // ——它只被文法闸矩阵的 locate/ScoreGap 格与 OutputReadabilityTests 的 Contains 断言守着，
    // 两者都判得出「这句话还在」，判不出「这一段一个字没变」。而 N1 恰恰要动它的产地
    // （把「藏起来的是哪一批」从 Fold.Line 里写死的 bool 分支变成一个槽），故先立判据再动。
    //
    // 语料的判据是分差要越过 40 那道收口线：查询串在 ZzWidget 上逐字相同（100 分），在其余几个
    // 上只以**子串**形式出现（不在开头、也不在任何词的开头），子串支封顶 50 分，相对首条掉 50。
    [Fact]
    public async Task Locate_ScoreGapFold()
    {
        var files = Enumerable.Range(0, 4)
            .Select(i => ($"ZzHolderOfZzWidgetParts{i:D2}.cs",
                $"namespace Zz {{ public class ZzHolderOfZzWidgetParts{i:D2} {{ }} }}"))
            .Append(("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"))
            .ToArray();

        var (indexer, defs, catalog) = BuildIndex(files);
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "type:ZzWidget" });

        Verify("locate/score-gap-fold", content);
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

    // 混源：五段的来源标签逐行印（未截断，故段头不印构成）
    [Fact]
    public async Task Locate_MixedSources()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }")],
            [("ZzWidgetTwo.cs", "namespace Zz { public class ZzWidgetTwo { } }")]);

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget" });

        Verify("locate/mixed-sources", content);
    }

    // 截断 + 总数跨源：段头改印全集构成（各源之和恒等于表头那个总数）
    [Fact]
    public async Task Locate_TruncatedSpanningSources()
    {
        var vanilla = Enumerable.Range(0, 12)
            .Select(i => ($"ZzThing{i:D2}.cs", $"namespace Zz {{ public class ZzThing{i:D2} {{ }} }}"))
            .ToArray();

        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            vanilla, [("ZzThingGuest.cs", "namespace Zz { public class ZzThingGuest { } }")]);

        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzThing" });

        Verify("locate/truncated-spanning-sources", content);
    }

    // Files 段的**兜底**那一支：其余四段全空时列模糊文件名命中，且总数是「列出的 + 被砍掉的」
    // （精确补充那一支没有折叠行，两支的 fileTotal 判据不同）
    [Fact]
    public async Task Locate_FilesFallbackWithFold()
    {
        var files = Enumerable.Range(0, 14)
            .Select(i => ($"ZzLonely{i:D2}.xml", "<Defs>\n  <ZzOtherDef>\n    <defName>ZzUnrelated"
                                                + $"{i:D2}</defName>\n  </ZzOtherDef>\n</Defs>\n"))
            .ToArray();

        var (indexer, defs, catalog) = BuildIndex(files);
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzLonely" });

        Verify("locate/files-fallback-fold", content);
    }

    // Members 段多组：三类轮流占配额 + 段末一条按总量计的折叠行
    [Fact]
    public async Task Locate_MembersAcrossKinds()
    {
        var files = Enumerable.Range(0, 8)
            .Select(i => ($"ZzHolder{i:D2}.cs",
                $"namespace Zz {{ public class ZzHolder{i:D2} {{ public int ZzSlotField{i:D2}; "
                + $"public void ZzSlotTick{i:D2}() {{ }} public int ZzSlotProp{i:D2} {{ get; set; }} }} }}"))
            .ToArray();

        var (indexer, defs, catalog) = BuildIndex(files);
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzSlot" });

        Verify("locate/members-across-kinds", content);
    }

    // 零命中 + scope 外有命中：RetryWider 让位给越界脚注（两句并排会把同一个出路说两遍）
    [Fact]
    public async Task Locate_ZeroHitsWithOutOfScopeFooter()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzHost.cs", "namespace Zz { public class ZzHost { } }")],
            [("ZzGuestWidget.cs", "namespace Zz { public class ZzGuestWidget { } }")]);

        var content = await Run(
            new LocateTool(indexer, defs, catalog), new { query = "ZzGuestWidget", scope = "vanilla" });

        Verify("locate/zero-hits-out-of-scope", content);
    }

    // 前缀给了冒号却没给值
    [Fact]
    public async Task Locate_EmptyFilterValueNotice()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"));

        // 冒号后带空格是被支持的写法（`type: ZzWidget` 照样生效），故这里要的是**后面什么都没有**
        var content = await Run(new LocateTool(indexer, defs, catalog), new { query = "ZzWidget type:" });

        Verify("locate/empty-filter-value", content);
    }

    // 条件目录：五段共用一份 ConditionalReport，行内的键与整份的成因各印一半
    [Fact]
    public async Task Locate_ConditionalFolderTagAndFootnote()
    {
        var conditionalDir = _workspace.Dir("Core", "1.6", "CE");
        _workspace.WriteFile(Path.Combine("Core", "ZzWidget.cs"),
            "namespace Zz { public class ZzWidget { } }");
        _workspace.WriteFile(Path.Combine("Core", "1.6", "CE", "ZzWidgets.xml"),
            "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetGated</defName>\n  </ZzWidgetDef>\n</Defs>\n");

        var root = _workspace.Dir("Core");
        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var defs = new DefIndexer();
        defs.Scan(root);
        defs.FreezeIndex();

        var content = await Run(
            new LocateTool(
                indexer, defs, ScopeCatalog.Build([("vanilla", root)], null, null),
                conditional: ConditionalFolders.Build([
                    new ConditionalArea(conditionalDir, "1.6/CE", "CETeam.CombatExtended active", "vanilla")
                ])),
            new { query = "ZzWidget" });

        Verify("locate/conditional-folder", content);
    }

    // 零命中 + 查询里带短词。这一形最要紧：有结果时读者至少还能看到别的段落，零命中时整份
    // 返回只有「No results」一句，而「短词根本没被查过」这件事恰恰在这时最容易被读成
    // 「索引里没有」（见指导文档 §7）。
    [Fact]
    public async Task Locate_ZeroHitsWithShortToken()
    {
        var (indexer, defs, catalog) = BuildIndex(("ZzWidget.cs", "namespace Zz { public class ZzWidget { } }"));

        var content = await Run(
            new LocateTool(indexer, defs, catalog), new { query = "ZzAbsentThing 20" });

        Verify("locate/zero-hits-short-token", content);
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

    // extractClass 的类体上限折叠行（`+N more lines ('X' is M lines of a K-line file and the cap
    // is 2000; ...)`）。全服六处折叠行里唯一没有字节级基线的一形，而它同时是唯一一处「下一步」
    // 分两支的：这里取的是没传 methodName 那支。
    //
    // 类体故意做到恰好越过上限一行：那一行同时钉住 `+1 more lines` ——这一形不走全服共用的
    // 构词（表头的 entries / lines 都走），见指导文档 §7。
    [Fact]
    public async Task ReadCode_ClassCapFold()
    {
        // 上限 2000 数的是 classBody.Body 的行数（含类声明行、结尾的 `}` 与尾部空行），
        // 1997 条填充行恰好把它顶到 2001。
        var filler = string.Join("\n", Enumerable.Repeat("        // x", 1997));
        var body = $"namespace Zz\n{{\n    public class ZzHuge\n    {{\n{filler}\n    }}\n}}\n";
        var (indexer, _, catalog) = BuildIndex(("ZzHuge.cs", body));
        PathSecurity.Initialize([Path.Combine(_workspace.Root, "Core")]);

        var content = await Run(
            new ReadCodeTool(indexer, catalog), new { fileName = "ZzHuge.cs", extractClass = "ZzHuge" });

        Verify("read_code/class-cap-fold", content);
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

    // 分页折叠行：下一步是 `pass offset=N`，落不进 Fold.Line 的三分支，故走 Fold.Explicit
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

    // 只剩一项时的分页折叠行。表头那个 `12 entries` 走全服构词、同一份输出里四行之下的
    // `+1 more entries` 不走——两处数的是同一批东西，见指导文档 §7。
    [Fact]
    public async Task ListDirectory_PageFoldSingleRemaining()
    {
        var files = Enumerable.Range(0, 12).Select(i => ($"ZzEntry{i:D2}.cs", "// x\n")).ToArray();
        var (_, _, catalog) = BuildIndex(files);
        var core = Path.Combine(_workspace.Root, "Core");
        PathSecurity.Initialize([core]);

        var content = await Run(new ListDirectoryTool(catalog), new { path = core, limit = 11 });

        Verify("list_directory/page-fold-single", content);
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

    // 成员大纲的折叠行。它是全服唯一一条不从 Fold 出的折叠行——文本整段由
    // RimSearcher.Core 的 RoslynHelper 拼，够不到 Server 侧的 Output/（见指导文档 §5 末行），
    // 而此前一份字节级基线都没有：Contains 断言只钉住了 `+N more properties` 那半截，
    // 缩进、名词槽的构词、括号里那句下一步全无判据。
    //
    // 溢出数一单一复：属性溢 1（`+1 more property`）、方法溢 2，一份基线钉住两形。
    [Fact]
    public async Task Inspect_OutlineFold()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzHuge.cs",
                "namespace Zz { public class ZzHuge { "
                + "public int ZzA { get; set; } public int ZzB { get; set; } public int ZzC { get; set; } "
                + "public void ZzOne() { } public void ZzTwo() { } "
                + "public void ZzThree() { } public void ZzFour() { } } }"));

        var content = await Run(
            new InspectTool(indexer, defs, catalog), new { name = "ZzHuge", limit = 2 });

        Verify("inspect/outline-fold", content);
    }

    // Def 关联到的 C# 类型超过 10 个时的折叠行。这份判据立在接上 Fold.Explicit 之前，
    // 接的那一步才有字节级的闸可对；溢出恰好一条，一并钉住单复数走全服构词（见指导文档 §7）。
    [Fact]
    public async Task Inspect_LinkedTypesFold()
    {
        // 元素名必须各不相同：Def 解析按 XML 语义合并同名子元素（后者胜），11 个 `<compClass>`
        // 只会剩一个。认作类型引用的判据是「元素名以 Class/Worker 结尾」，故编号放在后缀之前。
        var links = string.Concat(
            Enumerable.Range(0, 11).Select(i => $"    <zzLink{i:D2}Class>Zz.ZzLinked{i:D2}</zzLink{i:D2}Class>\n"));
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidgetDef.cs", "namespace Zz { public class ZzWidgetDef { } }"),
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n"
                + links + "  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzWidgetAlpha" });

        Verify("inspect/linked-types-fold", content);
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

    // ---- M1：inspect 剩下的六形 ----
    //
    // 这个工具此前只有四份基线，而它的字面量体量是全服第二（`InspectTool` 641 行 131 处），
    // 于是「触碰它」等于没有闸。下面六形按返回点逐个盘出来，一形一份。

    // 合并 XML 一屏放不下时的**首次调用**：头 200 行 + 尾 50 行两段。表头走 F30 那套三态
    // （裸 N = 完整集，`N of M` = 被截了）——第十三轮盲测里被测方从「裸表头」归纳出「这就是
    // 完整的」并写进了交付答案，那条假规则的判据至今只活在源码注释里，输出侧一份闸都没有。
    [Fact]
    public async Task Inspect_DefXmlWindow()
    {
        var content = await Run(BuildLongDef(), new { name = "ZzLongAlpha" });

        Verify("inspect/def-xml-window", content);
    }

    // 续读窗口正好收到末尾：表头改口 `lines X-Y of Z`，末行是「读完了」而不是再指一次
    // xmlStartLine——指了就是死循环，而这一形是分页路径唯一的出口。
    [Fact]
    public async Task Inspect_DefXmlPagedTail()
    {
        var content = await Run(BuildLongDef(), new { name = "ZzLongAlpha", xmlStartLine = 300 });

        Verify("inspect/def-xml-paged-tail", content);
    }

    // 同名 def 分属两个 defType。返回的是其中一个，而那句 `_Note:` 是调用方唯一能看出
    // 「还有另一个同名的」的地方；缺席时这份返回读起来就是「这个名字只有这一个 def」。
    [Fact]
    public async Task Inspect_DefTypeAmbiguous()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzShared.xml",
                "<Defs>\n  <ZzAlphaDef>\n    <defName>ZzSharedName</defName>\n  </ZzAlphaDef>\n"
                + "  <ZzBetaDef>\n    <defName>ZzSharedName</defName>\n  </ZzBetaDef>\n</Defs>\n"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzSharedName" });

        Verify("inspect/def-type-ambiguous", content);
    }

    // def 模式从不读 limit，而 schema 里 limit 是这个工具的正式参数。传了不说一声，
    // 调用方拿到的是一份「limit 生效了」的默认解读——而 def 模式确实会截断，只是换了个参数。
    [Fact]
    public async Task Inspect_DefLimitIgnoredNote()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzWidgetDef.cs", "namespace Zz { public class ZzWidgetDef { } }"),
            ("ZzWidgets.xml",
                "<Defs>\n  <ZzWidgetDef>\n    <defName>ZzWidgetAlpha</defName>\n"
                + "    <label>alpha widget</label>\n  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(
            new InspectTool(indexer, defs, catalog), new { name = "ZzWidgetAlpha", limit = 5 });

        Verify("inspect/def-limit-ignored", content);
    }

    // def 沿 ParentName 链合并：链行印的是链本身，而下面那份 XML 是合并后的结果——
    // 与类型模式同名的那行语义正好相反（类型模式：继承成员**不**在下面）。
    [Fact]
    public async Task Inspect_DefParentChain()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzChain.xml",
                "<Defs>\n  <ZzWidgetDef Name=\"ZzWidgetBase\" Abstract=\"True\">\n"
                + "    <label>base widget</label>\n  </ZzWidgetDef>\n"
                + "  <ZzWidgetDef ParentName=\"ZzWidgetBase\">\n    <defName>ZzWidgetChild</defName>\n"
                + "  </ZzWidgetDef>\n</Defs>\n"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzWidgetChild" });

        Verify("inspect/def-parent-chain", content);
    }

    // 「scope 内找不到」与「根本不存在」是两件事，混为一谈会让调用方断言符号不存在。
    // 两形各一份：前者必须点名去哪儿找得到，后者只指路 locate。
    [Fact]
    public async Task Inspect_NotFoundInScopeButElsewhere()
    {
        var (indexer, defs, catalog) = BuildTwoSourceIndex(
            [("ZzHere.cs", "namespace Zz { public class ZzHere { } }")],
            [("ZzThere.cs", "namespace Zz { public class ZzThere { } }")]);

        var content = await Run(
            new InspectTool(indexer, defs, catalog), new { name = "ZzThere", scope = "vanilla" });

        Verify("inspect/not-found-in-scope", content);
    }

    [Fact]
    public async Task Inspect_NotFoundAnywhere()
    {
        var (indexer, defs, catalog) = BuildIndex(
            ("ZzHere.cs", "namespace Zz { public class ZzHere { } }"));

        var content = await Run(new InspectTool(indexer, defs, catalog), new { name = "ZzNoSuchThing" });

        Verify("inspect/not-found-anywhere", content);
    }

    // 323 行合并 XML：> 头 200 + 尾 50 + 50，故首次调用走截断那一支。
    private InspectTool BuildLongDef()
    {
        var fields = string.Concat(
            Enumerable.Range(0, 320).Select(i => $"    <zzField{i:D3}>{i}</zzField{i:D3}>\n"));

        var (indexer, defs, catalog) = BuildIndex(
            ("ZzLongDef.cs", "namespace Zz { public class ZzLongDef { } }"),
            ("ZzLong.xml",
                "<Defs>\n  <ZzLongDef>\n    <defName>ZzLongAlpha</defName>\n"
                + fields + "  </ZzLongDef>\n</Defs>\n"));

        return new InspectTool(indexer, defs, catalog);
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

    // diff 报告里的两条折叠行——接上 Fold.Explicit 的另外两处。
    //
    // 这两条是全服**最偏离**共用文法的：翻页那条把下一步写在破折号后面而不是括号里，
    // 成员那条的 `+` 曾经也是缺的。故先各立一份字节级判据。
    [Fact]
    public async Task SyncSources_DiffPageFold()
    {
        var content = await Run(BuildSyncWithArchive(), new { action = "diff", limit = 5 });

        Verify("sync_sources/diff-page-fold", content);
    }

    // 成员折叠行只在**列举**路径上（granularity='members' 逐文件展开），不在 `file=` 的
    // 单文件差异报告里——后者是另一段代码，一条不折。故这里不传 file。
    [Fact]
    public async Task SyncSources_DiffMemberFold()
    {
        var content = await Run(
            BuildSyncWithArchive(), new { action = "diff", granularity = "members", limit = 5 });

        Verify("sync_sources/diff-member-fold", content);
    }

    // ---- M1：sync_sources 剩下的五形 ----

    // 单文件行级 diff。这条路径与列表模式是两段各自独立的代码（`RunFileDiff`），此前
    // 一份基线都没有，而它是这个工具唯一会印代码正文的返回。
    [Fact]
    public async Task SyncSources_DiffSingleFile()
    {
        var content = await Run(
            BuildSyncWithArchive(), new { action = "diff", file = "ZzSync00.cs", limit = 50 });

        Verify("sync_sources/diff-single-file", content);
    }

    // 单文件 + granularity='members'：已经收窄到一个文件，成员清单不再截断，末行改成
    // 「拿 method 去看行级差异」。这一形此前只认复数 'members'，两种写法的输出如今同出一处。
    [Fact]
    public async Task SyncSources_DiffSingleFileMembers()
    {
        var content = await Run(
            BuildSyncWithArchive(),
            new { action = "diff", file = "ZzChanged.cs", granularity = "members" });

        Verify("sync_sources/diff-single-file-members", content);
    }

    // 要的版本比保留的还老：夹到最老那一代并**说明夹过**。夹到最新一代会给出与不传 version
    // 完全相同的结果，等于把参数悄悄吃掉——那条判据只有这一形照得到。
    [Fact]
    public async Task SyncSources_DiffVersionClampedToOldest()
    {
        var content = await Run(
            BuildSyncWithArchive(), new { action = "diff", file = "ZzSync00.cs", version = 99, limit = 50 });

        Verify("sync_sources/diff-version-clamped", content);
    }

    // source_history_depth = 0：diff 是确定性报错，且指引必须带时序（改 config → 重启 →
    // 先跑一次 sync），否则照做后重跑拿到的是逐字相同的这一句。
    [Fact]
    public async Task SyncSources_DiffWithHistoryDisabled()
    {
        var source = _workspace.Dir("src");
        _workspace.WriteFile(Path.Combine("src", "ZzWidget.cs"), "// current\n");

        var config = new AppConfig { SourceHistoryDepth = 0, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = source,
            AssemblyPaths = [_workspace.Dir("assemblies")],
        };

        var service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));
        PathSecurity.Initialize([source]);

        var content = await Run(new SyncSourcesTool(service), new { action = "diff" });

        Verify("sync_sources/diff-history-disabled", content);
    }

    // 开着历史但还没归档过：与上一形的措辞必须分开——一个要改配置重启，一个只要跑一次 sync。
    [Fact]
    public async Task SyncSources_DiffWithNothingArchivedYet()
    {
        var source = _workspace.Dir("src");
        _workspace.WriteFile(Path.Combine("src", "ZzWidget.cs"), "// current\n");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = source,
            AssemblyPaths = [_workspace.Dir("assemblies")],
        };

        var service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));
        PathSecurity.Initialize([source]);

        var content = await Run(new SyncSourcesTool(service), new { action = "diff" });

        Verify("sync_sources/diff-nothing-archived", content);
    }

    // 13 个变更文件（limit=5 时翻页折叠行成立），其中一个删掉了 25 个成员
    // （超过每文件成员列举上限，成员折叠行成立）。与 OutputGrammarGateTests.BuildSync 同形。
    private SyncSourcesTool BuildSyncWithArchive()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");

        for (var i = 0; i < 12; i++)
        {
            _workspace.WriteFile(Path.Combine("src", $"ZzSync{i:D2}.cs"), $"// old {i}\n");
            _workspace.WriteFile(Path.Combine("staging", $"ZzSync{i:D2}.cs"), $"// new {i}\n");
        }

        var members = new StringBuilder("namespace Zz {\n  public class ZzChanged {\n");
        for (var i = 0; i < 25; i++) members.Append($"    public void ZzGone{i:D2}() {{ }}\n");
        members.Append("  }\n}");
        _workspace.WriteFile(Path.Combine("src", "ZzChanged.cs"), members.ToString());
        _workspace.WriteFile(Path.Combine("staging", "ZzChanged.cs"),
            "namespace Zz {\n  public class ZzChanged { }\n}");

        var config = new AppConfig { SourceHistoryDepth = 2, GameVersion = "1.6" };
        var entry = new SourcePathEntry
        {
            Name = "Core",
            Path = source,
            AssemblyPaths = [_workspace.Dir("assemblies")],
        };

        var service = new SourceSyncService(config, new ResolvedSources([entry], []), _workspace.Dir("cache"));
        service.History.Capture("Core", source, staging);

        // 归档的是旧内容、现盘换成新内容 → diff 报出全部 13 个文件的变化
        for (var i = 0; i < 12; i++)
            _workspace.WriteFile(Path.Combine("src", $"ZzSync{i:D2}.cs"), $"// new {i}\n");
        _workspace.WriteFile(
            Path.Combine("src", "ZzChanged.cs"), "namespace Zz {\n  public class ZzChanged { }\n}");

        PathSecurity.Initialize([source]);

        return new SyncSourcesTool(service);
    }
}
