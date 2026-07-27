using System.Text.Json;
using System.Collections.Concurrent;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class TraceTool : ITool
{
    // 两个 mode 的缺省条数不同：继承树本身就是要看全的，故 inheritors 缺省即展开到硬上限；
    // usages 是扫盘结果、一条命中一行，缺省就给到硬上限会把上下文吃掉，仍按 50 起步。
    // 两者的显式 limit 与 'all' 都走同一套语义（见 ScopeArgs）。
    private const int InheritorsDefaultLimit = ScopeArgs.HardLimit;
    private const int UsagesDefaultLimit = 50;

    // 单文件最多取几条，避免一个文件把配额吃光
    private const int MaxMatchesPerFile = 3;

    private readonly SourceIndexer _sourceIndexer;
    private readonly ScopeCatalog _scopeCatalog;

    public TraceTool(SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__trace";

    public string Description =>
        "Cross-reference analysis for C# and XML. Mode: 'inheritors' (subclasses and interface implementors) " +
        "or 'usages' (file/line references).";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            symbol = new
            {
                type = "string",
                minLength = 1,
                description = "Class or member to trace. Examples: 'ThingComp', 'CompShield', 'TakeDamage'."
            },
            mode = new
            {
                type = "string",
                @enum = new[] { "inheritors", "usages" },
                description =
                    "Trace mode: 'inheritors' for the subclass/implementor tree (interfaces included; lists " +
                    "every match up to the server cap unless limit says otherwise), " +
                    "'usages' for textual references in C# and XML."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeArgs.LimitSchemaProperty(UsagesDefaultLimit)
        },
        required = new[] { "symbol", "mode" }
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__trace",
        "symbol (a class or member, e.g. 'ThingComp') and mode ('inheritors' or 'usages'). Aliases accepted for symbol: query, name, type.",
        "symbol (required), mode (required, 'inheritors' | 'usages'), scope, limit.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var symbol = ToolArgs.StripLocateFilterPrefix(
            ToolArgs.GetRequiredString(args, ArgSpec, "symbol", "query", "name", "type"));
        var mode = ToolArgs.GetRequiredString(args, ArgSpec, "mode", "traceMode", "direction").ToLowerInvariant();

        if (mode is not ("inheritors" or "usages"))
            return new ToolResult($"Unknown mode '{mode}'. Use 'inheritors' (subclass tree) or 'usages' (textual references).", true);

        var scope = ScopeArgs.Resolve(_scopeCatalog, args);

        if (mode == "inheritors")
        {
            var limit = ScopeArgs.GetDisplayLimit(args, fallback: InheritorsDefaultLimit);

            cancellationToken.ThrowIfCancellationRequested();
            var inheritors = _sourceIndexer.GetInheritors(symbol, scope, limit.Count);

            if (inheritors.Items.Count == 0)
            {
                var report = new ScopeReport();
                report.Add(inheritors);
                var footer = report.Render(scope);
                return new ToolResult($"No subclasses of '{symbol}' found in scope '{scope.Expression}'.{footer ?? string.Empty}");
            }

            var results = inheritors.Items.Select(entry =>
            {
                var paths = _sourceIndexer.GetPathsByType(entry.Item);
                return $"- `{entry.Item}` ({string.Join(", ", paths.Select(System.IO.Path.GetFileName))}){ScopeArgs.Label(entry.SourceName)}";
            });

            var sbInheritors = new System.Text.StringBuilder();
            sbInheritors.AppendLine($"Subclasses of '{symbol}' ({inheritors.TotalInScope} in scope '{scope.Expression}'):");
            sbInheritors.AppendLine(string.Join(Environment.NewLine, results));

            var fold = ScopeArgs.FoldLine(inheritors, indent: "", limit: limit);
            if (fold != null) sbInheritors.AppendLine(fold);

            var inheritorsReport = new ScopeReport();
            inheritorsReport.Add(inheritors);
            var inheritorsFooter = inheritorsReport.Render(scope);
            if (inheritorsFooter != null) sbInheritors.Append(inheritorsFooter);

            return new ToolResult(sbInheritors.ToString());
        }
        else
        {
            // 显式 limit 原样生效：原先写成 `limit == 0 ? 50 : Math.Max(limit, 50)`，
            // limit:5 会被抬到 50，limit:'all' 又被压在 50 —— 两个方向都不听调用方的。
            var limit = ScopeArgs.GetDisplayLimit(args, fallback: UsagesDefaultLimit);
            var maxTotalResults = limit.Count;

            var results = new ConcurrentBag<(string file, int lineNum, string preview)>();
            // 扫盘类工具不额外统计 scope 外的命中——那要把过滤掉的文件再读一遍，
            // 代价与全域搜索相同，故这里 scope 是硬过滤，只在结果头部标明范围。
            var files = _sourceIndexer.GetAllFiles(scope)
                .Where(f => f.EndsWith(".cs") || f.EndsWith(".xml"))
                .ToList();

            var regex = new System.Text.RegularExpressions.Regex(
                $@"\b{System.Text.RegularExpressions.Regex.Escape(symbol)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

            int globalCount = 0;
            int processedCount = 0;
            int totalFiles = files.Count;
            int truncatedFlag = 0;

            await Parallel.ForEachAsync(files, cancellationToken, async (file, ct) =>
            {
                if (Interlocked.CompareExchange(ref globalCount, 0, 0) >= maxTotalResults)
                {
                    Interlocked.Exchange(ref truncatedFlag, 1);
                    return;
                }

                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    string? line;
                    int lineNum = 0;
                    int matchesInFile = 0;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        lineNum++;
                        if (regex.IsMatch(line))
                        {
                            var currentCount = Interlocked.Increment(ref globalCount);
                            if (currentCount <= maxTotalResults)
                            {
                                var preview = line.Trim();
                                if (preview.Length > 100) preview = preview[..97] + "...";
                                results.Add((file, lineNum, preview));
                            }
                            matchesInFile++;
                            if (matchesInFile >= MaxMatchesPerFile || currentCount >= maxTotalResults)
                            {
                                if (currentCount >= maxTotalResults)
                                    Interlocked.Exchange(ref truncatedFlag, 1);
                                break;
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    var current = Interlocked.Increment(ref processedCount);
                    if (current % 10 == 0 || current == totalFiles)
                    {
                        progress?.Report((double)current / totalFiles);
                    }
                }
            });

            if (results.Count == 0)
                return new ToolResult($"No references to '{symbol}' found in scope '{scope.Expression}'.");

            var grouped = results
                .GroupBy(r => r.file)
                .OrderBy(g => g.Key);

            int totalMatches = results.Count;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"References to '{symbol}' ({totalMatches} found in scope '{scope.Expression}'):");
            sb.AppendLine();

            foreach (var group in grouped)
            {
                var fileTag = group.Key.EndsWith(".xml") ? "[XML]" : "[C#]";
                var fileName = System.IO.Path.GetFileName(group.Key);
                sb.AppendLine($"{fileTag} `{fileName}`{ScopeArgs.Label(scope.ShowLabels ? scope.SourceNameOf(group.Key) : null)}");
                foreach (var match in group.OrderBy(m => m.lineNum))
                {
                    sb.AppendLine($"  L{match.lineNum}: {match.preview}");
                }
            }

            var wasTruncated = Interlocked.CompareExchange(ref truncatedFlag, 0, 0) == 1;
            if (wasTruncated || totalMatches >= maxTotalResults)
            {
                // 已经顶到硬上限时别再劝 limit:'all'，那只会原地重试
                sb.AppendLine(limit.Unlimited
                    ? $"\n[Results truncated at the server cap of {maxTotalResults}, use a more specific symbol or a narrower scope]"
                    : $"\n[Results truncated at limit {maxTotalResults}, raise limit (up to {ScopeArgs.HardLimit}) or use limit:'all']");
            }

            return new ToolResult(sb.ToString());
        }
    }
}
