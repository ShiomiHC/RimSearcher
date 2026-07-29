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
            foreach (Match m in Regex.Matches(line, @"\b(\d+) ([a-z]+)\b"))
            {
                var count = int.Parse(m.Groups[1].Value);
                var noun = m.Groups[2].Value;

                // 只管我们登记过的那批词的单复数;句子里别的词不归这道闸。
                var singular = singulars.FirstOrDefault(s => s == noun || plurals[s] == noun);
                if (singular is null) continue;

                var expected = NounRegistry.Form(singular, count);
                Assert.True(noun == expected,
                    $"{file}: '{m.Value}' should read '{count} {expected}'.");
            }
        }
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
