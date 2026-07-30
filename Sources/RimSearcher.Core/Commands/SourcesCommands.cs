using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Sources;

namespace RimSearcher.Commands;

/// <summary>
/// 反编译树的共用部分:根目录在哪、要建哪些树、以及「差异去问 git」这句话的产地。
/// </summary>
internal static class SourcesShared
{
    /// <summary>暂存目录名。以点开头,不会被当成一棵树。</summary>
    internal const string StagingDir = ".staging";

    internal static string Root(CommandContext ctx)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root))
            throw new CliUsageException(
                "No decompiled source tree is configured, so there is nowhere to put the C#. " +
                "Set 'decompiled_dir' in the config file to the directory that should hold it.");
        return Path.GetFullPath(root);
    }

    /// <summary>这个目录是不是一个 git 工作树的根。</summary>
    internal static bool IsGitRoot(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// 「版本间差异去问 git」这句话只有一个产地。
    ///
    /// 本命令刻意**不实现 diff**:一棵一万四千文件的生成源码树要看版本间差异,那正是版本控制的
    /// 本职,而 git 顺带给出自制 diff 给不了的三样东西 —— 重命名检测、跨多个游戏版本回溯、
    /// 单文件演化史。自己写一份只会得到它的一个子集。
    /// </summary>
    internal static void SayHowToDiff(CommandContext ctx, string root)
    {
        if (IsGitRoot(root))
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"What changed is a question for git: run 'git -C \"{root}\" diff' for the working diff, " +
                "'git log -p -- <file>' for one file's history. This command does not compare versions itself.");
            return;
        }

        ctx.Report.Notice(NoticeKind.NextStep,
            $"'{root}' is not a git repository, so there is nothing to compare this against. " +
            "Run 'git init' in it and commit once; from then on every sync shows up as a diff, with rename " +
            "detection and per-file history. Keep it local — this is decompiled game code, so do not add a " +
            "remote or publish it.");
    }

    /// <summary>快照里的 mod 列表,或 <c>--modlist</c> 指名的那一份。</summary>
    internal static (IReadOnlyList<string> Ids, string GameVersion, string From) PackageIds(CommandContext ctx)
    {
        var listName = ctx.Args.Value("modlist");
        if (listName is { Length: > 0 })
        {
            var list = ModListIo.Resolve(ctx.Config, listName);
            // mod 列表里没有游戏版本可读,拿快照的 —— loadFolders 的版本比对总要一个数。
            return (list.Ids, ctx.Db.Meta.GameVersion, $"mod list '{listName}'");
        }

        var ids = ctx.Db.Mods.Select(m => m.PackageId).ToList();
        return (ids, ctx.Db.Meta.GameVersion, "the snapshot");
    }
}

public sealed class SourcesListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "sources list",
        Aliases = ["sources status", "sources check"],
        Summary = "List the decompiled source trees and say which ones no longer match the installed assemblies.",
        Remarks =
            "Each tree carries a manifest naming the assemblies it was decompiled from and their hashes, so " +
            "'stale' here means exactly one thing: a dll on disk is not the dll this tree came from.\n\n" +
            "It does not say what changed inside the source. That is git's job — see the note this command prints.",
        Options = [],
        UsesGlobals = true,
        Examples = ["rimsearcher sources list"],
    };

    public override int Run(CommandContext ctx)
    {
        var root = SourcesShared.Root(ctx);
        if (!Directory.Exists(root))
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{root}' does not exist yet, so there is no decompiled C# to search. " +
                "'rimsearcher sources sync' creates it.");
            return 1;
        }

        var (ids, gameVersion, from) = SourcesShared.PackageIds(ctx);
        var installed = InstalledMods.Scan(ctx.Config);
        var plans = SourcePlanner.Plan(ctx.Config, ids, gameVersion, installed, out var notInstalled)
                    .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var onDisk = Directory.EnumerateDirectories(root)
                              .Select(d => Path.GetFileName(d)!)
                              .Where(n => !n.StartsWith('.'))
                              .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                              .ToList();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var stale = 0;
        var missing = 0;

        foreach (var name in onDisk.Union(plans.Keys, StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(root, name);
            var exists = Directory.Exists(dir);
            var state = exists ? SourceTreeState.Read(dir) : null;
            var files = exists ? CountFiles(dir) : 0;

            string status;
            if (!plans.TryGetValue(name, out var plan))
                // 计划里没有它:这棵树对应的 mod 不在这次的列表里。不是「坏了」,
                // 但也不该被当成当前环境的一部分 —— 说清是哪一种。
                status = "not in " + from;
            else if (state is null)
                { status = exists ? "no manifest" : "never built"; missing++; }
            else if (!state.SameSources(SourcePlanner.Manifest(plan, gameVersion)))
                { status = "stale"; stale++; }
            else
                status = "current";

            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = name,
                ["files"] = files == 0 ? "" : files.ToString(),
                ["assemblies"] = plan is null ? (state?.Assemblies.Count.ToString() ?? "") : plan.Assemblies.Count.ToString(),
                ["status"] = status,
            });
        }

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No decompiled source trees under '{root}' yet. 'rimsearcher sources sync' builds them.");
            return 1;
        }

        ctx.Report.Notice(NoticeKind.Boundary,
            $"{Tally.Complete(rows.Count).Render("source tree")} under '{root}', " +
            $"checked against {from} ({ids.Count} mods, game {SourcePlanner.NormalizeGameVersion(gameVersion)}).");

        if (stale > 0 || missing > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                $"{stale} tree(s) came from assemblies that have since changed and {missing} were never built; " +
                "'rimsearcher sources sync' brings them up to date. Anything they say about code is from the " +
                "older build until then.");

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(notInstalled.Count).Render("mod")} in {from} " +
                $"{(notInstalled.Count == 1 ? "is" : "are")} not installed on this machine, so no tree can be " +
                $"built for {(notInstalled.Count == 1 ? "it" : "them")}: {string.Join(", ", notInstalled)}.");

        ctx.Report.Table("trees", ["tree", "files", "assemblies", "status"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }
}

