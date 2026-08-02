using System.Reflection;
using System.Text.RegularExpressions;
using RimSearcher.Cli;

namespace RimSearcher.Tests;

/// <summary>
/// skill 文档的**承诺闸**:验的是承诺的**语义**,不是命令行的存在性。
///
/// 立的是一张索引 —— 每条承诺句连着一道守它的闸,两个方向都被机器盯着:
///   <see cref="承诺句都还在原地"/>       原文被改写就红,逼人重新确认实现还兑不兑现;
///   <see cref="每条承诺都指名了守它的那道闸"/> 指的那道闸不存在就红(改名、删掉都算)。
///
/// 扫描面 = SKILL.md + references 下的手写页(cli-reference.md 是生成物,不在此列):
/// 2026-08-01 精简后,低频承诺句下沉进了 usage-notes.md —— 下沉不是删除,承诺照钉。
///
/// **往 skill 文档里加一句承诺,就要往 <see cref="Promises"/> 里加一行。** 这条规矩没法由
/// 机器强制 ——「哪句是承诺」不可判定。
/// </summary>
public class SkillPromiseTests
{
    /// <param name="Quote">skill 文档里的原文,空白折叠后逐字匹配(换行位置不算改动)。</param>
    /// <param name="ProvenBy">证它的那个测试方法名。必须在本程序集里真实存在。</param>
    private sealed record Promise(string Quote, string ProvenBy);

