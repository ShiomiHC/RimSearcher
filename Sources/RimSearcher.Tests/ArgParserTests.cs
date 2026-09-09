using RimSearcher.Cli;
using RimSearcher.Commands;

namespace RimSearcher.Tests;

public class ArgParserTests
{
    private static readonly CommandSpec Spec = new SearchCommand().Spec;

    private static ParseResult Parse(params string[] argv)
        => ArgParser.Parse(Spec, GlobalOptions.All, argv);

    // ---- 未知 flag 严格模式 ----

    [Fact]
    public void 未知flag报错而不是静默吞掉()
    {
        var r = Parse("shield", "--nonsense", "x");
        Assert.True(r.HasErrors);
        Assert.Contains(r.Errors, e => e.Contains("--nonsense"));
    }

    [Fact]
    public void 未知flag的报错带近似候选()
    {
        var r = Parse("shield", "--lmit", "5");
        Assert.Contains(r.Errors, e => e.Contains("--limit"));
    }

    [Fact]
    public void 完全没有候选时把接受的参数列出来免得再跑一轮help()
    {
        var r = Parse("shield", "--zzzzzzzz", "1");
        Assert.Single(r.Errors, e => e.Contains("--zzzzzzzz"));
        Assert.Contains(r.Errors, e => e.Contains("--limit") && e.Contains("--scope"));
    }

    [Fact]
    public void 未知flag后面跟的取值不再引出第二条无关报错()
    {
        var r = Parse("shield", "--bogus", "somevalue");
        Assert.Single(r.Errors);
    }

    // ---- 有意接受的拼写变体(调用方发明参数名是常态)----

    [Theory]
    [InlineData("--limit")]
    [InlineData("--Limit")]
    [InlineData("--max-results")]
    [InlineData("--max_results")]
    [InlineData("--maxResults")]
    [InlineData("--count")]
    [InlineData("-n")]
    public void 同一意图的多种拼法都被接受(string flag)
    {
        var r = Parse("shield", flag, "7");
        Assert.False(r.HasErrors, string.Join("; ", r.Errors));
        Assert.Equal(7, r.Limit().Count);
    }

    // ---- limit 的取值 ----

    [Fact]
    public void 不给limit就是全部()
    {
        var r = Parse("shield");
        Assert.False(r.HasErrors);
        Assert.True(r.Limit().IsAll);
    }

    /// <summary>
    /// 给了数字就照给的数,没有第二道闸在它之上。夹板撤于 2026-09-05:330 次真实的
    /// 数字调用无一超过 2000,而留着它会让 `--limit 5000` 拿到比不给还少的行。
    /// </summary>
    [Fact]
    public void 大数字的limit照数给不再夹紧()
    {
        var limit = Parse("shield", "--limit", "5000").Limit();
        Assert.Equal(5000, limit.Count);
        Assert.False(limit.IsAll);
    }

    /// <summary>
    /// <c>all</c> / <c>none</c> / <c>0</c> / <c>-1</c> 曾经都读成「解除上限」。不给已经就是
    /// 全部,它们再没有第二种意思可表达,于是一律退回用法错误。消息只说两件事:
    /// 收什么、你给的是什么。
    /// </summary>
    /// <remarks>
    /// **这条断言此前多要一句「Leave --limit out to get every row」,那句话没被测过就写下了。**
    /// 原注释的理由是「否则读的人只会换一个词再猜一次」,而实测的重写形态不支持它:
    /// d155104 之后撞上这条错的 20 次可归因调用里,12 次下一句就不给 --limit、4 次照写
    /// all(那句在场也没被读)、1 次给了个具体数字,**「改写成一个巨大的数字」零例**。
    /// 而那个数字真被写出来也不伤人 —— <c>--limit 999999</c> 与不给的输出逐字节相同。
    /// 剩下的那半句因此只是在复述 --help 里已有的一行。产地 tools/scan-all-rewrite.py。
    ///
    /// 顺带记下这句话为什么能活这么久:它一直被这道闸钉着,而闸只验证句子**在**,
    /// 验证不了它当初该不该在。
    /// </remarks>
    [Theory]
    [InlineData("all")]
    [InlineData("none")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("lots")]
    public void limit只收正整数且落空时回声给的那个值(string raw)
    {
        var r = Parse("shield", "--limit", raw);
        var ex = Assert.Throws<CliUsageException>(() => r.Limit());
        Assert.Contains(raw, ex.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole number", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Leave --limit out", ex.Message, StringComparison.Ordinal);
    }

