using System.Text.Json;
using System.Collections.Concurrent;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

public class TraceTool : ITool
{
    // 两个 mode 的缺省条数不同：继承树本身就是要看全的，故 inheritors 缺省即展开到硬上限；
    // usages 是扫盘结果、一条命中一行，缺省就给到硬上限会把上下文吃掉，仍按 50 起步。
    // 两者的显式 limit 与 'all' 都走同一套语义（见 ScopeAndLimitArgs）。
    private const int InheritorsDefaultLimit = ScopeAndLimitArgs.HardLimit;
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
    private readonly ConditionalFolders _conditional;

    public TraceTool(
        SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog, ConditionalFolders? conditional = null)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__trace";

    // scope / limit 两族取 ScopeAndLimitArgs 的名单，不再在这里各抄一遍：抄漏的 `max` 与 `top`
    // 读得进来却被报成被忽略，同一份返回自相矛盾。
    public IEnumerable<string> ExtraAcceptedKeys =>
        [.. ScopeAndLimitArgs.ScopeKeys, .. ScopeAndLimitArgs.LimitKeys,
         "query", "name", "type", "typeName", "symbol", "symbolName", "traceMode", "direction"];

    public string Description =>
        "Cross-reference analysis for C# and XML. 'inheritors' lists the transitive subclass/implementor tree — " +
        "every descendant, not just direct ones, indirect ones tagged '[depth N]' and direct ones left untagged " +
        // 这三个数此前是手打的，而**同一个文件**下面 mode 那格的说明写的就是
        // `{ScopeAndLimitArgs.HardLimit}` 的插值形——同一个 200，一句改得动一句改不动。
        $"— up to the server cap of {ScopeAndLimitArgs.HardLimit}; a tree larger than that comes back truncated, " +
        "and the header states the " +
        "true total plus how many of the whole tree are direct and how deep it goes. " +
        $"'usages' is a line-by-line whole-word text match, case-insensitive (default {UsagesDefaultLimit}, " +
        $"at most {MaxMatchesPerFile} preview lines " +
        "per file plus a '+N more of M matching lines in this file' count — counts are matching lines, not match " +
        "sites, so a line hit twice counts once). Usages is not a call graph: it is raw text, so same-named " +
        "members on unrelated types, differently-cased identifiers and commented-out code all land in the same " +
        "list, while inherited calls are missed. " +
        // `at least N` 的读法此前只成文于 locate 的 Description，而这个工具同样会印它。
        // 记号在两处出现、读法只在一处写着，缺席的那一处就只能靠调用方就近找个上限来解释——
        // 而这边最顺手的上限恰好是 limit 的 default 50。
        "A usages header that reads 'at least N matching lines' means some file could not be scanned in full " +
        "and N is a floor; the trailing note names those files. limit never produces that wording — when the " +
        "result cap bites, the header switches to 'first N preview lines' instead. " +
        // 这个工具此前一句都没有，理由是不想把 R59 那段常驻边界贴第三遍。收敛成契约之后
        // 它只有一句，而 inheritors 恰恰是最需要它的地方：一个只在 CE 启用时才存在的子类，
        // 与 vanilla 的子类在返回里逐字同形。
        SourceLabeling.Contract + " " +
        ConditionalReport.Contract;

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
                    "indirect descendants are listed too and tagged '[depth N]', direct ones are untagged), " +
                    $"which defaults to the server cap of {ScopeAndLimitArgs.HardLimit} — trees bigger than that come " +
                    "back truncated and no limit lifts the cap, so read the header's total before treating the " +
                    "listing as the whole tree; 'usages' for textual references in C# and XML, which defaults " +
                    "to 50 matches. The 'limit' default noted below is the 'usages' one."
            },
            scope = ScopeAndLimitArgs.ScopeSchemaProperty(_scopeCatalog),
            // trace 两种模式都不是模糊搜索：inheritors 的候选分数恒为 100（继承关系是精确的，
            // ScopeFilter 那里 scoreGap 传的就是 null），usages 是逐行全词文本匹配。
            // 照抄模糊工具的文案会让调用方以为「剩下的是低相关度、调多大 limit 都拿不回来」，
            // 从而把一份被 limit 截断的引用清单当成完整结论。
            limit = ScopeAndLimitArgs.LimitSchemaProperty(UsagesDefaultLimit, fuzzy: false)
        },
        required = new[] { "symbol", "mode" }
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__trace",
        // 两个必填参数各有各的别名，故都写成 `for <参数名>` 的形式：不点名的话读者只能猜这串
        // 别名是谁的，而这个工具恰好两个都收别名。
        "symbol (a class or member, e.g. 'ThingComp') and mode ('inheritors' or 'usages'). "
        + "Aliases accepted for symbol: symbolName, query, name, type, typeName. "
        + "Aliases accepted for mode: traceMode, direction.",
        "symbol (required), mode (required, 'inheritors' | 'usages'), scope, limit.");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var symbol = ToolArgs.StripLocateFilterPrefix(
            ToolArgs.GetRequiredFuzzyString(
                args, ArgSpec, "symbol", "symbolName", "query", "name", "type", "typeName"));
        var mode = ToolArgs.GetRequiredString(args, ArgSpec, "mode", "traceMode", "direction").ToLowerInvariant();

        if (mode is not ("inheritors" or "usages"))
            return new ToolResult($"Unknown mode '{mode}'. Use 'inheritors' (subclass tree) or 'usages' (textual references).", true);

        var scope = ScopeAndLimitArgs.Resolve(_scopeCatalog, args);

        // 拼错的 scope 被静默退回全域，两个 mode 的每条返回路径都要带上这行，
        // 否则调用方会把全域结果当成自己限定过的范围内结果。
        var scopeNotice = ScopeNotices.Unresolved(_scopeCatalog, scope) ?? string.Empty;

        if (mode == "inheritors")
        {
            var limit = ScopeAndLimitArgs.GetDisplayLimit(args, fallback: InheritorsDefaultLimit);

            cancellationToken.ThrowIfCancellationRequested();
            var inheritors = _sourceIndexer.GetInheritors(
                symbol, scope, limit.Count, out var depths, out var shape);

            return new ToolResult(InheritorsRenderer.Render(new InheritorsOutput
            {
                Symbol = symbol,
                Scope = scope,
                Inheritors = inheritors,
                // 全域 BFS 的产物，scope 过滤发生在它之后。逐行的 `[depth N]` 与越界脚注里
                // 「把落选那批算进来整棵树是什么形状」都从这一份读，故两处对不上不了。
                Depths = depths,
                Paths = inheritors.Items.ToDictionary(
                    e => e.Item,
                    e => (IReadOnlyList<string>)_sourceIndexer.GetPathsByType(e.Item)),
                Shape = shape,
                Limit = limit,
                Conditional = new ConditionalReport(_conditional),
                // 「索引里没这个名字」与「有，但没人继承它」是两件事，下一步完全不同。
                // 与 scope 无关，故它是事实；那句「这是答案」的背书该不该给由 renderer 判
                // （还要看越界脚注在不在场）。
                TypeIsIndexed = _sourceIndexer.IsKnownType(symbol),
                ScopeNotice = scopeNotice,
            }));
        }
        else
        {
            // 显式 limit 原样生效：原先写成 `limit == 0 ? 50 : Math.Max(limit, 50)`，
            // limit:5 会被抬到 50，limit:'all' 又被压在 50 —— 两个方向都不听调用方的。
            var limit = ScopeAndLimitArgs.GetDisplayLimit(args, fallback: UsagesDefaultLimit);
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

            // 同一条 pattern 的大小写敏感版本，只用来数「有多少行是按查询原样拼写的」。
            //
            // 匹配是不分大小写的全词匹配，而 C# 的命名习惯保证「类型 CompRefuelable → 局部变量
            // compRefuelable」——实测 CompRefuelable 的 108 行里有 26 行是纯变量名。调用方拿这个
            // 108 当「这个类被引用了多少处」写进结论就直接错了 32%，而返回里没有任何一处能让它
            // 察觉。这个数只在命中行上多跑一次正则，代价可忽略。
            var exactCaseRegex = new System.Text.RegularExpressions.Regex(
                $@"\b{System.Text.RegularExpressions.Regex.Escape(symbol)}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            // collectedCount 是「已占用的预览配额」，totalMatchCount 是「真实命中总数」。
            // 原先只有一个 globalCount 同时充当这两者，表头才会把显示条数当成命中数报出去。
            int collectedCount = 0;
            int totalMatchCount = 0;
            int exactCaseMatchCount = 0;
            int processedCount = 0;
            int totalFiles = files.Count;
            int truncatedFlag = 0;

            // 两处静默削减的计数。search_regex 一直在报它们，trace 此前一声不吭——而
            // 「没有尾注即完整命中集」这条读法是调用方从 search_regex 那儿学来的，套到这里
            // 就会把一份漏了六万行的结果当成穷尽结论（本语料里 81022 行的
            // UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs 恰好越过行闸）。
            int lineCappedFiles = 0;
            int unreadableFiles = 0;

            // 与两个计数并行的名单（基名）。见 ScanReport.NameSample：只报个数时调用方
            // 无从判断那个文件与本次查询有没有关系，只能把整份结果一律当成下界。
            var lineCappedNames = new ConcurrentBag<string>();
            var unreadableNames = new ConcurrentBag<string>();

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
                            if (exactCaseRegex.IsMatch(line)) Interlocked.Increment(ref exactCaseMatchCount);

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
                                // 预览行长度上限与截法都归 SourceIndexer 一处，见那边的
                                // MaxPreviewLength：两个扫盘工具的预览进的是同一个渲染器，
                                // 长度不一致会在同一屏上给出两种行宽。
                                results.Add((fileOrdinal, file, lineNum,
                                    SourceIndexer.TruncatePreview(line)));
                            }
                        }

                        if (lineNum >= MaxLinesScannedPerFile)
                        {
                            Interlocked.Increment(ref lineCappedFiles);
                            lineCappedNames.Add(Path.GetFileName(file));
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
                    unreadableNames.Add(Path.GetFileName(file));
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

            // 按 (文件序号, 行号) 排完再截：序号来自 files，files 就是展示顺序，故留下的恒是
            // 读者看到的那一段的前缀。拿 ConcurrentBag 的枚举序去截则每次都可能不同。
            var ordered = results.OrderBy(r => r.ordinal).ThenBy(r => r.lineNum).ToList();
            var wasTruncated = Interlocked.CompareExchange(ref truncatedFlag, 0, 0) == 1
                               || stoppedEarly
                               || ordered.Count > maxTotalResults;

            int totalMatches = Interlocked.CompareExchange(ref totalMatchCount, 0, 0);
            int exactCaseMatches = Interlocked.CompareExchange(ref exactCaseMatchCount, 0, 0);

            // 匹配口径就地声明，这个数为什么必须报见 exactCaseRegex 上方。截断时不报它——
            // 那时 totalMatches 本身就只反映「恰好扫到了哪些文件」，再派生一个数只是把
            // 不确定量翻倍。
            var echoes = new List<string>
            {
                wasTruncated
                    ? "whole word and case-insensitive"
                    : exactCaseMatches == totalMatches
                        ? "whole word and case-insensitive — all match the query's own casing"
                        : $"whole word and case-insensitive — {exactCaseMatches} of them match "
                          + "the query's own casing"
            };

            // 名单排序后再交出去：并发桶的枚举序看线程调度，不排的话同一条查询两次会给出
            // 两种点名顺序，与「同一条查询恒给同一份答案」的契约相冲（同 search_regex）。
            var sorted = (ConcurrentBag<string> bag) => (IReadOnlyList<string>)bag
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            var blocks = ordered
                .Take(maxTotalResults)
                // 已按 (文件序号, 行号) 排好，GroupBy 保序，故文件块的次序就是展示顺序、
                // 块内预览行的次序就是行号升序。两者都不必再排一遍。
                .GroupBy(r => r.file)
                .Select(g => new ScanFileBlock(
                    g.Key,
                    g.Select(m => (m.lineNum, m.preview)).ToList(),
                    // 基数取扫盘时数出来的真实命中数：预览每文件封顶 3 条，拿到手的条数当基数
                    // 会少报。配额在这个文件中途耗尽时 g 会不足 3 条，那时按常数减同样少报，
                    // 故减的是实际条数（renderer 里的 shown）。
                    matchesByFile.TryGetValue(g.Key, out var inFile) ? inFile : g.Count()))
                .ToList();

            return new ToolResult(ScanOutputRenderer.Render(new ScanOutput
            {
                // 表头动词是 "Text matches for" 而不是 "References to"。原先的写法配上「文件 +
                // 行号 + 代码」的正文排版，读起来就是一份引用清单，于是那个数被直接当成「这个符号
                // 被引用了多少处」写进结论——而它既含大小写不同的同名标识符，也含注释掉的行，还会
                // 把无关类型上的同名成员算进来（Description 里那句 "not a call graph" 说的正是这件
                // 事，但它在返回文本里一个字都没有）。inheritors 那种语义结果的措辞与它就此分开。
                Subject = $"Text matches for '{symbol}'",
                // 扫盘分支是硬 scope 过滤、不统计落选来源，故 RetryWider 是零命中形唯一的
                // 「别处也许有」的痕迹——它由 renderer 挂，见 ScanOutputRenderer.Empty。
                EmptyLine = $"No text matches for '{symbol}' in scope '{scope.Expression}' "
                            + "(whole word, case-insensitive).",
                Scope = scope,
                ParameterEchoes = echoes,
                Blocks = blocks,
                // 文件数不封第二道闸，见 ScanOutput.FileListCap
                FileListCap = null,
                PreviewCapPerFile = MaxMatchesPerFile,
                // 未截断时这个数才是真实命中总数。截断时不能报它：配额一满就不再打开新文件，
                // 它只反映「恰好扫到了哪些文件」，随线程调度浮动——renderer 在那一形下不报它。
                TotalMatchingLines = totalMatches,
                ScanStopped = wasTruncated,
                Limit = limit,
                // 两处静默削减，为什么必须报见 lineCappedFiles 上方。这里没有超时那一档：
                // 本模式的 pattern 是转义后的全词匹配，回溯不起来。
                Completeness = new ScanCompleteness(
                    UnreadableFiles: Interlocked.CompareExchange(ref unreadableFiles, 0, 0),
                    UnreadableNames: sorted(unreadableNames),
                    LineCappedFiles: Interlocked.CompareExchange(ref lineCappedFiles, 0, 0),
                    LineCappedNames: sorted(lineCappedNames),
                    LineCap: MaxLinesScannedPerFile),
                Conditional = new ConditionalReport(_conditional),
                ScopeNotice = scopeNotice,
            }));
        }
    }
}
