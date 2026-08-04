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
    /// 真实调用方把「最多要几条」拼成 maxResults / max_results / limit 三种,且常写
    /// <c>limit: "all"</c> —— 同义词进别名,<c>all</c> 是正式取值而不是错误。
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

    /// <summary>翻页。措辞产地在 <see cref="Report.PageNotice"/>;每条列表命令都认它。</summary>
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
        // 一词两义:这里的 'vanilla' = Ludeon 出的每一个模块(Core 加全部已装 DLC),
        // 而一份**叫** vanilla 的快照可能只有 Core。
        Help = "Restrict results to some of the mods in the snapshot. Comma-separated; a leading '-' excludes. " +
               "'all', 'vanilla', a packageId, or a group name from the config file. Writing 'all,-vanilla' " +
               "means everything except vanilla. 'vanilla' (also 'core', 'base', 'official') means every module " +
               "Ludeon ships — Core and each DLC in the snapshot — which is not the same thing as a snapshot " +
               "that happens to be named vanilla; the output spells out what it resolved to.",
        Default = ScopeFilter.DefaultScope,
        Narrows = true,
    };

    /// <summary>
    /// 后缀匹配的对侧开关。默认那条后缀是纯文本、不在 <c>.</c> 上对齐,于是
    /// <c>graphicData.shaderType</c> 连 <c>swimmingGraphicData.shaderType</c> 一起收走 ——
    /// 结果里那句「横跨几种路径形状」说得出这件事,而在此之前没有一条命令能把它筛掉。
    /// </summary>
    public static readonly OptionSpec ExactPath = new()
    {
        Name = "exact-path",
        Aliases = ["whole-path", "path-exact"],
        Arity = Arity.Flag,
        Help = "Match the field path as a whole instead of as a suffix. Write '[]' for any index, so a path " +
               "shape such as 'lifeStages[].bodyGraphicData.shaderType' can be pasted straight back in.",
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
    /// 跑很久的命令用来**当场**说一句话的地方 —— <see cref="Report"/> 攒到命令结束才渲染。
    /// 走 stderr:它不是结果,不该混进 stdout 那份有字节级闸的输出。默认丢弃。
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
    /// <c>--limit</c> 的取值,**并且把夹紧说出来** —— 参数被静默改写而输出里没有迹象时,
    /// 裸计数会被读成「一共就这么多」。
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
    /// def 类型的那一半)用的取法:与 <see cref="Limit"/> 只差缺省值 —— 那边 25 条,
    /// 这边全给,因为「一共有哪些」截一刀就答不完整(实测一份带 mod 的快照有 232 个 def 类型)。
    /// </summary>
    public LimitValue LimitOrAll()
        => Args.Value("limit") is null ? LimitValue.All : Limit();

    public ScopeFilter Scope()
    {
        var filter = ScopeFilter.Parse(Args.Value("scope"), Db.PackageIds(), Config);
        if (filter.UnknownTokens.Count > 0)
            throw new CliUsageException(
                $"--scope does not know {string.Join(", ", filter.UnknownTokens.Select(t => $"'{t}'"))}. " +
                // 举例子也要说清没举出来的有多少,否则「举了 8 个」与「一共就这 8 个」同形
                // (产地在 NameList)。
                $"This snapshot contains: {NameList.Render(Db.PackageIds(), 8)}" +
                ". 'rimsearcher mods' lists them all.");
        AnnounceScope(filter);
        return filter;
    }

    /// <summary>
    /// 一个 scope 词展开成了什么,在**有结果时**也要说 —— 展开口径直接决定答案怎么读
    /// (<c>vanilla</c> 展开成六个 Ludeon 模块,含 DLC)。
    ///
    /// 判据取「展开与你输入的字面不同」而不是「多于一个 mod」:后者对
    /// <c>--scope ludeon.rimworld</c> 这种写死 packageId 的调用也要发声,而你写的就是你得到的。
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
    /// <c>--scope all,-X</c> 排除掉的那一半里,有多少也满足这次查询 —— 非零就说破。
    ///
    /// **这里的沉默推得出错结论**,与本项目别处的沉默不同:排除式的心智模型是
    /// 「我只是不想要 X」,而 X 里可能正是答案。实测
    /// <c>where compClass --value Vethara --scope all,-vanilla</c> 返回 92 个 def,
    /// 表干净、完整、看不出任何问题 —— 而问的「这个 mod 挂到哪些宿主上」那 7 个宿主
    /// 全在被排除的 vanilla 里,一个都没进表,零提示。**一张静默的错表比零结果贵得多**,
    /// 零结果至少当场知道自己没拿到东西。
    ///
    /// 判据当场算得出来:补集在 <see cref="ScopeFilter"/> 里就是 universe 减 included,
    /// 不用重新解析表达式;数一遍多跑一次查询,那是同一条 SQL 换个谓词。
    ///
    /// 计数放句尾,免得动词跟着单复数变 —— NounRegistry 管名词,不管动词。
    /// </summary>
    public void AnnounceExcluded(ScopeFilter scope, Func<ScopeFilter, int> count, string noun)
    {
        var rest = scope.Complement();
        if (rest is null) return;
        var n = count(rest);
        if (n == 0) return;
        Report.Notice(NoticeKind.Boundary,
            $"What --scope {scope.Expression} left out is not empty for this query: with " +
            $"--scope {rest.Describe()} instead, it finds {Tally.Complete(n).Render(noun)}.");
    }

    /// <summary>
    /// 快照寻址与过期自证是同一次比对的两个产出。**正常态一个字都不说** —— 一致时发声
    /// 等于每次查询都交一次上下文税。
    /// </summary>
    private void AnnounceSnapshot(SnapshotSelection selection, SnapshotDb db)
    {
        if (_snapshotNoticed) return;
        _snapshotNoticed = true;

        var report = SnapshotCatalog.Compare(db, Config);
        var name = selection.Alias ?? Path.GetFileNameWithoutExtension(selection.Path);

        // 「这次用了哪个快照」与「这个快照过没过期」是两件事。快照选错就是答案错,
        // 所以自动选择要说出选了哪个;只有一个快照时仍然零字节 —— 那时不存在选错。
        //
        // 走标签不走整句(<see cref="Report.SnapshotTag"/> 记着为什么),而 auto-detected
        // 的「这是猜的」一并进标签:`snapshot list` 的 active 列标得出**哪个**在用,标不出
        // 它是钉的还是猜的。
        //
        // 不报「还注册了哪几个」,也不指路 `snapshot list`:名单逐字重复,而那句指路
        // 在 SKILL.md 的 Snapshots 一节里已经是原文。这里只留这一次真正的选择结果。
        if (selection.Source is not (SelectionSource.ExplicitAlias or SelectionSource.ExplicitDb))
        {
            if (SnapshotCatalog.Enumerate(Config).Count > 1)
                Report.SnapshotTag =
                    selection.Source == SelectionSource.Pinned ? name : $"{name} auto-detected";
        }

        // 一词两义,而两义在这一次调用里都活着:快照叫 vanilla,--scope vanilla 是另一回事。
        // 「显式指定就闭嘴」这条原则在这里不成立 —— 它的前提是调用方知道自己选的环境是什么,
        // 而这一格正是他以为自己知道其实不知道的。只在**撞名**时说。
        if (ScopeFilter.IsGroupName(name, Config))
        {
            var ids = Db.PackageIds();
            Report.Notice(NoticeKind.Boundary,
                $"'{name}' is both this snapshot's name and a --scope group name, and the two cover " +
                $"different things. This snapshot holds {Tally.Complete(ids.Count).Render("mod")}: " +
                $"{NameList.Render(ids, 6)}. Anything outside them — another mod, or a DLC this export " +
                $"did not have enabled — is absent from every answer below, not reported as missing.");
        }

        // 「游戏现在多开/少开了哪些 mod」这一层**每次查询都不说**(成因见
        // EnvironmentReport.Added)。次序不是那一层:没人挑得出一个「次序变体」环境,
        // 而加载顺序决定同名 patch 谁赢 —— 于是快照里的值不是不全,是错的。
        // 这条与 VersionDrift / ContentDrift 同类,显式选择也不闭嘴。
        // 尾句不指 `snapshot status`:SKILL.md 的 Snapshots 一节把「它是那次完整比对」写死了,
        // 而这条每次查询都发,那个指路就是同一句话的第 N 份副本。留下的是后果 —— 它决定
        // 下面那个值怎么读,推不出来。
        if (report.Reordered)
            Report.Notice(NoticeKind.Staleness,
                $"The mods snapshot '{name}' describes are in a different load order in the game now. " +
                "Load order decides which patch wins, so a value below can differ from what the game resolves.");

        switch (report.Match)
        {
            case EnvironmentMatch.Same:
                return;   // 一致:除了上面那行「用的是哪个」,不再多说

            case EnvironmentMatch.VersionDrift:
                // 「下面的值出自旧那一版」是前半句的同义反复 —— 两个版本号已经把它说完了。
                Report.Notice(NoticeKind.Staleness,
                    $"Snapshot '{name}' was exported from game version {db.Meta.GameVersion}, " +
                    $"but the game is now on {report.GameVersion}. Re-export to refresh.");
                return;

            // 显式选择在这一格**不闭嘴**:`--snapshot modded` 声明的是「我要查那个环境」,
            // 不是「我知道那些 mod 的 XML 已经不是导出时那份了」。与 VersionDrift 同类。
            //
            // 但位置让结果去定(见 Report.DeferredNotice):这一条与版本漂移不同,它点着
            // 具体几个 mod,而绝大多数查询与那几个无关 —— 每次都占着表头第一行,读到第五遍
            // 就把表头整块训练成盲区,而表头恰恰是 scope 展开、精确/包含拆分这些真会改变
            // 答案的东西所在。无关时沉到表下,点到了(或证不出无关)照旧在最上面。
            case EnvironmentMatch.ContentDrift:
                Report.DeferredNotice(NoticeKind.Staleness, ContentDrift.Sentence(name, report.Content!),
                    [.. report.Content!.Changed, .. report.Content.Missing]);
                return;

            case EnvironmentMatch.Unknown:
                // 读不到 ModsConfig.xml 是常态噪声,不在每次查询里发声,详情分流到 snapshot status。
                return;
        }
    }

    public void Dispose() => _db?.Dispose();
}

