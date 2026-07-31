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

    public string Expression { get; }
    public IReadOnlyCollection<string> UnknownTokens { get; }

    private ScopeFilter(string expression, HashSet<string> included, bool all, List<string> unknown)
    {
        Expression = expression;
        _included = included;
        _all = all;
        UnknownTokens = unknown;
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
            if (exclude) set.ExceptWith(resolved);
            else set.UnionWith(resolved);
        }

        var isAll = sawAll && set.SetEquals(universe);
        return new ScopeFilter(expression, set, isAll, unknown);
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
