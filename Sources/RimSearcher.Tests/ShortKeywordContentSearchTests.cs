using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 第十二轮盲测：字段值索引只收长度 >= 3 的 token（DefIndexer.MinContentTokenLength），
// 于是查 '20'、'AI' 这类短词时 Content Matches 段直接不出现——与「这个词在所有 def 的
// 字段里都不存在」逐字同形。调用方据此断定某个数值没有出现在任何 def 里，而真相是
// 那一段压根没被搜过。「没搜」与「没有」必须分开。
public class ShortKeywordContentSearchTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private LocateTool BuildTool()
    {
        var csRoot = _workspace.Dir("Core");
        _workspace.WriteFile(Path.Combine("Core", "ZzHolder.cs"), """
            namespace Zz
            {
                public class ZzHolder
                {
                    public int ZzShieldPulse { get; set; }
                }
            }
            """);

        var defRoot = _workspace.Dir("Defs");
        // stackLimit 的值就是 20：字段值里确实有这个 token，只是索引不收它
        _workspace.WriteFile(Path.Combine("Defs", "ZzThings.xml"),
            "<Defs>\n  <ThingDef>\n    <defName>ZzShieldRelic</defName>\n"
            + "    <label>shield relic</label>\n    <stackLimit>20</stackLimit>\n"
            + "    <description>a shield relic</description>\n  </ThingDef>\n</Defs>\n");

        var indexer = new SourceIndexer();
        indexer.Scan(csRoot);
        indexer.FreezeIndex();

        var defIndexer = new DefIndexer();
        defIndexer.Scan(defRoot);
        defIndexer.FreezeIndex();

        return new LocateTool(indexer, defIndexer,
            ScopeCatalog.Build([("vanilla", csRoot), ("vanilla", defRoot)], null, null));
    }

    private async Task<string> Run(string query)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query }));
        var result = await BuildTool().ExecuteAsync(args.RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        return result.Content;
    }

    // 短词被静默跳过是原缺陷；脚注要指名道姓说是哪个词、为什么、以及换什么工具
    [Fact]
    public async Task ShortKeyword_IsCalledOutAsNotSearched()
    {
        var content = await Run("ZzShield 20");

        Assert.Contains("'20'", content);
        Assert.Contains("shorter than 3 characters", content);
        Assert.Contains("'not searched', not 'not present'", content);
        Assert.Contains("search_regex", content);
    }

    // 三个字符正好在界内，不该退化成常亮脚注
    [Fact]
    public async Task ThreeCharacterKeyword_PrintsNothing()
    {
        var content = await Run("ZzShield abc");

        Assert.DoesNotContain("shorter than 3 characters", content);
    }

    [Fact]
    public async Task NoShortKeyword_PrintsNothing()
    {
        var content = await Run("ZzShield");

        Assert.DoesNotContain("shorter than", content);
    }

    // 多个短词合并成一条，且用复数——脚注自身也受同一套文法约束
    [Fact]
    public async Task SeveralShortKeywords_AreListedInOneFootnote()
    {
        var content = await Run("ZzShield 20 AI");

        Assert.Contains("'20', 'AI'", content);
        Assert.Contains("are shorter than", content);
        Assert.Contains("searched for them", content);
    }
}