/// <summary>
/// 「参考侧 XML 变了」这句话的**唯一产地**。日常查询与 <c>snapshot status</c> 都念它,
/// 两处各写一遍的话,详略不同会被读成两件不同的事。
/// </summary>
public static class ContentDrift
{
    /// <summary>
    /// 两个成因分开说 —— 「文件改了」下一步是重导,「mod 从磁盘上没了」下一步是先把它
    /// 装回来,混成一句会指错路。
    ///
    /// 措辞刻意说 "changed on disk",不说 "were edited":判据是长度与 mtime,
    /// 而 Steam 重下一份逐字节相同的文件也会让它响(<see cref="Snapshot.ContentFingerprint"/>)。
    /// 说成「被编辑过」就是拿一句证不了的话去指挥下一步。
    ///
    /// **一个方位词都不许有。** 这句话的位置由结果决定(见 <see cref="Output.Report.Settle"/>):
    /// 与答案无关时沉到表下,那时一句 "answers below" 指的是一片不存在的下文。
    /// 而且旧的那部分只是那几个 mod,不是整份答案 —— 指名道姓比指方位既准又不会指错。
    /// </summary>
    public static string Sentence(string name, ContentComparison content)
    {
        var parts = new List<string>();

        if (content.Changed.Count > 0)
            parts.Add($"{Tally.Complete(content.Changed.Count).Render("mod")} in snapshot '{name}' " +
                      $"({NameList.Render(content.Changed, 3)}) " +
                      $"{(content.Changed.Count == 1 ? "has" : "have")} Defs or Patches XML that changed on disk " +
                      "since the export, so anything read from " +
                      $"{(content.Changed.Count == 1 ? "it" : "them")} describes the older files. " +
                      "Re-export to pick them up.");

        if (content.Missing.Count > 0)
            parts.Add($"{Tally.Complete(content.Missing.Count).Render("mod")} the export read " +
                      $"({NameList.Render(content.Missing, 3)}) cannot be found on disk now, so whether " +
                      $"{(content.Missing.Count == 1 ? "its" : "their")} files still match cannot be checked at all.");

        return string.Join(" ", parts);
    }
}

