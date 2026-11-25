using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;
using RimTooling.IO;

namespace RimSearcher.Commands;

public sealed class SnapshotListCommand : Command
{
    private static readonly CliOption AllGames = CliOption.Flag("all-games") with
    {
        Help = "同时列出较旧游戏版本的快照。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot list",
        Verb = "list",
        Summary = "列出本机已登记的快照。",
        Remarks =
            "不指定名称时，每个快照名占一行，「较早世代数」列数的是重新导出时在该名下保留的旧世代" +
            "（<名称>.prev、<名称>.prev2 等）。" +
            "每个世代都可传给 --snapshot 或 'snapshot diff'。",
        Positionals =
        [
            new CliPositional
            {
                Name = "name",
                Required = false,
                Help = "列出该快照及其旧世代。",
            },
        ],
        Options =
        [
            AllGames,
        ],
        UsesGlobals = true,
        Examples = ["rimsearcher snapshot list", "rimsearcher snapshot list baseline", "rimsearcher snapshot list --all-games"],
        JsonKeys =
        [
            new("snapshots",
                "每个快照一行：name、active、defs、mods、game、exported；不指定名称时另有 older（该名称保留的旧世代数）。" +
                "不带 --all-games 时，较旧游戏版本的快照不占行，当前快照除外。",
                Rows: true),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var entries = SnapshotCatalog.Enumerate(ctx.Config, out var dirUnreadable);
        var named = ctx.Args.Positional(0);
        if (entries.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                dirUnreadable ??
                "尚无快照。'rimsearcher export --modlist <名称>' 无人值守地运行游戏，生成一份。");
            return 1;
        }

        var active = ctx.Config.ActiveSnapshot;
        bool IsActive(string alias) => string.Equals(alias, active, StringComparison.OrdinalIgnoreCase);
        var names = entries.Select(e => e.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var read = new Dictionary<string, (string Mods, string Game, string Exported, string Defs)>(StringComparer.OrdinalIgnoreCase);
        (string Mods, string Game, string Exported, string Defs) Read(SnapshotEntry e)
        {
            if (read.TryGetValue(e.Alias, out var got)) return got;
            try
            {
                using var db = SnapshotDb.Open(e.Path);
                got = (db.Mods.Count.ToString(), db.Meta.GameVersion, db.Meta.ExportedAtUtc, db.DefCount().ToString());
            }
            catch (Exception ex) when (ex is SnapshotFormatError or SnapshotFormatException)
            {
                got = ("无法读取", "无法读取", "无法读取", "无法读取");
            }
            return read[e.Alias] = got;
        }

        List<SnapshotEntry> shown;
        Dictionary<string, int>? older = null;
        var leftOut = new List<(Version Line, string Alias)>();
        if (named is not null)
        {
            var family = SnapshotRetention.GenerationOf(named).Name;
            shown = entries
                .Select(e => (Entry: e, Gen: SnapshotRetention.GenerationOf(e.Alias)))
                .Where(x => string.Equals(x.Gen.Name, family, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Gen.Generation)
                .Select(x => x.Entry)
                .ToList();
            if (shown.Count == 0)
                throw new CliUsageException(
                    $"没有名为 '{named}' 的快照。已登记：" +
                    $"{string.Join("、", entries.Select(e => SnapshotRetention.GenerationOf(e.Alias).Name).Distinct(StringComparer.OrdinalIgnoreCase))}。");
        }
        else
        {
            older = new(StringComparer.OrdinalIgnoreCase);
            shown = [];
            foreach (var e in entries)
            {
                var (name, gen) = SnapshotRetention.GenerationOf(e.Alias);
                if (gen > 0 && names.Contains(name))
                {
                    older[name] = older.GetValueOrDefault(name) + 1;
                    if (!IsActive(e.Alias)) continue;
                }
                shown.Add(e);
            }

            if (!ctx.Args.Flag(AllGames)
                && shown.Select(e => SnapshotCatalog.GameLine(Read(e).Game)).Where(l => l is not null).Max() is { } newest)
            {
                foreach (var e in shown)
                    if (!IsActive(e.Alias) && SnapshotCatalog.GameLine(Read(e).Game) is { } line && line < newest)
                        leftOut.Add((line, e.Alias));
                var gone = leftOut.Select(x => x.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
                shown.RemoveAll(e => gone.Contains(e.Alias));
            }
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var e in shown)
        {
            var (mods, game, exported, defs) = Read(e);

            var row = new Dictionary<string, object?>
            {
                ["name"] = e.Alias,
                ["active"] = IsActive(e.Alias) ? "是" : "",
                ["defs"] = defs,
                ["mods"] = mods,
                ["game"] = game,
                ["exported"] = exported,
            };
            if (older is not null) row["older"] = older.GetValueOrDefault(e.Alias).ToString();
            rows.Add(row);
        }

        if (older is null)
        {
            ctx.Report.Table("snapshots", ["name", "active", "defs", "mods", "game", "exported"], rows);
            return 0;
        }

        ctx.Report.Table("snapshots", ["name", "active", "older", "defs", "mods", "game", "exported"], rows);
        if (leftOut.Count > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                "以下快照属较旧游戏版本，未列出：" +
                string.Join("；", leftOut.GroupBy(x => x.Line).OrderByDescending(g => g.Key)
                                         .Select(g => $"{g.Key}：{string.Join("、", g.Select(x => x.Alias))}")) +
                "。");
        return 0;
    }
}

public sealed class SnapshotStatusCommand : Command
{
    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot status",
        Verb = "status",
        Summary = "完整说明当前使用哪份快照，以及它与此刻已安装的游戏比对的结果。",
        Remarks =
            "游戏版本变化后，各 mod 的文件比对不执行，只报告版本变化。纹理与音频在比对范围之外。\n\n" +
            "「丢失路径的 def 数」数的是导出被截断的 def：超出字段上限、超出深度上限、在列表中途停止，或有值在读取时抛出异常；" +
            "这些 def 上，'get' 列出的字段路径少于该 def 实有的字段路径。「值被截断的 def 数」数的是保留全部字段路径、" +
            "只有值按长度上限截断的 def；这些 def 的字段路径齐全，'get' 的输出行里只显示值的前段。" +
            "两类 def 都见 'snapshot truncated'。",
        Options = [],
        Examples = ["rimsearcher snapshot status"],
        JsonKeys =
        [
            new("snapshot",
                "一个对象：所选快照与已安装游戏的比对，含 defs_with_paths_dropped 与 defs_with_values_cut。"),
            new("xml",
                "Defs 或 Patches XML 存在改动或未找到的本快照的每个 mod 一行：package_id、state。没有时为空。",
                Rows: true),
            new("languages",
                "Languages/ 下 Keyed 或 DefInjected 文件存在改动的本快照的每个 mod 一行：package_id、state、next。" +
                "没有时，或未记录 languages_fingerprint 时为空。",
                Rows: true),
            new("assemblies",
                "游戏加载的程序集存在改动的本快照的每个 mod 一行：package_id、state、next。" +
                "没有时，或未记录 assemblies_fingerprint 时为空。",
                Rows: true),
            new("mod_list",
                "状态为「已启用，不在快照」或「在快照，未启用」的每个 packageId 一行：package_id、state。" +
                "两侧的 mod 匹配时为空。",
                Rows: true),
            new("layers",
                "本快照可含的每个数据层一行：layer、state、next、why。state 为「齐备」，" +
                "或为已跳过、无法采集、未采集、未配置、空、未建之一；next 是补齐该层的命令（「齐备」行为 null；「无法采集」行是导出器记下的原因）。" +
                "缺层时，查询输出同样形状的 absent 行，不带 why。",
                Rows: true),
            new("export_timings",
                "仅在 --json 下、且导出记录耗时时出现：导出器在游戏内每一层一行，含 layer、seconds、share、part_of，不含游戏启动的耗时。" +
                "层名以 _emit 结尾的行是 part_of 那一层中构造与写入行的部分，计入该层，share 为 null。"),
            new("import_timings",
                "仅在 --json 下、且导入记录耗时时出现：建本快照库的每一层一行，形状相同。"),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var selection = SnapshotCatalog.Resolve(ctx.Config, ctx.Args.Value(GlobalOptions.Db), ctx.Args.Value(GlobalOptions.Snapshot));
        using var db = SnapshotDb.Open(selection.Path);
        var env = SnapshotCatalog.Compare(db, ctx.Config);

        var why = selection.Source switch
        {
            SelectionSource.ExplicitDb => "命令行传入 --db",
            SelectionSource.ExplicitAlias => "命令行传入 --snapshot",
            SelectionSource.Pinned => "由 'snapshot use' 固定",
            SelectionSource.AutoDetected => "本快照的 mod 与游戏当前启用的 mod 匹配",
            SelectionSource.OnlyOne => "为唯一已登记的快照",
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
            new("xml_fingerprint", db.Content is { } c
                ? $"{Tally.Complete(c.XmlMods).Render("mod")} 共 {Tally.Complete(c.Files).Render("file")}"
                : "未记录"),
            new("languages_fingerprint", db.Content is { } l
                ? $"{Tally.Complete(l.LangMods).Render("mod")} 共 {Tally.Complete(l.LangFiles).Render("file")}"
                : "未记录"),
            .. ExportCap.DroppedDefs(db.TruncatedDefSpread(), db.TruncatedDefCount()),
        ]);

        var layers = DataLayers.Rows(DataLayers.Ledger(db, ctx.Config, name), withWhy: true);
        ctx.Report.Table("layers", [.. new[] { "layer", "state", "next", "why" }.Where(c => c is "layer" or "state" || layers.Any(r => r[c] is not null))],
            layers, unclipped: true);

        void TimingTable(string name, IReadOnlyDictionary<string, long> t)
        {
            var total = t.TryGetValue(IntermediateFormat.TimingKeys.Total, out var tt) && tt > 0 ? tt : 0;
            static string? Parent(string key)
                => key.EndsWith("_emit", StringComparison.Ordinal) ? key[..^"_emit".Length] : null;
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
                         ["share"] = total > 0 && kv.Key != IntermediateFormat.TimingKeys.Total
                                               && Parent(kv.Key) is null
                             ? $"{100.0 * kv.Value / total:0}%" : null,
                     };
                     if (hasPart) row["part_of"] = Parent(kv.Key);
                     return (IReadOnlyDictionary<string, object?>)row;
                 }).ToList());
        }

        if (ctx.Json && db.ExportTimings is { Count: > 0 } exportTimings) TimingTable("export_timings", exportTimings);
        if (ctx.Json && db.ImportTimings is { Count: > 0 } importTimings) TimingTable("import_timings", importTimings);

        if (env.Added > 0 || env.Removed > 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("游戏当前启用的 mod 与本快照不匹配：", Tally.Complete(env.Added).Render("mod"),
                         "已启用而本快照没有，这些 mod 的内容不在本快照中；", Tally.Complete(env.Removed).Render("mod"),
                         "在本快照中而游戏已不再启用。") +
                "");
            ctx.Report.Table("mod_list", ["package_id", "state"],
                Roster(
                    env.AddedMods.Select(id => (id, "已启用，不在快照")),
                    env.RemovedMods.Select(id => (id, "在快照，未启用"))));
        }

        if (env.Reordered)
            ctx.Report.Notice(NoticeKind.Staleness,
                "本快照记录的 mod 在游戏中的加载顺序已改变。加载顺序决定哪个 patch 生效，" +
                "因此输出中的值可能与游戏解析出的值不匹配。");

        var sameList = env is { Added: 0, Removed: 0, Reordered: false };

        var languages = env.Match is EnvironmentMatch.Same or EnvironmentMatch.ContentDrift
            ? SnapshotCatalog.CompareLanguages(db, ctx.Config, env)
            : null;
        var languagesDrifted = languages is { Count: > 0 };

        switch (env.Match)
        {
            case EnvironmentMatch.Same:
                ctx.Report.Notice(NoticeKind.SnapshotChoice,
                    (sameList && !languagesDrifted
                        ? "在全部比对项上，本快照与此刻已安装的游戏匹配：mod 相同、顺序相同、游戏版本相同"
                        : sameList
                        ? "除下方的语言文件外，本快照与此刻已安装的游戏匹配：mod 相同、顺序相同、游戏版本相同"
                        : $"除{(env is { Added: 0, Removed: 0 } ? "加载顺序" : "启用的 mod")}" +
                          $"{(languagesDrifted ? "与下方的语言文件" : "")}外，" +
                          "本快照与此刻已安装的游戏匹配：游戏版本相同") +
                    (env.Content is { } ok
                        ? Cjk.Join("；自导出以来，", Tally.Complete(ok.Scanned).Render("mod"), "的 Defs 与 Patches XML，" +
                                   "以及", Tally.Complete(db.Content!.AsmMods).Render("mod"), "的程序集，" +
                                   "文件大小与时间戳均未改变。")
                        : "。"));
                ctx.Report.Notice(NoticeKind.Boundary, env.Content is null
                    ? "这些 mod 内部的文件未比对：本快照没有 XML 指纹。"
                    : "比对项为 Defs/ 与 Patches/ 下的文件、游戏加载的程序集" +
                      (languages is null ? "" : "、Languages/ 下的 Keyed 与 DefInjected 文件") +
                      "的大小与时间戳。");
                break;
            case EnvironmentMatch.VersionDrift:
                ctx.Report.Notice(NoticeKind.Staleness,
                    (sameList ? "mod 与顺序相同，但" : "") +
                    $"自导出以来游戏已更新到 {env.GameVersion}（快照：{db.Meta.GameVersion}）。");
                break;
            case EnvironmentMatch.ContentDrift:
                ctx.Report.Notice(NoticeKind.Staleness,
                    Cjk.Join(sameList ? "mod 相同、顺序相同、游戏版本相同，但" : "游戏版本相同，但",
                             "下列 mod 的文件与导出时不匹配。"));
                break;
            case EnvironmentMatch.Unknown:
                ctx.Report.Notice(NoticeKind.Boundary,
                    "无法读取游戏的 ModsConfig.xml，因此无法与当前游戏比对。");
                break;
        }

        if (env.Match is EnvironmentMatch.Same or EnvironmentMatch.ContentDrift)
            DriftTables.Print(ctx.Json, name, env.Content, languages ?? [],
                              (table, columns, rows, _) => ctx.Report.Table(table, columns, rows, unclipped: true));

        if (env.VersionSource == EnvironmentVersionSource.ModsConfig)
            ctx.Report.Notice(NoticeKind.Boundary,
                "上面的游戏版本读自 ModsConfig.xml，游戏只在保存启用的 mod 时改写该文件，因此该版本号可能落后于 Steam 更新。" +
                "在配置中设置 game_dir 可改从 Assembly-CSharp.dll 读取。", teach: true);

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
    public override bool SidesByPosition => true;

    private static readonly CliOption LimitOption = CommonOptions.Limit("字段差异");

    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot diff",
        Verb = "diff",
        Summary = "比较两份具名快照解析后的 def 与字段。",
        Remarks =
            "两份快照的游戏版本与 mod 的 XML 可以不同。\n\n" +
            "本命令没有 mod 筛选。旧世代（<名称>.prev、<名称>.prev2 等）都可在这里点名。" +
            "--limit 限制字段差异表的行数，新增的 def 表与删除的 def 表超过该数时也受其限制。\n\n" +
            "「丢失路径的 def 数」与「值被截断的 def 数」数的是两侧都有、且在任一侧导出被截断的 def；这些 def 上，未列出的差异无法排除：" +
            "丢失路径的 def 字段路径不全，值被截断的 def 值按长度上限截断。\n\n" +
            "「旧值取自」与「新值取自」各自记录该侧 'get' 的「取自」列的取值：" +
            "XML、XML=parent、XML=缺省、parent、缺省、运行时、缺省|运行时或「在 <container> 下」；" +
            "patch 写过该行时带 +patch，在 patch 运行之前读取 XML 的一侧为「未写」。" +
            "两侧都在全部 patch 运行之后读取 XML 时，两侧的 XML 都未写过的字段，其值来自代码：" +
            $"字段初始值，或 def 加载期间运行的代码。「两侧 XML 均未写的字段数」数的就是这些行。" +
            "其中，代码在每次启动时随机挑选的值，与 mod 的 C# 在两次导出之间改动过的值，无法分辨。",
        Positionals =
        [
            new CliPositional { Name = "old", Help = "较早的快照。取自 'snapshot list' 的名称。" },
            new CliPositional { Name = "new", Help = "较晚的快照。取自 'snapshot list' 的名称。" },
        ],
        Options = [LimitOption],
        Examples = ["rimsearcher snapshot diff current.prev current"],
        JsonKeys =
        [
            new("defs_added",
                "只在较晚快照中存在的每个 def 一行：def_name、def_type、declared_in。",
                Rows: true),
            new("defs_removed",
                "只在较早快照中存在的每个 def 一行：def_name、def_type、declared_in。",
                Rows: true),
            new("fields",
                "两份快照之间值有变化的每个字段一行：def_name、def_type、path、old、new、" +
                $"{OldXmlColumn}、{NewXmlColumn}、declared_in。缺少 old 或 new 表示该侧的索引中没有这个字段；" +
                "null 与空集合从不进入索引。",
                Rows: true),
            new("xml",
                $"一个对象，fields 有行且两份快照都带 xml_written 行时出现。它含 {NeitherXmlKey}：" +
                "fields 中两侧都没有 XML 写的行数，不受 --limit 影响。"),
            new("notation",
                "一个对象，仅当一份快照在 1.6 及更早、另一份在 1.7 及更晚导出时出现。" +
                $"它含 {ReprintedKey}：1.7 值按 1.6 的输出方式四舍五入后与 1.6 值相等、因而未列入 fields 的向量与矩形字段数。"),
            new("truncation",
                "一个对象，仅当两侧都有的某个 def 在任一侧导出被截断时出现：defs_with_paths_dropped 与 defs_with_values_cut。"),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var oldSel = Named(ctx, ctx.Args.Positional(0)!);
        var newSel = Named(ctx, ctx.Args.Positional(1)!);
        var limit = ctx.Limit(LimitOption);

        string oldFp, newFp;
        bool sameMods;
        UnityEraSide unitySide;
        bool oldPostPatch, newPostPatch;
        using (var oldDb = SnapshotDb.Open(oldSel.Path))
        using (var newDb = SnapshotDb.Open(newSel.Path))
        {
            oldFp = oldDb.Meta.ModlistFingerprint;
            newFp = newDb.Meta.ModlistFingerprint;
            sameMods = oldDb.PackageIds().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(newDb.PackageIds());
            unitySide = EngineNotation.Side(oldDb.Meta.GameVersion, newDb.Meta.GameVersion);
            oldPostPatch = oldDb.Meta.IndexesPostPatchXml;
            newPostPatch = newDb.Meta.IndexesPostPatchXml;
        }

        if (oldFp != newFp)
            throw new CliUsageException(
                (sameMods ? "这两份快照的 mod 相同，加载顺序不同。" : "这两份快照的 mod 不同。") +
                "须比较由同一组 mod 按同一加载顺序导出的两份快照。");

        var diff = SnapshotDiff.Compare(oldSel.Path, newSel.Path, limit.Effective, unitySide);

        if (diff.AddedTotal == 0 && diff.RemovedTotal == 0 && diff.FieldsTotal == 0)
        {
            ctx.Report.Notice(NoticeKind.Count,
                "这两份快照的 def 与字段值没有差异。");
            NoteReprinted(ctx, diff);
            NoteTruncation(ctx, diff);
            return 0;
        }

        ctx.Report.CountNotice(Tally.Of(diff.Added.Count, diff.AddedTotal), "def added");
        EmitDefs(ctx, "defs_added", diff.Added, diff.AddedTotal);
        ctx.Report.CountNotice(Tally.Of(diff.Removed.Count, diff.RemovedTotal), "def removed");
        EmitDefs(ctx, "defs_removed", diff.Removed, diff.RemovedTotal);
        ctx.Report.CountNotice(Tally.Of(diff.Fields.Count, diff.FieldsTotal), "field");
        if (diff.FieldsTotal > 0)
        {
            using var oldDb = SnapshotDb.Open(oldSel.Path);
            using var newDb = SnapshotDb.Open(newSel.Path);
            var oldXml = new XmlOriginLookup(oldDb);
            var newXml = new XmlOriginLookup(newDb);

            ctx.Report.Notice(NoticeKind.Boundary, oldPostPatch == newPostPatch
                ? Cjk.Join("「旧值取自」与「新值取自」两列所指的 XML 均在", XmlLayer(newPostPatch), "读取。")
                : Cjk.Join("「旧值取自」所指的 XML 在", XmlLayer(oldPostPatch),
                           "读取，「新值取自」所指的 XML 在", XmlLayer(newPostPatch), "读取。"));

            ctx.Report.Table("fields", ["def_name", "def_type", "path", "old", "new", OldXmlColumn, NewXmlColumn, "declared_in"],
                diff.Fields.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["def_name"] = r.DefName,
                    ["def_type"] = r.DefType,
                    ["path"] = r.Path,
                    ["old"] = r.Old,
                    ["new"] = r.New,
                    [OldXmlColumn] = oldXml.From(r.DefType, r.DefName, r.Path),
                    [NewXmlColumn] = newXml.From(r.DefType, r.DefName, r.Path),
                    ["declared_in"] = r.Mod,
                }).ToList());

            if (!ctx.Report.Absent(DataLayers.XmlWrittenRow(oldDb, oldSel.Alias!),
                                   DataLayers.XmlWrittenRow(newDb, newSel.Alias!)))
            {
                var neither = diff.AllFields.Count(r =>
                    oldXml.Resolve(r.DefType, r.DefName, r.Path) == XmlOrigin.No &&
                    newXml.Resolve(r.DefType, r.DefName, r.Path) == XmlOrigin.No);
                ctx.Report.Detail("xml", [new(NeitherXmlKey, neither)]);
            }
        }

        NoteReprinted(ctx, diff);
        NoteTruncation(ctx, diff);
        return 0;
    }

    private const string OldXmlColumn = "old_from";
    private const string NewXmlColumn = "new_from";
    private const string NeitherXmlKey = "fields_xml_writes_on_neither_side";

    private static string XmlLayer(bool postPatch) => postPatch ? "全部 patch 运行之后" : "patch 运行之前";

    private const string ReprintedKey = "fields_equal_to_two_decimals";

    private static void NoteReprinted(CommandContext ctx, SnapshotDiffResult diff)
    {
        if (diff.ReprintedOnly is not { } n) return;
        ctx.Report.Detail("notation", [new(ReprintedKey, n)]);
    }

    private static SnapshotSelection Named(CommandContext ctx, string name)
    {
        var entries = SnapshotCatalog.Enumerate(ctx.Config, out var dirUnreadable);
        var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new CliUsageException(
                $"没有名为 '{name}' 的快照。" +
                (entries.Count == 0
                    ? dirUnreadable ?? "尚未登记任何快照。"
                    : $"已登记：{string.Join("、", entries.Select(e => e.Alias))}。"));
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

    private static void NoteTruncation(CommandContext ctx, SnapshotDiffResult diff)
    {
        if (diff.TruncatedDefs == 0) return;
        ctx.Report.Detail("truncation", ExportCap.DroppedDefs(diff.TruncatedSpread, diff.TruncatedDefs));
    }
}

