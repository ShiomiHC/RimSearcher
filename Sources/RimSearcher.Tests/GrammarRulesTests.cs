using RimSearcher.Core;
using RimSearcher.Server.Tools;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Tests;

// 闸自己的反面用例。
//
// 隔壁 OutputGrammarGateTests 喂的是**真输出**，故它只证得了「现在没有违规」；一条判据是不是
// 还能红，它一个字都答不出来。而闸最坏的失效形态恰恰是这个：判据写坏了、正则不再匹配、
// 名单删空了——全都表现为「继续绿」。台账里 `ms` 那条就险些如此（判据在，只在耗时取整到 1 时
// 才红），F26 的成因判据从「同现」升格成「可指认」时也需要一个反例来钉住新的那一档。
//
// 故每条判据至少要有一条**故意违规**的文本，断言它确实被抓住。GrammarRules 里两处注释此前
// 就指名引用「反面用例见 GrammarRulesTests」，而这个类型一直不存在——判据自称有反例，反例没有。
public class GrammarRulesTests
{
    private static string[] Rules(string text)
        => GrammarRules.Check(text).Select(v => v.Rule).ToArray();

    // ---- 名词槽登记在案（规则一的词表侧） ----

    // 名词槽非空、首词也不在 NotNouns 里——旧判据整条放行。
    //
    // 反面用例拿产地渲染一条真的，再**只**把名词换成名单外的词：这样它与产品的排版同步，
    // 又保住了那一处故意的偏离。整行手写的话，产品哪天改了折叠行的排版，这条用例会从「名词槽
    // 不合法」漂成「整个形状不合法」——闸照旧红，红的却不再是它要钉的那件事。
    [Fact]
    public void FoldLine_WithAnUnregisteredNoun_IsCaught()
    {
        var corrupted = FoldLineOf(CountedNoun.Files, 7)
            .Replace(CountedNoun.Files.Plural, "widgets", StringComparison.Ordinal);

        Assert.Contains("名词槽登记在案", Rules(corrupted));
    }

    // 反向：登记过的词不许被这条误伤，单数式也一样——折叠行在 N==1 时印的正是单数式；
    // 每文件折叠行（唯一不带括号的一形）同理。
    public static TheoryData<string> WellFormedFoldLines() =>
    [
        FoldLineOf(CountedNoun.CSharpTypes, 7),
        FoldLineOf(CountedNoun.CSharpTypes, 1),
        Fold.PerFile(4, 7),
    ];

    [Theory]
    [MemberData(nameof(WellFormedFoldLines))]
    public void FoldLine_WithARegisteredNoun_IsClean(string line)
    {
        Assert.DoesNotContain("名词槽登记在案", Rules(line));
    }

    private static string FoldLineOf(CountedNoun noun, int hidden)
        => OutputText.FoldLine(hidden, noun, "pass limit:'all' to expand", null, "  ")!;

    // ---- 名词槽不空（规则五的 `N of M` 侧） ----

    // 旧判据只验「下一个字符是字母」，于是 `in` 当了那个名词。这一形是 N5 改掉的那条表头的
    // 一个近似写法——它先要看得见，那一步才有判据。
    [Fact]
    public void CountOfTotal_FollowedByAPreposition_IsCaught()
    {
        Assert.Contains("名词槽不空", Rules("## 'ZzBase' — 5 of 12 in scope 'all'"));
    }

    [Fact]
    public void CountOfTotal_FollowedByARegisteredNoun_IsClean()
    {
        Assert.DoesNotContain("名词槽不空", Rules("## 'ZzBase' — 5 of 12 subclasses (in scope 'all')"));
    }

    // read_code 的区间形不是那个截断惯用法：`lines 2-30 of 30` 里名词按英文语序落在区间前面，
    // 那个 of 说的是「取自」。它此前靠 NofM 的一条区间豁免放行，M2 之后靠的是它自己有了产地
    // （Tally.Window）——故这里逐字拿产地渲染出来喂进去，闸认它就等于认那个产地。
    [Fact]
    public void ReadCodeLineRange_IsNotACountIdiom()
    {
        var rules = Rules($"ZzThing.cs ({Tally.Window(CountedNoun.Lines, 2, 30, 30)})");
        Assert.DoesNotContain("名词槽不空", rules);
        Assert.DoesNotContain("of 的读法", rules);
    }

