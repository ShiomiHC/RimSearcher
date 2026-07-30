using RimSearcher.Config;

namespace RimSearcher.Snapshot;

/// <summary>
/// scope = **快照内按 mod 维度筛结果**。语法从 master 带走(01;上游只有 <c>--mod</c> 单值),
/// 实现新写。
///
/// 同一语法符号不背两种语义(06):<c>--scope</c> 只管过滤,**不选快照**。选哪次导出是
/// <c>--snapshot</c> / <c>snapshot use</c> / 自动检测那三层的事。
///
/// 语法:逗号分隔;<c>-x</c> 表示排除;组名在 config.toml 的 [scope_groups] 里定义。
/// 首个词是排除时,起点集合为全部(<c>all,-vanilla</c> 与 <c>-vanilla</c> 同义 —— 07 实证
/// 排除语法有真实使用记录)。
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
    /// 三轮 R10 的一词两义:<c>--scope vanilla|base|core|official</c> 展开成**每个**
    /// <c>ludeon.rimworld*</c> 模块,也就是 Core 加全部已装 DLC;而一份**叫** vanilla 的
    /// 快照可能只有 Core。两份文档都没写这件事,而两者在句子里长得一模一样。
    /// 展开是当场算得出来的,那就算出来 —— 让读者去记一份对照表,等于把工具的活推给他。
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
            : $"{Expression} (= {ids.Count} mods: {string.Join(", ", ids.Take(3))}, …)";
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

    private static HashSet<string> Resolve(string name, HashSet<string> universe, RimConfig config)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (name is "vanilla" or "base" or "core" or "official")
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
