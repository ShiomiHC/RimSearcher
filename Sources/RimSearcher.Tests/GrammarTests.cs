using System.Text.RegularExpressions;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 文法闸。
///
/// 写法教训(01,master 提交 1338603):规则判**说没说**,不许用 Contains 短子串重新声明
/// 「该怎么说」—— 那样同一句话红不红取决于成因措辞。所以这里判的是产地渲染出来的形态与
/// 语义类别,不是逐字比对渲染完的句子。
/// </summary>
public class GrammarTests
{
    // ---- 三态截断文法(01 头号资产)----

    [Fact]
    public void 完整集合裸写数字不多说一个字()
        => Assert.Equal("12 defs", Tally.Complete(12).Render("def"));

    [Fact]
    public void 被截时写成N_of_M()
        => Assert.Equal("12 of 347 defs", Tally.Of(12, 347).Render("def"));

    [Fact]
    public void 总数等于展示数时退回裸写()
        => Assert.Equal("12 defs", Tally.Of(12, 12).Render("def"));

    [Fact]
    public void 只知道下界时写成at_least()
        => Assert.Equal("at least 12 matches", Tally.AtLeast(12).Render("match"));

    [Fact]
    public void 单数不写复数()
    {
        Assert.Equal("1 def", Tally.Complete(1).Render("def"));
        Assert.Equal("1 of 9 defs", Tally.Of(1, 9).Render("def"));
    }

    [Fact]
    public void 三态互斥且被截状态可判定()
    {
        Assert.False(Tally.Complete(5).IsTruncated);
        Assert.False(Tally.Of(5, 5).IsTruncated);
        Assert.True(Tally.Of(5, 6).IsTruncated);
        Assert.True(Tally.AtLeast(5).IsTruncated);
    }

    /// <summary>
    /// 正常态零声明字节(06 上下文预算硬约束):完整集合不触发任何截断声明。
    /// 这正是三态文法「省字节」的那一半 —— 它不是修辞,是预算。
    /// </summary>
    [Fact]
    public void 完整集合不产生截断声明()
    {
        var report = new Report().TruncationNotice(Tally.Complete(5), "def", "raise --limit.");
        Assert.Empty(report.Notices);
    }

    [Fact]
    public void 被截时必须产生截断声明()
    {
        var report = new Report().TruncationNotice(Tally.Of(5, 50), "def", "raise --limit.");
        Assert.Single(report.Notices);
        Assert.Equal(NoticeKind.Truncation, report.Notices[0].Kind);
    }

    /// <summary>
    /// 「N of M」里的 M 是**总数**,不许随 `--limit` 变。
    ///
    /// 这一条是自家踩出来的,两个方向都踩过:search 的子串补扫先按已显示的行去重
    /// (`--limit 3` 把没显示出来的 FTS 命中当成新增,报 3 of 41),改完又先 Take 再累加
    /// (M 跟着 limit 缩,报 3 of 22,真值 23)。两种写法都让调用方按小 limit 试一次就
    /// 拿到一个**错的总数**,而三态文法的全部价值就在那个 M 上。
    /// </summary>
    [Fact]
    public void 总数不随limit变()
    {
        static int Total(string limit)
        {
            var (json, _, _) = Fixture.Run("search", "VoidNode", "--limit", limit, "--json");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("notes")[0].GetProperty("text").GetString()!;
            var m = Regex.Match(text, @"\b(?:\d+ of )?(\d+) defs\b");
            Assert.True(m.Success, $"No count in '{text}'.");
            return int.Parse(m.Groups[1].Value);
        }

        // 三条 VoidNode:两条 FTS 命中,GleamingVoidNode 只有子串扫描找得到。
        var whole = Total("all");
        Assert.Equal(3, whole);
        foreach (var limit in new[] { "1", "2", "3" })
            Assert.Equal(whole, Total(limit));
    }

    // ---- 可数名词登记处 ----

    [Fact]
    public void 未登记的名词拒绝渲染而不是自己拼个s()
        => Assert.Throws<InvalidOperationException>(() => Tally.Complete(2).Render("widget"));

    [Fact]
    public void 不规则复数是登记过的而不是拼出来的()
    {
        // 「拼个 s」会拼出 matchs;多词名词还得复数化正确的那一个词。
        Assert.Equal("matches", NounRegistry.Form("match", 2));
        Assert.Equal("def types", NounRegistry.Form("def type", 2));
        Assert.Equal("field paths", NounRegistry.Form("field path", 2));

        foreach (var noun in NounRegistry.Known)
        {
            var plural = NounRegistry.Form(noun, 2);
            Assert.NotEqual(noun, plural);
            Assert.False(plural.EndsWith("chs", StringComparison.Ordinal) ||
                         plural.EndsWith("ys", StringComparison.Ordinal) ||
                         plural.EndsWith("shs", StringComparison.Ordinal),
                $"'{plural}' is what naive pluralisation of '{noun}' would produce.");
        }
    }

    // ---- 输出收口(01 ToolResult 条目)----