    // 上一条的反面，也是「豁免真的删掉了」的证据：豁免还在的话，它认的是「N 前面有没有连字符」
    // 这个纯文本特征，于是任何长这样的东西都被放行。现在放行的判据是「出自 Tally.Window」，
    // 而 rows 不是登记在案的计数名词，Window 渲染不出这一行。
    [Fact]
    public void ARangeThatNoOriginRenders_IsStillCaught()
    {
        Assert.NotEmpty(Rules("ZzThing.cs (rows 2-30 of 30)"));
    }

    // ---- of 的读法（规则三） ----

    [Fact]
    public void CountEqualToTotal_IsCaught()
    {
        Assert.Contains("of 的读法", Rules("## 'Zz' — 12 of 12 subclasses"));
    }

    // 普通英文介词不归这条管——语料里三种 `of` 并存，这是其中一种。
    [Fact]
    public void OfAsAnOrdinaryPreposition_IsClean()
    {
        Assert.DoesNotContain("of 的读法",
            Rules("'ZzHuge' is 2001 lines of a 2003-line file and the cap is 2000"));
    }

    // ---- 单复数（规则二） ----

    [Fact]
    public void SingularCountWithAPluralNoun_IsCaught()
    {
        Assert.Contains("单复数", Rules("first 1 preview lines in scope 'all'"));
    }

    // `1 file was abandoned` 里的 was 不是漏写单数的复数名词；`1 ms` 是单复数同形的单位符号
    // ——后者此前不在 NotNouns 里，于是那道闸只在耗时恰好取整到 1 时才红。
    [Theory]
    [InlineData("1 file was abandoned mid-scan")]
    [InlineData("Source check (1 ms, 3 sources)")]
    public void CountsFollowedByNonNouns_AreClean(string line)
    {
        Assert.DoesNotContain("单复数", Rules(line));
    }

    [Fact]
    public void LazyPluralSuffix_IsCaught()
    {
        Assert.Contains("单复数", Rules("Found 3 match(es) in scope 'all'"));
    }

    // ---- at least 的读法（规则四） ----

    [Fact]
    public void FloorMarkWithoutACause_IsCaught()
    {
        Assert.Contains("at least 的读法", Rules("## 'Zz' — 5 of at least 12 subclasses"));
    }

    // 第九轮把这条从「同现」升格成「可指认」：成因确实在场时，读者仍会就近拿另一个上限说明去
    // 解释那个下界（实测三条任务链各自独立误读了同一个 `at least 105`）。M2 再升一格——从
    // 「同屏有诱饵时才要引用」变成「一律要引用」，因为「读者可能拿来解释它的别的上限句」是个
    // 开放集合，枚举得出来的那张诱饵表挡不住新加的第五句。
    //
    // 引用只认产地渲染出来的那两条（扫描侧 / 候选池侧）。此前闸另认一句 `'at least' because`，
    // 而产品一处都不产出它——那是条从落地起就没有产地的死项，只在这个用例里活着。
    [Fact]
    public void FloorMarkWithoutAPointer_IsCaught()
    {
        var causeInPlace =
            $"## 'Zz' — 5 of {ScanReport.FloorMark}12 subclasses\n"
            + ScanReport.NotScannedInFull(["2 hit the time budget"]) + "\n"
            + Fold.PerFilePreviewCap(3);

        Assert.Contains("at least 的读法", Rules(causeInPlace));

        var withPointer = causeInPlace.Replace(
            "subclasses\n",
            "subclasses" + ScanReport.LowerBoundReason(true) + "\n",
            StringComparison.Ordinal);

        Assert.DoesNotContain("at least 的读法", Rules(withPointer));
    }

    // 反向那一支：有文件没扫全时总数只是下界，表头必须跟着改口。
    [Fact]
    public void UnscannedFilesWithADefiniteHeader_IsCaught()
    {
        Assert.Contains("at least 的读法", Rules(
            "Found 7 matching files in scope 'all'\n"
            + "... some files were not scanned in full (2 hit the time budget)"));
    }

    // 例外：表头换了量纲的那一支数的是**印出来的**预览行，那个数是确定的，不该也不能改口。
    [Fact]
    public void HeaderThatSwitchedUnits_DoesNotNeedToHedge()
    {
        Assert.DoesNotContain("at least 的读法", Rules(
            "Regex matches for 'Zz' (first 3 preview lines in scope 'all')\n"
            + "... some files were not scanned in full (2 hit the time budget)"));
    }

