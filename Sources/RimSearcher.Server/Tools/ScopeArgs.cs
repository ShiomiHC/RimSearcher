using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// 解析好的条数上限。
// Count 永远是 [1, HardLimit] 里的具体数字：不再用 0 当「无限」哨兵值交给各工具自己翻译，
// 那种写法正是 TraceTool 里 `limit == 0 ? 50 : Math.Max(limit, 50)` 的来源——0 被翻译成 50，
// 而显式的 limit:5 又被 Math.Max 抬到 50，两个方向都违背调用方意图。
// Unlimited 只用来决定折叠行怎么说话：已经要过 'all' 的调用方，不该再被劝一次 'all'。
public readonly record struct ResultLimit(int Count, bool Unlimited)
{
    // 分组配额之类需要放大 limit 的场景；放大后仍不得越过硬上限
    public ResultLimit Scale(int factor)
    {
        var scaled = (long)Count * Math.Max(1, factor);
        return new ResultLimit((int)Math.Clamp(scaled, 1, ScopeArgs.HardLimit), Unlimited);
    }
}

// scope / limit 两个参数在六个工具上语义一致，解析与呈现都集中在这里，
// 免得各工具各写一遍别名吸收与折叠行文案。
public static class ScopeArgs
{
    // limit 的三段语义，所有工具共用：
    //   缺省      → 调用方传进来的 fallback（列表型工具 10，扫盘型工具 50~100）；
    //               fallback 给 HardLimit 即表示「缺省就展开到硬上限」。
    //   显式数字  → 原样尊重，只在越过硬上限时夹住。不得被任何下限抬高。
    //   'all'/'*' → 展开到硬上限，而不是某个魔数（trace 原先固定 50，search_regex 原先 500）。
    // JSON schema 里的 maximum 只是给 client 的提示、不是约束——client 照样能传 100000，
    // 所以真正的夹紧必须发生在服务端，就在这一处。
    public const int DefaultDisplayLimit = 10;

    // 服务端硬上限。取 200 的理由是响应体积与上下文预算：结果一条一行，预览行按 100 字符
    // 截断，200 行 ≈ 20 KB ≈ 5–6k token，已经是单次工具响应该占的天花板（search_regex
    // 自己的 50 文件 × 3 条预览 = 150 行也落在这条线以内）。再往上调用方读不完，
    // 只会把上下文里更有用的东西挤出去。
    public const int HardLimit = 200;

    public static ScopeSelection Resolve(ScopeCatalog catalog, JsonElement args)
    {
        var expression = ToolArgs.GetOptionalString(args, "scope", "scopes", "source", "sources", "mod", "mods", "in");
        return catalog.Resolve(expression);
    }

    public static ResultLimit GetDisplayLimit(JsonElement args, int fallback = DefaultDisplayLimit)
    {
        if (!ToolArgs.TryGetElement(args, out var value, "limit", "maxResults", "max", "count", "top"))
            return fallback >= HardLimit || fallback <= 0 ? Unlimited : new ResultLimit(fallback, false);

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString()?.Trim().ToLowerInvariant();
            if (raw is "all" or "full" or "*" or "everything") return Unlimited;
        }

        // 解释不了的 limit 必须报错，不能退回默认值。
        //
        // 与拼错的 scope 不对称，是因为两者退回的方向相反：scope 退回全域给出的是**超集**，
        // 调用方少不了东西，一行提示足以；而 limit 退回默认给出的是**子集**——调用方要 100 条、
        // 拿到 10 条、且它自己没写过 10 这个数。这种「静默给少」在只读工具返回文本的调用方那里
        // 会直接沉淀成「一共就这么多」。
        if (!TryCoerceLimit(value, out var parsed))
        {
            throw new ToolArgumentException(
                $"Parameter 'limit' must be a number or one of 'all' / 'full' / '*' / 'everything'; "
                + $"received {DescribeLimitValue(value)}. Pass a number for a cap, or 'all' to expand up to "
                + $"the server cap of {HardLimit}.");
        }

