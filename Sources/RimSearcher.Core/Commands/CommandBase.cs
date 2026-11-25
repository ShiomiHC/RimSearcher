using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public static class GlobalOptions
{
    public static readonly CliOption Snapshot = CliOption.Text("snapshot") with
    {
        Aliases = ["snap", "env"],
        Placeholder = "<名称>",
        Help = "查询指定的快照，不用当前快照。",
    };

    public static readonly CliOption Db = CliOption.Text("db") with
    {
        Aliases = ["database", "snapshot-path"],
        Placeholder = "<路径>",
        Help = "直接查询该路径上的快照库，不经登记表。",
    };

    public static readonly CliOption Json = CliOption.Flag("json") with
    {
        Help = "输出机器可读的 JSON。文本输出里以句子表达的内容移入 'notes' 数组。本命令自己的表键恒在：" +
               "无匹配时为空数组。统计本命令自己的表有几行的提示另带数值键 'shown' 与 'total'；" +
               "只知下界时 'total' 为 null，其他提示（包括句中引用别的数的提示）不带这两个键。",
    };

    public static readonly CliOption Quiet = CliOption.Flag("quiet") with
    {
        Help = "stdout 只输出数据块，说明无结果成因的表属数据块。" +
               "提示（含脚注与快照标签）不输出到 stdout（--json 形式仍以 'snapshot' 键保留标签），仍整份写入运行日志。",
    };

    public static readonly CliOption Config = CliOption.Text("config") with
    {
        Placeholder = "<路径>",
        Help = "使用这份配置文件，不用缺省的那份。",
    };

    public static readonly IReadOnlyList<CliOption> All = [Snapshot, Db, Json, Quiet, Config];
}

public static class CommonOptions
{
    public static CliOption Limit(string what) => CliOption.Text("limit") with
    {
        Short = 'n',
        Aliases = ["count", "head"],
        Placeholder = "<n>",
        Help = Cjk.Join("返回的", what, "数上限。"),
        Default = "全部",
    };

    public static CliOption Offset(string what) => CliOption.Text("offset") with
    {
        Placeholder = "<n>",
        Help = Cjk.Join("列出前先跳过的", what, "数。"),
        Default = "0",
    };

    public static readonly CliOption Scope = CliOption.Text("scope") with
    {
        Aliases = ["mod", "mods"],
        Placeholder = "<表达式>",
        Help = "只看快照里的部分 mod。逗号分隔，前缀 '-' 表示排除。各项为 'all'、'vanilla'、packageId 或配置文件里的组名；" +
               "'all,-vanilla' 表示原版以外的全部。'vanilla'（也写 'core'、'base'、'official'）指 Ludeon 发行的每个模块，" +
               "即快照里的 Core 与各 DLC。",
        Default = ScopeFilter.DefaultScope,
        Narrows = true,
    };

    public static CliOption ExactPath() => CliOption.Flag("exact-path") with
    {
        Help = "把作为参数给出的字段路径按整条匹配。",
        Narrows = true,
    };

    public static CliOption PathContainsBeside() => CliOption.Repeat("path-contains") with
    {
        Aliases = ["filter", "grep"],
        BarMeansOr = true,
        Placeholder = "<文本>",
        Help = "只匹配含这段文本的字段路径；给出路径参数时须同时匹配该路径。重复该选项或写成 'A|B' 可放宽范围。" + AnyIndexNote,
        Narrows = true,
    };

    public static CliOption ExactPathFilter(bool single = false) =>
        (single ? CliOption.Text("exact-path") : CliOption.Repeat("exact-path")) with
    {
        Placeholder = "<路径>",
        Help = "按整条匹配所给的字段路径，取值须为完整的字段路径。" + AnyIndexNote,
        Narrows = true,
    };

    public const string AnyIndexNote =
        "'[]' 表示任意下标：'comps[].props.energyMax' 匹配每一条 comps[N].props.energyMax。";

