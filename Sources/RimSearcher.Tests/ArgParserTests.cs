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
    public void limit接受all为正式取值()
    {
        var r = Parse("shield", "--limit", "all");
        Assert.False(r.HasErrors);
        Assert.True(r.Limit().IsAll);
    }

    [Fact]
    public void limit超上限时夹紧并留下夹紧标记()
    {
        var r = Parse("shield", "--limit", (Limits.MaxLimit + 1).ToString());
        var limit = r.Limit();
        Assert.Equal(Limits.MaxLimit, limit.Count);
        Assert.True(limit.Clamped);
    }

    [Fact]
    public void limit给了非数字时错误消息说清接受什么()
    {
        var r = Parse("shield", "--limit", "lots");
        var ex = Assert.Throws<CliUsageException>(() => r.Limit());
        Assert.Contains("'all'", ex.Message);
        Assert.Contains("lots", ex.Message);
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

    // ---- 归一化 ----

    [Theory]
    [InlineData("fileFilter", "filefilter")]
    [InlineData("file_filter", "filefilter")]
    [InlineData("File-Filter", "filefilter")]
    [InlineData("FILEFILTER", "filefilter")]
    public void 归一化吃掉大小写与分隔符差异(string input, string expected)
        => Assert.Equal(expected, ArgParser.Normalize(input));
}
