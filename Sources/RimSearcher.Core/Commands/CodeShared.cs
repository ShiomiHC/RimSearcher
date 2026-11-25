using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimTooling.Cli;

namespace RimSearcher.Commands;

internal static class CodeShared
{
    internal static readonly CliOption Source = CliOption.Repeat("source") with
    {
        Aliases = ["tree", "from-tree", "mod", "mods"],
        Placeholder = "<源码树>",
        Help = "要读取的反编译源码树。可重复指定以读取多棵。不指定时读取全部源码树；" +
               $"{SourcePlanner.Listed(SourcePlanner.LastResortTrees, describe: true)}只在其他源码树都没有该名称时读取。",
        Narrows = true,
    };

    internal static CliOption AssemblyOption(string help) => CliOption.Repeat("assembly") with
    {
        Aliases = ["assemblies", "asm"],
        Placeholder = "<名称>",
        Help = help + "名称即 'types' 在「程序集」列输出的名称（'callers' 输出在「调用方程序集」与「目标程序集」列，" +
               "'usages' 输出在「使用方程序集」列）。'*' 匹配任意一串字符。可重复指定多个。" +
               "指定程序集即指定其源码树：该源码树即使平时只在指定时读取，此时也会读取。",
        Narrows = true,
    };

    internal sealed record Selection(IReadOnlyList<string> Trees, IReadOnlySet<string>? Assemblies,
                                     IReadOnlyList<(string Tree, string Name)> Exact)
    {
        public bool Keeps(string assembly) => Assemblies is null || Assemblies.Contains(assembly);

        public LoadedCode.ReadScope Scope(CommandContext ctx) => LoadedCode.ReadScope.Of(ctx, CodeShared.Trees(ctx), Exact);

        public string Phrase => Assemblies is null ? ""
            : Assemblies.Count <= 6 ? "程序集 " + SourcePlanner.Listed(Assemblies.Order(StringComparer.OrdinalIgnoreCase))
            : $"{Tally.Complete(Assemblies.Count).Render("assembly")}（" +
              NameList.Render([.. Assemblies.Order(StringComparer.OrdinalIgnoreCase).Select(a => $"'{a}'")], 6) + "）";
    }

    internal static Selection Select(CommandContext ctx, string root, IReadOnlyList<string>? within = null, string by = "--source")
    {
        var sources = Trees(ctx);
        var values = ctx.Args.Values(ctx.Args.Own(AssemblyOptionName))
                        .Select(v => v.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v)
                        .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count == 0) return new Selection(sources, null, []);

