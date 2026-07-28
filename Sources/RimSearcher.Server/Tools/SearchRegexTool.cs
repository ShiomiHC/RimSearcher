using System.Text.Json;
using RimSearcher.Core;

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

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "regex", "fileExtension", "extension", "ext", "caseInsensitive", "maxResults", "count", "scopes", "source", "sources", "mod", "mods", "in"];

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

            // 拼错的 scope 被静默退回全域，有结果、无结果两条路径都要说
            var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

            // 同 trace usages：scope 对扫盘工具是硬过滤，落选来源不统计，故要显式点一句
            if (results.Count == 0)
            {
                // fileFilter 必须出现在零命中消息里：'.txt' 这种把候选集筛成 0 的过滤，
                // 原先的措辞会说成「scope 'all' 里没有」——而 scope 里有的是命中，
                // 只是没有一个 .txt 文件。同时报出过滤后的候选文件数，让「筛空了」一眼可见。
                // 「matched」在这句里出现两次而指两件事：pattern 的命中、以及过滤器留下的候选。
                // 第一眼读成「1496 个文件命中了这个 pattern」，与紧邻的「No matches」直接打架。
                var filterNote = string.IsNullOrEmpty(fileFilter)
                    ? string.Empty
                    : $" with fileFilter '{fileFilter}' "
                      + $"(that filter left {OutputText.Quantity(diagnostics.CandidateFiles, "files")} to search)";

                // 零命中时最该回显的就是这个开关：case-sensitive 恰恰是「明明有却查不到」的常见成因
                return new ToolResult(
                    $"No matches for pattern '{pattern}' in scope '{scope.Expression}'{filterNote}, "
                    + $"{(ignoreCase ? "case-insensitive" : "case-sensitive")}."
                    + $"{ScopeArgs.RetryWiderNotice(scope)}{scopeNotice}");
            }

            // 索引层是并发扫描后从 ConcurrentBag 收口的，文件之间的先后完全看线程调度；
            // 不排一下，同一次查询重跑两遍文件顺序就能不一样。
            var allFiles = results
                .GroupBy(r => r.Path)
                // 排序键与印出来的东西必须是同一个：只印文件名却按完整路径排，读者看到的是
                // 「每进一个目录字母序就重来一遍」。这也正是扫盘推进的顺序（SourceIndexer
                // .InDisplayOrder），故截断留下的恒是这张表的前缀。
                .OrderBy(g => System.IO.Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var shownFiles = allFiles.Take(MaxFilesShown).ToList();

            // 未截断时报真实命中总数（各文件命中数之和），而不是预览条数——预览每文件封顶，
            // 两者可以差很远。截断时这个和只覆盖恰好扫到的文件，随线程调度浮动，故只说确定的量。
            var totalMatches = matchesByFile.Values.Sum();
            // 「扫描在上限处停了」只说一次，说在末尾那行——那里同时给得出下一步。表头
            // 原先也说一遍（", scan stopped at the limit"），而 "first N" 本就含着这个意思。
            // 单位改说 "previews"：表头的 N 数的是预览行，与「命中数」是两个量。
            // 生效的 ignoreCase 必须回显。它默认 true 而只写在参数表里，于是同一个 pattern 的
            // 命中数会因为一个没人传过的开关而浮动，返回里却没有任何字段能事后判断跑的是哪一档
            // ——盲测里调用方拿本工具去「交叉验证」trace usages 的数，两边跑的其实是同一个默认
            // 开关，于是把一个偏大的数当成「已独立复现」。
            var casing = ignoreCase ? "case-insensitive" : "case-sensitive";
            var headline = truncated
                ? $"first {results.Count} preview lines in scope '{scope.Expression}', {casing}"
                // 有文件没扫全时这个总数只是下界，表头与末尾那条尾注要同时改口。
                // 改口时还要就地指出成因在哪——见 ScopeArgs.LowerBoundReason。
                : $"{ScopeArgs.FoundCount(totalMatches, diagnostics.AnyFileIncomplete)} "
                  + $"in scope '{scope.Expression}', {casing}"
                  + ScopeArgs.LowerBoundReason(diagnostics.AnyFileIncomplete);

            // 本次要列出的文件里有重名时补目录（见 ScopeArgs.DisambiguateFileNames）
            var displayNames = ScopeArgs.DisambiguateFileNames(shownFiles.Select(g => g.Key));

            // 列出来的文件全同源时标签只印一次（见 ScopeArgs.SourceLabeling）
            var labels = ScopeArgs.SourceLabeling.Of(
                shownFiles.Select(g => scope.ShowLabels ? scope.SourceNameOf(g.Key) : null));

            // string.Join 立即枚举，故读这两个旗时它们已经攒完了
            var anyFileFolded = false;
            var conditional = new ConditionalReport(_conditional);
            var output = $"Regex matches for '{pattern}' ({headline}){labels.Header}:\n\n" +
                         string.Join("\n\n", shownFiles.Select(g =>
                         {
                             var fileName = displayNames[g.Key];
                             // 同理，同一文件内的命中也是乱序到达的。不排序时 Take(3) 拿到的是任意
                             // 三条，读起来像 L17、L15 这样倒着走，且靠前的命中可能根本没被选中。
                             var groupItems = g.OrderBy(m => m.LineNumber).ToList();
                             var matches = groupItems.Take(SourceIndexer.MaxPreviewsPerFile).Select(m => $"  L{m.LineNumber}: {m.Preview}");

                             // 减的是实际显示条数，且基数取索引层数出来的真实命中数：预览在索引层
                             // 每文件封顶 3 条（与这里的 Take 对齐），拿 groupItems.Count 当基数
                             // 会把第 4 条起的命中吞掉。
                             var shown = Math.Min(groupItems.Count, SourceIndexer.MaxPreviewsPerFile);
                             var inFile = matchesByFile.TryGetValue(g.Key, out var c) ? c : groupItems.Count;
                             // 脚注说的是「每文件 3 行上限」，故只有真撞上那个上限的折叠才算数。
                             // 扫描停在预览配额上时，最后一个文件的 shown 会不足 3——它的折叠成因
                             // 是配额耗尽（末尾那句 `scan stopped at the N-preview cap` 已经说了），
                             // 把它也算进来会让脚注对这个文件给出错误归因：读者会以为「这个文件最多
                             // 只能看到 3 行」，而其实放宽 limit 就能多印一行。
                             if (inFile > shown && shown >= SourceIndexer.MaxPreviewsPerFile) anyFileFolded = true;
                             var moreCount = inFile > shown
                                 ? "\n" + ScopeArgs.PerFileFold(inFile - shown, inFile)
                                 : "";

                             // 条件标记排在来源标签**之前**：行尾的 `[x]` 是全服的来源标签位
                             // （见 ScopeArgs.SourceLabeling 与文法闸规则六），别的记号挤进去
                             // 会让「同源就提到表头」那条判据在这一行上读不出来。
                             var label = labels.Row(scope.ShowLabels ? scope.SourceNameOf(g.Key) : null);
                             return $"`{fileName}`{conditional.Tag(g.Key)}{label}\n"
                                    + $"{string.Join("\n", matches)}{moreCount}";
                         }));

            // 两处截断互相独立：truncated 说的是扫描在命中上限处停了，文件数上限则是
            // 这里静默 Take 掉的。原先只有一条提示且挂在前者上，于是「命中没超限但文件超了」
            // 的情况完全不吭声，调用方会把不完整的列表当成全部。
            //
            // 两者的尾注文法也不同，且各自都对：扫描停了就不知道还剩多少（那些文件根本没
            // 打开过），只有文件数超了才数得出准数，能用全服统一的 `... +N more`。
            if (truncated)
            {
                // 截断时 allFiles 只是「已扫到的那批预览」里的文件数，不是命中文件总数——扫描早已
                // 在命中上限处停下，后面的候选文件根本没打开过。原先无条件称其为 "matching files"，
                // 那个数比真实值小一到两个数量级，而调用方会拿它当结论。
                var extra = allFiles.Count > MaxFilesShown
                    ? new[]
                    {
                        $"only the first {MaxFilesShown} files are listed, and the {allFiles.Count} distinct files "
                        + "seen so far are not the total number of matching files"
                    }
                    : null;
                output += "\n\n" + ScopeArgs.ScanStoppedLine(results.Count, limit, extra);
            }
            else if (allFiles.Count > MaxFilesShown)
            {
                // 同 PerFileFold：这一份返回里，表头数的是**行**、正文分的是**文件**、这一行数的是
                // **没列出来的文件**——三个口径三个名词，唯独「本次列了几个文件」从头到尾没出现，
                // 而它是常数 50 这件事也没写在任何地方。扫描没被截断时 allFiles.Count 就是命中文件
                // 总数（是确定值），直接给出来，读者不必去数正文里的文件块。
                // 「列了几个」也要给。同一个工具的 scan-stopped 那一形明写 `only the first 50
                // files are listed`，这一形不写，读者只能做 97−47 的减法——R47 当时判定这个减法
                // 可接受，第九轮盲测里它第一次结出错误推理：调用方据 50 个文件的来源标签断言
                // 「97 个文件清一色落在那 11 个源内」。两形对齐，减法就不必做了。
                output += $"\n\n... +{allFiles.Count - MaxFilesShown} more of "
                          + $"{OutputText.Quantity(allFiles.Count, "matching files")} "
                          + $"({MaxFilesShown} listed; narrow the pattern or the scope)";
            }

            if (anyFileFolded)
                output += "\n\n" + ScopeArgs.PerFilePreviewCapLine(SourceIndexer.MaxPreviewsPerFile);

            // 「没有尾注即完整」是本工具写在 Description 里的契约，被跳过/被弃扫的文件必须破这个契约
            if (diagnostics.AnyFileIncomplete)
            {
                var incomplete = new List<string>();
                // 这句里跟着 N 变的不止名词：动词、代词、和后半句的主谓都要跟着换，
                // 拼字符串拼到第四个三目就没人读得懂了，故整句两写。
                if (diagnostics.TimedOutFiles > 0)
                    incomplete.Add((diagnostics.TimedOutFiles == 1
                        ? "1 file was abandoned mid-scan because the pattern timed out on it "
                          + "(catastrophic backtracking) — its per-file match count is missing"
                        : $"{diagnostics.TimedOutFiles} files were abandoned mid-scan because the pattern "
                          + "timed out on them (catastrophic backtracking) — their per-file match counts are missing")
                        + ScopeArgs.NameSample(diagnostics.TimedOutNames));
                if (diagnostics.UnreadableFiles > 0)
                    incomplete.Add($"{OutputText.Quantity(diagnostics.UnreadableFiles, "files")} could not be read "
                                   + $"and {(diagnostics.UnreadableFiles == 1 ? "was" : "were")} skipped entirely"
                                   + ScopeArgs.NameSample(diagnostics.UnreadableNames));
                if (diagnostics.LineCappedFiles > 0)
                    incomplete.Add($"{OutputText.Quantity(diagnostics.LineCappedFiles, "files")} "
                                   + $"{(diagnostics.LineCappedFiles == 1 ? "was" : "were")} "
                                   + $"only scanned to line {diagnostics.LineCap}"
                                   + ScopeArgs.NameSample(diagnostics.LineCappedNames));

                output += "\n\n" + ScopeArgs.NotScannedInFullLine(incomplete);
            }

            // 条件目录的成因整份说一次（行内只放键，见 ConditionalReport）
            output += conditional.Render() ?? string.Empty;

            // 同 trace usages：这里没有 out-of-scope 逐源计数，而缺席会被读成「scope 外没有」
            output += ScopeArgs.HardScopeFilterNotice(scope);
            output += scopeNotice;

            return new ToolResult(output);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid Regex Pattern: {ex.Message}", true);
        }
    }

}
