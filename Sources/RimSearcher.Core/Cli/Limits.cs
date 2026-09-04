namespace RimSearcher.Cli;

/// <summary>
/// CLI 侧的数值上限 —— 声明层的数字产地。
///
/// 散文里出现的每个数字都从这里插值(--help、cli-reference.md、输出里的自证句子),
/// 不要在别处写死。
/// </summary>
public static class Limits
{
    /// <summary>
    /// 猜错字段值时,拿来当近似候选的那份值域最多取几条。这是**内部取样界**,
    /// 不是 --limit —— 用户那一侧不给就是全部,给了就照给的数来,没有任何夹板。
    /// </summary>
    public const int ValueSpaceSample = 2000;

    /// <summary>code-search 正则单文件匹配超时(毫秒),防灾难性回溯。</summary>
    public const int CodeSearchRegexTimeoutMs = 2000;

    /// <summary>同名文件几选一时最多列几条。</summary>
    public const int AmbiguousFiles = 8;


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
