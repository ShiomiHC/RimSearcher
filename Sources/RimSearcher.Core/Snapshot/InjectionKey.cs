using System.Text.RegularExpressions;

namespace RimSearcher.Snapshot;

/// <summary>
/// 注入键 → 对外坐标。产地唯一:导入侧照这里写库,呈现侧照这里说话。
///
/// 游戏对同一个列表元素认两种键串:下标式 <c>stages.0.label</c> 与把手式
/// <c>stages.observed_corpse.label</c>(把手来自元素的 label,见
/// <c>TranslationHandleUtility</c>),语言文件里两种都真注入得上。而字段表那一侧用的是
/// 第三种写法 <c>stages[0].label</c>。三种并存的后果是 <c>--path-contains stages[0]</c>
/// 对译文那栏恒回零 —— 与「这个 def 没这条译文」逐字同形。
///
/// 所以库里存两样东西:<c>key</c> 是数据源给的那一串,原样不动(要写语言文件的人需要它);
/// <c>path</c> 是归一到字段表文法的那一串,两张表于是同坐标可比。
/// </summary>
public static class InjectionKey
{
    /// <summary>数字下标段 → 方括号。<c>stages.0.label</c> → <c>stages[0].label</c>。</summary>
    private static readonly Regex NumericSegment = new(@"\.(\d+)(?=\.|$)", RegexOptions.Compiled);

    /// <summary>任意下标 → <c>[0]</c>。type_fields 把每个列表位都归到 0,查它得先同形。</summary>
    private static readonly Regex AnyIndex = new(@"\[\d+\]", RegexOptions.Compiled);

    /// <summary>
    /// 下标式注入键 → 字段表文法。纯机械改写,不查任何表:注入键里的纯数字段**一定**是
    /// 列表位置 —— <c>DefInjectionPackage</c> 分段时第一条判据就是 <c>int.TryParse</c>,
    /// 认下来的那支直接当索引用,不走把手那条路。
    /// </summary>
    public static string ToFieldPath(string key) =>
        NumericSegment.Replace(key, m => "[" + m.Groups[1].Value + "]");

    /// <summary>把每个下标归到 <c>[0]</c> —— 拿去和 <c>type_fields</c> 的路径比。</summary>
    public static string CanonicalIndex(string fieldPath) => AnyIndex.Replace(fieldPath, "[0]");

    /// <summary>
    /// 这条译文的键在**槽位名册**上找得到吗。名册是一个槽位一行的
    /// <c>injection_keys</c>,产地是游戏自己的 <c>ForEachPossibleDefInjection</c>。
    ///
    /// 三个取值各自单义,合并任何两个都会让一种「印出来与真相同形」重新出现。
    /// 缺这一列(<c>null</c>)是第四态:这份快照没有名册,问不出口。
    ///
    /// **这一列不记「键是哪种拼法」。** 早先一版记的是 handle / index / unmapped /
    /// untested —— 拼法与在不在册两件事挤在一列里,而判在不在册用的是字段表当替身:
    /// 整表注入的键不带元素下标,字段表里那条路径带,逐字比一次不中,于是 1348 条里
    /// 956 条被判成坏译文。拼法这件事读者本来也不需要:<c>key</c> 那一格就是原样。
    /// </summary>
    public static class State
    {
        /// <summary>名册上有这个键,而且这个槽位允许注入译文。</summary>
        public const string Resolved = "resolved";

        /// <summary>
        /// 名册上没有这个键。**游戏那边同样注入不上** —— 所以这不是查询侧的缺陷,是数据里
        /// 真实存在的一种坏译文。主要成因是把手过期:把手取自 label,作者改了 label 之后
        /// 旧译文的键就再也配不上任何槽位。
        ///
        /// 这一档的 <c>path</c> 是 key 的机械改写,**不是与字段表可比的坐标**。
        /// </summary>
        public const string NoSlot = "no-slot";

        /// <summary>
        /// 名册上有这个键,但这个槽位标着不许译(<c>NoTranslate</c> / <c>Unsaved</c>,
        /// 或者上游某一层这么标了)。键没写错,游戏照样不认这条译文。
        /// 与 <see cref="NoSlot"/> 分开:出路不同 —— 那一档要改键,这一档改了也没用。
        /// </summary>
        public const string Refused = "refused";
    }
}
