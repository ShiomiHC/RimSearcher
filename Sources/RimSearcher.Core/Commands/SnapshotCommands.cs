using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

public sealed class SnapshotListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot list",
        Summary = "List the snapshots this machine knows about.",
        Options = [],
        UsesGlobals = true,
        Examples = ["rimsearcher snapshot list"],
        JsonKeys = [new() { Key = "snapshots", Rows = true, What = "one row per registered snapshot: name, active, defs, mods, game, exported." }],
    };

    public override int Run(CommandContext ctx)
    {
        var entries = SnapshotCatalog.Enumerate(ctx.Config);
        if (entries.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                "No snapshots yet. A snapshot comes out of the game: run 'rimsearcher export --modlist <name>' " +
                "to drive the game unattended, or use the export button on the mod's settings page and then " +
                "'rimsearcher snapshot import <file>'.");
            return 1;
        }

        var active = ctx.Config.ActiveSnapshot;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var e in entries)
        {
            string mods, game, exported, defs;
            try
            {
                using var db = SnapshotDb.Open(e.Path);
                mods = db.Mods.Count.ToString();
                game = db.Meta.GameVersion;
                exported = db.Meta.ExportedAtUtc;
                defs = db.DefCount().ToString();
            }
            catch (Exception ex) when (ex is SnapshotFormatError or SnapshotFormatException)
            {
                mods = game = exported = defs = "unreadable";
            }

            // 状态不进名字格:要被复制回命令行的单元格只放能原样粘贴的东西。
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = e.Alias,
                ["active"] = string.Equals(e.Alias, active, StringComparison.OrdinalIgnoreCase) ? "yes" : "",
                ["defs"] = defs,
                ["mods"] = mods,
                ["game"] = game,
                ["exported"] = exported,
            });
        }

        ctx.Report.Table("snapshots", ["name", "active", "defs", "mods", "game", "exported"], rows);
        return 0;
    }
}