    public static readonly CliOption Type = CliOption.Text("type") with
    {
        Aliases = ["def-type"],
        Placeholder = "<DefType>",
        Help = "只看一个 def 类型，如 ThingDef 或 HediffDef。",
        Narrows = true,
    };
}

public sealed class CommandContext(RimConfig config, CommandArgs args)
{
    private SnapshotDb? _db;
    private bool _snapshotNoticed;
    private bool _scopeNoticed;

    public RimConfig Config { get; } = config;
    public CommandArgs Args { get; } = args;
    public Report Report { get; } = new() { Narrowing = args.Narrowing(), TeachViaHook = RunLog.Enabled };
    public bool Json => Args.Flag(GlobalOptions.Json);
    public bool Quiet => Args.Flag(GlobalOptions.Quiet);

    public string Without(params CliOption[] options)
    {
        var parts = new List<string> { CommandRegistry.ExeName, Args.Operation.Name };
        parts.AddRange(Args.Positionals.Select(QuoteArg));
        var slots = Args.Operation.Positionals.Select(p => p.Option).OfType<CliOption>().ToHashSet();
        foreach (var o in Args.Operation.Options)
        {
            if (options.Contains(o) || o.Name is "limit" or "offset" || slots.Contains(o)) continue;
            if (Args.Values(o).Count > 0) parts.Add(FilterAsGiven(o));
        }
        if (!options.Contains(GlobalOptions.Snapshot) &&
            Args.Value(GlobalOptions.Snapshot) is { Length: > 0 } snap)
            parts.Add($"--snapshot {QuoteArg(snap)}");
        return string.Join(" ", parts);
    }

