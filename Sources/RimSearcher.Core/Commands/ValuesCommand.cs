using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class ValuesCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("值");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("值");

    private static readonly CliOption Field = CliOption.Repeat("field") with
    {
        Placeholder = "<路径>",
        Help = "字段路径。'rimsearcher values ThingDef --field techLevel' 等同于 " +
               "'rimsearcher values techLevel --type ThingDef'。",
    };

    private static readonly CliOption ExactPath = CommonOptions.ExactPath();

    private static readonly CliOption PathContains = CommonOptions.PathContainsBeside();

    public override CliOperation Operation { get; } = new()
    {
        Name = "values",
        Aliases = ["distinct"],
        Summary = "列出字段的不同取值，最常见的在前。",
        Positionals =
        [
            new CliPositional
            {
                Name = "fieldPath",
                Variadic = true,
                Option = Field,
                Help = "字段路径或其末段，如 compClass。" + CommonOptions.AnyIndexNote +
                       EntryKeys.HelpNote +
                       "'--field' 是同一参数的另一种写法。一次可给多条路径；" +
                       "--limit 与 --offset 对每条路径分别生效，每条路径各有一行计数，各有一个以「所查」行开头的块，" +
                       "「字段路径」列注明各行来自哪条路径。",
            },
        ],
        Options =
        [
            LimitOption, OffsetOption, CommonOptions.Scope,
            CommonOptions.Type,
            Field,
            ExactPath,
            PathContains,
        ],
        Examples =
        [
            "rimsearcher values compClass",
            "rimsearcher values expandingIconTexture --type WorldObjectDef",
            "rimsearcher values thingClass --scope vanilla",
            "rimsearcher values \"statBases[stat=Mass].value\" --type ThingDef",
        ],
        JsonKeys =
        [
            new("values",
                "每个不同取值一行：value、defs、field_path（该行计入所查路径中的哪一条，单路径调用也有）。",
                Rows: true),
            new("field",
                "数组，每条所查字段路径一项，按给出顺序；每项含一个 field 对象（field[0].field），" +
                "与 get 的 defs[] 同样嵌套。该对象说明所查路径，以及取值来自哪些完整路径与 def 类型：" +
                "asked、matched_paths、def_types、defs_with_field。" +
                "只查一条路径时也是数组。恒出现：结果为空时 asked 保留所给路径，matched_paths 与 def_types 为空，" +
                "defs_with_field 为 0。"),
            EmptyCause.JsonKey,
            Completeness.JsonKey,
            NameLookup.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit(LimitOption);
        var scope = ctx.Scope();
        var type = ctx.Args.Value(CommonOptions.Type);
        var offset = ctx.Args.Offset(OffsetOption);

        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ctx.Args.Positionals)
            if (seen.Add(p)) asked.Add(p);
        foreach (var p in asked) EntryKeys.Validate(p);

        var table = new List<IReadOnlyDictionary<string, object?>>();
        var listed = new List<(string Asked, IReadOnlyList<(string DefType, int Count)> DefTypes)>();
        foreach (var one in asked)
            RunOne(ctx, one, limit, scope, type, offset, table, listed);
        ctx.Report.EndItems();

        if (listed.Count == 0) return 1;

        var types = listed.SelectMany(l => l.DefTypes.Select(d => d.DefType))
                          .Distinct(StringComparer.Ordinal).ToList();
        var byType = types.SelectMany(t => ctx.Db.TruncatedDefsOfType(t).ByType)
                          .GroupBy(t => t.Type, StringComparer.Ordinal)
                          .Select(g => (Type: g.Key, Defs: g.Max(t => t.Defs)))
                          .ToList();
        if (byType.Count > 0)
            Completeness.NoteIndexedPathsOnly(ctx, new TruncationScope(byType.Sum(t => t.Defs), byType),
                type is { Length: > 0 }
                    ? $"{type} 的全部 def"
                    : "带有" + (listed.Count > 1 ? "这些路径中任一条" : "这条路径") + "的每个 def 类型");

        ctx.Report.Table("values", ["value", "defs", "field_path"], table,
            given: new Dictionary<string, string?> { ["field_path"] = asked.Count == 1 ? asked[0] : null });
        return 0;
    }

    private static void RunOne(CommandContext ctx, string path, LimitValue limit, ScopeFilter scope,
                               string? type, int offset,
                               List<IReadOnlyDictionary<string, object?>> table,
                               List<(string Asked, IReadOnlyList<(string DefType, int Count)> DefTypes)> listed)
    {
        var pq = new PathQuery(path, ctx.Args.Flag(ExactPath), Contains: ctx.Args.Values(PathContains));
        var (rows, total) = ctx.Db.DistinctValues(pq, scope, limit.Effective, type, offset);

        string? rewritten = null;
        if (total == 0 && pq.CanTolerateIndex)
        {
            var tolerant = pq with { IndexTolerant = true };
            var retry = ctx.Db.FieldPathExists(tolerant, scope)
                ? ctx.Db.DistinctValues(tolerant, scope, limit.Effective, type, offset)
                : ([], 0);
            if (retry.Total > 0)
            {
                rewritten = $"按原样给出的 {path} 之下没有值，本次查询放开该下标。" +
                            "实际并入的路径见下方「匹配的字段路径」行，其中任一条都可配 --exact-path 传回。";
                pq = tolerant;
                (rows, total) = retry;
            }
        }
        if (total == 0 && EntryKeys.ReadAs(ctx, pq, scope, type) is { } keyedPath)
        {
            rewritten = $"'{path}'{ReadCommand.ReadAsPhrase}'{keyedPath}'。";
            path = keyedPath;
            pq = pq with { Text = keyedPath };
            (rows, total) = ctx.Db.DistinctValues(pq, scope, limit.Effective, type, offset);
        }

        void NoteRewrite()
        {
            if (rewritten is null) return;
            ctx.Report.Notice(NoticeKind.NextStep, rewritten);
            rewritten = null;
        }

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"{path} 共有 {Tally.Complete(total).Render("value")}。");
                NoteRewrite();
                EmitFieldCoverage(ctx, path, pq, scope, type);
                return;
            }

            var withoutType = type is not null && ctx.Db.FieldPathExists(pq, scope);
            var wideScope = ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config);
            var outsideScope = !withoutType && !scope.IsAll && ctx.Db.FieldPathExists(pq, wideScope);

            var loose = pq.Exact && !withoutType && !outsideScope ? PathWithout.Existing(ctx, pq with { Exact = false }, scope) : null;
            var uncontained = !withoutType && !outsideScope && loose is null && pq.Contains is { Count: > 0 } &&
                              !ctx.Db.FieldPathExists(pq, scope)
                ? PathWithout.Existing(ctx, pq with { Contains = null }, scope) : null;
            var hiddenByContains = uncontained is { } u ? ctx.Db.FindByField(u, null, false, scope, 0, 0, type).Defs : 0;
            var selfFiltered = loose is not null || hiddenByContains > 0;

            var deeper = !withoutType && !outsideScope && !selfFiltered
                ? Completeness.ValuesLiveDeeper(ctx, path, scope) : null;
            var keyed = withoutType || outsideScope || selfFiltered || deeper is not null
                ? null : EntryKeys.ValuesHint(ctx, path, scope) ?? EntryKeys.TailHint(ctx, "values", path, scope, type);
            var keyMissing = keyed is null && deeper is null && !withoutType && !outsideScope && !selfFiltered &&
                             EntryKeys.SayKeyMissing(ctx, pq, scope, type);
            var beyond = withoutType || outsideScope || selfFiltered || keyMissing || keyed is not null
                ? [] : NameLookup.Beyond(ctx, path, NameLookup.Side.Data, type);

            if (loose is { } l)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ExactPath),
                    ctx.Db.FindByField(l, null, false, scope, 0, 0, type).Defs, ctx.Without(ExactPath)));
            else if (hiddenByContains > 0)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(PathContains), hiddenByContains,
                                                       ctx.Without(PathContains)));
            else if (withoutType)
            {
                if (ctx.UnknownDefType(type) is { } unknown) ctx.Report.Notice(NoticeKind.Filter, unknown);
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Type),
                    ctx.Db.FindByField(pq, null, false, scope, 0, 0).Defs, ctx.Without(CommonOptions.Type)));
            }
            else if (outsideScope)
                OutsideScope.Say(ctx, pq, type);
            else if (!keyMissing)
                ctx.Report.Notice(NoticeKind.NextStep,
                              $"本快照中没有 def 带以 {path} 结尾的字段路径" +
                              (scope.IsAll ? "" : $"（--scope {scope.Expression} 之外也没有）") + "。");
            if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
            else if (keyed is not null) ctx.Report.Notice(NoticeKind.NextStep, keyed);
            NameLookup.Say(ctx, beyond);

            if (ctx.Json) ctx.Report.Item("field").Detail("field", [
                new("asked", path),
                new("matched_paths", ""),
                new("def_types", ""),
                new("defs_with_field", (object)0),
            ]);
            return;
        }

        ctx.Report.PageNotice("value", rows.Count, offset, total, path);

        NoteRewrite();

        var cov = EmitFieldCoverage(ctx, path, pq, scope, type);

        if (cov.PathTotal > 1 && !pq.Exact && pq.Selector is { } sel)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{path} 匹配 {Tally.Complete(cov.PathTotal).Render("path shape")}，本表把它们合并计数。" +
                "把上面列出的任一形状写回方括号后，再配 --exact-path 传回，即只计那一种：" +
                $"'rimsearcher values {EntryKeys.Quote(sel.Reinsert(cov.Paths[0].Path))} --exact-path'。",
                footnote: true);
        else if (cov.PathTotal > 1 && !pq.Exact)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{path} 的值来自合并在一起的 {Tally.Complete(cov.PathTotal).Render("field path")}。" +
                "上面列出的任一路径配 --exact-path 传回，即只合并那一条。",
                footnote: true);

        ctx.AnnounceExcluded(scope, rest => ctx.Db.DistinctValues(pq, rest, 0, type, 0).Total,
                             "value", path);

        listed.Add((path, cov.DefTypes));

        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["value"] = r.Value,
                ["defs"] = r.Count,
                ["field_path"] = path,
            });
    }

    private static (IReadOnlyList<(string Path, int Count)> Paths, int PathTotal,
                    IReadOnlyList<(string DefType, int Count)> DefTypes, int DefsCovered)
        EmitFieldCoverage(CommandContext ctx, string asked, PathQuery pq, ScopeFilter scope, string? type)
    {
        var cov = ctx.Db.ValueCoverage(pq, scope, Limits.MaxSuggestions, type);

        var pathList = NameList.Render([.. cov.Paths.Select(x => $"{x.Path}（{x.Count}）")], cov.Paths.Count,
                                       cov.PathTotal);

        if (pq.Selector is not null)
        {
            var shapes = ctx.Db.FindPathShapes(pq, null, false, scope, type);
            pathList = NameList.Render([.. shapes.Select(x => $"{x.Shape}（{x.Count}）")], Limits.MaxSuggestions);
            cov.Paths = shapes;
            cov.PathTotal = shapes.Count;
        }

        var typeList = string.Join("、", cov.DefTypes.Select(x =>
            $"{x.DefType}（{Tally.Of(x.Count, ctx.Db.CountDefsOfType(x.DefType, scope)).Render("def")}）"));

        ctx.Report.Item("field").Detail("field", [
            new("asked", asked),
            new("matched_paths", pathList),
            new("def_types", typeList),
            new("defs_with_field", (object)cov.DefsCovered),
        ]);
        return cov;
    }
}
