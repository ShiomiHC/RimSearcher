using System.Text;

namespace RimSearcher.Tests;

/// <summary>
/// 字节级基线。跑一批固定调用,把 stdout 逐字节钉进 <c>Snapshots/</c>。
///
/// 这道闸看的是**输出契约**:措辞、列宽、声明区位置、空行、行尾 —— 凡是调用方读得到的
/// 东西,改动都会在这里变红。
///
/// 基线不对时用 <c>RIMSEARCHER_UPDATE_SNAPSHOTS=1 dotnet test</c> 重写,然后**读 diff**。
/// </summary>
[Collection(Collection)]
public class OutputSnapshotTests
{
    /// <summary>
    /// 每条基线都是一次真实调用。名字既是文件名也是这条用例在说什么。
    /// 覆盖面:查名字、看细节、反查、值域、代码搜索、报错路径。
    /// </summary>
    public static TheoryData<string, string[]> Cases => new()
    {
        { "search-hit",            ["search", "shield"] },
        { "search-miss",           ["search", "zzzznothing"] },
        { "search-miss-classlike", ["search", "CompShield"] },
        // 落空的四种成因,一种一份基线 —— 各自要的下一步不同,而其中三种的答案就在同一个库里。
        { "search-miss-xmlnode",   ["search", "BaseBullet"] },
        { "search-miss-deftype",   ["search", "ThingDef"] },
        { "search-miss-class",     ["search", "TestVariantDef"] },
        { "search-miss-mod",       ["search", "ludeon.rimworld"] },
        // 被自己的 --scope 挡住 —— 「过滤掉了」被说成「没有」是最贵的那种。
        { "search-miss-scoped",    ["search", "TestModGun", "--scope", "ludeon.rimworld"] },
        // 第五种落点:打进来的是屏幕上的一句界面文案 —— 问的是「这句**话**是什么」,
        // 而 search 的索引里没有它。
        { "search-miss-keyed",     ["search", "没有电力"] },
        // 同一句话由几个 key 各自承载时不许挑一个说成「就是这个」—— 真数据里
        // 「转至事件发生地点」同时是 JumpToLocation 与 ClickToJumpToProblem。
        { "search-miss-keyed-multi", ["search", "转至此处"] },
        // scope 展开在**有结果时**也要说:组名那份必须带展开句,写死 packageId 那份不多说一个字。
        { "find-scope-group",      ["find", "thingClass", "RimWorld.Bullet", "--scope", "vanilla"] },
        { "find-scope-literal",    ["find", "thingClass", "RimWorld.Bullet", "--scope", "ludeon.rimworld"] },
        // 换一份已注册的快照就拿得到 —— 这句话是算得出来的,不该报成「没有」。
        { "get-other-snapshot",    ["get", "OnlyInOtherSnapshot"] },
        { "inherit-other-snapshot", ["inherit", "OnlyInOtherSnapshot"] },
        { "search-typo",           ["search", "Aparel_ShieldBelt"] },
        // 混合命中:两条 FTS + 一条只有子串扫描找得到,再加一个小 limit ——
        // 钉住「N of M 的 M 不随 limit 变」。
        { "search-substring",      ["search", "VoidNode"] },
        { "search-substring-cap",  ["search", "VoidNode", "--limit", "2"] },
        { "get-full",              ["get", "Apparel_ShieldBelt"] },
        { "get-path-filter",       ["get", "Apparel_ShieldBelt", "--path", "comps"] },
        { "get-path-no-match",     ["get", "Apparel_ShieldBelt", "--path", "zzzz"] },
        { "get-truncated-export",  ["get", "Bullet_Revolver"] },
        // 代码默认值的三个落点:字段名与提问一字不差、值却是声明默认值 ——
        // 点了名就必须印出来,并且当场说清它是哪一种。
        { "get-code-default-path", ["get", "Bullet_Revolver", "--path", "burstCount"] },
        { "get-code-default-all",  ["get", "Bullet_Revolver", "--defaults"] },
        { "get-code-default-json", ["get", "Bullet_Revolver", "--defaults", "--json"] },
        { "get-generated",         ["get", "Meat_Muffalo"] },
        { "get-missing",           ["get", "NoSuchDef"] },
        // 同名跨 def_type。两份的分工:不带 --type 时提示在场;带 --type 时提示不许消失、
        // 且父节点/译文不许串味。
        { "get-name-collision",    ["get", "Firefoam"] },
        { "get-name-collision-typed", ["get", "Firefoam", "--type", "StatDef"] },
        // 桶名不一致(XML 根元素 TestVariantDef,def 落在 TestBaseDef 桶)时 inherits_from 仍要在场。
        { "get-bucket-mismatch",   ["get", "VariantOne"] },
        { "find-hit",              ["find", "compClass", "RimWorld.CompShield"] },
        { "find-miss-compprops",   ["find", "compClass", "CompProperties_Shield"] },
        { "find-miss-field",       ["find", "noSuchField", "x"] },
        // 单位置参数落空的三档。敲一个词进来的人多半给的是**值**而不是字段路径
        // (这条命令的正脸就是「从一个类名或一个值反查 def」),所以名字的落点要当场算:
        //   CompShield      它是某些 def 的字段取值 —— 指得动填好参数的 find
        //   Bullet_Revolver 它是 def 名 —— NameLookup 那句「is not a def name」在这里是假话
        //   noSuchField     哪儿都不是 —— 只剩那句带 <text> 占位的通用指路
        { "find-miss-name-is-value", ["find", "CompShield"] },
        { "find-miss-name-is-def", ["find", "Bullet_Revolver"] },
        // def 名那一档的另一半:没有任何字段指向它 —— 那时不许指向一条空手而归的 --value,
        // 「没有谁按名字引用它」本身就是答案。顺带钉住同名跨类型不让这句话变形。
        { "find-miss-name-unreferenced", ["find", "Firefoam"] },
        { "find-miss-bare",        ["find", "noSuchField"] },
        // 另一半问法。行的形状不同,--json 的顶层键也就不同(matches / paths)。
        { "find-by-value",         ["find", "--value", "CompShield"] },
        // 继承层的四条路各钉一份:抽象节点(有子、被 patch 点名)、具体 def(往上走)、
        // 断链(父不在快照里)、名字不在这一层 —— 四条的措辞各说一件不同的事。
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
        // list 的另一半:不给 def 类型时列类型总表。
        { "list-types",            ["list"] },
        { "mods",                  ["mods"] },
        // limit 取 2 而不是 3:默认值行不进表,ShieldBelt 只剩 3 条可列,--limit 3 截不到东西,
        // 而这份基线要的正是「JSON 里的截断声明」。
        { "json-mode",             ["get", "Apparel_ShieldBelt", "--limit", "2", "--json"] },
        // 代码块在 --json 里得是行,不是一串 "path:line:text" 字符串 ——
        // 路径里本来就可能有冒号,拼起来解析不回去。
        { "json-code-search",      ["code-search", "public", "--files", "ThingComp.cs", "-C", "1", "--json"] },
        { "json-read-member",      ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--json"] },
        { "usage-unknown-flag",    ["search", "shield", "--lmit", "5"] },
        { "usage-unknown-command", ["serach", "shield"] },
        // 同一个词在别的命令上是选项、在这条上是位置参数。--field 是 get / inherit / read
        // 认的写法,搬到 find 上就落空,而「这里怎么写」是算得出来的 —— 连值一起填好。
        { "usage-field-is-positional", ["find", "--field", "compClass"] },
        // 值给了两遍且不一样。位置参数与 --value 说的是同一件事,挑一个跑下去的话
        // 另一个被丢了在输出里看不出来。
        { "usage-value-twice",     ["find", "compClass", "RimWorld.CompShield", "--value", "Other"] },
        // 夹具恒追加 --db/--config,而总览那条分支要求 argv 恰好一个词。
        { "help-overview",         ["--help"] },
        // `--help <command>` 不接,但那个词不许被默默扔掉 —— 说清这一屏是什么,
        // 并把该打的那一条(`<command> --help`)原样给出来。
        { "help-with-command",     ["--help", "search"] },
        { "help-get",              ["get", "--help"] },
        { "help-code-search",      ["code-search", "--help"] },
        { "help-sources-sync",     ["sources", "sync", "--help"] },
        // 没配 decompiled_dir 时说的那句话。反编译树是**唯一**不在快照里的数据源,
        // 这条路必然被走到,输出必须说清该往哪补一行配置。
        // 这一条要的是**没有**配置,所以自带 --config 覆盖掉 Fixture.Run 默认追加的那份。
        { "sources-not-configured", ["sources", "list", "--config", "no-such-config.toml"] },
        // 以下每条盯 code-search 的一件事:
        { "code-search-hit",       ["code-search", ": ThingComp"] },
        // 上下文窗口重叠:-C 1 打在连着命中的五行上,窗口要合并。
        { "code-search-context",   ["code-search", "public", "--files", "ThingComp.cs", "-C", "1"] },
        // --limit 只管印几行,不许缩短扫描:总数必须仍是准数(「N of M」而非「at least N」)。
        { "code-search-limit",     ["code-search", "public", "--limit", "2"] },
        // 单文件上限:同上,过了上限的命中仍要进总数。
        { "code-search-per-file",  ["code-search", "public", "--max-per-file", "1"] },
        // 文件数上限咬下去:某棵树只读了一部分要说破、没读到的树要点名、
        // .git 与空树不许出现在名单里。
        { "code-search-max-files", ["code-search", ": ThingComp", "--max-files", "2"] },
        // 同一道闸 + 零命中:「没匹配到」与「没读完」必须分得开。
        { "code-search-capped-miss", ["code-search", "zzzznothing", "--max-files", "2"] },
        // 真零结果:扫完了确实没有。这一条才该指路去 search / find。
        { "code-search-miss",      ["code-search", "zzzznothing"] },
        // 第三种零结果:glob 一个文件都没打中 —— 带 '/' 的 glob 匹配的是相对**根目录**
        // 的整条路径,少写树名就全空。
        { "code-search-glob-empty", ["code-search", "public", "--files", "Verse/ThingComp.cs"] },
        // 第四种零结果:树在名单里、目录也在,里面一个文件都没有 —— 真因是这棵树该 sync 一遍,
        // 不许与上一条同形(否则答案会变成「改 glob」)。
        { "code-search-empty-tree", ["code-search", "public", "--source", "zz.emptytree"] },
        // 别名 --file-extension 收下 'cs',值却按 glob 解 —— 两种文法的零结果要分得开。
        { "code-search-bare-ext",  ["code-search", "public", "--file-extension", "cs"] },
        // --path 筛空的两种成因:真没有这条路径 vs 给进来的文本其实是个**值**
        // (stat 名装在 statBases[N].stat 里)。
        { "get-path-is-value",     ["get", "Apparel_ShieldBelt", "--path", "MarketValue"] },
        // --source 已经给出时,补救措施里不许再列 --source。
        { "code-search-source-cap", ["code-search", "public", "--source", "vanilla", "--max-files", "1"] },
        { "code-search-no-tree",   ["code-search", "public", "--source", "HAR"] },
        // 界面文案接上代码行。语料那三行各是一种形态,这一份同时钉住三件事:
        // 查得到的 key 进表、查不到的字面量点名、运行时拼出来的 key 单独说。
        { "code-search-ui-text",   ["code-search", "Translate"] },
        // 同一次调用关掉它:那三条声明必须一起消失,不许留一句孤零零的边界话。
        { "code-search-no-ui-text", ["code-search", "Translate", "--no-ui-text"] },
        // keyed 的两个方向。key → 显示什么;文案 → 是哪个 key(带上「拿它去搜代码」那一步)。
        { "keyed-hit",             ["keyed", "CannotUseNoPower"] },
        { "keyed-text",            ["keyed", "没有电力"] },
        // keyed 自己那条下一步提示:一个 key 时命令填好,几个 key 时说破要按行挑 ——
        // 填第一个等于替读的人挑了一个。
        { "keyed-text-multi",      ["keyed", "转至此处"] },
        // 占位:表里它与真译文同形,而游戏显示的是英文。这一份守的是那句说破在场。
        { "keyed-placeholder",     ["keyed", "TodoKey"] },
        // 过滤器筛空 ≠ 没有这个 key。
        { "keyed-placeholder-none", ["keyed", "CannotUseNoPower", "--placeholders"] },
        // 第三条路:不给查询词的整层枚举 —— 「把还没译的全列出来」这条意图要有一种
        // 可表达的形式。两份基线:整层第一页,以及这条意图本身。
        { "keyed-all",             ["keyed"] },
        { "keyed-all-placeholders", ["keyed", "--placeholders", "--limit", "all"] },
        // 枚举走的是分页文法而不是精确 key 那一路,所以翻过头这条分支也得有。
        { "keyed-all-past-end",    ["keyed", "--placeholders", "--offset", "9"] },
        // --placeholders 是收窄参数,计数要念回它划的那道线 —— 不念的话「1 key.」会被
        // 读成「filler 一共命中一条」,而真值是 2100 条里有一条占位。
        { "keyed-text-placeholders", ["keyed", "filler", "--placeholders"] },
        // 零结果的两种成因:代码里有这个字面量而语言文件里没有(死 key),
        // 以及问的其实是个 def 名 —— 后者该被指回 get/search,而不是报「没有」。
        { "keyed-miss",            ["keyed", "NoSuchUiKey"] },
        { "keyed-miss-def",        ["keyed", "Apparel_ShieldBelt"] },
        // read 的两处错法:定位到哪个文件、以及配平括号找到的是不是那一段。
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
        // 路径的中间段写错、文件名对。名字唯一时读下去 —— 但**必须说破**:后面每一句
        // 印的都是解析出来的那条路径,不说的话这次输出与「路径本来就写对了」逐字同形,
        // 而调用方会把那条错路径记下来接着用。名字仍撞车时照旧不选。
        { "read-wrong-dir",        ["read", "vanilla/RimWorld/Widgets.cs"] },
        { "read-wrong-dir-ambiguous", ["read", "vanilla/RimWorld/Outline.cs"] },
        // 两种读法同时传:不排优先级,当场说破这是两件事。
        { "read-two-modes",        ["read", "Outline.cs", "--lines", "1-3", "--member", "Shared"] },
        // 括号配平法认错声明的三种形态(语料见 Fixture.WriteSourceTree)。
        //
        // 元组类型:`internal (int left, int right) Split(int at)` 的第一个顶层 '(' 是类型。
        // 取它左边的标识符 = 取到修饰符,于是 Split 与 bounds 双双消失,列里剩两个
        // 叫 internal / private 的「方法」,而**行号是对的**。
        { "read-outline-tuple",    ["read", "vanilla/Verse/Tuples.cs", "--outline"] },
        // 同一件事在 --member 上的样子:名字白纸黑字在文件里,命令说没有,
        // 而它给的理由(「配平括号不是解析」)会把人引去改拼写。
        { "read-member-tuple",     ["read", "vanilla/Verse/Tuples.cs", "--member", "Split"] },
        // 约束连写:`where T : class where U : struct` 里的 `class where` 被认成类型声明,
        // 压栈之后 Declarable 放行,方法体里的 if 跟着变成成员 —— 一处误判毁一整块。
        { "read-outline-constrained", ["read", "vanilla/Verse/Constrained.cs", "--outline"] },
        // 泛型元数不同的同名类型。这一条不是错,是歧义:两行轮廓逐字相同,
        // --type 会把两段都给出来而消歧提示发不出(它只在 --type 缺席时说话)。
        { "read-outline-arity",    ["read", "vanilla/Verse/Pair.cs", "--outline"] },
        { "read-type-arity",       ["read", "vanilla/Verse/Pair.cs", "--type", "Pair"] },
        // 分页的三个位置:中间页要说自己从第几条起、末页不许再给下一页的参数、
        // 翻过头不是「没有这个东西」。
        { "page-middle",           ["list", "ThingDef", "--limit", "2", "--offset", "2"] },
        { "page-last",             ["list", "ThingDef", "--limit", "4", "--offset", "5"] },
        { "page-past-end",         ["list", "ThingDef", "--offset", "900"] },
        // 同一套文法长在另外三条命令上。search 的结果集是「FTS 命中」接着「子串补扫」两段拼的,
        // 翻页要在拼好的那条序列上走 —— 两段各自跳一次 offset 会让第二页重印第一页的补扫结果。
        { "page-search",           ["search", "VoidNode", "--limit", "1", "--offset", "1"] },
        { "page-fields",           ["fields", "ThingDef", "--limit", "3", "--offset", "3"] },
        { "page-values",           ["values", "thingClass", "--limit", "1", "--offset", "1"] },
        // 负偏移在 SQLite 里等同于 0 —— 不拦下来,「少给了一个负号」与「这就是第一页」同形。
        { "page-negative",         ["list", "ThingDef", "--offset", "-2"] },
        // 参数被夹紧就要当场说破。
        { "limit-clamped",         ["list", "ThingDef", "--limit", "5000"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void 输出与基线逐字节一致(string name, string[] argv)
    {
        var (stdout, stderr, code) = Fixture.Run(argv);

        // stdout / stderr / 退出码是同一个契约的三面,三者一起进基线。
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
    /// 基线目录里不许有没人认领的文件 —— 删了用例却留下基线,那份文件看着还在闸内、
    /// 其实早没人跑。
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
    /// 「keyed 这一层整个是空的」与「这个 key 不在里面」是两件事。空层只可能来自一份缺了
    /// 这一节的快照,所以这句话必须说**快照**,不许说 key。
    ///
    /// 这条走不了字节级基线:它要一份自己动过手的库,而 <c>--db</c> 是绝对路径,
    /// 印进基线就把本机 TEMP 路径绑死了。
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
        // 成因归到快照身上,而不是归到问的那个 key 身上。
        Assert.Contains("property of the snapshot", empty);
        Assert.DoesNotContain("No keyed translation matches", empty);

        // 反面:库里有这一层、只是没这个 key 时,上面那句话一个字都不许出现。
        var (missing, _, _) = Fixture.Run("keyed", "NoSuchUiKey");
        Assert.DoesNotContain("no keyed translations at all", missing);
        Assert.Contains("No keyed translation matches", missing);

        // 不给查询词那一路也要说快照,不能拿一个不存在的 query 拼进句子。
        var (bare, _, bareCode) = Fixture.Run("keyed", "--db", db);
        Assert.Equal(1, bareCode);
        Assert.Contains("property of the snapshot", bare);
        Assert.Contains("what this layer holds", bare);
    }

    /// <summary>
    /// 「一条占位都没有」是一个**完整的肯定回答**(这份快照译全了),而按行数它走的是
    /// exit 1。那句话必须把两件事都说出来:覆盖率是满的,以及退出码非零只是因为一行都没印。
    ///
    /// 与上面那条同理走不了字节级基线:它要一份自己动过手的库。
    /// </summary>
    [Fact]
    public void 整层没有占位时说的是覆盖率满而不是查不到()
    {
        var db = Path.Combine(Path.GetTempPath(), "rimsearcher-tests", "keyed-no-placeholders.db");
        if (File.Exists(db)) File.Delete(db);
        File.Copy(Fixture.Db, db);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE keyed SET placeholder = 0;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (text, _, code) = Fixture.Run("keyed", "--placeholders", "--db", db);
        Assert.Equal(1, code);
        Assert.Contains("carry a real translation", text);
        // 分母是整层的行数,不是「筛剩下的零」。
        Assert.Contains("2105 keyed translations", text);
        Assert.Contains("the exit code is still non-zero", text);
        // 「没找到」的措辞一个字都不许出现:那会把「译全了」说成「查不到」。
        Assert.DoesNotContain("No keyed translation matches", text);
    }

    internal static string SnapshotDir => Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Tests", "Snapshots");

    /// <summary>
    /// 读写基线的测试类共用的 collection 名。xUnit 默认一个测试类一个 collection、
    /// collection 之间并行,而基线目录被一个类写、另一个类读 —— 同名进一个 collection
    /// 才能让它们串行。闸在 <c>GateTests.读写基线的测试类同属一个collection</c>。
    /// </summary>
    internal const string Collection = "baseline-files";
}
