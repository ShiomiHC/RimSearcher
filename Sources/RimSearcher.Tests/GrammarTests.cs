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

            // 判据是「朴素加 s 会不会拼错这个词」,而原先它按结尾字符串直接匹配 ——
            // 于是 `key → keys`(**正确**的英语:元音 + y 只加 s)被判成拼错。
            // 04 记过一次「闸把一句完全正确的话判红」(GateTests 那条复数自噬),
            // 这是同一形状的第二次:判据写成了它想表达的规则的一个粗糙代理。
            // 现在只在**辅音 + y**(city → cities)与 -ch/-sh(match → matches)上要求
            // 登记形态不同于朴素加 s。
            var last = noun.Split(' ')[^1];
            var consonantY = last.Length >= 2 && last[^1] == 'y' && !"aeiou".Contains(last[^2]);
            var sibilant = last.EndsWith("ch", StringComparison.Ordinal) ||
                           last.EndsWith("sh", StringComparison.Ordinal);
            if (consonantY || sibilant)
                Assert.NotEqual(noun + "s", plural);
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

        // 写死 packageId:你写的就是你得到的,**播报那一行不多印**。
        // 断言写成「不含带括号的那种形态」是不够的 —— 判据一旦退回「多于一个 mod 就播」,
        // 印出来的是不带括号的 `--scope ludeon.rimworld.`,那样的闸红不了。所以钉的是
        // 「有没有一条**以它开头的声明行**」:计数句里那句 `1 def within --scope
        // ludeon.rimworld.` 是另一件事的产地(用户侧收窄要念回去),不在这条闸的射程内。
        var (literal, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld");
        Assert.DoesNotMatch(new Regex(@"^--scope ludeon\.rimworld", RegexOptions.Multiline), literal);

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
        // 12 行而不是 9:Widgets.cs 末尾加了三行 .Translate() 语料(keyed 那一层的落点)。
        var (whole, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs");
        Assert.Contains("all 12 lines", whole, StringComparison.Ordinal);
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
        Assert.Contains("2 of 9 defs, starting at 3", mid, StringComparison.Ordinal);
        Assert.Contains("--offset 4", mid, StringComparison.Ordinal);

        var (last, _, lastCode) = Fixture.Run("list", "ThingDef", "--limit", "4", "--offset", "5");
        Assert.Equal(0, lastCode);
        Assert.Contains("starting at 6", last, StringComparison.Ordinal);
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

    /// <summary>
    /// 一个 defName 对应几行是常态(Firefoam 既是 ThingDef 又是 StatDef,mod 覆盖原版时同理)。
    /// 模糊回退在这里建过「名字 → 一行」的字典,同名处当场抛 —— 用户打错一个字母,
    /// 换回来一个 exit 70。闸判三件事:不许崩、两行都在、页脚的数按**行**算。
    ///
    /// 第三件是前两件的陪嫁:只留一行也「不崩」,而那份输出与正确输出逐字同形。
    /// </summary>
    [Fact]
    public void 同名两个def不许把模糊兜底打成内部错误()
    {
        var (stdout, _, code) = Fixture.Run("search", "Firefoan");
        Assert.Equal(0, code);

        // 两条 Firefoam 各是一个 def,都得出现 —— 少一条正是「同形的错答案」。
        Assert.Contains("ThingDef", stdout, StringComparison.Ordinal);
        Assert.Contains("StatDef", stdout, StringComparison.Ordinal);

        // 页脚报的数不许小于表里的行数。
        var rows = Regex.Matches(stdout, @"^Firefoam\s", RegexOptions.Multiline).Count;
        Assert.Equal(2, rows);
        if (Regex.Match(stdout, @"\b(\d+) of (\d+) defs?\b") is { Success: true } m)
            Assert.True(int.Parse(m.Groups[2].Value) >= rows,
                $"页脚说共 {m.Groups[2].Value} 条,表里印了 {rows} 行:{stdout}");
    }

    /// <summary>
    /// FTS 只索引译文的**译过来的那一侧**。于是一份中文快照上,每个 def 的英文原文都在库里
    /// 躺着,却一个也搜不到 —— 而落空那句话当时还写着「covers … and translations」,
    /// 把只覆盖一半说成覆盖全部。
    ///
    /// 语料里 "A blob of firefoam." 只在 original 侧,label 与 description 都不含 blob。
    /// </summary>
    [Fact]
    public void 英文原文在中文快照上搜得到()
    {
        var (stdout, _, code) = Fixture.Run("search", "blob");
        Assert.Equal(0, code);
        Assert.Contains("Firefoam", stdout, StringComparison.Ordinal);

        // 兜底必须**排在模糊回退之前**:不然拼写噪声会把真答案挤掉,
        // 而那份输出看起来就是「没有,这是几个拼写相近的」。
        Assert.DoesNotContain("closest names by spelling", stdout, StringComparison.Ordinal);

        // 命中来自哪一侧要说破 —— 不说,这一行在中文快照上无从解释。
        Assert.Contains("original", stdout, StringComparison.Ordinal);

        // 译文按 defName 关联(注入目录名是 XML 根元素,与运行时桶名对不上是常态,
        // 拿它做连接条件会整批漏),于是同名的 StatDef 也进来了。它自己一个字都不含
        // 查询词,那一格就不许写成与「真·靠索引文本命中」同形的解释。
        var stat = Assert.Single(stdout.Split('\n'), l => l.Contains("StatDef", StringComparison.Ordinal));
        Assert.Contains("same def_name", stat, StringComparison.Ordinal);
        Assert.DoesNotContain("indexed text", stat, StringComparison.Ordinal);
    }

    /// <summary>
    /// 子串匹配不留痕 —— 第五轮盲测里直接产出错结论的那一条。
    /// `get X --path soundImpact` 只回一行 `soundImpactDefault`(语义相反的另一个字段,
    /// `code_default=no` 让它看着像作者刻意设的),而输出里没有一处说过「你打的这个词
    /// 作为完整的一段一次都没命中」。
    ///
    /// 三个落点都要判,因为改一处剩两处的输出一字不变:`get --path` / `fields --path` /
    /// `find --value`。每处判两档:一条整段都没有 → 说破;有整段也有子串 → 给拆分。
    /// </summary>
    [Fact]
    public void 子串匹配要说破自己不是整段命中()
    {
        // get:语料里 Apparel_ShieldBelt 有 comps[0].props.energyMax。查 "energy" 命中它,
        // 而没有任何一段整个叫 energy。
        var (get0, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "energy");
        Assert.Contains("whole path segment", get0, StringComparison.Ordinal);
        Assert.Contains("nothing here is called exactly that", get0, StringComparison.Ordinal);
        // 第七轮 T2:这句话原先收在一句关于存在性的强断言上,而它对「前缀式列举」这个
        // 正常用法照喊 —— 一份轨迹「差一点因为那句话就掠过去了」,而要的字段就在下面那张表里。
        // 被劝退才是它造成的真损失,所以「这一行一条都没滤掉」这半句是承重的。
        Assert.Contains("removes none of the matched fields", get0, StringComparison.Ordinal);

        // 查 "comps" 则条条整段命中 —— 这时候一个字都不许多说。
        var (getAll, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "comps");
        Assert.DoesNotContain("whole path segment", getAll, StringComparison.Ordinal);
        Assert.DoesNotContain("longer name", getAll, StringComparison.Ordinal);

        // fields:同一条纪律在「这个类型有没有这个字段」的正式问法上。
        var (f0, _, _) = Fixture.Run("fields", "ThingDef", "--path", "energy");
        Assert.Contains("whole path segment", f0, StringComparison.Ordinal);

        var (fMix, _, _) = Fixture.Run("fields", "ThingDef", "--path", "compClass");
        Assert.DoesNotContain("whole path segment", fMix, StringComparison.Ordinal);

        // find --value:值侧。语料里 compClass 的值是 RimWorld.CompShield,
        // 整值等于 CompShield 的一条都没有。
        var (v0, _, _) = Fixture.Run("find", "--value", "CompShield");
        Assert.Contains("No value here is exactly 'CompShield'", v0, StringComparison.Ordinal);

        // 而 --exact 是调用方自己点的名,这时候不许再劝一遍。
        var (vExact, _, _) = Fixture.Run("find", "--value", "RimWorld.CompShield", "--exact");
        Assert.DoesNotContain("exactly", vExact, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「本快照没有」在读的人眼里就是「这东西不存在」。R10 原先只覆盖「按名字取一个 def」
    /// 那条路,而第五轮实测里落空的是 `find` —— races 那份快照里明明有 6 条。
    ///
    /// 叠加不替换:本快照那句成因分流一个字不许少,别处那句排在它**后面**。
    /// </summary>
    [Fact]
    public void find落空时说破别的快照里有()
    {
        // OtherMod.CompOnlyElsewhere 只在 other.db 里。
        var (byValue, _, code) = Fixture.Run("find", "--value", "CompOnlyElsewhere");
        Assert.Equal(1, code);
        Assert.Contains("No field in this snapshot holds a value", byValue, StringComparison.Ordinal);
        Assert.Contains("'other'", byValue, StringComparison.Ordinal);
        Assert.Contains("--snapshot other", byValue, StringComparison.Ordinal);

        // 成因分流那句必须排在前面 —— 顺序反了就成了「换一份快照」压过「这里为什么没有」。
        Assert.True(byValue.IndexOf("No field in this snapshot", StringComparison.Ordinal) <
                    byValue.IndexOf("Another registered snapshot", StringComparison.Ordinal));

        // 指名字段那条路同样要接上。
        var (byField, _, _) = Fixture.Run("find", "compClass", "CompOnlyElsewhere");
        Assert.Contains("--snapshot other", byField, StringComparison.Ordinal);

        // 哪儿都没有的时候不许无中生有 —— 一句「别处有」比没有更坏。
        var (nowhere, _, _) = Fixture.Run("find", "--value", "NoSuchValueAnywhereAtAll");
        Assert.DoesNotContain("Another registered snapshot", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「像不像类名」这个判据决定要不要说一句**猜测**,而猜错的代价是把未经验证的
    /// 「如果 X 是抽象基类……」摆在输出位置,读的人当结论用。
    ///
    /// 旧判据三处各写各的,`IsUpper(v[0]) || Contains('.')` 会把 XML 里最常见的两种值
    /// 一并算进来:`True`(首字母大写)、`Sounds/Foo.ogg`(含点)。
    /// </summary>
    [Fact]
    public void 像不像类名的判据挡得住字面量与资源路径()
    {
        Assert.True(ClassNameShape.Looks("CompShield"));
        Assert.True(ClassNameShape.Looks("RimWorld.Bullet"));
        Assert.True(ClassNameShape.Looks("MapPortal"));

        Assert.False(ClassNameShape.Looks("True"));
        Assert.False(ClassNameShape.Looks("False"));
        Assert.False(ClassNameShape.Looks(".ogg"));
        Assert.False(ClassNameShape.Looks("Foo.ogg"));
        Assert.False(ClassNameShape.Looks("Sounds/Impact"));
        Assert.False(ClassNameShape.Looks("Comp Shield"));
        Assert.False(ClassNameShape.Looks("1.5"));
        Assert.False(ClassNameShape.Looks("12"));
        Assert.False(ClassNameShape.Looks("Steel"));
        Assert.False(ClassNameShape.Looks("AB"));

        Assert.Equal("Bullet", ClassNameShape.Tail("RimWorld.Bullet"));
        Assert.Equal("MapPortal", ClassNameShape.Tail("MapPortal"));

        // 落到输出上:值是 True 时不许出现那段索引边界,值是类名形状时必须出现。
        var (literal, _, _) = Fixture.Run("find", "--value", "True");
        Assert.DoesNotContain("If that is a class name", literal, StringComparison.Ordinal);

        var (cls, _, _) = Fixture.Run("find", "--value", "NoSuchCompClass");
        Assert.Contains("If that is a class name", cls, StringComparison.Ordinal);
        // 指的路是本工具自己那条,而且带上要搜的符号 —— 光说「用 code-search」
        // 等于把拼命令行这一步扔回给读的人。
        Assert.Contains("code-search \"class NoSuchCompClass", cls, StringComparison.Ordinal);
    }

    /// <summary>
    /// `sources list` 的表头报「33 棵树 / 24 个 mod」,两个数怎么合上原先要读者自己数 33 行。
    /// 对账行把两边各自拆到桶里,而闸判的正是**加得起来** —— 一句对不上的对账比没有更坏,
    /// 它让人以为自己看懂了。
    ///
    /// 两条等式各只用一个单位:混着数会得出一个凑巧对得上的和(树侧的 vanilla 那一棵
    /// 同时又代表 mod 侧那几个 packageId,重复计一次照样「相等」)。
    /// </summary>
    [Fact]
    public void sources对账的两条等式都要加得起来()
    {
        var (stdout, _, _) = Fixture.Run("sources", "list");

        var mods = Regex.Match(stdout,
            @"Mods in [^(]*\((\d+)\): (\d+) with a tree of their own, (\d+) folded into[^,]*, " +
            @"(\d+) with no assembly to decompile, (\d+) not installed here, (\d+) the exporter itself\.");
        Assert.True(mods.Success, stdout);
        var m = mods.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value)).ToList();
        Assert.Equal(m[0], m[1] + m[2] + m[3] + m[4] + m[5]);

        var trees = Regex.Match(stdout,
            @"Trees on disk \((\d+)\): (\d+) current, (\d+) stale, (\d+) never built, " +
            @"(\d+) holding no \.cs file, (\d+) from outside");
        Assert.True(trees.Success, stdout);
        var t = trees.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value)).ToList();
        Assert.Equal(t[0], t[1] + t[2] + t[3] + t[4] + t[5]);

        // 只加注,不缩范围。把这件事实现成「默认只扫快照内的树」会让穷举论证整批作废,
        // 而降级前后的输出一模一样 —— 所以这句承诺本身要在场。
        Assert.Contains("reads every tree either way", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 完整性尾注末尾那条命令,要走得到**它刚说的那一批**。
    ///
    /// 原先一律给裸 `snapshot truncated`,而它列的是全库(真快照上 239 条),尾注刚说的
    /// 却是「与本次结果同类型」的一小批。照着跑一遍拿到的是另一个集合,而两份输出的
    /// 形状一模一样 —— 上一道闸只验了那条命令**存在**,这一道验它**走得到**。
    /// </summary>
    [Fact]
    public void 完整性尾注指的命令要走得到它刚说的那批()
    {
        var (stdout, _, _) = Fixture.Run("find", "--value", "CompShield");

        var m = Regex.Match(stdout,
            @"Counted over indexed field paths only: [^']*?also hold (\d+) defs? that lost fields[^']*" +
            @"'rimsearcher snapshot truncated([^']*)' lists them\.");
        Assert.True(m.Success, stdout);

        var claimed = int.Parse(m.Groups[1].Value);
        var argv = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(argv);   // 裸命令走到的是全库,不是刚说的那批

        var (listed, _, _) = Fixture.Run(["snapshot", "truncated", .. argv, "--limit", "all"]);
        // 计数句现在会把用户自己划的那道线念回去(`1 def within --type ThingDef.`)——
        // 数还是同一个数,取数的正则跟着放宽,不是把那半句当噪音滤掉。
        var got = Regex.Match(listed, @"^(\d+) defs?( within [^.]*)?\.", RegexOptions.Multiline);
        Assert.True(got.Success, listed);
        Assert.Equal(claimed, int.Parse(got.Groups[1].Value));

        // 第六轮:句子原先写「defs of **the same def types**」,而它指的是「哪些类型能带这条
        // 路径」,与表里那几行的类型没有任何关系 —— 实测 `find label 狂暴 --exact` 四行全是
        // MentalStateDef,脚注却建议 `--type BodyDef --type DutyDef --type ThingDef`。
        // 「the same」是句子里唯一让人去对照的那个词,而它对照的东西不存在。
        Assert.DoesNotContain("the same def types", stdout, StringComparison.Ordinal);

        // 类型要在散文里点名,而且与命令里那几个逐字一致 —— 不点名就没法核对。
        foreach (var t in argv.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
            Assert.Contains(t, stdout.Split("'rimsearcher snapshot truncated")[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// 收窄之后的零结果与「整份快照都没有」不是一回事,而两句话原先是同一句 ——
    /// 「counts over field paths are complete for it」在收窄时担保的是整份快照,
    /// 而它只查了其中一小块。
    /// </summary>
    [Fact]
    public void 收窄之后的零结果不许担保整份快照()
    {
        var (narrow, _, _) = Fixture.Run("snapshot", "truncated", "--def", "Anesthetic");
        Assert.Contains("--def Anesthetic", narrow, StringComparison.Ordinal);
        Assert.Contains("for that much", narrow, StringComparison.Ordinal);
        // 全库那个数要一起给出来,否则「这里没有」读起来就是「哪儿都没有」。
        Assert.Matches(@"Snapshot-wide the figure is \d+ defs?\.", narrow);
    }

    /// <summary>
    /// 同一块 `comps[N]` 里的字段互相约束。第五轮实测:`minFuelCost=50` 盖掉同块的
    /// `fuelPerTile=3`,差 16 倍 —— 而只列出后者的那张表干净、计数明确、一条警告都没有。
    ///
    /// 语料里 Apparel_ShieldBelt 的 comps[0] 有三条:compClass(默认值,不点名)、
    /// props.energyMax(有人设过)、index(默认值)。
    /// </summary>
    [Fact]
    public void 同一块里有人设过的兄弟字段要点名()
    {
        var (get, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "energyMax");
        Assert.Contains("same block as the rows above", get, StringComparison.Ordinal);
        Assert.Contains("energyLossPerDamage", get, StringComparison.Ordinal);

        // 只点 code_default=no 的:默认值那一批没人挑过,列出来等于把类的字段表倒一遍。
        var tail = get.Split("same block")[1];
        Assert.DoesNotContain("compClass", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("index", tail, StringComparison.Ordinal);

        // find 走另一条路,同一句话要在。
        var (find, _, _) = Fixture.Run("find", "energyMax", "0.5");
        Assert.Contains("energyLossPerDamage", find, StringComparison.Ordinal);

        // 但**你看的这一行自己**是声明默认值时不提示:判别字段按定义就是默认值,
        // 而 `find compClass CompShield` 是文档推荐的那条主查询 —— 在它上面挂一句
        // 「同块还有 energyMax」是纯噪音,而噪音要在所有调用上收税。
        var (disc, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield");
        Assert.DoesNotContain("same block as the rows above", disc, StringComparison.Ordinal);

        // 不带下标的层不算容器 —— 那是分类不是实例,兄弟太多且不成组,
        // 提示会退化成每次都挂的免责声明。
        Assert.Null(PathSegments.ContainerPrefix("projectile.damageAmountBase"));
        Assert.Equal("comps[0].", PathSegments.ContainerPrefix("comps[0].props.energyMax"));

        // 同块里没有别人设过的东西时,一个字都不说。
        var (quiet, _, _) = Fixture.Run("get", "TestModGun", "--path", "compClass");
        Assert.DoesNotContain("same block as the rows above", quiet, StringComparison.Ordinal);

        // 第六轮:块名不许写死成 comps[N] —— ContainerPrefix 对任何带下标的层都成立,
        // 而实测里 statBases[8]、corePart.parts[6]、degreeDatas[0].statFactors[0]
        // 三种块上都挂出了「Fields in one comps[N] entry」。8 份轨迹里 3 份撞上。
        var (stat, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "statBases[0].stat");
        Assert.Contains("statBases[0]", stat.Split("same block")[1], StringComparison.Ordinal);
        Assert.DoesNotContain("comps[N]", stat, StringComparison.Ordinal);

        // 而且指的那条路要**填好**再发出去。原先发的是字面量
        // `rimsearcher get <defName> --path <block>`,两个占位符一个没填。
        Assert.DoesNotContain("<defName>", stat, StringComparison.Ordinal);
        Assert.DoesNotContain("<block>", stat, StringComparison.Ordinal);
        Assert.Contains("rimsearcher get Apparel_ShieldBelt --path statBases[0]", stat, StringComparison.Ordinal);

        // 走得到:那条命令真列得出刚被点名的兄弟。
        var (whole, _, wcode) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "statBases[0]");
        Assert.Equal(0, wcode);
        Assert.Contains("statBases[0].value", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 块级 `--path` 上那句「整段命中」是**必然误报**:判据把 `[N]` 从段里剥掉,
    /// 于是 `comps[0]` 这种带下标的写法永远不可能等于任何一段,而三条命中明明全在
    /// 那个块里。第六轮实测 `get Apparel_ShieldBelt --path "comps[0]"` 回
    /// 「None of those has 'comps[0]' as a whole path segment … so a field by exactly
    /// that name may not exist here」—— 每个字都在把人推离正确答案。
    /// </summary>
    [Fact]
    public void 块级路径不许被判成子串误命中()
    {
        var (block, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "comps[0]");
        Assert.Equal(0, code);
        Assert.DoesNotContain("whole path segment", block, StringComparison.Ordinal);
        Assert.Contains("comps[0].props.energyMax", block, StringComparison.Ordinal);

        // 不带下标的裸名字照旧走整段判定 —— 这一改只放过「本来就是块前缀」的写法。
        var (leaf, _, _) = Fixture.Run("get", "Bullet_Revolver", "--path", "damageAmount");
        Assert.Contains("whole path segment", leaf, StringComparison.Ordinal);
    }

    /// <summary>
    /// 落空那句话报的值域不许超发。它说 covers 什么,就得真覆盖到什么。
    /// </summary>
    [Fact]
    public void 落空句报的值域与真覆盖面一致()
    {
        var (stdout, _, code) = Fixture.Run("search", "NoSuchThingAnywhere");
        Assert.Equal(1, code);

        // 「translations」不带限定就是承诺两侧都覆盖。真覆盖到了才准这么写;
        // 而覆盖到了,上面那道闸就得是绿的 —— 两条互为对方的证明。
        var (blob, _, _) = Fixture.Run("search", "blob");
        if (!blob.Contains("Firefoam", StringComparison.Ordinal))
            Assert.DoesNotContain("translations", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「别的命令认这个参数」那句话原先只列前三条就收尾。`--limit` 挂在十一条命令上,
    /// 而 `snapshot list --limit 5` 吃到的回话是
    /// 「It is accepted by 'search' and 'get' and 'find'」—— 与「一共就这三条认」逐字同形,
    /// 而真相是「几乎每条都认,不认的偏偏是你敲的这条」。后半句才是让人改对的那半句,
    /// 也正好是被省略号吃掉的那个数量,与 <see cref="NameList"/> 那一轮清的是同一个形状。
    /// </summary>
    [Fact]
    public void 别处认这个参数的名单不许悄悄截断()
    {
        var (_, err, code) = Fixture.Run("snapshot", "list", "--limit", "5");
        Assert.Equal(2, code);
        Assert.Contains("It is accepted by", err, StringComparison.Ordinal);
        Assert.Matches(@"and \d+ more, but not by 'snapshot list'", err);

        // 只有一两条认的时候不许凭空长出尾巴。
        var (_, one, _) = Fixture.Run("snapshot", "list", "--member", "x");
        Assert.Contains("It is accepted by 'read', but not by", one, StringComparison.Ordinal);
    }

    /// <summary>
    /// 零行就是 exit 1,`types` 也不例外。R12 的退出码约定被四条命令守着、被这一条破着:
    /// `types --scope brrainz.harmony` 印「0 def types.」然后 exit 0 —— 脚本按退出码分流
    /// 会把它当成「查到了」,而唯一能纠正的信息在那一行散文里。
    ///
    /// 顺带:那句话也不许把 scope 造成的空说成快照的空。`values` 原先回
    /// 「No def in this snapshot has a field path ending in 'defName'」——
    /// 而不带 scope 时 defName 每个 def 都有。
    /// </summary>
    [Fact]
    public void 零行一律exit1且不把scope的空说成快照的空()
    {
        var empty = new[] { "--scope", "all,-ludeon.rimworld,-test.mod" };

        var (types, _, tcode) = Fixture.Run(["types", .. empty]);
        Assert.Equal(1, tcode);
        Assert.Contains("--scope all,-ludeon.rimworld,-test.mod", types, StringComparison.Ordinal);
        Assert.Matches(@"Snapshot-wide the figure is \d+ def types?\.", types);

        var (values, _, vcode) = Fixture.Run(["values", "defName", .. empty]);
        Assert.Equal(1, vcode);
        // 快照里明明每个 def 都有 defName —— 空是 scope 造的,句子得这么说。
        Assert.DoesNotContain("No def in this snapshot has a field path ending in 'defName'.",
                              values, StringComparison.Ordinal);
        Assert.Contains("--scope all,-ludeon.rimworld,-test.mod", values, StringComparison.Ordinal);

        // 真不存在的路径照旧说「这快照里没有」,不许被上面那一改冲掉。
        var (gone, _, gcode) = Fixture.Run("values", "zzznotafield");
        Assert.Equal(1, gcode);
        Assert.Contains("No def in this snapshot has a field path ending in 'zzznotafield'",
                        gone, StringComparison.Ordinal);
    }

    /// <summary>
    /// `get` 的 `source` 行印的是**没有目录的裸文件名**,而文档此前一个字都没提这一列。
    ///
    /// 第六轮 C11 与 C41 各自浪费一轮在它上面:SKILL 的「What is out of range」宣称
    /// 「没有从 def 回到可编写源码的路」,而这一列一直在打印文件名 —— 该有的预期没建立,
    /// 不该有的期望也没掐掉,**给一半最费人**。承诺进了 SKILL,这里钉住它说的那件事:
    /// 有这一列、值里没有目录分隔符、代码生成的 def 走的是占位符那一档。
    /// </summary>
    [Fact]
    public void source列印的是没有目录的裸文件名()
    {
        var (json, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var def = doc.RootElement.GetProperty("defs")[0].GetProperty("def");
        var source = def.GetProperty("source").GetString();

        Assert.Equal("Apparel_Belts.xml", source);
        Assert.DoesNotContain('/', source!);
        Assert.DoesNotContain('\\', source!);

        // 代码里造出来的 def 那一档不是文件名,是导出器写死的占位符 —— 两者不许同形。
        var (implied, _, _) = Fixture.Run("get", "Meat_Muffalo");
        Assert.Contains(RimSearcher.Contract.IntermediateFormat.ImpliedDefsSourceFile,
                        implied, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用户自己划的那道线,计数句要念回去。
    ///
    /// 三态计数(Tally)覆盖的是**工具造成的**收窄:行数上限、扫描没跑完。`--scope`
    /// `--type` `--exact` `--path` 这些造成的收窄不在其中,于是
    /// `search 狂暴 --type MentalStateDef` 报一个完整式的「52 defs.」—— 字面完整,
    /// 实则「在我自己划的范围内完整」,而第六轮有三份轨迹据此下了「一个不漏」的结论。
    ///
    /// 三头都要钉:给了就念、没给就一个字不多、**而且不许念错东西** ——
    /// `get --type` 挑的是哪个 def 不是从字段里筛,念回去会被读成「去掉它还有更多字段」,
    /// 而去掉它得到的是另一个 def。判据在声明层(OptionSpec.Narrows),不在这里。
    /// </summary>
    [Fact]
    public void 用户自己划的收窄要在计数句里念回去()
    {
        var (scoped, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "vanilla");
        Assert.Contains("1 def within --scope vanilla.", scoped, StringComparison.Ordinal);

        // 多个收窄参数一起念,顺序按声明层。
        var (two, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet",
                                      "--scope", "vanilla", "--exact");
        Assert.Contains("within --scope vanilla --exact", two, StringComparison.Ordinal);

        // --path 是 Multi,给几次念几次。
        var (paths, _, _) = Fixture.Run("fields", "ThingDef", "--path", "comps");
        Assert.Contains("field paths within --path comps.", paths, StringComparison.Ordinal);

        // 一个都没给就一个字不多 —— 否则这半句退化成每条输出都挂的免责声明。
        var (bare, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet");
        Assert.DoesNotContain(" within ", bare, StringComparison.Ordinal);

        // --limit / --offset 不算收窄:它们管印几行,三态文法早已把那件事说清。
        var (limited, _, _) = Fixture.Run("list", "ThingDef", "--limit", "2");
        Assert.DoesNotContain(" within ", limited, StringComparison.Ordinal);

        // get 的 --type 挑的是哪个 def,不是从这个 def 的字段里筛 —— 不许念。
        var (typed, _, _) = Fixture.Run("get", "Firefoam", "--type", "StatDef");
        Assert.DoesNotContain("within --type", typed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 值侧是**单语**的:`find` 查的是游戏加载时那一份文本,译文的另一侧只活在文本索引里。
    ///
    /// 第六轮实测的形状:中文快照上 `find --value "shield belt"` 回「本快照没有任何字段
    /// 装着这段文本」,而 `search "shield belt"` 当场命中 —— 两句话都对,而前者与
    /// 「这东西真不存在」逐字同形。C41 差点据此答错(AbilityDef Berserk 的简中 label
    /// 是「激怒」而不是「狂暴」),C42 是同一族的另一面。
    ///
    /// 夹具是反过来的一份(英文值 + 中文注入),形状一样:`find --value 护盾腰带` 空手,
    /// 而文本索引里躺着 Apparel_ShieldBelt。真不存在的那种不许挂这句 ——
    /// 挂了它就退化成每次落空都发的免责声明。
    /// </summary>
    [Fact]
    public void 值查不到时要说破值侧是单语的()
    {
        var (byValue, _, code) = Fixture.Run("find", "--value", "护盾腰带");
        Assert.Equal(1, code);
        Assert.Contains("The text index does have '护盾腰带' though", byValue, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt (ThingDef)", byValue, StringComparison.Ordinal);
        Assert.Contains("rimsearcher search 护盾腰带", byValue, StringComparison.Ordinal);

        // 指名字段的那一支走的是另一条分流,同样得挂(夹具的 label 不是字段路径,
        // 拿一条真存在的路径来问 —— 落空的是**值**,而不是路径)。
        var (byField, _, _) = Fixture.Run("find", "thingClass", "护盾腰带");
        Assert.Contains("The text index does have", byField, StringComparison.Ordinal);

        // 文本索引里也没有的,一个字都不说。
        var (gone, _, _) = Fixture.Run("find", "--value", "zzznothingatall");
        Assert.DoesNotContain("The text index does have", gone, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表里两行的 label **与 def 类型都一样**,而问的人只想要其中一个。
    ///
    /// 第六轮 C42:`TrapSpringChance` 与 `PawnTrapSpringChance` 的简中 label 都是
    /// 「陷阱触发率」,def_type 都是 StatDef,mod 都是 Core。这一类不能靠「多说一句边界」修
    /// —— 查询技术上成功了,表是完整的,没有任何异常信号,只是看得见的那几列不足以判。
    ///
    /// 跨类型的那种(ConceptDef 与 ThingDef 都叫「护盾腰带」)表里当场分得开,不许出声:
    /// 那时「表里没有列分得开」这半句本身是**假的**,一句为防误读而加的话自己先说错,
    /// 比不说更坏。
    /// </summary>
    [Fact]
    public void 同类型里label逐字相同的行要点出来()
    {
        var (clash, _, _) = Fixture.Run("search", "firefoam");
        Assert.Contains("carry the same label and the same def type", clash, StringComparison.Ordinal);
        Assert.Contains("'firefoam' (ThingDef: Firefoam, FoamPopper)", clash, StringComparison.Ordinal);
        Assert.Contains("--path description", clash, StringComparison.Ordinal);

        // 同名跨类型的那一对(ThingDef / StatDef 都叫 Firefoam)label 并不相同,不许误发。
        var (single, _, _) = Fixture.Run("search", "shield belt");
        Assert.DoesNotContain("carry the same label", single, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一次 `find` 的命中横跨几种**路径形状**,得当场说出来。
    ///
    /// 第六轮 C31:`find stat Mass` 的 1229 行里混着 1 行 `statFactors[N].stat`,其余是
    /// `statBases[N].stat`。拿这个结果集做集合差时那一行是个**静默假阴性** —— 表里确实印了
    /// path 列,但默认 25 行的视图下没人会逐行核对形状,而 `find` 恰恰是这套命令里
    /// 用来做集合运算的那一个。`values` 早有 matched_paths 表头,这是把同一条补到 find 上。
    ///
    /// 数的是**整个结果集**不是这一页:翻一页换一句结论是同一个病换个位置。
    /// 只有一种形状时一个字不说。
    /// </summary>
    [Fact]
    public void find的命中横跨多种路径形状时要说破()
    {
        var (mixed, _, _) = Fixture.Run("find", "stat", "MarketValue", "--limit", "1");
        Assert.Contains("span more than one path shape", mixed, StringComparison.Ordinal);
        Assert.Contains("statBases[].stat (2)", mixed, StringComparison.Ordinal);
        Assert.Contains("statFactors[].stat (1)", mixed, StringComparison.Ordinal);

        var (single, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield");
        Assert.DoesNotContain("span more than one path shape", single, StringComparison.Ordinal);
    }

    /// <summary>
    /// `--json` 里那个数据键**恒在**,零行时是空数组,不是整个消失。
    ///
    /// 第六轮实测:`find … --offset 9000 --json` 回的对象里根本没有 `matches`,消费方拿到的
    /// 不是空数组而是 KeyError。而这份 JSON 上「翻过头了」「快照里没有」「工具崩了」
    /// 形状完全一样 —— 都是那个键不在。翻页越界只是它最容易撞到的一副面孔,
    /// 所以闸按**命令**过一遍七条查询命令的各种零行成因,而不是只钉越界那一种。
    ///
    /// 反向也要钉:`find` 的两张表互斥,认领的那张有、另一张不许平白出现 ——
    /// 空数组在机器侧读作「查过了,没有」,凭空多一个就是凭空多一句假话。
    /// </summary>
    [Fact]
    public void json的数据键零行时是空数组而不是整个消失()
    {
        (string Key, string[] Argv)[] cases =
        [
            ("defs",      ["search", "zzznothing"]),
            ("defs",      ["list", "ThingDef", "--offset", "9000"]),
            ("matches",   ["find", "compClass", "zzznothing"]),
            ("matches",   ["find", "compClass", "--offset", "9000"]),
            ("paths",     ["find", "--value", "zzznothing"]),
            ("fields",    ["fields", "ThingDef", "--path", "zzznothing"]),
            ("values",    ["values", "zzznotafield"]),
            ("types",     ["types", "--scope", "all,-ludeon.rimworld,-test.mod"]),
            ("truncated", ["snapshot", "truncated", "--def", "zzznothing"]),
            ("matches",   ["code-search", "zzzznothing"]),
        ];

        foreach (var (key, argv) in cases)
        {
            var (json, err, _) = Fixture.Run([.. argv, "--json"]);
            Assert.True(json.Length > 0, $"'{string.Join(" ", argv)}' 没有 stdout: {err}");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty(key, out var data),
                        $"'{string.Join(" ", argv)}' 的 JSON 里没有 '{key}' 键");
            Assert.Equal(System.Text.Json.JsonValueKind.Array, data.ValueKind);
            Assert.Equal(0, data.GetArrayLength());
        }

        // find 的两张表互斥,不许两个都出现。
        var (byField, _, _) = Fixture.Run("find", "compClass", "zzznothing", "--json");
        using var f = System.Text.Json.JsonDocument.Parse(byField);
        Assert.False(f.RootElement.TryGetProperty("paths", out _));

        var (byValue, _, _) = Fixture.Run("find", "--value", "zzznothing", "--json");
        using var v = System.Text.Json.JsonDocument.Parse(byValue);
        Assert.False(v.RootElement.TryGetProperty("matches", out _));
    }

    /// <summary>
    /// 扫了几棵树,要跟 `sources list` 列的那几棵对得上账。
    ///
    /// 第六轮实测:`code-search` 说「across 23 source trees」,`sources list` 同一台机器上
    /// 列 33 棵 —— 两个数谁也不解释谁,而八份答卷里有四份把「23 棵里一次都没出现」
    /// 当成「全库唯一」用掉了。差额是十棵空目录(旧别名残留,从没反编译过),
    /// 但读的人无从知道它不是「十棵没扫的代码」。
    ///
    /// 闸盯两头:数要写成「N of M」而不是光秃秃一个 N,并且要说破差额是什么;
    /// 树都非空时(M == N)不许多话,否则这句话退化成每次都挂的免责声明。
    /// </summary>
    [Fact]
    public void 扫过的树数要跟磁盘上的树数对得上账()
    {
        // fixture 三棵树,zz.emptytree 一个文件都没有 —— 差额恒为 1。
        var (miss, _, mcode) = Fixture.Run("code-search", "--", "zzzznothing");
        Assert.Equal(1, mcode);
        Assert.Contains("2 of 3 source trees on disk", miss, StringComparison.Ordinal);
        Assert.Contains("the rest hold no file matching --files '*.cs'", miss, StringComparison.Ordinal);
        Assert.Contains("never been decompiled", miss, StringComparison.Ordinal);

        // 有命中的那句用的是同一个取景,不许只修零结果那一支。
        var (hit, _, hcode) = Fixture.Run("code-search", "props");
        Assert.Equal(0, hcode);
        Assert.Contains("2 of 3 source trees on disk", hit, StringComparison.Ordinal);

        // glob 一收窄只剩一棵树扫得到,这句话更不能消失(消失了就与「全库就这么多」同形),
        // 而且报的必须是**当次**的 glob,不是写死的 '*.cs'。
        var (narrow, _, _) = Fixture.Run("code-search", "ThingComp", "--files", "*Comp*.cs");
        Assert.Contains("1 of 3 source trees on disk", narrow, StringComparison.Ordinal);
        Assert.Contains("--files '*Comp*.cs'", narrow, StringComparison.Ordinal);

        // 窄化到一棵树时走另一支(「in the 'X' tree alone」),不许两句话叠着说。
        var (one, _, _) = Fixture.Run("code-search", "ThingComp", "--source", "vanilla");
        Assert.DoesNotContain("on disk — the rest", one, StringComparison.Ordinal);
    }

    /// <summary>
    /// `code-search` 零命中时原先只给一种别的解释:「你要找的其实是 def 吧,去 search/find」。
    /// 而这棵树是反编译产物 —— 作者写的注释一条都没留下(本机 23 棵树 19467 个文件里
    /// `///` 零条,`^\s*//` 的 1369 条里 1334 条是 ILSpy 自己的备注),局部变量名也没留下
    /// (`numN = ` 有 17212 条)。**照注释或照记忆里的局部变量名去 grep,永远零命中**,
    /// 而那句话把人推去换数据源,是把已知盲区指成了别的方向。
    ///
    /// 两条触发都要有落点,而不该触发的那条也要证明它闭着嘴 —— 否则这句话退化成
    /// 每次落空都挂的免责声明。
    /// </summary>
    [Fact]
    public void 代码零命中要说破反编译抹掉了什么()
    {
        var (comment, _, _) = Fixture.Run("code-search", "--", @"//\s*TODO");
        Assert.Contains("comment", comment, StringComparison.Ordinal);
        Assert.Contains("ILSpy", comment, StringComparison.Ordinal);

        var (local, _, _) = Fixture.Run("code-search", "--", "myFuelCounter");
        Assert.Contains("Local variable names", local, StringComparison.Ordinal);

        // 带元字符的模式不是「照名字找一个局部变量」,不许挂那句话。
        var (regex, _, _) = Fixture.Run("code-search", "--", @"zzz\w+\(");
        Assert.DoesNotContain("Local variable names", regex, StringComparison.Ordinal);
        Assert.DoesNotContain("ILSpy", regex, StringComparison.Ordinal);

        // 有命中时一个字都不说 —— 这是零结果分支的话。
        var (hit, _, hcode) = Fixture.Run("code-search", "props");
        Assert.Equal(0, hcode);
        Assert.DoesNotContain("Local variable names", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反编译产物**不重复父类的成员**,于是 `read MapPortal.cs --member Destroy` 落空,
    /// 而 Destroy 就在两跳之外的 Thing 里 —— `MapPortal : Building`,一行之内看得见。
    /// 原先那句话只给「跑 --outline 看看这文件有什么」和一句没带参数的
    /// 「code-search 搜的是文本本身」:前者确认它不在,后者指了条路却不说往哪走,
    /// 而「这文件里没有」与「这个类型没有这个成员」是两件事,读的人会读成后者。
    ///
    /// 基类型就在类声明那一行上,算得出来就算,不猜。
    /// </summary>
    [Fact]
    public void 成员落空时指向它可能继承自谁()
    {
        var (miss, _, code) = Fixture.Run("read", "CompShield.cs", "--member", "PostSpawnSetup");
        Assert.Equal(1, code);
        Assert.Contains("CompShield", miss, StringComparison.Ordinal);
        Assert.Contains("ThingComp", miss, StringComparison.Ordinal);
        // 指的那条路要走得到 —— 语料里 ThingComp.cs 上确实有这个成员。
        var (there, _, ok) = Fixture.Run("read", "ThingComp.cs", "--member", "PostSpawnSetup");
        Assert.Equal(0, ok);
        Assert.Contains("PostSpawnSetup", there, StringComparison.Ordinal);

        // 没有基类型可说时不许硬编一个。Outer 是个裸类。
        var (bare, _, _) = Fixture.Run("read", "Outline.cs", "--member", "NoSuchMember");
        Assert.DoesNotContain("inherit", bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 「Global options」这个词本身就在暗示位置自由,而解析器要求它写在命令**之后**。
    /// `rimsearcher --json types` 于是 exit 2 —— 而人是照着 --help 那个小标题写的。
    /// 错误消息上一轮已经说清了(Runner 那段注释就是为它写的),抵触的那一头没动。
    /// </summary>
    [Fact]
    public void 全局参数的位置约束要写在它自己的标题上()
    {
        var overview = new StringWriter { NewLine = "\n" };
        RimSearcher.Cli.Runner.Run(["--help"], overview, new StringWriter());

        foreach (var help in new[] { overview.ToString(), Fixture.Run("types", "--help").Stdout })
        {
            var at = help.IndexOf("Global options", StringComparison.Ordinal);
            Assert.True(at >= 0, "帮助里应当有 Global options 这一段");
            // 位置约束要与标题同行 —— 隔一段再说等于没说,读的人照标题写命令。
            var line = help[at..].Split('\n')[0];
            Assert.Contains("after the command", line, StringComparison.Ordinal);
        }

        // 而写在前面时那条纠正话里不许留多余空格 —— --json 没有占位符。
        var (_, err, code) = Fixture.Run("--json", "types");
        Assert.Equal(2, code);
        Assert.Contains("... --json'.", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第六轮 C31:「这个值是哪一层写的」完全无解 —— <c>get</c> 给合并后的值,<c>inherit</c>
    /// 明说抽象节点在快照里没有自己的字段表,两条命令各自诚实、拼起来正面答不了。绕法
    /// (证人兄弟法)可靠但要自己发明。<c>inherit --path</c> 把那个绕法收进命令,而它的
    /// 全部价值就在**分母**上:分母算错,每一层都看着「后代全都带」,于是每一层都像声明者。
    ///
    /// 两个方向各有各的错法,红法也不同:
    ///   ① 被问的那个 def 算进自己的分母 —— 它当然带着这条路径,于是最近那一层
    ///      恒为「1 of 1」,而那正是读的人最想拿来定罪的一行。
    ///   ② 异构桶的后代掉出分母 —— <c>xml_nodes.def_type</c> 是 XML 根元素名,
    ///      <c>defs.def_type</c> 是桶名,硬要求相等会把整批异构桶的后代丢掉(R2 已经
    ///      为 inherits_from 踩过同一脚)。分母小了,结论就往「是这一层」偏。
    /// </summary>
    [Fact]
    public void 证人的分母既不算上自己也不许漏掉异构桶的后代()
    {
        var (json, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass", "--json");
        var rows = System.Text.Json.JsonDocument.Parse(json).RootElement
                      .GetProperty("nodes")[0].GetProperty("witnesses");

        static (int Others, int WithPath) Row(System.Text.Json.JsonElement rows, string layer)
        {
            foreach (var r in rows.EnumerateArray())
                if (r.GetProperty("layer").GetString() == layer)
                    return (r.GetProperty("other_defs").GetInt32(), r.GetProperty("with_path").GetInt32());
            throw new Xunit.Sdk.XunitException($"witnesses 表里没有 '{layer}' 这一层");
        }

        // BaseBullet 名下只有 Bullet_Revolver 一个后代,而它就是被问的那个 —— 分母是 0,
        // 不是 1。是 1 的话这一层看着「1 of 1 全都带」,读的人当场把它当成声明者。
        Assert.Equal((0, 0), Row(rows, "BaseBullet"));

        // BaseProjectile 名下除 Bullet_Revolver 外还有两个:ThingDef 的 Firefoam,以及
        // VariantOne —— 后者的 XML 根元素是 TestVariantDef 而 def 落在 TestBaseDef 桶。
        // 少了它就是 (1, 1),一个「全都带」的假证词。
        Assert.Equal((2, 1), Row(rows, "BaseProjectile"));
    }

    /// <summary>
    /// 参照值不在场时,<c>same_value</c> 这一列整个不出。
    ///
    /// 抽象节点自己没有字段表,于是没有「这个 def 上的那个值」可比。照印一列恒为 0 的数,
    /// 读起来是「一个兄弟都不同意」—— 一个比不印强烈得多的结论,而它是凭空的。
    /// 这是「错的输出与对的输出同形」的又一例:0 既可以是量过为零,也可以是没量。
    /// </summary>
    [Fact]
    public void 没有参照值时证人表不许印一列恒零的同值数()
    {
        static System.Text.Json.JsonElement Witnesses(string json)
            => System.Text.Json.JsonDocument.Parse(json).RootElement
                  .GetProperty("nodes")[0].GetProperty("witnesses");

        var (abstractNode, _, _) = Fixture.Run("inherit", "BaseBullet", "--path", "thingClass", "--json");
        foreach (var r in Witnesses(abstractNode).EnumerateArray())
            Assert.False(r.TryGetProperty("same_value", out _),
                "抽象节点没有自己的值可比,same_value 不该在场");

        // 反向:有参照值的那一侧必须有这一列,否则上面那条断言换成「永远不印」也照样绿。
        var (concrete, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass", "--json");
        foreach (var r in Witnesses(concrete).EnumerateArray())
            Assert.True(r.TryGetProperty("same_value", out _), "有参照值时 same_value 必须在场");
    }

    /// <summary>
    /// 截断那条免责说的是**这个分母里**有几条被截,不是整库有几条。
    ///
    /// 字段表在导出时被截过的 def 会「没有这条路径」而其实有 —— 正好是让一层被误判成
    /// 「没声明」的那个方向,所以这句话非说不可。但拿整库的数来说就恒为非零,而恒真的
    /// 免责声明会被学着跳过(00 论据 3),那句话也就等于没说。
    /// </summary>
    [Fact]
    public void 截断免责只在这次的分母真被截时才说()
    {
        const string Caveat = "counted in other_defs had the field list cut short";

        // Bullet_Revolver 的字段表被截过(fields_truncated = 3),而 BaseBullet 名下的
        // 分母里正好有它 —— 这一句要出。
        var (withTruncated, _, _) = Fixture.Run("inherit", "BaseBullet", "--path", "thingClass");
        Assert.Contains(Caveat, withTruncated, StringComparison.Ordinal);

        // 换成问 Bullet_Revolver 自己,它被排除在分母外,剩下的两条都没被截 —— 不许出。
        // 整库照旧有被截的 def,所以拿整库计数的实现在这一格就是红的。
        var (clean, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass");
        Assert.DoesNotContain(Caveat, clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// 证人表必须自己说破逆命题不成立。
    ///
    /// 「with_path 追平 other_defs」只与「这一层声明了它」相容,并不蕴含它 —— 每个后代
    /// 各写各的一份,印出来逐字相同。实测 vanilla 的 <c>BaseBullet --path damageAmountBase</c>
    /// 就是 61 of 61,而 61 个子弹各写各的伤害;same_value 只有 9 才是那句话的证据。
    /// 一张只出数不说读法的表,读的人默认会往强的那边读。
    /// </summary>
    [Fact]
    public void 证人表要说破全都带着并不等于这一层写的()
    {
        var (text, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass");
        Assert.Contains("The converse does not hold", text, StringComparison.Ordinal);
        Assert.Contains("every descendant writing the field separately", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T7:「快照与当前安装的游戏一致」这句话要自己说清**没比的是什么**。
    ///
    /// 原句到「same mods, same order, same version」为止 —— 它把范围列全了,而
    /// **八份盲测轨迹一份都没问它比的是什么**,一致地读成了「快照 = 现在的游戏数据」的背书。
    /// 其中一份据此对「我刚改了自己 mod 里的音效文件,生效没有」下了否定判决,而那恰恰是
    /// 这个比较结构上不可能察觉的那一类改动:mod 列表、顺序、版本号三样全都不变。
    ///
    /// 与上一轮「33 棵树 vs 23 棵树」同形:工具把自己的口径老老实实印出来,读的人一致
    /// 把那句自我限定读成了背书。所以正面那半在场不算数,**没比的那半必须同时在场**。
    ///
    /// 这道闸能存在,靠的是 <c>mods_config</c> 进了配置层 —— 那条路径原先写死在代码里,
    /// 于是这条分支在测试里根本到不了,而到不了的分支上挂的话红不了。
    /// </summary>
    [Fact]
    public void 一致这句话要同时说清没比的是什么()
    {
        var (stdout, _, _) = Fixture.Run("snapshot", "status");

        // 正面那半:三样东西点名,不能只说「一致」。
        Assert.Contains("same mods, same order, same version", stdout, StringComparison.Ordinal);

        // 反面那半 —— 承重的是这一半。
        Assert.Contains("Nothing inside those mods is compared", stdout, StringComparison.Ordinal);
        Assert.Contains("leaves this line reading 'matches' all the same", stdout, StringComparison.Ordinal);

        // 导出那一刻要报出来:「自那以后改过的文件看不见」这句话没有时刻就落不了地。
        Assert.Contains("2026-01-01T00:00:00.0000000Z UTC", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T6:披露句要跟着调用方**自己已经划掉**的那一维一起收。
    ///
    /// 实测 `values maxSimultaneous --type SoundDef` 的表头已声明「SoundDef (1231 of 1231)」——
    /// 已经滤干净了 —— 而脚注仍在说「the def types that carry this path (**ThingDef**) also
    /// hold 2 defs …」。那不是噪音,是在一张与它无关的表下面挂了一个完整性告警。
    ///
    /// 代价看起来是 0 次调用,列进靶子是因为它侵蚀的是披露机制本身:**一旦有一句被发现
    /// 是过期的,其余每一句都要被重新审视**,而这套输出的全部价值就在那些句子上。
    /// </summary>
    [Fact]
    public void 完整性脚注要跟着自己划的类型一起收()
    {
        const string Note = "Counted over indexed field paths only";

        // 语料:comps[0].compClass 同时落在 ThingDef 与 HediffDef 上,而只有 ThingDef
        // 那边有被截过的 def(Bullet_Revolver)。不划类型时这句话成立,要出。
        var (wide, _, _) = Fixture.Run("values", "compClass");
        Assert.Contains(Note, wide, StringComparison.Ordinal);
        Assert.Contains("ThingDef", wide, StringComparison.Ordinal);

        // 划到 HediffDef 之后,表里一条 ThingDef 都没有了 —— 这句话跟着一起没。
        var (narrow, _, _) = Fixture.Run("values", "compClass", "--type", "HediffDef");
        Assert.Contains("HediffDef (1 of 1)", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain(Note, narrow, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T5:目录在而里面一个 .cs 都没有,是关于**磁盘**的事实,与「这棵树在不在这次
    /// 的计划里」正交 —— 而实现原先把计划外的一律短路成「not in the snapshot」。
    ///
    /// 后果有两层。表面一层:汇总行照旧报「0 never built」,而磁盘上十棵是空的,字面为假。
    /// 更贵的一层:<c>code-search</c> 的页脚正指着这一列(「'sources list' says which of
    /// those have never been decompiled」),指到的是一个对这十棵永远不发声的字段 ——
    /// **一条指路指了个空**,而 33 与 23 的差额恰好就是它们。
    ///
    /// 三个断言各守一段:空这件事要单成一档、files 列要把 0 与「没有这个目录」分开、
    /// 而 `sources sync` 填不了的那些要说破(不说破就是换了一条走不通的指路)。
    /// </summary>
    [Fact]
    public void 空的源码树要自成一档而不是被计划外那句话吸收掉()
    {
        var (stdout, _, _) = Fixture.Run("sources", "list");

        // zz.emptytree 在计划外,而它是空的 —— 空压得住计划内外。
        Assert.Matches(new Regex(@"zz\.emptytree\s+0\s+empty", RegexOptions.Multiline), stdout);

        // 汇总行单列一档。少了它,「0 never built」就是这份磁盘状态的假陈述。
        Assert.Contains("holding no .cs file", stdout, StringComparison.Ordinal);

        // 指路要走得通:sync 计划里没有它们,那句「sync rebuilds them」对它们不成立。
        Assert.Contains("will never fill them", stdout, StringComparison.Ordinal);
        Assert.Contains("code-search' reports reading no file from", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T3:反查落空时要说破**这个索引里装的只是值**。
    ///
    /// 导出器见 null 直接 return(DefExporter:284),那条路径从来没进过索引。于是
    /// 「这个字段不存在」与「它在,只是每个 def 上都是 null」在输出上完全同形 ——
    /// 六份轨迹独立踩,其中三次差点交出反向结论(「本体标了但没人用」「没被挡,是运气」)。
    ///
    /// <c>get</c> 早就为**单个 def** 说过这句话(五轮 F4),而 find / values / fields
    /// 三条反查路一直没说。三处都判,因为补一处剩两处的输出一字不变。
    /// </summary>
    [Fact]
    public void 反查落空要说破索引里装的只是值()
    {
        const string Line = "never entered this index";

        var (find, _, _) = Fixture.Run("find", "noSuchField", "x");
        Assert.Contains(Line, find, StringComparison.Ordinal);

        var (values, _, _) = Fixture.Run("values", "noSuchField");
        Assert.Contains(Line, values, StringComparison.Ordinal);

        var (fields, _, _) = Fixture.Run("fields", "ThingDef", "--path", "zzznosuchtext");
        Assert.Contains(Line, fields, StringComparison.Ordinal);

        // identity 那一档不说 —— 答案已经给全了,再挂一句索引边界是纯噪音。
        // 这一条反着守:少了它,「到处都说一遍」也能让上面三条全绿。
        var (identity, _, _) = Fixture.Run("find", "mod");
        Assert.DoesNotContain(Line, identity, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T4:默认值折叠按「谁设的值」筛,而提问常常是「这个列表有几项」——
    /// 两个维度正交,却归同一个开关管。
    ///
    /// 一整个列表项被折光时,「这个列表只有一项」就成了看得见的形状,而真值是它更长。
    /// 实测一份轨迹正是这么判的 subSound 数量。下标前缀不受折叠影响(matchedPaths 是
    /// 折叠前的),所以这件事算得出来 —— **两边都要说**:藏了就点名,没藏就把那句
    /// 正面的话给出来。靠沉默承载「没藏」正是这套输出一直在清的东西。
    /// </summary>
    [Fact]
    public void 折叠藏掉整个列表项时要点名没藏时要说没藏()
    {
        // --limit 2 把 statBases[0] 那一族整个挤出视野 —— 藏了就点名。
        var (hidden, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--limit", "2");
        Assert.Contains("Nothing above shows any field of these list entries", hidden, StringComparison.Ordinal);
        Assert.Contains("statBases[0]", hidden, StringComparison.Ordinal);

        // 不截时每个下标都露过面 —— 这时候要给正面那句话,不能沉默。
        var (whole, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("Every list index this def has does appear above", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing above shows any field", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第七轮 T1:嵌套 <c>&lt;li Class="…"&gt;</c> 的运行时类型这一维,要按导出器版本分说。
    ///
    /// 0.2.0 起导出器给列表元素发一条 <c>&lt;path&gt;.Class</c>,于是「哪些 ThingDef 挂了
    /// 这个节点」这类 def 侧最常见的反查第一次有了查法。而**老快照对 `find Class X` 回的
    /// 那个零,与「量过了、确实没人用它」逐字同形** —— 给索引加一维恰恰是最容易造出
    /// 这个形状的改动,所以两个世界各要一个落点:主快照标 0.2.0,other 那份标 0.1.0。
    /// </summary>
    [Fact]
    public void 嵌套类型这一维量没量过要按导出器版本分说()
    {
        // 量过的那份:这一维真的能查到东西。
        var (hit, _, code) = Fixture.Run("find", "Class", "RimWorld.CompProperties_Shield");
        Assert.Equal(0, code);
        Assert.Contains("TestModGun", hit, StringComparison.Ordinal);

        // 量过的那份落空时:指的路是这一维本身。
        var (miss, _, _) = Fixture.Run("find", "noSuchField", "x");
        Assert.Contains("indexed as '<path>.Class'", miss, StringComparison.Ordinal);

        // 没量过的那份:不许长成一样。说破是这份快照没量,而不是没人用。
        var other = Path.Combine(Fixture.SnapshotDir, "other.db");
        _ = Fixture.Db;
        var (old, _, _) = Fixture.Run("find", "noSuchField", "x", "--db", other);
        Assert.Contains("before that type entered the index", old, StringComparison.Ordinal);
        Assert.DoesNotContain("indexed as '<path>.Class'", old, StringComparison.Ordinal);
    }

}
