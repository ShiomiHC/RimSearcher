namespace RimSearcher.Output;

/// <summary>
/// 三态截断文法(01 的头号资产,master 被盲测反复验证过的第一优先级问题)。
///
///   裸 N          —— 这就是完整集合,没有更多了
///   N of M        —— 被上限截断,M 是总数
///   at least N    —— 只知道下界(没数全就停了)
///
/// 教训(01):表头没有行数时,「裸 N = 完整」曾被盲测方归纳成假规则再交付给用户。所以
/// 三态必须是**同一个产地渲染出来的三种形态**,不能有哪条路径绕开它自己拼句子。
///
/// 第二轮盲测推翻了「完整态一个字都不说」这条口径(四个 agent 独立踩,其中一个据此
/// **二次确认**了一个错答案)。省字节的做法是让「完整」由**沉默**承载,而沉默只在
/// 「输出可能变短的原因有且只有一个」时才无歧义。实际有两个:行数上限,以及匹配级
/// 提前停(search 命中第一级就不跑后面的)。两条路都通向同一片空白,于是
/// `search VoidNode` 与 `search VoidNode --limit all` 逐字相同,读者只能归纳出「完整」——
/// 而真值是漏了一条。**计数恒在**,三态在同一位置用同一文法区分,代价是十来个字节。
/// </summary>
public readonly record struct Tally
{
    private Tally(int shown, int? total, bool lowerBound)
    {
        Shown = shown; Total = total; LowerBound = lowerBound;
    }

    public int Shown { get; }
    public int? Total { get; }
    public bool LowerBound { get; }

    /// <summary>已知这就是全部。</summary>
    public static Tally Complete(int shown) => new(shown, shown, false);

    /// <summary>知道总数;total &gt; shown 时渲染成 <c>N of M</c>。</summary>
    public static Tally Of(int shown, int total) => new(shown, total, false);

    /// <summary>只有下界(扫描被上限打断,总数未知)。</summary>
    public static Tally AtLeast(int shown) => new(shown, null, true);

    public bool IsTruncated => LowerBound || (Total is { } t && t > Shown);

    /// <summary>渲染成「12 defs」/「12 of 347 defs」/「at least 12 defs」。</summary>
    public string Render(string noun)
    {
        var word = NounRegistry.Form(noun, LowerBound || Total is null ? Shown : Total.Value);
        if (LowerBound) return $"at least {Shown} {NounRegistry.Form(noun, Shown)}";
        if (Total is { } t && t > Shown) return $"{Shown} of {t} {word}";
        return $"{Shown} {NounRegistry.Form(noun, Shown)}";
    }
}

/// <summary>
/// 可数名词登记处。闸(CountedNounRegistryTests 的对应物)要求:输出里出现的每个被计数名词
/// 都在这里登记过复数形式 —— 防止某条新写的路径自己拼 "s" 拼出 "matchs"。
/// </summary>
public static class NounRegistry
{
    private static readonly Dictionary<string, string> Plurals = new(StringComparer.Ordinal)
    {
        ["def"] = "defs",
        ["def type"] = "def types",
        ["field"] = "fields",
        ["field path"] = "field paths",
        ["value"] = "values",
        ["match"] = "matches",
        ["file"] = "files",
        ["mod"] = "mods",
        ["translation"] = "translations",
        ["source tree"] = "source trees",
        ["XML node"] = "XML nodes",
        ["direct child"] = "direct children",
        ["patch operation"] = "patch operations",
    };

    public static IReadOnlyCollection<string> Known => Plurals.Keys;

    public static bool IsRegistered(string noun) => Plurals.ContainsKey(noun);

    public static string Form(string noun, int count)
    {
        if (!Plurals.TryGetValue(noun, out var plural))
            throw new InvalidOperationException(
                $"Counted noun '{noun}' is not registered in NounRegistry. " +
                "Register it there rather than pluralising at the call site.");
        return count == 1 ? noun : plural;
    }
}
