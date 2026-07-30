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
        JsonKeys = [new() { Key = "snapshots", What = "one row per registered snapshot: name, defs, mods, game, language, exported, pinned, path." }],
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

            // 状态不进名字格。凡是要被复制回命令行的单元格都只放能原样粘贴的东西 ——
            // 实测里有人把 `modded (active)` 整个抄进 --snapshot,吃了一句「No snapshot named」。
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
        ]);

        var truncated = db.TruncatedDefCount();
        if (truncated > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(truncated).Render("def")} in this snapshot had fields dropped at export time for " +
                "depth or size. For those, a field path missing from 'get' is not proof that the def lacks it.");

        switch (env.Match)
        {
            case EnvironmentMatch.Same:
                ctx.Report.Notice(NoticeKind.SnapshotChoice,
                    "This snapshot matches the game as installed right now: same mods, same order, same version.");
                break;
            case EnvironmentMatch.VersionDrift:
                ctx.Report.Notice(NoticeKind.Staleness,
                    $"Same mods and order, but the game has moved to {env.GameVersion} since the export " +
                    $"(snapshot: {db.Meta.GameVersion}). Re-export to refresh.");
                break;
            case EnvironmentMatch.DifferentModlist:
                ctx.Report.Notice(NoticeKind.Staleness,
                    $"The game currently has a different mod list: {env.Added} enabled that this snapshot lacks, " +
                    $"{env.Removed} in this snapshot that are no longer enabled. This is not automatically wrong — " +
                    "you may be querying another environment on purpose — but nothing here reflects those mods.");
                break;
            case EnvironmentMatch.Unknown:
                ctx.Report.Notice(NoticeKind.Boundary,
                    "The game's ModsConfig.xml could not be read, so no comparison with the live game was possible. " +
                    "Everything above describes the snapshot alone.");
                break;
        }

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
        Examples = ["rimsearcher snapshot use vanilla"],
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
            "Every count this tool reports over field paths — 'find', 'values', 'fields' — is complete only " +
            "for what got indexed. These defs are where that gap can hide, so this is how a claim of " +
            "'that is all of them' gets cross-checked rather than trusted.",
        Options = [CommonOptions.Limit("defs"), CommonOptions.Scope],
        Examples = ["rimsearcher snapshot truncated", "rimsearcher snapshot truncated --limit all"],
        JsonKeys = [new() { Key = "truncated", What = "one row per def that lost fields at export time: def_name, def_type, dropped, mod." }],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var scope = ctx.Scope();
        var (rows, total) = ctx.Db.TruncatedDefs(scope, limit.Effective);

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.Count,
                "No def in this snapshot lost fields at export time" +
                (scope.IsAll ? "" : $" within --scope {scope.Describe()}") +
                ", so counts over field paths are complete for it.");
            return 0;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "def", "raise --limit to see the rest.");
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
                Help = "Also scan the language files of every installed mod, including ones not enabled in the " +
                       "snapshot, so that a translated name still finds the def. Harvested rows are marked and " +
                       "never replace the values the game actually had.",
            },
        ],
        Examples =
        [
            "rimsearcher snapshot import",
            "rimsearcher snapshot import exports/vanilla.rsx.jsonl.gz --name vanilla --harvest-translations",
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

        var importer = new SnapshotImporter
        {
            ModRoots = ctx.Args.Flag("harvest-translations") ? ctx.Config.ModRoots : [],
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
            new("xml_nodes", stats.XmlNodes),
            new("game_version", stats.Meta.GameVersion),
            new("language", stats.Meta.Language),
            new("mods", stats.Meta.Mods.Count),
        ]);

        if (stats.TruncatedDefs > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(stats.TruncatedDefs).Render("def")} had fields dropped at export time for depth " +
                "or size. 'get' says so per def, so that a missing path is never mistaken for an absent field.");

        if (!ctx.Args.Flag("harvest-translations") && ctx.Config.ModRoots.Count > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                "Only the translations the game actually had are indexed. Pass --harvest-translations to also " +
                "index language files from installed mods, which helps when the machine has no localisation mod " +
                "enabled but you still want to search by translated name.");

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