    // ---- 位置参数 ----

    [Fact]
    public void 缺必填位置参数时报错并说明它是什么()
    {
        var r = Parse();
        Assert.Contains(r.Errors, e => e.Contains("<query>"));
    }

    [Fact]
    public void 多给位置参数时报错并说明这条命令的形状()
    {
        var r = Parse("a", "b", "c");
        Assert.Contains(r.Errors, e => e.Contains("'b'") && e.Contains("<query>"));
    }

    [Fact]
    public void help不受缺参影响()
    {
        var r = Parse("--help");
        Assert.True(r.WantsHelp);
        Assert.False(r.HasErrors);
    }

    // ---- 取值枚举 ----

    [Fact]
    public void 等号写法与空格写法等价()
    {
        Assert.Equal("ThingDef", Parse("x", "--type=ThingDef").Value("type"));
        Assert.Equal("ThingDef", Parse("x", "--type", "ThingDef").Value("type"));
    }

    [Fact]
    public void 开关不接受取值()
    {
        var r = ArgParser.Parse(new FindCommand().Spec, GlobalOptions.All, ["compClass", "--exact=yes"]);
        Assert.Contains(r.Errors, e => e.Contains("--exact"));
    }

    [Fact]
    public void 双横线之后一律当位置参数()
    {
        var r = Parse("--", "--not-a-flag");
        Assert.Equal("--not-a-flag", r.Positional(0));
    }

    // ---- 负数取值 ----

    [Fact]
    public void 负数是取值不是未知选项()
    {
        var r = ArgParser.Parse(new FindCommand().Spec, GlobalOptions.All, ["statBases[].value", "-74"]);
        Assert.False(r.HasErrors);
        Assert.Equal("-74", r.Positional(1));
    }

    [Fact]
    public void 负小数也是取值()
    {
        var r = ArgParser.Parse(new FindCommand().Spec, GlobalOptions.All, ["offset", "-0.5"]);
        Assert.False(r.HasErrors);
        Assert.Equal("-0.5", r.Positional(1));
    }

    [Fact]
    public void 数字打头才让路选项名照旧解析()
    {
        // 判据是「以数字打头」而不是「以减号打头」—— 后者会把每一个拼错的短选项都放行。
        var r = Parse("shield", "-x");
        Assert.Contains(r.Errors, e => e.Contains("-x"));
    }

    // ---- `--` 把选项吞成位置参数 ----

    [Fact]
    public void 双横线吞掉选项时报错点名它并给出去掉它的写法()
    {
        var r = ArgParser.Parse(new FindCommand().Spec, GlobalOptions.All,
                                ["statBases[].value", "--", "-74", "--exact", "--type", "ThingDef"]);
        var e = Assert.Single(r.Errors, x => x.Contains("Unexpected argument"));
        Assert.Contains("'--'", e);
        Assert.Contains("'--exact'", e);
        // 出路是去掉 `--` 的整条命令。
        Assert.Contains("where statBases[].value -74 --exact --type ThingDef", e);
        Assert.DoesNotContain(" -- ", e);
    }

    [Fact]
    public void 多给位置参数而没用双横线时不提它()
    {
        var r = ArgParser.Parse(new FindCommand().Spec, GlobalOptions.All, ["a", "b", "c"]);
        var e = Assert.Single(r.Errors, x => x.Contains("Unexpected argument"));
        Assert.DoesNotContain("'--'", e);
    }

    // ---- 归一化 ----

    [Theory]
    [InlineData("fileFilter", "filefilter")]
    [InlineData("file_filter", "filefilter")]
    [InlineData("File-Filter", "filefilter")]
    [InlineData("FILEFILTER", "filefilter")]
    public void 归一化吃掉大小写与分隔符差异(string input, string expected)
        => Assert.Equal(expected, ArgParser.Normalize(input));
}
