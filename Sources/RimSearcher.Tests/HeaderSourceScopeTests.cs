using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：**来源标签的辖域是「印出来的那几行」，而同一行上的计数的辖域是「整个范围」**。
//
// R19 把同源标签从每一行提到段头，理由是「每行都一样就是纯噪音」。但段头恰好也是**总数**
// 所在的那一行，于是两个辖域不同的量被排版成了一个断言：
//   `10 of 36 members` + `**Members** [vanilla]` —— 36 条里有 2 条来自 Cinders；
//   `511 in scope 'all' … Listed below: 200 … [vanilla]` —— 511 横跨五个源；
//   `3 of 13 XML defs` + `**XML Defs** [vanilla]` —— 13 里有 7 条来自 Wolfein / kiiro / Milira，
//   且返回明说那 7 条 limit 与收窄查询都拉不回来。
//
// 第十一轮盲测五条链，这个根因占了三条。三条**全都答对了**，但没有一条是靠默认返回答对的：
// 一条第一次调用就带了 limit:'all' 跳过默认视图，一条自费多打了三次 trace，一条干脆放弃
// locate 改走 search_regex。判官对这三条的 survivesWithoutLuck 一律判 false。
//
// 这与 R42 是同一型：那次是 direct / deepest 按展示切片算、却排在描述全树的总数后面。
// 来源标签是同一个表头上最后一个仍按切片算的量。
//
// 「切片同源、全集混源」不是巧合而是结构性偏置：locate 同分按 Rank 升序（vanilla 的 rank
// 最小）、继承树按 depth 再按字母序，截断留下的前缀因此系统性地偏向 vanilla——正好是让
// 标签变假的那种切法。
//
// 收口判据：段头的方括号恒描述**这一段的总数**。全集单源就印那个源名（与此前逐字相同），
// 全集混源就印构成，而构成之和恰好等于表头那个总数——这是它自证的全部本事。
// 未截断时不印构成：那时行本身就是构成，再印一遍正是 R19 删掉的那种噪音。
public class HeaderSourceScopeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // vanilla 侧四个同名成员，mod 侧一个。分数全等（都是逐字同名），故排序落到 Rank 上，
    // vanilla 的 rank 最小 —— limit:2 切出来的两行必定全是 vanilla，而全集是混源的。
    // 这正是真语料上 `method:Notify_PawnDied` 那一形的最小复现。
    // ZzSolo 只在 vanilla 侧有，用来测「截断了但全集单源」那一支——不能靠「catalog 里只配
    // 一个源」来造这一支，那会让 ShowLabels 直接为 false、一个标签都不印，测的是另一条路。
    private const string VanillaSource = """
        namespace Vv
        {
            public class VvOne   { public void ZzPing() { } public void ZzSolo() { } }
            public class VvTwo   { public void ZzPing() { } public void ZzSolo() { } }
            public class VvThree { public void ZzPing() { } public void ZzSolo() { } }
            public class VvFour  { public void ZzPing() { } public void ZzSolo() { } }
        }
        """;

    private const string ModSource = """
        namespace Mm
        {
            public class MmOne { public void ZzPing() { } }
        }
        """;

    private LocateTool BuildTool(bool withMod = true)
    {
        var vanillaRoot = _workspace.Dir("Vanilla");
        _workspace.WriteFile(Path.Combine("Vanilla", "VvOne.cs"), VanillaSource);

        var sources = new List<(string, string)> { ("vanilla", vanillaRoot) };

        var indexer = new SourceIndexer();
        indexer.Scan(vanillaRoot);

        if (withMod)
        {
            var modRoot = _workspace.Dir("Wombat");
            _workspace.WriteFile(Path.Combine("Wombat", "MmOne.cs"), ModSource);
            indexer.Scan(modRoot);
            sources.Add(("Wombat", modRoot));
        }

        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build(sources, null, null));
    }

    private static async Task<string> Run(LocateTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // 正面：截断了、且全集混源 —— 段头改印构成，且各源之和等于表头那个总数。
    [Fact]
    public async Task TruncatedHeader_DescribesTheWholeTotal_NotTheListedRows()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzPing","scope":"all","limit":2}""");

        Assert.Contains("2 of 5 members", content);

        // 这一条守的是缺陷本身：切片全是 vanilla，段头**不许**因此说整段是 vanilla。
        Assert.DoesNotContain("**Members** [vanilla]:", content);
        Assert.Contains("**Members** [vanilla 4, Wombat 1]:", content);
    }

    // 反面一：截断了，但全集本来就单源 —— 与此前逐字相同，一个字不多。
    // 这一支是常亮的主要风险口：混源判据写错的话，单源结果也会被摊成 `[vanilla 4]`。
    // 注意 mod 源仍然配着（故 ShowLabels 为真），只是这个查询一条也没命中它。
    [Fact]
    public async Task TruncatedHeader_StaysABareName_WhenTheWholeTotalIsOneSource()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzSolo","scope":"all","limit":2}""");

        Assert.Contains("2 of 4 members", content);
        Assert.Contains("**Members** [vanilla]:", content);
        Assert.DoesNotContain("vanilla 4", content);
    }

    // 反面二：混源，但一条都没被截断 —— 不印构成。行本身就是构成，段头再印一遍是把
    // R19 删掉的噪音换个位置请回来。
    [Fact]
    public async Task CompleteListing_NeverCarriesTheBreakdown_EvenWhenMixed()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzPing","scope":"all"}""");

        Assert.Contains("5 members", content);
        Assert.DoesNotContain(" of ", content);
        Assert.DoesNotContain("vanilla 4", content);

        // 未截断且混源时走的是行级标签那条老路，一字未动
        Assert.Contains("[Wombat]", content);
    }

    // 反面三：scope 把源钉死时一个方括号都不该出现 —— ShowLabels=false 那条既有短路
    // 必须先于构成生效，否则钉死单源的查询会平白多出一个 `[vanilla 4]`。
    [Fact]
    public async Task PinnedScope_CarriesNoSourceBracketAtAll()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzPing","scope":"vanilla","limit":2}""");

        Assert.Contains("2 of 4 members", content);
        Assert.Contains("**Members**:", content);
        Assert.DoesNotContain("[vanilla", content);
    }

    // 构成必须**含被断层收口折掉的那些**：它们同样计入 TotalInScope，而构成限定的正是那个数。
    // 真语料上 `def:ShieldBelt` 的 13 条里有 10 条是折掉的、且返回明说 limit 拉不回来——
    // 一个永远拿不回来的集合配一个说它全是 vanilla 的标签，是这条缺陷最难受的一形。
    // 这里用 C# 类型段复现：ZzBelt 逐字命中 100 分；mod 侧那条把 ZzBelt 埋在名字**中间**，
    // 走的是子串匹配（封顶 50 分），落在断层线 100−40 之下，故 limit 调多大都拉不回来。
    // 名字必须是内部子串而非前缀——前缀命中拿的是 90 分，越得过断层线，测的就不是这一支了。
    [Fact]
    public async Task Breakdown_CountsRowsFoldedAwayByTheScoreGap()
    {
        var vanillaRoot = _workspace.Dir("Vanilla2");
        _workspace.WriteFile(
            Path.Combine("Vanilla2", "ZzBelt.cs"),
            "namespace Vv { public class ZzBelt { } }");

        var modRoot = _workspace.Dir("Wombat2");
        _workspace.WriteFile(
            Path.Combine("Wombat2", "Long.cs"),
            "namespace Mm { public class MmHarnessZzBeltAssemblyController { } }");

        var indexer = new SourceIndexer();
        indexer.Scan(vanillaRoot);
        indexer.Scan(modRoot);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(
            indexer, defIndexer,
            ScopeCatalog.Build([("vanilla", vanillaRoot), ("Wombat", modRoot)], null, null));

        var content = await Run(tool, """{"query":"type:ZzBelt","scope":"all","limit":"all"}""");

        // limit:'all' 也拉不回来的那一条，其来源仍须出现在段头
        Assert.Contains("1 of 2 C# types", content);
        Assert.Contains("**C# Types** [vanilla 1, Wombat 1]:", content);
    }

    // 缺陷回归（同一轮的另一条）：**Content Matches 借用了其余四段的行语法，而它的行首指的是
    // 另一种东西**。四段行首是「被查中的东西」，这一段行首是「装着那个字段值的宿主 def」——
    // 同一处版面位置在同一份返回里表示两种关系，返回里没有任何记号区分。第十一轮盲测里被测方
    // 是靠 tools/list 的描述补出这层语义的，它甚至把出处记成了「返回开头」；只盯返回的调用方
    // 最自然的读法就是「一个名字近似命中的 def」。改成语序说清谁装着谁。
    [Fact]
    public async Task ContentMatchRow_ReadsAsFieldInDef_NotAsAMatchedName()
    {
        var defRoot = _workspace.Dir("Defs3");
        _workspace.WriteFile(
            Path.Combine("Defs3", "Kinds.xml"),
            "<Defs>\n  <PawnKindDef>\n    <defName>ZzSlasher</defName>\n"
            + "    <apparelRequired>\n      <li>ZzShieldBelt</li>\n    </apparelRequired>\n"
            + "  </PawnKindDef>\n</Defs>\n");

        var defIndexer = new DefIndexer();
        defIndexer.Scan(defRoot);
        defIndexer.FreezeIndex();

        var indexer = new SourceIndexer();
        indexer.FreezeIndex();

        var tool = new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", defRoot)], null, null));
        var content = await Run(tool, """{"query":"ZzShieldBelt"}""");

        Assert.Contains("**Content Matches**", content);
        Assert.Contains("PawnKindDef.apparelRequired.li in `ZzSlasher`", content);

        // 宿主名不许再占「命中项」那个位置——那正是让调用方把它读成 def 名近似命中的排版
        Assert.DoesNotContain("- `ZzSlasher` -", content);
    }
}
