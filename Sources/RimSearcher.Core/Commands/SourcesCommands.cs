using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

internal static class SourcesShared
{
    internal const string StagingDir = ".staging";

    internal const string FromSnapshot = "快照";

    internal static string Root(CommandContext ctx)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root))
            throw new CliUsageException(
                "未配置反编译源码树，因此无处存放 C#。在配置文件中把 decompiled_dir 设为存放 C# 的目录。");
        return Path.GetFullPath(root);
    }

    internal static string NotConfiguredToRead(string action)
    {
        return $"未配置反编译源码树，因此没有可{action}的内容。" +
               "在配置文件中把 decompiled_dir 设为存放反编译 C# 的目录。" +
               "'types'、'members'、'callers' 与 'il' 也需要这个目录：未配置时这四个命令无法读取程序集。";
    }

    internal static bool IsGitRoot(string dir) => SourceGit.IsRepository(dir);

    internal static IEnumerable<string> TreeNames(string root)
        => Directory.EnumerateDirectories(root)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => n.Length > 0 && !n.StartsWith('.'));

    internal static IEnumerable<string> TreesOfTier(string root, int tier)
        => TreeNames(root).Where(n => SourcePlanner.NameTier(n) == tier);

    internal static void SayReserved(CommandContext ctx, IReadOnlyList<string> reserved)
    {
        if (reserved.Count == 0) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            $"{Tally.Complete(reserved.Count).Render("mod")} 的 packageId 是保留的源码树名" +
            $"（{string.Join("、", new[] { SourcePlanner.VanillaTree }.Concat(SourcePlanner.GameDirTrees))}），" +
            $"因此这些 mod 没有自己的源码树，它们的 C# 无法读取：{string.Join("、", reserved)}。");
    }

    internal static string GameDirSentence(IReadOnlyList<string> here, string from)
    {
        if (here.Count == 0) return "";
        var who = string.Join("，", here.Select(t => $"{t} 为{SourcePlanner.Describe(t)}"));
        var byName = new List<string>();
        if (here.Where(t => SourcePlanner.NameTier(t) > 0).ToList() is { Count: > 0 } onMiss)
            byName.Add($"只在其他树都没有该名字时读取 {SourcePlanner.Listed(onMiss)}");
        if (here.Where(SourcePlanner.IsLastResort).ToList() is { Count: > 0 } last)
            byName.Add($"'callers' 与 'usages' 只在点名时搜索 {SourcePlanner.Listed(last)} 中的调用处与使用处");
        var engine = byName.Count > 0 ? "；按名字查找的命令" + string.Join("，", byName) : "";
        return here.Count == 1
            ? $"{who}，只依 game_dir 列入计划；'code-search' 只在点名时搜索该树" +
              engine + "。"
            : $"{who}，{(here.Count == 2 ? "两者" : "这些树")}均只依 game_dir 列入计划；" +
              "'code-search' 只在点名时搜索其中每一棵" + engine + "。";
    }

    internal static void SayHowToDiff(CommandContext ctx, string root)
    {
        if (IsGitRoot(root))
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"版本间的改动见 git：sync 每改写一棵树就提交一次，'git -C \"{root}\" log --stat' " +
                "列出这些提交，'git show <commit>' 显示一次提交的改动，单个文件的历史见 'git log -p -- <文件>'。", teach: true);
            return;
        }

        ctx.Report.Notice(NoticeKind.NextStep,
            $"{root} 不是 git 仓库，因此没有可对照的历史。" +
            "在该目录执行 'git init' 并提交一次；此后每次同步都会提交当次改写的树。" +
            "仅在本地保存：这是反编译的游戏代码，不得公开发布。");
    }

    internal static (IReadOnlyList<string> Ids, string GameVersion, string From,
                     IReadOnlyDictionary<string, IReadOnlyList<Snapshot.LoadedAssembly>>? Loaded) PackageIds(CommandContext ctx, CliOption? modlist)
    {
        var listName = ctx.Args.Value(modlist);
        if (listName is { Length: > 0 })
        {
            var list = ModListIo.Resolve(ctx.Config, listName);
            var version = GameBuild.Installed(ctx.Config.GameDir) ?? ctx.Db.Meta.GameVersion;
            return (list.Ids, version, Cjk.Join("mod 名单", listName), null);
        }

        var ids = ctx.Db.Mods.Select(m => m.PackageId).ToList();
        var loaded = ctx.Db.Meta.Mods.Where(m => m.Assemblies is not null)
                        .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Assemblies!, StringComparer.OrdinalIgnoreCase);
        return (ids, ctx.Db.Meta.GameVersion, FromSnapshot, loaded);
    }
}

public sealed class SourcesListCommand : Command
{
    public override CliOperation Operation { get; } = new()
    {
        Name = "sources list",
        Verb = "list",
        Aliases = ["sources status", "sources check"],
        Summary = "列出反编译源码树，并指出哪些已与已安装的程序集不匹配。",
        Remarks =
            "「程序集已改动」表示已安装的程序集文件与这棵树所出自的不同。" +
            "「引用的程序集已改动」表示程序集文件匹配，但其引用的某个程序集此后已被另一版本替换。" +
            "「反编译器不匹配」表示程序集文件匹配，但这棵树由另一版反编译器或另一组反编译器设置写出。" +
            "「空树（不在快照）」的目录不在同步计划内。\n\n" +
            "「副本数」「调用边数」与「访问边数」三列说明一棵树是否带有所出自程序集的副本、调用图，以及字段访问与类型引用的表。没有副本的树上，'il' 退回读取已安装的程序集文件；" +
            "没有这些表的树上，'callers' 与 'usages' 看不到该树的内部。" +
            "'sources sync' 重建同步计划中不匹配的树，并补上缺少的副本与这些表。\n\n" +
            "'sources list' 不比对源码内部的改动。",
        Options = [],
        UsesGlobals = true,
        Examples = ["rimsearcher sources list"],
        JsonKeys = [new("trees",
            "每棵反编译源码树一行：tree、files、assemblies、copies、edges、accesses、status，" +
            "以及 assembly_names：一个数组，列出 assemblies 所数的程序集，各按 --assembly 接受的写法。",
            Rows: true)],
    };

