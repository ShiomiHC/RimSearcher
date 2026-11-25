using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Sources;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class MembersCommand : Command
{
    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只读取该程序集中的类型与成员。");

    private static readonly CliOption NameOption = CliOption.Repeat("name") with
    {
        Aliases = ["filter", "name-contains"],
        Placeholder = "<文本>",
        BarMeansOr = true,
        Help = "只列名称含该文本的成员。重复该选项或写成 'A|B' 取含其中任一者的成员。不指定类型时，查询哪些类型声明这样的成员；" +
               $"此时{SourcePlanner.Listed(SourcePlanner.LastResortTrees)}只在点名时读取。",
        Narrows = true,
    };

    private static readonly CliOption MemberKind = CliOption.Text("kind") with
    {
        Aliases = ["kinds", "of-kind", "member-kind"],
        Placeholder = "<种类>",
        Help = "只列一个种类：方法、构造函数、属性、字段或事件。" +
               "多个种类用逗号分隔。",
        Narrows = true,
    };

    private static readonly CliOption Static = CliOption.Flag("static") with
    {
        Aliases = ["statics"],
        Help = "只列静态成员。",
        Narrows = true,
    };

    private static readonly CliOption Instance = CliOption.Flag("instance") with
    {
        Aliases = ["non-static"],
        Help = "只列实例成员。",
        Narrows = true,
    };

    private static readonly CliOption Virtual = CliOption.Flag("virtual") with
    {
        Aliases = ["virtuals", "patchable"],
        Help = "只列虚成员，即派生类型可重写的成员。",
        Narrows = true,
    };

    private static readonly CliOption Abstract = CliOption.Flag("abstract") with
    {
        Aliases = ["abstracts"],
        Help = "只列抽象成员。这些成员自身没有方法体，实现位于声明该成员的类型的派生类型上。",
        Narrows = true,
    };

    private static readonly CliOption Overrides = CliOption.Flag("overrides") with
    {
        Aliases = ["overriding", "override"],
        Help = "只列重写基类型成员的成员；隐藏基类型同名成员的，不算。",
        Narrows = true,
    };

    private static readonly CliOption Access = CliOption.Text("access") with
    {
        Aliases = ["accessibility", "visibility"],
        Placeholder = "<级别>",
        Help = "只列该访问级别的成员：public、protected、internal、private、" +
               "'protected internal' 或 'private protected'。",
        Narrows = true,
    };

    private static readonly string[] AccessLevels =
        ["public", "protected", "internal", "private", "protected internal", "private protected"];

    private static readonly CliOption Inherited = CliOption.Flag("inherited") with
    {
        Aliases = ["with-inherited", "include-base"],
        Help = "一并列出基类型声明的成员，每行注明由哪个类型声明。",
    };

    private static readonly CliOption LimitOption = CommonOptions.Limit("成员");

    public override CliOperation Operation { get; } = new()
    {
        Name = "members",
        Aliases = ["search-members", "member-search", "list-members", "find-member"],
        Summary = "列出 C# 类型的成员，可按种类与修饰符筛选。",
        Remarks =
            "属性既列属性本身（用 C# 名称），也列 IL 实际所含的 get_/set_ 方法。",
        Positionals =
        [
            new CliPositional
            {
                Name = "type",
                Variadic = true,
                Required = false,
                Help = "类型名：'Verse.ThingComp'、'ThingComp' 或其片段。多个名称进入同一张表，由「类型」与「程序集」列" +
                       "区分各行归属。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            CodeShared.Source,
            AssemblyOption,
            NameOption,
            MemberKind,
            Static,
            Instance,
            Virtual,
            Abstract,
            Overrides,
            Access,
            Inherited,
            LimitOption,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher members Verse.ThingComp",
            "rimsearcher members Verse.Pawn --virtual",
            "rimsearcher members Verse.Pawn --name Faction --inherited",
            "rimsearcher members RimWorld.GenRecipe --kind 方法 --static",
        ],
        JsonKeys =
        [
            new("members",
                "每个成员一行：assembly、type、kind（方法 / 构造函数 / 属性 / 字段 / 事件）、member、signature、" +
                "static、virtual、abstract、override、accessibility。",
                Rows: true),
            EmptyCause.JsonKeyCounting("member"),
            Snapshot.DataLayers.AssemblyCopyJsonKey,
            NameLookup.CodeJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;

        var byName = NameText(ctx);
        if (asked.Count == 0 && ctx.Args.Flag(Inherited))
            throw new CliUsageException("--inherited 须与类型名一起指定。");
        if (asked.Count == 0 && byName is not { Length: > 0 })
            throw new CliUsageException(
                "缺少类型名，如 'Verse.ThingComp'。查询哪些类型声明某名称的成员时，" +
                "单独指定 --name：'rimsearcher members --name HungerMultiplier'。");

        ctx.Report.Promises("members");

        using var lookup = CodeShared.Open(ctx, out _);

        if (asked.Count == 0)
        {
            var found = NameParts(byName!)
                .SelectMany(part => lookup.FindMembersAnywhere(part, int.MaxValue, substring: true))
                .Distinct().ToList();
            lookup.TreesInAnswer.UnionWith(found.Select(r => r.Type.Assembly.Tree));
            if (found.Count == 0)
            {
                lookup.AnsweredFromEveryTree();
                if (CodeShared.LeftOutOnly(lookup))
                {
                    ctx.Report.Table("members", Columns, []);
                    return 1;
                }
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"本次读取的程序集中，没有类型声明名称含 " +
                    $"{string.Join(" 或 ", NameParts(byName!).Select(p => $"'{p}'"))} 的成员。" +
                    $"改用 'rimsearcher code-search {(byName!.Contains('|') ? $"\"{byName}\"" : byName)}' 搜索 C# 文本。");
                ctx.Report.Table("members", Columns, []);
                return 1;
            }
            return Emit(ctx, [.. found], byName!);
        }

        var types = new List<TypeHit>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
        {
            var symbol = SymbolRef.Parse(one).AsWholeType();
            lookup.Ask([symbol.TypeName]);
            var hit = lookup.FindTypes(symbol.TypeName);
            if (hit.Count == 0) { CodeShared.SayNoType(ctx, lookup, symbol); continue; }

            if (hit.Count > 1)
                ctx.Report.Notice(NoticeKind.Filter,
                    $"'{symbol.TypeName}' 在本次读取的程序集中对应 {Tally.Complete(hit.Count).Render("type")}：" +
                    $"{NameList.Render([.. hit.Select(t => t.FullName)], 6)}。已全部列出；" +
                    "写出命名空间可选定其中一个。");

            foreach (var t in hit)
                if (seenTypes.Add(t.Assembly + " " + t.FullName))
                    types.Add(t);
        }
        lookup.TreesInAnswer.UnionWith(types.Select(t => t.Assembly.Tree));

        if (types.Count == 0)
        {
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var inherited = ctx.Args.Flag(Inherited);
        var hierarchies = inherited ? new HierarchyByReach(lookup) : null;

        var all = new List<MemberRow>();
        foreach (var t in types)
        {
            all.AddRange(lookup.Members(t));
            if (hierarchies is null) continue;

            var hierarchy = hierarchies.For(t);
            using var quiet = lookup.Unnoted();
            foreach (var b in hierarchy.BaseChain(t.FullName))
                foreach (var bt in lookup.FindTypes(b).Take(1))
                {
                    lookup.TreesInAnswer.Add(bt.Assembly.Tree);
                    all.AddRange(lookup.Members(bt));
                }
        }

        var declared = all.Count;
        var kept = Filter(ctx, all);

        if (kept.Count == 0)
        {
            SayNothingPassed(ctx, types[0], all, inherited, hierarchies?.For(types[0]));
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var limit = ctx.Limit(LimitOption);
        var rows = kept.Take(limit.Effective).Select(Row).ToList();

        ctx.Report.CountNotice(Tally.Of(rows.Count, kept.Count), "member");

        if (!inherited)
            ctx.Report.Notice(NoticeKind.Boundary,
                types.Count == 1 ? $"只列 {types[0].FullName} 自身声明的成员。" : "只列各类型自身声明的成员。", teach: true);

        ctx.Report.Table("members", Columns, rows);
        return 0;
    }

    private static int Emit(CommandContext ctx, List<MemberRow> found, string byName)
    {
        var declared = found.Count;
        var kept = Filter(ctx, found);

        if (kept.Count == 0)
        {
            if (!ctx.Report.EmptyBecause(SingleDrops(ctx, found), "member"))
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{byName}' 匹配 {Tally.Complete(declared).Render("member")}，本命令的其他筛选条件合起来未保留其中任何一个，" +
                    $"单独去掉其中任一条件也仍为零。只按名称查询见 '{ctx.Without(Sieves)}'。");
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var limit = ctx.Limit(LimitOption);
        var rows = kept.Take(limit.Effective).Select(Row).ToList();

        ctx.Report.CountNotice(Tally.Of(rows.Count, kept.Count), "member");

        var types = kept.Select(r => r.Type.FullName).Distinct(StringComparer.Ordinal).Count();
        ctx.Report.Notice(NoticeKind.Boundary,
            $"这些成员分属 {Tally.Complete(types).Render("type")}。", teach: true);

        ctx.Report.Table("members", Columns, rows);
        return 0;
    }

    private static readonly CliOption[] Sieves =
        [NameOption, MemberKind, Static, Instance, Virtual, Abstract, Overrides, Access];

    private static List<EmptyCause> SingleDrops(CommandContext ctx, List<MemberRow> all)
        => Sieves.Where(ctx.Args.Has)
                 .Select(o => new EmptyCause(ctx.FilterAsGiven(o), Filter(ctx, all, except: o).Count, ctx.Without(o)))
                 .ToList();

    private static string? NameText(CommandContext ctx)
        => ctx.Args.Values(NameOption) is { Count: > 0 } given ? string.Join("|", given) : null;

    private static string[] NameParts(string text)
        => text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<MemberRow> Filter(CommandContext ctx, List<MemberRow> rows, CliOption? except = null)
    {
        bool On(CliOption option) => !ReferenceEquals(option, except);

        var text = NameText(ctx);
        if (text is { Length: > 0 } && On(NameOption))
        {
            var parts = NameParts(text);
            rows = rows.Where(r => parts.Any(p => r.Name.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var kinds = ctx.Args.Value(MemberKind);
        if (kinds is { Length: > 0 } && On(MemberKind))
        {
            var given = kinds.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = given.Where(k => CsKind.Parse(k) is not { } w || !Kinds.Contains(w)).ToList();
            if (unknown.Count > 0)
                throw new CliUsageException(
                    $"--kind 不接受 {NameList.Render([.. unknown.Select(u => $"'{u}'")], 4)}。可选值：" +
                    $"{string.Join("、", Kinds.Select(CsKind.Of))}。");
            var wanted = given.Select(k => CsKind.Parse(k)!).ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(r => wanted.Contains(r.Kind)).ToList();
        }

        if (ctx.Args.Flag(Static) && ctx.Args.Flag(Instance))
            throw new CliUsageException(
                "--static 与 --instance 不能同时传入。");

        if (ctx.Args.Flag(Static) && On(Static)) rows = rows.Where(r => r.IsStatic).ToList();
        if (ctx.Args.Flag(Instance) && On(Instance)) rows = rows.Where(r => !r.IsStatic).ToList();
        if (ctx.Args.Flag(Virtual) && On(Virtual)) rows = rows.Where(r => r.IsVirtual).ToList();
        if (ctx.Args.Flag(Abstract) && On(Abstract)) rows = rows.Where(r => r.IsAbstract).ToList();
        if (ctx.Args.Flag(Overrides) && On(Overrides)) rows = rows.Where(r => r.IsOverride).ToList();

        var access = ctx.Args.Value(Access);
        if (access is { Length: > 0 } && !AccessLevels.Contains(access, StringComparer.OrdinalIgnoreCase))
            throw new CliUsageException($"--access 不接受 '{access}'。可选值：{string.Join("、", AccessLevels)}。");
        if (access is { Length: > 0 } && On(Access))
            rows = rows.Where(r => string.Equals(r.Accessibility, access, StringComparison.OrdinalIgnoreCase))
                       .ToList();

        return rows;
    }

    private static void SayNothingPassed(
        CommandContext ctx, TypeHit type, List<MemberRow> all, bool inherited, Hierarchy? hierarchy)
    {
        var declared = all.Count;
        if (declared == 0)
        {
            var chain = hierarchy?.BaseChain(type.FullName) ?? [];
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{type.FullName} 自身没有声明成员。" +
                (inherited && chain.Count == 0
                    ? "该类型的基类型不在本次读取的程序集中，因此无从继续上溯。"
                    : inherited ? "" : "该类型的基类型声明的成员由 '--inherited' 列出。"));
            return;
        }

        if (ctx.Report.EmptyBecause(SingleDrops(ctx, all), "member")) return;
        ctx.Report.Notice(NoticeKind.Filter,
            $"{type.FullName} 声明 {Tally.Complete(declared).Render("member")}" +
            (inherited ? "（计入基类型）" : "") +
            $"，无一通过此处给出的筛选条件，单独去掉其中任一条件也仍为零。全部成员见 '{ctx.Without(Sieves)}'。");
    }

    private static readonly string[] Kinds = ["method", "constructor", "property", "field", "event"];

    private static readonly string[] Columns =
        ["assembly", "type", "kind", "member", "signature", "static", "virtual", "abstract",
         "override", "accessibility"];

    private static IReadOnlyDictionary<string, object?> Row(MemberRow m)
        => new Dictionary<string, object?>
        {
            ["assembly"] = m.Type.Assembly.Name,
            ["type"] = m.Type.FullName,
            ["kind"] = CsKind.Of(m.Kind),
            ["member"] = m.Name,
            ["signature"] = m.Signature.Length > 0 ? m.Signature : null,
            ["static"] = m.IsStatic,
            ["virtual"] = m.IsVirtual,
            ["abstract"] = m.IsAbstract,
            ["override"] = m.IsOverride,
            ["accessibility"] = m.Accessibility.Length > 0 ? m.Accessibility : null,
        };
}
