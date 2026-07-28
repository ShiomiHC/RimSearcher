using System.Text.RegularExpressions;

namespace RimSearcher.Core;

public static class FuzzyMatcher
{
    private static readonly Regex WordSplitRegex = new(@"[_\.\-\s]+", RegexOptions.Compiled);

    public static double CalculateFuzzyScore(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return 0.0;

        string textLower = text.ToLowerInvariant();
        string queryLower = query.ToLowerInvariant();

        if (textLower == queryLower) return 100.0;
        // 必须显式写 Ordinal。`StartsWith(string)` 的默认重载是 **CurrentCulture**，而 ICU 的
        // 语言学比较会**整体忽略 default-ignorable 码点**（U+00AD 软连字符、U+200B/U+200D 零宽、
        // C0 控制符、变体选择符……）。于是：
        //   - `"<U+00AD>abcdefgh".StartsWith("abcd")` 为真 → 得 90 分，而两串的序数前缀关系不成立、
        //     编辑距离是 5，其余每一支也都不成立；
        //   - 更狠的一头：query 整串都是可忽略字符时它 collate 成空串，于是**任意** text 都
        //     「以它开头」——`CalculateFuzzyScore("pawn_needs_joy", "<U+00AD><U+00AD>")` 给 90 分。
        //     一次查询把整个索引当成满分命中，第 11 行的非空判断拦不住。
        // 三路独立验算（逐支枚举 / 代数 / 对抗性穷举，各自带反驳者）都只找到这一个破口：
        // 干净字母表上穷举五千多万对零违例，一旦把可忽略字符加进字母表就成批出现，且**全部**
        // 落在这一行的 90 分上。
        //
        // 这不是纸面问题：SourceIndexer.QualifiedMemberKeys 把「够 60 分」翻译成四个区间查询，
        // 其中「相等/前缀」那一支查的是 OrdinalIgnoreCase 排序数组——两边不是同一个谓词，
        // 精确枚举就会漏掉这一类键。改成 Ordinal 后两侧逐字同义（这里两串都已 ToLowerInvariant）。
        if (textLower.StartsWith(queryLower, StringComparison.Ordinal)) return 90.0;

        int editDistance = LevenshteinDistance(textLower, queryLower);
        int queryLength = query.Length;
        int textLength = text.Length;

        if (editDistance <= 2)
        {
            double tolerance = queryLength <= 4 ? 0.5 : 0.3;
            if (editDistance <= queryLength * tolerance)
            {
                double typoScore = 95.0 - (editDistance * 5.0);
                if (Math.Abs(textLength - queryLength) <= 1) typoScore += 3.0;
                return Math.Min(typoScore, 95.0);
            }
        }

        if (IsCamelCaseMatch(text, query))
            return queryLength <= 5 ? 85.0 : 75.0;

        if (IsWordBoundaryMatch(text, query))
            return 80.0;

        int maxLength = Math.Max(textLength, queryLength);
        double similarity = 1.0 - (double)editDistance / maxLength;

        if (editDistance <= 3 && similarity >= 0.75) return 70.0 * similarity;
        if (similarity >= 0.6) return 55.0 * similarity;

        // 同上：`IndexOf(string)` 的默认重载也是 CurrentCulture，同一批可忽略字符在这里会让
        // 「找得到子串」这件事同样失真。这一支被夹在 [30, 50]、够不到 60 分线，故它不影响
        // QualifiedMemberKeys 的完备性——但一个文件里两处同类比较只改一处，下一个人只会以为
        // 另一处是有意为之。
        int substringIndex = textLower.IndexOf(queryLower, StringComparison.Ordinal);
        if (substringIndex >= 0)
        {
            double positionScore = 50.0 - (substringIndex * 2.0 / textLength * 10.0);
            double lengthRatio = (double)queryLength / textLength;
            positionScore += lengthRatio * 10.0;
            return Math.Max(30.0, Math.Min(positionScore, 50.0));
        }

        return 0.0;
    }

    private static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return source.Length;

        int sourceLength = source.Length;
        int targetLength = target.Length;

        var previousRow = new int[targetLength + 1];
        var currentRow = new int[targetLength + 1];

        for (int j = 0; j <= targetLength; j++) previousRow[j] = j;

