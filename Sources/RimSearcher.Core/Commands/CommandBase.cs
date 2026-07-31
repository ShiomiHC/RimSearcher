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
               "moves into a 'notes' array, so nothing is lost. The command's own table key is " +
               "always present — an empty array when nothing matched, never a missing key.",
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

    /// <summary>
    /// 翻页。声明产地唯一,措辞在 <see cref="Report.PageNotice"/> ——
    /// 一个只在 <c>list</c> 上有的参数等于没有:调用方记不住哪条命令认它,于是一律去接管道。
    /// </summary>
    public static OptionSpec Offset(string what) => new()
    {
        Name = "offset",
        Aliases = ["skip", "start", "page-from"],
        Placeholder = "<n>",
        Help = $"Skip this many {what} before listing. The total is always reported, so you can tell when " +
               "you have reached the end.",
        Default = "0",
    };

    public static readonly OptionSpec Scope = new()
    {
        Name = "scope",
        Aliases = ["mod", "mods", "source", "from"],
        Placeholder = "<expr>",
        // R10 的一词两义:'vanilla' 在这里 = Ludeon 出的每一个模块(Core 加全部已装 DLC),
        // 而一份**叫** vanilla 的快照可能只有 Core。两份文档原先都没写,而两者在句子里
        // 长得一模一样 —— 一个快照名和一个 scope 词恰好同形,是最容易被读成同一件事的那种。
        Help = "Restrict results to some of the mods in the snapshot. Comma-separated; a leading '-' excludes. " +
               "'all', 'vanilla', a packageId, or a group name from the config file. Writing 'all,-vanilla' " +
               "means everything except vanilla. 'vanilla' (also 'core', 'base', 'official') means every module " +
               "Ludeon ships — Core and each DLC in the snapshot — which is not the same thing as a snapshot " +
               "that happens to be named vanilla; the output spells out what it resolved to.",
        Default = ScopeFilter.DefaultScope,
        Narrows = true,
    };

    public static readonly OptionSpec Type = new()
    {
        Name = "type",
        Aliases = ["def-type", "kind", "category"],
        Placeholder = "<DefType>",
        Help = "Restrict results to one def type, for example ThingDef or HediffDef.",
        Narrows = true,
    };
}

/// <summary>一次命令执行的上下文。命令只通过它取参、开库、写报告。</summary>
public sealed class CommandContext(RimConfig config, ParseResult args)
{
    private SnapshotDb? _db;
    private bool _snapshotNoticed;
    private bool _scopeNoticed;

    public RimConfig Config { get; } = config;
    public ParseResult Args { get; } = args;
    public Report Report { get; } = new() { Narrowing = args.Narrowing() };
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

    /// <summary>
    /// <c>--limit</c> 的取值,**并且把夹紧说出来**。
    ///
    /// <see cref="LimitValue.Clamped"/> 与 <see cref="NoticeKind.Clamp"/> 从落地起就没有一个
    /// 引用点:解析器老老实实记下了「这个数被我改了」,而没有任何一条路把它印出来。于是
    /// <c>--limit 5000</c> 与 <c>--limit 2000</c> 的输出逐字相同 —— 调用方明确要了 5000,
    /// 拿回 2000 条,再从裸计数读出「一共就这么多」。这与本轮反复在修的那类错同形:
    /// **参数被静默改写,而输出里没有任何迹象**。声明层早就写着「超过 2000 会被夹紧」,
    /// 差的只是当场说一句。
    /// </summary>
    public LimitValue Limit(string name = "limit", int? fallback = null)
    {
        var limit = Args.Limit(name, fallback);
        if (limit.Clamped)
            Report.Notice(NoticeKind.Clamp,
                $"--{name} {Args.Value(name)} is above the ceiling of {Limits.MaxLimit}, so at most " +
                $"{Limits.MaxLimit} were taken. Pass --{name} all to lift the cap, or page with --offset.");
        return limit;
    }

    /// <summary>
    /// 「不给 <c>--limit</c> 就是全量」的那几条命令(<c>mods</c>、以及 <c>list</c> 不带
    /// def 类型的那一半)用的取法。
    ///
    /// 与 <see cref="Limit"/> 的区别只在**缺省值**:那边缺省是 25 条,这边缺省是全给 ——
    /// 截一刀会让「一共有哪些」这个问题答不完整,而这两处问的正是这个。
    /// (原本的理由写的是「就那么几十条,截一刀省不下什么」,不成立:实测一份装了 mod 的
    /// 快照有 232 个 def 类型。缺省全给是拿 context 换完整,不是白捡的便宜。)
    /// 写成 <c>Args.Value("limit") is null ? LimitValue.All : ctx.Limit()</c>
    /// 曾在两处各写一份;那是一条**语义**(这条命令默认给全),不该以一个三元表达式的形态
    /// 散落在调用点。
    /// </summary>
    public LimitValue LimitOrAll()
        => Args.Value("limit") is null ? LimitValue.All : Limit();

