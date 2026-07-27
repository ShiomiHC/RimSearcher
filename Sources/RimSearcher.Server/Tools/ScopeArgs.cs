using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

// scope / limit 两个参数在六个工具上语义一致，解析与呈现都集中在这里，
// 免得各工具各写一遍别名吸收与折叠行文案。
public static class ScopeArgs
{
    public const int DefaultDisplayLimit = 10;

    // 展开全部：limit 收到 "all"/"full"/"*" 或负数时不截断
    private const int Unlimited = 0;

    public static ScopeSelection Resolve(ScopeCatalog catalog, JsonElement args)
    {
        var expression = ToolArgs.GetOptionalString(args, "scope", "scopes", "source", "sources", "mod", "mods", "in");
        return catalog.Resolve(expression);
    }

    public static int GetDisplayLimit(JsonElement args, int fallback = DefaultDisplayLimit)
    {
        if (!ToolArgs.TryGetElement(args, out var value, "limit", "maxResults", "max", "count", "top"))
            return fallback;

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString()?.Trim().ToLowerInvariant();
            if (raw is "all" or "full" or "*" or "everything") return Unlimited;
        }

        var parsed = ToolArgs.GetInt(args, fallback, "limit", "maxResults", "max", "count", "top");
        return parsed <= 0 ? Unlimited : parsed;
    }

    public static object ScopeSchemaProperty(ScopeCatalog catalog) => new
    {
        type = "string",
        description = $"Optional search scope. {catalog.DescribeAvailable()}"
    };

    public static object LimitSchemaProperty() => new
    {
        type = "string",
        description = "Optional result cap per section (default 10). Pass a number, or 'all' to expand every match."
    };

    public static string Label(ScopedEntry<object> entry) => Label(entry.SourceName);

    public static string Label(string? sourceName)
        => string.IsNullOrEmpty(sourceName) ? string.Empty : $" [{sourceName}]";

    // 折叠行。断层收口时说明被折叠的是低匹配度结果，免得读者以为还有同等相关的东西没显示。
    public static string? FoldLine<T>(ScopedResult<T> result, string indent = "  ")
    {
        if (result.HiddenCount <= 0) return null;

        return result.TruncatedByScoreGap
            ? $"{indent}... +{result.HiddenCount} more (lower relevance, pass limit:'all' to expand)"
            : $"{indent}... +{result.HiddenCount} more (pass limit:'all' to expand)";
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
