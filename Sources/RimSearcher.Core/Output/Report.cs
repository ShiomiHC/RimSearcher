using System.Text;

namespace RimSearcher.Output;

public enum NoticeKind
{
    Truncation,
    Count,
    Filter,
    Staleness,
    SnapshotChoice,
    Boundary,
    Advisory,
    Clamp,
    NextStep,
}

public static class NoticeKindText
{
    public static string Of(NoticeKind kind) => kind switch
    {
        NoticeKind.Truncation => "截断",
        NoticeKind.Count => "计数",
        NoticeKind.Filter => "筛选",
        NoticeKind.Staleness => "快照不匹配",
        NoticeKind.SnapshotChoice => "快照选择",
        NoticeKind.Boundary => "边界",
        NoticeKind.Advisory => "参考",
        NoticeKind.Clamp => "封顶",
        NoticeKind.NextStep => "下一步",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

public abstract record ReportEntry;

public sealed record Notice(NoticeKind Kind, string Text, bool Footnote = false) : ReportEntry
{
    public bool Teach { get; init; }

    public Tally? Count { get; init; }

    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}

public abstract record Block : ReportEntry
{
    public string? Collection { get; init; }

    public int Item { get; init; }
}

public sealed record TableBlock(string Name, IReadOnlyList<string> Columns,
                                IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
                                string? Caption = null, bool Unclipped = false,
                                IReadOnlyCollection<string>? Whole = null,
                                IReadOnlyDictionary<string, string?>? Given = null) : Block;

public sealed record DetailBlock(string Name, IReadOnlyList<KeyValuePair<string, object?>> Pairs) : Block;

public sealed record CompletenessBlock(string Name, string Scope,
                                       IReadOnlyList<(string Type, int Defs)> ByType,
                                       int Defs, string Verify) : Block;

public sealed record TextBlock(string Name, IReadOnlyList<string> Lines,
                               IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null) : Block;

public sealed class Report
{
    private readonly List<ReportEntry> _entries = [];
    private readonly List<string> _promised = [];

    public IReadOnlyList<ReportEntry> Entries => _entries;

    public bool TeachViaHook { get; set; }

    public IReadOnlyList<ReportEntry> Rendered
        => TeachViaHook ? _entries.Where(e => e is not Notice { Teach: true }).ToList() : _entries;

    public IReadOnlyList<Notice> Notices => Rendered.OfType<Notice>().ToList();
    public IReadOnlyList<Block> Blocks => _entries.OfType<Block>().ToList();

    public IReadOnlyList<string> Promised => _promised;

    public string Narrowing { get; set; } = "";

    private string Within => Narrowing.Length == 0 ? "" : $"（限于 {Narrowing}）";

    public string SnapshotTag { get; set; } = "";

    public HashSet<object> StaysEmpty { get; } = new(ReferenceEqualityComparer.Instance);

    public Report Promises(string tableName)
    {
        if (!_promised.Contains(tableName, StringComparer.Ordinal)) _promised.Add(tableName);
        return this;
    }

    public Report Notice(NoticeKind kind, string text, bool footnote = false, Tally? count = null,
                         IReadOnlyDictionary<string, object?>? data = null, bool teach = false)
    {
        _entries.Add(new Notice(kind, text.TrimEnd(), footnote) { Count = count, Data = data, Teach = teach });
        return this;
    }

    public Report NoticeOnce(NoticeKind kind, string text)
    {
        var t = text.TrimEnd();
        return _entries.Any(e => e is Notice n && n.Text == t) ? this : Notice(kind, t);
    }

    public int Mark => _entries.Count;

    public Report InsertNotice(int at, NoticeKind kind, string text)
    {
        _entries.Insert(at, new Notice(kind, text.TrimEnd()));
        return this;
    }

    private readonly List<(int At, NoticeKind Kind, Func<string?> Text)> _late = [];

    public Report LateNotice(NoticeKind kind, Func<string?> text)
    {
        _late.Add((_entries.Count, kind, text));
        return this;
    }

    private (Notice Notice, IReadOnlyList<string> Mods)? _deferred;

    private readonly List<(TableBlock Table, IReadOnlyList<string> Mods)> _deferredTables = [];

    public Report DeferredNotice(NoticeKind kind, string text, IReadOnlyList<string> aboutMods)
    {
        var notice = new Notice(kind, text, Footnote: true);
        _deferred = (notice, aboutMods);
        _entries.Add(notice);
        return this;
    }

    public Report DeferredTable(string name, IReadOnlyList<string> columns,
                                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
                                IReadOnlyList<string> aboutMods)
    {
        var table = new TableBlock(name, columns, rows, null, true);
        _deferredTables.Add((table, aboutMods));
        _entries.Add(table);
        return this;
    }

    public void Settle()
    {
        foreach (var (at, kind, text) in Enumerable.Reverse(_late).ToList())
            if (text() is { } t) InsertNotice(at, kind, t);
        _late.Clear();

        if (_deferred is { } d)
        {
            _deferred = null;
            var at = _entries.IndexOf(d.Notice);
            if (at >= 0 && !ProvablyUnrelated(d.Mods))
                _entries[at] = d.Notice with { Footnote = false };
        }

        var placed = _deferredTables.Select(t => (t.Table, Unrelated: ProvablyUnrelated(t.Mods))).ToList();
        _deferredTables.Clear();
        var front = 0;
        foreach (var (table, unrelated) in placed)
        {
            if (!_entries.Remove(table)) continue;
            if (unrelated) _entries.Add(table);
            else _entries.Insert(front++, table);
        }
    }

    private static readonly string[] ModKeys = ["declared_in", "declaredIn"];

    private bool ProvablyUnrelated(IReadOnlyList<string> mods)
    {
        var withMod = 0;

        foreach (var block in _entries.OfType<Block>())
        {
            if (ModCells(block) is not { } cells) continue;
            withMod++;
            if (cells.Any(c => mods.Contains(c, StringComparer.OrdinalIgnoreCase))) return false;
        }

        return withMod > 0;
    }

    private static IReadOnlyList<string>? ModCells(Block block) => block switch
    {
        TableBlock t when t.Rows.Count > 0 && t.Columns.Any(c => ModKeys.Contains(c, StringComparer.Ordinal)) =>
            [.. t.Rows.Select(r => ModKeys.Select(k => r.GetValueOrDefault(k)).FirstOrDefault(v => v is not null)?.ToString() ?? "")],
        DetailBlock d when d.Pairs.Any(p => ModKeys.Contains(p.Key, StringComparer.Ordinal)) =>
            [.. d.Pairs.Where(p => ModKeys.Contains(p.Key, StringComparer.Ordinal)).Select(p => p.Value?.ToString() ?? "")],
        _ => null,
    };

    public Report CountNotice(Tally tally, string noun, string fact = "")
        => tally.IsTruncated
            ? Notice(NoticeKind.Truncation,
                     tally.RenderTotalFirst(noun, aside: Within) +
                     (fact.Length == 0 ? "。" : $"；{fact}"), count: tally)
            : Notice(NoticeKind.Count, $"{tally.Render(noun, aside: Within)}。", count: tally);

    public Report PageNotice(string noun, int shown, int offset, int total, string scope = "")
    {
        var seen = offset + shown;
        var tally = shown < total ? Tally.Of(shown, total) : Tally.Complete(shown);
        return Notice(tally.IsTruncated ? NoticeKind.Truncation : NoticeKind.Count,
            (tally.IsTruncated
                ? tally.RenderTotalFirst(noun, offset == 0, scope, Within)
                : tally.Render(noun, scope, Within)) +
            (offset > 0 ? Cjk.Join("，从第", (offset + 1).ToString(), NounRegistry.Measure(noun), "起") : "") +
            (seen >= total && offset > 0 ? "；这是最后一页。" : "。"), count: tally);
    }

    public Report PastEnd(int offset, string rest)
        => Notice(NoticeKind.NextStep, $"--offset {offset} 已超出末尾：{rest}");

    public Report TruncationNotice(Tally tally, string noun)
    {
        if (!tally.IsTruncated) return this;
        return Notice(NoticeKind.Truncation,
            $"{tally.RenderTotalFirst(noun)}。", count: tally);
    }

    private string? _collection;
    private int _item = -1;

    public Report Item(string collection)
    {
        if (_collection != collection) { _collection = collection; _item = 0; }
        else _item++;
        return this;
    }

    public Report EndItems() { _collection = null; _item = -1; return this; }

    public Report Add(Block block)
    {
        _entries.Add(_collection is null ? block : block with { Collection = _collection, Item = _item });
        return this;
    }

    public Report Table(string name, IReadOnlyList<string> columns,
                        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? caption = null,
                        bool unclipped = false, IReadOnlyCollection<string>? whole = null,
                        IReadOnlyDictionary<string, string?>? given = null)
        => Add(new TableBlock(name, columns, rows, caption, unclipped, whole, given));

    public Report Detail(string name, IReadOnlyList<KeyValuePair<string, object?>> pairs)
        => Add(new DetailBlock(name, pairs));

    public bool Absent(params IReadOnlyList<Snapshot.LayerRow> rows)
    {
        var missing = rows.Where(r => !r.Complete).ToList();
        if (missing.Count == 0) return false;
        Table(AbsentTable, ["layer", "state", "next"], Snapshot.DataLayers.Rows(missing, withWhy: false),
              unclipped: true);
        return true;
    }

    public const string AbsentTable = "absent";

    public bool EmptyBecause(EmptyCause cause, string unit = "def") => EmptyBecause([cause], unit);

    public bool EmptyBecause(IReadOnlyList<EmptyCause> causes, string unit = "def")
    {
        var rows = causes.Where(c => c.Hidden > 0)
                         .Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                         {
                             ["filter"] = c.Filter,
                             ["hidden"] = c.Hidden,
                             ["next"] = c.Next,
                         }).ToList();
        if (rows.Count == 0) return false;
        AppendRows(EmptyBecauseTable, ["filter", "hidden", "next"], rows, caption: EmptyBecauseCaption(unit));
        return true;
    }

    public const string EmptyBecauseTable = "empty_because";

    public void AppendRows(string name, IReadOnlyList<string> columns,
                           IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? caption = null,
                           IReadOnlyCollection<string>? whole = null)
    {
        if (rows.Count == 0) return;
        var at = _entries.FindIndex(e => e is TableBlock t && t.Name == name &&
                                         t.Collection == _collection && (_collection is null || t.Item == _item));
        if (at >= 0)
        {
            var t = (TableBlock)_entries[at];
            _entries[at] = t with { Rows = [.. t.Rows, .. rows] };
            return;
        }
        Table(name, columns, rows, caption: caption, unclipped: whole is null, whole: whole);
    }
    public static string EmptyBecauseCaption(string unit)
        => Cjk.Join("无结果。去掉下面任一选项后返回的", NounRegistry.Noun(unit), "数：");

    public Report Text(string name, IReadOnlyList<string> lines,
                       IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows = null)
        => Add(new TextBlock(name, lines, rows));
}

public readonly record struct Qualified(object? Value, string Note);

public static class OutputText
{
    public const string Newline = "\n";