    public override int Run(CommandContext ctx)
    {
        var root = SourcesShared.Root(ctx);
        if (!Directory.Exists(root))
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{root} 尚不存在，因此没有可搜索的反编译 C#。" +
                "'rimsearcher sources sync' 会创建该目录。");
            return 1;
        }

        var (ids, gameVersion, from, loaded) = SourcesShared.PackageIds(ctx, modlist: null);
        var installed = InstalledMods.Scan(ctx.Config);
        var plans = SourcePlanner.Plan(ctx.Config, ids, gameVersion, installed, out var notInstalled, out var reserved, loaded)
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var onDisk = SourcesShared.TreeNames(root)
                                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var stale = 0;
        var otherBuild = 0;
        var changedRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var oldDecompiler = 0;
        var missing = 0;
        var current = 0;
        var outside = 0;
        var empty = 0;
        var unchecked_ = 0;
        var modulesNow = CallGraphFreshness.Index(plans.Values.SelectMany(p => p.Assemblies));

        foreach (var name in onDisk.Union(plans.Keys, StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(root, name);
            var exists = Directory.Exists(dir);
            var state = exists ? SourceTreeState.Read(dir) : null;
            var files = exists ? CountFiles(dir) : 0;

            string status;
            plans.TryGetValue(name, out var plan);
            var gameDirTree = SourcePlanner.IsGameDirTree(name);
            var unplanned = gameDirTree ? SourcePlanner.GameDirUnplanned(ctx.Config, name) : null;
            if (exists && files == 0 && plan is null)
            {
                status = gameDirTree ? $"空树（{unplanned}）" : "空树（不在快照）";
                empty++;
            }
            else if (plan is null && gameDirTree)
                { status = unplanned!; unchecked_++; }
            else if (plan is null)
                { status = "不在快照"; outside++; }
            else
            {
                var cs = TreeFreshness.Cs(dir, state, SourcePlanner.Manifest(plan, gameVersion), n => modulesNow.GetValueOrDefault(n));
                switch (cs.State)
                {
                    case CsFreshness.Unbuilt: status = exists ? "无清单" : "未建"; missing++; break;
                    case CsFreshness.AssembliesChanged: status = "程序集已改动"; stale++; break;
                    case CsFreshness.ReferencesChanged:
                        status = "引用的程序集已改动"; otherBuild++; changedRefs.UnionWith(cs.ChangedReferences); break;
                    case CsFreshness.OldDecompiler: status = "反编译器不匹配"; oldDecompiler++; break;
                    case CsFreshness.NoSource: status = "空树"; empty++; break;
                    default: status = "匹配"; current++; break;
                }
            }

            var copies = exists ? CountCopies(dir) : 0;
            var peek = exists ? CallGraphStore.Peek(dir) : null;

            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = name,
                ["files"] = exists ? files.ToString() : "",
                ["assemblies"] = plan is null ? (state?.Assemblies.Count.ToString() ?? "") : plan.Assemblies.Count.ToString(),
                ["assembly_names"] = (plan is null
                        ? state?.Assemblies.Select(a => KeptAssemblies.AssemblyName(dir, a)) ?? []
                        : plan.Assemblies.Select(AssemblyFilter.AssemblyName))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                ["copies"] = exists ? copies.ToString() : "",
                ["edges"] = peek is { } p ? p.Edges.ToString() : exists ? "无" : "",
                ["accesses"] = peek is { } q ? q.Accesses.ToString() : exists ? "无" : "",
                ["status"] = status,
            });
        }

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{root} 下尚无反编译源码树。'rimsearcher sources sync' 会建出这些源码树。");
            return 1;
        }

        ctx.Report.Notice(NoticeKind.Boundary,
            Cjk.Join(root, "下有", Tally.Complete(rows.Count).Render("source tree"), "，已对照", from) +
            $"（{Tally.Complete(ids.Count).Render("mod")}，" +
            $"游戏版本 {SourcePlanner.NormalizeGameVersion(gameVersion)}）。");

        var vanillaIds = plans.ContainsKey(SourcePlanner.VanillaTree) ? ids.Count(SourcePlanner.IsVanilla) : 0;
        var exporterIds = ids.Count(i => Contract.IntermediateFormat.IsExporterPackageId(i));
        var ownTree = plans.Keys.Count(k => !SourcePlanner.IsReserved(k));
        var noCode = ids.Count - vanillaIds - exporterIds - notInstalled.Count - reserved.Count - ownTree;
        var gameDirHere = SourcePlanner.GameDirTrees
                                       .Where(t => plans.ContainsKey(t) || onDisk.Contains(t, StringComparer.OrdinalIgnoreCase))
                                       .ToList();

        ctx.Report.Notice(NoticeKind.Count,
            Cjk.Join(from, $"中的 mod（{ids.Count}）：自有源码树 {ownTree}，") +
            $"并入唯一一棵 {SourcePlanner.VanillaTree} 树 {vanillaIds}，" +
            $"没有可反编译的程序集 {noCode}，本机未安装 {notInstalled.Count}，" +
            (reserved.Count > 0 ? $"packageId 为保留树名 {reserved.Count}，" : "") +
            $"导出器本身 {exporterIds}。" +
            $"源码树（{rows.Count}）：匹配 {current}，程序集已改动 {stale}，" +
            (otherBuild > 0 ? $"引用的程序集已改动 {otherBuild}，" : "") +
            (oldDecompiler > 0 ? $"反编译器不匹配 {oldDecompiler}，" : "") +
            $"未建 {missing}，" +
            Cjk.Join($"不含 .cs 文件 {empty}，来自", from, $"之外 {outside}") +
            (unchecked_ > 0 ? $"，未对照 game_dir {unchecked_}" : "") + "。" +
            SourcesShared.GameDirSentence(gameDirHere, from));

        SayUnloadedBehind(ctx, root, onDisk, plans, from, n => modulesNow.GetValueOrDefault(n));

        if (stale > 0 || otherBuild > 0 || oldDecompiler > 0 || missing > 0 || empty > 0)
        {
            var parts = new List<string>();
            if (stale > 0)
                parts.Add($"出自此后已改动的程序集（{Tally.Complete(stale).Render("source tree")}）");
            if (otherBuild > 0)
                parts.Add($"反编译时所据的 {NameList.Render([.. changedRefs], 4)} 此后已被另一版本替换" +
                          $"（{Tally.Complete(otherBuild).Render("source tree")}）");
            if (oldDecompiler > 0)
                parts.Add($"由另一版反编译器或另一组设置写出（{Tally.Complete(oldDecompiler).Render("source tree")}）");
            if (missing > 0)
                parts.Add($"从未建过（{Tally.Complete(missing).Render("source tree")}）");
            if (empty > 0)
                parts.Add($"目录中没有 .cs 文件（{Tally.Complete(empty).Render("source tree")}）");

            ctx.Report.Notice(NoticeKind.Staleness,
                "未处于匹配状态：" + string.Join("；", parts) + "。");
        }

        SourcesShared.SayReserved(ctx, reserved);

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(from, "中有", Tally.Complete(notInstalled.Count).Render("mod"), "未在本机安装，因此无法为其建树：") +
                $"{string.Join("、", notInstalled)}。");

        ctx.Report.Table("trees", ["tree", "files", "assemblies", "copies", "edges", "accesses", "status"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
    }

    private static void SayUnloadedBehind(CommandContext ctx, string root, IReadOnlyList<string> onDisk,
                                          IReadOnlyDictionary<string, SourceTreePlan> plans, string from,
                                          Func<string, IReadOnlyCollection<Guid>?> modulesNow)
    {
        var behind = new List<(string Tree, string Assembly, string File, bool FileChanged, bool RefsChanged)>();
        foreach (var name in onDisk)
        {
            if (SourcePlanner.IsReserved(name)) continue;
            var dir = Path.Combine(root, name);
            if (SourceTreeState.Read(dir) is not { } state) continue;
            plans.TryGetValue(name, out var plan);
            var planned = plan?.Assemblies.Select(a => AssemblyStore.Relative(plan.Root, a)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                          ?? [];
            foreach (var unit in state.Assemblies.Where(a => !planned.Contains(a.Path)))
            {
                var dll = Path.Combine(plan?.Root ?? state.Root, unit.Path);
                if (!File.Exists(dll)) continue;
                string? hash;
                try { hash = AssemblyFilter.Sha256(dll); }
                catch { hash = null; }
                var refs = state.ReferencesOf(unit);
                var fileChanged = hash != unit.Sha256;
                var refsChanged = refs is not null && CallGraphFreshness.Changed(refs, modulesNow).Count > 0;
                if (!fileChanged && !refsChanged) continue;
                behind.Add((name, KeptAssemblies.AssemblyName(dir, unit), Path.GetFileName(unit.Path),
                            fileChanged, refsChanged));
            }
        }
        if (behind.Count == 0) return;

        var loaders = behind.ToDictionary(b => (b.Tree, b.Assembly), _ => new List<string>());
        foreach (var entry in SnapshotCatalog.Enumerate(ctx.Config))
        {
            try
            {
                using var db = SnapshotDb.Open(entry.Path);
                foreach (var mod in db.Meta.Mods)
                    foreach (var a in mod.Assemblies ?? [])
                        if (loaders.TryGetValue((mod.PackageId, a.Name), out var names) && !names.Contains(entry.Alias))
                            names.Add(entry.Alias);
            }
            catch {  }
        }

        var allFileChanged = behind.All(b => b.FileChanged);
        var allRefsChanged = behind.All(b => b.RefsChanged);
        var mixedCauses = behind.Any(b => b.FileChanged != allFileChanged || b.RefsChanged != allRefsChanged);
        var items = behind.Select(b =>
        {
            var loader = loaders[(b.Tree, b.Assembly)] is { Count: > 0 } names
                ? $"由 {NameList.Render(names, 3)} 加载"
                : "没有快照加载它";
            var cause = !mixedCauses ? ""
                : b.FileChanged && b.RefsChanged ? "程序集文件与所对照的程序集均已改动；"
                : b.FileChanged ? "出自此后已改动的程序集文件；"
                : "所对照的程序集此后已改动；";
            return $"{b.Tree}/{b.File}（{cause}{loader}）";
        });
        var whyText = mixedCauses
            ? "而它们各自的程序集或所对照的程序集此后已改动"
            : allFileChanged && allRefsChanged
                ? "而它们出自此后已改动的程序集文件，且所对照的程序集此后也已改动"
                : allFileChanged
                    ? "而它们出自此后已改动的程序集文件"
                    : "而它们所对照的程序集此后已改动";
        ctx.Report.Notice(NoticeKind.Staleness,
            Cjk.Join("以下程序集在", from, "下未加载，因此以", from, "执行的 sync 会原样保留它们；") +
            whyText +
            $"（{Tally.Complete(behind.Count).Render("assembly")}）：{NameList.Render([.. items], 6)}。");
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    private static int CountCopies(string dir)
    {
        try
        {
            var copies = AssemblyStore.CopyDirectory(dir);
            return Directory.Exists(copies)
                ? Directory.EnumerateFiles(copies, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }
}

public sealed class SourcesSyncCommand : Command
{
    private static readonly CliOption Modlist = CliOption.Text("modlist") with
    {
        Aliases = ["list", "profile"],
        Placeholder = "<名称>",
        Help = "从这份已保存的 mod 名单中取 mod，而非从快照中取。",
    };

    private static readonly CliOption Only = CliOption.Text("only") with
    {
        Aliases = ["tree", "mod"],
        Placeholder = "<名称>",
        Help = "只建这一棵树。接受 'sources list' 输出的树名。",
    };

    private static readonly CliOption Force = CliOption.Flag("force") with
    {
        Aliases = ["rebuild"],
        Help = "连已匹配的树也重建。不覆盖有未提交 git 改动的树。",
    };

    private static readonly CliOption DryRun = CliOption.Flag("dry-run") with
    {
        Aliases = ["plan"],
        Help = "报告将要反编译的内容后停止，不改动任何源码树。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "sources sync",
        Verb = "sync",
        Aliases = ["sources decompile", "decompile"],
        Summary = "把游戏实际加载的程序集反编译到配置的源码树中。",
        Remarks =
            "纳入本次同步的 mod 默认取自快照。" +
            "每个 mod 内只反编译游戏会加载的程序集；取自快照时，Assemblies/ 之外但游戏实际加载的程序集也一并反编译。\n\n" +
            "每个含程序集的 mod 一棵树，Ludeon 的模块同属 vanilla。树中游戏在本次同步所用的 mod 名单下未加载的程序集原样带入新树，包括其 C#、副本与调用处。" +
            "'sources list' 点名其中已改动的程序集，以及加载它们的快照。\n\n" +
            "每棵树还会得到其所出自程序集的副本，以及调用图，'read'、'il' 与 'callers' 都从该树所出自的程序集版本回答。",
        Options =
        [
            Modlist,
            Only,
            Force,
            DryRun,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher sources sync",
            "rimsearcher sources sync --dry-run",
            "rimsearcher sources sync --only erdelf.humanoidalienraces --force",
        ],
        JsonKeys =
        [
            new("rebuilt",
                "不带 --dry-run 时，每棵重建的树一行：tree、assemblies、files、edges、accesses。" +
                "调用图未能建成的树，edges 与 accesses 为 null。此时不出现 plan。"),
            new("plan",
                "带 --dry-run 时，每棵将要重建的树一行：tree、assemblies、reason、root。" +
                "此时不出现 rebuilt。"),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var root = SourcesShared.Root(ctx);
        var (ids, gameVersion, from, loaded) = SourcesShared.PackageIds(ctx, Modlist);
        var installed = InstalledMods.Scan(ctx.Config);
        var plans = SourcePlanner.Plan(ctx.Config, ids, gameVersion, installed, out var notInstalled, out var reserved, loaded);
        var everyPlan = plans;
        var loose = LooseAssemblies(ctx, ids, gameVersion, installed, loaded);

        var only = ctx.Args.Value(Only);
        if (only is { Length: > 0 })
        {
            var kept = plans.Where(p => p.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == 0 && SourcePlanner.IsGameDirTree(only))
            {
                var tree = SourcePlanner.GameDirTrees.First(t => t.Equals(only, StringComparison.OrdinalIgnoreCase));
                throw new CliUsageException(
                    $"{tree} 只依 game_dir 建立" +
                    (SourcePlanner.GameDirUnplanned(ctx.Config, tree) == SourcePlanner.GameDirNotSet
                        ? "；当前未配置 game_dir。在配置文件中把 game_dir 设为 RimWorld 安装目录。"
                        : $"，而 {ctx.Config.GameDir} 中{SourcePlanner.GameDirSource(tree)}。"));
            }
            if (kept.Count == 0)
                throw new CliUsageException(
                    Cjk.Join("依", from, "不会建出名为", $"'{only}'", "的树。") +
                    (loose.TryGetValue(only, out var looseFiles)
                        ? $"其程序集文件位于{LooseWhere}（{NameList.Render(looseFiles, 4)}）。" +
                          LooseMechanism(ctx) + LooseNextStep(ctx)
                        : "") +
                    (plans.Count > 0
                        ? $"可建的树：{NameList.Render([.. plans.Select(p => p.Name)], Limits.MaxSuggestions)}。"
                        : ""));
            plans = kept;
            loose = loose.Where(p => p.Key.Equals(only, StringComparison.OrdinalIgnoreCase))
                         .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }

        if (plans.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join("没有可反编译的内容：", from, "中没有已安装且带程序集的 mod") +
                (ctx.Config.GameDir is { Length: > 0 } ? "" : "，且未配置 game_dir，" +
                    "因此游戏自身的程序集也找不到") + "。");
            return 1;
        }

        var force = ctx.Args.Flag(Force);
        var dryRun = ctx.Args.Flag(DryRun);

        ctx.Report.Promises(dryRun ? "plan" : "rebuilt");
        SayPlanIsNotWhatTheGameLoaded(ctx,
            only is { Length: > 0 } ? plans.Select(p => p.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                                    : ids.ToHashSet(StringComparer.OrdinalIgnoreCase),
            everyPlan);
        SayLooseAssemblies(ctx, loose);

        var work = new List<(SourceTreePlan Plan, SourceTreeState Manifest, string Reason)>();
        var skipped = new List<string>();
        var filledCopiesAndGraph = new List<string>();
        var filledCopiesWithRefresh = new List<string>();
        var filledGraphOnly = new List<string>();
        var blocked = new List<string>();
        var dirty = new List<string>();
        var gitFailed = new List<(string Tree, string Error)>();
        var clashing = new List<string>();
        var modulesNow = CallGraphFreshness.Index(everyPlan.SelectMany(p => p.Assemblies));
        var resolving = new ReferencePlanner(ctx.Config.GameDir, everyPlan);

        foreach (var plan in plans)
        {
            var dir = Path.Combine(root, plan.Name);
            var manifest = SourcePlanner.Manifest(plan, gameVersion);

            if (!SourceTreeState.IsOurs(dir))
            {
                blocked.Add(plan.Name);
                continue;
            }

            if (TreeIdentity.Clashes(plan.Root, plan.Assemblies) is { Count: > 0 } clashes)
            {
                var names = clashes.SelectMany(c => new[] { c.Path, c.By }).Distinct(StringComparer.OrdinalIgnoreCase)
                                   .Order(StringComparer.OrdinalIgnoreCase).ToList();
                clashing.Add($"{plan.Name}（{string.Join("、", names.Take(names.Count - 1))} 与 {names[^1]}）");
                continue;
            }

            var cs = TreeFreshness.Cs(dir, SourceTreeState.Read(dir), manifest, n => modulesNow.GetValueOrDefault(n));
            if (!force && cs.State == CsFreshness.Current)
            {
                skipped.Add(plan.Name);
                if (!dryRun)
                {
                    var (copied, hadMissingGraph) = Backfill(ctx, plan, resolving, manifest, dir);
                    if (copied && hadMissingGraph) filledCopiesAndGraph.Add(plan.Name);
                    else if (copied) filledCopiesWithRefresh.Add(plan.Name);
                    else if (hadMissingGraph) filledGraphOnly.Add(plan.Name);
                }
                continue;
            }

            var (gitState, gitError) = SourceGit.TreeStatus(root, plan.Name);
            if (gitState == GitTreeState.Dirty)
            {
                dirty.Add(plan.Name);
                continue;
            }
            if (gitState == GitTreeState.Error)
            {
                gitFailed.Add((plan.Name, gitError ?? "git 失败"));
                continue;
            }

            work.Add((plan, manifest, cs.State switch
            {
                CsFreshness.Unbuilt => "新建",
                CsFreshness.AssembliesChanged => "程序集已改动",
                CsFreshness.ReferencesChanged => "引用的程序集已改动：" + NameList.Render(cs.ChangedReferences, 3),
                CsFreshness.OldDecompiler => "反编译器不匹配",
                CsFreshness.NoSource => "无 .cs 文件",
                _ => "强制",
            }));
        }

        if (blocked.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(root, "下有", Tally.Complete(blocked.Count).Render("directory"), "非空且没有 RimSearcher 清单，因此未作改动：") +
                $"{string.Join("、", blocked)}。把这些目录移开后方可重建。");

        if (dirty.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(Tally.Complete(dirty.Count).Render("source tree"), "在 git 中有未提交的改动，因此未作改动：") +
                $"{string.Join("、", dirty)}。提交或还原后再执行本命令。--force 不覆盖这一点。");

        if (gitFailed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("git 无法报告", Tally.Complete(gitFailed.Count).Render("source tree"), "的状态，因此未作改动：") +
                $"{string.Join("；", gitFailed.Select(x => $"{x.Tree}（{x.Error}）"))}。排查 git 后再执行本命令。--force 不覆盖这一点。");

        if (clashing.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "这份名单加载的目录中有程序集彼此同文件名或同程序集名，而一棵树每个名称只容纳一个程序集，" +
                $"因此以下源码树未重建，保持原状（{Tally.Complete(clashing.Count).Render("source tree")}）：{string.Join("；", clashing)}。");

        if (only is not { Length: > 0 }) SourcesShared.SayReserved(ctx, reserved);

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(from, "中有", Tally.Complete(notInstalled.Count).Render("mod"), "未在本机安装，已跳过：") +
                $"{string.Join("、", notInstalled)}。");

        void SayFilled(List<string> trees, string action)
        {
            if (trees.Count == 0) return;
            AssemblyStore.EnsureIgnored(root);
            ctx.Report.Notice(NoticeKind.Boundary,
                $"以下源码树已匹配，本次执行无须重新反编译，即为其{action}" +
                $"（{Tally.Complete(trees.Count).Render("source tree")}）：{NameList.Render(trees, 6)}。");
        }
        SayFilled(filledCopiesAndGraph, "补上缺少的程序集副本及按已安装的程序集生成的调用图");
        SayFilled(filledCopiesWithRefresh, "补上缺少的程序集副本并重生成调用图");
        SayFilled(filledGraphOnly, "补上按已安装的程序集生成的调用图");

        if (work.Count == 0)
        {
            if (!dryRun) RefreshPacks(ctx, root, only);
            if (dirty.Count > 0 || gitFailed.Count > 0 || clashing.Count > 0)
            {
                SourcesShared.SayHowToDiff(ctx, root);
                return 1;
            }

            if (skipped.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"全部 {Tally.Complete(skipped.Count).Render("source tree")}都已出自已安装的程序集，" +
                    "并由本版反编译器写出；未反编译任何源码树。--force 仍会重建它们。");
            SourcesShared.SayHowToDiff(ctx, root);
            return 0;
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var source = work.All(w => SourcePlanner.IsSearchedOnlyWhenNamed(w.Plan.Name)) ? "game_dir"
            : work.Any(w => SourcePlanner.IsSearchedOnlyWhenNamed(w.Plan.Name)) ? Cjk.Join(from, "与 game_dir") : from;

        if (dryRun)
        {
            foreach (var (plan, _, reason) in work)
                rows.Add(new Dictionary<string, object?>
                {
                    ["tree"] = plan.Name,
                    ["assemblies"] = plan.Assemblies.Count,
                    ["reason"] = reason,
                    ["root"] = plan.Root,
                });
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(Tally.Complete(work.Count).Render("source tree"), "将依", source, "反编译；",
                         Tally.Complete(skipped.Count).Render("source tree"), "已匹配。未改动任何源码树。"));
            ctx.Report.Table("plan", ["tree", "assemblies", "reason", "root"], rows);
            return 0;
        }

        Directory.CreateDirectory(root);
        var failures = new List<string>();
        var totalFiles = 0;
        var totalEdges = 0;
        var totalAccesses = 0;
        var noGraph = new List<string>();
        var unrestored = new List<string>();
        var untyped = new List<string>();
        var busy = new List<string>();
        var written = new List<(string Tree, SourceTreeState Planned)>();
        var dropped = new List<string>();
        var replaced = new List<string>();
        var uncarried = new List<string>();
        var swapped = new List<(string Tree, string Reason)>();

        foreach (var (plan, planned, reason) in work)
        {
            var entries = new List<SourceAssembly>();
            var target = Path.Combine(root, plan.Name);
            var existing = SourceTreeState.Read(target);
            var kept = new List<SourceAssembly>();
            var (extras, superseded) = existing is null ? ([], []) : KeptAssemblies.Split(target, existing, planned);
            foreach (var (old, by) in superseded) replaced.Add($"{plan.Name}/{old.Path}（被 {by} 取代）");
            foreach (var k in extras)
                if (File.Exists(Path.Combine(plan.Root, k.Path))) kept.Add(k);
                else dropped.Add($"{plan.Name}/{Path.GetFileName(k.Path)}");
            var previousGraph = kept.Count > 0 ? CallGraphStore.Read(target, withAccesses: true) : null;
            var staging = Path.Combine(root, SourcesShared.StagingDir, plan.Name);
            TryDeleteDir(staging);
            Directory.CreateDirectory(staging);

            ctx.Progress.WriteLine($"[{plan.Name}] 反编译 {Tally.Complete(plan.Assemblies.Count).Render("assembly")}…");

            var ok = true;
            foreach (var (dll, entry) in plan.Assemblies.Zip(planned.Assemblies))
            {
                var outcome = Decompiler.Decompile(new DecompileRequest
                {
                    AssemblyPath = dll,
                    OutputDirectory = Path.Combine(staging, SourceLayout.PartsDir, TreeIdentity.Part(dll)),
                    References = resolving.ForAssembly(plan, dll),
                    Language = LanguageLevel.Parse(entry.Language),
                });

                if (!outcome.Success)
                {
                    failures.Add($"{plan.Name}/{Path.GetFileName(dll)}：{outcome.Error}");
                    ok = false;
                    break;
                }

                var self = AssemblyFilter.AssemblyName(dll);
                var resolved = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, mvid) in outcome.References)
                    if (!name.Equals(self, StringComparison.OrdinalIgnoreCase)) resolved.TryAdd(name, mvid.ToString());
                entries.Add(entry with { References = resolved });
            }

            if (!ok) { TryDeleteDir(staging); continue; }

            var parts = Path.Combine(staging, SourceLayout.PartsDir);
            var carried = new List<SourceAssembly>();
            foreach (var k in kept)
                if (KeptAssemblies.Carry(target, k, parts)) carried.Add(k);
                else uncarried.Add($"{plan.Name}/{Path.GetFileName(k.Path)}");
            var manifest = planned with
            {
                Assemblies = [.. entries.Concat(carried).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)],
            };
            written.Add((plan.Name, planned with { Assemblies = entries }));

            SourceLayout.Merge(parts, staging);
            var files = Directory.EnumerateFiles(staging, "*.cs", SearchOption.AllDirectories).Count();
            var raw = Decompiler.CountUnrestoredStateMachineFiles(staging);
            if (raw > 0) unrestored.Add($"{plan.Name}（{raw}）");
            var unknown = Decompiler.CountUnknownTypeLines(staging);
            if (unknown > 0) untyped.Add($"{plan.Name}（{unknown}）");

            AssemblyStore.CopyInto(staging, plan.Root, plan.Assemblies);
            KeptAssemblies.CopyCopies(target, carried, staging);

            manifest.Write(staging);

            var previous = staging + ".previous";
            TryDeleteDir(previous);

            try { if (Directory.Exists(target)) Directory.Move(target, previous); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDir(staging);
                busy.Add($"{plan.Name}：{ex.Message}");
                continue;
            }

            try { Directory.Move(staging, target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(previous) && !Directory.Exists(target)) Directory.Move(previous, target);
                TryDeleteDir(staging);
                busy.Add($"{plan.Name}：{ex.Message}");
                continue;
            }

            TryDeleteDir(previous);
            swapped.Add((plan.Name, reason));

            var built = BuildCallGraph(ctx, plan, resolving, manifest, target, previousGraph);
            totalEdges += built?.Edges ?? 0;
            totalAccesses += built?.Accesses ?? 0;
            if (built is null) noGraph.Add(plan.Name);

            totalFiles += files;
            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = plan.Name,
                ["assemblies"] = plan.Assemblies.Count,
                ["files"] = files,
                ["edges"] = built?.Edges,
                ["accesses"] = built?.Accesses,
            });
        }

        AssemblyStore.EnsureIgnored(root);
        if (Directory.Exists(EmbeddedPayloads.Directory(root)))
            AssemblyStore.EnsureIgnored(root, [EmbeddedPayloads.DirName + "/"],
                "# 从 mod 程序集的内嵌资源里解出来的程序集 —— 按内容寻址,可重建的派生物,不进历史。");

        TryDeleteDir(Path.Combine(root, SourcesShared.StagingDir));
        RefreshPacks(ctx, root, only);
        SayCopiesBehind(ctx, root, written);
        CommitSwapped(ctx, root, swapped, from, gameVersion);

        if (dropped.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "这份名单不加载以下程序集，且其程序集文件已不存在，因此已从所在树中去掉" +
                $"（{Tally.Complete(dropped.Count).Render("assembly")}）：{NameList.Render(dropped, 6)}。");

        if (replaced.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "这份名单加载的另一个程序集与以下程序集同文件名或同程序集名，而一棵树每个名称只容纳一个程序集，" +
                $"因此它们已从所在树中去掉（{Tally.Complete(replaced.Count).Render("assembly")}）：{NameList.Render(replaced, 6)}。");

        if (uncarried.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "这份名单不加载以下程序集，且树中找不到这些程序集的项目文件，无法带入其 C#，因此已从所在树中去掉；" +
                "对照一份加载它们的快照执行 sync 会重新建出它们" +
                $"（{Tally.Complete(uncarried.Count).Render("assembly")}）：{NameList.Render(uncarried, 6)}。");

        if (failures.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(Tally.Complete(failures.Count).Render("assembly"), "反编译失败，其所在树未替换：") +
                string.Join("；", failures));

        if (busy.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "以下源码树已反编译但未能替换；树中的文件被其他进程打开时即如此，再次执行本命令可重试" +
                $"（{Tally.Complete(busy.Count).Render("source tree")}）：{string.Join("；", busy)}");

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep, "没有替换任何源码树。");
            return 1;
        }

        if (noGraph.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "以下源码树重建时未建成调用图，因此 'rimsearcher callers' 与 'usages' 无法看到这些源码树的内部" +
                $"（{Tally.Complete(noGraph.Count).Render("source tree")}）：{NameList.Render(noGraph, 6)}。");

        if (unrestored.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "反编译器未能将迭代器与异步方法还原为 yield return 或 await 形式，这些方法仍为原始状态机类" +
                "（形如 _003CFoo_003Ed__3）；每棵树的文件数：" + string.Join("、", unrestored) + "。");

        if (untyped.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "反编译器无法推断类型的值，在 C# 中标为「Unknown result type」；" +
                "每棵树的行数：" + string.Join("、", untyped) + "。");

        ctx.Report.Notice(NoticeKind.Boundary,
            Cjk.Join(Tally.Complete(rows.Count).Render("source tree"), "已依", source, "重建，合计",
                     Tally.Complete(totalFiles).Render("C# file"), "、",
                     Tally.Complete(totalEdges).Render("call edge"), "与",
                     Tally.Complete(totalAccesses).Render("access edge")) +
            (skipped.Count > 0 ? Cjk.Join("；", Tally.Complete(skipped.Count).Render("source tree"), "已匹配") : "") + "。");
        ctx.Report.Table("rebuilt", ["tree", "assemblies", "files", "edges", "accesses"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
    }

    private static void CommitSwapped(
        CommandContext ctx, string root, List<(string Tree, string Reason)> swapped, string from, string gameVersion)
    {
        if (swapped.Count == 0 || !SourceGit.IsRepository(root)) return;

        var game = GameBuild.Installed(ctx.Config.GameDir) ?? gameVersion;
        var committed = new List<string>();
        var unchanged = new List<string>();
        var failed = new List<string>();
        foreach (var (tree, reason) in swapped)
        {
            var origin = SourcePlanner.IsReserved(tree) ? "game_dir"
                       : from == SourcesShared.FromSnapshot && ctx.SnapshotName is { } name ? Cjk.Join(SourcesShared.FromSnapshot, name)
                       : from;
            var body = $"from: {origin}" + (game is { Length: > 0 } ? $"\ngame: {game}" : "");
            ctx.Progress.WriteLine($"[{tree}] 提交到 git…");
            var outcome = SourceGit.CommitTree(root, tree, $"{tree}: sources sync ({reason})", body);
            (outcome.Ok ? outcome.Changed ? committed : unchanged : failed)
                .Add(outcome.Ok ? tree : $"{tree}（{outcome.Error}）");
        }

        if (committed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"已提交到 git，每棵树一次提交（{Tally.Complete(committed.Count).Render("source tree")}）：" +
                $"{NameList.Render(committed, 6)}。");
        if (unchanged.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "以下源码树的重建结果与 git 中已有的内容相同，因此未提交" +
                $"（{Tally.Complete(unchanged.Count).Render("source tree")}）：{NameList.Render(unchanged, 6)}。");
        if (failed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "以下源码树已重建但未提交到 git，因此在这些源码树被提交或还原之前，下一次同步不会改动它们" +
                $"（{Tally.Complete(failed.Count).Render("source tree")}）：{string.Join("；", failed)}。");
    }

    private static void RefreshPacks(CommandContext ctx, string root, string? only)
    {
        if (!Directory.Exists(root)) return;
        var trees = SourcesShared.TreeNames(root).Order(StringComparer.Ordinal).ToList();
        var failed = new List<string>();
        foreach (var name in trees)
        {
            if (only is { Length: > 0 } && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (SourcePack.IsCurrent(root, name)) continue;
            ctx.Progress.WriteLine($"[{name}] 打包…");
            try { SourcePack.Build(root, name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{name}：{ex.Message}");
            }
        }
        if (only is not { Length: > 0 }) SourcePack.Prune(root, trees);
        if (Directory.Exists(Path.Combine(root, SourcePack.Dir)))
            AssemblyStore.EnsureIgnored(root, [SourcePack.Dir + "/"],
                "# code-search 读的打包 —— 每次搜索都与树逐文件核对,可重建的派生物,不进历史。");

        if (failed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "以下源码树无法打包，'code-search' 搜索这些源码树时较慢" +
                $"（{Tally.Complete(failed.Count).Render("source tree")}）：{string.Join("；", failed)}");
    }

    private static (bool Copies, bool MissingGraph) Backfill(
        CommandContext ctx, SourceTreePlan plan, ReferencePlanner references, SourceTreeState manifest, string dir)
    {
        var missingCopies = TreeFreshness.CopiesMissing(dir, plan);
        var missingGraph = CallGraphStore.Peek(dir) is null;
        if (!missingCopies && !missingGraph) return (false, false);

        var previous = CallGraphStore.Read(dir, withAccesses: true);
        if (missingCopies) AssemblyStore.CopyInto(dir, plan.Root, plan.Assemblies);
        BuildCallGraph(ctx, plan, references, manifest, dir, previous);
        return (missingCopies, missingGraph);
    }

    private static (int Edges, int Accesses)? BuildCallGraph(
        CommandContext ctx, SourceTreePlan plan, ReferencePlanner references,
        SourceTreeState manifest, string treeDir, CallGraph? previous)
    {
        try
        {
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copies = new List<ResolvedAssembly>();
            foreach (var dll in plan.Assemblies)
            {
                var copy = AssemblyStore.CopyPath(treeDir, AssemblyStore.Relative(plan.Root, dll));
                if (!File.Exists(copy)) continue;
                installed[copy] = dll;
                copies.Add(new ResolvedAssembly
                {
                    Tree = plan.Name,
                    Name = AssemblyFilter.AssemblyName(copy),
                    Path = copy,
                    Origin = AssemblyOrigin.Copy,
                });
            }

            if (copies.Count == 0) return null;

            ctx.Progress.WriteLine($"[{plan.Name}] 建调用图…");

            var graph = CallGraphBuilder.Build(
                plan.Name, copies, c => references.ForAssembly(plan, installed[c.Path]),
                [.. manifest.Assemblies.Select(a => a.Sha256)]);

            var planned = plan.Assemblies.Select(d => AssemblyStore.Relative(plan.Root, d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keptNames = manifest.Assemblies.Where(a => !planned.Contains(a.Path))
                                    .Select(a => KeptAssemblies.AssemblyName(treeDir, a))
                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            graph = KeptAssemblies.Merge(graph, previous, keptNames);

            CallGraphStore.Write(treeDir, graph);
            return (graph.Edges.Count, graph.Accesses?.Count ?? 0);
        }
        catch
        {
            return null;
        }
    }

    private static void SayPlanIsNotWhatTheGameLoaded(CommandContext ctx, IReadOnlySet<string> scope,
                                                      IReadOnlyList<SourceTreePlan> plans)
    {
        if (ctx.Args.Value(Modlist) is { Length: > 0 }) return;
        var wanted = scope;
        var installed = InstalledMods.Scan(ctx.Config);
        var byId = plans.ToDictionary(p => p.PackageId, StringComparer.OrdinalIgnoreCase);
        var differs = new List<string>();
        foreach (var mod in ctx.Db.Meta.Mods)
        {
            if (mod.Assemblies is not { } recorded || !wanted.Contains(mod.PackageId) ||
                SourcePlanner.IsVanilla(mod.PackageId) || SourcePlanner.IsReserved(mod.PackageId) ||
                !installed.ContainsKey(mod.PackageId) ||
                Contract.IntermediateFormat.IsExporterPackageId(mod.PackageId))
                continue;

            var loaded = recorded.Select(a => a.Name)
                                 .Where(n => !AssemblyFilter.IsRuntimeAssembly(n + ".dll"))
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var planned = byId.TryGetValue(mod.PackageId, out var plan)
                ? plan.Assemblies.Select(AssemblyFilter.AssemblyName)
                      .Where(n => !AssemblyFilter.IsRuntimeAssembly(n + ".dll"))
                      .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var gameOnly = loaded.Except(planned, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var planOnly = planned.Except(loaded, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (gameOnly.Count == 0 && planOnly.Count == 0) continue;

            var parts = new List<string>();
            if (gameOnly.Count > 0) parts.Add("已加载、未计划：" + NameList.Render(gameOnly, 4));
            if (planOnly.Count > 0) parts.Add("已计划、未加载：" + NameList.Render(planOnly, 4));
            differs.Add($"{mod.PackageId}（{string.Join("；", parts)}）");
        }
        if (differs.Count == 0) return;

        ctx.Report.Notice(NoticeKind.Boundary,
            "快照记录的、导出时游戏为这些 mod 加载的程序集，与本次同步从这些 mod 的目录中挑出的程序集不匹配" +
            $"（{Tally.Complete(differs.Count).Render("mod")}）：{string.Join("；", differs)}。");
    }

    private const string LooseWhere =
        "游戏规则选中的某个加载目录的顶层，在每个此类目录的 Assemblies/ 之外";

    private static string LooseMechanism(CommandContext ctx)
        => "游戏自身只加载 Assemblies/ 下的程序集文件；mod 自己的代码可以加载其他程序集文件，而" +
           (ctx.Args.Value(Modlist) is { Length: > 0 } ? " mod 名单不记录" : "本快照未记录") +
           "是否有 mod 这样做。";

    private static Dictionary<string, List<string>> LooseAssemblies(
        CommandContext ctx, IReadOnlyList<string> ids, string gameVersion,
        IReadOnlyDictionary<string, InstalledMod> installed,
        IReadOnlyDictionary<string, IReadOnlyList<Snapshot.LoadedAssembly>>? loaded)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (SourcePlanner.IsVanilla(id) || SourcePlanner.IsReserved(id) || loaded?.ContainsKey(id) == true ||
                Contract.IntermediateFormat.IsExporterPackageId(id) ||
                !installed.TryGetValue(id, out var mod))
                continue;
            if (SourcePlanner.LooseAssemblies(mod.Directory, gameVersion, ids) is { Count: > 0 } files)
                result[id] = files;
        }
        return result;
    }

    private static void SayLooseAssemblies(CommandContext ctx, IReadOnlyDictionary<string, List<string>> loose)
    {
        if (loose.Count == 0) return;
        var named = loose.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(p => $"{p.Key}（{NameList.Render(p.Value, 4)}）").ToList();
        ctx.Report.Notice(NoticeKind.Boundary,
            $"未列入计划的程序集文件，位于{LooseWhere}（{Tally.Complete(loose.Count).Render("mod")}）：{string.Join("；", named)}。" +
            LooseMechanism(ctx) + LooseNextStep(ctx));
    }

    private static string LooseNextStep(CommandContext ctx)
    {
        if (ctx.Args.Value(Modlist) is not { Length: > 0 } listName)
            return "";
        var name = ModListIo.Resolve(ctx.Config, listName).Name;
        var sync = $"'{CommandRegistry.ExeName} sources sync --snapshot {CommandContext.QuoteArg(name)}'";
        return File.Exists(Snapshot.SnapshotCatalog.DatabasePath(ctx.Config, name))
            ? $"{sync} 会计划该快照记录的、游戏已加载的程序集。"
            : $"'{CommandRegistry.ExeName} export --modlist {CommandContext.QuoteArg(listName)}' 记录游戏加载哪些程序集，" +
              $"此后 {sync} 即计划这些程序集。";
    }

    private static void SayCopiesBehind(CommandContext ctx, string root, List<(string Tree, SourceTreeState Planned)> written)
    {
        if (written.Count == 0) return;
        var holders = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var dlls = new List<string>();
        foreach (var tree in SourcesShared.TreeNames(root))
        {
            var copies = Path.Combine(root, tree, AssemblyStore.CopyDir);
            if (!Directory.Exists(copies)) continue;
            foreach (var dll in Directory.EnumerateFiles(copies, "*", SearchOption.AllDirectories))
            {
                dlls.Add(dll);
                foreach (var name in new[] { Path.GetFileNameWithoutExtension(dll), AssemblyFilter.AssemblyName(dll) })
                {
                    if (!holders.TryGetValue(name, out var set)) holders[name] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(tree);
                }
            }
        }

        var index = CallGraphFreshness.Index(dlls);
        var behind = new List<string>();
        var rebuild = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tree, planned) in written)
        {
            var changed = planned.ChangedReferences(planned, n => index.GetValueOrDefault(n));
            if (changed.Count == 0) continue;
            behind.Add($"{tree}（{string.Join("、", changed)}）");
            foreach (var n in changed) rebuild.UnionWith(holders[n]);
        }
        if (behind.Count == 0) return;

        ctx.Report.Notice(NoticeKind.Staleness,
            "以下源码树反编译时对照的是已安装的程序集，而 'il'、'callers' 与 'usages' 解析这些程序集所用的副本是另一个版本，" +
            "因此 'callers' 与 'usages' 不纳入这些树指向这些程序集的边" +
            $"（{Tally.Complete(behind.Count).Render("source tree")}）：{string.Join("；", behind)}。" +
            $"重建 {NameList.Render([.. rebuild], 6)} 可使副本更新为已安装的版本：" +
            (rebuild.Count == 1 ? $"'rimsearcher sources sync --only {rebuild.First()}'。" : "'rimsearcher sources sync'。"));
    }

    private static void TryDeleteDir(string dir)
    {
        try { RimTooling.IO.FileSystemSafety.DeleteTreeJunctionSafe(dir); }
        catch {  }
    }
}
