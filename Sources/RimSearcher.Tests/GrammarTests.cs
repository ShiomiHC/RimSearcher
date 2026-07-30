using System.Text.RegularExpressions;
using RimSearcher.Cli;
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
            // code-search 的最坏情形:三道闸同时咬,四句申报加一句计数。
            // 这一条是补上来的 —— 本轮把印刷闸拆成两个旋钮,声明条数跟着涨了一条,
            // 而这道闸原先根本没覆盖这条命令。
            ["code-search", "public", "--limit", "1", "--max-per-file", "1", "--max-files", "1"],
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
    /// R11:被接受的开关不许被吞。
    ///
    /// `find --value X --exact` 原先接受 --exact 然后完全不读它,输出与不加时一字不差 ——
    /// 三轮唯一一处既成的静默吞掉,而它最坏的地方是让人以为拿到的是精确匹配计数。
    /// SKILL 承诺的是「Unknown options are rejected rather than ignored … so a wrong guess
    /// costs one line, not a wrong answer」,而那层保护只覆盖选项**名**。
    ///
    /// 这条断言的形状是「加了它输出必须变」,不是「输出等于某个具体值」—— 后者钉不住
    /// 「被忽略」这件事本身,因为被忽略时输出也是一个合法的值。
    /// </summary>
    [Fact]
    public void exact在按值反查时不被吞掉()
    {
        // 语料里 comps[0].compClass = RimWorld.CompShield。子串能命中,整值相等不能。
        var (loose, _, looseCode) = Fixture.Run("find", "--value", "CompShield");
        var (strict, _, strictCode) = Fixture.Run("find", "--value", "CompShield", "--exact");

        Assert.Equal(0, looseCode);
        Assert.NotEqual(loose, strict);
        Assert.Equal(1, strictCode);
        // 落空时要指出 --exact 是这次落空的成因之一,否则「没有」被读成绝对的没有。
        Assert.Contains("--exact", strict);

        // 反过来:整值给对时 --exact 必须还能命中,否则这个开关就是把路堵死而不是收窄。
        var (hit, _, hitCode) = Fixture.Run("find", "--value", "RimWorld.CompShield", "--exact");
        Assert.Equal(0, hitCode);
        Assert.Contains("comps[0].compClass", hit);
    }

    /// <summary>
    /// R6:<c>inherit</c> 的 patch 计数每个节点都要在场,不能只在非零时出现。
    ///
    /// 文档承诺「a node with 0 of them is exactly what the game read」—— 读者据此以为会看到
    /// 一个数字,零就放心。实现原先只在非零时打印一句散文,于是「零」和「这件事没做」分不开,
    /// 那个保证在实现里根本不存在。二轮 F2(三态文法的裸 N 从未渲染出来过)是同一个形状。
    /// </summary>
    [Fact]
    public void inherit的patch计数在干净节点也在场()
    {
        // BaseBullet 被两条 patch 点名,Bullet_Revolver 一条都没有 —— 两边都要报出数字。
        var (patched, _, _) = Fixture.Run("inherit", "BaseBullet", "--json");
        var (clean, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--json");

        static int PatchOps(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("nodes")[0].GetProperty("node")
                      .GetProperty("patch_ops").GetInt32();
        }

        Assert.Equal(2, PatchOps(patched));
        Assert.Equal(0, PatchOps(clean));

        // 后果那句散文只在非零时说 —— 0 的那一条不需要解释,而常驻声明是 00 论据 3 淘汰掉的。
        var (patchedText, _, _) = Fixture.Run("inherit", "BaseBullet");
        var (cleanText, _, _) = Fixture.Run("inherit", "Bullet_Revolver");
        Assert.Contains("is targeted by name by", patchedText);
        Assert.DoesNotContain("is targeted by name by", cleanText);
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

    // ---- code-search 的三道闸(三轮 R3 / R4 / R13 / R15 与「--limit 静默提前终止扫描」)----

    /// <summary>
    /// **印几行**的两道闸都不许缩短扫描。
    ///
    /// 这一条守的是 SKILL 里那句「--limit 只管行,code-search 另有一道管文件数的闸」——
    /// 三轮盲测十份轨迹一次都没记下它是假的:实现里 --limit 一到就 break 掉整个扫描,
    /// 于是「有多少个这种形状的方法」这类问题拿到的是「印满为止的那个数」,
    /// 而它还被写成 at least 形态,读起来像是闸的锅。字节基线钉不住这条 ——
    /// 措辞一改就跟着改,而这里判的是三种调法必须给出同一个总数。
    /// </summary>
    [Fact]
    public void 印刷上限不缩短扫描()
    {
        static int Total(params string[] argv)
        {
            var (stdout, _, _) = Fixture.Run(argv);
            var m = Regex.Match(stdout, @"^(?:at least )?(\d+) match");
            Assert.True(m.Success, $"'{string.Join(' ', argv)}' printed no match count:\n{stdout}");
            return int.Parse(m.Groups[1].Value);
        }

        var all = Total("code-search", "public", "--limit", "all");
        Assert.Equal(all, Total("code-search", "public", "--limit", "1"));
        Assert.Equal(all, Total("code-search", "public", "--max-per-file", "1"));
        Assert.Equal(all, Total("code-search", "public", "--limit", "1", "--max-per-file", "1"));

        // 而**读多少**的那道闸咬下去,总数就必须降级成下界 —— 三态文法的分界线在这里。
        var (capped, _, _) = Fixture.Run("code-search", "public", "--max-files", "1");
        Assert.StartsWith("at least ", capped);
    }

    /// <summary>
    /// R15:点开头的目录不是源码树。判据在 SourcesShared.TreeNames,这里守的是
    /// code-search 真的走了那一份 —— 语料里 .git 下摆着一个匹配 *.cs 的文件。
    /// </summary>
    [Fact]
    public void 点开头的目录不算源码树()
    {
        var (stdout, _, code) = Fixture.Run("code-search", "class Sneaky");
        Assert.Equal(1, code);
        Assert.DoesNotContain("Sneaky.cs", stdout);
    }

    /// <summary>
    /// R13:上下文窗口重叠时合并,同一行不许印两遍。判的是行号不重复,不是措辞。
    /// </summary>
    [Fact]
    public void 上下文窗口重叠时合并()
    {
        var (stdout, _, _) = Fixture.Run("code-search", "public", "--files", "ThingComp.cs", "-C", "2");
        var located = Regex.Matches(stdout, @"^(\S+\.cs:\d+)[:-]", RegexOptions.Multiline)
                           .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(located);
        Assert.Equal(located.Count, located.Distinct().Count());
    }

    /// <summary>
    /// R3 fatal(六个场景):没读完的零结果不是零结果。
    ///
    /// 两件事必须分开说 —— 「扫完了,代码里没有」该指路去 search / find(问错了数据源),
    /// 「没扫完」则一个字都不许提别的数据源,该做的是把闸抬开。原先两条路说同一句话。
    /// </summary>
    [Fact]
    public void 没读完的零结果与真零结果分得开()
    {
        var (real, _, realCode) = Fixture.Run("code-search", "zzzznothing");
        Assert.Equal(1, realCode);
        Assert.Contains("rimsearcher search", real);

        var (capped, _, cappedCode) = Fixture.Run("code-search", "zzzznothing", "--max-files", "1");
        Assert.Equal(1, cappedCode);
        Assert.DoesNotContain("rimsearcher search", capped);
        Assert.Contains("did not finish", capped);

        // 机器侧靠 kind 分:真零是「下一步该怎么做」,没读完是截断。
        var (json, _, _) = Fixture.Run("code-search", "zzzznothing", "--max-files", "1", "--json");
        Assert.Contains("\"kind\": \"truncation\"", json);
        Assert.DoesNotContain("\"kind\": \"next_step\"", json);

        // 第三种成因:glob 一个文件都没打中。三条路各说各的,不许并成一句。
        var (empty, _, emptyCode) = Fixture.Run("code-search", "public", "--files", "Verse/ThingComp.cs");
        Assert.Equal(1, emptyCode);
        Assert.Contains("No file matched", empty);
        Assert.DoesNotContain("rimsearcher search", empty);
    }

    // ---- 零结果的成因分流(三轮 R8 四种误诊 + R10 fatal)----

    /// <summary>
    /// 六种落点各说各的,而且**说的是算出来的那一条**,不是猜的那一条。
    ///
    /// 三轮 R8 的四种误诊全部出自同两句猜话:「像类名」→ find/code-search,否则 → types。
    /// 于是抽象父节点、def 类型、mod 名三种输入都被推去了必然空手的地方,其中两种的答案
    /// 就在同一个库的另一张表里。字节基线钉住措辞,这一条钉住的是**分流本身**:
    /// 每种输入必须落到自己那一支,而不是落到别人那一支。
    /// </summary>
    [Fact]
    public void 零结果按算得出来的落点分流()
    {
        (string Argv, string Must, string MustNot)[] cases =
        [
            // 继承层里的抽象 Name= —— 不许再说「像个类名」
            ("BaseBullet",        "rimsearcher inherit BaseBullet", "find compClass"),
            // 存储桶的名字,不是一个 def
            ("ThingDef",          "is a def type in this snapshot", "find compClass"),
            // def 自己的运行时 class
            ("TestVariantDef",    "--class TestVariantDef",         "rimsearcher types"),
            // 字段取值(comps[N].compClass 那一类)—— 这一支是修这条时自己弄丢又补回来的
            ("CompShield",        "rimsearcher find compClass CompShield", "no class"),
            // 快照覆盖的 mod
            ("ludeon.rimworld",   "is a mod this snapshot covers",  "rimsearcher types"),
        ];

        foreach (var (query, must, mustNot) in cases)
        {
            var (stdout, _, code) = Fixture.Run("search", query);
            Assert.Equal(1, code);
            Assert.Contains(must, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(mustNot, stdout, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 被自己的 <c>--scope</c> 挡住,不是「没有」。这一条排在分流的第一位,因为
    /// 把「过滤掉了」说成「不存在」是这批误诊里最贵的一种:数据就在手边。
    /// </summary>
    [Fact]
    public void 被scope挡住时说破是过滤器干的()
    {
        var (stdout, _, _) = Fixture.Run("search", "TestModGun", "--scope", "ludeon.rimworld");
        Assert.Contains("is in this snapshot after all", stdout, StringComparison.Ordinal);
        Assert.Contains("test.mod", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10 fatal(三个场景):「它在别的快照里」这句话一直是可算出来的,工具从没说过。
    /// get 与 inherit 两条路都要说 —— 三轮踩到的是 get 那条,而两条落空的成因同源。
    /// </summary>
    [Fact]
    public void 别的快照里有时点名说出来()
    {
        string[][] paths =
        [
            ["get", "OnlyInOtherSnapshot"],
            ["inherit", "OnlyInOtherSnapshot"],
            ["search", "OnlyInOtherSnapshot"],
        ];

        foreach (var argv in paths)
        {
            var (stdout, _, code) = Fixture.Run(argv);
            Assert.Equal(1, code);
            Assert.Contains("--snapshot other", stdout, StringComparison.Ordinal);
        }

        // 反面:六种落点都算过、别的快照也问过之后,「没有」才是个结论 —— 而且要说破
        // 自己查过哪些,否则读的人无从判断该不该相信它。
        var (absent, _, _) = Fixture.Run("get", "NoSuchDefAnywhere");
        Assert.Contains("not a def type, a class, a mod", absent, StringComparison.Ordinal);
        Assert.DoesNotContain("--snapshot", absent, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10 的一词两义:<c>--scope vanilla</c> 展开成每个 ludeon.rimworld* 模块,而一份
    /// **叫** vanilla 的快照可能只有 Core。展开当场算得出来,那就写进句子里。
    /// </summary>
    [Fact]
    public void scope在散文里展开成实际圈住的mod()
    {
        var (stdout, _, _) = Fixture.Run("search", "zzzznothing", "--scope", "vanilla");
        Assert.Contains("--scope vanilla (= ludeon.rimworld)", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// R3 的第四件事:<c>--source</c> 已经给出时,补救措施里不许再列它 ——
    /// 实测 <c>--source vanilla</c> 换来的是一模一样的警告,而那句话仍在建议加 <c>--source</c>。
    /// 顺带守住单树也要说破「只读了一部分」。
    /// </summary>
    [Fact]
    public void 补救措施不重复已经给出的参数()
    {
        var (stdout, _, _) = Fixture.Run("code-search", "public", "--source", "vanilla", "--max-files", "1");
        Assert.DoesNotContain("--source <tree>", stdout);
        Assert.Contains("read only in part", stdout);

        var (both, _, _) = Fixture.Run("code-search", "public", "--source", "vanilla",
                                       "--files", "*.cs", "--max-files", "1");
        Assert.DoesNotContain("narrow with", both);
    }

    // ---- read(三轮 R5:CLI 侧读不了文件)----

    /// <summary>
    /// 配平括号得躲开四种「看起来是括号」:注释里的、字符串里的、字符字面量里的、
    /// 逐字字符串里双写引号后面的。躲不开的后果不是少认一个成员,而是**认错边界** ——
    /// 交出去的那段代码从中间截断,而它看上去是完整的一段。
    /// </summary>
    [Fact]
    public void 括号只在真的是括号时算数()
    {
        var (stdout, _, code) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--outline");
        Assert.Equal(0, code);

        // 类体的两端就是文件里那一对真括号:少认一个,Outer 会在 Marker 那一行提前收尾。
        Assert.Contains("5-29", stdout, StringComparison.Ordinal);
        // 初值里的 Make( 不是一个方法声明,字段名才是 Marker。
        Assert.Contains("Marker", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Make", stdout, StringComparison.Ordinal);
        // 方法体里的 if (n > 0) { 不是一个叫 if 的成员。
        Assert.DoesNotContain(" if ", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同名成员:全给、说破归属、<c>--type</c> 能收敛到一个。
    /// 「文件里没有这个成员」与「有,但不在你说的那个类型里」必须是两句话 —— 前者会被
    /// 直接读成「这个类没有覆写它」,而那个成员就在同一份文件的另一个类型里。
    /// </summary>
    [Fact]
    public void 同名成员分得开也说得清()
    {
        var (all, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--member", "Shared");
        Assert.Contains("Outer.Shared", all, StringComparison.Ordinal);
        Assert.Contains("Inner.Shared", all, StringComparison.Ordinal);

        var (one, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Inner");
        Assert.Contains("Inner.Shared", one, StringComparison.Ordinal);
        Assert.DoesNotContain("Outer.Shared", one, StringComparison.Ordinal);

        var (wrong, _, wrongCode) = Fixture.Run("read", "vanilla/Verse/Outline.cs",
                                                "--member", "Shared", "--type", "Nope");
        Assert.Equal(1, wrongCode);
        Assert.Contains("after all", wrong, StringComparison.Ordinal);
        Assert.Contains("Outer", wrong, StringComparison.Ordinal);
    }

    /// <summary>
    /// 基名撞车时不许静默选一份。选错的输出与选对的**逐字同形**(mod 的覆盖版与原版),
    /// 所以这条路上唯一安全的动作是把重名列出来。
    /// </summary>
    [Fact]
    public void 同名文件不替调用方挑()
    {
        var (stdout, _, code) = Fixture.Run("read", "Outline.cs");
        Assert.Equal(1, code);
        Assert.Contains("vanilla/Verse/Outline.cs", stdout, StringComparison.Ordinal);
        Assert.Contains("zz.othermod/Outline.cs", stdout, StringComparison.Ordinal);
        // 只列不读:一行源码都不许出现。
        Assert.DoesNotContain("class Outer", stdout, StringComparison.Ordinal);

        // --source 收敛到一棵树之后,同一个基名就该直接读到。
        var (narrowed, _, ok) = Fixture.Run("read", "Outline.cs", "--source", "vanilla", "--lines", "7");
        Assert.Equal(0, ok);
        Assert.Contains("class Outer", narrowed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 分页:总行数与下一页的参数恒在。R5 的成因不是「读不到」,是**没有读的路**,
    /// 于是调用方去编正则;给了路还得让它走得下去,不然第二页又回到编的老路上。
    /// </summary>
    [Fact]
    public void 裸行读随时说得出总数与下一页()
    {
        var (page, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "7-12");
        Assert.Contains("of 30", page, StringComparison.Ordinal);
        Assert.Contains("--lines 13", page, StringComparison.Ordinal);

        // 一次读完时不许再劝人翻页 —— 那一句会被读成「后面还有」。
        var (whole, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs");
        Assert.Contains("all 9 lines", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("next page", whole, StringComparison.Ordinal);

        // 印刷上限咬下去时,接着读的那一段是算得出来的,就得给出来。
        var (capped, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--type", "Outer", "--limit", "4");
        Assert.Contains("--lines 9-29", capped, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两种读法同时传时不排优先级。旧世系在这里是静默择一的,后果不是「少了点什么」,
    /// 而是拿回**完全另一块代码**,而返回里一个字都不提被丢掉的那个参数。
    /// </summary>
    [Fact]
    public void 两种读法同时传时当场说破()
    {
        var (stdout, stderr, code) = Fixture.Run("read", "Outline.cs", "--lines", "1-3", "--member", "Shared");
        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("--lines", stderr, StringComparison.Ordinal);
        Assert.Contains("--member", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// 配平括号不是解析,这句边界必须跟着**用了轮廓的**那几条路走(R51:写进它作用的
    /// 那个块)。裸行读没有任何推断,那里不该多这一句 —— 常驻免责声明对手上这一条什么也没说。
    /// </summary>
    [Fact]
    public void 能力边界只挂在做了推断的那几条路上()
    {
        foreach (var argv in new[]
                 {
                     new[] { "read", "vanilla/Verse/Outline.cs", "--outline" },
                     ["read", "vanilla/Verse/Outline.cs", "--member", "Shared"],
                     ["read", "vanilla/Verse/Outline.cs", "--type", "Inner"],
                 })
            Assert.Contains("not by parsing C#", Fixture.Run(argv).Stdout, StringComparison.Ordinal);

        var (raw, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "1-5");
        Assert.DoesNotContain("not by parsing C#", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// 分页的三个位置各说各的话:中间页说得出自己从第几条起,末页**不给**下一页的参数,
    /// 翻过了头是一句「翻过头了」而不是一句「没有这个东西」。
    ///
    /// 第三条是这一组里最贵的:R8 那批误诊的形状就是「分不清缺席的成因就报最强的那种」,
    /// 而分页给缺席添了一种全新的成因。
    /// </summary>
    [Fact]
    public void 分页的三个位置各说各的话()
    {
        var (mid, _, midCode) = Fixture.Run("list", "ThingDef", "--limit", "2", "--offset", "2");
        Assert.Equal(0, midCode);
        Assert.Contains("2 of 8 defs, starting at 3", mid, StringComparison.Ordinal);
        Assert.Contains("--offset 4", mid, StringComparison.Ordinal);

        var (last, _, lastCode) = Fixture.Run("list", "ThingDef", "--limit", "4", "--offset", "4");
        Assert.Equal(0, lastCode);
        Assert.Contains("starting at 5", last, StringComparison.Ordinal);
        Assert.DoesNotContain("next page", last, StringComparison.Ordinal);
        // 「到头了」不许由那句话的缺席承载 —— 末页要自己说出来。
        Assert.Contains("that is the last page", last, StringComparison.Ordinal);

        var (past, _, pastCode) = Fixture.Run("list", "ThingDef", "--offset", "900");
        Assert.Equal(1, pastCode);
        Assert.Contains("past the end", past, StringComparison.Ordinal);
        Assert.DoesNotContain("No def type named", past, StringComparison.Ordinal);
    }

    /// <summary>
    /// 负偏移当场拒绝。SQL 把负的 OFFSET 当 0,于是「我少给了一个负号」与「这就是第一页」
    /// 逐字相同 —— 又一处「错的输出与对的输出同形」。
    /// </summary>
    [Fact]
    public void 负偏移不被悄悄当成零()
    {
        var (stdout, stderr, code) = Fixture.Run("list", "ThingDef", "--offset", "-2");
        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("--offset", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// 声明了 --offset 的命令必须真的翻得动。声明层与实现层各写一份是这套代码里最容易
    /// 长出来的漂移:参数表上挂着,Run 里没读,于是 <c>--offset 2</c> 与不给一模一样 ——
    /// 静默忽略一个参数,正是本轮 R11 修过的那个错。
    /// </summary>
    [Fact]
    public void 声明了offset的命令都真的翻得动()
    {
        string[][] probes =
        [
            ["list", "ThingDef"],
            ["search", "VoidNode"],
            ["find", "thingClass"],
            ["fields", "ThingDef"],
            ["values", "thingClass"],
        ];

        foreach (var probe in probes)
        {
            var declared = new CommandRegistry().Specs
                .Single(s => s.Name == probe[0]).Options.Any(o => o.Name == "offset");
            Assert.True(declared, $"'{probe[0]}' does not declare --offset.");

            var first = Fixture.Run([.. probe, "--limit", "1"]).Stdout;
            var second = Fixture.Run([.. probe, "--limit", "1", "--offset", "1"]).Stdout;
            Assert.NotEqual(first, second);
            Assert.Contains("starting at 2", second, StringComparison.Ordinal);
        }
    }
}
