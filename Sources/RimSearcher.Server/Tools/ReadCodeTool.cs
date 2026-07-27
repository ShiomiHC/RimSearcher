using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class ReadCodeTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;

    public ReadCodeTool(SourceIndexer sourceIndexer)
    {
        _sourceIndexer = sourceIndexer;
    }

    public string Name => "rimworld-searcher__read_code";

    public string Description =>
        "Read C# source by method/property/constructor, class body, or raw line range.";

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
                    "Member to extract: method ('CompTick'), property ('Label'), constructor (class name or '.ctor'), indexer ('this'), or operator ('+')."
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
                description = "Optional: Extract the entire class/struct/interface body by name. Example: 'CompShield'."
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
                maximum = 2000,
                @default = 150,
                description = "Optional number of lines for raw read mode. Default is 150."
            }
        },
        required = new[] { "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var path = ToolArgs.GetRequiredString(args, ArgSpec, "path", "query", "file", "filePath", "fileName");
        path = ToolArgs.StripLocateFilterPrefix(path);

        var resolvedPath = ResolvePath(path);
        if (resolvedPath == null)
            return new ToolResult($"File not found: '{Path.GetFileName(path)}'. Use 'locate' to find the correct file first.", true);

        path = resolvedPath;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var extractClass = ToolArgs.GetOptionalString(args, "extractClass", "class");
            if (!string.IsNullOrEmpty(extractClass))
            {
                var extractClassName = ToolArgs.StripLocateFilterPrefix(extractClass);
                var classBody = await RoslynHelper.GetClassBodyAsync(path, extractClassName);
                if (string.IsNullOrEmpty(classBody) || classBody.Contains("not found"))
                    return new ToolResult($"Class '{extractClassName}' not found in {Path.GetFileName(path)}. Use inspect tool to verify.", true);
                return new ToolResult($"```csharp\n{classBody}\n```");
            }

            var member = ToolArgs.GetOptionalString(args, "methodName", "method", "member", "memberName");
            if (!string.IsNullOrEmpty(member))
            {
                var methodName = ToolArgs.StripLocateFilterPrefix(member);
                var className = ToolArgs.GetOptionalString(args, "className", "type", "typeName");
                var body = await RoslynHelper.GetMemberBodyAsync(path, methodName, className);
                if (string.IsNullOrEmpty(body) || body.Contains("not found"))
                {
                    return new ToolResult(
                        $"Member '{methodName}' not found in {Path.GetFileName(path)}. Use inspect tool to see available members.",
                        true);
                }

                return new ToolResult($"```csharp\n// {methodName}\n{body}\n```");
            }

            int startLine = Math.Max(0, ToolArgs.GetInt(args, 0, "startLine", "start", "offset"));
            int lineCount = ToolArgs.GetInt(args, 150, "lineCount", "lines", "count", "limit", "maxResults");
            if (lineCount <= 0)
                return new ToolResult("lineCount must be greater than 0.", true);

            var allLines = File.ReadAllLines(path);
            int totalLines = allLines.Length;

            var resultLines = allLines.Skip(startLine).Take(lineCount).Select((line, idx) => $"L{startLine + idx + 1}: {line}").ToList();

            if (resultLines.Count == 0)
                return new ToolResult($"Line range {startLine + 1}-{startLine + lineCount} exceeds file length ({totalLines} lines).", true);

            var sb = new StringBuilder();
            sb.AppendLine($"```csharp");
            sb.AppendLine($"// {Path.GetFileName(path)} (lines {startLine + 1}-{Math.Min(startLine + lineCount, totalLines)} of {totalLines})");
            foreach (var line in resultLines) sb.AppendLine(line);
            sb.AppendLine("```");

            if (startLine + lineCount < totalLines)
            {
                sb.AppendLine($"\n[{totalLines - (startLine + lineCount)} more lines available, use startLine={startLine + lineCount}]");
            }

            return new ToolResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return new ToolResult($"Read failed: {ex.Message}", true);
        }
    }

    private string? ResolvePath(string input)
    {
        if (Path.IsPathRooted(input) && File.Exists(input) && PathSecurity.IsPathSafe(input))
            return input;

        var nameNoExt = Path.GetFileNameWithoutExtension(input);
        var indexPath = _sourceIndexer.GetPath(nameNoExt);
        if (indexPath != null && File.Exists(indexPath))
            return indexPath;

        var rawName = Path.GetFileName(input);
        if (rawName != nameNoExt)
        {
            indexPath = _sourceIndexer.GetPath(rawName);
            if (indexPath != null && File.Exists(indexPath))
                return indexPath;
        }

        return null;
    }
}
