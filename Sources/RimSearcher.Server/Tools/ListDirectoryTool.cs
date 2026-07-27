using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class ListDirectoryTool : ITool
{
    public string Name => "rimworld-searcher__list_directory";

    public string Description =>
        "List files/subdirectories under an absolute allowed path. Directory names are suffixed with '/'.";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                minLength = 1,
                description = "Absolute directory path to inspect. Example: '/path/to/RimWorld/Source/Core/Defs'."
            },
            limit = new
            {
                type = "integer",
                minimum = 1,
                maximum = 1000,
                description = "Maximum entries to return. If exceeded, output includes a 'more entries available' hint.",
                @default = 100
            }
        },
        required = new[] { "path" }
    };

    // schema 里的 maximum 只是给 client 的提示、不是约束——client 照样能传 999999，
    // 真正的夹紧必须发生在服务端。这里执行的是 schema 自己声明的那个 1000，而不是
    // ScopeArgs.HardLimit（200）：那个数是按「一行一条结果、预览行截 100 字符」算出来的
    // 体积天花板，而目录项一行只有一个文件名，1000 条与之同量级。
    private const int MaxEntries = 1000;

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__list_directory",
        "path (an absolute directory path). Aliases accepted: query, directory, dir.",
        "path (required), limit (default 100).");

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var path = ToolArgs.GetRequiredString(args, ArgSpec, "path", "query", "directory", "dir");
        // 下界一并夹住：limit<=0 原先会走成 Take(1)，回一条目录项外加一句「还有更多」，
        // 看着像目录几乎是空的。
        int limit = Math.Clamp(ToolArgs.GetInt(args, 100, "limit", "maxResults", "count"), 1, MaxEntries);

        cancellationToken.ThrowIfCancellationRequested();

        if (!PathSecurity.IsPathSafe(path)) return Task.FromResult(new ToolResult("Path outside allowed directories.", true));
        if (!Directory.Exists(path)) return Task.FromResult(new ToolResult("Directory not found.", true));

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Take(limit + 1)
                .ToList();

            var hasMore = entries.Count > limit;
            var displayedEntries = entries.Take(limit)
                .Select(e => Path.GetFileName(e) + (Directory.Exists(e) ? "/" : ""));

            var result = $"`{path}`\n" + string.Join("\n", displayedEntries);
            if (hasMore)
            {
                result += $"\n... [more entries available, increase limit]";
            }
            return Task.FromResult(new ToolResult(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Failed to list directory: {ex.Message}", true));
        }
    }
}
