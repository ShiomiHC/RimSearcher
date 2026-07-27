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
        JsonValueKind.Array => $"an array of {value.GetArrayLength()} item(s)",
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
            + (fuzzy
                ? " Fuzzy sections also fold away results far below the top score, and those do not come back at any limit."
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
    public static string? FoldLine<T>(ScopedResult<T> result, string indent = "  ", ResultLimit? limit = null)
        => FoldLine(
            result.HiddenCount, result.Items.Count,
            result.TruncatedByScoreGap, result.TruncatedByLimit, indent, limit);

    // 显式计数的重载。分段显示的场景（locate 的 Members 按 method/property/field 分组）里，
    // 真正被藏起来的条数由「ScopeFilter 的 limit」和「每组的显示配额」两层共同决定，
    // ScopedResult.HiddenCount 只看得见第一层。
    public static string? FoldLine(
        int hiddenCount,
        int shownCount,
        bool truncatedByScoreGap,
        bool truncatedByLimit,
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
                    : "pass limit:'all' to expand"
            // 断层收口砍掉的是「相对首条掉了 40 分以上」的结果，要够到它们只能让首条不再那么
            // 突出——换个更宽泛的词，或改用 search_regex。原先写的是 refine（收窄），方向正好反了：
            // 照做只会把这些结果推得更远。
            : "broaden or reword the query; limit does not expand these";

        return truncatedByScoreGap
            ? $"{indent}... +{hiddenCount} more (lower relevance, {hint})"
            : $"{indent}... +{hiddenCount} more ({hint})";
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

    public string? Render(ScopeSelection scope)
    {
        if (_outOfScope.Count == 0) return null;

        var parts = _outOfScope
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} {kv.Value}");

        var sb = new StringBuilder();
        sb.Append($"\n_Outside scope '{scope.Expression}': ");
        sb.Append(string.Join(", ", parts));
        sb.Append(". Pass scope to include them (e.g. scope:'all')._");
        return sb.ToString();
    }
}
