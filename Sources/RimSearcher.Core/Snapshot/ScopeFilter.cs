using RimSearcher.Config;
using RimSearcher.Output;

namespace RimSearcher.Snapshot;

/// <summary>
/// scope = **快照内按 mod 维度筛结果**。<c>--scope</c> 只管过滤,**不选快照** ——
/// 选哪次导出是 <c>--snapshot</c> / <c>snapshot use</c> / 自动检测那三层的事。
///
/// 语法:逗号分隔;<c>-x</c> 表示排除;组名在 config.toml 的 [scope_groups] 里定义。
/// 首个词是排除时,起点集合为全部(<c>all,-vanilla</c> 与 <c>-vanilla</c> 同义)。
/// </summary>
public sealed class ScopeFilter
{
    public const string DefaultScope = "all";

    private readonly HashSet<string> _included;
    private readonly bool _all;
    private readonly HashSet<string> _universe;
    // 「除了 X 之外的全部」里那个 X 的原文(逗号拼接)。这个形态下补集恰好就是这些词的
    // 并集,于是句子里给得出一条能直接敲的命令,而不是回显一串 packageId。
    // null = 这个表达式不是那个形态 —— 但那有两种成因,见 Complement 的注释,
    // 别把它们当成同一件事。
    private readonly string? _complementExpression;

    public string Expression { get; }
    public IReadOnlyCollection<string> UnknownTokens { get; }

    private ScopeFilter(string expression, HashSet<string> included, bool all, List<string> unknown,
                        HashSet<string> universe, string? complementExpression)
    {
        Expression = expression;
        _included = included;
        _all = all;
        UnknownTokens = unknown;
        _universe = universe;
        _complementExpression = complementExpression;
    }

    /// <summary>
    /// 这个 scope 排除掉的那一半,本身也是个可用的 scope。**只对起点是全集的排除式**
    /// (<c>all,-X</c> / <c>-X</c>)给得出。
    ///
    /// 用处是止住一张**静默的错表**:排除式的心智模型是「我几乎什么都有,只是不想要 X」,
    /// 而 X 里可能正是答案。实测 <c>where compClass --value Vethara --scope all,-vanilla</c>
    /// 返回 92 个干净、完整、看不出任何问题的 def —— 而问的那 7 个宿主全在被排除的 vanilla 里。
    ///
    /// 返回 null 有**两种成因,别混**:
    /// <list type="bullet">
    /// <item><c>vanilla,-core</c> 这种白名单再减 —— 补集里混着全部第三方 mod,
    /// **拼不出**一条能直接敲的命令。算不出。</item>
    /// <item>纯白名单 <c>--scope vanilla</c> —— 补集**拼得出**(就是 <c>all,-vanilla</c>),
    /// 这里是**不说**。白名单的心智模型是「我要的恰好是这些」,边界本来就是明说的;
    /// 这句话瞄的是「我几乎什么都有」那个假完整感,对白名单一律为真,就成了纯噪声。</item>
    /// </list>
    /// 后一条的沉默现在是**承重的**:消费侧薄层拿「白名单侧没有这句」当判据,读成
    /// 「那边的排除就是你的本意」。要给白名单也开这句,得先知会那边 —— 别当成
    /// 一处漏补的不对称顺手改了。闸在 <c>排除式scope说破被排除的那一半</c> 里。
    /// </summary>
    public ScopeFilter? Complement()
    {
        if (_complementExpression is null || _all) return null;
        var rest = new HashSet<string>(_universe, StringComparer.OrdinalIgnoreCase);
        rest.ExceptWith(_included);
        if (rest.Count == 0) return null;
        return new ScopeFilter(_complementExpression, rest, false, [], _universe, null);
    }

    public bool IsAll => _all;

    public bool Includes(string? packageId)
        => _all || (packageId is not null && _included.Contains(packageId));

    /// <summary>
    /// 这个 scope 实际圈住了谁 —— 写进散文时用这个,不要用裸的 <see cref="Expression"/>。
    ///
    /// 「vanilla」一词两义:<c>--scope vanilla|base|core|official</c> 展开成每个
    /// <c>ludeon.rimworld*</c> 模块(Core 加全部已装 DLC),而一份**叫** vanilla 的
    /// 快照可能只有 Core。
    ///
    /// 只有一个调用点(<c>CommandBase.AnnounceScope</c>,展开与字面不同时无条件说一句):
    /// 展开一句话说一次,别在同一次输出里重复两遍。
    /// </summary>
    public string Describe()
    {
        if (_all) return Expression;
        if (_included.Count == 0) return $"{Expression} (which matches no mod in this snapshot)";

        var ids = _included.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 1)
            return string.Equals(ids[0], Expression, StringComparison.OrdinalIgnoreCase)
                ? Expression
                : $"{Expression} (= {ids[0]})";

