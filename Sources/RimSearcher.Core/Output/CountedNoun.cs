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

    /// <summary>
    /// 同样三态,但**总数占第一个数的位置**。只给计数说明那一行用,句中的 tally 不换 ——
    /// 「across 3 source trees, showing the first 1」在从句里读不通。
    ///
    /// 换位的依据不是措辞好坏,是这两个数性质不对等:<c>Shown</c> 是 <c>--limit</c> 缺省
    /// 造出来的数,<c>Total</c> 是数据里的数。注意力锚落在第一个数上,那个位置该给后者。
    ///
    /// **实测(2026-08-05,30 个 haiku 闭卷样本):裸截断行上 `25 of 79` 组 0/7、本形态 7/7。**
    /// 同一批模型同一批题走 <c>--json</c> 时两组都 3/3 —— 所以旧形态的全灭不是模型数不清,
    /// 是那行文字在主动误导。
    ///
    /// 赢的可能不止锚点位置,还切断了一条误读路径:`25 of 79` 的语法允许 79 被读成**定语**
    /// (「79 个里挑出的 25 个」),实测抓到一例把 79 复述出来了仍报 25 —— 它把总数读成了
    /// 搜索范围。本形态把总数钉成主语,7/7 里零例角色倒置。
    ///
    /// **代价照记**:反方向(问「本页印了几行」,真值 25)本形态 2/3、旧形态 3/3,一例把 79
    /// 当成了本页。样本小,量级未定。取舍是赔率:真实查询里问总数远比问本页行数常见,
    /// 而这边是从 0 抬到满分。
    ///
    /// **措辞一个字都不许动** —— 7/7 是对这句话测出来的,改一个词那个数就不成立了。
    /// </summary>
    /// <param name="fromStart">
    /// 这一批是不是从头取的。<c>false</c> 时不许说 "the first" —— <c>--offset</c> 翻到中间那页
    /// 时那句话是**假的**(`22 field paths, showing the first 3, starting at 4`),
    /// 而起始位置由调用方在后面自己补。
    /// </param>
    public string RenderTotalFirst(string noun, bool fromStart = true)
    {
        if (LowerBound) return $"at least {Shown} {NounRegistry.Form(noun, Shown)}";
        if (Total is { } t && t > Shown)
            return $"{t} {NounRegistry.Form(noun, t)}, showing {(fromStart ? "the first " : "")}{Shown}";
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
        // 下标归一后的路径。与 "field path" 分开登记:comps[0..4].explosiveRadius 是
        // 五条 field path、一个 path shape,而做集合运算的人问的是后者。
        ["path shape"] = "path shapes",
        ["value"] = "values",
        ["match"] = "matches",
        ["file"] = "files",
        ["C# file"] = "C# files",
        ["mod"] = "mods",
        // 数的是 .rml 文件本身,与「列表里有几个 mod」不是一回事。
        ["mod list"] = "mod lists",
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
        // 往上那条链数的是层数,与 "direct child"(往下一层的宽度)不是一回事。
        ["ancestor"] = "ancestors",
        ["patch operation"] = "patch operations",
        ["assembly"] = "assemblies",
        ["directory"] = "directories",
        ["line"] = "lines",
        ["declaration"] = "declarations",
        // 数的是「按当前页大小还要翻几次」,与 "line"(总量)不是一回事 —— 同一个文件换个
        // --limit 就换一个页数,而行数不变。
        ["page"] = "pages",
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
