using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

internal static class PathFilterText
{
    public static string Say(IReadOnlyList<string> parts)
        => parts.Count == 1 ? $"'{parts[0]}'" : string.Join(" 或 ", parts.Select(p => $"'{p}'"));
}

internal static class OutsideScope
{
    public static void Say(CommandContext ctx, PathQuery pq, string? type)
    {
        var hidden = ctx.Db.FindByField(pq, null, false, ctx.Unscoped(), 0, 0, type).Defs;
        ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ctx.ScopeOption), hidden, ctx.Without(ctx.ScopeOption)));
    }
}

internal static class PathWithout
{
    public static PathQuery? Existing(CommandContext ctx, PathQuery q, ScopeFilter scope)
    {
        if (ctx.Db.FieldPathExists(q, scope)) return q;
        var tolerant = q with { IndexTolerant = true };
        return q.CanTolerateIndex && ctx.Db.FieldPathExists(tolerant, scope) ? tolerant : (PathQuery?)null;
    }
}

internal static class PathFilterSummary
{
    private const string Pin = "把字段路径列中的一条以 '--exact-path <该路径>' 传回，只保留那一条。";

    public static string Say(IReadOnlyList<string> filters, string subject, string noun,
                             int matched, int total, int whole, string whose = "")
        => Cjk.Join("匹配", PathFilterText.Say(filters), whose, "：", Tally.Complete(matched).Render(noun),
                    "，", subject, "共", Tally.Complete(total).Render(noun), "。") +
           (whole == 0
               ? $"其中没有一条以 {PathFilterText.Say(filters)} 为完整路径段：每处命中都是在更长的名字里包含它。" +
                 Pin
               : whole < matched
                   ? $"其中作为完整路径段的有 {Tally.Complete(whole).Render(noun)}，" +
                     $"嵌在更长名字里的有 {Tally.Complete(matched - whole).Render(noun)}。"
                   : "");
}
