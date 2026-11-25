using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

internal static class Advisory
{
    public static void NoteValueAuthorship(CommandContext ctx, PathQuery own, string? value, bool exact,
                                           Snapshot.ScopeFilter scope, string? defType)
    {
        if (value is not { Length: > 0 }) return;
        if (!ClassNameShape.Looks(value) || ClassNameShape.FromGameCode(value)) return;

        var mods = ctx.Db.FindModsHolding(own, value, exact, scope, defType);
        if (mods.Count == 0) return;

        var official = ScopeFilter.Parse("vanilla", ctx.Db.PackageIds(), ctx.Config);
        var onOfficial = mods.Where(m => official.Includes(m.Mod)).ToList();
        if (onOfficial.Count == 0) return;

        ctx.Report.Notice(NoticeKind.Boundary,
            $"'{value}' 出现在 {Tally.Complete(onOfficial.Sum(m => m.Defs)).Render("def")} 上，这些 def 声明于原版 mod。");
    }

    public static void NoteTextIndexHasIt(CommandContext ctx, string value, string reachedBy = "--value")
    {
        var wide = ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config);
        var (rows, total) = ctx.Db.SearchFts(value, wide, null, Limits.MaxSuggestions);
        if (rows.Count == 0) return;

        ctx.Report.Notice(NoticeKind.NextStep,
            $"文本索引里有 '{value}'：" +
            NameList.Render([.. rows.Select(r => $"{r.DefName}（{r.DefType}）")], Limits.MaxSuggestions,
                            total: total) +
            $"。字段值只有本快照的语言（{ctx.Db.Meta.Language}）；" +
            $"'rimsearcher search {Quote(value)}' 查得到，{reachedBy} 查不到。");
    }

    internal static string Quote(string v) => v.Contains(' ') ? $"\"{v}\"" : v;

    public static void NoteValueElsewhere(CommandContext ctx, PathQuery own, string? value, bool exact,
                                          Snapshot.ScopeFilter scope, int here)
    {
        if (value is not { Length: > 0 }) return;
        if (ctx.Db.ValueElsewhere(own, value, exact ? ValueMatch.Exact : ValueMatch.Substring,
                                  scope) is not { } el) return;
        var others = el.CrossTypeShapes > 0
            ? Cjk.Join("，其中", Tally.Complete(el.CrossTypeShapes).Render("path shape"), "在",
                       string.Join("/", el.Types.OrderBy(t => t, StringComparer.Ordinal)), "以外的 def 类型上")
            : "";
        var lone = el.Shown.Select(s => s.Types).Distinct(StringComparer.Ordinal).ToList();
        var oneType = lone.Count == 1 && el.OtherShapes == el.Shown.Count;
        ctx.Report.Notice(NoticeKind.Boundary,
            Cjk.Join($"'{Quote(value)}' 在本路径之外还出现在", Tally.Complete(el.OtherShapes).Render("path shape"), "上") +
            others + (oneType ? $"，全在 {lone[0]} 上" : "") + "：" +
            string.Join("、", el.Shown.Select(s => oneType ? $"{s.Shape}（{s.Defs}）" : $"{s.Shape}（{s.Defs}，{s.Types}）")) +
            (el.OtherShapes > el.Shown.Count
                ? Cjk.Join("，另有", Tally.Complete(el.OtherShapes - el.Shown.Count).Render("path shape"), "未列出")
                : "") + "。");
    }

    public static void NoteSubstringWidened(CommandContext ctx, PathQuery own, string? value, bool exact,
                                            Snapshot.ScopeFilter scope, int here, string? defType = null)
    {
        if (exact || value is not { Length: > 0 } || here == 0) return;
        var strict = ctx.Db.FindByField(own, value, true, scope, 0, 0, defType).Defs;
        if (strict == here) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            strict == 0
                ? $"这里没有恰好为 '{Quote(value)}' 的值：每一行都是在更长的值里包含所查值，见值列。"
                : $"这里的 {Tally.Complete(here).Render("def")} 中，{strict} 个恰好为 '{Quote(value)}'，" +
                  $"{here - strict} 个在更长的值里包含所查值（值列标明哪些）。");
    }

    public static void NoteMixedPathShapes(CommandContext ctx, IReadOnlyList<(string Shape, int Count)> shapes)
    {
        if (shapes.Count < 2) return;
        var shown = shapes.Take(shapes.Count == Limits.MaxSuggestions + 1
                                    ? shapes.Count : Limits.MaxSuggestions).ToList();
        ctx.Report.Notice(NoticeKind.Boundary,
            "这些行横跨不止一种路径形状：" +
            string.Join("、", shown.Select(x => $"{x.Shape}（{x.Count}）")) +
            (shapes.Count > shown.Count
                ? Cjk.Join("，另有", Tally.Complete(shapes.Count - shown.Count).Render("path shape"), "未列出")
                : "") +
            "。");
    }

    public static void NoteGeneratedDefs(CommandContext ctx, int generated, int defs, int onThisPage)
    {
        if (generated == 0) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            $"这里匹配的 {Tally.Complete(defs).Render("def")} 中有 {generated} 个在加载时由代码生成（写于=代码）。" +
            (onThisPage == 0 ? "本页一个都没有。" : ""));
    }

    private static readonly IEqualityComparer<(string, string DefType)> TupleComparer =
        EqualityComparer<(string, string DefType)>.Default;

    public static void NoteSameLabel(CommandContext ctx, IReadOnlyList<DefRow> rows)
    {
        var clashes = rows.Where(r => r.Label is { Length: > 0 })
                          .GroupBy(r => (r.Label!, r.DefType), TupleComparer)
                          .Where(g => g.Count() > 1)
                          .ToList();
        if (clashes.Count == 0) return;

        var shown = clashes.Take(Limits.MaxSuggestions)
                           .Select(g => $"'{g.Key.Item1}'（{g.Key.DefType}：" +
                                        $"{NameList.Render([.. g.Select(r => r.DefName)], Limits.MaxSuggestions)}）")
                           .ToList();
        ctx.Report.Notice(NoticeKind.Advisory,
            "上面有几组行，组内 label 相同、def 类型也相同：" + string.Join("；", shown) +
            (clashes.Count > shown.Count ? $"，另有 {clashes.Count - shown.Count} 组行，组内 label 相同、def 类型也相同" : "") +
            "。",
            footnote: true);
    }

    public static void NoteAuthoredSiblings(CommandContext ctx,
                                            IEnumerable<(string DefName, long DefId, string Path)> shown,
                                            string? selectedBy = null)
    {
        var rows = shown.ToList();
        var names = ctx.Db.AuthoredSiblings(rows.Select(r => (r.DefId, r.Path)))
                       .Where(n => !string.Equals(n, selectedBy, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) return;

        var first = rows.FirstOrDefault(r => PathSegments.ContainerPrefix(r.Path) is not null);
        var block = first.Path is null ? null : PathSegments.ContainerPrefix(first.Path)?.TrimEnd('.');

        ctx.Report.Notice(NoticeKind.Advisory,
            $"与上面各行同块、值与新建实例不同的字段：{NameList.Render(names, Limits.MaxSuggestions)}。" +
            (block is null
                ? ""
                : $"整个 {block} 条目见 'rimsearcher get {first.DefName} --path-contains {block}'。"),
            footnote: true);
    }
}
