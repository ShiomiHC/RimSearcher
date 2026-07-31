using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 导出编排 —— 起游戏、拿数据、建库、自校,全程无人值守。
///
/// **真实 ModsConfig.xml 永不触碰**:整个 SaveData 经 <c>-savedatafolder</c> 重定向到临时目录。
/// 游戏退出时自己会回写 ModsConfig,所以「备份 - 换入 - 还原」与它是竞争关系。
///
/// Config/ 是**整体复制**而不是只造一个 ModsConfig.xml:各 mod 的 Mod_*.xml 设置会改 patch
/// 结果,丢掉它们导出的就是另一份数据。
/// </summary>
public sealed class ExportCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "export",
        Summary = "Run the game unattended with a chosen mod list and import what it exports.",
        Remarks =
            "The game's own configuration is never modified. A copy of it is made in a temporary save-data folder, " +
            "the copy's mod list is rewritten, and the game is pointed at the copy. Every mod in the list is checked " +
            "against what is installed before the game is started, so a typo costs a second rather than a whole launch.\n\n" +
            "When it finishes, the mods the game reported are compared with the mods that were asked for, and the " +
            "import is rejected if they differ.\n\n" +
            "The game runs headless: no window appears and nothing is written to the display settings the game " +
            "keeps outside its save-data folder. Pass --show-window if a mod in the list needs a graphics device " +
            "while it loads.",
        Options =
        [
            new OptionSpec
            {
                Name = "modlist",
                Aliases = ["list", "mods", "profile"],
                Placeholder = "<name>",
                Help = "Which mod list to run. 'rimsearcher modlist list' shows the names.",
                Required = true,
            },
            new OptionSpec
            {
                Name = "name",
                Aliases = ["as", "alias"],
                Placeholder = "<name>",
                Help = "Name to register the resulting snapshot under. Defaults to the mod list's name.",
            },
            new OptionSpec
            {
                Name = "timeout",
                Aliases = ["timeout-seconds", "wait"],
                Placeholder = "<seconds>",
                Help = "How long to wait for the game to finish, and the only thing that will stop it. A large " +
                       "mod list can take minutes to load; if a stage sits still for a while this command says so " +
                       "and keeps waiting, so raise this rather than trusting a stall report.",
                Default = "900",
            },
            new OptionSpec
            {
                Name = "show-window",
                Arity = Arity.Flag,
                Aliases = ["window", "windowed", "graphics"],
                Help = "Start the game with its window instead of headless. Only needed if a mod in the list " +
                       "requires a graphics device while loading; headless is otherwise identical and faster.",
            },
            new OptionSpec
            {
                Name = "keep-temp",
                Arity = Arity.Flag,
                Help = "Keep the temporary save-data folder afterwards, for looking at what the game was given.",
            },
            new OptionSpec
            {
                Name = "dry-run",
                Arity = Arity.Flag,
                Aliases = ["check", "validate"],
                Help = "Do everything except start the game: resolve the list, check every mod is installed, " +
                       "and report what would be run.",
            },
            new OptionSpec
            {
                Name = "harvest-translations",
                Arity = Arity.Flag,
                Aliases = ["harvest"],
                Help = "Passed through to the import step, and on by default there: also index language files of " +
                       "installed mods that the list does not enable. Pass it explicitly only to be sure.",
            },
            new OptionSpec
            {
                Name = "no-harvest-translations",
                Arity = Arity.Flag,
                Aliases = ["no-harvest"],
                Help = "Passed through to the import step: skip the language-file scan, and record in the snapshot " +
                       "that the disk layer was never measured.",
            },
        ],
        Examples = ["rimsearcher export --modlist vanilla", "rimsearcher export --modlist vanilla --dry-run"],
        JsonKeys =
        [
            new() { Key = "exported", What = "an object: the snapshot that was produced, and what it covers." },
            new() { Key = "would_run", What = "with --dry-run: an object describing the launch that was not performed." },
        ],
    };

    /// <summary>
    /// 把每个 mod 声明的硬依赖补进发射列表,返回补了哪些。就地改 <paramref name="ids"/>。
    ///
    /// 依赖插在**第一个需要它的 mod 之前** —— 游戏要求前置先加载。
    /// 依赖自己还有依赖(AncotLibrary 要 Harmony),所以反复扫到不动为止。
    ///
    /// 没装的依赖不在这里报错,由调用方那段「缺 mod」的检查一并列出。
    /// </summary>
    public static List<string> ResolveDependencies(List<string> ids, IReadOnlyDictionary<string, InstalledMod> installed)
    {
        var added = new List<string>();
        for (var pass = 0; pass < 16; pass++)
        {
            var inserted = false;
            for (var i = 0; i < ids.Count; i++)
            {
                if (!installed.TryGetValue(ids[i], out var mod)) continue;
                foreach (var dep in mod.Dependencies)
                {
                    if (ids.Contains(dep, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!installed.ContainsKey(dep)) continue;   // 没装的留给缺失检查去报
                    ids.Insert(i, dep);
                    added.Add(dep);
                    inserted = true;
                    i++;   // 刚插在当前项之前,当前项后移了一位
                }
            }
            if (!inserted) break;
        }
        return added;
    }

    /// <summary>
    /// 起游戏的命令行。**唯一产地** —— <c>--dry-run</c> 报的和真跑用的必须是同一份。
    ///
    /// 无头是默认,理由在调用处的注释里(渲染零帧、注册表隔离不到)。
    /// </summary>
    public static IReadOnlyList<string> BuildGameArguments(string temp, string outFile, bool showWindow)
    {
        var argv = new List<string>
        {
            $"-savedatafolder={temp}",
            $"-{IntermediateFormat.CommandLineSwitch}={outFile}",
            // 日志必须落在我们指定的地方:Unity 默认那份 LocalLow\Player.log 在挂死的一次跑里
            // 可能一个字都不写。
            $"-logfile={Path.Combine(temp, GameLogName)}",
        };
        if (!showWindow) { argv.Add("-batchmode"); argv.Add("-nographics"); }
        return argv;
    }

    /// <summary>游戏日志在临时 savedata 目录里的文件名。失败时留着不删。</summary>
    public const string GameLogName = "game.log";

    /// <summary>
    /// 一个阶段停多久开始说话。**这是软限制** —— 到点只出一句提醒,不碰进程。
    ///
    /// 硬停只有 <c>--timeout</c> 一个:这个阈值选不准 ——「读定义」那一段随 mod 数量放大,
    /// 20 个 mod 约 35 秒,上百个要几分钟都算正常。误杀不可逆,误报只花一行字。
    /// </summary>
    public const int StageStallSeconds = 120;

    /// <summary>等待期间每一次裁决的三种结果。</summary>
    public enum WaitAction
    {
        /// <summary>继续等。</summary>
        KeepWaiting,
        /// <summary>说一句,然后继续等。</summary>
        Warn,
        /// <summary>到 --timeout 了,停掉它。</summary>
        GiveUp,
    }

    /// <summary>
    /// 等待循环里每 500ms 做一次的裁决。抽成纯函数是因为**这是本命令唯一会主动杀进程的
    /// 地方**,而起一次游戏要几十秒,端到端覆盖不到「什么时候不该杀」。
    /// </summary>
    public static WaitAction Decide(bool pastDeadline, string? stage, double stageSeconds, bool warned)
    {
        if (pastDeadline) return WaitAction.GiveUp;
        if (warned || stageSeconds < StageStallSeconds) return WaitAction.KeepWaiting;
        // 导出阶段不提醒:那一段本来就长,而且对它没有任何下一步可说。
        if (stage == IntermediateFormat.StageExporting) return WaitAction.KeepWaiting;
        return WaitAction.Warn;
    }

    /// <summary>
    /// 停在某一阶段意味着什么。提醒和超时报错共用这一句。
    ///
    /// **判据是 DataMod 在两个分界点写的进度文件,不是 CPU 占用** —— 后者是代理指标,
    /// 会把很慢的 I/O 判成卡死,也会被空转的 mod 盖住真卡死。
    /// </summary>
    public static string StageDiagnosis(string? stage) => stage switch
    {
        null =>
            "The exporter has not reported in at all, so the game never got as far as constructing mod classes. " +
            "Either the exporter mod is not loading, or something stopped the game before that.",
        IntermediateFormat.StageModClasses =>
            "The game has loaded its assemblies but has not started reading defs. A big mod list genuinely takes " +
            "a while here. But this is also where a dialog waiting for a click sits — and with no window there is " +
            "nothing to click, so it would wait forever. A mod dependency that no mod declares is the usual cause: " +
            "this command already adds the declared ones. Re-run with --show-window to see the dialog if there is one.",
        IntermediateFormat.StageExporting =>
            "The game has loaded everything and is writing the export. Time here scales with how much is loaded.",
        _ => $"The game reports stage '{stage}'.",
    };

    /// <summary>
    /// 等游戏跑完。正常结束回 null;只有 <c>--timeout</c> 到了才回一句话(并由调用方停掉它)。
    /// 中途的阶段停顿只经 <paramref name="warn"/> 说出去,进程照跑。
    /// </summary>
    private static string? WaitForGame(
        Process proc, TimeSpan timeout, string outFile, string progressFile, Action<string> warn)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stageSince = DateTime.UtcNow;
        string? stage = null;
        var warned = false;

        while (!proc.WaitForExit(500))
        {
            var now = DateTime.UtcNow;

            // 只认往前走的阶段:游戏正在写那个文件的一瞬间会读到 null,那不是「退回到没有阶段」。
            var seen = ReadStage(progressFile);
            if (seen is not null && seen != stage) { stage = seen; stageSince = now; warned = false; }

            var stageSeconds = (now - stageSince).TotalSeconds;
            switch (Decide(now >= deadline, stage, stageSeconds, warned))
            {
                case WaitAction.KeepWaiting:
                    continue;

                case WaitAction.Warn:
                    warned = true;
                    // 措辞不许把「还没走完」说成「卡死了」:阈值选不准,断言错了会教人去中止
                    // 一次本来能成的导出。只说事实、下一步,并明说还在等。
                    warn($"still at stage '{stage ?? "none"}' after {stageSeconds:0} seconds. " +
                         StageDiagnosis(stage) +
                         $" Still waiting — nothing is stopped until --timeout ({timeout.TotalSeconds:0}s).");
                    continue;

                case WaitAction.GiveUp:
                    return $"The game was still running after {timeout.TotalSeconds:0} seconds and was stopped, " +
                           $"at stage '{stage ?? "none"}'. " + StageDiagnosis(stage) +
                           " A larger mod list may simply need longer: raise --timeout. " +
                           (File.Exists(outFile)
                               ? "An export file was written before the timeout; import it with 'snapshot import'."
                               : "No export file was written.");
            }
        }
        return null;
    }

    /// <summary>读一次进度文件。游戏正在写它的一瞬间读不到 —— 那不是「回退到没有阶段」。</summary>
    private static string? ReadStage(string progressFile)
    {
        try { return File.Exists(progressFile) ? File.ReadAllText(progressFile).Trim() : null; }
        catch { return null; }
    }

    /// <summary>
    /// 失败时才从游戏日志里取最后几行 —— 平常那几十行是 Unity 启动横幅,只在什么都没产出时
    /// 才构成线索。
    /// </summary>
    private static string LastLines(StringBuilder log, int n = 5)
    {
        var lines = log.ToString()
                       .Split('\n')
                       .Select(l => l.TrimEnd('\r'))
                       .Where(l => l.Trim().Length > 0)
                       .ToList();
        if (lines.Count == 0) return " The game printed nothing.";
        return " The game's last output was: " + string.Join(" | ", lines.TakeLast(n));
    }

    public override int Run(CommandContext ctx)
    {
        var listName = ctx.Args.Value("modlist")!;
        var list = ModListIo.Resolve(ctx.Config, listName);
        var snapshotName = ctx.Args.Value("name") ?? list.Name;

        var gameDir = ctx.Config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new CliUsageException(
                "The game directory is not configured, so the game cannot be started. " +
                "Set 'game_dir' in the config file to the folder holding RimWorldWin64.exe.");

        var exe = Directory.EnumerateFiles(gameDir, "RimWorld*.exe").FirstOrDefault()
            ?? throw new CliUsageException($"No RimWorld executable found in '{gameDir}'.");

        // 导出器接挂。必须在扫描之前 —— 扫描要看见它,才判得了「导出器装没装」。
        //
        // using 是本命令唯一的断开产地。漏得掉的只有进程被强杀,那一种由**下一次** Attach
        // 清理(它接管已存在的联接)。
        using var exporterLink = DataModLink.Attach(ctx.Config);
        if (exporterLink.WasAlreadyThere && exporterLink.State == DataModLink.LinkState.Attached)
            ctx.Report.Notice(NoticeKind.Advisory,
                "The exporter was already attached before this run — either an earlier export was killed before it " +
                "could detach, or it was attached by hand. It has been re-attached and will be detached when this " +
                "command finishes.");

        // 启动前验证:缺一个 mod 就失败并报候选,不烧一轮游戏启动。
        var installed = InstalledMods.Scan(ctx.Config);
        var missing = list.Ids.Where(id => !installed.ContainsKey(id)).ToList();

        // 声明了却没装的**依赖**和列表里没装的 mod 是一回事:两者都让游戏在加载定义之前
        // 弹一个点不掉的对话框。所以一条消息一并报,不分两轮。
        var uninstalledDeps = list.Ids
            .Where(installed.ContainsKey)
            .SelectMany(id => installed[id].Dependencies.Select(d => (Needs: id, Dep: d)))
            .Where(t => !installed.ContainsKey(t.Dep) && !list.Ids.Contains(t.Dep, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(t => t.Dep, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count > 0 || uninstalledDeps.Count > 0)
            throw new CliUsageException(
                (missing.Count > 0
                    ? $"{Tally.Complete(missing.Count).Render("mod")} in '{list.Name}' are not installed: " +
                      $"{string.Join(", ", missing)}. "
                    : "") +
                (uninstalledDeps.Count > 0
                    ? $"{Tally.Complete(uninstalledDeps.Count).Render("mod")} declared as a dependency are not " +
                      "installed: " +
                      string.Join(", ", uninstalledDeps.Select(t => $"{t.Dep} (needed by {t.Needs})")) + ". "
                    : "") +
                "The game was not started. " +
                (InstalledMods.Roots(ctx.Config).Count == 0
                    ? "No mod directories are configured either; set 'mod_roots' in the config file."
                    : $"Searched: {string.Join(", ", InstalledMods.Roots(ctx.Config))}."));

        // 导出器不在列表里,游戏会照常起来、走到主菜单,然后**什么也不做** —— 没有人处理
        // 那个命令行开关,于是只能挂到超时。发射前就拦住。
        //
        // 拦住之后是补上而不是拒绝:导出器跟 Harmony 一样属于基础设施,而 `modlist save`
        // 从游戏里捕获的列表天然不会有它。
        var launchIds = list.Ids.ToList();

        // 依赖补全。缺一个硬依赖,游戏会在**加载定义之前**弹一个「缺少前置」的对话框 ——
        // 无头模式下既看不见也点不掉,于是加载完就永久等待。
        //
        // 补而不是拒:依赖是列表作者的疏漏,不是意图。`modlist save` 捕获的列表天然是全的,
        // 手写的才会漏。
        var added = ResolveDependencies(launchIds, installed);
        if (added.Count > 0)
            ctx.Report.Notice(NoticeKind.Advisory,
                $"'{list.Name}' does not list {Tally.Complete(added.Count).Render("mod")} that mods in it " +
                $"declare as dependencies; they were added for this run: {string.Join(", ", added)}.");

        if (!launchIds.Contains(IntermediateFormat.ExporterPackageId, StringComparer.OrdinalIgnoreCase))
        {
            if (!installed.ContainsKey(IntermediateFormat.ExporterPackageId))
                throw new CliUsageException(
                    $"The exporter mod '{IntermediateFormat.ExporterPackageId}' is not installed, so the game has " +
                    "no way to write an export. Build Sources/RimSearcher.DataMod, then either set 'datamod_dir' in " +
                    "the config file to the mod folder the build stages — it is attached for the duration of a run " +
                    "and detached afterwards — or copy that folder into the game's Mods folder to keep it there. " +
                    "The game was not started.");

            launchIds.Add(IntermediateFormat.ExporterPackageId);
            ctx.Report.Notice(NoticeKind.Advisory,
                $"'{list.Name}' does not list the exporter mod, so it was added for this run. " +
                "It is not part of the snapshot's mod list.");
        }

        var exportDir = ctx.Config.ExportDir ?? Path.Combine(ctx.Config.ResolveSnapshotDir(), "..", "exports");
        Directory.CreateDirectory(Path.GetFullPath(exportDir));
        var outFile = Path.GetFullPath(Path.Combine(exportDir, snapshotName + IntermediateFormat.FileExtension));

        var temp = Path.Combine(Path.GetTempPath(), "rimsearcher-export-" + Guid.NewGuid().ToString("N")[..8]);
        var argv = BuildGameArguments(temp, outFile, ctx.Args.Flag("show-window"));

        if (ctx.Args.Flag("dry-run"))
        {
            ctx.Report.Detail("would_run",
            [
                new("modlist", list.Name),
                new("mods", launchIds.Count),
                new("executable", exe),
                new("arguments", string.Join(' ', argv)),
                new("export_file", outFile),
                new("snapshot", snapshotName),
            ]);
            ctx.Report.Notice(NoticeKind.NextStep,
                "Every mod in the list is installed. Drop --dry-run to start the game.");
            return 0;
        }

        PrepareSaveDataFolder(ctx, temp, launchIds);

        if (File.Exists(outFile)) File.Delete(outFile);

        // 进度文件也要先删:留着上一次的,这一次就会带着一个陈旧的阶段开跑。
        var progressFile = outFile + IntermediateFormat.ProgressFileSuffix;
        if (File.Exists(progressFile)) File.Delete(progressFile);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            // 游戏的 stdout(Unity 启动横幅、几十行 memorysetup-*)不接管的话会混进本命令的
            // 输出。接过来丢掉,需要的话看日志文件。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 无头是默认。导出在 StaticConstructorOnStartup 里做完就 Root.Shutdown(),整条路径
        // 一帧都不渲染,于是图形设备纯属额外开销,而窗口还会抢焦点、能被误关。
        //
        // 也不走「开个 640x480 小窗」:-screen-width/-screen-height 会写进
        // HKCU\...\Screenmanager*,而注册表是 -savedatafolder **隔离不到**的地方。
        foreach (var a in argv) psi.ArgumentList.Add(a);

        var timeout = TimeSpan.FromSeconds(ctx.Args.Int("timeout", 900));
        var sw = Stopwatch.StartNew();

        // 声明在 using 之外:游戏没写出文件时,它最后几行就是唯一的线索。
        var gameLog = new StringBuilder();

        using (var proc = Process.Start(psi)
            ?? throw new CliUsageException($"Could not start '{exe}'."))
        {
            // 必须读干净,否则管道缓冲区写满时游戏会卡在写 stdout 上,表现为「导出超时」。
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) gameLog.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) gameLog.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var stall = WaitForGame(proc, timeout, outFile, progressFile,
                // 当场刷出去 —— 这句话的全部价值就在于人还在等的时候能看见。
                warn: line =>
                {
                    ctx.Progress.Write(OutputText.Finish($"{CommandRegistry.ExeName}: {line}"));
                    ctx.Progress.Flush();
                });
            if (stall is not null)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }

                // 失败时临时目录留着 —— 游戏日志在里面,是唯一的现场。
                throw new CliUsageException(stall + LastLines(gameLog) +
                    $" The game's own log was kept at {Path.Combine(temp, GameLogName)}.");
            }
        }

        if (!File.Exists(outFile))
            throw new CliUsageException(
                $"The game exited after {sw.Elapsed.TotalSeconds:0} seconds without writing an export file. " +
                "The usual cause is that the exporter mod is not enabled in this list: it has to be one of the " +
                $"entries in '{list.Name}'." +
                // 第二种成因:一个在加载期碰 GUI 的 mod。不指出来的话会一路把人往
                // 「导出器没装上」那条查不出东西的路上引。
                (ctx.Args.Flag("show-window")
                    ? ""
                    : " If it is enabled, a mod in the list may need a graphics device while loading: " +
                      "retry with --show-window.") +
                LastLines(gameLog) +
                $" The game's own log was kept at {Path.Combine(temp, GameLogName)}.");

        // 与 `snapshot import` 同一个口径:收割默认开,只有显式否定才关 —— 两条路都能造快照。
        var importer = new SnapshotImporter
        {
            ModRoots = ctx.Args.Flag("no-harvest-translations") ? [] : ctx.Config.ModRoots,
        };
        var dbPath = Path.Combine(ctx.Config.ResolveSnapshotDir(), snapshotName + ".db");
        var stats = importer.Import(outFile, dbPath);

        // 指纹自校 —— 请求的 ids 序列必须等于产出 meta 的 ids 序列。
        //
        // 比的是**发射用的**列表,不是原始列表:导出器与补全的依赖是这一轮临时加进去的。
        var produced = stats.Meta.Mods.Select(m => m.PackageId).ToList();
        if (ExportMeta.ComputeModlistFingerprint(launchIds) != ExportMeta.ComputeModlistFingerprint(produced))
            ctx.Report.Notice(NoticeKind.Staleness,
                $"The game reported a different mod list than the one it was given: asked for {launchIds.Count}, " +
                $"got {produced.Count}. The snapshot describes what the game actually loaded, not what was requested. " +
                "A mod that fails its own load check is the usual cause.");

        ctx.Report.Detail("exported",
        [
            new("snapshot", snapshotName),
            new("path", dbPath),
            new("defs", stats.Defs),
            new("mods", produced.Count),
            new("game_version", stats.Meta.GameVersion),
            new("seconds", (int)sw.Elapsed.TotalSeconds),
        ]);

        try { File.Delete(progressFile); } catch { /* 留着也无害 */ }

        if (!ctx.Args.Flag("keep-temp"))
            try { Directory.Delete(temp, recursive: true); } catch { /* 留着也无害 */ }

        return 0;
    }

    private static void PrepareSaveDataFolder(CommandContext ctx, string temp, IReadOnlyList<string> ids)
    {
        var realConfig = Path.GetDirectoryName(Path.GetFullPath(ctx.Config.ModsConfigPath()));
        var targetConfig = Path.Combine(temp, "Config");
        Directory.CreateDirectory(targetConfig);

        if (realConfig is { Length: > 0 } && Directory.Exists(realConfig))
            foreach (var file in Directory.EnumerateFiles(realConfig))
                File.Copy(file, Path.Combine(targetConfig, Path.GetFileName(file)), overwrite: true);

        var modsConfig = Path.Combine(targetConfig, "ModsConfig.xml");
        XDocument doc;
        if (File.Exists(modsConfig))
        {
            doc = XDocument.Load(modsConfig);
            doc.Root!.Element("activeMods")?.Remove();
        }
        else
        {
            doc = new XDocument(new XElement("ModsConfigData", new XElement("version", "1.6")));
        }
        doc.Root!.Add(new XElement("activeMods", ids.Select(i => new XElement("li", i))));
        doc.Save(modsConfig);
    }
}
