using System.Text;
using System.Text.Json;

namespace RimSearcher.Output;

public static class TextRenderer
{
    public const int MaxCellWidth = 72;

    public static string Render(Report report, bool quiet = false)
    {
        var sb = new StringBuilder();

        var tag = quiet ? "" : report.SnapshotTag is { Length: > 0 } t ? $"[{t}] " : "";

        var runs = NoticeRuns(report);
        var seen = 0;
        foreach (var entry in report.Rendered)
        {
            switch (entry)
            {
                case Notice n:
                    if (quiet) break;
                    if (!n.Footnote)
                    {
                        sb.Append(tag)
                          .Append(runs.Contains(seen) ? OutputText.NoticeMark : "")
                          .Append(n.Text).Append(OutputText.Newline);
                        tag = "";
                        seen++;
                    }
                    break;

                case Block block:
                    if (IsEmpty(block)) continue;
                    if (tag.Length > 0) { sb.Append(tag.TrimEnd()).Append(OutputText.Newline); tag = ""; }
                    if (sb.Length > 0) sb.Append(OutputText.Newline);
                    RenderBlock(sb, block);
                    break;
            }
        }

        var footnotes = quiet ? [] : report.Notices.Where(n => n.Footnote).ToList();
        if (footnotes.Count > 0)
        {
            if (sb.Length > 0) sb.Append(OutputText.Newline);
            foreach (var n in footnotes)
            {
                sb.Append(tag).Append(n.Text).Append(OutputText.Newline);
                tag = "";
            }
        }

        if (quiet && sb.Length == 0) return "";
        return OutputText.Finish(sb.ToString());
    }

    private static HashSet<int> NoticeRuns(Report report)
    {
        var marked = new HashSet<int>();
        var index = 0;
        var runStart = 0;
        var runLength = 0;

        void Flush()
        {
            if (runLength >= 3)
                for (var i = runStart + 1; i < runStart + runLength; i++) marked.Add(i);
            runLength = 0;
        }

        foreach (var entry in report.Rendered)
            switch (entry)
            {
                case Notice { Footnote: false }:
                    if (runLength == 0) runStart = index;
                    runLength++;
                    index++;
                    break;
                case Block b when !IsEmpty(b):
                    Flush();
                    break;
            }
        Flush();
        return marked;
    }

    private static bool IsEmpty(Block block) => block switch
    {
        TableBlock t => t.Rows.Count == 0 && string.IsNullOrEmpty(t.Caption),
        DetailBlock d => d.Pairs.All(p => OutputText.Cell(p.Value).Length == 0),
        TextBlock x => x.Lines.Count == 0,
        CompletenessBlock c => c.ByType.Count == 0,
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
                var labels = d.Pairs.Select(p => ColumnHeaders.For(d.Name, p.Key)).ToArray();
                var width = labels.Length == 0 ? 0 : labels.Max(OutputText.Width);
                for (var i = 0; i < d.Pairs.Count; i++)
                {
                    var cell = OutputText.Cell(d.Pairs[i].Value);
                    if (cell.Length == 0) continue;
                    sb.Append(labels[i]).Append(' ', width - OutputText.Width(labels[i]))
                      .Append("  ").Append(cell).Append(OutputText.Newline);
                }
                break;
            case TextBlock x:
                foreach (var line in x.Lines) sb.Append(line).Append(OutputText.Newline);
                break;
            case CompletenessBlock c:
                sb.Append("导出时被截断、本次回答可能缺少的 def —— ")
                  .Append(c.Scope).Append(OutputText.Newline);
                RenderTable(sb, new TableBlock("completeness", ["def_type", "defs_cut_short"],
                    [.. c.ByType.Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["def_type"] = t.Type,
                        ["defs_cut_short"] = t.Defs,
                    })]));
                sb.Append("核对：").Append(c.Verify).Append(OutputText.Newline);
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
            {
                var cell = OutputText.Cell(t.Rows[r].GetValueOrDefault(t.Columns[c]));
                cells[r][c] = t.Unclipped || t.Whole?.Contains(t.Columns[c]) == true
                    ? cell : OutputText.Truncate(cell, MaxCellWidth);
            }
        }

        var (columns, rows, folded) = Fold(t.Columns, cells);

        var said = folded.Where(f => t.Given?.GetValueOrDefault(f.Column) is not { } g || g != f.Value).ToList();
        if (said.Count > 0)
            sb.Append("各行相同，下表不再逐行列出：")
              .Append(string.Join("，", said.Select(f => $"{ColumnHeaders.For(t.Name, f.Column)}={f.Value}")))
              .Append('。').Append(OutputText.Newline);

        var headers = columns.Select(c => ColumnHeaders.For(t.Name, c)).ToArray();
        var widths = new int[columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            widths[c] = OutputText.Width(headers[c]);
            var whole = t.Whole?.Contains(columns[c]) == true;
            for (var r = 0; r < rows.Length; r++)
            {
                var w = OutputText.Width(rows[r][c]);
                widths[c] = Math.Max(widths[c], whole ? Math.Min(w, MaxCellWidth) : w);
            }
        }

        AppendRow(sb, headers, widths);
        for (var r = 0; r < rows.Length; r++) AppendRow(sb, rows[r], widths);
    }

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
            if (c != cells.Length - 1)
                sb.Append(' ', Math.Max(0, widths[c] - OutputText.Width(cells[c])));
        }
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        sb.Append(OutputText.Newline);
    }
}

