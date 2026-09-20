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
        var entries = SnapshotCatalog.Enumerate(ctx.Config, out var dirUnreadable);
        if (entries.Count == 0)
        {
            // 空列表有两种成因,而「去 export 一个」只对得上其中一种。
            ctx.Report.Notice(NoticeKind.NextStep,
                dirUnreadable ??
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
            "Ordinary queries stay quiet when the snapshot matches the game, and say one line when it does not, " +
            "naming at most three mods. This command is where the full comparison lives: every packageId whose " +
            "Defs or Patches XML moved or cannot be found, and every packageId only on one side of the mod list, " +
            "each as a row. The XML comparison is by file size and timestamp under Defs/ and Patches/, not by " +
            "contents: a re-download of identical bytes reads as a change, and an edit that keeps both is the " +
            "one case it misses. Languages/, textures and audio are outside it entirely.\n\n" +
            // 两拨的读法住这里;输出里只有两格数(Docs/25 丁1)。
            "defs_with_paths_dropped counts defs whose export stopped short — past a field cap, past the depth " +
            "cap, or partway down a list; on those, 'get' lists fewer field paths than the def has. " +
            "defs_with_values_cut counts defs that kept every field path and only had values cut to the " +
            "length cap; no field path is missing on those, the rows just show the front of their value. " +
            "A snapshot exported before those were told apart has one figure, defs_with_fields_dropped, " +
            "covering both; on those, what 'get' prints is not the whole def. 'snapshot truncated' lists " +
            "the defs. In the timing tables a layer whose name ends in '_emit' is the part of the layer " +
            "named in part_of that this tool spends on building and writing lines; the rest of that layer " +
            "is the game's own traversal, so the _emit row is counted inside its parent and has no share. " +
            "'export_timings' is the exporter's own layers inside the game; 'import_timings' is building " +
            "this database from the file it wrote. The game's startup before the exporter runs — reading " +
            "XML, resolving defs, applying patches — is in neither table: it is the gap between the two " +
            "totals and how long 'rimsearcher export' actually took.",
        Options = [],
        Examples = ["rimsearcher snapshot status"],
        JsonKeys =
        [
            new()
            {
                Key = "snapshot",
                What = "an object, not an array: the chosen snapshot compared with the installed game, including " +
                       "defs_with_paths_dropped and defs_with_values_cut (a snapshot exported before those were " +
                       "told apart has defs_with_fields_dropped instead).",
            },
            new()
            {
                Key = "xml",
                Rows = true,
                What = "one row per snapshot mod whose Defs or Patches XML moved or cannot be found: package_id, state. Empty when none.",
            },
            new()
            {
                Key = "mod_list",
                Rows = true,
                What = "one row per packageId that is enabled in the game but missing from this snapshot, or in this snapshot but no longer enabled: package_id, state. Empty when the lists match.",
            },
            new()
            {
                Key = "layers",
                Rows = true,
                What = "one row per data layer this snapshot could hold: layer, state, next, why. 'state' is 'ok' " +
                       "or one of pre-measure / skipped / unavailable / unmeasured / unconfigured / partial / empty; " +
                       "'next' is the command that fills the layer (null on 'ok' rows). Queries print the same " +
                       "row shape as 'absent' for a layer they needed and found short — without 'why'.",
            },
        ],
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

        var name = selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path);
        ctx.Report.Detail("snapshot",
        [
            new("name", name),
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
            // 被截过的 def 数分两格(没分过类的库一格);此前是表下一句两拨分开说的散文(Docs/25 丁1)。
            .. ExportCap.DroppedDefs(db.TruncatedDefSpread(), db.TruncatedDefCount()),
        ]);

        // 整张层账只在这里印:每一层在不在、不在的成因、出路。查询面碰到缺层只印它需要的那几行
        // (`absent` 表,不带 why)—— 查空比查到贵,全账不许上查询路径。
        ctx.Report.Table("layers", ["layer", "state", "next", "why"],
            DataLayers.Rows(DataLayers.Ledger(db, ctx.Config, name), withWhy: true), unclipped: true);

        // 一次导出到底慢在哪 —— 两侧各一张分层耗时表。此前两侧都不记耗时,于是「哪一层慢」
        // 只能按行数猜;实测游戏那侧两分多钟出文件、建库这侧十几分钟,猜错的正是这一刀。
        // 缺这一格就整张表不摆:补一张全零的表等于宣布「每一段都不花时间」,而真相是没量。
        void TimingTable(string name, IReadOnlyDictionary<string, long> t)
        {
            var total = t.TryGetValue(IntermediateFormat.TimingKeys.Total, out var tt) && tt > 0 ? tt : 0;
            static string? Parent(string key)
                => key.EndsWith("_emit", StringComparison.Ordinal) ? key[..^"_emit".Length] : null;
            // 子集行(_emit)紧跟在它的父层下面,part_of 一列说破它长在谁里面;此前是表下一句
            // 「A layer whose name ends in '_emit' is the part of the layer above it …」(Docs/25 丁1)。
            var ordered = t.Where(kv => kv.Key != IntermediateFormat.TimingKeys.Total && Parent(kv.Key) is null)
                           .OrderByDescending(kv => kv.Value)
                           .SelectMany(kv => new[] { kv }.Concat(
                               t.Where(e => string.Equals(Parent(e.Key), kv.Key, StringComparison.Ordinal))))
                           .Concat(total > 0
                               ? [new KeyValuePair<string, long>(IntermediateFormat.TimingKeys.Total, total)]
                               : [])
                           .ToList();
            var hasPart = ordered.Any(kv => Parent(kv.Key) is not null);
            ctx.Report.Table(name, hasPart ? ["layer", "seconds", "share", "part_of"] : ["layer", "seconds", "share"],
                ordered.Select(kv =>
                 {
                     var row = new Dictionary<string, object?>
                     {
                         ["layer"] = kv.Key,
                         ["seconds"] = (kv.Value / 1000.0).ToString("0.0"),
                         // total 那一行的份额留空:它与自己比恒是 100%,印出来只占一格。
                         // 子集行(_emit)的也留空:它长在别的行**里面**,给它一个百分比会让
                         // 这一列加起来超过 100%,而读者是按「各行互斥」读这一列的。
                         ["share"] = total > 0 && kv.Key != IntermediateFormat.TimingKeys.Total
                                               && Parent(kv.Key) is null
                             ? $"{100.0 * kv.Value / total:0}%" : null,
                     };
                     if (hasPart) row["part_of"] = Parent(kv.Key);
                     return (IReadOnlyDictionary<string, object?>)row;
                 }).ToList());
        }

        if (db.ExportTimings is { Count: > 0 } exportTimings) TimingTable("export_timings", exportTimings);
        if (db.ImportTimings is { Count: > 0 } importTimings) TimingTable("import_timings", importTimings);
        // 两张表各自盖住哪一段、哪一段谁也没盖,住 help(Remarks)—— 机制不在查询面。

        // 集合差在**这里**逐条列出 packageId,而每次查询一个字都不说(成因见
        // EnvironmentReport.AddedMods)—— 于是「为什么查询不提这件事」的答案得在这一句里,
        // 否则沉默会被当成没差异。
        // 归 Boundary 不归 Staleness:它讲的是这份数据覆盖到哪儿为止,不是它过没过期。
        if (env.Added > 0 || env.Removed > 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The game currently has a different mod list: {env.Added} enabled that this snapshot lacks, " +
                $"{env.Removed} in this snapshot that are no longer enabled, so nothing here reflects those " +
                "mods. Ordinary queries stay silent about this; the list below is the only place it is said.");
            ctx.Report.Table("mod_list", ["package_id", "state"],
                Roster(
                    env.AddedMods.Select(id => (id, "enabled_not_in_snapshot")),
                    env.RemovedMods.Select(id => (id, "in_snapshot_not_enabled"))));
        }

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
                    // 「re-download 读成变化 / 保住两者的编辑漏掉 / Languages、纹理、音频不在内」2026-09-20
                    // 搬进 Remarks:29 次实印,随后文字没人提;是机制,不是这次比对的事实。
                    : "Compared are file size and timestamp under Defs/ and Patches/.");
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
                    " — but " + ContentDrift.Sentence(env.Content!));
                ctx.Report.Table(ContentDrift.Table, ContentDrift.Columns,
                    ContentDrift.Rows(selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path),
                                      env.Content!),
                    unclipped: true);
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
                "The game version above was read from ModsConfig.xml, which the game rewrites only when the " +
                "mod list is saved, so it can lag behind a Steam update. Set 'game_dir' in the config to read " +
                "it from Assembly-CSharp.dll.", teach: true);

        return 0;
    }

    private static List<IReadOnlyDictionary<string, object?>> Roster(
        params IEnumerable<(string Id, string State)>[] parts)
        => parts.SelectMany(p => p).Select(row => (IReadOnlyDictionary<string, object?>)
            new Dictionary<string, object?>
            {
                ["package_id"] = row.Id,
                ["state"] = row.State,
            }).ToList();
}

