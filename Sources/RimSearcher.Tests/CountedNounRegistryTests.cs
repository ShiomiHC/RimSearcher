using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RimSearcher.Core;

namespace RimSearcher.Tests;

// 计数名词名单的**保鲜**闸。
//
// 「产品里有个计数名词没登记」这个方向已经不需要闸了：`CountedNoun` 是个类型，构造函数私有，
// 名词槽只收它——没登记的词编译期就传不进去。这一条此前由本文件的一个源码扫描断言守着
// （落地时它抓到了 `checked sources`），M3 之后由编译器接管，故删掉。
//
// 剩下的是**反方向**：名单里的词不再被任何人用了，编译器不会说话。`changed sources` 与
// `name keys` 当年逃掉的正是这一侧——两个词从没对应过任何产品字面量，而闸照绿了很久
// （见「单一产地重构指导」§2 甲）。
//
// M3 顺带堵掉了这一侧原先的盲区。此前这条判据是「表里的词必须在产品源码里作为字符串出现过」，
// 于是 `defs` 这个死项因为 `AppConfig` 里有个同名**配置键**而看起来活着，只能靠手工核对删掉。
// 现在查的是 `CountedNoun.Defs` 这样的**成员引用**——配置键名与计数名词在文本上不再同形，
// 盲区没有了。
public class CountedNounRegistryTests
{
    [Fact]
    public void EveryRegisteredNoun_IsActuallyUsedByTheProduct()
    {
        var sources = ProductSources().ToList();

        // 声明本身不算引用：CountedNoun.cs 里每一条都写着自己的名字。
        var uses = sources
            .Where(s => Path.GetFileName(s.Path) != "CountedNoun.cs")
            .Select(s => s.Text)
            .ToList();

        var dead = MemberNames()
            .Where(m => !uses.Any(t => Regex.IsMatch(t, $@"\bCountedNoun\.{Regex.Escape(m)}\b")))
            .ToList();

        Assert.True(dead.Count == 0,
            $"{dead.Count} 个词登记了却没有任何产品调用点，它们守的是没人说的话：\n"
            + string.Join("\n", dead));
    }

    // 名单与成员名一一对应。少一条说明有人给 CountedNoun 加了个不进 Registry 的字段——
    // 那样它在闸这边隐形（GrammarRules 一路取的都是 `CountedNoun.All`），产品那边却照用，
    // 于是那个槽位的单复数又没人守了，正好回到 M3 要消掉的那一形。
    [Fact]
    public void EveryPublicNoun_IsInTheRegistry()
    {
        var declared = MemberNames().ToHashSet(StringComparer.Ordinal);
        var registered = typeof(CountedNoun)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(CountedNoun))
            .Select(f => f.Name)
            .ToList();

        Assert.Equal(registered.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            declared.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(registered.Count, CountedNoun.All.Count);
    }

    // 每个词的两种派生式逐条钉住。`Singularize` 是回推式的（按词尾猜），改动它会静默地把某个
    // 词写成 `entrie` / `content matche` 这类——而那正是 R30 那批缺陷的形状；`TitleCase` 同理，
    // 写成 `Xml Defs` / `C# Types` 走样一个字母都不会有别的东西喊出来。名单是封闭的，故这件事
    // 做得完：这里列的必须与 CountedNoun.All 一一对上，多一条少一条都判红。
    [Fact]
    public void EveryRegisteredNoun_HasTheFormsWeExpect()
    {
        Dictionary<string, (string Singular, string Heading)> expected = new(StringComparer.Ordinal)
        {
            ["C# types"] = ("C# type", "C# Types"),
            ["members"] = ("member", "Members"),
            ["XML defs"] = ("XML def", "XML Defs"),
            ["content matches"] = ("content match", "Content Matches"),
            ["files"] = ("file", "Files"),
            ["matching files"] = ("matching file", "Matching Files"),
            ["matching lines"] = ("matching line", "Matching Lines"),
            ["preview lines"] = ("preview line", "Preview Lines"),
            ["subclasses"] = ("subclass", "Subclasses"),
            ["levels"] = ("level", "Levels"),
            ["methods"] = ("method", "Methods"),
            ["properties"] = ("property", "Properties"),
            ["fields"] = ("field", "Fields"),
            ["types"] = ("type", "Types"),
            ["lines"] = ("line", "Lines"),
            ["entries"] = ("entry", "Entries"),
            ["changed files"] = ("changed file", "Changed Files"),
            ["checked sources"] = ("checked source", "Checked Sources"),
            ["versions"] = ("version", "Versions"),
            ["C# paths"] = ("C# path", "C# Paths"),
            ["XML paths"] = ("XML path", "XML Paths"),
            ["matches"] = ("match", "Matches"),
            ["items"] = ("item", "Items"),
            ["parameters"] = ("parameter", "Parameters"),
            ["conditional folders"] = ("conditional folder", "Conditional Folders"),
            ["minutes"] = ("minute", "Minutes"),
        };

        Assert.Equal(
            expected.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            CountedNoun.All.Select(n => n.Plural).OrderBy(x => x, StringComparer.Ordinal).ToList());

        foreach (var noun in CountedNoun.All)
        {
            Assert.Equal(expected[noun.Plural].Singular, noun.Singular);
            Assert.Equal(expected[noun.Plural].Heading, noun.Heading);
        }
    }

    // `CountedNoun.cs` 里那批 `public static readonly CountedNoun Xxx = Register("…");`。
    // 用文本读而不是反射，是为了让上面那条「都进了 Registry 吗」有一个**独立**的第二来源：
    // 两边都走反射的话，漏进 Registry 的字段两边同时看不见。
    private static IEnumerable<string> MemberNames()
    {
        var text = ProductSources().Single(s => Path.GetFileName(s.Path) == "CountedNoun.cs").Text;

        foreach (Match m in Regex.Matches(
                     text, @"public static readonly CountedNoun (?<name>\w+) = Register\("))
            yield return m.Groups["name"].Value;
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
