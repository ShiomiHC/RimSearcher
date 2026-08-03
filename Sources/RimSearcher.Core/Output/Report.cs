using System.Text;

namespace RimSearcher.Output;

/// <summary>
/// 声明的类别。闸按类别判「说没说」,不判渲染完的字 —— 按子串判会让同一句话的红绿
/// 取决于措辞。
/// </summary>
public enum NoticeKind
{
    /// <summary>结果被上限截断。</summary>
    Truncation,
    /// <summary>结果计数,完整集也报(三态文法的「裸 N」态)。</summary>
    Count,
    /// <summary>调用方自己要求的过滤,不是截断 —— 两者混用会被读成结果不完整。</summary>
    Filter,
    /// <summary>快照与当前游戏环境不一致。</summary>
    Staleness,
    /// <summary>用了哪个快照、为什么。</summary>
    SnapshotChoice,
    /// <summary>能力边界:本次输出没做什么 —— 写进它作用的那个块。</summary>
    Boundary,
    /// <summary>数据来自快照环境之外,仅供参考。</summary>
    Advisory,
    /// <summary>参数被夹紧到上限。</summary>
    Clamp,
    /// <summary>下一步该怎么做的指路。</summary>
    NextStep,
}

/// <summary>
/// 一次输出里排得出先后的东西:声明与数据块共用这一条序列。
///
/// 共用是为了让**位置成为写命令时的显式选择** —— 此前渲染器无条件把全部声明提到最前,
/// 于是一句只讲字段表的话与一句只讲译文表的话挨在一起,而调用方 pipe 一个 head
/// 切掉的恰好是数据。现在 Add 的先后就是印出来的先后。
/// </summary>
public abstract record ReportEntry;

public sealed record Notice(NoticeKind Kind, string Text, bool Footnote = false) : ReportEntry;

public abstract record Block : ReportEntry
{
    /// <summary>
    /// 属于哪个重复项集合。为 null 时块直接挂在 JSON 顶层;非 null 时它是
    /// <c>root[Collection][Item]</c> 下的一个键 —— 这样「一次输出里有 N 个同构对象」
    /// 有个恒定形状,不必把名字拼进键里。
    /// </summary>
    public string? Collection { get; init; }

    public int Item { get; init; }
}

/// <summary>表格块。列名即 JSON 键(snake_case),文本与 JSON 两个渲染器共用同一份行数据。</summary>
public sealed record TableBlock(string Name, IReadOnlyList<string> Columns,
                                IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
                                string? Caption = null) : Block;

/// <summary>键值明细块(get 这类单对象输出)。</summary>
public sealed record DetailBlock(string Name, IReadOnlyList<KeyValuePair<string, object?>> Pairs) : Block;

/// <summary>
/// 自由文本块(代码片段)。文本侧必须逐字保真 —— 表格会按
/// <see cref="TextRenderer.MaxCellWidth"/> 截单元格,而截过的源码不是源码。
///
/// <paramref name="Rows"/> 是同一批内容的结构化形态,只给 <c>--json</c> 用:拼成一行的
/// <c>"vanilla/Verse/Widgets.cs:12:\tpublic static void Draw()"</c> 没法安全反解 ——
/// 路径里本来就可能有冒号。两侧同源,不是两份数据。
/// </summary>
public sealed record TextBlock(string Name, IReadOnlyList<string> Lines,
                               IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null) : Block;

/// <summary>
/// 一次命令输出的完整模型。命令只管往里塞内容,行尾/尾空行/声明区排布由渲染器统一收口:
/// 行尾一律 LF、TrimEnd 尾空行 —— 尾空行会被 LLM 读成「后面被截断了」。
/// </summary>
public sealed class Report
{
    private readonly List<ReportEntry> _entries = [];
    private readonly List<string> _promised = [];

    /// <summary>声明与数据块按 Add 的先后排在一起 —— 文本渲染器读这一份。</summary>
    public IReadOnlyList<ReportEntry> Entries => _entries;

    public IReadOnlyList<Notice> Notices => _entries.OfType<Notice>().ToList();
    public IReadOnlyList<Block> Blocks => _entries.OfType<Block>().ToList();

    /// <summary>这条命令答应过要有的顶层数据键,哪怕这次一行都没有。</summary>
    public IReadOnlyList<string> Promised => _promised;

