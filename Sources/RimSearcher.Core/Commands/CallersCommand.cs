using RimTooling.Cli;
using System.Reflection.Metadata.Ecma335;
using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Sources;

namespace RimSearcher.Commands;

public sealed class CallersCommand : Command
{
    private static readonly CliOption Callees = CliOption.Flag("callees") with
    {
        Aliases = ["calls", "outgoing", "reverse"],
        Help = "改为列出该方法所调用的方法。",
    };

    private static readonly CliOption Source = CliOption.Repeat("source") with
    {
        Aliases = ["tree", "from-tree", "mod", "mods"],
        Placeholder = "<源码树>",
        Help = "只计该源码树中的调用处；多棵用 --source 重复指定。调用方与被调用方仍从每棵源码树中识别。" +
               $"不指定时搜索除 {SourcePlanner.Listed(SourcePlanner.LastResortTrees, describe: true)}以外的每棵源码树。",
        Narrows = true,
    };

    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只计该程序集中的调用处。与 --callees 一起指定时，只计进入该程序集的调用；" +
                              "这些调用仍从 --source 指定的源码树读取，未指定时从缺省的源码树读取。" +
                              "方法本身在每棵源码树中查找。");

    private static readonly CliOption LimitOption = CommonOptions.Limit("调用方") with
    {
        Help = CommonOptions.Limit("调用方").Help + "配 --callees 时计的是被调用方。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "callers",
        Aliases = ["callsites", "who-calls"],
        Summary = "查找某个方法的调用方，或该方法所调用的方法。",
        Remarks =
            "记录的目标是调用处所指的方法。" +
            "运行时分派到重写的调用计在基类型的方法上；重写本身由 'rimsearcher types <type> --derived --transitive --declares <member>' 查找。" +
            "运行时按名称发出的调用（反射、Harmony patch）同样不计入；" +
            "指名某个方法的字符串由 'rimsearcher code-search' 查找。",
        Positionals =
        [
            new CliPositional
            {
                Name = "symbol",
                Variadic = true,
                Help = "方法：'Verse.Pawn.Tick'、'Verse.Pawn::Tick'、'Verse.ThingDef..ctor'。有多个重载的名称涵盖每个重载，" +
                       "此时成员列带各重载的参数列表。带参数列表写出时（'SceneManager.LoadScene(int)'），名称只指该重载。" +
                       "多个方法进入同一张表，由「目标类型」与「目标成员」列区分各调用的归属。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            Callees,
            Source,
            AssemblyOption,
            LimitOption,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher callers Verse.Pawn.Kill",
            "rimsearcher callers RimWorld.ThoughtUtility.GiveThoughtsForPawnOrganHarvested --source vanilla",
            "rimsearcher callers Verse.Pawn.Tick --callees",
        ],
        JsonKeys =
        [
            new("calls",
                "每个调用方与目标的组合一行：tree、from_assembly、from_type、from_member、to_assembly、to_type、" +
                "to_member、call_sites、loaded。loaded 说明游戏在快照中是否加载调用方所在的程序集：已加载、mod 不在快照、" +
                "程序集未加载（这两种只在指定 --include-unloaded 或本次调用指定该源码树或程序集时出现）或未知。",
                Rows: true),
            new("absent",
                "本次搜索未覆盖的每棵源码树一行：layer（'call_graph:' + 树名）、state 未建、next（建表的 sync 命令）；" +
                "搜索的每棵源码树都有表时为空。查调用方时列出每棵没有表的源码树；查被调用方时只列所指方法所在的源码树，" +
                "除非搜索的源码树都没有表，此时两种情形都列出全部源码树。另外，因源码树未留存副本而改为读取已安装的程序集，" +
                "也各占一行（'assembly_copy:' + 树/程序集）。",
                Rows: true),
            NameLookup.CodeJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        if (asked.Count == 0)
            throw new CliUsageException("缺少方法，如 'Verse.Pawn.Kill'。");

        ctx.Report.Promises("calls");

        using var lookup = CodeShared.Open(ctx, out var root, out var selection, everyTree: true);

        var methods = new List<MethodHit>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
            foreach (var m in IlCommand.Locate(ctx, lookup, SymbolRef.Parse(one), typeGoesToUsages: true))
                if (seenMethods.Add($"{m.Type.Assembly} {m.Type.FullName} {m.Name} {m.Signature}"))
                    methods.Add(m);

        if (methods.Count == 0)
        {
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        var callees = ctx.Args.Flag(Callees);
        var only = callees ? CodeShared.Trees(ctx) : selection.Trees;
        List<string> homeTrees = [.. methods.Select(m => m.Type.Assembly.Tree).Distinct(StringComparer.OrdinalIgnoreCase)];
        var graphs = CallGraphSet.Load(root, only.Count > 0 ? only : null, also: callees ? homeTrees : []);

        if (graphs.Graphs.Count == 0)
        {
            var blind = graphs.Without.Count > 0 ? graphs.Without
                      : only;
            ctx.Report.Absent(DataLayers.CallGraphRows(blind));
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        SayWhatWasSearched(ctx, graphs, root, callees, homeTrees);

        var load = LoadedCode.Open(ctx, root);
        var scope = LoadedCode.ReadScope.Of(ctx, CodeShared.Trees(ctx), callees ? [] : selection.Exact);

        lookup.PinReferences(graphs.References);
        var trust = new GraphTrust(graphs, lookup);
        trust.Say(ctx,
                  callees ? null : [.. methods.Select(m => m.Type.Assembly.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
                  callees ? homeTrees : [.. graphs.Graphs.Select(g => g.Tree)],
                  (tree, from) => load is null || !LoadedCode.Unloaded(load.OfAssembly(tree, from).State) || scope.Covers(tree, from));

        var pairs = new Dictionary<(string, string, int, string, int), (CallSite Site, int Count)>();
        foreach (var m in methods)
        {
            var token = MetadataTokens.GetToken(m.Handle);
            var asm = m.Type.Assembly.Name;
            foreach (var site in callees ? graphs.CalleesOf(asm, token) : graphs.CallersOf(asm, token))
            {
                if (lookup.CopyFor(site.Tree, callees ? site.Edge.FromAssembly : site.Edge.ToAssembly) != m.Type.Assembly)
                    continue;
                if (!trust.Current(site.Tree, site.Edge.FromAssembly, site.Edge.ToAssembly)) continue;
                if (!selection.Keeps(callees ? site.Edge.ToAssembly : site.Edge.FromAssembly)) continue;
                var key = (site.Tree, site.Edge.FromAssembly, site.Edge.FromToken, site.Edge.ToAssembly, site.Edge.ToToken);
                pairs[key] = pairs.TryGetValue(key, out var cur) ? (cur.Site, cur.Count + 1) : (site, 1);
            }
        }
        (CodeLoad State, string? Assembly) LoadOf(CallSite site)
            => load?.OfAssembly(site.Tree, site.Edge.FromAssembly) ?? (CodeLoad.Unknown, null);
        var leftOut = new List<(string Tree, CodeLoad State, string? Assembly)>();
        var kept = new List<(CallSite Site, int Count)>();
        foreach (var p in pairs.Values)
        {
            var state = LoadOf(p.Site);
            if (LoadedCode.Unloaded(state.State) && !scope.Covers(p.Site.Tree, p.Site.Edge.FromAssembly)) leftOut.Add((p.Site.Tree, state.State, state.Assembly));
            else kept.Add(p);
        }
        var noun = callees ? "callee" : "caller";
        var leftOutSentence = load is null ? null : LoadedCode.LeftOut(load, leftOut, noun);
        lookup.TreesInAnswer.UnionWith(kept.Select(p => p.Site.Tree));

        if (kept.Count == 0)
        {
            lookup.TreesSearched.UnionWith(graphs.Graphs.Select(g => g.Tree));
            if (leftOutSentence is not null) ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
            else SayNone(ctx, lookup, methods, graphs, callees, selection);
            if (!callees) CodeShared.SayOwnTreeUnsearched(ctx, homeTrees, only, "调用处");
            ctx.Report.Table("calls", Columns, []);
            return 1;
        }

        var limit = ctx.Limit(LimitOption);
        var ordered = kept.OrderByDescending(p => p.Count)
                          .ThenBy(p => p.Site.Edge.FromAssembly, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var notLoaded = ordered.Select(p => (p.Site.Tree, Load: LoadOf(p.Site)))
                               .Where(n => n.Load.State != CodeLoad.Loaded)
                               .Select(n => (n.Tree, n.Load.State, n.Load.Assembly))
                               .ToList();

        var rows = ordered.Take(limit.Effective).Select(p => Row(lookup, graphs, p.Site, p.Count, LoadOf(p.Site).State)).ToList();

        var tally = ordered.Count > limit.Effective
            ? Tally.Of(limit.Effective, ordered.Count)
            : Tally.Complete(ordered.Count);

        if (callees)
            ctx.Report.CountNotice(tally, "callee");
        else
            ctx.Report.CountNotice(tally, "caller");

        if (!callees) CodeShared.SayOwnTreeUnsearched(ctx, homeTrees, only, "调用处");
        if (leftOutSentence is not null) ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
        if (load is not null)
            LoadedCode.SayNotLoaded(ctx, load, notLoaded, ordered.Count, noun, null,
                                    callees ? "这些调用" : "这些调用方", "快照未记录其程序集",
                                    callingSide: callees);

        ctx.Report.Table("calls", LoadedCode.WithColumn(Columns, rows, load), rows);
        return 0;
    }

    private static void SayWhatWasSearched(
        CommandContext ctx, CallGraphSet graphs, string root, bool callees, IReadOnlyList<string> homeTrees)
    {
        var blind = callees
            ? graphs.Without.Where(t => homeTrees.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
            : [.. graphs.Without];

        ctx.Report.Absent(DataLayers.CallGraphRows(blind));

        var stale = graphs.Graphs.Where(g => IsStale(root, g)).Select(g => g.Tree).ToList();
        if (stale.Count > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                Cjk.Join(Tally.Complete(stale.Count).Render("source tree"), "的调用图早于程序集的当前版本：") +
                $"{NameList.Render(stale, 6)}。");
    }

    internal static bool IsStale(string root, CallGraph graph)
    {
        var state = SourceTreeState.Read(Path.Combine(root, graph.Tree));
        if (state is null) return false;

        var recorded = state.Assemblies.Select(a => a.Sha256).OrderBy(h => h, StringComparer.Ordinal);
        var built = graph.SourceHashes.OrderBy(h => h, StringComparer.Ordinal);
        return !recorded.SequenceEqual(built, StringComparer.OrdinalIgnoreCase);
    }

    private static void SayNone(
        CommandContext ctx, MetadataLookup lookup, IReadOnlyList<MethodHit> methods,
        CallGraphSet graphs, bool callees, CodeShared.Selection selection)
    {
        var names = NameList.Render([.. methods.Select(m => $"{m.Type.FullName}::{m.DisplayName}").Distinct()], 4);

        if (callees)
        {
            var unread = methods.Select(m => m.Type.Assembly.Tree).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !graphs.Graphs.Any(g => string.Equals(g.Tree, t, StringComparison.OrdinalIgnoreCase))
                         && !graphs.Without.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unread.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    Cjk.Join("--source 不含", names, $"所在的源码树 {NameList.Render(unread, 4)}，因此未读取其调用表。"));
                return;
            }

            var bodyless = methods.Where(m => m.Bodyless).ToList();
            ctx.Report.Notice(NoticeKind.Boundary,
                bodyless.Count == methods.Count
                    ? bodyless.All(m => m.BodyKind == "abstract")
                        ? $"{names} 为 abstract，因此不调用任何方法：实现该方法的类型由 'rimsearcher types " +
                          $"{methods[0].Type.FullName} --derived --transitive' 列出。"
                        : $"{names} 没有方法体（{string.Join("、", bodyless.Select(m => IlCommand.Bodyless(m.BodyKind)).Distinct())}），" +
                          "因此不调用任何方法。"
                    : selection.Assemblies is null ? $"表中没有记录 {names} 调用任何方法。"
                    : Cjk.Join("表中没有记录", names, "调用", selection.Phrase, "中的任何方法。"));
            return;
        }

        var head = selection.Assemblies is null
            ? Cjk.Join("所搜索的", Tally.Complete(graphs.Graphs.Count).Render("source tree"), "中没有调用", names, "的方法。")
            : Cjk.Join(selection.Phrase, "中没有调用", names, "的方法。");

        var member = methods[0].SourceName;
        var bases = DeclaringBases(lookup, methods[0]);

        ctx.Report.Notice(NoticeKind.Boundary,
            bases.Count > 0
                ? head + $"{member} 也由 {NameList.Render(bases, 4)} 声明：" +
                         $"'rimsearcher callers {bases[^1]}.{member}' 计入针对该类型写出的调用。"
                : head);
    }

    private static List<string> DeclaringBases(MetadataLookup lookup, MethodHit method)
    {
        if (!Hierarchy.Overrides(lookup, method.Type).Any(o => o.Handle == method.Handle)) return [];
        using var quiet = lookup.Unnoted();
        lookup.ReadLike(method.Type);

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

    private static IReadOnlyDictionary<string, object?> Row(
        MetadataLookup lookup, CallGraphSet graphs, CallSite site, int count, CodeLoad load)
    {
        var from = lookup.ResolveToken(site.Tree, site.Edge.FromAssembly, site.Edge.FromToken);
        var to = lookup.ResolveToken(site.Tree, site.Edge.ToAssembly, site.Edge.ToToken);
        var recorded = to is null ? graphs.TargetName(site) : null;

        return new Dictionary<string, object?>
        {
            ["tree"] = site.Tree,
            ["from_assembly"] = site.Edge.FromAssembly,
            ["from_type"] = from?.Type.FullName,
            ["from_member"] = from?.DisplayName,
            ["to_assembly"] = site.Edge.ToAssembly,
            ["to_type"] = to?.Type.FullName ?? recorded?.Type,
            ["to_member"] = to?.DisplayName ?? recorded?.Member,
            ["call_sites"] = count,
            ["loaded"] = LoadedCode.Value(load),
        };
    }
}
