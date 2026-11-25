using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class FieldsCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("字段路径");

    private static readonly CliOption PathContains = CliOption.Repeat("path-contains") with
    {
        Aliases = ["filter", "grep", "path"],
        BarMeansOr = true,
        Placeholder = "<文本>",
        Help = "只列含此文本的字段路径。重复给出或写成 'A|B' 以放宽筛选。" +
               CommonOptions.AnyIndexNote,
        Narrows = true,
    };

    private static readonly CliOption ExactPath = CommonOptions.ExactPathFilter();

    private static readonly CliOption OffsetOption = CommonOptions.Offset("字段路径");

    public override CliOperation Operation { get; } = new()
    {
        Name = "fields",
        Aliases = ["paths", "schema"],
        Summary = "列出某个 def 类型实际用到的字段路径，及各自出现在多少个 def 上。",
        Remarks =
            "用于找出交给 where 的字段路径。计数表明该类型普遍都有这条路径，" +
            "还是只有少数 def 有。\n\n" +
            "列出的是导出器记录到值的每条路径。该类型声明的字段集按有限的嵌套深度展开，" +
            "没有匹配的路径时注明展开到多深；嵌套更深的字段在测量范围之外。",
        Positionals =
        [
            new CliPositional
            {
                Name = "defType",
                Variadic = true,
                Help = "def 类型，如 ThingDef。一次可给多个类型；--limit 与 --offset 对每个类型分别生效，" +
                       "每个类型各有一行计数，「def 类型」列注明各行来自哪个类型。",
            },
        ],
        Options =
        [
            LimitOption,
            PathContains,
            ExactPath,
            OffsetOption,
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path-contains comps",
            "rimsearcher fields HediffDef",
        ],
        JsonKeys =
        [
            new("fields",
                "每条字段路径一行：path、defs（用到该字段路径的 def 数）、def_type（该行计入哪个类型，单类型调用也有）。",
                Rows: true),
            Completeness.JsonKey,
            IndexGap.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit(LimitOption);
        var (filters, exactPath) = ctx.Args.PathFilters(PathContains, ExactPath);
        var pathSpelling = exactPath ? "--exact-path" : "--path-contains";
        var offset = ctx.Args.Offset(OffsetOption);

        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ctx.Args.Positionals)
            if (seen.Add(t)) asked.Add(t);

        var table = new List<IReadOnlyDictionary<string, object?>>();
        var listed = new List<string>();
        foreach (var type in asked)
            if (RunOne(ctx, type, limit, filters, offset, exactPath, table) == 0) listed.Add(type);

        if (listed.Count == 0) return 1;

        var cut = listed.Select(ctx.Db.TruncatedDefsOfType).ToList();
        var byType = cut.SelectMany(s => s.ByType).ToList();
        if (byType.Count > 0)
            Completeness.NoteIndexedPathsOnly(ctx, new TruncationScope(cut.Sum(s => s.Count), byType),
                $"{NameList.Render(listed, listed.Count)} 的全部 def，与 {pathSpelling} 无关");

        ctx.Report.Table("fields", ["path", "defs", "def_type"], table,
            given: new Dictionary<string, string?> { ["def_type"] = asked.Count == 1 ? asked[0] : null });
        return 0;
    }

    private static int RunOne(CommandContext ctx, string type, LimitValue limit,
                              IReadOnlyList<string> filters, int offset, bool exactPath,
                              List<IReadOnlyDictionary<string, object?>> table)
    {
        var (rows, total, whole) = ctx.Db.FieldPathsForType(type, limit.Effective, filters, offset, exactPath);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"共匹配 {Tally.Complete(total).Render("field path")}。");
                return 1;
            }

            if (filters.Count > 0 && ctx.Db.FieldPathsForType(type, 1).Rows.Count > 0)
            {
                if (exactPath)
                {
                    var relaxed = ctx.Db.FieldPathsForType(type, Limits.MaxSuggestions, filters);
                    if (relaxed.Rows.Count > 0)
                    {
                        var shown = relaxed.Rows.Select(r => r.Path).ToList();
                        ctx.Report.Notice(NoticeKind.NextStep,
                            $"{type} 上没有恰好为 {PathFilterText.Say(filters)} 的字段路径。" +
                            "改按子串匹配：" +
                            string.Join("、", shown) +
                            (relaxed.Total > shown.Count
                                ? Cjk.Join("，另有", Tally.Complete(relaxed.Total - shown.Count).Render("field path"), "未列出")
                                : "") +
                            "。其中任一条都可直接传回 --exact-path。");
                        return 1;
                    }
                }

                var declared = ctx.Db.TypeDeclaredPaths(type, filters, exactPath);
                if (declared.Count > 0)
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        Cjk.Join(type, "声明", Tally.Complete(declared.Count).Render("field path",
                                     scope: Cjk.Join("匹配", PathFilterText.Say(filters)))) +
                        "；本快照没有记录这些路径上的任何值。");
                    IndexGap.Say(ctx, string.Join(", ", filters), IndexGap.NullOrEmptyOnType,
                                 Completeness.DeclarationSearch(filters[0]));
                    return 1;
                }
                var (valueAxisChecked, asValue) = FilterIsValue(ctx, type, filters, exactPath);

                var notAValue = valueAxisChecked && asValue is null
                    ? $"{type} 的 def 也都不以 {PathFilterText.Say(filters)} 为值。"
                    : "";

                {
                    var reach = ctx.Db.TypeDeclaredPathMaxSegments(type);
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"{type} 有字段路径，但没有一条含 {PathFilterText.Say(filters)}，" +
                        "该类型自身声明的字段也都不匹配" +
                        (asValue is null && reach is { } n ? $"（声明的字段展开到 {n} 段深）。" : "。") +
                        notAValue);
                    if (asValue is not null) SayValueNotPath(ctx, type, filters[0], asValue);
                    else IndexGap.Say(ctx, string.Join(", ", filters), IndexGap.Undeclared,
                                      Completeness.DeclarationSearch(filters[0]));
                    return 1;
                }
            }
            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(ctx.Scope()).Select(t => t.Type), "fields"));
            if (DefTypeMiss.InSourceInstead(ctx, type) is { } inSource)
                ctx.Report.Notice(NoticeKind.NextStep, inSource);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total, $"{type} 上");

        if (filters.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                PathFilterSummary.Say(filters, $"{type} 上", "field path",
                                      total, ctx.Db.FieldPathsForType(type, 1).Total, whole));

        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["defs"] = r.Count,
                ["def_type"] = type,
            });
        return 0;
    }

    private static (bool Checked, string? Hit) FilterIsValue(CommandContext ctx, string type,
                                                             IReadOnlyList<string> filters, bool exactPath)
    {
        if (filters.Count != 1) return (false, null);
        var text = filters[0];

        var rows = ctx.Db.PathsWithValue(text, ctx.Scope(), 64, Storage.ValueMatch.Identifier, 0, type).Rows;
        if (rows.Count == 0) return (true, null);

        var shapes = rows.Select(r => Search.PathSegments.Shape(r.Path))
                         .Distinct(StringComparer.Ordinal)
                         .ToList();
        var shown = shapes.Take(Limits.MaxSuggestions).ToList();
        var defs = rows.Sum(r => r.Defs);

        return (true, $"'{text}' 是 {type} 上的值：该值位于 " +
               string.Join("、", shown) +
               (shapes.Count > shown.Count
                   ? Cjk.Join("，另有", Tally.Complete(shapes.Count - shown.Count).Render("path shape"), "未列出")
                   : "") +
               $"，涉及 {Tally.Complete(defs).Render("def")}。");
    }

    private static void SayValueNotPath(CommandContext ctx, string type, string text, string fact)
    {
        ctx.Report.Notice(NoticeKind.NextStep, fact);
        IndexGap.Say(ctx, text, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {text} --type {type}");
    }
}
