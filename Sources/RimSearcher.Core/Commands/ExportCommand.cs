using RimTooling.Cli;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;
using RimTooling.IO;
using RimTooling.Processes;
using RimTooling.RimWorld;

namespace RimSearcher.Commands;

public sealed class ExportCommand : Command
{
    private static readonly CliOption Modlist = CliOption.Text("modlist") with
    {
        Aliases = ["profile"],
        Placeholder = "<名称>",
        Help = "本次导出所用的 mod 名单。名单名称见 'rimsearcher modlist list'。",
        Required = true,
    };

    private static readonly CliOption NameOption = CliOption.Text("name") with
    {
        Aliases = ["as", "alias"],
        Placeholder = "<名称>",
        Help = "生成的快照所登记的名称。缺省为 mod 名单的名称。",
    };

    private static readonly CliOption Timeout = CliOption.Text("timeout") with
    {
        Aliases = ["timeout-seconds", "wait"],
        Placeholder = "<秒数>",
        Help = "等待游戏结束的时长。mod 名单较大时加载可能需要数分钟。",
        Default = "900",
    };

    private static readonly CliOption ShowWindow = CliOption.Flag("show-window") with
    {
        Aliases = ["window", "windowed", "graphics"],
        Help = "以可见窗口启动游戏。名单中有 mod 在加载时需要图形设备，或须查看游戏加载时弹出的对话框时使用。" +
               "除此之外，缺省方式结果相同。",
    };

    private static readonly CliOption NoEconomy = CliOption.Flag("no-economy") with
    {
        Aliases = ["skip-economy", "without-economy"],
        Help = "跳过经济层：marketValue、costToMake、workToProduce 与成本链。",
    };

    private static readonly CliOption KeepTemp = CliOption.Flag("keep-temp") with
    {
        Help = "结束后保留临时存档数据目录，用于查看交给游戏的内容。",
    };

    private static readonly CliOption DryRun = CliOption.Flag("dry-run") with
    {
        Aliases = ["plan"],
        Help = "不启动游戏：只解析名单、核对每个 mod 均已安装，并报告将要执行的内容。",
    };