/// <summary>
/// 「导出器半路停了,字段没发全」这半句的**唯一产地**。<c>get</c> / <c>snapshot status</c> /
/// <c>snapshot import</c> 三处都念它,此前各写各的,后两句已经逐字相同却仍是两份。
///
/// **只统一事实,不统一后果。** 三处的读者站的位置不一样:一个正看着这个 def 的字段表
/// (「下面这张表」),一个还没查任何 def(「以后 get 的时候」),一个刚导完(「工具将来会提醒你」)。
/// 把后果也压成一句,三处就都得说得含糊,而后果恰恰是这条声明存在的理由。
/// </summary>
internal static class ExportCap
{
    /// <summary>
    /// 某一个 def 上丢了几个字段。数是**下界** —— 导出器是停下来了,不是数完了
    /// (见 <c>snapshot truncated</c> 那一侧的同一条注解)。
    /// </summary>
    public static string OnDef(int fields)
        => $"{Tally.AtLeast(fields).Render("field")} were dropped at export time for depth or size";

    /// <summary>一批 def 里有几个被截过。<paramref name="among"/> 是插在计数与谓语之间的定语。</summary>
    public static string OverDefs(int defs, string among = "")
        => $"{Tally.Complete(defs).Render("def")}{among} had fields dropped at export time for depth or size";
}

