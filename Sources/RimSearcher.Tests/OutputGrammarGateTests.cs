using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 输出文法常驻闸的**矩阵 + 遍历器**那一层（断言器在 GrammarRules）。
//
// 台账 §七当年把这一条判成「做不了」，理由是「要把『全语料合乎共用文法』变成常驻闸，须先有
// 一份可入库的语料快照，而语料来自本机 RimWorld 安装，不能进仓」。**那个判据错在前提**：闸要
// 守的从来不是「真实语料」，而是**全部输出形态**。真实语料只是碰巧能撞到多种形态的一个来源，
// 而且撞得并不全——零命中、单源、恰好卡在断层收口、恰好卡在硬上限这些分支，真语料要靠运气才
// 踩得到，合成 fixture 是**指定**它出现。
//
// 于是这里缺的不是一份进不了仓的资产，是一张表：工具 × 输出分支。
//
// **不适用的格要显式记为不适用**，且要写明理由。少了这一条，矩阵会悄悄漏掉一整列——而那正是
// 历史上那批缺陷的形状（`trace usages` 有的尾注 `search_regex` 没有；`at least` 只在 locate
// 一处实现）。TheMatrixCoversEveryToolAndBranch 就是守这个的。
[Collection("PathSecurity")]
public class OutputGrammarGateTests : IDisposable
{
    // ---- 形态维度。不是笛卡尔积，是**已知会改变输出形态**的那些分支 ----
    private const string Empty = "零命中";
    private const string Single = "单条";
    private const string AtLimit = "恰好等于 limit";
    private const string OverLimit = "超过 limit";
    private const string OverServerCap = "超过服务端上限";
    private const string ScoreGap = "断层收口截";
    private const string ScanStopped = "扫描停在预览上限";
    private const string TotalIsFloor = "总数是下界";
    private const string MultiSource = "跨源";
    private const string PinnedSource = "scope 钉死单源";
    private const string OutOfScope = "有越界命中";
    private const string MissingArg = "参数缺失";
    private const string UnknownArg = "参数名认不出";
    private const string NoSuchPath = "路径不存在";
    private const string BadLimit = "非法 limit";

    // 与上面的 Single 分界要写死，否则这两维迟早被读成同一件事，而它们照的不是同一批槽：
    // **Single 管主计数**——结果集只有一条，走的是没有截断那条路（`1 XML def` / `1 entry`）；
    // **这一维管主计数之外的槽取到 1**——折叠增量、预览行配额、属格里的总数。
    //
    // 历史缺陷全部落在后者：R30 那条 `... +1 more C# types` 是折叠增量，`first 1 preview lines`
    // 是预览配额，两者都不是「结果只有一条」，故 Single 那一列从来照不到它们。要紧的是
    // **闸的规则本来就抓得住**——规则二甲是纯结构判定（`1 preview lines` 里 lines 以 s 结尾又不在
    // NotNouns 里 → 判违规），压根不查词表。缺的从来只是喂给它的 fixture：两个 ScanStopped 格
    // 用的都是 limit = 4，十五个维度里没有一个把计数逼到 1。
    private const string SingleCount = "折叠/配额侧的计数恰好为 1";

    private static readonly string[] AllBranches =
    [
        Empty, Single, SingleCount, AtLimit, OverLimit, OverServerCap, ScoreGap, ScanStopped,
        TotalIsFloor, MultiSource, PinnedSource, OutOfScope, MissingArg, UnknownArg, NoSuchPath,
        BadLimit,
    ];

    private static readonly string[] AllTools =
    [
        "locate", "inspect", "read_code", "trace", "search_regex", "list_directory", "sync_sources",
    ];

    // Run 为 null 即「不适用」，此时 Why 必须写明为什么。Expect 是这一格**打算**产出的那个形态
    // 的记号——没有它，一格 fixture 悄悄退化成另一种形态时闸照样绿，覆盖率就成了错觉。
    private sealed record Cell(string Tool, string Branch, Func<Task<string>>? Run, string Why = "", string? Expect = null);