public sealed class SnapshotDiffCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot diff",
        Summary = "Compare the resolved defs and fields of two named snapshots.",
        Remarks =
            "The two names are from 'snapshot list'. This command never opens the live snapshot, so it does not " +
            "repeat the staleness banner a query would raise against the installed game: it is asking what two " +
            "databases contain, not whether either still matches disk. --db and --snapshot do not pick a side.\n\n" +
            "The snapshots must have been exported with the same ordered mod list. A different list is refused — " +
            "that comparison belongs on 'snapshot status' and its mod_list table — because mixing list changes " +
            "with content changes would make every added def look like a data change. The game build and the XML " +
            "on disk may differ; that is the point of re-exporting the same list after a mod update.\n\n" +
            "This command has no mod filter: restricting declared_in to the mods 'snapshot status' named as " +
            "changed would drop vanilla defs those mods patched. Re-exporting a name keeps its previous generations as " +
            "'<name>.prev', '<name>.prev2' and so on, and each is nameable here. --limit caps the " +
            "fields table, and the two def tables if they grow past it. Each side still reports its total, " +
            "including zero.\n\n" +
            "A 'truncation' block counts defs whose export stopped short on both sides. " +
            "defs_with_paths_dropped: a field that looks unchanged may have been one of the ones they lost. " +
            "defs_with_values_cut: every field path is there, but both sides were cut at the same length, so " +
            "a value that looks unchanged may still differ past the cut. When either side was exported " +
            "before those were told apart there is one figure, defs_with_fields_dropped: something that " +
            "looks unchanged on those may differ past what was exported.",
        Positionals =
        [
            new PositionalSpec { Name = "old", Help = "The earlier snapshot. A name from 'snapshot list'." },
            new PositionalSpec { Name = "new", Help = "The later snapshot. A name from 'snapshot list'." },
        ],
        Options = [CommonOptions.Limit("field changes")],
        Examples = ["rimsearcher snapshot diff current.prev current"],
        JsonKeys =
        [
            new()
            {
                Key = "defs_added",
                Rows = true,
                What = "one row per def present only in the later snapshot: def_name, def_type, declared_in.",
            },
            new()
            {
                Key = "defs_removed",
                Rows = true,
                What = "one row per def present only in the earlier snapshot: def_name, def_type, declared_in.",
            },
            new()
            {
                Key = "fields",
                Rows = true,
                What = "one row per field whose value differs between the snapshots: def_name, def_type, path, old, new, declared_in. A missing old or new is a field that appeared or disappeared; null never enters the index.",
            },
            new()
            {
                Key = "truncation",
                What = "an object, present only when some def on both sides had its export stopped short: " +
                       "defs_with_paths_dropped and defs_with_values_cut, or defs_with_fields_dropped when " +
                       "either side predates telling those apart.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var oldSel = Named(ctx, ctx.Args.Positional(0)!);
        var newSel = Named(ctx, ctx.Args.Positional(1)!);
        var limit = ctx.Limit();

        string oldFp, newFp;
        using (var oldDb = SnapshotDb.Open(oldSel.Path))
        using (var newDb = SnapshotDb.Open(newSel.Path))
        {
            oldFp = oldDb.Meta.ModlistFingerprint;
            newFp = newDb.Meta.ModlistFingerprint;
        }

        if (oldFp != newFp)
            throw new CliUsageException(
                "Those snapshots were exported with different mod lists, so a def-level diff would mix list " +
                "changes with content changes. 'rimsearcher snapshot status' lists every packageId only on one " +
                "side in its mod_list table. Compare two snapshots of the same ordered list.");

        var diff = SnapshotDiff.Compare(oldSel.Path, newSel.Path, limit.Effective);

        if (diff.AddedTotal == 0 && diff.RemovedTotal == 0 && diff.FieldsTotal == 0)
        {
            ctx.Report.Notice(NoticeKind.Count,
                "No difference between those snapshots in defs or field values.");
            NoteTruncation(ctx, diff);
            return 0;
        }

        // 计数恒在,含零。空表文本层本来就不印(连空行都不留,以免看起来像截断),
        // 所以零只能写在这一句上 —— 缺席会被读成「可能有、没印出来」。
        ctx.Report.CountNotice(Tally.Of(diff.Added.Count, diff.AddedTotal), "def added");
        EmitDefs(ctx, "defs_added", diff.Added, diff.AddedTotal);
        ctx.Report.CountNotice(Tally.Of(diff.Removed.Count, diff.RemovedTotal), "def removed");
        EmitDefs(ctx, "defs_removed", diff.Removed, diff.RemovedTotal);
        ctx.Report.CountNotice(Tally.Of(diff.Fields.Count, diff.FieldsTotal), "field");
        if (diff.FieldsTotal > 0)
        {
            ctx.Report.Table("fields", ["def_name", "def_type", "path", "old", "new", "declared_in"],
                diff.Fields.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["def_name"] = r.DefName,
                    ["def_type"] = r.DefType,
                    ["path"] = r.Path,
                    ["old"] = r.Old,
                    ["new"] = r.New,
                    ["declared_in"] = r.Mod,
                }).ToList());
        }

        NoteTruncation(ctx, diff);
        return 0;
    }

    private static SnapshotSelection Named(CommandContext ctx, string name)
    {
        var entries = SnapshotCatalog.Enumerate(ctx.Config, out var dirUnreadable);
        var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new CliUsageException(
                $"No snapshot named '{name}'. " +
                (entries.Count == 0
                    ? dirUnreadable ?? "None are registered yet."
                    : $"Registered: {string.Join(", ", entries.Select(e => e.Alias))}."));
        return new SnapshotSelection(hit.Path, hit.Alias, SelectionSource.ExplicitAlias);
    }

    private static void EmitDefs(CommandContext ctx, string key, IReadOnlyList<DiffDefRow> rows, int total)
    {
        if (total == 0) return;
        ctx.Report.Table(key, ["def_name", "def_type", "declared_in"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["declared_in"] = r.Mod,
            }).ToList());
    }

    /// <summary>
    /// 两侧都被截过的 def 数,两格(两侧至少一侧没分过类时一格)。读法住 Remarks:少了路径的
    /// 那拨,看不见的是**行**;只切了值的那拨,行两边都在,两侧又都切在同一处,于是切口之后的
    /// 差异逐字同形地印成「没变」。此前是表下一句散文(Docs/25 丁1)。
    /// </summary>
    private static void NoteTruncation(CommandContext ctx, SnapshotDiffResult diff)
    {
        if (diff.TruncatedDefs == 0) return;
        ctx.Report.Detail("truncation", ExportCap.DroppedDefs(diff.TruncatedSpread, diff.TruncatedDefs));
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

public sealed class SnapshotRenameCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "snapshot rename",
        Summary = "Rename a snapshot by moving the files that share its name.",
        Remarks =
            "A snapshot name is three files that share it: the database in the snapshot directory " +
            "('{name}.db'), the mod list next to the config file ('{name}.rml'), and the export file " +
            "in the export directory ('{name}.rsx.jsonl.gz'). This command moves whichever of those " +
            // 「不完整的集合是正常状态,不是错误」是安抚:前半句已经说了缺的会被报出来,
            // 而读者要的是「缺一个会不会中断」这个行为承诺。
            "exist and says which were absent; a missing one does not stop the rename. " +
            "It never overwrites: if the new name is already used at any of the three places, the " +
            "command refuses and names the file that is in the way. Previous generations of the " +
            "database ('{name}.prev', '{name}.prev2' and so on) move with it when the database itself is " +
            "being renamed.\n\n" +
            "If 'snapshot use' has pinned the old name, the pin follows, and the output says so. " +
            "--db and --snapshot do not pick which files to rename; the two names do.\n\n" +
            "A failure after some files have moved is rolled back; if the rollback itself fails, " +
            "the output names every file that was left in the new place.",
        Positionals =
        [
            new PositionalSpec { Name = "old", Help = "The current name. Any of the three files is enough." },
            new PositionalSpec { Name = "new", Help = "The name to move those files to. Must not already be in use." },
        ],
        Options = [],
        Examples = ["rimsearcher snapshot rename vanilla baseline"],
        JsonKeys =
        [
            new()
            {
                Key = "renamed",
                What = "an object: from, to, snapshot, modlist, export, pin. snapshot / modlist / export " +
                       "each say whether that file was moved or that it was not present in the place this " +
                       "command looks. pin says whether 'snapshot use' followed.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var plan = SnapshotRename.Prepare(ctx.Config, ctx.Args.Positional(0)!, ctx.Args.Positional(1)!);
        SnapshotRename.Execute(plan.Moves);

        var pinUpdated = false;
        if (plan.PinFollows)
        {
            try
            {
                ctx.Config.SaveActiveSnapshot(plan.To);
                pinUpdated = true;
            }
            catch (Exception ex)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The files were renamed but the pin could not be updated ({ex.Message}). " +
                    $"state.toml still names '{plan.From}'. Run '{CommandRegistry.ExeName} snapshot use {plan.To}' " +
                    "to finish.");
            }
        }

        ctx.Report.Detail("renamed",
        [
            new("from", plan.From),
            new("to", plan.To),
            new("snapshot", plan.Status[SnapshotSlot.Snapshot]),
            new("modlist", plan.Status[SnapshotSlot.ModList]),
            new("export", plan.Status[SnapshotSlot.Export]),
            new("pin", pinUpdated ? "followed"
                : plan.PinFollows ? $"not updated (still '{plan.From}')"
                : SnapshotRename.PinStatus(plan)),
        ]);

        if (pinUpdated)
            ctx.Report.Notice(NoticeKind.SnapshotChoice,
                $"Commands that used '{plan.From}' because of 'snapshot use' now use '{plan.To}'.");

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
                // "name" 不在这里:snapshot import / export 的 --name 是**快照**的名字,
                // 这里问的是 def 的名字 —— 两件事撞在同一个词上,而且都不会报错。
                Aliases = ["def-name", "defname"],
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
        JsonKeys =
        [
            new()
            {
                Key = "truncated",
                Rows = true,
                What = "one row per def whose export stopped short: def_name, def_type, past_field_cap (fields " +
                       "dropped once the def hit its field cap), values_cut (values cut to the length cap — the " +
                       "path is there, the value is not whole), past_depth_cap (nested objects left unwalked, " +
                       "one per subtree), lists_cut (lists stopped at the item cap, one per list). Every count " +
                       "is a lower bound: the exporter stopped, it did not finish counting. A snapshot exported " +
                       "before the causes were told apart has one column, fields_dropped, instead of the four.",
            },
            EmptyCause.JsonKey,
        ],
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
            // 收窄之后的零结果与「整份快照都没有」不是一回事:自己给的每个筛子各算一次
            // 「单独拿掉能回来几个」,进 empty_because(Docs/25 丁1;此前是一句「Snapshot-wide
            // the figure is N」,把三个筛子压成一个数)。一行都没有 = 整份快照真的没有。
            ctx.Report.Notice(NoticeKind.Count,
                types.Count > 0 || defName is { Length: > 0 } || !scope.IsAll
                    ? "No def under the filters given lost fields at export time, so counts over their field paths are complete."
                    : "No def in this snapshot lost fields at export time, so counts over field paths are complete for it.");
            var causes = new List<EmptyCause>();
            if (!scope.IsAll)
                causes.Add(new(ctx.FilterAsGiven("scope"), ctx.Db.TruncatedDefs(ctx.Unscoped(), 0, types, defName).Total, ctx.Without("scope")));
            if (types.Count > 0)
                causes.Add(new(ctx.FilterAsGiven("type"), ctx.Db.TruncatedDefs(scope, 0, [], defName).Total, ctx.Without("type")));
            if (defName is { Length: > 0 })
                causes.Add(new(ctx.FilterAsGiven("def"), ctx.Db.TruncatedDefs(scope, 0, types, null).Total, ctx.Without("def")));
            ctx.Report.EmptyBecause(causes);
            return 0;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "def");

        // 表上方,与「计数在它数的那张表上方」同一条纪律:这句说的是「这张表全不全」。
        ctx.AnnounceExcluded(scope, rest => ctx.Db.TruncatedDefs(rest, 0, types, defName).Total, "def");

        // 四种成因各一列,列名带单元(Docs/25 丁1);没分过类的库只有那一个总数。每个数都是下界,
        // 那句住 help。
        if (rows.All(r => r.Causes is not null))
            ctx.Report.Table("truncated", ["def_name", "def_type", "past_field_cap", "values_cut", "past_depth_cap", "lists_cut"],
                rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["def_name"] = r.DefName,
                    ["def_type"] = r.DefType,
                    ["past_field_cap"] = r.Causes!.Cap,
                    ["values_cut"] = r.Causes.Length,
                    ["past_depth_cap"] = r.Causes.Depth,
                    ["lists_cut"] = r.Causes.Items,
                }).ToList());
        else
            ctx.Report.Table("truncated", ["def_name", "def_type", "fields_dropped"],
                rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["def_name"] = r.DefName,
                    ["def_type"] = r.DefType,
                    ["fields_dropped"] = r.Dropped,
                }).ToList());
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
            "which is what a crash mid-export looks like. " +
            "Re-importing a name rotates its old file to '{name}.prev' and the one before it to '{name}.prev2'; " +
            "'snapshot_keep' in the config file, or --keep here, says how many generations to keep, counting the " +
            "one being written. Whatever falls past that count is deleted, and the output says which.",
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
                       "actually had. Pass --no-harvest-translations to skip it.",
            },
            new OptionSpec
            {
                Name = "no-harvest-translations",
                Arity = Arity.Flag,
                Aliases = ["no-harvest", "skip-languages"],
                Help = "Index only the translations the game actually had, and record in the snapshot that the " +
                       "disk layer was never measured, so a later 'nothing on disk' is not read as an answer.",
            },
            SnapshotRetention.Keep,
            SnapshotRetention.ReplacePrev,
        ],
        Examples =
        [
            "rimsearcher snapshot import",
            "rimsearcher snapshot import exports/vanilla.rsx.jsonl.gz --name vanilla --no-harvest-translations",
        ],
        JsonKeys =
        [
            new() { Key = "imported", What = "an object: the snapshot that was written, and what went into it." },
            new()
            {
                Key = "truncation",
                What = "an object, present only when some def's export stopped short: defs_with_paths_dropped " +
                       "and defs_with_values_cut, or defs_with_fields_dropped when the export predates telling " +
                       "those apart. 'snapshot status --help' says how each reads.",
            },
            new()
            {
                Key = "absent",
                What = "when the snapshot just written is short on a layer: one row per such layer — layer, " +
                       "state, next — the same table the queries print. Today that is 'disk_translations' " +
                       "when the language files on disk were not scanned.",
            },
        ],
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

        // 裸文件名先去导出目录找:重导入的指路命令(`DataLayers.ImportCommand`)填的是建库时记下的
        // 文件名,不带目录 —— 读者照抄就该能跑,不必先 cd 到导出目录。
        if (!File.Exists(file) && Path.GetFileName(file) == file)
        {
            var inExports = Path.Combine(ctx.Config.ResolveExportDir(), file);
            if (File.Exists(inExports)) file = inExports;
        }

        if (!File.Exists(file))
            throw new CliUsageException($"No export file at '{file}'.");

        var name = ctx.Args.Value("name") ?? StripExtensions(Path.GetFileName(file));
        var dbPath = SnapshotCatalog.DatabasePath(ctx.Config, name);

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
            HarvestRequested = harvest,
            Environment = ctx.Config,
        };
        var incoming = SnapshotRetention.IncomingPath(dbPath);
        var stats = importer.Import(file, incoming);
        var keep = SnapshotRetention.ResolveKeep(ctx.Config, ctx.Args);
        var install = SnapshotRetention.Install(incoming, dbPath, keep);
        if (install.Kind == SnapshotInstallKind.Unchanged)
            ctx.Report.Notice(NoticeKind.Count, SnapshotRetention.Unchanged(name));
        else if (install.Kind == SnapshotInstallKind.Replaced)
            ctx.Report.Notice(NoticeKind.NextStep, SnapshotRetention.KeptPrevious(name, install.Kept));
        if (install.Dropped is { Length: > 0 } dropped)
            ctx.Report.Notice(NoticeKind.Boundary, SnapshotRetention.DroppedOldest(dropped, keep));

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
        {
            // 分拨去问刚装好的那份库,而不是在导入循环里另数一遍:两处各数各的时,
            // 判据一改就只改得动其中一处,而两个数印在一起看不出哪个陈了。
            // 与 snapshot status 同一组格(ExportCap.DroppedDefs)—— 同一件事不许两种说法。
            using var imported = SnapshotDb.Open(dbPath);
            ctx.Report.Detail("truncation", ExportCap.DroppedDefs(imported.TruncatedDefSpread(), stats.TruncatedDefs));
        }

        // 没收割要说破:刚建好的库在哪一层短了,与查询面同一张 absent 表 —— 成因(收回参数 /
        // 去配 mod_roots)在 state 与 next 里,产地 DataLayers。
        using (var built = SnapshotDb.Open(dbPath))
            ctx.Report.Absent(DataLayers.DiskTranslationsRow(built, ctx.Config, name));

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
