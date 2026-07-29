using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RimSearcher.Tests;

// 词表与产品的**同步**闸。
//
// GrammarRules.CountedNouns 那张表的注释从落地那天起就写着「新加一个计数名词却不登记在这里，
// 那个槽位的单复数就没人守」——警告在，机制不在。于是它真的漂了：28 项里 `changed sources`
// 在产品侧一处字面量都没有（`git log -S'"changed sources"' -- Sources/` 只有引入 CountedNouns
// 的那一个 commit），而产品那边一直叫 `checked sources`，那个词从没进过表。这一格从第一天起
// 既守不住任何东西、也没有任何人守。
//
// 隔壁 OutputGrammarGateTests 抓不到这一形：它吃的是**输出文本**，而输出里那个名词写成什么样
// 就是什么样，「它有没有被登记」在文本里看不见。故这一条改吃**产品源码**。
//
// 两个方向各有各的判据，且都是纯文本判定——不共用产品的任何判断（§3 判据六）：
//   - 正向：`NounFor` / `Quantity` 第二个实参上的字面量，按定义就是计数名词，必须在表里；
//   - 反向：表里的词必须在产品源码里出现过，否则它守的是一句没人说的话。
//
// 反向那条有一个已知的盲区：`defs` 曾经是死项，却因为 AppConfig 里有个同名**配置键**而看起来
// 活着。文本判定分不出这两者，故它是靠手工核对删掉的（见本轮 commit）。要把这个盲区也堵上，
// 得让计数名词有自己的类型（`单一产地重构指导` 的 M3），那是另一步。
public class CountedNounRegistryTests
{
    // `OutputText.NounFor(n, "X")` / `OutputText.Quantity(n, "X")`。第一个实参允许含一层括号
    // （`value.GetArrayLength()`），不允许含字符串——那样第二个槽就认错了。
    private static readonly Regex NounLiteral = new(
        @"\b(?:NounFor|Quantity)\(\s*[^(),""]*(?:\([^()""]*\))?[^(),""]*,\s*""(?<noun>[^""]+)""",
        RegexOptions.Compiled);

    // 退化守卫。正则一旦因为调用写法变化而失配，这条闸会静静地全绿——与矩阵那边的 Expect
    // 同一个理由：查不到东西的检查与查过了都合格的检查，结果长得一模一样。
    //
    // 落地时实测 29 处，这里取 20：产品新增调用只会让它更宽松，而真的掉到 20 以下时，要么是
    // 正则跟不上写法了，要么是构词入口被收敛了（M3 那一步就会这样）——两种都该被逼着重看一眼。
    private const int KnownCallSites = 20;

    [Fact]
    public void EveryCountedNounLiteralInTheProduct_IsRegistered()
    {
        var unregistered = new List<string>();
        var seen = 0;

        foreach (var (path, text) in ProductSources())
        foreach (Match m in NounLiteral.Matches(text))
        {
            seen++;
            var noun = m.Groups["noun"].Value;
            if (GrammarRules.IsRegisteredCountedNoun(noun)) continue;
            unregistered.Add($"{Path.GetFileName(path)}：'{noun}' 走了构词却不在 CountedNouns 里");
        }

        Assert.True(
            seen >= KnownCallSites,
            $"只扫到 {seen} 处构词调用（至少应有 {KnownCallSites} 处）——正则没跟上产品的写法，"
            + "这道闸已经形同虚设。");

        Assert.True(unregistered.Count == 0,
            $"{unregistered.Count} 个计数名词没登记，它们的单复数没人守：\n"
            + string.Join("\n", unregistered.Distinct()));
    }

    [Fact]
    public void EveryRegisteredCountedNoun_IsSpokenByTheProduct()
    {
        var sources = ProductSources().Select(s => s.Text).ToList();

        var dead = GrammarRules.RegisteredCountedNouns
            .Where(noun => !sources.Any(t => t.Contains($"\"{noun}\"", StringComparison.Ordinal)))
            .ToList();

        Assert.True(dead.Count == 0,
            $"{dead.Count} 个词登记了却没有任何产品字面量对应，它们守的是没人说的话：\n"
            + string.Join("\n", dead));
    }

    private static IEnumerable<(string Path, string Text)> ProductSources(
        [CallerFilePath] string here = "")
    {
        var sources = Directory.GetParent(Path.GetDirectoryName(here)!)!.FullName;

        foreach (var project in new[] { "RimSearcher.Core", "RimSearcher.Server" })
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(sources, project), "*.cs", SearchOption.AllDirectories))
        {
            // 构建产物里有生成的 .cs（AssemblyInfo 之类），它们不是产品文本。
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            yield return (file, File.ReadAllText(file));
        }
    }
}
