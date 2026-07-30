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
/// **真实 ModsConfig.xml 永不触碰**(第二轮裁决 6)。备份还原方案被否决过:换入之后、还原
/// 之前的那段窗口里一崩,用户的 mod 配置就留在了被改写的状态;而且游戏退出时自己也会回写
/// ModsConfig,和还原动作是竞争关系。取而代之的是 <c>-savedatafolder</c> 隔离:整个 SaveData
/// 重定向到临时目录,真配置连打开都没打开过。
///
/// Config/ 是**整体复制**而不是只造一个 ModsConfig.xml:各 mod 的 Mod_*.xml 设置会改 patch
/// 结果(03 甲的 ConditionalSettings/EasyMode),丢掉它们导出的就是另一份数据。
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
                Help = "Passed through to the import step: also index language files of installed mods that the " +
                       "list does not enable.",
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
    /// 依赖插在**第一个需要它的 mod 之前** —— 前置必须先加载,否则补了等于没补。
    /// 依赖自己还有依赖(AncotLibrary 要 Harmony),所以反复扫到不动为止。
    ///
    /// 没装的依赖不在这里报错:那由调用方那段「缺 mod」的检查统一报,消息里能一并列出。
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
    /// 起游戏的命令行。**唯一产地** —— <c>--dry-run</c> 报的和真跑用的是同一份,
    /// 否则 dry-run 就成了「报告一件与实际不同的事」,而它存在的全部意义就是先看清楚。
    ///
    /// 无头是默认,理由在调用处的注释里(渲染零帧、注册表隔离不到)。
    /// </summary>
    public static IReadOnlyList<string> BuildGameArguments(string temp, string outFile, bool showWindow)
    {
        var argv = new List<string>
        {
            $"-savedatafolder={temp}",
            $"-{IntermediateFormat.CommandLineSwitch}={outFile}",
            // 日志必须落在我们指定的地方。Unity 默认写 LocalLow 那份 Player.log,而**这次跑
            // 有可能一个字都不写进去**(实测:一次挂死的导出,那个文件的时间戳停在半小时前)。
            // 没有日志,「游戏卡住了」就只剩一句没有下文的话。
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
    /// 硬停自始至终只有 <c>--timeout</c> 一个,因为这个阈值**注定选不准**:
    /// 「读定义」那一段随 mod 数量放大,20 个 mod 实测 35 秒,几十上百个 mod 的整备列表
    /// 要几分钟是正常的。拿它去杀进程,就是拿一个猜出来的数去毁掉一次已经付过的加载 ——
    /// 而误杀不可逆,误报只花一行字。所以选可逆的那一侧:到点报,不到点闭嘴,杀不杀交给
    /// 人显式给的 --timeout。
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
    /// 地方**,而它必须判得了 —— 起一次游戏要几十秒,靠实测覆盖不到「什么时候不该杀」。
    /// </summary>
    public static WaitAction Decide(bool pastDeadline, string? stage, double stageSeconds, bool warned)
    {
        if (pastDeadline) return WaitAction.GiveUp;
        if (warned || stageSeconds < StageStallSeconds) return WaitAction.KeepWaiting;
        // 导出跑起来之后不提醒:那一段本来就长,而这里对它没有任何下一步可说 ——
        // 一句没有下文的提醒只是噪音,还会把上面那两句真有下文的稀释掉。
        if (stage == IntermediateFormat.StageExporting) return WaitAction.KeepWaiting;
        return WaitAction.Warn;
    }

    /// <summary>
    /// 停在某一阶段意味着什么。提醒和超时报错共用这一句 —— 两处说的是同一件事,
    /// 分两处写迟早会各说各的。
    ///
    /// **判据是游戏侧自报的阶段,不是 CPU 占用。**曾经想用「加载完了 CPU 却贴近零」来认
    /// 卡在对话框上的样子 —— 那是代理指标,而代理会撒谎:一段真的很慢的 I/O 会被判成卡死,
    /// 一个空转的 mod 又会把真卡死盖过去。改由 DataMod 在两个分界点写进度文件,
    /// 于是「停在哪一步」是**事实**,只有「这一步为什么久」才是推测。
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

            // 只认往前走的阶段。游戏正在写那个文件的一瞬间会读到 null,而那不是「退回到没有
            // 阶段」—— 把它当成变化会白白重置计时器,把真卡住的那一次拖成超时。
            var seen = ReadStage(progressFile);
            if (seen is not null && seen != stage) { stage = seen; stageSince = now; warned = false; }

            var stageSeconds = (now - stageSince).TotalSeconds;
            switch (Decide(now >= deadline, stage, stageSeconds, warned))
            {
                case WaitAction.KeepWaiting:
                    continue;

                case WaitAction.Warn:
                    warned = true;
                    // 措辞不许把「还没走完」说成「卡死了」:这个阈值选不准,而一句断言错了
                    // 就会教人去中止一次本来能成的导出。说事实(停在哪一步、多久),
                    // 说下一步(要看对话框就 --show-window),然后明说还在等。
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
    /// 失败时才从游戏日志里取最后几行。成功路径一个字节都不带 —— 平常那几十行
    /// Unity 启动横幅对调用方没有任何价值,只在什么都没产出时才是唯一线索。
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
        // using 是本命令唯一的断开产地:下面每一条 throw、每一个 return 都会经过它。
        // 唯一漏得掉的是进程被强杀,而那一种由**下一次** Attach 清理(它照样接管已存在的联接)。
        using var exporterLink = DataModLink.Attach(ctx.Config);
        if (exporterLink.WasAlreadyThere && exporterLink.State == DataModLink.LinkState.Attached)
            ctx.Report.Notice(NoticeKind.Advisory,
                "The exporter was already attached before this run — either an earlier export was killed before it " +
                "could detach, or it was attached by hand. It has been re-attached and will be detached when this " +
                "command finishes.");

        // 步骤 2:启动前验证。缺一个 mod 就失败并报候选,不烧一轮游戏启动。
        var installed = InstalledMods.Scan(ctx.Config);
        var missing = list.Ids.Where(id => !installed.ContainsKey(id)).ToList();

        // 声明了却没装的**依赖**和列表里没装的 mod 是一回事:两者都让游戏在加载定义之前
        // 弹一个点不掉的对话框。分成两条消息报,第二条就会在第一条通过之后才出现 ——
        // 那等于让人白跑一次几十秒的加载。
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
        // 那个命令行开关。这件事在启动前一毫秒就能知道,原来却要等到超时(实测:23 个 mod
        // 的列表挂了二十分钟)才由一条事后消息告诉你。发射前就该拦住。
        //
        // 拦住之后是补上而不是拒绝:导出器是工具而不是内容,跟 Harmony 一样属于基础设施。
        // 让每一份 mod 列表都记得带上它,只是把工具的实现细节摊派给了使用者 ——
        // 而 `modlist save` 从游戏里捕获的列表天然就不会有它。
        var launchIds = list.Ids.ToList();

        // 依赖补全。缺一个硬依赖,游戏会在**加载定义之前**弹一个「缺少前置」的对话框 ——
        // 无头模式下它既看不见也点不掉,于是加载完就永久等待。实测:手写的 races 列表漏了
        // Ancot.AncotLibrary,挂到超时才收场。这与「导出器不在列表里」是同一个缺陷的同一种
        // 形状,当时只堵了那一个具体条目而没有推广,于是换个条目又踩一遍。
        //
        // 补而不是拒:依赖是列表作者的疏漏,不是意图。`modlist save` 从游戏里捕获的列表
        // 天然是全的,手写的才会漏,而手写正是这条路存在的理由。
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

        // 进度文件也要先删。留着上一次的,这一次就会带着一个陈旧的阶段开跑 ——
        // 与探针那次「读到上一轮的日志签名、把上次的失败当成这次的结论」是同一个错。
        var progressFile = outFile + IntermediateFormat.ProgressFileSuffix;
        if (File.Exists(progressFile)) File.Delete(progressFile);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            // 游戏的 stdout(Unity 的启动横幅、几十行 memorysetup-*)会顺着我们的 stdout 一起
            // 流给调用方,把一条 6 行的结果冲成 80 行。它既不是本命令的输出,也不构成契约的
            // 一部分 —— 接过来丢掉,需要的话再走 --verbose 交给日志文件。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 无头是默认。导出在 StaticConstructorOnStartup 里做完就 Root.Shutdown(),整条路径
        // 一帧都不渲染 —— 于是 Unity 的图形设备纯属额外开销,而那个抢焦点、能被误关的窗口
        // 更是纯粹的副作用。实测(23 mod + 导出器):无头 26 秒、窗口 27 秒,产出 defs /
        // field_values / translations 逐项相同。
        //
        // 不走「开个 640x480 小窗」那条路,虽然它也能跑通:-screen-width/-screen-height
        // 会写进 HKCU\...\Screenmanager*,而那是 -savedatafolder **隔离不到**的地方
        // (实测确实改了 Window Position Y)。导出不许在真实配置上留下任何痕迹,注册表
        // 也算真实配置。无头两次实测注册表零改动。
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
                // 当场刷出去。攒在缓冲里等命令结束才吐,就跟把它放进 Report 里一样没用 ——
                // 这句话的全部价值就在于人还在等的时候能看见。
                warn: line =>
                {
                    ctx.Progress.Write(OutputText.Finish($"{CommandRegistry.ExeName}: {line}"));
                    ctx.Progress.Flush();
                });
            if (stall is not null)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }

                // 失败时临时目录留着 —— 抛异常就走不到末尾那段清理,而游戏日志在里面,
                // 是唯一的现场。出了事还清理证物,等于让下一次调查从零开始。
                throw new CliUsageException(stall + LastLines(gameLog) +
                    $" The game's own log was kept at {Path.Combine(temp, GameLogName)}.");
            }
        }

        if (!File.Exists(outFile))
            throw new CliUsageException(
                $"The game exited after {sw.Elapsed.TotalSeconds:0} seconds without writing an export file. " +
                "The usual cause is that the exporter mod is not enabled in this list: it has to be one of the " +
                $"entries in '{list.Name}'." +
                // 无头是默认之后多了第二种成因。不指出来的话,一个在加载期碰 GUI 的 mod 会
                // 一路把人往「导出器没装上」那条错路上引 —— 而那条路上什么也查不出来。
                (ctx.Args.Flag("show-window")
                    ? ""
                    : " If it is enabled, a mod in the list may need a graphics device while loading: " +
                      "retry with --show-window.") +
                LastLines(gameLog) +
                $" The game's own log was kept at {Path.Combine(temp, GameLogName)}.");

        var importer = new SnapshotImporter
        {
            ModRoots = ctx.Args.Flag("harvest-translations") ? ctx.Config.ModRoots : [],
        };
        var dbPath = Path.Combine(ctx.Config.ResolveSnapshotDir(), snapshotName + ".db");
        var stats = importer.Import(outFile, dbPath);

        // 步骤 6:指纹自校 —— 请求的 ids 序列必须等于产出 meta 的 ids 序列。
        // 期望环境由 CLI 主动制造并验证过,自动检测就只剩「手动导出归属谁」一个用途了。
        //
        // 比的是**发射用的**列表,不是原始列表:导出器是这一轮临时加进去的基础设施,
        // 拿原始列表去比,每一次自动补全都会报一条假的「游戏加载的和要求的不一样」。
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
