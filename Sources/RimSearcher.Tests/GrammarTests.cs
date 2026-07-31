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
            ["find", "compClass", "RimWorld.CompShield"],
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
    /// 上一条的另一半:没有可申报的边界时,完整结果集只有计数一句,一个字的散文都没有 ——
    /// 少了这一条,那条 boundary 就可能悄悄变成每次都挂的常驻声明。
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
        var (group, _, code) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "vanilla");
        Assert.Equal(0, code);
        Assert.Contains("--scope vanilla (= ludeon.rimworld)", group);

        // 写死 packageId:你写的就是你得到的,**播报那一行不多印**。钉的是「有没有一条
        // 以它开头的声明行」—— 计数句里那句 `1 def within --scope ludeon.rimworld.`
        // 是另一件事(用户侧收窄要念回去),不在这条闸的射程内。
        var (literal, _, _) = Fixture.Run("find", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld");
        Assert.DoesNotMatch(new Regex(@"^--scope ludeon\.rimworld", RegexOptions.Multiline), literal);

        // 零结果那一侧也要说,但**只说一遍** —— 两遍会被读成两条独立证据。
        var (miss, _, _) = Fixture.Run("find", "--value", "NoSuchValueXyz", "--scope", "vanilla");
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
    /// 同一句尾注的第二处:外层 COUNT 也要带 scope 谓词,否则「可能属于这里而没露面」
    /// 说的是一批 scope 明明排除掉的 def。
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
    /// 上下文窗口重叠时合并,同一行不许印两遍。判的是行号不重复,不是措辞。
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
        var (empty, _, emptyCode) = Fixture.Run("code-search", "public", "--files", "Verse/ThingComp.cs");
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
        var (starred, _, _) = Fixture.Run("code-search", "public", "--files", "*.zzz");
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
            ("BaseBullet",        "rimsearcher inherit BaseBullet", "find compClass"),
            // 存储桶的名字,不是一个 def
            ("ThingDef",          "is a def type in this snapshot", "find compClass"),
            // def 自己的运行时 class。MustNot 锚在那句**兜底话自己**的措辞上 ——
            // 算得出落点就不许退回猜。
            ("TestVariantDef",    "--class TestVariantDef",         "lists what kinds of def this snapshot holds"),
            // 字段取值(comps[N].compClass 那一类)
            ("CompShield",        "rimsearcher find compClass CompShield", "no class"),
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
                                       "--files", "*.cs", "--max-files", "1");
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
    /// 分页:总行数与下一页的参数恒在 —— 走不下去的第二页会把调用方逼回自己编正则。
    /// </summary>
    [Fact]
    public void 裸行读随时说得出总数与下一页()
    {
        var (page, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--lines", "7-12");
        Assert.Contains("of 30", page, StringComparison.Ordinal);
        Assert.Contains("--lines 13", page, StringComparison.Ordinal);

        // 一次读完时不许再劝人翻页 —— 那一句会被读成「后面还有」。
        // Widgets.cs 末尾有三行 .Translate() 语料(keyed 那一层的落点),所以是 12 行。
        var (whole, _, _) = Fixture.Run("read", "vanilla/Verse/Widgets.cs");
        Assert.Contains("all 12 lines", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("next page", whole, StringComparison.Ordinal);

        // 印刷上限咬下去时,接着读的那一段是算得出来的,就得给出来。
        var (capped, _, _) = Fixture.Run("read", "vanilla/Verse/Outline.cs", "--type", "Outer", "--limit", "4");
        Assert.Contains("--lines 9-29", capped, StringComparison.Ordinal);
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
    /// 分页的三个位置各说各的话:中间页说得出自己从第几条起,末页**不给**下一页的参数,
    /// 翻过了头是一句「翻过头了」而不是一句「没有这个东西」—— 分页给「缺席」添了一种
    /// 新成因,分不清就会报最强的那种。
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

    // ---- C# 声明默认值与被人设过的值不许同形 ----

    /// <summary>
    /// <c>--path</c> 点了名的字段绝不许因为「是默认值」而消失 —— 藏起来会把回答变成
    /// 「没有路径含 burstCount」,比印错值更彻底。
    /// </summary>
    [Fact]
    public void 点了名的字段不因为是默认值而消失()
    {
        var (named, _, code) = Fixture.Run("get", "Bullet_Revolver", "--path", "burstCount");
        Assert.Equal(0, code);
        Assert.Contains("projectile.burstCount", named, StringComparison.Ordinal);
        // 印出来还不够,还得说清它是哪一种 —— 只印值就与「有人设过」同形。
        Assert.Contains(FieldDefault.Column, named, StringComparison.Ordinal);
        Assert.Contains("yes", named, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--path</c> 筛空的两种成因不许同形:def 真没有这条路径,与**给进来的文本是个值**
    /// (stat 名装在 <c>statBases[N].stat</c> 里,按它筛路径必空)。
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
        var (find, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield", "--json");
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
    /// 子串匹配要留痕:`get X --path soundImpact` 只回 `soundImpactDefault`(语义相反的另一个
    /// 字段)时,得说出「你打的这个词作为完整的一段一次都没命中」。
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
        // 这句话不许收在关于存在性的强断言上 —— 「前缀式列举」是正常用法,要的字段就在
        // 下面那张表里,所以「这一行一条都没滤掉」这半句是承重的。
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
    /// 「本快照没有」在读的人眼里就是「这东西不存在」,所以 `find` 落空也要说破别处有。
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
        var (literal, _, _) = Fixture.Run("find", "--value", "True");
        Assert.DoesNotContain("If that is a class name", literal, StringComparison.Ordinal);

        var (cls, _, _) = Fixture.Run("find", "--value", "NoSuchCompClass");
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
        var (stdout, _, _) = Fixture.Run("find", "--value", "CompShield");

        var m = Regex.Match(stdout,
            @"Counted over indexed field paths only: [^']*?also hold (\d+) defs? that lost fields[^']*" +
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
        // 而 `find compClass CompShield` 是文档推荐的那条主查询,在它上面挂一句
        // 「同块还有 energyMax」是纯噪音。
        var (disc, _, _) = Fixture.Run("find", "compClass", "RimWorld.CompShield");
        Assert.DoesNotContain("same block as the rows above", disc, StringComparison.Ordinal);

        // 不带下标的层不算容器 —— 那是分类不是实例,兄弟太多且不成组,
        // 提示会退化成每次都挂的免责声明。
        Assert.Null(PathSegments.ContainerPrefix("projectile.damageAmountBase"));
        Assert.Equal("comps[0].", PathSegments.ContainerPrefix("comps[0].props.energyMax"));

        // 同块里没有别人设过的东西时,一个字都不说。
        var (quiet, _, _) = Fixture.Run("get", "TestModGun", "--path", "compClass");
        Assert.DoesNotContain("same block as the rows above", quiet, StringComparison.Ordinal);

        // 块名不许写死成 comps[N] —— ContainerPrefix 对任何带下标的层都成立
        // (statBases[8]、corePart.parts[6]、degreeDatas[0].statFactors[0] 都是块)。
        var (stat, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "statBases[0].stat");
        Assert.Contains("statBases[0]", stat.Split("same block")[1], StringComparison.Ordinal);
        Assert.DoesNotContain("comps[N]", stat, StringComparison.Ordinal);

        // 而且指的那条路要**填好**再发出去,不许留 <defName> / <block> 这种占位符。
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
    /// 于是 `comps[0]` 这种带下标的写法永远不可能等于任何一段,而命中明明全在那个块里。
    /// </summary>
    [Fact]
    public void 块级路径不许被判成子串误命中()
    {
        var (block, _, code) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "comps[0]");
        Assert.Equal(0, code);
        Assert.DoesNotContain("whole path segment", block, StringComparison.Ordinal);
        Assert.Contains("comps[0].props.energyMax", block, StringComparison.Ordinal);

        // 不带下标的裸名字照旧走整段判定 —— 只放过「本来就是块前缀」的写法。
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
    /// `--type` `--exact` `--path` 不在其中,于是那些查询报出一个字面完整的计数,
    /// 实则「在我自己划的范围内完整」。
    ///
    /// 三头都要钉:给了就念、没给就一个字不多、**而且不许念错东西** ——
    /// `get --type` 挑的是哪个 def 不是从字段里筛,念回去会被读成「去掉它还有更多字段」。
    /// 判据在声明层(OptionSpec.Narrows),不在这里。
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
    /// 于是 `find --value` 落空与「这东西真不存在」逐字同形,而 `search` 同一个词当场命中。
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
        Assert.Contains("--path description", clash, StringComparison.Ordinal);

        // 同名跨类型的那一对(ThingDef / StatDef 都叫 Firefoam)label 并不相同,不许误发。
        var (single, _, _) = Fixture.Run("search", "shield belt");
        Assert.DoesNotContain("carry the same label", single, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一次 `find` 的命中横跨几种**路径形状**,得当场说出来:`find stat Mass` 的上千行里
    /// 混着一行 `statFactors[N].stat`,其余是 `statBases[N].stat`,而默认视图下没人会逐行
    /// 核对 path 列 —— `find` 又恰恰是这套命令里用来做集合运算的那一个。
    ///
    /// 数的是**整个结果集**不是这一页。只有一种形状时一个字不说。
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
    /// `--json` 里那个数据键**恒在**,零行时是空数组,不是整个消失 —— 键不在时消费方拿到的
    /// 是 KeyError,而「翻过头了」「快照里没有」「工具崩了」在这份 JSON 上形状完全一样。
    /// 闸按**命令**逐条过各种零行成因,不只钉越界那一种。
    ///
    /// 反向也要钉:`find` 的两张表互斥,认领的那张有、另一张不许平白出现 ——
    /// 空数组在机器侧读作「查过了,没有」。
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
        var (byField, _, _) = Fixture.Run("find", "compClass", "zzznothing", "--json");
        using var f = System.Text.Json.JsonDocument.Parse(byField);
        Assert.False(f.RootElement.TryGetProperty("paths", out _));

        var (byValue, _, _) = Fixture.Run("find", "--value", "zzznothing", "--json");
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
    /// <c>--path</c> 重复给是**并集**,而计数句念回去的那几个必须都真的生效过 ——
    /// 只用第一个而把两个都念进「within --path A --path B」,输出与一个正确结果逐字同形。
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

        var onlyComps = Total("fields", "ThingDef", "--path", "comps", "--limit", "1");
        var onlyStats = Total("fields", "ThingDef", "--path", "statBases", "--limit", "1");
        var both = Total("fields", "ThingDef", "--path", "comps", "--path", "statBases", "--limit", "1");

        Assert.True(onlyComps > 0 && onlyStats > 0, "语料没覆盖到这两个 path,闸问不出话来。");
        Assert.True(both >= onlyComps && both >= onlyStats,
            $"--path comps --path statBases 的总数是 {both},而单独给是 {onlyComps} / {onlyStats} —— " +
            "并集比其中一项还小,说明第二个 --path 根本没生效。");
        Assert.True(both > Math.Min(onlyComps, onlyStats),
            "两个 --path 的并集与其中较小的那个一样大,第二个大概率被丢了。");
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
    /// <c>--placeholders</c> 在**取页之前**筛:页内 <c>Where(r =&gt; r.Placeholder)</c> 会把
    /// 「第一页里没有占位」说成「一条占位都没有」,而这个开关唯一的用途就是回答
    /// 「这批有没有没译的」,假阴性与真阴性逐字相同。
    ///
    /// 语料把唯一那条占位排在 2100 条的最末,页内筛必然摸不到它。
    /// </summary>
    [Fact]
    public void placeholders是在取页之前筛的()
    {
        var (json, _, code) = Fixture.Run("keyed", "filler", "--placeholders", "--limit", "5", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var keys = doc.RootElement.GetProperty("keys").EnumerateArray()
                      .Select(k => k.GetProperty("key").GetString()).ToList();
        Assert.Contains("FillerKey2099", keys);
        Assert.All(doc.RootElement.GetProperty("keys").EnumerateArray(),
                   k => Assert.True(k.GetProperty("placeholder").GetBoolean()));

        // 反向:落空那句话的分母是**过滤之前**的命中数,不是自己筛剩的零。
        var (miss, _, missCode) = Fixture.Run("keyed", "转至此处", "--placeholders");
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

        // 没配 mod_roots 时闭嘴 —— 这台机器上没有第二层可漏。
        var (quiet, _, _) = Fixture.Run("keyed", "CannotUseNoPower");
        Assert.DoesNotContain(Says, quiet, StringComparison.Ordinal);
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
        var (miss, _, mcode) = Fixture.Run("code-search", "--", "zzzznothing");
        Assert.Equal(1, mcode);
        Assert.Contains("2 of 3 source trees on disk", miss, StringComparison.Ordinal);
        Assert.Contains("the rest hold no file matching --files '*.cs'", miss, StringComparison.Ordinal);
        Assert.Contains("never been decompiled", miss, StringComparison.Ordinal);

        // 有命中的那句用同一个取景。
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
    /// <c>inherit --path</c> 用证人兄弟法回答「这个值是哪一层写的」(<c>get</c> 给的是合并后的
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
    /// 读起来是「一个兄弟都不同意」—— 而 0 既可以是量过为零,也可以是没量。
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
    /// 免责声明会被学着跳过。
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
        // 整库照旧有被截的 def,所以拿整库计数的实现在这一格红。
        var (clean, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass");
        Assert.DoesNotContain(Caveat, clean, StringComparison.Ordinal);
    }

    /// <summary>
    /// 证人表必须自己说破逆命题不成立。
    ///
    /// 「with_path 追平 other_defs」只与「这一层声明了它」相容,并不蕴含它 —— 每个后代
    /// 各写各的一份,印出来逐字相同(vanilla 的 <c>BaseBullet --path damageAmountBase</c>
    /// 是 61 of 61,而 61 个子弹各写各的伤害)。真正的证据是 same_value。
    /// </summary>
    [Fact]
    public void 证人表要说破全都带着并不等于这一层写的()
    {
        var (text, _, _) = Fixture.Run("inherit", "Bullet_Revolver", "--path", "thingClass");
        Assert.Contains("The converse does not hold", text, StringComparison.Ordinal);
        Assert.Contains("every descendant writing the field separately", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「快照与当前安装的游戏一致」这句话要自己说清**没比的是什么**:比的只有 mod 列表、
    /// 顺序、版本号,而改动 mod 内部的文件三样全都不变 —— 一句范围列全了的自我限定
    /// 照样会被读成「快照 = 现在的游戏数据」的背书。正面那半在场不算数,
    /// **没比的那半必须同时在场**。
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
    /// 披露句要跟着调用方**自己已经划掉**的那一维一起收 —— 在一张已经滤干净的表下面挂
    /// 一个针对别的类型的完整性告警,不是噪音而是错话,而一句被发现过期,其余每一句
    /// 都要被重新审视。
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
        Assert.Contains("Nothing above shows any field of these list entries", hidden, StringComparison.Ordinal);
        Assert.Contains("statBases[0]", hidden, StringComparison.Ordinal);

        // 不截时每个下标都露过面 —— 这时候要给正面那句话,不能沉默。
        var (whole, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("Every list index this def has does appear above", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing above shows any field", whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// 嵌套 <c>&lt;li Class="…"&gt;</c> 的运行时类型这一维,要按导出器版本分说:0.2.0 起
    /// 导出器给列表元素发一条 <c>&lt;path&gt;.Class</c>,而**老快照对 `find Class X` 回的那个零,
    /// 与「量过了、确实没人用它」逐字同形**。两个世界各要一个落点:主快照标 0.2.0,
    /// other 那份标 0.1.0。
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
        Assert.Contains("not that this def chose it", shared, StringComparison.Ordinal);
        Assert.Contains("soundImpactDefault (9)", shared, StringComparison.Ordinal);
        // 名单不许截 —— 截了的话「不在名单里」同时意味着两件事。
        var brackets = shared.Split("the count in brackets:")[1].Split('\n')[0];
        Assert.DoesNotContain("more", brackets, StringComparison.Ordinal);

        // 另一个类型上没有这种值 —— 这时候要明说没有,不能靠沉默。
        var (own, _, _) = Fixture.Run("get", "VariantOne");
        Assert.Contains("No value above is one that most of the", own, StringComparison.Ordinal);
        Assert.DoesNotContain("not that this def chose it", own, StringComparison.Ordinal);
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
    /// <c>--class</c> 与 <c>--offset</c> 只在给了 def 类型时才有意义,而它们仍然声明在这条
    /// 命令上 —— 「不给类型还传了它们」不许照单收下再悄悄不生效
    /// (同 <see cref="CommandContext.Limit"/> 那条静默夹紧)。
    ///
    /// 所以当场退 2,并且**说清该往哪走**:桶归属这个问题下面那条零行分流早就会答。
    /// </summary>
    [Fact]
    public void 不给def类型时不许悄悄吃掉class与offset()
    {
        foreach (var argv in new[] { new[] { "list", "--class", "TestVariantDef" },
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
        Assert.Contains("--class TestVariantDef", holder, StringComparison.Ordinal);
    }
}
