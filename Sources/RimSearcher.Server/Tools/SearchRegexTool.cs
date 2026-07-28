using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class SearchRegexTool : ITool
{
    // 命中可能集中在少数文件，也可能散落在几千个文件里。后者全列出来对调用方无用，
    // 但截掉了就必须说，见下面的 notes。
    private const int MaxFilesShown = 50;

    // 缺省命中上限。扫盘型工具比列表型工具给得多（默认 10 条对正则搜索没意义），
    // 但 'all' 一律走 ScopeArgs.HardLimit，不再是原先那个写死的 500。
    private const int DefaultMatchLimit = 100;

    private readonly SourceIndexer _indexer;
    private readonly ScopeCatalog _scopeCatalog;

    public SearchRegexTool(SourceIndexer indexer, ScopeCatalog scopeCatalog)
    {
        _indexer = indexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__search_regex";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "regex", "fileExtension", "extension", "ext", "caseInsensitive", "maxResults", "count", "scopes", "source", "sources", "mod", "mods", "in"];

    public string Description =>
        ".NET regex search across indexed C# and XML files, with an optional extension filter (e.g. '.cs') and " +
        "scope. Results are grouped by file, showing at most 3 preview lines per file and at most 50 files; both " +
        "cuts are always stated in a trailing note, so output without that note is the complete match set. " +
        "Counts are matching lines, not match sites — a line the pattern hits twice counts once.";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__search_regex",
        "pattern (a regex, e.g. 'class.*:.*ThingComp'). Aliases accepted: query, regex.",
        "pattern (required), ignoreCase, fileFilter (aliases: fileExtension, extension, ext), scope, limit.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            pattern = new
            {
                type = "string",
                minLength = 1,
                description = "Regex pattern to search. Examples: '<thingClass>Apparel</thingClass>', 'void CompTick\\(\\)', 'class.*:.*ThingComp'. Aliases 'query'/'regex' are also accepted."
            },
            ignoreCase = new { type = "boolean", @default = true, description = "Whether to ignore case, defaults to true." },
            fileFilter = new { type = "string", description = "Optional extension filter such as '.cs' or '.xml'. Aliases 'fileExtension'/'extension'/'ext' are also accepted." },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeArgs.LimitSchemaProperty(DefaultMatchLimit, fuzzy: false)
        },
        required = new[] { "pattern" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var pattern = ToolArgs.GetRequiredString(args, ArgSpec, "pattern", "query", "regex");
        var ignoreCase = ToolArgs.GetBool(args, true, "ignoreCase", "caseInsensitive");
        var fileFilter = ToolArgs.GetOptionalString(args, "fileFilter", "fileExtension", "extension", "ext");
        var scope = ScopeArgs.Resolve(_scopeCatalog, args);
        var limit = ScopeArgs.GetDisplayLimit(args, fallback: DefaultMatchLimit);

        try
        {
            // scope 与 fileFilter 都下推给索引层在扫描前生效——留到这里筛会被命中上限吃空
            var (results, truncated, matchesByFile, diagnostics) = await _indexer.SearchRegexAsync(
                pattern, scope, fileFilter, ignoreCase, limit.Count, cancellationToken, progress);

            // 拼错的 scope 被静默退回全域，有结果、无结果两条路径都要说
            var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

            // 同 trace usages：scope 对扫盘工具是硬过滤，落选来源不统计，故要显式点一句
            if (results.Count == 0)
            {
                // fileFilter 必须出现在零命中消息里：'.txt' 这种把候选集筛成 0 的过滤，
                // 原先的措辞会说成「scope 'all' 里没有」——而 scope 里有的是命中，
                // 只是没有一个 .txt 文件。同时报出过滤后的候选文件数，让「筛空了」一眼可见。
                var filterNote = string.IsNullOrEmpty(fileFilter)
                    ? string.Empty
                    : $" with fileFilter '{fileFilter}' ({diagnostics.CandidateFiles} file(s) matched that filter)";

                return new ToolResult(
                    $"No matches for pattern '{pattern}' in scope '{scope.Expression}'{filterNote}."
                    + $"{ScopeArgs.RetryWiderNotice(scope)}{scopeNotice}");
            }

            // 索引层是并发扫描后从 ConcurrentBag 收口的，文件之间的先后完全看线程调度；
            // 不排一下，同一次查询重跑两遍文件顺序就能不一样。
            var allFiles = results
                .GroupBy(r => r.Path)
                // 排序键与印出来的东西必须是同一个：只印文件名却按完整路径排，读者看到的是
                // 「每进一个目录字母序就重来一遍」。这也正是扫盘推进的顺序（SourceIndexer
                // .InDisplayOrder），故截断留下的恒是这张表的前缀。
                .OrderBy(g => System.IO.Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var shownFiles = allFiles.Take(MaxFilesShown).ToList();

            // 未截断时报真实命中总数（各文件命中数之和），而不是预览条数——预览每文件封顶，
            // 两者可以差很远。截断时这个和只覆盖恰好扫到的文件，随线程调度浮动，故只说确定的量。
            var totalMatches = matchesByFile.Values.Sum();
            // 「扫描在上限处停了」只说一次，说在末尾那行——那里同时给得出下一步。表头
            // 原先也说一遍（", scan stopped at the limit"），而 "first N" 本就含着这个意思。
            // 单位改说 "previews"：表头的 N 数的是预览行，与「命中数」是两个量。
            var headline = truncated
                ? $"first {results.Count} previews in scope '{scope.Expression}'"
                // 有文件没扫全时这个总数只是下界，表头与末尾那条尾注要同时改口
                : $"{ScopeArgs.FoundCount(totalMatches, diagnostics.AnyFileIncomplete)} "
                  + $"in scope '{scope.Expression}'";

            // 列出来的文件全同源时标签只印一次（见 ScopeArgs.SourceLabeling）
            var labels = ScopeArgs.SourceLabeling.Of(
                shownFiles.Select(g => scope.ShowLabels ? scope.SourceNameOf(g.Key) : null));

            // string.Join 立即枚举，故读这个旗时它已经攒完了
            var anyFileFolded = false;
            var output = $"Regex matches for '{pattern}' ({headline}){labels.Header}:\n\n" +
                         string.Join("\n\n", shownFiles.Select(g =>
                         {
                             var fileName = System.IO.Path.GetFileName(g.Key);
                             // 同理，同一文件内的命中也是乱序到达的。不排序时 Take(3) 拿到的是任意
                             // 三条，读起来像 L17、L15 这样倒着走，且靠前的命中可能根本没被选中。
                             var groupItems = g.OrderBy(m => m.LineNumber).ToList();
                             var matches = groupItems.Take(SourceIndexer.MaxPreviewsPerFile).Select(m => $"  L{m.LineNumber}: {m.Preview}");

                             // 减的是实际显示条数，且基数取索引层数出来的真实命中数：预览在索引层
                             // 每文件封顶 3 条（与这里的 Take 对齐），拿 groupItems.Count 当基数
                             // 会把第 4 条起的命中吞掉。
                             var shown = Math.Min(groupItems.Count, SourceIndexer.MaxPreviewsPerFile);
                             var inFile = matchesByFile.TryGetValue(g.Key, out var c) ? c : groupItems.Count;
                             if (inFile > shown) anyFileFolded = true;
                             var moreCount = inFile > shown
                                 ? "\n" + ScopeArgs.PerFileFold(inFile - shown)
                                 : "";

                             var label = labels.Row(scope.ShowLabels ? scope.SourceNameOf(g.Key) : null);
                             return $"`{fileName}`{label}\n{string.Join("\n", matches)}{moreCount}";
                         }));

            // 两处截断互相独立：truncated 说的是扫描在命中上限处停了，文件数上限则是
            // 这里静默 Take 掉的。原先只有一条提示且挂在前者上，于是「命中没超限但文件超了」
            // 的情况完全不吭声，调用方会把不完整的列表当成全部。
            //
            // 两者的尾注文法也不同，且各自都对：扫描停了就不知道还剩多少（那些文件根本没
            // 打开过），只有文件数超了才数得出准数，能用全服统一的 `... +N more`。
            if (truncated)
            {
                // 截断时 allFiles 只是「已扫到的那批预览」里的文件数，不是命中文件总数——扫描早已
                // 在命中上限处停下，后面的候选文件根本没打开过。原先无条件称其为 "matching files"，
                // 那个数比真实值小一到两个数量级，而调用方会拿它当结论。
                var extra = allFiles.Count > MaxFilesShown
                    ? new[]
                    {
                        $"only the first {MaxFilesShown} files are listed, and the {allFiles.Count} distinct files "
                        + "seen so far are not the total number of matching files"
                    }
                    : null;
                output += "\n\n" + ScopeArgs.ScanStoppedLine(results.Count, limit, extra);
            }
            else if (allFiles.Count > MaxFilesShown)
            {
                output += $"\n\n... +{allFiles.Count - MaxFilesShown} more matching files "
                          + "(narrow the pattern or the scope)";
            }

            if (anyFileFolded)
                output += "\n\n" + ScopeArgs.PerFilePreviewCapLine(SourceIndexer.MaxPreviewsPerFile);

            // 「没有尾注即完整」是本工具写在 Description 里的契约，被跳过/被弃扫的文件必须破这个契约
            if (diagnostics.AnyFileIncomplete)
            {
                var incomplete = new List<string>();
                if (diagnostics.TimedOutFiles > 0)
                    incomplete.Add($"{diagnostics.TimedOutFiles} file(s) were abandoned mid-scan because the pattern "
                                   + "timed out on them (catastrophic backtracking) — their per-file match counts are missing");
                if (diagnostics.UnreadableFiles > 0)
                    incomplete.Add($"{diagnostics.UnreadableFiles} file(s) could not be read and were skipped entirely");
                if (diagnostics.LineCappedFiles > 0)
                    incomplete.Add($"{diagnostics.LineCappedFiles} file(s) were only scanned to line {diagnostics.LineCap}");

                output += "\n\n" + ScopeArgs.NotScannedInFullLine(incomplete);
            }

            output += scopeNotice;

            return new ToolResult(output);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }

}
