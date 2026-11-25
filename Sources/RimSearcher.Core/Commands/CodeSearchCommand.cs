using RimTooling.Cli;
using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Sources;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

public sealed class CodeSearchCommand : Command
{
    private static readonly CliOption FileGlob = CliOption.Repeat("file-glob") with
    {
        Aliases = ["file", "files", "file-filter", "glob", "include"],
        Placeholder = "<glob>",
        Help = "只搜索路径匹配该 glob 的文件。不含 '/' 的 glob 只匹配文件名（*.cs 为任意深度的每个 .cs 文件）；" +
               "含 '/' 时匹配相对反编译根目录的路径，" +
               "此时 '*' 不跨越 '/'，'**' 跨越。因此 */Verse/* 只匹配下一层的路径，**/Verse/** 匹配任意层的路径。" +
               "一个文件都匹配不到时，按路径的尾段匹配：'Verse/*.cs' 读作 '**/Verse/*.cs'。" +
               "'{a,b}' 匹配其中任一写法，{Pawn,Hediff*}.cs 即 Pawn.cs 与 Hediff*.cs。" +
               "重复该选项时，匹配其中任一 glob 的文件都搜索。",
        Default = "*.cs",
        Narrows = true,
    };

    private static readonly CliOption InOption = CliOption.Repeat("in") with
    {
        Aliases = ["path", "within"],
        Placeholder = "<文件或目录>",
        Help = "只搜索该文件或目录，写法与 'read' 指定文件相同：相对反编译根目录的路径、该路径的任意尾段（'Verse/Pawn.cs'）或裸文件名，'.cs' 可省略。目录写法相同（'Verse/AI'），取其下每个文件。" +
               "可重复指定多个。",
        Narrows = true,
    };

    private static readonly CliOption MaxFiles = CliOption.Text("max-files") with
    {
        Aliases = ["scan-limit", "max-scan"],
        Placeholder = "<n>",
        Help = "扫描停止前最多读取多少个文件，在 --file-glob 过滤之后计数，不计因游戏未加载而不进入答案的文件。不指定时读取 glob 选中的每个文件。",
        Default = "全部文件",
    };

    private static readonly CliOption MaxPerFile = CliOption.Text("max-per-file") with
    {
        Aliases = ["per-file", "matches-per-file", "max-matches-per-file"],
        Placeholder = "<n>",
        Help = "每个文件最多输出多少个匹配行。不指定时全部输出。",
        Default = "全部",
    };

    private static readonly CliOption MaxLineChars = CliOption.Text("max-line-chars") with
    {
        Placeholder = "<n>",
        Help = "长于此值的行只输出其命中处附近的内容，略去的字符以 '…' 表示。上下文行显示行首。" +
               "传 0 则每行整行输出。不影响 --json：那里的 text 总是整行。",
        Default = Limits.CodeSearchLineChars.ToString(),
    };

    private static readonly CliOption Source = CliOption.Repeat("source") with
    {
        Aliases = ["tree", "mod", "mods"],
        Placeholder = "<名称>",
        Help = Cjk.Join("要搜索的反编译源码树；可重复指定以搜索多棵。不指定时搜索除",
                        Listed(SourcePlanner.GameDirTrees, describe: true), "以外的每棵源码树。") +
               "本选项与 --scope 选定源码树。选中的源码树里，游戏在快照中未加载的代码不进入答案；指定 --include-unloaded，或由本选项或 --scope 写出该源码树的名称时列入。",
        Narrows = true,
    };

    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只搜索编译进该程序集的文件。");

    private static readonly CliOption ScopeOption = CliOption.Text("scope") with
    {
        Placeholder = "<表达式>",
        Help = "与查 def 的命令用同样的表达式选定源码树：packageId、配置文件中的组名、'vanilla'、'all'，以及表示排除的 '-'。" +
               Cjk.Join("'all' 为除", Listed(SourcePlanner.GameDirTrees), "以外的每棵源码树。") +
               "写出而未排除的 packageId 等同于用 --source 点名该源码树。",
        Narrows = true,
    };

    private static readonly CliOption Context = CliOption.Text("context") with
    {
        Short = 'C',
        Placeholder = "<n|a-b|a+n>",
        Help = "显示每处命中周围的行。数字 N 为上下各 N 行；'0-20' 为上方 0 行、下方 20 行，" +
               "'10+4' 为上方 10 行、下方 4 行。重叠或相接的窗口合并，因此没有行输出两次。",
        Default = "0",
    };

    private static readonly CliOption LimitOption = CommonOptions.Limit("命中行") with
    {
        Help = CommonOptions.Limit("命中行").Help + "配 --files-only 时计的是文件。",
    };

    private static readonly CliOption RegexOption = CliOption.Flag("regex") with
    {
        Hidden = true,
    };

    private static readonly CliOption FilesOnly = CliOption.Flag("files-only") with
    {
        Short = 'l',
        Aliases = ["files-with-matches", "list-files"],
        Help = "每个文件输出一行，取代逐行输出：文件、其中匹配的行数、文件总行数。未指定 regex 时，--source、--scope、--file-glob 与 --in 选中的文件连同行数列出。",
    };

    private static readonly CliOption IgnoreCaseOption = CliOption.Flag("ignore-case") with
    {
        Short = 'i',
        Aliases = ["case-insensitive"],
        Help = "匹配时不区分大小写。",
    };

