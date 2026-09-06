using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 类型,以及它与别的类型的关系。
///
/// 关系走元数据,不走文本:反编译树里一个类型只写着它的直接基类,「谁派生自它」在树上是
/// 搜不出来的 —— 那需要把每棵树都读一遍再反过来连。这里连一次要 15 毫秒,所以不落盘。
/// </summary>
public sealed class TypesCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "types",
        Aliases = ["search-types", "type-search", "find-type"],
        Summary = "Find C# types and show what they derive from and what derives from them.",
        Remarks =
            "The name can be a full one ('Verse.ThingComp'), a bare one ('ThingComp'), or a fragment — a " +
            "full name is tried first, then a bare one, then anything ending in it, and the first of those " +
            "that matches is what you get. Several types can carry the same bare name across mods; all of " +
            "them are listed rather than one being picked.\n\n" +
            "--derived walks downward and --bases upward. Both stop at the edge of the synced trees: " +
            "System.Object and the rest of the framework are not in any tree, so a base chain ending " +
            "somewhere else is the chain leaving what was read, not a gap in it.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "name",
                Help = "A type name: 'Verse.ThingComp', 'ThingComp', or a fragment of one.",
            },
        ],
        Options =
        [
            CodeShared.Source,
            new OptionSpec
            {
                Name = "derived",
                Arity = Arity.Flag,
                Aliases = ["subclasses", "implementors", "children"],
                Help = "List the types that derive from it, or implement it when it is an interface. " +
                       "Direct ones only unless --transitive.",
            },
            new OptionSpec
            {
                Name = "transitive",
                Arity = Arity.Flag,
                Aliases = ["deep", "recursive"],
                Help = "With --derived, follow the chain all the way down instead of one level.",
            },
            new OptionSpec
            {
                Name = "bases",
                Arity = Arity.Flag,
                Aliases = ["base-types", "parents", "ancestors"],
                Help = "List the chain of base types upward instead.",
            },
            new OptionSpec
            {
                Name = "namespace",
                Aliases = ["ns", "in-namespace"],
                Placeholder = "<prefix>",
                Help = "Only types whose namespace starts with this.",
                Narrows = true,
            },
            CommonOptions.Limit("types"),
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher types Verse.ThingComp --derived --transitive",
            "rimsearcher types CompProperties --namespace RimWorld",
            "rimsearcher types Verse.Pawn --bases",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "types",
                Rows = true,
                What = "one row per type: assembly, type, namespace, base, interfaces, derived, compiler_generated.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)
            ?? throw new CliUsageException("Name a type, for example 'Verse.ThingComp'.");

        ctx.Report.Promises("types");

        using var lookup = CodeShared.Open(ctx, out _);
        var symbol = SymbolRef.Parse(name).AsWholeType();
        var matches = lookup.FindTypes(symbol.TypeName);

        var ns = ctx.Args.Value("namespace");
        if (ns is { Length: > 0 })
            matches = matches.Where(t => t.Namespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            CodeShared.SayNoType(ctx, lookup, symbol);
            ctx.Report.Table("types", Columns, []);
            return 1;
        }

        var hierarchy = new Hierarchy(lookup);
        var wantDerived = ctx.Args.Flag("derived");
        var wantBases = ctx.Args.Flag("bases");
        var limit = ctx.Limit();

        // 三条支路各自摊平成一串行,计数与截断在**一处**发 —— 分头发的话,
        // 「印了 5 行」与「一共 710 个」会各自出现在不同的句子里,而中间那个 of 就没了。
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        List<IReadOnlyDictionary<string, object?>> found;

        if (wantDerived)
        {
            var transitive = ctx.Args.Flag("transitive");
            var kids = new List<TypeHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var t in matches)
                foreach (var k in transitive ? hierarchy.AllDerived(t.FullName) : hierarchy.DirectDerived(t.FullName))
                    // 去重的键带上程序集:同一个全名在两个 mod 里各有一份是常事,
                    // 只按全名去重就会把其中一个悄悄扔掉 —— 而扔掉哪个看不出来。
                    if (seen.Add(k.Assembly.Name + "!" + k.FullName)) kids.Add(k);

            if (kids.Count == 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"Nothing in the assemblies read here derives from {matches[0].FullName}. A subclass living " +
                    "in a mod whose tree has not been synced is absent from that, not reported as missing — " +
                    "'rimsearcher sources list' names the trees that were read.");
                ctx.Report.Table("types", Columns, []);
                return 1;
            }

            if (!transitive)
                ctx.Report.Notice(NoticeKind.Filter,
                    "One level down only. --transitive follows the chain all the way to the leaves.");

            found = kids.Select(k => Row(k, hierarchy)).ToList();
        }
        else if (wantBases)
        {
            var chain = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var t in matches)
            {
                var names = hierarchy.BaseChain(t.FullName);
                if (names.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"{t.FullName} has no base type recorded in the assemblies read here. An interface has " +
                        "none by definition; for a class it means the base is a constructed generic, which this " +
                        "chain does not decode.");
                    continue;
                }

                foreach (var b in names)
                {
                    var hit = lookup.FindTypes(b).FirstOrDefault();
                    chain.Add(hit is null
                        ? new Dictionary<string, object?>
                        {
                            ["assembly"] = null,
                            ["type"] = b,
                            ["namespace"] = null,
                            ["base"] = null,
                            ["interfaces"] = null,
                            ["derived"] = null,
                            ["compiler_generated"] = null,
                        }
                        : Row(hit, hierarchy));
                }

                var last = names[^1];
                if (lookup.FindTypes(last).Count == 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"The chain from {t.FullName} ends at {last}, which is not in any tree read here — " +
                        "that is where it leaves the synced assemblies, not where it stops having a base.");
            }

            if (chain.Count == 0)
            {
                ctx.Report.Table("types", Columns, []);
                return 1;
            }
            found = chain;
        }
        else
        {
            found = matches.Select(t => Row(t, hierarchy)).ToList();
        }

        rows.AddRange(found.Take(limit.Effective));

        // 两个名词各写成字面量:登记处的闸靠扫源码认名词,变量里的那个词它认不出来。
        var tally = found.Count > limit.Effective
            ? Tally.Of(limit.Effective, found.Count)
            : Tally.Complete(found.Count);
        const string more = "--limit all shows every one";

        if (wantDerived)
        {
            if (tally.IsTruncated) ctx.Report.TruncationNotice(tally, "derived type", more);
            else ctx.Report.CountNotice(tally, "derived type");
        }
        else
        {
            if (tally.IsTruncated) ctx.Report.TruncationNotice(tally, "type", more);
            else ctx.Report.CountNotice(tally, "type");
        }

        var generated = rows.Count(r => r.TryGetValue("compiler_generated", out var v) && v is true);
        if (generated > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "Compiler-generated among them — iterator state machines and closure holders — " +
                $"{Tally.Complete(generated).Render("type")}. These have no file in the decompiled tree, " +
                "because the decompiler turns them back into yield and lambda; 'rimsearcher il' is what reads them.");

        ctx.Report.Table("types", Columns, rows);
        return 0;
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
