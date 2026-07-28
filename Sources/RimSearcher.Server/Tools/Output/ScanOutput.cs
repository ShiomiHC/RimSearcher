using RimSearcher.Core;

namespace RimSearcher.Server.Tools.Output;

// 扫盘类工具（search_regex / trace usages）的输出模型：说「有什么」，不说「怎么印」。
//
// 每一个字段都是一条**事实**，没有一个是措辞。判据是：这个字段能不能在不读 renderer 的前提下
// 由查询结果直接答出来。`ScanStopped` 是事实（配额到了没有），`first N preview lines` 是措辞；
// `Completeness` 是事实（哪些文件没扫全），`at least` 与尾注点名是它的两个投影。
//
// 这条分界不是洁癖，它就是本轮重构要解决的那个问题。十三轮盲测修的缺陷绝大多数是同一形：
// 某条契约在某个分支上只印了半边——表头改口成 `at least` 而尾注忘了给成因、每文件折叠印了而
// preview-cap 脚注没印、返回里有重名文件而消歧忘了做。只要工具还在手拼文本，这些都是能漏的；
// 在这个模型里它们**漏不掉**，因为它们不是各自独立的字段：
//
//   - 表头的下界记号、成因引用、尾注点名，三处同出于 Completeness 一个值；
//   - 每文件折叠与 preview-cap 脚注的触发条件都从 Blocks 自己算，不由工具置旗；
//   - 重名消歧、来源标签提头还是逐行、列几个文件块、超出的那条折叠行——全要看整批 Blocks
//     才判得出来，故它们压根不在模型里。工具给不出正确答案的东西就不该由工具给。
//
// 工具还能自己决定的措辞只剩两处，且都有理由：Subject（"Regex matches for 'X'" 与
// "Text matches for 'X'" 是两个工具刻意分开的动词）与 EmptyLine（零命中那句要回显本工具独有的
// 参数）。即便这两处，**它们后面挂哪些脚注、什么顺序**仍归 renderer。
public sealed record ScanOutput
{
    // 表头第一句的主语，不含括号里的计数。
    public required string Subject { get; init; }

    // 零命中时的整句（含句末句号）。这一形与有命中形是两句完全不同的话，且要回显的参数也不同
    // （search_regex 要报 fileFilter 筛掉之后还剩几个候选文件），故措辞由工具给。
    public required string EmptyLine { get; init; }

    public required ScopeSelection Scope { get; init; }

    // 表头里 scope 之后的参数回显，各项不带前导逗号（标点归 renderer）。
    // 三个参数要么都回显要么都不回显：只差一个时，「没回显 = 没生效」会被当成规则学走。
    public required IReadOnlyList<string> ParameterEchoes { get; init; }

    // **全部**命中文件，按展示顺序排好；每块的预览行也排好，但不要预先截断——列几块、每块
    // 印几行都是 renderer 的事（它才知道 FileListCap 与 PreviewCapPerFile 怎么与折叠行、
    // 脚注联动）。空列表 = 零命中形。
    public required IReadOnlyList<ScanFileBlock> Blocks { get; init; }

    // 一次最多列几个文件块，null = 不封顶。措辞里要印这个常数本身（`only the first 50 files
    // are listed`），而不是实际列出的块数——「本次列了几个」与「上限是多少」是两个数。
    //
    // 两个扫盘工具在这一格上真的不同，不是漏配：search_regex 每文件最多 3 行、故 50 个文件
    // 才封得住版面；trace usages 的配额是全局的预览行数（limit），文件数封第二道闸只会让
    // 「列了几个」和「limit 是多少」两个上限在同一份返回里互相解释不清。写 null 而不是
    // int.MaxValue，是因为后者会让 `Blocks.Count - cap` 这类算术在别处静默溢出成负数，
    // 而「没有这道闸」本来就该是个能判断的状态。
    public int? FileListCap { get; init; }

    public required int PreviewCapPerFile { get; init; }

    // 真实命中总数（各文件命中数之和），不是预览行数——预览每文件封顶，两者可以差很远。
    // ScanStopped 时这个数只覆盖恰好扫到的那些文件，随线程调度浮动，renderer 因此不报它。
    public required int TotalMatchingLines { get; init; }