    private static readonly Promise[] Promises =
    [
        // ---- 数据边界 ----
        new("inheritance, discarded by the game before export; `inherit` alone reads it",
            nameof(继承层的来路与补丁时间差写在inherit自己的说明里)),
        // 2026-08-01 第九轮盲测证伪了原来钉在这里的那句「zero means what you see is what
        // the game read」:`Human` 声明了 Name= 因而报 patch_ops 0,而它的运行时类型是
        // `AlienRace.ThingDef_AlienRace` —— 被一条 defName 定向 patch 换掉了。
        // **0 只说明没有按 Name 命中的 patch**,而这道闸此前一直在守那句假话。
        new("patches that target a node **by `Name=`**",
            "inherit的patch计数在干净节点也在场"),
        new("A `0` is not evidence the def is unpatched",
            "inherit的patch计数在干净节点也在场"),
        new("reports `n/a` rather than `0`",
            "inherit的patch计数在干净节点也在场"),
        new("An abstract node shows no field values",
            "抽象节点不在defs里但在继承层里"),
        new("`get` cannot reach them (it says so)",
            "get落空时不再无条件谈抽象父节点"),

        // ---- 三态计数与分页 ----
        new("A count is always printed above the table",
            nameof(每条查询的第一句都是计数)),
        new("`12 defs.` — all of them",
            "完整集合裸写数字不多说一个字"),
        new("`at least 12 matches` — the scan stopped early, true total unknown",
            "只知道下界时写成at_least"),
        new("A `--path` match count is a filter, not a truncation (`kind: \"filter\"` vs `\"truncation\"` in `--json`)",
            nameof(过滤与截断在json里是两种kind)),
        new("Fields the exporter dropped are `kind: \"boundary\"` instead",
            nameof(导出期丢字段与本次分页截断在json里是两种kind)),
        // 消费侧提的原案是「第三方以 `Class=` 挂上去的会是 `no`」—— 用他们自己那张表就能
        // 证伪(第三方 comp 经 `Class=` 挂上去恰恰是 `yes`),而 vanilla 自己有一千多条 `no`。
        // 收下的是他们的观察(「normal state」把 `no` 推成异常),不是他们的规则。
        new("a `no` beside it is just as ordinary, reached by more than one route",
            nameof(class字段上的yes与no同时是常态)),
        new("The last page says it is the last one; an `--offset` past the end is reported as an overshoot, not as \"nothing found\"",
            "分页的三个位置各说各的话"),

        // ---- search / find / values 的分工 ----
        // 快照的 translations 表只有 def 侧的注入,Languages/*/Keyed 那一整套 UI 字符串
        // 一条都不在库里 —— 承诺必须限定成「注入到 def 上的译文」并明说不覆盖 Keyed。
        new("covers def names, labels, descriptions and the translations injected onto defs",
            nameof(search只认名字标签与译文而不认C类名)),
        new("the UI strings under `Languages/*/Keyed`",
            nameof(search只认名字标签与译文而不认C类名)),
        new("an English term finds its def on a Chinese snapshot",
            nameof(GrammarTests.英文原文在中文快照上搜得到)),
        new("gives the whole value space and prints which full paths and def types contributed",
            nameof(values说清这些值来自哪些路径与def类型)),

        // ---- 界面文案那一层 ----
        new("keyed translations belonging to no def, unreachable by `search`/`get`/`find`",
            nameof(界面文案不在search的射程里而keyed认它)),
        new("a zero result names which one you hit",
            nameof(界面文案不在search的射程里而keyed认它)),
        // 「every key written as a literal on a matching line」两处都太宽:触发条件是
        // `.Translate()` 收下的字面量,而覆盖面是**印出来的**行而非全部命中行。
        new("resolves the literal keys passed to `.Translate()` on the lines it prints and appends a `ui_text` table beside the hits",
            nameof(code_search把字面量key的译文当场解出来)),
        new("A key the code assembles at runtime (`\"Stat_\" + x`) has no literal to resolve, and the answer says how many lines were like that rather than leaving them blank",
            nameof(code_search把字面量key的译文当场解出来)),
        new("Only `in effect` rows are what the game displays",
            "收割的keyed标成非生效且同key不去重"),
        new("there are no keyed translations in the snapshot — `keyed` says that in those words instead of reporting your key absent",
            nameof(OutputSnapshotTests.keyed层为空时说破是快照的缘故而不是查不到)),

        // ---- 子串匹配与同块兄弟 ----
        new("the output says when nothing matched as a whole segment",
            nameof(GrammarTests.子串匹配要说破自己不是整段命中)),
        new("the output names hand-set fields in the block it cut away",
            nameof(GrammarTests.同一块里有人设过的兄弟字段要点名)),
        new("(narrow `--type`, `--def`), which the footnote prints already filled in",
            nameof(GrammarTests.完整性尾注指的命令要走得到它刚说的那批)),
        new("Global options (`--snapshot`, `--db`, `--json`, `--config`) go **after** the command name",
            nameof(GrammarTests.全局参数的位置约束要写在它自己的标题上)),
        // find --value --exact 在 skill 文档里没有专属句子,兜住它的是下面那条
        // 「Unknown options are rejected rather than ignored」。不给它单独立一行:
        // 能钉住的原文只有收窄表里孤零零一个 `--exact`,那样的 pin 无论实现怎么变都不会红。

        // ---- mod 列表:导出的**输入**那一侧 ----
        // 措辞取的是实现里那句窄的(SearchAll 的落空话),不是「这个 mod 在本机装了没」——
        // 两者差着这条命令根本没看过的一整个游戏目录。
        new("It answers **which saved lists name a mod** — not whether the mod is installed",
            nameof(GrammarTests.列表点没点名与快照覆没覆盖是两个问题)),

        // ---- 代码默认值 ----
        new("`yes` rows hide by default (a line says how many)",
            nameof(GrammarTests.默认值行被拿掉时当场说清有多少条)),
        new("`--path` always shows a named field",
            nameof(GrammarTests.点了名的字段不因为是默认值而消失)),
        new("`unknown` = type not constructible",
            nameof(GrammarTests.没法比的那一档照常显示且不与被改过的同形)),

        // ---- code-search 的三道闸 ----
        new("`--limit` and `--max-per-file` only shape what is printed (the count stays exact)",
            "印刷上限不缩短扫描"),
        new("A zero result from a scan that stopped short says so and does **not** point you at the snapshot",
            "没读完的零结果与真零结果分得开"),
        new("reports matches and files as two numbers",
            nameof(code的匹配数与文件数是两个数)),
        new("pointed at a data question it says so",
            nameof(code的零结果指路回快照而不是硬说没有)),
        new("A `--files` glob containing `/` starts at the tree name",
            nameof(files的glob语义在打不中时当场讲清)),

        // ---- read ----
        new("when a bare file name matches several files it lists them instead of picking",
            "同名文件不替调用方挑"),
        new("`--lines` together with `--member`/`--type` is a usage error rather than a silent preference",
            "两种读法同时传时当场说破"),
        // 这条替掉的原文是「`read --member` has no in-command window at all — read the count
        // line, then pipe」:一句 CLI 当场证伪得了的假话,而它是禁管道那段里**唯一授权
        // 去管道**的场合。第九轮盲测把它当论据、文档照搬,两边都没跑过一次。
        new("`--limit` caps the lines and the count line hands back the exact `--lines a-b` to resume from",
            nameof(GrammarTests.成员读的窗口是limit加印回来的续读区间)),
        new("match **braces, not C#**",
            "能力边界只挂在做了推断的那几条路上"),

        // ---- --json / 退出码 / 参数 ----
        new("every prose sentence moves into `notes` as `{kind, text}`",
            "json模式下声明区搬进notes一条不丢"),
        new("`<command> --help` lists each command's keys",
            nameof(skill列出的json键与声明一致)),
        // 越界 offset 时那个键不许整个消失 —— 消费方会拿到 KeyError 而不是空数组。
        new("always present when produced, empty array and all",
            "json的数据键零行时是空数组而不是整个消失"),
        new("`get`'s `source` line is a bare, unverified file name",
            "source列印的是没有目录的裸文件名"),
        new("`0` ran, `1` zero rows, `2` usage error, `70` tool defect",
            "退出码如实传给shell"),
        new("Unknown options are rejected rather than ignored, with the nearest accepted spelling — or, if another command takes that option, which one",
            nameof(未知选项的报错点名接受它的那条命令)),
        // 播报判据是「展开与你输入的字面不同」,不是「多于一个 mod」——
        // `--scope ludeon.rimworld` 展开成一个 mod 时也要播报。
        new("the output spells out what a scope resolved to",
            "scope在散文里展开成实际圈住的mod"),

        // ---- 落空的成因 ----
        new("A zero result names its own cause",
            "零结果按算得出来的落点分流"),
        // 同一条规矩在 find 上的样子。第二句是**不给死路**那一半:算出来是个 def 名不等于
        // 有人引用它,指一条必然空手的命令与不指路一样贵。
        new("given a single word that is not a field path, `find` works out what that word actually is",
            nameof(GrammarTests.find给一个词落空时要说破那个词其实是什么)),
        new("it says so instead of handing back a query that would come back empty",
            nameof(GrammarTests.find给一个词落空时要说破那个词其实是什么)),
        new("another registered snapshot holding the def is named in the zero result",
            "别的快照里有时点名说出来"),
        // 位置本身成了承诺:只扫表头的读法会漏掉沉到表下的那一条,而这句话就是在
        // 说破那个新的沉默形状 —— 于是它比大多数承诺更需要一道闸看着。
        new("It is repositioned, never suppressed",
            nameof(StalenessTests.漂移横幅点到那个mod时才占表头)),
        // 承诺的是一条**区分**能力,不是一列的存在:不印修饰符时那两行逐字同形,
        // 而「覆写了基类的什么」正是读轮廓的人最常从这里得出的结论。
        new("they point at opposite next steps",
            nameof(GrammarTests.轮廓分得出覆写与新引入并报出读的是哪个文件)),
        // 第九轮盲测证伪了这里原来那句「whole-segment suffix」:点路径是纯文本后缀,
        // 不在 `.` 上对齐。承诺改成实况的同时钉住新开关 —— 没有它这条说明只是个警告。
        new("a dotted one is raw text that does not stop", nameof(GrammarTests.点路径的后缀不在点上对齐而exactpath钉得住)),
        // 文档批补的四条可实测口径。每条都是「不这么以为就会拿错答案」的那种句子,
        // 而文档与实现是两处产地。
        new("is case-sensitive unless you pass `-i`", nameof(GrammarTests.skill那几条可实测的默认与口径逐条对得上)),
        new("it caps printed lines rather than narrowing a result set, so `--limit all` *widens*",
            nameof(GrammarTests.skill那几条可实测的默认与口径逐条对得上)),
        new("its `def_types`\n  row names them", nameof(GrammarTests.skill那几条可实测的默认与口径逐条对得上)),
        new("says where the def was declared, not who wrote the value you asked",
            nameof(GrammarTests.skill那几条可实测的默认与口径逐条对得上)),
        // 恒真的东西长得与铁证一模一样,而这一句是唯一说破它的地方。
        new("is no evidence at all for a field the whole def type carries",
            nameof(GrammarTests.抽象节点也给得出same_value并摆出恒真那一档的分母)),
        new("counts against the most common value under it, not against a value the node",
            nameof(GrammarTests.抽象节点也给得出same_value并摆出恒真那一档的分母)),
        new("`--exact-path` pins the whole path, with `[]` standing for any index",
            nameof(GrammarTests.点路径的后缀不在点上对齐而exactpath钉得住)),
        new("on a Core-only snapshot, `1 def` means one in Core",
            "被scope挡住时说破是过滤器干的"),

        // ---- 快照还等不等于磁盘 ----
        // 「比了哪几样」是一句会过时的话:2026-07-31 之前它写着三样,而实现已经是四样。
        // 每一样各连一道闸,少一样就红。
        new("Four things: same mods, same order, same game build",
            nameof(GrammarTests.一致这句话要同时说清没比的是什么)),
        new("It reports which mods moved, by name",
            "漂移声明点名到mod"),
        new("a re-download of identical bytes reads as a change, and an edit that preserves both is\nthe one case it misses",
            "量过了也要说清比的只是尺寸与时间戳"),
        // 2026-08-01 实测校对:原文钉的是 `xml_fingerprint: not recorded` —— 冒号那个形状
        // 输出里不存在(是对齐的列),而字面也被截短了。钉住的必须是渲染器真吐的那一串。
        new("`xml_fingerprint` as `not recorded (exported before this was measured)`",
            nameof(GrammarTests.一致这句话要同时说清没比的是什么)),
        new("names the source only in the fallback case",
            "版本来自ModsConfig时说破它会落后"),
        new("which the game only rewrites when you save a change on\nits mod list page",
            "版本来自ModsConfig时说破它会落后"),
    ];

