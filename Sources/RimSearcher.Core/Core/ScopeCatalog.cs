using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RimSearcher.Core;

// 一个逻辑源常跨多个根目录（HAR = AlienRace 的 C# + Defs + 1.6/Defs），故 Roots 是列表；
// 同名条目在 csharp / xml 两侧都会归到同一个源。
public sealed class ScopeSource
{
    public ScopeSource(string name, IReadOnlyList<string> roots)
    {
        Name = name;
        Roots = roots;
    }

    public string Name { get; }

    // 已规范化：全路径、统一分隔符、去尾分隔符
    public IReadOnlyList<string> Roots { get; }
}

// 一次查询选中的源集合。Rank 决定同分时谁排前面（= scope 表达式里的书写顺序）。
public sealed class ScopeSelection
{
    private readonly ScopeCatalog _catalog;
    private readonly int[] _rankBySource;

    internal ScopeSelection(ScopeCatalog catalog, int[] rankBySource, string expression, bool includesEverything)
    {
        _catalog = catalog;
        _rankBySource = rankBySource;
        Expression = expression;
        IncludesEverything = includesEverything;
        SelectedCount = rankBySource.Count(rank => rank >= 0);
    }

    public string Expression { get; }

    public bool IncludesEverything { get; }

    public int SelectedCount { get; }

    // 单源时来源标签是纯噪音（每行都一样），只有并选多源才标
    public bool ShowLabels => SelectedCount > 1;

    // 未落在任何已配置源里的文件（理论上不该有，索引本来就只扫这些根）按未选中处理
    public int RankOf(string filePath)
    {
        var sourceIndex = _catalog.ResolveSourceIndex(filePath);
        if (sourceIndex < 0) return IncludesEverything ? int.MaxValue - 1 : -1;
        return _rankBySource[sourceIndex];
    }

    public bool Contains(string filePath) => RankOf(filePath) >= 0;

    public string? SourceNameOf(string filePath)
    {
        var sourceIndex = _catalog.ResolveSourceIndex(filePath);
        return sourceIndex < 0 ? null : _catalog.Sources[sourceIndex].Name;
    }

    // 落在选中集合之外的命中，用于「其他组另有 N 条」提示
    public string OutOfScopeLabel(string filePath) => SourceNameOf(filePath) ?? "unindexed";
}

public sealed class ScopeCatalog
{
    private readonly ConcurrentDictionary<string, int> _sourceIndexByPath = new(PathComparer);
    private readonly Dictionary<string, List<int>> _groups;
    private readonly string? _defaultExpression;

    private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public const string EverythingKeyword = "all";

    private ScopeCatalog(List<ScopeSource> sources, Dictionary<string, List<int>> groups, string? defaultExpression)
    {
        Sources = sources;
        _groups = groups;
        _defaultExpression = defaultExpression;

        var everythingRanks = new int[sources.Count];
        for (int i = 0; i < everythingRanks.Length; i++) everythingRanks[i] = i;
        Everything = new ScopeSelection(this, everythingRanks, EverythingKeyword, includesEverything: true);
    }

    public IReadOnlyList<ScopeSource> Sources { get; }

    public ScopeSelection Everything { get; }

    public IReadOnlyList<string> GroupNames => _groups.Keys.ToList();

    public bool HasSources => Sources.Count > 0;

    // rawSources：同名条目会被合并成一个源，顺序按首次出现。
    // groups：组名 → 源名列表，允许一个源同属多组；引用不存在的源名会被忽略（config 手误不该让服务器起不来）。
    public static ScopeCatalog Build(
        IEnumerable<(string Name, string Path)> rawSources,
        IReadOnlyDictionary<string, List<string>>? groups,
        string? defaultScopeExpression)
    {
        var rootsByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var (name, path) in rawSources)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;

            var normalized = NormalizeRoot(path);
            if (normalized == null) continue;

            if (!rootsByName.TryGetValue(name, out var roots))
            {
                roots = new List<string>();
                rootsByName[name] = roots;
                order.Add(name);
            }

