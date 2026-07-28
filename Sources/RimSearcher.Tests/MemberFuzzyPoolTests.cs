using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools;

namespace RimSearcher.Tests;

// 成员搜索这条链上**两道限额**的回归。两者都只在撑爆限额的语料上才显形，小 fixture 上
// 怎么写都过——F28 与 F29 因此都是在真实语料里撞出来的，本文件的 fixture 专门把限额撑爆。
//
// F28（候选池）：模糊候选池此前按**索引枚举序**硬截 200 条
// （`foreach (ngram) foreach (memberKey) { … if (set.Count >= 200) break; }`）。枚举序跟着
// 索引期的并发写入走、与查询毫无关系，于是几十万个 key 的真实语料里，光查询的头一个 2-gram
// 就能瞬间填满配额，真值几乎必然落选——`method:CompTickRar` 在有 CompTickRare 的语料里返回
// 0 条成员，而 locate 头一句就承诺把拼错的名字换成准确名。
//
// F29（key 展开）：候选池之后还有一道 `Take(10)`，分够 60 的 key 只展开前 10 个。这道更糟，
// 因为 `TotalInScope` 数的是截断之后那一批，于是表头把切片印成「完整集」。
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

    // ── F29：候选池之后那道 key 展开限额 ────────────────────────────────

    // 同前缀成员数要压过旧实现的 Take(10)。
    private const int SamePrefixCount = 15;

    // 这批名字彼此之间**没有能一网打尽的父 key**：名字里既无大写边界也无下划线，
    // SplitIntoWords 分不出第二个词，故每个方法恰好只对应一个 member key。
    // （真实语料里 `Notify_` 会分出 `notify` 这个词 key，它挂着全部 Notify_* 成员；只是它比
    // `notify_*` 少覆盖一个 2-gram，在几百个同前缀 key 的挤压下进不了候选池——这也正是
    // `method:Notify_` 只回 22 条的原因。fixture 直接把那个父 key 拿掉，钉住的是同一件事。）
    private (SourceIndexer Indexer, ScopeCatalog Catalog) BuildSamePrefixIndex()
    {
        var root = _workspace.Dir("Prefix");

        var source = new StringBuilder();
        source.AppendLine("namespace Zq");
        source.AppendLine("{");
        source.AppendLine("    public class ZqPrefixHolder");
        source.AppendLine("    {");
        for (var i = 0; i < SamePrefixCount; i++)
            source.AppendLine($"        public void Zqx{new string((char)('a' + i), 3)}() {{ }}");
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("Prefix", "ZqPrefixHolder.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // 15 个同前缀成员全部 90 分（StartsWith）。旧实现按 key 升序取前 10，`Zqxkkk` 往后的
    // 五个连同它们的成员一起消失。
    [Fact]
    public void EverySamePrefixMember_IsReachable_NotJustTheFirstTenKeys()
    {
        var (indexer, catalog) = BuildSamePrefixIndex();

        var hits = indexer.SearchMembersByKeywords(["Zqx"], catalog.Everything, 100, ["Method"]);

        Assert.Equal(SamePrefixCount, hits.Items.Count);
        Assert.Contains(hits.Items, entry => entry.Item.MemberName == "Zqxooo");
    }

    // 表头那个数必须跟着一起对。**这一条才是 F29 真正的要害**：少回几条只是慢一轮，而
    // TotalInScope 跟着截断走，会让 locate 把一个切片印成「这一段的完整集」。
    [Fact]
    public void SamePrefixHeaderTotal_CountsEveryMember_NotTheExpandedSlice()
    {
        var (indexer, catalog) = BuildSamePrefixIndex();

        var hits = indexer.SearchMembersByKeywords(["Zqx"], catalog.Everything, 3, ["Method"]);

        // 只列 3 条，但总数仍要报 15——「列了几条」与「一共有几条」是两个量
        Assert.Equal(3, hits.Items.Count);
        Assert.Equal(SamePrefixCount, hits.TotalInScope);
    }

    [Fact]
    public async Task LocateSamePrefixQuery_HeaderAgreesWithWhatItLists()
    {
        var (indexer, catalog) = BuildSamePrefixIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(indexer, defIndexer, catalog);
        using var args = JsonDocument.Parse("""{"query":"method:Zqx","limit":"all"}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains($"{SamePrefixCount} members", result.Content);
        Assert.Contains("Zqxooo", result.Content);
    }

    // ── 两字符关键词那条路 ──────────────────────────────────────────────

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