public sealed class SnapshotStatusCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot status",
        Summary = "Explain in full which snapshot is in use and how it compares with the game as installed right now.",
        Remarks =
            "Ordinary queries stay quiet when the snapshot matches the game, and say one line when it does not. " +
            "This command is where the full comparison lives, so that the detail never has to ride along with " +
            "every result.",
        Options = [],
        Examples = ["rimsearcher snapshot status"],
        JsonKeys = [new() { Key = "snapshot", What = "an object, not an array: the chosen snapshot compared with the installed game." }],
    };

    public override int Run(CommandContext ctx)
    {
        var selection = SnapshotCatalog.Resolve(ctx.Config, ctx.Args.Value("db"), ctx.Args.Value("snapshot"));
        using var db = SnapshotDb.Open(selection.Path);
        var env = SnapshotCatalog.Compare(db, ctx.Config);

        var why = selection.Source switch
        {
            SelectionSource.ExplicitDb => "you passed --db",
            SelectionSource.ExplicitAlias => "you passed --snapshot",
            SelectionSource.Pinned => "it is pinned by 'snapshot use'",
            SelectionSource.AutoDetected => "its mod list matches the game's currently enabled mods",
            SelectionSource.OnlyOne => "it is the only snapshot registered",
            _ => "",
        };

        ctx.Report.Detail("snapshot",
        [
            new("name", selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path)),
            new("chosen_because", why),
            new("path", selection.Path),
            new("game_version", db.Meta.GameVersion),
            new("language", db.Meta.Language),
            new("exported_at_utc", db.Meta.ExportedAtUtc),
            new("exporter_version", db.Meta.ExporterVersion),
            new("defs", db.DefCount()),
            new("mods", db.Mods.Count),
            new("fingerprint", db.Meta.Fingerprint),
            // 「量过没量过」要有一格看得见的位置 —— 缺席时那条判据整个不说话,
            // 而沉默与「比过了,没变」在输出里同形。
            new("xml_fingerprint", db.Content is { } c
                ? $"{Tally.Complete(c.Files).Render("file")} across {Tally.Complete(c.Mods.Count).Render("mod")}"
                : "not recorded (exported before this was measured)"),
        ]);

        var truncated = db.TruncatedDefCount();
        if (truncated > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{ExportCap.OverDefs(truncated, " in this snapshot")}. " +
                "For those, a field path missing from 'get' is not proof that the def lacks it.");

        // 集合差在**这里**逐条讲,而每次查询一个字都不说(成因见 EnvironmentReport.Added)——
        // 于是「为什么查询不提这件事」的答案得在这一句里,否则沉默会被当成没差异。
        // 归 Boundary 不归 Staleness:它讲的是这份数据覆盖到哪儿为止,不是它过没过期。
        if (env.Added > 0 || env.Removed > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The game currently has a different mod list: {env.Added} enabled that this snapshot lacks, " +
                $"{env.Removed} in this snapshot that are no longer enabled. This is not automatically wrong — " +
                "you may be querying another environment on purpose — but nothing here reflects those mods. " +
                "Ordinary queries stay silent about this; they report only the differences below.");

        // 次序是另一回事:它不是「另一个环境」,而是同一批 mod 的另一种解析结果。
        if (env.Reordered)
            ctx.Report.Notice(NoticeKind.Staleness,
                "The mods this snapshot describes are in a different load order in the game now. Load order " +
                "decides which patch wins, so a value here can differ from what the game resolves. Re-export " +
                "to settle it.");

        // 下面三支只讲版本与文件那两层,mod 列表已由上面两句收口 —— 于是「same mods」这类
        // 断言只在列表真的一字不差时才出得来。
        var sameList = env is { Added: 0, Removed: 0, Reordered: false };

        switch (env.Match)
        {
            case EnvironmentMatch.Same:
                ctx.Report.Notice(NoticeKind.SnapshotChoice,
                    (sameList
                        ? "This snapshot matches the game as installed right now on everything that is compared: " +
                          "same mods, same order, same game build"
                        : "Apart from the mod list, this snapshot matches the game as installed right now: " +
                          "same game build") +
                    (env.Content is { } ok
                        ? $", and the Defs and Patches XML of {Tally.Complete(ok.Scanned).Render("mod")} is " +
                          "unchanged in file size and timestamp since the export."
                        : "."));
                // 「matches」是对**比过的那几项**的背书,不是对整份数据的。没比的那半要印出来,
                // 否则它会被读成「快照 = 当前游戏数据」。
                ctx.Report.Notice(NoticeKind.Boundary, env.Content is null
                    ? "The files inside those mods are not compared: this snapshot has no XML fingerprint, so a " +
                      $"mod edited since the export ({db.Meta.ExportedAtUtc} UTC) leaves this line reading " +
                      "'matches' all the same. Re-export to start recording that layer."
                    : "Compared are file size and timestamp under Defs/ and Patches/, not file contents — a " +
                      "re-download of identical bytes reads as a change, and an edit that keeps both is the one " +
                      "case this misses. Languages/, textures and audio are outside it entirely.");
                break;
            case EnvironmentMatch.VersionDrift:
                ctx.Report.Notice(NoticeKind.Staleness,
                    (sameList ? "Same mods and order, but t" : "T") +
                    $"he game has moved to {env.GameVersion} since the export " +
                    $"(snapshot: {db.Meta.GameVersion}). Re-export to refresh.");
                break;
            case EnvironmentMatch.ContentDrift:
                ctx.Report.Notice(NoticeKind.Staleness,
                    (sameList ? "Same mods, same order, same game build" : "Same game build") +
                    " — but the files those mods are made of have moved. " +
                    ContentDrift.Sentence(
                        selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path), env.Content!));
                break;
            case EnvironmentMatch.Unknown:
                ctx.Report.Notice(NoticeKind.Boundary,
                    "The game's ModsConfig.xml could not be read, so no comparison with the live game was possible. " +
                    "Everything above describes the snapshot alone.");
                break;
        }

        // 版本这一句是从哪来的,决定了它值多少。ModsConfig.xml 那个数是游戏上次保存 mod
        // 列表时写下的,不是安装事实 —— 说破它,免得「same version」被当成 Steam 没动过。
        if (env.VersionSource == GameVersionSource.ModsConfig)
            ctx.Report.Notice(NoticeKind.Boundary,
                "The game version above came from ModsConfig.xml, which the game only rewrites when you save a " +
                "change on its mod list page. A Steam update within the same 1.x line does not touch it, so that " +
                "number can lag behind what is installed. Set 'game_dir' in the config and it is read from " +
                "Assembly-CSharp.dll instead, which is the installed fact.");

        return 0;
    }
}

public sealed class SnapshotUseCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot use",
        Summary = "Pin a snapshot so later commands use it without being told each time.",
        Remarks = "A pinned choice still loses to an explicit --snapshot or --db on a single command.",
        Positionals = [new PositionalSpec { Name = "name", Help = "A name from 'snapshot list'." }],
        Options = [],
        // 例子刻意不叫 vanilla:find / list 的 --help 逐字警告过「--scope vanilla 与一个
        // 恰好叫 vanilla 的快照不是一回事」,而这里拿它当命名示范就是在教人制造那次撞名。
        Examples = ["rimsearcher snapshot use modded"],
        JsonKeys = [new() { Key = "pinned", What = "an object: which snapshot is now pinned, and where the choice was written." }],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        var entries = SnapshotCatalog.Enumerate(ctx.Config);
        var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new CliUsageException(
                $"No snapshot named '{name}'. " +
                (entries.Count == 0 ? "None are registered yet." : $"Registered: {string.Join(", ", entries.Select(e => e.Alias))}."));

        ctx.Config.SaveActiveSnapshot(hit.Alias);
        ctx.Report.Detail("pinned", [new("snapshot", hit.Alias), new("path", hit.Path)]);
        return 0;
    }
}

