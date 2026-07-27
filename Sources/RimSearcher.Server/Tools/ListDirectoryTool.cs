using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class ListDirectoryTool : ITool
{
    public string Name => "rimworld-searcher__list_directory";

    // 「allowed path」原先没有定义，调用方只能先撞一次 "Path outside allowed directories." 才知道
    // 白名单是什么。把范围写进 description，那一次越界失败就省了。
    public string Description =>
        "List the files and subdirectories of one absolute directory; subdirectory names are suffixed with '/'. "
        + "The path must be one of the server's indexed source roots (the csharp/xml paths a config.toml source "
        + "resolves to, including the decompile output directory it gets when csharp is omitted) or a directory "
        + "below one — anything outside that whitelist is refused, the parent of a source root included.";

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
                // 不声明 minimum：服务端认的是 `<= 0`（与 ScopeArgs 的同类参数一致），
                // 声明 minimum=0 会让照着描述传 -1 的调用在 client 侧就被校验挡下。
                maximum = 1000,
                description =
                    "Maximum entries to return (default 100, server cap 1000). 0 or a negative value means "
                    + "no cap below that server cap. If entries are left out, the output says so.",
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

        // limit<=0 与本服务器其余工具同义：不是「要 0 条」，而是「别截断」，故放到上限。
        // 夹到 1 曾经也算「夹住了下界」，但结果是一条目录项加一句「还有更多」，读起来像
        // 这个目录几乎是空的——而调用方的本意恰恰是要全部。
        var requested = ToolArgs.GetInt(args, 100, "limit", "maxResults", "count");
        int limit = requested <= 0 ? MaxEntries : Math.Min(requested, MaxEntries);

        cancellationToken.ThrowIfCancellationRequested();

        if (!PathSecurity.IsPathSafe(path)) return Task.FromResult(new ToolResult("Path outside allowed directories.", true));
        if (!Directory.Exists(path)) return Task.FromResult(new ToolResult("Directory not found.", true));

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Take(limit + 1)
                .ToList();

            var hasMore = entries.Count > limit;
            var atServerCap = limit >= MaxEntries;
            var displayedEntries = entries.Take(limit)
                .Select(e => Path.GetFileName(e) + (Directory.Exists(e) ? "/" : ""));

            var result = $"`{path}`\n" + string.Join("\n", displayedEntries);
            // 顶到服务端上限时「increase limit」是一条死路——limit 已经无法再高。
            // 这一支现在很容易走到：limit<=0 直接就把上限用满。
            if (hasMore)
            {
                result += atServerCap
                    ? $"\n... [more entries available; {MaxEntries} is the server cap — list a deeper subdirectory, "
                      + "or use search_regex to filter this one by name/extension]"
                    : "\n... [more entries available, increase limit]";
            }
            return Task.FromResult(new ToolResult(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Failed to list directory: {ex.Message}", true));
        }
    }
}
