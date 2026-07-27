using System.Text;
using System.Text.Json;

namespace RimSearcher.Server.Tools;

// 参数契约问题导致的失败：调用方带着可修正的错误参数名而来，必须拿到能自我纠正的提示，
// 而不是 JsonElement.GetProperty 抛出的 KeyNotFoundException 冒泡成 -32603 Internal error。
// 后者会被调用方读成「服务器坏了」，从而整体放弃本工具集。
public sealed class ToolArgumentException(string message) : Exception(message);

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

    public static string? GetOptionalString(JsonElement args, params string[] names)
        => TryGetElement(args, out var value, names) ? CoerceToString(value)?.Trim() : null;

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