    /// <summary>
    /// 这一次用户自己划的那道线,原样贴回命令行的形态(<c>--type MentalStateDef --exact</c>)。
    ///
    /// 三态计数(<see cref="Tally"/>)只覆盖**工具造成的**收窄:行数上限、扫描没跑完。
    /// 用户侧的收窄不在其中,于是一句完整式的「52 defs.」会被读成「一个不漏」,
    /// 而它实为「在我自己划的范围内完整」。
    ///
    /// 判据在声明层(<see cref="Cli.OptionSpec.Narrows"/>),这里只负责念回去。
    /// </summary>
    public string Narrowing { get; set; } = "";

    private string Within => Narrowing.Length == 0 ? "" : $" within {Narrowing}";

    /// <summary>
    /// 数据键恒在 —— 与「计数恒在」同一条道理的机器侧版本。
    ///
    /// 零行时命令一律提前 return,不认领的话 <c>--json</c> 里那个键就**整个消失**,
    /// 消费方拿到的不是空数组而是 KeyError;而「翻过头了」「快照里没有」「工具崩了」
    /// 在这份 JSON 上同形。文本侧照旧不印空表(<see cref="TextRenderer"/> 自己滤),
    /// 这条只管机器侧。
    ///
    /// 在命令**开查之前**声明,而不是在零行分支里补 —— 后者漏一条分支就漏一个形状。
    /// </summary>
    public Report Promises(string tableName)
    {
        if (!_promised.Contains(tableName, StringComparer.Ordinal)) _promised.Add(tableName);
        return this;
    }

    public Report Notice(NoticeKind kind, string text, bool footnote = false)
    {
        _entries.Add(new Notice(kind, text, footnote));
        return this;
    }

    /// <summary>那些 mod 一动、这次的答案就可能不对的那条话,以及它点名的 mod。</summary>
    private (Notice Notice, IReadOnlyList<string> Mods)? _deferred;

    /// <summary>
    /// 位置等结果出来再定的一条声明:先按脚注挂上,<see cref="Settle"/> 再决定要不要提回
    /// 表头。发的时候查询还没跑,而判据在结果里。
    ///
    /// **只调位置,一次都不抑制。**「结果里没点到那个 mod」不等于「答案没受它影响」——
    /// 那个 mod 可能正是把某一行改没了的那个,而这种失效恰好落在零结果上,也就是最需要
    /// 这句话的场合。表头留给随查询变化的东西(scope 展开成哪几个 mod、精确/包含的拆分、
    /// 截断脚注),恒定的环境声明沉到表下 —— 一条每次都在同一位置说同样话的横幅,读到第五遍
    /// 之后会把整个表头区一起训练成盲区。
    /// </summary>
    public Report DeferredNotice(NoticeKind kind, string text, IReadOnlyList<string> aboutMods)
    {
        var notice = new Notice(kind, text, Footnote: true);
        _deferred = (notice, aboutMods);
        _entries.Add(notice);
        return this;
    }

    /// <summary>结果已经在手,把延后的那条摆到它该去的位置。渲染之前调一次。</summary>
    public void Settle()
    {
        if (_deferred is not { } d) return;
        _deferred = null;

        var at = _entries.IndexOf(d.Notice);
        if (at >= 0 && !ProvablyUnrelated(d.Mods))
            _entries[at] = d.Notice with { Footnote = false };
    }

    /// <summary>结果里的 mod 这一维,叫这个名字。</summary>
    private const string ModKey = "mod";

    /// <summary>
    /// 「这次的答案与那几个 mod 无关」证得出来吗 —— 证不出就提回表头。
    ///
    /// 只有一种证得出:输出里有块带着 mod 这一维,而它点的名一个都不在那几个里。
    /// 反过来的两种都算证不出 —— 一行都没有(零结果:被改没的那一行长的就是这个样子),
    /// 以及整个输出没有 mod 这一维(<c>fields</c> / <c>values</c> 这类跨 mod 的聚合)。
    ///
    /// 带 mod 维的块**只要有一个就作数**,不要求每个块都有:<c>get</c> 的字段表是那个
    /// def 的附属,归属已经由它上面的明细块说过了,拿字段表的「没有 mod 列」去否掉
    /// 明细块给出的答案,等于永远证不出。
    /// </summary>
    private bool ProvablyUnrelated(IReadOnlyList<string> mods)
    {
        var withMod = 0;

        foreach (var block in _entries.OfType<Block>())
        {
            if (ModCells(block) is not { } cells) continue;
            withMod++;
            if (cells.Any(c => mods.Contains(c, StringComparer.OrdinalIgnoreCase))) return false;
        }

        return withMod > 0;
    }