    public ScopeFilter Scope()
    {
        var filter = ScopeFilter.Parse(Args.Value("scope"), Db.PackageIds(), Config);
        if (filter.UnknownTokens.Count > 0)
            throw new CliUsageException(
                $"--scope does not know {string.Join(", ", filter.UnknownTokens.Select(t => $"'{t}'"))}. " +
                // 原先这里是裸 ", …" —— 22 个 packageId 里举 8 个,而省掉的正是「还有 14 个」。
                // 读出来是「大概就这些」,与「一共就这 8 个」逐字同形。举例子这一层也要说清
                // 没举出来的有多少(产地在 NameList)。
                $"This snapshot contains: {NameList.Render(Db.PackageIds(), 8)}" +
                ". 'rimsearcher mods' lists them all.");
        AnnounceScope(filter);
        return filter;
    }

    /// <summary>
    /// 一个 scope 词展开成了什么,在**有结果时**也要说。
    ///
    /// 文档两处白纸黑字承诺过(SKILL《Parameters》与 cli-reference 每个 --scope 条目:
    /// 「the output spells out what a scope resolved to whenever it is more than one mod」),
    /// 而 <see cref="ScopeFilter.Describe"/> 的七个调用点全嵌在零结果句里 ——
    /// **查到东西时不告诉你范围,查不到时才告诉你**,承诺与实现正好反了向。
    /// 四份轨迹撞上,其中一份连着五次 --scope vanilla 零次播报,而那个词的口径直接
    /// 决定答案怎么写(它实际展开成六个 Ludeon 模块,含 DLC)。
    ///
    /// 判据取「展开与你输入的字面不同」而不是「多于一个 mod」:后者对
    /// <c>--scope ludeon.rimworld</c> 这种写死 packageId 的调用也要发声,而那种调用
    /// 一个字都不需要 —— 你写的就是你得到的。前者覆盖了全部已举证的用例而不收那份税。
    /// (SKILL.md 那句已按此改写,承诺闸盯着;八轮审计前它挂了一轮没跟上。)
    /// </summary>
    private void AnnounceScope(ScopeFilter filter)
    {
        if (_scopeNoticed || filter.IsAll) return;
        var described = filter.Describe();
        if (string.Equals(described, filter.Expression, StringComparison.Ordinal)) return;
        _scopeNoticed = true;
        Report.Notice(NoticeKind.Filter, $"--scope {described}.");
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

        // 一词两义,而两义都在这一次调用里活着:快照叫 vanilla,--scope vanilla 是另一回事,
        // 提问者嘴里的「原版」是第三回事。实测代价 22 倍 —— 机器上恰好有个叫 vanilla 的
        // 快照(Core + 导出器,两个 mod),而问的是原版行为,顺手 --snapshot vanilla,
        // 于是唯一烧油的那个穿梭机(来自 Odyssey)整个不在射程里,输出一个字不提。
        //
        // 「显式指定就闭嘴,因为你已经说了要哪个环境」这条原则在这里恰好不成立:
        // 前提是调用方知道自己选的环境是什么,而这一格正是他以为自己知道其实不知道的。
        // 只在**撞名**时说,所以这句话平常一次都不出现。
        if (ScopeFilter.IsGroupName(name, Config))
        {
            var ids = Db.PackageIds();
            Report.Notice(NoticeKind.Boundary,
                $"'{name}' is both this snapshot's name and a --scope group name, and the two cover " +
                $"different things. This snapshot holds {Tally.Complete(ids.Count).Render("mod")}: " +
                $"{NameList.Render(ids, 6)}. Anything outside them — another mod, or a DLC this export " +
                $"did not have enabled — is absent from every answer below, not reported as missing.");
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

/// <summary>
/// 「磁盘那一层没量过」这句话的**唯一产地**。
///
/// 收割从 v7 起默认开,但 <c>--no-harvest-translations</c> 和没配 <c>mod_roots</c> 都还能造出
/// 没量过的库。这种库里,凡是把译文按「生效 / 磁盘上」分栏的输出都在暗示磁盘那一层在场 ——
/// 而它一行也没有,读的人会把「没量过」读成「磁盘上也没有」。差得最远的两句话共用一个形状。
///
/// 发在表**旁边**而不是表里:少一整层与某一行缺一格是两回事,写进列里会被当成行的属性。
///
/// 条件里带上**这台机器现在配没配 mod_roots**:没配的机器上根本没有第二层可言,那句话
/// 就成了每一次 get 都跟着的一句废话,而废话读多了会连带着把旁边真正的边界说明一起跳过。
/// 配了却没量,才是「本可以知道而没去知道」—— 也只有那时,补救(重导一次)是成立的。
/// </summary>
internal static class DiskLayer
{
    public static void NoteIfUnmeasured(CommandContext ctx)
    {
        if (ctx.Db.Harvested || ctx.Config.ModRoots.Count == 0) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            "This snapshot never scanned the language files on disk, so every row here is one the game actually " +
            "had: the absence of an 'on disk' row is not evidence that no installed mod translates it. " +
            "'rimsearcher snapshot import' scans by default — re-import to measure that layer.", footnote: true);
    }
}

public abstract class Command
{
    public abstract CommandSpec Spec { get; }
    public abstract int Run(CommandContext ctx);
}