    [Fact]
    public void 行尾一律LF()
    {
        var s = OutputText.Finish("a\r\nb\rc\n");
        Assert.DoesNotContain('\r', s);
    }

    [Fact]
    public void 结尾恰好一个换行且没有尾空行()
    {
        // 空行会被 LLM 读成「后面被截断了」,引出多余重查
        var s = OutputText.Finish("body\n\n\n   \n");
        Assert.Equal("body\n", s);
    }

    [Fact]
    public void 单元格里的换行被压平免得撑坏表格()
        => Assert.Equal("a b", OutputText.Cell("a\nb"));

    [Fact]
    public void 空值渲染成空而不是null字样()
        => Assert.Equal("", OutputText.Cell(null));

    // ---- 渲染器 ----

    [Fact]
    public void 文本渲染的每一行都不带尾随空格()
    {
        var report = new Report()
            .Notice(NoticeKind.Truncation, "Showing 2 of 9 defs; raise --limit.")
            .Table("defs", ["def_name", "label"],
            [
                new Dictionary<string, object?> { ["def_name"] = "A", ["label"] = "aaa" },
                new Dictionary<string, object?> { ["def_name"] = "LongerName", ["label"] = null },
            ]);

        foreach (var line in TextRenderer.Render(report).Split('\n'))
            Assert.Equal(line.TrimEnd(), line);
    }

    [Fact]
    public void json模式下声明区搬进notes一条不丢()
    {
        var report = new Report()
            .Notice(NoticeKind.Staleness, "Snapshot is older than the game.")
            .Notice(NoticeKind.Advisory, "Some translations were not in effect.", footnote: true)
            .Table("defs", ["def_name"], [new Dictionary<string, object?> { ["def_name"] = "A" }]);

        var json = JsonRenderer.Render(report);
        Assert.Contains("\"notes\"", json);
        Assert.Contains("staleness", json);
        Assert.Contains("advisory", json);
        Assert.Contains("Snapshot is older", json);
    }

    /// <summary>
    /// 声明区行数上限(06 上下文预算)。超了就该聚合成尾注,而不是逐条铺开 ——
    /// OutputVolumeCapTests 的落点。
    /// </summary>
    [Fact]
    public void 声明区不超过行数上限()
    {
        string[][] invocations =
        [
            ["search", "shield"],
            ["get", "Apparel_ShieldBelt"],
            ["get", "Bullet_Revolver"],
            ["find", "compClass", "RimWorld.CompShield"],
            ["types"],
            ["list", "ThingDef", "--limit", "2"],
        ];

        foreach (var argv in invocations)
        {
            var (stdout, _, _) = Fixture.Run([.. argv, "--json"]);
            var notes = Regex.Matches(stdout, "\"kind\"\\s*:").Count;
            Assert.True(notes <= RimSearcher.Cli.Limits.MaxNoticeLines,
                $"'{string.Join(' ', argv)}' emitted {notes} notices, over the budget of {RimSearcher.Cli.Limits.MaxNoticeLines}.");
        }
    }

    /// <summary>
    /// 00 论据 3 淘汰掉的「每次返回挂一段免责声明」不得以任何形式重生。
    ///
    /// 判据在第二轮盲测后改过一次。原来判的是「完整结果集里一个句子都不许有」,那把
    /// **计数**也一并禁掉了 —— 而靠沉默传达「这就是全部」正是那轮最贵的一条错误来源
    /// (四个 agent 独立踩,一个据此二次确认了错答案)。现在判的是:完整态只准出现
    /// 计数那一句(kind=count),不准有边界/建议类散文。禁的是免责声明,不是数字。
    /// </summary>
    [Fact]
    public void 结果完整时只有计数一句而没有免责声明()
    {
        var (json, _, code) = Fixture.Run("find", "compClass", "RimWorld.CompShield", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var kinds = doc.RootElement.TryGetProperty("notes", out var notes)
            ? notes.EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList()
            : [];

        // find compClass 落在 ThingDef 上,而语料里有一个 ThingDef 在导出时被砍过字段 ——
        // 那条 boundary 是**有据的**,不是免责声明:它点名了成因,并给出交叉验证的命令。
        Assert.Equal(["count", "boundary"], kinds);
        Assert.Contains("snapshot truncated",
            notes.EnumerateArray().Last().GetProperty("text").GetString());

        var (stdout, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2 defs.", lines[0]);
        Assert.StartsWith("def_name", lines[1]);
    }

    /// <summary>
    /// 上一条的另一半:没有可申报的边界时,完整结果集只有计数一句,一个字的散文都没有。
    /// 两条合起来才是「禁的是免责声明,不是数字」—— 少了这一条,那条 boundary 就可能
    /// 悄悄变成每次都挂的常驻声明,而这正是 00 论据 3 淘汰掉的东西。
    /// </summary>
    [Fact]
    public void 没有边界可申报时完整结果集只有计数()
    {
        var (json, _, code) = Fixture.Run("find", "hediffClass", "Verse.HediffWithComps", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var kinds = doc.RootElement.GetProperty("notes")
                       .EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["count"], kinds);
    }
}
