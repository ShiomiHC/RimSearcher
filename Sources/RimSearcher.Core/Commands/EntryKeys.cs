using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

internal static class EntryKeys
{
    public const string HelpNote =
        "'[field=value]' 按列表条目自身的某个字段选取条目：'statBases[stat=Mass].value' 在每个带有该条目的 def 上" +
        "读取 stat 为 Mass 的 statBases 条目的 value，'costList[thingDef=Steel].count' 同理。" +
        "方括号后接要读取的字段，一条路径只接受一个 '[field=value]'。" +
        "没有路径以 'statBases.Mass' 结尾时，该写法读作 'statBases[stat=Mass].value'。";

    public static void Validate(string path)
    {
        EntrySelector.Parse(path, out var problem);
        if (problem is not null) throw new CliUsageException($"'{path}'：{problem}");
    }

    public static bool SayKeyMissing(CommandContext ctx, PathQuery pq, ScopeFilter scope, string? type)
    {
        if (pq.Selector is not { } sel) return false;
        if (!ctx.Db.FieldPathExists(pq with { Text = sel.BaseText }, scope)) return false;

        var names = ctx.Db.DistinctValues(pq with { Text = sel.KeyShape }, scope, Limits.ValueSpaceSample, type)
                          .Rows.Select(r => r.Value).ToList();
        var close = FuzzyMatcher.Rank(names, sel.Value).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
        ctx.Report.Notice(NoticeKind.NextStep,
            $"没有 {sel.Key} 为 '{sel.Value}' 的 {sel.Before}[] 条目" +
            (close.Count > 0 ? $"；这些条目中最接近的 {sel.Key}：{string.Join("、", close)}" : "") +
            $"。这些条目的全部 {sel.Key} 见 '{CommandRegistry.ExeName} values {sel.KeyShape}'。");
        return true;
    }

    public static (string Path, int Defs, IReadOnlyList<(string KeyShape, int Defs)> All)? AsEntryKey(
        CommandContext ctx, string name, ScopeFilter scope)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') ||
            !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
            return null;

