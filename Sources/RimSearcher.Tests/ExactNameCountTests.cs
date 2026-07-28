using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：**一个数同时承载完整性与精确性，而返回里只印得出前者**。
//
// F30 把表头的 bare `N` / `N of M` 教成完整性记号（「这一段有没有被截断」），调用方拿到手的
// 却是一个精确/近名的混合数。第十轮盲测两条互不相干的链各自独立踩到：
//   `method:Draw` 表头 `10 of 1591 members`，其下 10 行**全部** 100%——真正叫 Draw 的只有 35，
//   agent 自费展开一次 limit:'all' 看见 90 分的尾巴才刹住，它自己写「差点把 1591 交出去」；
//   `RangedIndustrial.xml` 表头 `4 files`，四行逐字同形、整段不带分数——真值 2。
//
// 同一轮里另一条链问的是 `method:PostSpawnSetup`，表头 `10 of 104 members`、印出来的 10 行
// 同样全是 100%，而 104 这个总数**确实**全是精确命中，答 104 就是对的。两题的默认视图逐字
// 同形，M 的性质相反，输出里没有任何一处分得开——那条链答对纯属巧合。
//
// 判据：「全集里满分的有几条」在两题上相反（35 ≠ 1591 / 104 == 104），而这个量在
// ScopeFilter.Apply 的 ordered 上是一行代码、与 TotalInScope 同一趟数出来。等于总数时一个字
// 都不印，故不会退化成常亮。
public class ExactNameCountTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ZzDraw 精确一条，ZzDrawTwice / ZzDrawThrice 是前缀近名——正是 F32 放开召回之后
    // 进入成员段总数的那一类。
    private const string Source = """
        namespace Zz
        {
            public class ZzFleck
            {
                public void ZzDraw() { }
                public void ZzDrawTwice() { }
            }

            public class ZzMote
            {
                public void ZzDrawThrice() { }

                // 与上面三个都不沾边：查它时全集只有它自己，用来看反面那一支
                public void Wombat() { }
            }
        }
        """;

    private LocateTool BuildTool()
    {
        var root = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzFleck.cs"), Source);

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    private static async Task<string> Run(LocateTool tool, string json)
    {
        using var args = JsonDocument.Parse(json);
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);
        return result.Content;
    }

    // 正面：总数里混着近名时，表头就地说清其中几条是问的那个名字。
    [Fact]
    public async Task MemberHeader_SaysHowManyOfTheTotalAreTheNameThatWasAsked()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzDraw"}""");

        Assert.Contains("3 members (1 at 100%)", content);

        // 记号必须能就地兑换：行内的 (N%) 与表头的 100% 是同一个记号（F33 规则甲）
        Assert.Contains("`Zz.ZzFleck.ZzDraw` (100%)", content);
    }

    // 反面：全集本来就都是精确命中时一个字都不多印——否则这个限定会退化成常亮，
    // 而常亮的限定读者一轮就学会跳过。
    [Fact]
    public async Task MemberHeader_StaysBare_WhenEveryHitIsTheNameThatWasAsked()
    {
        var content = await Run(BuildTool(), """{"query":"method:Wombat"}""");

        Assert.Contains("1 member", content);
        Assert.DoesNotContain("at 100%", content);
    }

    // 多关键词时不印。成员分是 baseScore + keywordBonus 封顶 100，两个以上关键词能把一条
    // 90 分的前缀命中推到 100——那时「满分的有几条」是个假数，第九轮正是据此驳回了
    // 「把表头拆成 exact-name / approximate」的提议。这一条守的就是那次驳回。
    [Fact]
    public async Task MemberHeader_WithhoLdsTheCount_WhenTheQueryCarriesSeveralKeywords()
    {
        var content = await Run(BuildTool(), """{"query":"method:ZzDraw Twice"}""");

        Assert.Contains("members", content);
        Assert.DoesNotContain("at 100%", content);
    }

    // C# Types 段用的是纯模糊分（无 keywordBonus），100% 恒等于逐字同名，故不受上面那道限制。
    [Fact]
    public async Task TypeHeader_CarriesTheSameQualifier()
    {
        var content = await Run(BuildTool(), """{"query":"type:ZzFleck"}""");

        Assert.Contains("`ZzFleck` (100%)", content);
        Assert.DoesNotContain("at 100%", content);
    }
}
