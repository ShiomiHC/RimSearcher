using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public enum CodeLoad { Loaded, ModNotInSnapshot, AssemblyNotLoaded, Unknown }

public sealed class LoadedCode
{
    public static readonly CliOption IncludeOption = CliOption.Flag("include-unloaded") with
    {
        Aliases = ["unloaded", "include-not-loaded", "all-code"],
        Help = "一并读取游戏在快照中未加载的代码：快照外的 mod，以及快照内某个 mod 中游戏未加载的程序集。" +
               "不指定时这些代码不进入答案。本次调用指定的源码树或程序集无需本选项即读取。",
    };

    public static bool Include(CommandContext ctx) => ctx.Args.Flag(ctx.Args.Own(IncludeOption.Name));

    public sealed class ReadScope
    {
        private readonly bool _all;
        private readonly HashSet<string> _trees = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _assemblies = new(StringComparer.OrdinalIgnoreCase);

        private ReadScope(bool all) => _all = all;

        public static ReadScope Of(CommandContext ctx, IEnumerable<string>? trees = null,
                                   IEnumerable<(string Tree, string Name)>? assemblies = null)
        {
            var scope = new ReadScope(Include(ctx));
            scope._trees.UnionWith(trees ?? []);
            scope._assemblies.UnionWith((assemblies ?? []).Select(a => Key(a.Tree, a.Name)));
            return scope;
        }

        public bool All => _all;

        public void AddTree(string tree) => _trees.Add(tree);

        public bool Covers(string tree) => _all || _trees.Contains(tree);

        public bool Covers(string tree, string assembly) => Covers(tree) || _assemblies.Contains(Key(tree, assembly));

        public bool Covers(string tree, IEnumerable<string> assemblies) => Covers(tree) || assemblies.Any(a => _assemblies.Contains(Key(tree, a)));

        public bool NamesAssemblies => !_all && _assemblies.Count > 0;

        private static string Key(string tree, string assembly) => tree + "/" + assembly;
    }

    public static bool Unloaded(CodeLoad state) => state is CodeLoad.ModNotInSnapshot or CodeLoad.AssemblyNotLoaded;

    public static Func<ResolvedAssembly, CodeTag> Tagger(LoadedCode? load, ReadScope scope)
        => a => load is null || !Unloaded(load.OfAssembly(a.Tree, a.Name).State) ? CodeTag.Normal
              : scope.Covers(a.Tree, a.Name) ? CodeTag.Marked : CodeTag.Hidden;

    public string Snapshot { get; }

    public string Outside => _given ? "范围外" : $"快照 '{Snapshot}' 范围外";

    private readonly bool _given;

    private readonly Dictionary<string, HashSet<string>?> _mods;

    private readonly TreeAssemblies _files;

