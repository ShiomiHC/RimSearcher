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

    // 单文件最多显示几条预览，避免一个文件把配额吃光。
    // 注意这只限制「显示」：原先命中第 3 条就 break 掉整个文件的扫描，于是该文件剩下的命中
    // 既不出现在预览里、也不进总数，调用方会把「显示了 3 条」读成「只调用了 3 次」。
    private const int MaxMatchesPerFile = 3;

    // 数完整个文件是为了报准每文件命中数，但不能让一个病态大文件（生成代码、拼接的 XML）
    // 把整轮扫描的时间吃光，故仍留一道行数闸；越过它时该文件的计数退化为下界。
    private const int MaxLinesScannedPerFile = 20000;

    private readonly SourceIndexer _sourceIndexer;
    private readonly ScopeCatalog _scopeCatalog;

    public TraceTool(SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__trace";

    public string Description =>
        "Cross-reference analysis for C# and XML. 'inheritors' lists the subclass/implementor tree and expands " +
        "to the server cap by default; 'usages' is a line-by-line regex text match (default 50, at most 3 preview " +
        "lines per file plus a '+N more in this file' count). Usages is not a call graph: same-named members on " +
        "unrelated types land in one list and inherited calls are missed.";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            symbol = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Class or member to trace. Examples: 'ThingComp', 'CompShield', 'TakeDamage'. In mode " +
                    "'usages' it is matched as a case-insensitive whole word, not resolved as a symbol."
            },
            mode = new
            {
                type = "string",
                @enum = new[] { "inheritors", "usages" },
                description =
                    "Trace mode: 'inheritors' for the subclass/implementor tree (interfaces included), which " +
                    "defaults to the server cap so the whole tree comes back; 'usages' for textual references " +
                    "in C# and XML, which defaults to 50 matches. The 'limit' default noted below is the " +
                    "'usages' one."
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

        // 拼错的 scope 被静默退回全域，两个 mode 的每条返回路径都要带上这行，
        // 否则调用方会把全域结果当成自己限定过的范围内结果。
        var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

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
                return new ToolResult(
                    $"No subclasses of '{symbol}' found in scope '{scope.Expression}'."
                    + $"{ScopeArgs.RetryWiderNotice(scope)}{footer ?? string.Empty}{scopeNotice}");
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
            sbInheritors.Append(scopeNotice);

            return new ToolResult(sbInheritors.ToString());
        }
        else
        {
            // 显式 limit 原样生效：原先写成 `limit == 0 ? 50 : Math.Max(limit, 50)`，
            // limit:5 会被抬到 50，limit:'all' 又被压在 50 —— 两个方向都不听调用方的。
            var limit = ScopeArgs.GetDisplayLimit(args, fallback: UsagesDefaultLimit);
            var maxTotalResults = limit.Count;

            var results = new ConcurrentBag<(string file, int lineNum, string preview)>();
            // 每个文件的真实命中数，与 results 里的预览条数是两个量：预览封顶 3 条，计数不封顶。
            var matchesByFile = new ConcurrentDictionary<string, int>();
            // 扫盘类工具不额外统计 scope 外的命中——那要把过滤掉的文件再读一遍，
            // 代价与全域搜索相同，故这里 scope 是硬过滤，只在结果头部标明范围。
            var files = _sourceIndexer.GetAllFiles(scope)
                .Where(f => f.EndsWith(".cs") || f.EndsWith(".xml"))
                .ToList();

            var regex = new System.Text.RegularExpressions.Regex(
                $@"\b{System.Text.RegularExpressions.Regex.Escape(symbol)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

            // collectedCount 是「已占用的预览配额」，totalMatchCount 是「真实命中总数」。
            // 原先只有一个 globalCount 同时充当这两者，表头才会把显示条数当成命中数报出去。
            int collectedCount = 0;
            int totalMatchCount = 0;
            int processedCount = 0;
            int totalFiles = files.Count;
            int truncatedFlag = 0;

            await Parallel.ForEachAsync(files, cancellationToken, async (file, ct) =>
            {
                // 配额用尽后整个文件都不再打开——这才是真正的提前收工，表头因此只能给下界。
                if (Interlocked.CompareExchange(ref collectedCount, 0, 0) >= maxTotalResults)
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
                            matchesInFile++;
                            Interlocked.Increment(ref totalMatchCount);

                            // 已开始读的文件一律读到底再收工：文件句柄和缓冲都已经付过钱了，
                            // 读完才换得「+N more in this file」是准数而不是猜的。
                            if (matchesInFile <= MaxMatchesPerFile)
                            {
                                var slot = Interlocked.Increment(ref collectedCount);
                                if (slot <= maxTotalResults)
                                {
                                    var preview = line.Trim();
                                    if (preview.Length > 100) preview = preview[..97] + "...";
                                    results.Add((file, lineNum, preview));
                                }
                                else
                                {
                                    Interlocked.Exchange(ref truncatedFlag, 1);
                                }
                            }
                        }

                        if (lineNum >= MaxLinesScannedPerFile) break;
                    }

                    if (matchesInFile > 0) matchesByFile[file] = matchesInFile;
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

            // 配额用尽后剩下的文件是从 Parallel 的委托头部直接 return 的，不走 finally 里的
            // 计数，进度于是永远停在半路（实测 limit:5 时停在 1.3%）。扫描已经结束了，
            // 补一次满格，免得调用方那边的进度条挂在原地。
            progress?.Report(1.0);

            // 扫盘分支是硬 scope 过滤、不统计落选来源，故这条提示是它唯一的「别处也许有」的痕迹
            if (results.Count == 0)
                return new ToolResult(
                    $"No references to '{symbol}' found in scope '{scope.Expression}'."
                    + $"{ScopeArgs.RetryWiderNotice(scope)}{scopeNotice}");

            var grouped = results
                .GroupBy(r => r.file)
                .OrderBy(g => g.Key);

            var wasTruncated = Interlocked.CompareExchange(ref truncatedFlag, 0, 0) == 1;
            int totalMatches = Interlocked.CompareExchange(ref totalMatchCount, 0, 0);

            // 未截断时这个数才是真实命中总数——那正是本次修复的目的（原先它是显示条数）。
            // 截断时不能报它：配额一满就不再打开新文件，totalMatches 只反映「恰好扫到了哪些
            // 文件」，随线程调度浮动，同一次查询重跑两遍会给出两个数。与其报一个不稳定的下界，
            // 不如只说确定的量（显示了多少条、扫描在何处停下），把总数留给末尾的提示。
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(wasTruncated
                ? $"References to '{symbol}' (first {results.Count} in scope '{scope.Expression}', scan stopped at the limit):"
                : $"References to '{symbol}' ({totalMatches} found in scope '{scope.Expression}'):");
            sb.AppendLine();

            foreach (var group in grouped)
            {
                var fileTag = group.Key.EndsWith(".xml") ? "[XML]" : "[C#]";
                var fileName = System.IO.Path.GetFileName(group.Key);
                sb.AppendLine($"{fileTag} `{fileName}`{ScopeArgs.Label(scope.ShowLabels ? scope.SourceNameOf(group.Key) : null)}");

                var shown = 0;
                foreach (var match in group.OrderBy(m => m.lineNum))
                {
                    sb.AppendLine($"  L{match.lineNum}: {match.preview}");
                    shown++;
                }

                // 减的是实际显示条数而不是 MaxMatchesPerFile：配额在这个文件中途耗尽时
                // shown 会不足 3，按常数减会少报。文案与 search_regex 保持一致。
                var inFile = matchesByFile.TryGetValue(group.Key, out var c) ? c : shown;
                if (inFile > shown) sb.AppendLine($"  ... +{inFile - shown} more in this file");
            }

            // 判据只看 truncatedFlag。原先还或上 `totalMatches >= maxTotalResults`，
            // 而 totalMatches 现在是真实命中数——单文件折叠出来的命中会误触发这条提示。
            if (wasTruncated)
            {
                // 已经顶到硬上限时别再劝 limit:'all'，那只会原地重试。
                // 文案点明限的是「预览行」：表头的 N+ 是命中数，两个数量不是一回事，
                // 不说清楚就会被读成「找到 N 条却只让看 50 条」的矛盾。
                sb.AppendLine(limit.Unlimited
                    ? $"\n[Preview lines truncated at the server cap of {maxTotalResults} and scanning stopped there, use a more specific symbol or a narrower scope]"
                    : $"\n[Preview lines truncated at limit {maxTotalResults} and scanning stopped there, raise limit (up to {ScopeArgs.HardLimit}) or use limit:'all']");
            }

            // usages 分支是硬 scope 过滤、没有 ScopeReport footer（见上面扫盘的注释），
            // 这行就是它唯一的 scope 级脚注。
            sb.Append(scopeNotice);

            return new ToolResult(sb.ToString());
        }
    }
}
