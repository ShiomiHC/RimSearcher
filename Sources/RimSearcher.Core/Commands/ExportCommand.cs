using System.Diagnostics;
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
            "import is rejected if they differ.",
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
                Help = "How long to wait for the game to finish. A large mod list can take minutes to load.",
                Default = "900",
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
    };

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

        // 步骤 2:启动前验证。缺一个 mod 就失败并报候选,不烧一轮游戏启动。
        var installed = InstalledMods.Scan(ctx.Config);
        var missing = list.Ids.Where(id => !installed.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new CliUsageException(
                $"{Tally.Complete(missing.Count).Render("mod")} in '{list.Name}' are not installed: " +
                $"{string.Join(", ", missing)}. The game was not started. " +
                (InstalledMods.Roots(ctx.Config).Count == 0
                    ? "No mod directories are configured either; set 'mod_roots' in the config file."
                    : $"Searched: {string.Join(", ", InstalledMods.Roots(ctx.Config))}."));

        var exportDir = ctx.Config.ExportDir ?? Path.Combine(ctx.Config.ResolveSnapshotDir(), "..", "exports");
        Directory.CreateDirectory(Path.GetFullPath(exportDir));
        var outFile = Path.GetFullPath(Path.Combine(exportDir, snapshotName + IntermediateFormat.FileExtension));

        var temp = Path.Combine(Path.GetTempPath(), "rimsearcher-export-" + Guid.NewGuid().ToString("N")[..8]);

        if (ctx.Args.Flag("dry-run"))
        {
            ctx.Report.Detail("would_run",
            [
                new("modlist", list.Name),
                new("mods", list.Ids.Count),
                new("executable", exe),
                new("export_file", outFile),
                new("snapshot", snapshotName),
            ]);
            ctx.Report.Notice(NoticeKind.NextStep,
                "Every mod in the list is installed. Drop --dry-run to start the game.");
            return 0;
        }

        PrepareSaveDataFolder(ctx, temp, list.Ids);

        if (File.Exists(outFile)) File.Delete(outFile);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"-savedatafolder={temp}");
        psi.ArgumentList.Add($"-{IntermediateFormat.CommandLineSwitch}={outFile}");

        var timeout = TimeSpan.FromSeconds(ctx.Args.Int("timeout", 900));
        var sw = Stopwatch.StartNew();

        using (var proc = Process.Start(psi)
            ?? throw new CliUsageException($"Could not start '{exe}'."))
        {
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
                throw new CliUsageException(
                    $"The game was still running after {timeout.TotalSeconds:0} seconds and was stopped. " +
                    "A larger mod list may simply need longer: raise --timeout. " +
                    (File.Exists(outFile)
                        ? "An export file was written before the timeout; import it with 'snapshot import'."
                        : "No export file was written."));
            }
        }

        if (!File.Exists(outFile))
            throw new CliUsageException(
                $"The game exited after {sw.Elapsed.TotalSeconds:0} seconds without writing an export file. " +
                "The usual cause is that the exporter mod is not enabled in this list: it has to be one of the " +
                $"entries in '{list.Name}'.");

        var importer = new SnapshotImporter
        {
            ModRoots = ctx.Args.Flag("harvest-translations") ? ctx.Config.ModRoots : [],
        };
        var dbPath = Path.Combine(ctx.Config.ResolveSnapshotDir(), snapshotName + ".db");
        var stats = importer.Import(outFile, dbPath);

        // 步骤 6:指纹自校 —— 请求的 ids 序列必须等于产出 meta 的 ids 序列。
        // 期望环境由 CLI 主动制造并验证过,自动检测就只剩「手动导出归属谁」一个用途了。
        var produced = stats.Meta.Mods.Select(m => m.PackageId).ToList();
        if (ExportMeta.ComputeModlistFingerprint(list.Ids) != ExportMeta.ComputeModlistFingerprint(produced))
            ctx.Report.Notice(NoticeKind.Staleness,
                $"The game reported a different mod list than the one it was given: asked for {list.Ids.Count}, " +
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