    private readonly TempWorkspace _workspace = new();
    private readonly string _coreRoot;
    private readonly LocateTool _locate;
    private readonly InspectTool _inspect;
    private readonly ReadCodeTool _readCode;
    private readonly TraceTool _trace;
    private readonly SearchRegexTool _searchRegex;
    private readonly ListDirectoryTool _listDirectory = new();
    private readonly SyncSourcesTool _sync;
    private readonly LocateTool _locateOverCap;

    // 210 > 服务端硬上限 200，故 limit:'all' 这一格真的会撞到上限而不是只撞到 limit
    private const int BulkCount = 210;
    // 81 > search_regex 的 50 个文件上限；且每文件 3 行预览合计 243 > 200 那道服务端预览上限
    // （文件数上限只夹「列出来的」，扫描本身是被预览数停住的，两道闸各管一头）
    private const int ScanFileCount = 80;

    public OutputGrammarGateTests()
    {
        _coreRoot = _workspace.Dir("Core");
        var modRoot = _workspace.Dir("Mods");

        _workspace.WriteFile(Path.Combine("Core", "ZzWidget.cs"),
            "namespace Zz { public class ZzWidget { public void ZzWidgetTick() { } public int ZzWidgetField; } }");

        // 名字与 ZzWidget 只沾一点边，故落在断层收口的另一侧（相对首条掉 40 分以上）
        _workspace.WriteFile(Path.Combine("Core", "ZzWidgetHolderOfManyUnrelatedThingsIndeed.cs"),
            "namespace Zz { public class ZzWidgetHolderOfManyUnrelatedThingsIndeed { } }");

        _workspace.WriteFile(Path.Combine("Core", "ZzBase.cs"), "namespace Zz { public class ZzBase { } }");

        // 一个文件装 210 个类型：同时喂饱 locate 的类型段、成员段与 trace inheritors，
        // 而磁盘上只多一个文件（210 个小文件在 Windows 上的 I/O 才是这套 fixture 的大头）。
        var bulk = new StringBuilder("namespace Zz {\n");
        for (var i = 0; i < BulkCount; i++)
            bulk.Append($"  public class ZzBulk{i:D3} : ZzBase {{ public void ZzBulkTick() {{ }} }}\n");
        bulk.Append('}');
        _workspace.WriteFile(Path.Combine("Core", "ZzBulk.cs"), bulk.ToString());

        // 大纲折叠用：一个类型里放足够多的方法。210 > 服务端硬上限 200，故 limit:'all' 这一格
        // 也能撞到上限而不是只撞到 limit
        var wide = new StringBuilder("namespace Zz {\n  public class ZzWide {\n");
        for (var i = 0; i < BulkCount; i++) wide.Append($"    public void ZzWideStep{i:D3}() {{ }}\n");
        wide.Append("  }\n}");
        _workspace.WriteFile(Path.Combine("Core", "ZzWide.cs"), wide.ToString());

        // 查询词只以**子串**形式出现（不在开头、也不在任何词的开头），故落在断层收口的另一侧：
        // 子串支封顶 50 分，相对首条的 100 分掉了 50，超过 40 那道收口线。
        _workspace.WriteFile(Path.Combine("Core", "ZzHolderOfZzWidgetParts.cs"),
            "namespace Zz { public class ZzHolderOfZzWidgetParts { } }");

        // 单文件多命中 → 每文件折叠行（`... +N more of M matching lines in this file`）
        _workspace.WriteFile(Path.Combine("Core", "ZzNeedle.cs"),
            string.Concat(Enumerable.Repeat("// ZzNeedleMark\n", 30)));

        // 文件数超过 search_regex 的 50 上限；每文件 3 行使总命中行数（30 + 60×3 = 210）
        // 也越过 200 那道服务端上限
        for (var i = 0; i < ScanFileCount; i++)
            _workspace.WriteFile(Path.Combine("Core", $"ZzScan{i:D3}.cs"),
                "// ZzNeedleMark\n// ZzNeedleMark\n// ZzNeedleMark\n");

        var defs = new StringBuilder("<Defs>\n");
        for (var i = 0; i < 12; i++)
            defs.Append($"  <ThingDef><defName>ZzThing{i:D3}</defName><label>zz thing {i}</label>"
                        + "<description>ZzNeedleMark</description></ThingDef>\n");
        // 名字与那 12 条都拉开距离，故「单条」这一格真的只回一条——ZzThing000 不行，
        // 它与 ZzThing001 只差一个字符，整批都够得上拼写容错那一支
        defs.Append("  <ThingDef><defName>ZzSolitaryBeacon</defName><label>lone</label></ThingDef>\n");

        // inspect 的「计数恰好为 1」那一格用的：Linked C# Types 段的上限写死在 10，故 11 个链接
        // 恰好溢出 1 个。**这是 inspect 唯一一个能取到 1 的计数槽**——大纲折叠取不到，因为
        // ScopeAndLimitArgs.GetDisplayLimit 在 limit >= 200 时直接返回 Unlimited（不折叠），而 ZzWide 有
        // 210 个成员，limit 只能取到 199，溢出至少 11 个。
        //
        // 元素名各不相同：Def 解析按 XML 语义合并同名子元素（后者胜），11 个同名 Class 元素只会
        // 剩一个。认作类型引用的判据是「元素名以 Class/Worker 结尾」，故编号放在后缀之前。
        // 名字与那 12 个 ZzThing 拉开距离，免得挤进 `def:ZzThing` 那两格的计数里。
        defs.Append("  <ThingDef><defName>ZzLinkHub</defName><label>hub</label>\n");
        for (var i = 0; i < 11; i++)
            defs.Append($"    <zzLink{i:D2}Class>Zz.ZzLinked{i:D2}</zzLink{i:D2}Class>\n");
        defs.Append("  </ThingDef>\n");

        defs.Append("</Defs>");
        _workspace.WriteFile(Path.Combine("Core", "Defs", "ZzThings.xml"), defs.ToString());

        // 重名文件（read_code 的「多份同名」分支）与只存在于 HAR 的类型（越界分支）
        _workspace.WriteFile(Path.Combine("Core", "ZzShared.cs"), "namespace Zz { public class ZzSharedCore { } }");
        _workspace.WriteFile(Path.Combine("Mods", "ZzShared.cs"), "namespace Zz { public class ZzSharedHar { } }");
        _workspace.WriteFile(Path.Combine("Mods", "ZzWidgetHar.cs"), "namespace Zz { public class ZzWidgetHar { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(_coreRoot);
        indexer.Scan(modRoot);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.Scan(_coreRoot);
        defIndexer.FreezeIndex();

        // 默认组只含 vanilla，故 HAR 的命中天然落在 scope 之外——「有越界命中」这一格不必另造语料
        var catalog = ScopeCatalog.Build(
            [("vanilla", _coreRoot), ("HAR", modRoot)],
            new Dictionary<string, List<string>> { ["base"] = ["vanilla"] },
            "base");

        _locate = new LocateTool(indexer, defIndexer, catalog);
        _inspect = new InspectTool(indexer, defIndexer, catalog);
        _readCode = new ReadCodeTool(indexer, catalog);
        _trace = new TraceTool(indexer, catalog);
        _searchRegex = new SearchRegexTool(indexer, catalog);
        _locateOverCap = BuildOverCapLocate();
        _sync = BuildSync();

        PathSecurity.ResetForTests();
        PathSecurity.Initialize([_coreRoot, modRoot, _workspace.Dir("src")]);
    }

    // 成员名字键超过一次展开上限 → 表头改口 `at least`。键数由 SourceIndexer 那个常量定，
    // 故这里跟着它走而不是写死一个数——上限一调，这一格自动仍然刚好越过它。
    private LocateTool BuildOverCapLocate()
    {
        var root = _workspace.Dir("OverCap");
        var sb = new StringBuilder("namespace Zz {\n  public class ZzOverCap {\n");
        for (var i = 0; i <= SourceIndexer.MemberQualifiedKeyCap; i++)
            sb.Append($"    public void Zqc{i:D6}() {{ }}\n");
        sb.Append("  }\n}");
        _workspace.WriteFile(Path.Combine("OverCap", "ZzOverCap.cs"), sb.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defs = new DefIndexer();
        defs.FreezeIndex();

        return new LocateTool(indexer, defs, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private SyncSourcesTool BuildSync()
    {
        var source = _workspace.Dir("src");
        var staging = _workspace.Dir("staging");

        // 12 个变更文件 → limit=5 时翻页折叠行成立；其中一个改了 25 个成员 → 成员折叠行成立
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
        _workspace.WriteFile(Path.Combine("src", "ZzChanged.cs"), "namespace Zz {\n  public class ZzChanged { }\n}");

        return new SyncSourcesTool(service);
    }

    public void Dispose()
    {
        PathSecurity.ResetForTests();
        _workspace.Dispose();
    }

    // 拼法与 RimSearcher.cs 的分发层一致，因为**调用方读到的是那一段**，不是 ExecuteAsync 的
    // 返回值本身。两处必须一样，否则「参数缺失」「参数名认不出」这两列在闸这边永远是空的：
    //   - 缺参走 ToolArgumentException，由分发层转成一条带纠正提示的 isError 结果；
    //   - 认不出的键由分发层补一句尾注，工具自己不知道有这回事。
    // 两者都是**返回文本**，同样受这九条约束（历史上 R19 的名词槽、F21 的结尾空行都出现过在
    // 尾注这一段上）。
    //
    // 唯独不跟分发层做那次 TrimEnd：这里要守的是「每一段自己就是干净的」，而不是「拼完再擦
    // 一次」。擦过之后 F21 那条断言在这台闸上就恒真了，等于白写。
    private static async Task<string> Run(ITool tool, object payload)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        }
        catch (ToolArgumentException ex)
        {
            result = new ToolResult(ex.Message, true);
        }

        return result.Content + ToolArgs.UnknownKeyNotice(tool, args.RootElement);
    }

    private List<Cell> Matrix()
    {
        var core = _coreRoot;

        return
        [
            // ---- locate ----
            new("locate", Empty, () => Run(_locate, new { query = "ZzNothingAnywhere" }), Expect: "No results"),
            new("locate", Single, () => Run(_locate, new { query = "def:ZzSolitaryBeacon" }), Expect: "1 XML def"),
            new("locate", SingleCount, () => Run(_locate, new { query = "def:ZzThing", limit = 11 }), Expect: "+1 more"),
            new("locate", AtLimit, () => Run(_locate, new { query = "def:ZzThing", limit = 12 }), Expect: "12 XML defs"),
            new("locate", OverLimit, () => Run(_locate, new { query = "type:ZzBulk", limit = 5 }), Expect: "... +"),
            new("locate", OverServerCap, () => Run(_locate, new { query = "type:ZzBulk", limit = "all" }), Expect: "server cap 200 reached"),
            new("locate", ScoreGap, () => Run(_locate, new { query = "type:ZzWidget", limit = 10 }), Expect: "lower relevance"),
            new("locate", ScanStopped, null, "locate 不打开文件正文，没有预览，也就没有『扫描停在预览上限』这一形"),
            new("locate", TotalIsFloor, () => Run(_locateOverCap, new { query = "method:Zqc", limit = 3 }), Expect: "of at least"),
            new("locate", MultiSource, () => Run(_locate, new { query = "type:ZzWidget", scope = "all" }), Expect: "[HAR]"),
            new("locate", PinnedSource, () => Run(_locate, new { query = "type:ZzWidget", scope = "vanilla" })),
            new("locate", OutOfScope, () => Run(_locate, new { query = "type:ZzWidgetHar" }), Expect: "Outside scope"),
            new("locate", MissingArg, () => Run(_locate, new { })),
            new("locate", UnknownArg, () => Run(_locate, new { query = "ZzWidget", zzBogus = 1 }), Expect: "Ignored unknown"),
            new("locate", NoSuchPath, null, "locate 不收路径参数"),
            new("locate", BadLimit, () => Run(_locate, new { query = "ZzWidget", limit = -3 })),

            // ---- inspect ----
            new("inspect", Empty, () => Run(_inspect, new { name = "ZzNothingAnywhere" })),
            new("inspect", Single, () => Run(_inspect, new { name = "ZzWidget" })),
            new("inspect", SingleCount, () => Run(_inspect, new { name = "ZzLinkHub" }), Expect: "+1 more type"),
            new("inspect", AtLimit, () => Run(_inspect, new { name = "ZzWide", limit = BulkCount })),
            new("inspect", OverLimit, () => Run(_inspect, new { name = "ZzWide", limit = 5 }), Expect: "... +"),
            new("inspect", OverServerCap, () => Run(_inspect, new { name = "ZzWide", limit = "all" })),
            new("inspect", ScoreGap, null, "inspect 只吃精确名字，没有打分，也就没有断层"),
            new("inspect", ScanStopped, null, "inspect 不扫文件正文"),
            new("inspect", TotalIsFloor, null, "inspect 的每个计数都来自一次完整枚举（成员表 / 继承链 / 合并后的 XML 行数），没有下界形态"),
            new("inspect", MultiSource, () => Run(_inspect, new { name = "ZzSharedCore", scope = "all" })),
            new("inspect", PinnedSource, () => Run(_inspect, new { name = "ZzWidget", scope = "vanilla" })),
            new("inspect", OutOfScope, () => Run(_inspect, new { name = "ZzWidgetHar" })),
            new("inspect", MissingArg, () => Run(_inspect, new { })),
            new("inspect", UnknownArg, () => Run(_inspect, new { name = "ZzWidget", zzBogus = 1 }), Expect: "Ignored unknown"),
            new("inspect", NoSuchPath, null, "inspect 不收路径参数"),
            new("inspect", BadLimit, () => Run(_inspect, new { name = "ZzWide", limit = -3 })),

            // ---- read_code ----
            new("read_code", Empty, () => Run(_readCode, new { path = "ZzWidget", methodName = "ZzNoSuchMethod" })),
            new("read_code", Single, () => Run(_readCode, new { path = "ZzWidget", methodName = "ZzWidgetTick" })),
            new("read_code", SingleCount, () => Run(_readCode, new { path = "ZzNeedle", startLine = 0, lineCount = 29 }), Expect: "+1 more"),
            new("read_code", AtLimit, () => Run(_readCode, new { path = "ZzNeedle", startLine = 1, lineCount = 30 })),
            new("read_code", OverLimit, () => Run(_readCode, new { path = "ZzNeedle", startLine = 1, lineCount = 5 }), Expect: "... +"),
            new("read_code", OverServerCap, null, "读取上限就是 lineCount 自己（封顶 2000），没有第二道服务端上限——故不存在『limit 已给足仍被截』这一形"),
            new("read_code", ScoreGap, null, "read_code 不打分"),
            new("read_code", ScanStopped, null, "read_code 只读一个文件，不扫描"),
            new("read_code", TotalIsFloor, null, "行数来自一次完整读取，是确定值"),
            new("read_code", MultiSource, () => Run(_readCode, new { path = "ZzShared", scope = "all" }), Expect: "share this name"),
            new("read_code", PinnedSource, () => Run(_readCode, new { path = "ZzShared", scope = "vanilla" })),
            new("read_code", OutOfScope, () => Run(_readCode, new { path = "ZzWidgetHar", scope = "vanilla" })),
            new("read_code", MissingArg, () => Run(_readCode, new { })),
            new("read_code", UnknownArg, () => Run(_readCode, new { path = "ZzWidget", zzBogus = 1 }), Expect: "Ignored unknown"),
            new("read_code", NoSuchPath, () => Run(_readCode, new { path = "ZzNoSuchFileAnywhere" })),
            new("read_code", BadLimit, () => Run(_readCode, new { path = "ZzWidget", startLine = 1, lineCount = -3 })),

            // ---- trace ----
            new("trace", Empty, () => Run(_trace, new { symbol = "ZzNothingAnywhere", mode = "usages" })),
            new("trace", Single, () => Run(_trace, new { symbol = "ZzWidgetTick", mode = "usages" })),
            new("trace", SingleCount, () => Run(_trace, new { symbol = "ZzNeedleMark", mode = "usages", limit = 1 }), Expect: "first 1 preview line in scope"),
            new("trace", AtLimit, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", limit = BulkCount })),
            new("trace", OverLimit, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", limit = 5 }), Expect: "... +"),
            new("trace", OverServerCap, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", limit = "all" }), Expect: "server cap"),
            new("trace", ScoreGap, null, "trace 收的是精确符号名，两种模式都不打分"),
            new("trace", ScanStopped, () => Run(_trace, new { symbol = "ZzNeedleMark", mode = "usages", limit = 4 }), Expect: "more matches exist"),
            new("trace", TotalIsFloor, () => Run(_trace, new { symbol = "ZzNeedleMark", mode = "usages", limit = "all" })),
            new("trace", MultiSource, () => Run(_trace, new { symbol = "ZzNeedleMark", mode = "usages", scope = "all", limit = 5 })),
            new("trace", PinnedSource, () => Run(_trace, new { symbol = "ZzNeedleMark", mode = "usages", scope = "vanilla", limit = 5 })),
            new("trace", OutOfScope, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", limit = 5 })),
            new("trace", MissingArg, () => Run(_trace, new { mode = "usages" })),
            new("trace", UnknownArg, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", zzBogus = 1 }), Expect: "Ignored unknown"),
            new("trace", NoSuchPath, null, "trace 不收路径参数"),
            new("trace", BadLimit, () => Run(_trace, new { symbol = "ZzBase", mode = "inheritors", limit = -3 })),

            // ---- search_regex ----
            new("search_regex", Empty, () => Run(_searchRegex, new { pattern = "ZzNothingAnywhere" }), Expect: "No matches"),
            new("search_regex", Single, () => Run(_searchRegex, new { pattern = "ZzWidgetField" })),
            new("search_regex", SingleCount, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = 1 }), Expect: "first 1 preview line in scope"),
            new("search_regex", AtLimit, () => Run(_searchRegex, new { pattern = "ZzWidgetField", limit = 1 })),
            new("search_regex", OverLimit, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = 200 }), Expect: "... +"),
            new("search_regex", OverServerCap, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = "all" }), Expect: "server cap"),
            new("search_regex", ScoreGap, null, "search_regex 是正则命中，不打分"),
            new("search_regex", ScanStopped, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = 4 }), Expect: "more matches exist"),
            new("search_regex", TotalIsFloor, null, "下界形态的成因是『有文件没扫全』（读不开 / 撞到 20000 行闸），两者都要靠真实磁盘故障或超大文件才触发，合成语料造不出来——这一格由 SearchRegexHonestyTests 用注入的诊断计数覆盖"),
            new("search_regex", MultiSource, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", scope = "all", limit = 6 })),
            new("search_regex", PinnedSource, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", scope = "vanilla", limit = 6 })),
            new("search_regex", OutOfScope, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = 6 }), Expect: "never opened"),
            new("search_regex", MissingArg, () => Run(_searchRegex, new { })),
            new("search_regex", UnknownArg, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = 3, zzBogus = 1 }), Expect: "Ignored unknown"),
            new("search_regex", NoSuchPath, null, "search_regex 不收路径参数（fileFilter 是后缀过滤，不是路径）"),
            new("search_regex", BadLimit, () => Run(_searchRegex, new { pattern = "ZzNeedleMark", limit = -3 })),

            // ---- list_directory ----
            new("list_directory", Empty, () => Run(_listDirectory, new { path = Path.Combine(_workspace.Dir("Core"), "Defs"), offset = 999 }), Expect: "past the end"),
            new("list_directory", Single, () => Run(_listDirectory, new { path = Path.Combine(core, "Defs") }), Expect: "1 entry"),
            new("list_directory", SingleCount, () => Run(_listDirectory, new { path = _workspace.Dir("Mods"), limit = 1 }), Expect: "+1 more"),
            new("list_directory", AtLimit, () => Run(_listDirectory, new { path = Path.Combine(core, "Defs"), limit = 1 })),
            new("list_directory", OverLimit, () => Run(_listDirectory, new { path = core, limit = 5 }), Expect: "... +"),
            new("list_directory", OverServerCap, null, "服务端上限是每页 1000 条，而它与 limit 是同一道闸（limit 夹到 1000），撞上时走的仍是『超过 limit』那一形，括号里换一句 server cap 而已"),
            new("list_directory", ScoreGap, null, "list_directory 按名字排序枚举，不打分"),
            new("list_directory", ScanStopped, null, "list_directory 不读文件内容"),
            new("list_directory", TotalIsFloor, null, "条目总数来自一次完整枚举，是确定值"),
            new("list_directory", MultiSource, null, "list_directory 收的是一个绝对目录，天然只属于一个源，没有跨源形态"),
            new("list_directory", PinnedSource, null, "同上：路径自己就把源钉死了，没有 scope 参数"),
            new("list_directory", OutOfScope, null, "同上"),
            new("list_directory", MissingArg, () => Run(_listDirectory, new { })),
            new("list_directory", UnknownArg, () => Run(_listDirectory, new { path = core, limit = 3, zzBogus = 1 }), Expect: "Ignored unknown"),
            new("list_directory", NoSuchPath, () => Run(_listDirectory, new { path = Path.Combine(core, "ZzNoSuchDirectory") })),
            new("list_directory", BadLimit, () => Run(_listDirectory, new { path = core, limit = -3 })),

            // ---- sync_sources ----
            new("sync_sources", Empty, () => Run(_sync, new { action = "diff", file = "ZzSync00.cs", version = "ZzNoSuchVersion" })),
            new("sync_sources", Single, () => Run(_sync, new { action = "diff", file = "ZzSync00.cs" })),
            new("sync_sources", SingleCount, () => Run(_sync, new { action = "diff", limit = 12 }), Expect: "+1 more"),
            new("sync_sources", AtLimit, () => Run(_sync, new { action = "diff", limit = 13 })),
            new("sync_sources", OverLimit, () => Run(_sync, new { action = "diff", limit = 5 }), Expect: "... +"),
            new("sync_sources", OverServerCap, null, "limit 的服务端封顶是 2000，而一次 sync 的变更文件数远达不到；撞上时走的仍是『超过 limit』那一形"),
            new("sync_sources", ScoreGap, null, "sync_sources 报的是文件哈希差异，不打分"),
            new("sync_sources", ScanStopped, null, "diff 一次读完两侧内容，没有扫描配额"),
            new("sync_sources", TotalIsFloor, null, "变更集来自两侧文件表的完整比对，是确定值"),
            new("sync_sources", MultiSource, () => Run(_sync, new { action = "check" })),
            new("sync_sources", PinnedSource, () => Run(_sync, new { action = "diff", limit = 5, source = "Core" })),
            new("sync_sources", OutOfScope, null, "sync_sources 不做 scope 过滤：它报的是被配置的每一个源，没有『落在 scope 之外』这回事"),
            new("sync_sources", MissingArg, () => Run(_sync, new { action = "diff", granularity = "members" })),
            new("sync_sources", UnknownArg, () => Run(_sync, new { action = "check", zzBogus = 1 }), Expect: "Ignored unknown"),
            new("sync_sources", NoSuchPath, () => Run(_sync, new { action = "diff", file = "ZzNoSuchFile.cs" })),
            new("sync_sources", BadLimit, () => Run(_sync, new { action = "diff", limit = -3 })),
        ];
    }

    // 遍历器。失败时报 `(工具, 分支, 断言, 原文行)` 四元组——四个都要有：只报断言名，
    // 下一个人得把 112 格重跑一遍才知道是哪一格红的。
    [Fact]
    public async Task EveryShapeInTheMatrix_ObeysTheSharedGrammar()
    {
        var failures = new List<string>();

        foreach (var cell in Matrix())
        {
            if (cell.Run == null) continue;

            var text = await cell.Run();

            foreach (var violation in GrammarRules.Check(text))
                failures.Add($"{cell.Tool} / {cell.Branch} / {violation}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} 处违反共用文法：\n" + string.Join("\n", failures));
    }

    // fixture 退化检测。一格本来打算撞出「服务端上限」那一形，语料一改就悄悄变成「超过 limit」，
    // 而两形的输出都合文法——闸照绿，覆盖率成了错觉。故每一格打算产出的记号要单独钉住。
    [Fact]
    public async Task EveryCellStillProducesTheShapeItWasWrittenFor()
    {
        var missing = new List<string>();

        foreach (var cell in Matrix())
        {
            if (cell.Run == null || cell.Expect == null) continue;

            var text = await cell.Run();
            if (!text.Contains(cell.Expect, StringComparison.Ordinal))
                missing.Add($"{cell.Tool} / {cell.Branch}：期望出现 '{cell.Expect}'，实际返回：\n{Head(text)}");
        }

        Assert.True(missing.Count == 0, string.Join("\n\n", missing));
    }

    // 矩阵完整性。**这条才是本闸与一堆零散用例的区别**：漏掉一整列不会表现为某条断言变红，
    // 只会表现为那一列从来没被跑过——历史上那批缺陷（trace usages 有的尾注 search_regex 没有）
    // 正是这个形状。故「七个工具 × 全部分支」必须格格有主，不适用的也要写明为什么不适用。
    [Fact]
    public void TheMatrixCoversEveryToolAndBranch()
    {
        var cells = Matrix();
        var byKey = cells.ToLookup(c => (c.Tool, c.Branch));

        var gaps = new List<string>();

        foreach (var tool in AllTools)
        foreach (var branch in AllBranches)
        {
            var found = byKey[(tool, branch)].ToList();
            if (found.Count == 0) { gaps.Add($"{tool} / {branch}：矩阵里没有这一格"); continue; }
            if (found.Count > 1) { gaps.Add($"{tool} / {branch}：重复了 {found.Count} 次"); continue; }
            if (found[0].Run == null && found[0].Why.Length == 0)
                gaps.Add($"{tool} / {branch}：记为不适用却没写理由");
        }

        foreach (var cell in cells)
        {
            if (!AllTools.Contains(cell.Tool)) gaps.Add($"{cell.Tool}：不在工具表里");
            if (!AllBranches.Contains(cell.Branch)) gaps.Add($"{cell.Tool} / {cell.Branch}：不在分支表里");
        }

        Assert.True(gaps.Count == 0, string.Join("\n", gaps));
    }

    private static string Head(string text)
    {
        var lines = text.Split('\n');
        return string.Join("\n", lines.Take(12)) + (lines.Length > 12 ? $"\n… 共 {lines.Length} 行" : "");
    }
}