    /// <summary>这个块的 mod 列/键有哪些取值。<c>null</c> = 它根本没有这一维。</summary>
    private static IReadOnlyList<string>? ModCells(Block block) => block switch
    {
        TableBlock t when t.Rows.Count > 0 && t.Columns.Contains(ModKey, StringComparer.Ordinal) =>
            [.. t.Rows.Select(r => r.GetValueOrDefault(ModKey)?.ToString() ?? "")],
        DetailBlock d when d.Pairs.Any(p => p.Key == ModKey) =>
            [.. d.Pairs.Where(p => p.Key == ModKey).Select(p => p.Value?.ToString() ?? "")],
        _ => null,
    };

    /// <summary>
    /// 计数恒在。完整集渲染成裸 N 并按 <see cref="NoticeKind.Count"/> 归类,被截时按
    /// <see cref="NoticeKind.Truncation"/> 归类 —— 两态同一个产地、同一个位置,因为靠
    /// 沉默传达「完整」一定会被读错。
    ///
    /// <paramref name="howToSeeMore"/> 留空是常态,与 <see cref="PageNotice"/> 同一条纪律:
    /// 「--limit all 能一次吃完」这类出路逐字不随查询变,SKILL.md 已按命令列全,在每次计数上
    /// 重念是同一份知识的第三个副本。截断信号由 <c>n of N</c> 这个形状自己带着,不靠尾句。
    /// 只有当出路带着**算出来的**参数时才传它。
    /// </summary>
    public Report CountNotice(Tally tally, string noun, string howToSeeMore = "")
        => tally.IsTruncated
            ? Notice(NoticeKind.Truncation,
                     $"Showing {tally.Render(noun)}{Within}" +
                     (howToSeeMore.Length == 0 ? "." : $"; {howToSeeMore}"))
            : Notice(NoticeKind.Count, $"{tally.Render(noun)}{Within}.");

    /// <summary>
    /// 分页态的计数,产地唯一。
    ///
    /// 没有 <c>--offset</c> 的表只有两条出路:把 <c>--limit</c> 抬到全量(一次吃掉整个
    /// 上下文预算),或者管道接 head(把声明区连同计数一起截掉,而那是这套输出唯一
    /// 说得清「你没看到什么」的地方)。
    ///
    /// 三件事恒在:这一页几条、总共几条、下一页怎么要。到头时**不给**下一页的参数 ——
    /// 一句「pass --offset N」挂在最后一页上,会被读成后面还有;而末页照样得明说
    /// 「这是最后一页」,否则「4 of 8 defs, starting at 5」与半截结果同形。
    ///
    /// 留下的只有算出来的那个 offset。「--limit all 能一次吃完」「与其翻页不如用 --path 筛」
    /// 都逐字不随查询变,SKILL.md 已按命令列全,在这里逐次重念是同一份知识的第三个副本。
    /// </summary>
    public Report PageNotice(string noun, int shown, int offset, int total)
    {
        var seen = offset + shown;
        var tally = shown < total ? Tally.Of(shown, total) : Tally.Complete(shown);
        return Notice(tally.IsTruncated ? NoticeKind.Truncation : NoticeKind.Count,
            tally.Render(noun) + Within +
            (offset > 0 ? $", starting at {offset + 1}" : "") +
            (seen < total
                ? $"; pass --offset {seen} for the next page."
                : offset > 0
                    ? "; that is the last page."
                    : "."));
    }

    /// <summary>
    /// 翻过了头。**不是**「没有这个东西」—— 分开说,否则一次翻页会被读成一次否定。
    ///
    /// 前半截各命令逐字相同,尾句各说各的(数的东西不一样)。<paramref name="rest"/>
    /// 接的就是那个尾句,自带句号。
    /// </summary>
    public Report PastEnd(int offset, string rest)
        => Notice(NoticeKind.NextStep, $"--offset {offset} is past the end: {rest}");

    /// <summary>
    /// 只在被截断时发声。留给「完整态另有更贴切的说法」的调用点 —— 那里再补一条裸计数
    /// 就成了两句话说同一件事。
    /// </summary>
    public Report TruncationNotice(Tally tally, string noun, string howToSeeMore)
    {
        if (!tally.IsTruncated) return this;
        return Notice(NoticeKind.Truncation,
            $"Showing {tally.Render(noun)}; {howToSeeMore}");
    }

    private string? _collection;
    private int _item = -1;

