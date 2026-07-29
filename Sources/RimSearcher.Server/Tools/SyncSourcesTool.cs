using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

// 手动触发的源同步。放成工具而非只做启动检测，是为了让「发现有更新」和「把更新拉进来」
// 发生在同一个对话里，不必切到终端去跑反编译再重启。
public class SyncSourcesTool : ITool
{
    private readonly SourceSyncService _syncService;
    private readonly IndexRebuilder? _rebuilder;

    public SyncSourcesTool(SourceSyncService syncService, IndexRebuilder? rebuilder = null)
    {
        _syncService = syncService;
        _rebuilder = rebuilder;
    }

    public string Name => "rimworld-searcher__sync_sources";

    public IEnumerable<string> ExtraAcceptedKeys => ["mode", "op", "detail", "level", "versionId", "path", "filePath", "method", "member", "memberName", "class", "type", "typeName", "maxResults", "scopes", "source", "mod", "mods", "in"];

    // 本工具会调 IndexRebuilder 拿写锁，被读锁挡住就是自己等自己
    public bool BypassIndexGate => true;

    // 返回里已列了本次同步的逐类型 diff，不需要再追加一条过期提示
    public bool SuppressStalenessNotice => true;

    // 全服务器唯一一个会写盘的工具：action='sync' 反编译并就地改写源码目录。
    // 沿用接口默认的 true 会让 client 把它当查询直接放行，用户没机会在改盘前叫停。
    public bool ReadOnlyHint => false;

