using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

internal static class FieldsTable
{
    static readonly string[] Identity = ["def_type", "class", "declared_in", "source"];

    const int ValuesPerCell = 3;

    public static IReadOnlyList<string> Columns(CommandContext ctx)
    {
        var columns = new List<string>();
        foreach (var v in ctx.Args.Values(GetCommand.FieldsOption))
            foreach (var c in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!columns.Contains(c, StringComparer.OrdinalIgnoreCase)) columns.Add(c);
        if (columns.Count == 0) return columns;

        if (ctx.Args.Values(GetCommand.PathContains).Count > 0 || ctx.Args.Values(GetCommand.ExactPath).Count > 0 || ctx.Args.Flag(GetCommand.Defaults))
            throw new CliUsageException(
                "--fields 输出每条路径一列的表，--path-contains、--exact-path 与 --defaults 选取的是块内的行；" +
                "路径只传给 --fields。");
        foreach (var c in columns)
            if (c.Contains('=')) EntryKeys.Validate(c);
        return columns;
    }

    public static int Render(CommandContext ctx, IReadOnlyList<DefRow> defs, IReadOnlyList<string> asked, bool wholeType)
    {
        var limit = ctx.Limit(GetCommand.LimitOption);
        var shown = limit.IsAll ? defs : defs.Take(limit.Effective).ToList();
        var ids = shown.Select(d => d.Id).ToList();

        var headers = new List<string>();
        var cells = new Dictionary<string, Func<DefRow, object?>>(StringComparer.Ordinal);
        var rewritten = new List<string>();
        var emptyColumns = new List<string>();
        var containerColumns = new List<(string Path, DefRow Def)>();
        var questionMarks = false;

        foreach (var col in asked)
        {
            if (Identity.FirstOrDefault(i => string.Equals(i, col, StringComparison.OrdinalIgnoreCase)) is { } id)
            {
                headers.Add(id);
                cells[id] = d => id switch
                {
                    "def_type" => d.DefType,
                    "class" => d.Class,
                    "declared_in" => d.SourceMod,
                    _ => d.SourceFile,
                };
                continue;
            }

            var header = InjectionKey.ToFieldPath(col);
            var (values, containers) = ctx.Db.Project(ids, header);
            if (values.Count == 0 && containers.Count == 0 && !header.Contains('=') &&
                EntryKeys.TailAsKey(ctx, header, ctx.Unscoped(), null) is (var keyed, _, true))
            {
                var again = ctx.Db.Project(ids, keyed);
                rewritten.Add($"{col} 读作 {keyed}");
                (header, values, containers) = (keyed, again.Values, again.Containers);
            }
            if (values.Count == 0 && containers.Count == 0) emptyColumns.Add(header);
            if (shown.FirstOrDefault(d => containers.ContainsKey(d.Id) && !values.ContainsKey(d.Id)) is { } holder)
                containerColumns.Add((header, holder));

            headers.Add(header);
            cells[header] = d =>
            {
                if (values.TryGetValue(d.Id, out var vs)) return Join(vs);
                if (containers.TryGetValue(d.Id, out var n)) return $"（{Tally.Complete(n).Render("field")}）";
                if (d.FieldsTruncated > 0) { questionMarks = true; return "?"; }
                return null;
            };
        }

        var mixed = shown.Select(d => d.DefType).Distinct(StringComparer.Ordinal).Count() > 1;
        var columns = new List<string> { "def_name" };
        if (mixed && !headers.Contains("def_type")) columns.Add("def_type");
        columns.AddRange(headers);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var d in shown)
        {
            var row = new Dictionary<string, object?> { ["def_name"] = d.DefName };
            if (mixed && !headers.Contains("def_type")) row["def_type"] = d.DefType;
            foreach (var h in headers) row[h] = cells[h](d);
            rows.Add(row);
        }

        if (!wholeType || shown.Count < defs.Count)
            ctx.Report.CountNotice(Tally.Of(shown.Count, defs.Count), "def");
        if (rewritten.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{string.Join("、", rewritten)}：末段是列表条目的键值。");
        var cut = shown.Count(d => d.FieldsTruncated > 0);
        var whom = cut == 0 ? "这些 def" : $"完整导出的 def（{Tally.Of(shown.Count - cut, shown.Count).Render("def")}）";
        foreach (var col in emptyColumns)
            ctx.Report.Notice(NoticeKind.NextStep,
                (cut == shown.Count
                    ? $"{col} 列没有值，而这里每个 def 的导出都被截断，因此不能说明该路径不存在。"
                    : Cjk.Join($"{col} 列没有值：", whom, "都没有", What(col)) + "。") +
                $"含该文本的字段路径见 '{CommandRegistry.ExeName} get {shown[0].DefName} " +
                $"--type {shown[0].DefType} --path-contains {Quote(Container(col))}'。");
        foreach (var (col, holder) in containerColumns)
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{col} 列下的「（N 个字段）」是容器；其下内容见 '{CommandRegistry.ExeName} get " +
                $"{holder.DefName} --type {holder.DefType} --path-contains {Quote(col)}'。");

        ctx.Report.Promises("rows");
        ctx.Report.Table("rows", columns, rows, whole: headers);
        if (questionMarks)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"「?」标出导出被截断的 def：该字段可能存在而未导出。", footnote: true);
        return 0;
    }

    static string? Join(List<string?> values)
    {
        var shown = values.Take(ValuesPerCell).Select(v => v ?? "(null)");
        var text = string.Join("; ", shown);
        return values.Count > ValuesPerCell ? $"{text} (+{values.Count - ValuesPerCell})" : text;
    }

    static string What(string col) =>
        System.Text.RegularExpressions.Regex.Match(col, @"^([^\[]+)\[([^=\]]+)=([^\]]+)\]") is { Success: true } m
            ? $"{m.Groups[2].Value} 为 {m.Groups[3].Value} 的 {m.Groups[1].Value} 条目"
            : $"恰好为 {col} 的字段路径";

    static string Container(string col) =>
        col.IndexOf('[') is var i and > 0 && col.IndexOf('=', i) > 0 ? col[..i] : col;

    static string Quote(string s) => s.Contains('[') || s.Contains('|') || s.Contains(' ') ? $"\"{s}\"" : s;
}