    private static readonly CliOption NoResolveKeys = CliOption.Flag("no-resolve-keys") with
    {
        Aliases = ["no-translations", "no-lookup-keys", "no-translate", "no-translation-lookup", "code-only"],
        Help = "不解析输出行中出现的翻译键。缺省时，含 \"SomeKey\".Translate() 的输出行会在快照中查出译文，另行列出。本选项不影响命中本身与命中数。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "code-search",
        Aliases = ["grep", "search-code", "regex"],
        Summary = "用正则表达式搜索反编译源码。",
        Remarks =
            "本命令用于只有文本才能回答的问题，如跨所有类型的方法签名模式。符号级的问题用相应命令：" +
            "类型及其派生类型用 'types'，类型的内容用 'members'，" +
            "方法的调用方用 'callers'，单个方法体用 'read --member'。\n\n" +
            "本命令不搜索 def。数据问题（「哪些 def 使用这个 Class」「这个字段取哪些值」）" +
            "归 'where'、'values' 与 'search'。",
        Positionals = [new CliPositional
        {
            Name = "regex",
            NormalizeBackslashesInRefused = true,
            Required = false,
            Help = ".NET 正则表达式。指定 --files-only 时可省略。",
        }],
        Options =
        [
            LoadedCode.IncludeOption,
            FileGlob,
            InOption,
            MaxFiles,
            MaxPerFile,
            MaxLineChars,
            Source,
            AssemblyOption,
            ScopeOption,
            Context,
            LimitOption,
            RegexOption,
            FilesOnly,
            IgnoreCaseOption,
            NoResolveKeys,
        ],
        Refused =
        [
            new()
            {
                Name = "type",
                ValuePlaceholder = "<type>",
                Where = "单个 C# 类型的文件用 --in 选取：'rimsearcher code-search <regex> --in <type>'。",
            },
            new()
            {
                Name = "path-contains",
                ValuePlaceholder = "<文件>",
                Where = "文件或目录用 --in 选取：'rimsearcher code-search <regex> --in <文件>'。",
            },
            new()
            {
                Name = "A",
                ValuePlaceholder = "<n>",
                Where = "命中处下方的行用 --context 写成「上方+下方」：'rimsearcher code-search <regex> -C 0+<n>'。",
            },
            new()
            {
                Name = "B",
                ValuePlaceholder = "<n>",
                Where = "命中处上方的行用 --context 写成「上方+下方」：'rimsearcher code-search <regex> -C <n>+0'。",
            },
        ],
        Examples =
        [
            "rimsearcher code-search \"class \\w+ : ThingComp\"",
            "rimsearcher code-search \"Notify_\\w+\\(\" --context 2",
            "rimsearcher code-search \"PostSpawnSetup\\(\" --context 0+20",
        ],
        JsonKeys =
        [
            new("matches",
                "每个输出行对应一条记录：file、line、is_match、group、text、loaded。上下文行的 is_match 为 false，" +
                "group 为其所属的合并窗口。" +
                "--files-only 下为空，答案由 'files' 承载。loaded 说明游戏在快照中是否加载该文件的代码：" +
                "已加载、mod 不在快照、程序集未加载（这两种只在指定 --include-unloaded 或位于本次调用指定的源码树或程序集中时出现），" +
                "无法打开快照或快照无法判断时为未知。",
                Rows: true),
            new("files",
                "只在 --files-only 下出现，每个文件一行：file、matches（未指定 regex 时为 null）、lines、loaded（同 'matches'）。" +
                "没有匹配时为空数组。"),
            new("absent",
                "--source 指定的源码树存在但不含反编译文件时有一行：layer（'source_tree:' + 树名）、state 为「空」、" +
                "next（填充该源码树的 sync 命令）；其余情形为空。这种情形下没有读取任何内容，因此 'matches' 也为空。",
                Rows: true),
            new("ui_text",
                "只在某个输出的匹配行对快照能解析的字面键调用 .Translate() 时出现：key、translated、original，" +
                "每个不同的键一行。解析不到任何内容的键，以及键在运行时拼出的行，在 notes 中报出；表中不留空行。" +
                "--no-resolve-keys 完全不输出这一项。"),
            NameLookup.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var filesOnly = ctx.Args.Flag(FilesOnly);
        var pattern = ctx.Args.Positional(0);
        if (pattern is null && !filesOnly)
            throw new CliUsageException(
                $"{Operation.Name} 缺少 <regex>：{Operation.Positionals[0].Help}\n" +
                $"运行 '{CommandRegistry.ExeName} {Operation.Name} --help' 查看本命令的用法与参数。");
        if (filesOnly)
            foreach (var perLine in (CliOption[])[Context, MaxPerFile, MaxLineChars])
                if (ctx.Args.Has(perLine))
                    throw new CliUsageException(
                        $"{ctx.Args.Spelled(perLine)} 与 --files-only 不能同时传入：--files-only 不输出匹配行。");

        if (pattern is not null && (pattern.Contains("&lt;") || pattern.Contains("&gt;") || pattern.Contains("&amp;")))
            throw new CliUsageException(
                "所给模式含 HTML 转义（&lt;、&gt;、&amp;），只匹配这些字面字符。" +
                $"改写为：{pattern.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")}");

        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("搜索"));

        var sources = CodeShared.Trees(ctx);
        var sourceName = sources.Count == 1 ? sources[0] : null;
        var scopeExpr = ctx.Args.Value(ScopeOption);
        IReadOnlyList<string> globs = ctx.Args.Values(FileGlob) is { Count: > 0 } given
            ? [.. given.Distinct(StringComparer.OrdinalIgnoreCase)] : ["*.cs"];
        var contextSpec = ctx.Args.Value(Context);
        var (before, after) = ParseContext(contextSpec, out var contextRewritten);
        var limit = ctx.Limit(LimitOption);
        var maxPerFile = PositiveOrEveryOne(ctx, MaxPerFile);
        var lineChars = LineCharCap(ctx);
        if (ctx.Json) lineChars = 0;
        var elision = new Elision();