    // ---- 来源标签（规则六） ----

    [Fact]
    public void RowLabelsThatAreAllTheSame_ShouldHaveBeenHoisted()
    {
        Assert.Contains("来源标签", Rules(
            "- ZzOne.cs [Core]\n- ZzTwo.cs [Core]\n- ZzThree.cs [Core]"));
    }

    [Fact]
    public void MixedRowLabels_AreClean()
    {
        Assert.DoesNotContain("来源标签", Rules("- ZzOne.cs [Core]\n- ZzTwo.cs [Milira]"));
    }

    // 行尾的 [conditional: X] 不是来源标签：哪一行受条件目录影响是**逐行不同的事实**，
    // 它按设计就该逐行挂，把它当成该上提的噪音正好反了。
    [Fact]
    public void ConditionalTagsOnEveryRow_AreNotSourceLabels()
    {
        Assert.DoesNotContain("来源标签", Rules(
            "- ZzOne.cs [conditional: 1.6/CE]\n"
            + "- ZzTwo.cs [conditional: 1.6/CE]\n"
            + "- ZzThree.cs [conditional: 1.6/CE]"));
    }

    // ---- 折叠行文法（规则一的形状侧） ----

    // sync_sources 两处折叠行历史上就长这样：一处丢了 `+` 与名词槽，一处把下一步写在破折号后面。
    [Theory]
    [InlineData("... 12 more of 30 — next page: pass offset=12")]
    [InlineData("... 12 more members — pass file=ZzThing.cs")]
    public void FoldLinesThatMissTheSharedShape_AreCaught(string line)
    {
        Assert.Contains("折叠行文法", Rules(line));
    }

    // `... ` 开头却**故意**不带计数的那四句不是折叠行，不受这条管。
    //
    // 逐字取产地渲染，不手写：此前这里的第一条写作 `... more matches exist (narrow the query)`
    // ——一句产品从没产出过的话。闸那边的名单也只抄到括号为止，于是两边一起绿着，谁也没在验
    // 真正那句长什么样。第四句（中段省略）此前压根不在名单里，只在规则九那边另有一条豁免。
    public static TheoryData<string> UncountedNotices() =>
    [
        ScanReport.ScanStopped(3, new ResultLimit(3, false)),
        ScanReport.NotScannedInFull(["2 hit the time budget"]),
        Fold.PerFilePreviewCap(3),
        Fold.Elision(73, 201, 273),
    ];

    [Theory]
    [MemberData(nameof(UncountedNotices))]
    public void DeliberatelyUncountedNotices_AreNotFoldLines(string line)
    {
        Assert.DoesNotContain("折叠行文法", Rules(line));
    }

    // ---- 下一步（规则九） ----

    // 中段省略两侧的行都印着，翻页参数写在紧邻的续读提示里，故这一形本来就不带下一步。
    // 此前它靠这里一句手写的 `StartsWith("... [Truncated ")` 豁免。
    [Fact]
    public void TheElisionMarker_DoesNotNeedANextStep()
    {
        Assert.DoesNotContain("下一步", Rules(Fold.Elision(73, 201, 273)));
    }

    // 乙：顶到服务端上限了还劝 `limit:'all'`，照做是原地重试。Fold.Line 的三分支本就把这两句写成
    // 互斥的，故这条判的是「有人绕开三分支另拼了一句」。
    //
    // 两条提示语逐字取自 Fold.Line 自己的渲染——这同时是「闸这边的产地推导没退化成空串」的反面
    // 用例：推导出空串的话 `Contains("")` 恒真，下面第二、三条断言会立刻红。
    [Fact]
    public void ServerCapReachedTogetherWithAdviseAll_IsCaught()
    {
        var capReached = Fold.Line(
            1, ScopeAndLimitArgs.HardLimit, null, true, CountedNoun.Files, string.Empty,
            new ResultLimit(ScopeAndLimitArgs.HardLimit, true))!;
        var advisesAll = Fold.Line(
            1, 1, null, true, CountedNoun.Files, string.Empty, new ResultLimit(5, false))!;

        Assert.Contains("下一步", Rules(capReached + "\n" + advisesAll));
        Assert.DoesNotContain("下一步", Rules(capReached));
        Assert.DoesNotContain("下一步", Rules(advisesAll));
    }
}
