using RimSearcher.Config;
using RimSearcher.Output;

namespace RimSearcher.Snapshot;

public sealed class ScopeFilter
{
    public const string DefaultScope = "all";

    private readonly HashSet<string> _included;
    private readonly bool _all;
    private readonly HashSet<string> _universe;
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

    public string Describe()
    {
        if (_all) return Expression;
        if (_included.Count == 0) return $"{Expression}（本快照中没有匹配的 mod）";

        var ids = _included.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 1)
            return string.Equals(ids[0], Expression, StringComparison.OrdinalIgnoreCase)
                ? Expression
                : $"{Expression}（= {ids[0]}）";

        return ids.Count <= 4
            ? $"{Expression}（= {string.Join("、", ids)}）"
            : $"{Expression}（= {Tally.Complete(ids.Count).Render("mod")}：{string.Join("、", ids.Take(3))}…）";
    }

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

    private static readonly string[] BuiltinGroups = ["vanilla", "base", "core", "official"];

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

        foreach (var id in universe)
        {
            var tail = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
            if (string.Equals(tail, name, StringComparison.OrdinalIgnoreCase)) result.Add(id);
        }
        return result;
    }
}