public sealed class SnapshotUseCommand : Command
{
    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot use",
        Verb = "use",
        Summary = "固定一份快照，此后的命令无须每次指定即可使用它。",
        Remarks = "固定的选择仍让位于单个命令上显式传入的 --snapshot 或 --db。",
        Positionals = [new CliPositional { Name = "name", Help = "取自 'snapshot list' 的名称。" }],
        Options = [],
        Examples = ["rimsearcher snapshot use modded"],
        JsonKeys = [new("pinned",
            "一个对象：现在固定的快照（snapshot），及其所指的快照库文件（path）。")],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        var entries = SnapshotCatalog.Enumerate(ctx.Config);
        var hit = entries.FirstOrDefault(e => string.Equals(e.Alias, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new CliUsageException(
                $"没有名为 '{name}' 的快照。" +
                (entries.Count == 0 ? "尚未登记任何快照。" : $"已登记：{string.Join("、", entries.Select(e => e.Alias))}。"));

        ctx.Config.SaveActiveSnapshot(hit.Alias);
        ctx.Report.Detail("pinned", [new("snapshot", hit.Alias), new("path", hit.Path)]);
        return 0;
    }
}

public sealed class SnapshotRenameCommand : Command
{
    public override bool SidesByPosition => true;

    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot rename",
        Verb = "rename",
        Summary = "移动共用快照名的文件，以改名一份快照。",
        Remarks =
            "一个快照名对应共用该名的文件：快照目录中的快照库（<名称>.db，连同其旧世代）、配置文件旁的 mod 名单（<名称>.toml），" +
            "以及导出目录中的导出文件（<名称>.rsx.jsonl.gz）。本命令移动其中存在的文件。",
        Positionals =
        [
            new CliPositional { Name = "old", Help = "当前名称。上述文件有其一即可。" },
            new CliPositional { Name = "new", Help = "这些文件要改成的名称。须未被占用。" },
        ],
        Options = [],
        Examples = ["rimsearcher snapshot rename vanilla baseline"],
        JsonKeys =
        [
            new("renamed",
                "一个对象：from、to、snapshot、modlist、export、pin。snapshot、modlist、export " +
                "各自说明该文件已移动，或在本命令查找的位置不存在。pin 说明 'snapshot use' 的固定是否随之改到新名。"),
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
                    $"文件已改名，但固定未能更新（{ex.Message}）。" +
                    $"state.toml 仍指向 {plan.From}。执行 '{CommandRegistry.ExeName} snapshot use {plan.To}' 即可完成。");
            }
        }

        ctx.Report.Detail("renamed",
        [
            new("from", plan.From),
            new("to", plan.To),
            new("snapshot", plan.Status[SnapshotSlot.Snapshot]),
            new("modlist", plan.Status[SnapshotSlot.ModList]),
            new("export", plan.Status[SnapshotSlot.Export]),
            new("pin", pinUpdated ? SnapshotRename.PinFollowed
                : plan.PinFollows ? $"未更新（仍为「{plan.From}」）"
                : SnapshotRename.PinStatus(plan)),
        ]);

        if (pinUpdated)
            ctx.Report.Notice(NoticeKind.SnapshotChoice,
                $"因 'snapshot use' 而使用 {plan.From} 的命令，现在改用 {plan.To}。");

        var movedModList = plan.Moves.Any(m =>
            PathIdentity.Same(m.Destination, ModListIo.LocalPath(ctx.Config, plan.To)));
        if (movedModList)
        {
            var dependents = FindModsFromDependents(ModListIo.LocalDirectory(ctx.Config), plan.From);
            if (dependents.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    Tally.Complete(dependents.Count).Render("mod list") +
                    Cjk.Join($"（{NameList.Render(dependents, 5)}）仍经 mods_from 引用", plan.From) +
                    $"；将这些名单的 mods_from 改为 {plan.To}。");
        }

        return 0;
    }

    private static List<string> FindModsFromDependents(string dir, string from)
    {
        var dependents = new List<string>();
        if (!Directory.Exists(dir)) return dependents;
        string[] files;
        try { files = Directory.GetFiles(dir, "*" + ModListIo.Extension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return dependents; }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.EndsWith(".prefs.toml", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".newgame.toml", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var (exists, table) = TomlDocument.TryLoad(file);
                if (!exists) continue;
                if (table.TryGetString("mods_from", out var parent) &&
                    string.Equals(parent.Trim(), from, StringComparison.OrdinalIgnoreCase))
                    dependents.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
            {
            }
        }
        return dependents;
    }
}