    private static readonly CliOption NoHarvestTranslations = CliOption.Flag("no-harvest-translations") with
    {
        Aliases = ["no-harvest"],
        Help = "跳过语言文件扫描，并在快照中记录 mod 语言文件这一层未采集。" +
               "不指定时，名单未启用的已安装 mod，其语言文件也建入索引。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "export",
        Summary = "以选定的 mod 名单无人值守地运行游戏，并导入其导出的数据。",
        Remarks = "游戏在临时的存档数据目录中运行，游戏自身的配置与存档保持原样。",
        Options =
        [
            Modlist,
            NameOption,
            Timeout,
            ShowWindow,
            NoEconomy,
            KeepTemp,
            DryRun,
            NoHarvestTranslations,
            SnapshotRetention.Keep,
            SnapshotRetention.ReplacePrev,
        ],
        Examples = ["rimsearcher export --modlist vanilla", "rimsearcher export --modlist vanilla --dry-run"],
        JsonKeys =
        [
            new("exported",
                "一个对象：生成的快照及其覆盖范围。"),
            new("would_run",
                "带 --dry-run 时，一个对象，描述未执行的启动。"),
        ],
    };

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
                    if (!installed.ContainsKey(dep)) continue;
                    ids.Insert(i, dep);
                    added.Add(dep);
                    inserted = true;
                    i++;
                }
            }
            if (!inserted) break;
        }
        return added;
    }

    public static IReadOnlyList<string> BuildGameArguments(GameEngine engine, string temp, string outFile,
                                                           bool showWindow, bool skipEconomy = false)
    {
        var argv = new List<string>
        {
            $"-savedatafolder={temp}",
            $"-{IntermediateFormat.CommandLineSwitch}={outFile}",
        };
        if (engine == GameEngine.Unity)
        {
            argv.Add("-logFile");
            argv.Add(Path.Combine(temp, GameLogName));
            if (!showWindow) { argv.Add("-batchmode"); argv.Add("-nographics"); }
        }
        else if (!showWindow) argv.Add("-headless");
        if (skipEconomy) argv.Add($"-{IntermediateFormat.SkipEconomySwitch}");
        return argv;
    }

    public const string GameLogName = "game.log";

    public const int StageStallSeconds = 120;

    public enum WaitAction
    {
        KeepWaiting,
        Warn,
        GiveUp,
        LoadFailed,
    }

    public static WaitAction Decide(bool pastDeadline, string? stage, double stageSeconds, bool warned,
                                    bool loadFailed = false)
    {
        if (loadFailed) return WaitAction.LoadFailed;
        if (pastDeadline) return WaitAction.GiveUp;
        if (warned || stageSeconds < StageStallSeconds) return WaitAction.KeepWaiting;
        if (stage == IntermediateFormat.StageExporting) return WaitAction.KeepWaiting;
        return WaitAction.Warn;
    }

    public static string StageDiagnosis(string? stage) => StageDiagnosis(stage, windowShown: false);

    public static string StageDiagnosis(string? stage, bool windowShown) => stage switch
    {
        null =>
            "导出器没有报告过进度：游戏未进行到构造 mod 类，或导出器 mod 未加载。",
        IntermediateFormat.StageModClasses =>
            "游戏已构造 mod 类，正在加载 def 与其他内容；加载完成后导出开始。" +
            (windowShown ? "进度见游戏窗口。" : "加 --show-window 重新执行，可显示游戏窗口。"),
        IntermediateFormat.StageExporting =>
            "游戏已加载全部内容，正在写入导出文件。这一阶段的耗时随加载内容的规模增长。",
        _ => $"游戏报告的阶段为 {stage}。",
    };

    private sealed record GameStop(string Message, bool QuoteLastLines);

    private static GameStop? WaitForGame(
        LaunchedProcess proc, TimeSpan timeout, string outFile, string progressFile, bool windowShown,
        Func<string?> loadFailure, Action<string> warn)
    {
        var started = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + timeout;
        var stageSince = DateTime.UtcNow;
        string? stage = null;
        var warned = false;

        while (!proc.WaitForExit(500))
        {
            var now = DateTime.UtcNow;

            var seen = ReadStage(progressFile);
            if (seen is not null && seen != stage) { stage = seen; stageSince = now; warned = false; }

            var stageSeconds = (now - stageSince).TotalSeconds;
            var failure = loadFailure();
            switch (Decide(now >= deadline, stage, stageSeconds, warned, failure is not null))
            {
                case WaitAction.KeepWaiting:
                    continue;

                case WaitAction.Warn:
                    warned = true;
                    warn($"{stageSeconds:0} 秒后仍处于阶段 {stage ?? "无"}。" +
                         StageDiagnosis(stage, windowShown) +
                         $"仍在等待；--timeout 为 {timeout.TotalSeconds:0} 秒。");
                    continue;

                case WaitAction.GiveUp:
                    return new GameStop(
                        $"游戏运行 {timeout.TotalSeconds:0} 秒后仍未结束，已被停止，停止时处于阶段 {stage ?? "无"}。" +
                        StageDiagnosis(stage, windowShown) +
                        "" +
                        (File.Exists(outFile)
                            ? "超时之前已写出导出文件，可由 'rimsearcher snapshot import' 导入。"
                            : "未写出导出文件。"),
                        QuoteLastLines: true);

                case WaitAction.LoadFailed:
                    return new GameStop(
                        $"游戏自身的数据加载在第 {(now - started).TotalSeconds:0} 秒失败。" +
                        $"游戏已被停止。游戏的报错：{failure}",
                        QuoteLastLines: false);
            }
        }
        return null;
    }

    private static string? ReadStage(string progressFile)
    {
        try { return File.Exists(progressFile) ? File.ReadAllText(progressFile).Trim() : null; }
        catch { return null; }
    }

    public static string GameLogNote(string temp)
    {
        var path = Path.Combine(temp, GameLogName);
        return File.Exists(path)
            ? $"游戏自身的日志保留在 {path}。"
            : $"游戏未在 {path} 写出日志文件。";
    }

    public static string LastLines(string logPath, string? stdout, int n = 5)
    {
        var lines = GameLauncher.ReadLogTail(logPath, n, skipWaylithStackFrames: true, fallbackOutput: stdout);
        if (lines.Count == 0) return "游戏没有任何输出。";
        return "游戏最后的输出：" + string.Join(" | ", lines) + "。";
    }

    public override int Run(CommandContext ctx)
    {
        var listName = ctx.Args.Value(Modlist)!;
        var list = ModListIo.Resolve(ctx.Config, listName);
        var snapshotName = ctx.Args.Value(NameOption) ?? list.Name;

        var gameDir = ctx.Config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            throw new CliUsageException(
                (string.IsNullOrWhiteSpace(gameDir) ? "未配置游戏目录" : $"game_dir 所指的目录 {gameDir} 不存在") + "，因此无法启动游戏。" +
                $"在配置文件中把 game_dir 设为 {Sources.GameLayout.ExeNames} 所在的目录。");

        Sources.GameLayout layout;
        try
        {
            layout = Sources.GameLayout.Detect(gameDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var coreName = RimTooling.RimWorld.GameLayout.CoreAssemblyFileName;
            var corePath = Path.Combine(gameDir, coreName);
            if (AtomicFile.HasTemporary(corePath))
                throw new CliUsageException(
                    $"无法判断 {gameDir} 中安装的 RimWorld 引擎：{coreName} 旁存在写入临时文件。" +
                    "等待写入完成或清理临时文件后重新执行。");
            if (ex is UnauthorizedAccessException)
                throw new CliUsageException(
                    $"无法判断 {gameDir} 中安装的 RimWorld 引擎：检查 {coreName} 时被拒绝访问。" +
                    "核对该目录的权限后重新执行。");
            throw new CliUsageException(
                $"无法判断 {gameDir} 中安装的 RimWorld 引擎：检查 {coreName} 时读取失败（{ex.Message}）。" +
                "核对该目录状态后重新执行。");
        }

        var exe = layout.Exe;
        if (!File.Exists(exe))
            throw new CliUsageException(
                $"{gameDir} 中没有 RimWorld 可执行文件：game_dir 应为 {Sources.GameLayout.ExeNames} 所在的目录。");

        var installed = InstalledMods.Scan(ctx.Config);
        var missing = list.Ids.Where(id => !installed.ContainsKey(id)).ToList();

        var uninstalledDeps = list.Ids
            .Where(installed.ContainsKey)
            .SelectMany(id => installed[id].Dependencies.Select(d => (Needs: id, Dep: d)))
            .Where(t => !installed.ContainsKey(t.Dep) && !list.Ids.Contains(t.Dep, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(t => t.Dep, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count > 0 || uninstalledDeps.Count > 0)
            throw new CliUsageException(
                (missing.Count > 0
                    ? Cjk.Join(list.Name, "中有", Tally.Complete(missing.Count).Render("mod"), "未安装：") +
                      $"{string.Join("、", missing)}。"
                    : "") +
                (uninstalledDeps.Count > 0
                    ? Cjk.Join(Tally.Complete(uninstalledDeps.Count).Render("mod"), "被声明为依赖但未安装：") +
                      string.Join("、", uninstalledDeps.Select(t => $"{t.Dep}（{t.Needs} 需要）")) + "。"
                    : "") +
                "游戏未启动。" +
                (InstalledMods.Roots(ctx.Config).Count == 0
                    ? "也未配置任何 mod 目录；在配置文件中设置 mod_roots。"
                    : $"已搜索：{string.Join("、", InstalledMods.Roots(ctx.Config))}。"));

        var launchIds = list.Ids.ToList();

        var added = ResolveDependencies(launchIds, installed);
        if (added.Count > 0)
            ctx.Report.Notice(NoticeKind.Advisory,
                Cjk.Join($"名单 {list.Name} 未列出其 mod 所依赖的", Tally.Complete(added.Count).Render("mod")) +
                $"；本次执行已补上：{string.Join("、", added)}。");

        ExporterMod.RequireAssembly();
        var exporterId = ExporterMod.NewPackageId();
        launchIds.Add(exporterId);

        var exportDir = ctx.Config.ResolveExportDir();
        Directory.CreateDirectory(Path.GetFullPath(exportDir));
        var outFile = SnapshotCatalog.ExportPath(ctx.Config, snapshotName);

        var temp = Path.Combine(Path.GetTempPath(), "rimsearcher-export-" + Guid.NewGuid().ToString("N")[..8]);
        var argv = BuildGameArguments(layout.Engine, temp, outFile, ctx.Args.Flag(ShowWindow),
                                      ctx.Args.Flag(NoEconomy));
        var windowShown = ctx.Args.Flag(ShowWindow);

        if (ctx.Args.Flag(DryRun))
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
                "名单中的每个 mod 均已安装。");
            return 0;
        }

        using var exporter = ExporterMod.Attach(ctx.Config, gameDir, exporterId);

        PrepareSaveDataFolder(ctx, temp, launchIds);

        if (File.Exists(outFile)) File.Delete(outFile);

        var progressFile = outFile + IntermediateFormat.ProgressFileSuffix;
        if (File.Exists(progressFile)) File.Delete(progressFile);

        var timeout = TimeSpan.FromSeconds(ctx.Args.Int(Timeout, 900));
        var sw = Stopwatch.StartNew();
        var logPath = Path.Combine(temp, GameLogName);
        var watch = new LoadFailureWatch();
        var isWaylith = layout.Engine == GameEngine.Waylith;

        LaunchedProcess launched;
        try
        {
            launched = GameLauncher.Start(new GameLaunchOptions
            {
                ExecutablePath = exe,
                WorkingDirectory = gameDir,
                Arguments = argv,
                LogFilePath = isWaylith ? logPath : null,
                OnOutputLine = isWaylith ? line => watch.Feed(line) : null,
                CaptureOutput = !isWaylith,
                FailIfExitedImmediately = false,
            });
        }
        catch (ProcessLaunchException ex)
        {
            throw new GameRunException($"无法启动 {exe}：{ex.Message}");
        }

        string capturedOutput;
        using (launched)
        {
            var stop = WaitForGame(launched, timeout, outFile, progressFile, windowShown,
                loadFailure: () => watch.Failure,
                warn: line =>
                {
                    ctx.Progress.Write(OutputText.Finish($"{CommandRegistry.ExeName}: {line}"));
                    ctx.Progress.Flush();
                });
            if (stop is not null)
            {
                launched.TryKill(entireProcessTree: true);
                launched.WaitForExit(5_000);
                launched.CloseOutput();

                throw new GameRunException(stop.Message +
                    (stop.QuoteLastLines ? LastLines(logPath, launched.CapturedOutput) : "") +
                    GameLogNote(temp));
            }

            launched.WaitForExit();
            launched.CloseOutput();
            capturedOutput = launched.CapturedOutput;
        }

        if (!File.Exists(outFile))
            throw new GameRunException(
                $"游戏运行 {sw.Elapsed.TotalSeconds:0} 秒后退出，未写出导出文件。" +
                (windowShown ? "" : "加 --show-window 重新执行，可显示游戏窗口。") +
                LastLines(logPath, capturedOutput) +
                GameLogNote(temp));

        var harvest = !ctx.Args.Flag(NoHarvestTranslations);
        var importer = new SnapshotImporter
        {
            ModRoots = harvest ? ctx.Config.ModRoots : [],
            HarvestRequested = harvest,
            Environment = ctx.Config,
        };
        var dbPath = SnapshotCatalog.DatabasePath(ctx.Config, snapshotName);
        var incoming = SnapshotRetention.IncomingPath(dbPath);
        var stats = importer.Import(outFile, incoming);
        var keep = SnapshotRetention.ResolveKeep(ctx.Config, ctx.Args);
        var install = SnapshotRetention.Install(incoming, dbPath, keep);
        if (install.Kind == SnapshotInstallKind.Unchanged)
            ctx.Report.Notice(NoticeKind.Count, SnapshotRetention.Unchanged(snapshotName));
        else if (install.Kind == SnapshotInstallKind.Replaced && install.Kept > 0)
            ctx.Report.Notice(NoticeKind.NextStep, SnapshotRetention.KeptPrevious(snapshotName, install.Kept));
        if (install.Dropped.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary, SnapshotRetention.DroppedOldest(install.Dropped, keep));

        var produced = stats.Meta.Mods.Select(m => m.PackageId).ToList();
        var requested = launchIds
            .Select(id => IntermediateFormat.IsExporterPackageId(id) ? IntermediateFormat.ExporterPackageId : id)
            .ToList();
        if (ExportMeta.ComputeModlistFingerprint(requested) != ExportMeta.ComputeModlistFingerprint(produced))
        {
            var notLoaded = requested.Except(produced, StringComparer.OrdinalIgnoreCase).ToList();
            var unasked = produced.Except(requested, StringComparer.OrdinalIgnoreCase).ToList();
            var parts = new List<string>();
            if (notLoaded.Count > 0) parts.Add($"未加载：{NameList.Render(notLoaded, 6)}");
            if (unasked.Count > 0) parts.Add($"未经请求而加载：{NameList.Render(unasked, 6)}");
            if (parts.Count == 0) parts.Add("mod 相同而加载顺序不同");
            ctx.Report.Notice(NoticeKind.Staleness,
                $"游戏加载的 mod 与交给游戏的名单不匹配（请求 {Tally.Complete(requested.Count).Render("mod")}，" +
                $"加载 {Tally.Complete(produced.Count).Render("mod")}）：{string.Join("；", parts)}。");
        }

        ctx.Report.Detail("exported",
        [
            new("snapshot", snapshotName),
            new("path", dbPath),
            new("defs", stats.Defs),
            new("mods", produced.Count),
            new("game_version", stats.Meta.GameVersion),
            new("seconds", (int)sw.Elapsed.TotalSeconds),
        ]);

        try { File.Delete(progressFile); } catch {  }

        if (!ctx.Args.Flag(KeepTemp))
            try { FileSystemSafety.DeleteTreeJunctionSafe(temp); } catch {  }

        return 0;
    }

    public sealed class LoadFailureWatch
    {
        private const string AsyncEvent = "Exception from asynchronous event: ";
        private const string LoadEntry = "Verse.PlayDataLoader.LoadAllPlayData";
        private const string Recovering = "Caught exception while loading play data";
        private const string ExceptionWas = "The exception was: ";
        private static readonly Regex Entry = new(@"^\d\d:\d\d:\d\d\s+(?:\w+\s+)?(?<message>.*)$", RegexOptions.CultureInvariant);

        private string? _asyncEvent;
        private string? _recovering;
        private volatile string? _failure;

        public string? Failure => _failure;

        public void Feed(string line)
        {
            if (_failure is not null) return;
            var entry = Entry.Match(line);
            if (entry.Success)
            {
                if (_recovering is not null) { _failure = _recovering; return; }
                var message = entry.Groups["message"].Value.Trim();
                var at = message.IndexOf(AsyncEvent, StringComparison.Ordinal);
                _asyncEvent = at >= 0 ? message[(at + AsyncEvent.Length)..] : null;
                _recovering = message.Contains(Recovering, StringComparison.Ordinal) ? message : null;
                return;
            }
            if (_asyncEvent is not null && line.Contains(LoadEntry, StringComparison.Ordinal))
                _failure = _asyncEvent;
            else if (_recovering is not null && line.StartsWith(ExceptionWas, StringComparison.Ordinal))
                _failure = line[ExceptionWas.Length..].Trim();
        }
    }

    public static IReadOnlyList<string> ActiveModIds(IEnumerable<string> ids)
        => ids.Select(i => i.ToLowerInvariant()).ToList();

    private static void PrepareSaveDataFolder(CommandContext ctx, string temp, IReadOnlyList<string> ids)
    {
        var realModsConfig = ctx.Config.ModsConfigPath();
        var realConfig = realModsConfig is { Length: > 0 }
            ? Path.GetDirectoryName(Path.GetFullPath(realModsConfig))
            : null;
        var targetConfig = Path.Combine(temp, "Config");
        Directory.CreateDirectory(targetConfig);

        if (realConfig is { Length: > 0 } && Directory.Exists(realConfig))
            foreach (var file in Directory.EnumerateFiles(realConfig))
                File.Copy(file, Path.Combine(targetConfig, Path.GetFileName(file)), overwrite: true);

        IReadOnlyList<string>? knownExpansions = null;
        if (ctx.Config.GameDir is { Length: > 0 } gameDir)
        {
            try
            {
                var dataDir = RimTooling.RimWorld.GameLayout.Of(gameDir).DataDir;
                if (Directory.Exists(dataDir))
                    knownExpansions = RimWorldModsConfig.KnownExpansions(dataDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        var modsConfig = Path.Combine(targetConfig, "ModsConfig.xml");
        var doc = ModsConfig(File.Exists(modsConfig) ? XDocument.Load(modsConfig) : null, ids,
                             GameBuild.Installed(ctx.Config.GameDir), knownExpansions);
        AtomicFile.Write(modsConfig, tmp => doc.Save(tmp));
    }

    public static XDocument ModsConfig(XDocument? copied, IEnumerable<string> ids, string? installedVersion,
                                       IEnumerable<string>? knownExpansions = null)
        => copied is null
            ? RimWorldModsConfig.Create(ids, knownExpansions, installedVersion)
            : RimWorldModsConfig.Rewrite(copied, ids, installedVersion, knownExpansions);
}
