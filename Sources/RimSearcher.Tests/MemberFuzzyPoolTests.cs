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
//
// F30（表头口径）：F29 之后候选池是这条链上仅剩的一道限额，而它同样在总数之前生效——总数
// 数的是**进了池的**键挂着的成员。这一道不打算取消（它是成本上界），故改由表头说出来：
// 装不下同等好的匹配时写 `N of at least M`。本文件因此有两组相反的断言，缺一不可——
// 装不下时必须改口，而池子被噪声填满、只有少数够分时**不许**改口（NearMiss 那三条）：
// 一个常亮的下界警告与没有警告等价。
//
// 精确化（本轮）：候选池整个取消了。60 分这条线可以逐条翻译成结构条件（前缀 / 词边界 /
// camel 首字母 / 编辑距离 <= 3），于是「哪些键够分」从**打完分再看**变成**查得出来**，
// 合格集合是精确算出来的。两个魔数（500 / 50）与 F30 那条启发式判据一起消失。
//
// **本文件有三条断言因此反向重写**（600 那两条 + 两字符那条）：它们钉的是「池子装不下」，
// 而 600 与 301 都远在新上限之内，池子这个概念本身已经不存在。下界那条路仍然守着，
// 只是判据换成了可判定的那一个，见 CapOverflow 两条。
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

        // 1200 个噪声键一个都不够分：它们既不以查询串为前缀、编辑距离也远超 3。
        // 合格集合只有 `comptickrare` 一个，故总数是确数、不该改口。
        // （旧实现在这里是靠「进池的是否全部够分」猜出同一个结论的——那条启发式自带漏报角落，
        // 现在不必猜了。）
        Assert.False(hits.TotalIsLowerBound);
        Assert.Equal(1, hits.TotalInScope);
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

        // 表头不该无端改口。`at least` 是给「总数只是地板」用的，用在这里会把一个准确的
        // 总数说成不确定，调用方无从分辨哪一次的 at least 是真的。
        Assert.DoesNotContain("at least", result.Content);
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

        // 15 个键装得进 500 的池子，一个都没被丢掉，故这个总数是确数
        Assert.DoesNotContain("at least", result.Content);
    }

    // ── 反向重写：600 个同前缀成员不再是「装不下」 ──────────────────────

    // 这个数原本的意义是「压过 MemberFuzzyPoolSize = 500」。池子取消之后它压不过任何东西，
    // 但用例本身留着——它现在钉的是相反的一件事：**600 个同等好的匹配必须一个不少地数进总数**。
    private const int PoolOverflowCount = 600;

    // 名字构造同 BuildSamePrefixIndex：无大写边界无下划线，故一个方法恰好一个 member key。
    private (SourceIndexer Indexer, ScopeCatalog Catalog) BuildPoolOverflowIndex()
    {
        var root = _workspace.Dir("Overflow");

        var source = new StringBuilder();
        source.AppendLine("namespace Zq");
        source.AppendLine("{");
        source.AppendLine("    public class ZqOverflowHolder");
        source.AppendLine("    {");
        for (var i = 0; i < PoolOverflowCount; i++)
            source.AppendLine($"        public void Zqp{i:D4}() {{ }}");
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("Overflow", "ZqOverflowHolder.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    // 反向重写（原 `PoolFullOfEquallyGoodKeys_ReportsTheTotalAsAFloor`）。
    // 旧断言是 `TotalIsLowerBound == true` 且 `TotalInScope ∈ [1, 599]`——那描述的是
    // 「池子只装得下 500 个键」这个已经不存在的事实。600 个键远在新上限之内，故总数是确数。
    [Fact]
    public void SixHundredEquallyGoodKeys_AreAllCounted_NotCappedAtAPoolSize()
    {
        var (indexer, catalog) = BuildPoolOverflowIndex();

        var hits = indexer.SearchMembersByKeywords(["Zqp"], catalog.Everything, 10, ["Method"]);

        Assert.Equal(PoolOverflowCount, hits.TotalInScope);
        Assert.False(hits.TotalIsLowerBound);
    }

    // 反向重写（原 `LocatePoolOverflowQuery_HeaderSaysTheTotalIsAFloor`）
    [Fact]
    public async Task LocateSixHundredQuery_HeaderGivesTheRealTotal()
    {
        var (indexer, catalog) = BuildPoolOverflowIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(indexer, defIndexer, catalog);
        using var args = JsonDocument.Parse("""{"query":"method:Zqp","limit":10}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains($"10 of {PoolOverflowCount} members", result.Content);
        Assert.DoesNotContain("at least", result.Content);
    }

    // ── 两字符关键词那条路 ──────────────────────────────────────────────

    // 两字符关键词此前走的是另一条路（前缀匹配 + Take(50)），10 倍于 2-gram 那条路的收紧，
    // 而两个数都是旧代码的遗留、没有判据支撑。现在两条路合成一条：判据都是「分够 60」。
    [Fact]
    public void TwoCharKeyword_KeepsTheShortestPrefixMatches_AndCountsThemAll()
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

        // 短的优先这一条不变：`Zqa` 必须在，而不是被 300 个长名按名序挤掉
        Assert.Contains(hits.Items, entry => entry.Item.MemberName == "Zqa");

        // 反向重写：旧断言是 `TotalIsLowerBound == true`（301 个键塞不进 50 的池子）。
        // 两字符查询不再有自己的一道上限，301 个同分命中一个不少。
        Assert.Equal(301, hits.TotalInScope);
        Assert.False(hits.TotalIsLowerBound);
    }

    // ── 可判定的那一道上限 ──────────────────────────────────────────────

    // 展开上限仍然存在（它是成本上界），只是判据换成了可判定的那一个：合格集合先算完整，
    // 再看装不装得下。本仓的固定套路是「每加一道限额，就配一个刚好越过它的 fixture」，
    // 这一条就是那个 fixture——上限是多少，fixture 就跟到多少。
    private const int OverCapCount = SourceIndexer.MemberQualifiedKeyCap + 100;

    // 一个方法恰好一个键（无大写边界无下划线），故键数 == 成员数 == OverCapCount，
    // 越过上限的那 100 个连同它们的成员从未被数过。
    private (SourceIndexer Indexer, ScopeCatalog Catalog) BuildOverCapIndex()
    {
        var root = _workspace.Dir("OverCap");

        var source = new StringBuilder();
        source.AppendLine("namespace Zq");
        source.AppendLine("{");
        source.AppendLine("    public class ZqCapHolder");
        source.AppendLine("    {");
        for (var i = 0; i < OverCapCount; i++)
            source.AppendLine($"        public void Zqc{i:D6}() {{ }}");
        source.AppendLine("    }");
        source.AppendLine("}");

        _workspace.WriteFile(Path.Combine("OverCap", "ZqCapHolder.cs"), source.ToString());

        var indexer = new SourceIndexer();
        indexer.Scan(root);
        indexer.FreezeIndex();

        return (indexer, ScopeCatalog.Build([("vanilla", root)], null, null));
    }

    [Fact]
    public void MoreQualifiedKeysThanTheCap_ReportsTheTotalAsAFloor()
    {
        var (indexer, catalog) = BuildOverCapIndex();

        var hits = indexer.SearchMembersByKeywords(["Zqc"], catalog.Everything, 10, ["Method"]);

        Assert.True(hits.TotalIsLowerBound);
        Assert.Equal(SourceIndexer.MemberQualifiedKeyCap, hits.TotalInScope);
    }

    [Fact]
    public async Task LocateOverCapQuery_HeaderSaysTheTotalIsAFloor_AndNamesTheCause()
    {
        var (indexer, catalog) = BuildOverCapIndex();
        var defIndexer = new DefIndexer();
        defIndexer.FreezeIndex();

        var tool = new LocateTool(indexer, defIndexer, catalog);
        using var args = JsonDocument.Parse("""{"query":"method:Zqc","limit":10}""");
        var result = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("of at least", result.Content);

        // `at least` 不许孤立出现。扫描类工具的那一个恒与「有文件没扫全」的尾注同现，
        // 调用方从那里学到的读法是「出现 at least 就去看成因」；locate 此前只改表头、
        // 不给成因，于是同一个记号在两个工具上要学两遍。
        Assert.Contains("expansion cap", result.Content);
    }

    // ---- 精确枚举那条承重论证上的唯一破口 ----

    // QualifiedMemberKeys 把「够 60 分」翻译成四个区间查询，其中「相等/前缀」那一支查的是
    // **OrdinalIgnoreCase** 排序数组。这只有在 CalculateFuzzyScore 的 90 分支也按序数判前缀时
    // 才等价，而 `StartsWith(string)` 的默认重载是 CurrentCulture——ICU 会整体忽略
    // default-ignorable 码点，于是软连字符、零宽字符、C0 控制符能凭空造出一个「前缀」。
    //
    // 三路独立验算（逐支枚举 / 代数 / 对抗性穷举）各带一名反驳者，一致收敛到这一处：干净
    // 字母表上穷举五千多万对零违例，把可忽略字符加进字母表后成批出现，且**全部**是 90 分那支。
    // 已改成显式 Ordinal，这两条守住它不被改回去——改回去不会有任何现成用例变红。
    //
    // 不可见字符**不写进字面量**，一律由码点现算。直接贴进源码的话，下一次格式化、编码转换
    // 或一次复制粘贴就能把它悄悄抹掉，用例于是变成「abcdefgh 对 abcd 不该得 90」——恒假，
    // 而没有任何人看得出它已经不测原来那件事了。
    private static string Ignorable(int codePoint, int count = 1) => new((char)codePoint, count);

    [Theory]
    // 前缀凭空成立：序数前缀不成立、编辑距离 5、其余每一支也都不成立，旧实现却给 90
    [InlineData(0x00AD, "abcdefgh", "abcd")]
    [InlineData(0x200B, "compproperties_power", "compprop")]
    [InlineData(0x200D, "pawnkinddef_colonist", "pawnkinddef")]
    public void IgnorableCodePoints_DoNotFabricateAPrefixMatch(int codePoint, string text, string query)
    {
        Assert.True(FuzzyMatcher.CalculateFuzzyScore(Ignorable(codePoint) + text, query) < 90.0);
    }

    // 反向：可忽略字符落在 query 侧同样能造出前缀
    [Fact]
    public void IgnorableCodePointsInTheQuery_DoNotFabricateAPrefixMatchEither()
    {
        Assert.True(FuzzyMatcher.CalculateFuzzyScore("abcdefgh", "abcd" + Ignorable(0x00AD)) < 90.0);
    }

    // 最狠的一头：query 整串都是可忽略字符时它 collate 成空串，于是**任意** text 都「以它
    // 开头」——一次查询把整个索引当成满分命中。第一行的非空判断拦不住（串确实非空）。
    [Fact]
    public void AQueryOfNothingButIgnorableCodePoints_DoesNotMatchEverything()
    {
        Assert.Equal(0.0, FuzzyMatcher.CalculateFuzzyScore("pawn_needs_joy", Ignorable(0x00AD, 2)));
    }
}
