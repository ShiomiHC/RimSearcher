using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 文法闸:判产地渲染出来的形态与语义类别,不逐字比对渲染完的句子。
/// </summary>
public class GrammarTests
{
    // ---- 举例子的名单(NameList)----

    /// <summary>
    /// 被截掉的部分**必须有数**。
    /// </summary>
    [Fact]
    public void 举例子的名单说清没举出来的有几条()
    {
        string[] five = ["a", "b", "c", "d", "e"];
        Assert.Equal("a, b, c, and 2 more", NameList.Render(five, 3));

        // 装得下就一个字都不多说 ——「and 0 more」让人以为有下文。
        Assert.Equal("a, b, c, d, e", NameList.Render(five, 5));
        Assert.Equal("a, b, c, d, e", NameList.Render(five, 99));
        Assert.Equal("", NameList.Render([], 3));

        // 差一条就截:边界上不许把「刚好装下」算成「截了」。
        Assert.Equal("a, b, c, d, and 1 more", NameList.Render(five, 4));
    }

    /// <summary>
    /// 近似候选**不报**被截掉的数量:「Closest」本身声明了它是个 top-N,
    /// 补一句「还有 37 个」会让人以为答案可能在那 37 个里。
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
    /// 完整集合不触发任何截断声明 —— 上下文预算的硬约束。
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
    /// 「N of M」里的 M 是**总数**,不许随 `--limit` 变 —— 三态文法的全部价值就在那个 M 上。
    /// 两个易错点:子串补扫按已显示的行去重,以及先 Take 再累加。
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
    /// 被导出期截断的 def 自报字段下界,不把加法留给读者。
    ///
    /// 两个加数此前分在两句里(索引数在上一段、丢掉数在这一段,中间还常隔着一大截),
    /// 而「这个 def 一共有多少字段」的正解只有那个和。实测六个闭卷样本全都认出「索引数
    /// 是下界」,只有两个把 <c>+N</c> 做完 —— 而两个加数从头到尾都在 CLI 手上。
    ///
    /// 和必须是 <c>at least</c> 口径:两个加数里有一个本身就是下界(导出器是停了,
    /// 不是数完了),而 null 字段从来没进过任何一个加数。
    /// </summary>
    [Fact]
    public void 被截断的def自报字段下界而不是让读者相加()
    {
        var (text, _, _) = Fixture.Run("get", "Bullet_Revolver");
        Assert.Contains("at least 3 fields were dropped", text, StringComparison.Ordinal);
        Assert.Contains("Added to the 8 paths that did get indexed", text, StringComparison.Ordinal);
        Assert.Contains("at least 11 field paths", text, StringComparison.Ordinal);

        // --path-contains 那一支逐字相同 —— 那里的上文给的是「out of 8 fields on the def」,
        // 句子不许改成依赖上文那个数,否则换一支就指空。
        var (narrowed, _, _) = Fixture.Run("get", "Bullet_Revolver", "--path-contains", "burstCount");
        Assert.Contains("at least 11 field paths", narrowed, StringComparison.Ordinal);
    }

    /// <summary>
    /// scope 筛空了不许说成「快照里没有」:零结果分流的判据都是 scope 过滤过的,
    /// 分不清成因就会报最强的那一种。
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

