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

        // 快照标签贴在第一条声明行前,贴过就清空。一条声明都没有时它自己成行 ——
        // 那是它唯一独占一行的场合。
        var tag = report.SnapshotTag is { Length: > 0 } t ? $"[{t}] " : "";

        // 按 Add 的先后走,声明与数据块交错 —— 每句话就落在它所讲的那个块旁边,而调用方
        // pipe 一个 head 先拿到的是数据。位置由写命令的人决定,不由这里统一提到最前。
        // 连着三条以上时,第二条起挂一个记号。**不加标题**:标题是个标签,而标签会被学会
        // 跳过 —— 这几句偏偏不是免责声明,是承重的更正(子串那句改变的是每一行怎么读)。
        // 记号只做一件事:让读者看出这是 N 件互不相干的事,而不是一段连着的话。
        //
        // 第一行始终不挂记号,因为它是 `| head -1` 唯一的幸存者,那个位置留给计数。
        // 门槛取 3 而不是 2:两句话不构成墙,加记号只是多两个字符。
        // 实测最高的一堵是 `where <字段> --value <值>` 不带 --exact:5 段、1169 字,
        // 而多数命令是 1~3 段。
        var runs = NoticeRuns(report);
        var seen = 0;
        foreach (var entry in report.Entries)
        {
            switch (entry)
            {
                case Notice n:
                    // 脚注另有归处,在最后统一收。
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
                    // 空块直接跳过,连分隔空行都不留:连着的两个空行会被读成「后面还有,被截断了」。
                    if (IsEmpty(block)) continue;
                    if (tag.Length > 0) { sb.Append(tag.TrimEnd()).Append(OutputText.Newline); tag = ""; }
                    // 块与它上面的东西之间空一行;声明之间不空 —— 连着的几句话是一段。
                    if (sb.Length > 0) sb.Append(OutputText.Newline);
                    RenderBlock(sb, block);
                    break;
            }
        }

        var footnotes = report.Notices.Where(n => n.Footnote).ToList();
        if (footnotes.Count > 0)
        {
            if (sb.Length > 0) sb.Append(OutputText.Newline);
            foreach (var n in footnotes)
            {
                sb.Append(tag).Append(n.Text).Append(OutputText.Newline);
                tag = "";
            }
        }

        return OutputText.Finish(sb.ToString());
    }

    /// <summary>
    /// 哪些声明该挂记号:**连续三条以上的那一段里,除第一条之外的每一条**。
    ///
    /// 「连续」按渲染顺序算,中间夹了一个数据块就断开 —— 隔着一张表的两句话本来就
    /// 不是一段。脚注不参与:它们在最后统一收,与这里的连续性无关。
    ///
    /// 返回的下标是**非脚注声明在渲染序列里的序号**,与 <c>report.Notices</c> 的下标
    /// 不是一回事(后者含脚注)。
    /// </summary>
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

        foreach (var entry in report.Entries)
            switch (entry)
            {
                case Notice { Footnote: false }:
                    if (runLength == 0) runStart = index;
                    runLength++;
                    index++;
                    break;
                // 空块不渲染,所以它也不该断开一段 —— 判据与 IsEmpty 共用,
                // 否则「块在但印不出来」会在这里造出一个看不见的分段。
                case Block b when !IsEmpty(b):
                    Flush();
                    break;
            }
        Flush();
        return marked;
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

        // 文本侧那个标签在这里有自己的键,不塞进 notes:机器侧要判「哪份快照」时,
        // 从一句话里抠字符串是两份数据。取值与文本侧同源,逐字相同。
        if (report.SnapshotTag is { Length: > 0 } tag) root["snapshot"] = tag;

        if (report.Notices.Count > 0)
            root["notes"] = report.Notices
                .Select(n =>
                {
                    var note = new Dictionary<string, object?>
                    {
                        ["kind"] = SnakeCase(n.Kind.ToString()),
                        ["text"] = n.Text,
                    };
                    // 计数句才有这两个键 —— 缺席说的是「这条不是计数」,不是「没截断」。
                    // total 显式为 null 是第三态(只知道下界,扫描没跑完),与「total 等于
                    // shown」不是一回事,所以写出 null 而不是省掉这个键。
                    if (n.Count is { } c)
                    {
                        note["shown"] = c.Shown;
                        note["total"] = c.Total;
                    }
                    return note;
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
