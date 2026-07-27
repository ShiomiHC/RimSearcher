using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class ReadCodeTool : ITool
{
    // 裸行读取的缺省与硬上限。schema 里的 maximum 只是给 client 的提示，client 照样能传
    // lineCount:100000，所以夹紧必须在服务端做一次（见下面的 Math.Min）。
    private const int DefaultLineCount = 150;
    private const int MaxLineCount = 2000;

    private readonly SourceIndexer _sourceIndexer;
    private readonly ScopeCatalog _scopeCatalog;

    public ReadCodeTool(SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__read_code";

    // 三种模式的优先级此前只写在下面几个 if 的先后顺序里，调用方同时传 extractClass 和
    // methodName 时无从知道哪个生效，只能从返回内容倒推。契约就得写在 description 里。
    public string Description =>
        "Read C# or XML source out of one specific file — an indexed file name or an absolute path, not a search term. "
        + "Three exclusive modes: extractClass (the whole type), methodName (one member), or startLine/lineCount "
        + "(raw lines). If more than one is passed, extractClass wins over methodName, which wins over the line range. "
        + "The first two parse C#; on an XML file only the line range applies.";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__read_code",
        "path (a FILE: 'CompShield', 'CompShield.cs' or an absolute path). Aliases accepted: query, file, filePath, fileName.",
        "path (required), methodName, className, extractClass, startLine, lineCount.",
        "If you only have a search term rather than a file, call rimworld-searcher__locate first.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", minLength = 1, description = "File path or indexed file name. Examples: 'CompShield','CompShield.cs','/abs/path/CompShield.cs'. Aliases 'query'/'file'/'filePath' are also accepted." },
            methodName = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Member to extract: method ('CompTick'), property ('Label'), field or event ('energy'), "
                    + "constructor (class name or '.ctor'), indexer ('this'), operator ('+') or enum value "
                    + "('Resetting') — anything locate lists as a member. Every member of that name in the file is "
                    + "returned — pass className to get just one."
            },
            className = new
            {
                type = "string",
                minLength = 1,
                description = "Optional: The class name to resolve ambiguity if multiple classes have the same member name."
            },
            extractClass = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Optional: Extract the entire class/struct/interface/record body by name — an enum or delegate "
                    + "declaration works too. Example: 'CompShield'."
            },
            startLine = new
            {
                type = "integer",
                minimum = 0,
                @default = 0,
                description = "Optional 0-based start line for raw read mode (used when methodName/extractClass is not set)."
            },
            lineCount = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLineCount,
                @default = DefaultLineCount,
                description =
                    $"Optional number of lines for raw read mode. Default is {DefaultLineCount}, " +
                    $"values above {MaxLineCount} are clamped to it."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog)
        },
        required = new[] { "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var path = ToolArgs.GetRequiredString(args, ArgSpec, "path", "query", "file", "filePath", "fileName");
        path = ToolArgs.StripLocateFilterPrefix(path);

        var scope = ScopeArgs.Resolve(_scopeCatalog, args);

        var requestedPath = path;
        var resolvedPath = ResolvePath(
            path, scope, out var outOfScopeFallback, out var blockedByPathSecurity, out var rootedInputRedirected);

        // 「越权」与「不存在」必须分开说。文件明明在磁盘上、只是不在白名单里时回一句
        // 「File not found，去 locate 找找」，调用方会照做，而 locate 同样不会返回一个
        // 不在索引根下的文件——它只能反复试。list_directory 对同一情况说的就是越权。
        if (blockedByPathSecurity)
            return WithUnresolvedScopeNotice(scope, new ToolResult(
                $"Path outside allowed directories: '{path}'. Only files under the server's indexed source roots "
                + "can be read; use 'locate' to find the indexed copy of what you are after.", true));

        if (resolvedPath == null)
            return WithUnresolvedScopeNotice(scope, new ToolResult(
                $"File not found: '{Path.GetFileName(path)}'. Use 'locate' to find the correct file first.", true));

        path = resolvedPath;

        // 按名解析到了 scope 之外的文件时必须说明，否则读者会以为读的是 scope 内那一份
        var scopeNotice = outOfScopeFallback
            ? Comment(path, $"note: no file by this name inside scope '{scope.Expression}'; reading from {scope.OutOfScopeLabel(path)}") + "\n"
            : string.Empty;

        if (rootedInputRedirected)
            scopeNotice =
                Comment(path, $"note: '{requestedPath}' does not exist; reading the indexed file of the same name at {path}")
                + "\n" + scopeNotice;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var extractClass = ToolArgs.GetOptionalString(args, "extractClass", "class");
            if (!string.IsNullOrEmpty(extractClass))
            {
                var extractClassName = ToolArgs.StripLocateFilterPrefix(extractClass);
                var classBody = await RoslynHelper.GetClassBodyAsync(path, extractClassName);

                // 按状态判断，不看正文内容。原先是 classBody.Contains("not found")——
                // 反编译产物里 Log.Error("... not found") 这类字面量遍地都是，取到的正文
                // 一旦含这段文本就会被误报成「类不存在」，而代码明明就在那里。
                if (!classBody.IsOk)
                    return WithUnresolvedScopeNotice(scope,
                        Failure(classBody, path, $"Class '{extractClassName}'", "Use inspect tool to verify the type name."));

                // 与 member 模式对称地回显目标名。少了这行，同一个文件里连开几个 extractClass
                // 的返回长得一模一样，读者只能靠正文首行去认这是哪个类。
                return WithUnresolvedScopeNotice(scope,
                    new ToolResult($"```{Fence(path)}\n{scopeNotice}{Comment(path, extractClassName)}\n{classBody.Content}\n```"));
            }

            var member = ToolArgs.GetOptionalString(args, "methodName", "method", "member", "memberName");
            if (!string.IsNullOrEmpty(member))
            {
                var methodName = ToolArgs.StripLocateFilterPrefix(member);
                var className = ToolArgs.GetOptionalString(args, "className", "type", "typeName");
                var body = await RoslynHelper.GetMemberBodyAsync(path, methodName, className);

                if (!body.IsOk)
                    return WithUnresolvedScopeNotice(scope,
                        Failure(body, path, $"Member '{methodName}'", "Use inspect tool to see available members."));

                return WithUnresolvedScopeNotice(scope,
                    new ToolResult($"```{Fence(path)}\n{scopeNotice}{Comment(path, methodName)}\n{body.Content}\n```"));
            }

            int startLine = Math.Max(0, ToolArgs.GetInt(args, 0, "startLine", "start", "offset"));
            int lineCount = ToolArgs.GetInt(args, DefaultLineCount, "lineCount", "lines", "count", "limit", "maxResults");
            if (lineCount <= 0)
                return WithUnresolvedScopeNotice(scope, new ToolResult("lineCount must be greater than 0.", true));
            lineCount = Math.Min(lineCount, MaxLineCount);

            var allLines = File.ReadAllLines(path);
            int totalLines = allLines.Length;

            var resultLines = allLines.Skip(startLine).Take(lineCount).Select((line, idx) => $"L{startLine + idx + 1}: {line}").ToList();

            if (resultLines.Count == 0)
                return WithUnresolvedScopeNotice(scope, new ToolResult(
                    $"Line range {startLine + 1}-{startLine + lineCount} exceeds file length ({totalLines} lines).", true));

            var sb = new StringBuilder();
            sb.AppendLine($"```{Fence(path)}");
            if (scopeNotice.Length > 0) sb.Append(scopeNotice);
            sb.AppendLine(Comment(path,
                $"{Path.GetFileName(path)} (lines {startLine + 1}-{Math.Min(startLine + lineCount, totalLines)} of {totalLines})"));
            foreach (var line in resultLines) sb.AppendLine(line);
            sb.AppendLine("```");

            if (startLine + lineCount < totalLines)
            {
                sb.AppendLine($"\n[{totalLines - (startLine + lineCount)} more lines available, use startLine={startLine + lineCount}]");
            }

            return WithUnresolvedScopeNotice(scope, new ToolResult(sb.ToString()));
        }
        catch (Exception ex)
        {
            return WithUnresolvedScopeNotice(scope, new ToolResult($"Read failed: {ex.Message}", true));
        }
    }

    // 围栏的语言标记按扩展名选：raw 模式最常见的用途之一就是读 Defs 的 XML，一律标 csharp
    // 会让阅读端按 C# 高亮一份 XML。头部注释必须跟着换语法——`// ...` 留在 xml 块里就是一行
    // 非法内容，整块复制出去直接解析失败。
    private static bool IsXml(string path)
        => Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    private static string Fence(string path) => IsXml(path) ? "xml" : "csharp";

    private static string Comment(string path, string text) => IsXml(path) ? $"<!-- {text} -->" : $"// {text}";

    // 拼错的 scope 被 ScopeCatalog 静默退回全域，返回里不说一句，调用方就会以为自己限定过范围。
    // 一律追加在正文最末尾：正文通常是 ```csharp 代码块，提示混进块里就成了源码的一部分。
    private ToolResult WithUnresolvedScopeNotice(ScopeSelection scope, ToolResult result)
    {
        var notice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope);
        return notice == null ? result : result with { Content = result.Content + notice };
    }

    // 三种失败原因给三种不同的下一步：文件没了要重查、文件过大要改用裸行读、目标不存在才该去 inspect。
    // 原先它们都被折叠成一句「not found」，读者据此断言「类不存在」，而真相可能是文件被重新同步掉了。
    private static ToolResult Failure(SourceLookupResult result, string path, string target, string notFoundHint)
    {
        var fileName = Path.GetFileName(path);

        var message = result.Status switch
        {
            SourceLookupStatus.FileNotFound =>
                $"File disappeared while reading: '{fileName}'. Sources may have just been re-synced — call locate again.",
            SourceLookupStatus.FileTooLarge =>
                $"'{fileName}' is larger than {RoslynHelper.MaxParseFileSize / (1024 * 1024)} MB, so it is not parsed. " +
                "Read it with startLine/lineCount instead, or narrow down with search_regex.",
            _ => $"{target} not found in {fileName}. {notFoundHint}"
        };

        return new ToolResult(message, true);
    }

    private string? ResolvePath(
        string input, ScopeSelection scope,
        out bool outOfScopeFallback, out bool blockedByPathSecurity, out bool rootedInputRedirected)
    {
        outOfScopeFallback = false;
        blockedByPathSecurity = false;
        rootedInputRedirected = false;

        // 绝对路径是调用方自己给的，不受 scope 约束——它已经知道要读哪个文件了
        if (Path.IsPathRooted(input) && File.Exists(input))
        {
            if (PathSecurity.IsPathSafe(input)) return input;

            // 文件确实存在、只是不在白名单内。按名再查一遍索引没有意义（调用方给的是
            // 一条完整绝对路径），直接把真实原因回上去。
            blockedByPathSecurity = true;
            return null;
        }

        // 绝对路径打错时下面仍会按文件名去索引里另找一份同名文件，读的就不是调用方点名的
        // 那条路径了。这一点必须回上去说：返回里的头部注释打印的是解析后的文件名，
        // 光看返回没有任何线索表明发生过替换。
        var rootedButMissing = Path.IsPathRooted(input);

        var nameNoExt = Path.GetFileNameWithoutExtension(input);
        var indexPath = _sourceIndexer.GetPath(nameNoExt, scope, out outOfScopeFallback);
        if (indexPath != null && File.Exists(indexPath))
        {
            rootedInputRedirected = rootedButMissing;
            return indexPath;
        }

        var rawName = Path.GetFileName(input);
        if (rawName != nameNoExt)
        {
            indexPath = _sourceIndexer.GetPath(rawName, scope, out outOfScopeFallback);
            if (indexPath != null && File.Exists(indexPath))
            {
                rootedInputRedirected = rootedButMissing;
                return indexPath;
            }
        }

        outOfScopeFallback = false;
        return null;
    }
}