        var shapes = ctx.Db.EntriesKeyedBy(name, scope);
        foreach (var (shape, defs) in shapes.Take(Limits.MaxSuggestions))
            if (SelectorFor(ctx, shape, name) is { } path)
                return (path, defs, shapes);
        return null;
    }

    public static string? SelectorFor(CommandContext ctx, string keyShape, string value)
    {
        var dot = keyShape.LastIndexOf('.');
        if (dot < 2 || !keyShape[..dot].EndsWith("[]", StringComparison.Ordinal)) return null;
        var entry = keyShape[..dot];
        var key = keyShape[(dot + 1)..];
        var others = ctx.Db.EntryLeaves(entry)
                           .Where(l => !string.Equals(l, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return others.Count == 1 ? $"{entry[..^2]}[{key}={value}].{others[0]}" : null;
    }

    public static string? ValuesHint(CommandContext ctx, string name, ScopeFilter scope)
    {
        if (AsEntryKey(ctx, name, scope) is not { } hit) return null;
        var (path, defs, all) = hit;
        var sel = EntrySelector.Parse(path, out _)!;
        var rest = all.Where(s => !string.Equals(s.KeyShape, sel.KeyShape, StringComparison.Ordinal)).ToList();
        return $"{name} 是 {sel.Before}[] 条目的 {sel.Key} 取值，出现在 " +
               $"{Tally.Complete(defs).Render("def")} 上。" +
               $"这些条目所带的值见 '{CommandRegistry.ExeName} values {Quote(path)}'，" +
               "以同一路径运行 where 则逐个 def 列出。" +
               (rest.Count == 0
                   ? ""
                   : "其他列表也以该名字为键值：" +
                     NameList.Render([.. rest.Select(s => $"{s.KeyShape}（{Tally.Complete(s.Defs).Render("def")}）")],
                                     Limits.MaxSuggestions) + "。");
    }

    public static (string Path, int Defs, bool Under)? TailAsKey(CommandContext ctx, string path, ScopeFilter scope, string? type)
    {
        if (TailParts(path) is not var (head, list, tail)) return null;
        var shapes = ctx.Db.EntriesKeyedBy(tail, scope);
        foreach (var (shape, _) in shapes)
        {
            if (!string.Equals(ListOf(shape), list, StringComparison.Ordinal) ||
                SelectorFor(ctx, shape, tail) is not { } full)
                continue;
            var open = full.LastIndexOf('[');
            foreach (var keyed in new[] { head + full[open..], full })
            {
                if (keyed == full && !Unindexed(full[..open]).EndsWith(Unindexed(head), StringComparison.Ordinal)) continue;
                var defs = ctx.Db.FindByField(new PathQuery(keyed), null, false, scope, 0, 0, type).Defs;
                if (defs > 0) return (keyed, defs, true);
            }
        }
        if (shapes.Any(x => string.Equals(ListOf(x.KeyShape), list, StringComparison.Ordinal))) return null;
        foreach (var (shape, _) in shapes.Take(Limits.MaxSuggestions))
            if (SelectorFor(ctx, shape, tail) is { } full &&
                ListOf(shape) + full[full.LastIndexOf('[')..] is var keyed &&
                ctx.Db.FindByField(new PathQuery(keyed), null, false, scope, 0, 0, type).Defs is var defs and > 0)
                return (keyed, defs, false);
        return null;
    }

    public static string? ReadAs(CommandContext ctx, PathQuery asked, ScopeFilter scope, string? type)
    {
        if (asked.Exact || TailParts(asked.Text) is null) return null;
        var literal = new PathQuery(asked.Text);
        if (ctx.Db.FieldPathExists(literal, ctx.Unscoped()) ||
            literal.CanTolerateIndex && ctx.Db.FieldPathExists(literal with { IndexTolerant = true }, ctx.Unscoped()))
            return null;
        return TailAsKey(ctx, asked.Text, scope, type) is (var keyed, _, true) ? keyed : null;
    }

    static string Unindexed(string path) => "." + System.Text.RegularExpressions.Regex.Replace(path, @"\[[^\]]*\]", "");

    public static string? TailHint(CommandContext ctx, string verb, string path, ScopeFilter scope, string? type)
    {
        if (TailAsKey(ctx, path, scope, type) is not var (keyed, defs, under)) return null;
        var sel = EntrySelector.Parse(keyed, out _)!;
        var list = ListOf(sel.KeyShape);
        return (under
                   ? $"{sel.Value} 是 {list}[].{sel.Key} 的取值："
                   : $"{sel.Value} 不是 {TailParts(path)!.Value.List} 下的字段，该列表也没有以该值为键值的条目；该值是 {list}[].{sel.Key} 的取值：") +
               $"'{CommandRegistry.ExeName} {verb} {Quote(keyed)}' 在 {Tally.Complete(defs).Render("def")} 上" +
               $"读取该条目的 {sel.After.TrimStart('.')}。";
    }

    public static string? TailListOnDef(IEnumerable<FieldRow> cells, string path)
    {
        if (TailParts(path) is not var (_, list, tail)) return null;
        return cells.Any(c => c.Value is { } v && string.Equals(v, tail, StringComparison.OrdinalIgnoreCase) &&
                              c.Path.LastIndexOf('.') is var dot and > 0 && c.Path[dot - 1] == ']' &&
                              string.Equals(ListOf(Search.PathSegments.Shape(c.Path)), list, StringComparison.Ordinal))
            ? list
            : null;
    }

    static (string Head, string List, string Tail)? TailParts(string path)
    {
        if (path.Contains('=')) return null;
        var cut = path.LastIndexOf('.');
        if (cut <= 0 || cut == path.Length - 1) return null;
        var head = path[..cut];
        if (head.EndsWith(']') && head.LastIndexOf('[') is var open and > 0) head = head[..open];
        var tail = path[(cut + 1)..];
        if (tail.IndexOfAny(['[', ']']) >= 0) return null;
        var list = head[(head.LastIndexOf('.') + 1)..];
        return list.Length == 0 || list.Contains('[') ? null : (head, list, tail);
    }

    static string ListOf(string keyShape)
    {
        var entry = keyShape[..keyShape.LastIndexOf('.')];
        if (entry.EndsWith("[]", StringComparison.Ordinal)) entry = entry[..^2];
        return entry[(entry.LastIndexOf('.') + 1)..];
    }

    public static string Quote(string path) => "\"" + path + "\"";
}
