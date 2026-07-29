using System.Text;
using System.Text.Json;

namespace RimSearcher.Output;

/// <summary>
/// 默认渲染器:紧凑文本。
///
/// 为什么默认不是裸 JSON(06「与 JSON 主体的缝合格式」开放点的定稿):这条管线的实际消费方
/// 是读 stdout 的 LLM,而不是 jq。同一批数据渲染成对齐表比 JSON 省一半以上的字节,这直接
/// 服务于上下文预算硬约束;散文声明区也需要一个不破坏结构的落点。要机器可组合的那一份用
/// <c>--json</c>,那时声明区搬进 <c>notes</c> 数组,一个字都不丢。
/// stderr 不用于声明 —— 管道场景下 LLM 调用方会漏读。
/// </summary>
public static class TextRenderer
{
    public const int MaxCellWidth = 72;

    public static string Render(Report report)
    {
        var sb = new StringBuilder();

        foreach (var n in report.Notices.Where(n => !n.Footnote))
            sb.Append(n.Text).Append(OutputText.Newline);

        var first = true;
        foreach (var block in report.Blocks)
        {
            // 空块直接跳过,连分隔空行都不留。分隔符原本写在渲染**之前**,于是一个空表
            // 会留下两个连着的空行 —— 而空行会被读成「后面还有,被截断了」,正是文法闸
            // 明令要躲开的那个误读。空块不是「渲染成空」,是根本不存在。
            if (IsEmpty(block)) continue;
            if (sb.Length > 0 && !first) sb.Append(OutputText.Newline);
            if (sb.Length > 0 && first && report.Notices.Any(n => !n.Footnote)) sb.Append(OutputText.Newline);
            first = false;
            RenderBlock(sb, block);
        }

        var footnotes = report.Notices.Where(n => n.Footnote).ToList();
        if (footnotes.Count > 0)
        {
            if (sb.Length > 0) sb.Append(OutputText.Newline);
            foreach (var n in footnotes) sb.Append(n.Text).Append(OutputText.Newline);
        }

        return OutputText.Finish(sb.ToString());
    }

    /// <summary>
    /// 一个块渲染出来会不会是零字节。判据要与 <see cref="RenderBlock"/> 的跳过规则一致 ——
    /// DetailBlock 会逐条跳过空值,所以「有 pairs」不等于「有输出」。
    /// </summary>
    private static bool IsEmpty(Block block) => block switch
    {
        TableBlock t => t.Rows.Count == 0 && string.IsNullOrEmpty(t.Caption),
        DetailBlock d => d.Pairs.All(p => OutputText.Cell(p.Value).Length == 0),
        TextBlock x => x.Lines.Count == 0,
        _ => false,
    };

    private static void RenderBlock(StringBuilder sb, Block block)
    {
        switch (block)
        {
            case TableBlock t:
                if (t.Caption is { Length: > 0 }) sb.Append(t.Caption).Append(OutputText.Newline);
                RenderTable(sb, t);
                break;
            case DetailBlock d:
                var width = d.Pairs.Count == 0 ? 0 : d.Pairs.Max(p => p.Key.Length);
                foreach (var (k, v) in d.Pairs)
                {
                    var cell = OutputText.Cell(v);
                    if (cell.Length == 0) continue;
                    sb.Append(k.PadRight(width)).Append("  ").Append(cell).Append(OutputText.Newline);
                }
                break;
            case TextBlock x:
                foreach (var line in x.Lines) sb.Append(line).Append(OutputText.Newline);
                break;
        }
    }

    private static void RenderTable(StringBuilder sb, TableBlock t)
    {
        if (t.Rows.Count == 0) return;

        var cells = new string[t.Rows.Count][];
        for (var r = 0; r < t.Rows.Count; r++)
        {
            cells[r] = new string[t.Columns.Count];
            for (var c = 0; c < t.Columns.Count; c++)
                cells[r][c] = OutputText.Truncate(
                    OutputText.Cell(t.Rows[r].GetValueOrDefault(t.Columns[c])), MaxCellWidth);
        }

        var widths = new int[t.Columns.Count];
        for (var c = 0; c < t.Columns.Count; c++)
        {
            widths[c] = t.Columns[c].Length;
            for (var r = 0; r < cells.Length; r++) widths[c] = Math.Max(widths[c], cells[r][c].Length);
        }

        AppendRow(sb, t.Columns.ToArray(), widths);
        for (var r = 0; r < cells.Length; r++) AppendRow(sb, cells[r], widths);
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            if (c > 0) sb.Append("  ");
            sb.Append(c == cells.Length - 1 ? cells[c] : cells[c].PadRight(widths[c]));
        }
        sb.Append(OutputText.Newline);
    }
}

/// <summary>
/// <c>--json</c> 渲染器。声明区不丢:全部搬进 <c>notes</c>,每条带 kind,机器侧可判类别。
/// </summary>
public static class JsonRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(Report report)
    {
        var root = new Dictionary<string, object?>();

        if (report.Notices.Count > 0)
            root["notes"] = report.Notices
                .Select(n => new Dictionary<string, object?>
                {
                    ["kind"] = SnakeCase(n.Kind.ToString()),
                    ["text"] = n.Text,
                })
                .ToList();

        var collections = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);

        foreach (var block in report.Blocks)
        {
            var (name, value) = block switch
            {
                TableBlock t => (t.Name, (object?)t.Rows),
                DetailBlock d => (d.Name, d.Pairs.ToDictionary(p => p.Key, p => p.Value)),
                TextBlock x => (x.Name, x.Lines),
                _ => ("", null),
            };
            if (name.Length == 0) continue;

            if (block.Collection is { } coll)
            {
                if (!collections.TryGetValue(coll, out var items))
                    collections[coll] = items = [];
                while (items.Count <= block.Item) items.Add([]);
                Put(items[block.Item], name, value, $"{coll}[{block.Item}]");
            }
            else
            {
                Put(root, name, value, "the top level");
            }
        }

        foreach (var (name, items) in collections) Put(root, name, items, "the top level");

        return OutputText.Finish(JsonSerializer.Serialize(root, Options));
    }

    /// <summary>
    /// 覆盖式赋值是这套输出唯一一处会**静默丢数据**的地方(第二轮盲测实证:同名 def 的
    /// fields 被后一个覆盖成空,而 notes 还在说匹配到了)。宁可当场炸,也不许交出一份
    /// 自洽度不明的 JSON —— 消费方没有任何办法从结果里看出少了东西。
    /// </summary>
    private static void Put(Dictionary<string, object?> target, string key, object? value, string where)
    {
        if (!target.TryAdd(key, value))
            throw new InvalidOperationException(
                $"Output block '{key}' was emitted twice at {where}. Repeated blocks belong in a collection " +
                "(Report.Item) so that each one keeps its own slot; writing them to the same key loses data.");
    }

    private static string SnakeCase(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
