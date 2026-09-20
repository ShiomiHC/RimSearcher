using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 两道跨产物的闸:生成的参数参考必须与声明一致,基线里的每一行输出必须过文法。
/// </summary>
[Collection(OutputSnapshotTests.Collection)]
public class GateTests
{
    internal static string ReferencePath =>
        Path.Combine(DeclarationTests.RepoRoot(), "skills", "rimsearcher", "references", "cli-reference.md");

    // ---- 参数参考(声明区产地唯一)----

    /// <summary>
    /// <c>--help</c> 与这份 markdown 是同一批 <c>CommandSpec</c> 的两个渲染器。
    /// 入库的那份跟现在渲染出来的对不上,就说明改了声明却没重生成 —— 于是 skill 指着的
    /// 参考页开始描述一个不存在的 CLI。人工同步的文档必然漂移,所以这里逐字节比。
    /// </summary>
    [Fact]
    public void 入库的参数参考与声明渲染逐字节一致()
    {
        var rendered = MarkdownRenderer.Render(CommandRegistry.ExeName, new CommandRegistry().Specs,
                                               GlobalOptions.All, CommandRegistry.Tagline);
        Assert.True(File.Exists(ReferencePath), $"'{ReferencePath}' is missing; run 'rimsearcher docs --out <path>'.");
        var committed = File.ReadAllText(ReferencePath).Replace("\r\n", "\n");
        Assert.Equal(committed, rendered.Replace("\r\n", "\n"));
    }

    // ---- 声明的行键 vs 实际吐出来的行键 ----

    /// <summary>
    /// 每条命令的 <c>--help</c> 里那句「one row per X: a, b, c」点的名,必须真是那张表的列。
    ///
    /// 第九轮盲测抓到的形状:<c>snapshot truncated --help</c> 写着
    /// <c>def_name, def_type, dropped, mod</c>,而实际行是 <c>{def_name, def_type, fields_dropped}</c>
    /// 且根本没有 mod 列 —— 照 help 写的解析代码会静默拿到 null,而 null 与「这个 def 没丢字段」
    /// 同形。这类漂移人工比不出来,因为两边都长得像对的。
    ///
    /// 只认「冒号后、句号前的逗号清单」这一种写法:提取不出名字的散文一律跳过,于是这道闸
    /// **只会漏报、不会误报**。想被它守住,行键就照那个写法列。
    /// </summary>
    [Fact]
    public void 声明里点名的行键都是真列()
    {
        // 每个行式键配一次真调用。新增一个 Rows=true 的键就要在这里加一行 ——
        // 下面第一条断言保证漏加会红,第二条保证「探针没造出行」不被沉默放过。
        var probes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["search.defs"] = ["search", "shield"],
            ["search.empty_because"] = ["search", "shield", "--scope", "test.mod"],
            ["get.defs"] = ["get", "Apparel_ShieldBelt"],
            ["get.empty_because"] = ["get", "Apparel_ShieldBelt", "--type", "HediffDef"],
            // 值索引底下没有、成因算得出来的那张表。get 的那张住 defs[i] 里(归它所属的 def),
            // 不是根键,这里探不到;fields 的在根上,拿声明层探。
            ["fields.index_gap"] = ["fields", "ThingDef", "--path-contains", "neverSet", "--db", Fixture.PresenceDb],
            ["get.absent"] = ["get", "OnlyInOtherSnapshot", "--db", Fixture.OtherDb],
            // 名字落在别处那张表:五条命令各探一档(抽象节点 / def / 字段值 / def 类型)。
            ["search.found_as"] = ["search", "BaseBullet"],
            ["get.found_as"] = ["get", "BaseBullet"],
            ["where.found_as"] = ["where", "CompShield"],
            // 官方 def 上的类名值 + 共享夹具没有 xml 列(0.2.0)→ xml_written 一行。
            ["where.absent"] = ["where", "compClass", "TestMod.CompBoltedOn"],
            ["inherit.found_as"] = ["inherit", "ThingDef"],
            ["keyed.found_as"] = ["keyed", "Bullet_Revolver"],
            ["list.defs"] = ["list", "ThingDef"],
            ["list.types"] = ["list"],
            ["list.empty_because"] = ["list", "ThingDef", "--find", "zzznothing"],
            // 名字是 class 不是 def 类型 → found_as 一行(is = class)。
            ["list.found_as"] = ["list", "TestVariantDef"],
            ["where.matches"] = ["where", "thingClass", "RimWorld.Bullet"],
            ["where.paths"] = ["where", "--value", "RimWorld.Bullet"],
            // 零行成因表只在自己给的筛子确实挡掉了东西时有行 —— 拿 --scope 圈空的那条探。
            ["where.empty_because"] = ["where", "thingClass", "RimWorld.Bullet", "--scope", "test.mod"],
            ["fields.fields"] = ["fields", "ThingDef"],
            ["values.values"] = ["values", "thingClass"],
            ["values.empty_because"] = ["values", "workerClass", "--scope", "ludeon.rimworld"],
            ["mods.mods"] = ["mods"],
            ["inherit.nodes"] = ["inherit", "BaseBullet"],
            ["keyed.keys"] = ["keyed", "CannotUseNoPower"],
            ["keyed.empty_because"] = ["keyed", "CannotUseNoPower", "--empty-translation"],
            // 语料库是没配 mod_roots 建的,这一行在共享夹具上就有。
            ["keyed.absent"] = ["keyed", "CannotUseNoPower"],
            ["economy.things"] = ["economy"],
            ["economy.empty_because"] = ["economy", "--calc-state", "not_producible", "--category", "Building"],
            // 缺层那张表只在缺层的库上有行 —— 拿建于经济面之前的那份夹具探。
            ["economy.absent"] = ["economy", "--db", Fixture.OtherDb],
            ["economy.found_as"] = ["economy", "BaseBullet"],
            ["code-search.matches"] = ["code-search", "Translate"],
            ["code-search.ui_text"] = ["code-search", "Translate"],
            ["read.source"] = ["read", "CompShield.cs", "--lines", "1-5"],
            ["read.empty_because"] = ["read", "vanilla/Verse/Outline.cs", "--member", "Shared", "--type", "Nope"],
            ["read.declarations"] = ["read", "CompShield.cs", "--outline"],
            ["snapshot list.snapshots"] = ["snapshot", "list"],
            ["snapshot status.xml"] = ["snapshot", "status"],
            ["snapshot status.mod_list"] = ["snapshot", "status"],
            ["snapshot status.layers"] = ["snapshot", "status"],
            ["snapshot truncated.truncated"] = ["snapshot", "truncated"],
            ["snapshot truncated.empty_because"] = ["snapshot", "truncated", "--def", "Anesthetic"],
            ["snapshot diff.defs_added"] = ["snapshot", "diff"],
            ["snapshot diff.defs_removed"] = ["snapshot", "diff"],
            ["snapshot diff.fields"] = ["snapshot", "diff"],
            ["modlist list.modlists"] = ["modlist", "list"],
            ["modlist show.mods"] = ["modlist", "show", "fixture-current"],
            ["sources list.trees"] = ["sources", "list"],
            // 元数据那四条读的是夹具里那份真程序集(FixtureAssembly),不是手写的 .cs。
            ["il.il"] = ["il", "RimWorld.CompShield.PostSpawnSetup"],
            ["types.types"] = ["types", "Verse.ThingComp"],
            ["types.empty_because"] = ["types", "Verse.ThingComp", "--derived", "--declares", "NoSuchMember"],
            ["members.members"] = ["members", "Verse.ThingComp"],
            ["members.empty_because"] = ["members", "Verse.ThingComp", "--name", "zzznothing"],
            ["callers.calls"] = ["callers", "Verse.Widgets.Label"],
            // 夹具里 vanilla 有边表、另外两棵没有 —— 查调用者那一路上这两棵各一行。
            ["callers.absent"] = ["callers", "Verse.Widgets.Label"],
            // 这三条的 absent 只有一种行(树旁没有 dll 副本),共享夹具造不出来;JSON 由
            // FixtureAssembly.InstalledOnlyJsonFor 在一棵一次性的树上取,argv 在那边。
            ["members.absent"] = ["members", "Verse.ThingComp"],
            ["il.absent"] = ["il", "Verse.ThingComp.PostSpawnSetup"],
            ["types.absent"] = ["types", "Verse.ThingComp"],
            ["code-search.absent"] = ["code-search", "public", "--source", "zz.emptytree"],
        };

