using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class SearchRegexTool : ITool
{
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

            var grouped = results.GroupBy(r => r.Path).Take(50);

            var output = $"Regex matches for '{pattern}' ({results.Count} found in scope '{scope.Expression}'):\n\n" +
                         string.Join("\n\n", grouped.Select(g =>
                         {
                             var fileName = System.IO.Path.GetFileName(g.Key);
                             var groupItems = g.ToList();
                             var matches = groupItems.Take(3).Select(m => "  " + m.Preview);
                             var moreCount = groupItems.Count > 3 ? $"\n  ... +{groupItems.Count - 3} more in this file" : "";
                             var label = ScopeArgs.Label(scope.ShowLabels ? scope.SourceNameOf(g.Key) : null);
                             return $"`{fileName}`{label}\n{string.Join("\n", matches)}{moreCount}";
                         }));

            if (truncated)
            {
                output += "\n\n[Results limited to 50 files, use more specific pattern to narrow down]";
            }

            return new ToolResult(output);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }
}
