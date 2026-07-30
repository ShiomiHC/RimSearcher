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

    /// <summary>
    /// scope 筛空了不许说成「快照里没有」。
    ///
    /// 装机验收时当场撞见的:`list ThingDef --scope zh`(汉化包一个 def 都不加)报了
    /// 「No def type named 'ThingDef' in this snapshot」—— 一句彻头彻尾的假话,
    /// 而 ThingDef 在同一份快照里有 3538 个。零结果分流的每条判据当时都是 scope 过滤过的,
    /// 于是分不清成因就报了最强的那种,与 list CreepJoinerAggressiveDef 那条同形。
    /// </summary>
    [Fact]
    public void scope筛空时不说成快照里没有()
    {
        // 语料:HediffDef 只有 ludeon.rimworld 那一个,test.mod 名下一个都没有。
        var (stdout, _, code) = Fixture.Run("list", "HediffDef", "--scope", "test.mod");
        Assert.Equal(RimSearcher.Cli.Runner.ExitNoResults, code);

        Assert.DoesNotContain("No def type named", stdout, StringComparison.Ordinal);
        Assert.Contains("test.mod", stdout, StringComparison.Ordinal);   // 说破是哪个 scope 筛空的
        Assert.Contains("1 def of it overall", stdout, StringComparison.Ordinal);  // 并给出快照里的真实数量

        // 另一半:真不存在的类型仍要照直说,否则这条分流就成了一律不认账。
        var (absent, _, _) = Fixture.Run("list", "NoSuchDefType", "--scope", "test.mod");
        Assert.Contains("No def type named", absent, StringComparison.Ordinal);
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

    /// <summary>
    /// R2:同名提示不随 <c>--type</c> 消失。
    ///
    /// 三轮最恶劣的一处就在这个缝上 —— 提示原先挂在**过滤后**的集合上,于是按 SKILL 教的
    /// 加了 --type,提示走了、按名字关联来的错行留下,对冲归零。而调用方主动收窄这个动作
    /// 恰恰说明它知道有歧义:这是最需要那句提示的时刻,不是最不需要的。
    /// </summary>
    [Fact]
    public void 同名提示不随type消失()
    {
        foreach (var argv in new[]
                 {
                     new[] { "get", "Firefoam" },
                     ["get", "Firefoam", "--type", "StatDef"],
                     ["get", "Firefoam", "--type", "ThingDef"],
                 })
        {
            var (stdout, _, code) = Fixture.Run(argv);
            Assert.Equal(0, code);
            Assert.Contains("share the name 'Firefoam'", stdout);
        }
    }

    /// <summary>
    /// R2 的另一半,也是这条修复自己最容易犯的错:收窄之后**不许**把该显示的弄丢。
    ///
    /// 继承层的 def_type 是 XML 根元素名,defs 表的是 AllDefTypesWithDatabases 的桶名,
    /// 两者会不一致(实测本机快照 26 个,如 Blindhealer 的 CreepJoinerFormKindDef →
    /// PawnKindDef)。语料里 VariantOne 就是这个形状:硬要求 def_type 相等,它的
    /// inherits_from 会整批消失 —— 串味换成丢数据,正是 R2 要修的那类错本身。
    /// </summary>
    [Fact]
    public void 桶名不一致时父节点仍在场()
    {
        var (stdout, _, code) = Fixture.Run("get", "VariantOne");
        Assert.Equal(0, code);
        Assert.Contains("inherits_from", stdout);

        // 同名的那一半反过来:StatDef 的 Firefoam 没有自己的 XML 节点,
        // 就一个字都不许提父节点(ThingDef 那个有,但那不是它的)。
        var (typed, _, _) = Fixture.Run("get", "Firefoam", "--type", "StatDef");
        Assert.DoesNotContain("inherits_from", typed);
        Assert.DoesNotContain("灭火泡沫", typed);
    }
}
