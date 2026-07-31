using System.Text.RegularExpressions;

namespace RimSearcher.Search;

/// <summary>
/// 模糊打分器。候选集是快照内的 def 名(万级),直接全量打分,不做预筛。
///
/// 三把刀的分工:FTS 管自然语言,正则管代码内容,这里管**标识符** —— 打错字
/// (CompTikRare)、驼峰缩写、文件名式查询在 FTS 上一律零命中。
/// </summary>
public static class FuzzyMatcher
{
    private static readonly Regex WordSplitRegex = new(@"[_\.\-\s]+", RegexOptions.Compiled);

    /// <summary>候选进入结果集的分数线。</summary>
    public const double Threshold = 60.0;

    public static double Score(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return 0.0;

        var textLower = text.ToLowerInvariant();
        var queryLower = query.ToLowerInvariant();

        if (textLower == queryLower) return 100.0;

        // 必须显式写 Ordinal:StartsWith(string) 默认是 CurrentCulture,而 ICU 会整体忽略
        // default-ignorable 码点(U+00AD、U+200B/U+200D、C0 控制符、变体选择符……),
        // 于是全由可忽略字符组成的 query collate 成空串,任意 text 都「以它开头」。
        if (textLower.StartsWith(queryLower, StringComparison.Ordinal)) return 90.0;

        var editDistance = LevenshteinDistance(textLower, queryLower);
        var queryLength = query.Length;
        var textLength = text.Length;

        if (editDistance <= 2)
        {
            var tolerance = queryLength <= 4 ? 0.5 : 0.3;
            if (editDistance <= queryLength * tolerance)
            {
                var typoScore = 95.0 - editDistance * 5.0;
                if (Math.Abs(textLength - queryLength) <= 1) typoScore += 3.0;
                return Math.Min(typoScore, 95.0);
            }
        }

        if (IsCamelCaseMatch(text, query)) return queryLength <= 5 ? 85.0 : 75.0;
        if (IsWordBoundaryMatch(text, query)) return 80.0;

        var maxLength = Math.Max(textLength, queryLength);
        var similarity = 1.0 - (double)editDistance / maxLength;

        if (editDistance <= 3 && similarity >= 0.75) return 70.0 * similarity;
        if (similarity >= 0.6) return 55.0 * similarity;

        // 同上:IndexOf(string) 的默认重载也是 CurrentCulture。
        var substringIndex = textLower.IndexOf(queryLower, StringComparison.Ordinal);
        if (substringIndex >= 0)
        {
            var positionScore = 50.0 - substringIndex * 2.0 / textLength * 10.0;
            positionScore += (double)queryLength / textLength * 10.0;
            return Math.Max(30.0, Math.Min(positionScore, 50.0));
        }

        return 0.0;
    }

    public static IEnumerable<(string Text, double Score)> Rank(IEnumerable<string> candidates, string query, double threshold = Threshold)
        => candidates.Select(c => (c, Score(c, query)))
                     .Where(t => t.Item2 >= threshold)
                     .OrderByDescending(t => t.Item2)
                     .ThenBy(t => t.Item1.Length)
                     .ThenBy(t => t.Item1, StringComparer.Ordinal);

    /// <summary>
    /// <c>method:CompTick</c> / <c>field:id</c> 这类 kind 前缀。
    /// 返回剥掉前缀后的查询与 kind;不带前缀时 kind 为 null。
    /// </summary>
    public static (string Query, string? Kind) StripKindPrefix(string query)
    {
        var colon = query.IndexOf(':');
        if (colon <= 0) return (query, null);
        var kind = query[..colon].ToLowerInvariant();
        return kind is "method" or "field" or "type" or "class" or "def" or "property" or "member"
            ? (query[(colon + 1)..].Trim(), kind)
            : (query, null);
    }

    private static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        var previousRow = new int[target.Length + 1];
        var currentRow = new int[target.Length + 1];
        for (var j = 0; j <= target.Length; j++) previousRow[j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1), previousRow[j - 1] + cost);
            }
            (previousRow, currentRow) = (currentRow, previousRow);
        }
        return previousRow[target.Length];
    }

    private static bool IsWordBoundaryMatch(string text, string query)
        => SplitIntoWords(text).Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase));

    private static bool IsCamelCaseMatch(string text, string query)
    {
        var initials = ExtractCamelCaseInitials(text);
        return initials.Equals(query, StringComparison.OrdinalIgnoreCase)
            || initials.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractCamelCaseInitials(string text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : new string(SplitIntoWords(text).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0])).ToArray());

    public static List<string> SplitIntoWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var result = new List<string>();
        foreach (var part in WordSplitRegex.Split(text))
            if (!string.IsNullOrEmpty(part))
                result.AddRange(SplitCamelCase(part));
        return result;
    }

    private static List<string> SplitCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var result = new List<string>();
        var wordStart = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if ((char.IsUpper(text[i]) && char.IsLower(text[i - 1])) ||
                (i < text.Length - 1 && char.IsUpper(text[i]) && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1])))
            {
                result.Add(text[wordStart..i]);
                wordStart = i;
            }
        }
        if (wordStart < text.Length) result.Add(text[wordStart..]);
        return result.Where(w => w.Length > 0).ToList();
    }
}