    public const string NoticeMark = "  - ";

    public static string Finish(string s)
        => s.Replace("\r\n", Newline).Replace('\r', '\n').TrimEnd('\n', ' ', '\t') + Newline;

    public static string Join(IEnumerable<string> lines)
        => Finish(string.Join(Newline, lines));

    public static string Cell(object? v) => v switch
    {
        null => "",
        Qualified q => q.Value is null ? "" : $"{Cell(q.Value)}（{q.Note}）",
        bool b => b ? "是" : "否",
        string s => s.Replace("\r", "").Replace("\n", " "),
        _ => v.ToString() ?? "",
    };

    public static string Truncate(string s, int max)
    {
        if (Width(s) <= max) return s;
        var sb = new StringBuilder();
        var w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var rw = RuneWidth(r.Value);
            if (w + rw > max - 1) break;
            sb.Append(r);
            w += rw;
        }
        return sb.Append('…').ToString();
    }

    public static int Width(string s)
    {
        var w = 0;
        foreach (var r in s.EnumerateRunes()) w += RuneWidth(r.Value);
        return w;
    }

    private static int RuneWidth(int c) => c switch
    {
        >= 0x0300 and <= 0x036F => 0,
        >= 0x200B and <= 0x200F => 0,
        >= 0x1100 and <= 0x115F => 2,
        >= 0x2E80 and <= 0x303E => 2,
        >= 0x3041 and <= 0x33FF => 2,
        >= 0x3400 and <= 0x4DBF => 2,
        >= 0x4E00 and <= 0x9FFF => 2,
        >= 0xA000 and <= 0xA4CF => 2,
        >= 0xAC00 and <= 0xD7A3 => 2,
        >= 0xF900 and <= 0xFAFF => 2,
        >= 0xFE30 and <= 0xFE6F => 2,
        >= 0xFF00 and <= 0xFF60 => 2,
        >= 0xFFE0 and <= 0xFFE6 => 2,
        >= 0x1F300 and <= 0x1FAFF => 2,
        >= 0x20000 and <= 0x3FFFD => 2,
        _ => 1,
    };
}