        Regex? regex = null;
        try
        {
            if (pattern is not null)
                regex = new Regex(pattern,
                    (ctx.Args.Flag(IgnoreCaseOption) ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(Limits.CodeSearchRegexTimeoutMs));
        }
        catch (ArgumentException ex)
        {
            throw new CliUsageException($"这不是有效的正则表达式：{ex.Message}");
        }

        foreach (var t in sources)
            if (!Directory.Exists(Path.Combine(root, t)))
                throw new CliUsageException(NoSuchTree(t, SourcesShared.TreeNames(root)));
        IReadOnlyList<string>? scoped = sources.Count > 1 ? sources : null;
        if (scopeExpr is { Length: > 0 })
        {
            if (sources.Count > 0)
                throw new CliUsageException(
                    $"--source 与 --scope 不能同时传入：二者都选定源码树。'--scope {string.Join(",", sources)}' 读取同样的源码树。");
            scoped = ScopedTrees(ctx, root, scopeExpr);
        }
        var selection = CodeShared.Select(ctx, root, scopeExpr is { Length: > 0 } ? scoped : null,
                                          scopeExpr is { Length: > 0 } ? "--scope" : "--source");
        var byAssembly = selection.Assemblies is not null && sources.Count == 0 && scopeExpr is not { Length: > 0 };
        if (byAssembly)
        {
            if (selection.Trees.Count == 1) sourceName = selection.Trees[0];
            else scoped = selection.Trees;
        }
        var owners = selection.Assemblies is null ? null : new TreeAssemblies(root);
        var outside = 0;
        bool InSelection(string rel)
        {
            if (owners is null) return true;
            var slash = rel.IndexOf('/');
            if (slash > 0 && owners.Owners(rel[..slash], rel[(slash + 1)..]).Any(selection.Keeps)) return true;
            outside++;
            return false;
        }
        if (sources.Count > 1)
            ctx.Report.Absent([.. sources.Where(t => EmptyTree(root, t)).Select(DataLayers.EmptySourceTreeRow)]);

        var rereadGlobs = new List<(string Given, string Read)>();
        globs = [.. globs.Select(g =>
        {
            if (TailGlob(root, sourceName, scoped, g) is not { } tail) return g;
            rereadGlobs.Add((g, tail));
            return tail;
        })];
        var globText = string.Join("、", globs.Select(g => $"'{g}'"));
        var regexes = globs.Select(GlobToRegex).ToList();
        bool Matcher(string rel) => regexes.Any(r => r.IsMatch(rel));
        var scope = selection.Scope(ctx);
        if (scopeExpr is { Length: > 0 })
            foreach (var t in ScopeWords(scopeExpr))
                if (!ctx.Config.ScopeGroups.ContainsKey(t) && scoped!.Contains(t, StringComparer.OrdinalIgnoreCase)) scope.AddTree(t);
        var picked = PickedBy(ctx, root, sources.Count > 0 ? sources : byAssembly ? selection.Trees : null, scope);
        foreach (var glob in globs.SelectMany(ExpandBraces))
            if (glob.IndexOf('/') is > 0 and var globCut && glob[..globCut] is var globTree &&
                globTree.IndexOfAny(['*', '?', '[']) < 0 && Directory.Exists(Path.Combine(root, globTree)))
                scope.AddTree(globTree);
        var maxFiles = PositiveOrEveryOne(ctx, MaxFiles);

        var lines = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var fileRows = new List<IReadOnlyDictionary<string, object?>>();
        var filesListed = 0;
        var filesAttempted = 0;
        var filesRead = 0;
        var filesCandidate = 0;
        var filesWithMatches = 0;
        var totalMatches = 0;
        var printed = 0;
        var filesCapped = false;
        var perFileCapped = 0;
        var timedOut = new List<string>();
        var unreadable = new List<string>();
        var hiddenUnreadable = 0;
        string? partialTree = null;
        var partialRead = 0;
        var partialTotal = 0;
        var unreached = new List<(string Tree, int Files)>();
        LoadedCode? load = null;
        var loadOpened = false;
        var notLoaded = new List<(string Tree, CodeLoad State, string? Assembly)>();
        var leftOut = new List<(string Tree, CodeLoad State, string? Assembly)>();
        var hiddenFiles = new List<(string Tree, CodeLoad State, string? Assembly)>();
        var counted = 0;
        var notLoadedUnmarked = 0;
        (CodeLoad State, string? Assembly) LoadOf(string rel)
        {
            var slash = rel.IndexOf('/');
            if (SourcePlanner.IsReserved(rel[..slash])) return (CodeLoad.Loaded, null);
            if (!loadOpened) { load = LoadedCode.Open(ctx, root); loadOpened = true; }
            return load?.Of(rel[..slash], rel[(slash + 1)..]) ?? (CodeLoad.Unknown, null);
        }

        Func<string, bool> named = picked is not null
            ? _ => true
            : t => scoped?.Contains(t, StringComparer.OrdinalIgnoreCase) == true ||
                   globs.SelectMany(ExpandBraces).Any(g => g.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase));
        foreach (var (tree, files) in EnumerateTrees(root, sourceName, scoped, named))
        {
            var treeFiles = files.Select(f => (Abs: f.FullName, Rel: Rel(root, f.FullName), f.Length,
                                               Ticks: f.LastWriteTimeUtc.Ticks))
                                 .Where(f => Matcher(f.Rel) && (picked is null || picked(f.Rel)) && InSelection(f.Rel))
                                 .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (treeFiles.Count == 0) continue;
            filesCandidate += treeFiles.Count;

            var attemptedHere = 0;
            var readHere = 0;
            var hiddenHere = 0;
            var packOpened = false;
            SourcePack? pack = null;
            foreach (var (abs, rel, length, ticks) in treeFiles)
            {
                var state = LoadOf(rel);
                var hidden = LoadedCode.Unloaded(state.State) && !scope.Covers(tree) &&
                             !(scope.NamesAssemblies && owners is not null &&
                               scope.Covers(tree, owners.Owners(tree, rel[(rel.IndexOf('/') + 1)..])));
                if (hidden)
                {
                    hiddenHere++;
                    filesCandidate--;
                    hiddenFiles.Add((tree, state.State, state.Assembly));
                }
                else
                {
                    if (filesAttempted >= maxFiles) { filesCapped = true; break; }
                    filesAttempted++;
                    attemptedHere++;
                }
                if (!packOpened) { pack = SourcePack.Open(root, tree); packOpened = true; }

                var text = pack?.TryReadLines(rel[(rel.IndexOf('/') + 1)..], length, ticks);
                if (text is null)
                {
                    try { text = File.ReadAllLines(abs); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (hidden) hiddenUnreadable++;
                        else unreadable.Add(rel);
                        continue;
                    }
                }

                if (!hidden)
                {
                    filesRead++;
                    readHere++;
                }

                if (hidden)
                {
                    var any = regex is null;
                    for (var i = 0; !any && i < text.Length; i++)
                    {
                        try { any = regex!.IsMatch(text[i]); }
                        catch (RegexMatchTimeoutException) { break; }
                    }
                    if (any) leftOut.Add((tree, state.State, state.Assembly));
                    continue;
                }

                var hitsHere = 0;
                var toPrint = new List<int>();
                for (var i = 0; regex is not null && i < text.Length; i++)
                {
                    bool hit;
                    try { hit = regex.IsMatch(text[i]); }
                    catch (RegexMatchTimeoutException) { timedOut.Add(rel); break; }
                    if (!hit) continue;

                    hitsHere++;
                    totalMatches++;
                    if (filesOnly) continue;
                    if (toPrint.Count >= maxPerFile) continue;
                    if (printed >= limit.Effective) continue;
                    toPrint.Add(i);
                    printed++;
                }

                if (hitsHere > 0) filesWithMatches++;
                if (hitsHere > 0 || (filesOnly && regex is null))
                {
                    counted++;
                    if (state.State != CodeLoad.Loaded) notLoaded.Add((tree, state.State, state.Assembly));
                }
                var unmarked = (hitsHere > 0 || (filesOnly && regex is null)) && state.State != CodeLoad.Loaded;
                if (filesOnly)
                {
                    if (regex is not null && hitsHere == 0) continue;
                    if (filesListed++ >= limit.Effective) { if (unmarked) notLoadedUnmarked++; }
                    else
                        fileRows.Add(new Dictionary<string, object?>
                        {
                            ["file"] = rel,
                            ["matches"] = regex is null ? null : hitsHere,
                            ["lines"] = text.Length,
                            ["loaded"] = LoadedCode.Value(state.State),
                        });
                    continue;
                }
                if (hitsHere > toPrint.Count && toPrint.Count >= maxPerFile) perFileCapped++;
                if (toPrint.Count > 0)
                    Emit(lines, rows, rel, text, toPrint, before, after, hitsHere, regex!, lineChars, elision,
                         LoadMark(state, load), LoadedCode.Value(state.State));
                else if (unmarked) notLoadedUnmarked++;
            }
            pack?.Dispose();

            if (attemptedHere == 0 && hiddenHere == treeFiles.Count) continue;
            if (attemptedHere == 0) unreached.Add((tree, treeFiles.Count - hiddenHere));
            else if (filesCapped && attemptedHere < treeFiles.Count - hiddenHere)
                { partialTree = tree; partialRead = readHere; partialTotal = treeFiles.Count - hiddenHere; }
        }

