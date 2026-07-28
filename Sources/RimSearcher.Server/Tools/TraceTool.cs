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

    // 分块推进的块大小。块内仍是满盘并发，只是每块整块扫完才判配额——代价是配额满的那一刻
    // 最多多扫一块，换来的是「扫过的恒是文件表的一个前缀」。与 search_regex 取同一个数。
    private const int ScanChunkFiles = 256;

    private readonly SourceIndexer _sourceIndexer;
    private readonly ScopeCatalog _scopeCatalog;

    public TraceTool(SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__trace";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "name", "typeName", "symbolName", "traceMode", "direction", "maxResults", "scopes", "source", "sources", "mod", "mods", "in"];

    public string Description =>
        "Cross-reference analysis for C# and XML. 'inheritors' lists the transitive subclass/implementor tree — " +
        "every descendant, not just direct ones, each tagged with its depth — and expands to the server cap by " +
        "default; 'usages' is a line-by-line regex text match (default 50, at most 3 preview lines per file plus " +
        "a '+N more matching lines in this file' count — counts are matching lines, not match sites, so a line " +
        "hit twice counts once). Usages is not a call graph: same-named members on unrelated types land in one " +
        "list and inherited calls are missed.";

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
                    "Trace mode: 'inheritors' for the transitive subclass/implementor tree (interfaces included; " +
                    "indirect descendants are listed too, each tagged 'direct' or 'depth N'), which " +
                    "defaults to the server cap so the whole tree comes back; 'usages' for textual references " +
                    "in C# and XML, which defaults to 50 matches. The 'limit' default noted below is the " +
                    "'usages' one."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            // trace 两种模式都不是模糊搜索：inheritors 的候选分数恒为 100（继承关系是精确的，
            // ScopeFilter 那里 scoreGap 传的就是 null），usages 是逐行全词文本匹配。
            // 照抄模糊工具的文案会让调用方以为「剩下的是低相关度、调多大 limit 都拿不回来」，
            // 从而把一份被 limit 截断的引用清单当成完整结论。
            limit = ScopeArgs.LimitSchemaProperty(UsagesDefaultLimit, fuzzy: false)
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
            ToolArgs.GetRequiredFuzzyString(args, ArgSpec, "symbol", "query", "name", "type"));
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
            var inheritors = _sourceIndexer.GetInheritors(symbol, scope, limit.Count, out var depths);

            if (inheritors.Items.Count == 0)
            {
                var report = new ScopeReport();
                report.Add(inheritors);
                var footer = report.Render(scope);

                // 「索引里没有这个类型」和「有，但没人继承它」是两件事，下一步也完全不同：
                // 前者要去确认名字（多半拼错了或不在配置的源里），后者已经是答案。
                // 原先两者同一句话，调用方读到的都是「没有子类」，于是拿着一个根本不存在的
                // 名字继续往下查。
                // 「这是答案」这句背书只在**真的是完整答案**时给。scope 外还有派生类时，
                // 它下面跟的是一行小字斜体的 out-of-scope 计数——盲测里调用方把整份返回压缩成
                // 了「没有子类」，而那个被丢掉的 1 足以让「可以安全改签名」这类结论翻车：
                // 语气最重的那句和唯一的反证放在一起，读者只会记住前者。
                var known = _sourceIndexer.IsKnownType(symbol);
                var message = known
                    ? footer != null
                        ? $"'{symbol}' is indexed, and nothing in scope '{scope.Expression}' derives from it — "
                          + "but it does have subclasses outside that scope, so this is not the whole answer."
                        : $"'{symbol}' is indexed, and nothing in scope '{scope.Expression}' derives from it "
                          + "(this is an answer, not a lookup failure)."
                    : $"No type named '{symbol}' is in the index, so this is not evidence that it has no "
                      + "subclasses. Check the spelling with rimworld-searcher__locate, and note that "
                      + "inheritors resolves C# type names only.";

                return new ToolResult(
                    $"{message}{ScopeArgs.RetryWiderNotice(scope, footer != null)}{footer ?? string.Empty}{scopeNotice}");
            }

            // 列出来的类型全同源时标签只印一次（见 ScopeArgs.SourceLabeling）
            var inheritorLabels = ScopeArgs.SourceLabeling.Of(inheritors.Items.Select(e => e.SourceName));

            var results = inheritors.Items.Select(entry =>
            {
                var paths = _sourceIndexer.GetPathsByType(entry.Item);
                // 深度必须逐条标出来：树是拍平成一列返回的，不标就分不出「直接子类」
                // 和「曾孙」，而这两者在判断「要覆写哪个方法」时含义完全不同。
                // 但**只标非直接的**：直接子类占绝大多数（本转储 601 行全是），
                // 每行挂一个 `[direct]` 是把表头已经说过的话再说 601 遍。
                // 表头在有深层项时会点明「无标记 = 直接子类」。
                var depth = depths.TryGetValue(entry.Item, out var d) ? d : 1;
                var depthLabel = depth == 1 ? "" : $" [depth {depth}]";
                return $"- `{entry.Item}`{depthLabel}{SymbolRow.FileNote(entry.Item, paths)}{inheritorLabels.Row(entry.SourceName)}";
            });

            // 这两个数只描述**列出来的这些条目**，不描述整棵树：Items 是截断后的展示切片，
            // 而 depths 覆盖的是 scope 过滤之前的全集。拿任何一边去当另一边的统计量，
            // 都会造出一个「看起来像结论」的假数字——正是本轮要清除的那类输出。
            var shownDirect = inheritors.Items.Count(e => !depths.TryGetValue(e.Item, out var d) || d == 1);
            var shownDeepest = inheritors.Items
                .Select(e => depths.TryGetValue(e.Item, out var d) ? d : 1)
                .DefaultIfEmpty(1).Max();

            var sbInheritors = new System.Text.StringBuilder();
            // 深度标记的约定只在真的出现深层项时才需要说明；全是直接子类时一个标记都不印，
            // 表头的 "deepest 1 level down" 已经把这件事说完了。
            var depthLegend = shownDeepest > 1 ? ", untagged = direct" : "";
            sbInheritors.AppendLine(
                $"Subclasses of '{symbol}' ({inheritors.TotalInScope} in scope '{scope.Expression}', transitive — "
                + $"indirect descendants included). Listed below: {inheritors.Items.Count} "
                + $"({shownDirect} direct, deepest {OutputText.Quantity(shownDeepest, "levels")} "
                + $"down{depthLegend}){inheritorLabels.Header}:");
            sbInheritors.AppendLine(string.Join(Environment.NewLine, results));

            var fold = ScopeArgs.FoldLine(inheritors, "subclasses", indent: "", limit: limit);
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

            // 每条命中带上它所属文件在 files 里的序号：截断与排序都靠它，才与线程调度无关。
            var results = new ConcurrentBag<(int ordinal, string file, int lineNum, string preview)>();
            // 每个文件的真实命中数，与 results 里的预览条数是两个量：预览封顶 3 条，计数不封顶。
            var matchesByFile = new ConcurrentDictionary<string, int>();
            // 扫盘类工具不额外统计 scope 外的命中——那要把过滤掉的文件再读一遍，
            // 代价与全域搜索相同，故这里 scope 是硬过滤，只在结果头部标明范围。
            var files = SourceIndexer.InDisplayOrder(
                _sourceIndexer.GetAllFiles(scope).Where(f => f.EndsWith(".cs") || f.EndsWith(".xml")));

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

            // 两处静默削减的计数。search_regex 一直在报它们，trace 此前一声不吭——而
            // 「没有尾注即完整命中集」这条读法是调用方从 search_regex 那儿学来的，套到这里
            // 就会把一份漏了六万行的结果当成穷尽结论（本语料里 81022 行的
            // UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs 恰好越过行闸）。
            int lineCappedFiles = 0;
            int unreadableFiles = 0;

            // 结果取舍必须与线程调度无关。原先是整张 files 满盘并发 + 配额一到就从委托头部
            // return：**哪些文件赶在配额前被扫到**取决于线程调度，`limit:1` 同一条查询两次
            // 能给出两个不同的文件，而返回里那句 "first 1" 于是没有定义。改成按 files 顺序
            // 分块推进——扫过的恒是 files 的一个前缀，而 files 正是展示顺序，故留下的就是
            // 读者看到的那一段的开头。search_regex 走的是同一套（SourceIndexer 内同名注释）。
            var stoppedEarly = false;
            for (var chunkStart = 0; chunkStart < files.Count; chunkStart += ScanChunkFiles)
            {
                var chunk = new List<(int Ordinal, string Path)>();
                for (var i = chunkStart; i < Math.Min(chunkStart + ScanChunkFiles, files.Count); i++)
                    chunk.Add((i, files[i]));

                await ScanChunkAsync(chunk);

                if (Interlocked.CompareExchange(ref collectedCount, 0, 0) >= maxTotalResults)
                {
                    stoppedEarly = chunkStart + ScanChunkFiles < files.Count;
                    if (stoppedEarly) Interlocked.Exchange(ref truncatedFlag, 1);
                    break;
                }
            }

            async Task ScanChunkAsync(List<(int Ordinal, string Path)> chunk) =>
                await Parallel.ForEachAsync(chunk, cancellationToken, async (item, ct) =>
            {
                var (fileOrdinal, file) = item;

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
                            //
                            // 本块内的命中一条不丢，全收进来，截断留到最后按 (文件序号, 行号)
                            // 排完序再做。原先是在这里抢配额、抢不到就丢：分块只保证「扫了哪些
                            // 文件」是确定的，块**内**谁先抢到那 20 个名额仍看线程调度，
                            // 于是 limit:20 同一条查询两次还是能给出两批不同的行。
                            if (matchesInFile <= MaxMatchesPerFile)
                            {
                                Interlocked.Increment(ref collectedCount);
                                var preview = line.Trim();
                                if (preview.Length > 100) preview = preview[..97] + "...";
                                results.Add((fileOrdinal, file, lineNum, preview));
                            }
                        }

                        if (lineNum >= MaxLinesScannedPerFile)
                        {
                            Interlocked.Increment(ref lineCappedFiles);
                            break;
                        }
                    }

                    if (matchesInFile > 0) matchesByFile[file] = matchesInFile;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 原先是裸 catch：读不开的文件既不上报，也会连带跳过上面那行
                    // `matchesByFile[file] = matchesInFile`，于是该文件的
                    // `+N more in this file` 也一并静默消失（与 F9(a) 同型）。
                    Interlocked.Increment(ref unreadableFiles);
                }
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

            // 按 (文件序号, 行号) 排完再截：序号来自 files，files 就是展示顺序，故留下的恒是
            // 读者看到的那一段的前缀。拿 ConcurrentBag 的枚举序去截则每次都可能不同。
            var ordered = results.OrderBy(r => r.ordinal).ThenBy(r => r.lineNum).ToList();
            var wasTruncated = Interlocked.CompareExchange(ref truncatedFlag, 0, 0) == 1
                               || stoppedEarly
                               || ordered.Count > maxTotalResults;
            var shownResults = ordered.Take(maxTotalResults).ToList();
            var grouped = shownResults.GroupBy(r => r.file).ToList();
            int totalMatches = Interlocked.CompareExchange(ref totalMatchCount, 0, 0);

            // 未截断时这个数才是真实命中总数——那正是本次修复的目的（原先它是显示条数）。
            // 截断时不能报它：配额一满就不再打开新文件，totalMatches 只反映「恰好扫到了哪些
            // 文件」，随线程调度浮动，同一次查询重跑两遍会给出两个数。与其报一个不稳定的下界，
            // 不如只说确定的量（显示了多少条、扫描在何处停下），把总数留给末尾的提示。
            var sb = new System.Text.StringBuilder();
            // 列出来的文件全同源时标签只印一次（见 ScopeArgs.SourceLabeling）
            var usageLabels = ScopeArgs.SourceLabeling.Of(
                grouped.Select(g => scope.ShowLabels ? scope.SourceNameOf(g.Key) : null));

            var capped = Interlocked.CompareExchange(ref lineCappedFiles, 0, 0);
            var unreadable = Interlocked.CompareExchange(ref unreadableFiles, 0, 0);
            var anyFileIncomplete = capped > 0 || unreadable > 0;

            sb.AppendLine(wasTruncated
                ? $"References to '{symbol}' (first {shownResults.Count} preview lines in scope '{scope.Expression}'){usageLabels.Header}:"
                : $"References to '{symbol}' ({ScopeArgs.FoundCount(totalMatches, anyFileIncomplete)} "
                  + $"in scope '{scope.Expression}'){usageLabels.Header}:");
            sb.AppendLine();

            // 本次要列出的文件里有重名时补目录（见 ScopeArgs.DisambiguateFileNames）。
            // 两处都叫调用方 `use read_code on a file`，而 read_code 收基名——重名不消歧，
            // 那句下一步就是错的。
            var usageDisplayNames = ScopeArgs.DisambiguateFileNames(grouped.Select(g => g.Key));

            var groupsWritten = 0;
            var anyFileFolded = false;
            foreach (var group in grouped)
            {
                // 组与组之间空一行。search_regex 输出的是同一个结构（文件名 + 缩进的预览行）
                // 却一直空着行，两处一密一疏，读者每换一个工具就得重新找组的边界在哪。
                if (groupsWritten++ > 0) sb.AppendLine();

                // 原先每组挂一个 `[C#]` / `[XML]` 前缀，而紧跟其后的文件名带着 .cs / .xml
                // 后缀，说的是同一件事。search_regex 同样按文件分组、从来没有这个前缀。
                var fileName = usageDisplayNames[group.Key];
                sb.AppendLine($"`{fileName}`{usageLabels.Row(scope.ShowLabels ? scope.SourceNameOf(group.Key) : null)}");

                var shown = 0;
                foreach (var match in group.OrderBy(m => m.lineNum))
                {
                    sb.AppendLine($"  L{match.lineNum}: {match.preview}");
                    shown++;
                }

                // 减的是实际显示条数而不是 MaxMatchesPerFile：配额在这个文件中途耗尽时
                // shown 会不足 3，按常数减会少报。文案与 search_regex 保持一致。
                var inFile = matchesByFile.TryGetValue(group.Key, out var c) ? c : shown;
                if (inFile > shown)
                {
                    sb.AppendLine(ScopeArgs.PerFileFold(inFile - shown));
                    // 只有真撞上每文件上限的折叠才让脚注出现——配额在这个文件中途耗尽时 shown
                    // 不足 3，那条折叠的成因是扫描停了，不是每文件上限。同 search_regex。
                    if (shown >= MaxMatchesPerFile) anyFileFolded = true;
                }
            }

            // 判据只看 truncatedFlag。原先还或上 `totalMatches >= maxTotalResults`，
            // 而 totalMatches 现在是真实命中数——单文件折叠出来的命中会误触发这条提示。
            if (wasTruncated)
            {
                // 与 search_regex 同一句话：两个工具在同一个事件（预览行扫到上限就停）上原先
                // 各写各的措辞，而它们的输出结构本来就一样。已顶到硬上限时那句里不会再劝
                // limit:'all'（原地重试），这条判断在 ScanStoppedLine 内部。
                sb.AppendLine();
                sb.AppendLine(ScopeArgs.ScanStoppedLine(maxTotalResults, limit));
            }

            if (anyFileFolded)
            {
                sb.AppendLine();
                sb.AppendLine(ScopeArgs.PerFilePreviewCapLine(MaxMatchesPerFile));
            }

            // 与 search_regex 逐字同句：两个工具有一模一样的两处静默削减，此前只有它说出口
            if (anyFileIncomplete)
            {
                var incomplete = new List<string>();
                if (unreadable > 0)
                    incomplete.Add($"{OutputText.Quantity(unreadable, "files")} could not be read "
                                   + $"and {(unreadable == 1 ? "was" : "were")} skipped entirely");
                if (capped > 0)
                    incomplete.Add($"{OutputText.Quantity(capped, "files")} {(capped == 1 ? "was" : "were")} "
                                   + $"only scanned to line {MaxLinesScannedPerFile}");

                sb.AppendLine();
                sb.AppendLine(ScopeArgs.NotScannedInFullLine(incomplete));
            }

            // usages 分支是硬 scope 过滤、没有 ScopeReport footer（见上面扫盘的注释）。
            // 那条 footer 的缺席本身会被读成「scope 外没有」，故把缺席的含义明说一次。
            sb.Append(ScopeArgs.HardScopeFilterNotice(scope));
            sb.Append(scopeNotice);

            return new ToolResult(sb.ToString());
        }
    }
}