    public LoadedCode(string snapshot, IEnumerable<ModRef> mods, string root, bool given = false)
    {
        Snapshot = snapshot;
        _given = given;
        _files = new TreeAssemblies(root);
        _mods = mods.GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key,
                                  g => g.First().Assemblies?.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                                  StringComparer.OrdinalIgnoreCase);
    }

    public static LoadedCode? Open(CommandContext ctx, string root)
    {
        try
        {
            var selection = SnapshotCatalog.Resolve(ctx.Config, ctx.Args.Value(GlobalOptions.Db), ctx.Args.Value(GlobalOptions.Snapshot));
            using var db = SnapshotDb.Open(selection.Path);
            return new LoadedCode(selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path), db.Meta.Mods, root,
                                  selection.Source is SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb);
        }
        catch (Exception ex) when (ex is SnapshotFormatError or CliUsageException)
        {
            return null;
        }
    }

    public (CodeLoad State, string? Assembly) Of(string tree, string relInTree)
    {
        if (SourcePlanner.IsReserved(tree)) return (CodeLoad.Loaded, null);
        if (!_mods.TryGetValue(tree, out var loaded)) return (CodeLoad.ModNotInSnapshot, null);
        if (loaded is null) return (CodeLoad.Unknown, null);

        var assemblies = _files.Names(tree);
        if (assemblies.Count == 0) return (CodeLoad.Unknown, null);
        if (assemblies.All(loaded.Contains)) return (CodeLoad.Loaded, null);

        var owners = _files.Owners(tree, relInTree);
        if (owners.Count == 0) return (CodeLoad.Unknown, null);
        return owners.Any(loaded.Contains) ? (CodeLoad.Loaded, null) : (CodeLoad.AssemblyNotLoaded, owners[0]);
    }

    public (CodeLoad State, string? Assembly) OfAssembly(string tree, string assembly)
    {
        if (SourcePlanner.IsReserved(tree)) return (CodeLoad.Loaded, null);
        if (!_mods.TryGetValue(tree, out var loaded)) return (CodeLoad.ModNotInSnapshot, null);
        if (loaded is null) return (CodeLoad.Unknown, null);
        return loaded.Contains(assembly) ? (CodeLoad.Loaded, null) : (CodeLoad.AssemblyNotLoaded, assembly);
    }

    public static string Value(CodeLoad state) => state switch
    {
        CodeLoad.Loaded => "已加载",
        CodeLoad.ModNotInSnapshot => "mod 不在快照",
        CodeLoad.AssemblyNotLoaded => "程序集未加载",
        _ => "未知",
    };

    public static IReadOnlyList<string> WithColumn(IReadOnlyList<string> columns,
                                                   IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, LoadedCode? load)
        => load is not null && rows.Any(r => r.GetValueOrDefault("loaded") is string v && v != Value(CodeLoad.Loaded))
            ? [.. columns, "loaded"]
            : columns;

    public static void SayNotLoaded(CommandContext ctx, LoadedCode? load,
                                    List<(string Tree, CodeLoad State, string? Assembly)> notLoaded, int counted,
                                    string noun, string? qualifier, string these, string unknownWhy,
                                    bool callingSide = false)
    {
        if (counted == 0) return;
        if (load is null)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("无法打开快照，因此", these, "未标注加载状态。"));
            return;
        }

        var scope = qualifier ?? "";
        var skipped = notLoaded.Where(n => n.State is CodeLoad.ModNotInSnapshot or CodeLoad.AssemblyNotLoaded).ToList();
        if (skipped.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(Tally.Of(skipped.Count, counted).Render(noun, scope), callingSide ? "由" : "在", load.Outside) +
                (callingSide ? "的代码调用：" : "：") + Parts(skipped) + "。");

        var unknown = notLoaded.Where(n => n.State == CodeLoad.Unknown).ToList();
        if (unknown.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("快照", $"'{load.Snapshot}'", callingSide ? "无法判断游戏是否加载调用" : "无法判断游戏是否加载",
                         Tally.Of(unknown.Count, counted).Render(noun, scope),
                         callingSide ? "的代码：" : "所在的代码：") +
                Parts(unknown) + $"（{unknownWhy}）。");
    }

    private static string Parts(IEnumerable<(string Tree, CodeLoad State, string? Assembly)> group)
    {
        var trees = group.GroupBy(n => n.Tree, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join("、", trees.Take(6).Select(g => g.Key + Why(g))) +
               (trees.Count > 6 ? Cjk.Join("，其余", Tally.Complete(trees.Count - 6).Render("source tree")) : "");
    }

    private static string Why(IEnumerable<(string Tree, CodeLoad State, string? Assembly)> tree)
    {
        if (tree.Any(n => n.State != CodeLoad.AssemblyNotLoaded)) return "";
        var names = tree.Select(n => n.Assembly).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 1
            ? $"（程序集 {names[0]} 未加载）"
            : $"（程序集 {string.Join("、", names.Take(names.Count - 1))} 和 {names[^1]} 未加载）";
    }

    public static string? LeftOut(LoadedCode load, IReadOnlyCollection<(string Tree, CodeLoad State, string? Assembly)> leftOut,
                                  string noun, string verb = "")
        => leftOut.Count == 0 ? null :
            Cjk.Join(load.Outside, "有", Tally.Complete(leftOut.Count).Render(noun)) +
            $"{verb}：{Parts(leftOut)}。";

    public static string? ReadAnyway(LoadedCode load, IReadOnlyCollection<(string Tree, CodeLoad State, string? Assembly)> read, string noun)
        => read.Count == 0 ? null :
            Cjk.Join(Tally.Complete(read.Count).Render(noun), "在", load.Outside) + $"：{Parts(read)}。";
}
