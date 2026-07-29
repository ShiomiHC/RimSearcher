using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// 参数契约问题导致的失败：调用方带着可修正的错误参数名而来，必须拿到能自我纠正的提示，
// 而不是 JsonElement.GetProperty 抛出的 KeyNotFoundException 冒泡成 -32603 Internal error。
// 后者会被调用方读成「服务器坏了」，从而整体放弃本工具集。
public sealed class ToolArgumentException(string message) : Exception(message);

// ============================ schema 声明什么，不声明什么 ============================
//
// 本层长期两种做法并存：有的地方「服务端认得比 schema 多」（sync_sources 的 action 分支认 9
// 个拼法而 enum 只有 3 个），有的地方「schema 比服务端严」（limit 声明 maximum，而服务端对
// 超出的值是夹紧不是拒绝）。区别只是当时谁写的。这里定一条，此后处处照它。
//
// **判据只有一个问题：一个严格照 schema 生成请求的调用方，会不会因为这处声明（或不声明）
// 而吃亏？**
//
//   - 服务端会**拒绝**某类输入而 schema 不说 → 调用方发出去才知道，白跑一轮。**补声明。**
//     （`maxLength = MaxFuzzyQueryLength`：超长的 query 是抛异常，不是截断。
//      `read_code.lineCount` 的 `minimum = 1`：`<= 0` 返回 isError，不是夹紧。）
//
//   - 服务端会**接受**某类输入而 schema 不许 → 校验型客户端在发出之前就把一个完全能跑的请求
//     挡下了。**放宽声明。**（所有 integer 属性写成 `["integer","string"]`：GetInt 收字符串，
//     而 LLM 调用方实测常把 `limit` 写成 `"5"`。浮点不列进去——服务端确实收 `50.5` 并截断，
//     但声明 `number` 是在鼓励一种没人该传的输入，而不传它的调用方一点不吃亏。）
//
//   - 服务端**夹紧**而不是拒绝 → 声明成 `maximum` / `minimum` 就是在撒一个会伤人的谎：
//     JSON Schema 的 `maximum` 意思是「大于它非法」，而这里的意思是「大于它给你夹到它」。
//     **不声明成硬约束，把那个数写进 description**（那边是插值的，人和 LLM 读得到，
//     而校验器本来就不该拦）。这条不是新定的——`list_directory.limit` 那格早就写着
//     「不声明 minimum：……声明 minimum=0 会让照着描述传 -1 的调用在 client 侧就被校验挡下」，
//     P7 只是把同一句理由贯彻到同一格里的 maximum，以及另外几个工具。
//
//   - 服务端多认几个**同义拼法**（`action:'update'` 之于 `'sync'`，locate 的 `m:` 之于
//     `method:`）→ **不补进 enum。**这一类的受益者按定义就是「没照 schema 走的调用方」；
//     照 schema 走的那些用的是规范拼法，一点不吃亏，而把 9 个拼法塞进 enum 只会让 tools/list
//     多出一堆噪音，还暗示存在 9 种语义。要写下来的不是拼法清单，是**这条政策本身**——
//     否则下一个人看见 switch 认 9 个而 enum 声明 3 个，会当成 bug 去「修」，
//     从而掐掉唯一受益的那批调用方。故那两处 switch 各留一句指回这里。
//
// 一句话：**schema 声明的是调用方需要预判的东西，不是服务端代码的镜像。**
// ====================================================================================

