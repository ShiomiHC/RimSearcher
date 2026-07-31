using System.Text;

namespace RimSearcher.Tests;

/// <summary>
/// 字节级基线(04 的主闸)。跑一批固定调用,把 stdout 逐字节钉进 <c>Snapshots/</c>。
///
/// 这道闸看的是**输出契约**,不是某个断言想到的那几条性质。措辞、列宽、声明区位置、
/// 空行、行尾 —— 凡是调用方读得到的东西,改动都会在这里变红。它抓得住的正是断言抓不住的
/// 那一类回归:「谁也没想到要断言的那句话被顺手改了」。
///
/// 基线不对时用 <c>RIMSEARCHER_UPDATE_SNAPSHOTS=1 dotnet test</c> 重写,然后**读 diff**。
/// 重写是一个动作,不是一个默认值 —— 默认重写等于没有闸。
/// </summary>
[Collection(Collection)]
public class OutputSnapshotTests
{
    /// <summary>
    /// 每条基线都是一次真实调用。名字既是文件名也是这条用例在说什么。
    /// 覆盖面按 07 的真实意图分布挑:查名字、看细节、反查、值域、代码搜索、报错路径。
    /// </summary>
    public static TheoryData<string, string[]> Cases => new()
    {
        { "search-hit",            ["search", "shield"] },
        { "search-miss",           ["search", "zzzznothing"] },
        { "search-miss-classlike", ["search", "CompShield"] },
        // R8 的四种误诊,一种一份基线。原先这四条走同两句猜话:「像类名」→ find/code-search,
        // 否则 → types。四种成因要的下一步各不相同,而其中三种的答案就在同一个库里。
        { "search-miss-xmlnode",   ["search", "BaseBullet"] },
        { "search-miss-deftype",   ["search", "ThingDef"] },
        { "search-miss-class",     ["search", "TestVariantDef"] },
        { "search-miss-mod",       ["search", "ludeon.rimworld"] },
        // 被自己的 --scope 挡住 —— 「过滤掉了」被说成「没有」是最贵的那种。
        { "search-miss-scoped",    ["search", "TestModGun", "--scope", "ludeon.rimworld"] },
        // 第五种落点,keyed 那一层落地之后才算得出来:打进来的是屏幕上的一句界面文案。
        // 它与前四种不同 —— 前四种问的都是「这个**名字**是什么」,这一种问的是
        // 「这句**话**是什么」,而 search 的索引里没有它。R4 记的那个洞就是这个形状。
        { "search-miss-keyed",     ["search", "没有电力"] },
        // 同一句话由几个 key 各自承载时,这条路不许挑一个说成「就是这个」——
        // 真数据里「转至事件发生地点」同时是 JumpToLocation 与 ClickToJumpToProblem,
        // 而表里那几行长得一模一样,挑错了看不出来。
        { "search-miss-keyed-multi", ["search", "转至此处"] },
        // 五轮 F2:scope 展开在**有结果时**也要说。这两份是那道闸的字节落点 ——
        // 组名那份必须带展开句,写死 packageId 那份必须一个字都不多说。
        { "find-scope-group",      ["find", "thingClass", "RimWorld.Bullet", "--scope", "vanilla"] },
        { "find-scope-literal",    ["find", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld"] },
        // R10 fatal:换一份快照就拿得到,而这句话一直是可算出来的。
        { "get-other-snapshot",    ["get", "OnlyInOtherSnapshot"] },
        { "inherit-other-snapshot", ["inherit", "OnlyInOtherSnapshot"] },
        { "search-typo",           ["search", "Aparel_ShieldBelt"] },
        // 混合命中:两条 FTS + 一条只有子串扫描找得到,再加上一个小 limit ——
        // 「N of M 的 M 不随 limit 变」除了 GrammarTests 那条断言,这里也逐字节钉一份。
        { "search-substring",      ["search", "VoidNode"] },
        { "search-substring-cap",  ["search", "VoidNode", "--limit", "2"] },
        { "get-full",              ["get", "Apparel_ShieldBelt"] },
        { "get-path-filter",       ["get", "Apparel_ShieldBelt", "--path", "comps"] },
        { "get-path-no-match",     ["get", "Apparel_ShieldBelt", "--path", "zzzz"] },
        { "get-truncated-export",  ["get", "Bullet_Revolver"] },
        // R1 的三个落点。第一条是那份报告里错答案的原形:字段名与提问一字不差,值却是
        // 声明默认值 —— 点了名就必须印出来,并且当场说清它是哪一种。
        { "get-code-default-path", ["get", "Bullet_Revolver", "--path", "burstCount"] },
        { "get-code-default-all",  ["get", "Bullet_Revolver", "--defaults"] },
        { "get-code-default-json", ["get", "Bullet_Revolver", "--defaults", "--json"] },
        { "get-generated",         ["get", "Meat_Muffalo"] },
        { "get-missing",           ["get", "NoSuchDef"] },
        // R2:同名跨 def_type。两份基线的分工是「不带 --type 时提示在场」与「带 --type 时
        // 提示不许消失、且父节点/译文不许串味」—— 后者是本轮最恶劣的一处:按 SKILL 教的
        // 加了 --type 之后,同名提示反而没了,错行留下,对冲归零。
        { "get-name-collision",    ["get", "Firefoam"] },
        { "get-name-collision-typed", ["get", "Firefoam", "--type", "StatDef"] },
        // 桶名不一致(XML 根元素 TestVariantDef,def 落在 TestBaseDef 桶)时 inherits_from
        // 仍要在场 —— R2 的修法收窄了关联条件,这一份守的是它没有收窄过头。
        { "get-bucket-mismatch",   ["get", "VariantOne"] },
        { "find-hit",              ["find", "compClass", "RimWorld.CompShield"] },
        { "find-miss-compprops",   ["find", "compClass", "CompProperties_Shield"] },
        { "find-miss-field",       ["find", "noSuchField", "x"] },
        // 另一半问法。行的形状不同,--json 的顶层键也就不同(matches / paths),
        // 而「同一条命令按参数换键」正是 R14 里猜错键换来一个空结果的那种落差。
        { "find-by-value",         ["find", "--value", "CompShield"] },
        // 继承层的四条路各钉一份:抽象节点(有子、被 patch 点名)、具体 def(往上走)、
        // 断链(父不在快照里)、名字不在这一层。四条的措辞各说一件不同的事,
        // 而它们混起来正是「零结果一律报最强的那种」那类事故的温床。
        { "inherit-abstract",      ["inherit", "BaseBullet"] },
        { "inherit-def",           ["inherit", "Bullet_Revolver"] },
        { "inherit-broken-chain",  ["inherit", "TestModGun"] },
        { "inherit-not-in-layer",  ["inherit", "Apparel_ShieldBelt"] },
        { "inherit-missing",       ["inherit", "NoSuchNode"] },
        { "get-xml-node-only",     ["get", "BaseBullet"] },
        { "list-limited",          ["list", "ThingDef", "--limit", "2"] },
        { "list-scope-empty",      ["list", "HediffDef", "--scope", "test.mod"] },
        { "fields-filtered",       ["fields", "ThingDef", "--path", "comps"] },
        { "values-coverage",       ["values", "compClass"] },
        { "values-miss",           ["values", "noSuchField"] },
        // 并条之后的两种模式各钉一份:上面 list-limited / list-scope-empty 是给了 def 类型
        // 的那一半,这一份是不给的那一半。
        { "list-types",            ["list"] },
        { "mods",                  ["mods"] },
        // limit 取 2 而不是 3:R1 把默认值行从表里拿掉之后,ShieldBelt 只剩 3 条可列,
        // --limit 3 就再也截不到东西了 —— 这份基线原本就是为「JSON 里的截断声明」立的。
        { "json-mode",             ["get", "Apparel_ShieldBelt", "--limit", "2", "--json"] },
        // R14 的第二半:代码块在 --json 里也得是行,不是一串 "path:line:text" 字符串。
        // 消费方重新解析我们刚拼好的东西,是把一个已经有答案的问题外包出去 ——
        // 而路径里本来就可能有冒号,解析回来不一定还原得了。
        { "json-code-search",      ["code-search", "public", "--files", "ThingComp.cs", "-C", "1", "--json"] },
        { "json-read-member",      ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--json"] },
        { "usage-unknown-flag",    ["search", "shield", "--lmit", "5"] },
        { "usage-unknown-command", ["serach", "shield"] },
        // 这一份此前钉的是「Unknown command '--help'」—— 夹具恒追加 --db/--config,
        // 而总览那条分支要求 argv 恰好一个词,于是基线看着覆盖了命令总表,实际一次都没到过。
        { "help-overview",         ["--help"] },
        // `--help <command>` 不接(盲测:七个求助的调用方全打 `<command> --help`),
        // 但那个词不许被默默扔掉 —— 说清这一屏是什么,并把该打的那一条原样给出来。
        { "help-with-command",     ["--help", "search"] },
        { "help-get",              ["get", "--help"] },
        { "help-code-search",      ["code-search", "--help"] },
        { "help-sources-sync",     ["sources", "sync", "--help"] },
        // 没配 decompiled_dir 时说的那句话。反编译树是**唯一**不在快照里的数据源,
        // 于是「没有它」这条路必然被走到,而它必须说清该往哪补一行配置。
        // 这一条要的是**没有**配置,所以自带 --config 覆盖掉默认那份(Fixture.Run 的规矩)。
        { "sources-not-configured", ["sources", "list", "--config", "no-such-config.toml"] },
        // 三轮 R3/R4/R13/R15 与「--limit 静默提前终止扫描」全落在 code-search 上,
        // 而它此前一条输出基线都没有 —— 六个场景踩同一处、闸上却没有落点,不是巧合。
        // 每一条盯一件事:
        { "code-search-hit",       ["code-search", ": ThingComp"] },
        // 上下文窗口重叠(R13):-C 1 打在连着命中的五行上。不合并就是每行印三遍。
        { "code-search-context",   ["code-search", "public", "--files", "ThingComp.cs", "-C", "1"] },
        // --limit 只管印几行,不许缩短扫描:总数必须仍是准数(「N of M」而非「at least N」)。
        { "code-search-limit",     ["code-search", "public", "--limit", "2"] },
        // 单文件上限(R4):同上,过了上限的命中仍要进总数。
        { "code-search-per-file",  ["code-search", "public", "--max-per-file", "1"] },
        // 文件数上限咬下去(R3):某棵树只读了一部分要说破、没读到的树要点名、
        // .git 与空树不许出现在名单里。
        { "code-search-max-files", ["code-search", ": ThingComp", "--max-files", "2"] },
        // 同一道闸 + 零命中 —— 本轮最贵的一条:「没匹配到」与「没读完」必须分得开。
        { "code-search-capped-miss", ["code-search", "zzzznothing", "--max-files", "2"] },
        // 真零结果:扫完了确实没有。这一条才该指路去 search / find。
        { "code-search-miss",      ["code-search", "zzzznothing"] },
        // 第三种零结果:glob 一个文件都没打中。写这条修复时自己撞上去的 ——
        // 带 '/' 的 glob 匹配的是相对**根目录**的整条路径,少写树名就全空。
        { "code-search-glob-empty", ["code-search", "public", "--files", "Verse/ThingComp.cs"] },
        // 第四种零结果,第四轮回归实测撞到的:树在名单里、目录也在,里面一个文件都没有。
        // 与上一条逐字同形过 —— 于是答案变成「改 glob」,而真因是这棵树该 sync 一遍。
        { "code-search-empty-tree", ["code-search", "public", "--source", "zz.emptytree"] },
        // 别名 --file-extension 收下 'cs',值却按 glob 解 —— 两种文法的零结果曾逐字同形。
        { "code-search-bare-ext",  ["code-search", "public", "--file-extension", "cs"] },
        // --path 筛空的两种成因:真没有这条路径 vs 给进来的文本其实是个**值**(B5 的形状,
        // stat 名装在 statBases[N].stat 里)。此前两种输出逐字同形。
        { "get-path-is-value",     ["get", "Apparel_ShieldBelt", "--path", "MarketValue"] },
        // --source 已经给出时,补救措施里不许再列 --source(R3)。
        { "code-search-source-cap", ["code-search", "public", "--source", "vanilla", "--max-files", "1"] },
        { "code-search-no-tree",   ["code-search", "public", "--source", "HAR"] },
        // 界面文案接上代码行。语料那三行各是一种形态,所以这一份同时钉住三件事:
        // 查得到的 key 进表、查不到的字面量点名、运行时拼出来的 key 单独说 ——
        // 三者混起来的话,「这一行没被解释」就与「这个 key 没有译文」同形了。
        { "code-search-ui-text",   ["code-search", "Translate"] },
        // 同一次调用关掉它:那三条声明必须一起消失,不许留一句孤零零的边界话。
        { "code-search-no-ui-text", ["code-search", "Translate", "--no-ui-text"] },
        // keyed 的两个方向。key → 显示什么;文案 → 是哪个 key(带上「拿它去搜代码」那一步,
        // 因为这正是从屏幕回到代码的那一跳)。
        { "keyed-hit",             ["keyed", "CannotUseNoPower"] },
        { "keyed-text",            ["keyed", "没有电力"] },
        // 同上,keyed 自己那条下一步提示的落点:一个 key 时命令填好,几个 key 时
        // 说破要按行挑 —— 填第一个等于替读的人挑了一个。
        { "keyed-text-multi",      ["keyed", "转至此处"] },
        // 占位:表里它与真译文同形,而游戏显示的是英文。这一份守的是那句说破在场。
        { "keyed-placeholder",     ["keyed", "TodoKey"] },
        // 过滤器筛空 ≠ 没有这个 key。三轮 R8 那类误诊在这条命令上的落点。
        { "keyed-placeholder-none", ["keyed", "CannotUseNoPower", "--placeholders"] },
        // 零结果的两种成因:代码里有这个字面量而语言文件里没有(死 key),
        // 以及问的其实是个 def 名 —— 后者该被指回 get/search,而不是报「没有」。
        { "keyed-miss",            ["keyed", "NoSuchUiKey"] },
        { "keyed-miss-def",        ["keyed", "Apparel_ShieldBelt"] },
        // 三轮 R5:CLI 侧读不了文件,于是 CLI-only 时读代码退化成编造正则。read 补上这条
        // 底线,而它自己的错法集中在两处 —— 定位到哪个文件、以及配平括号找到的是不是那一段。
        // 轮廓:注释/字符串/字符字面量里的括号不许算数,方法体里的 if 不许变成成员,
        // 带初值的字段不许被初值里的括号认成方法。
        { "read-outline",          ["read", "Outline.cs", "--source", "vanilla", "--outline"] },
        // 同名成员分属两个类型:不带 --type 全给并说破归属,带 --type 只给一份。
        { "read-member",           ["read", "vanilla/Verse/Outline.cs", "--member", "Shared"] },
        { "read-member-typed",     ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Inner"] },
        // 「有这个成员但不在那个类型里」与「整个文件都没有」是两句不同的话。
        { "read-member-wrong-type", ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Nope"] },
        { "read-member-missing",   ["read", "vanilla/Verse/Outline.cs", "--member", "Shard"] },
        { "read-type",             ["read", "vanilla/Verse/Outline.cs", "--type", "Inner"] },
        // 裸行三态:一段、整份、越过末尾。翻页参数与总行数恒在,这条命令的分页就靠它。
        { "read-lines",            ["read", "vanilla/Verse/Outline.cs", "--lines", "7-12"] },
        { "read-whole-file",       ["read", "vanilla/Verse/Widgets.cs"] },
        { "read-past-end",         ["read", "vanilla/Verse/Outline.cs", "--lines", "900"] },
        { "read-line-cap",         ["read", "vanilla/Verse/Outline.cs", "--type", "Outer", "--limit", "4"] },
        // 基名撞车时不选,只列 —— 选错的输出与选对的逐字同形。
        { "read-ambiguous",        ["read", "Outline.cs"] },
        { "read-no-file",          ["read", "NoSuchFile.cs"] },
        // 两种读法同时传:不排优先级,当场说破这是两件事(旧世系在这里是静默择一的)。
        { "read-two-modes",        ["read", "Outline.cs", "--lines", "1-3", "--member", "Shared"] },
        // 全树自检撞出来的三种形态(语料见 Fixture.WriteSourceTree)。这三份基线的作用不是
        // 保护现状 —— 它们先钉下**错的**输出,再让修法的 diff 把错处逐字显出来。
        //
        // 元组类型:`internal (int left, int right) Split(int at)` 的第一个顶层 '(' 是类型。
        // 取它左边的标识符 = 取到修饰符,于是 Split 与 bounds 双双消失,列里剩两个
        // 叫 internal / private 的「方法」,而**行号是对的** —— 错答案穿着对答案的衣服。
        { "read-outline-tuple",    ["read", "vanilla/Verse/Tuples.cs", "--outline"] },
        // 同一件事在 --member 上的样子:名字白纸黑字在文件里,命令说没有,
        // 而它给的理由(「配平括号不是解析」)会把人引去改拼写。
        { "read-member-tuple",     ["read", "vanilla/Verse/Tuples.cs", "--member", "Split"] },
        // 约束连写:`where T : class where U : struct` 里的 `class where` 被认成类型声明,
        // 压栈之后 Declarable 放行,方法体里的 if 跟着变成成员 —— 崩塌型,一处误判毁一整块。
        { "read-outline-constrained", ["read", "vanilla/Verse/Constrained.cs", "--outline"] },
        // 泛型元数不同的同名类型。这一条不是错,是歧义:两行轮廓逐字相同,
        // --type 会把两段都给出来而消歧提示发不出(它只在 --type 缺席时说话)。
        { "read-outline-arity",    ["read", "vanilla/Verse/Pair.cs", "--outline"] },
        { "read-type-arity",       ["read", "vanilla/Verse/Pair.cs", "--type", "Pair"] },
        // 三轮 R5 的另一半:没有 --offset 的表把调用方逼去接管道,而管道会把声明区
        // 连同计数一起截掉。四条盯分页的三个位置 —— 中间页要说自己从第几条起、
        // 末页不许再给下一页的参数、翻过头不是「没有这个东西」。
        { "page-middle",           ["list", "ThingDef", "--limit", "2", "--offset", "2"] },
        { "page-last",             ["list", "ThingDef", "--limit", "4", "--offset", "5"] },
        { "page-past-end",         ["list", "ThingDef", "--offset", "900"] },
        // 同一套文法长在另外三条命令上(产地唯一的意思就是措辞不许各写一份)。
        // search 的结果集是「FTS 命中」接着「子串补扫」两段拼的,翻页要在拼好的那条序列上走 ——
        // 两段各自跳一次 offset 会让第二页把第一页的补扫结果原样再印一遍。
        { "page-search",           ["search", "VoidNode", "--limit", "1", "--offset", "1"] },
        { "page-fields",           ["fields", "ThingDef", "--limit", "3", "--offset", "3"] },
        { "page-values",           ["values", "thingClass", "--limit", "1", "--offset", "1"] },
        // 负偏移在 SQL 里等同于 0 —— 不拦下来,「少给了一个负号」与「这就是第一页」逐字相同。
        { "page-negative",         ["list", "ThingDef", "--offset", "-2"] },
        // 参数被改写就要说破:--limit 5000 与 --limit 2000 原先输出逐字相同,
        // 而调用方明确要了 5000。声明层早写着会夹紧,差的只是当场说一句。
        { "limit-clamped",         ["list", "ThingDef", "--limit", "5000"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 输出与基线逐字节一致(string name, string[] argv)
    {
        var (stdout, stderr, code) = Fixture.Run(argv);

        // stdout / stderr / 退出码是同一个契约的三面。只钉 stdout,一条命令从「有结果」
        // 变成「报错但仍打印点什么」也能悄悄溜过去。
        var actual = new StringBuilder()
            .Append("$ rimsearcher ").Append(string.Join(' ', argv)).Append('\n')
            .Append("exit ").Append(code).Append('\n')
            .Append("--- stdout ---\n").Append(stdout)
            .Append("--- stderr ---\n").Append(stderr)
            .ToString()
            .Replace("\r\n", "\n");

        var path = Path.Combine(SnapshotDir, name + ".txt");

        if (Environment.GetEnvironmentVariable("RIMSEARCHER_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(SnapshotDir);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path),
            $"No baseline for '{name}'. Run with RIMSEARCHER_UPDATE_SNAPSHOTS=1 to create it, then read the diff.");

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 基线目录里不许有没人认领的文件。删掉一条用例却留下它的基线,那份文件就成了
    /// 「看起来还在闸内、其实早没人跑」的东西 —— 比没有闸更坏。
    /// </summary>
    [Fact]
    public void 基线目录里没有孤儿文件()
    {
        if (!Directory.Exists(SnapshotDir)) return;
        var claimed = Cases.Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.EnumerateFiles(SnapshotDir, "*.txt")
                               .Select(Path.GetFileNameWithoutExtension)
                               .Where(n => n is not null && !claimed.Contains(n))
                               .ToList();
        Assert.True(orphans.Count == 0, $"Baselines with no case: {string.Join(", ", orphans)}.");
    }

    /// <summary>
    /// 「keyed 这一层整个是空的」与「这个 key 不在里面」是两件事,而它们的输出天然会长成
    /// 一样 —— 首要禁令的那个形状。空层只可能来自一份缺了这一节的快照,所以这句话必须说
    /// **快照**,不许说 key。
    ///
    /// 这条走不了字节级基线:它要一份自己动过手的库,而 <c>--db</c> 是绝对路径 ——
    /// 印进基线就把本机 TEMP 路径绑死了。断言换成看两句话在不在、以及**不在**该不在的地方。
    /// </summary>
    [Fact]
    public void keyed层为空时说破是快照的缘故而不是查不到()
    {
        var db = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "keyed-empty.db");
        if (File.Exists(db)) File.Delete(db);
        File.Copy(Fixture.Db, db);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // keyed_fts 是 contentless 的,DELETE 不认 —— 清空要走 fts5 自己那条命令。
            cmd.CommandText = "INSERT INTO keyed_fts(keyed_fts) VALUES('delete-all'); DELETE FROM keyed;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (empty, _, code) = Fixture.Run("keyed", "CannotUseNoPower", "--db", db);
        Assert.Equal(1, code);
        Assert.Contains("no keyed translations at all", empty);
        // 这一句才是用例的全部意义:成因归到快照身上,而不是归到问的那个 key 身上。
        Assert.Contains("property of the snapshot", empty);
        Assert.DoesNotContain("No keyed translation matches", empty);

        // 反面:库里有这一层、只是没这个 key 时,上面那句话一个字都不许出现。
        var (missing, _, _) = Fixture.Run("keyed", "NoSuchUiKey");
        Assert.DoesNotContain("no keyed translations at all", missing);
        Assert.Contains("No keyed translation matches", missing);
    }

    internal static string SnapshotDir => Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Tests", "Snapshots");

    /// <summary>
    /// 读写基线的测试类共用的 collection 名。xUnit 默认「一个测试类一个 collection、
    /// collection 之间并行」,而基线目录是**同一批文件**被一个类写、被另一个类读 ——
    /// 同名进一个 collection 是让它们串起来的唯一办法。
    /// 闸在 <c>GateTests.读写基线的测试类同属一个collection</c>。
    /// </summary>
    internal const string Collection = "baseline-files";
}
