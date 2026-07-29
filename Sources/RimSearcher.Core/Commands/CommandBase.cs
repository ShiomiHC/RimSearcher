using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 全局参数 —— 与命令声明同源,由 <see cref="ArgParser"/> 合并进每条命令的参数表。
/// </summary>
public static class GlobalOptions
{
    public static readonly OptionSpec Snapshot = new()
    {
        Name = "snapshot",
        Aliases = ["snap", "env", "profile"],
        Placeholder = "<name>",
        Help = "Query this named snapshot instead of the one that would be picked automatically. " +
               "An explicit choice always wins over auto-detection.",
    };

    public static readonly OptionSpec Db = new()
    {
        Name = "db",
        Aliases = ["database", "snapshot-path"],
        Placeholder = "<path>",
        Help = "Query the snapshot database at this path directly, bypassing the registry.",
    };

    public static readonly OptionSpec Json = new()
    {
        Name = "json",
        Arity = Arity.Flag,
        Help = "Emit machine-readable JSON. Anything the text output would have said in prose " +
               "moves into a 'notes' array, so nothing is lost.",
    };

    public static readonly OptionSpec Config = new()
    {
        Name = "config",
        Placeholder = "<path>",
        Help = "Use this config file instead of the default one.",
    };

    public static readonly IReadOnlyList<OptionSpec> All = [Snapshot, Db, Json, Config];
}

/// <summary>常用的按命令参数。声明放在这里是为了让别名与措辞只有一个产地。</summary>
public static class CommonOptions
{
    /// <summary>
    /// 07-② 实证:同一个「最多要几条」的意图被真实调用方拼成 maxResults / max_results / limit
    /// 三种,且 <c>limit: "all"</c> 高频出现。归一化吃掉大小写与分隔符差异,同义词列在别名里,
    /// <c>all</c> 是正式取值而不是错误。
    /// </summary>
    public static OptionSpec Limit(string what) => new()
    {
        Name = "limit",
        Short = 'n',
        Aliases = ["max-results", "count", "top", "rows", "num", "head"],
        Placeholder = "<n|all>",
        Help = $"How many {what} to return. Use 'all' for no cap. " +
               $"Values above {Limits.MaxLimit} are clamped to {Limits.MaxLimit}.",
        Default = Limits.DefaultLimit.ToString(),
    };

    public static readonly OptionSpec Scope = new()
    {
        Name = "scope",
        Aliases = ["mod", "mods", "source", "from"],
        Placeholder = "<expr>",
        Help = "Restrict results to some of the mods in the snapshot. Comma-separated; a leading '-' excludes. " +
               "'all', 'vanilla', a packageId, or a group name from the config file. Example: all,-vanilla",
        Default = ScopeFilter.DefaultScope,
    };

    public static readonly OptionSpec Type = new()
    {
        Name = "type",
        Aliases = ["def-type", "kind", "category"],
        Placeholder = "<DefType>",
        Help = "Restrict results to one def type, for example ThingDef or HediffDef.",
    };
}

/// <summary>一次命令执行的上下文。命令只通过它取参、开库、写报告。</summary>
public sealed class CommandContext(RimConfig config, ParseResult args)
{
    private SnapshotDb? _db;
    private bool _snapshotNoticed;

    public RimConfig Config { get; } = config;
    public ParseResult Args { get; } = args;
    public Report Report { get; } = new();
    public bool Json => Args.Flag("json");

    public SnapshotDb Db
    {
        get
        {
            if (_db is not null) return _db;
            var selection = SnapshotCatalog.Resolve(Config, Args.Value("db"), Args.Value("snapshot"));
            _db = SnapshotDb.Open(selection.Path);
            AnnounceSnapshot(selection, _db);
            return _db;
        }
    }

    public ScopeFilter Scope()
    {
        var filter = ScopeFilter.Parse(Args.Value("scope"), Db.PackageIds(), Config);
        if (filter.UnknownTokens.Count > 0)
            throw new CliUsageException(
                $"--scope does not know {string.Join(", ", filter.UnknownTokens.Select(t => $"'{t}'"))}. " +
                $"This snapshot contains: {string.Join(", ", Db.PackageIds().Take(8))}" +
                (Db.PackageIds().Count > 8 ? ", …" : "") +
                ". 'rimsearcher mods' lists them all.");
        return filter;
    }

    /// <summary>
    /// 快照寻址与过期自证是同一次比对的两个产出(06)。
    /// **正常态一个字都不说** —— 上下文预算硬约束:一致时发声等于每次查询都交一次无用税。
    /// </summary>
    private void AnnounceSnapshot(SnapshotSelection selection, SnapshotDb db)
    {
        if (_snapshotNoticed) return;
        _snapshotNoticed = true;

        var report = SnapshotCatalog.Compare(db, Config);
        var name = selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path);

        switch (report.Match)
        {
            case EnvironmentMatch.Same:
                return;   // 正常态:零字节

            case EnvironmentMatch.VersionDrift:
                Report.Notice(NoticeKind.Staleness,
                    $"Snapshot '{name}' was exported from game version {db.Meta.GameVersion}, " +
                    $"but the game is now on {report.GameVersion}. Values below are from the older build; " +
                    "re-export to refresh.");
                return;

            case EnvironmentMatch.DifferentModlist:
                // 声明调用方**没有选过**的事情,不声明它选过的。
                // 显式指定或固定了快照,就等于已经说过「我要查的是另一个环境」——每次再复述一遍,
                // 就是 00 论据 3 淘汰掉的那种「每次返回挂免责声明」换个马甲回来。
                // 自动检测/只有一份兜底选出来的才需要说,因为那不是调用方的意思。
                if (selection.Source is SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb or SelectionSource.Pinned)
                    return;
                Report.Notice(NoticeKind.Staleness,
                    $"Snapshot '{name}' was picked for you, and it does not describe the mods currently enabled in " +
                    $"the game ({report.Added} enabled now that it lacks, {report.Removed} in it that are no longer " +
                    "enabled). 'rimsearcher snapshot status' explains the difference; --snapshot picks another.");
                return;

            case EnvironmentMatch.Unknown:
                // 读不到 ModsConfig.xml 时不在每次查询里发声(那是常态噪声),
                // 详情分流到 snapshot status —— 06 上下文预算的「详情分流专用命令」。
                return;
        }
    }

    public void Dispose() => _db?.Dispose();
}

public abstract class Command
{
    public abstract CommandSpec Spec { get; }
    public abstract int Run(CommandContext ctx);
}