public sealed class SourcesSyncCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "sources sync",
        Aliases = ["sources decompile", "decompile"],
        Summary = "Decompile the assemblies the game actually loads into the configured source tree.",
        Remarks =
            "Which mods to cover comes from the snapshot, not from a hand-written list: the snapshot's mod " +
            "list is the game's own answer, and a hand-written one drifts. Within each mod, only the " +
            "assemblies the game would load are decompiled — the version folders and loadFolders.xml " +
            "conditions are resolved the same way the game resolves them, so years-old dlls and " +
            "mutually-exclusive branches stay out.\n\n" +
            "A tree whose source assemblies have not changed is left alone. Comparing versions is not this " +
            "command's job: keep the tree in git and 'git diff' answers it, with rename detection and history " +
            "that a bespoke comparison cannot offer.",
        Options =
        [
            new OptionSpec
            {
                Name = "modlist",
                Aliases = ["list", "from", "profile"],
                Placeholder = "<name>",
                Help = "Cover the mods in this saved mod list instead of the ones in the snapshot.",
            },
            new OptionSpec
            {
                Name = "only",
                Aliases = ["tree", "source", "mod"],
                Placeholder = "<name>",
                Help = "Build just this one tree. Takes a tree name as 'sources list' prints it.",
            },
            new OptionSpec
            {
                Name = "force",
                Arity = Arity.Flag,
                Aliases = ["rebuild", "all"],
                Help = "Rebuild even the trees whose assemblies have not changed.",
            },
            new OptionSpec
            {
                Name = "dry-run",
                Arity = Arity.Flag,
                Aliases = ["plan", "check"],
                Help = "Report what would be decompiled and stop without writing anything.",
            },
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher sources sync",
            "rimsearcher sources sync --dry-run",
            "rimsearcher sources sync --only erdelf.humanoidalienraces --force",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var root = SourcesShared.Root(ctx);
        var (ids, gameVersion, from) = SourcesShared.PackageIds(ctx);
        var installed = InstalledMods.Scan(ctx.Config);
        var plans = SourcePlanner.Plan(ctx.Config, ids, gameVersion, installed, out var notInstalled);

        var only = ctx.Args.Value("only");
        if (only is { Length: > 0 })
        {
            var kept = plans.Where(p => p.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == 0)
                throw new CliUsageException(
                    $"Nothing named '{only}' would be built from {from}." +
                    (plans.Count > 0
                        ? $" It covers: {string.Join(", ", plans.Take(Limits.MaxSuggestions).Select(p => p.Name))}" +
                          (plans.Count > Limits.MaxSuggestions ? ", …" : "") + "."
                        : ""));
            plans = kept;
        }

        if (plans.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing to decompile: no mod in {from} loads an assembly" +
                (ctx.Config.GameDir is { Length: > 0 } ? "" : ", and 'game_dir' is not configured, " +
                    "so the game's own assemblies cannot be found either") + ".");
            return 1;
        }

        var force = ctx.Args.Flag("force");
        var dryRun = ctx.Args.Flag("dry-run");

        // 哈希整批来源 dll。vanilla 那几个大文件实测不到一秒,而它换来的是「没变就不重跑」——
        // 少一次重跑就少一屏假 diff。
        var work = new List<(SourceTreePlan Plan, SourceTreeState Manifest, string Reason)>();
        var skipped = new List<string>();
        var blocked = new List<string>();

        foreach (var plan in plans)
        {
            var dir = Path.Combine(root, plan.Name);
            var manifest = SourcePlanner.Manifest(plan, gameVersion);

            if (!SourceTreeState.IsOurs(dir))
            {
                blocked.Add(plan.Name);
                continue;
            }

            var existing = SourceTreeState.Read(dir);
            if (!force && existing is not null && existing.SameSources(manifest) && Directory.Exists(dir))
            {
                skipped.Add(plan.Name);
                continue;
            }

            work.Add((plan, manifest, existing is null ? "new" : "assemblies changed"));
        }

        if (blocked.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(blocked.Count).Render("directory")} under '{root}' " +
                $"{(blocked.Count == 1 ? "is" : "are")} not empty and carry no RimSearcher manifest, so " +
                $"{(blocked.Count == 1 ? "it was" : "they were")} left untouched rather than overwritten: " +
                $"{string.Join(", ", blocked)}. Move the directory aside if you want it rebuilt.");

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(notInstalled.Count).Render("mod")} in {from} " +
                $"{(notInstalled.Count == 1 ? "is" : "are")} not installed here and " +
                $"{(notInstalled.Count == 1 ? "was" : "were")} skipped: {string.Join(", ", notInstalled)}.");

        if (work.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Every one of {Tally.Complete(skipped.Count).Render("source tree")} already came from the " +
                "assemblies now on disk; nothing was decompiled. --force rebuilds them anyway.");
            SourcesShared.SayHowToDiff(ctx, root);
            return 0;
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();

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
                $"{Tally.Complete(work.Count).Render("source tree")} would be decompiled from {from}; " +
                $"{skipped.Count} already current. Nothing was written.");
            ctx.Report.Table("plan", ["tree", "assemblies", "reason", "root"], rows);
            return 0;
        }

        Directory.CreateDirectory(root);
        var failures = new List<string>();
        var totalFiles = 0;

        foreach (var (plan, manifest, _) in work)
        {
            var target = Path.Combine(root, plan.Name);
            // 暂存后转正。一次失败留下半棵树的后果不是「少了点东西」,而是 code-search 从此
            // 在一棵残缺的树上给出看起来完整的答案。
            var staging = Path.Combine(root, SourcesShared.StagingDir, plan.Name);
            TryDeleteDir(staging);
            Directory.CreateDirectory(staging);

            ctx.Progress.WriteLine($"[{plan.Name}] 反编译 {plan.Assemblies.Count} 个程序集…");

            var ok = true;
            var files = 0;
            foreach (var dll in plan.Assemblies)
            {
                var outcome = Decompiler.Decompile(new DecompileRequest
                {
                    AssemblyPath = dll,
                    OutputDirectory = Path.Combine(staging, Path.GetFileNameWithoutExtension(dll)),
                    ReferencePaths = SourcePlanner.ReferencePaths(plan, dll, plans, ctx.Config),
                });

                if (!outcome.Success)
                {
                    failures.Add($"{plan.Name}/{Path.GetFileName(dll)}: {outcome.Error}");
                    ok = false;
                    break;
                }
                files += outcome.FileCount;
            }

            if (!ok) { TryDeleteDir(staging); continue; }

            manifest.Write(staging);
            TryDeleteDir(target);
            Directory.Move(staging, target);

            totalFiles += files;
            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = plan.Name,
                ["assemblies"] = plan.Assemblies.Count,
                ["files"] = files,
            });
        }

        TryDeleteDir(Path.Combine(root, SourcesShared.StagingDir));

        if (failures.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(failures.Count).Render("assembly")} failed to decompile, and each one's whole " +
                "tree was rolled back rather than left half-written — the previous source for those trees is " +
                $"still in place: {string.Join("; ", failures)}");

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep, "No source tree was replaced.");
            return 1;
        }

        ctx.Report.Notice(NoticeKind.Boundary,
            $"{Tally.Complete(rows.Count).Render("source tree")} rebuilt from {from}, " +
            $"{totalFiles} C# files in total" + (skipped.Count > 0 ? $"; {skipped.Count} already current" : "") + ".");
        ctx.Report.Table("rebuilt", ["tree", "assemblies", "files"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 转正前会再试一次;真删不掉由 Directory.Move 报出来 */ }
    }
}
