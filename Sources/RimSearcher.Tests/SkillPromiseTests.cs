using System.Reflection;
using System.Text.RegularExpressions;
using RimSearcher.Cli;

namespace RimSearcher.Tests;

/// <summary>
/// SKILL.md 的**承诺闸**。
///
/// 三轮的方法论一条:第二轮给 SKILL.md 立的闸只验命令行**存在性**(每条 `rimsearcher …`
/// 对着 CommandRegistry 验),不验**承诺的语义**。于是那一轮四条 skill_doc 全是同一个形状 ——
/// 文档承诺了、实现没做到:R4 把闸门数成两道(封闭列举语气,实际有第三道)、
/// R6「zero means what you see is what the game read」(干净节点根本不打印那个零)、
/// R11「Unknown options are rejected rather than ignored」(只覆盖选项**名**)、
/// R14「nothing is lost」(--json 键名无文档,猜错静默返回空数组)。
///
/// 所以这里立的是一张**索引**:每条承诺句连着一道守它的闸,两个方向都被机器盯着 ——
///   <see cref="承诺句都还在原地"/>       原文被改写就红,逼人重新确认实现还兑不兑现;
///   <see cref="每条承诺都指名了守它的那道闸"/> 指的那道闸不存在就红(改名、删掉都算)。
///
/// **往 SKILL.md 里加一句承诺,就要往 <see cref="Promises"/> 里加一行。** 这条规矩没法由
/// 机器强制(「哪句是承诺」不可判定),但漏掉的代价写在上面那四条里了。
/// </summary>
public class SkillPromiseTests
{
    /// <param name="Quote">SKILL.md 里的原文,空白折叠后逐字匹配(换行位置不算改动)。</param>
    /// <param name="ProvenBy">证它的那个测试方法名。必须在本程序集里真实存在。</param>
    private sealed record Promise(string Quote, string ProvenBy);

    private static readonly Promise[] Promises =
    [
        // ---- 数据边界 ----
        new("`inherit` is the only command that reads it, and it says so",
            nameof(继承层的来路与补丁时间差写在inherit自己的说明里)),
        // 五轮:原承诺是「each named node reports … zero means what you see is what the game read」,
        // 而无 Name= 的节点拿到的是导出器硬写的 0 —— 承诺照字面读在那些 def 上是假的。
        // 收窄成「声明了 Name= 的才报数」并把另一半明说成 n/a,两半各由那道闸的两组断言证。
        new("reports how many patch operations target it by name — zero means what you see is what\n   the game read",
            "inherit的patch计数在干净节点也在场"),
        new("A node without a `Name=` reports `n/a`, not zero",
            "inherit的patch计数在干净节点也在场"),
        new("an abstract node has no field values of its own here",
            "抽象节点不在defs里但在继承层里"),
        new("`get` recognises a name that lives only there and says so rather than reporting it absent",
            "get落空时不再无条件谈抽象父节点"),

        // ---- 三态计数与分页 ----
        new("Results are a plain table with a count above it, and the count is **always** there",
            nameof(每条查询的第一句都是计数)),
        new("`12 defs.` — that is all of them",
            "完整集合裸写数字不多说一个字"),
        new("`at least 12 matches` — the scan stopped early; the true total is unknown",
            "只知道下界时写成at_least"),
        new("in `--json` those carry `kind: \"filter\"` while a real cut-off carries `kind: \"truncation\"`",
            nameof(过滤与截断在json里是两种kind)),
        new("The last page says it is the last one rather than leaving you to do the arithmetic, and an `--offset` past the end is reported as an overshoot, not as \"nothing found\"",
            "分页的三个位置各说各的话"),

        // ---- search / find / values 的分工 ----
        // 五轮:原承诺写「translations」不带限定,而快照的 translations 表是 def 侧的注入,
        // Languages/*/Keyed 那一整套 UI 字符串一条都不在库里 —— 「收获这个 UI 词对应什么」
        // 照字面读会被当成查得到的问题。收窄成「注入到 def 上的译文」并把 Keyed 明说成不覆盖。
        new("It covers def names, labels, descriptions and the translations injected onto defs — **not C# class names**, and **not the UI strings under `Languages/*/Keyed`**",
            nameof(search只认名字标签与译文而不认C类名)),
        new("an English term still finds its def on a\n   Chinese snapshot",
            nameof(GrammarTests.英文原文在中文快照上搜得到)),
        new("`values <field>` gives the whole value space, and prints which full paths and def types contributed",
            nameof(values说清这些值来自哪些路径与def类型)),

        // ---- 五轮:子串匹配与同块兄弟 ----
        new("The output says when nothing matched as a whole path segment",
            nameof(GrammarTests.子串匹配要说破自己不是整段命中)),
        new("the output names any hand-set field in the same `comps[N]` block as the rows it printed",
            nameof(GrammarTests.同一块里有人设过的兄弟字段要点名)),
        new("takes `--type` and `--def` to narrow to the\nones a particular answer depended on. The footnote on such an answer prints that command already\nfilled in",
            nameof(GrammarTests.完整性尾注指的命令要走得到它刚说的那批)),
        new("The global options (`--snapshot`, `--db`, `--json`, `--config`) go **after** the command name",
            nameof(GrammarTests.全局参数的位置约束要写在它自己的标题上)),
        // R11 那处唯一既成的静默吞掉(find --value --exact)在 SKILL 里没有专属句子,
        // 兜住它的是下面那条「Unknown options are rejected rather than ignored」。
        // 这里不给它单独立一行:能钉住的原文只有收窄表里孤零零一个 `--exact`,
        // 那样的 pin 无论实现怎么变都不会红 —— 一道红不了的闸比没有更坏,它看起来像覆盖。

        // ---- R1:代码默认值 ----
        new("`yes` rows are left out of the listing by default, with a line saying how many and how to see them",
            nameof(GrammarTests.默认值行被拿掉时当场说清有多少条)),
        new("`--path <text>` always shows a named field whichever kind it is",
            nameof(GrammarTests.点了名的字段不因为是默认值而消失)),
        new("`unknown` means the type could not be constructed for comparison, so neither claim holds",
            nameof(GrammarTests.没法比的那一档照常显示且不与被改过的同形)),

        // ---- code-search 的三道闸 ----
        new("`--limit` and `--max-per-file` decide how many matching lines are *printed*, and neither shortens the scan",
            "印刷上限不缩短扫描"),
        new("A zero result from a scan that stopped short says so and does **not** point you at the snapshot",
            "没读完的零结果与真零结果分得开"),
        new("`code-search` also reports matches and files as two different numbers",
            nameof(code的匹配数与文件数是两个数)),
        new("Pointing it at data questions returns nothing and tells you so",
            nameof(code的零结果指路回快照而不是硬说没有)),
        new("a glob with a `/` in it starts with the tree's name",
            nameof(files的glob语义在打不中时当场讲清)),

        // ---- read ----
        new("when a bare file name matches several files it lists them instead of picking",
            "同名文件不替调用方挑"),
        new("`--lines` together with `--member`/`--type` is a usage error rather than a silent preference",
            "两种读法同时传时当场说破"),
        new("It finds a declaration's end by matching braces, not by parsing C#, and says so on the paths where that inference happens",
            "能力边界只挂在做了推断的那几条路上"),

        // ---- --json / 退出码 / 参数 ----
        new("every prose sentence moves into `notes` as `{kind, text}`",
            "json模式下声明区搬进notes一条不丢"),
        new("Do not guess: `<command> --help` lists that command's keys",
            nameof(skill列出的json键与声明一致)),
        // 第六轮:越界 offset 时那个键整个消失,消费方拿到 KeyError 而不是空数组。
        new("The key the command does produce is always there, empty array and all",
            "json的数据键零行时是空数组而不是整个消失"),
        // 第六轮:C11 与 C41 各自浪费一轮 —— 文档从不承认这一列,而它一直在印文件名。
        new("`get` does print a `source` line, and it is less than it looks: the bare file name the game reported, no directory, unverified",
            "source列印的是没有目录的裸文件名"),
        new("Exit codes carry four distinct meanings",
            "退出码如实传给shell"),
        new("Unknown options are rejected rather than ignored, with the nearest accepted spelling — or, if another command takes that option, which one",
            nameof(未知选项的报错点名接受它的那条命令)),
        new("the output spells out what a scope resolved to whenever it is more than one mod",
            "scope在散文里展开成实际圈住的mod"),

        // ---- 落空的成因 ----
        new("A zero result names its own cause",
            "零结果按算得出来的落点分流"),
        new("If another registered snapshot has that def, the zero result says so by name",
            "别的快照里有时点名说出来"),
        new("If the snapshot covers Core only and your game has mods enabled",
            "被scope挡住时说破是过滤器干的"),
    ];