    /// <summary>SKILL.md + references 下的手写页(生成的 cli-reference.md 除外),拼成一份扫。</summary>
    private static string SkillText()
    {
        var dir = Path.Combine(DeclarationTests.RepoRoot(), "skills", "rimsearcher");
        var files = new List<string> { Path.Combine(dir, "SKILL.md") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(dir, "references"), "*.md")
                                .Where(f => Path.GetFileName(f) != "cli-reference.md")
                                .OrderBy(f => f, StringComparer.Ordinal));
        return Collapse(string.Join("\n", files.Select(File.ReadAllText)));
    }

    /// <summary>空白折叠 —— 重新折行不是语义改动,不该让整张表红。</summary>
    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ");

    [Fact]
    public void 承诺句都还在原地()
    {
        var text = SkillText();
        var missing = Promises.Where(p => !text.Contains(Collapse(p.Quote), StringComparison.Ordinal))
                              .Select(p => p.Quote)
                              .ToList();
        Assert.True(missing.Count == 0,
            "These promises are no longer in the skill docs word for word. A reworded promise is a new promise: " +
            "check the gate still proves it, then update the quote.\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void 每条承诺都指名了守它的那道闸()
    {
        var facts = typeof(SkillPromiseTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var dangling = Promises.Where(p => !facts.Contains(p.ProvenBy)).ToList();
        Assert.True(dangling.Count == 0,
            "These promises name a gate that does not exist:\n  " +
            string.Join("\n  ", dangling.Select(p => $"{p.ProvenBy}  ←  \"{p.Quote}\"")));
    }

    // ---- 上表点名、而别处没有的那几道 ----

    /// <summary>
    /// 「count is **always** there」是最强的一句承诺:输出的第一行永远是一句计数,
    /// 于是「静默」在这套工具里不构成一种回答。判据是三态文法的形状,不是某个具体措辞。
    /// </summary>
    [Fact]
    public void 每条查询的第一句都是计数()
    {
        string[][] queries =
        [
            ["search", "shield"], ["list", "ThingDef"], ["get", "Apparel_ShieldBelt"],
            ["find", "compClass", "RimWorld.CompShield"], ["values", "thingClass"],
            ["fields", "ThingDef"], ["list"], ["mods"], ["inherit", "BaseBullet"],
            // 界面文案那一层此前不在这份名单里,而它恰是全仓唯一一处把分页句 Add 在表
            // 之后的命令 —— 名单漏了谁,谁就可以一直反着来。
            ["keyed", "CannotUseNoPower"], ["keyed", "--limit", "3"],
        ];

        // 「N noun」/「N of M noun」/「at least N noun」—— 三态各自的开头形状。
        var counted = new Regex(@"^(at least \d+|\d+( of \d+)?) [a-z]", RegexOptions.None);

        foreach (var argv in queries)
        {
            var what = string.Join(' ', argv);
            var (stdout, _, _) = Fixture.Run(argv);
            var lines = stdout.Split('\n');

            var countAt = Array.FindIndex(lines, counted.IsMatch);
            Assert.True(countAt >= 0, $"'{what}' printed no count at all.");

            // 承诺的原话是「a plain table with a count **above it**」—— 判的是计数与表的
            // 相对位置,不是「输出的第一行」。两者此前分不开,是因为渲染器把全部声明无条件
            // 提到最前,那时候每条命令的第一行必然是声明;现在 get 先端出这个 def 自己、
            // values 先端出概览,表都在后面,而承诺照旧成立。
            //
            // 表头从 --json 的列名反查,不靠猜文本形状:详情块渲染出来也是「名字 空格 值」。
            var (json, _, _) = Fixture.Run([.. argv, "--json"]);
            var columns = FirstTableColumns(System.Text.Json.JsonDocument.Parse(json).RootElement);
            Assert.True(columns is not null, $"'{what}' produced no table.");

            // 文本侧的表头是 JSON 列名的**子集**:整列同值的列被折进表上方那一行了。
            // 第一列永不折(它是行的身份),所以拿它锚定,其余的词只要求出自列名。
            var wanted = string.Join(' ', columns!);
            var header = Array.FindIndex(lines, l =>
            {
                var words = Regex.Replace(l, @"\s+", " ").Trim().Split(' ');
                return words[0] == columns[0] && words.All(columns.Contains);
            });
            Assert.True(header >= 0, $"'{what}' has columns [{wanted}] in --json but no such header in the text.");
            Assert.True(countAt < header,
                $"'{what}' puts its count below the table: count on line {countAt + 1}, header on line {header + 1}.");
        }
    }

    /// <summary>
    /// 这份输出里第一张**表**的列名。
    ///
    /// 「对象数组」还不够判:<c>defs</c> 那种集合也是对象数组,而它的每一项装的是别的块。
    /// 表的判据是行里全为标量。
    /// </summary>
    private static string[]? FirstTableColumns(System.Text.Json.JsonElement el)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Array:
                if (el.GetArrayLength() == 0) return null;
                if (el[0].ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                if (el[0].EnumerateObject().All(p => p.Value.ValueKind
                        is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)))
                    return el[0].EnumerateObject().Select(p => p.Name).ToArray();
                foreach (var item in el.EnumerateArray())
                    if (FirstTableColumns(item) is { } nested) return nested;
                return null;

            case System.Text.Json.JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    // 声明区不是数据 —— 它也是对象数组,形状与表逐字同构。
                    if (p.Name == "notes") continue;
                    if (FirstTableColumns(p.Value) is { } nested) return nested;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 「我主动筛掉的」与「结果被截了」在机器侧必须是两种 kind —— 混用会让扫 notes 的
    /// 下一位把一次精确提问读成「结果不完整」,或者反过来把截断读成自己要的过滤。
    /// </summary>
    [Fact]
    public void 过滤与截断在json里是两种kind()
    {
        var (filtered, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--path", "comps", "--json");
        Assert.Contains("\"kind\": \"filter\"", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\": \"truncation\"", filtered, StringComparison.Ordinal);

        var (cut, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--limit", "1", "--json");
        Assert.Contains("\"kind\": \"truncation\"", cut, StringComparison.Ordinal);
    }

    /// <summary>
    /// class 字段上 `yes` 与 `no` **同时**是常态。守的是「`no` 是异常」这个读法 ——
    /// 现文原来只说 `yes` is their normal state,而正常两个字反着念就是「`no` 不正常」,
    /// 于是每一条 `no` 都像个发现。真快照上 vanilla 自己就有一千多条 `no`,零第三方参与。
    ///
    /// 闸只证得了「两种取值在同一条路径上共存」,证不了成因 —— 成因确实不止一条
    /// (裸 `&lt;compClass&gt;` 挂在基类 `CompProperties` 上是一条;`Class=` 点了名而那个
    /// 子类的构造函数不供 compClass 是另一条),所以句子里写的是「不止一条路」而不是
    /// 枚举。**别把这道闸升级成对成因的断言** —— 那正是这句话上一次写错的方式。
    /// </summary>
    [Fact]
    public void class字段上的yes与no同时是常态()
    {
        var (json, _, _) = Fixture.Run("find", "compClass", "--limit", "all", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var defaults = doc.RootElement.GetProperty("matches").EnumerateArray()
            .Select(r => r.GetProperty("code_default").GetString())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("yes", defaults);
        Assert.Contains("no", defaults);
    }

    /// <summary>
    /// **导出期丢掉的字段不是 `truncation`**,是 `boundary` —— `truncation` 专给这一次查询
    /// 自己截的那一刀。区分是刻意的(一个是数据的边界,另一个是本次分页),但它把一条
    /// 现成的体检做法变成了陷阱:照「Truncation notes carry kind truncation」写
    /// `notes | where kind=="truncation"` 去筛截断,恰好漏掉唯一那种**答案会因此错**的截断
    /// —— 分页截断你自己下的命令就知道,导出截断只有这条 note 会说。
    /// </summary>
    [Fact]
    public void 导出期丢字段与本次分页截断在json里是两种kind()
    {
        // Bullet_Revolver 在语料里带着 fields_truncated=3。
        var (json, _, _) = Fixture.Run("get", "Bullet_Revolver", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var dropped = doc.RootElement.GetProperty("notes").EnumerateArray()
            .Where(n => n.GetProperty("text").GetString()!
                         .Contains("exporter stopped short", StringComparison.Ordinal))
            .ToList();

        Assert.True(dropped.Count == 1, $"Expected exactly one export-truncation note, got {dropped.Count}.");
        Assert.Equal("boundary", dropped[0].GetProperty("kind").GetString());
    }

    /// <summary>
    /// search 认名字/标签/译文,不认 C# 类名。这句话若不成立,读者会拿 `search CompShield`
    /// 的零结果当成「游戏里没有这个类」—— 而它其实是问错了地方。
    /// </summary>
    [Fact]
    public void search只认名字标签与译文而不认C类名()
    {
        var (byName, _, nameCode) = Fixture.Run("search", "shield");
        Assert.Equal(0, nameCode);
        Assert.Contains("Apparel_ShieldBelt", byName, StringComparison.Ordinal);

        // 语料里 comps[0].compClass = RimWorld.CompShield,而 search 照样查不到它。
        var (byClass, _, classCode) = Fixture.Run("search", "CompShield");
        Assert.Equal(1, classCode);
        Assert.DoesNotContain("Apparel_ShieldBelt", byClass, StringComparison.Ordinal);
        // 只说「没有」不够:得把该去哪儿问说出来,否则这条承诺帮不到任何人。
        Assert.Contains("find", byClass, StringComparison.Ordinal);

        // 快照的 translations 表只有 def 侧的注入(def_type + def_name + field 三列),
        // Languages/*/Keyed 那一整套 UI 字符串一条都进不来 —— schema 级的硬边界,
        // 不是覆盖率问题。
        Assert.Contains("Languages/*/Keyed", byClass, StringComparison.Ordinal);
        Assert.Contains("injected onto defs", byClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// 界面文案那一层与 def 无关,于是 search / get / find 三条路原理上都到不了它 ——
    /// 而「到不了」的样子与「游戏里没有这句话」天然同形。
    ///
    /// 两个方向都验:search 打进一句界面文案确实落空(而且落空时**点名**说出该问 keyed),
    /// 而 keyed 认它。
    /// </summary>
    [Fact]
    public void 界面文案不在search的射程里而keyed认它()
    {
        // 语料里 CannotUseNoPower 的译文是「没有电力」,而它不是任何 def 的 label。
        var (bySearch, _, searchCode) = Fixture.Run("search", "没有电力");
        Assert.Equal(1, searchCode);
        // 落空的那一句必须把落点算出来,而不是停在「不覆盖」。
        Assert.Contains("rimsearcher keyed", bySearch, StringComparison.Ordinal);
        Assert.Contains("interface text", bySearch, StringComparison.Ordinal);

        var (byKeyed, _, keyedCode) = Fixture.Run("keyed", "没有电力");
        Assert.Equal(0, keyedCode);
        Assert.Contains("CannotUseNoPower", byKeyed, StringComparison.Ordinal);

        // 反方向:拿 key 问也认,而且给出的是游戏真会显示的那一句。
        var (byKey, _, keyCode) = Fixture.Run("keyed", "CannotUseNoPower");
        Assert.Equal(0, keyCode);
        Assert.Contains("没有电力", byKey, StringComparison.Ordinal);
        Assert.Contains("in effect", byKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「代码里这行显示的是什么字」是 code-search 最常见的下一问,而它是当场解得出来的:
    /// 97% 的调用点把 key 写成紧邻的字面量。解不出来的那些(运行时拼出来的 key)必须
    /// **报数**而不是留白 —— 留白与「这一行没有 key」同形。
    /// </summary>
    [Fact]
    public void code_search把字面量key的译文当场解出来()
    {
        var (stdout, _, code) = Fixture.Run("code-search", "Translate");
        Assert.Equal(0, code);

        // 解出来的那一条:key 与它的两侧文本都在。
        Assert.Contains("ui_text", Fixture.Run("code-search", "Translate", "--json").Stdout, StringComparison.Ordinal);
        Assert.Contains("没有电力", stdout, StringComparison.Ordinal);

        // 语料里另外两行是故意留的两种解不出来:语言文件里没有这个 key,以及 key 是拼出来的。
        Assert.Contains("no keyed translation for", stdout, StringComparison.Ordinal);
        Assert.Contains("not a literal", stdout, StringComparison.Ordinal);

        // 关得掉,而且关掉之后一个字都不多说。
        var (off, _, _) = Fixture.Run("code-search", "Translate", "--no-ui-text");
        Assert.DoesNotContain("ui_text", off, StringComparison.Ordinal);
        Assert.DoesNotContain("没有电力", off, StringComparison.Ordinal);
    }

    /// <summary>
    /// 值域计数没有产地就是负资产:「out of 207 values」被读成「值的形态有讲究」,
    /// 而真因往往是一个裸字段名同时命中了几条不相干的路径。
    /// </summary>
    [Fact]
    public void values说清这些值来自哪些路径与def类型()
    {
        var (stdout, _, code) = Fixture.Run("values", "compClass");
        Assert.Equal(0, code);
        Assert.Contains("matched_paths", stdout, StringComparison.Ordinal);
        Assert.Contains("comps[0].compClass", stdout, StringComparison.Ordinal);
        Assert.Contains("def_types", stdout, StringComparison.Ordinal);
        Assert.Contains("ThingDef", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 「多少条命中」与「读了多少文件」是两个数。混成一个,问「有多少个方法长这样」的人
    /// 会拿到文件数当答案 —— 而两者都是合法的数字,错的那个不长得像错的。
    /// </summary>
    [Fact]
    public void code的匹配数与文件数是两个数()
    {
        var (stdout, _, _) = Fixture.Run("code-search", "class");
        Assert.Matches(@"\d+ matches? in \d+ files?", stdout);
    }

    /// <summary>
    /// 拿 code-search 问数据问题,答案是「这里不搜 XML,去 search / find」而不是一句
    /// 干巴巴的没有 —— 后者与「这个东西不存在」完全同形。
    /// </summary>
    [Fact]
    public void code的零结果指路回快照而不是硬说没有()
    {
        var (stdout, _, code) = Fixture.Run("code-search", "zzznosuchsymbolanywhere");
        Assert.Equal(1, code);
        Assert.Contains("rimsearcher search", stdout, StringComparison.Ordinal);
        Assert.Contains("rimsearcher find", stdout, StringComparison.Ordinal);
        Assert.Contains("XML", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// --files 的 glob 是相对反编译根匹配的,于是带 `/` 的写法必须以树名打头。这条规则
    /// 只写在文档里没用 —— 打不中的那一刻它必须在输出里,那才是有人需要它的时候。
    /// </summary>
    [Fact]
    public void files的glob语义在打不中时当场讲清()
    {
        var (stdout, _, code) = Fixture.Run("code-search", "class", "--files", "zzznotree/**");
        Assert.Equal(1, code);
        Assert.Contains("relative to the decompiled root", stdout, StringComparison.Ordinal);
        Assert.Contains("sources list", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// 猜错一个开关名要一行就能纠正。「另一条命令接受它」比「拼写接近」更有用 ——
    /// 前者说的是「你走错门了」,后者只说「这个门没有」。
    /// </summary>
    [Fact]
    public void 未知选项的报错点名接受它的那条命令()
    {
        var (_, stderr, code) = Fixture.Run("list", "ThingDef", "--member", "foo");
        Assert.Equal(2, code);
        Assert.Contains("read", stderr, StringComparison.Ordinal);
        Assert.Contains("list", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// skill 文档里那份 --json 数据键清单与声明层是两处产地。两个方向都验:文档里出现的键
    /// 必须真被声明过,而 skill 教的那几条命令声明的键必须都在文档里 —— 后者漏了,
    /// 猜错键就静默拿到空数组。清单 2026-08-01 起住在 usage-notes.md 的专节里。
    /// </summary>
    [Fact]
    public void skill列出的json键与声明一致()
    {
        var registry = new CommandRegistry();
        var text = File.ReadAllText(Path.Combine(DeclarationTests.RepoRoot(),
                                                 "skills", "rimsearcher", "references", "usage-notes.md"))
                       .Replace("\r\n", "\n");

        var paragraph = Regex.Match(text, @"## `--json` data keys per command.*?(?=\n## )",
                                    RegexOptions.Singleline);
        Assert.True(paragraph.Success, "The --json section in usage-notes.md was not found; the scanner needs updating.");

        var commandNames = registry.Specs
            .SelectMany(s => new[] { s.Name }.Concat(s.Aliases))
            .ToHashSet(StringComparer.Ordinal);
        var declared = registry.Specs.SelectMany(s => s.JsonKeys.Select(k => k.Key))
                                     .ToHashSet(StringComparer.Ordinal);
        // notes 与行内的行结构键不是顶层数据键,但同一段里就摆着 —— 它们属于这段话本身。
        string[] structural = ["notes", "kind", "text", "file", "line", "is_match", "group", "--json", "--help"];

        var mentioned = Regex.Matches(paragraph.Value, @"`([^`]+)`").Select(m => m.Groups[1].Value)
            .Where(t => Regex.IsMatch(t, "^[a-z_]+$"))
            .Where(t => !structural.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

        // 文档里出现的、既不是命令名也不是结构键的词,必须是某条命令声明过的数据键。
        var invented = mentioned.Where(t => !declared.Contains(t) && !commandNames.Contains(t)).ToList();
        Assert.True(invented.Count == 0,
            "The --json paragraph names keys that no command declares: " + string.Join(", ", invented));

        // 反向:SKILL 教的那几条命令,声明的每个键都得在这段话里。
        string[] taught = ["search", "list", "get", "find", "values", "fields", "mods", "inherit",
                           "keyed", "read", "code-search"];
        var undocumented = registry.Specs
            .Where(s => taught.Contains(s.Name))
            .SelectMany(s => s.JsonKeys.Select(k => (s.Name, k.Key)))
            .Where(x => !mentioned.Contains(x.Key))
            .ToList();
        Assert.True(undocumented.Count == 0,
            "These commands declare a --json key that the SKILL paragraph never names: " +
            string.Join(", ", undocumented.Select(x => $"{x.Name}.{x.Key}")));
    }

    /// <summary>
    /// 「inherit 是唯一读 XML 的那条,而且它自己说」—— 说在哪儿:命令自己的说明里
    /// (来路 + 补丁时间差),以及每次输出里那个 patch_ops 数。两处都验,少一处这句话就落空。
    /// </summary>
    [Fact]
    public void 继承层的来路与补丁时间差写在inherit自己的说明里()
    {
        var spec = new CommandRegistry().Specs.Single(s => s.Name == "inherit");
        var remarks = spec.Remarks ?? "";
        Assert.Contains("XML", remarks, StringComparison.Ordinal);
        Assert.Contains("PatchOperation", remarks, StringComparison.Ordinal);

        // 别的命令不许也声称自己读 XML —— 「唯一」这个词是这句承诺的重点。
        var others = new CommandRegistry().Specs
            .Where(s => s.Name != "inherit" && (s.Remarks ?? "").Contains("mods' XML", StringComparison.Ordinal))
            .Select(s => s.Name).ToList();
        Assert.True(others.Count == 0, "These commands also claim to read the mods' XML: " + string.Join(", ", others));

        var (stdout, _, _) = Fixture.Run("inherit", "BaseBullet");
        Assert.Contains("patch_ops", stdout, StringComparison.Ordinal);
    }
}
