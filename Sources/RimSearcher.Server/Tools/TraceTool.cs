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
    private readonly ConditionalFolders _conditional;

    public TraceTool(
        SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog, ConditionalFolders? conditional = null)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__trace";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "name", "typeName", "symbolName", "traceMode", "direction", "maxResults", "scopes", "source", "sources", "mod", "mods", "in"];

    public string Description =>
        "Cross-reference analysis for C# and XML. 'inheritors' lists the transitive subclass/implementor tree — " +
        "every descendant, not just direct ones, indirect ones tagged '[depth N]' and direct ones left untagged " +
        "— up to the server cap of 200; a tree larger than that comes back truncated, and the header states the " +
        "true total plus how many of the whole tree are direct and how deep it goes. " +
        "'usages' is a line-by-line whole-word text match, case-insensitive (default 50, at most 3 preview lines " +
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
                    $"which defaults to the server cap of {ScopeArgs.HardLimit} — trees bigger than that come " +
                    "back truncated and no limit lifts the cap, so read the header's total before treating the " +
                    "listing as the whole tree; 'usages' for textual references in C# and XML, which defaults " +
                    "to 50 matches. The 'limit' default noted below is the 'usages' one."
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
            var inheritors = _sourceIndexer.GetInheritors(symbol, scope, limit.Count, out var depths, out var shape);

            if (inheritors.Items.Count == 0)
            {
                var report = new ScopeReport();
                report.Add(inheritors);
                var footer = report.Render(scope, "subclasses");

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

                // 这里**不挂** RetryWiderNotice。那句「retry with scope:'all' before concluding it does
                // not exist」在本分支的三种情形下全是错的或白跑的：
                //   - 已知类型 + 有越界子类  → footer 已经逐源报了数，且上一句刚说过「这不是完整答案」；
                //   - 已知类型 + 无越界子类  → 继承闭包是全域算的，scope:'all' 一条也加不出来；
                //   - 索引里没这个名字      → IsKnownType 本就与 scope 无关，换 scope 返回逐字相同。
                // 实测第三种给出的是 "…Check the spelling… Only sources in scope 'base' were searched —
                // retry with scope:'all'"：两句语气相反，而后一句保证白跑一轮。
                return new ToolResult($"{message}{footer ?? string.Empty}{scopeNotice}");
            }

            // 列出来的类型全同源时标签只印一次（见 ScopeArgs.SourceLabeling）
            var inheritorLabels = ScopeArgs.SourceLabeling.Of(inheritors.Items.Select(e => e.SourceName));
            var inheritorConditional = new ConditionalReport(_conditional);

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
                // 声明散在多份文件里时只有全部落在条件目录里才打标——有一份无条件的，
                // 这个类型在任何实机上都在（见 ConditionalFolders.OfAll）。
                return $"- `{entry.Item}`{depthLabel}{SymbolRow.FileNote(entry.Item, paths)}"
                       + $"{inheritorConditional.TagAll(paths)}{inheritorLabels.Row(entry.SourceName)}";
            });

            // 表头里所有数都描述**同一批东西**：scope 内的整棵树。direct 与 deepest 原先取自
            // inheritors.Items（截断后的展示切片），而总数取自 TotalInScope（全树）——两组数
            // 句法对称地并排，读者只会当成一件事。实测 ThingComp 因此写出
            // 「381 … Listed below: 200 (200 direct, deepest 1 level down)」，而那棵树真有四层。
            // 现在两个数由 GetInheritors 在 scope 过滤时一并数出来，只剩「Listed below」描述切片。
            var shownDeepest = inheritors.Items
                .Select(e => depths.TryGetValue(e.Item, out var d) ? d : 1)
                .DefaultIfEmpty(1).Max();

            var sbInheritors = new System.Text.StringBuilder();
            // 深度标记的约定只在**这次真的印出了标记**时才需要说明；一个标记都没有时讲解一套
            // 不存在的记法，反而会让读者去找它（同 R9：表头说过的话不逐行重复，没发生的事不说）。
            //
            // 「direct = depth 1」必须点破：整份返回里 depth 的原点从没写过，于是表头的
            // `deepest 6 levels down` 该对应 `[depth 6]` 还是 `[depth 5]` 无从判断，而这两种
            // 读法在「要覆写哪一层」上给出不同答案。
            var depthLegend = shownDeepest > 1 ? ", untagged = direct (depth 1)" : "";
            // 没被截断就不写「Listed below」——那时它逐字等于前面那个总数。沿用 R33 的读法：
            // 出现「列了多少」这一格本身就是「被截了」的信号。
            //
            // 截断留下的**恒是最浅的那一批**（GetInheritors 按 depth 升序排候选，见那里的注释），
            // 而返回里一个字都没说这件事。默认读法是「列表是这棵树的一个样本」，于是「样本里
            // 最深的一层」被当成「树最深的一层」——R42 治好的是表头报错深度，这里复发成
            // **把 depth 4 的那批名字报成 depth 6 的成员**。尾行只说「被截了」，没说截的是哪一批。
            //
            // 「第 5、6 层有谁」这套工具确实给不出（没有 offset、也没有参数抬得动 200 这个顶）。
            // 答不了不是缺陷，不说自己答不了才是。
            var depthCoverage = shownDeepest < shape.Deepest
                ? $", shallowest first — nothing below depth {shownDeepest} is listed"
                : ", shallowest first";
            var listed = inheritors.Items.Count < inheritors.TotalInScope
                ? $". Listed below: {inheritors.Items.Count}{depthCoverage}"
                : string.Empty;
            sbInheritors.AppendLine(
                $"Subclasses of '{symbol}' ({inheritors.TotalInScope} in scope '{scope.Expression}', transitive — "
                + $"indirect descendants included; {shape.Direct} direct, deepest "
                + $"{OutputText.Quantity(shape.Deepest, "levels")} down{depthLegend})"
                + $"{listed}{inheritorLabels.Header}:");
            sbInheritors.AppendLine(string.Join(Environment.NewLine, results));

            // 顶到 200 时「narrow the query」在继承树上不是个可执行动作：查询词就是那个类名，
            // 没得再窄，而这个模式既没有 offset 也没有任何参数抬得动上限。唯一的出路是从列表里
            // 挑一个子树根重跑——这个动作在 schema、Description、返回里此前一处都没写。
            var fold = ScopeArgs.FoldLine(
                inheritors, "subclasses", indent: "", limit: limit,
                capAction: "re-trace a listed type as its own root; depths then restart from it");
            if (fold != null) sbInheritors.AppendLine(fold);

            // 行内标记是上面那个 Select 打的，而它是惰性的——AppendLine(string.Join(...)) 已经
            // 把它跑完了，故这里读得到。位置按全服惯例：本段自己的脚注在前，scope 相关的在后。
            var inheritorsConditionalFooter = inheritorConditional.Render();
            if (inheritorsConditionalFooter != null) sbInheritors.Append(inheritorsConditionalFooter);

            var inheritorsReport = new ScopeReport();
            inheritorsReport.Add(inheritors);
            // 越界脚注原先只报「外面还有 91 个」，而表头那句 `23 direct, deepest 6 levels down`
            // 是 scope 内的形状——「换个 scope 会不会改变深度」在返回里完全不可判定，调用方只能猜。
            // 盲测里它猜错并写进了答案正文（实测 scope:'all' 仍是 deepest 6）。与 R42 同形，
            // 轴从「整树 vs 截断切片」换成「域内树 vs 全域树」。
            //
            // 这两个数不必重算：depths 是 GetInheritors 在**全域**上跑完 BFS 的产物，
            // scope 过滤发生在它之后。
            var directEverywhere = depths.Values.Count(d => d == 1);
            var deepestEverywhere = depths.Values.DefaultIfEmpty(1).Max();
            var inheritorsFooter = inheritorsReport.Render(
                scope, "subclasses",
                extra: $"including them the tree is {directEverywhere} direct, deepest "
                       + $"{OutputText.Quantity(deepestEverywhere, "levels")} down");
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

            // 与两个计数并行的名单（基名）。见 ScopeArgs.NameSample：只报个数时调用方
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
                                var preview = line.Trim();
                                if (preview.Length > 100) preview = preview[..97] + "...";
                                results.Add((fileOrdinal, file, lineNum, preview));
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

            // 扫盘分支是硬 scope 过滤、不统计落选来源，故这条提示是它唯一的「别处也许有」的痕迹
            if (results.Count == 0)
                return new ToolResult(
                    $"No text matches for '{symbol}' in scope '{scope.Expression}' "
                    + "(whole word, case-insensitive)."
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

            // 表头动词从 "References to" 改成 "Text matches for"。原先的写法配上「文件 + 行号 +
            // 代码」的正文排版，读起来就是一份引用清单，于是那个数被直接当成「这个符号被引用了
            // 多少处」写进结论——而它既含大小写不同的同名标识符，也含注释掉的行，还会把无关类型
            // 上的同名成员算进来（Description 里那句 "not a call graph" 说的正是这件事，但它在
            // 返回文本里一个字都没有）。inheritors 那种语义结果的措辞与它就此分开。
            int exactCaseMatches = Interlocked.CompareExchange(ref exactCaseMatchCount, 0, 0);
            // 匹配口径就地声明。截断时不报精确大小写数——那时 totalMatches 本身就只反映
            // 「恰好扫到了哪些文件」，再派生一个数只是把不确定量翻倍。
            var casing = wasTruncated
                ? string.Empty
                : exactCaseMatches == totalMatches
                    ? ", whole word and case-insensitive — all match the query's own casing"
                    : $", whole word and case-insensitive — {exactCaseMatches} of them match the query's own casing";

            sb.AppendLine(wasTruncated
                ? $"Text matches for '{symbol}' (first {shownResults.Count} preview lines in scope "
                  + $"'{scope.Expression}', whole word and case-insensitive){usageLabels.Header}:"
                // 下界记号必须就地指出成因，否则读者会拿 limit 去解释它（见 ScopeArgs.LowerBoundReason）
                : $"Text matches for '{symbol}' ({ScopeArgs.FoundCount(totalMatches, anyFileIncomplete)} "
                  + $"in scope '{scope.Expression}'{casing}"
                  + $"{ScopeArgs.LowerBoundReason(anyFileIncomplete)}){usageLabels.Header}:");
            sb.AppendLine();

            // 本次要列出的文件里有重名时补目录（见 ScopeArgs.DisambiguateFileNames）。
            // 两处都叫调用方 `use read_code on a file`，而 read_code 收基名——重名不消歧，
            // 那句下一步就是错的。
            var usageDisplayNames = ScopeArgs.DisambiguateFileNames(grouped.Select(g => g.Key));

            var groupsWritten = 0;
            var anyFileFolded = false;
            var usageConditional = new ConditionalReport(_conditional);
            foreach (var group in grouped)
            {
                // 组与组之间空一行。search_regex 输出的是同一个结构（文件名 + 缩进的预览行）
                // 却一直空着行，两处一密一疏，读者每换一个工具就得重新找组的边界在哪。
                if (groupsWritten++ > 0) sb.AppendLine();

                // 原先每组挂一个 `[C#]` / `[XML]` 前缀，而紧跟其后的文件名带着 .cs / .xml
                // 后缀，说的是同一件事。search_regex 同样按文件分组、从来没有这个前缀。
                // 条件标记排在来源标签之前（同 search_regex）：行尾的 `[x]` 是来源标签位
                var fileName = usageDisplayNames[group.Key];
                sb.AppendLine($"`{fileName}`{usageConditional.Tag(group.Key)}"
                              + $"{usageLabels.Row(scope.ShowLabels ? scope.SourceNameOf(group.Key) : null)}");

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
                    sb.AppendLine(ScopeArgs.PerFileFold(inFile - shown, inFile));
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
                // 名单排序后再交出去：并发桶的枚举序看线程调度，不排的话同一条查询两次会给出
                // 两种点名顺序，与「同一条查询恒给同一份答案」的契约相冲（同 search_regex）。
                var sorted = (ConcurrentBag<string> bag) => (IReadOnlyList<string>)bag
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

                if (unreadable > 0)
                    incomplete.Add($"{OutputText.Quantity(unreadable, "files")} could not be read "
                                   + $"and {(unreadable == 1 ? "was" : "were")} skipped entirely"
                                   + ScopeArgs.NameSample(sorted(unreadableNames)));
                if (capped > 0)
                    incomplete.Add($"{OutputText.Quantity(capped, "files")} {(capped == 1 ? "was" : "were")} "
                                   + $"only scanned to line {MaxLinesScannedPerFile}"
                                   + ScopeArgs.NameSample(sorted(lineCappedNames)));

                sb.AppendLine();
                sb.AppendLine(ScopeArgs.NotScannedInFullLine(incomplete));
            }

            // 条件目录的成因整份说一次（行内只放键，见 ConditionalReport）
            sb.Append(usageConditional.Render() ?? string.Empty);

            // usages 分支是硬 scope 过滤、没有 ScopeReport footer（见上面扫盘的注释）。
            // 那条 footer 的缺席本身会被读成「scope 外没有」，故把缺席的含义明说一次。
            sb.Append(ScopeArgs.HardScopeFilterNotice(scope));
            sb.Append(scopeNotice);

            return new ToolResult(sb.ToString());
        }
    }
}