        var declared = new CommandRegistry().Specs
            .SelectMany(s => s.JsonKeys.Where(k => k.Rows).Select(k => (Command: s.Name, k.Key, k.What)))
            .ToList();

        var uncovered = declared.Where(d => !probes.ContainsKey($"{d.Command}.{d.Key}")).ToList();
        Assert.True(uncovered.Count == 0,
            "These row-shaped keys have no probe here, so nothing checks their column names: " +
            string.Join(", ", uncovered.Select(d => $"{d.Command}.{d.Key}")));

        var complaints = new List<string>();
        foreach (var (command, key, what) in declared)
        {
            var named = ColumnsNamedIn(what);
            if (named.Count == 0) continue;

            // snapshot status 的两张表在共享 fixture 上经常是空的(那正是「一致」);
            // snapshot diff 不能走 Fixture.Run(它会塞 --db);snapshot truncated 的四个成因列
            // 只在分过类的库上有,共享夹具没分过类。列名得拿到真有行的环境里验。
            var json = command switch
            {
                "snapshot status" => StalenessTests.StatusJsonFor(key),
                "snapshot diff" => SnapshotDiffTests.DiffJsonFor(key),
                "snapshot truncated" when key == "truncated" => TruncationCauseTests.TruncatedJsonFor(),
                "members" or "il" or "types" when key == "absent" => FixtureAssembly.InstalledOnlyJsonFor(command),
                _ => Fixture.Run([.. probes[$"{command}.{key}"], "--json"]).Stdout,
            };
            var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty(key, out var rows) ||
                rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() == 0)
            {
                complaints.Add($"{command}.{key}: the probe produced no rows, so its column names went unchecked.");
                continue;
            }