// 各工具主参数名互不相同（locate=query / inspect=name / read_code=path / search_regex=pattern /
// trace=symbol），调用方极易从一个工具类推到另一个。这里统一吸收别名与标量类型漂移。
public static class ToolArgs
{
    public static bool TryGetElement(JsonElement args, out JsonElement value, params string[] names)
    {
        value = default;
        if (args.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in names)
        {
            if (args.TryGetProperty(name, out var found) && found.ValueKind != JsonValueKind.Null)
            {
                value = found;
                return true;
            }
        }

        // 大小写/下划线差异（max_results vs maxResults）也吸收
        foreach (var property in args.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (NormalizeKey(property.Name) == NormalizeKey(name) && property.Value.ValueKind != JsonValueKind.Null)
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        return false;
    }

    // 缺失即抛 ToolArgumentException；消息须自带纠正路径（收到了什么键、该用什么、别名有哪些）
    public static string GetRequiredString(JsonElement args, ToolArgSpec spec, params string[] names)
    {
        if (!TryGetElement(args, out var value, names))
            throw new ToolArgumentException(spec.BuildMissingMessage(names[0], args));

        var text = CoerceToString(value);
        if (string.IsNullOrWhiteSpace(text))
            throw new ToolArgumentException($"Parameter '{names[0]}' for {spec.ToolName} must be a non-empty string.\n{spec.BuildUsage()}");

        return text.Trim();
    }

    // 会喂给模糊匹配的参数（locate 的 query / inspect 的 name / trace 的 symbol）的长度闸。
    //
    // 这些串要与索引里每一个类型名、成员名、defName 逐一打分，代价是 O(语料 × 串长)：
    // 实测一条 100 KB 的 query 让服务端 210% CPU 烧了 77 秒，然后把那 100 KB 原样回显进
    // "No results for '…'"——既拖垮进程，又把等量垃圾塞回调用方的上下文。
    // 语料里最长的符号名也就几十个字符，256 已经宽得离谱；越过它的一定是误用，
    // 该立刻给一条能改的错误，而不是先烧一分多钟再说没找到。
    public const int MaxFuzzyQueryLength = 256;

    public static string GetRequiredFuzzyString(JsonElement args, ToolArgSpec spec, params string[] names)
    {
        var text = GetRequiredString(args, spec, names);
        if (text.Length <= MaxFuzzyQueryLength) return text;

        throw new ToolArgumentException(
            $"Parameter '{names[0]}' for {spec.ToolName} is {text.Length} characters; the limit is "
            + $"{MaxFuzzyQueryLength}. This parameter is matched against every indexed name, so a long "
            + "string costs a full-corpus scan and cannot match anything. Pass just the symbol or def "
            + "name; to match patterns against file contents use rimworld-searcher__search_regex.\n"
            + spec.BuildUsage());
    }

    // 回显调用方给的串时用：错误信息里的原样回显是「输入多大、输出就多大」的放大器
    public static string ForEcho(string value, int maxLength = 120)
        => value.Length <= maxLength ? value : value[..maxLength] + $"… ({value.Length} chars total)";

    public static string? GetOptionalString(JsonElement args, params string[] names)
        => TryGetElement(args, out var value, names) ? CoerceToString(value)?.Trim() : null;

    // 名字位（read_code 的 extractClass / methodName / className）收到布尔值时不能走
    // CoerceToString：那会把 extractClass:true 变成一次「找不到名叫 true 的类」的查找失败，
    // 返回读起来是「这个文件里没有这个类，去 inspect 核对名字」——方向完全相反，照做只会
    // 再确认一遍那个类确实存在，而真因是参数传错了型。
    //
    // 误传的概率不低：extractClass 这个名字本身就像个开关（「要不要提取整个类」），而它
    // 要的是类名。limit 那一侧早就是严格的（'many' / true / object 一律拒绝而不是静默换
    // 默认值，schema 里也这么写着），名字位一直宽着——同一个工具箱里两套松紧本身就是误导。
    public static string? GetOptionalName(JsonElement args, ToolArgSpec spec, string whatItNames, params string[] names)
    {
        if (!TryGetElement(args, out var value, names)) return null;

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            throw new ToolArgumentException(
                $"Parameter '{names[0]}' for {spec.ToolName} takes {whatItNames}, not a boolean; "
                + $"received {(value.ValueKind == JsonValueKind.True ? "true" : "false")}. "
                + $"It is not a switch — pass the name itself (e.g. {names[0]}: 'CompShield').\n"
                + spec.BuildUsage());

        return CoerceToString(value)?.Trim();
    }

    // 数字参数常被传成字符串（实测 "max_results":"5"）；宽容解析，不可解析才报错
    public static int GetInt(JsonElement args, int fallback, params string[] names)
    {
        if (!TryGetElement(args, out var value, names)) return fallback;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out var n) ? n : (int)Math.Clamp(value.GetDouble(), int.MinValue, int.MaxValue);
            case JsonValueKind.String:
                var raw = value.GetString();
                if (int.TryParse(raw, out var parsed)) return parsed;
                if (double.TryParse(raw, out var asDouble)) return (int)asDouble;
                return fallback;
            default:
                return fallback;
        }
    }

    public static bool GetBool(JsonElement args, bool fallback, params string[] names)
    {
        if (!TryGetElement(args, out var value, names)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" or "on" => true,
                "false" or "0" or "no" or "n" or "off" => false,
                _ => fallback
            },
            _ => fallback
        };
    }

    // 列表语义的参数要按列表收全。单值位的 CoerceToString 对数组只取首元素，用在这里会把
    // ["vanilla","Milira"] 静默截成 vanilla——而参数说明写着 "comma-separated names"，
    // 客户端把它序列化成数组是很自然的写法，截断后调用方拿到的是一份少一半的结果且无任何提示。
    public static string[]? GetStringList(JsonElement args, params string[] names)
    {
        if (!TryGetElement(args, out var value, names)) return null;

        IEnumerable<string?> items = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(CoerceToString)
            : [CoerceToString(value)];

        // 数组的每个元素自身仍可能是逗号串（["vanilla,Milira"]），两种写法都要认
        var result = items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .SelectMany(item => item!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        return result.Length > 0 ? result : null;
    }

    // 单值位上收到数组时取首元素——调用方偶发把标量包成数组
    private static string? CoerceToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetArrayLength() > 0 ? CoerceToString(value[0]) : null,
            _ => null
        };
    }

    private static string NormalizeKey(string key) => ConfigToml.NormalizeKey(key);

    private static readonly string[] LocateFilterPrefixes =
        ["type:", "def:", "method:", "field:", "class:", "member:", "property:"];

    // locate 的过滤前缀会被调用方带到只认裸名的工具上（实测 inspect 收到 'def:VoidNode'、
    // read_code 收到 'type:CompVoidNode'）。前缀在这些工具里是纯冗余，剥掉即可正常解析。
    public static string StripLocateFilterPrefix(string value)
    {
        var trimmed = value.Trim();
        foreach (var prefix in LocateFilterPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim();
        }
        return trimmed;
    }

    public static IReadOnlyList<string> ReceivedKeys(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return [];
        return args.EnumerateObject().Select(p => p.Name).ToList();
    }

    // 认不出的参数键必须回上去说一声。错误方向与 sync_sources 已经修过的 granularity 拼错
    // 完全同型：返回是一份逐字正常、看不出任何异常的结果，而调用方据此得出的结论是
    // 「我按 X 过滤后就这些」，实际是未过滤的全量前 N 条。提示只在差集非空时出现，
    // 健康调用零开销。
    public static string? UnknownKeyNotice(ITool tool, JsonElement args)
    {
        var received = ReceivedKeys(args);
        if (received.Count == 0) return null;

        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in SchemaPropertyNames(tool.JsonSchema)) accepted.Add(NormalizeKey(name));
        foreach (var name in tool.ExtraAcceptedKeys) accepted.Add(NormalizeKey(name));

        // schema 读不出属性时不做判断——宁可不提示，也不能把合法参数报成被忽略
        if (accepted.Count == 0) return null;

        var unknown = received.Where(k => !accepted.Contains(NormalizeKey(k))).ToArray();
        if (unknown.Length == 0) return null;

        return $"\n\n_Ignored unknown {CountedNoun.Parameters.For(unknown.Length)}: "
            + $"{string.Join(", ", unknown.Select(k => $"'{k}'"))}. "
            + $"{tool.Name} accepts: {string.Join(", ", SchemaPropertyNames(tool.JsonSchema))}._";
    }

    private static IReadOnlyList<string> SchemaPropertyNames(object schema)
    {
        var element = JsonSerializer.SerializeToElement(schema);
        if (element.ValueKind != JsonValueKind.Object) return [];
        if (!element.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return [];
        return props.EnumerateObject().Select(p => p.Name).ToList();
    }
}

// 每个工具声明一份，用于把「缺参」变成一条可照着改的说明
public sealed class ToolArgSpec(string toolName, string requiredSummary, string allParameters, string? extraHint = null)
{
    public string ToolName { get; } = toolName;

    public string BuildMissingMessage(string canonicalName, JsonElement args)
    {
        var received = ToolArgs.ReceivedKeys(args);
        var sb = new StringBuilder();
        sb.AppendLine($"Missing required parameter '{canonicalName}' for {ToolName}.");
        sb.AppendLine(received.Count > 0
            ? $"Received keys: {string.Join(", ", received)}."
            : "Received no arguments.");
        sb.AppendLine(BuildUsage());
        if (!string.IsNullOrWhiteSpace(extraHint)) sb.AppendLine(extraHint);
        return sb.ToString().TrimEnd();
    }

    public string BuildUsage()
        => $"Required: {requiredSummary}\nAll parameters: {allParameters}";
}