    private static string SkillText()
    {
        var path = Path.Combine(DeclarationTests.RepoRoot(), "skills", "rimsearcher", "SKILL.md");
        return Collapse(File.ReadAllText(path));
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
            "These promises are no longer in SKILL.md word for word. A reworded promise is a new promise: " +
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
            ["fields", "ThingDef"], ["types"], ["mods"], ["inherit", "BaseBullet"],
        ];

        // 「N noun」/「N of M noun」/「at least N noun」—— 三态各自的开头形状。
        var counted = new Regex(@"^(at least \d+|\d+( of \d+)?) [a-z]", RegexOptions.None);

        foreach (var argv in queries)
        {
            var (stdout, _, _) = Fixture.Run(argv);
            var first = stdout.Split('\n')[0];
            Assert.True(counted.IsMatch(first),
                $"'{string.Join(' ', argv)}' opens with \"{first}\", which is not a count.");
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

        // 「译文」这个词不带限定就在超发。快照的 translations 表只有 def 侧的注入
        // (def_type + def_name + field 三列),Languages/*/Keyed 那一整套 UI 字符串
        // 一条都进不来 —— 这是 schema 级的硬边界,不是覆盖率问题。
        Assert.Contains("Languages/*/Keyed", byClass, StringComparison.Ordinal);
        Assert.Contains("injected onto defs", byClass, StringComparison.Ordinal);
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
    /// SKILL 把 --json 的数据键当场列了一遍(R14 的修法),而那份清单与声明层是两处产地。
    /// 两个方向都验:文档里出现的键必须真被声明过,而 SKILL 教的那几条命令声明的键
    /// 必须都在文档里 —— 后者才是 R14 的形状(加了个键,文档没跟上,猜错静默拿到空数组)。
    /// </summary>
    [Fact]
    public void skill列出的json键与声明一致()
    {
        var registry = new CommandRegistry();
        var text = File.ReadAllText(Path.Combine(DeclarationTests.RepoRoot(), "skills", "rimsearcher", "SKILL.md"))
                       .Replace("\r\n", "\n");

        var paragraph = Regex.Match(text, @"`--json` gives machine-readable output:.*?(?=\n\n)",
                                    RegexOptions.Singleline);
        Assert.True(paragraph.Success, "The --json paragraph in SKILL.md was not found; the scanner needs updating.");

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
        string[] taught = ["search", "list", "get", "find", "values", "fields", "types", "mods", "inherit",
                           "read", "code-search"];
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
