namespace RimSearcher.Tests;

// 已入库的每一份呈现层基线都要合共用文法。
//
// 这道闸补的是两层之间的一道缝，而那道缝已经漏过一次：
//
//   - OutputSnapshotTests 存的是**字节级** diff，判据只有「与上次一模一样」。一份基线从落地
//     那天起就带着违规的话，它每次都与上次一模一样，永远绿。
//   - OutputGrammarGateTests 判的是文法，但它只吃**矩阵那 112 格**——矩阵是一张表，表上记成
//     「不适用」的格子从来不跑。
//
// 于是「产品输出了一种形态、这形态进了字节级基线、却从没被文法闸读过」是完全可能的，而且
// 不是假设：`search_regex/at-least-unreadable`、`at-least-line-capped`、`zero-hits-names-timeout`
// 与 `trace/usages-zero-hits-line-capped` 四份基线曾同时带着违规躺在仓里，两道闸各自全绿。
// 它们真正现身只发生在全量跑时某个文件偶发被放弃的那几次——闸不是常绿，是**时红时绿**。
//
// 判据本身一个字都不新写：语料取 SnapshotGate.Names()，断言取 GrammarRules.Check()。这道闸
// 的全部内容就是「把已有的两样东西接上」，而缺的一直只是这一下。
public class SnapshotGrammarGateTests
{
    // tools/list 那一族排除在外，理由不是「它红」，是**这套文法压根不描述它**：
    //
    // 输出文法管的是 tools/call 返回给调用方的那段正文——表头怎么数、折叠行怎么写、尾注按
    // 什么顺序挂。tools/list 基线存的是工具的 JSON schema 与 Description，那是**说明书**，
    // 里面出现 `at least` 是在向调用方解释这个记号该怎么读，而规则四甲会把它当成一个表头上
    // 的下界记号去找成因。同理，schema 里的 `limit` 说明不是折叠行，`description` 里的
    // 句子也不该被规则二按「数词 + 名词」验单复数。
    //
    // 那一族有它自己的闸（ToolListSnapshotTests 的字节级 diff，加参数层那几道判 schema 与
    // 取参别名对不对得上的），不是没人管。
    //
    // 目录名取 ToolListSnapshotTests.Area，不在这里抄第二遍：抄的那份在那边改名之后会静默
    // 失效（这一族又进来了，闸恒红）或静默扩大（一族基线悄悄没人检，闸恒绿）。
    private static bool IsPresentationLayer(string name)
        => !name.StartsWith(ToolListSnapshotTests.Area + "/", StringComparison.Ordinal);

    public static TheoryData<string> EveryPresentationSnapshot()
        => [.. SnapshotGate.Names().Where(IsPresentationLayer)];

    [Theory]
    [MemberData(nameof(EveryPresentationSnapshot))]
    public void EverySnapshot_ObeysTheSharedGrammar(string name)
    {
        var violations = GrammarRules.Check(SnapshotGate.Read(name));

        Assert.True(violations.Count == 0, GrammarRules.Describe($"基线 {name}", violations));
    }

    // 上面那条 Theory 的语料是从磁盘枚举来的，而**枚举不到东西时 Theory 是 0 个用例，照绿**
    // ——与「基线不存在时判红而不是静默生成」是同一条道理，也是同一个失效形状：绿的时候没人
    // 知道那是「全都合文法」还是「没有东西可检」。
    //
    // 三条各堵一种漂法：目录整个找不着 / 排除判据认不出那一族（那边改了名）/ 排除判据把语料
    // 全吃掉。中间那条尤其要紧——它红的时候说的是「你以为排掉了 tools_list，其实没排掉」。
    [Fact]
    public void TheGateActuallyHasCorpus()
    {
        var all = SnapshotGate.Names();

        Assert.NotEmpty(all);
        Assert.Contains(all, name => !IsPresentationLayer(name));
        Assert.Contains(all, IsPresentationLayer);
    }
}