public sealed class SnapshotTruncatedCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("def");

    private static readonly CliOption Type = CliOption.Repeat("type") with
    {
        Aliases = ["def-type", "deftype"],
        Placeholder = "<DefType>",
        Help = "只列该类型的 def。可重复以指定多个。",
        Narrows = true,
    };

    private static readonly CliOption Def = CliOption.Text("def") with
    {
        Aliases = ["def-name", "defname"],
        Placeholder = "<defName>",
        Help = "只列这个 def。",
        Narrows = true,
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot truncated",
        Verb = "truncated",
        Summary = "列出导出被截断的 def。",
        Options =
        [
            LimitOption, CommonOptions.Scope,
            Type,
            Def,
        ],
        Examples =
        [
            "rimsearcher snapshot truncated",
            "rimsearcher snapshot truncated --type ThingDef",
            "rimsearcher snapshot truncated --def Bullet_Revolver",
        ],
        JsonKeys =
        [
            new("truncated",
                "导出被截断的每个 def 一行：def_name、def_type、past_field_cap（该 def 达到字段上限后丢弃的字段）、" +
                "values_cut（按长度上限截断的值：路径在，值只有前段）、past_depth_cap（未遍历的嵌套对象，每棵子树计一）、" +
                "lists_cut（达到元素上限而停止的列表，每个列表计一）、unreadable（在游戏中读取时抛出异常、未写入表的值）。" +
                "每个计数都是下界：导出器停止时并未数完。",
                Rows: true),
            EmptyCause.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit(LimitOption);
        var scope = ctx.Scope();
        var types = ctx.Args.Values(Type);
        var defName = ctx.Args.Value(Def);
        var (rows, total) = ctx.Db.TruncatedDefs(scope, limit.Effective, types, defName);

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.Count,
                types.Count > 0 || defName is { Length: > 0 } || !scope.IsAll
                    ? "在给定的筛选条件下，没有 def 的导出被截断。"
                    : "本快照中没有 def 的导出被截断。");
            foreach (var t in types)
                if (ctx.UnknownDefType(t) is { } unknown) ctx.Report.Notice(NoticeKind.Filter, unknown);
            var causes = new List<EmptyCause>();
            if (!scope.IsAll)
                causes.Add(new(ctx.FilterAsGiven(CommonOptions.Scope), ctx.Db.TruncatedDefs(ctx.Unscoped(), 0, types, defName).Total, ctx.Without(CommonOptions.Scope)));
            if (types.Count > 0)
                causes.Add(new(ctx.FilterAsGiven(Type), ctx.Db.TruncatedDefs(scope, 0, [], defName).Total, ctx.Without(Type)));
            if (defName is { Length: > 0 })
                causes.Add(new(ctx.FilterAsGiven(Def), ctx.Db.TruncatedDefs(scope, 0, types, null).Total, ctx.Without(Def)));
            ctx.Report.EmptyBecause(causes);
            return 1;
        }

        ctx.Report.CountNotice(Tally.Of(rows.Count, total), "def");

        ctx.AnnounceExcluded(scope, rest => ctx.Db.TruncatedDefs(rest, 0, types, defName).Total, "def");

        ctx.Report.Table("truncated", ["def_name", "def_type", "past_field_cap", "values_cut", "past_depth_cap", "lists_cut", "unreadable"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["past_field_cap"] = r.Causes.Cap,
                ["values_cut"] = r.Causes.Length,
                ["past_depth_cap"] = r.Causes.Depth,
                ["lists_cut"] = r.Causes.Items,
                ["unreadable"] = r.Causes.Error,
            }).ToList());
        return 0;
    }
}

