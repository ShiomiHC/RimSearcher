namespace RimSearcher.Core;

// 打分候选。Path 用于判定所属源；Score 参与排序与断层收口。
public readonly struct ScoredCandidate<T>
{
    public ScoredCandidate(T item, double score, string path)
    {
        Item = item;
        Score = score;
        Path = path;
    }

    public T Item { get; }
    public double Score { get; }
    public string Path { get; }
}

public sealed class ScopedEntry<T>
{
    public ScopedEntry(T item, double score, string? sourceName)
    {
        Item = item;
        Score = score;
        SourceName = sourceName;
    }

    public T Item { get; }
    public double Score { get; }
    public string? SourceName { get; }
}

public sealed class ScopedResult<T>
{
    public static readonly ScopedResult<T> Empty = new(new List<ScopedEntry<T>>(), 0, new List<(string, int)>(), false);

    public ScopedResult(
        List<ScopedEntry<T>> items,
        int totalInScope,
        List<(string Source, int Count)> outOfScope,
        bool truncatedByScoreGap,
        bool truncatedByLimit = false,
        bool totalIsLowerBound = false)
    {
        Items = items;
        TotalInScope = totalInScope;
        OutOfScope = outOfScope;
        TruncatedByScoreGap = truncatedByScoreGap;
        TruncatedByLimit = truncatedByLimit;
        TotalIsLowerBound = totalIsLowerBound;
    }

    public List<ScopedEntry<T>> Items { get; }

    // 截断前的真实命中数——折叠行的「+N more」必须用它，否则计数会被内部上限骗掉
    public int TotalInScope { get; }

    public List<(string Source, int Count)> OutOfScope { get; }

    public bool TruncatedByScoreGap { get; }

    // limit 是否真的砍掉了东西。断层收口砍掉的那部分调多大的 limit 都拿不回来（见 ScopeFilter），
    // 两者不分开的话折叠行只能笼统地劝「pass limit:'all'」——照做了却一条也多不出来。
    public bool TruncatedByLimit { get; }

    // TotalInScope 只是下界——检索层在数出这个总数之前就有候选被内部上限截掉了。
    // 与 TruncatedByLimit / TruncatedByScoreGap 是不同的两件事：那两个说的是「这个总数里
    // 有多少没列出来」（总数本身是准的），这个说的是**总数自己不准**，展示层要据此改口
    // （`N of M` → `N of at least M`），否则调用方会把一个下界当成穷尽结论。
    public bool TotalIsLowerBound { get; }

    public int HiddenCount => Math.Max(0, TotalInScope - Items.Count);

    public int OutOfScopeTotal => OutOfScope.Sum(x => x.Count);
}

// 子类树在 scope 内的形状：整棵树里有几个直接子类、最深几层。
// 与 ScopedResult.Items 是两个量——后者是被 limit 截断后的展示切片。表头同时要印这两组数，
// 而它们**必须各自说明自己数的是哪一批**，否则「200 direct, deepest 1 level down」会被读成
// 对整棵 381 条的树的描述（见 SourceIndexer.GetInheritors 里的注释）。
public readonly record struct InheritorTreeShape(int Direct, int Deepest);

public static class ScopeFilter
{
    // 相对首条掉这么多分即视为断层：低于它的多是纯子串噪音（子串匹配封顶 50 分）
    public const double DefaultScoreGap = 40.0;

    // 只有存在足够强的命中时才允许断层收口；否则「全是弱匹配」的查询会被砍到只剩一条
    private const double ScoreGapMinTopScore = 70.0;

    // 候选序列须已按调用方自己的次要规则排好（如名字长度）——这里的排序是稳定的，
    // 故 Score 降序、同分 Rank 升序之后，调用方的次序仍作为第三级保留。
    // scoreGap 传 null 关闭断层收口（用于按命中计数排序、分值不可比的场景）。
    // totalIsLowerBound 由调用方传：候选是不是已经被它自己的内部上限截过，这里无从判断。
    public static ScopedResult<T> Apply<T>(
        IEnumerable<ScoredCandidate<T>> candidates,
        ScopeSelection scope,
        int limit,
        double? scoreGap = DefaultScoreGap,
        bool totalIsLowerBound = false)
    {
        var inScope = new List<(ScoredCandidate<T> Candidate, int Rank)>();
        Dictionary<string, int>? outOfScope = null;

        foreach (var candidate in candidates)
        {
            var rank = scope.RankOf(candidate.Path);
            if (rank >= 0)
            {
                inScope.Add((candidate, rank));
            }
            else
            {
                outOfScope ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var label = scope.OutOfScopeLabel(candidate.Path);
                outOfScope[label] = outOfScope.GetValueOrDefault(label) + 1;
            }
        }

        var ordered = inScope
            .OrderByDescending(x => x.Candidate.Score)
            .ThenBy(x => x.Rank)
            .ToList();

        var cutoff = ordered.Count;
        var truncatedByScoreGap = false;

        if (scoreGap.HasValue && ordered.Count > 1 && ordered[0].Candidate.Score >= ScoreGapMinTopScore)
        {
            var floor = ordered[0].Candidate.Score - scoreGap.Value;
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Candidate.Score < floor)
                {
                    cutoff = i;
                    truncatedByScoreGap = true;
                    break;
                }
            }
        }

        var effectiveLimit = limit <= 0 ? cutoff : Math.Min(limit, cutoff);

        var items = ordered
            .Take(effectiveLimit)
            .Select(x => new ScopedEntry<T>(
                x.Candidate.Item,
                x.Candidate.Score,
                scope.ShowLabels ? scope.SourceNameOf(x.Candidate.Path) : null))
            .ToList();

        var outOfScopeList = outOfScope == null
            ? new List<(string, int)>()
            : outOfScope
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

        return new ScopedResult<T>(
            items, ordered.Count, outOfScopeList, truncatedByScoreGap,
            truncatedByLimit: items.Count < cutoff, totalIsLowerBound: totalIsLowerBound);
    }
}
