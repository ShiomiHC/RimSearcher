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

// scope / limit 两个参数在六个工具上语义一致，别名吸收与夹紧集中在这里。
//
// 呈现成员曾经也住在这里（约 440 行：折叠行、来源标签、下界记号、各类脚注……），理由只是
// 「六个工具都 using 着它」。它们已迁往 Tools/Output/，那里才是呈现契约的家；这个类回到只管
// 参数语义。留在这边的 HardLimit 与 ResultLimit 仍被 Output 侧引用——折叠行的三个分支要靠
// 「调用方要过 'all' 没有」和「顶到服务端上限没有」才分得开，那两件事本来就是参数语义。
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
            // method:/def: 这类查询也被扣上「可能还有你永远拿不到的结果」——那是个不可证伪
            // 的疑虑：返回里没有任何一处能判断它有没有发生。
            //
            // 原先这句把 method:/field: 称作 "exact-name filters"，理由是「实测精确名过滤走的是
            // 全等匹配，分数恒为 100」。F32 之后那个理由不成立了：合格键改成按 60 分线精确枚举，
            // 前缀命中（CompTickRare 之于 CompTick）以 90 分正式进入这一段的总数。于是
            // `method:CompTick` 的表头写 200，而叫这个名字的方法只有 144，默认 limit 印出来的
            // 前 10 条又恰好全是 100%——混合在默认返回里一处痕迹都没有。
            // 这句话本身成了假陈述，且是**唯一**一处告诉调用方「这个总数只含同名项」的地方。
            + (fuzzy
                ? " Score-gap folding drops results far below the top score and no limit brings them back; it "
                  + "only applies to fuzzy matching. method: and field: restrict the search to members but still "
                  + "match names by score, so their section total counts near-name matches too — the (N%) on each "
                  + "row is what tells them apart, and anything below 100% is not the name that was asked for."
                : string.Empty)
    };
}
