using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Metadata;
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

    /// <summary>
    /// 「没配反编译目录」这句话的**查询侧**产地。<see cref="Root"/> 那份是写侧,措辞不同:
    /// 元数据那几条命令读的是程序集,不经过落盘。
    /// </summary>
    internal static string NotConfiguredToRead(string verb)
        => $"No decompiled source tree is configured, so there is nothing to {verb}. " +
           "Set 'decompiled_dir' in the config file to the directory holding the decompiled C#. " +
           "Symbol-level questions do not need it: 'types', 'members', 'callers' and 'il' read the assemblies directly.";

    /// <summary>这个目录是不是一个 git 工作树的根。</summary>
    internal static bool IsGitRoot(string dir) => SourceGit.IsRepository(dir);

    /// <summary>
    /// 根目录下哪些子目录算一棵源码树。判据只此一处 —— 顺序不共用(list 要纯字母序,
    /// code-search 要 vanilla 优先),但**什么算树**必须共用,否则 <c>.git</c> 会被当成一棵树。
    /// </summary>
    internal static IEnumerable<string> TreeNames(string root)
        => Directory.EnumerateDirectories(root)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => n.Length > 0 && !n.StartsWith('.'));

    /// <summary>
    /// 「版本间差异去问 git」这句话只有一个产地。本命令刻意**不实现 diff**:git 顺带给出
    /// 自制 diff 给不了的重命名检测、跨版本回溯与单文件演化史。
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
            "The copies and edges columns say which of the three projections a tree has: the C# it holds, a " +
            "copy of the assemblies it came from, and a table of the call sites in them. A tree built before " +
            "those were kept has neither, and 'il' there falls back to the installed dll while 'callers' " +
            "cannot see into it at all.\n\n" +
            "It does not say what changed inside the source. That is git's job — see the note this command prints.",
        Options = [],
        UsesGlobals = true,
        Examples = ["rimsearcher sources list"],
        JsonKeys = [new() { Key = "trees", Rows = true, What = "one row per decompiled source tree: tree, files, assemblies, copies, edges, status." }],
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

        var onDisk = SourcesShared.TreeNames(root)
                                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var stale = 0;
        var missing = 0;
        var current = 0;
        var outside = 0;
        var empty = 0;
        var emptyOrphan = 0;
        var noCopies = 0;
        var noEdges = 0;

        foreach (var name in onDisk.Union(plans.Keys, StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(root, name);
            var exists = Directory.Exists(dir);
            var state = exists ? SourceTreeState.Read(dir) : null;
            var files = exists ? CountFiles(dir) : 0;

            string status;
            // 目录在而里面一个 .cs 都没有,是关于**磁盘**的事实,与「这棵树在不在这次的计划里」
            // 正交 —— 所以空这件事先判,它压得住计划内外。
            plans.TryGetValue(name, out var plan);
            if (exists && files == 0)
            {
                status = plan is null ? $"empty (not in {from})" : "empty";
                empty++;
                if (plan is null) emptyOrphan++;
            }
            else if (plan is null)
                // 这棵树对应的 mod 不在这次的列表里 —— 不是「坏了」,但也不算当前环境的一部分。
                { status = "not in " + from; outside++; }
            else if (state is null)
                { status = exists ? "no manifest" : "never built"; missing++; }
            else if (!state.SameSources(SourcePlanner.Manifest(plan, gameVersion)))
                { status = "stale"; stale++; }
            else
                { status = "current"; current++; }

            // 三种投影是不是齐的。它们各自决定一条命令答不答得上来,而没有任何别处说得出来:
            // 副本没了 'il' 就退回读安装目录里那份(可能是另一个版本),边表没了 'callers'
            // 在这棵树上就是盲的。
            var copies = exists ? CountCopies(dir) : 0;
            var peek = exists ? CallGraphStore.Peek(dir) : null;
            if (exists && copies == 0) noCopies++;
            if (exists && files > 0 && peek is null) noEdges++;

            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = name,
                // 目录在而空着的印 0,目录根本不在的才留白 ——
                // 「反编译出来是空的」与「这里没有这个目录」要的下一步不是一回事。
                ["files"] = exists ? files.ToString() : "",
                ["assemblies"] = plan is null ? (state?.Assemblies.Count.ToString() ?? "") : plan.Assemblies.Count.ToString(),
                ["copies"] = exists ? copies.ToString() : "",
                ["edges"] = peek is { } p ? p.Edges.ToString() : exists ? "none" : "",
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
            $"checked against {from} ({Tally.Complete(ids.Count).Render("mod")}, " +
            $"game {SourcePlanner.NormalizeGameVersion(gameVersion)}).");

        // 对账。四个桶都得点名,不许留「余数」:没进树的 mod 不是坏了,而是**根本没有 C#**
        // (纯 XML 的 mod,SourcePlanner 见 dlls.Count == 0 直接 continue),或者被并进了
        // vanilla 那一棵(每个 DLC 各是一个 packageId,树只有一棵)。
        //
        // 两条等式各自封闭,而且**各只用一个单位** —— 树与 mod 混着数会重复计入 vanilla 那一棵。
        // 句中不出现随计数变形的动词:冒号在前,数在后。
        // 「并进 vanilla 那一棵」只在那一棵真被计划出来时才说得通:没配 game_dir 时一个 DLL 都
        // 找不到,这几个 packageId 的真实处境是「没有可反编译的程序集」,归到下面那个桶里。
        var vanillaIds = plans.ContainsKey(SourcePlanner.VanillaTree) ? ids.Count(SourcePlanner.IsVanilla) : 0;
        var exporterIds = ids.Count(i => string.Equals(i, Contract.IntermediateFormat.ExporterPackageId,
                                                       StringComparison.OrdinalIgnoreCase));
        var ownTree = plans.Keys.Count(k => !string.Equals(k, SourcePlanner.VanillaTree, StringComparison.OrdinalIgnoreCase));
        var noCode = ids.Count - vanillaIds - exporterIds - notInstalled.Count - ownTree;

        ctx.Report.Notice(NoticeKind.Count,
            $"Mods in {from} ({ids.Count}): {ownTree} with a tree of their own, " +
            $"{vanillaIds} folded into the single '{SourcePlanner.VanillaTree}' tree, " +
            $"{noCode} with no assembly to decompile, {notInstalled.Count} not installed here, " +
            $"{exporterIds} the exporter itself. " +
            $"Trees on disk ({rows.Count}): {current} current, {stale} stale, {missing} never built, " +
            $"{empty} holding no .cs file, {outside} from outside {from}. " +
            "'code-search' reads every tree either way; this list is the only place that says which is which.");

        // 两截各自只在非零时出现 —— 「0 were never built」既占字节又要读者过滤。
        if (stale > 0 || missing > 0 || empty > 0)
        {
            // 从句里不带随数变形的动词:名词有登记处,动词没有。
            var parts = new List<string>();
            if (stale > 0)
                parts.Add($"built from an assembly that has changed since — {Tally.Complete(stale).Render("source tree")}");
            if (missing > 0)
                parts.Add($"never built at all — {Tally.Complete(missing).Render("source tree")}");
            if (empty > 0)
                parts.Add($"a directory holding no .cs file — {Tally.Complete(empty).Render("source tree")}");

            ctx.Report.Notice(NoticeKind.Staleness,
                "Not current: " + string.Join("; ", parts) +
                ". 'rimsearcher sources sync' rebuilds the ones it plans; until then anything those trees " +
                "say about code is from the older build.");

            // 计划里根本没有它们的那些空目录,`sources sync` 一辈子也不会去填 —— 上面那句
            // 「sync rebuilds them」对它们是一条走不通的指路。
            if (emptyOrphan > 0)
                // 句里不许有跟着计数变形的动词。计数进破折号后的名词短语,后面一律用 each,
                // 单复数就不再是个问题。
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'sources sync' plans no tree under those names, so it will never fill them — " +
                    $"{Tally.Complete(emptyOrphan).Render("source tree")} out of the ones just listed. " +
                    "Each is an empty directory left over from an earlier naming, and each is one of the " +
                    "trees 'code-search' reports reading no file from. Removing the directory is the only " +
                    "thing that changes this line.");
        }

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(notInstalled.Count).Render("mod")} in {from} " +
                $"{(notInstalled.Count == 1 ? "is" : "are")} not installed on this machine, so no tree can be " +
                $"built for {(notInstalled.Count == 1 ? "it" : "them")}: {string.Join(", ", notInstalled)}.");

        if (noCopies > 0 || noEdges > 0)
        {
            var parts = new List<string>();
            if (noCopies > 0)
                parts.Add("no copy of the assemblies it came from, so 'il' there reads whatever is installed " +
                          $"now — {Tally.Complete(noCopies).Render("source tree")}");
            if (noEdges > 0)
                parts.Add("no call-graph table, so 'callers' does not reach into it — " +
                          $"{Tally.Complete(noEdges).Render("source tree")}");

            ctx.Report.Notice(NoticeKind.Boundary,
                "Built before the assemblies and call sites were kept alongside the C#: " +
                string.Join("; ", parts) +
                ". 'rimsearcher sources sync' adds both without decompiling again.");
        }

        ctx.Report.Table("trees", ["tree", "files", "assemblies", "copies", "edges", "status"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
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
            // 副本按清单里的相对路径分层放(同名 dll 在一个 mod 里出现两次是常事),
            // 所以要往下数 —— 只数顶层的话,分层放的那些一个都不算,而 0 正好是
            // 「从来没抄过」的取值。
            return Directory.Exists(copies)
                ? Directory.EnumerateFiles(copies, "*.dll", SearchOption.AllDirectories).Count()
                : 0;
        }
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
            "that a bespoke comparison cannot offer. A tree with uncommitted git changes is also left alone — " +
            "overwriting it would discard the working diff, which is the only record of the last sync until " +
            "you commit. Commit or restore that tree, then run this again. --force does not override this: " +
            "that flag only rebuilds trees whose assemblies have not changed.\n\n" +
            "Each tree also gets a copy of the assemblies it was built from and a table of the call sites in " +
            "them, so that 'read', 'il' and 'callers' all answer from one build rather than from whatever is " +
            "installed at the moment each is asked. Both are derived data and are added to the tree's " +
            ".gitignore. A tree that is already current but was built before those were kept gets both " +
            "without being decompiled again.",
        Options =
        [
            new OptionSpec
            {
                Name = "modlist",
                // "from" 不在这里:modlist save 有一个真的 --from。
                Aliases = ["list", "profile"],
                Placeholder = "<name>",
                Help = "Cover the mods in this saved mod list instead of the ones in the snapshot.",
            },
            new OptionSpec
            {
                Name = "only",
                // "source" 不在这里:code-search / read 有一个真的 --source(源码树名)。
                Aliases = ["tree", "mod"],
                Placeholder = "<name>",
                Help = "Build just this one tree. Takes a tree name as 'sources list' prints it.",
            },
            new OptionSpec
            {
                Name = "force",
                Arity = Arity.Flag,
                Aliases = ["rebuild"],
                Help = "Rebuild even the trees whose assemblies have not changed. Does not overwrite a tree that has uncommitted git changes.",
            },
            new OptionSpec
            {
                Name = "dry-run",
                Arity = Arity.Flag,
                // check 归 'docs --check' 那一档(判定,不等就非零)。见 ExportCommand 同一处。
                Aliases = ["plan"],
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
        JsonKeys =
        [
            new()
            {
                Key = "rebuilt",
                What = "without --dry-run: one row per tree that was rewritten — tree, assemblies, files, " +
                       "edges. 'edges' is null for a tree whose call-graph table could not be built. " +
                       "'plan' is absent then.",
            },
            new()
            {
                Key = "plan",
                What = "with --dry-run: one row per tree that would be rebuilt — tree, assemblies, reason, root. " +
                       "Nothing is written, and 'rebuilt' is absent.",
            },
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
                        ? $" It covers: {NameList.Render([.. plans.Select(p => p.Name)], Limits.MaxSuggestions)}."
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

        // 两张表互斥,`--dry-run` 一给就定了哪一张:凭空多一个空数组等于说「那一路也做过了」。
        ctx.Report.Promises(dryRun ? "plan" : "rebuilt");

        // 哈希整批来源 dll(vanilla 那几个大文件不到一秒),换来「没变就不重跑」。
        var work = new List<(SourceTreePlan Plan, SourceTreeState Manifest, string Reason)>();
        var skipped = new List<string>();
        var filled = new List<string>();
        var blocked = new List<string>();
        var dirty = new List<string>();

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
                // 树是当前的,缺的只是另外两种投影。补它们不必重新反编译 ——
                // .cs 已经出自这几个 dll,而副本与边表是同一份 dll 的另外两种读法。
                if (!dryRun && Backfill(ctx, plan, plans, manifest, dir)) filled.Add(plan.Name);
                continue;
            }

            // 只拦「本来要写」的树:当前且干净的不必报脏,另一棵的未提交改动也不该挡住这一棵。
            if (SourceGit.HasUncommittedChanges(root, plan.Name))
            {
                dirty.Add(plan.Name);
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

        if (dirty.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(dirty.Count).Render("source tree")} " +
                $"{(dirty.Count == 1 ? "has" : "have")} uncommitted changes in git, so " +
                $"{(dirty.Count == 1 ? "it was" : "they were")} left untouched rather than overwritten: " +
                $"{string.Join(", ", dirty)}. Commit or restore " +
                $"{(dirty.Count == 1 ? "it" : "them")}, then run this again. --force does not override this — " +
                "that flag only rebuilds trees whose assemblies have not changed.");

        if (notInstalled.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(notInstalled.Count).Render("mod")} in {from} " +
                $"{(notInstalled.Count == 1 ? "is" : "are")} not installed here and " +
                $"{(notInstalled.Count == 1 ? "was" : "were")} skipped: {string.Join(", ", notInstalled)}.");

        if (filled.Count > 0)
        {
            AssemblyStore.EnsureIgnored(root);
            ctx.Report.Notice(NoticeKind.Boundary,
                "Already current, and given the assembly copies and the call-graph table without being " +
                $"decompiled again — {Tally.Complete(filled.Count).Render("source tree")}: " +
                $"{NameList.Render(filled, 6)}. The C# in each was already built from those same dlls.");
        }

        if (work.Count == 0)
        {
            if (dirty.Count > 0)
            {
                SourcesShared.SayHowToDiff(ctx, root);
                return 1;
            }

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
                $"{Tally.Complete(skipped.Count).Render("source tree")} already current. Nothing was written.");
            ctx.Report.Table("plan", ["tree", "assemblies", "reason", "root"], rows);
            return 0;
        }

        Directory.CreateDirectory(root);
        var failures = new List<string>();
        var totalFiles = 0;
        var totalEdges = 0;
        var noGraph = new List<string>();

        foreach (var (plan, manifest, _) in work)
        {
            var target = Path.Combine(root, plan.Name);
            // 暂存后转正:半棵树会让 code-search 在残缺的树上给出看起来完整的答案。
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

            // 三种投影同源:.cs 已经在暂存里了,dll 副本跟着一起进去,一起转正。
            AssemblyStore.CopyInto(staging, plan.Root, plan.Assemblies);

            manifest.Write(staging);

            // 旧树先挪开再删,不是先删再挪 —— 先删的话,转正这一步失败就把上一版一起带走了,
            // 而那正是「失败时回滚」这套暂存机制要保住的东西。实测过一次:转正报了拒绝访问,
            // 树就此不在磁盘上,只剩 git 里那份能捡回来。
            var previous = target + ".previous";
            TryDeleteDir(previous);
            if (Directory.Exists(target)) Directory.Move(target, previous);

            try { Directory.Move(staging, target); }
            catch
            {
                if (Directory.Exists(previous) && !Directory.Exists(target)) Directory.Move(previous, target);
                throw;
            }

            TryDeleteDir(previous);

            // 边表在转正**之后**建。建它要把副本连同它们引用的程序集一起打开,而那些句柄
            // 会按住暂存目录不放,于是 Directory.Move 报「拒绝访问」—— 整棵树就此丢掉,
            // 只因为第三种投影多开了几个文件。写边表本身是原子的(先写 .tmp 再改名)。
            var edges = BuildCallGraph(ctx, plan, plans, manifest, target);
            totalEdges += edges ?? 0;
            if (edges is null) noGraph.Add(plan.Name);

            totalFiles += files;
            rows.Add(new Dictionary<string, object?>
            {
                ["tree"] = plan.Name,
                ["assemblies"] = plan.Assemblies.Count,
                ["files"] = files,
                ["edges"] = edges,
            });
        }

        AssemblyStore.EnsureIgnored(root);

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

        if (noGraph.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "Rebuilt without a call-graph table, so 'rimsearcher callers' does not reach into " +
                $"{(noGraph.Count == 1 ? "it" : "them")} — " +
                $"{Tally.Complete(noGraph.Count).Render("source tree")}: {NameList.Render(noGraph, 6)}. " +
                "The C# and the assembly copies are in place; only the index over call sites is missing.");

        ctx.Report.Notice(NoticeKind.Boundary,
            $"{Tally.Complete(rows.Count).Render("source tree")} rebuilt from {from}, " +
            $"{Tally.Complete(totalFiles).Render("C# file")} and " +
            $"{Tally.Complete(totalEdges).Render("call edge")} in total" +
            (skipped.Count > 0 ? $"; {Tally.Complete(skipped.Count).Render("source tree")} already current" : "") + ".");
        ctx.Report.Table("rebuilt", ["tree", "assemblies", "files", "edges"], rows);
        SourcesShared.SayHowToDiff(ctx, root);
        return 0;
    }

    /// <summary>
    /// 树是当前的,但副本或边表缺着 —— 把缺的那部分补上,不重新反编译。
    ///
    /// 这条路存在是因为「当前」这个判据只管 .cs:在副本与边表存在之前建的树,每一棵都是
    /// 当前的,而 <c>--force</c> 是唯一能让它们长出另外两种投影的开关 —— 那要把全部树重新
    /// 反编译一遍,只为拷几个文件和扫一遍指令流。
    /// </summary>
    private static bool Backfill(
        CommandContext ctx, SourceTreePlan plan, IReadOnlyList<SourceTreePlan> plans,
        SourceTreeState manifest, string dir)
    {
        var missingCopies = plan.Assemblies.Any(
            d => !File.Exists(AssemblyStore.CopyPath(dir, AssemblyStore.Relative(plan.Root, d))));
        var missingGraph = CallGraphStore.Peek(dir) is null;
        if (!missingCopies && !missingGraph) return false;

        if (missingCopies) AssemblyStore.CopyInto(dir, plan.Root, plan.Assemblies);
        BuildCallGraph(ctx, plan, plans, manifest, dir);
        return true;
    }

    /// <summary>
    /// 这棵树的调用边表。读的是刚抄进暂存目录的那份副本 —— 与 .cs 同源的正是它,
    /// 而安装目录里的原件在这一刻已经可能是别的版本了。
    ///
    /// 建不出来返回 <c>null</c>,不是 0:「这棵树里没有调用」与「这棵树没建成表」在
    /// 反查的结果里同形,而后者的下一步是重建。
    /// </summary>
    private static int? BuildCallGraph(
        CommandContext ctx, SourceTreePlan plan, IReadOnlyList<SourceTreePlan> plans,
        SourceTreeState manifest, string treeDir)
    {
        try
        {
            var copies = plan.Assemblies
                .Select(dll => new ResolvedAssembly
                {
                    Tree = plan.Name,
                    Name = Path.GetFileNameWithoutExtension(dll),
                    Path = AssemblyStore.CopyPath(treeDir, AssemblyStore.Relative(plan.Root, dll)),
                    Origin = AssemblyOrigin.Copy,
                })
                .Where(a => File.Exists(a.Path))
                .ToList();

            if (copies.Count == 0) return null;

            var search = plan.Assemblies
                .SelectMany(dll => SourcePlanner.ReferencePaths(plan, dll, plans, ctx.Config))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            ctx.Progress.WriteLine($"[{plan.Name}] 建调用边表…");

            var graph = CallGraphBuilder.Build(
                plan.Name, copies, search, [.. manifest.Assemblies.Select(a => a.Sha256)]);

            CallGraphStore.Write(treeDir, graph);
            return graph.Edges.Count;
        }
        catch
        {
            // 边表建不出来不该把已经反编译好的那棵树一起丢掉 —— 它是第三种投影,
            // 缺了它另外两种照旧可用。
            return null;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 转正前会再试一次;真删不掉由 Directory.Move 报出来 */ }
    }
}
