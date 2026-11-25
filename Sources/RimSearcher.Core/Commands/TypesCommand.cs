using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class TypesCommand : Command
{
    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只读取该程序集中的类型。");

    private static readonly CliOption Derived = CliOption.Flag("derived") with
    {
        Aliases = ["subclasses", "implementors", "children"],
        Help = "列出直接派生自该类型的类型；指定的是接口时，列出实现该接口的类型。",
    };

    private static readonly CliOption Transitive = CliOption.Flag("transitive") with
    {
        Aliases = ["deep", "recursive"],
        Help = "与 --derived 一起指定，沿继承链一直向下。",
    };

    private static readonly CliOption Bases = CliOption.Flag("bases") with
    {
        Aliases = ["base-types", "parents", "ancestors"],
        Help = "改为向上列出整条基类型链。",
    };

    private static readonly CliOption Declares = CliOption.Text("declares") with
    {
        Aliases = ["declaring", "with-member"],
        Placeholder = "<member>",
        Help = "只列自身声明该名称成员的类型。",
        Narrows = true,
    };

    private static readonly CliOption NamespaceOption = CliOption.Text("namespace") with
    {
        Aliases = ["ns", "in-namespace"],
        Placeholder = "<前缀>",
        Help = "只列命名空间以此开头的类型；与 --derived 或 --bases 一起指定时，筛的是列出的派生类型或基类型。不指定类型名时，先列出直接声明在该命名空间中的类型，" +
               "再列出其下各命名空间及各自声明的类型数；编译器生成的类型不列入列表。",
        Narrows = true,
    };

    private static readonly CliOption LimitOption = CommonOptions.Limit("类型");

    public override CliOperation Operation { get; } = new()
    {
        Name = "types",
        Aliases = ["search-types", "type-search", "find-type"],
        Summary = "查找 C# 类型，显示其基类型与派生类型。",
        Remarks =
            "名称可以是全名（'Verse.ThingComp'）、裸名（'ThingComp'）或片段：依次尝试全名、裸名、以所给名称结尾的任何名称，" +
            "取第一个有匹配的结果。同一裸名匹配多个类型时全部列出。\n\n" +
            "「编译器生成」列标记迭代器状态机与闭包容器类型。",
        Positionals =
        [
            new CliPositional
            {
                Name = "name",
                Variadic = true,
                Required = false,
                Help = "类型名：'Verse.ThingComp'、'ThingComp' 或其片段。多个名称进入同一张表，由「类型」列区分各行归属。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            CodeShared.Source,
            AssemblyOption,
            Derived,
            Transitive,
            Bases,
            Declares,
            NamespaceOption,
            LimitOption,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher types Verse.ThingComp --derived --transitive",
            "rimsearcher types CompProperties --namespace RimWorld",
            "rimsearcher types --namespace Verse.AI.Group",
            "rimsearcher types Verse.Pawn --bases",
            "rimsearcher types Verse.ThingComp --derived --transitive --declares CompTick",
        ],
        JsonKeys =
        [
            new("types",
                "每个类型一行：assembly、type、namespace、base、interfaces、derived、compiler_generated。" +
                "多个名称的行按给出名称的顺序放在同一个数组中。",
                Rows: true),
            new(NamespacesTable,
                "仅在指定 --namespace 且未指定类型名时：其下每个命名空间一行，含 namespace、types" +
                "（自身声明的类型数，不计编译器生成的类型）。"),
            EmptyCause.JsonKeyCounting("type"),
            Snapshot.DataLayers.AssemblyCopyJsonKey,
            NameLookup.CodeJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        var ns = ctx.Args.Value(NamespaceOption);
        if (asked.Count == 0 && ns is not { Length: > 0 })
            throw new CliUsageException(
                "缺少类型名，如 'Verse.ThingComp'；或指定 --namespace <前缀> 列出某命名空间中的类型。");

        var wantDerived = ctx.Args.Flag(Derived);
        var wantBases = ctx.Args.Flag(Bases);
        if (wantDerived && wantBases)
            throw new CliUsageException("--derived 与 --bases 不能同时传入。");
        if (ctx.Args.Flag(Transitive) && !wantDerived && !wantBases)
            throw new CliUsageException("--transitive 须与 --derived 一起指定。");
        if (asked.Count == 0 && (wantDerived || wantBases))
            throw new CliUsageException($"{(wantDerived ? "--derived" : "--bases")} 须与类型名一起指定。");

        ctx.Report.Promises("types");

        using var lookup = CodeShared.Open(ctx, out _);

        var matches = new List<TypeHit>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<SymbolRef>();
        List<IReadOnlyDictionary<string, object?>>? under = null;
        if (asked.Count == 0)
        {
            var inPrefix = lookup.AllTypes(withHidden: true)
                .Where(t => !t.CompilerGenerated && t.Namespace.StartsWith(ns!, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var t in inPrefix) lookup.Note(t.Assembly, "type", t.FullName);
            inPrefix = [.. inPrefix.Where(t => lookup.TagOf(t.Assembly) != CodeTag.Hidden)];
            bool Itself(TypeHit t) => string.Equals(t.Namespace, ns, StringComparison.OrdinalIgnoreCase);
            matches.AddRange(inPrefix.Where(Itself)
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Assembly.Name, StringComparer.OrdinalIgnoreCase));
            under = [.. inPrefix.Where(t => !Itself(t))
                .GroupBy(t => t.Namespace, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["namespace"] = g.Key,
                    ["types"] = g.Count(),
                })];
            ctx.Report.Promises(NamespacesTable);
            if (inPrefix.Count == 0)
            {
                lookup.AnsweredFromEveryTree();
                if (!CodeShared.LeftOutOnly(lookup))
                ctx.Report.Notice(NoticeKind.Boundary,
                    Cjk.Join("本次读取的", Tally.Complete(lookup.AssemblyCount).Render("assembly"), $"中没有以 '{ns}' 开头的命名空间") +
                    "。");
            }
            else if (matches.Count == 0)
            {
                lookup.TreesInAnswer.UnionWith(inPrefix.Select(t => t.Assembly.Tree));
                ctx.Report.Notice(NoticeKind.Count, $"'{ns}' 自身没有声明类型。");
                ctx.Report.Table("types", Columns, []);
                SayUnder(ctx, ns!, under, countedAbove: false);
                return 0;
            }
        }
        foreach (var one in asked)
        {
            var symbol = SymbolRef.Parse(one).AsWholeType();
            lookup.Ask([symbol.TypeName]);
            var hit = lookup.FindTypes(symbol.TypeName);
            if (hit.Count == 0) { missing.Add(symbol); continue; }
            foreach (var t in hit)
                if (seenTypes.Add(t.Assembly + "\0" + t.FullName))
                    matches.Add(t);
        }

        foreach (var symbol in missing)
        {
            lookup.Ask([symbol.TypeName]);
            CodeShared.SayNoType(ctx, lookup, symbol);
        }
        lookup.TreesInAnswer.UnionWith(matches.Select(t => t.Assembly.Tree));

        if (matches.Count == 0)
        {
            ctx.Report.Table("types", Columns, []);
            return 1;
        }

        var hierarchies = new HierarchyByReach(lookup);
        var limit = ctx.Limit(LimitOption);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var listed = new List<(TypeHit? Type, string Name, Hierarchy In)>();
        bool InNamespace(string typeNamespace)
            => ns is not { Length: > 0 } || typeNamespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase);

        if (wantDerived)
        {
            var transitive = ctx.Args.Flag(Transitive);
            var kids = new List<TypeHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var withHidden = lookup.HasHidden;
            var drawnFrom = new Dictionary<TypeHit, TypeHit>(ReferenceEqualityComparer.Instance);
            foreach (var t in matches)
            {
                var full = hierarchies.For(t, withHidden);
                foreach (var k in transitive ? full.AllDerived(t.FullName) : full.DirectDerived(t.FullName))
                {
                    if (InNamespace(k.Namespace)) lookup.Note(k.Assembly, "derived type", k.FullName);
                    if (lookup.TagOf(k.Assembly) == CodeTag.Hidden) continue;
                    if (!seen.Add(k.Assembly.Name + "!" + k.FullName)) continue;
                    kids.Add(k);
                    drawnFrom[k] = t;
                }
            }
            lookup.TreesInAnswer.UnionWith(kids.Select(k => k.Assembly.Tree));

            if (kids.Count == 0)
            {
                lookup.AnsweredFromEveryTree();
                if (!CodeShared.LeftOutOnly(lookup))
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"本次读取的程序集中没有派生自 " +
                    $"{NameList.Render(matches.Select(m => m.FullName).Distinct(StringComparer.Ordinal).ToList(), Limits.MaxSuggestions)} 的类型。");
                ctx.Report.Table("types", Columns, []);
                return 1;
            }

            if (!transitive)
                ctx.Report.Notice(NoticeKind.Filter,
                    "只列下一层。", teach: true);

            listed.AddRange(kids.Select(k => ((TypeHit?)k, k.FullName, hierarchies.For(drawnFrom[k]))));
        }
        else if (wantBases)
        {
            using var quiet = lookup.Unnoted();
            foreach (var t in matches)
            {
                var hierarchy = hierarchies.For(t);
                var names = hierarchy.BaseChain(t.FullName);
                if (names.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"{t.FullName} 没有基类型。");
                    continue;
                }

                foreach (var b in names)
                {
                    var hit = lookup.FindTypes(b).FirstOrDefault();
                    if (hit is not null) lookup.TreesInAnswer.Add(hit.Assembly.Tree);
                    listed.Add((hit, b, hierarchy));
                }

                var last = names[^1];
                if (lookup.FindTypes(last).Count == 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"{t.FullName} 的基类型链在 {last} 处离开本次读取的程序集。");
            }

            if (listed.Count == 0)
            {
                ctx.Report.Table("types", Columns, []);
                return 1;
            }
        }
        else
        {
            listed.AddRange(matches.Select(t => ((TypeHit?)t, t.FullName, hierarchies.For(t))));
        }

        var byNamespace = asked.Count > 0 && ns is { Length: > 0 };
        List<(TypeHit? Type, string Name, Hierarchy In)> Keep(bool byNs, bool byDeclares)
            => [.. listed.Where(l => (!byNs || InNamespace(l.Type?.Namespace ?? NamespaceOf(l.Name)))
                                  && (!byDeclares || (l.Type is { } t && DeclaresIt(ctx, lookup, t))))];
        var declares = ctx.Args.Value(Declares) is { Length: > 0 };
        var kept = Keep(byNamespace, declares);
        if (declares && listed.Where(l => l.Type is null).Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } unread)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"未检查 {string.Join("、", unread)} 自身的声明：不在本次读取的程序集中。");
        if (kept.Count == 0)
        {
            var causes = new List<EmptyCause>();
            if (byNamespace)
                causes.Add(new(ctx.FilterAsGiven(NamespaceOption), Keep(false, declares).Count, ctx.Without(NamespaceOption)));
            if (declares)
                causes.Add(new(ctx.FilterAsGiven(Declares), Keep(byNamespace, false).Count, ctx.Without(Declares)));
            if (!ctx.Report.EmptyBecause(causes, "type"))
                ctx.Report.Notice(NoticeKind.Filter,
                    Cjk.Join(Tally.Complete(listed.Count).Render("type"),
                             $"中，没有一个同时通过 {string.Join(" 与 ", causes.Select(c => c.Filter))}。"));
            ctx.Report.Table("types", Columns, []);
            return 1;
        }
        if (kept.Count != listed.Count)
            ctx.Report.Notice(NoticeKind.Filter,
                (wantDerived ? Tally.Complete(listed.Count).Render("derived type") : Tally.Complete(listed.Count).Render("type")) +
                "中，只列出" +
                string.Join("、", new[]
                {
                    byNamespace ? $"命名空间以 '{ns}' 开头" : null,
                    declares ? $"自身声明 '{ctx.Args.Value(Declares)}' " : null,
                }.OfType<string>()) + "的类型。");

        var found = kept.Select(l => l.Type is { } t ? Row(t, l.In) : new Dictionary<string, object?>
        {
            ["assembly"] = null,
            ["type"] = l.Name,
            ["namespace"] = null,
            ["base"] = null,
            ["interfaces"] = null,
            ["derived"] = null,
            ["compiler_generated"] = null,
        }).ToList();
        rows.AddRange(found.Take(limit.Effective));

        var tally = found.Count > limit.Effective
            ? Tally.Of(limit.Effective, found.Count)
            : Tally.Complete(found.Count);

        if (wantDerived)
            ctx.Report.CountNotice(tally, "derived type");
        else
            ctx.Report.CountNotice(tally, "type");

        var generated = rows.Where(r => r.TryGetValue("compiler_generated", out var v) && v is true).ToList();
        if (generated.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("上表中有", Tally.Complete(generated.Count).Render("type"), "为编译器生成：反编译源码中没有对应文件，成员由 members 列出，例如 " +
                         $"'{CommandRegistry.ExeName} members \"{generated[0]["type"]}\"'。"));

        ctx.Report.Table("types", Columns, rows);
        if (under is not null) SayUnder(ctx, ns!, under, countedAbove: true);
        return 0;
    }

    private const string NamespacesTable = "namespaces";

    private static void SayUnder(CommandContext ctx, string ns, IReadOnlyList<IReadOnlyDictionary<string, object?>> under,
                                 bool countedAbove)
    {
        if (under.Count == 0) return;
        ctx.Report.Table(NamespacesTable, ["namespace", "types"], under,
            caption: $"{Tally.Complete(under.Count).Render("namespace", scope: $"'{ns}' 下")}，各附自身声明的类型数" +
                     (countedAbove ? "（不计入上方计数）" : "") + "：");
    }

    private static bool DeclaresIt(CommandContext ctx, MetadataLookup lookup, TypeHit type)
    {
        var member = ctx.Args.Value(Declares)!;
        return lookup.FindMethods(type, member).Count > 0
               || lookup.Members(type).Any(m => string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase));
    }

    private static string NamespaceOf(string fullName)
    {
        var outer = fullName.Split('+')[0];
        return outer.LastIndexOf('.') is > 0 and var cut ? outer[..cut] : "";
    }

    private static readonly string[] Columns =
        ["assembly", "type", "namespace", "base", "interfaces", "derived", "compiler_generated"];

    private static IReadOnlyDictionary<string, object?> Row(TypeHit t, Hierarchy h)
    {
        var rel = h.Relation(t.FullName);
        return new Dictionary<string, object?>
        {
            ["assembly"] = t.Assembly.Name,
            ["type"] = t.FullName,
            ["namespace"] = t.Namespace,
            ["base"] = rel?.BaseName is { Length: > 0 } b ? b : null,
            ["interfaces"] = rel is { Interfaces.Count: > 0 } ? string.Join(", ", rel.Interfaces) : null,
            ["derived"] = h.DirectDerived(t.FullName).Count,
            ["compiler_generated"] = t.CompilerGenerated,
        };
    }
}