public sealed class SnapshotTruncatedCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot truncated",
        Summary = "List the defs whose fields the exporter stopped short on.",
        Remarks =
            "Every count this tool reports over field paths — 'where', 'values', 'fields' — is complete only " +
            "for what got indexed. These defs are where that gap can hide, so this is how a claim of " +
            "'that is all of them' gets cross-checked rather than trusted.",
        Options =
        [
            CommonOptions.Limit("defs"), CommonOptions.Scope,
            new OptionSpec
            {
                Name = "type",
                Aliases = ["def-type", "deftype", "kind"],
                Placeholder = "<DefType>",
                Arity = Arity.Multi,
                Help = "Only defs of this type. Repeat it for several — the completeness footnotes elsewhere " +
                       "name the types they mean, and this is the switch that carries them over.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "def",
                Aliases = ["def-name", "defname", "name"],
                Placeholder = "<defName>",
                Help = "Only this def. Answers 'was this particular def cut short' without reading the whole list.",
                Narrows = true,
            },
        ],
        Examples =
        [
            "rimsearcher snapshot truncated",
            "rimsearcher snapshot truncated --type ThingDef",
            "rimsearcher snapshot truncated --def Bullet_Revolver",
        ],
        JsonKeys = [new() { Key = "truncated", Rows = true, What = "one row per def that lost fields at export time: def_name, def_type, fields_dropped. The count is a lower bound — the exporter stopped, it did not finish counting." }],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var types = ctx.Args.Values("type");
        var defName = ctx.Args.Value("def");
        var (rows, total) = ctx.Db.TruncatedDefs(scope, limit.Effective, types, defName);

        if (rows.Count == 0)
        {
            // 收窄之后的零结果与「整份快照都没有」不是一回事:收窄了就把条件念回去,
            // 并把全库那个数一起给出。
            var narrowed = types.Count > 0 || defName is { Length: > 0 };
            var parts = new List<string>();
            if (types.Count > 0) parts.Add("--type " + string.Join(" --type ", types));
            if (defName is { Length: > 0 }) parts.Add($"--def {defName}");
            if (!scope.IsAll) parts.Add($"--scope {scope.Expression}");

            ctx.Report.Notice(NoticeKind.Count,
                narrowed
                    ? $"No def matching {string.Join(" ", parts)} lost fields at export time, so counts over " +
                      $"field paths are complete for that much. Snapshot-wide the figure is " +
                      $"{Tally.Complete(ctx.Db.TruncatedDefCount()).Render("def")}."
                    : "No def in this snapshot lost fields at export time" +
                      (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                      ", so counts over field paths are complete for it.");
            // 零结果这一支最该说:上面刚担保「计数是完整的」,而排除掉的那半边有截断的话,
            // 那句担保只对你留下的那半边成立。
            ctx.AnnounceExcluded(scope, rest => ctx.Db.TruncatedDefs(rest, 0, types, defName).Total, "def");
            return 0;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "def");

        // 表上方,与「计数在它数的那张表上方」同一条纪律:这句说的是「这张表全不全」。
        ctx.AnnounceExcluded(scope, rest => ctx.Db.TruncatedDefs(rest, 0, types, defName).Total, "def");

        ctx.Report.Table("truncated", ["def_name", "def_type", "fields_dropped"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["fields_dropped"] = r.Dropped,
            }).ToList());
        ctx.Report.Notice(NoticeKind.Boundary,
            "The count is a lower bound per def: the exporter stopped, it did not finish counting.");
        return 0;
    }
}

