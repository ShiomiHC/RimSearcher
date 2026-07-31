namespace RimSearcher.Output;

/// <summary>
/// 三态截断文法 —— 这套输出的头号资产。
///
///   裸 N          —— 这就是完整集合,没有更多了
///   N of M        —— 被上限截断,M 是总数
///   at least N    —— 只知道下界(没数全就停了)
///
/// 三态必须是**同一个产地渲染出来的三种形态**,不能有哪条路径绕开它自己拼句子。
///
/// 「完整」不能由沉默承载:输出变短有两个成因(行数上限,以及匹配级提前停 ——
/// search 命中第一级就不跑后面的),两条路通向同一片空白,读者只能归纳出「完整」。
/// **计数恒在**,三态在同一位置用同一文法区分。
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
/// 可数名词登记处。输出里出现的每个被计数名词都必须在这里登记复数形式 ——
/// 防止某条新写的路径自己拼 "s" 拼出 "matchs"。
/// </summary>
public static class NounRegistry
{
    private static readonly Dictionary<string, string> Plurals = new(StringComparer.Ordinal)
    {
        ["def"] = "defs",
        ["def type"] = "def types",
        // 运行时 class 与存储桶不是一回事:数 class 的地方不能借「def type」这个词。
        ["def class"] = "def classes",
        ["field"] = "fields",
        ["field path"] = "field paths",
        ["value"] = "values",
        ["match"] = "matches",
        ["file"] = "files",
        ["C# file"] = "C# files",
        ["mod"] = "mods",
        ["translation"] = "translations",
        // 界面文案那一层单独登记,不借 "translation":def 的 label 走 DefInjected、
        // keyed 走 key,两层的来源与生效规则都不同。
        ["keyed translation"] = "keyed translations",
        ["key"] = "keys",
        // code-search 数的是「代码行里出现的 key」,与上面那个 "key"(库里的一条 keyed
        // 记录)不是同一批东西 —— 一行代码里的 key 可能压根不在库里。
        ["translation key"] = "translation keys",
        ["source tree"] = "source trees",
        ["XML node"] = "XML nodes",
        ["direct child"] = "direct children",
        ["patch operation"] = "patch operations",
        ["assembly"] = "assemblies",
        ["directory"] = "directories",
        ["line"] = "lines",
        ["declaration"] = "declarations",
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