public sealed class SnapshotImportCommand : Command
{
    private static readonly CliOption Name = CliOption.Text("name") with
    {
        Aliases = ["as", "alias"],
        Placeholder = "<名称>",
        Help = "快照所登记的名称。缺省取导出文件的文件名。",
    };

    private static readonly CliOption NoHarvestTranslations = CliOption.Flag("no-harvest-translations") with
    {
        Aliases = ["no-harvest", "skip-languages"],
        Help = "只索引游戏实际使用的译文，并在快照中记录 mod 语言文件这一层未采集。" +
               "不指定且已配置 mod_roots 时，同时扫描每个已安装 mod 的语言文件，包括快照中未启用的 mod；" +
               "收集到的行标为「文件」，从不替换游戏实际使用的值。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "snapshot import",
        Verb = "import",
        Summary = "用游戏内导出器写出的文件建一份可查询的快照库。",
        Positionals =
        [
            new CliPositional
            {
                Name = "file",
                Help = "导出文件。省略时取导出目录中最新的一份。",
                Required = false,
            },
        ],
        Options =
        [
            Name,
            NoHarvestTranslations,
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
            new("imported",
                "一个对象：写入的快照及其内容。"),
            new("truncation",
                "一个对象，仅当有 def 的导出被截断时出现：defs_with_paths_dropped 与 defs_with_values_cut。" +
                "各自的读法见 'snapshot status --help'。"),
            new("absent",
                "刚写入的快照缺层时出现：每个所缺的层一行，含 layer、state、next，与查询输出的表相同。" +
                "mod 语言文件未扫描时，该层为 disk_translations。"),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var file = ctx.Args.Positional(0);
        if (file is null)
        {
            var dir = Path.GetFullPath(ctx.Config.ResolveExportDir());
            if (!Directory.Exists(dir))
                throw new CliUsageException(
                    $"未指定导出文件，且导出目录 '{dir}' 不存在。直接指定文件，或在配置文件中设置 export_dir。");
            try
            {
                file = Directory.EnumerateFiles(dir, "*" + IntermediateFormat.FileExtension)
                                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    ?? throw new CliUsageException($"导出目录 '{dir}' 中没有 *{IntermediateFormat.FileExtension} 文件。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliUsageException($"无法读取导出目录 '{dir}'：{ex.Message}");
            }
        }

        if (!File.Exists(file) && Path.GetFileName(file) == file)
        {
            var inExports = Path.Combine(ctx.Config.ResolveExportDir(), file);
            if (File.Exists(inExports)) file = inExports;
        }

        if (!File.Exists(file))
            throw new CliUsageException($"{file} 处没有导出文件。");

        var name = ctx.Args.Value(Name) ?? StripExtensions(Path.GetFileName(file));
        var dbPath = SnapshotCatalog.DatabasePath(ctx.Config, name);

        var harvest = !ctx.Args.Flag(NoHarvestTranslations);
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
        else if (install.Kind == SnapshotInstallKind.Replaced && install.Kept > 0)
            ctx.Report.Notice(NoticeKind.NextStep, SnapshotRetention.KeptPrevious(name, install.Kept));
        if (install.Dropped.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary, SnapshotRetention.DroppedOldest(install.Dropped, keep));

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
            using var imported = SnapshotDb.Open(dbPath);
            ctx.Report.Detail("truncation", ExportCap.DroppedDefs(imported.TruncatedDefSpread(), stats.TruncatedDefs));
        }

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
