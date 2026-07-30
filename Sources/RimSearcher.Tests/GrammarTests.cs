using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Search;

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
    // ---- 举例子的名单(NameList)----

    /// <summary>
    /// 举例子这一层与三态文法是同一条纪律的两个位置:被截掉的部分**必须有数**。
    /// 这道闸守的是产地本身,而 <c>名单截断时不许把数量省成省略号</c> 守的是没人绕开它。
    /// </summary>
    [Fact]
    public void 举例子的名单说清没举出来的有几条()
    {
        string[] five = ["a", "b", "c", "d", "e"];
        Assert.Equal("a, b, c, and 2 more", NameList.Render(five, 3));

        // 装得下就一个字都不多说 —— 「and 0 more」比沉默更糟,它让人以为有下文。
        Assert.Equal("a, b, c, d, e", NameList.Render(five, 5));
        Assert.Equal("a, b, c, d, e", NameList.Render(five, 99));
        Assert.Equal("", NameList.Render([], 3));

        // 差一条就截:边界上不许把「刚好装下」算成「截了」。
        Assert.Equal("a, b, c, d, and 1 more", NameList.Render(five, 4));
    }

    /// <summary>
    /// 近似候选**不报**被截掉的数量,这与上面那条相反,是有意的:排在第 4 位往后的
    /// 按定义就不是「最近的」,补一句「还有 37 个」会让人以为答案可能在那 37 个里。
    /// 「Closest」这个词本身声明了它是个 top-N。
    /// </summary>
    [Fact]
    public void 近似候选不谎报也不追加数量()
    {
        var pool = Enumerable.Range(0, 40).Select(i => $"Bullet_Revolver{i}").ToList();
        var said = Suggestion.Say(Suggestion.Closest(pool, "Bullet_Revolver"));
        Assert.Contains("Closest by spelling:", said);
        Assert.DoesNotContain("more", said);

        // 一条候选都没有时走 whenNone,而不是留下一个空的「Closest by spelling: .」
        Assert.Equal(" nothing close.", Suggestion.Say([], " nothing close."));
        Assert.Equal("", Suggestion.Say([]));
    }

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
    ///
    /// 五轮修正:这道闸原先拿 <c>Bullet_Revolver</c> 当「测得零」的样本,**而那个节点根本
    /// 没有 Name=** —— 导出器对它硬写 0(XmlNodeExporter:66,计数正则只认 <c>@Name=</c>)。
    /// 于是闸自己把假零当成了真零去守,四份盲测轨迹独立栽在这一格上。真正的测得零在
    /// <c>BaseProjectile</c>(有 Name=、零条 patch 点名),无名那一侧另立断言:
    /// 必须印出一个**非数字**,并说破计数口径。
    /// </summary>
    [Fact]
    public void inherit的patch计数在干净节点也在场()
    {
        // BaseBullet 被两条 patch 点名,BaseProjectile 有 Name= 而一条都没有 —— 两边都是数字。
        var (patched, _, _) = Fixture.Run("inherit", "BaseBullet", "--json");
        var (clean, _, _) = Fixture.Run("inherit", "BaseProjectile", "--json");
        // Bullet_Revolver 只有 ParentName=,这一格从来没量过。
        var (unnamed, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--json");

        static System.Text.Json.JsonElement PatchOps(string json)
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("nodes")[0].GetProperty("node")
                      .GetProperty("patch_ops").Clone();
        }

        Assert.Equal(2, PatchOps(patched).GetInt32());
        Assert.Equal(0, PatchOps(clean).GetInt32());

        // 没量过的那一格不许是数字 —— 是数字就与「量过了、确实没人 patch」逐字同形。
        var unmeasured = PatchOps(unnamed);
        Assert.Equal(System.Text.Json.JsonValueKind.String, unmeasured.ValueKind);
        Assert.Equal("n/a", unmeasured.GetString());

        // 后果那句散文只在非零时说 —— 真的 0 不需要解释,而常驻声明是 00 论据 3 淘汰掉的。
        // 无名那一条要的是另一句话:说破口径,不是说后果。
        var (patchedText, _, _) = Fixture.Run("inherit", "BaseBullet");
        var (cleanText, _, _) = Fixture.Run("inherit", "BaseProjectile");
        var (unnamedText, _, _) = Fixture.Run("inherit", "Bullet_Revolver");
        Assert.Contains("is targeted by name by", patchedText);
        Assert.DoesNotContain("is targeted by name by", cleanText);
        Assert.DoesNotContain("patch_ops is not measured", cleanText);
        Assert.Contains("patch_ops is not measured", unnamedText);
    }

    /// <summary>
    /// 五轮 F4:默认值那句声明不许承诺它做不到的事。
    ///
    /// 原句是「The def has N fields in all; pass --defaults to list every one」,两处超发:
    /// N 是**索引到的路径数**而不是 def 的字段数;而值为 null 的字段导出器见了直接 return
    /// (DefExporter:284),那条路径从来没进过索引,<c>--defaults</c> 也召不回来。
    /// 于是「这个字段不存在」与「它的值是 null」在输出上完全同形 —— 实测里有人跨三份
    /// 列表交叉验证,只是把错结论**加固**了,因为缺的是同一批字段。
    ///
    /// 闸判**说没说**:承诺范围有没有限定在索引到的路径上,以及第四态有没有说破。
    /// 第一分句(那句被反复点名的认识论诚实)另有字节基线钉着,这里不重复。
    /// </summary>
    [Fact]
    public void 默认值声明不承诺它做不到的事()
    {
        var (text, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("Not listed:", text);

        // 「列出全部字段」和「这个 def 一共有 N 个字段」都不成立,一个字都不许出现。
        Assert.DoesNotContain("list every one", text);
        Assert.DoesNotContain("in all", text);

        // 第四态要说破,否则这两个开关的边界读起来就是「加上它们就齐了」。
        Assert.Contains("never entered the index", text);
    }

    /// <summary>
    /// 五轮 F3:一个词同时是快照名和 scope 组名时,要说破两者不是一回事。
    ///
    /// 实测代价 22 倍:机器上恰好有一份叫 vanilla 的快照(Core + 导出器,两个 mod),
    /// 而提问问的是「原版怎么算」,顺手 <c>--snapshot vanilla</c> —— 唯一烧油的那个
    /// 穿梭机来自 Odyssey,整个不在射程里,而输出一个字不提。
    /// <c>--scope vanilla</c> 则是六个 Ludeon 模块(含 DLC),第三义是提问者嘴里的「原版」。
    ///
    /// 「显式指定就闭嘴,你已经说了要哪个环境」这条原则在这里恰好不成立:
    /// 它的前提是调用方知道自己选的环境是什么,而这一格正是他以为知道其实不知道的。
    /// 只在撞名时说,所以平常一次都不出现 —— 下面第二组断言守的就是这个。
    /// </summary>
    [Fact]
    public void 快照名与scope组名撞车时说破()
    {
        const string collision = "is both this snapshot's name and a --scope group name";

        // core.db:文件名就是内置组名之一。
        var (hit, _, _) = Fixture.Run("get", "OnlyInCoreSnapshot", "--db", Fixture.CoreDb);
        Assert.Contains(collision, hit);
        // 说破的必须是**这份快照实际盖住什么**,不是一句泛泛的免责声明。
        Assert.Contains("other.mod", hit);

        // 名字不撞车的那份,一个字都不多说。
        var (quiet, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.DoesNotContain(collision, quiet);
    }

    /// <summary>
    /// 五轮 F2:scope 展开成了什么,在**有结果时**也要说。
    ///
    /// 文档两处承诺过,而 <c>Describe()</c> 的七个调用点原先全嵌在零结果句里 ——
    /// 查到东西时不告诉你范围,查不到时才告诉你,方向正好反了。四份轨迹撞上,
    /// 其中一份连着五次 <c>--scope vanilla</c> 零次播报,而那个词实际展开成六个
    /// Ludeon 模块(含 DLC),口径直接决定答案怎么写。
    ///
    /// 判据是「展开与字面不同」,不是「多于一个 mod」:写死 packageId 的调用
    /// 一个字都不该多收。同一次输出里也只说一遍 —— 散文里一律写调用方输入的字面。
    /// </summary>
    [Fact]
    public void scope展开在有结果时也播报()
    {
        // 组名:夹具里 vanilla 展开成 ludeon.rimworld,与字面不同 —— 必须说。
        var (group, _, code) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "vanilla");
        Assert.Equal(0, code);
        Assert.Contains("--scope vanilla (= ludeon.rimworld)", group);

        // 写死 packageId:你写的就是你得到的,**一行都不多印**。
        // 断言写成「不含带括号的那种形态」是不够的 —— 判据一旦退回「多于一个 mod 就播」,
        // 印出来的是不带括号的 `--scope ludeon.rimworld.`,那样的闸红不了。
        var (literal, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld");
        Assert.DoesNotContain("--scope ludeon.rimworld", literal);

        // 零结果那一侧原先就说,现在仍要说,但**只说一遍**:散文改用字面之后,
        // 展开只剩播报那一个产地。两遍与一遍在这里差的是「读者以为看到了两条独立证据」。
        var (miss, _, _) = Fixture.Run("find", "--value", "NoSuchValueXyz", "--scope", "vanilla");
        Assert.Single(Regex.Matches(miss, @"= ludeon\.rimworld\)"));
    }

    /// <summary>
    /// 五轮 F1:截断尾注是给「这就是全部」背书的那句话,而它自己算错了。
    ///
    /// 两处根因。一是 <c>find --value</c> 按**结果里的每条路径**各查一次再求和,同一个被砍的
    /// def 出现在几条路径上就被数几次;二是那个子查询问的是「用过这条路径的 def 类型」,
    /// 而结果里只要有一条路径叫 <c>defName</c> —— 命中一个 def 名时必然有 —— 它就退化成
    /// 全体类型,这一项独自等于全库。真数据上报出 251 与 242,而快照总共 239 个被砍的 def。
    ///
    /// 闸按**数学上不可能**立,不按具体数字立:尾注说的是「同类型里还有几个」,那是全库
    /// 被砍总数的一个子集,任何时候都不许超过它。子集大于全集,这句背书就作废了,而它
    /// 印出来与一个正常计数逐字同形 —— 八份盲测轨迹里没有一份当场看出来。
    ///
    /// 红不红验过:把调用改回 <c>rows.Select(r =&gt; r.Path).Distinct().Sum(...)</c>,
    /// <c>find --value RimWorld</c> 命中两条路径(thingClass 与 comps[0].compClass),
    /// 在夹具上报 2 而全库只有 1 个被砍的 def,当场红。
    /// </summary>
    [Fact]
    public void 截断尾注的计数不许超过全库被砍的总数()
    {
        var (all, _, _) = Fixture.Run("snapshot", "truncated", "--limit", "all", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(all);
        var total = doc.RootElement.GetProperty("truncated").GetArrayLength();
        Assert.True(total > 0, "The fixture has no truncated def, so this gate cannot go red.");

        // 覆盖四条会出这句尾注的路:按值查(求和的那条)、按路径+值查、值空间、字段路径表。
        string[][] queries =
        [
            ["find", "--value", "RimWorld"],
            ["find", "--value", "CompShield"],
            ["find", "thingClass", "RimWorld.Apparel"],
            ["values", "thingClass"],
            ["fields", "ThingDef"],
        ];

        foreach (var argv in queries)
        {
            var (text, _, _) = Fixture.Run(argv);
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"Counted over indexed field paths only: (\d+) defs?\b");
            if (!m.Success) continue;
            var counted = int.Parse(m.Groups[1].Value);
            Assert.True(counted <= total,
                $"'{string.Join(' ', argv)}' counted {counted}, out of a snapshot-wide truncated total of " +
                $"{total} — a subset larger than the whole, printed exactly like a sound count.");
        }
    }

    /// <summary>
    /// 同一句尾注的第二处:它原先只在内层收窄 scope,外层的 COUNT 不带谓词,于是
    /// <c>--scope</c> 把结果收进一个 mod 之后,「可能属于这里而没露面」说的仍是一批
    /// scope 明明排除掉的 def。四份轨迹各自报过「计数不跟随 --scope」。
    /// </summary>
    [Fact]
    public void 截断尾注跟随scope收窄()
    {
        // 夹具里唯一被砍的 def(Bullet_Revolver)属于 ludeon.rimworld,而同一个值
        // RimWorld.CompShield 在 test.mod 的 TestModGun 上也有 —— 收窄之后仍有结果,
        // 变的只是这句背书该不该说话。
        var (wide, _, _) = Fixture.Run("find", "--value", "CompShield");
        Assert.Contains("Counted over indexed field paths only:", wide);

        // 收到 test.mod 之后被砍的那个不在 scope 里,这句背书就不该再提它。
        var (narrow, _, _) = Fixture.Run("find", "--value", "CompShield", "--scope", "test.mod");
        Assert.DoesNotContain("Counted over indexed field paths only:", narrow);
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

        // 第四种成因:树在名单里、目录也在磁盘上,里面一个文件都没有(从没反编译过)。
        // 它此前落进上面那条 glob 分支,于是读的人去改 glob,而真因是这棵树该 sync 一遍。
        var (bare, _, bareCode) = Fixture.Run("code-search", "public", "--source", "zz.emptytree");
        Assert.Equal(1, bareCode);
        Assert.Contains("holds no decompiled files", bare);
        Assert.Contains("sources sync", bare);
        Assert.DoesNotContain("No file matched", bare);   // 不许再赖到 glob 头上
        Assert.DoesNotContain("rimsearcher search", bare);

        // glob 那条的子形状:别名 --file-extension 收下裸扩展名,值却按 glob 解。
        // 「这里没有 .cs 文件」与「你的值不是 glob」此前逐字同形。
        var (ext, _, extCode) = Fixture.Run("code-search", "public", "--file-extension", "cs");
        Assert.Equal(1, extCode);
        Assert.Contains("no wildcard", ext);
        Assert.Contains("'*.cs'", ext);

        // 反向:带通配符的 glob 打不中时,不许再教人加通配符。
        var (starred, _, _) = Fixture.Run("code-search", "public", "--files", "*.zzz");
        Assert.Contains("No file matched", starred);
        Assert.DoesNotContain("no wildcard", starred);
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
        // 那六种落点全在快照里,而快照只装 def 侧。听上去穷尽、其实没查代码树 ——
        // 第四轮 B6 的 MapPortal 就活在 vanilla 树里。没查的那一半必须自己说破。
        Assert.Contains("code-search \"class NoSuchDefAnywhere\"", absent, StringComparison.Ordinal);
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
    /// 参数被改写就要说破。<c>LimitValue.Clamped</c> 与 <c>NoticeKind.Clamp</c> 从落地起
    /// 一个引用点都没有:解析器老实记下了「这个数被我改了」,却没有任何一条路把它印出来,
    /// 于是 <c>--limit 5000</c> 与 <c>--limit 2000</c> 的输出逐字相同 —— 又一处
    /// 「静默改写调用方给的参数」,与 R11 吞掉 --exact 同形。
    /// </summary>
    [Fact]
    public void 超过上限的limit不被悄悄夹紧()
    {
        var (stdout, _, _) = Fixture.Run("list", "ThingDef", "--limit", (Limits.MaxLimit + 1).ToString());
        Assert.Contains(Limits.MaxLimit.ToString(), stdout, StringComparison.Ordinal);
        Assert.Contains((Limits.MaxLimit + 1).ToString(), stdout, StringComparison.Ordinal);

        var (plain, _, _) = Fixture.Run("list", "ThingDef", "--limit", Limits.MaxLimit.ToString());
        Assert.DoesNotContain("ceiling", plain, StringComparison.Ordinal);

        // 机器侧靠 kind 分类,不靠措辞。
        var (json, _, _) = Fixture.Run("list", "ThingDef", "--limit", (Limits.MaxLimit + 1).ToString(), "--json");
        Assert.Contains("\"kind\": \"clamp\"", json, StringComparison.Ordinal);
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

    // ---- R1:C# 声明默认值与被人设过的值不许同形 ----

    /// <summary>
    /// R1 报告里那一行的形状:**字段名与提问一字不差,值却是声明默认值**。四个错结论
    /// 全是这么生成的。所以 <c>--path</c> 点了名的字段绝不许因为「是默认值」而消失 ——
    /// 藏起来会把回答变成「没有路径含 burstCount」,那是比印错值更彻底的假话。
    /// </summary>
    [Fact]
    public void 点了名的字段不因为是默认值而消失()
    {
        var (named, _, code) = Fixture.Run("get", "Bullet_Revolver", "--path", "burstCount");
        Assert.Equal(0, code);
        Assert.Contains("projectile.burstCount", named, StringComparison.Ordinal);
        // 印出来还不够,还得说清它是哪一种 —— 只印值就退回了 R1 本身。
        Assert.Contains(FieldDefault.Column, named, StringComparison.Ordinal);
        Assert.Contains("yes", named, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--path</c> 筛空的两种成因不许同形:def 真没有这条路径,与**给进来的文本是个值**。
    /// 第四轮回归实测的形状(B5):stat 名装在 <c>statBases[N].stat</c> 里,按它筛路径必空,
    /// 而「值在不在这个 def 上」是算得出来的 —— 算出来再说,不猜。
    /// </summary>
    [Fact]
    public void 把值当成路径筛时说破它是个值()
    {
        var (asValue, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "MarketValue");
        Assert.Equal(0, code);
        Assert.Contains("No field path", asValue, StringComparison.Ordinal);
        Assert.Contains("as a field's value", asValue, StringComparison.Ordinal);
        Assert.Contains("find --value MarketValue", asValue, StringComparison.Ordinal);

        // 反向:真的哪儿都没有时,不许无中生有地指路去 find --value。
        var (nowhere, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "zzzznothing");
        Assert.Contains("No field path", nowhere, StringComparison.Ordinal);
        Assert.DoesNotContain("as a field's value", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// 不点名时默认值行不列,但**不许静默**:少了多少条、为什么、怎么看回来,都要在场。
    /// 机器侧靠 kind 分类(这是过滤不是截断,混用会让扫 notes 的下一位读成「结果不完整」)。
    /// </summary>
    [Fact]
    public void 默认值行被拿掉时当场说清有多少条()
    {
        var (plain, _, _) = Fixture.Run("get", "Bullet_Revolver");
        Assert.DoesNotContain("projectile.burstCount", plain, StringComparison.Ordinal);
        Assert.Contains("Not listed", plain, StringComparison.Ordinal);
        Assert.Contains("--defaults", plain, StringComparison.Ordinal);

        var (json, _, _) = Fixture.Run("get", "Bullet_Revolver", "--json");
        Assert.Contains("\"kind\": \"filter\"", json, StringComparison.Ordinal);

        var (all, _, _) = Fixture.Run("get", "Bullet_Revolver", "--defaults");
        Assert.Contains("projectile.burstCount", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// 三态里「没法比」最容易被顺手并进某一边。它必须**照常显示**(少省一点篇幅,
    /// 换「不会有值凭空消失」),而且在列里与「有人改过」分得开 —— 把没比成印成
    /// 有人改过,正是 R1 本身,只是换了个入口。
    /// </summary>
    [Fact]
    public void 没法比的那一档照常显示且不与被改过的同形()
    {
        var (plain, _, _) = Fixture.Run("get", "Bullet_Revolver");
        Assert.Contains("projectile.speed", plain, StringComparison.Ordinal);
        Assert.Contains("unknown", plain, StringComparison.Ordinal);

        Assert.Equal("no", FieldDefault.Render(DefaultState.Differs));
        Assert.Equal("yes", FieldDefault.Render(DefaultState.Same));
        Assert.Equal("unknown", FieldDefault.Render(DefaultState.Unknown));
        Assert.Equal(3, new[] { DefaultState.Differs, DefaultState.Same, DefaultState.Unknown }
            .Select(FieldDefault.Render).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 表的形状不许随数据变:这一列恒在,不因为「本次没有默认值行」就消失。照着一次输出
    /// 写解析器的人下一次还得取到同一个键,而缺键与「值是 no」在 JSON 里是两回事。
    /// </summary>
    [Fact]
    public void 默认值列恒在而不随本次有没有默认值行出现()
    {
        // Anesthetic 只有一条字段,且不是默认值 —— 「本次没有默认值行」的那一种。
        var (json, _, _) = Fixture.Run("get", "Anesthetic", "--json");
        Assert.Contains($"\"{FieldDefault.Column}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Not listed", json, StringComparison.Ordinal);

        // find 走另一条查询路径,同样要带这一列。
        var (find, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield", "--json");
        Assert.Contains($"\"{FieldDefault.Column}\"", find, StringComparison.Ordinal);
    }
}