    public string? UnknownDefType(string? type) =>
        type is { Length: > 0 } &&
        !Db.Types(Unscoped()).Any(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))
            ? $"本快照中没有名为 '{type}' 的 def 类型。"
            : null;

    public string FilterAsGiven(CliOption option)
    {
        if (option.Kind == OptionKind.Flag)
            return option.FlagName;
        var given = Args.Values(option);
        return given.Count == 0
            ? option.FlagName
            : string.Join(" ", given.Select(v => $"{option.FlagName} {QuoteArg(v)}"));
    }

    internal static string QuoteArg(string v)
        => v.IndexOfAny(Expanding) >= 0 && !v.Contains('\'') ? $"'{v}'"
         : v.Length == 0 || v.IndexOfAny(ShellMeta) >= 0 || v.Any(char.IsWhiteSpace) ? $"\"{v}\""
         : v;

    private static readonly char[] Expanding = ['$', '`', '"'];
    private static readonly char[] ShellMeta = [.. "<>|&;`$(){}\"'"];

    public string? SnapshotName { get; private set; }

    public TextWriter Progress { get; init; } = TextWriter.Null;

    public bool ReadsTranslations { get; init; }

    public SnapshotDb Db
    {
        get
        {
            if (_db is not null) return _db;
            var selection = SnapshotCatalog.Resolve(Config, Args.Value(GlobalOptions.Db), Args.Value(GlobalOptions.Snapshot));
            _db = SnapshotDb.Open(selection.Path);
            AnnounceSnapshot(selection, _db);
            return _db;
        }
    }

    public LimitValue Limit(CliOption option) => Args.Limit(option);

    public CliOption ScopeOption => Args.Own(CommonOptions.Scope.Name) ?? CommonOptions.Scope;

    public ScopeFilter Scope()
    {
        var filter = ScopeFilter.Parse(Args.Value(ScopeOption), Db.PackageIds(), Config);
        if (filter.UnknownTokens.Count > 0)
            throw new CliUsageException(
                $"--scope 不接受 {string.Join("、", filter.UnknownTokens.Select(t => $"'{t}'"))}。" +
                $"本快照包含：{NameList.Render(Db.PackageIds(), 8)}" +
                "。全部 mod 见 'rimsearcher mods'。");
        AnnounceScope(filter);
        return filter;
    }

    private void AnnounceScope(ScopeFilter filter)
    {
        if (_scopeNoticed || filter.IsAll) return;
        var described = filter.Describe();
        if (string.Equals(described, filter.Expression, StringComparison.Ordinal)) return;
        _scopeNoticed = true;
        Report.Notice(NoticeKind.Filter, $"--scope {described}。");
    }

    public ScopeFilter Unscoped() => ScopeFilter.Parse("all", Db.PackageIds(), Config);

    public void AnnounceExcluded(ScopeFilter scope, Func<ScopeFilter, int> count, string noun,
                                 string qualifier = "")
    {
        var rest = scope.Complement();
        if (rest is null) return;
        var n = count(rest);
        if (n == 0) return;
        Report.Notice(NoticeKind.Boundary,
            Cjk.Join($"--scope {scope.Expression} 排除的部分里也有本次查询的结果：改用 --scope {rest.Describe()}",
                     "可找到", Tally.Complete(n).Render(noun, scope: qualifier), "。"));
    }

    private void AnnounceSnapshot(SnapshotSelection selection, SnapshotDb db)
    {
        if (_snapshotNoticed) return;
        _snapshotNoticed = true;

        var report = SnapshotCatalog.Compare(db, Config);
        var name = selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path);
        SnapshotName = name;

        if (selection.Source is not (SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb))
        {
            if (SnapshotCatalog.Enumerate(Config).Count > 1)
                Report.SnapshotTag =
                    selection.Source == SelectionSource.Pinned ? name : $"{name} 自动选中";
        }

        if (ScopeFilter.IsGroupName(name, Config))
        {
            var ids = Db.PackageIds();
            Report.Notice(NoticeKind.Boundary,
                $"'{name}' 既是本快照的名字，也是 --scope 的组名。" +
                $"本快照包含 {Tally.Complete(ids.Count).Render("mod")}：{NameList.Render(ids, 6)}。");
        }

        if (report.Reordered)
            Report.Notice(NoticeKind.Staleness,
                $"快照 '{name}' 记录的 mod 在游戏中的加载顺序已改变。加载顺序决定哪个 patch 生效，" +
                "因此输出中的值可能与游戏实际解析出的值不匹配。");

        IReadOnlyList<string> languages = [];
        if (ReadsTranslations && report.Match is EnvironmentMatch.Same or EnvironmentMatch.ContentDrift &&
            SnapshotCatalog.CompareLanguages(db, Config, report) is { Count: > 0 } changed)
            languages = changed;
        if (report.Match is EnvironmentMatch.Same or EnvironmentMatch.ContentDrift)
            DriftTables.Print(Json, name, report.Content, languages, Report.DeferredTable);

        switch (report.Match)
        {
            case EnvironmentMatch.Same:
                return;

            case EnvironmentMatch.VersionDrift:
                Report.Notice(NoticeKind.Staleness,
                    $"快照 '{name}' 由游戏版本 {db.Meta.GameVersion} 导出，已安装的游戏现为 {report.GameVersion}。");
                return;

            case EnvironmentMatch.ContentDrift:
                return;

            case EnvironmentMatch.Unknown:
                return;
        }
    }

    public void Dispose() => _db?.Dispose();
}

public static class DriftTables
{
    public delegate Report Printer(string name, IReadOnlyList<string> columns,
                                   IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
                                   IReadOnlyList<string> aboutMods);