        return ids.Count <= 4
            ? $"{Expression} (= {string.Join(", ", ids)})"
            : $"{Expression} (= {Tally.Complete(ids.Count).Render("mod")}: {string.Join(", ", ids.Take(3))}, …)";
    }

    /// <summary>拼一段 SQL 谓词。全选时返回 null,让调用方省掉这个条件。</summary>
    public string? SqlPredicate(string column, IDictionary<string, object?> parameters, string prefix = "@sc")
    {
        if (_all) return null;
        if (_included.Count == 0) return "0";
        var names = new List<string>();
        var i = 0;
        foreach (var id in _included)
        {
            var p = prefix + i++;
            parameters[p] = id;
            names.Add(p);
        }
        return $"{column} IN ({string.Join(", ", names)})";
    }

    public static ScopeFilter Parse(string? expression, IReadOnlyList<string> allPackageIds, RimConfig config)
    {
        expression = string.IsNullOrWhiteSpace(expression) ? DefaultScope : expression.Trim();
        var universe = new HashSet<string>(allPackageIds, StringComparer.OrdinalIgnoreCase);
        var tokens = expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = new List<string>();

        var startsWithExclusion = tokens.Length > 0 && tokens[0].StartsWith('-');
        var set = startsWithExclusion
            ? new HashSet<string>(universe, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawAll = startsWithExclusion;
        // 补集表达式只有在「起点全集、之后只做排除」时才等于这些词的并集。
        // 中途再并进来一个词(vanilla,-core,mods)就不成立了,那时置 null 不发声。
        var excludedWords = new List<string>();
        var addedAfterStart = false;

        foreach (var token in tokens)
        {
            var exclude = token.StartsWith('-');
            var name = exclude ? token[1..].Trim() : token;
            if (name.Length == 0) continue;

            if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (exclude) set.Clear();
                else { set.UnionWith(universe); sawAll = true; }
                continue;
            }

            var resolved = Resolve(name, universe, config);
            if (resolved.Count == 0) { unknown.Add(name); continue; }
            if (exclude) { set.ExceptWith(resolved); excludedWords.Add(name); }
            else { set.UnionWith(resolved); addedAfterStart = true; }
        }

        var isAll = sawAll && set.SetEquals(universe);
        var complement = sawAll && !addedAfterStart && excludedWords.Count > 0
            ? string.Join(",", excludedWords)
            : null;
        return new ScopeFilter(expression, set, isAll, unknown, universe, complement);
    }

    private static readonly string[] VanillaPrefixes = ["ludeon.rimworld"];

    /// <summary>内置组名。与 <see cref="Resolve"/> 共用一份。</summary>
    private static readonly string[] BuiltinGroups = ["vanilla", "base", "core", "official"];

    /// <summary>
    /// 这个词是不是一个 scope **组名**(而不是一个 packageId)。
    ///
    /// 用处只有一个:一份快照恰好也叫这个名字时(<c>--snapshot vanilla</c>)
    /// 要说破两者不是一回事。
    /// </summary>
    public static bool IsGroupName(string? name, RimConfig config)
        => name is { Length: > 0 } &&
           (BuiltinGroups.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            config.ScopeGroups.ContainsKey(name));

    private static HashSet<string> Resolve(string name, HashSet<string> universe, RimConfig config)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (BuiltinGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var id in universe)
                if (VanillaPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    result.Add(id);
            return result;
        }

        if (config.ScopeGroups.TryGetValue(name, out var group))
        {
            foreach (var id in group)
                foreach (var u in universe)
                    if (string.Equals(u, id, StringComparison.OrdinalIgnoreCase)) result.Add(u);
            return result;
        }

        foreach (var id in universe)
            if (string.Equals(id, name, StringComparison.OrdinalIgnoreCase)) result.Add(id);
        if (result.Count > 0) return result;

        // 末段匹配:让 `--scope milira` 命中 `Ludeon.Milira` 这类写法
        foreach (var id in universe)
        {
            var tail = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
            if (string.Equals(tail, name, StringComparison.OrdinalIgnoreCase)) result.Add(id);
        }
        return result;
    }
}
