using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class SearchRegexTool : ITool
{
    // 命中可能集中在少数文件，也可能散落在几千个文件里。后者全列出来对调用方无用，
    // 但截掉了就必须说，见下面的 notes。
    private const int MaxFilesShown = 50;

    private readonly SourceIndexer _indexer;
    private readonly ScopeCatalog _scopeCatalog;

    public SearchRegexTool(SourceIndexer indexer, ScopeCatalog scopeCatalog)
    {
        _indexer = indexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__search_regex";

    public string Description =>
        "Regex search across indexed C# and XML files. Supports optional extension filter (e.g., '.cs') and scope.";

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
            limit = ScopeArgs.LimitSchemaProperty()
        },
        required = new[] { "pattern" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var pattern = ToolArgs.GetRequiredString(args, ArgSpec, "pattern", "query", "regex");
        var ignoreCase = ToolArgs.GetBool(args, true, "ignoreCase", "caseInsensitive");
        var fileFilter = ToolArgs.GetOptionalString(args, "fileFilter", "fileExtension", "extension", "ext");
        var scope = ScopeArgs.Resolve(_scopeCatalog, args);
        var maxResults = ScopeArgs.GetDisplayLimit(args, fallback: 100);

        try
        {
            // scope 与 fileFilter 都下推给索引层在扫描前生效——留到这里筛会被命中上限吃空
            var (results, truncated) = await _indexer.SearchRegexAsync(
                pattern, scope, fileFilter, ignoreCase, maxResults == 0 ? 500 : maxResults, cancellationToken, progress);

            if (results.Count == 0)
                return new ToolResult($"No matches for pattern '{pattern}' in scope '{scope.Expression}'.");

            var allFiles = results.GroupBy(r => r.Path).ToList();
            var shownFiles = allFiles.Take(MaxFilesShown);

            var output = $"Regex matches for '{pattern}' ({results.Count} found in scope '{scope.Expression}'):\n\n" +
                         string.Join("\n\n", shownFiles.Select(g =>
                         {
                             var fileName = System.IO.Path.GetFileName(g.Key);
                             var groupItems = g.ToList();
                             var matches = groupItems.Take(3).Select(m => "  " + m.Preview);
                             var moreCount = groupItems.Count > 3 ? $"\n  ... +{groupItems.Count - 3} more in this file" : "";
                             var label = ScopeArgs.Label(scope.ShowLabels ? scope.SourceNameOf(g.Key) : null);
                             return $"`{fileName}`{label}\n{string.Join("\n", matches)}{moreCount}";
                         }));

            // 两处截断互相独立：truncated 说的是扫描在命中上限处停了，文件数上限则是
            // 这里静默 Take 掉的。原先只有一条提示且挂在前者上，于是「命中没超限但文件超了」
            // 的情况完全不吭声，调用方会把不完整的列表当成全部。
            var notes = new List<string>();
            if (truncated) notes.Add($"scanning stopped at the {results.Count}-match cap");
            if (allFiles.Count > MaxFilesShown) notes.Add($"only the first {MaxFilesShown} of {allFiles.Count} matching files are listed");

            if (notes.Count > 0)
                output += $"\n\n[{string.Join("; ", notes)} — narrow the pattern or the scope to see the rest]";

            return new ToolResult(output);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }
}
