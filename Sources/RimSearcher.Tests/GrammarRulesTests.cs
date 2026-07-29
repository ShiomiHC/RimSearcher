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

    // 这条是本轮新加的，故先证它能红：名词槽非空、首词也不在 NotNouns 里——旧判据整条放行。
    [Fact]
    public void FoldLine_WithAnUnregisteredNoun_IsCaught()
    {
        Assert.Contains("名词槽登记在案", Rules("  ... +7 more widgets (pass limit:'all' to expand)"));
    }

    // 反向：登记过的词不许被这条误伤，单数式也一样——折叠行在 N==1 时印的正是单数式。
    [Theory]
    [InlineData("  ... +7 more C# types (pass limit:'all' to expand)")]
    [InlineData("  ... +1 more C# type (pass limit:'all' to expand)")]
    [InlineData("  ... +4 more of 7 matching lines in this file")]
    public void FoldLine_WithARegisteredNoun_IsClean(string line)
    {
        Assert.DoesNotContain("名词槽登记在案", Rules(line));
    }

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

    // read_code 的区间形不是这个计数惯用法：`lines 2-30 of 30` 里名词按英文语序落在区间前面，
    // 那个 of 说的是「取自」。规则三与规则五都靠 NofM 的区间豁免放行它，故一并钉住。
    [Fact]
    public void ReadCodeLineRange_IsNotACountIdiom()
    {
        var rules = Rules("ZzThing.cs (lines 2-30 of 30)");
        Assert.DoesNotContain("名词槽不空", rules);
        Assert.DoesNotContain("of 的读法", rules);
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

    // 第九轮把这条从「同现」升格成「可指认」：成因确实在场，但同屏还有另一个上限说明时，
    // 读者会就近拿它解释那个下界（实测三条任务链各自独立误读了同一个 `at least 105`）。
    // 此时记号必须自带指向真成因的引用。
    [Fact]
    public void FloorMarkNextToADecoyCause_NeedsAPointer()
    {
        var withDecoy =
            "## 'Zz' — 5 of at least 12 subclasses\n"
            + "... some files were not scanned in full (2 hit the time budget)\n"
            + "... previews are capped at 3 lines per file and no parameter widens that";

        Assert.Contains("at least 的读法", Rules(withDecoy));

        var withPointer = withDecoy.Replace(
            "subclasses\n",
            "subclasses\n_It says 'at least' because 2 files hit the time budget._\n",
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

    // `... ` 开头却**故意**不带计数的那三句不是折叠行，不受这条管。
    [Theory]
    [InlineData("... more matches exist (narrow the query)")]
    [InlineData("... some files were not scanned in full (2 hit the time budget)")]
    [InlineData("... previews are capped at 3 lines per file and no parameter widens that")]
    public void DeliberatelyUncountedNotices_AreNotFoldLines(string line)
    {
        Assert.DoesNotContain("折叠行文法", Rules(line));
    }
}