    public static void Print(bool json, string snapshot, ContentComparison? content, IReadOnlyList<string> languages,
                             Printer print)
    {
        IReadOnlyList<string> changed = content?.Changed ?? [], missing = content?.Missing ?? [], dlls = content?.Assemblies ?? [];
        if (json)
        {
            if (languages.Count > 0)
                print(LanguageDrift.Table, ContentDrift.Columns, LanguageDrift.Rows(snapshot, languages), languages);
            if (changed.Count + missing.Count > 0)
                print(ContentDrift.Table, ContentDrift.Columns, ContentDrift.Rows(snapshot, content!), [.. changed, .. missing]);
            if (dlls.Count > 0)
                print(AssemblyDrift.Table, ContentDrift.Columns, AssemblyDrift.Rows(snapshot, dlls), dlls);
            return;
        }

        var ids = changed.Concat(dlls).Concat(languages).Concat(missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return;
        bool In(IReadOnlyList<string> list, string id) => list.Contains(id, StringComparer.OrdinalIgnoreCase);
        print(ContentDrift.Table, ["package_id", "state"],
            [.. ids.Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["package_id"] = id,
                ["state"] = In(missing, id) ? ContentDrift.Missing
                    : Cjk.Join(string.Join("、", new[] { (changed, "XML"), (dlls, "程序集"), (languages, "语言文件") }
                          .Where(k => In(k.Item1, id)).Select(k => k.Item2)), "自导出后" + ContentDrift.Changed),
            })], ids);
    }
}

public static class ContentDrift
{
    public const string Table = "xml";
    public static readonly string[] Columns = ["package_id", "state", "next"];

    public const string Changed = "存在改动";
    public const string Missing = "未找到";

    public static List<IReadOnlyDictionary<string, object?>> Rows(string snapshot, ContentComparison content)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var id in content.Changed)
            rows.Add(new Dictionary<string, object?>
            {
                ["package_id"] = id,
                ["state"] = Changed,
                ["next"] = Snapshot.DataLayers.ExportCommand(snapshot),
            });
        foreach (var id in content.Missing)
            rows.Add(new Dictionary<string, object?>
            {
                ["package_id"] = id,
                ["state"] = Missing,
                ["next"] = null,
            });
        return rows;
    }
}

public static class LanguageDrift
{
    public const string Table = "languages";

    public static List<IReadOnlyDictionary<string, object?>> Rows(string snapshot, IReadOnlyList<string> changed)
        => changed.Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["package_id"] = id,
            ["state"] = ContentDrift.Changed,
            ["next"] = Snapshot.DataLayers.ExportCommand(snapshot),
        }).ToList();
}

public static class AssemblyDrift
{
    public const string Table = "assemblies";

    public static List<IReadOnlyDictionary<string, object?>> Rows(string snapshot, IReadOnlyList<string> changed)
        => changed.Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["package_id"] = id,
            ["state"] = ContentDrift.Changed,
            ["next"] = Snapshot.DataLayers.ExportCommand(snapshot),
        }).ToList();
}

public static class ExportCap
{
    public static string OnDef(TruncationCauses by)
        => string.Join("；", by.Ranked().Select(p => Phrase(p.Cause, p.Count)));

    private static string Phrase(TruncationCause cause, int n)
    {
        return cause switch
        {
            TruncationCause.Cap => $"{Tally.Complete(n).Render("field")}超出该 def 的字段上限而丢弃",
            TruncationCause.Length => $"{Tally.Complete(n).Render("value")}按长度上限截断",
            TruncationCause.Depth =>
                $"{Tally.Complete(n).Render("nested object")}超出深度上限而未展开",
            TruncationCause.Items => $"{Tally.Complete(n).Render("list")}在条数上限处截断",
            TruncationCause.Error => $"{Tally.Complete(n).Render("field")}在游戏里读取时抛出异常而略去",
            _ => throw new ArgumentOutOfRangeException(nameof(cause)),
        };
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> DroppedDefs(SnapshotDb.TruncationSpread spread, int defs)
        => defs == 0 ? [] : [new(DefsWithPathsDropped, spread.LostPaths), new(DefsWithValuesCut, spread.ValuesOnly)];

    public const string DefsWithPathsDropped = "defs_with_paths_dropped";
    public const string DefsWithValuesCut = "defs_with_values_cut";
}

public abstract class Command
{
    public abstract CliOperation Operation { get; }

    public virtual bool ReadsTranslations => false;

    public virtual bool SidesByPosition => false;

    public abstract int Run(CommandContext ctx);
}
