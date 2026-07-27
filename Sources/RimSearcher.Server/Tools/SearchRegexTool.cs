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

    public string Description =>
        ".NET regex search across indexed C# and XML files, with an optional extension filter (e.g. '.cs') and " +
        "scope. Results are grouped by file, showing at most 3 preview lines per file and at most 50 files; both " +
        "cuts are always stated in a trailing note, so output without that note is the complete match set.";

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
            limit = ScopeArgs.LimitSchemaProperty(DefaultMatchLimit)
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
            var (results, truncated, matchesByFile) = await _indexer.SearchRegexAsync(
                pattern, scope, fileFilter, ignoreCase, limit.Count, cancellationToken, progress);

            // 拼错的 scope 被静默退回全域，有结果、无结果两条路径都要说
            var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

            // 同 trace usages：scope 对扫盘工具是硬过滤，落选来源不统计，故要显式点一句
            if (results.Count == 0)
                return new ToolResult(
                    $"No matches for pattern '{pattern}' in scope '{scope.Expression}'."
                    + $"{ScopeArgs.RetryWiderNotice(scope)}{scopeNotice}");

            // 索引层是并发扫描后从 ConcurrentBag 收口的，文件之间的先后完全看线程调度；
            // 不排一下，同一次查询重跑两遍文件顺序就能不一样。
            var allFiles = results
                .GroupBy(r => r.Path)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var shownFiles = allFiles.Take(MaxFilesShown);

            // 未截断时报真实命中总数（各文件命中数之和），而不是预览条数——预览每文件封顶，
            // 两者可以差很远。截断时这个和只覆盖恰好扫到的文件，随线程调度浮动，故只说确定的量。
            var totalMatches = matchesByFile.Values.Sum();
            var headline = truncated
                ? $"first {results.Count} in scope '{scope.Expression}', scan stopped at the limit"
                : $"{totalMatches} found in scope '{scope.Expression}'";

            var output = $"Regex matches for '{pattern}' ({headline}):\n\n" +
                         string.Join("\n\n", shownFiles.Select(g =>
                         {
                             var fileName = System.IO.Path.GetFileName(g.Key);
                             // 同理，同一文件内的命中也是乱序到达的。不排序时 Take(3) 拿到的是任意
                             // 三条，读起来像 L17、L15 这样倒着走，且靠前的命中可能根本没被选中。
                             var groupItems = g.OrderBy(m => m.LineNumber).ToList();
                             var matches = groupItems.Take(3).Select(m => $"  L{m.LineNumber}: {m.Preview}");

                             // 减的是实际显示条数，且基数取索引层数出来的真实命中数：预览在索引层
                             // 每文件封顶 5 条，拿 groupItems.Count 当基数会把第 6 条起的命中吞掉。
                             var shown = Math.Min(groupItems.Count, 3);
                             var inFile = matchesByFile.TryGetValue(g.Key, out var c) ? c : groupItems.Count;
                             var moreCount = inFile > shown ? $"\n  ... +{inFile - shown} more in this file" : "";

                             var label = ScopeArgs.Label(scope.ShowLabels ? scope.SourceNameOf(g.Key) : null);
                             return $"`{fileName}`{label}\n{string.Join("\n", matches)}{moreCount}";
                         }));

            // 两处截断互相独立：truncated 说的是扫描在命中上限处停了，文件数上限则是
            // 这里静默 Take 掉的。原先只有一条提示且挂在前者上，于是「命中没超限但文件超了」
            // 的情况完全不吭声，调用方会把不完整的列表当成全部。
            var notes = new List<string>();
            if (truncated) notes.Add($"scanning stopped at the {results.Count}-preview cap");
            if (allFiles.Count > MaxFilesShown) notes.Add($"only the first {MaxFilesShown} of {allFiles.Count} matching files are listed");

            if (notes.Count > 0)
                output += $"\n\n[{string.Join("; ", notes)} — narrow the pattern or the scope to see the rest]";

            output += scopeNotice;

            return new ToolResult(output);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }

}
