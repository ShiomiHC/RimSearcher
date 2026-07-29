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
        // 'profile' 归 export 的 --modlist:mod 列表才是 RimWorld 语境里的 profile。
        // 两处都挂着,同一命令里就有两个归宿,归一化后必然撞车(声明层的闸抓到的正是这个)。
        Aliases = ["snap", "env"],
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
               "'all', 'vanilla', a packageId, or a group name from the config file. Writing 'all,-vanilla' " +
               "means everything except vanilla.",
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

    /// <summary>
    /// 跑很久的命令用来**当场**说一句话的地方。<see cref="Report"/> 是攒到命令结束才渲染的,
    /// 而对一次十分钟的导出,攒着等于没说 —— 人正盯着一个不动的终端,那一句话要能当场到。
    ///
    /// 走 stderr:它不是结果,不该混进 stdout 那份有字节级闸的输出里。默认丢弃,
    /// 于是测试里不必为它准备任何东西。
    /// </summary>
    public TextWriter Progress { get; init; } = TextWriter.Null;

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
    /// 不带任何 mod 过滤的 scope。零结果分流要用它 —— 「这个 scope 里没有」和
    /// 「整个快照里没有」是两件事,而分不清时报后者就是把缺席说成事实。
    /// </summary>
    public ScopeFilter Unscoped() => ScopeFilter.Parse("all", Db.PackageIds(), Config);

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

        // 「这次用了哪个快照」与「这个快照过没过期」是两件事,第一轮只修了后者。
        // 实测:注册了 modded 与 vanilla 两个,问的是 vanilla 的事,不带 --snapshot 跑出来的是
        // modded,而输出里一个字都没提 —— 发现它靠的是某个值里恰好混进了 mod 前缀,纯属运气。
        // 快照选错就是答案错,这一行不能省。只有一个快照时仍然零字节:那时不存在选错。
        if (selection.Source is not (SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb))
        {
            var registered = SnapshotCatalog.Enumerate(Config);
            if (registered.Count > 1)
            {
                var others = registered.Where(e => !string.Equals(e.Alias, name, StringComparison.OrdinalIgnoreCase))
                                       .Select(e => e.Alias);
                Report.Notice(NoticeKind.SnapshotChoice,
                    $"Using snapshot '{name}' ({(selection.Source == SelectionSource.Pinned ? "pinned" : "auto-detected")}); " +
                    $"also registered: {string.Join(", ", others)}.");
            }
        }

        switch (report.Match)
        {
            case EnvironmentMatch.Same:
                return;   // 一致:除了上面那行「用的是哪个」,不再多说

            case EnvironmentMatch.VersionDrift:
                Report.Notice(NoticeKind.Staleness,
                    $"Snapshot '{name}' was exported from game version {db.Meta.GameVersion}, " +
                    $"but the game is now on {report.GameVersion}. Values below are from the older build; " +
                    "re-export to refresh.");
                return;

            case EnvironmentMatch.DifferentModlist:
                // 声明调用方**这次没有说过**的事情,不声明它这次说过的。
                // 本次调用带了 --snapshot/--db,就是当场声明了「我要查的是那个环境」,再复述一遍
                // 就是 00 论据 3 淘汰掉的「每次返回挂免责声明」换个马甲回来。
                if (selection.Source is SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb)
                    return;

                // Pinned 不算说过。`snapshot use` 是**过去某一刻**的选择,而 mod 列表是会变的 ——
                // 两者不一致恰恰说明那次选择已经过时,这与 VersionDrift 是同一类事实。
                // 实测代价:Core-only 快照 + 22 个已启用 mod,`find` 返回一条裸计数,
                // 按三态文法读就是「全世界只有这一个」,而真相是「这份快照里只有这一个」。
                Report.Notice(NoticeKind.Staleness,
                    selection.Source == SelectionSource.Pinned
                        ? $"Snapshot '{name}' is the pinned one, but the game's mod list has changed since " +
                          $"({report.Added} enabled now that it lacks, {report.Removed} in it that are no longer " +
                          "enabled), so counts below are complete for the snapshot, not for your game. " +
                          "'rimsearcher snapshot status' explains the difference."
                        : $"Snapshot '{name}' was picked for you, and it does not describe the mods currently enabled in " +
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
