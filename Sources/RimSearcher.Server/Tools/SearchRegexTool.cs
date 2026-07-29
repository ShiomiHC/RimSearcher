using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

public class SearchRegexTool : ITool
{
    // 命中可能集中在少数文件，也可能散落在几千个文件里。后者全列出来对调用方无用，
    // 但截掉了就必须说，见下面的 notes。
    private const int MaxFilesShown = 50;

    // 缺省命中上限。扫盘型工具比列表型工具给得多（默认 10 条对正则搜索没意义），
    // 但 'all' 一律走 ScopeArgs.HardLimit，不再是原先那个写死的 500。
    private const int DefaultMatchLimit = 100;

    private readonly SourceIndexer _indexer;
    private readonly ScopeCatalog _scopeCatalog;
    private readonly ConditionalFolders _conditional;

    public SearchRegexTool(
        SourceIndexer indexer, ScopeCatalog scopeCatalog, ConditionalFolders? conditional = null)
    {
        _indexer = indexer;
        _scopeCatalog = scopeCatalog;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__search_regex";

    // scope / limit 两族取 ScopeArgs 的名单，不再在这里各抄一遍：抄漏的 `max` 与 `top`
    // 读得进来却被报成被忽略，同一份返回自相矛盾。
    public IEnumerable<string> ExtraAcceptedKeys =>
        [.. ScopeArgs.ScopeKeys, .. ScopeArgs.LimitKeys,
         "query", "pattern", "regex", "fileFilter", "fileExtension", "extension", "ext",
         "ignoreCase", "caseInsensitive"];

    // 「both cuts are always stated in a trailing note」原先只数了两刀（每文件 3 行预览、
    // 50 个文件），而第三刀——有文件没被扫全——不只加一条尾注，还会把表头的总数降格成
    // `at least N`。三刀混在一句「both」里说，于是那个下界记号在 schema 里找不到自己的成因，
    // 读者只能就近拿 `limit` 的 default 100 去解释它（`at least 105` 与 100 只差 5）。
    // 三刀分开写，并把「limit 从不改这个数」明说出来。
    public string Description =>
        ".NET regex search across indexed C# and XML files, with an optional extension filter (e.g. '.cs') and " +
        "scope. Results are grouped by file, showing at most 3 preview lines per file and at most 50 files. " +
        "Three things can cut the output: those two caps, and files the scan could not read in full; all three " +
        "are stated in a trailing note, and the third additionally degrades the header count to 'at least N'. " +
        // 「limit never changes that count」读起来是个无条件承诺：随便传多小的 limit，总数照报。
        // 例外虽然就写在后半句，却以 'instead' 起头附在承诺之后，读者已经先把承诺收下了——
        // 第十轮盲测里一条链据此传了 limit:1 想省一轮拿总数，结果扫描直接停、计数整个消失，
        // 白烧一轮。真实语义是「limit 不会把总数改**小**，但咬人时它会把总数**删掉**」，
        // 两件事此前被措辞混成了一件。先说会发生什么，再说不会发生什么。
        "A limit small enough to bite replaces that count with 'first N preview lines' and stops the scan " +
        "there — pass limit:'all' when the total is what is wanted. Short of that, limit only shortens the " +
        "listing and never lowers the reported count. " +
        "Counts are matching lines, not match sites — a line the pattern hits twice counts once — and this tool " +
        "never reports how many lines or files matched across the whole corpus when the scan stopped early. " +
        "Matches are raw text: commented-out code, disabled XML and prose inside comments all count, so a match " +
        "count is not a count of things that exist — confirm with locate or inspect before treating it as one. " +
        // R59 那三句常驻能力边界（「条件目录一律收下、条件不判定」）对每一次调用都成立，因而
        // 对**手上这一条命中**什么也没说。F34 把它收敛成契约，条件由命中自己带（见 ConditionalReport）。
        SourceLabeling.Contract + " " +
        ConditionalReport.Contract + " " +
        "The header echoes whether the scan ran case-insensitively (the default).";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__search_regex",
        "pattern (a regex, e.g. 'class.*:.*ThingComp'). Aliases accepted: query, regex.",
        "pattern (required), ignoreCase, fileFilter (aliases: fileExtension, extension, ext), scope, limit.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            pattern = new
            {
                type = "string",
                minLength = 1,
                description = "Regex pattern to search. Examples: '<thingClass>Apparel</thingClass>', 'void CompTick\\(\\)', 'class.*:.*ThingComp'. Aliases 'query'/'regex' are also accepted."
            },
            ignoreCase = new { type = "boolean", @default = true, description = "Whether to ignore case, defaults to true." },
            fileFilter = new { type = "string", description = "Optional extension filter such as '.cs' or '.xml'. Aliases 'fileExtension'/'extension'/'ext' are also accepted." },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeArgs.LimitSchemaProperty(DefaultMatchLimit, fuzzy: false)
        },
        required = new[] { "pattern" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var pattern = ToolArgs.GetRequiredString(args, ArgSpec, "pattern", "query", "regex");
        var ignoreCase = ToolArgs.GetBool(args, true, "ignoreCase", "caseInsensitive");
        var fileFilter = ToolArgs.GetOptionalString(args, "fileFilter", "fileExtension", "extension", "ext");
        var scope = ScopeArgs.Resolve(_scopeCatalog, args);
        var limit = ScopeArgs.GetDisplayLimit(args, fallback: DefaultMatchLimit);

        try
        {
            // scope 与 fileFilter 都下推给索引层在扫描前生效——留到这里筛会被命中上限吃空
            var (results, truncated, matchesByFile, diagnostics) = await _indexer.SearchRegexAsync(
                pattern, scope, fileFilter, ignoreCase, limit.Count, cancellationToken, progress);

            // 生效的 ignoreCase 必须回显。它默认 true 而只写在参数表里，于是同一个 pattern 的
            // 命中数会因为一个没人传过的开关而浮动，返回里却没有任何字段能事后判断跑的是哪一档
            // ——盲测里调用方拿本工具去「交叉验证」trace usages 的数，两边跑的其实是同一个默认
            // 开关，于是把一个偏大的数当成「已独立复现」。
            var casing = ignoreCase ? "case-insensitive" : "case-sensitive";

            // fileFilter 此前只出现在**零命中**那条消息里，成功分支的表头只回显 scope 与 casing。
            // 服务端确实照做了（下推给索引层），问题是调用方没有观察点：`ext:'xml'` 少写个点、
            // 或传了个筛不掉任何东西的值，都无从察觉。而另外两个参数被回显这件事反过来教出
            // 「没回显 = 没生效」——第十三轮两条链的被测方各自写下同一句自评「我无从判断它生效没有」。
            // 补齐比留着更省字：三个参数要么都回显，要么都不回显，不该只差这一个。
            var echoes = new List<string> { casing };
            if (!string.IsNullOrEmpty(fileFilter)) echoes.Add($"files filtered to '{fileFilter}'");

            // fileFilter 必须出现在零命中消息里：'.txt' 这种把候选集筛成 0 的过滤，
            // 原先的措辞会说成「scope 'all' 里没有」——而 scope 里有的是命中，
            // 只是没有一个 .txt 文件。同时报出过滤后的候选文件数，让「筛空了」一眼可见。
            // 「matched」在这句里出现两次而指两件事：pattern 的命中、以及过滤器留下的候选。
            // 第一眼读成「1496 个文件命中了这个 pattern」，与紧邻的「No matches」直接打架。
            var filterNote = string.IsNullOrEmpty(fileFilter)
                ? string.Empty
                : $" with fileFilter '{fileFilter}' "
                  + $"(that filter left {CountedNoun.Files.Quantity(diagnostics.CandidateFiles)} to search)";

            // 索引层是并发扫描后从 ConcurrentBag 收口的，文件之间的先后完全看线程调度；
            // 不排一下，同一次查询重跑两遍文件顺序就能不一样。
            var blocks = results
                .GroupBy(r => r.Path)
                // 排序键与印出来的东西必须是同一个：只印文件名却按完整路径排，读者看到的是
                // 「每进一个目录字母序就重来一遍」。这也正是扫盘推进的顺序（SourceIndexer
                // .InDisplayOrder），故截断留下的恒是这张表的前缀。
                .OrderBy(g => System.IO.Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ScanFileBlock(
                    g.Key,
                    // 同理，同一文件内的命中也是乱序到达的。不排序时取到的是任意几条，
                    // 读起来像 L17、L15 这样倒着走，且靠前的命中可能根本没被选中。
                    g.OrderBy(m => m.LineNumber).Select(m => (m.LineNumber, m.Preview)).ToList(),
                    // 基数取索引层数出来的真实命中数：预览在索引层每文件封顶，拿到手的条数
                    // 当基数会把第 4 条起的命中吞掉。
                    matchesByFile.TryGetValue(g.Key, out var inFile) ? inFile : g.Count()))
                .ToList();

            return new ToolResult(ScanOutputRenderer.Render(new ScanOutput
            {
                Subject = $"Regex matches for '{pattern}'",
                EmptyLine =
                    $"No matches for pattern '{pattern}' in scope '{scope.Expression}'{filterNote}, {casing}.",
                Scope = scope,
                ParameterEchoes = echoes,
                Blocks = blocks,
                FileListCap = MaxFilesShown,
                PreviewCapPerFile = SourceIndexer.MaxPreviewsPerFile,
                // 未截断时报真实命中总数（各文件命中数之和），而不是预览条数——预览每文件封顶，
                // 两者可以差很远。截断时这个和只覆盖恰好扫到的文件，随线程调度浮动，故 renderer
                // 在那一形下不报它，只说确定的量。
                TotalMatchingLines = matchesByFile.Values.Sum(),
                ScanStopped = truncated,
                Limit = limit,
                Completeness = new ScanCompleteness(
                    diagnostics.TimedOutFiles, diagnostics.TimedOutNames,
                    diagnostics.UnreadableFiles, diagnostics.UnreadableNames,
                    diagnostics.LineCappedFiles, diagnostics.LineCappedNames,
                    diagnostics.LineCap),
                Conditional = new ConditionalReport(_conditional),
                // 拼错的 scope 被静默退回全域，有结果、无结果两条路径都要说
                ScopeNotice = ScopeNotices.Unresolved(_scopeCatalog, scope),
            }));
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }

}
