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

    /// <summary>code-search 单文件最多回传的匹配行数。</summary>
    public const int CodeSearchMatchesPerFile = 20;

    /// <summary>code-search 最多扫描的文件数;超出即停,计数以 at least 形态回传。</summary>
    public const int CodeSearchMaxFiles = 4000;

    /// <summary>code-search 正则单文件匹配超时(毫秒),防灾难性回溯。</summary>
    public const int CodeSearchRegexTimeoutMs = 2000;

    /// <summary>get 命令默认展开的字段条数;超出以三态文法声明。</summary>
    public const int DefaultFieldsPerDef = 60;

    /// <summary>声明区(散文)最多行数。超出即聚合成尾注,防止声明挤占上下文(06 上下文预算硬约束)。</summary>
    public const int MaxNoticeLines = 6;

    /// <summary>未知 flag 报错时最多给出的近似候选数。</summary>
    public const int MaxSuggestions = 3;

    /// <summary>模糊匹配回退触发前,精确/前缀匹配需要达到的最少命中数。</summary>
    public const int FuzzyFallbackThreshold = 1;
}