        var limit = within ?? (sources.Count > 0 ? sources : null);
        var pool = AssemblyStore.ResolveAll(root, AssemblyStore.InstallRoots(ctx.Config))
                                .Select(a => (a.Tree, a.Name)).Distinct().ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exact = new List<(string Tree, string Name)>();
        var trees = new List<string>();
        foreach (var value in values)
        {
            var match = pool.Where(a => Matches(a.Name, value)).ToList();
            var kept = limit is null ? match : match.Where(a => limit.Contains(a.Tree, StringComparer.OrdinalIgnoreCase)).ToList();
            if (kept.Count == 0)
                throw new CliUsageException(match.Count > 0
                    ? (match.Select(a => a.Tree).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is var outTrees
                       && outTrees.Count == 1
                          ? $"--assembly '{value}' 位于源码树 {outTrees[0]}，而 {by} 未包含该源码树。去掉 {by}，或把该源码树加入其中。"
                          : $"--assembly '{value}' 位于 {SourcePlanner.Listed(outTrees)}，而 {by} 未包含这些源码树。" +
                            $"去掉 {by}，或把这些源码树加入其中。")
                    : $"没有反编译源码树含名为 '{value}' 的程序集。" +
                      Suggestion.Say(Suggestion.Closest(pool.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase), value.Replace("*", ""))) +
                      "类型在各棵源码树中所属的程序集见 'rimsearcher types <名称> --assembly \"*\"'。");
            foreach (var (tree, name) in kept)
            {
                names.Add(name);
                if (!value.Contains('*')) exact.Add((tree, name));
                if (!trees.Contains(tree, StringComparer.OrdinalIgnoreCase)) trees.Add(tree);
            }
        }
        return new Selection(limit ?? trees, names, exact);
    }

    internal const string AssemblyOptionName = "assembly";

    internal static bool Matches(string name, string pattern)
        => pattern.Contains('*')
            ? System.Text.RegularExpressions.Regex.IsMatch(name,
                  "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
                  System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> Trees(CommandContext ctx)
        => [.. ctx.Args.Values(ctx.Args.Own(Source.Name)).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    internal static MetadataLookup Open(CommandContext ctx, out string root, bool everyTree = false)
        => Open(ctx, out root, out _, everyTree);

    internal static MetadataLookup Open(CommandContext ctx, out string root, out Selection selection, bool everyTree = false)
    {
        root = SourcesShared.Root(ctx);
        if (!Directory.Exists(root))
            throw new CliUsageException(
                $"'{root}' 不存在，因此没有可读的程序集。" +
                "该目录由 'rimsearcher sources sync' 创建。");

        foreach (var t in Trees(ctx))
            if (!Directory.Exists(Path.Combine(root, t)))
                throw new CliUsageException(
                    $"没有名为 '{t}' 的反编译源码树。全部源码树见 'rimsearcher sources list'。");
        selection = Select(ctx, root);
        var only = selection.Trees;

        var narrowed = !everyTree && only.Count > 0;
        var all = AssemblyStore.ResolveAll(root, AssemblyStore.InstallRoots(ctx.Config), narrowed ? only : null);
        var assemblies = Split(all, only, out var lastResort);
        if (!everyTree && selection.Assemblies is { } keep) assemblies = [.. assemblies.Where(a => keep.Contains(a.Name))];
        if (assemblies.Count == 0)
            throw new CliUsageException(
                (narrowed
                    ? only.Count == 1
                        ? $"源码树 '{only[0]}' 未列出本机可访问的程序集。"
                        : $"源码树 {NameList.Render([.. only.Select(t => $"'{t}'")], 6)} 均未列出本机可访问的程序集。"
                    : "没有源码树列出本机可访问的程序集。") +
                "程序集文件由 'rimsearcher sources sync' 复制进来。");

        if (narrowed && only.Count > 1)
        {
            var bare = only.Where(t => !assemblies.Any(a => string.Equals(a.Tree, t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (bare.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"--source 中的 {NameList.Render([.. bare.Select(t => $"'{t}'")], 6)} 未列出本机可访问的程序集，" +
                    "因此未从中读取任何内容。程序集文件由 'rimsearcher sources sync' 复制进来。");
        }

        var load = LoadedCode.Open(ctx, root);
        var lookup = new MetadataLookup(assemblies, TierOf(only), LoadedCode.Tagger(load, selection.Scope(ctx)), lastResort);
        Announce(ctx, lookup, only);
        SayUnloaded(ctx, lookup, load);
        return lookup;
    }

    private static void SayUnloaded(CommandContext ctx, MetadataLookup lookup, LoadedCode? load)
    {
        ctx.Report.LateNotice(NoticeKind.Boundary, () =>
        {
            if (load is null)
                return lookup.TreesInAnswer.Any(t => !SourcePlanner.IsReserved(t))
                    ? "无法打开快照，因此游戏未加载的代码与已加载的代码无从区分。"
                    : null;
            var said = lookup.Notes
                .GroupBy(n => (n.Hidden, n.Noun))
                .Select(g =>
                {
                    var rows = g.Select(n => (n.Assembly.Tree, load.OfAssembly(n.Assembly.Tree, n.Assembly.Name).State,
                                              (string?)n.Assembly.Name)).ToList();
                    return g.Key.Hidden ? LoadedCode.LeftOut(load, rows, g.Key.Noun) : LoadedCode.ReadAnyway(load, rows, g.Key.Noun);
                })
                .OfType<string>().ToList();
            return said.Count == 0 ? null : string.Concat(said);
        });
    }

    private static List<ResolvedAssembly> Split(IReadOnlyList<ResolvedAssembly> all, IReadOnlyList<string> only,
                                                out List<ResolvedAssembly> lastResort)
    {
        bool Last(ResolvedAssembly a) => SourcePlanner.IsLastResort(a.Tree) && !only.Contains(a.Tree, StringComparer.OrdinalIgnoreCase);
        lastResort = [.. all.Where(Last)];
        return [.. all.Where(a => !Last(a))];
    }

    private static Func<string, int> TierOf(IReadOnlyList<string> only)
        => tree => only.Contains(tree, StringComparer.OrdinalIgnoreCase) ? 0 : SourcePlanner.NameTier(tree);

    internal static MetadataLookup? OpenQuietly(CommandContext ctx)
    {
        try
        {
            var root = SourcesShared.Root(ctx);
            if (!Directory.Exists(root)) return null;
            var assemblies = Split(AssemblyStore.ResolveAll(root, AssemblyStore.InstallRoots(ctx.Config)), [], out var lastResort);
            return assemblies.Count == 0 ? null
                : new MetadataLookup(assemblies, TierOf([]), LoadedCode.Tagger(LoadedCode.Open(ctx, root), LoadedCode.ReadScope.Of(ctx)), lastResort);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (CliUsageException) { return null; }
    }

    private static void Announce(CommandContext ctx, MetadataLookup lookup, IReadOnlyList<string> only)
    {
        var copies = new List<ResolvedAssembly>();
        void Opened(IEnumerable<ResolvedAssembly> batch, int unreadableBefore)
        {
            var items = batch.ToList();
            copies.AddRange(items.Where(a => a.Origin == AssemblyOrigin.Copy));
            var installed = items.Where(a => a.Origin == AssemblyOrigin.Installed).ToList();
            if (installed.Count > 0)
                ctx.Report.Absent([.. installed.Take(6).Select(a => DataLayers.AssemblyCopyRow(a.Tree, a.Name))]);
            var unreadable = lookup.Unreadable.Skip(unreadableBefore).ToList();
            if (unreadable.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    Cjk.Join(Tally.Complete(unreadable.Count).Render("assembly"), "完全无法打开，因此未搜索：") +
                    $"{Trees(unreadable)}。");
        }

        List<ResolvedAssembly> Read(List<ResolvedAssembly> of, bool searchedToo)
            => [.. of.Where(a => only.Contains(a.Tree, StringComparer.OrdinalIgnoreCase)
                              || lookup.TreesInAnswer.Contains(a.Tree)
                              || searchedToo && lookup.TreesSearched.Contains(a.Tree))];

        ctx.Report.LateNotice(NoticeKind.Staleness, () =>
        {
            var read = Read(copies, searchedToo: true).Where(a => a.OriginalChanged == true).ToList();
            return read.Count == 0 ? null :
                Cjk.Join(Tally.Complete(read.Count).Render("assembly"), "的本机安装版本，在源码树旁留存副本之后又有改动：") +
                $"{Trees(read)}。";
        });

        ctx.Report.LateNotice(NoticeKind.Boundary, () =>
        {
            var read = Read([.. copies.Where(a => a.OriginalMissing)], searchedToo: false);
            return read.Count == 0 ? null :
                Cjk.Join(Tally.Complete(read.Count).Render("assembly"), "已不再安装，只能读取源码树旁留存的副本：") +
                $"{Trees(read)}。";
        });

        Opened(lookup.Assemblies, 0);
        var unreadableAtFirst = lookup.Unreadable.Count;
        lookup.LastResortOpened = batch => Opened(batch.Except(lookup.Unreadable), unreadableAtFirst);
    }

    private static string Trees(IReadOnlyList<ResolvedAssembly> items)
        => NameList.Render([.. items.Select(a => $"{a.Tree}/{a.Name}")], 6);

    internal static void SayOwnTreeUnsearched(CommandContext ctx, IEnumerable<string> homeTrees, IReadOnlyList<string> only, string sites)
    {
        if (only.Count > 0) return;
        var last = homeTrees.Where(SourcePlanner.IsLastResort).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (last.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"未搜索 {SourcePlanner.Listed(last)} 中的{sites}，由 '{string.Join(" ", last.Select(t => "--source " + t))}' 搜索。");
    }

    internal static bool LeftOutOnly(MetadataLookup lookup) => lookup.Notes.Any(n => n.Hidden);

    internal static void SayNoType(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol)
    {
        lookup.AnsweredFromEveryTree();
        if (LeftOutOnly(lookup)) return;

        if (OutsideAssemblies(ctx, lookup, symbol) is { } outside)
        {
            ctx.Report.Notice(NoticeKind.Boundary, outside);
            return;
        }

        var whole = symbol.AsWholeType().TypeName;
        var inNamespace = lookup.AllTypes(withHidden: true)
            .Where(t => !t.CompilerGenerated &&
                        (string.Equals(t.Namespace, whole, StringComparison.OrdinalIgnoreCase) ||
                         t.Namespace.StartsWith(whole + ".", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var t in inNamespace) lookup.Note(t.Assembly, "type", t.FullName);
        inNamespace.RemoveAll(t => lookup.TagOf(t.Assembly) == CodeTag.Hidden);

        using var quiet = lookup.Unnoted();
        var near = lookup.AllTypes()
                         .Where(t => !t.CompilerGenerated && t.Name.Contains(symbol.TypeName, StringComparison.OrdinalIgnoreCase))
                         .Take(6).Select(t => t.FullName).ToList();
        var members = symbol.HasMember || symbol.TypeName.Contains('.')
            ? [] : NameLookup.Beyond(ctx, symbol.TypeName, NameLookup.Side.Code);
        NameLookup.Say(ctx, members);

        if (inNamespace.Count > 0)
            NameLookup.Say(ctx, new NameLookup.Sighting(
                NameLookup.Where.CodeMember, whole, "C# 命名空间",
                Cjk.Join(NameList.Render([.. inNamespace.Select(t => $"{t.Assembly.Tree}/{t.Assembly.Name}").Distinct(StringComparer.OrdinalIgnoreCase)], 4),
                         "中有", Tally.Complete(inNamespace.Count).Render("type")),
                $"{CommandRegistry.ExeName} types --namespace {whole}"));

        ctx.Report.Notice(NoticeKind.Boundary,
            Cjk.Join("本次读取的", Tally.Complete(lookup.AssemblyCount).Render("assembly"), $"中没有名为 '{symbol.TypeName}' 的类型") +
            (near.Count > 0 ? $"。名称含 '{symbol.TypeName}' 的类型：{NameList.Render(near, 6)}" : "") +
            "。");
    }

    private static string? OutsideAssemblies(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol)
    {
        var given = ctx.Args.Values(ctx.Args.Own(AssemblyOptionName))
                       .Select(v => v.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v)
                       .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (given.Count == 0) return null;

        var said = new List<string>();
        if (lookup.AssemblyCount == 0 && lookup.HasHidden && given.Any(g => g.Contains('*')))
            said.Add("--assembly 选中的程序集都在游戏未加载的代码中，而带 '*' 的 --assembly 不读这部分代码。");

        using var wide = OpenQuietly(ctx);
        wide?.Ask([symbol.TypeName]);
        var elsewhere = (wide?.FindTypes(symbol.TypeName) ?? [])
            .Where(t => !given.Any(g => Matches(t.Assembly.Name, g)))
            .Select(t => (t.Assembly.Tree, t.Assembly.Name)).Distinct().ToList();
        if (elsewhere.Count > 0)
            said.Add($"'{symbol.TypeName}' 在 {NameList.Render([.. elsewhere.Select(a => $"{a.Tree}/{a.Name}")], 4)} 中，" +
                     "不在 --assembly 保留的程序集里。要从该类型查起，把该类型所在的程序集也写进 --assembly：" +
                     $"再加 '--assembly {elsewhere[0].Name}'。");

        return said.Count == 0 ? null : string.Join("", said);
    }

    internal static void SayNoMember(CommandContext ctx, MetadataLookup lookup, TypeHit type, string member)
    {
        using var quiet = lookup.Unnoted();
        lookup.TreesInAnswer.Add(type.Assembly.Tree);
        foreach (var holder in new[] { type.FullName }.Concat(new Hierarchy(lookup).BaseChain(type.FullName)))
        {
            var t = holder == type.FullName ? type : lookup.FindTypes(holder).FirstOrDefault();
            if (t is null) continue;
            var field = lookup.FindFields(t, member).FirstOrDefault();
            if (field.Name is null) continue;
            NameLookup.Say(ctx, new NameLookup.Sighting(
                NameLookup.Where.CodeMember, $"{type.FullName}.{member}", "C# 字段",
                $"{t.FullName}（{t.Assembly.Tree}/{t.Assembly.Name}）",
                $"{CommandRegistry.ExeName} usages {t.FullName}.{field.Name}"));
            return;
        }

        var bases = new Hierarchy(lookup).BaseChain(type.FullName)
                        .SelectMany(b => lookup.FindTypes(b).Take(1))
                        .Where(b => lookup.FindMethods(b, member).Count > 0
                                 || lookup.Members(b).Any(m => string.Equals(m.Name, member,
                                                                             StringComparison.OrdinalIgnoreCase)))
                        .Select(b => b.FullName).Take(4).ToList();

        if (bases.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{type.FullName} 未声明名为 '{member}' 的成员，该成员继承自基类型：基类型链在 " +
                $"{NameList.Render(bases, 4)} 上声明该成员。到声明处查询：" +
                $"'rimsearcher il {bases[0]}.{member}'。" +
                $"{type.FullName} 继承的成员与每个成员的声明类型见 'rimsearcher members {type.FullName} --inherited'。");
            return;
        }

        var elsewhere = lookup.FindMethodsAnywhere(member, 6)
                              .Where(m => m.Type.FullName != type.FullName)
                              .Select(m => m.Type.FullName).Distinct().Take(4).ToList();

        ctx.Report.Notice(NoticeKind.Boundary,
            $"{type.FullName} 没有名为 '{member}' 的成员，该类型的基类型链上也没有" +
            (elsewhere.Count > 0
                ? $"。带有该名称的无关类型：{NameList.Render(elsewhere, 4)}"
                : "") +
            $"。{type.FullName} 现有的成员见 'rimsearcher members {type.FullName}'。");
    }
}