/// <summary>
/// 「磁盘那一层没量过」这句话的**唯一产地**。
///
/// 收割默认开,但 <c>--no-harvest-translations</c> 与没配 <c>mod_roots</c> 都能造出没量过的库。
/// 那种库里「生效 / 磁盘上」的分栏仍在暗示磁盘那一层在场,于是「没量过」会被读成「磁盘上也没有」。
///
/// 发在表**旁边**而不是表里:少一整层与某一行缺一格是两回事,写进列里会被当成行的属性。
///
/// 条件里带上 <c>mod_roots</c> 配没配:没配的机器上不存在第二层,那句话就成了废话;
/// 配了却没量才有补救(重导一次)可言。
/// </summary>
internal static class DiskLayer
{
    public static void NoteIfUnmeasured(CommandContext ctx)
    {
        if (ctx.Db.Harvested || ctx.Config.ModRoots.Count == 0) return;
        ctx.Report.Notice(NoticeKind.Boundary,
            // 「import 默认就扫」是那条命令自己的 help 文本(cli-reference 的
            // --no-harvest-translations 行就是它渲染的),不在每次查询上重念;
            // 补救留一句,不留就把这条边界写成了死路。
            "This snapshot never scanned the language files on disk, so every row here is one the game actually " +
            "had: the absence of an 'on disk' row is not evidence that no installed mod translates it. " +
            "Re-import to measure that layer.", footnote: true);
    }
}

public abstract class Command
{
    public abstract CommandSpec Spec { get; }
    public abstract int Run(CommandContext ctx);
}
