using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class ListCommand : Command
{
    private const string ListColumns =
        "list 只输出名字。每个 def 一行并列出字段取值的表见 " +
        "'rimsearcher get --type <defType> --fields <路径>'。";

    private const string ListFieldFilter =
        "list 按名字（--find）或 Class（--class）选取 def。按字段取值选取 def 见 " +
        "'rimsearcher where <字段> <值>'：加 --type <defType> 只在一个 def 类型内查找，" +
        "不给值则列出带此字段的全部 def。";

    internal static (IReadOnlyList<string> Argv, string Note)? AsWhere(
        CliOperation list, CliOperation where, IReadOnlyList<CliOption> globals, IReadOnlyList<string> rest)
    {
        static CliOption? Find(IEnumerable<CliOption> opts, string name)
            => opts.FirstOrDefault(o => ArgText.Normalize(o.Name) == name ||
                                        o.Aliases.Any(a => ArgText.Normalize(a) == name));

        string? field = null, value = null;
        var hits = 0;
        var types = new List<string>();
        var kept = new List<string>();
        var shownToo = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            var t = rest[i];
            if (t == "--") return null;
            if (!t.StartsWith('-') || t.Length == 1 || char.IsAsciiDigit(t[1])) { types.Add(t); continue; }

            var body = t.TrimStart('-');
            var eq = body.IndexOf('=');
            var name = ArgText.Normalize(eq < 0 ? body : body[..eq]);
            string? Take() => eq >= 0 ? body[(eq + 1)..] : i + 1 < rest.Count ? rest[++i] : null;

            switch (name)
            {
                case "where" or "has":
                    if (field is not null || Take() is not { Length: > 0 } w) return null;
                    var at = name == "where" ? w.IndexOf('=') : -1;
                    field = (at < 0 ? w : w[..at]).Trim();
                    if (at >= 0) value = w[(at + 1)..].TrimStart('=').Trim();
                    hits++;
                    continue;
                case "field" or "fieldpath":
                    if (field is not null || Take() is not { Length: > 0 } f) return null;
                    field = f;
                    hits++;
                    continue;
                case "value":
                    if (value is not null || Take() is not { Length: > 0 } v) return null;
                    value = v;
                    hits++;
                    continue;
            }

            var spec = Find(globals, name) ?? (Find(where.Options, name) is { } o && Find(list.Options, name) is not null ? o : null);
            if (spec is null) return null;
            var from = kept.Count;
            kept.Add(t);
            if (spec.Kind != OptionKind.Flag && eq < 0 && i + 1 < rest.Count) kept.Add(rest[++i]);
            if (spec == GlobalOptions.Snapshot || !globals.Contains(spec)) shownToo.AddRange(kept.Skip(from));
        }

        if (hits == 0 || field is not { Length: > 0 } || types.Count > 1) return null;

        var asked = new List<string> { field };
        if (value is { Length: > 0 }) asked.Add(value);
        if (types.Count == 1) asked.AddRange(["--type", types[0]]);
        var shown = string.Join(" ", asked.Concat(shownToo).Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
        return ([.. asked, .. kept],
                $"list 按名字或 Class 选取 def；按字段取值选取 def 由 where 承担，" +
                $"因此以下为 '{CommandRegistry.ExeName} where {shown}' 的结果。");
    }

    private static readonly CliOption LimitOption = CommonOptions.Limit("def");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("def");

    private static readonly CliOption ClassOption = CliOption.Text("class") with
    {
        Aliases = ["own-class", "def-class", "runtime-class"],
        Placeholder = "<ClassName>",
        Help = "只列自身 Class 为此值的 def，值为类名或带命名空间的全名。不指定类型时在全部 def 类型中查。",
        Narrows = true,
    };

    private static readonly CliOption FindOption = CliOption.Text("find") with
    {
        Aliases = ["filter", "grep", "search", "match", "name-contains"],
        Placeholder = "<文本>",
        Help = "只列名字或 label 含此文本的 def。不指定类型时，只列名字含此文本的 def 类型。",
        Narrows = true,
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "list",
        Aliases = ["ls", "def-types"],
        Summary = "列出某个类型的全部 def；不指定类型时列出快照中的全部 def 类型。",
        Positionals =
        [
            new CliPositional
            {
                Name = "defType",
                NormalizeBackslashesInRefused = true,
                Required = false,
                Variadic = true,
                Help = "def 类型，如 ThingDef。一次可给多个；--limit 与 --offset 对每个类型分别生效，" +
                       "每个类型各有一行计数，「def 类型」列注明各行来自哪个类型。不指定类型、也不给 --class 时列出 def 类型本身" +
                       "及各自的 def 数，未指定 --limit 时全部列出。",
            },
        ],
        Options =
        [
            LimitOption,
            CommonOptions.Scope,
            OffsetOption,
            ClassOption,
            FindOption,
        ],
        Refused =
        [
            new() { Name = "field", Aliases = ["field-path"], ValuePlaceholder = "<字段>", Where = ListFieldFilter },
            new() { Name = "value", ValuePlaceholder = "<值>", Where = ListFieldFilter },
            new() { Name = "where", Aliases = ["has"], Where = ListFieldFilter },
            new() { Name = "values", Aliases = ["fields"], ValuePlaceholder = "<路径>", Where = ListColumns },
        ],
        Examples =
        [
            "rimsearcher list",
            "rimsearcher list HediffDef",
            "rimsearcher list GenStepDef --find scatter",
            "rimsearcher list CreepJoinerBaseDef --class CreepJoinerAggressiveDef",
            "rimsearcher list ThingDef --scope all,-vanilla",
        ],
        JsonKeys =
        [
            new("defs",
                "给出 def 类型或 --class 时：每个 def 一行，含 def_name、label、declared_in、def_type（该行来自所查类型中的哪一个，" +
                "单类型调用也有），某个类型含多个 Class 时另有 class。被其他 mod patch 过的 def 在 declared_in 中仍为声明它的 mod，--scope 筛的也是这一列。"),
            new("types",
                "两者都不给时：每个 def 类型一行，含 def_type、defs。"),
            EmptyCause.JsonKeyCounting("row"),
            NameLookup.JsonKey,
        ],
    };

    public override bool ReadsTranslations => true;

    public override int Run(CommandContext ctx)
    {
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ctx.Args.Positionals)
            if (seen.Add(t)) asked.Add(t);

        if (asked.Count == 0 && ctx.Args.Value(ClassOption) is { } cls)
        {
            ctx.Report.Promises("defs");
            asked = [.. ctx.Db.TypesHoldingClass(cls, ctx.Scope()).Select(t => t.DefType)];
            if (asked.Count == 0) return NoClass(ctx, cls);
        }
        else if (asked.Count == 0) return RunTypes(ctx);

        ctx.Report.Promises("defs");

        var table = new List<IReadOnlyDictionary<string, object?>>();
        var showClass = false;
        var listed = 0;
        foreach (var type in asked)
            if (RunDefs(ctx, type, table, ref showClass) == 0) listed++;

        if (listed == 0) return 1;

        ctx.Report.Table("defs",
            showClass
                ? ["def_name", "class", "label", "declared_in", "def_type"]
                : ["def_name", "label", "declared_in", "def_type"],
            table, given: new Dictionary<string, string?> { ["def_type"] = ctx.Args.Positionals.Count == 1 ? asked[0] : null });
        return 0;
    }

    private static int NoClass(CommandContext ctx, string cls)
    {
        var everywhere = ctx.Scope().IsAll ? 0 : ctx.Db.TypesHoldingClass(cls, ctx.Unscoped()).Sum(t => t.Count);
        if (everywhere > 0)
        {
            ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), everywhere, ctx.Without(CommonOptions.Scope)));
            return 1;
        }

        ctx.Report.Notice(NoticeKind.NextStep, $"本快照中没有 Class 为 '{cls}' 的 def。");
        if (ctx.Db.FindByField(new PathQuery("Class"), cls, false, ctx.Scope(), 0, 0).Defs is var nested and > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join(Tally.Complete(nested).Render("def"), "的字段用到该 Class") + $"：'{CommandRegistry.ExeName} where Class {cls}'。");
        else if (NameLookup.Locate(ctx, cls) is { } sighting) NameLookup.Say(ctx, sighting);
        return 1;
    }

    private static int RunTypes(CommandContext ctx)
    {
        if (ctx.Args.Offset(OffsetOption) > 0)
            throw new CliUsageException(
                "--offset 须配合 def 类型翻页。不给 def 类型时列出 def 类型本身。");

        var scope = ctx.Scope();
        ctx.Report.Promises("types");
        var everything = ctx.Db.Types(scope);

        var typeFind = ctx.Args.Value(FindOption);
        var all = typeFind is null
            ? everything
            : everything.Where(t => t.Type.Contains(typeFind, StringComparison.OrdinalIgnoreCase)).ToList();

        if (all.Count == 0 && everything.Count > 0)
        {
            ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(FindOption), everything.Count, ctx.Without(FindOption)), "def type");
            return 1;
        }

        if (all.Count == 0)
        {
            if (scope.IsAll)
                ctx.Report.Notice(NoticeKind.NextStep,
                    "本快照中没有任何 def。快照的导出时间见 'rimsearcher snapshot list'；" +
                    "重新导出快照用 'rimsearcher export'。");
            else
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope),
                    ctx.Db.Types(ctx.Unscoped()).Count, ctx.Without(CommonOptions.Scope)), "def type");
            return 1;
        }

        var limit = ctx.Limit(LimitOption);
        var rows = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        if (typeFind is not null)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"已按 --find '{typeFind}' 筛选；本快照共有 " +
                $"{Tally.Complete(everything.Count).Render("def type")}。");

        ctx.Report.CountNotice(Tally.Of(rows.Count, all.Count), "def type");

        ctx.AnnounceExcluded(scope, rest =>
        {
            var there = ctx.Db.Types(rest);
            return typeFind is null
                ? there.Count
                : there.Count(t => t.Type.Contains(typeFind, StringComparison.OrdinalIgnoreCase));
        }, "def type");

        ctx.Report.Table("types", ["def_type", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_type"] = r.Type,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }

    private static int RunDefs(CommandContext ctx, string type,
                               List<IReadOnlyDictionary<string, object?>> table, ref bool showClass)
    {
        var limit = ctx.Limit(LimitOption);
        var offset = ctx.Args.Offset(OffsetOption);
        var wantClass = ctx.Args.Value(ClassOption);
        var find = ctx.Args.Value(FindOption);
        var scope = ctx.Scope();

        var (rows, total) = ctx.Db.ListByType(type, scope, limit.Effective, offset, wantClass, find);

        if (rows.Count == 0)
        {
            var narrowed = Cjk.Join(wantClass is null ? "" : $"Class 为 {wantClass}",
                                    wantClass is not null && find is not null ? "、" : "",
                                    find is null ? "" : $"名字或 label 含 '{find}'");

            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset,
                    Cjk.Join("本快照有", Tally.Complete(total).Render("def", scope: Cjk.Join("def 类型", type, "下"), aside: narrowed.Length > 0 ? $"（{narrowed}）" : "")) + "。");
                return 1;
            }

            if (!scope.IsAll && offset == 0)
            {
                var (_, everywhere) = ctx.Db.ListByType(type, ctx.Unscoped(), 1, 0, wantClass, find);
                if (everywhere > 0)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), everywhere, ctx.Without(CommonOptions.Scope)));
                    return 1;
                }
            }

            if (find is not null && offset == 0)
            {
                var (_, unfiltered) = ctx.Db.ListByType(type, scope, 1, 0, wantClass);
                if (unfiltered > 0)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(FindOption), unfiltered, ctx.Without(FindOption)));
                    Advisory.NoteTextIndexHasIt(ctx, find, "--find");
                    return 1;
                }
            }

            if (wantClass is null && NameLookup.AsClass(ctx, type, scope) is { } asClass)
            {
                NameLookup.Say(ctx, asClass);
                return 1;
            }

            if (wantClass is not null)
            {
                var present = ctx.Db.ClassesInType(type, scope);
                if (present.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.NextStep,
                        DefTypeMiss.Say(type, ctx.Db.Types(scope).Select(t => t.Type), "list"));
                    return 1;
                }

                if (present.Count == 1)
                {
                    var nested = ctx.Db.FindByField(new PathQuery("Class"), wantClass, false, scope, 0, 0, type).Defs;
                    ctx.Report.Notice(NoticeKind.NextStep,
                        Cjk.Join(Tally.Complete(present[0].Count).Render("def", scope: Cjk.Join("def 类型", type, "下")),
                                 $"都属同一个 Class {present[0].Class}") +
                        "，因此 --class 无法区分它们" +
                        (nested > 0 ? $"；该 Class 用在字段中的 def 见 'rimsearcher where Class {wantClass} --type {type}'。" : "。"));
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ClassOption), present[0].Count, ctx.Without(ClassOption)));
                    return 1;
                }

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"def 类型 {type} 下没有 Class 为 '{wantClass}' 的 def。该类型含 " +
                    NameList.Render([.. present.Select(c => $"{c.Class}（{c.Count}）")], Limits.MaxSuggestions) + "。");
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ClassOption), present.Sum(c => c.Count), ctx.Without(ClassOption)));
                return 1;
            }

            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.Say(type, ctx.Db.Types(scope).Select(t => t.Type), "list"));
            return 1;
        }

        if (find is not null)
        {
            var (_, unfiltered) = ctx.Db.ListByType(type, scope, 1, 0, wantClass);
            ctx.Report.Notice(NoticeKind.Boundary,
                $"已按 --find '{find}' 筛选；" +
                Cjk.Join(type, "共有", Tally.Complete(unfiltered).Render("def",
                    scope: wantClass is null ? "" : Cjk.Join("Class 为", wantClass))) + "。");
        }

        ctx.Report.PageNotice("def", rows.Count, offset, total, $"def 类型 {type} 下");

        ctx.AnnounceExcluded(scope, rest => ctx.Db.ListByType(type, rest, 0, 0, wantClass, find).Total,
                             "def", $"def 类型 {type} 下");

        var classes = ctx.Db.ClassesInType(type, scope);
        var heterogeneous = wantClass is null && classes.Count > 1;
        showClass |= heterogeneous;
        if (heterogeneous)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("def 类型", type, "含", Tally.Complete(classes.Count).Render("def class")) + "：" +
                NameList.Render([.. classes.Select(c => $"{Tail(c.Class)}（{c.Count}）")], Limits.MaxSuggestions) +
                "。");

        foreach (var r in rows)
            table.Add(new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["class"] = r.Class is { } c ? Tail(c) : null,
                ["label"] = r.Label,
                ["declared_in"] = r.SourceMod,
                ["def_type"] = type,
            });

        return 0;
    }

    private static string Tail(string v)
    {
        var i = v.LastIndexOf('.');
        return i < 0 ? v : v[(i + 1)..];
    }
}