    // 预览配额用尽、扫描在中途停下。后面的候选文件根本没打开过，故这一形下 Blocks 只是
    // 「恰好扫到的那批」，不是命中文件全集——两形的措辞因此必须不同。
    public required bool ScanStopped { get; init; }

    // 折叠行的三个分支要靠它才分得开（调用方要过 'all' 没有、顶到服务端上限没有）
    public required ResultLimit Limit { get; init; }

    // 静默削减了什么。表头的下界记号与尾注的点名都是它的投影，见 ScanCompleteness。
    public required ScanCompleteness Completeness { get; init; }

    // 行内标记与整份脚注的配对器。renderer 打标记、renderer 收脚注——两者中间隔着几十行正文，
    // 而「记号与成因之间要有可指认的连接」这条判据只有同一个持有者保证得了。
    public required ConditionalReport Conditional { get; init; }

    // 拼错的 scope 被静默退回全域时那一行。有命中、无命中两条路径都要带。
    public string? ScopeNotice { get; init; }
}

// 一个文件块：文件名、印出来的预览行、以及这个文件里真实有多少条命中。
//
// Path 是**完整路径**而不是要印的名字：印什么名字要看整批文件里有没有重名，那是 renderer
// 拿到全部 Blocks 才判得出来的事（见 FileNames.Disambiguate）。来源名同理——它由 Path 与
// Scope 一起决定，而「同源提到表头、混源逐行」这条判据也要看整批。工具答不出的都不在这里。
//
// TotalInFile 是索引层数出来的真实命中数，与 Previews.Count 是两个量：预览每文件封顶，
// 拿 Previews.Count 当基数会把第 4 条起的命中吞掉。
public sealed record ScanFileBlock(
    string Path,
    IReadOnlyList<(int Line, string Preview)> Previews,
    int TotalInFile);

// 扫描静默削减了什么。三种成因各自的计数与点名。
//
// 单独成一个值而不是六个平铺字段，是因为它有三个投影且必须同时成立：表头的总数要不要改口成
// `at least`、下界记号旁边那条成因引用、以及尾注里逐条点名。第九轮盲测三条互不相干的任务链
// 各自独立误读了同一个 `at least 105`——它们就近拿 limit 的 default 100 去解释那个下界（只差 5，
// 算术上太顺），而真正的成因隔在整份结果之后。三处从同一个值派生，就漏不掉、也对不上不了。
public sealed record ScanCompleteness(
    int TimedOutFiles = 0,
    IReadOnlyList<string>? TimedOutNames = null,
    int UnreadableFiles = 0,
    IReadOnlyList<string>? UnreadableNames = null,
    int LineCappedFiles = 0,
    IReadOnlyList<string>? LineCappedNames = null,
    int LineCap = 0)
{
    public static readonly ScanCompleteness Complete = new();

    public bool AnyIncomplete => TimedOutFiles > 0 || UnreadableFiles > 0 || LineCappedFiles > 0;

    // 逐条成因的整句。次序固定：超时 → 读不开 → 行闸。
    //
    // 每条都整句两写而不是拼三目：跟着 N 变的不止名词，动词、代词和后半句的主谓都要跟着换，
    // 拼到第四个三目就没人读得懂了。
    public IReadOnlyList<string> Reasons()
    {
        var reasons = new List<string>();

        if (TimedOutFiles > 0)
            reasons.Add((TimedOutFiles == 1
                ? "1 file was abandoned mid-scan because the pattern timed out on it "
                  + "(catastrophic backtracking) — its per-file match count is missing"
                : $"{TimedOutFiles} files were abandoned mid-scan because the pattern "
                  + "timed out on them (catastrophic backtracking) — their per-file match counts are missing")
                + ScanReport.NameSample(TimedOutNames));

        if (UnreadableFiles > 0)
            reasons.Add($"{OutputText.Quantity(UnreadableFiles, "files")} could not be read "
                        + $"and {(UnreadableFiles == 1 ? "was" : "were")} skipped entirely"
                        + ScanReport.NameSample(UnreadableNames));

        if (LineCappedFiles > 0)
            reasons.Add($"{OutputText.Quantity(LineCappedFiles, "files")} "
                        + $"{(LineCappedFiles == 1 ? "was" : "were")} "
                        + $"only scanned to line {LineCap}"
                        + ScanReport.NameSample(LineCappedNames));

        return reasons;
    }
}
