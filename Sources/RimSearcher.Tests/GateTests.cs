using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Commands;
using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 两道跨产物的闸:生成的参数参考必须与声明一致,基线里的每一行输出必须过文法。
/// </summary>
public class GateTests
{
    private static string ReferencePath =>
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
    /// SKILL.md 里写出来的每一条命令行都得真能跑。
    ///
    /// 参考页有逐字节闸是因为它是**生成产物**;SKILL.md 是手写的,反而一直没人守 ——
    /// 而 04 的口径是「skill 文档本身进入被测物」:模型照它拼命令行,写错一个开关,
    /// 代价是调用方白跑一轮。这里判的不是措辞,是**命令名与开关名在注册表里存不存在**。
    /// </summary>
    [Fact]
    public void skill文档里的命令行都能解析()
    {
        var registry = new CommandRegistry();
        var globals = GlobalOptions.All.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var text = File.ReadAllText(SkillPath).Replace("\r\n", "\n");

        var invocations = Regex.Matches(text, @"`" + CommandRegistry.ExeName + @"([^`]*)`");
        Assert.True(invocations.Count > 5, "SKILL.md suddenly names almost no commands; the scanner is probably broken.");

        foreach (Match inv in invocations)
        {
            var argv = Tokenize(inv.Groups[1].Value);
            if (argv.Count == 0) continue;                       // 光提 exe 名(「the rimsearcher CLI」)
            if (argv[0].StartsWith('<') || argv[0].StartsWith("--")) continue;  // 占位命令名

            var (command, rest) = registry.Resolve(argv);
            Assert.True(command is not null, $"SKILL.md invokes '{inv.Value}', but there is no such command.");

            var accepted = command!.Spec.Options
                                  .SelectMany(o => new[] { o.Name }.Concat(o.Aliases))
                                  .ToHashSet(StringComparer.Ordinal);
            foreach (var token in rest.Where(t => t.StartsWith("--", StringComparison.Ordinal)))
            {
                var name = token[2..].Split('=')[0];
                Assert.True(accepted.Contains(name) || (command.Spec.UsesGlobals && globals.Contains(name)),
                    $"SKILL.md writes '{inv.Value}', but '{command.Spec.Name}' does not accept '--{name}'.");
            }
        }
    }

    /// <summary>
    /// 收窄开关那张表:每一行的命令必须真的接受同一行里列出的每个开关。
    /// 上一条只看得见成句的命令行,而这张表把命令与开关拆在两个单元格里 ——
    /// 恰恰是最容易漂的形态(它的前身是一句「Every command takes --path, --type, --scope or --files」,
    /// 而当时 --type 只挂在一条命令上,四个 agent 各撞一次)。
    /// </summary>
    [Fact]
    public void skill文档的收窄开关表与声明一致()
    {
        var registry = new CommandRegistry();
        var text = File.ReadAllText(SkillPath).Replace("\r\n", "\n");

        var rows = Regex.Matches(text, @"^\| `([a-z-]+(?: [a-z-]+)?)` \| ((?:`--[a-z-]+`(?:, )?)+) \|$",
                                 RegexOptions.Multiline);
        Assert.True(rows.Count >= 5, "The narrowing table in SKILL.md was not found; the scanner needs updating.");

        foreach (Match row in rows)
        {
            var (command, _) = registry.Resolve(row.Groups[1].Value.Split(' '));
            Assert.True(command is not null, $"The narrowing table names '{row.Groups[1].Value}', which is not a command.");

            var accepted = command!.Spec.Options
                                  .SelectMany(o => new[] { o.Name }.Concat(o.Aliases))
                                  .ToHashSet(StringComparer.Ordinal);
            foreach (Match opt in Regex.Matches(row.Groups[2].Value, @"--([a-z-]+)"))
                Assert.True(accepted.Contains(opt.Groups[1].Value),
                    $"The narrowing table gives '{command.Spec.Name}' the option '--{opt.Groups[1].Value}', which it does not accept.");
        }
    }

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
    /// 01 留的那道缝:文法闸只判自己造的那几个 <see cref="Tally"/>,而真实输出走的是另一条路。
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
            // 一句完全正确的话被判红。这正是 1338603 那条教训的复发形态:用短子串重新
            // 声明「该怎么说」。所以按**最长登记名词**匹配,先试两词再退回一词。
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
    /// R7 的产地侧闸。上面那条判的是**基线里出现过的**句子,于是一条只在少见分支上
    /// 才打印的手拼计数(`{ids.Count} mods`)永远等不到红灯 —— 而 R7 那次正是这种。
    ///
    /// 这里改判源码:插值里出现一个数,紧跟着一个登记过的名词,而那个插值又没走登记处,
    /// 就是在自己拼单复数。手拼的地方迟早遇到 1,而「1 mods」不只是难看 —— 它说明
    /// **这个数与这个名词的对应关系是当场编的**,而 R7 的形状恰恰是编错了对应关系。
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
    /// R14 的事实侧闸。<c>--json</c> 的顶层键名此前只活在代码里,消费方只能先猜键再发命令,
    /// 猜错拿到 null —— 而那与「查到了但确实没有」在下游同形。键名进了声明层
    /// (<see cref="JsonKeySpec"/>)之后,这里拿**真跑出来的输出**验那份声明:
    /// 出现过而没声明的键会红,于是文档不可能落后于实现。
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
                if (prop.Name == "notes") continue;
                if (!declared.Contains(prop.Name))
                    undeclared.Add($"{command.Spec.Name} emits '{prop.Name}' (seen in case '{name}')");
            }
        }

        Assert.True(undeclared.Count == 0,
            "These --json keys are not declared in CommandSpec.JsonKeys:\n  " + string.Join("\n  ", undeclared));
    }

    /// <summary>
    /// 反方向:声明了却从没产出过的键是一句空承诺,而空承诺正是 R14 归因到 skill_doc 的那条
    /// (「nothing is lost」写着,键名没有)。只对**基线覆盖到的命令**判 —— 没有实测输出的
    /// 命令这里判不了,那是覆盖率的问题,不该在这条闸上假装守住了。
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
    /// 02-7 的 '*' 就是这么被上游推给调用方的。
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