public static class JsonRenderer
{
    public static string Render(Report report, bool quiet = false)
    {
        var root = new Dictionary<string, object?>();

        if (report.SnapshotTag is { Length: > 0 } tag) root["snapshot"] = tag;

        if (quiet)
            root["notes"] = new List<object>();
        else if (report.Notices.Count > 0)
            root["notes"] = report.Notices
                .Select(n =>
                {
                    var note = new Dictionary<string, object?>
                    {
                        ["kind"] = NoticeKindText.Of(n.Kind),
                        ["text"] = n.Text,
                    };
                    if (n.Count is { } c)
                    {
                        note["shown"] = c.Shown;
                        note["total"] = c.Total;
                    }
                    if (n.Data is { } d)
                        foreach (var (k, v) in d) Put(note, k, v, $"'{NoticeKindText.Of(n.Kind)}' 提示");
                    return note;
                })
                .ToList();

        static object? Bare(object? v) => v is Qualified q ? q.Value : v;
        static IReadOnlyDictionary<string, object?> Unwrap(IReadOnlyDictionary<string, object?> row)
            => row.Values.Any(v => v is Qualified)
                ? row.ToDictionary(kv => kv.Key, kv => Bare(kv.Value), StringComparer.Ordinal)
                : row;

        var collections = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);

        foreach (var block in report.Blocks)
        {
            var (name, value) = block switch
            {
                CompletenessBlock c => (c.Name, (object?)new Dictionary<string, object?>
                {
                    ["scope"] = c.Scope,
                    ["defs_cut_short"] = c.Defs,
                    ["types"] = c.ByType.Select(t => new Dictionary<string, object?>
                    {
                        ["def_type"] = t.Type,
                        ["defs_cut_short"] = t.Defs,
                    }).ToList(),
                    ["verify"] = c.Verify,
                }),
                TableBlock t => (t.Name, (object?)t.Rows.Select(Unwrap).ToList()),
                DetailBlock d => (d.Name, d.Pairs.ToDictionary(p => p.Key, p => Bare(p.Value))),
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
                Put(root, name, value, "顶层");
            }
        }

        foreach (var (name, items) in collections) Put(root, name, items, "顶层");

        foreach (var name in report.Promised)
            if (!root.ContainsKey(name)) root[name] = new List<IReadOnlyDictionary<string, object?>>();

        return OutputText.Finish(JsonTree.Write(root, indented: true));
    }

    private static void Put(Dictionary<string, object?> target, string key, object? value, string where)
    {
        if (!target.TryAdd(key, value))
            throw new InvalidOperationException(
                $"输出块 '{key}' 在{where}中重复写出。重复的块须放进集合（Report.Item），各占一格；" +
                "写进同一个键会丢数据。");
    }
}