        // 0 与负数在旧协议里就是「别截断」，沿用；其余原样尊重，只夹硬上限
        if (parsed <= 0) return Unlimited;
        return parsed >= HardLimit ? Unlimited : new ResultLimit(parsed, false);
    }

    private static bool TryCoerceLimit(JsonElement value, out int parsed)
    {
        parsed = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out parsed)) return true;
                if (!value.TryGetDouble(out var asDouble)) return false;
                parsed = (int)Math.Clamp(asDouble, int.MinValue, int.MaxValue);
                return true;

            case JsonValueKind.String:
                var raw = value.GetString()?.Trim();
                if (int.TryParse(raw, out parsed)) return true;
                if (double.TryParse(raw, out var fromString))
                {
                    parsed = (int)Math.Clamp(fromString, int.MinValue, int.MaxValue);
                    return true;
                }
                return false;

            // 标量位收到单元素数组是客户端序列化的常见抖动，跟着 ToolArgs 的口径认它
            case JsonValueKind.Array:
                return value.GetArrayLength() == 1 && TryCoerceLimit(value[0], out parsed);

            default:
                return false;
        }
    }

    private static string DescribeLimitValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string '{ToolArgs.ForEcho(value.GetString() ?? string.Empty, 40)}'",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.ValueKind.ToString().ToLowerInvariant()}",
        JsonValueKind.Array => $"an array of {OutputText.Quantity(value.GetArrayLength(), "items")}",
        JsonValueKind.Object => "an object",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };

    private static ResultLimit Unlimited => new(HardLimit, true);

    public static object ScopeSchemaProperty(ScopeCatalog catalog) => new
    {
        type = "string",
        description = $"Optional search scope. {catalog.DescribeAvailable()}"
    };

    // 类型必须同时允许数字：描述让调用方「pass a number」，而 schema 只写 string 时，
    // 按 schema 严格校验的 client 会在发出请求之前就把 limit:10 拒掉。
    // fuzzy: 结果分段呈现且会按相关度折叠（locate / trace inheritors）；
    // 非 fuzzy 的 search_regex 两者都没有，照抄那段文案等于告诉调用方存在一批「调多大 limit
    // 都拿不回来」的结果，而它其实只要 'all' 就能拿全。
    public static object LimitSchemaProperty(int defaultLimit = DefaultDisplayLimit, bool fuzzy = true) => new
    {
        type = new[] { "integer", "string" },
        description =
            (fuzzy ? $"Optional result cap per section (default {defaultLimit}). " : $"Optional result cap (default {defaultLimit}). ")
            + $"Pass a number, or 'all' to expand up to the server cap of {HardLimit}; larger numbers, 0 and "
            + "negatives are all clamped to that cap. Anything else — 'many', true, an object — is rejected "
            + "rather than silently replaced by the default."
            // 断层收口只作用于**真正模糊的那一批**。无条件写「fuzzy sections also fold away…」时，
            // method:/def: 这类精确名查询也被扣上「可能还有你永远拿不到的结果」——那是个不可证伪
            // 的疑虑：返回里没有任何一处能判断它有没有发生。实测精确名过滤走的是全等匹配，
            // 分数恒为 100，断层收口对它不可能触发。
            + (fuzzy
                ? " Score-gap folding drops results far below the top score and no limit brings them back; it "
                  + "only applies to fuzzy matching, so exact-name filters (method:, field:) are never folded that way."
                : string.Empty)
    };

    // 拼错的 scope 会被 ScopeCatalog 静默退回全域（空集合会更糟，见那里的注释）。
    // 退回本身没问题，无声才是问题：调用方拿着全域结果，会以为自己限定过范围。
    public static string? UnresolvedNotice(ScopeCatalog catalog, ScopeSelection scope)
    {
        if (scope.UnresolvedTokens.Count == 0) return null;

        var names = string.Join(", ", scope.UnresolvedTokens.Select(t => $"'{t}'"));
        var fellBack = scope.IncludesEverything && scope.UnresolvedTokens.Count > 0;

        return $"\n_Scope {names} matched no configured group or source and was ignored"
             + (fellBack ? $" — searched everything instead" : $"; searched '{scope.Expression}'")
             + $". Available — {catalog.DescribeAvailable()}._";
    }

    // 零命中 + 窄 scope 是「搜不到」被读成「不存在」的高发点：那一刻返回里通常连一条
    // out-of-scope 计数都没有（扫盘类工具本就不统计，模糊搜索也可能真的一条落选都没有），
    // 于是全篇没有任何痕迹提示还有别的地方没找过。默认 scope 来自 config，调用方多半
    // 根本不知道自己被限定在了哪几个源里。
    // hasOutOfScopeFooter：ScopeReport 的脚注已经把这件事说得更全（它点明限制、逐源给出
    // 落选命中数、并给同一条出路）。两句并排时同一个 scope 表达式在两行里出现三次、
    // 同一个「改用 scope:'all'」被两套措辞各说一遍，读者以为是两条不同的提示。
    public static string? RetryWiderNotice(ScopeSelection scope, bool hasOutOfScopeFooter = false)
        => scope.IncludesEverything || hasOutOfScopeFooter
            ? null
            : $" Only sources in scope '{scope.Expression}' were searched — "
              + $"retry with scope:'{ScopeCatalog.EverythingKeyword}' before concluding it does not exist.";

    // 有结果时的对应件：说清「这里为什么没有 out-of-scope 那一行」。
    //
    // 同一批工具里 locate 与 trace inheritors 会逐源报出 scope 外还有多少命中，而两个扫盘类
    // 工具（trace usages / search_regex）不报——它们是硬 scope 过滤，落选文件根本没被打开，
    // 要统计就得再读一遍，代价与全域搜索相同（见 TraceTool 扫盘处的注释）。问题在于返回里
    // 同样写着 `in scope 'X'`，于是「没有那一行」会被读成「scope 外没有」。盲测里这一条被
    // 单列为「最容易造成静默漏检的缺口」：缺席不等于没有，而缺席本身不留痕迹。
    //
    // 全域时不印——那时本来就没有「外面」。
    public static string? HardScopeFilterNotice(ScopeSelection scope)
        => scope.IncludesEverything
            ? null
            // 括号里那半句原先是「the absence of such a line is not evidence of absence」——双重否定
            // 套 absence，而它要说的事第一句已经正面说过了（"cannot tell you whether there are matches
            // there"）。同一件事说两遍，第二遍还更难读。收成一句陈述。
            : $"\n\n_Files outside scope '{scope.Expression}' were never opened, so this tool cannot tell you "
              + $"whether there are matches there; pass scope:'{ScopeCatalog.EverythingKeyword}' to include them. "
              + "(locate and trace inheritors do count out-of-scope hits; this tool never prints such a line.)_";

    // 同一份返回里出现重名文件时，基名不再是一个能定位的标识。实测 search_regex 一次返回里
    // `RangedIndustrial.xml` / `Buildings_Security_Turrets.xml` / `Items_Resource_Manufactured.xml`
    // 各出现两次（行号不单调，是不同目录下的两份），而两处都叫调用方 `use read_code on a file`
    // ——按名去读必然只命中其中一份，另一份的命中就此消失；把两组行号合起来读则会数出一个
    // 根本不存在的文件。
    //
    // 判据与 R1/R8/R20 同源（推得出来就不印）：基名在本次返回里唯一就只印基名，重名时补上
    // **刚好能把它们分开**的那几级目录，不是无条件印全路径。
    public static IReadOnlyDictionary<string, string> DisambiguateFileNames(IEnumerable<string> paths)
    {
        var all = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sameName in all.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var group = sameName.ToList();
            if (group.Count == 1)
            {
                result[group[0]] = sameName.Key;
                continue;
            }

            // 逐级向上加目录，直到组内互不相同。加到 4 级还分不开就给全路径——那时再省
            // 已经不是省 token，是省掉了唯一能定位的信息。
            for (int depth = 1; depth <= 4; depth++)
            {
                var candidates = group.ToDictionary(p => p, p => TailSegments(p, depth + 1));
                if (candidates.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == group.Count)
                {
                    foreach (var (path, tail) in candidates) result[path] = tail;
                    break;
                }

                if (depth == 4) foreach (var path in group) result[path] = path;
            }
        }

        return result;
    }

    private static string TailSegments(string path, int count)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Join("/", segments.Skip(Math.Max(0, segments.Length - count)));
    }

    public static string Label(ScopedEntry<object> entry) => Label(entry.SourceName);

    public static string Label(string? sourceName)
        => string.IsNullOrEmpty(sourceName) ? string.Empty : $" [{sourceName}]";

    // 一批结果行的来源标签该印在哪儿。ScopeCatalog.ShowLabels 只回答「scope 选中了几个源」；
    // scope 是 'all' 而结果恰好全落在一个源里时，每行仍挂着同一个 ` [vanilla]`——实测
    // locate 一次 200 条的返回里 412 个标签约 4120 字，占正文 14%。ScopeCatalog 自己的注释
    // 早写着「单源时来源标签是纯噪音（每行都一样）」，这里把那条判据从 scope 挪到**实际列出
    // 的行**上：同源就提到表头印一次，混源才逐行印。标签是移位，不是删除。
    public readonly struct SourceLabeling
    {
        private readonly bool _perRow;
        private readonly string? _common;

        private SourceLabeling(bool perRow, string? common)
        {
            _perRow = perRow;
            _common = common;
        }

        public static SourceLabeling Of(IEnumerable<string?> rowSources)
        {
            string? common = null;
            var seen = false;

            foreach (var name in rowSources)
            {
                // 有一行说不出来源，说明 scope 已经把源钉死了（ShowLabels=false 时 SourceName
                // 恒为 null），这批本来就一个标签都不该印。
                if (string.IsNullOrEmpty(name)) return new SourceLabeling(false, null);

                if (!seen) { common = name; seen = true; }
                else if (!string.Equals(common, name, StringComparison.OrdinalIgnoreCase))
                    return new SourceLabeling(true, null);
            }

            return new SourceLabeling(false, common);
        }

        public string Header => _common == null ? string.Empty : $" [{_common}]";

        public string Row(string? sourceName) => _perRow ? Label(sourceName) : string.Empty;
    }

    // 折叠行。断层收口时说明被折叠的是低匹配度结果，免得读者以为还有同等相关的东西没显示。
    //
    // 下一步的建议必须按「是谁砍掉的」分开给，三种情况的正确动作互不相同：
    //   - limit 砍的，且还没要过 'all'  → 'all' 真的能展开，劝它；
    //   - limit 砍的，且已经顶到硬上限  → 再要一次 'all' 是原地重试，只能劝收窄查询；
    //   - 只有断层收口砍的              → 那部分调多大的 limit 都拿不回来（见 ScopeFilter.Apply
    //     的 effectiveLimit = Min(limit, cutoff)）。原先这里一律劝 'all'，调用方照做后
    //     一条也没多出来，还会把「+N more」读成服务端在敷衍。
    public static string? FoldLine<T>(
        ScopedResult<T> result, string noun, string indent = "  ", ResultLimit? limit = null)
        => FoldLine(
            result.HiddenCount, result.Items.Count,
            result.TruncatedByScoreGap, result.TruncatedByLimit, noun, indent, limit);

    // 显式计数的重载。分段显示的场景（locate 的 Members 按 method/property/field 分组）里，
    // 真正被藏起来的条数由「ScopeFilter 的 limit」和「每组的显示配额」两层共同决定，
    // ScopedResult.HiddenCount 只看得见第一层。
    public static string? FoldLine(
        int hiddenCount,
        int shownCount,
        bool truncatedByScoreGap,
        bool truncatedByLimit,
        string noun,
        string indent = "  ",
        ResultLimit? limit = null)
    {
        if (hiddenCount <= 0) return null;

        // Unlimited 只说明调用方要过 'all'，不等于真的产出了 HardLimit 条——
        // 五条结果的查询也会走进这里，原先照样宣布「server cap 200 reached」。
        var capReached = limit?.Unlimited == true && shownCount >= HardLimit;

        var hint = truncatedByLimit
            ? capReached
                ? $"server cap {HardLimit} reached, narrow the query"
                : limit?.Unlimited == true
                    ? "narrow the query to see the rest"
                    // 'all' 也只到硬上限。藏起来的比上限还多时，`to expand` 会被读成「照做就拿全了」
                    // ——`... +767 more C# types (pass limit:'all' to expand)` 照做仍差 567 条，
                    // 而调用方没有任何线索能察觉。同一件事 trace usages 那边是说清了的
                    // （`raise the cap to 200`），这里跟上。
                    : shownCount + hiddenCount > HardLimit
                        ? $"pass limit:'all' for the first {HardLimit}; the rest needs a narrower query"
                        : "pass limit:'all' to expand"
            // 断层收口砍掉的是「相对首条掉了 40 分以上」的结果，要够到它们只能让首条不再那么
            // 突出——换个更宽泛的词，或改用 search_regex。原先写的是 refine（收窄），方向正好反了：
            // 照做只会把这些结果推得更远。
            : "broaden or reword the query; limit does not expand these";

        // 名词槽不留空。locate 的 Members 段是分种类子组印的，折叠行又与组内条目同缩进，
        // 于是 `... +1938 more` 紧跟在 Properties 组末尾时读起来像「还有 1938 个 property」，
        // 而它数的是三类之和。全服文法（README「低 Token 消耗」一节）本就要求这个槽有名词。
        return truncatedByScoreGap
            ? $"{indent}... +{hiddenCount} more {OutputText.NounFor(hiddenCount, noun)} (lower relevance, {hint})"
            : $"{indent}... +{hiddenCount} more {OutputText.NounFor(hiddenCount, noun)} ({hint})";
    }

    // 「扫到预览行上限就停了」的尾注。search_regex 与 trace usages 报的是同一件事，原先
    // 却是两句话——`[Preview lines truncated at limit 1 and scanning stopped there, raise
    // limit (up to 200) or use limit:'all']` 对 `[scanning stopped at the 1-preview cap
    // — pass limit:'all' to raise the cap to 200, …]`。同一个事件读两遍不同措辞，调用方
    // 只能各认一次。
    //
    // 与 FoldLine 的差别在于「剩下多少」这里是不知道的：扫描是在上限处停的，后面的候选
    // 文件根本没打开过。所以不写 `+N more`——那个数没人算得出来，编一个就是假的。
    // extraNotes 收本工具独有的补充（如「文件数也超了」），一并挂在同一句里。
    // 「有文件没扫全」的尾注。search_regex 与 trace usages 有一模一样的两处静默削减——单文件
    // 行闸（扫到第 20000 行就停）与读不开的文件直接跳过——此前只有前者说出口。调用方从
    // search_regex 学到的是「没有尾注即完整命中集」（那是写在它 Description 里的契约），
    // 顺手套到 trace 上，就会把一份可能漏了六万行的结果当成穷尽结论。
    public static string NotScannedInFullLine(IReadOnlyList<string> reasons)
        => $"... some files were not scanned in full ({string.Join("; ", reasons)}; "
           + "matches in the unscanned parts would not be listed)";

    // 上面那句里每条成因都要点名涉及哪个文件。只给个数时调用方无从判断它与本次查询有没有
    // 关系，只能把整份结果一律当成下界——第八轮盲测三条任务链各自独立踩到这一处（一条把精确的
    // 108 写成 `at least 108` 并把置信度降了一档），而三次的元凶都是同一个文件。
    //
    // 注意这**不影响**表头的 `at least N` 判据：行闸是在第 20000 行停的，那之后有没有命中
    // 谁也不知道，即便已扫部分零命中，总数仍然只是下界。点名解决的是「该不该在意」，
    // 不是「这个数准不准」。
    //
    // 名字多了没有额外信息，列前 max 个，其余记数。
    public static string NameSample(IReadOnlyList<string>? names, int max = 3)
    {
        if (names == null || names.Count == 0) return string.Empty;
        var head = string.Join(", ", names.Take(max));
        var rest = names.Count - Math.Min(max, names.Count);
        return rest > 0 ? $" ({head} and {rest} more)" : $" ({head})";
    }

    // 有文件没扫全时，命中总数就不再是确定值而是下界。表头与上面那行尾注必须同时改口，
    // 否则一句说「7 found」、一句说「有文件没扫全」，调用方无从判断该信哪个。
    //
    // 名词是 matching lines 而不是 matches：两个工具数的都是 `regex.IsMatch(line)` 逐行累加，
    // 同一行里命中两次仍只算一行。原先只写 `743 found`，而表头前半句是 "Regex matches for" /
    // "References to"——读者按「743 处命中」读，在一行多处的 pattern 上这个数直接是错的。
    public static string FoundCount(int total, bool anyFileIncomplete)
        => anyFileIncomplete
            ? $"at least {OutputText.Quantity(total, "matching lines")}"
            : OutputText.Quantity(total, "matching lines");



    // 每文件预览的折叠行。search_regex 与 trace usages 共用，且它是全语料里出现最频的一条
    // 折叠行（92/181），此前却是唯一两个槽都空着的一条：`... +77 more in this file`。
    // 名词按 FoundCount 同一条判据补成 matching lines。
    //
    // 增量之外还要给总数。只印 `+19 more` 时，读者要拿它和上面印出来的行数相加才得到 22，
    // 而「上面印了几行」是常数 3 这条规则**并不总成立**：扫描停在预览配额上时，最后一个文件
    // 只印了 1–2 行也带这条折叠（本语料的 Alert_Exhaustion.cs 印 2 行、折叠 2 条）。于是那条
    // 被诱导出来的「加 3」心算在一部分文件上给出错数，而这一行自己看不出落在哪种情况。
    // 沿用 R33 的 `N of M` 读法：出现 `of` 就是没给全。
    public static string PerFileFold(int hiddenCount, int totalInFile, string indent = "  ")
        => $"{indent}... +{hiddenCount} more of {OutputText.Quantity(totalInFile, "matching lines")} in this file";

    // 「怎么才能拿到更多」这半句不逐文件印，整份返回里说一次（同 §R19：逐行一模一样的东西
    // 上提到表头/脚注）。且只在这次真有文件被折叠时才印——没有折叠就没有这条。
    //
    // 其余 19 种折叠行都以 `(pass limit:'all' …)` 之类收尾，于是「留空」会被读成「这条漏印了
    // 参数名」。而这里每文件预览条数是常数、没有任何参数放得宽，这件事推不出来，必须明说。
    public static string PerFilePreviewCapLine(int previewsPerFile)
        => $"... previews are capped at {previewsPerFile} lines per file and no parameter widens that; "
           + "use read_code on a file to see the rest";

    public static string ScanStoppedLine(
        int previewCap, ResultLimit limit, IReadOnlyList<string>? extraNotes = null)
    {
        // 已经顶到硬上限时别再劝 limit:'all'，那只会原地重试；此时把「这就是服务端上限」
        // 说出来，否则调用方只会看见一个数，不知道它已经是天花板。
        var cap = limit.Unlimited
            ? $"scan stopped at the server cap of {previewCap} preview lines"
            : $"scan stopped at the {previewCap}-preview cap";
        var route = limit.Unlimited
            ? "narrow the query or the scope"
            : $"pass limit:'all' to raise the cap to {HardLimit}, or narrow the query or the scope";

        var notes = new List<string> { cap };
        if (extraNotes != null) notes.AddRange(extraNotes);

        return $"... more matches exist ({string.Join("; ", notes)}; {route})";
    }
}

