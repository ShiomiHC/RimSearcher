using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class ListDirectoryTool : ITool
{
    private readonly ScopeCatalog? _scopeCatalog;

    // catalog 可选：这个工具本身不吃 scope，要它只为把「根」与「源」的关系说清（见 RootsSentence）。
    // 测试里大量以无参形式构造，故不改成必需参数。
    public ListDirectoryTool(ScopeCatalog? scopeCatalog = null) => _scopeCatalog = scopeCatalog;

    public string Name => "rimworld-searcher__list_directory";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "directory", "dir", "maxResults", "count", "skip", "start"];

    // 「allowed path」原先只写了**规则**（「已索引源根及其下级」），一个具体路径都没有，
    // 而调用方读不到 config.toml——那次本想省掉的越界失败照样要撞。同一份 tools/list 里
    // locate 的 scope 描述早就把真实源名注了进去，这套注入机制是现成的。
    public string Description =>
        "List the files and subdirectories of one absolute directory; subdirectory names are suffixed with '/'. "
        + "Entries come back sorted — subdirectories first, then files, each by name — so a truncated listing is "
        + "the alphabetical head of the directory, and `offset` pages through the rest. "
        + "The path must be one of the server's indexed source roots or a directory below one; anything outside "
        + "that whitelist is refused, the parent of a source root included. " + RootsSentence(_scopeCatalog);

    // 白名单在启动时按 config 解析定型，故可以直接读进说明书。skip_path_security 关掉检查时
    // 说「没有限制」，否则调用方会照着一份并不生效的清单去自我设限。
    private static string RootsSentence(ScopeCatalog? catalog)
    {
        if (!PathSecurity.Enabled)
            return "Path security is off (skip_path_security in config.toml), so any absolute directory is listable.";

        var roots = PathSecurity.Roots;
        if (roots.Count == 0) return "No source root is configured, so every path is refused.";

        var shown = string.Join(", ", roots.Take(8));

        // 露出来的前 8 个根形如 `Decompiled\<源名>`，逐一对应 scope 里的前 8 个源名——于是
        // 「根 ≈ 源」被坐实，87 个根读成 87 个源，而 scope 只枚举 11 个。第九轮盲测里两条链
        // 各自撞上这道粒度差：一条据此写下「scope:'all' 可能有盲区」的假 unanswerable，
        // 一条为反查覆盖面多跑一整轮。真值（PathSecurity 与 ScopeCatalog 是同一份 resolvedSources
        // 的两个投影：前者取路径、后者取名字）只写在 ScopeCatalog 的注释里，注释不进输出。
        var attribution = catalog is { HasSources: true }
            ? $" These {roots.Count} roots are the indexed folders of the {catalog.Sources.Count} configured "
              + "sources listed under 'scope' — one source usually spans several roots, so this count is not "
              + "a source count."
            : string.Empty;

        return $"The roots on this server: {shown}"
            + (roots.Count > 8 ? $", and {roots.Count - 8} more." : ".")
            + attribution;
    }

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
            },
            offset = new
            {
                type = "integer",
                minimum = 0,
                @default = 0,
                description =
                    "How many entries to skip, for paging past the server cap. Entries are sorted (subdirectories "
                    + "first, then files, each by name), so paging is stable. The output prints the total entry "
                    + $"count and the offset to pass next; without this a directory of more than {MaxEntries} "
                    + "entries could not be enumerated at all.",
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
        "path (required), limit (default 100), offset (page past the server cap).");

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var path = ToolArgs.GetRequiredString(args, ArgSpec, "path", "query", "directory", "dir");

        // limit<=0 与本服务器其余工具同义：不是「要 0 条」，而是「别截断」，故放到上限。
        // 夹到 1 曾经也算「夹住了下界」，但结果是一条目录项加一句「还有更多」，读起来像
        // 这个目录几乎是空的——而调用方的本意恰恰是要全部。
        var requested = ToolArgs.GetInt(args, 100, "limit", "maxResults", "count");
        int limit = requested <= 0 ? MaxEntries : Math.Min(requested, MaxEntries);
        int offset = Math.Max(0, ToolArgs.GetInt(args, 0, "offset", "skip", "start"));

        cancellationToken.ThrowIfCancellationRequested();

        // 三条失败路径都回显收到的 path。不带它时，同一句 "Directory not found." 对应不到是哪次
        // 调用出的错，而 read_code 的同类返回一直是带路径的。
        if (!PathSecurity.IsPathSafe(path))
        {
            // 相对路径会先被解析成相对于服务进程工作目录的一条路径，再判越界，于是拼错路径与
            // 「忘了写成绝对路径」都收敛到同一句越界提示上。后者其实是参数格式问题。
            var hint = Path.IsPathRooted(path)
                ? "Only the server's indexed source roots and directories below them can be listed. " + RootsSentence(_scopeCatalog)
                : "This path is not absolute; list_directory takes an absolute directory path. " + RootsSentence(_scopeCatalog);
            return Task.FromResult(new ToolResult($"Path outside allowed directories: '{path}'. {hint}", true));
        }

        if (!Directory.Exists(path))
        {
            // 指到一个确实存在的文件时，「目录不存在」会被读成「这个文件也不在」——而它就在那儿，
            // 只是该换个工具读。
            if (File.Exists(path))
                return Task.FromResult(new ToolResult(
                    $"'{path}' is a file, not a directory. Read it with read_code, "
                    + "or list the directory that contains it.", true));

            return Task.FromResult(new ToolResult($"Directory not found: '{path}'.", true));
        }

        try
        {
            // 排序必须在截断之前。原先直接吃 EnumerateFileSystemEntries 的产出顺序（文件系统
            // 顺序，目录与文件混排），于是 1755 项的目录默认只给出其中任意 100 条——调用方
            // 既无法据此断言「某文件不在这个目录」，也无法预判把 limit 调大会补上哪些。
            // 排序后截断的语义变成「按名序的前 N 个」，缺席才是可推理的；子目录归拢在前，
            // 顺带让「先看有哪些子目录再下钻」这个最常见意图不必整段扫读。
            var all = Directory.EnumerateFileSystemEntries(path)
                .Select(e => (Name: Path.GetFileName(e), IsDir: Directory.Exists(e)))
                .OrderBy(e => e.IsDir ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var page = all.Skip(offset).Take(limit).ToList();
            var shownThrough = offset + page.Count;

            // 单复数走全服共用的构词（README「低 Token 消耗」：不写 `1 entries`）。只有一项的
            // 目录并不罕见——Mods 下一个只放 About/ 的壳目录就是。
            var count = OutputText.Quantity(all.Count, "entries");

            var result = $"`{path}` ({count}"
                + (offset > 0 ? $", showing {offset + 1}-{shownThrough}" : shownThrough < all.Count ? $", showing the first {page.Count}" : "")
                + ")\n"
                + string.Join("\n", page.Select(e => e.Name + (e.IsDir ? "/" : "")));

            if (page.Count == 0)
                result = $"`{path}` ({count}) — offset {offset} is past the end.";

            // 「list a deeper subdirectory / use search_regex」两条旧出路对触发上限的目录都是
            // 死路：被略去的多半正是顶层文件（不在任何子目录里），而 search_regex 匹配的是
            // 文件正文行、fileFilter 只是路径后缀，写不出「限定在这个目录下」。offset 才是
            // 真的能把 >1000 项的目录枚举完的那条路。
            // 文法与全服统一的截断脚注一致：`... +N more <什么> (<怎么拿到>)`，见 ScopeArgs.FoldLine。
            else if (shownThrough < all.Count)
                result += $"\n... +{all.Count - shownThrough} more entries (pass offset={shownThrough} for the next page"
                    + (limit >= MaxEntries ? $"; {MaxEntries} is the server cap per page)" : ", or a larger limit)");

            return Task.FromResult(new ToolResult(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Failed to list directory: {ex.Message}", true));
        }
    }
}