    /// <summary>
    /// 开始集合里的下一项。之后加进来的块都归它,直到 <see cref="EndItems"/>。
    ///
    /// 键里**不能拼名字**:同名跨 def 类型会撞键,后写的静默覆盖先写的,而拼进键里的
    /// 名字本来就没法安全解析(段数还随内容变)。所以这一层走集合。
    /// </summary>
    public Report Item(string collection)
    {
        if (_collection != collection) { _collection = collection; _item = 0; }
        else _item++;
        return this;
    }

    public Report EndItems() { _collection = null; _item = -1; return this; }

    public Report Add(Block block)
    {
        _entries.Add(_collection is null ? block : block with { Collection = _collection, Item = _item });
        return this;
    }

    public Report Table(string name, IReadOnlyList<string> columns,
                        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? caption = null)
        => Add(new TableBlock(name, columns, rows, caption));

    public Report Detail(string name, IReadOnlyList<KeyValuePair<string, object?>> pairs)
        => Add(new DetailBlock(name, pairs));

    public Report Text(string name, IReadOnlyList<string> lines,
                       IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null)
        => Add(new TextBlock(name, lines, rows));
}

public static class OutputText
{
    public const string Newline = "\n";

    /// <summary>
    /// 收口:行尾一律 LF(AppendLine 在 Windows 出 CRLF 会与写死的 \n 混形)、
    /// TrimEnd 尾空行。所有走 stdout 的文本都必须过这里。
    /// </summary>
    public static string Finish(string s)
        => s.Replace("\r\n", Newline).Replace('\r', '\n').TrimEnd('\n', ' ', '\t') + Newline;

    public static string Join(IEnumerable<string> lines)
        => Finish(string.Join(Newline, lines));

    /// <summary>把值渲染成单元格文本。null 渲染成空,不渲染成 "null"。</summary>
    public static string Cell(object? v) => v switch
    {
        null => "",
        bool b => b ? "yes" : "no",
        string s => s.Replace("\r", "").Replace("\n", " "),
        _ => v.ToString() ?? "",
    };

    public static string Truncate(string s, int max)
    {
        if (Width(s) <= max) return s;
        var sb = new StringBuilder();
        var w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var rw = RuneWidth(r.Value);
            if (w + rw > max - 1) break;
            sb.Append(r);
            w += rw;
        }
        return sb.Append('…').ToString();
    }

    /// <summary>
    /// 这段字在终端上占几格。对齐是拿空格补出来的,所以补几个得问显示宽度而不是
    /// <c>string.Length</c> —— 后者按 UTF-16 码元数,于是 CJK 标签那一列整体左偏,
    /// 而这套输出的读者一半是人。
    ///
    /// 按 rune 走不按 char 走:CJK 扩展 B(U+20000 起)是代理对,一个字两个 char,
    /// 数 char 会把一个两格宽的字算成四格。
    /// </summary>
    public static int Width(string s)
    {
        var w = 0;
        foreach (var r in s.EnumerateRunes()) w += RuneWidth(r.Value);
        return w;
    }

    /// <summary>
    /// 区间取自 Unicode East Asian Width 的 W / F 两类,外加宽度为零的组合记号。
    /// 逐个码点判而不是查表:这里只需要「1 还是 2」,不需要完整的 EAW 属性。
    /// </summary>
    private static int RuneWidth(int c) => c switch
    {
        >= 0x0300 and <= 0x036F => 0,      // 组合用变音记号:附在前一个字上,不占格
        >= 0x200B and <= 0x200F => 0,      // 零宽空格与方向标记
        >= 0x1100 and <= 0x115F => 2,      // 韩文字母
        >= 0x2E80 and <= 0x303E => 2,      // 部首、康熙部首、CJK 符号与标点
        >= 0x3041 and <= 0x33FF => 2,      // 假名、注音、CJK 兼容
        >= 0x3400 and <= 0x4DBF => 2,      // CJK 扩展 A
        >= 0x4E00 and <= 0x9FFF => 2,      // CJK 统一表意
        >= 0xA000 and <= 0xA4CF => 2,      // 彝文
        >= 0xAC00 and <= 0xD7A3 => 2,      // 韩文音节
        >= 0xF900 and <= 0xFAFF => 2,      // CJK 兼容表意
        >= 0xFE30 and <= 0xFE6F => 2,      // 竖排标点、小写变体、全角标点
        >= 0xFF00 and <= 0xFF60 => 2,      // 全角字母数字与标点
        >= 0xFFE0 and <= 0xFFE6 => 2,      // 全角货币与符号
        >= 0x1F300 and <= 0x1FAFF => 2,    // emoji 与符号
        >= 0x20000 and <= 0x3FFFD => 2,    // CJK 扩展 B 及以后
        _ => 1,
    };
}
