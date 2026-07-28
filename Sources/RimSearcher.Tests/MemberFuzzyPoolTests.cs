using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 缺陷回归：`SearchMembersByKeywords` 的模糊候选池此前按**索引枚举序**硬截 200 条
// （`foreach (ngram) foreach (memberKey) { … if (set.Count >= 200) break; }`）。
// 枚举序跟着索引期的并发写入走、与查询毫无关系，于是几十万个 key 的真实语料里，光查询的
// 头一个 2-gram 就能瞬间填满配额，真值几乎必然落选——`method:CompTickRar` 在有
// CompTickRare 的语料里返回 0 条成员，而 locate 头一句就承诺把拼错的名字换成准确名。
//
// 本文件的 fixture 专门把池子撑爆：小 fixture 上候选装得下，模糊怎么写都生效，
// 断言不到这个缺陷（R52 那条用例正是因此只断言了措辞）。
public class MemberFuzzyPoolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // 噪声成员数要压过候选池容量（500）。噪声名与查询共享开头若干 2-gram（co/om/mp/pt/ti/
    // ic/ck），这样无论查询从哪个 2-gram 开始扫，旧实现都会先被噪声填满。
    private const int DecoyCount = 1200;

    private (SourceIndexer Indexer, ScopeCatalog Catalog) BuildOverflowIndex()
    {
        var root = _workspace.Dir("Core");

        var source = new StringBuilder();
        source.AppendLine("namespace Zz");
        source.AppendLine("{");
        source.AppendLine("    public class ZzNoise");
        source.AppendLine("    {");
        for (var i = 0; i < DecoyCount; i++)
            source.AppendLine($"        public void CompTickNode{i:D4}() {{ }}");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    public class ZzThing");
        source.AppendLine("    {");
        source.AppendLine("        public void CompTickRare() { }");
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("Core", "ZzNoise.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // 三种残缺形态各查一次：截断（少末尾一个字符）、掉头字符、中间拼错一个字符。
    // 三条都只有 CompTickRare 一个合理答案，噪声的模糊分够不到 60 的闸。
    [Theory]
    [InlineData("CompTickRar")]
    [InlineData("ompTickRare")]
    [InlineData("CompTikRare")]
    public void NearMissMemberName_SurvivesACandidatePoolFullOfDecoys(string query)
    {
        var (indexer, catalog) = BuildOverflowIndex();

        var hits = indexer.SearchMembersByKeywords([query], catalog.Everything, 10, ["Method"]);

        Assert.Contains(hits.Items, entry =>
            entry.Item.TypeName == "Zz.ZzThing" && entry.Item.MemberName == "CompTickRare");
        Assert.DoesNotContain(hits.Items, entry => entry.Item.MemberName.StartsWith("CompTickNode"));
    }

    // 候选池的取舍是全序的（重合度 → key 长度 → key），三项都与扫描次序无关，故同一条查询
    // 在任何进程里得到同一个池子——与 F22/F25 同一条可复现判据。这一条不另立用例：本机能建出
    // 的 fixture 规模下，`_memberIndex` 的枚举序是同一份哈希布局，两次建索引不会分歧，
    // 断言「两次一样」在旧实现上照样通过，什么也没钉住。

    // 端到端：locate 的 Description 头一句承诺把残缺或拼错的名字换成准确的成员名，
    // 而这条链此前在真实规模的索引上给出一个空的 Members 段。
    [Fact]
    public async Task LocateWithAMisspelledMemberName_ReturnsTheRealMember()
    {
        var (indexer, catalog) = BuildOverflowIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(indexer, defIndexer, catalog);
        using var args = JsonDocument.Parse("""{"query":"method:CompTickRar"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("Members", result.Content);
        Assert.Contains("CompTickRare", result.Content);
        Assert.DoesNotContain("CompTickNode", result.Content);
    }

    // 两字符关键词走的是另一条路（前缀匹配 + Take(50)），当年同样按枚举序截断。
    // 前缀命中在 CalculateFuzzyScore 下全部同分 90，故取哪 50 条完全决定了输出，
    // 而正确答案——最短的那个——只是运气好才在里面。
    [Fact]
    public void TwoCharKeyword_KeepsTheShortestPrefixMatches()
    {
        var root = _workspace.Dir("Short");

        var source = new StringBuilder();
        source.AppendLine("namespace Zq");
        source.AppendLine("{");
        source.AppendLine("    public class ZqHolder");
        source.AppendLine("    {");
        source.AppendLine("        public void Zqa() { }");
        for (var i = 0; i < 300; i++)
            source.AppendLine($"        public void Zqlongername{i:D4}() {{ }}");
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("Short", "ZqHolder.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();
        var catalog = ScopeCatalog.Build([("vanilla", root)], null, null);

        var hits = indexer.SearchMembersByKeywords(["Zq"], catalog.Everything, 10, ["Method"]);

        Assert.Contains(hits.Items, entry => entry.Item.MemberName == "Zqa");
    }
}
