using System.Text;
using System.Text.Json;

namespace RimSearcher.Output;

/// <summary>
/// 默认渲染器:紧凑文本。
///
/// 默认不是裸 JSON:实际消费方是读 stdout 的 LLM 而不是 jq,同一批数据渲染成对齐表比
/// JSON 省一半以上的字节。要机器可组合的那一份用 <c>--json</c>,那时声明区搬进
/// <c>notes</c> 数组,一个字都不丢。
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
            // 空块直接跳过,连分隔空行都不留:连着的两个空行会被读成「后面还有,被截断了」。
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

        var (columns, rows, folded) = Fold(t.Columns, cells);

        // 折出来的话摆在表**上方**而不是脚下:它说的是下面每一行的一部分,读到行的时候
        // 得已经知道。措辞点明「没在下面重复」—— 只说「每行都一样」会被读成列被删了。
        if (folded.Count > 0)
            sb.Append("Same in every row, not repeated below: ")
              .Append(string.Join(", ", folded.Select(f => $"{f.Column}={f.Value}")))
              .Append('.').Append(OutputText.Newline);

        var widths = new int[columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            widths[c] = OutputText.Width(columns[c]);
            for (var r = 0; r < rows.Length; r++) widths[c] = Math.Max(widths[c], OutputText.Width(rows[r][c]));
        }

        AppendRow(sb, columns, widths);
        for (var r = 0; r < rows.Length; r++) AppendRow(sb, rows[r], widths);
    }

    /// <summary>
    /// 整列同值的列不在每行重复它。一列 40 字符宽、一屏几十行,印的全是同一个字,
    /// 而这一列真正携带的信息量是一句话。
    ///
    /// 四条边界:
    ///   **第一列不折** —— 这套输出的第一列一律是那行的身份(def_name、path、key、order),
    ///     而下一步命令要拿它当参数。两个同名 def 的 def_name 确实整列同值,折掉之后剩下的
    ///     行不再是记录;
    ///   **两行起**才折 —— 一行表里「每行都一样」是废话,且折叠反而多印一行;
    ///   **空值不折** —— 一列全空折成 `label=` 会读成「这些 def 的标签是空串」,
    ///     而实情是这一列在这批行上没有值;
    ///   **不折光** —— 每一列都同值时(单行表之外这罕见)退回原样,只剩一句话没有表
    ///     比重复更难读。
    /// </summary>
    private static (string[] Columns, string[][] Rows, List<(string Column, string Value)> Folded)
        Fold(IReadOnlyList<string> columns, string[][] cells)
    {
        var folded = new List<(string, string)>();
        var keep = new List<int>();
        for (var c = 0; c < columns.Count; c++)
        {
            if (c == 0) { keep.Add(c); continue; }
            var first = cells[0][c];
            var same = cells.Length > 1 && first.Length > 0;
            for (var r = 1; same && r < cells.Length; r++) same = cells[r][c] == first;
            if (same) folded.Add((columns[c], first));
            else keep.Add(c);
        }

        if (keep.Count == 0)
        {
            folded.Clear();
            keep.AddRange(Enumerable.Range(0, columns.Count));
        }

        return (keep.Select(c => columns[c]).ToArray(),
                cells.Select(row => keep.Select(c => row[c]).ToArray()).ToArray(),
                folded);
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            if (c > 0) sb.Append("  ");
            sb.Append(cells[c]);
            // 末列不补:行尾空格会被 Finish 之外的比对(快照基线)当成有意义的字节。
            if (c != cells.Length - 1)
                sb.Append(' ', widths[c] - OutputText.Width(cells[c]));
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
                // 有结构化形态就用它,不要让消费方去拆 "path:line:text"。
                TextBlock x => (x.Name, x.Rows is null ? x.Lines : (object)x.Rows),
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

        // 答应过的数据键就得在,哪怕是空数组 —— 见 Report.Promises。
        foreach (var name in report.Promised)
            if (!root.ContainsKey(name)) root[name] = new List<IReadOnlyDictionary<string, object?>>();

        return OutputText.Finish(JsonSerializer.Serialize(root, Options));
    }

    /// <summary>
    /// 覆盖式赋值是这套输出唯一一处会**静默丢数据**的地方(同名 def 的 fields 被后一个
    /// 覆盖成空,而 notes 还在说匹配到了),消费方无从看出少了东西 —— 所以当场炸。
    /// </summary>
    private static void Put(Dictionary<string, object?> target, string key, object? value, string where)
    {
        if (!target.TryAdd(key, value))
            throw new InvalidOperationException(
                $"Output block '{key}' was emitted twice at {where}. Repeated blocks belong in a collection " +
                "(Report.Item) so that each one keeps its own slot; writing them to the same key loses data.");
    }

    /// <summary>
    /// <see cref="NoticeKind"/> → JSON 里的 kind 值。参考页列出的取值集合必须走这里,
    /// 不要另抄一份。
    /// </summary>
    internal static string SnakeCase(string s)
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