        var incomplete = filesCapped || timedOut.Count > 0 || unreadable.Count > 0;
        var found = incomplete ? Tally.AtLeast(totalMatches) : Tally.Complete(totalMatches);

        var nothing = filesOnly ? filesListed == 0 : lines.Count == 0;
        var leftOutSentence = load is null ? null
            : LoadedCode.LeftOut(load, leftOut, "file", regex is null ? "" : "命中");
        if (nothing && filesCandidate == 0 && load is not null && hiddenFiles.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary, (leftOutSentence ?? LoadedCode.LeftOut(load, hiddenFiles, "file"))!);
        else if (nothing && !incomplete && leftOutSentence is not null)
            ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
        else if (nothing)
        {
            if (filesCandidate == 0 && sourceName is { Length: > 0 } && EmptyTree(root, sourceName))
                ctx.Report.Absent(DataLayers.EmptySourceTreeRow(sourceName));
            else if (filesCandidate == 0 && outside > 0)
                ctx.Report.Notice(NoticeKind.NextStep,
                    Cjk.Join(Tally.Complete(outside).Render("file"), "通过其余筛选，但均未编入", selection.Phrase) +
                    "，因此未读取任何文件。");
            else if (filesCandidate == 0 && picked is not null)
                ctx.Report.Notice(NoticeKind.NextStep,
                    Cjk.Join("--in 选中的文件均未通过",
                        ctx.Args.Has(FileGlob) && ctx.Args.Has(ScopeOption)
                            ? "--file-glob 与 --scope"
                            : ctx.Args.Has(ScopeOption) ? "--scope"
                            : ctx.Args.Has(FileGlob) ? "--file-glob" : $"--file-glob {globText}",
                        "的筛选，因此未读取任何文件。"));
            else if (filesCandidate == 0)
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"没有文件匹配 --file-glob {globText}。" +
                    (globs.Any(g => g.Contains('/'))
                        ? "含 '/' 的 glob 匹配整条路径或路径的尾段，'*' 不跨越 '/'；任意深度写作 'vanilla/**/Widgets.cs'。不含 '/' 时匹配任意深度下的文件名。"
                        : string.Concat(globs.SelectMany(ExpandBraces).Where(g => !g.Contains('*') && !g.Contains('?')).Select(g =>
                            $"'{g}' 不含通配符，只匹配恰好为此名的文件。" +
                            (g.TrimStart('.').Contains('.') ? "" : $"按扩展名匹配写 '*.{g.TrimStart('.')}'，") + $"按文件名片段匹配写 '*{g}*'。"))));
            else if (incomplete)
                ctx.Report.Notice(NoticeKind.Truncation,
                    (BreAlternation(pattern ?? "") ?? "") +
                    (filesRead == 0
                        ? "未能读取任何文件。"
                        : Cjk.Join("已读取的", Tally.Complete(filesRead).Render("file"), "中没有任何一行命中。")));
            else
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    (BreAlternation(pattern ?? "") ?? "") +
                    Tally.Complete(filesRead).Render("file") + "中没有任何一行命中" +
                    (regex is not null && sourceName is not { Length: > 0 } && (scoped is null || SelectsFromAll(scopeExpr))
                        ? HiddenMatches(root, regex, Matcher, named) : "") + "。");
                if (IsBareName(pattern ?? "") && WhereElse(ctx, pattern!.Trim('"')) is { } sighting)
                    NameLookup.Say(ctx, sighting);
            }
        }
        else
        {
            var fileTally = filesRead < filesCandidate
                ? Tally.Of(filesRead, filesCandidate) : Tally.Complete(filesRead);
            var read = filesRead < filesCandidate ? $"；读取 {fileTally.Render("file")}" : "";
            var listed = fileRows.Count < filesListed ? $"（列出 {Tally.Complete(fileRows.Count).Render("file")}）" : "";
            var listCount = incomplete
                ? Tally.AtLeast(fileRows.Count)
                : fileRows.Count < filesListed
                    ? Tally.Of(fileRows.Count, filesListed)
                    : Tally.Complete(fileRows.Count);
            if (filesOnly && regex is null)
                ctx.Report.Notice(incomplete || fileRows.Count < filesListed ? NoticeKind.Truncation : NoticeKind.Count,
                    $"{fileTally.Render("file", scope: $"匹配 {globText}")}{listed}。", count: listCount);
            else if (filesOnly)
                ctx.Report.Notice(incomplete || fileRows.Count < filesListed ? NoticeKind.Truncation : NoticeKind.Count,
                    $"{found.Render("match")}，分布于 {Tally.Complete(filesWithMatches).Render("file")}{listed}{read}。", count: listCount);
            else
                ctx.Report.Notice(found.IsTruncated || printed < totalMatches ? NoticeKind.Truncation : NoticeKind.Count,
                    ContextEcho(contextSpec, before, after, contextRewritten) +
                    $"{found.Render("match")}，分布于 {Tally.Complete(filesWithMatches).Render("file")}" +
                    (printed < totalMatches ? $"（输出其中 {printed} 处）" : "") + read + "。",
                    count: incomplete ? Tally.AtLeast(printed)
                         : printed < totalMatches ? Tally.Of(printed, totalMatches) : Tally.Complete(printed));
        }

        if (rereadGlobs.Count > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                "--file-glob " + string.Join("、", rereadGlobs.Select(g => $"'{g.Given}'{ReadCommand.ReadAsPhrase}'{g.Read}'")) + "。");
        if (!nothing && leftOutSentence is not null) ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
        else if (nothing && incomplete && filesCandidate > 0 && leftOutSentence is not null)
            ctx.Report.Notice(NoticeKind.Boundary, leftOutSentence);
        if (loadOpened && (load is null || notLoadedUnmarked > 0))
            LoadedCode.SayNotLoaded(ctx, load, notLoaded, counted, "file", regex is null ? "读取" : "有命中", "这些文件",
                                    "快照未记录该 mod 的程序集，或无法判断该文件属于哪个程序集");

        foreach (var glob in globs.Where(g => filesCandidate > 0 && !g.Contains('/') && !g.Contains('.')))
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join($"'{glob}' 按文件名匹配，不区分大小写；要匹配目录", glob.Trim('*'),
                         "，写", $"'**/{glob.Trim('*')}/**'。"));

        if (perFileCapped > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                Cjk.Join("--max-per-file 限定每个文件最多输出", Tally.Complete(maxPerFile).Render("match"),
                         "，有", Tally.Complete(perFileCapped).Render("file"), "超出该数。"));
        if (elision.Lines > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                Cjk.Join(Tally.Complete(elision.Lines).Render("printed line"), $"按 --max-line-chars {lineChars} 截断。"));
        if (filesCapped) SayFilesCapped(ctx, filesRead,
                                        partialTree, partialRead, partialTotal, unreached);
        if (timedOut.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("有", Tally.Complete(timedOut.Count).Render("file"),
                         $"的匹配耗时超过 {Limits.CodeSearchRegexTimeoutMs} 毫秒，扫描中途跳过：") +
                $"{NameList.Render(timedOut, 6)}。");
        if (unreadable.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("有", Tally.Complete(unreadable.Count).Render("file"), "无法读取，扫描未覆盖：") +
                $"{NameList.Render(unreadable, 6)}。");
        if (hiddenUnreadable > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("另有", Tally.Complete(hiddenUnreadable).Render("file"),
                         "属游戏未加载的代码且无法读取，扫描未覆盖。"));

        if (filesOnly)
        {
            ctx.Report.Promises("files");
            ctx.Report.Table("files", LoadedCode.WithColumn(regex is null ? ["file", "lines"] : ["file", "matches", "lines"],
                                                            fileRows, load),
                             fileRows, unclipped: true);
            return nothing ? 1 : 0;
        }

        if (nothing) return 1;

        ctx.Report.Text("matches", lines, rows);
        if (!ctx.Args.Flag(NoResolveKeys)) ResolveUiText(ctx, rows, elision);
        return 0;
    }

    private static void ResolveUiText(CommandContext ctx, List<IReadOnlyDictionary<string, object?>> rows,
                                      Elision elision)
    {
        var literal = new List<string>();
        var assembled = 0;
        foreach (var row in rows)
        {
            if (row["is_match"] is not true) continue;
            var text = row["text"] as string ?? "";
            if (!text.Contains(".Translate", StringComparison.Ordinal)) continue;

            var hits = TranslateKeyPattern.Matches(text);
            if (hits.Count == 0) { assembled++; continue; }
            foreach (System.Text.RegularExpressions.Match m in hits) literal.Add(m.Groups[1].Value);
        }

        if (literal.Count == 0 && assembled == 0) return;

        IReadOnlyDictionary<string, KeyedRow> found;
        try
        {
            found = literal.Count > 0 ? ctx.Db.KeyedInEffect(literal) : new Dictionary<string, KeyedRow>();
        }
        catch (Exception ex) when (ex is SnapshotFormatError or CliUsageException)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("输出的行中出现", Tally.Complete(literal.Distinct(StringComparer.Ordinal).Count()).Render("translation key")) +
                "，但无法打开任何快照来给出这些键的显示文字。");
            return;
        }

        var distinct = literal.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var resolved = distinct.Where(found.ContainsKey).ToList();
        if (elision.HidTranslate && resolved.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "有 .Translate() 调用落在输出行以「…」省略的部分；下表的键取自整行。");
        if (resolved.Count > 0)
            ctx.Report.Table("ui_text", ["key", "translated", "original"],
                resolved.Select(k => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["key"] = k,
                    ["translated"] = found[k].Placeholder ? null : found[k].Translated,
                    ["original"] = found[k].Original ?? (found[k].Placeholder ? found[k].Translated : null),
                }).ToList());

        var missing = distinct.Where(k => !found.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("这些行里有", Tally.Complete(missing.Count).Render("translation key"),
                         "在此快照中没有 Keyed 译文：") +
                $"{NameList.Render(missing, Limits.MaxSuggestions)}。");

        if (assembled > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("这里有", Tally.Complete(assembled).Render("line"),
                         "对字符串字面量以外的内容调用 .Translate()，因此无法解析这些调用的键。"));
    }

    private static readonly Regex TranslateKeyPattern =
        new("\"([\\w.-]+)\"\\s*\\.\\s*Translate", RegexOptions.Compiled);

    private static void SayFilesCapped(CommandContext ctx, int filesRead,
                                       string? partialTree, int partialRead, int partialTotal,
                                       IReadOnlyList<(string Tree, int Files)> unreached)
    {
        var parts = new List<string>
        {
            Cjk.Join("扫描在读取", Tally.Complete(filesRead).Render("file"), "后停止，因此本次答案不完整。"),
        };

        if (partialTree is not null)
            parts.Add(Cjk.Join("源码树", partialTree, "只读取一部分：") +
                      $"{Tally.Of(partialRead, partialTotal).Render("file")}。");

        if (unreached.Count > 0)
        {
            var names = unreached.Take(NamedTrees).Select(u => u.Tree).ToList();
            var more = unreached.Count - names.Count;
            parts.Add("完全未读取：" + string.Join("、", names) +
                      (more > 0 ? $"，其余 {Tally.Complete(more).Render("source tree")}" : "") +
                      $"；合计 {Tally.Complete(unreached.Count).Render("source tree")}、" +
                      $"{Tally.Complete(unreached.Sum(u => u.Files)).Render("file")}。");
        }

        ctx.Report.Notice(NoticeKind.Truncation, string.Concat(parts));
    }

    private static string? BreAlternation(string pattern) =>
        BrePipe.IsMatch(pattern)
            ? @"'\|' 匹配字面的 '|'；「或」写作 '|'。"
            : null;

    private static readonly Regex BrePipe = new(@"(?<!\\)\\\|", RegexOptions.Compiled);

    private static NameLookup.Sighting? WhereElse(CommandContext ctx, string name)
    {
        try
        {
            var defs = ctx.Db.GetDefsNamed(name);
            return defs.Count > 0 ? NameLookup.AsDef(name, defs) : NameLookup.Locate(ctx, name);
        }
        catch (Exception ex) when (ex is SnapshotFormatError or CliUsageException)
        {
            return null;
        }
    }

    private static bool IsBareName(string pattern) =>
        Regex.IsMatch(pattern, "^\"?[A-Za-z_][A-Za-z0-9_]*\"?$");

    private const int NamedTrees = 5;

    private static void Emit(List<string> lines, List<IReadOnlyDictionary<string, object?>> rows,
                             string rel, string[] text, List<int> hits, int before, int after, int hitsHere,
                             Regex regex, int lineChars, Elision elision, string mark, string loaded)
    {
        var isHit = hits.ToHashSet();
        var group = rows.Count == 0 ? 0 : (int)rows[^1]["group"]! + 1;

        if (lines.Count > 0) lines.Add("");
        lines.Add($"{rel}  {Tally.Complete(hitsHere).Render("match")}" +
                  (hits.Count < hitsHere ? $"（输出其中 {hits.Count} 处）" : "") + mark);

        var pad = Math.Min(text.Length, hits[^1] + 1 + after).ToString().Length;
        var hasWindow = before > 0 || after > 0;

        var i = 0;
        var firstGroup = true;
        while (i < hits.Count)
        {
            var start = Math.Max(0, hits[i] - before);
            var end = Math.Min(text.Length - 1, hits[i] + after);
            while (i + 1 < hits.Count && hits[i + 1] - before <= end + 1)
            {
                i++;
                end = Math.Max(end, Math.Min(text.Length - 1, hits[i] + after));
            }

            if (hasWindow && !firstGroup) lines.Add("--");
            firstGroup = false;
            for (var c = start; c <= end; c++)
            {
                var whole = text[c].TrimEnd();
                var shown = Elide(whole, regex, isHit.Contains(c), lineChars, elision);
                lines.Add($"{(c + 1).ToString().PadLeft(pad)}{(isHit.Contains(c) ? ":" : "-")} {shown}");
                rows.Add(new Dictionary<string, object?>
                {
                    ["file"] = rel,
                    ["line"] = c + 1,
                    ["is_match"] = isHit.Contains(c),
                    ["group"] = group,
                    ["text"] = whole,
                    ["loaded"] = loaded,
                });
            }
            group++;
            i++;
        }
    }

    private static string LoadMark((CodeLoad State, string? Assembly) state, LoadedCode? load) => load is null ? "" : state.State switch
    {
        CodeLoad.ModNotInSnapshot => $"  [mod 不在快照 {load.Snapshot} 中]",
        CodeLoad.AssemblyNotLoaded => $"  [快照 {load.Snapshot} 中程序集 {state.Assembly} 未加载]",
        CodeLoad.Unknown => $"  [快照 {load.Snapshot} 中加载状态未知]",
        _ => "",
    };

    private sealed class Elision
    {
        public int MatchLines;

        public int ContextLines;
        public int Lines => MatchLines + ContextLines;
        public bool HidTranslate;
    }

    private const string Gap = "…";

    private const int MinElideMargin = 8;

    private static string Elide(string line, Regex regex, bool isHit, int cap, Elision tally)
    {
        if (cap <= 0 || line.Length <= cap) return line;

        var spans = new List<(int Start, int End)>();
        if (isHit)
        {
            var margin = Math.Max(MinElideMargin, cap / 4);
            try
            {
                foreach (Match m in regex.Matches(line))
                {
                    var s = Math.Max(0, m.Index - margin);
                    var e = Math.Min(line.Length, m.Index + Math.Max(m.Length, 1) + margin);
                    if (spans.Count > 0 && s <= spans[^1].End)
                        spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, e));
                    else spans.Add((s, e));
                }
            }
            catch (RegexMatchTimeoutException) { spans.Clear(); }
        }

        if (spans.Count == 0) spans.Add((0, Math.Min(line.Length, cap)));

        var text = new System.Text.StringBuilder();
        var budget = cap;
        var covered = 0;
        foreach (var (start, wanted) in spans)
        {
            if (budget <= 0) break;
            var end = Math.Min(wanted, start + budget);
            if (text.Length == 0) { if (start > 0) text.Append(Gap); }
            else text.Append(Gap);
            text.Append(line, start, end - start);
            budget -= end - start;
            covered = end;
        }
        if (covered < line.Length) text.Append(Gap);

        var shown = text.ToString();
        if (isHit) tally.MatchLines++; else tally.ContextLines++;
        if (isHit
            && line.Contains(".Translate", StringComparison.Ordinal)
            && !shown.Contains(".Translate", StringComparison.Ordinal))
            tally.HidTranslate = true;
        return shown;
    }

    private static int LineCharCap(CommandContext ctx)
    {
        var raw = ctx.Args.Value(MaxLineChars);
        if (string.IsNullOrEmpty(raw)) return Limits.CodeSearchLineChars;
        if (int.TryParse(raw, out var n) && n >= 0) return n;
        throw new CliUsageException(
            $"--max-line-chars 取值须为表示字符数的非负整数，传 0 则输出整行（收到 '{raw}'）。");
    }

    private static string Rel(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private const string LudeonCore = "ludeon.rimworld";

    private static IReadOnlyList<string> ScopedTrees(CommandContext ctx, string root, string expr)
    {
        var tokens = ScopeWords(expr);
        bool Named(string t) => tokens.Contains(t, StringComparer.OrdinalIgnoreCase);
        var hidden = HiddenOnDisk(root, Named);
        var trees = SourcesShared.TreeNames(root)
                                 .Where(t => !SourcePlanner.IsSearchedOnlyWhenNamed(t) || Named(t)).ToList();
        var hasVanilla = trees.Contains("vanilla", StringComparer.OrdinalIgnoreCase);
        var words = expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.TrimStart('-').Trim()).ToList();
        var ludeon = words.Concat(ctx.Config.ScopeGroups.Values.SelectMany(g => g))
                          .Where(id => id.StartsWith(LudeonCore, StringComparison.OrdinalIgnoreCase))
                          .Append(LudeonCore);
        var universe = trees.Where(t => !string.Equals(t, "vanilla", StringComparison.OrdinalIgnoreCase))
                            .Concat(hasVanilla ? ludeon : [])
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var filter = ScopeFilter.Parse(expr, universe, ctx.Config);
        bool Ludeon(string id) => id.StartsWith(LudeonCore, StringComparison.OrdinalIgnoreCase);
        var picked = universe.Where(filter.Includes)
                             .Select(id => Ludeon(id) ? "vanilla" : id)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (picked.Count == 0)
        {
            var snapshots = SnapshotCatalog.Enumerate(ctx.Config).Select(e => e.Alias)
                                           .Where(a => words.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            throw new CliUsageException(
                $"--scope '{expr}' 在此未选中任何源码树。" +
                (snapshots.Count > 0
                    ? $"'{snapshots[0]}' 是快照名；--scope 在这里按源码树名匹配。"
                    : "") +
                "mod 的源码树按 packageId 命名，没有程序集的 mod 没有源码树。" +
                (hidden.Count switch
                {
                    0 => "",
                    1 => Cjk.Join(Listed(hidden), "只在点名时选中。"),
                    _ => Cjk.Join(Listed(hidden), "均只在点名时选中。"),
                }) +
                "源码树名单见 'rimsearcher sources list'。");
        }
        var unknown = filter.UnknownTokens.Where(t => !hidden.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join("--scope 中的", NameList.Render(unknown.Select(t => $"'{t}'").ToList(), Limits.MaxSuggestions),
                         "没有匹配任何源码树，未选中任何内容。"));

        var dlcs = words.Where(w => Ludeon(w) && !string.Equals(w, LudeonCore, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dlcs.Count > 0 && hasVanilla)
            ctx.Report.Notice(NoticeKind.Boundary,
                Cjk.Join(NameList.Render(dlcs.Select(d => $"'{d}'").ToList(), Limits.MaxSuggestions),
                         "没有自己的源码树，按整棵 vanilla 源码树读取，Core 也包括在内。"));
        return picked;
    }

    private static List<string> ScopeWords(string expr) => [.. expr.Split(',').Select(t => t.Trim())];

    private static bool SelectsFromAll(string? expr)
        => expr is { Length: > 0 } && (expr.TrimStart().StartsWith('-') ||
           ScopeWords(expr).Any(w => string.Equals(w, "all", StringComparison.OrdinalIgnoreCase)));

    public static string NoSuchTree(string? typed, IEnumerable<string> available)
    {
        var all = available.ToList();
        var close = Suggestion.Closest(all, typed);
        return $"没有名为 '{typed}' 的反编译源码树。" +
               (close.Count > 0
                   ? $"拼写最接近的：{string.Join("、", close)}。mod 的源码树以 packageId 命名。"
                   : "") +
               "源码树名单见 'rimsearcher sources list'。" +
               (all.Count > 0 && all.Count <= Limits.MaxSuggestions
                   ? $"当前全部源码树：{string.Join("、", all)}。"
                   : "");
    }

    private static string HiddenMatches(string root, Regex regex, Func<string, bool> matcher, Func<string, bool> named)
    {
        var hits = HiddenOnDisk(root, named).Select(t => (Tree: t, Matches: TreeMatches(root, t, regex, matcher)))
                                            .Where(x => x.Matches > 0).ToList();
        return hits.Count == 0 ? ""
            : "；未点名的 " + string.Join("、", hits.Select(x => Cjk.Join(x.Tree, "有", Tally.Complete(x.Matches).Render("match"))));
    }

    private static List<string> HiddenOnDisk(string root, Func<string, bool> named)
    {
        var onDisk = SourcesShared.TreeNames(root).ToList();
        return [.. SourcePlanner.GameDirTrees.Where(t => onDisk.Contains(t, StringComparer.OrdinalIgnoreCase) && !named(t))];
    }

    private static string Listed(IEnumerable<string> trees, bool describe = false) => SourcePlanner.Listed(trees, describe);

    private static int TreeMatches(string root, string tree, Regex regex, Func<string, bool> matcher)
    {
        var hits = 0;
        using var pack = SourcePack.Open(root, tree);
        foreach (var f in new DirectoryInfo(Path.Combine(root, tree)).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = Rel(root, f.FullName);
            if (!matcher(rel)) continue;
            var text = pack?.TryReadLines(rel[(rel.IndexOf('/') + 1)..], f.Length, f.LastWriteTimeUtc.Ticks);
            if (text is null)
            {
                try { text = File.ReadAllLines(f.FullName); }
                catch { continue; }
            }
            try { hits += text.Count(regex.IsMatch); }
            catch (RegexMatchTimeoutException) { }
        }
        return hits;
    }

    private static Func<string, bool>? PickedBy(CommandContext ctx, string root, IReadOnlyList<string>? trees,
                                                LoadedCode.ReadScope scope)
    {
        var values = ctx.Args.Values(InOption);
        if (values.Count == 0) return null;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (var value in values)
        {
            var (f, d, via) = PickIn(root, value, trees);
            if (f.Count == 0 && d.Count == 0)
            {
                var names = (trees is null ? SourcePaths.AllFiles(root, null) : trees.SelectMany(t => SourcePaths.AllFiles(root, t))).Select(p => Path.GetFileName(p))
                                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                throw new CliUsageException(
                    Cjk.Join($"--in '{value}'", "在反编译根目录下",
                             trees is null ? "" : Cjk.Join("的源码树", string.Join("、", trees), "中"),
                             "没有对应的文件或目录。") +
                    Suggestion.Say(Suggestion.Closest(names, Path.GetFileName(value.Replace('\\', '/').TrimEnd('/')))));
            }

            files.UnionWith(f);
            dirs.AddRange(d);
            var norm = value.Replace('\\', '/').Trim('/');
            var head = norm.IndexOf('/') is > 0 and var cut ? norm[..cut] : norm;
            if (via is null && Directory.Exists(Path.Combine(root, head)) &&
                f.Concat(d).All(x => x.Equals(head, StringComparison.OrdinalIgnoreCase) ||
                                     x.StartsWith(head + "/", StringComparison.OrdinalIgnoreCase)))
                scope.AddTree(head);

            var named = f.Concat(d.Select(x => x + "/")).ToList();
            if (via is null && named.Count == 1 && f.Count == 1 &&
                string.Equals(f[0], value.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase))
                continue;
            var list = (named.Count > 1 ? $"以下 {named.Count} 项：" : "") + NameList.Render(named, Limits.AmbiguousFiles);
            ctx.Report.Notice(NoticeKind.Boundary, via is null
                ? Cjk.Join($"--in '{value}'", "选中", list) + "。"
                : Cjk.Join($"--in '{value}'", "按路径找不到；按文件名", via, "选中", list) + "。");
        }

        return rel => files.Contains(rel) || dirs.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static (IReadOnlyList<string> Files, IReadOnlyList<string> Dirs, string? Via) PickIn(
        string root, string value, IReadOnlyList<string>? trees)
    {
        if (trees is null) return Pick(root, value, null);
        var each = trees.Select(t => Pick(root, value, t)).ToList();
        return ([.. each.SelectMany(x => x.Files)], [.. each.SelectMany(x => x.Dirs)],
                each.Select(x => x.Via).FirstOrDefault(v => v is not null));
    }

    private static (IReadOnlyList<string> Files, IReadOnlyList<string> Dirs, string? Via) Pick(
        string root, string value, string? sourceName)
    {
        var norm = value.Replace('\\', '/').Trim('/');
        var f = SourcePaths.Files(root, norm, sourceName);
        var d = SourcePaths.Dirs(root, norm, sourceName);
        if (f.Count + d.Count > 0) return (f, d, null);

        var dotted = !norm.Contains('/') && norm.Contains('.') && !norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        if (dotted)
        {
            var slashed = norm.Replace('.', '/');
            f = SourcePaths.Files(root, slashed + ".cs", sourceName);
            d = SourcePaths.Dirs(root, slashed, sourceName);
            if (f.Count + d.Count > 0) return (f, d, null);
        }

        var bare = dotted && ClassNameShape.Looks(norm) ? ClassNameShape.Tail(norm) + ".cs" : Path.GetFileName(norm);
        if (bare.Length > 0 && bare != norm)
        {
            f = SourcePaths.Files(root, bare, sourceName);
            if (f.Count > 0) return (f, [], bare);
        }

        return ([], [], null);
    }

    private static bool EmptyTree(string root, string sourceName)
    {
        var dir = Path.Combine(root, sourceName);
        try { return Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static IEnumerable<(string Tree, IEnumerable<FileInfo> Files)> EnumerateTrees(
        string root, string? sourceName, IReadOnlyList<string>? scoped = null, Func<string, bool>? named = null)
    {
        if (sourceName is { Length: > 0 })
        {
            yield return (sourceName,
                new DirectoryInfo(Path.Combine(root, sourceName)).EnumerateFiles("*", SearchOption.AllDirectories));
            yield break;
        }

        var trees = SourcesShared.TreeNames(root)
                                 .Where(n => scoped is null
                                     ? !SourcePlanner.IsSearchedOnlyWhenNamed(n) || named?.Invoke(n) == true
                                     : scoped.Contains(n, StringComparer.OrdinalIgnoreCase))
                                 .OrderBy(n => n is "vanilla" or "Core" ? 0 : 1)
                                 .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
        foreach (var t in trees)
            yield return (t, new DirectoryInfo(Path.Combine(root, t)).EnumerateFiles("*", SearchOption.AllDirectories));
    }

    internal static (int Before, int After) ParseContext(string? spec)
        => ParseContext(spec, out _);

    internal static (int Before, int After) ParseContext(string? spec, out string? rewritten)
    {
        rewritten = null;
        if (string.IsNullOrEmpty(spec)) return (0, 0);

        var given = spec.Trim();
        var normalized = NormalizeWindow(given);
        if (!string.Equals(normalized, given.Replace(" ", ""), StringComparison.Ordinal))
            rewritten = normalized;
        spec = normalized;

        int At(string s, string what)
            => int.TryParse(s.Trim(), out var v) && v >= 0
                ? v
                : throw new CliUsageException(
                    $"--context 取值须为非负整数行数，'{s.Trim()}'（{what}）不符合。写法如 '8'、'0-20' 或 '10+4'。");

        var dash = spec.IndexOf('-');
        if (dash > 0)
            return (At(spec[..dash], "上方"), At(spec[(dash + 1)..], "下方"));

        var plus = spec.IndexOf('+');
        if (plus > 0)
            return (At(spec[..plus], "上方"), At(spec[(plus + 1)..], "下方"));

        var n = At(spec, "窗口");
        return (n, n);
    }

    private static string NormalizeWindow(string spec)
    {
        var s = spec.Replace(" ", "").Replace("\t", "");
        var chars = new List<char>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is 'L' or 'l' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) continue;
            if (c is '–' or '—' or '−' or ':' or ',') { chars.Add('-'); continue; }
            if (c == '.' && i + 1 < s.Length && s[i + 1] == '.') { chars.Add('-'); i++; continue; }
            chars.Add(c);
        }

        return new string(chars.ToArray());
    }

    private static string ContextEcho(string? spec, int before, int after, string? rewritten)
    {
        if (string.IsNullOrEmpty(spec)) return "";
        var given = spec.Trim();
        var form = rewritten ?? NormalizeWindow(given);
        if (!form.Contains('-', StringComparison.Ordinal) && !form.Contains('+', StringComparison.Ordinal))
            return "";

        var window = Cjk.Join("即上方", Tally.Complete(before).Render("line"), "、下方", Tally.Complete(after).Render("line"));
        return rewritten is not null
            ? $"--context {given}{ReadCommand.ReadAsPhrase}{rewritten}，{window}。"
            : $"--context {given} {window}。";
    }

    private static int PositiveOrEveryOne(CommandContext ctx, CliOption option)
    {
        var raw = ctx.Args.Value(option);
        if (string.IsNullOrEmpty(raw)) return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException(
            $"{option.FlagName} 取值须为正整数（收到 '{raw}'）。");
    }

    private static string? TailGlob(string root, string? sourceName, IReadOnlyList<string>? scoped, string glob)
    {
        if (!glob.Contains('/') || ExpandBraces(glob) is var forms && forms.All(f => f.StartsWith("**/", StringComparison.Ordinal)))
            return null;
        var trees = SourcesShared.TreeNames(root);
        if (forms.Any(f => f.IndexOf('/') is var cut && (cut == 0 || cut > 0 && trees.Contains(f[..cut], StringComparer.OrdinalIgnoreCase))))
            return null;
        var written = GlobToRegex(glob);
        var tail = "**/" + glob;
        var byTail = GlobToRegex(tail);
        var any = false;
        foreach (var (_, files) in EnumerateTrees(root, sourceName, scoped))
            foreach (var f in files)
            {
                var rel = Rel(root, f.FullName);
                if (written.IsMatch(rel)) return null;
                any = any || byTail.IsMatch(rel);
            }
        return any ? tail : null;
    }

    private static Regex GlobToRegex(string glob)
        => new(string.Join('|', ExpandBraces(glob).Select(GlobPattern)), RegexOptions.IgnoreCase);

    private static IReadOnlyList<string> ExpandBraces(string glob)
    {
        for (var open = glob.IndexOf('{'); open >= 0; open = glob.IndexOf('{', open + 1))
        {
            var parts = new List<string>();
            int depth = 0, start = open + 1, close = -1;
            for (var i = open; i < glob.Length && close < 0; i++)
            {
                if (glob[i] == '{') depth++;
                else if (glob[i] == ',' && depth == 1) { parts.Add(glob[start..i]); start = i + 1; }
                else if (glob[i] == '}' && --depth == 0) { parts.Add(glob[start..i]); close = i; }
            }
            if (close < 0 || parts.Count < 2) continue;
            return [.. parts.SelectMany(p => ExpandBraces(glob[..open] + p + glob[(close + 1)..])).Distinct()];
        }
        return [glob];
    }

    private static string GlobPattern(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        var body = sb.ToString();
        return glob.Contains('/') ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
    }
}
