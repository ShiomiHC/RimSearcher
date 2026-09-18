using System.Reflection.Metadata.Ecma335;
using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Sources;

namespace RimSearcher.Commands;

/// <summary>
/// 谁调用了这个方法。
///
/// 这是唯一一件**必须落盘**的:类型、成员、IL、继承关系都能从要看的那个程序集当场读出来,
/// 而「谁调用了它」的答案散在别的每一个程序集里 —— 不预先扫一遍,每次查询都要把全源
/// 104.8 万条指令流重走一遍。边表跟着 <c>sources sync</c> 一起建,一棵树一张。
/// </summary>
public sealed class CallersCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "callers",
        // usages / refs 是真实调用里敲过的写法,收进来而不是让它们报「未知命令」。
        Aliases = ["usages", "refs", "references", "callsites", "who-calls"],
        Summary = "Find the methods that call a given method, or the ones it calls.",
        Remarks =
            "Answered from a call-graph table built by 'rimsearcher sources sync', one per source tree. " +
            "A tree without one is not searched, and the output names those trees — a caller living there " +
            "would not appear below.\n\n" +
            "The recorded target is the method named at the call site. A callvirt names the base method even " +
            "when the object it runs on is a subclass, so calls that dispatch to an override at run time are " +
            "counted against the base — 'rimsearcher types <type> --derived' finds the overrides themselves. " +
            "The same rule is why a method the game invokes only through an override can have no caller at " +
            "all here. Calls made by name at run time — reflection, Harmony patches — are in no instruction " +
            "stream and are not counted either; 'rimsearcher code-search' finds the strings that name a " +
            "method.\n\n" +
            "--callees turns it around and lists what the named method calls.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "symbol",
                Variadic = true,
                Help = "The method: 'Verse.Pawn.Tick', 'Verse.Pawn::Tick', 'Verse.ThingDef..ctor'. Several " +
                       "methods go into the same table — the to_type and to_member columns say which calls " +
                       "belong to which — and one that cannot be resolved is reported in a note while the " +
                       "others still print.",
            },
        ],
        Options =
        [
            new OptionSpec
            {
                Name = "callees",
                Arity = Arity.Flag,
                Aliases = ["calls", "outgoing", "reverse"],
                Help = "List what this method calls instead of what calls it.",
            },
            new OptionSpec
            {
                Name = "source",
                Aliases = ["tree", "from-tree"],
                Placeholder = "<tree>",
                Help = "Only count call sites in this source tree. Both ends are still named from every " +
                       "tree, so the answer is narrowed, not blinded.",
                Narrows = true,
            },
            CommonOptions.Limit("callers"),
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher callers Verse.Pawn.Kill",
            "rimsearcher callers RimWorld.ThoughtUtility.GiveThoughtsForPawnOrganHarvested --source vethara",
            "rimsearcher callers Verse.Pawn.Tick --callees",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "calls",
                Rows = true,
                What = "one row per calling method and target pair: tree, from_assembly, from_type, " +
                       "from_member, to_assembly, to_type, to_member, call_sites.",
            },
            new()
            {
                Key = "absent",
                Rows = true,
                What = "one row per source tree this search was blind on — layer ('call_graph:' + the tree), " +
                       "state missing, next (the sync command that builds the table); empty when every tree " +
                       "searched had one. Looking for callers, every tree without a table is listed; looking " +
                       "for callees, only the tree the named method lives in.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        if (asked.Count == 0)
            throw new CliUsageException("Name the method, for example 'Verse.Pawn.Kill'.");

        ctx.Report.Promises("calls");

        using var lookup = CodeShared.Open(ctx, out var root, everyTree: true);

        // 几个符号并成一串方法。行里带着 to_type / to_member,块与块分得开。
        // Locate 落空时自己已经说了话,这里不要再报一遍。
        var methods = new List<MethodHit>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
            foreach (var m in IlCommand.Locate(ctx, lookup, SymbolRef.Parse(one)))
                if (seenMethods.Add($"{m.Type.Assembly} {m.Type.FullName} {m.Name} {m.Signature}"))
                    methods.Add(m);

        if (methods.Count == 0)
        {
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        var only = ctx.Args.Value("source");
        var graphs = CallGraphSet.Load(root, only is { Length: > 0 } ? [only] : null);

        if (graphs.Graphs.Count == 0)
        {
            // 一棵有表的树都没有:absent 表里每棵盲树一行(点名了 --source 就只有那一棵)。
            // 树目录本身也不在时 Without 是空的 —— 那时给 --source 那个名字一行,好让出路在场。
            var blind = graphs.Without.Count > 0 ? graphs.Without
                      : only is { Length: > 0 } ? [only] : [];
            ctx.Report.Absent(DataLayers.CallGraphRows(blind));
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        var callees = ctx.Args.Flag("callees");

        SayWhatWasSearched(ctx, graphs, root, callees,
                           [.. methods.Select(m => m.Type.Assembly.Tree).Distinct(StringComparer.OrdinalIgnoreCase)]);

        // 同一个方法体里调同一个目标两次是两条边。行按「调用方 → 被调方」这一对合并,
        // 次数进 call_sites —— 不合并的话一个循环展开就能把一个调用方印成八行。
        var pairs = new Dictionary<(string, int, string, int), (CallSite Site, int Count)>();
        foreach (var m in methods)
        {
            var token = MetadataTokens.GetToken(m.Handle);
            var asm = m.Type.Assembly.Name;
            foreach (var site in callees ? graphs.CalleesOf(asm, token) : graphs.CallersOf(asm, token))
            {
                var key = (site.Edge.FromAssembly, site.Edge.FromToken, site.Edge.ToAssembly, site.Edge.ToToken);
                pairs[key] = pairs.TryGetValue(key, out var cur) ? (cur.Site, cur.Count + 1) : (site, 1);
            }
        }

        if (pairs.Count == 0)
        {
            SayNone(ctx, lookup, methods, graphs, callees);
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        var limit = ctx.Limit();
        var ordered = pairs.Values.OrderByDescending(p => p.Count)
                                  .ThenBy(p => p.Site.Edge.FromAssembly, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        var rows = ordered.Take(limit.Effective).Select(p => Row(lookup, p.Site, p.Count)).ToList();

        // 两个方向的名词各写成字面量,不合并成一个变量:登记处的闸靠扫源码认名词,
        // 三元表达式里的那两个词它认不出来,于是它们会被判成「登记了没人用」。
        var tally = ordered.Count > limit.Effective
            ? Tally.Of(limit.Effective, ordered.Count)
            : Tally.Complete(ordered.Count);
        const string more = "Leave --limit out to get every one";

        if (callees)
        {
            if (tally.IsTruncated) ctx.Report.TruncationNotice(tally, "callee", more);
            else ctx.Report.CountNotice(tally, "callee");
        }
        else
        {
            if (tally.IsTruncated) ctx.Report.TruncationNotice(tally, "caller", more);
            else ctx.Report.CountNotice(tally, "caller");
        }

        // 反射 / Harmony 按名调用不在指令流里 —— 机制,住 help;此前每次都印,是常量横幅。
        ctx.Report.Table("calls", Columns, rows);
        return 0;
    }

    /// <summary>
    /// 这次搜的是哪些树。**没有边表的那些必须点名** —— 它们在结果里与「那里确实没有调用者」
    /// 逐字同形,而两者的下一步相反。
    ///
    /// 两个方向漏的不是同一件事,所以话也分两句:查调用者时,漏的是住在那些树里的调用点,
    /// 哪棵树没表都算数;查被调用者时,边全都出自被点名的方法自己那棵树 —— 别的树有没有表
    /// 与这次答案无关,而**它自己那棵**没表时整份答案是空的。后者是查得出来的,不必写成
    /// 「万一它住在里面」这种设问。
    /// </summary>
    private static void SayWhatWasSearched(
        CommandContext ctx, CallGraphSet graphs, string root, bool callees, IReadOnlyList<string> homeTrees)
    {
        var blind = callees
            ? graphs.Without.Where(t => homeTrees.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
            : [.. graphs.Without];

        // 盲树一棵一行(Docs/25 的 absent 表)。此前是一句散文,还按方向各带半句后果;
        // 表行只说「这棵树没有边表」,后果由读者从方向推 —— 查调用者时它藏着调用点,
        // 查被调用者时它正是方法住的那棵。
        ctx.Report.Absent(DataLayers.CallGraphRows(blind));

        var stale = graphs.Graphs.Where(g => IsStale(root, g)).Select(g => g.Tree).ToList();
        if (stale.Count > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                "Built from assemblies that are no longer the ones the tree records — " +
                $"{Tally.Complete(stale.Count).Render("source tree")}: {NameList.Render(stale, 6)}. " +
                "The call sites from those trees are the ones the older build had. " +
                "'rimsearcher sources sync' rebuilds them.");
    }

    /// <summary>边表是不是比树旧。判据与树自己的判据同源:来源 dll 的哈希。</summary>
    private static bool IsStale(string root, CallGraph graph)
    {
        var state = SourceTreeState.Read(Path.Combine(root, graph.Tree));
        if (state is null) return false;

        var recorded = state.Assemblies.Select(a => a.Sha256).OrderBy(h => h, StringComparer.Ordinal);
        var built = graph.SourceHashes.OrderBy(h => h, StringComparer.Ordinal);
        return !recorded.SequenceEqual(built, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一条也没有。这句话说得出口的前提是上面那两条边界已经发过 —— 否则
    /// 「没有调用者」与「装着调用者的那棵树没建表」在读者眼里是同一句话。
    /// </summary>
    private static void SayNone(
        CommandContext ctx, MetadataLookup lookup, IReadOnlyList<MethodHit> methods,
        CallGraphSet graphs, bool callees)
    {
        var names = NameList.Render([.. methods.Select(m => $"{m.Type.FullName}::{m.Name}").Distinct()], 4);

        if (callees)
        {
            var bodyless = methods.Where(m => m.Bodyless).ToList();
            // 没有方法体的三种里只有 abstract 才有实现可问;种类元数据分得开,不并列着猜。
            // 「只读字段就返回的方法一个调用都没有」是情景举例,2026-09-18 删(Docs/25 §19)。
            ctx.Report.Notice(NoticeKind.Boundary,
                bodyless.Count == methods.Count
                    ? bodyless.All(m => m.BodyKind == "abstract")
                        ? $"{names} is abstract, so it calls nothing: 'rimsearcher types " +
                          $"{methods[0].Type.FullName} --derived' lists the types that implement it."
                        : $"{names} has no method body ({string.Join(", ", bodyless.Select(m => m.BodyKind).Distinct())}), " +
                          "so it calls nothing."
                    : $"{names} calls nothing that the tables record.");
            return;
        }

        var head = $"Nothing in the {Tally.Complete(graphs.Graphs.Count).Render("source tree")} searched calls " +
                   $"{names}. ";

        // 覆写方法上的零几乎总是这一条,而不是「没人用它」:调用点写的是基类那个名字。
        // 实测 Verse.Pawn::Kill 在 vanilla 里 0 个调用者,Verse.Thing::Kill 有 59 个。
        var member = methods[0].SourceName;
        var bases = DeclaringBases(lookup, methods[0]);

        // 「调用点写的是基类的名字」与「游戏经覆写调进 mod 代码」都是机制,住 help;
        // 这里只剩事实(还有谁声明了这个成员)与填好参数的出路。
        ctx.Report.Notice(NoticeKind.Boundary,
            bases.Count > 0
                ? head + $"{member} is also declared by {NameList.Render(bases, 4)}: " +
                         $"'rimsearcher callers {bases[^1]}.{member}' counts the calls written against that type."
                : head.TrimEnd());
    }

    /// <summary>
    /// 基类链上也声明了这个成员的那些类型,近的在前。
    ///
    /// 全给而不是只给最近的一个:调用点写的是它当时那个静态类型,而那可能是链上任何一层 ——
    /// 只指出最近的一个,读者照着问一次,很可能又是零。
    /// </summary>
    private static List<string> DeclaringBases(MetadataLookup lookup, MethodHit method)
    {
        if (!Hierarchy.Overrides(lookup, method.Type).Any(o => o.Handle == method.Handle)) return [];

        var found = new List<string>();
        foreach (var b in new Hierarchy(lookup).BaseChain(method.Type.FullName))
            foreach (var bt in lookup.FindTypes(b).Take(1))
                if (lookup.FindMethods(bt, method.SourceName).Count > 0)
                    found.Add(bt.FullName);
        return found;
    }

    private static readonly string[] Columns =
        ["tree", "from_assembly", "from_type", "from_member", "to_assembly", "to_type", "to_member",
         "call_sites"];

    private static IReadOnlyDictionary<string, object?> Row(MetadataLookup lookup, CallSite site, int count)
    {
        var from = lookup.ResolveToken(site.Edge.FromAssembly, site.Edge.FromToken);
        var to = lookup.ResolveToken(site.Edge.ToAssembly, site.Edge.ToToken);

        return new Dictionary<string, object?>
        {
            ["tree"] = site.Tree,
            ["from_assembly"] = site.Edge.FromAssembly,
            ["from_type"] = from?.Type.FullName,
            ["from_member"] = from?.Name,
            ["to_assembly"] = site.Edge.ToAssembly,
            ["to_type"] = to?.Type.FullName,
            ["to_member"] = to?.Name,
            ["call_sites"] = count,
        };
    }
}