        for (int i = 1; i <= sourceLength; i++)
        {
            currentRow[0] = i;
            for (int j = 1; j <= targetLength; j++)
            {
                int cost = (source[i - 1] == target[j - 1]) ? 0 : 1;
                currentRow[j] = Math.Min(Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1), previousRow[j - 1] + cost);
            }
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLength];
    }

    private static bool IsWordBoundaryMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
        return SplitIntoWords(text).Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCamelCaseMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
        var initials = ExtractCamelCaseInitials(text);
        return initials.Equals(query, StringComparison.OrdinalIgnoreCase) || initials.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    // 公开是为了让索引层能把 initials → key 建成一张表：`IsCamelCaseMatch` 的判据整个就是
    // 「initials 以 query 开头」，也就是一次前缀区间查询。判据留在这里、表建在那边，
    // 两处才不会各自长出一套对「首字母」的理解。
    public static string ExtractCamelCaseInitials(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return new string(SplitIntoWords(text).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0])).ToArray());
    }

    // 「编辑距离是否 <= max」。索引层用它做长度桶的预筛：真正要的是分数，而分数只在
    // 编辑距离足够小时才可能过线，先用这个便宜的判定挡掉绝大多数候选。
    //
    // 只算 |i-j| <= max 的那条带：带外的格子恒 > max，算出来也用不上。于是复杂度从
    // O(n×m) 降到 O(n×(2max+1))，而 max 是常数。整行都超过 max 时立刻收工——
    // 编辑距离沿行单调不减，后面的行只会更大。
    //
    // 与 CalculateFuzzyScore 里那份 LevenshteinDistance 同一套代价模型（增删改各 1），
    // 故「这里说 <= max」与「那里算出来 <= max」恒等价。它不参与打分，只决定要不要打分。
    public static bool EditDistanceAtMost(string source, string target, int max)
    {
        if (max < 0) return false;
        if (string.IsNullOrEmpty(source)) return (target?.Length ?? 0) <= max;
        if (string.IsNullOrEmpty(target)) return source.Length <= max;
        if (Math.Abs(source.Length - target.Length) > max) return false;

        var targetLength = target.Length;
        var previousRow = new int[targetLength + 1];
        var currentRow = new int[targetLength + 1];

        for (int j = 0; j <= targetLength; j++) previousRow[j] = j;

        for (int i = 1; i <= source.Length; i++)
        {
            var from = Math.Max(1, i - max);
            var to = Math.Min(targetLength, i + max);

            currentRow[0] = i;
            // 带外的格子参与 min 运算，故必须填成一个「大到不会被选中」的值而不是留着上一轮的残值
            if (from > 1) currentRow[from - 1] = max + 1;

            // 带外一律当成 max+1：j=0 只有在它落进带内（from==1）时才是真值
            var rowMin = from == 1 ? currentRow[0] : max + 1;
            for (int j = from; j <= to; j++)
            {
                int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                var deletion = previousRow[j] > max ? max + 1 : previousRow[j] + 1;
                currentRow[j] = Math.Min(Math.Min(currentRow[j - 1] + 1, deletion), previousRow[j - 1] + cost);
                if (currentRow[j] < rowMin) rowMin = currentRow[j];
            }

            if (to < targetLength) currentRow[to + 1] = max + 1;
            if (rowMin > max) return false;

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLength] <= max;
    }

    public static List<string> SplitIntoWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        var parts = WordSplitRegex.Split(text);
        var result = new List<string>();

        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part)) result.AddRange(SplitCamelCase(part));
        }
        return result;
    }

    private static List<string> SplitCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        var result = new List<string>();
        int wordStart = 0;

        for (int i = 1; i < text.Length; i++)
        {
            if ((char.IsUpper(text[i]) && char.IsLower(text[i - 1])) ||
                (i < text.Length - 1 && char.IsUpper(text[i]) && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1])))
            {
                result.Add(text.Substring(wordStart, i - wordStart));
                wordStart = i;
            }
        }

        if (wordStart < text.Length) result.Add(text.Substring(wordStart));
        return result.Where(w => !string.IsNullOrEmpty(w)).ToList();
    }

    public static List<string> GenerateNgrams(string text, int n, int maxCount = 50)
    {
        if (string.IsNullOrEmpty(text) || text.Length < n) return new List<string>();
        var ngrams = new List<string>();
        var lowerText = text.ToLowerInvariant();
        int limit = maxCount > 0 ? Math.Min(lowerText.Length - n + 1, maxCount) : lowerText.Length - n + 1;

        for (int i = 0; i < limit; i++) ngrams.Add(lowerText.Substring(i, n));
        return ngrams;
    }
}
