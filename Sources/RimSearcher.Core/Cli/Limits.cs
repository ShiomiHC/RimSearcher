namespace RimSearcher.Cli;

/// <summary>
/// CLI 侧的数值上限 —— 声明层的数字产地。
///
/// 纪律(01 声明政策 / master SearchRegexTool.Description 范式):散文里出现的每个数字都从
/// 这里插值,改上限时 --help、cli-reference.md、输出里的自证句子同时跟着变。任何地方写死
/// 一个与此处不同的数,就是把产地劈成了两份。
/// </summary>
public static class Limits
{
    /// <summary>列表类命令未指定 --limit 时的默认条数。</summary>
    public const int DefaultLimit = 25;

    /// <summary>--limit 允许的最大值;超出会被夹紧(夹紧不声明成硬约束,把数写进散文 —— 01 声明政策)。</summary>
    public const int MaxLimit = 2000;

    /// <summary>
    /// code-search 单文件最多**印出**的匹配行数(--max-per-file 的默认值)。
    /// 过了它的命中照样计数,所以它不影响总数准不准。
    /// </summary>
    public const int CodeSearchMatchesPerFile = 20;

    /// <summary>
    /// code-search 最多**读**的文件数;超出即停,计数降级成 at least 形态。
    ///
    /// 4000 → 50000(三轮 R3):旧值低于单棵 vanilla 树(本机 10222 个 .cs,全部 24 棵树
    /// 合计 19467),于是任何一条不带 --source 的问句都在默认配置下被截掉八成,而截断
    /// 恰恰是本命令最贵的错法。实测全量扫一遍 120 MB 树是 1.6 秒 —— 这道闸原本挡的成本
    /// 根本不存在,它只是在制造假的零结果。留着是当失控兜底(一棵畸形大树),不是当预算。
    /// </summary>
    public const int CodeSearchMaxFiles = 50000;

    /// <summary>code-search 正则单文件匹配超时(毫秒),防灾难性回溯。</summary>
    public const int CodeSearchRegexTimeoutMs = 2000;

    /// <summary>read 不给 --lines 时读多少行。翻页的一页就是它。</summary>
    public const int ReadWindow = 150;

    /// <summary>
    /// read 一次最多印多少行(--limit 的默认值,也是它的上限)。
    ///
    /// 一个反编译出来的大类动辄四五千行,而这份输出是被整个读进上下文的 —— 这道闸挡的是
    /// 「一次调用吃掉整个预算」。它只管印:总行数与该翻到哪一页恒在,所以被它咬到不会
    /// 变成一个看不出来的截断。
    /// </summary>
    public const int ReadMaxLines = 2000;

    /// <summary>同名文件几选一时最多列几条。</summary>
    public const int AmbiguousFiles = 8;

    /// <summary>get 命令默认展开的字段条数;超出以三态文法声明。</summary>
    public const int DefaultFieldsPerDef = 60;

    /// <summary>声明区(散文)最多行数。超出即聚合成尾注,防止声明挤占上下文(06 上下文预算硬约束)。</summary>
    public const int MaxNoticeLines = 6;

    /// <summary>未知 flag 报错时最多给出的近似候选数。</summary>
    public const int MaxSuggestions = 3;

    /// <summary>模糊匹配回退触发前,精确/前缀匹配需要达到的最少命中数。</summary>
    public const int FuzzyFallbackThreshold = 1;
}