// 跨段累加落在 scope 之外的命中，最后汇成一行提示。
// 防的是「按默认 scope 搜不到 → 断言该符号不存在」这类错误结论。
public sealed class ScopeReport
{
    private readonly Dictionary<string, int> _outOfScope = new(StringComparer.OrdinalIgnoreCase);

    public void Add<T>(ScopedResult<T> result)
    {
        foreach (var (source, count) in result.OutOfScope)
        {
            _outOfScope[source] = _outOfScope.GetValueOrDefault(source) + count;
        }
    }

    public void Add(string sourceName, int count)
    {
        if (count <= 0) return;
        _outOfScope[sourceName] = _outOfScope.GetValueOrDefault(sourceName) + count;
    }

    public bool HasOutOfScope => _outOfScope.Count > 0;

    // noun：合计的名词槽。locate 的这份脚注跨四段累加（类型 / 成员 / def / 内容命中），
    // 只有 "matches" 说得准；trace inheritors 那边全是子类，故由调用方点名。
    public string? Render(ScopeSelection scope, string noun = "matches")
    {
        if (_outOfScope.Count == 0) return null;

        var parts = _outOfScope
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} {kv.Value}");

        // 多源时先给合计。同一份返回里 scope **内**的量在表头是加总好的（`144 members`），
        // 这一行句式并列却只给分项，读者得临时切换成心算——整份输出里唯一一处要做算术的地方，
        // 且紧挨着一个不必做算术的同型数字。盲测里 7 个分项被加成 41（真值 47）。
        // 单源时不加：那时合计逐字等于那一个数（同「推得出来就不印」）。
        var total = _outOfScope.Count > 1
            ? $"{OutputText.Quantity(_outOfScope.Values.Sum(), noun)} — "
            : string.Empty;

        var sb = new StringBuilder();
        sb.Append($"\n_Outside scope '{scope.Expression}': {total}");
        sb.Append(string.Join(", ", parts));
        sb.Append(". Pass scope to include them (e.g. scope:'all')._");
        return sb.ToString();
    }
}
