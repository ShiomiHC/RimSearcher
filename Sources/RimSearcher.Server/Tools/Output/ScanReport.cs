using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 扫描削减了什么：表头侧的下界记号 + 尾注侧的成因。
//
// 五条句子放在同一处不是为了归类整齐，是因为它们**三处联动**（见指导文档 §4 的已知耦合）：
// 有文件没扫全 ⇒ 表头总数改口 `at least`（FoundCount）**且**记号旁就地挂成因引用
// （LowerBoundReason）**且**尾注点名是哪些文件（NotScannedInFull + NameSample）。
// 三者少任何一个，`at least` 就会被读者就近拿一个别的上限去解释——第九轮盲测三条互不相干的
// 任务链正是这么各自独立误读了同一个 `at least 105`。散在三个文件里，这条耦合就没有一处
// 看得见；这里把它建模成一个整体，三条句子同居于此。
public static class ScanReport
{
    // 「这个数只是地板」的记号本身。两处下界（扫描没扫全、候选池装不下）共用同一个词，
    // 因为调用方要学的读法只有一条。此前它是两个文件里的两个字面量（这里与 Tally.Cell），
    // 「共用」只写在注释里。
    public const string FloorMark = "at least ";

    // 有文件没扫全时，命中总数就不再是确定值而是下界。表头与下面那行尾注必须同时改口，
    // 否则一句说「7 found」、一句说「有文件没扫全」，调用方无从判断该信哪个。
    //
    // 名词是 matching lines 而不是 matches：两个工具数的都是 `regex.IsMatch(line)` 逐行累加，
    // 同一行里命中两次仍只算一行。原先只写 `743 found`，而表头前半句是 "Regex matches for" /
    // "References to"——读者按「743 处命中」读，在一行多处的 pattern 上这个数直接是错的。
    public static string FoundCount(int total, bool anyFileIncomplete)
        => anyFileIncomplete
            ? $"{FloorMark}{CountedNoun.MatchingLines.Quantity(total)}"
            : CountedNoun.MatchingLines.Quantity(total);

    // 表头三态里换了量纲的那一支：扫描停在预览上限时，数的是**印出来的**预览行。
    //
    // 这个数是确定的，故它不带下界记号，也不该带——但正因为不带，「有文件没扫全却没有 at least」
    // 这条判据必须认得出它，否则那一支恒红。判据认的是这一形，所以这一形要有产地：此前它写在
    // ScanOutputRenderer.Headline 的分支里，而三态的另外两支（FoundCount / LowerBoundReason）
    // 都在这个类里——一套三态分居两处，闸只好照着文本手抄一句 "preview lines in scope" 来认它。
    public static string PreviewLineCount(int previewLinesCollected)
        => $"first {CountedNoun.PreviewLines.Quantity(previewLinesCollected)}";

    // 下界记号自己不带成因引用时，读者会就近找一个上限来解释它。search_regex 的 schema 里唯一
    // 带上限语义的东西是 `limit` 的 default 100，而 `at least 105` 与它只差 5——第九轮盲测三条
    // 互不相干的任务链各自独立做了同一个算术推断（「被 limit 截了」），其中一条据此归因错人并
    // 写进结论，另一条为「解除截断」白跑一轮。真正的成因（有文件没扫全）写在整份结果之后，
    // 中间还隔着预览上限那一行，两处之间没有任何可指认的连接。
    //
    // R49 点名了是哪个文件，但点名解决的是「该不该在意」，不是「这个 at least 从哪来」。
    // 这里补的正是后者：记号旁边就说清成因在哪、以及 limit 与它无关（limit 咬人时表头走的是
    // `first N preview lines` 那一形，压根不会出现 at least）。
    public static string LowerBoundReason(bool anyFileIncomplete)
        => anyFileIncomplete
            ? "; 'at least' comes from the trailing 'not scanned in full' note, not from limit"
            : string.Empty;

    // 「扫到预览行上限就停了」的尾注。search_regex 与 trace usages 报的是同一件事，原先
    // 却是两句话——`[Preview lines truncated at limit 1 and scanning stopped there, raise
    // limit (up to 200) or use limit:'all']` 对 `[scanning stopped at the 1-preview cap
    // — pass limit:'all' to raise the cap to 200, …]`。同一个事件读两遍不同措辞，调用方
    // 只能各认一次。
    //
    // 与 Fold.Line 的差别在于「剩下多少」这里是不知道的：扫描是在上限处停的，后面的候选
    // 文件根本没打开过。所以不写 `+N more`——那个数没人算得出来，编一个就是假的。
    // extraNotes 收本工具独有的补充（如「文件数也超了」），一并挂在同一句里。
    public static string ScanStopped(
        int previewCap, ResultLimit limit, IReadOnlyList<string>? extraNotes = null)
    {
        // 已经顶到硬上限时别再劝 limit:'all'，那只会原地重试；此时把「这就是服务端上限」
        // 说出来，否则调用方只会看见一个数，不知道它已经是天花板。
        // 同一条修法的潜伏形：这一支要 previewCap 取 1 才写错，而它只在 limit:'all' 时走到，
        // 那时 previewCap 恒等于 HardLimit（200），故当前语料到不了。仍然改——名词槽一律走构词，
        // 「现在到不了」不是让一处产地留在名单外的理由。另一支 `{previewCap}-preview cap` 是
        // 定语复合词，英语里本就不带复数，不动。
        var cap = limit.Unlimited
            ? $"scan stopped at the server cap of {CountedNoun.PreviewLines.Quantity(previewCap)}"
            : $"scan stopped at the {previewCap}-preview cap";
        var route = limit.Unlimited
            ? "narrow the query or the scope"
            : $"pass limit:'all' to raise the cap to {ScopeAndLimitArgs.HardLimit}, or narrow the query or the scope";

        var notes = new List<string> { cap };
        if (extraNotes != null) notes.AddRange(extraNotes);

        return $"... more matches exist ({string.Join("; ", notes)}; {route})";
    }

    // 「有文件没扫全」的尾注。search_regex 与 trace usages 有一模一样的两处静默削减——单文件
    // 行闸（扫到第 20000 行就停）与读不开的文件直接跳过——此前只有前者说出口。调用方从
    // search_regex 学到的是「没有尾注即完整命中集」（那是写在它 Description 里的契约），
    // 顺手套到 trace 上，就会把一份可能漏了六万行的结果当成穷尽结论。
    public static string NotScannedInFull(IReadOnlyList<string> reasons)
        => $"... some files were not scanned in full ({string.Join("; ", reasons)}; "
           + "matches in the unscanned parts would not be listed)";

    // 上面那句里每条成因都要点名涉及哪个文件。只给个数时调用方无从判断它与本次查询有没有
    // 关系，只能把整份结果一律当成下界——第八轮盲测三条任务链各自独立踩到这一处（一条把精确的
    // 108 写成 `at least 108` 并把置信度降了一档），而三次的元凶都是同一个文件。
    //
    // 注意这**不影响**表头的 `at least N` 判据：行闸是在第 20000 行停的，那之后有没有命中
    // 谁也不知道，即便已扫部分零命中，总数仍然只是下界。点名解决的是「该不该在意」，
    // 不是「这个数准不准」。
    //
    // 名字多了没有额外信息，列前 max 个，其余记数。
    public static string NameSample(IReadOnlyList<string>? names, int max = 3)
    {
        if (names == null || names.Count == 0) return string.Empty;
        var head = string.Join(", ", names.Take(max));
        var rest = names.Count - Math.Min(max, names.Count);
        return rest > 0 ? $" ({head} and {rest} more)" : $" ({head})";
    }
}