            var actual = rows[0].EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var n in named.Where(n => !actual.Contains(n)))
                complaints.Add($"{command}.{key}: --help names '{n}', but the rows carry [{string.Join(", ", actual)}].");
        }

        Assert.True(complaints.Count == 0, string.Join("\n  ", complaints.Prepend("")));
    }

    /// <summary>
    /// 同一个数据键由两条路发出时,两边的键集必须逐键相等。
    ///
    /// 上面那条闸比的是 <c>--help</c> 与**一条**路,所以另一条路自己漂了它看不见:
    /// economy 的整层列表少七个键、单条详情齐全,而 help 点的名两边都不完全等于 ——
    /// 三份两两不同,却没有一处能自己发现。
    ///
    /// 缺键在这一层尤其贵:JSON 里没有的键,消费侧读出来是 null,而这一层**处处**以 null
    /// 表示「游戏算不出这个数」—— 于是「这条路不发这个键」与「这个东西没有难度变体」
    /// 逐字节同形,两者的下一步完全相反。
    /// </summary>
    [Theory]
    [InlineData("things", new[] { "economy" }, new[] { "economy", "TestModGun" })]
    // modlist show 的两条路:指名一份 vs --find 搜全部。后者多一个 modlist 键 ——
    // 方向与 economy 相反(多而不是少),而代价一样:按 modlist 分组的消费代码在指名那条路上
    // 拿到 null,读出来是「这行不属于任何列表」。
    [InlineData("mods", new[] { "modlist", "show", "fixture-current" },
                        new[] { "modlist", "show", "--find", "test.mod" })]
    public void 同一个键的两条路发出同一套键(string key, string[] listing, string[] detail)
    {
        Assert.Equal(KeysOf(key, listing), KeysOf(key, detail));

        static List<string> KeysOf(string key, string[] argv)
        {
            var (json, _, _) = Fixture.Run([.. argv, "--json"]);
            var rows = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty(key);
            Assert.NotEqual(0, rows.GetArrayLength());
            return [.. rows[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// 「引导符之后、句号之前」那一段里的逗号分隔标识符。括号里的解释先剥掉
    /// (<c>defs (how many defs use it)</c> 点的列名是 <c>defs</c>)。
    ///
    /// **两道缝,叠着的**,都在 2026-08-15 补上 —— 而它们的沉默与「查过了,没问题」同形:
    /// 上面 <c>uncovered</c> 那条断言只查探针在不在,探针都在,覆盖率读出来是满的。
    ///
    /// 一、引导符原先只认冒号,而三条命令(economy / code-search / keyed)的 What 写的是
    /// 破折号,于是整条 <c>continue</c> 掉;
    /// 二、标识符正则原先只认 snake_case,而 economy 一层的键全是驼峰(<c>defName</c>),
    /// 逐个被滤光 —— 单修第一道时这道闸仍是绿的。
    ///
    /// economy 的全表投影少七个键正是从这两道缝里一起过去的。
    /// </summary>
    private static List<string> ColumnsNamedIn(string what)
    {
        var lead = what.IndexOfAny([':', '—']);
        if (lead < 0) return [];
        var tail = what[(lead + 1)..];
        var stop = tail.IndexOf('.');
        if (stop >= 0) tail = tail[..stop];

        return [.. tail.Split(',')
            .Select(part => Regex.Replace(part, @"\(.*", "").Trim())
            // 小写开头,内部允许驼峰 —— 两种键名风格本仓都在用。首字母大写不收:
            // 那多半是清单里的一个普通英文词或一个值(Item / Building),不是列名。
            .Where(part => Regex.IsMatch(part, "^[a-z][a-zA-Z0-9_]*$"))];
    }

    /// <summary>
    /// 参考页里不许出现本机路径。它是要进库、要给别人读的 —— 一条 <c>C:\Users\CCH</c>
    /// 既是噪声,也是把作者的机器当成了世界。
    /// </summary>
    [Fact]
    public void 参数参考里没有本机路径()
    {
        var text = File.ReadAllText(ReferencePath);
        Assert.DoesNotContain("CCH", text, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\SteamLibrary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S:\\works", text, StringComparison.Ordinal);
    }

    // ---- skill 文档 ----

    private static string SkillPath =>
        Path.Combine(DeclarationTests.RepoRoot(), "skills", "rimsearcher", "SKILL.md");

    /// <summary>
    /// skill 目录下的 md 不能带 BOM。
    ///
    /// SKILL.md 头上那三个字节落在 `---` 前面，前置区的开界符于是不在行首 ——
    /// 能不能认看加载方的实现，而那不在本仓里。它不是人敲出来的，是改文的脚本面默认
    /// 写 utf-8-sig 带进来的，而带进来之后源码面一字不差。
    /// </summary>
    [Fact]
    public void skill文档不带BOM()
    {
        var dir = Path.GetDirectoryName(SkillPath)!;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var head = File.ReadAllBytes(file).Take(3).ToArray();
            Assert.False(head.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }),
                $"{Path.GetFileName(file)} starts with a UTF-8 BOM; write it without one.");
        }
    }

    /// <summary>
    /// skill 文档里写出来的每一条命令行都得真能跑。
    ///
    /// SKILL.md 与 references 下的手写页都是手写的:模型照它们拼命令行,写错一个开关,
    /// 代价是调用方白跑一轮。这里判的不是措辞,是**命令名与开关名在注册表里存不存在**。
    /// cli-reference.md 除外 —— 它是 docs 命令的生成物,由上面的字节闸守着。
    /// </summary>
    [Fact]
    public void skill文档里的命令行都能解析()
    {
        var registry = new CommandRegistry();
        var globals = GlobalOptions.All.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        var files = new List<string> { SkillPath };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(SkillPath)!, "references"), "*.md")
                                .Where(f => Path.GetFileName(f) != "cli-reference.md"));

        var total = 0;
        foreach (var file in files)
        {
            var doc = Path.GetFileName(file);
            var text = File.ReadAllText(file).Replace("\r\n", "\n");

            foreach (Match inv in Regex.Matches(text, @"`" + CommandRegistry.ExeName + @"([^`]*)`"))
            {
                total++;
                var argv = Tokenize(inv.Groups[1].Value);
                if (argv.Count == 0) continue;                       // 光提 exe 名(「the rimsearcher CLI」)
                if (argv[0].StartsWith('<') || argv[0].StartsWith("--")) continue;  // 占位命令名

                var (command, rest) = registry.Resolve(argv);
                Assert.True(command is not null, $"{doc} invokes '{inv.Value}', but there is no such command.");

                var accepted = command!.Spec.Options
                                      .SelectMany(o => new[] { o.Name }.Concat(o.Aliases))
                                      .ToHashSet(StringComparer.Ordinal);
                foreach (var token in rest.Where(t => t.StartsWith("--", StringComparison.Ordinal)))
                {
                    var name = token[2..].Split('=')[0];
                    Assert.True(accepted.Contains(name) || (command.Spec.UsesGlobals && globals.Contains(name)),
                        $"{doc} writes '{inv.Value}', but '{command.Spec.Name}' does not accept '--{name}'.");
                }
            }
        }

        Assert.True(total > 5, "The skill docs suddenly name almost no commands; the scanner is probably broken.");
    }

    // 2026-08-04(14 批 B):`skill文档的收窄开关表与声明一致` 随那张表一起退役。它守的是
    // 一张十一行的手写表 ——「命令与开关拆在两个单元格里,恰恰是最容易漂的形态」—— 而表
    // 退成了 `<command> --help`,那一份直接由 CommandSpec 生成,漂移这件事本身没有了。
    // **不是把闸放松了**:上面那条(文档里成句的命令行逐个 token 对 Spec)覆盖面没变,
    // 而这条现在没有输入可扫。表若哪天回来,连这道闸一起回来。

    /// <summary>引号与转义之外的最小分词 —— 这里只需要把一条命令行拆成 token。</summary>
    private static List<string> Tokenize(string s)
    {
        var argv = new List<string>();
        foreach (Match m in Regex.Matches(s, "\"([^\"]*)\"|(\\S+)"))
            argv.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return argv;
    }

    // ---- 把基线喂回文法检查 ----

    /// <summary>
    /// 文法闸只判自己造的那几个 <see cref="Tally"/>,而真实输出走的是另一条路。
    /// 字节基线正好是一整批真实输出 —— 把它们逐行过一遍文法,才算把缝合上。
    ///
    /// 判的是**形态**不是措辞:凡是写成 "N of M X" 的地方,M 必须真的大于 N。
    /// 一条 "12 of 12 defs" 说明三态被写成了两态,而这正是三态文法要省掉的那些字节。
    /// </summary>
    [Fact]
    public void 基线里没有伪截断的计数()
    {
        foreach (var (file, line) in BaselineLines())
        {
            foreach (Match m in Regex.Matches(line, @"\b(\d+) of (\d+) ([a-z ]+?)\b"))
            {
                var shown = int.Parse(m.Groups[1].Value);
                var total = int.Parse(m.Groups[2].Value);
                Assert.True(total > shown,
                    $"{file}: '{m.Value}' is written as truncated but nothing was cut off.");
            }
        }
    }

    /// <summary>
    /// 基线里出现的每个可数名词都得是登记过的复数形态。
    /// 这一条抓的是「在别处手拼了一个复数」—— 那种写法绕过登记处,登记表的闸看不见它。
    /// </summary>
    [Fact]
    public void 基线里的复数都是登记过的形态()
    {
        var singulars = NounRegistry.Known.ToList();
        var plurals = singulars.ToDictionary(n => n, n => NounRegistry.Form(n, 2));

        foreach (var (file, line) in BaselineLines())
        {
            // 名词可能是多词的(field path / def type / source tree)。只截一个词去比,
            // 「2 field paths」里的 "field" 会被拿去跟单词名词 field 对,判成该写 "fields" ——
            // 一句完全正确的话被判红。所以按**最长登记名词**匹配,先试两词再退回一词。
            foreach (Match m in Regex.Matches(line, @"\b(\d+) ([a-z]+(?: [a-z]+)?)\b"))
            {
                var count = int.Parse(m.Groups[1].Value);
                var phrase = m.Groups[2].Value;
                var oneWord = phrase.Split(' ')[0];

                var singular = singulars.FirstOrDefault(s => s == phrase || plurals[s] == phrase)
                            ?? singulars.FirstOrDefault(s => s == oneWord || plurals[s] == oneWord);
                if (singular is null) continue;

                // 判的是与实际写出来的那一段对不对,而不是与截断出来的那一段。
                var noun = singular == phrase || plurals[singular] == phrase ? phrase : oneWord;

                var expected = NounRegistry.Form(singular, count);
                Assert.True(noun == expected,
                    $"{file}: '{m.Value}' should read '{count} {expected}'.");
            }
        }
    }

    /// <summary>
    /// 产地侧闸。上面那条判的是**基线里出现过的**句子,于是一条只在少见分支上才打印的
    /// 手拼计数(`{ids.Count} mods`)永远等不到红灯。
    ///
    /// 这里改判源码:插值里出现一个数,紧跟着一个登记过的名词,而那个插值又没走登记处,
    /// 就是在自己拼单复数。手拼的地方迟早遇到 1,而「1 mods」说明**这个数与这个名词的
    /// 对应关系是当场编的**。
    /// </summary>
    [Fact]
    public void 源码里没有绕开登记处的手拼计数()
    {
        var words = NounRegistry.Known
            .SelectMany(n => new[] { n, NounRegistry.Form(n, 2) })
            .OrderByDescending(w => w.Length)
            .Select(Regex.Escape);
        // `{…}` 之后允许夹一个修饰词(「and 3 more def types」),再往后就不是这个数在数的东西了。
        var suspect = new Regex(@"\{([^{}]*)\}\s+(?:more\s+)?(" + string.Join("|", words) + @")\b");

        var dir = Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Core");
        var flagged = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in suspect.Matches(line))
                {
                    // 走了登记处的那一种正是我们要的写法;名词字面量本身(Render("mod"))也不算。
                    if (m.Groups[1].Value.Contains("Render(", StringComparison.Ordinal)) continue;
                    if (!LooksNumeric(m.Groups[1].Value)) continue;
                    flagged.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Value.Trim()}");
                }
            }
        }

        Assert.True(flagged.Count == 0,
            "These count these nouns without going through NounRegistry:\n  " + string.Join("\n  ", flagged));
    }

    /// <summary>
    /// 印给使用者看的句子里不许出现 <c>--limit all</c>。
    ///
    /// <c>--limit</c> 只收正整数,<c>all</c> 是用法错误 —— 而「怎么看全部」这句话正长在
    /// 截断提示上,读者照着敲就是一条报错。此前 types / members / callers 三条都这么写着。
    /// 规范说法只有一句,产地是参数解析自己报错时说的那句:留空。
    ///
    /// 只扫字符串字面量,注释不算 —— 注释里引用这个写法是在讲历史用量,不是在教人敲。
    /// </summary>
    [Fact]
    public void 提示句里不教人敲limit_all()
    {
        var suspect = new Regex(@"--limit\s+all", RegexOptions.IgnoreCase);
        var dir = Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Core");
        var flagged = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                    continue;
                if (!suspect.IsMatch(line)) continue;
                // 行尾注释里的不算:整行里 `"` 之前就出现的才是字面量。
                var quote = line.IndexOf('"');
                var hit = suspect.Match(line).Index;
                if (quote < 0 || hit < quote) continue;
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0 && hit > comment) continue;
                flagged.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(flagged.Count == 0,
            "'--limit all' is a usage error, so no visible sentence may suggest it. " +
            "Say 'Leave --limit out' instead:\n  " + string.Join("\n  ", flagged));
    }

    /// <summary>
    /// 这段插值里装的是不是一个数。判不准的代价不对称:漏判只是少守一处,误判会把
    /// <c>$"{Extension} file"</c> 这种「常量 + 恰好同形的名词」判红,逼人把正确的句子改坏。
    /// 所以只认三种明确形态:<c>.Count</c>/<c>.Length</c> 结尾、含算术、以及小写起头的局部变量
    /// (计数变量在这份代码里一律是局部量,而常量与属性名一律大写起头)。
    /// </summary>
    private static bool LooksNumeric(string expr)
    {
        var e = expr.Trim();
        if (e.Length == 0) return false;
        if (e.EndsWith(".Count", StringComparison.Ordinal) || e.EndsWith(".Length", StringComparison.Ordinal)) return true;
        if (e.Contains(" - ", StringComparison.Ordinal) || e.Contains(" + ", StringComparison.Ordinal)) return true;
        var last = e[(e.LastIndexOf('.') + 1)..];
        return last.Length > 0 && char.IsLower(last[0]);
    }

    /// <summary>
    /// <c>--json</c> 的顶层键名进了声明层(<see cref="JsonKeySpec"/>),这里拿**真跑出来的
    /// 输出**验那份声明:出现过而没声明的键会红,于是文档不可能落后于实现。
    /// 没有声明,消费方只能先猜键,猜错拿到 null —— 而那与「查到了但确实没有」在下游同形。
    /// </summary>
    [Fact]
    public void 每个json顶层键都在声明里()
    {
        var registry = new CommandRegistry();
        var undeclared = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (name, argv) in OutputSnapshotTests.Cases.Select(c => ((string)c[0], (string[])c[1])))
        {
            if (argv.Contains("--help") || argv.Contains("--json")) continue;
            var (command, _) = registry.Resolve(argv);
            if (command is null || !command.Spec.UsesGlobals) continue;

            var (stdout, _, _) = Fixture.Run([.. argv, "--json"]);
            if (stdout.Length == 0) continue;

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            var declared = command.Spec.JsonKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 这两个是每条命令都可能有的输出元数据,不是谁的数据键,不进各自的声明。
                if (prop.Name is "notes" or "snapshot") continue;
                if (!declared.Contains(prop.Name))
                    undeclared.Add($"{command.Spec.Name} emits '{prop.Name}' (seen in case '{name}')");
            }
        }

        Assert.True(undeclared.Count == 0,
            "These --json keys are not declared in CommandSpec.JsonKeys:\n  " + string.Join("\n  ", undeclared));
    }

    /// <summary>
    /// 反方向:声明了却从没产出过的键是一句空承诺。只对**基线覆盖到的命令**判 ——
    /// 没有实测输出的命令这里判不了,那是覆盖率的问题,不该在这条闸上假装守住了。
    /// </summary>
    [Fact]
    public void 声明过的json键都真的产出过()
    {
        var registry = new CommandRegistry();
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var argv in OutputSnapshotTests.Cases.Select(c => (string[])c[1]))
        {
            if (argv.Contains("--help") || argv.Contains("--json")) continue;
            var (command, _) = registry.Resolve(argv);
            if (command is null || !command.Spec.UsesGlobals) continue;

            var (stdout, _, _) = Fixture.Run([.. argv, "--json"]);
            if (stdout.Length == 0) continue;

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            if (!seen.TryGetValue(command.Spec.Name, out var keys)) seen[command.Spec.Name] = keys = [];
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name != "notes") keys.Add(prop.Name);
        }

        var never = new List<string>();
        foreach (var (cmd, keys) in seen)
            foreach (var declared in registry.Specs.Single(s => s.Name == cmd).JsonKeys)
                if (!keys.Contains(declared.Key))
                    never.Add($"{cmd} declares '{declared.Key}', which no baseline case produces");

        Assert.True(never.Count == 0, string.Join("\n  ", never));
    }

    /// <summary>
    /// 基线里不许出现教人绕路的话。CLI 该做的事不该让调用方替它做 ——
    /// 上游就是这么把 '*' 推给调用方的。
    /// </summary>
    [Fact]
    public void 基线里没有教人绕路的措辞()
    {
        string[] banned =
        [
            "always prefix", "add a '*'", "append '*'", "you must add",
            "remember to", "don't forget", "as a workaround", "manually add",
        ];

        foreach (var (file, line) in BaselineLines())
            foreach (var phrase in banned)
                Assert.False(line.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                    $"{file} teaches a workaround: '{line.Trim()}'. Fix the CLI instead.");
    }

    /// <summary>
    /// 举例子的名单被截断时,读者必须能算出**没举出来的有几个**。
    ///
    /// 裸 <c>", …"</c> 读出来是「大概就这些」,与「一共就这几个」逐字同形。
    ///
    /// 合法只有两种:走 <see cref="NameList"/>(它自己接 and N more),或者在同一句里
    /// 先把总数说出来(<c>ScopeFilter.Describe</c> 的「= 22 mods: a, b, c, …」—— 分母
    /// 已经在场,再补一句「还有 19 个」是复述)。所以豁免按**方法**列,不按文件。
    /// </summary>
    [Fact]
    public void 名单截断时不许把数量省成省略号()
    {
        // 这一条的产地是 ScopeFilter.Describe:它在省略号**之前**已经给出了 mod 总数。
        string[] exempt = ["ScopeFilter.cs"];

        var dir = Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Core");
        var flagged = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (exempt.Contains(Path.GetFileName(file), StringComparer.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                // 判的是「逗号接省略号」这个形状本身,不管它后面拼的是什么 —— 收尾写死成
                // ", …" 与 ", …)" 的话,`{Join(…)}, …"` 两种都不是,红不了。
                if (line.Contains(", …", StringComparison.Ordinal))
                    flagged.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(flagged.Count == 0,
            "These truncate a list into '…' without saying how many are hidden. Use NameList.Render, " +
            "or state the total in the same sentence:\n  " + string.Join("\n  ", flagged));
    }

    /// <summary>
    /// 「你是不是想打这个」只有一种说法 —— 同一件事多种措辞,读的人会以为差别有意义。
    ///
    /// 两处豁免,各有实质理由:
    /// - <c>CodeSearchCommand.NoSuchTree</c> 在名单后面还要说「树名是 packageId,外号匹配
    ///   不上任何东西」—— 那是这条命令独有的成因;
    /// - <c>where</c> 的值域近似先做**末段精确匹配**(CompAmbientSound 对
    ///   RimWorld.CompAmbientSound 是同一个名字,不是「长得像」),说 by spelling 是假话。
    /// </summary>
    [Fact]
    public void 近似候选的措辞只有一个产地()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Suggestion.cs",          // 产地
            "CodeSearchCommand.cs",   // 树名:名单后面接一句独有的成因
            "QueryCommands.cs",       // find 值域:不是拼写近似
        };

        // 判的是**字面量里**的 Closest,不是标识符 —— `Suggestion.Closest(pool, name)` 正是
        // 我们要的写法,把它一起判红,这道闸就变成了「不许调用产地」。
        var inLiteral = new Regex("\"[^\"]*Closest");

        var dir = Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Core");
        var flagged = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!inLiteral.IsMatch(lines[i])) continue;
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                if (allowed.Contains(Path.GetFileName(file))) continue;
                flagged.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(flagged.Count == 0,
            "These word the near-miss suggestion themselves. Route it through Suggestion.Say:\n  " +
            string.Join("\n  ", flagged));
    }

    /// <summary>
    /// **输出自己指的路必须走得通。** 命令改名、子命令合并、开关换词,散在几十条散文里的
    /// 指路当场变成假话,而假话与真话在输出里逐字同形。
    ///
    /// 判的是**基线**而不是源码:源码里那些字符串带插值,拼不出完整命令行;基线是真跑出来的。
    /// 代价是没进基线的指路守不到,所以下面顺带断言基线里指路的条数不能突然掉下去。
    /// </summary>
    [Fact]
    public void 输出里指的每条命令都真的存在()
    {
        var registry = new CommandRegistry();
        // 命令名后面可能跟子命令(「sources sync」),再往后是位置参数与开关 —— 一律不管,
        // 这里只判**命令名解析得出来**。Resolve 自己会吃掉一到两段。
        var invocation = new Regex(CommandRegistry.ExeName + @" ([a-z][a-z-]*)(?: ([a-z][a-z-]*))?");

        var seen = 0;
        foreach (var (file, line) in BaselineLines())
        {
            // 基线首行是回显的命令行本身,它是被测输入不是指路。
            if (line.StartsWith("$ ", StringComparison.Ordinal)) continue;
            foreach (Match m in invocation.Matches(line))
            {
                var argv = new List<string> { m.Groups[1].Value };
                if (m.Groups[2].Success) argv.Add(m.Groups[2].Value);
                var (command, _) = registry.Resolve(argv);

                // 两段解析不出来时退回一段:「rimsearcher search shield」里 shield 是查询词。
                if (command is null && argv.Count == 2)
                    (command, _) = registry.Resolve([argv[0]]);

                // 拼错的命令名是有意的语料(「did you mean」那条基线回显的就是错名)。
                if (command is null && line.Contains("did you mean", StringComparison.OrdinalIgnoreCase)) continue;

                Assert.True(command is not null,
                    $"{file} points at '{m.Value}', but there is no such command:\n  {line.Trim()}");
                seen++;
            }
        }

        Assert.True(seen > 20, $"Only {seen} pointers found across the baselines; the scanner is probably broken.");
    }

    /// <summary>
    /// 零结果不许是死路:退出码 1 的输出必须给出**下一步能敲什么**。
    ///
    /// 它守的是「工具说没有,然后没了」这一类;**指错方向**那一类机器分辨不出来,这里守不住。
    ///
    /// 「下一步」认三种:另一条命令、一个能改的开关、一处能改的配置。
    /// </summary>
    [Fact]
    public void 零结果不许是死路()
    {
        var deadEnds = new List<string>();
        foreach (var file in Directory.EnumerateFiles(OutputSnapshotTests.SnapshotDir, "*.txt"))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("\nexit 1\n", StringComparison.Ordinal)) continue;
            // 去掉回显的命令行与 `--- stdout ---` 那两条分隔线 —— 分隔线自带 `--`,
            // 留着的话每一份基线都「提到了一个开关」,这道闸就恒绿。
            var body = string.Join("\n", text.Split('\n')
                                             .Skip(1)
                                             .Where(l => !l.StartsWith("--- ", StringComparison.Ordinal)));
            if (body.Contains(CommandRegistry.ExeName + " ", StringComparison.Ordinal)) continue;
            if (body.Contains("--", StringComparison.Ordinal)) continue;
            if (body.Contains("Set '", StringComparison.Ordinal)) continue;
            deadEnds.Add(Path.GetFileName(file));
        }

        Assert.True(deadEnds.Count == 0,
            "These end with 'there is none' and no way forward:\n  " + string.Join("\n  ", deadEnds));
    }

    /// <summary>
    /// 「没量过」不许印成一个数。
    ///
    /// 导出器的 patch_ops 计数正则只认 <c>@Name=</c>,无 <c>Name=</c> 的节点若硬写 0,
    /// 「量过了、确实没人 patch」与「这一格根本没量」就**逐字相同**。
    ///
    /// 字节基线单独挡不住它:改了代码顺手重生成基线,红就没了。所以这道闸读的是基线的
    /// **结构** —— identity 块里印出数字的 patch_ops,同一块里必须有 name 行(名字为空时
    /// Renderers 会整行跳过,所以「有 name 行」正好等价于「这个节点声明了 Name=」)。
    /// </summary>
    [Fact]
    public void 没量过的计数不许印成一个数()
    {
        var unmeasured = new List<string>();
        foreach (var file in Directory.EnumerateFiles(OutputSnapshotTests.SnapshotDir, "*.txt"))
        {
            var lines = File.ReadAllText(file).Split('\n');
            // 块之间以空行分隔;name/patch_ops 是同一个 identity 块里的两行。
            var blockHasName = false;
            foreach (var line in lines)
            {
                if (line.Length == 0) { blockHasName = false; continue; }
                if (line.StartsWith("name ", StringComparison.Ordinal)) blockHasName = true;
                if (!line.StartsWith(InheritCommand.PatchOpsName + " ", StringComparison.Ordinal)) continue;
                var cell = line[(InheritCommand.PatchOpsName + " ").Length..].Trim();
                if (cell.Length > 0 && char.IsAsciiDigit(cell[0]) && !blockHasName)
                    unmeasured.Add($"{Path.GetFileName(file)}: '{line.Trim()}' on a node with no Name=");
            }
        }

        Assert.True(unmeasured.Count == 0,
            "A count that was never taken is printed as a number, so it reads exactly like a measured zero:\n  " +
            string.Join("\n  ", unmeasured));
    }

    /// <summary>输出契约在基线上的落点:不许有行尾空格,不许有 CR。</summary>
    [Fact]
    public void 基线里没有行尾空格也没有CR()
    {
        foreach (var file in Directory.EnumerateFiles(OutputSnapshotTests.SnapshotDir, "*.txt"))
        {
            var raw = File.ReadAllText(file);
            Assert.DoesNotContain('\r', raw);
            foreach (var line in raw.Split('\n'))
                Assert.Equal(line.TrimEnd(), line);
        }
    }

    /// <summary>
    /// 写基线的类与读基线的类不许并行跑。
    ///
    /// xUnit 默认每个测试类自成一个 collection,collection 之间并行。于是带
    /// <c>RIMSEARCHER_UPDATE_SNAPSHOTS=1</c> 时,<see cref="OutputSnapshotTests"/> 的
    /// <c>File.WriteAllText</c> 与这里的 <c>File.ReadAllText</c> 会落在同一份 .txt 上:
    /// Windows 的共享语义下写方持 Write/FileShare.Read、读方要 Read/FileShare.Read,
    /// 两边互不相容 —— 一次 IOException,只红一条,原样重跑就绿。
    ///
    /// 竞态本身立不出一道稳定红的闸(它按时序发作),所以这里判的是**结构**:
    /// 凡是碰基线目录的测试类,都得挂同一个 collection 名。少挂一个就红,
    /// 而红的时机与机器快慢无关。
    /// </summary>
    [Fact]
    public void 读写基线的测试类同属一个collection()
    {
        var dir = Path.Combine(DeclarationTests.RepoRoot(), "Sources", "RimSearcher.Tests");
        var offenders = new List<string>();

        foreach (var type in typeof(GateTests).Assembly.GetTypes().OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!type.IsClass || type.IsAbstract) continue;
            if (!type.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), true).Length > 0)) continue;

            var file = Path.Combine(dir, type.Name + ".cs");
            if (!File.Exists(file)) continue;

            // 判据按**产地**走,不按标识符名:基线目录只有 OutputSnapshotTests.SnapshotDir
            // 这一个产地(它自己在类内裸用)。此前这里匹配裸 `SnapshotDir`、只排掉 Fixture.
            // 前缀,于是每一个同名成员都会被误抓 —— RimConfig.SnapshotDir 是配置里的快照库
            // 路径,与基线目录毫无关系,写一句 `new RimConfig { SnapshotDir = ... }` 就被判成
            // 「碰了基线」,然后为一场不存在的竞态被拖进串行。同名不是同物,而按名字匹配
            // 读不出这个区别:两边在源码里长得一模一样。
            if (type != typeof(OutputSnapshotTests) &&
                !File.ReadAllText(file).Contains("OutputSnapshotTests.SnapshotDir", StringComparison.Ordinal))
                continue;

            // xunit 2.x 的 CollectionAttribute 只有构造参数、没有 Name 属性,只能读 attribute data。
            var name = type.GetCustomAttributesData()
                           .Where(a => a.AttributeType == typeof(CollectionAttribute))
                           .Select(a => a.ConstructorArguments[0].Value as string)
                           .FirstOrDefault();
            if (name != OutputSnapshotTests.Collection)
                offenders.Add($"{type.Name}: {(name is null ? "没有 [Collection]" : $"[Collection(\"{name}\")]")}");
        }

        Assert.True(offenders.Count == 0,
            $"These test classes touch the baseline directory but are not in the " +
            $"'{OutputSnapshotTests.Collection}' collection, so xUnit may run them in parallel and " +
            "their reads and writes will collide on the same .txt:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 每条 sqlite 连接都不入池,而清全进程连接池那句话一处都不许有。
    ///
    /// Microsoft.Data.Sqlite 的连接池在 <c>Dispose</c> 之后**仍开着那个文件**,而快照文件
    /// 随后要被轮转(<see cref="RimSearcher.Snapshot.SnapshotRetention.Install"/> 的
    /// Move/Delete)—— Windows 上开着的文件动不了。曾经的补救是
    /// <c>SqliteConnection.ClearAllPools()</c>,而它是**进程级**的:它把别的线程正在用的
    /// 连接一起处置掉,那边随后在自己的连接上收到 <c>ObjectDisposedException</c>。
    /// 一次实测就落在导入侧 <c>db.Open()</c> 之后的建表上,而发作的类完全看运气 ——
    /// xUnit 的 collection 之间并行,于是整套测试随机红一条、隔离跑必绿。
    ///
    /// 竞态按时序发作,立不出稳定红的闸,所以这里判**结构**:连接自带
    /// <c>Pooling=false</c>(那样 Close 就是真关文件,谁也不必去动别人的连接),
    /// 且没有任何一处调用 <c>ClearAllPools</c>。
    /// </summary>
    [Fact]
    public void sqlite连接不入池且没人去清全进程的池()
    {
        // 拼出来而不是写成一个字面量:这条闸自己也在被扫的目录里,写全了它第一个违规。
        const string Banned = "Clear" + "AllPools";

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(DeclarationTests.RepoRoot(), "Sources"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var name = Path.GetFileName(file);
            // 注释行剔掉:这条规矩的成因**就得写在注释里**(几处 `Pooling=false` 旁边都指着
            // 它),而把注释一起读进来的话,解释为什么不许调用 ClearAllPools 的那句话
            // 自己就是一条违规。行号照旧从原文数,所以这里换空行而不是删行。
            var text = string.Join("\n", File.ReadAllLines(file)
                .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l));
            if (text.Contains(Banned, StringComparison.Ordinal))
                offenders.Add($"{name}: 调用了 {Banned}() —— 它连别的线程正在用的连接一起处置。");

            foreach (Match m in Regex.Matches(text, @"new (?:Microsoft\.Data\.Sqlite\.)?SqliteConnection\("))
            {
                // 连接串与 builder 两种写法都认:";Pooling=False" 与 "Pooling = false,"。
                var tail = text.Substring(m.Index, Math.Min(400, text.Length - m.Index));
                if (!Regex.IsMatch(tail, @"Pooling\s*=\s*[Ff]alse"))
                    offenders.Add($"{name}: 第 {text.Take(m.Index).Count(c => c == '\n') + 1} 行的连接没有 Pooling=false。");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders.Prepend("")));
    }

    private static IEnumerable<(string File, string Line)> BaselineLines()
    {
        Assert.True(Directory.Exists(OutputSnapshotTests.SnapshotDir),
            "No baselines to check; run the snapshot tests with RIMSEARCHER_UPDATE_SNAPSHOTS=1 first.");

        var any = false;
        foreach (var file in Directory.EnumerateFiles(OutputSnapshotTests.SnapshotDir, "*.txt"))
        {
            any = true;
            var name = Path.GetFileName(file);
            foreach (var line in File.ReadAllLines(file))
                yield return (name, line);
        }
        Assert.True(any, "The baseline directory is empty.");
    }
}
