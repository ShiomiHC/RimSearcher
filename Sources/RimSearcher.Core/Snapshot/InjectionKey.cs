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
    /// <c>path</c> 是怎么从 <c>key</c> 来的。四个取值各自单义,合并任何两个都会让一种
    /// 「印出来与真相同形」重新出现。
    /// </summary>
    public static class Form
    {
        /// <summary>
        /// key 里有把手,快照自己的注入键表给出了对应的下标式,<c>path</c> 由它改写而来。
        /// 这一档的依据是游戏自己在 <c>ForEachPossibleDefInjection</c> 里配的那一对,
        /// 不是本项目猜的。
        /// </summary>
        public const string Handle = "handle";

        /// <summary>
        /// key 本来就是下标式(改写后落在这个 def 类型的字段路径全集里),<c>path</c> 是它的
        /// 机械改写。
        /// </summary>
        public const string Index = "index";

        /// <summary>
        /// 两条都不成立:注入键表里没有它,改写后也不是这个类型的字段路径。<c>path</c> 是
        /// key 的机械改写,**它不是与字段表可比的坐标**。
        ///
        /// 主要成因是把手过期 —— 把手取自 label,作者改了 label 之后旧译文的键就再也配不上
        /// 任何槽位(游戏那边这条译文同样注入不上)。所以这一档不是查询侧的缺陷,它是
        /// 数据里真实存在的一种坏译文,而合并进 <see cref="Index"/> 会把它印成好的。
        /// </summary>
        public const string Unmapped = "unmapped";

        /// <summary>
        /// 没判 —— 这条译文的 def 类型判不出来(注入 key 不带类型这一维),于是
        /// 「是不是这个类型的字段路径」这个问题问不出口。<c>path</c> 是 key 的机械改写。
        /// 与 <see cref="Unmapped"/> 分开:那一档是问过了、没配上,这一档是没得问。
        /// </summary>
        public const string Untested = "untested";
    }
}