            if (!roots.Contains(normalized, PathComparer)) roots.Add(normalized);
        }

        var sources = order.Select(name => new ScopeSource(name, rootsByName[name])).ToList();

        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sources.Count; i++) indexByName[sources[i].Name] = i;

        var resolvedGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (groups != null)
        {
            foreach (var (groupName, memberNames) in groups)
            {
                if (string.IsNullOrWhiteSpace(groupName) || memberNames == null) continue;
                if (string.Equals(groupName, EverythingKeyword, StringComparison.OrdinalIgnoreCase)) continue;

                var members = new List<int>();
                foreach (var memberName in memberNames)
                {
                    if (memberName != null && indexByName.TryGetValue(memberName, out var idx) && !members.Contains(idx))
                        members.Add(idx);
                }

                if (members.Count > 0) resolvedGroups[groupName] = members;
            }
        }

        return new ScopeCatalog(sources, resolvedGroups, defaultScopeExpression);
    }

    // 表达式语法：逗号分隔的组名/源名，`-` 前缀表示排除，`all` 表示全部源。
    // 空表达式落到 config 的默认组；默认组也没有则全域。
    public ScopeSelection Resolve(string? expression)
    {
        if (!HasSources) return Everything;

        var effective = string.IsNullOrWhiteSpace(expression) ? _defaultExpression : expression;
        if (string.IsNullOrWhiteSpace(effective)) return Everything;

        var ranks = new int[Sources.Count];
        Array.Fill(ranks, -1);

        var nextRank = 0;
        var excluded = new HashSet<int>();
        var matchedAnything = false;

        foreach (var rawToken in effective.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;

            var isExclusion = token[0] is '-' or '!';
            if (isExclusion) token = token[1..].Trim();
            if (token.Length == 0) continue;

            if (!TryExpandToken(token, out var members)) continue;
            matchedAnything = true;

            foreach (var member in members)
            {
                if (isExclusion)
                {
                    excluded.Add(member);
                    ranks[member] = -1;
                }
                else if (ranks[member] < 0 && !excluded.Contains(member))
                {
                    ranks[member] = nextRank++;
                }
            }
        }

        // 表达式整体无法解析（全是拼错的名字）时退回全域，而不是给出一个空集合——
        // 空集合会让调用方收到「没有结果」并误判成「不存在」。
        if (!matchedAnything) return Everything;

        // 只写了排除项（如 "-vanilla"）时，未被排除的全部源即为选中集合
        if (nextRank == 0 && excluded.Count > 0)
        {
            for (int i = 0; i < ranks.Length; i++)
            {
                if (!excluded.Contains(i)) ranks[i] = nextRank++;
            }
        }

        if (nextRank == 0) return Everything;

        // 不能拿 nextRank 当选中数：'all,-vanilla' 里 vanilla 先被计入再被排除，nextRank 会多算，
        // 于是排除了源却仍自称全域——未落在任何源里的路径会被 RankOf 当成命中收进来。
        var selectedCount = ranks.Count(rank => rank >= 0);
        return new ScopeSelection(this, ranks, effective.Trim(), selectedCount == Sources.Count);
    }

    private bool TryExpandToken(string token, out IReadOnlyList<int> members)
    {
        if (string.Equals(token, EverythingKeyword, StringComparison.OrdinalIgnoreCase))
        {
            members = Enumerable.Range(0, Sources.Count).ToList();
            return true;
        }

        if (_groups.TryGetValue(token, out var group))
        {
            members = group;
            return true;
        }

        for (int i = 0; i < Sources.Count; i++)
        {
            if (string.Equals(Sources[i].Name, token, StringComparison.OrdinalIgnoreCase))
            {
                members = new[] { i };
                return true;
            }
        }

        members = Array.Empty<int>();
        return false;
    }

    // 最长根前缀胜出：嵌套配置（`<mod>/Defs` 与 `<mod>/1.6/Defs` 同时在册）时才归对源。
    // 结果按文件路径缓存——索引里的路径是 interned 的，同一路径会被反复问到。
    public int ResolveSourceIndex(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return -1;

        return _sourceIndexByPath.GetOrAdd(filePath, path =>
        {
            var normalized = NormalizeRoot(path);
            if (normalized == null) return -1;

            var bestIndex = -1;
            var bestLength = -1;

            for (int i = 0; i < Sources.Count; i++)
            {
                foreach (var root in Sources[i].Roots)
                {
                    if (root.Length <= bestLength) continue;
                    if (!IsUnderRoot(normalized, root)) continue;

                    bestIndex = i;
                    bestLength = root.Length;
                }
            }

            return bestIndex;
        });
    }

    private static bool IsUnderRoot(string normalizedPath, string root)
    {
        if (!normalizedPath.StartsWith(root, PathComparison)) return false;
        if (normalizedPath.Length == root.Length) return true;

        var separator = normalizedPath[root.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static string? NormalizeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return full;
        }
        catch
        {
            return null;
        }
    }

    // 供工具的参数说明与「未知 scope」提示复用
    public string DescribeAvailable()
    {
        var parts = new List<string>();
        if (_groups.Count > 0) parts.Add($"groups: {string.Join(", ", _groups.Keys)}");
        if (Sources.Count > 0) parts.Add($"sources: {string.Join(", ", Sources.Select(s => s.Name))}");
        parts.Add($"'{EverythingKeyword}' selects everything; prefix '-' excludes (e.g. 'all,-vanilla')");
        if (!string.IsNullOrWhiteSpace(_defaultExpression)) parts.Add($"default: {_defaultExpression}");
        return string.Join(". ", parts);
    }
}
