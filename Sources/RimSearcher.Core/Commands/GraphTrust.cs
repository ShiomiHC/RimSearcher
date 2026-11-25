using RimSearcher.Metadata;
using RimSearcher.Output;

namespace RimSearcher.Commands;

internal sealed class GraphTrust
{
    private readonly Dictionary<(string Tree, string From), List<string>> _changed = [];

    public GraphTrust(CallGraphSet graphs, MetadataLookup lookup)
    {
        foreach (var g in graphs.Graphs)
        {
            var units = graphs.UnitReferences.GetValueOrDefault(g.Tree);
            var pairs = g.Edges.Select(e => (e.FromAssembly, e.ToAssembly))
                         .Concat((g.Accesses ?? []).Select(a => (a.FromAssembly, a.ToAssembly)))
                         .Distinct();
            foreach (var group in pairs.GroupBy(p => p.FromAssembly, StringComparer.OrdinalIgnoreCase))
            {
                var recorded = units?.GetValueOrDefault(group.Key);
                if (recorded is null) continue;
                var changed = new List<string>();
                foreach (var to in group.Select(p => p.ToAssembly).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!recorded.TryGetValue(to, out var built)) continue;
                    var copy = lookup.CopyFor(g.Tree, to);
                    IReadOnlyCollection<Guid> now = string.Equals(copy?.Tree, g.Tree, StringComparison.OrdinalIgnoreCase)
                        ? CallGraphFreshness.Mvid(copy!.Path) is { } own ? [own] : []
                        : lookup.Mvids(to);
                    if (now.Count > 0 && !now.Contains(built)) changed.Add(to);
                }
                if (changed.Count > 0) _changed[(g.Tree, group.Key)] = changed;
            }
        }
    }

    public bool Current(string tree, string fromAssembly, string toAssembly)
        => !_changed.TryGetValue((tree, fromAssembly), out var c) || !c.Contains(toAssembly, StringComparer.OrdinalIgnoreCase);

    public void Say(CommandContext ctx, IReadOnlyCollection<string>? into, IReadOnlyCollection<string> trees,
                    Func<string, string, bool> inScope)
    {
        var blind = _changed
            .Where(c => trees.Contains(c.Key.Tree, StringComparer.OrdinalIgnoreCase) && inScope(c.Key.Tree, c.Key.From))
            .Select(c => (c.Key.Tree, Asms: into is null ? c.Value
                                          : [.. c.Value.Where(a => into.Contains(a, StringComparer.OrdinalIgnoreCase))]))
            .Where(c => c.Asms.Count > 0)
            .GroupBy(c => c.Tree, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (blind.Count == 0) return;

        var asms = blind.SelectMany(b => b.SelectMany(c => c.Asms)).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase).ToList();
        ctx.Report.Notice(NoticeKind.Staleness,
            Cjk.Join(NameList.Render(asms, 4), "的当前版本与建调用图时的版本不匹配，因此指向这些程序集的调用与访问已略去。本答案在以下",
                     Tally.Complete(blind.Count).Render("source tree"), "上存在盲区：") +
            $"{NameList.Render([.. blind.Select(b => b.Key)], 6)}。");
    }
}