            // 只在**辅音 + y**(city → cities)与 -ch/-sh(match → matches)上要求登记形态
            // 不同于朴素加 s —— 元音 + y 只加 s(key → keys)本就正确。
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
    /// 声明区行数上限。超了就该聚合成尾注,而不是逐条铺开。
    /// </summary>
    [Fact]
    public void 声明区不超过行数上限()
    {
        string[][] invocations =
        [
            ["search", "shield"],
            ["get", "Apparel_ShieldBelt"],
            ["get", "Bullet_Revolver"],
            ["where", "compClass", "RimWorld.CompShield"],
            ["types"],
            ["list", "ThingDef", "--limit", "2"],
            // code-search 的最坏情形:三道闸同时咬,四句申报加一句计数。
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
    /// 「每次返回挂一段免责声明」不得以任何形式重生:完整态只准出现计数那一句
    /// (kind=count),不准有边界/建议类散文。禁的是免责声明,不是数字 ——
    /// 靠沉默传达「这就是全部」同样会被读错。
    /// </summary>
    [Fact]
    public void 结果完整时只有计数一句而没有免责声明()
    {
        var (json, _, code) = Fixture.Run("where", "compClass", "RimWorld.CompShield", "--json");
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

        var (stdout, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 两个数在这条查询上相等,于是名词是 def(那时它同时数着两样),
        // 而且不许多印一句 —— 下面 kinds 只有 count 与 boundary 两条把着这件事。
        Assert.Equal("2 defs.", lines[0]);
        // 那条 boundary 摆在表**上面**,这是它的位置而不只是它的存在:沉到表下时,
        // `| head` 砍掉尾巴之后剩下的输出与完整输出逐字相同 —— line 1 的计数只担保表,
        // 对表下的东西一个字都没说,于是那一刀不留任何痕迹。
        Assert.Contains("snapshot truncated", lines[1]);
        // 折叠行是**表的一部分** —— 整列同值的列提到表上方说一次,搬的是数据不是散文。
        // 「不许有免责声明」那一侧由上面的 kinds 断言把着:notices 里仍然只有 count 与
        // boundary 两条,渲染器折出来的这行根本不进 notes。
        var header = lines[2].StartsWith("Same in every row", StringComparison.Ordinal) ? lines[3] : lines[2];
        Assert.StartsWith("def_name", header);
    }

    /// <summary>
    /// 上一条的另一半:没有可申报的边界时,完整结果集只有计数一句,一个字的散文都没有 ——
    /// 少了这一条,那条 boundary 就可能悄悄变成每次都挂的常驻声明。
    /// </summary>
    [Fact]
    public void 没有边界可申报时完整结果集只有计数()
    {
        var (json, _, code) = Fixture.Run("where", "hediffClass", "Verse.HediffWithComps", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var kinds = doc.RootElement.GetProperty("notes")
                       .EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["count"], kinds);
    }

    /// <summary>
    /// 被接受的开关不许被吞。SKILL 承诺的「Unknown options are rejected rather than ignored」
    /// 只覆盖选项**名**,不覆盖收下之后不读。
    ///
    /// 断言的形状是「加了它输出必须变」而不是「输出等于某个具体值」—— 被忽略时输出也是
    /// 一个合法的值。
    /// </summary>
    [Fact]
    public void exact在按值反查时不被吞掉()
    {
        // 语料里 comps[0].compClass = RimWorld.CompShield。子串能命中,整值相等不能。
        var (loose, _, looseCode) = Fixture.Run("where", "--value", "CompShield");
        var (strict, _, strictCode) = Fixture.Run("where", "--value", "CompShield", "--exact");

        Assert.Equal(0, looseCode);
        Assert.NotEqual(loose, strict);
        Assert.Equal(1, strictCode);
        // 落空时要指出 --exact 是这次落空的成因之一,否则「没有」被读成绝对的没有。
        Assert.Contains("--exact", strict);

        // 反过来:整值给对时 --exact 必须还能命中,否则这个开关就是把路堵死而不是收窄。
        var (hit, _, hitCode) = Fixture.Run("where", "--value", "RimWorld.CompShield", "--exact");
        Assert.Equal(0, hitCode);
        Assert.Contains("comps[0].compClass", hit);
    }

    /// <summary>
    /// <c>inherit</c> 的 patch 计数每个节点都要在场,不能只在非零时出现 —— 只在非零时说话,
    /// 「零」与「这件事没做」就分不开,而文档承诺「a node with 0 of them is exactly what the
    /// game read」。
    ///
    /// 计数正则只认 <c>@Name=</c>(XmlNodeExporter),没有 Name= 的节点导出器硬写 0 ——
    /// 那一侧必须印一个**非数字**并说破计数口径,真正的测得零在 <c>BaseProjectile</c>。
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

        // 后果那句散文只在非零时说 —— 真的 0 不需要解释。
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
    /// 默认值那句声明不许承诺它做不到的事:承诺范围要限定在**索引到的路径**上,并说破
    /// 第四态 —— 值为 null 的字段导出器见了直接 return(DefExporter),那条路径从来没进过
    /// 索引,<c>--defaults</c> 也召不回来,于是「字段不存在」与「值是 null」在输出上同形。
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
    /// 一个词同时是快照名和 scope 组名时,要说破两者不是一回事:一份**叫** vanilla 的快照
    /// 可能只有 Core 加导出器,而 <c>--scope vanilla</c> 是六个 Ludeon 模块(含 DLC)。
    ///
    /// 「显式指定就闭嘴」在这里不成立 —— 它的前提是调用方知道自己选的环境是什么。
    /// 只在撞名时说,平常一次都不出现。
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
    /// scope 展开成了什么,在**有结果时**也要说 —— 口径直接决定答案怎么写。
    ///
    /// 判据是「展开与字面不同」,不是「多于一个 mod」:写死 packageId 的调用一个字都不该
    /// 多收。同一次输出里也只说一遍 —— 散文里一律写调用方输入的字面。
    /// </summary>
    [Fact]
    public void scope展开在有结果时也播报()
    {
        // 组名:夹具里 vanilla 展开成 ludeon.rimworld,与字面不同 —— 必须说。
        var (group, _, code) = Fixture.Run("where", "thingClass", "RimWorld.Bullet", "--scope", "vanilla");
        Assert.Equal(0, code);
        Assert.Contains("--scope vanilla (= ludeon.rimworld)", group);

        // 写死 packageId:你写的就是你得到的,**播报那一行不多印**。钉的是「有没有一条
        // 以它开头的声明行」—— 计数句里那句 `1 def within --scope ludeon.rimworld.`
        // 是另一件事(用户侧收窄要念回去),不在这条闸的射程内。
        var (literal, _, _) = Fixture.Run("where", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld");
        Assert.DoesNotMatch(new Regex(@"^--scope ludeon\.rimworld", RegexOptions.Multiline), literal);

        // 零结果那一侧也要说,但**只说一遍** —— 两遍会被读成两条独立证据。
        var (miss, _, _) = Fixture.Run("where", "--value", "NoSuchValueXyz", "--scope", "vanilla");
        Assert.Single(Regex.Matches(miss, @"= ludeon\.rimworld\)"));
    }

    /// <summary>
    /// 截断尾注是给「这就是全部」背书的那句话,两处易错:按结果里的每条路径各查一次再
    /// 求和(同一个被砍的 def 被数几次),以及子查询问成「用过这条路径的 def 类型」——
    /// 结果里只要有一条路径叫 <c>defName</c> 它就退化成全体类型。
    ///
    /// 闸按**数学上不可能**立,不按具体数字立:尾注说的是全库被砍总数的一个子集,
    /// 任何时候都不许超过它,而超了印出来与一个正常计数逐字同形。
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
            ["where", "--value", "RimWorld"],
            ["where", "--value", "CompShield"],
            ["where", "thingClass", "RimWorld.Apparel"],
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
    /// 同一句尾注的第二处:外层 COUNT 也要带 scope 谓词,否则「可能属于这里而没露面」
    /// 说的是一批 scope 明明排除掉的 def。
    /// </summary>
    [Fact]
    public void 截断尾注跟随scope收窄()
    {
        // 夹具里唯一被砍的 def(Bullet_Revolver)属于 ludeon.rimworld,而同一个值
        // RimWorld.CompShield 在 test.mod 的 TestModGun 上也有 —— 收窄之后仍有结果,
        // 变的只是这句背书该不该说话。
        var (wide, _, _) = Fixture.Run("where", "--value", "CompShield");
        Assert.Contains("Defs whose export was cut short", wide);

        // 收到 test.mod 之后被砍的那个不在 scope 里,这句背书就不该再提它。
        var (narrow, _, _) = Fixture.Run("where", "--value", "CompShield", "--scope", "test.mod");
        Assert.DoesNotContain("Defs whose export was cut short", narrow);
    }

    /// <summary>
    /// 同名提示不随 <c>--type</c> 消失:提示要挂在**过滤前**的集合上。调用方主动收窄恰恰
    /// 说明它知道有歧义,这是最需要那句提示的时刻。
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
    /// 上一条的另一半:收窄之后**不许**把该显示的弄丢。
    ///
    /// 继承层的 def_type 是 XML 根元素名,defs 表的是 AllDefTypesWithDatabases 的桶名,
    /// 两者会不一致(如 CreepJoinerFormKindDef → PawnKindDef)。硬要求 def_type 相等,
    /// 异构桶的 inherits_from 会整批消失。
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
    /// **印几行**的两道闸都不许缩短扫描 —— SKILL 承诺「--limit 只管行,code-search 另有
    /// 一道管文件数的闸」。判的是三种调法必须给出同一个总数,不判措辞。
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
    /// 点开头的目录不是源码树。判据在 SourcesShared.TreeNames,这里守的是
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
    /// 上下文窗口重叠时合并,同一行不许印两遍。判的是行号不重复,不是措辞 —— 所以问
    /// 结构化那一侧:<c>file</c> + <c>line</c> 是这条意图的真契约,而文本形态(路径逐行
    /// 重复,还是每文件一条标题)改起来不该惊动它。
    /// </summary>
    [Fact]
    public void 上下文窗口重叠时合并()
    {
        var (json, _, _) = Fixture.Run("code-search", "public", "--file-glob", "ThingComp.cs", "-C", "2", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var located = doc.RootElement.GetProperty("matches").EnumerateArray()
                         .Select(m => $"{m.GetProperty("file").GetString()}:{m.GetProperty("line").GetInt32()}")
                         .ToList();
        Assert.NotEmpty(located);
        Assert.Equal(located.Count, located.Distinct().Count());
    }

    /// <summary>
    /// 没读完的零结果不是零结果。「扫完了,代码里没有」该指路去 search / find(问错了
    /// 数据源),「没扫完」则一个字都不许提别的数据源,该做的是把闸抬开。
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
        var (empty, _, emptyCode) = Fixture.Run("code-search", "public", "--file-glob", "Verse/ThingComp.cs");
        Assert.Equal(1, emptyCode);
        Assert.Contains("No file matched", empty);
        Assert.DoesNotContain("rimsearcher search", empty);

        // 第四种成因:树在名单里、目录也在磁盘上,里面一个文件都没有(从没反编译过)——
        // 真因是这棵树该 sync 一遍,不是 glob 写错。
        var (bare, _, bareCode) = Fixture.Run("code-search", "public", "--source", "zz.emptytree");
        Assert.Equal(1, bareCode);
        Assert.Contains("holds no decompiled files", bare);
        Assert.Contains("sources sync", bare);
        Assert.DoesNotContain("No file matched", bare);   // 不许再赖到 glob 头上
        Assert.DoesNotContain("rimsearcher search", bare);

        // glob 那条的子形状:别名 --file-extension 收下裸扩展名,值却按 glob 解 ——
        // 「这里没有 .cs 文件」与「你的值不是 glob」不许同形。
        var (ext, _, extCode) = Fixture.Run("code-search", "public", "--file-extension", "cs");
        Assert.Equal(1, extCode);
        Assert.Contains("no wildcard", ext);
        Assert.Contains("'*.cs'", ext);

        // 反向:带通配符的 glob 打不中时,不许再教人加通配符。
        var (starred, _, _) = Fixture.Run("code-search", "public", "--file-glob", "*.zzz");
        Assert.Contains("No file matched", starred);
        Assert.DoesNotContain("no wildcard", starred);
    }

    // ---- 零结果的成因分流(三轮 R8 四种误诊 + R10 fatal)----

    /// <summary>
    /// 六种落点各说各的,而且**说的是算出来的那一条**,不是猜的那一条 —— 靠「像类名」
    /// 之类的猜测分流,抽象父节点、def 类型、mod 名会被推去必然空手的地方,而答案就在
    /// 同一个库的另一张表里。这一条钉的是分流本身,不是措辞。
    /// </summary>
    [Fact]
    public void 零结果按算得出来的落点分流()
    {
        (string Argv, string Must, string MustNot)[] cases =
        [
            // 继承层里的抽象 Name= —— 不许再说「像个类名」
            ("BaseBullet",        "rimsearcher inherit BaseBullet", "where compClass"),
            // 存储桶的名字,不是一个 def
            ("ThingDef",          "is a def type in this snapshot", "where compClass"),
            // def 自己的运行时 class。MustNot 锚在那句**兜底话自己**的措辞上 ——
            // 算得出落点就不许退回猜。
            ("TestVariantDef",    "--own-class TestVariantDef",         "lists what kinds of def this snapshot holds"),
            // 字段取值(comps[N].compClass 那一类)
            ("CompShield",        "rimsearcher where compClass CompShield", "no class"),
            // 快照覆盖的 mod
            ("ludeon.rimworld",   "is a mod this snapshot covers",  "lists what kinds of def this snapshot holds"),
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
    /// 算不出落点、只剩「像个类名」那一档的兜底话,不许声称快照不索引嵌套
    /// <c>&lt;li Class="..."&gt;</c> —— 那是**确定的假话**(`where Class` 整条路建立在这条索引上,
    /// 而覆盖到哪一层随导出器版本变),它的后果精确地是最贵的那种:把 `where Class` 的零
    /// 读成「工具看不见」而不是「确实没有」。
    ///
    /// 反方向那半同时钉:`where Class` 的零也可能是「没有 def 驱动这个类」,而类照样存在 ——
    /// 所以这句话必须把 code-search 指出来,否则读的人会在 def 那一侧原地打转。
    /// </summary>
    [Fact]
    public void 类名形状的兜底话指向findClass而不是声称索引不到()
    {
        var (stdout, _, code) = Fixture.Run("search", "CompProperties_NoSuchThing");
        Assert.Equal(1, code);

        // 措辞可以改,这个断言钉的是**不许说的那件事**。
        Assert.DoesNotContain("does not index those", stdout, StringComparison.Ordinal);
        Assert.Contains("rimsearcher where Class", stdout, StringComparison.Ordinal);
        Assert.Contains("code-search", stdout, StringComparison.Ordinal);

        // 那句索引边界必须来自唯一产地(措辞随快照的导出器版本分三支),不是在这条路上另写
        // 一份会过时的。主语料是 0.2.0,于是取的必须是「只量了列表元素」那一支。
        Assert.Contains("for list elements only", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>where</c> 落空时,**本次查询自己施加的过滤**是算得出来的成因,而「像个抽象基类」
    /// 只是个猜测 —— 算得出来的排在前面,猜测退场。
    ///
    /// 盲测 S7 走过的路:`--scope all,-vanilla` 把唯一那一行剔掉,输出却只回显了 scope、
    /// 再给一句抽象基类,于是「被我自己滤掉了」被读成「这个类没人用」。scope 的回显
    /// 不是成因 —— 两者差着一次重查。
    /// </summary>
    [Fact]
    public void find被scope滤空时说破并让猜测退场()
    {
        // 语料要的是**路径两边都在、值只在一边**:thingClass 在两个 mod 里都有,
        // 而 RimWorld.Bullet 只是 ludeon 的 Bullet_Revolver 的值。路径也落空的那种走的是
        // 另一条分支(它自己的句子里已经带着 --scope),这里钉的是值这一层。
        var (all, _, allCode) = Fixture.Run("where", "thingClass", "RimWorld.Bullet");
        Assert.Equal(0, allCode);

        var (scoped, _, code) = Fixture.Run(
            "where", "thingClass", "RimWorld.Bullet", "--scope", "test.mod");
        Assert.Equal(1, code);
        Assert.Contains("--scope test.mod is what emptied this", scoped, StringComparison.Ordinal);
        // 算出来的成因在场时,那句未经验证的猜测不许并排摆着。
        Assert.DoesNotContain("abstract base", scoped, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--value ""</c> 是用法错误,不是「没给 --value」。此前空串被读成后者,于是
    /// <c>where &lt;path&gt; --value ""</c> 静默退化成「列出所有带这个字段的 def」——
    /// 一张长得和合法答案一模一样的表,而问的人想要的是「哪些 def 把它设成了空」。
    /// usage-notes 那句「猜错一个开关只值一行,不值一个错答案」在这条上此前是不成立的。
    /// </summary>
    [Fact]
    public void find的空value是用法错误而不是静默退化()
    {
        var (stdout, stderr, code) = Fixture.Run("where", "thingClass", "--value", "");
        Assert.Equal(2, code);
        Assert.Equal("", stdout);
        // 两种写法各自的含义都要说,只说「不接受」会让人再猜一次。
        Assert.Contains("rimsearcher where thingClass", stderr, StringComparison.Ordinal);
        Assert.Contains("not in the index at all", stderr, StringComparison.Ordinal);

        // 没有字段路径那一支同样拒,而给的是那一支该给的写法。
        var (_, bare, bareCode) = Fixture.Run("where", "--value", "");
        Assert.Equal(2, bareCode);
        Assert.Contains("rimsearcher where --value", bare, StringComparison.Ordinal);
    }

    /// <summary>
    /// 类名形状的落空句不许把「抽象基类」当唯一解释:一个类可以完全不经过 def 被使用
    /// (C# 里直接 new),那时候两条查询都是零,而只说抽象基类会把人推去查一批不存在的子类。
    /// 盲测 S1 正是这么走完全程的。
    /// </summary>
    [Fact]
    public void find的类名落空句并列两种成因()
    {
        // 路径取 compClass 而不是 Class:主语料是 0.2.0,`Class` 那条路上索引缺口在场,
        // 于是猜测本来就该退场(那是另一条纪律,由 indexGap 守)。
        var (stdout, _, code) = Fixture.Run("where", "compClass", "RimWorld.CompNoSuchThing");
        Assert.Equal(1, code);
        Assert.Contains("abstract base", stdout, StringComparison.Ordinal);
        // 反方向那一半:没有 def 驱动它,而类照样存在。
        Assert.Contains("no def drives it at all", stdout, StringComparison.Ordinal);
        Assert.Contains("does not exist", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 被自己的 <c>--scope</c> 挡住,不是「没有」。这一条排在分流的第一位。
    /// </summary>
    [Fact]
    public void 被scope挡住时说破是过滤器干的()
    {
        var (stdout, _, _) = Fixture.Run("search", "TestModGun", "--scope", "ludeon.rimworld");
        Assert.Contains("is in this snapshot after all", stdout, StringComparison.Ordinal);
        Assert.Contains("test.mod", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「它在别的快照里」是可算出来的,get / inherit / search 三条路都要说。
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
        // 那六种落点全在快照里,而快照只装 def 侧 —— 听上去穷尽、其实没查代码树。
        // 没查的那一半必须自己说破。
        Assert.Contains("code-search \"class NoSuchDefAnywhere\"", absent, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一词两义:<c>--scope vanilla</c> 展开成每个 ludeon.rimworld* 模块,而一份
    /// **叫** vanilla 的快照可能只有 Core。展开当场算得出来,那就写进句子里。
    /// </summary>
    [Fact]
    public void scope在散文里展开成实际圈住的mod()
    {
        var (stdout, _, _) = Fixture.Run("search", "zzzznothing", "--scope", "vanilla");
        Assert.Contains("--scope vanilla (= ludeon.rimworld)", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--source</c> 已经给出时,补救措施里不许再列它。
    /// 顺带守住单树也要说破「只读了一部分」。
    /// </summary>
    [Fact]
    public void 补救措施不重复已经给出的参数()
    {
        var (stdout, _, _) = Fixture.Run("code-search", "public", "--source", "vanilla", "--max-files", "1");
        Assert.DoesNotContain("--source <tree>", stdout);
        Assert.Contains("read only in part", stdout);

        var (both, _, _) = Fixture.Run("code-search", "public", "--source", "vanilla",
                                       "--file-glob", "*.cs", "--max-files", "1");
        Assert.DoesNotContain("narrow with", both);
    }

    // ---- read ----

    /// <summary>
    /// 配平括号得躲开四种「看起来是括号」:注释里的、字符串里的、字符字面量里的、
    /// 逐字字符串里双写引号后面的。躲不开就**认错边界**,交出去的代码从中间截断
    /// 而看上去完整。
    /// </summary>
    [Fact]
    public void 括号只在真的是括号时算数()
    {
        var (stdout, _, code) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--outline");
        Assert.Equal(0, code);

        // 类体的两端就是文件里那一对真括号:少认一个,Outer 会在 Marker 那一行提前收尾。
        Assert.Contains("5-33", stdout, StringComparison.Ordinal);
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
    /// line 1 得自己就说清「几条、全不全」。
    ///
    /// 判据是**误导**,不是**空洞**:`| head` 是预训练习惯,两轮盲测(read 与 Mood)都证明
    /// 文字禁令拦不住它,所以出路是让被砍之后剩下的那一行不至于骗人。`get` 与 `values`
    /// 此前把 identity / 产地块排在最前,砍到二十行看着就是一份完整的答案 —— 那是误导。
    /// (`inherit` 不在此列:它砍完剩下的是一叠名字,不像个继承答案,空洞不等于骗人。)
    /// </summary>
    [Fact]
    public void 计数行走在数据块前面()
    {
        // 撞名时 line 1 已经是撞名那句,identity 块照旧留在最前 —— 各段的计数一旦提到
        // 自己的 identity 块之前,就会紧贴上一段的表尾,读成上一段的数。
        var (collide, _, _) = Fixture.Run("get", "Firefoam");
        Assert.StartsWith("2 defs share the name 'Firefoam'", collide, StringComparison.Ordinal);
        var head = collide.Split('\n')[1].TrimEnd('\r');
        Assert.Contains("read the def_type line at the top of a block", head, StringComparison.Ordinal);

        foreach (var (argv, want) in new[]
                 {
                     ((string[])["get", "Apparel_ShieldBelt"], "11 fields."),
                     (["values", "compClass"], "2 values."),
                 })
        {
            var (text, _, _) = Fixture.Run(argv);
            Assert.StartsWith(want, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 分页:总行数与下一页的参数恒在 —— 走不下去的第二页会把调用方逼回自己编正则。
    /// </summary>
    [Fact]
    public void 裸行读随时说得出总数与下一页()
    {
        var (page, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "7-12");
        Assert.Contains("of 34", page, StringComparison.Ordinal);
        Assert.Contains("--lines 13", page, StringComparison.Ordinal);

        // 一次读完时不许再劝人翻页 —— 那一句会被读成「后面还有」。
        // Widgets.cs 末尾有三行 .Translate() 语料(keyed 那一层的落点),所以是 12 行。
        var (whole, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs");
        Assert.Contains("all 12 lines", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("next page", whole, StringComparison.Ordinal);

        // 印刷上限咬下去时,接着读的那一段是算得出来的,就得给出来。
        var (capped, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--type", "Outer", "--limit", "4");
        Assert.Contains("--lines 9-33", capped, StringComparison.Ordinal);
    }

    /// <summary>
    /// 还剩三页以上时要指出 `--outline` 这条路。
    ///
    /// **这句是「别拿 grep/head 砍输出」这条契约现在唯一的承重点。** SKILL.md 里那条禁令
    /// 已按盲测删掉(8 个被试 4 v 4,两臂都零管道、答案全对,而且都自发用上了
    /// `--outline`/`--member`/`--lines`)—— 删得掉的前提是工具自己在人想砍输出的那一刻
    /// 给出了出路。08 量到 87% 的那个世系里,`get`/`code-search`/`read` 恰恰都不支持
    /// `--offset`,那时的结论是「规矩对最需要它的场合没给出路,不是调用方不守规矩」。
    /// 这句一旦消失而禁令又已不在,就直接退回那个世系。
    /// </summary>
    [Fact]
    public void 页数多到该换路子时要指出outline()
    {
        var (paged, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "1+8");
        Assert.Contains("--lines 9+8", paged, StringComparison.Ordinal);
        // 逐字咬,免得别处凑巧出现 `--outline` 就算过。剩几页要算得出来,而不是含混的「很多」。
        Assert.Contains(
            "Reaching the end that way takes 4 pages at this size; --outline instead lists "
                + "the file's declarations with each one's line range, to pass back to --lines.",
            paged,
            StringComparison.Ordinal);

        // 翻一两页是正常分页,不值得换路子 —— 那时这句不出现,否则每次分页都在劝人改道。
        var (few, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "1+20");
        Assert.DoesNotContain("--outline", few, StringComparison.Ordinal);
        Assert.Contains("--lines 21+20", few, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两种读法同时传时不排优先级 —— 静默择一拿回的是**完全另一块代码**。
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
    /// `--member` 也有命令内窗口:`--limit` 封顶,续读区间**当场算出来印回去**。
    ///
    /// 这条不是锦上添花的便利,它是禁管道那条规矩在 `read` 上能不能立住的地基 ——
    /// 「这一形状没有出路」是 SKILL.md 里唯一授权去管道的理由,而管道会把上面那句
    /// 「少了 N 行」一起吃掉。第九轮盲测据一句未经实测的断言把这条路记成不存在,
    /// 文档照搬了一轮。于是这里钉的是那句话的反面:窗口在,而且续读参数是给好的。
    /// </summary>
    [Fact]
    public void 成员读的窗口是limit加印回来的续读区间()
    {
        var (stdout, _, code) = Fixture.Run(
            "read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--limit", "3");
        Assert.Equal(0, code);
        Assert.Contains("--limit stopped the printing at 3 lines", stdout, StringComparison.Ordinal);

        // 区间不是摆设:粘回去要真的读得到下一段,否则这条出路与「自己数行号」等价。
        var m = Regex.Match(stdout, @"read on with --lines (\d+)-(\d+)");
        Assert.True(m.Success, $"No resume range in '{stdout}'.");
        var range = $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        var (resumed, _, resumedCode) = Fixture.Run(
            "read", "vanilla/Verse/Outline.cs", "--lines", range);
        Assert.Equal(0, resumedCode);
        Assert.Contains($"lines {range} of", resumed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 配平括号不是解析,这句边界只跟着**用了轮廓的**那几条路走。裸行读没有任何推断,
    /// 那里不该多这一句。
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
    /// 分页的三个位置各说各的话:中间页说得出自己从第几条起,末页说得出自己是末页,
    /// 翻过了头是一句「翻过头了」而不是一句「没有这个东西」—— 分页给「缺席」添了一种
    /// 新成因,分不清就会报最强的那种。
    ///
    /// **哪一页都不给下一页的参数。** 「pass --offset N for the next page」印了 34 次而
    /// `--offset` 全史使用 0 次(08),而同形判据不认它:`2 of 9 defs` 自己就带着截断
    /// 信号,去掉那半句没有任何错误结论变得同形。它省的是一次查表,不是防一次误判 ——
    /// 而它占的是 line 1,管道下唯一的幸存者。
    /// </summary>
    [Fact]
    public void 分页的三个位置各说各的话()
    {
        var (mid, _, midCode) = Fixture.Run("list", "ThingDef", "--limit", "2", "--offset", "2");
        Assert.Equal(0, midCode);
        Assert.Contains("2 of 9 defs, starting at 3", mid, StringComparison.Ordinal);
        Assert.DoesNotContain("next page", mid, StringComparison.Ordinal);

        var (first, _, _) = Fixture.Run("list", "ThingDef", "--limit", "2");
        Assert.StartsWith("2 of 9 defs.", first, StringComparison.Ordinal);

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
    /// 逐字相同。
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
    /// 参数被改写就要说破:<c>LimitValue.Clamped</c> 记下了「这个数被我改了」,得有一条路
    /// 把它印出来,否则 <c>--limit 5000</c> 与 <c>--limit 2000</c> 的输出逐字相同。
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
    /// 声明了 --offset 的命令必须真的翻得动 —— 声明层与实现层各写一份,最容易长出
    /// 「参数表上挂着、Run 里没读」的漂移。
    /// </summary>
    [Fact]
    public void 声明了offset的命令都真的翻得动()
    {
        string[][] probes =
        [
            ["list", "ThingDef"],
            ["search", "VoidNode"],
            ["where", "thingClass"],
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

    // ---- C# 声明默认值与被人设过的值不许同形 ----

    /// <summary>
    /// <c>--path-contains</c> 点了名的字段绝不许因为「是默认值」而消失 —— 藏起来会把回答变成
    /// 「没有路径含 burstCount」,比印错值更彻底。
    /// </summary>
    [Fact]
    public void 点了名的字段不因为是默认值而消失()
    {
        var (named, _, code) = Fixture.Run("get", "Bullet_Revolver", "--path-contains", "burstCount");
        Assert.Equal(0, code);
        Assert.Contains("projectile.burstCount", named, StringComparison.Ordinal);
        // 印出来还不够,还得说清它是哪一种 —— 只印值就与「有人设过」同形。
        Assert.Contains(FieldDefault.Column, named, StringComparison.Ordinal);
        Assert.Contains("yes", named, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--path-contains</c> 筛空的两种成因不许同形:def 真没有这条路径,与**给进来的文本是个值**
    /// (stat 名装在 <c>statBases[N].stat</c> 里,按它筛路径必空)。
    /// </summary>
    [Fact]
    public void 把值当成路径筛时说破它是个值()
    {
        var (asValue, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "MarketValue");
        Assert.Equal(0, code);
        Assert.Contains("No field path", asValue, StringComparison.Ordinal);
        Assert.Contains("as a field's value", asValue, StringComparison.Ordinal);
        Assert.Contains("where --value MarketValue", asValue, StringComparison.Ordinal);

        // 反向:真的哪儿都没有时,不许无中生有地指路去 find --value。
        var (nowhere, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "zzzznothing");
        Assert.Contains("No field path", nowhere, StringComparison.Ordinal);
        Assert.DoesNotContain("as a field's value", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第三种成因,而且是最容易被读反的那种:字段在同类型别的 def 上有,只是这个 def 上是
    /// null(null 不进索引)。「这个 def 没有」与「这个类型没有」在输出上同形,而工具手边
    /// 就有那个数 —— 不说出来,读的人会拿前者当后者用。
    ///
    /// 闸盯两头:有同类时报数并说破范围,没同类时不许凭空造出一个 0 来暗示什么。
    /// </summary>
    [Fact]
    public void 字段在同类型别的def上有时当场报数()
    {
        // Meat_Muffalo 有 ingestible.foodType,Apparel_ShieldBelt 没有 —— 同为 ThingDef。
        var (kin, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "ingestible");
        Assert.Equal(0, code);
        Assert.Contains("Other defs of this type do have it: 1 def", kin, StringComparison.Ordinal);
        Assert.Contains("missing from this def, not from ThingDef", kin, StringComparison.Ordinal);
        Assert.Contains("fields ThingDef --path-contains ingestible", kin, StringComparison.Ordinal);

        // 真的哪儿都没有时:换成「索引里没有值不等于字段不存在」那段,而不是报一个 0。
        var (nowhere, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "zzzznothing");
        Assert.DoesNotContain("Other defs of this type", nowhere, StringComparison.Ordinal);
        Assert.Contains("no indexed value sits at that path", nowhere, StringComparison.Ordinal);

        // 文本其实是个值的那一支已经解释过了,不许再挂一遍长段落。
        var (asValue2, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "MarketValue");
        Assert.DoesNotContain("no indexed value sits at that path", asValue2, StringComparison.Ordinal);
    }

    /// <summary>
    /// 共享值那句结语号称扫过「上面」全部行,而 shared_values 建表时就把「与声明默认值
    /// 相同」的行整批排除了 —— 一行 <c>code_default=yes</c> 从来没进过候选。于是
    /// 「上面没有一个」印在一张**只有 yes 行**的表下面时读起来像结论,其实是没比过。
    ///
    /// 闸盯两句:正反两句共用同一个范围声明,不许只改一句。
    /// </summary>
    [Fact]
    public void 共享值结语说破自己只比过非默认行()
    {
        // 表里唯一一行是 yes(projectile.burstCount=1,声明默认值本身)。
        var (onlyYes, _, _) = Fixture.Run("get", "Bullet_Revolver", "--path-contains", "burstCount");
        Assert.Contains("Rows marked yes were not compared", onlyYes, StringComparison.Ordinal);
        Assert.Contains("No value above with 'code_default'=no", onlyYes, StringComparison.Ordinal);

        // 有命中的那一句用同一个取景。
        var (hit, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("Only rows whose 'code_default' is no were compared", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--scope all,-X</c> 排除掉的那一半非空时要说破 —— **这里的沉默推得出错结论**。
    ///
    /// 实测:<c>where compClass --value Vethara --scope all,-vanilla</c> 返回 92 个 def,
    /// 表干净、完整、看不出任何问题,而问的「这个 mod 挂到哪些宿主上」那 7 个宿主全在被排除的
    /// vanilla 里。一张静默的错表比零结果贵得多 —— 零结果至少当场知道自己没拿到东西。
    ///
    /// 三条反面各挡一种误发声:被排除的那半边真的空、白名单式 scope(排除就是意图本身)、
    /// 以及**补集说不清的写法**。最后一条是设计的一半:补集表达式只有在「起点全集、
    /// 之后只做排除」时才等于那些词的并集,中途再并进一个词就不成立,那时宁可闭嘴 ——
    /// 给不出一条能直接敲的命令的话,这句话就退化成一个没有下一步的免责声明。
    /// </summary>
    [Fact]
    public void 排除式scope说破被排除的那一半()
    {
        const string Says = "left out is not empty";

        var (excl, _, _) = Fixture.Run("list", "ThingDef", "--scope", "all,-test.mod");
        Assert.Contains(Says, excl, StringComparison.Ordinal);
        // 带数字,并给出一条能直接敲的下一步 —— 只说「还有别的」等于把活推回去。
        Assert.Contains("--scope test.mod instead, it finds 4 defs", excl, StringComparison.Ordinal);

        // **排在表上方**,与「计数在它数的那张表上方」同一条纪律。位置对这一条格外要紧:
        // 它的受众定义上就是拿到一张长表的人,而那种人最可能 head/sed 截一段就走 ——
        // 落在末尾脚注区的话,最该读到它的人正好读不到。首次落地时它就在倒数第三行。
        var said = excl.IndexOf(Says, StringComparison.Ordinal);
        var firstRow = excl.IndexOf("Apparel_ShieldBelt", StringComparison.Ordinal);
        Assert.True(firstRow > said, "补集句要排在表上方,不许落进末尾脚注区");

        // 补集用**组名**时展开也要在:读者据此知道那个词实际圈住了谁。
        var (group, _, _) = Fixture.Run("list", "ThingDef", "--scope", "all,-vanilla");
        Assert.Contains("--scope vanilla (= ludeon.rimworld) instead", group, StringComparison.Ordinal);

        foreach (var quiet in new[]
                 {
                     Fixture.Run("list", "HediffDef", "--scope", "all,-test.mod").Stdout,   // 那半边真的空
                     // 纯白名单:补集非空(test.mod 那 4 条)且拼得出(all,-ludeon.rimworld),
                     // 所以这一格测的是**不说**而不是算不出 —— 理由见 ScopeFilter.Complement。
                     // 消费侧薄层拿这份沉默当判据,别当成漏补的不对称给「修」了。
                     Fixture.Run("list", "ThingDef", "--scope", "ludeon.rimworld").Stdout,
                     Fixture.Run("list", "ThingDef").Stdout,                                // 没给 scope
                     // 中途并进来一个词:补集不再等于被排除的那些词,拼不出下一步命令。
                     Fixture.Run("list", "ThingDef", "--scope", "all,-test.mod,ludeon.rimworld").Stdout,
                 })
            Assert.DoesNotContain(Says, quiet, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>code_default</c> 的口径在**三处**出声:这两句结语、<c>--defaults</c> 的 Help、
    /// 与 SKILL.md。三处必须同形 —— 而此前只有输出侧说反了:两句结语共用的后半句是
    /// 「a yes is a declared default already」,把「值与新 new 的相等」说成「它就是那个
    /// 默认值」,读者顺着推得出「所以作者没写」,正是 r13 题 2 那个错答的方向。
    ///
    /// **它与自己的产地注释矛盾**(<c>FieldDefault.Render</c>:「这不等于没人设过它 ——
    /// XML 里照着默认值再写一遍是常事,能证的只有『无从区分』」),所以是假话不是天花板。
    /// 天花板那条(「XML 写没写不在快照里」)说的是答不出来,而这句给了个答案。
    ///
    /// 钉两件事:身份断言不许回来、限定半句两支都要在。SKILL.md 那份措辞不同
    /// (<c>cannot tell whether anyone set it</c>)但概念同,不进这条闸 —— 它不经过 CLI,
    /// 改它时回来看这条注释。
    /// </summary>
    [Fact]
    public void code_default的口径在输出与help两处同形()
    {
        var (onlyYes, _, _) = Fixture.Run("get", "Bullet_Revolver", "--path-contains", "burstCount");
        var (hit, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        var (help, _, _) = Fixture.Run("get", "--help");

        foreach (var text in new[] { onlyYes, hit, help })
        {
            Assert.DoesNotContain("is a declared default", text, StringComparison.Ordinal);
            Assert.Contains("fresh instance of the declaring type", text, StringComparison.Ordinal);
        }

        // **否定排在主句、在「值相等」那个事实之前。** r17 抓到一个受测者复述了限定的
        // 前半句、接着自己接上「所以是没写、用类默认」—— 前半句可独立成立时,只读前半句
        // 反而显得更完整,后半句的免责就成了可以不读的尾巴。
        foreach (var text in new[] { onlyYes, hit })
        {
            var neg = text.IndexOf("is not evidence that nothing wrote", StringComparison.Ordinal);
            var fact = text.IndexOf("only says the value matches", StringComparison.Ordinal);
            Assert.True(neg >= 0 && fact > neg,
                        "「值相等不构成没人写的证据」要在主句,排在「值相等」那个事实之前");
        }
    }

    /// <summary>
    /// 否定那个推论的话必须落在**产生那个推论的**那条路径上。
    ///
    /// r17 抓到:不加 <c>--defaults</c> 时,提到那些字段的**只有** <c>Not listed:</c> 那一句,
    /// 而受测者正是从它推出「没人写过」;否定它的那句当时只在加了 <c>--defaults</c>、
    /// 渲染出 yes 行时才印。**三处副本(产地注释 / SKILL.md / Help)当时全是准确的** ——
    /// 副本都对,只是没有一份落在他走的那条路上,而那是最短、最常走的一条。
    ///
    /// 这给「改一处先查全部副本」补了一个维度:除了「有几处文案、哪些说反了」,
    /// 还有**「读者可能走的每条路径上,那句话在不在」**。与「输出侧的一句话只有落在
    /// 必经路径上才测得到」是同一件事的另一面。
    /// </summary>
    [Fact]
    public void 值相等不等于没人写这句落在默认路径上()
    {
        const string Denial = "is not evidence that nothing wrote";

        // 不加 --defaults:那些行根本不在表里,只有 Not listed 那句在说它们。
        var (plain, _, _) = Fixture.Run("get", "Bullet_Revolver");
        Assert.Contains("Not listed:", plain, StringComparison.Ordinal);
        Assert.Contains(Denial, plain, StringComparison.Ordinal);

        // JSON 侧同样 —— 那条失效样本走的就是 --json。
        Assert.Contains(Denial, Fixture.Run("get", "Bullet_Revolver", "--json").Stdout,
                        StringComparison.Ordinal);

        // 加了 --defaults 之后 Not listed 那句消失(那些行进表了),否定改由
        // NoteWidelySharedValues 的句尾承载 —— 换了个承载者,不是丢了。
        // 钉住这两支是因为有人只匹配了 advisory 的开头一句就判「这条路径上没有」。
        foreach (var extra in new[] { new[] { "--defaults" }, ["--defaults", "--json"] })
            Assert.Contains(Denial, Fixture.Run(["get", "Bullet_Revolver", .. extra]).Stdout,
                            StringComparison.Ordinal);

        // 「carrying」那个读法不许回来:它把「值相等」说成「它们带的就是类默认」。
        Assert.DoesNotContain("carrying the declaring type's own default", plain, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>inherit</c> 数节点、<c>get</c> 数 def,两个数**必然**对不上,而两条命令都用绝对语气
    /// 报自己那个。差额不解释的话,读的人只能自己编一个理由 —— 盲测里编的是「那几个是
    /// code-generated」,而它们明明来自具名 XML 文件,那个解释当场被自己推翻。
    ///
    /// 闸盯两头:有差额就点名是哪几个类型,没差额时不许多话。
    /// </summary>
    [Fact]
    public void inherit说破有几个同名def不在继承层里()
    {
        // Firefoam 的 ThingDef 在继承层里(声明了 ParentName=),同名的 StatDef 不在。
        var (gap, _, _) = Fixture.Run("inherit", "Firefoam");
        Assert.Contains("Outside this layer: 1 def also named 'Firefoam'", gap, StringComparison.Ordinal);
        Assert.Contains("StatDef", gap, StringComparison.Ordinal);
        Assert.Contains("counts 2 where this command counts 1", gap, StringComparison.Ordinal);

        // 没有同名的另一半时,这句话不该在场。
        var (clean, _, _) = Fixture.Run("inherit", "Bullet_Revolver");
        Assert.DoesNotContain("Outside this layer", clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--path-contains</c> 命中了几条、却没一条是整段时,那两句只穷举了两种读法,而漏掉的第三种
    /// 正是名值对结构:<c>statBases[N].stat = MarketValue</c> 把**字段自己的名字搬进了值那
    /// 一列**,<c>--path-contains</c> 结构上够不着它。于是表干净、完整,答的却是另一个问题。
    ///
    /// 闸盯两头:够得着时报出来,而整段命中过的那些不许多这一句(那时没有歧义)。
    /// </summary>
    [Fact]
    public void path部分命中时说破那个词也可能是个值()
    {
        // 'energy' 命中 comps[0].props.energy* 两条(都不是整段),而它同时是
        // statBases[1].stat 的值 EnergyShieldRechargeRate 的一部分。
        var (both, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "energy");
        Assert.Equal(0, code);
        Assert.Contains("A third reading is in play here", both, StringComparison.Ordinal);
        Assert.Contains("where --value energy", both, StringComparison.Ordinal);

        // 整段命中过:没有这种歧义,不许多话。
        var (whole, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "stat");
        Assert.DoesNotContain("A third reading", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>keyed</c> 的查询词要过两道剥离,而两道都不留痕:先是 FTS 的语法字符(<c>*</c> 就在
    /// 其中),再是分词器把其余非字母数字当分隔符。于是 <c>Command*Settle</c> 与
    /// <c>CommandSettle*</c> 回同一批、零警告 —— 只有再敲一个 <c>Settle*Command</c> 拿到零
    /// 才知道 <c>*</c> 只是被删了;而 <c>CE_</c> 实际按 <c>CE</c> 匹配,计数是一个更宽集合的数。
    ///
    /// 闸盯两头:改过就说破,一字没改时不许多话。
    /// </summary>
    [Fact]
    public void keyed说破查询词被规范化成了什么()
    {
        var (starred, _, _) = Fixture.Run("keyed", "Command*Settle");
        Assert.Contains("was not matched as typed", starred, StringComparison.Ordinal);
        Assert.Contains("CommandSettle", starred, StringComparison.Ordinal);
        Assert.Contains("'*' is not a wildcard", starred, StringComparison.Ordinal);

        // 下划线不是我们剥的,是分词器切的 —— 两道都要说破,否则计数无从解释。
        var (underscore, _, _) = Fixture.Run("keyed", "CommandSettle_");
        Assert.Contains("was not matched as typed", underscore, StringComparison.Ordinal);

        // 一字没改:这句话不许出场,否则它退化成每次都挂的免责声明。
        var (plain, _, _) = Fixture.Run("keyed", "CommandSettle");
        Assert.DoesNotContain("was not matched as typed", plain, StringComparison.Ordinal);
    }

    /// <summary>
    /// SKILL.md 里那几条**可实测**的默认与口径,逐条对回实现。它们各自都是一句
    /// 「不这么以为就会拿错答案」的话,而文档与实现是两处产地。
    /// </summary>
    [Fact]
    public void skill那几条可实测的默认与口径逐条对得上()
    {
        // code-search 默认区分大小写:错的那次拿到的零,与「真没有」形状相同。
        var (wrongCase, _, missCode) = Fixture.Run("code-search", "thingcomp");
        Assert.Equal(1, missCode);
        var (folded, _, hitCode) = Fixture.Run("code-search", "thingcomp", "-i");
        Assert.Equal(0, hitCode);
        Assert.Contains("ThingComp", folded, StringComparison.Ordinal);
        Assert.DoesNotContain("ThingComp", wrongCase, StringComparison.Ordinal);

        // read 的 --limit 数的是印出来的行,方向与别处相反:all 是放开,不是收窄。
        var (capped, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs", "--limit", "3");
        var (whole, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs", "--limit", "all");
        Assert.True(whole.Split('\n').Length > capped.Split('\n').Length);

        // 「哪些 def 类型有这个字段」values 自己就答得出,不必按类型一个个扫。
        var (types, _, _) = Fixture.Run("values", "compClass");
        Assert.Contains("def_types", types, StringComparison.Ordinal);
        Assert.Contains("ThingDef", types, StringComparison.Ordinal);
        Assert.Contains("HediffDef", types, StringComparison.Ordinal);

        // mod 列与 --scope 管的都是 def 的归属,不是谁写下了那个值。
        var (scoped, _, _) = Fixture.Run("where", "thingClass", "--scope", "test.mod");
        Assert.Contains("TestModGun", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("Bullet_Revolver", scoped, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>where</c> 的 line 1 数的是**行**,不是 def。
    ///
    /// 一行是一个(def, 路径)对,而后缀匹配一放开,同一个 def 就常在多条路径上命中 ——
    /// 真快照上 <c>where capacity Consciousness</c> 是 **155 行 / 80 个 def**
    /// (首页二十五行里 <c>AlcoholHigh</c> 一个占四行)。此前这句印的是「155 defs」,
    /// 而拿 <c>where</c> 做集合运算的人要的正是后一个数,差了将近一倍。
    ///
    /// 两半都要:名词跟着它真正数的东西走,而两数不等时补一句说破 ——
    /// 只改名词的话,「一共有几个 def」就整个不出了。相等时那一句不许出现,
    /// 否则它只是把上一句用另一个词再念一遍。
    /// </summary>
    [Fact]
    public void where的计数句数的是行并在它不等于def数时说破()
    {
        // Apparel_ShieldBelt 在 statBases[0] 与 statBases[1] 上各命中一次。
        var (many, _, code) = Fixture.Run("where", "stat");
        Assert.Equal(0, code);
        Assert.StartsWith("4 matches.", many, StringComparison.Ordinal);
        Assert.Contains("come from 3 defs", many, StringComparison.Ordinal);

        // 没给值的问法也走这里 —— 句子里不许出现指不到东西的「这个值」。
        Assert.DoesNotContain("this value", many, StringComparison.Ordinal);

        // 两数相等:一个字都不许多。
        var (one, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        Assert.StartsWith("2 defs.", one, StringComparison.Ordinal);
        Assert.DoesNotContain("come from", one, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>where</c> 的结果里混着加载期由 C# 造出来的 def,而它们与 XML 里写出来的完全同形。
    ///
    /// 第十二轮盲测:满配文档的四个臂里有三个拿 <c>where … --limit all --json</c> 灌进脚本,
    /// 交出一串按 defName 寻址的 <c>Blueprint_*</c> 补丁 —— 那批 def 根本没有 XML 节点,
    /// 补丁一条都打不上。文档里写着这件事,四取三照样踩,所以判定得落在输出上。
    ///
    /// 闸盯三处,少一处就还原成那次事故:
    /// 一是**逐行**分得开(出事那次是 <c>--json</c> 进脚本,而脚本不读 notes);
    /// 二是句子按**整个结果集**数,不是按这一页 —— 名字扎堆,首页常常一个都碰不上;
    /// 三是一个都没有时,那一列不许出现:那时它每行同值,是纯噪声。
    /// </summary>
    [Fact]
    public void where把代码造出来的def逐行标出来而句子数的是整个结果集()
    {
        // 混合:Meat_Muffalo 是 ImpliedDefs,同 soundDrop 值的其余几个是 XML 写的。
        var (mixed, _, code) = Fixture.Run("where", "soundDrop", "Standard_Drop", "--limit", "all");
        Assert.Equal(0, code);
        Assert.Contains("declared_in", mixed, StringComparison.Ordinal);
        Assert.Contains("created by the game in code at load time", mixed, StringComparison.Ordinal);
        Assert.Contains("Meat_Muffalo", mixed, StringComparison.Ordinal);
        Assert.Contains("PatchOperation addressed by defName cannot reach them", mixed, StringComparison.Ordinal);

        // 逐行:JSON 的每一行都带着判据,不必回头读 notes。
        var (json, _, _) = Fixture.Run("where", "soundDrop", "Standard_Drop", "--limit", "all", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("matches").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("def_name").GetString() == "Meat_Muffalo"
                                   && r.GetProperty("declared_in").GetString() == "code");
        Assert.Contains(rows, r => r.GetProperty("def_name").GetString() != "Meat_Muffalo"
                                   && r.GetProperty("declared_in").GetString() == "xml");

        // 整集口径:把 Meat_Muffalo 挤出这一页,句子照样在,并且说破它不在页上。
        var (paged, _, _) = Fixture.Run("where", "soundDrop", "Standard_Drop", "--limit", "1");
        Assert.DoesNotContain("Meat_Muffalo\n", paged, StringComparison.Ordinal);
        Assert.Contains("created by the game in code at load time", paged, StringComparison.Ordinal);
        Assert.Contains("None of them are on this page", paged, StringComparison.Ordinal);

        // 一个都没有时,这一列与这句话一起消失。
        var (none, _, _) = Fixture.Run("where", "thingClass", "--scope", "test.mod", "--limit", "all");
        Assert.DoesNotContain("declared_in", none, StringComparison.Ordinal);
        Assert.DoesNotContain("created by the game in code at load time", none, StringComparison.Ordinal);
    }

    /// <summary>
    /// 抽象节点自己没有值,而 <c>same_value</c> 此前就整个不出 —— 于是 <c>165 of 165</c>
    /// 看着像铁证,实际上「这一层声明了它」与「后代各写各的」在数上分不开,分得开的
    /// 那一列不在场,而说破这件事的那句话埋在第四条脚注里。
    ///
    /// 参照值改从子树众数取,并把「一个整个类型都带的字段,追平是恒真的」那个分母摆出来。
    /// </summary>
    [Fact]
    public void 抽象节点也给得出same_value并摆出恒真那一档的分母()
    {
        var (byMode, _, code) = Fixture.Run("inherit", "BaseProjectile", "--path-contains", "soundDrop");
        Assert.Equal(0, code);
        Assert.Contains("same_value", byMode, StringComparison.Ordinal);
        Assert.Contains("is a node, not a def, so it carries no value of its own", byMode, StringComparison.Ordinal);
        Assert.Contains("'Standard_Drop'", byMode, StringComparison.Ordinal);
        // 这条降级出路已经不存在,它指的那个动作也不再有意义。
        Assert.DoesNotContain("Give a def rather than an abstract node", byMode, StringComparison.Ordinal);
        // 口径不同就得说破:这一列比的是众数,不是节点声明的值。
        Assert.Contains("not one the node declares", byMode, StringComparison.Ordinal);

        // 追平的那一行要带着全类型分母,否则它读起来就是铁证。
        var (full, _, _) = Fixture.Run("inherit", "BaseBullet", "--path-contains", "soundDrop");
        Assert.Contains("The denominator for a full row", full, StringComparison.Ordinal);
        Assert.Contains("ThingDefs carry a path containing 'soundDrop'", full, StringComparison.Ordinal);

        // 没追平的表不许多这一句 —— 它本来就没在暗示什么。
        var (partial, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "projectile.burstCount");
        Assert.DoesNotContain("The denominator for a full row", partial, StringComparison.Ordinal);
    }

    /// <summary>
    /// 点路径的后缀是**纯文本**,不在 <c>.</c> 上对齐:问 <c>graphicData.texPath</c> 会把
    /// <c>building.blueprintGraphicData.texPath</c> 一起收走,而两者是不同的字段。表干净、
    /// 计数完整,答的是另一个问题 —— 第九轮盲测在真快照上量到 185 行里只有 130 行对得上。
    ///
    /// 闸盯三件事:开关真能钉住整段;<c>[]</c> 是下标通配(那句「横跨几种形状」印的就是
    /// 带 <c>[]</c> 的形状,得能原样粘回来);以及它把结果筛空时说的是「不是整段」而不是
    /// 「没有这个字段」。
    /// </summary>
    [Fact]
    public void 点路径的后缀不在点上对齐而exactpath钉得住()
    {
        // 默认:两种形状一起收走 —— 而其中一个 def 两条路径都占,于是 3 行只有 2 个 def,
        // 计数句自己就换了名词。这不是巧合:多收一个字段进来正是本条要说的那件事,
        // 而「行数 ≠ def 数」是它在 line 1 上留下的第一个痕迹。
        var (loose, _, _) = Fixture.Run("where", "graphicData.texPath");
        Assert.Contains("3 matches.", loose, StringComparison.Ordinal);
        Assert.Contains("come from 2 defs", loose, StringComparison.Ordinal);
        Assert.Contains("building.blueprintGraphicData.texPath", loose, StringComparison.Ordinal);

        // 钉住整段:那条跨边界的不在了。
        var (pinned, _, _) = Fixture.Run("where", "graphicData.texPath", "--exact-path");
        Assert.Contains("2 defs", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("blueprintGraphicData", pinned, StringComparison.Ordinal);

        // `[]` 是下标通配:两条 statBases 都在,而写死 [0] 只留一条。
        var (anyIndex, _, _) = Fixture.Run("where", "statBases[].stat", "--exact-path");
        Assert.Contains("EnergyShieldRechargeRate", anyIndex, StringComparison.Ordinal);
        var (zeroOnly, _, _) = Fixture.Run("where", "statBases[0].stat", "--exact-path");
        Assert.DoesNotContain("EnergyShieldRechargeRate", zeroOnly, StringComparison.Ordinal);

        // 被开关筛空:成因是「不是整段」,不是「没有这个字段」,而下一步就摆在句子里。
        var (empty, _, code) = Fixture.Run("where", "blueprintGraphicData.texPath", "--exact-path");
        Assert.Equal(1, code);
        Assert.Contains("No field path is exactly 'blueprintGraphicData.texPath'", empty, StringComparison.Ordinal);
        Assert.Contains("building.blueprintGraphicData.texPath (1)", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("No def in this snapshot has a field path", empty, StringComparison.Ordinal);

        // values 同一条开关、同一条成因分流。并了池就说破自己并了几条,钉住了就不说 ——
        // 否则那句话退化成每次都挂的免责声明。
        var (pool, _, _) = Fixture.Run("values", "graphicData.texPath");
        Assert.Contains("come from 2 field paths pooled together", pool, StringComparison.Ordinal);
        var (pooled, _, _) = Fixture.Run("values", "graphicData.texPath", "--exact-path");
        Assert.DoesNotContain("blueprintGraphicData", pooled, StringComparison.Ordinal);
        Assert.DoesNotContain("pooled together", pooled, StringComparison.Ordinal);
        var (none, _, _) = Fixture.Run("values", "blueprintGraphicData.texPath", "--exact-path");
        Assert.Contains("Drop --exact-path", none, StringComparison.Ordinal);
    }

    /// <summary>
    /// 轮廓不印修饰符时,「覆写了基类的成员」与「自己新引入的可覆写成员」逐字同形 ——
    /// 两行都是 <c>property … n-n</c>,而它们对「接下来该去基类找什么」给出相反的答案。
    ///
    /// 路径同理。轮廓是「先看看有什么」那一步,而它此前只报个数:名字按裸文件名解析出来时,
    /// 读的人手上没有一条粘得回去的路径,下一条 <c>--member</c> 只好再赌一次同样的名字。
    /// </summary>
    [Fact]
    public void 轮廓分得出覆写与新引入并报出读的是哪个文件()
    {
        var (outline, _, code) = Fixture.Run("read", "Outline.cs", "--source", "vanilla", "--outline");
        Assert.Equal(0, code);

        static string RowOf(string text, string name)
            => text.Split('\n').Single(l => l.Contains($" {name} ", StringComparison.Ordinal));

        Assert.Contains("protected override", RowOf(outline, "SeedPart"), StringComparison.Ordinal);
        Assert.Contains("protected virtual", RowOf(outline, "Radius"), StringComparison.Ordinal);

        Assert.Contains("vanilla/Verse/Outline.cs, 9 declarations.", outline, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表头把命中拆成「精确」与「包含」两组,分的是**路径**;而右边那列数的是 def,一直按
    /// 包含计。两个口径叠在一张表里而不说破,「56 精确」会把整张表连同 defs 列一起读成精确数。
    /// </summary>
    [Fact]
    public void 按值反查说破defs列与拆分不同口径()
    {
        // soundPickup / soundInteract 精确,ingestible.ingestSound 只含它。
        var (split, _, _) = Fixture.Run("where", "--value", "Standard_Pickup");
        Assert.Contains("Value exactly 'Standard_Pickup': 2 field paths; containing it: 1 field path.",
            split, StringComparison.Ordinal);
        Assert.Contains("also narrows the defs column", split, StringComparison.Ordinal);

        // 一条都不精确时没有两个口径可混,那句话就不该在场 —— 否则它退化成每次都挂的免责声明。
        var (none, _, _) = Fixture.Run("where", "--value", "CompShield");
        Assert.DoesNotContain("also narrows the defs column", none, StringComparison.Ordinal);
    }

    /// <summary>
    /// 截断脚注圈的那批 def 类型,**按查询方式各不相同**:用得到这条路径的 / 取到过这个值的 /
    /// 就是这一个类型。此前三条路共用一句写死的「carrying this path」,于是按值那条与按类型
    /// 那条上,句子说的不是它做的事。
    ///
    /// 还要说破范围比上面那张表宽 —— 一个被砍过的 def 丢掉的可能正是本次问的字段,担保只能
    /// 按类型给。不说的话,名单里冒出表里没有的类型,整条脚注会被当成虚警扔掉。
    /// </summary>
    [Fact]
    public void 截断脚注说破自己圈的是哪批def类型()
    {
        var (byPath, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        Assert.Contains("every def type that uses this path at all, not just the ones in the rows above",
            byPath, StringComparison.Ordinal);

        var (byValue, _, _) = Fixture.Run("where", "--value", "RimWorld.CompShield");
        Assert.Contains("every def type that holds this value anywhere", byValue, StringComparison.Ordinal);
        Assert.DoesNotContain("uses this path", byValue, StringComparison.Ordinal);

        // 按类型那条:数的是整个类型,与 --path-contains 无关 —— 被砍掉的字段本来就不在表里,
        // 拿 --path-contains 去限定它等于拿看得见的东西限定看不见的东西。
        var (byType, _, _) = Fixture.Run("fields", "ThingDef", "--path-contains", "comps");
        Assert.Contains("all of ThingDef, whatever --path-contains says", byType, StringComparison.Ordinal);
        Assert.DoesNotContain("uses this path", byType, StringComparison.Ordinal);
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
    /// 三态里「没法比」不许并进某一边:必须**照常显示**,而且在列里与「有人改过」分得开。
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
        var (find, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield", "--json");
        Assert.Contains($"\"{FieldDefault.Column}\"", find, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一个 defName 对应几行是常态(Firefoam 既是 ThingDef 又是 StatDef,mod 覆盖原版时同理),
    /// 所以模糊回退里不许建「名字 → 一行」的字典。闸判三件事:不许崩、两行都在、
    /// 页脚的数按**行**算 —— 只留一行也「不崩」,而那份输出与正确输出逐字同形。
    /// </summary>
    [Fact]
    public void 同名两个def不许把模糊兜底打成内部错误()
    {
        var (stdout, _, code) = Fixture.Run("search", "Firefoan");
        Assert.Equal(0, code);

        // 两条 Firefoam 各是一个 def,都得出现。
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
    /// FTS 要连译文的**原文那一侧**一起索引:否则中文快照上每个 def 的英文原文都在库里
    /// 躺着却一个也搜不到,而落空句还写着「covers … and translations」。
    ///
    /// 语料里 "A blob of firefoam." 只在 original 侧,label 与 description 都不含 blob。
    /// </summary>
    [Fact]
    public void 英文原文在中文快照上搜得到()
    {
        var (stdout, _, code) = Fixture.Run("search", "blob");
        Assert.Equal(0, code);
        Assert.Contains("Firefoam", stdout, StringComparison.Ordinal);

        // 兜底必须**排在模糊回退之前**,不然拼写噪声会把真答案挤掉。
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
    /// 子串匹配要留痕:`get X --path-contains soundImpact` 只回 `soundImpactDefault`(语义相反的另一个
    /// 字段)时,得说出「你打的这个词作为完整的一段一次都没命中」。
    ///
    /// 三个落点都要判,因为改一处剩两处的输出一字不变:`get --path-contains` / `fields --path-contains` /
    /// `where --value`。每处判两档:一条整段都没有 → 说破;有整段也有子串 → 给拆分。
    /// </summary>
    [Fact]
    public void 子串匹配要说破自己不是整段命中()
    {
        // get:语料里 Apparel_ShieldBelt 有 comps[0].props.energyMax。查 "energy" 命中它,
        // 而没有任何一段整个叫 energy。
        var (get0, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "energy");
        Assert.Contains("whole path segment", get0, StringComparison.Ordinal);
        Assert.Contains("nothing here is called exactly that", get0, StringComparison.Ordinal);
        // 这句话不许收在关于存在性的强断言上 —— 「前缀式列举」是正常用法,要的字段就在
        // 下面那张表里,所以「这一行一条都没滤掉」这半句是承重的。
        Assert.Contains("removes none of the matched fields", get0, StringComparison.Ordinal);

        // 查 "comps" 则条条整段命中 —— 这时候一个字都不许多说。
        var (getAll, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "comps");
        Assert.DoesNotContain("whole path segment", getAll, StringComparison.Ordinal);
        Assert.DoesNotContain("longer name", getAll, StringComparison.Ordinal);

        // fields:同一条纪律在「这个类型有没有这个字段」的正式问法上。
        var (f0, _, _) = Fixture.Run("fields", "ThingDef", "--path-contains", "energy");
        Assert.Contains("whole path segment", f0, StringComparison.Ordinal);

        var (fMix, _, _) = Fixture.Run("fields", "ThingDef", "--path-contains", "compClass");
        Assert.DoesNotContain("whole path segment", fMix, StringComparison.Ordinal);

        // find --value:值侧。语料里 compClass 的值是 RimWorld.CompShield,
        // 整值等于 CompShield 的一条都没有。
        var (v0, _, _) = Fixture.Run("where", "--value", "CompShield");
        Assert.Contains("No value here is exactly 'CompShield'", v0, StringComparison.Ordinal);

        // 而 --exact 是调用方自己点的名,这时候不许再劝一遍。
        var (vExact, _, _) = Fixture.Run("where", "--value", "RimWorld.CompShield", "--exact");
        Assert.DoesNotContain("exactly", vExact, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「本快照没有」在读的人眼里就是「这东西不存在」,所以 `where` 落空也要说破别处有。
    ///
    /// 叠加不替换:本快照那句成因分流一个字不许少,别处那句排在它**后面**。
    /// </summary>
    [Fact]
    public void find落空时说破别的快照里有()
    {
        // OtherMod.CompOnlyElsewhere 只在 other.db 里。
        var (byValue, _, code) = Fixture.Run("where", "--value", "CompOnlyElsewhere");
        Assert.Equal(1, code);
        Assert.Contains("No field in this snapshot holds a value", byValue, StringComparison.Ordinal);
        Assert.Contains("'other'", byValue, StringComparison.Ordinal);
        Assert.Contains("--snapshot other", byValue, StringComparison.Ordinal);

        // 成因分流那句必须排在前面 —— 顺序反了就成了「换一份快照」压过「这里为什么没有」。
        Assert.True(byValue.IndexOf("No field in this snapshot", StringComparison.Ordinal) <
                    byValue.IndexOf("Another registered snapshot", StringComparison.Ordinal));

        // 指名字段那条路同样要接上。
        var (byField, _, _) = Fixture.Run("where", "compClass", "CompOnlyElsewhere");
        Assert.Contains("--snapshot other", byField, StringComparison.Ordinal);

        // 哪儿都没有的时候不许无中生有 —— 一句「别处有」比没有更坏。
        var (nowhere, _, _) = Fixture.Run("where", "--value", "NoSuchValueAnywhereAtAll");
        Assert.DoesNotContain("Another registered snapshot", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「像不像类名」这个判据决定要不要说一句**猜测**,而猜错的代价是把未经验证的
    /// 「如果 X 是抽象基类……」摆在输出位置,读的人当结论用。
    ///
    /// 判据要一处产地:`IsUpper(v[0]) || Contains('.')` 会把 XML 里最常见的两种值一并算进来
    /// —— `True`(首字母大写)与 `Sounds/Foo.ogg`(含点)。
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
        var (literal, _, _) = Fixture.Run("where", "--value", "True");
        Assert.DoesNotContain("If that is a class name", literal, StringComparison.Ordinal);

        var (cls, _, _) = Fixture.Run("where", "--value", "NoSuchCompClass");
        Assert.Contains("If that is a class name", cls, StringComparison.Ordinal);
        // 指的路是本工具自己那条,而且带上要搜的符号 —— 光说「用 code-search」
        // 等于把拼命令行这一步扔回给读的人。
        Assert.Contains("code-search \"class NoSuchCompClass", cls, StringComparison.Ordinal);
    }

    /// <summary>
    /// `sources list` 的表头报的树数与 mod 数各有一行对账,闸判的是它们**加得起来**。
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

        // 只加注,不缩范围:实现成「默认只扫快照内的树」会让穷举论证整批作废,
        // 而降级前后的输出一模一样 —— 所以这句承诺本身要在场。
        Assert.Contains("reads every tree either way", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 完整性尾注末尾那条命令,要走得到**它刚说的那一批**:裸 `snapshot truncated` 列的是
    /// 全库,而尾注说的是「与本次结果同类型」的一小批,两份输出的形状一模一样。
    /// 上一道闸只验那条命令**存在**,这一道验它**走得到**。
    /// </summary>
    [Fact]
    public void 完整性尾注指的命令要走得到它刚说的那批()
    {
        var (stdout, _, _) = Fixture.Run("where", "--value", "CompShield");

        var m = Regex.Match(stdout,
            @"Defs whose export was cut short [^']*?holding (\d+) defs? cut short between them\. " +
            @"'rimsearcher snapshot truncated([^']*)' lists them\.");
        Assert.True(m.Success, stdout);

        var claimed = int.Parse(m.Groups[1].Value);
        var argv = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(argv);   // 裸命令走到的是全库,不是刚说的那批

        var (listed, _, _) = Fixture.Run(["snapshot", "truncated", .. argv, "--limit", "all"]);
        // 计数句会把用户自己划的那道线念回去(`1 def within --type ThingDef.`),
        // 取数的正则跟着放宽,不是把那半句当噪音滤掉。
        var got = Regex.Match(listed, @"^(\d+) defs?( within [^.]*)?\.", RegexOptions.Multiline);
        Assert.True(got.Success, listed);
        Assert.Equal(claimed, int.Parse(got.Groups[1].Value));

        // 句子不许写「defs of **the same def types**」:它指的是「哪些类型能带这条路径」,
        // 与表里那几行的类型没有关系,而「the same」是唯一让人去对照的那个词。
        Assert.DoesNotContain("the same def types", stdout, StringComparison.Ordinal);

        // 类型要在散文里点名,而且与命令里那几个逐字一致 —— 不点名就没法核对。
        foreach (var t in argv.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
            Assert.Contains(t, stdout.Split("'rimsearcher snapshot truncated")[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// 收窄之后的零结果与「整份快照都没有」不是一回事:
    /// 「counts over field paths are complete for it」在收窄时只查了其中一小块。
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
    /// 同一块 `comps[N]` 里的字段互相约束(如 `minFuelCost` 盖掉同块的 `fuelPerTile`),
    /// 所以只列其中一条的那张表看着干净、其实结论会错。
    ///
    /// 语料里 Apparel_ShieldBelt 的 comps[0] 有三条:compClass(默认值,不点名)、
    /// props.energyMax(有人设过)、index(默认值)。
    /// </summary>
    [Fact]
    public void 同一块里有人设过的兄弟字段要点名()
    {
        var (get, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "energyMax");
        Assert.Contains("same block as the rows above", get, StringComparison.Ordinal);
        Assert.Contains("energyLossPerDamage", get, StringComparison.Ordinal);

        // 只点 code_default=no 的:默认值那一批没人挑过,列出来等于把类的字段表倒一遍。
        var tail = get.Split("same block")[1];
        Assert.DoesNotContain("compClass", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("index", tail, StringComparison.Ordinal);

        // find 走另一条路,同一句话要在。
        var (find, _, _) = Fixture.Run("where", "energyMax", "0.5");
        Assert.Contains("energyLossPerDamage", find, StringComparison.Ordinal);

        // 但**你看的这一行自己**是声明默认值时不提示:判别字段按定义就是默认值,
        // 而 `where compClass CompShield` 是文档推荐的那条主查询,在它上面挂一句
        // 「同块还有 energyMax」是纯噪音。
        var (disc, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        Assert.DoesNotContain("same block as the rows above", disc, StringComparison.Ordinal);

        // 不带下标的层不算容器 —— 那是分类不是实例,兄弟太多且不成组,
        // 提示会退化成每次都挂的免责声明。
        Assert.Null(PathSegments.ContainerPrefix("projectile.damageAmountBase"));
        Assert.Equal("comps[0].", PathSegments.ContainerPrefix("comps[0].props.energyMax"));

        // 同块里没有别人设过的东西时,一个字都不说。
        var (quiet, _, _) = Fixture.Run("get", "TestModGun", "--path-contains", "compClass");
        Assert.DoesNotContain("same block as the rows above", quiet, StringComparison.Ordinal);

        // 块名不许写死成 comps[N] —— ContainerPrefix 对任何带下标的层都成立
        // (statBases[8]、corePart.parts[6]、degreeDatas[0].statFactors[0] 都是块)。
        var (stat, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "statBases[0].stat");
        Assert.Contains("statBases[0]", stat.Split("same block")[1], StringComparison.Ordinal);
        Assert.DoesNotContain("comps[N]", stat, StringComparison.Ordinal);

        // 而且指的那条路要**填好**再发出去,不许留 <defName> / <block> 这种占位符。
        Assert.DoesNotContain("<defName>", stat, StringComparison.Ordinal);
        Assert.DoesNotContain("<block>", stat, StringComparison.Ordinal);
        Assert.Contains("rimsearcher get Apparel_ShieldBelt --path-contains statBases[0]", stat, StringComparison.Ordinal);

        // 走得到:那条命令真列得出刚被点名的兄弟。
        var (whole, _, wcode) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "statBases[0]");
        Assert.Equal(0, wcode);
        Assert.Contains("statBases[0].value", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 块级 `--path-contains` 上那句「整段命中」是**必然误报**:判据把 `[N]` 从段里剥掉,
    /// 于是 `comps[0]` 这种带下标的写法永远不可能等于任何一段,而命中明明全在那个块里。
    /// </summary>
    [Fact]
    public void 块级路径不许被判成子串误命中()
    {
        var (block, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path-contains", "comps[0]");
        Assert.Equal(0, code);
        Assert.DoesNotContain("whole path segment", block, StringComparison.Ordinal);
        Assert.Contains("comps[0].props.energyMax", block, StringComparison.Ordinal);

        // 不带下标的裸名字照旧走整段判定 —— 只放过「本来就是块前缀」的写法。
        var (leaf, _, _) = Fixture.Run("get", "Bullet_Revolver", "--path-contains", "damageAmount");
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

        // 「translations」不带限定就是承诺两侧都覆盖,真覆盖到了才准这么写。
        var (blob, _, _) = Fixture.Run("search", "blob");
        if (!blob.Contains("Firefoam", StringComparison.Ordinal))
            Assert.DoesNotContain("translations", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「别的命令认这个参数」那句话不许只列前三条就收尾:`--limit` 挂在十来条命令上,
    /// 而「It is accepted by 'search' and 'get' and 'find'」与「一共就这三条认」逐字同形。
    /// 被省略的那个数量正是让人改对的那半句(同 <see cref="NameList"/>)。
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
    /// 零行就是 exit 1,列 def 类型的那一支也不例外 —— 印「0 def types.」再 exit 0,
    /// 脚本按退出码分流会把它当成「查到了」。
    ///
    /// 顺带:那句话也不许把 scope 造成的空说成快照的空(不带 scope 时 defName 每个 def 都有)。
    /// </summary>
    [Fact]
    public void 零行一律exit1且不把scope的空说成快照的空()
    {
        var empty = new[] { "--scope", "all,-ludeon.rimworld,-test.mod" };

        var (types, _, tcode) = Fixture.Run(["list", .. empty]);
        Assert.Equal(1, tcode);
        Assert.Contains("--scope all,-ludeon.rimworld,-test.mod", types, StringComparison.Ordinal);
        Assert.Matches(@"Snapshot-wide the figure is \d+ def types?\.", types);

        var (values, _, vcode) = Fixture.Run(["values", "defName", .. empty]);
        Assert.Equal(1, vcode);
        // 快照里明明每个 def 都有 defName —— 空是 scope 造的,句子得这么说。
        Assert.DoesNotContain("No def in this snapshot has a field path ending in 'defName'.",
                              values, StringComparison.Ordinal);
        Assert.Contains("--scope all,-ludeon.rimworld,-test.mod", values, StringComparison.Ordinal);

        // 真不存在的路径照旧说「这快照里没有」。
        var (gone, _, gcode) = Fixture.Run("values", "zzznotafield");
        Assert.Equal(1, gcode);
        Assert.Contains("No def in this snapshot has a field path ending in 'zzznotafield'",
                        gone, StringComparison.Ordinal);
    }

    /// <summary>
    /// `get` 的 `source` 行印的是**没有目录的裸文件名**。SKILL 承诺了这一列,这里钉住它说的
    /// 那三件事:有这一列、值里没有目录分隔符、代码生成的 def 走的是占位符那一档。
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
    /// 三态计数(Tally)覆盖的只是**工具造成的**收窄:行数上限、扫描没跑完。`--scope`
    /// `--type` `--exact` `--path-contains` 不在其中,于是那些查询报出一个字面完整的计数,
    /// 实则「在我自己划的范围内完整」。
    ///
    /// 三头都要钉:给了就念、没给就一个字不多、**而且不许念错东西** ——
    /// `get --type` 挑的是哪个 def 不是从字段里筛,念回去会被读成「去掉它还有更多字段」。
    /// 判据在声明层(OptionSpec.Narrows),不在这里。
    /// </summary>
    [Fact]
    public void 用户自己划的收窄要在计数句里念回去()
    {
        var (scoped, _, _) = Fixture.Run("where", "thingClass", "RimWorld.Bullet", "--scope", "vanilla");
        Assert.Contains("1 def within --scope vanilla.", scoped, StringComparison.Ordinal);

        // 多个收窄参数一起念,顺序按声明层。
        var (two, _, _) = Fixture.Run("where", "thingClass", "RimWorld.Bullet",
                                      "--scope", "vanilla", "--exact");
        Assert.Contains("within --scope vanilla --exact", two, StringComparison.Ordinal);

        // --path-contains 是 Multi,给几次念几次。
        var (paths, _, _) = Fixture.Run("fields", "ThingDef", "--path-contains", "comps");
        Assert.Contains("field paths within --path-contains comps.", paths, StringComparison.Ordinal);

        // 一个都没给就一个字不多 —— 否则这半句退化成每条输出都挂的免责声明。
        var (bare, _, _) = Fixture.Run("where", "thingClass", "RimWorld.Bullet");
        Assert.DoesNotContain(" within ", bare, StringComparison.Ordinal);

        // --limit / --offset 不算收窄:它们管印几行,三态文法早已把那件事说清。
        var (limited, _, _) = Fixture.Run("list", "ThingDef", "--limit", "2");
        Assert.DoesNotContain(" within ", limited, StringComparison.Ordinal);

        // get 的 --type 挑的是哪个 def,不是从这个 def 的字段里筛 —— 不许念。
        var (typed, _, _) = Fixture.Run("get", "Firefoam", "--type", "StatDef");
        Assert.DoesNotContain("within --type", typed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 值侧是**单语**的:`where` 查的是游戏加载时那一份文本,译文的另一侧只活在文本索引里。
    /// 于是 `where --value` 落空与「这东西真不存在」逐字同形,而 `search` 同一个词当场命中。
    ///
    /// 夹具是反过来的一份(英文值 + 中文注入),形状一样:`where --value 护盾腰带` 空手,
    /// 而文本索引里躺着 Apparel_ShieldBelt。真不存在的那种不许挂这句 ——
    /// 挂了它就退化成每次落空都发的免责声明。
    /// </summary>
    [Fact]
    public void 值查不到时要说破值侧是单语的()
    {
        var (byValue, _, code) = Fixture.Run("where", "--value", "护盾腰带");
        Assert.Equal(1, code);
        Assert.Contains("The text index does have '护盾腰带' though", byValue, StringComparison.Ordinal);
        Assert.Contains("Apparel_ShieldBelt (ThingDef)", byValue, StringComparison.Ordinal);
        Assert.Contains("rimsearcher search 护盾腰带", byValue, StringComparison.Ordinal);

        // 指名字段的那一支走的是另一条分流,同样得挂(夹具的 label 不是字段路径,
        // 拿一条真存在的路径来问 —— 落空的是**值**,而不是路径)。
        var (byField, _, _) = Fixture.Run("where", "thingClass", "护盾腰带");
        Assert.Contains("The text index does have", byField, StringComparison.Ordinal);

        // 文本索引里也没有的,一个字都不说。
        var (gone, _, _) = Fixture.Run("where", "--value", "zzznothingatall");
        Assert.DoesNotContain("The text index does have", gone, StringComparison.Ordinal);
    }

    /// <summary>
    /// 两行的 label **与 def 类型都一样**时要点出来:查询技术上成功了、表是完整的、
    /// 没有任何异常信号,只是看得见的那几列不足以分辨(如 StatDef 的 TrapSpringChance
    /// 与 PawnTrapSpringChance 简中 label 都是「陷阱触发率」)。
    ///
    /// 跨类型的那种表里当场分得开,不许出声 —— 那时「表里没有列分得开」这半句本身是假的。
    /// </summary>
    [Fact]
    public void 同类型里label逐字相同的行要点出来()
    {
        var (clash, _, _) = Fixture.Run("search", "firefoam");
        Assert.Contains("carry the same label and the same def type", clash, StringComparison.Ordinal);
        Assert.Contains("'firefoam' (ThingDef: Firefoam, FoamPopper)", clash, StringComparison.Ordinal);
        Assert.Contains("--path-contains description", clash, StringComparison.Ordinal);

        // 同名跨类型的那一对(ThingDef / StatDef 都叫 Firefoam)label 并不相同,不许误发。
        var (single, _, _) = Fixture.Run("search", "shield belt");
        Assert.DoesNotContain("carry the same label", single, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一次 `where` 的命中横跨几种**路径形状**,得当场说出来:`where stat Mass` 的上千行里
    /// 混着一行 `statFactors[N].stat`,其余是 `statBases[N].stat`,而默认视图下没人会逐行
    /// 核对 path 列 —— `where` 又恰恰是这套命令里用来做集合运算的那一个。
    ///
    /// 数的是**整个结果集**不是这一页。只有一种形状时一个字不说。
    /// </summary>
    [Fact]
    public void find的命中横跨多种路径形状时要说破()
    {
        var (mixed, _, _) = Fixture.Run("where", "stat", "MarketValue", "--limit", "1");
        Assert.Contains("span more than one path shape", mixed, StringComparison.Ordinal);
        Assert.Contains("statBases[].stat (2)", mixed, StringComparison.Ordinal);
        Assert.Contains("statFactors[].stat (1)", mixed, StringComparison.Ordinal);

        var (single, _, _) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        Assert.DoesNotContain("span more than one path shape", single, StringComparison.Ordinal);
    }

    /// <summary>
    /// `--json` 里那个数据键**恒在**,零行时是空数组,不是整个消失 —— 键不在时消费方拿到的
    /// 是 KeyError,而「翻过头了」「快照里没有」「工具崩了」在这份 JSON 上形状完全一样。
    /// 闸按**命令**逐条过各种零行成因,不只钉越界那一种。
    ///
    /// 反向也要钉:`where` 的两张表互斥,认领的那张有、另一张不许平白出现 ——
    /// 空数组在机器侧读作「查过了,没有」。
    /// </summary>
    [Fact]
    public void json的数据键零行时是空数组而不是整个消失()
    {
        (string Key, string[] Argv)[] cases =
        [
            ("defs",      ["search", "zzznothing"]),
            ("defs",      ["list", "ThingDef", "--offset", "9000"]),
            ("matches",   ["where", "compClass", "zzznothing"]),
            ("matches",   ["where", "compClass", "--offset", "9000"]),
            ("paths",     ["where", "--value", "zzznothing"]),
            ("fields",    ["fields", "ThingDef", "--path-contains", "zzznothing"]),
            ("values",    ["values", "zzznotafield"]),
            ("types",     ["list", "--scope", "all,-ludeon.rimworld,-test.mod"]),
            ("truncated", ["snapshot", "truncated", "--def", "zzznothing"]),
            ("matches",   ["code-search", "zzzznothing"]),
            // 用例表按命令补齐:漏掉一条命令,SKILL.md 那句「missing key 只可能是你问错了键」
            // 在它上面就是假的。认领动作本身在声明层(JsonKeySpec.Rows)。
            ("defs",      ["get", "zzznosuchdef"]),
            ("keys",      ["keyed", "zzznosuchkey"]),
            ("nodes",     ["inherit", "zzznosuchnode"]),
            ("source",    ["read", "zzznosuchfile.cs"]),
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
        var (byField, _, _) = Fixture.Run("where", "compClass", "zzznothing", "--json");
        using var f = System.Text.Json.JsonDocument.Parse(byField);
        Assert.False(f.RootElement.TryGetProperty("paths", out _));

        var (byValue, _, _) = Fixture.Run("where", "--value", "zzznothing", "--json");
        using var v = System.Text.Json.JsonDocument.Parse(byValue);
        Assert.False(v.RootElement.TryGetProperty("matches", out _));

        // read 的两张表同理互斥。
        var (outline, _, _) = Fixture.Run("read", "zzznosuchfile.cs", "--outline", "--json");
        using var o = System.Text.Json.JsonDocument.Parse(outline);
        Assert.True(o.RootElement.TryGetProperty("declarations", out var decls));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, decls.ValueKind);
        Assert.False(o.RootElement.TryGetProperty("source", out _));
    }

    /// <summary>
    /// <c>--path-contains</c> 重复给是**并集**,而计数句念回去的那几个必须都真的生效过 ——
    /// 只用第一个而把两个都念进「within --path-contains A --path-contains B」,输出与一个正确结果逐字同形。
    ///
    /// 判据是**总数的单调性**:并集不可能小于任一单项。数在分页之前数,所以与 --limit 无关。
    /// </summary>
    [Fact]
    public void fields的多个path是并集而不是只认第一个()
    {
        int Total(params string[] argv)
        {
            var (json, _, _) = Fixture.Run([.. argv, "--json"]);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            // 总数在计数句里,而计数句在 notes —— 直接读行数会被 --limit 骗。
            var text = string.Join(" ", doc.RootElement.GetProperty("notes")
                          .EnumerateArray().Select(n => n.GetProperty("text").GetString()));
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s+field paths?");
            Assert.True(m.Success, $"'{string.Join(" ", argv)}' 的计数句里读不到总数: {text}");
            // 「N of M」时要的是 M。
            var all = System.Text.RegularExpressions.Regex.Matches(text, @"(\d+)\s+field paths?");
            return all.Select(x => int.Parse(x.Groups[1].Value)).Max();
        }

        var onlyComps = Total("fields", "ThingDef", "--path-contains", "comps", "--limit", "1");
        var onlyStats = Total("fields", "ThingDef", "--path-contains", "statBases", "--limit", "1");
        var both = Total("fields", "ThingDef", "--path-contains", "comps", "--path-contains", "statBases", "--limit", "1");

        Assert.True(onlyComps > 0 && onlyStats > 0, "语料没覆盖到这两个 path,闸问不出话来。");
        Assert.True(both >= onlyComps && both >= onlyStats,
            $"--path-contains comps --path-contains statBases 的总数是 {both},而单独给是 {onlyComps} / {onlyStats} —— " +
            "并集比其中一项还小,说明第二个 --path-contains 根本没生效。");
        Assert.True(both > Math.Min(onlyComps, onlyStats),
            "两个 --path-contains 的并集与其中较小的那个一样大,第二个大概率被丢了。");
    }

    /// <summary>
    /// <c>--limit all</c> 解除**行上限**,这是全系统一句话的总纲(SKILL.md),不分命令。
    /// 把 <c>all</c> 翻译成 <see cref="Limits.MaxLimit"/> 是错的:MaxLimit 管的是「--limit 收
    /// 多大的数字」,而截断句给的补救「pass --limit all for the rest」会指着调用方刚用过的
    /// 那个参数。
    /// </summary>
    [Fact]
    public void limit_all在keyed上也解除行上限()
    {
        var (json, _, code) = Fixture.Run("keyed", "filler", "--limit", "all", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var rows = doc.RootElement.GetProperty("keys").GetArrayLength();
        Assert.True(rows > Limits.MaxLimit,
                    $"'--limit all' 只回了 {rows} 行,而语料有 2100 条 —— 它被夹到上限了。");

        // 全给了就没有截断可申报。留着那句「pass --limit all」等于叫人再传一次同一个参数。
        var kinds = doc.RootElement.GetProperty("notes")
                       .EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        Assert.DoesNotContain("truncation", kinds);
    }

    /// <summary>
    /// <c>--empty-translation</c> 在**取页之前**筛:页内 <c>Where(r =&gt; r.Placeholder)</c> 会把
    /// 「第一页里没有占位」说成「一条占位都没有」,而这个开关唯一的用途就是回答
    /// 「这批有没有没译的」,假阴性与真阴性逐字相同。
    ///
    /// 语料把唯一那条占位排在 2100 条的最末,页内筛必然摸不到它。
    /// </summary>
    [Fact]
    public void placeholders是在取页之前筛的()
    {
        var (json, _, code) = Fixture.Run("keyed", "filler", "--empty-translation", "--limit", "5", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var keys = doc.RootElement.GetProperty("keys").EnumerateArray()
                      .Select(k => k.GetProperty("key").GetString()).ToList();
        Assert.Contains("FillerKey2099", keys);
        Assert.All(doc.RootElement.GetProperty("keys").EnumerateArray(),
                   k => Assert.True(k.GetProperty("placeholder").GetBoolean()));

        // 反向:落空那句话的分母是**过滤之前**的命中数,不是自己筛剩的零。
        var (miss, _, missCode) = Fixture.Run("keyed", "转至此处", "--empty-translation");
        Assert.Equal(1, missCode);
        Assert.Contains("2 keys matched", miss, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--offset</c> 在 <c>keyed</c> 的**精确 key 命中**那一路也要生效 —— 读进来不用,
    /// <c>--offset 1</c> 与 <c>--offset 0</c> 印出逐字相同的表。
    /// 翻过头也要说破,不能落回「没有这个 key」(一次翻页会被读成一次否定)。
    /// </summary>
    [Fact]
    public void keyed精确命中那一路的offset也生效()
    {
        var (head, _, headCode) = Fixture.Run("keyed", "CannotUseNoPower", "--json");
        Assert.Equal(0, headCode);
        using var doc = System.Text.Json.JsonDocument.Parse(head);
        var sources = doc.RootElement.GetProperty("keys").GetArrayLength();

        // 翻过这个 key 的来源条数,得到的必须是「翻过头了」,而不是同一张表再来一遍。
        var (past, _, pastCode) = Fixture.Run("keyed", "CannotUseNoPower", "--offset", sources.ToString());
        Assert.Equal(1, pastCode);
        Assert.Contains($"--offset {sources} is past the end", past, StringComparison.Ordinal);
        Assert.DoesNotContain("No keyed translation matches", past, StringComparison.Ordinal);
    }

    /// <summary>
    /// 查询词恰好是一个真 key 时,前缀匹配被关掉 —— 这**本身没错**(问一个 key 就该答那个
    /// key),错的是它静默发生:`keyed CommandSettle` 的一行与「这个前缀下只有一个 key」
    /// 逐字同形,而 `CommandSettleDesc` 就躺在旁边。翻译覆盖率一类的问题会因此系统性少数。
    ///
    /// 三个方向一起钉:收窄了要说破并点名兄弟;**没有兄弟时不许发声**(那句话会变成
    /// 每次精确命中都跟着的噪音);前缀查询那一路照旧两条都在。
    /// </summary>
    [Fact]
    public void keyed精确命中把前缀匹配关掉时要说破()
    {
        var (collapsed, _, code) = Fixture.Run("keyed", "CommandSettle");
        Assert.Equal(0, code);
        Assert.Contains("CommandSettleDesc", collapsed, StringComparison.Ordinal);
        // 说破的是「匹配方式变了」,不只是「还有别的」—— 后者读起来像一条可选的建议。
        Assert.Contains("prefix", collapsed, StringComparison.Ordinal);

        // 没有兄弟的精确命中:一个字都不许多说。
        var (lone, _, loneCode) = Fixture.Run("keyed", "CannotUseNoPower");
        Assert.Equal(0, loneCode);
        Assert.DoesNotContain("prefix", lone, StringComparison.Ordinal);

        // 前缀那一路本来就两条都在,这句话在那里同样是噪音。
        var (prefix, _, prefixCode) = Fixture.Run("keyed", "CommandSettl");
        Assert.Equal(0, prefixCode);
        Assert.Contains("CommandSettle", prefix, StringComparison.Ordinal);
        Assert.Contains("CommandSettleDesc", prefix, StringComparison.Ordinal);
        Assert.DoesNotContain("matching stopped being", prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一份没量过磁盘那一层的库,不许让「磁盘上没有」由沉默来承载:<c>--no-harvest-translations</c>
    /// 与没配 <c>mod_roots</c> 都造得出这种库,而 origin 那一列照样写着「in effect」。
    ///
    /// 反向也要成立:没配 mod_roots 的机器上根本没有第二层,那句话就成了每次查询都跟着的
    /// 一句废话。
    /// </summary>
    [Fact]
    public void 没量过磁盘那一层的库要说破而不是沉默()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "harvestnote");
        Directory.CreateDirectory(dir);
        var modRoot = Path.Combine(dir, "mods");
        Directory.CreateDirectory(modRoot);
        var config = Path.Combine(dir, "config.toml");
        File.WriteAllText(config, "mod_roots = ['" + modRoot.Replace("\\", "\\\\") + "']\n");

        const string Says = "never scanned the language files on disk";

        // 语料库是不带 mod_roots 造的,所以它就是一份没量过的库。
        var (keyed, _, _) = Fixture.Run("keyed", "CannotUseNoPower", "--config", config);
        Assert.Contains(Says, keyed, StringComparison.Ordinal);
        var (get, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--config", config);
        Assert.Contains(Says, get, StringComparison.Ordinal);

        // 没配 mod_roots 时**照说**,只换出路。此前这一支是闭嘴的,理由写的是
        // 「这台机器上没有第二层可漏」—— 那句话把「本机没配扫描目录」当成了
        // 「磁盘上没有译文」。第二层照旧在(玩家装着的 mod 就在那儿),缺的是去够它的路。
        // 而 snapshot import 那条路上同一件事一直是说的,还明说了
        // 「That is a gap in this snapshot, not an answer about the mods on this machine」——
        // 两处产地矛盾时,闭嘴的那处是假话。
        var (noRoots, _, _) = Fixture.Run("keyed", "CannotUseNoPower");
        Assert.Contains(Says, noRoots, StringComparison.Ordinal);
        Assert.Contains("No 'mod_roots' is configured", noRoots, StringComparison.Ordinal);
        // 配了的那支出路不许跟着变 —— 那边重导一次就够,不用先去改配置。
        Assert.DoesNotContain("No 'mod_roots' is configured", keyed, StringComparison.Ordinal);
        Assert.Contains("Re-import to measure that layer", keyed, StringComparison.Ordinal);

        // 量过的库两支都闭嘴 —— 这条边界本身不在场。
        Assert.DoesNotContain(Says, Fixture.Run("snapshot", "list").Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 收割是**默认开**的,而两条造快照的路(<c>export</c> / <c>snapshot import</c>)口径一致。
    /// 分家的话「这份库量没量过磁盘」就取决于当初是哪条路造的它,而输出里没有一个字说得清。
    /// 同时给出正反两个开关要报错,不许静默挑一个 —— 挑了之后快照里那一层的含义就成了
    /// 这段代码的偏好,而没有任何输出承载它。
    /// </summary>
    [Fact]
    public void 收割默认开且两条造快照的路口径一致()
    {
        var specs = new CommandRegistry().Specs.ToDictionary(s => s.Name);
        foreach (var name in new[] { "export", "snapshot import" })
        {
            var opts = specs[name].Options.Select(o => o.Name).ToList();
            Assert.Contains("harvest-translations", opts);
            Assert.Contains("no-harvest-translations", opts);
        }

        // 默认那一路真的会去扫:配了 mod_roots 而不给任何开关,导出来的库自称量过。
        var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "harvestdefault");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        var config = Path.Combine(dir, "config.toml");
        File.WriteAllText(config,
            "mod_roots = ['" + Path.Combine(dir, "mods").Replace("\\", "\\\\") + "']\n" +
            "snapshot_dir = '" + dir.Replace("\\", "\\\\") + "'\n");

        var (json, _, code) = Fixture.Run("snapshot", "import", Fixture.ExportPath,
                                          "--name", "harvestdefault", "--json", "--config", config);
        Assert.Equal(0, code);
        using var db = SnapshotDb.Open(Path.Combine(dir, "harvestdefault.db"));
        Assert.True(db.Harvested, "不给开关的一次导入没有去扫 mod_roots —— 收割不是默认行为了。");
        Assert.DoesNotContain("never scanned", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 点名字段的那次查询,**外延不足**的那一半。`where` 对「结果比预期多」处理得很齐
    /// (跨形状、子串、scope 补集三句),反向一直是零提示 —— 而那种局面下表是干净完整的,
    /// 没有一处看得出问题。闭卷实测四个样本零反查,带着「字段名一律反查」那条文档的
    /// 那个照样踩。
    ///
    /// 三条一起钉,少一条这句话就退化:
    /// ① 别处那个形状要**点名**并带 def 数 —— 只说「还有别处」等于把活推回去;
    /// ② 「哪些形状跟提问是同一件事,从值里推不出来」这半句不许掉 —— 试过给并集
    ///    (靶题 17,真值 11),那是个看着像答案的错数;
    /// ③ 位置在表上方。
    /// </summary>
    [Fact]
    public void 点名字段时同值落在别的路径上要说破()
    {
        // Standard_Pickup 同时坐在 soundPickup 与 soundInteract 上,同为 ThingDef。
        var (hit, _, _) = Fixture.Run("where", "soundPickup", "--value", "Standard_Pickup", "--exact");
        Assert.Contains("soundInteract", hit, StringComparison.Ordinal);
        Assert.Contains("path shape", hit, StringComparison.Ordinal);
        // 相关性判不出来这件事要写在句子里,不留给读者自己想到。
        Assert.Contains("does not follow from the value", hit, StringComparison.Ordinal);

        // 表上方 —— 与补集句同一条纪律:受众定义上就是拿到一张表的人。
        var said = hit.IndexOf("Naming a field narrows", StringComparison.Ordinal);
        var head = hit.IndexOf("def_name", StringComparison.Ordinal);
        Assert.True(said >= 0 && head > said, "这句要排在表上方,不许落进末尾脚注区");

        foreach (var quiet in new[]
                 {
                     // 不给值:这条命令答的是「谁有这个字段」,没有「同值还在别处」这回事。
                     Fixture.Run("where", "soundPickup").Stdout,
                     // 值只在这一条路径上 —— 沉默此时是真的没有别处,不是没算。
                     Fixture.Run("where", "shortHash", "--value", "12345", "--exact").Stdout,
                 })
            Assert.DoesNotContain("Naming a field narrows", quiet, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「磁盘那一层没量过」这件事有**两个独立产地**:造库那一刻(<c>snapshot import</c>)
    /// 与每次查询(<c>DiskLayer</c>)。这条闸比对的是两者,不复述任何一边的理由 ——
    /// 前一版的闸把实现的理由(「没配 mod_roots 的机器上没有第二层可漏」)抄进了注释,
    /// 于是闸与实现成了同一份判断的两个副本,错判断被钉了很久还一路绿。
    /// **测试与实现共享错误前提时,独立性是名义上的。**
    ///
    /// 所以这里只钉两条跨产地的形状,不钉措辞:
    /// ① 两侧都得带否定标记 —— 「没量过」不许只报事实不挡推论;
    /// ② <c>mod_roots</c> 这个词两侧同进同出 —— 成因不同则出路不同,而出路是给谁的,
    /// 两个产地不许各说各话。
    /// </summary>
    [Fact]
    public void 磁盘层没量过这件事在造库与查询两处同口径()
    {
        static string Env(string name, string? modRoots)
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var config = Path.Combine(dir, "config.toml");
            var roots = modRoots is null ? "" :
                "mod_roots = ['" + Path.Combine(dir, modRoots).Replace("\\", "\\\\") + "']\n";
            if (modRoots is not null) Directory.CreateDirectory(Path.Combine(dir, modRoots));
            File.WriteAllText(config, roots + "snapshot_dir = '" + dir.Replace("\\", "\\\\") + "'\n");
            return config;
        }

        // 两个成因各造一次。A:配了 mod_roots 但显式关掉收割;B:压根没地方扫。
        var cfgA = Env("harvestcalA", "mods");
        var cfgB = Env("harvestcalB", null);
        var impA = Fixture.Run("snapshot", "import", Fixture.ExportPath, "--no-harvest-translations",
                               "--name", "a", "--config", cfgA).Stdout;
        var impB = Fixture.Run("snapshot", "import", Fixture.ExportPath,
                               "--name", "b", "--config", cfgB).Stdout;
        var qA = Fixture.Run("keyed", "CannotUseNoPower", "--config", cfgA, "--snapshot", "a").Stdout;
        var qB = Fixture.Run("keyed", "CannotUseNoPower", "--config", cfgB, "--snapshot", "b").Stdout;

        // ① 四份输出都得挡住那个推论。措辞不钉,只钉否定标记在场。
        // 三种说法都是实打实的否定,不是为了让闸变绿凑进来的:`cannot answer whether X exists`
        // 与 `is not evidence that…` 挡的是同一个推断,只是一个从工具说、一个从证据说。
        // (放宽标记集之前先按实质判过一遍 —— 否则就成了「照着实现写闸」,而那正是
        // 这条闸要绕开的东西。)
        static bool Denies(string s) => s.Contains("not an answer", StringComparison.Ordinal)
                                     || s.Contains("not evidence", StringComparison.Ordinal)
                                     || s.Contains("cannot answer", StringComparison.Ordinal);
        foreach (var (what, text) in new[] { ("import A", impA), ("import B", impB),
                                             ("query A", qA), ("query B", qB) })
            Assert.True(Denies(text), $"{what} 报了「没量过」却没挡住「所以磁盘上也没有」这个推论");

        // ② mod_roots 两侧同进同出:B 是「没地方扫」,出路只能是去配它;A 有地方扫,
        //    出路是收回那个开关 —— 提了 mod_roots 反而把人支去改一个已经对的配置。
        const string Roots = "mod_roots";
        Assert.Contains(Roots, impB, StringComparison.Ordinal);
        Assert.Contains(Roots, qB, StringComparison.Ordinal);
        Assert.DoesNotContain(Roots, impA, StringComparison.Ordinal);
        Assert.DoesNotContain(Roots, qA, StringComparison.Ordinal);
    }

    /// <summary>
    /// 数据键的**声明侧**:一个键要么在声明里就说明白它恒在(<see cref="JsonKeySpec.Rows"/>),
    /// 要么在它自己那句 <c>What</c> 的开头说明白它是有条件的 —— 两者都不占的键会掉进裂缝。
    ///
    /// 判据取措辞的开头而不是全文搜关键词:「with --outline: …」这种前置从句本身就是
    /// 「没给这个开关时它不在」的说明,与 Rows 钉成同一件事就没有各自漂移的余地。
    /// </summary>
    [Fact]
    public void 每个行数组键要么声明恒在要么在措辞开头说明它有条件()
    {
        var offenders = new List<string>();
        foreach (var spec in new CommandRegistry().Specs)
            foreach (var key in spec.JsonKeys)
            {
                // 「an object: …」那类不是行数组,不在此列。
                var isRowTable = key.What.StartsWith("one row per", StringComparison.Ordinal)
                              || key.What.StartsWith("one object per", StringComparison.Ordinal);
                var saysConditional = key.What.StartsWith("with ", StringComparison.Ordinal)
                                   || key.What.StartsWith("without ", StringComparison.Ordinal);
                if (isRowTable && !key.Rows)
                    offenders.Add($"{spec.Name} → '{key.Key}' 是行数组却没标 Rows");
                if (key.Rows && saysConditional)
                    offenders.Add($"{spec.Name} → '{key.Key}' 标了 Rows 却自称有条件");
            }

        Assert.True(offenders.Count == 0,
            "行数组键的「恒在」在声明与措辞两处说法不一:\n  " + string.Join("\n  ", offenders) +
            "\n恒在的标 Rows = true(Runner 统一认领);互斥/条件性的把条件写进 What 开头," +
            "并在命令自己那条分支上调 Report.Promises()。");
    }

    /// <summary>
    /// 扫了几棵树,要跟 `sources list` 列的那几棵对得上账 —— 两个数谁也不解释谁时,
    /// 「扫过的树里一次都没出现」会被当成「全库唯一」用掉,而差额可能只是空目录。
    ///
    /// 闸盯两头:数要写成「N of M」而不是光秃秃一个 N,并且要说破差额是什么;
    /// 树都非空时(M == N)不许多话,否则这句话退化成每次都挂的免责声明。
    /// </summary>
    [Fact]
    public void 扫过的树数要跟磁盘上的树数对得上账()
    {
        // fixture 三棵树,zz.emptytree 一个文件都没有 —— 差额恒为 1。
        var (miss, _, mcode) = Fixture.Run("code-search", "zzzznothing");
        Assert.Equal(1, mcode);
        Assert.Contains("2 of 3 source trees on disk", miss, StringComparison.Ordinal);
        Assert.Contains("the rest hold no file matching --file-glob '*.cs'", miss, StringComparison.Ordinal);
        Assert.Contains("never been decompiled", miss, StringComparison.Ordinal);

        // 有命中的那句用同一个取景。
        var (hit, _, hcode) = Fixture.Run("code-search", "props");
        Assert.Equal(0, hcode);
        Assert.Contains("2 of 3 source trees on disk", hit, StringComparison.Ordinal);

        // glob 一收窄只剩一棵树扫得到,这句话更不能消失(消失了就与「全库就这么多」同形),
        // 而且报的必须是**当次**的 glob,不是写死的 '*.cs'。
        var (narrow, _, _) = Fixture.Run("code-search", "ThingComp", "--file-glob", "*Comp*.cs");
        Assert.Contains("1 of 3 source trees on disk", narrow, StringComparison.Ordinal);
        Assert.Contains("--file-glob '*Comp*.cs'", narrow, StringComparison.Ordinal);

        // 窄化到一棵树时走另一支(「in the 'X' tree alone」),不许两句话叠着说。
        var (one, _, _) = Fixture.Run("code-search", "ThingComp", "--source", "vanilla");
        Assert.DoesNotContain("on disk — the rest", one, StringComparison.Ordinal);
    }

    /// <summary>
    /// `code-search` 零命中时要说破反编译抹掉了什么:树是 ILSpy 产物,作者写的注释一条不剩
    /// (剩下的 `//` 基本是 ILSpy 自己的备注),局部变量名也没了(一律 `numN`)。
    /// **照注释或照记忆里的局部变量名去 grep,永远零命中** —— 只说「你要找的其实是 def 吧」
    /// 是把已知盲区指成了别的方向。
    ///
    /// 两条触发都要有落点,而不该触发的那条也要证明它闭着嘴。
    /// </summary>
    [Fact]
    public void 代码零命中要说破反编译抹掉了什么()
    {
        var (comment, _, _) = Fixture.Run("code-search", @"//\s*TODO");
        Assert.Contains("comment", comment, StringComparison.Ordinal);
        Assert.Contains("ILSpy", comment, StringComparison.Ordinal);

        var (local, _, _) = Fixture.Run("code-search", "myFuelCounter");
        Assert.Contains("Local variable names", local, StringComparison.Ordinal);

        // 带元字符的模式不是「照名字找一个局部变量」,不许挂那句话。
        var (regex, _, _) = Fixture.Run("code-search", @"zzz\w+\(");
        Assert.DoesNotContain("Local variable names", regex, StringComparison.Ordinal);
        Assert.DoesNotContain("ILSpy", regex, StringComparison.Ordinal);

        // 有命中时一个字都不说 —— 这是零结果分支的话。
        var (hit, _, hcode) = Fixture.Run("code-search", "props");
        Assert.Equal(0, hcode);
        Assert.DoesNotContain("Local variable names", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反编译产物**不重复父类的成员**,于是按成员名读一个文件会落空,而它就在基类里 ——
    /// 「这文件里没有」与「这个类型没有这个成员」是两件事,读的人会读成后者。
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
    /// 「Global options」这个词本身在暗示位置自由,而解析器要求它写在命令**之后** ——
    /// 位置约束要写在那个小标题自己那一行上。
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

        // 纠正话里不许留多余空格 —— --json 没有占位符。
        var (_, err, code) = Fixture.Run("--json", "types");
        Assert.Equal(2, code);
        Assert.Contains("... --json'.", err, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>inherit --path-contains</c> 用证人兄弟法回答「这个值是哪一层写的」(<c>get</c> 给的是合并后的
    /// 值,抽象节点在快照里没有自己的字段表),而它的全部价值就在**分母**上:分母算错,
    /// 每一层都看着「后代全都带」,于是每一层都像声明者。
    ///
    /// 两个方向各有各的错法:
    ///   ① 被问的那个 def 算进自己的分母 —— 它当然带着这条路径,于是最近那一层
    ///      恒为「1 of 1」,而那正是读的人最想拿来定罪的一行。
    ///   ② 异构桶的后代掉出分母 —— <c>xml_nodes.def_type</c> 是 XML 根元素名,
    ///      <c>defs.def_type</c> 是桶名,硬要求相等会把整批异构桶的后代丢掉。
    ///      分母小了,结论就往「是这一层」偏。
    /// </summary>
    [Fact]
    public void 证人的分母既不算上自己也不许漏掉异构桶的后代()
    {
        var (json, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "thingClass", "--json");
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
    /// 参照值**一个都定不下来**时,<c>same_value</c> 这一列整个不出:照印一列恒为 0 的数,
    /// 读起来是「一个兄弟都不同意」—— 而 0 既可以是量过为零,也可以是没量。
    ///
    /// 抽象节点不在此列(2026-08-01 起):它自己没有值,但子树的众数定得下来。
    /// 定不下来的只剩一种 —— 问的那个 def 在这条路径上装着好几个不同的值。
    /// </summary>
    [Fact]
    public void 参照值定不下来时证人表不许印一列恒零的同值数()
    {
        static System.Text.Json.JsonElement Witnesses(string json)
            => System.Text.Json.JsonDocument.Parse(json).RootElement
                  .GetProperty("nodes")[0].GetProperty("witnesses");

        // projectile 下三个字段三个值,没有一个能当参照。
        var (spread, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "projectile", "--json");
        foreach (var r in Witnesses(spread).EnumerateArray())
            Assert.False(r.TryGetProperty("same_value", out _),
                "参照值定不下来时 same_value 不该在场");

        // 反向:定得下来的那一侧必须有这一列,否则上面那条断言换成「永远不印」也照样绿。
        var (concrete, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "thingClass", "--json");
        foreach (var r in Witnesses(concrete).EnumerateArray())
            Assert.True(r.TryGetProperty("same_value", out _), "有参照值时 same_value 必须在场");

        // 抽象节点这一侧也必须有 —— 它此前正是「静默丢掉一列」的那个形状。
        var (node, _, _) = Fixture.Run("inherit", "BaseBullet", "--path-contains", "thingClass", "--json");
        foreach (var r in Witnesses(node).EnumerateArray())
            Assert.True(r.TryGetProperty("same_value", out _), "抽象节点按众数比,same_value 必须在场");
    }

    /// <summary>
    /// 截断那条免责说的是**这个分母里**有几条被截,不是整库有几条。
    ///
    /// 字段表在导出时被截过的 def 会「没有这条路径」而其实有 —— 正好是让一层被误判成
    /// 「没声明」的那个方向,所以这句话非说不可。但拿整库的数来说就恒为非零,而恒真的
    /// 免责声明会被学着跳过。
    /// </summary>
    [Fact]
    public void 截断免责只在这次的分母真被截时才说()
    {
        const string Caveat = "counted in other_defs had the field list cut short";

        // Bullet_Revolver 的字段表被截过(fields_truncated = 3),而 BaseBullet 名下的
        // 分母里正好有它 —— 这一句要出。
        var (withTruncated, _, _) = Fixture.Run("inherit", "BaseBullet", "--path-contains", "thingClass");
        Assert.Contains(Caveat, withTruncated, StringComparison.Ordinal);

        // 换成问 Bullet_Revolver 自己,它被排除在分母外,剩下的两条都没被截 —— 不许出。
        // 整库照旧有被截的 def,所以拿整库计数的实现在这一格红。
        var (clean, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "thingClass");
        Assert.DoesNotContain(Caveat, clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// 证人表必须自己说破逆命题不成立。
    ///
    /// 「with_path 追平 other_defs」只与「这一层声明了它」相容,并不蕴含它 —— 每个后代
    /// 各写各的一份,印出来逐字相同(vanilla 的 <c>BaseBullet --path-contains damageAmountBase</c>
    /// 是 61 of 61,而 61 个子弹各写各的伤害)。真正的证据是 same_value。
    /// </summary>
    [Fact]
    public void 证人表要说破全都带着并不等于这一层写的()
    {
        var (text, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path-contains", "thingClass");
        Assert.Contains("The converse does not hold", text, StringComparison.Ordinal);
        Assert.Contains("every descendant writing the field separately", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「快照与当前安装的游戏一致」这句话要自己说清**没比的是什么**。一句范围列全了的
    /// 自我限定照样会被读成「快照 = 现在的游戏数据」的背书,所以正面那半在场不算数,
    /// **没比的那半必须同时在场**。
    ///
    /// 这里的语料没有参考侧 XML 指纹(fixture 的库是不带环境导入的),于是走的是
    /// 「这一层根本没量过」那一支 —— 而没量过与量过了没变必须说成两句话。
    /// 量过那一支的闸在 <see cref="StalenessTests"/>。
    /// </summary>
    [Fact]
    public void 一致这句话要同时说清没比的是什么()
    {
        var (stdout, _, _) = Fixture.Run("snapshot", "status");

        // 正面那半:比过的东西点名,不能只说「一致」。
        Assert.Contains("same mods, same order, same game build", stdout, StringComparison.Ordinal);

        // 反面那半 —— 承重的是这一半。
        Assert.Contains("this snapshot has no XML fingerprint", stdout, StringComparison.Ordinal);
        // 明细表里也要有一格,而且是这个字面:SKILL.md 拿它教人怎么认出「这条判据没量过」。
        Assert.Contains("xml_fingerprint   not recorded (exported before this was measured)",
                        stdout, StringComparison.Ordinal);
        Assert.Contains("leaves this line reading 'matches' all the same", stdout, StringComparison.Ordinal);

        // 导出那一刻要报出来:「自那以后改过的文件看不见」这句话没有时刻就落不了地。
        Assert.Contains("2026-01-01T00:00:00.0000000Z UTC", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 披露句要跟着调用方**自己已经划掉**的那一维一起收 —— 在一张已经滤干净的表下面挂
    /// 一个针对别的类型的完整性告警,不是噪音而是错话,而一句被发现过期,其余每一句
    /// 都要被重新审视。
    /// </summary>
    [Fact]
    public void 完整性脚注要跟着自己划的类型一起收()
    {
        const string Note = "cut short between them";

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
    /// 目录在而里面一个 .cs 都没有,是关于**磁盘**的事实,与「这棵树在不在这次的计划里」
    /// 正交 —— 把计划外的一律短路成「not in the snapshot」,汇总行的「0 never built」就是
    /// 假的,而 <c>code-search</c> 的页脚正指着这一列。
    ///
    /// 三个断言各守一段:空这件事要单成一档、files 列要把 0 与「没有这个目录」分开、
    /// 而 `sources sync` 填不了的那些要说破。
    /// </summary>
    [Fact]
    public void 空的源码树要自成一档而不是被计划外那句话吸收掉()
    {
        var (stdout, _, _) = Fixture.Run("sources", "list");

        // zz.emptytree 在计划外,而它是空的 —— 空压得住计划内外。
        Assert.Matches(new Regex(@"zz\.emptytree\s+0\s+empty", RegexOptions.Multiline), stdout);

        // 汇总行单列一档。
        Assert.Contains("holding no .cs file", stdout, StringComparison.Ordinal);

        // 指路要走得通:sync 计划里没有它们,那句「sync rebuilds them」对它们不成立。
        Assert.Contains("will never fill them", stdout, StringComparison.Ordinal);
        Assert.Contains("code-search' reports reading no file from", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反查落空时要说破**这个索引里装的只是值**:导出器见 null 直接 return(DefExporter),
    /// 那条路径从来没进过索引,于是「这个字段不存在」与「它在,只是每个 def 上都是 null」
    /// 在输出上完全同形。
    ///
    /// find / values / fields 三条反查路都判,因为补一处剩两处的输出一字不变。
    /// </summary>
    [Fact]
    public void 反查落空要说破索引里装的只是值()
    {
        const string Line = "keep a field out of this index without any sign here";

        var (find, _, _) = Fixture.Run("where", "noSuchField", "x");
        Assert.Contains(Line, find, StringComparison.Ordinal);

        var (values, _, _) = Fixture.Run("values", "noSuchField");
        Assert.Contains(Line, values, StringComparison.Ordinal);

        var (fields, _, _) = Fixture.Run("fields", "ThingDef", "--path-contains", "zzznosuchtext");
        Assert.Contains(Line, fields, StringComparison.Ordinal);

        // identity 那一档不说 —— 答案已经给全了,再挂一句索引边界是纯噪音。
        // 这一条反着守:少了它,「到处都说一遍」也能让上面三条全绿。
        var (identity, _, _) = Fixture.Run("where", "mod");
        Assert.DoesNotContain(Line, identity, StringComparison.Ordinal);
    }

    /// <summary>
    /// `where` 只给一个词时,那个词多半是**值**而不是字段路径 —— 这条命令的正脸就是
    /// 「从一个类名或一个值反查 def」,而 `where CompShield` 曾经落在「没有这个字段路径」上死掉,
    /// 同一份快照里 `where --value CompShield` 却当场有答案。
    ///
    /// 落点分流借 search 那一份产地,但 **def 名那一档要自己说**:借来的措辞是
    /// 「'X' is not a def name」,而这里它就是 def 名,照借等于把一句假话摆在输出位置。
    /// </summary>
    [Fact]
    public void find给一个词落空时要说破那个词其实是什么()
    {
        // 它是字段取值 —— 指路要把参数填好,而不是给一个 <text> 占位。
        var (asValue, _, _) = Fixture.Run("where", "CompShield");
        Assert.Contains("it appears as a field value", asValue, StringComparison.Ordinal);
        Assert.Contains("'rimsearcher where --value CompShield'", asValue, StringComparison.Ordinal);
        Assert.DoesNotContain("--value <text>", asValue, StringComparison.Ordinal);

        // 它是 def 名 —— 借来的那句在这里是假话,一个字都不许出现。
        var (asDef, _, _) = Fixture.Run("where", "Bullet_Revolver");
        Assert.DoesNotContain("is not a def name", asDef, StringComparison.Ordinal);
        Assert.Contains("is a def name in this snapshot, not a field path", asDef, StringComparison.Ordinal);

        // 指出去的那条路要走得通,否则这句话只是把死路换了个说法。
        var (points, _, code) = Fixture.Run("where", "--value", "Bullet_Revolver");
        Assert.Equal(0, code);
        Assert.Contains("verbs[0].defaultProjectile", points, StringComparison.Ordinal);

        // 反面:没人引用的 def 名不许指向那条空手而归的命令,而要把「没人引用」说出来。
        var (unreferenced, _, _) = Fixture.Run("where", "Firefoam");
        Assert.Contains("no indexed field value points at it", unreferenced, StringComparison.Ordinal);
        Assert.DoesNotContain("'rimsearcher where --value Firefoam'", unreferenced, StringComparison.Ordinal);

        // 哪儿都不是的那一档:算不出来就退回带占位的通用指路,不许硬编一句猜测。
        var (nowhere, _, _) = Fixture.Run("where", "noSuchField");
        Assert.Contains("'rimsearcher where --value <text>'", nowhere, StringComparison.Ordinal);
        Assert.DoesNotContain("is not a def name", nowhere, StringComparison.Ordinal);
    }

    /// <summary>
    /// 默认值折叠按「谁设的值」筛,而提问常常是「这个列表有几项」—— 两个维度正交,
    /// 却归同一个开关管。一整个列表项被折光时,「这个列表只有一项」就成了看得见的形状。
    ///
    /// 下标前缀不受折叠影响(matchedPaths 是折叠前的),所以这件事算得出来 ——
    /// **两边都要说**:藏了就点名,没藏就把那句正面的话给出来。
    /// </summary>
    [Fact]
    public void 折叠藏掉整个列表项时要点名没藏时要说没藏()
    {
        // --limit 2 把 statBases[0] 那一族整个挤出视野 —— 藏了就点名。
        var (hidden, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--limit", "2");
        Assert.Contains("Nothing below shows any field of these list entries", hidden, StringComparison.Ordinal);
        Assert.Contains("statBases[0]", hidden, StringComparison.Ordinal);

        // 不截时每个下标都露过面 —— 这时候要给正面那句话,不能沉默。
        var (whole, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("Every list index the def has appears below", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing below shows any field", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 嵌套 <c>&lt;li Class="…"&gt;</c> 的运行时类型这一维,要按导出器版本分说:0.2.0 起
    /// 导出器给列表元素发一条 <c>&lt;path&gt;.Class</c>,而**老快照对 `where Class X` 回的那个零,
    /// 与「量过了、确实没人用它」逐字同形**。两个世界各要一个落点:主快照标 0.2.0,
    /// other 那份标 0.1.0。
    /// </summary>
    [Fact]
    public void 嵌套类型这一维量没量过要按导出器版本分说()
    {
        // 量过的那份:这一维真的能查到东西。
        var (hit, _, code) = Fixture.Run("where", "Class", "RimWorld.CompProperties_Shield");
        Assert.Equal(0, code);
        Assert.Contains("TestModGun", hit, StringComparison.Ordinal);

        // 量过的那份落空时:指的路是这一维本身。
        var (miss, _, _) = Fixture.Run("where", "noSuchField", "x");
        Assert.Contains("indexed as '<path>.Class'", miss, StringComparison.Ordinal);

        // 没量过的那份:不许长成一样。说破是这份快照没量,而不是没人用。
        var other = Path.Combine(Fixture.SnapshotDir, "other.db");
        _ = Fixture.Db;
        var (old, _, _) = Fixture.Run("where", "noSuchField", "x", "--db", other);
        Assert.Contains("before that type entered the index", old, StringComparison.Ordinal);
        Assert.DoesNotContain("indexed as '<path>.Class'", old, StringComparison.Ordinal);
    }

    /// <summary>
    /// 折叠行是**表的一部分**,不是表上方的一句评论:被折走的那一列对每一行都仍然成立。
    ///
    /// 这一形状在消费侧统计里出现 120 次,而它一旦被读成「表里没有这一列」,整张表就少了
    /// 一维 —— 尤其是折的正好是 <c>code_default</c> 时,「这些行没有默认值信息」与
    /// 「这些行全都是 no」是相反的结论。机器侧那半同样要守:<c>--json</c> 一列都不折,
    /// 照文本形状写的解析器会在这里拿到一个不存在的键。
    /// </summary>
    [Fact]
    public void 折叠掉的列对每一行仍然成立而json一列都不折()
    {
        var (text, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains($"Same in every row, not repeated below: {FieldDefault.Column}=no", text,
            StringComparison.Ordinal);
        // 折了就不该再作为列出现 —— 两处都印的话这道闸证不出折叠真的发生过。
        var header = text.Split('\n').First(l => l.TrimStart().StartsWith("path", StringComparison.Ordinal));
        Assert.DoesNotContain(FieldDefault.Column, header, StringComparison.Ordinal);

        var (json, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // 撞名时每个 def 各占一块,所以字段表挂在 defs[] 里面而不是根上。
        var rows = doc.RootElement.GetProperty("defs")[0].GetProperty("fields").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        // 每一行都带,且带的就是被折走的那个值 —— 少一行都算折叠改写了数据。
        Assert.All(rows, r => Assert.Equal("no", r.GetProperty(FieldDefault.Column).GetString()));
    }

    /// <summary>
    /// <c>ThingDef.ResolveReferences</c> 之类的引擎代码会给**每一个** def 塞上同一个值
    /// (如 <c>soundImpactDefault</c>),而 <c>code_default</c> 那一列能证的只有「与刚 new
    /// 出来的实例不同」,读的人一律读成「有人挑了它」。
    ///
    /// 分辨「XML 写的」与「引擎填的」在这份快照里没有产地:导出跑在
    /// <c>StaticConstructorOnStartup</c>,resolve 早已做完,要插进去只能上 Harmony,
    /// 而 DataMod 是刻意无依赖的。所以不猜成因,**只报可核对的事实**:同类型里有几个
    /// def 也是这个值。
    ///
    /// 两边都判 —— 少了负面那半,「没说」就成了「都是这个 def 自己的」的沉默担保。
    /// </summary>
    [Fact]
    public void 同类型大多数都有的值要当场说破而不是让人读成有人挑过()
    {
        // fixture 的九个 ThingDef 全带 soundImpactDefault —— 与真实的引擎级默认同形。
        var (shared, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        // 指的是那一列的**名字**,不是它印出来的取值:整列同值时渲染器会把它折进表上方
        // 那一行,于是「上面的一个 no」在表里根本不存在。
        Assert.Contains($"their '{FieldDefault.Column}' is not this def having made a choice", shared,
            StringComparison.Ordinal);
        Assert.Contains("soundImpactDefault (9)", shared, StringComparison.Ordinal);
        // 这句话与折叠行同时在场时才有意义 —— 折叠没发生的话,它指的列就该在表里。
        Assert.Contains($"Same in every row, not repeated below: {FieldDefault.Column}=no", shared,
            StringComparison.Ordinal);
        // 名单不许截 —— 截了的话「不在名单里」同时意味着两件事。
        var brackets = shared.Split("the count in brackets:")[1].Split('\n')[0];
        Assert.DoesNotContain("more", brackets, StringComparison.Ordinal);

        // 另一个类型上没有这种值 —— 这时候要明说没有,不能靠沉默。
        var (own, _, _) = Fixture.Run("get", "VariantOne");
        Assert.Contains($"No value above with '{FieldDefault.Column}'=no is one that most of the", own,
            StringComparison.Ordinal);
        Assert.DoesNotContain("is not this def having made a choice", own, StringComparison.Ordinal);
    }

    // ---- mod 列表这一层(收束时才发现它一道闸都没有)----

    /// <summary>
    /// 「某份保存的列表点了它的名」与「这份快照覆盖了它」是两个问题,而且答案会不一样 ——
    /// 列表是**导出的输入**,快照是导出的产物,中间隔着一次没跑过的导出。
    ///
    /// 落空那一句是这条的要害。`modlist show --find` 找不到时,唯一诚实的话是
    /// 「没有哪份列表点它的名」,**不是**「本机没装」—— 后者这条命令根本没看过。
    /// </summary>
    [Fact]
    public void 列表点没点名与快照覆没覆盖是两个问题()
    {
        // fixture-extra 点了 test.notinsnapshot 的名,而快照里没有它。
        var (found, _, fcode) = Fixture.Run("modlist", "show", "--find", "test.notinsnapshot");
        Assert.Equal(0, fcode);
        Assert.Contains("fixture-extra", found, StringComparison.Ordinal);

        var (covered, _, _) = Fixture.Run("mods");
        Assert.DoesNotContain("test.notinsnapshot", covered, StringComparison.Ordinal);

        // 一份列表都没点名时,不许把话说成「没装」。
        var (miss, _, mcode) = Fixture.Run("modlist", "show", "--find", "zzznotamodanywhere");
        Assert.Equal(1, mcode);
        Assert.Contains("says nothing about whether it is installed", miss, StringComparison.Ordinal);
    }

    /// <summary>
    /// 读不通的 <c>.rml</c> 不许从名单里消失。
    ///
    /// 消失了的话,「你没存过这个列表」与「存了、但那个文件坏了」输出一个字都不差,
    /// 而读者的下一步是相反的两件事(重存一遍 / 去改那个文件)。
    ///
    /// 三处都判:<c>list</c> 的行还在且格子说得出「读不出」、<c>show --find</c> 的
    /// 完整性断言把跳过的那份算在外、按名字直接问它时说得出坏在哪。
    /// </summary>
    [Fact]
    public void 读不通的列表文件留在名单里而不是消失()
    {
        var (list, _, code) = Fixture.Run("modlist", "list");
        Assert.Equal(0, code);
        Assert.Contains("fixture-damaged", list, StringComparison.Ordinal);
        Assert.Contains("unreadable", list, StringComparison.Ordinal);
        // 「读不出」与「零个 mod」是两件事,那一格不许退化成 0。
        var row = list.Split('\n').Single(l => l.Contains("fixture-damaged", StringComparison.Ordinal));
        Assert.Contains("unreadable", row, StringComparison.Ordinal);

        // 搜遍所有列表时,「没有哪份点它的名」这句话覆盖不到打不开的那份 —— 得说破。
        var (miss, _, mcode) = Fixture.Run("modlist", "show", "--find", "zzznotamodanywhere");
        Assert.Equal(1, mcode);
        Assert.Contains("could not be read and so was not searched", miss, StringComparison.Ordinal);
        Assert.Contains("fixture-damaged", miss, StringComparison.Ordinal);

        // 有命中那一支同样要说破 —— 命中了不等于搜全了。
        var (found, _, fcode) = Fixture.Run("modlist", "show", "--find", "test.notinsnapshot");
        Assert.Equal(0, fcode);
        Assert.Contains("could not be read and so was not searched", found, StringComparison.Ordinal);
        Assert.Contains("A match could be sitting in there.", found, StringComparison.Ordinal);

        // 名字是条走得通的路:直接问它,答的是坏在哪,不是「没有这份列表」。
        var (show, showErr, scode) = Fixture.Run("modlist", "show", "fixture-damaged");
        Assert.Equal(2, scode);
        Assert.DoesNotContain("No mod list named", show + showErr, StringComparison.Ordinal);
        Assert.Contains("not readable XML", show + showErr, StringComparison.Ordinal);
    }

    // ---- 嵌套 Class= 那一维:三档快照各说各的话 ----

    /// <summary>
    /// <c>--own-class</c> 查的是 def **自己**的运行时类。在那个类恒定的类型上,它区分不了
    /// 任何东西 —— 而「确实没有 def 用这个类」与「这个选项问的根本不是这件事」
    /// 在一句 "No def of type X has class 'Y'" 上逐字同形。
    ///
    /// 实证:一次真实会话里,这句话把调用方送进了连续十条命令的死胡同 —— 它据此
    /// 认定「没有 GenStepDef 用 GenStep_ScatterLumpsMineable」,而事实是有,
    /// 只是那个信息当时不在库里。
    /// </summary>
    [Fact]
    public void 类恒定的类型上class选项要说破自己区分不了()
    {
        // modern 那份里 GenStepDef 的两个 def 都是 Verse.GenStepDef。
        var (miss, _, code) = Fixture.Run("list", "GenStepDef", "--own-class", "RimWorld.GenStep_Nothing",
                                          "--db", Fixture.ModernDb);
        Assert.Equal(1, code);
        Assert.Contains("--own-class cannot tell them apart", miss, StringComparison.Ordinal);
        Assert.Contains("is not evidence about", miss, StringComparison.Ordinal);
        // 转向要指到真正能查到多态的那条路上。
        Assert.Contains("where Class RimWorld.GenStep_Nothing", miss, StringComparison.Ordinal);

        // 类不止一种时,原来那句照旧 —— 它在那里是准的。
        var (multi, _, mcode) = Fixture.Run("list", "TestBaseDef", "--own-class", "NoSuchClass");
        Assert.Equal(1, mcode);
        Assert.Contains("That type holds", multi, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot tell them apart", multi, StringComparison.Ordinal);
    }

    /// <summary>
    /// 三档快照对「嵌套类型查得到吗」说的话必须互不相同,而**量全了的那档在这里一个字都不说**。
    ///
    /// 中间那档(0.2~0.3,只量列表元素)最险:<c>where Class &lt;单字段上的类&gt;</c> 照样回零,
    /// 而一句 "is the query that reaches it" 会把人送去查一条对 <c>genStep</c> 根本不存在
    /// 的路径 —— 走空了,落空句再把同一条规则念一遍,闭环。
    ///
    /// 0.4 那档在这个调用点上是**同一个闭环的另一半**:句子说「'find Class &lt;ClassName&gt;'
    /// 才是查得到它的那条查询」,而走到这里的前提(<c>isClassPath</c>)正是调用方刚跑完那条。
    /// 于是它把人指回他站着的地方。沉默在这里是有内容的 —— 与本工具别处一致,只有出问题才发声,
    /// 那两档一发声就是「你手上这个零是假的」。
    /// </summary>
    [Fact]
    public void 嵌套类型这一维按快照量到哪一步说话()
    {
        // 0.4:量全了 —— 不许再指一遍刚跑过的那条查询。
        var (modern, _, _) = Fixture.Run("where", "Class", "RimWorld.NotAnyClassHere", "--db", Fixture.ModernDb);
        Assert.DoesNotContain("in a list or on a single field", modern, StringComparison.Ordinal);
        Assert.DoesNotContain("is the query that reaches it", modern, StringComparison.Ordinal);

        // 0.2:只量了列表元素 —— 必须点名它够不着的是哪一类,且不许说成「查得到」。
        var (mid, _, _) = Fixture.Run("where", "Class", "RimWorld.NotAnyClassHere");
        Assert.Contains("for list elements only", mid, StringComparison.Ordinal);
        Assert.Contains("GenStepDef.genStep", mid, StringComparison.Ordinal);
        Assert.Contains("not evidence about it", mid, StringComparison.Ordinal);
        Assert.DoesNotContain("in a list or on a single field", mid, StringComparison.Ordinal);

        // 0.1:一点没量。
        var (old, _, _) = Fixture.Run("where", "Class", "RimWorld.NotAnyClassHere", "--db", Fixture.OtherDb);
        Assert.Contains("not in this snapshot at all", old, StringComparison.Ordinal);
        Assert.DoesNotContain("for list elements only", old, StringComparison.Ordinal);
    }

    /// <summary>
    /// 单字段上的 <c>Class=</c> 在 0.4 那档要真查得到 —— 这是整条 def→代码 的桥。
    /// </summary>
    [Fact]
    public void 单字段上的类在量全了的快照里查得到()
    {
        var (hit, _, code) = Fixture.Run("where", "Class", "RimWorld.GenStep_ScatterLumpsMineable",
                                         "--db", Fixture.ModernDb);
        Assert.Equal(0, code);
        Assert.Contains("FixtureScatterLumps", hit, StringComparison.Ordinal);
        // 路径不以 ] 收尾 —— 旧判据对它一条都发不出。
        Assert.Contains("genStep.Class", hit, StringComparison.Ordinal);
    }

    // ---- list --find:把 `list X | grep y` 挤掉 ----

    /// <summary>
    /// 管道接 grep 筛在 <c>--limit</c> **之后**,页外的东西压根到不了 grep,而计数句
    /// 也一起被吃掉 —— 于是「这一页里没有」与「整个快照里没有」在一个空结果上同形。
    /// <c>--find</c> 筛在之前,并且把分母说出来。
    /// </summary>
    [Fact]
    public void list的find筛在截断之前且报得出分母()
    {
        var (all, _, _) = Fixture.Run("list", "ThingDef", "--limit", "all");
        var total = System.Text.RegularExpressions.Regex.Match(all, @"(\d+) defs?\b").Groups[1].Value;

        // 只留一行的过滤 + 一个小得离谱的 limit:grep 那条路在这里必然落空。
        var (found, _, code) = Fixture.Run("list", "ThingDef", "--find", "shieldbelt", "--limit", "1");
        Assert.Equal(0, code);
        Assert.Contains("Apparel_ShieldBelt", found, StringComparison.Ordinal);
        // 分母不许丢:筛后的 total 单独摆着会被读成「这个类型总共就这些」。
        Assert.Contains($"this type holds {total} defs in all", found, StringComparison.Ordinal);

        // 筛空 ≠ 类型不存在。
        var (miss, _, mcode) = Fixture.Run("list", "ThingDef", "--find", "zzznotathing");
        Assert.Equal(1, mcode);
        Assert.Contains("in its name or label, out of", miss, StringComparison.Ordinal);
        Assert.DoesNotContain("No def type named", miss, StringComparison.Ordinal);

        // 不给 def 类型时筛的是**类型名** —— 这条路不退 2,它在这一半真的生效。
        var (types, _, tcode) = Fixture.Run("list", "--find", "Thing");
        Assert.Equal(0, tcode);
        Assert.Contains("ThingDef", types, StringComparison.Ordinal);
        Assert.Contains("def types in all", types, StringComparison.Ordinal);

        var (noType, _, ncode) = Fixture.Run("list", "--find", "zzznotatype");
        Assert.Equal(1, ncode);
        Assert.Contains("No def type in this snapshot has", noType, StringComparison.Ordinal);
    }

    // ---- types 并入 list(功能收束)----

    /// <summary>
    /// <c>types</c> 与 <c>list</c> 问的是同一张表的两个层级:「有哪些桶」与「桶里有哪些 def」,
    /// **不给 def 类型**就是问上面那一层。
    ///
    /// 判的是这条路走得通且答的是 def 类型:退出码、计数句的名词、表头。
    /// 不判逐字措辞 —— 那一份在字节基线里。
    /// </summary>
    [Fact]
    public void 不给def类型时list答的是有哪些def类型()
    {
        var (bare, _, code) = Fixture.Run("list");
        Assert.Equal(0, code);
        // 三态文法的第一句:数的名词必须是 def type,不是 def。
        Assert.Matches(@"^\d+( of \d+)? def types?\.", bare);
        Assert.Contains("def_type", bare, StringComparison.Ordinal);

        // 老名字还认(别名),且与裸 list 逐字同形。
        var (aliased, _, acode) = Fixture.Run("types");
        Assert.Equal(0, acode);
        Assert.Equal(bare, aliased);
    }

    /// <summary>
    /// 一条命令按有没有 def 类型换数据键(<c>defs</c> / <c>types</c>),而两边都得在
    /// **开查之前**声明 —— 否则零行时那个键整个消失,消费方拿到的不是空数组而是 KeyError,
    /// 与「工具崩了」同形(<see cref="Report.Promises"/> 的产地注释)。
    ///
    /// 反向也判:认领的那张有、另一张不许平白出现 —— 空数组在机器侧读作「查过了,没有」。
    /// </summary>
    [Fact]
    public void list按有没有给def类型换数据键且两边互斥()
    {
        var (types, _, _) = Fixture.Run("list", "--json");
        using var t = System.Text.Json.JsonDocument.Parse(types);
        Assert.True(t.RootElement.TryGetProperty("types", out _));
        Assert.False(t.RootElement.TryGetProperty("defs", out _));

        var (defs, _, _) = Fixture.Run("list", "ThingDef", "--json");
        using var d = System.Text.Json.JsonDocument.Parse(defs);
        Assert.True(d.RootElement.TryGetProperty("defs", out _));
        Assert.False(d.RootElement.TryGetProperty("types", out _));
    }

    /// <summary>
    /// <c>--own-class</c> 与 <c>--offset</c> 只在给了 def 类型时才有意义,而它们仍然声明在这条
    /// 命令上 —— 「不给类型还传了它们」不许照单收下再悄悄不生效
    /// (同 <see cref="CommandContext.Limit"/> 那条静默夹紧)。
    ///
    /// 所以当场退 2,并且**说清该往哪走**:桶归属这个问题下面那条零行分流早就会答。
    /// </summary>
    [Fact]
    public void 不给def类型时不许悄悄吃掉class与offset()
    {
        foreach (var argv in new[] { new[] { "list", "--own-class", "TestVariantDef" },
                                     ["list", "--offset", "2"] })
        {
            var (stdout, stderr, code) = Fixture.Run(argv);
            Assert.Equal(2, code);
            Assert.Equal("", stdout);
            Assert.Contains($"--{argv[1].TrimStart('-')} needs a def type", stderr, StringComparison.Ordinal);
        }

        // 指的那条路真的走得通 —— 不许指了个空。
        var (holder, _, hcode) = Fixture.Run("list", "TestVariantDef");
        Assert.Equal(1, hcode);
        Assert.Contains("--own-class TestVariantDef", holder, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--value</c> 与位置上的那个值说的是同一件事。
    ///
    /// 此前 <c>where</c> 的分支判据挂在「给没给 --value」上,于是
    /// <c>where --field X --value Y</c> 被拒掉 --field 之后,剩下的半条命令照样跑得通、
    /// 答的却是「哪些字段路径装着 Y」—— 一个语法正常、语义全错、还长得像正常结果的东西。
    /// 判据改成「给没给字段」之后,两种写法必须逐字节同形。
    /// </summary>
    [Fact]
    public void 字段在场时value与位置上的值是同一件事()
    {
        var (inline, _, inlineCode) = Fixture.Run("where", "compClass", "RimWorld.CompShield");
        var (named, _, namedCode) = Fixture.Run("where", "compClass", "--value", "RimWorld.CompShield");
        Assert.Equal(0, inlineCode);
        Assert.Equal(namedCode, inlineCode);
        Assert.Equal(inline, named);

        // 没有字段时 --value 仍是「搜遍所有字段」那一问,两种问法的表不许混成一张。
        var (anyField, _, _) = Fixture.Run("where", "--value", "RimWorld.CompShield", "--json");
        Assert.Contains("\"paths\"", anyField, StringComparison.Ordinal);
        Assert.DoesNotContain("\"matches\"", anyField, StringComparison.Ordinal);

        var (byField, _, _) = Fixture.Run("where", "compClass", "--value", "RimWorld.CompShield", "--json");
        Assert.Contains("\"matches\"", byField, StringComparison.Ordinal);
        Assert.DoesNotContain("\"paths\"", byField, StringComparison.Ordinal);
    }

    /// <summary>
    /// 打进来的名字是这条命令的**位置参数**时,报错要说破它该怎么写,并把值填进去 ——
    /// 「别的命令认这个选项」只说得出这里不行,说不出这里该怎么办。
    ///
    /// 让位于本命令的近似选项那一条也在这里判:位置参数一共就一两个,前缀匹配太容易撞上,
    /// 撞上就是把拼错了选项的人往沟里带。
    /// </summary>
    [Fact]
    public void 位置参数被当成选项打进来时给出填好的写法()
    {
        var (_, err, code) = Fixture.Run("where", "--field", "compClass");
        Assert.Equal(2, code);
        Assert.Contains("rimsearcher where compClass <value>", err, StringComparison.Ordinal);
        // 这一档取代了「别的命令有」那句 —— 两句一起说,长度翻倍而信息没多。
        Assert.DoesNotContain("It is accepted by", err, StringComparison.Ordinal);

        // 没有值可填时就摆出形状,不许凭空捏一个。
        var (_, bare, _) = Fixture.Run("where", "--field");
        Assert.Contains("rimsearcher where <fieldPath> <value>", bare, StringComparison.Ordinal);

        // 拼错的选项仍走近似候选:`--values` 前缀命中位置参数 <value>,而它要的显然是 --value。
        var (_, typo, _) = Fixture.Run("where", "compClass", "--values", "x");
        Assert.Contains("Did you mean --value?", typo, StringComparison.Ordinal);
        Assert.DoesNotContain("is an argument rather than an option", typo, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同名冲突那句的三档。语料里同名的只有 Firefoam 一对,于是端到端只走得到「剩一个别的」
    /// 那档 —— 剩好几个的那档在真数据里天天出现(`Space` 挂着六个 def),却一直没有落点,
    /// 复数形态在那里错了很久。
    /// </summary>
    [Fact]
    public void 同名冲突那句在剩几个别的类型上都成句()
    {
        // 全都在场:不点名任何一个,只说怎么收窄。
        var all = NameCollision.Say("Space", 6, ["MapGeneratorDef"], []);
        Assert.Contains("6 defs share the name 'Space' across different def types", all, StringComparison.Ordinal);
        Assert.Contains("all of them are shown", all, StringComparison.Ordinal);

        var one = NameCollision.Say("Firefoam", 2, ["StatDef"], ["ThingDef"]);
        Assert.Contains("The other is a ThingDef, shown only without --type.", one, StringComparison.Ordinal);

        var many = NameCollision.Say("Space", 6, ["MapGeneratorDef"],
            ["GenStepDef", "RoomStatDef", "TerrainDef", "BiomeDef", "WorldObjectDef"]);
        Assert.Contains(
            "The others are GenStepDef, RoomStatDef, TerrainDef, BiomeDef, WorldObjectDef, shown only without --type.",
            many, StringComparison.Ordinal);

        // 类型名本身以 Def 收尾,尾缀再接一个名词就是「WorldObjectDef def」——
        // 两支都不许长出这个尾巴。名单本身也不许用 and 串联(五个类型串起来是
        // 「A and B and C and D and E」),而句尾那句固定话里的 and 不算。
        foreach (var line in new[] { one, many })
        {
            Assert.DoesNotContain("Def def", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Def and ", line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 显式点了名的快照,**当场**验名字 —— 不等到有人开库。
    ///
    /// 寻址是懒的,而懒寻址顺带把「这个名字对不对」也变懒了:<c>code-search</c> 只有印出来
    /// 的行里带 <c>.Translate()</c> 才碰快照,于是同一个查无此名的 <c>--snapshot vanilla</c>,
    /// 在 <c>search</c> 上是 exit 2 的硬错、在它上面一声不吭照常出结果。一个参数在两条命令
    /// 上两种命运,读的人只能得出「它在这条命令上生效了」。
    ///
    /// 判的是**两条命令给同一个答案**,不判措辞挂在哪个类上。
    /// </summary>
    [Fact]
    public void 查无此名的快照在碰不碰库的命令上给同一个答案()
    {
        foreach (var argv in new[]
                 {
                     new[] { "search", "shield", "--snapshot", "vanilla", Fixture.Pinned },
                     // 这一条从头到尾不需要快照 —— 正是懒寻址让它此前静默放行的那条路。
                     ["code-search", ": ThingComp", "--snapshot", "vanilla", Fixture.Pinned],
                     // 一个字都不查库的命令同样要认这道闸。
                     ["sources", "list", "--snapshot", "vanilla", Fixture.Pinned],
                 })
        {
            var (out_, err, code) = Fixture.Run(argv);
            Assert.Equal(2, code);
            Assert.Contains("No snapshot named 'vanilla'", err, StringComparison.Ordinal);
            // 名单要给出来:这个名字不在册,而「在册的是哪些」正是下一句要问的。
            Assert.Contains("Registered:", err, StringComparison.Ordinal);
            // 错的参数不许还印出一份看着正常的结果 —— 那正是此前 code-search 干的事。
            Assert.Equal("", out_);
        }
    }

    /// <summary>
    /// <c>--snapshot</c> 在 <c>code-search</c> 上一寸范围都不收,而这件事必须说破。
    ///
    /// <c>--snapshot vanilla</c> 与 <c>--source vanilla</c> 逐字同形,写前者的人要的正是后者;
    /// 而计数句尾巴上那句「across N source trees」是常规取景,不会纠正任何人。
    ///
    /// **这句否定不许跟着结果分支**:有命中、零命中、连快照都真被查过(印出来的行里有
    /// <c>.Translate()</c>)—— 三条路上「它没有收窄这次搜索」同样成立。
    /// </summary>
    [Fact]
    public void 快照参数收不窄代码搜索这件事在每条路上都说()
    {
        const string denial = "did not narrow this search";

        foreach (var argv in new[]
                 {
                     new[] { "code-search", ": ThingComp", "--snapshot", "core", Fixture.Pinned },
                     ["code-search", "zzzznothing", "--snapshot", "core", Fixture.Pinned],
                     // 快照真被查过的那条:key 解析拿它去查了,而搜索范围照旧一寸没收。
                     ["code-search", "Translate", "--snapshot", "core", Fixture.Pinned],
                     // 已经给了 --source 也照说:那时它只是碰巧不再需要,不是它生效了。
                     ["code-search", ": ThingComp", "--source", "vanilla", "--snapshot", "core", Fixture.Pinned],
                 })
        {
            var (text, _, _) = Fixture.Run(argv);
            Assert.Contains(denial, text, StringComparison.Ordinal);
            // 出路要指到真正的那个旋钮上,否则说破了也无处可去。
            Assert.Contains("--source is what picks among those", text, StringComparison.Ordinal);
        }

        // 没给 --snapshot 就一个字都不说 —— 恒真的横幅读到第五遍会把整个声明区训练成盲区。
        var (quiet, _, _) = Fixture.Run("code-search", ": ThingComp");
        Assert.DoesNotContain(denial, quiet, StringComparison.Ordinal);

        // --db 是路径,不进这道闸:混淆是**名字形状**的,一条路径不会被当成树名。
        var (byPath, _, _) = Fixture.Run("code-search", ": ThingComp", "--db", Fixture.Db);
        Assert.DoesNotContain(denial, byPath, StringComparison.Ordinal);
    }
}
