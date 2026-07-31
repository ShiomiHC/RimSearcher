using RimSearcher.Cli;
using RimSearcher.Commands;

namespace RimSearcher.Tests;

/// <summary>
/// 声明层的名单侧闸。事实侧(真跑进程读 stdout)在 <see cref="ProcessTests"/>。
///
/// 01 的教训:「两侧立闸」的另一侧不许是另一份声明 —— schema 验 schema,两边同时错照绿。
/// 所以这里只判那些能从声明本身判定的性质(唯一性、冲突、措辞纪律),凡是「实际行为对不对」
/// 一律推给事实侧。
/// </summary>
public class DeclarationTests
{
    private static readonly CommandRegistry Registry = new();

    [Fact]
    public void 每条命令的名字唯一()
    {
        var names = Registry.Specs.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 命令别名不与任何命令名或别名冲突()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in Registry.Specs)
        {
            foreach (var key in new[] { spec.Name }.Concat(spec.Aliases))
            {
                var norm = ArgParser.Normalize(key);
                Assert.False(seen.TryGetValue(norm, out var owner) && owner != spec.Name,
                    $"'{key}' is claimed by both '{owner}' and '{spec.Name}'.");
                seen[norm] = spec.Name;
            }
        }
    }

    /// <summary>
    /// 别名的产地唯一:同一条命令里,归一化之后两个参数不许撞名。撞了就是「调用方写对了名字
    /// 却打到了另一个参数上」,比未知 flag 更隐蔽。
    /// </summary>
    [Fact]
    public void 同一命令内参数名与别名归一化后互不冲突()
    {
        foreach (var spec in Registry.Specs)
        {
            var options = spec.UsesGlobals
                ? spec.Options.Concat(GlobalOptions.All).ToList()
                : spec.Options.ToList();

            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var o in options)
                foreach (var key in new[] { o.Name }.Concat(o.Aliases))
                {
                    var norm = ArgParser.Normalize(key);
                    Assert.False(seen.TryGetValue(norm, out var owner) && owner != o.Name,
                        $"In '{spec.Name}', '{key}' would resolve to both --{owner} and --{o.Name}.");
                    seen[norm] = o.Name;
                }
        }
    }

    [Fact]
    public void 每条声明都有非空说明且是完整句子()
    {
        foreach (var spec in Registry.Specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Summary), $"'{spec.Name}' has no summary.");
            Assert.EndsWith(".", spec.Summary.TrimEnd());

            foreach (var p in spec.Positionals)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Help), $"<{p.Name}> of '{spec.Name}' has no help.");
                Assert.EndsWith(".", p.Help.TrimEnd());
            }

            foreach (var o in spec.Options)
            {
                Assert.False(string.IsNullOrWhiteSpace(o.Help), $"--{o.Name} of '{spec.Name}' has no help.");
                Assert.EndsWith(".", o.Help.TrimEnd());
            }
        }
    }

    /// <summary>
    /// 声明里出现的上限数字必须来自 <see cref="Limits"/>(master SearchRegexTool.Description 范式)。
    /// 判据是「散文里那个数与常量当前值一致」——改了常量忘了改散文,这条会红。
    /// </summary>
    [Fact]
    public void 散文里的上限数字与常量同步()
    {
        var limitHelp = CommonOptions.Limit("defs").Help;
        Assert.Contains(Limits.MaxLimit.ToString(), limitHelp);

        var codeSearch = new CodeSearchCommand().Spec;
        Assert.Contains(Limits.CodeSearchMatchesPerFile.ToString(),
            string.Join(" ", codeSearch.Options.Select(o => o.Help)) + codeSearch.Remarks);
    }

    /// <summary>
    /// 发布缝(需求口径 1):错误与声明文本不写死本机路径。写死了,别人机器上照抄就是错的。
    /// </summary>
    [Fact]
    public void 声明文本不含本机绝对路径()
    {
        foreach (var spec in Registry.Specs)
        {
            var all = spec.Summary + spec.Remarks + string.Join(" ", spec.Options.Select(o => o.Help))
                    + string.Join(" ", spec.Examples);
            Assert.DoesNotContain(":\\", all);
            Assert.DoesNotContain("C:/", all);
            Assert.DoesNotContain("CCH", all);
        }
    }

    /// <summary>
    /// 04 验收条款:skill 与声明都不许教调用方绕自家缺陷。上游的
    /// 「Always prefix-search shield*」是反例 —— 那种句子出现,说明该修的是 CLI。
    /// </summary>
    [Fact]
    public void 声明不教调用方绕过自家缺陷()
    {
        var banned = new[] { "always prefix", "add a '*'", "add an asterisk", "remember to append", "you must add" };
        foreach (var spec in Registry.Specs)
        {
            var all = (spec.Summary + " " + spec.Remarks + " " +
                       string.Join(" ", spec.Options.Select(o => o.Help))).ToLowerInvariant();
            foreach (var phrase in banned)
                Assert.False(all.Contains(phrase),
                    $"'{spec.Name}' tells the caller to work around the tool: \"{phrase}\".");
        }
    }

    [Fact]
    public void 每条命令的示例都能被解析器接受()
    {
        foreach (var spec in Registry.Specs)
        {
            foreach (var example in spec.Examples)
            {
                var argv = SplitCommandLine(example);
                Assert.Equal(CommandRegistry.ExeName, argv[0]);

                var (command, rest) = Registry.Resolve(argv.Skip(1).ToList());
                Assert.True(command is not null, $"Example does not resolve to a command: {example}");
                Assert.Equal(spec.Name, command!.Spec.Name);

                var parsed = ArgParser.Parse(command.Spec, GlobalOptions.All, rest);
                Assert.True(parsed.Errors.Count == 0,
                    $"Example is rejected by the parser: {example}\n  {string.Join("\n  ", parsed.Errors)}");
            }
        }
    }

    /// <summary>
    /// 每个 <c>Render("noun")</c> 用到的名词都必须登记过。
    ///
    /// 登记处的设计是「没登记就抛」,方向对,但落点错了:那一抛发生在**用户面前**,
    /// 表现为一条裸栈追踪。加一个名词是件小事,它却让一条正常查询整个失败 ——
    /// 实测里就是这样炸的。把闸挪到这里,漏登记在提交前就是红的。
    ///
    /// 扫源码是有意的:名词是字符串字面量,没有类型系统能替它把关,
    /// 而「跑一遍所有命令的所有分支」根本做不到 —— 报错分支恰恰是最难跑到的那些。
    /// </summary>
    [Fact]
    public void 代码里渲染过的每个名词都登记过()
    {
        var used = NounsUsedInCode();
        Assert.NotEmpty(used);
        var missing = used.Where(n => !Output.NounRegistry.IsRegistered(n)).ToList();
        Assert.True(missing.Count == 0,
            $"Rendered but not registered in NounRegistry: {string.Join(", ", missing)}.");
    }

    /// <summary>登记了却没人用的名词也是债:表越长,越没人敢动它。</summary>
    [Fact]
    public void 登记表里没有无人使用的名词()
    {
        var used = NounsUsedInCode();
        var unused = Output.NounRegistry.Known.Where(n => !used.Contains(n)).ToList();
        Assert.True(unused.Count == 0, $"Registered but never used: {string.Join(", ", unused)}.");
    }

    /// <summary>
    /// 名词有**三个**入口:直接 <c>Render("x")</c>,以及交给 <c>CountNotice</c> /
    /// <c>TruncationNotice</c> 由它们去渲染。漏掉任何一个,走那条路的名词都会被判成
    /// 「没人用」—— 第一版漏了 TruncationNotice 就红过一次,这一版拆出 CountNotice 时又红了一次。
    /// 每加一个会渲染名词的方法,这里都得跟上;判据是「代码里有没有把名词交出去」,
    /// 不是「哪个方法名」。
    /// </summary>
    private static SortedSet<string> NounsUsedInCode()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "Sources", "RimSearcher.Core"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"\.Render\(\s*""([^""]+)""\s*\)"))
                used.Add(m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         text, @"(?:Count|Truncation)Notice\([^;]*?,\s*""([^""]+)""\s*,"))
                used.Add(m.Groups[1].Value);
        }
        return used;
    }

    internal static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Sources")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    internal static List<string> SplitCommandLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var c in line)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
