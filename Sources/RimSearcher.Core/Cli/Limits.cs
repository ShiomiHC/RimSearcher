namespace RimSearcher.Cli;

/// <summary>
/// CLI 侧的数值上限 —— 声明层的数字产地。
///
/// 散文里出现的每个数字都从这里插值(--help、cli-reference.md、输出里的自证句子),
/// 不要在别处写死。
/// </summary>
public static class Limits
{
    /// <summary>列表类命令未指定 --limit 时的默认条数。</summary>
    public const int DefaultLimit = 25;

    /// <summary>--limit 允许的最大值;超出会被夹紧。</summary>
    public const int MaxLimit = 2000;

    /// <summary>
    /// code-search 单文件最多**印出**的匹配行数(--max-per-file 的默认值)。
    /// 过了它的命中照样计数,所以它不影响总数准不准。
    /// </summary>
    public const int CodeSearchMatchesPerFile = 20;

    /// <summary>
    /// code-search 最多**读**的文件数;超出即停,计数降级成 at least 形态。
    ///
    /// 取值远高于真实规模(全部源码树合计约两万个 .cs,全量扫 120 MB 只需 1.6 秒):
    /// 这是失控兜底(一棵畸形大树),不是预算闸。
    /// </summary>
    public const int CodeSearchMaxFiles = 50000;

    /// <summary>code-search 正则单文件匹配超时(毫秒),防灾难性回溯。</summary>
    public const int CodeSearchRegexTimeoutMs = 2000;

    /// <summary>read 不给 --lines 时读多少行。翻页的一页就是它。</summary>
    public const int ReadWindow = 150;

    /// <summary>
    /// read 一次最多印多少行(--limit 的默认值,也是它的上限)。
    ///
    /// 反编译出的大类动辄四五千行,而输出被整个读进上下文 —— 这道闸挡的是
    /// 「一次调用吃掉整个上下文预算」。
    /// </summary>
    public const int ReadMaxLines = 2000;

    /// <summary>同名文件几选一时最多列几条。</summary>
    public const int AmbiguousFiles = 8;

    /// <summary>get 命令默认展开的字段条数;超出以三态文法声明。</summary>
    public const int DefaultFieldsPerDef = 60;

    /// <summary>声明区(散文)最多行数。超出即聚合成尾注,防止声明挤占上下文。</summary>
    public const int MaxNoticeLines = 6;

    /// <summary>未知 flag 报错时最多给出的近似候选数。</summary>
    public const int MaxSuggestions = 3;

    /// <summary>
    /// 取样式提示为了不把分界线切在并列上,最多能展开到几条。
    ///
    /// 常规取样的两倍 —— 不是从数据里挑出来的阈值。真快照上确实有全部同大的退化情形
    /// (<c>ManeuverDef</c> 上值为 <c>0</c>:23 个路径形状一样大),没有天花板时一行提示会被
    /// 撑成 23 项;而 601 个采样值 × def_type 共 4240 组里,想要超过这个数的只有 16 组。
    /// </summary>
    public const int MaxShownShapes = MaxSuggestions * 2;

    /// <summary>模糊匹配回退触发前,精确/前缀匹配需要达到的最少命中数。</summary>
    public const int FuzzyFallbackThreshold = 1;
}