    // 粒度、版本、分页这些细节归各自的参数 description，这里只答「做什么、什么时候别用」。
    // 原先把四级 diff 粒度在这儿又讲了一遍，与 granularity/file/method 三个参数完全重复，
    // 长度是其余六个工具的五倍，纯占 tools/list 的上下文预算。
    public string Description =>
        "Keep the decompiled RimWorld/mod sources current. 'check' reports which assemblies changed (fast); "
        + "'sync' re-decompiles and reindexes in place (seconds to minutes); 'diff' reviews what a past sync "
        + "changed (needs source_history_depth > 0).";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[] { "check", "sync", "diff" },
                description =
                    "'check' (default) only reports which assemblies changed. 'sync' re-decompiles the changed sources. "
                    + "'diff' reports what a past sync changed (requires source_history_depth > 0).",
                @default = "check"
            },
            sources = new
            {
                // 数组写法也收（ToolArgs.GetStringList），schema 就得如实声明两种，
                // 否则又是一处「描述允许、schema 禁止」的自相矛盾
                type = new[] { "string", "array" },
                items = new { type = "string" },
                description =
                    "Optional source names to limit the operation, matching the 'name' of configured C# sources — "
                    + "a mod without an explicit name in config.toml takes its name from About.xml. Either a "
                    + "comma-separated string or an array of names. Omit to cover every followable source."
            },
            granularity = new
            {
                type = "string",
                @enum = new[] { "files", "members" },
                description =
                    "For action='diff': 'files' (default) lists the changed file paths. 'members' also parses the "
                    + "listed files marked modified ('~') and reports which methods/properties/fields changed inside "
                    + "them — an added or removed file has no member-level diff, so a page holding only those reads "
                    + "identically to 'files' and says so. One syntax tree per expanded file, so narrow it down with "
                    + "'sources' or a smaller 'limit'. Combined with 'file' it lists every changed member of that one "
                    + "file instead of its line-level diff.",
                @default = "files"
            },
            file = new
            {
                type = "string",
                description =
                    "For action='diff': a relative path from the diff listing (e.g. 'RimWorld\\CompShield.cs'). "
                    + "Given, returns the line-level unified diff for that one file instead of the file list."
            },
            method = new
            {
                type = "string",
                description =
                    "For action='diff' together with 'file': diff only this member instead of the whole file — a method "
                    + "('CompTick'), property ('Label'), constructor ('.ctor'), indexer ('this') or operator ('+'). "
                    + "Aliases 'methodName'/'member' are also accepted."
            },
            className = new
            {
                type = "string",
                description =
                    "Optional companion to 'method': the declaring class, when several types in the file share the "
                    + "member name."
            },
            version = new
            {
                // 描述第一句就是「A number counts backwards」，照字面传 JSON 数字的调用会被
                // 会校验 inputSchema 的 client 在发出前挡下——服务端其实两种都收。同文件里
                // limit / sources 早已按「两种都收就两种都声明」对齐，只有这里漏了。
                type = new[] { "string", "integer" },
                description =
                    "For action='diff': which archived version to compare the current sources against. A number counts "
                    + "backwards — 1 (or -1) is the most recent archived version, 2 the one before it; out-of-range "
                    + "clamps to the oldest kept version and says so. An explicit id such as 'v0002' also works. "
                    + "Defaults to the most recent."
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLimit,
                description =
                    "For action='diff': max changed files to list, or max diff lines when 'file' is given. Under "
                    + "granularity='members' this is also the parse budget — only the files actually listed get parsed.",
                @default = DefaultLimit
            },
            offset = new
            {
                type = "integer",
                minimum = 0,
                description =
                    "For action='diff' without 'file': skip this many changed files before listing, to page through "
                    + "a change set larger than 'limit'. The listing prints the next offset to use.",
                @default = 0
            }
        },
        required = Array.Empty<string>()
    };

    private const int DefaultLimit = 100;
    private const int MaxLimit = 2000;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var action = (ToolArgs.GetOptionalString(args, "action", "mode", "op") ?? "check").ToLowerInvariant();
        // 不收 'scope'：这个工具按源名工作，没有 scope 概念，而 ServerInstructions 在教
        // 别的工具用 scope:'all'。收了它，scope:'all' 会被当成一个不存在的源名而硬报错；
        // 不收，它就是个被忽略的多余参数，请求照常按全量走。
        var only = ToolArgs.GetStringList(args, "sources", "source", "name");

        // action 的合法性必须先判，再判「有没有可跟随的源」。反过来的话 action='typo' 会被
        // 下面那条早退吃掉：调用方拿到一份 config.toml 配置示例、isError=false，看不出自己
        // 请求的 diff 压根没被识别。这与「拼错的 action 不许静默落到 check」是同一个坑的两个入口。
        string? resolved = action switch
        {
            "sync" or "update" or "run" => "sync",
            "diff" or "changes" => "diff",
            "check" or "status" or "probe" => "check",
            _ => null
        };

        if (resolved == null)
        {
            // 逐值括注，不挂参数表。原先这里拼 ArgSpec.BuildUsage()：448 字符里 394 字符与
            // 这个错无关（六个参数在 action 定下来之前一个都用不上），还把枚举值说了第二遍，
            // 而三个动作各自的代价与前置条件——真正该说的那块——一句都没有。同工具的
            // granularity 与 trace 的 mode 一直就是这么写的。
            return new ToolResult(
                $"Unknown action '{action}'. Use 'check' (default; report which assemblies changed), "
                + "'sync' (re-decompile the changed ones and reindex) or "
                + "'diff' (review what a past sync changed; needs source_history_depth > 0 in config.toml).",
                true);
        }

        // granularity 与 action 是同一个坑的另一个入口：schema 声明了 enum，但 client 不一定校验，
        // 而服务端此前只认 'members'，其余一律当 'files' 走。于是 granularity='typo' 返回的是一份
        // 逐字正常的文件列表，调用方看不出自己要的成员粒度压根没被识别。
        var granularity = ToolArgs.GetOptionalString(args, "granularity", "detail", "level");
        var wantsMembers = granularity is not null
            && (granularity.Equals("members", StringComparison.OrdinalIgnoreCase)
                || granularity.Equals("member", StringComparison.OrdinalIgnoreCase));
        if (granularity is not null && !wantsMembers
            && !granularity.Equals("files", StringComparison.OrdinalIgnoreCase)
            && !granularity.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult(
                $"Unknown granularity '{granularity}'. Use 'files' (default, lists changed file paths) "
                + "or 'members' (also lists which members changed inside each modified file).", true);
        }

        var followable = _syncService.FollowableSources;
        if (followable.Count == 0)
        {
            // 标成错误：没有可跟随的源意味着这次请求执行不了，而不是「执行了、结果为空」。
            // isError=false 会让调用方把这段配置示例当成正常返回接着往下推理。
            return new ToolResult(
                "No followable sources configured.\n"
                + "Add an assemblies path to a [[sources]] block in config.toml, e.g.\n"
                + "  [[sources]]\n"
                + "  name       = \"Core\"\n"
                + "  csharp     = 'S:\\RimWorldSource\\Core'\n"
                + "  assemblies = 'D:\\SteamLibrary\\steamapps\\common\\RimWorld\\RimWorldWin64_Data\\Managed'",
                true);
        }

        // 'sources' 里的名字必须当场核对。三条 action 原先各自静默处理认不出的名字：
        // check 干脆忽略这个参数报全量，sync 逐个 continue 后报一份空结果，diff 走到
        // 「一个源都没轮到」那支，回的是「还没有历史，先跑 action='sync'」——那句话会
        // 把调用方推去做一次几分钟的重反编译，而它真正需要的只是改一个拼错的名字。
        string? sourcesNotice = null;
        if (only is { Length: > 0 })
        {
            var knownNames = followable.Select(entry => entry.Name).ToArray();
            var unknown = only
                .Where(name => !knownNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            // 「已配置但不可跟随」必须与「名字不存在」分开说。vanilla 是 config 里真实存在的
            // 源（每个查询工具的 scope 描述都写着 sources: vanilla, …），只是没配 assemblies；
            // 两者报同一句话，会让调用方向用户断言「vanilla 不是已配置的源」——与它刚从 scope
            // 里读到的清单直接矛盾。两种情形的修复动作也完全不同：一个补 assemblies 路径，
            // 一个改拼写。
            var allNames = _syncService.AllSourceNames;
            var configuredButNotFollowable = unknown
                .Where(name => allNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (unknown.Length == only.Length)
            {
                return new ToolResult(
                    configuredButNotFollowable.Length == unknown.Length
                        ? $"{Quote(unknown)} {(unknown.Length == 1 ? "is a configured source" : "are configured sources")} "
                          + "with no 'assemblies' path, so sync_sources cannot follow "
                          + $"{(unknown.Length == 1 ? "it" : "them")}. Add assemblies = '<path to the Managed folder>' "
                          + "to the matching [[sources]] block in config.toml. "
                          + $"Followable right now: {string.Join(", ", knownNames)}."
                        : $"No configured source matches {Quote(unknown.Except(configuredButNotFollowable, StringComparer.OrdinalIgnoreCase).ToArray())}. "
                          + $"Configured sources: {string.Join(", ", allNames)} "
                          + $"(followable: {string.Join(", ", knownNames)}).", true);
            }

            if (unknown.Length > 0)
            {
                sourcesNotice =
                    $"\n\n_Ignored {Quote(unknown)}: "
                    + (configuredButNotFollowable.Length == unknown.Length
                        ? "configured, but with no 'assemblies' path to follow."
                        : "no configured source by that name.")
                    + $" Followable sources: {string.Join(", ", knownNames)}._";
            }
        }

        try
        {
            var result = resolved switch
            {
                "sync" => await RunSyncAsync(only, cancellationToken),
                "diff" => RunDiff(new DiffRequest(
                    only,
                    // schema 里的 minimum/maximum 是给调用方看的，不是被强制执行的——服务端自己夹
                    Math.Clamp(ToolArgs.GetInt(args, DefaultLimit, "limit", "maxResults"), 1, MaxLimit),
                    Math.Max(0, ToolArgs.GetInt(args, 0, "offset", "skip", "start")),
                    ToolArgs.GetOptionalString(args, "file", "path", "filePath"),
                    ToolArgs.GetOptionalString(args, "method", "methodName", "member", "memberName"),
                    ToolArgs.GetOptionalString(args, "className", "class", "type", "typeName"),
                    ToolArgs.GetOptionalString(args, "version", "versionId"),
                    ToolArgs.GetOptionalString(args, "granularity", "detail", "level"))),
                _ => RunCheck(only)
            };

            return sourcesNotice == null ? result : result with { Content = result.Content + sourcesNotice };
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("Sync cancelled.", true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Sync failed: {ex.Message}", true);
        }
    }

    private static string Quote(IEnumerable<string> names)
        => string.Join(", ", names.Select(name => $"'{name}'"));

    private ToolResult RunCheck(string[]? only)
    {
        var report = _syncService.Check(only);
        var builder = new StringBuilder();

        // 表头说清下面那批数字是**待办**而不是战果：check 只读 sha256，一个字节都不写。
        // 不说的话，`6 new, 0 changed, 0 gone` 会被读成「已经同步好了 6 个」。
        // 原句两半都不准，且互相矛盾（第十二轮盲测里唯一一条被整条答错的链）：
        //   「differences against the decompiled copies on disk」——check 比的是**游戏程序集的
        //     sha256 与上次 sync 留下的记录**（Inspect 里 previousMap 取自 state.Sources），
        //     跟反编译产物没有关系；
        //   「nothing has been decompiled yet」——本意是「本次调用一个字节都没写」（恒真），
        //     写出来是「这台机器从来没反编译过」，而这是本次调用**根本没验证过**的量。
        // 本机恰好两者相反：产物在磁盘上、带着 RimSearcher 自己的归属标记，只是 sync 记录空了。
        // 被测方照这句判「C# 侧是空的、索引不能用、先跑一次全量 sync」——整条结论反了。
        //
        // 拆法：关于磁盘的那半句删掉，换成本次真正比过的东西；关于「本次没写盘」的那半句
        // 只在**有变更**时留——它要防的误读（把待办数读成战果）只在那时才可能发生，
        // 无变更时结论行是「All followable sources are up to date」，那里没有可被误读的数。
        // 「有变更」与「该重反编译」不是一回事，判据先算出来：整批变更是不是清一色
        // 「记录没了、产物还在」。清一色时那半句归结论行说一次，逐行不再重复（R19）。
        var onlyLostRecords = report.AnyChanges && report.Changes
            .Where(c => c.HasChanges)
            .All(c => c.IsLostRecordOnly);

        builder.AppendLine(
            $"Source check ({report.ElapsedMs} ms, game version {_syncService.GameVersion ?? "unknown"}) — "
            + "assembly hashes compared against the record left by the last sync"
            + (report.AnyChanges
                ? "; the counts below are pending work — this call decompiled nothing:"
                : ":"));
        foreach (var change in report.Changes)
            builder.AppendLine($"  {change.Describe(withOutputNote: !onlyLostRecords)}");

        // 结论的覆盖面必须跟着 only 走。sources 过滤生效之后，「全部可跟随的源都是最新的」
        // 就成了一句拿 1/10 抽样下的全称断言——没被扫到的那 9 个源变了也不会有人发现。
        // 有变更那支同理：不回填 sources 的话，照着提示跑 action='sync' 是全量重反编译。
        var partial = only is { Length: > 0 };

        // 回填的名字取自实际扫过的源，而不是 only 原样：only 允许夹着认不出的名字
        // （那种情况另有 _Ignored ..._ 提示），照抄回去等于教调用方再传一次错的名字。
        var checkedNames = string.Join(",", report.Changes.Select(change => change.SourceName));
        var withSources = partial && checkedNames.Length > 0 ? $" and sources='{checkedNames}'" : string.Empty;

        // 「有变更」与「该重反编译」不是一回事。整批变更都只是「记录没了、产物还在」时，
        // 照这句跑一次 action='sync' 是把十一个源全量重反编译一遍换来零内容变化——第十二轮
        // 盲测里被测方正是这么劝的。结论行要跟着分岔走，否则上面刚说清的那半句在这里被推翻。
        builder.AppendLine(onlyLostRecords
            ? $"\nNo assembly content differs — every count above is a missing record next to decompiled "
              + $"output that is still on disk, so the index is usable as it stands. Run action='sync'"
              + $"{withSources} only to rebuild the record; it re-decompiles everything listed and changes "
              + "no query result."
            : report.AnyChanges
            ? $"\nChanges detected. Run this tool again with action='sync'{withSources} to re-decompile."
            : partial
                ? $"\nThe {OutputText.Quantity(report.Changes.Count, "checked sources")} "
                  + $"{(report.Changes.Count == 1 ? "is" : "are")} up to date; "
                  + "the other followable sources were not checked."
                : "\nAll followable sources are up to date.");

        return new ToolResult(builder.ToString().TrimEnd());
    }

    private sealed record DiffRequest(
        string[]? Only,
        int Limit,
        int Offset,
        string? FilePath,
        string? Method,
        string? ClassName,
        string? Version,
        string? Granularity);

    // 概览里单个文件最多列出的成员变化条数。这一条纯粹是防单文件淹没整份概览：
    // 一个被大改的文件能有上百个成员变动，摊在文件列表里没人读得下去。
    // 想看全的出口是把 file 收窄到这个文件——那条路径不截断。
    private const int MaxMembersPerFileInListing = 20;

    private ToolResult RunDiff(DiffRequest request)
    {
        if (!_syncService.History.Enabled)
        {
            // 时序不能省。depth 是服务构造期一次性读进 SourceHistoryStore 的（无热重载），
            // 归档又只在 sync 路径里写；只说「改 config」的话，照做后重跑 diff 拿到的是逐字
            // 相同的这一句，零新信息。同一个 config 项在 sync 侧的指引一直是带时序的
            // （"before the next sync"），两条路径此前详略不同。
            return new ToolResult(
                "Source history is disabled, so nothing has been archived to diff against. "
                + "Set source_history_depth to 1 or more in config.toml, restart the server, "
                + "then run action='sync' — the archive is written by that sync, so diff stays empty until then.",
                true);
        }

        if (!string.IsNullOrWhiteSpace(request.FilePath)) return RunFileDiff(request);

        if (!string.IsNullOrWhiteSpace(request.Method))
        {
            return new ToolResult(
                $"'method' needs a 'file' to look in — pass the path of the file that contains '{request.Method}', "
                + "as printed by action='diff' without 'file'.", true);
        }

        var wantMembers = string.Equals(request.Granularity, "members", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Granularity, "member", StringComparison.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        var any = false;

        foreach (var entry in _syncService.FollowableSources)
        {
            if (request.Only is { Length: > 0 }
                && !request.Only.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

            var versions = _syncService.History.ListVersions(entry.Name);
            if (versions.Count == 0) continue;

            if (!TryResolveVersion(versions, request.Version, entry.Name, out var versionId, out var notice, out var error))
                return new ToolResult(error!, true);

            var target = versions.First(v => v.Id == versionId);

            // 版本是调用方指定的那一版，不再固定取最新一版——此前这里漏传 versionId，
            // 于是列表模式下 version 参数看着被接受，实际永远在跟最新一版比。
            var diff = _syncService.History.DiffAgainst(entry.Name, entry.Path, versionId);
            if (diff == null) continue;

            any = true;
            builder.AppendLine(
                $"## {entry.Name} — since {target.Id} ({target.CapturedAtUtc:yyyy-MM-dd HH:mm} UTC)");
            if (notice != null) builder.AppendLine(notice);
            builder.AppendLine(
                $"{diff.Added} added, {diff.Modified} modified, {diff.Removed} removed "
                + $"({OutputText.Quantity(versions.Count, "versions")} kept, "
                + $"{target.ArchivedBytes / 1024} KB archived)");

            // 解析预算就是这一页列出的文件数——没有第二个隐藏的天花板。列多少就解析多少，
            // 代价与输出量始终成正比，调用方用 limit 一个旋钮就能控住。
            var listedModified = 0;
            foreach (var change in diff.Changes.Skip(request.Offset).Take(request.Limit))
            {
                var mark = change.Kind switch
                {
                    FileChangeKind.Added => "+",
                    FileChangeKind.Removed => "-",
                    _ => "~"
                };
                builder.AppendLine($"  {mark} {change.RelativePath}");

                if (change.Kind == FileChangeKind.Modified) listedModified++;
                if (wantMembers && change.Kind == FileChangeKind.Modified)
                    AppendMemberChanges(builder, entry, versionId, change.RelativePath);
            }

            var shown = Math.Max(0, Math.Min(diff.Changes.Count - request.Offset, request.Limit));
            var remaining = diff.Changes.Count - request.Offset - shown;

            // 成员粒度只对 Modified 展开，故这一页全是新增/删除时它逐字等同于 granularity='files'。
            // 一句不说的话，这份返回读起来是「这些文件里没有成员变化」——而真相是这些文件整份都是
            // 新增的，成员级差异对它们不存在。首次 sync 之后的第一次 diff 必然走到这里。
            if (wantMembers && shown > 0 && listedModified == 0)
            {
                builder.AppendLine(diff.Modified > 0
                    ? $"  (granularity='members' expands modified files only; none on this page — "
                      + $"{diff.Modified} of the {OutputText.Quantity(diff.Changes.Count, "changed files")} "
                      + $"{(diff.Modified == 1 ? "is" : "are")} modified, page to them with offset)"
                    : "  (granularity='members' expands modified files only; this source has none — "
                      + "every change here is a whole file added or removed)");
            }

            // offset 翻过了整个变更集时一条也列不出来，而上面的表头照常写着「N added」——
            // 不说一句的话，这份返回读起来就是「这一段没有变更」。
            if (shown == 0 && diff.Changes.Count > 0)
            {
                var lastPage = Math.Max(0, ((diff.Changes.Count - 1) / request.Limit) * request.Limit);
                builder.AppendLine(
                    $"  (offset {request.Offset} is past the end of "
                    + $"{OutputText.Quantity(diff.Changes.Count, "changed files")} — "
                    + $"the last page starts at offset={lastPage})");
            }

            // 文法归 Fold.Explicit：`... +N more of M <什么> (<怎么拿到>)`。这一行原先三处都在
            // 共用文法之外：丢了 `+`（调用方就是按 `... +` 认截断的）、总数后面裸着一个没有名词
            // 的数（`of 30` 数的是什么全靠猜）、下一步写在破折号后面而不是括号里。同一个记号在别的
            // 六个工具上都是那一形，独这里要再学一遍——那三处偏离现在按构造不可能再发生。
            //
            // 走显式那一形而不是 Fold.Line：翻页不经过 ScopeFilter，下一步是 offset，
            // 而 limit:'all' 在这里只是换一页、narrow the query 更无从执行。
            if (remaining > 0)
            {
                builder.AppendLine(Fold.Explicit(
                    remaining, "changed files",
                    $"next page: offset={request.Offset + shown}"
                    + (request.Limit < MaxLimit ? $", or raise limit, max {MaxLimit}" : string.Empty),
                    total: diff.Changes.Count));
            }

            builder.AppendLine();
        }

        return new ToolResult(any
            ? builder.ToString().TrimEnd()
            : "No recorded history yet. Run action='sync' first.");
    }

    // 一个被改写的文件内部，具体是哪些成员变了。只对 Modified 展开：新增/删除的整份文件里
    // 每个成员当然都是新增/删除的，逐个列出来只是把文件名换了种更长的写法。
    private void AppendMemberChanges(StringBuilder builder, SourcePathEntry entry, string versionId, string relativePath)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        var currentPath = PathSecurity.ResolveInsideRoot(entry.Path, relativePath);
        if (currentPath == null || !PathSecurity.IsPathSafe(currentPath) || !File.Exists(currentPath)) return;

        if (TooLarge(currentPath))
        {
            builder.AppendLine("    (file too large to parse)");
            return;
        }

        var archived = _syncService.History.ReadArchived(entry.Name, versionId, relativePath);
        if (archived == null) return;

        string current;
        try
        {
            current = File.ReadAllText(currentPath);
        }
        catch
        {
            return;
        }

        var lines = DiffMembers(archived, current);

        if (lines == null)
        {
            builder.AppendLine("    (no parsable members)");
            return;
        }

        if (lines.Count == 0)
        {
            // 文件哈希变了但成员一个没动：改的是 using、命名空间或成员之外的琐碎内容
            builder.AppendLine("    (changed outside any member declaration)");
            return;
        }

        foreach (var line in lines.Take(MaxMembersPerFileInListing)) builder.AppendLine($"    {line}");
        if (lines.Count > MaxMembersPerFileInListing)
        {
            // 同上：原先丢了 `+`、下一步挂在破折号后面。缩进比列举那条深一级（这是文件下的成员）。
            builder.AppendLine(Fold.Explicit(
                lines.Count - MaxMembersPerFileInListing, "members",
                $"pass file='{relativePath}' with granularity='members' to list them all",
                indent: "    "));
        }
    }

    // 两份内容之间的成员级差异。null 表示两侧都解析不出任何成员（不是 C#，或解析失败），
    // 与「解析出来了但一个都没变」区分开——后者说明改动落在成员声明之外。
    private static List<string>? DiffMembers(string archivedText, string currentText)
    {
        var before = RoslynHelper.ListMemberTexts(archivedText);
        var after = RoslynHelper.ListMemberTexts(currentText);

        if (before.Count == 0 && after.Count == 0) return null;

        var lines = new List<string>();

        foreach (var (key, text) in after)
        {
            if (!before.TryGetValue(key, out var old)) lines.Add($"+ {key}");
            else if (!string.Equals(old, text, StringComparison.Ordinal)) lines.Add($"~ {key}");
        }

        foreach (var key in before.Keys)
        {
            if (!after.ContainsKey(key)) lines.Add($"- {key}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    // 版本选择。数字按「往前数第 n 代」解释（1 与 -1 都是最近一代），超出保留范围时
    // 夹到最老的一代并说明——夹到最新一代会给出与不传 version 完全相同的结果，
    // 等于把参数悄悄吃掉。'v0002' 这样的字面量 id 同样接受：index.json 里存的就是它。
    private static bool TryResolveVersion(
        IReadOnlyList<HistoryVersion> versions,
        string? raw,
        string sourceName,
        out string versionId,
        out string? notice,
        out string? error)
    {
        versionId = versions[^1].Id;
        notice = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        var text = raw.Trim();

        if (int.TryParse(text, out var requested))
        {
            var steps = Math.Abs(requested);
            if (steps == 0) steps = 1;

            if (steps > versions.Count)
            {
                versionId = versions[0].Id;
                notice =
                    $"(requested {OutputText.Quantity(steps, "versions")} back, "
                    + $"only {versions.Count} kept — using the oldest, {versionId})";
            }
            else
            {
                versionId = versions[^steps].Id;
            }

            return true;
        }

        // 只认索引里真实存在的 Id。放任任意串进去等于让调用方指定历史根下的任意子目录，
        // 而 files/ 那一层的相对路径校验只保证「不出这个子目录」，管不了子目录选在哪
        var matched = versions.FirstOrDefault(v => string.Equals(v.Id, text, StringComparison.OrdinalIgnoreCase));
        if (matched == null)
        {
            error =
                $"Unknown version '{text}' for source '{sourceName}'. "
                + $"Kept versions: {string.Join(", ", versions.Select(v => v.Id))}. "
                + "Pass a number instead to count backwards (1 = most recent), or omit 'version' entirely.";
            return false;
        }

        versionId = matched.Id;
        return true;
    }

    // 归档里的旧内容 vs 磁盘上的当前内容。文件在某一侧缺失即为纯新增/纯删除。
    //
    // file 与 version 都是调用方给的裸串，且直接参与路径拼接：不校验的话
    // 「归档 里没有 / 当前有」这一支就是一个任意绝对路径的存在性探针，配上存在的 version
    // 更能把源根与历史根之外的文本读出来。两个根各自独立校验，不共用结论。
    private ToolResult RunFileDiff(DiffRequest request)
    {
        var file = request.FilePath!;
        var limit = request.Limit;

        foreach (var entry in _syncService.FollowableSources)
        {
            if (request.Only is { Length: > 0 }
                && !request.Only.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

            var versions = _syncService.History.ListVersions(entry.Name);
            if (versions.Count == 0) continue;

            if (!TryResolveVersion(versions, request.Version, entry.Name, out var versionId, out var notice, out var error))
                return new ToolResult(error!, true);

            var currentPath = PathSecurity.ResolveInsideRoot(entry.Path, file);
            var archivedPath = _syncService.History.ResolveArchivedPath(entry.Name, versionId, file);

            if (currentPath == null || archivedPath == null)
            {
                return new ToolResult(
                    $"'{file}' is not a relative path inside a source directory — absolute paths and '..' are refused. "
                    + "Pass the path exactly as printed by action='diff' without 'file' "
                    + @"(e.g. 'RimWorld\CompShield.cs').", true);
            }

            // 当前文件此前完全不过白名单，是全仓唯一一条这样的读路径。源根被配成指向别处的
            // 链接时，只靠上面的「不出源根」校验并不能保证读到的东西在允许的目录内
            if (!PathSecurity.IsPathSafe(currentPath))
            {
                return new ToolResult(
                    $"'{file}' resolves outside the allowed directories. "
                    + "Only files under the configured source paths can be diffed.", true);
            }

            var relative = Path.GetRelativePath(entry.Path, currentPath);

            if (TooLarge(archivedPath) || TooLarge(currentPath))
            {
                return new ToolResult(
                    $"'{relative}' is larger than the {SourceHistoryStore.MaxComparableFileSize / (1024 * 1024)} MB "
                    + "diff limit. Read the archived and current copies separately instead of diffing them.", true);
            }

            var archived = _syncService.History.ReadArchived(entry.Name, versionId, file);

            string? current = null;
            try
            {
                if (File.Exists(currentPath)) current = File.ReadAllText(currentPath);
            }
            catch (Exception ex)
            {
                return new ToolResult($"Failed to read current file: {ex.Message}", true);
            }

            if (archived == null && current == null) continue;

            var header = notice == null ? string.Empty : notice + "\n";
            var member = request.Method;
            var label = string.IsNullOrWhiteSpace(member)
                ? $"{entry.Name}/{relative} @ {versionId}"
                : $"{entry.Name}/{relative}::{member} @ {versionId}";

            // 归档里没有旧内容不等于「这一版新增」：没被这一版动过的文件同样不入档。
            // 两者对调用方的含义正相反（「这次改了」vs「这次没动」），而 sync_sources 的
            // 主用途就是回答「更新后到底改了什么」，混为一谈会直接给出反向的事实。
            if (archived == null)
            {
                var presentBefore = _syncService.History.WasPresentAt(entry.Name, versionId, file);
                var verdict = presentBefore switch
                {
                    true => "(unchanged in this version — it already existed at this point and this sync did not touch it; "
                            + "the archive only keeps files a sync overwrote or removed)",
                    false => "(added in this version — no previous content)",
                    _ => "(no archived copy, and this version's file list could not be read, so 'added' and "
                         + "'untouched' cannot be told apart here — run action='diff' without 'file' to see "
                         + "which files this version actually changed)",
                };
                return new ToolResult($"{header}--- {label}\n{verdict}");
            }

            if (current == null)
                return new ToolResult($"{header}--- {label}\n(removed — only the archived copy remains)");

            // 已经收窄到一个文件了，成员清单就没有再截断的理由——概览里的那道上限
            // 是为了不让单个文件淹没整份列表，这里没有别的文件要保护
            if (string.IsNullOrWhiteSpace(member)
                && string.Equals(request.Granularity, "members", StringComparison.OrdinalIgnoreCase))
            {
                var members = DiffMembers(archived, current);

                if (members == null)
                    return new ToolResult($"{header}--- {label}\n(no parsable members — not C#, or it failed to parse)");

                if (members.Count == 0)
                    return new ToolResult($"{header}--- {label}\n(changed outside any member declaration)");

                var listing = new StringBuilder();
                listing.AppendLine($"{header}--- {label}");
                listing.AppendLine($"{OutputText.Quantity(members.Count, "members")} changed:");
                foreach (var line in members) listing.AppendLine($"  {line}");
                listing.Append("\nPass 'method' with one of these names for its line-level diff.");
                return new ToolResult(listing.ToString());
            }

            if (!string.IsNullOrWhiteSpace(member))
            {
                var className = ToolArgs.StripLocateFilterPrefix(request.ClassName ?? string.Empty);
                var memberName = ToolArgs.StripLocateFilterPrefix(member!);
                if (className.Length == 0) className = null;

                var before = RoslynHelper.ExtractMemberText(archived, memberName, className);
                var after = RoslynHelper.ExtractMemberText(current, memberName, className);

                if (!before.IsOk && !after.IsOk)
                {
                    return new ToolResult(
                        $"Member '{memberName}' not found in either version of {relative}. "
                        + "Use the inspect tool to see the members this file currently declares, or drop 'method' "
                        + "to diff the whole file.", true);
                }

                if (!before.IsOk)
                    return new ToolResult($"{header}--- {label}\n(added in this version)\n```csharp\n{after.Content}\n```");

                if (!after.IsOk)
                    return new ToolResult($"{header}--- {label}\n(removed in this version)\n```csharp\n{before.Content}\n```");

                return new ToolResult(header + UnifiedDiffFormatter.Format(
                    before.Content, after.Content, label, contextLines: 3, maxLines: limit));
            }

            return new ToolResult(header + UnifiedDiffFormatter.Format(
                archived, current, label, contextLines: 3, maxLines: limit));
        }

        return new ToolResult(
            $"'{file}' not found in any source's history. Run action='diff' without 'file' to see the changed file list.",
            true);
    }

    private static bool TooLarge(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > SourceHistoryStore.MaxComparableFileSize;
        }
        catch
        {
            // 拿不到大小就当它超限：读不出大小的文件更没理由硬读
            return true;
        }
    }

    private async Task<ToolResult> RunSyncAsync(string[]? only, CancellationToken cancellationToken)
    {
        var report = await _syncService.SyncAsync(only, cancellationToken);

        // 必须在打印任何统计之前拦掉：拒绝执行时 Changes 是空的，落到下面的循环里
        // 会输出一句「Sync finished in 0 ms」外加零条变更——看起来像「跑完了，没事可做」，
        // 而真相是压根没跑。
        if (report.AlreadyRunning)
        {
            return new ToolResult(
                "A sync is already running in this server process. Nothing was done — retry once it finishes.",
                true);
        }

        var builder = new StringBuilder();

        builder.AppendLine($"Sync finished in {report.ElapsedMs} ms (game version {_syncService.GameVersion ?? "unknown"}):");
        foreach (var change in report.Changes) builder.AppendLine($"  {change.Describe()}");

        if (report.Outcomes.Count > 0)
        {
            var succeeded = report.Outcomes.Count(o => o.Success);
            var files = report.Outcomes.Sum(o => o.FileCount);
            builder.AppendLine($"\nDecompiled {succeeded}/{report.Outcomes.Count} assemblies, {files} source files.");

            // 指向 diff 之前先看历史开没开。source_history_depth=0 时这次同步根本没归档，
            // action='diff' 是**确定性**报错——而正文刚说有几百个文件变更，调用方追查不到
            // 反过来还会怀疑这次 sync 是不是真的成功了。
            var diffHint = _syncService.History.Enabled
                ? " — use action='diff' for the file list."
                : " — no archive was kept (source_history_depth is 0), so action='diff' cannot list these; "
                  + "set it to 1 or more in config.toml before the next sync.";

            foreach (var changeSet in report.FileChanges.Where(c => c.Any))
            {
                builder.AppendLine(
                    $"  {changeSet.SourceName}: {changeSet.Added} added, {changeSet.Modified} modified, "
                    + $"{changeSet.Removed} removed{diffHint}");
            }

            foreach (var failure in report.Outcomes.Where(o => !o.Success).Take(10))
                builder.AppendLine($"  FAILED {Path.GetFileName(failure.AssemblyPath)}: {failure.Error}");
        }

        // XML 变了不需要反编译，但索引仍是旧的，同样得重扫一遍
        var xmlChanged = SourceChangeProbe.Pending?.ChangedXmlSources.Count > 0;
        if (xmlChanged && !report.AnyPromoted)
        {
            builder.AppendLine($"\nXML defs changed in: {string.Join(", ", SourceChangeProbe.Pending!.ChangedXmlSources)}"
                             + " — no decompile needed, reindexing only.");
        }

        // 反编译改的是磁盘，内存里的索引还是旧的，就地重扫一遍；重建期间其它查询会挂起等待。
        // 判据是「有源真的转正了」而不是「有程序集反编译成功」：事务化之后，一个源可以
        // 逐个程序集都成功、却在提交阶段失败并整体回滚，此时磁盘没变，重建只是白白
        // 清空重扫约 4 秒，期间所有查询挂起。
        if (report.AnyPromoted || xmlChanged)
        {
            if (_rebuilder == null)
            {
                builder.AppendLine(
                    "\nThe in-memory index still reflects the previous sources. Restart the MCP server to rebuild it.");
            }
            else
            {
                var rebuild = _rebuilder.Rebuild(TimeSpan.FromMinutes(2));
                builder.AppendLine(rebuild.Succeeded
                    ? $"\nIndex rebuilt in {rebuild.ElapsedMs} ms "
                      + $"({OutputText.Quantity(rebuild.CsharpPaths, "C# paths")}, "
                      + $"{OutputText.Quantity(rebuild.XmlPaths, "XML paths")}). No restart needed."
                    : "\nIndex rebuild skipped: another rebuild was already running. Retry, or restart the server.");

                SourceChangeProbe.RecordSync(report.FileChanges);
            }
        }

        // 有源整体回滚时必须标成错误：正文里那行「同步失败，已整体回滚」很容易被
        // 淹在成功源的统计里，而调用方据此决定要不要重试。
        return new ToolResult(builder.ToString().TrimEnd(), report.Failures.Count > 0);
    }
}