public sealed class SnapshotImportCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot import",
        Summary = "Build a queryable snapshot database out of a file the in-game exporter wrote.",
        Remarks =
            "The export file is refused rather than half-imported if it lacks the end marker the game writes last, " +
            "which is what a crash mid-export looks like. Everything about how the data is filtered and indexed is " +
            "decided here rather than in the game, so a change of policy only costs a re-import, not another play session.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "file",
                Help = "The export file. Omit it to take the newest one from the configured export directory.",
                Required = false,
            },
        ],
        Options =
        [
            new OptionSpec
            {
                Name = "name",
                Aliases = ["as", "alias"],
                Placeholder = "<name>",
                Help = "Name to register the snapshot under. Defaults to the export file's name.",
            },
            new OptionSpec
            {
                Name = "harvest-translations",
                Arity = Arity.Flag,
                Aliases = ["harvest", "scan-languages"],
                Help = "On by default whenever 'mod_roots' is configured: also scan the language files of every " +
                       "installed mod, including ones not enabled in the snapshot, so that a translated name still " +
                       "finds the def. Harvested rows are marked 'on disk' and never replace the values the game " +
                       "actually had. Pass it explicitly only to be sure; pass --no-harvest-translations to skip it.",
            },
            new OptionSpec
            {
                Name = "no-harvest-translations",
                Arity = Arity.Flag,
                Aliases = ["no-harvest", "skip-languages"],
                Help = "Index only the translations the game actually had, and record in the snapshot that the " +
                       "disk layer was never measured, so a later 'nothing on disk' is not read as an answer.",
            },
        ],
        Examples =
        [
            "rimsearcher snapshot import",
            "rimsearcher snapshot import exports/vanilla.rsx.jsonl.gz --name vanilla --no-harvest-translations",
        ],
        JsonKeys = [new() { Key = "imported", What = "an object: the snapshot that was written, and what went into it." }],
    };

    public override int Run(CommandContext ctx)
    {
        var file = ctx.Args.Positional(0);
        if (file is null)
        {
            var dir = ctx.Config.ExportDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                throw new CliUsageException(
                    "No export file was given and no export directory is configured, so there is nothing to import. " +
                    "Pass the file explicitly, or set 'export_dir' in the config file.");
            file = Directory.EnumerateFiles(dir, "*" + IntermediateFormat.FileExtension)
                            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                ?? throw new CliUsageException($"No '*{IntermediateFormat.FileExtension}' file in the configured export directory.");
        }

        if (!File.Exists(file))
            throw new CliUsageException($"No export file at '{file}'.");

        var name = ctx.Args.Value("name") ?? StripExtensions(Path.GetFileName(file));
        var dbPath = Path.Combine(ctx.Config.ResolveSnapshotDir(), name + ".db");

        // 收割默认开:代价约 2% 导入耗时,换来只在磁盘上存在的 key 不被答成「没有」。
        // 没收割的库对「磁盘上有没有」没有资格回答,所以扫了几个根目录要记进 meta。
        if (ctx.Args.Flag("harvest-translations") && ctx.Args.Flag("no-harvest-translations"))
            throw new CliUsageException(
                "--harvest-translations and --no-harvest-translations ask for opposite things. Picking one " +
                "silently would make the resulting snapshot's disk layer mean whichever this code happened to " +
                "prefer, and nothing in the output would say which.");

        var harvest = !ctx.Args.Flag("no-harvest-translations");
        var importer = new SnapshotImporter
        {
            ModRoots = harvest ? ctx.Config.ModRoots : [],
            Environment = ctx.Config,
        };
        var stats = importer.Import(file, dbPath);

        ctx.Report.Detail("imported",
        [
            new("snapshot", name),
            new("path", dbPath),
            new("defs", stats.Defs),
            new("field_values", stats.FieldValues),
            new("noise_fields_dropped", stats.NoiseDropped),
            new("translations_in_effect", stats.RuntimeTranslations),
            new("translations_from_files", stats.HarvestedTranslations),
            new("ui_text_in_effect", stats.KeyedInEffect),
            new("ui_text_from_files", stats.KeyedHarvested),
            new("xml_nodes", stats.XmlNodes),
            new("game_version", stats.Meta.GameVersion),
            new("language", stats.Meta.Language),
            new("mods", stats.Meta.Mods.Count),
        ]);

        if (stats.TruncatedDefs > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{ExportCap.OverDefs(stats.TruncatedDefs)}. " +
                "'get' says so per def, so that a missing path is never mistaken for an absent field.");

        // 没收割要说破,两个成因分开说 —— 补救不一样(收回参数 / 去配 mod_roots)。
        if (!harvest)
            ctx.Report.Notice(NoticeKind.Boundary,
                "--no-harvest-translations: only the translations the game actually had are indexed, so this " +
                "snapshot cannot answer whether a string exists in some installed mod's language files — it never " +
                "looked. Re-import without the flag to measure that layer.");
        else if (ctx.Config.ModRoots.Count == 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "No 'mod_roots' is configured, so there was nowhere to scan for language files and only the " +
                "translations the game actually had are indexed. That is a gap in this snapshot, not an answer " +
                "about the mods on this machine: set 'mod_roots' in the config file and import again.");

        return 0;
    }

    private static string StripExtensions(string fileName)
    {
        foreach (var suffix in new[] { IntermediateFormat.FileExtension, ".jsonl.gz", ".gz", ".jsonl" })
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName[..^suffix.Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
