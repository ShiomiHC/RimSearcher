using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class SearchRegexTool : ITool
{
    private readonly SourceIndexer _indexer;

    public SearchRegexTool(SourceIndexer indexer)
    {
        _indexer = indexer;
    }

    public string Name => "rimworld-searcher__search_regex";

    public string Description =>
        "Regex search across indexed C# and XML files. Supports optional extension filter (e.g., '.cs').";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__search_regex",
        "pattern (a regex, e.g. 'class.*:.*ThingComp'). Aliases accepted: query, regex.",
        "pattern (required), ignoreCase, fileFilter (aliases: fileExtension, extension, ext).");

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
            fileFilter = new { type = "string", description = "Optional extension filter such as '.cs' or '.xml'. Aliases 'fileExtension'/'extension'/'ext' are also accepted." }
        },
        required = new[] { "pattern" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var pattern = ToolArgs.GetRequiredString(args, ArgSpec, "pattern", "query", "regex");
        var ignoreCase = ToolArgs.GetBool(args, true, "ignoreCase", "caseInsensitive");
        var fileFilter = ToolArgs.GetOptionalString(args, "fileFilter", "fileExtension", "extension", "ext");

        try
        {
            var (results, truncated) = await _indexer.SearchRegexAsync(pattern, ignoreCase, cancellationToken, progress);
            
            if (!string.IsNullOrEmpty(fileFilter))
            {
                results = results.Where(r => r.Path.EndsWith(fileFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            
            if (results.Count == 0) return new ToolResult($"No matches for pattern: {pattern}");

            var grouped = results.GroupBy(r => r.Path).Take(50);
            
            var output = $"Regex matches for '{pattern}' ({results.Count} found):\n\n" + 
                         string.Join("\n\n", grouped.Select(g => 
                         {
                             var fileName = System.IO.Path.GetFileName(g.Key);
                             var groupItems = g.ToList();
                             var matches = groupItems.Take(3).Select(m => "  " + m.Preview);
                             var moreCount = groupItems.Count > 3 ? $"\n  ... +{groupItems.Count - 3} more in this file" : "";
                             return $"`{fileName}`\n{string.Join("\n", matches)}{moreCount}";
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
