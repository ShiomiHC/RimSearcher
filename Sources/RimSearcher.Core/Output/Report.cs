using System.Text;

namespace RimSearcher.Output;

/// <summary>
/// 声明的类别。闸按类别判「说没说」,不判渲染完的字(01 提交 1338603 的教训:规则用
/// Contains 短子串重新声明「该怎么说」时,同一句话红不红取决于成因措辞)。
/// </summary>
public enum NoticeKind
{
    /// <summary>结果被上限截断(02-3 暗截断的对策)。</summary>
    Truncation,
    /// <summary>结果计数,完整集也报(三态文法的「裸 N」态)。</summary>
    Count,
    /// <summary>调用方自己要求的过滤,不是截断 —— 机器侧靠 kind 分类,两者混用会被读成结果不完整。</summary>
    Filter,
    /// <summary>快照与当前游戏环境不一致(02-4 过期自证)。</summary>
    Staleness,
    /// <summary>用了哪个快照、为什么。</summary>
    SnapshotChoice,
    /// <summary>能力边界:本次输出没做什么(R51 —— 写进它作用的那个块)。</summary>
    Boundary,
    /// <summary>数据来自快照环境之外,仅供参考(静态收割的翻译)。</summary>
    Advisory,
    /// <summary>参数被夹紧到上限。</summary>
    Clamp,
    /// <summary>下一步该怎么做的指路。</summary>
    NextStep,
}

public sealed record Notice(NoticeKind Kind, string Text, bool Footnote = false);

public abstract record Block
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

/// <summary>自由文本块(code-search 的代码片段)。</summary>
public sealed record TextBlock(string Name, IReadOnlyList<string> Lines) : Block;

/// <summary>
/// 一次命令输出的完整模型。命令只管往里塞内容,行尾/尾空行/声明区排布由渲染器统一收口
/// (01 ToolResult 条目:行尾一律 LF、TrimEnd 尾空行 —— 空行会被 LLM 读成「后面被截断了」)。
/// </summary>
public sealed class Report
{
    private readonly List<Notice> _notices = [];
    private readonly List<Block> _blocks = [];

    public IReadOnlyList<Notice> Notices => _notices;
    public IReadOnlyList<Block> Blocks => _blocks;

    public Report Notice(NoticeKind kind, string text, bool footnote = false)
    {
        _notices.Add(new Notice(kind, text, footnote));
        return this;
    }

    /// <summary>
    /// 计数恒在。完整集渲染成裸 N 并按 <see cref="NoticeKind.Count"/> 归类,被截时追加
    /// 怎么看到剩下的、按 <see cref="NoticeKind.Truncation"/> 归类 —— 两态同一个产地、
    /// 同一个位置,读者不必靠「有没有那句话」反推(第二轮盲测:靠沉默传达完整会被读错)。
    /// </summary>
    public Report CountNotice(Tally tally, string noun, string howToSeeMore)
        => tally.IsTruncated
            ? Notice(NoticeKind.Truncation, $"Showing {tally.Render(noun)}; {howToSeeMore}")
            : Notice(NoticeKind.Count, $"{tally.Render(noun)}.");

    /// <summary>
    /// 只在被截断时发声。留给「完整态另有更贴切的说法」的调用点(get 的字段表由
    /// --path 分支自己报数,再补一条裸计数就成了两句话说同一件事)。
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
    /// 起因(第二轮盲测,3 个 agent):get 输出多个同名 def 时,JSON 的键是
    /// <c>fields:{DefName}</c> 两段而 <c>def:{DefName}:{DefType}</c> 三段,同名跨 def 类型
    /// 就撞键,后写的把先写的**静默覆盖**掉。更毒的是同一份输出里 notes 还在说
    /// 「1 field matched」,而那个键的值是空数组 —— 自相矛盾的输出比报错危险得多。
    /// 键里拼名字本来就没法安全解析,所以改成集合。
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
        _blocks.Add(_collection is null ? block : block with { Collection = _collection, Item = _item });
        return this;
    }

    public Report Table(string name, IReadOnlyList<string> columns,
                        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? caption = null)
        => Add(new TableBlock(name, columns, rows, caption));

    public Report Detail(string name, IReadOnlyList<KeyValuePair<string, object?>> pairs)
        => Add(new DetailBlock(name, pairs));

    public Report Text(string name, IReadOnlyList<string> lines)
        => Add(new TextBlock(name, lines));
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
        => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";
}
