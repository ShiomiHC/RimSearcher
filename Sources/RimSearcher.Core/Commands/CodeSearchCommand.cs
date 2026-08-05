using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 跨文件正则,对象是反编译落盘目录。
///
/// 与 DecompilerServer 的分工:符号级的一切走 MCP;这里保留的独立价值是**任意正则匹配
/// 方法体文本**,即 search_string_literals 覆盖不到的形状搜索。
///
/// **上限分两种,不许混。** <c>--limit</c> 与 <c>--max-per-file</c> 决定**印几行**,
/// 都不缩短扫描,所以命中总数仍是准数;<c>--max-files</c> 决定**读多少**,只有它咬下去
/// 总数才降级成下界。三刀分开声明,合并成一句话调用方就分不清该拧哪个旋钮。
/// </summary>
public sealed class CodeSearchCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "code-search",
        Aliases = ["grep", "search-code", "regex"],
        Summary = "Search the decompiled C# with a regular expression.",
        Remarks =
            "This is for shapes that only text can express, such as a method signature pattern across every class. " +
            "For anything symbol-level — one member's body, callers, overrides, derived types — the DecompilerServer " +
            "MCP answers it from metadata and is both faster and exact.\n\n" +
            "It does not search Defs: the game's XML is not on disk in the form the game ended up with. " +
            "Data questions ('which defs use this class', 'what values does this field take') belong to " +
            "'where', 'values', and 'search', which answer them from the snapshot exactly.\n\n" +
            "Three caps apply, and they divide in two. --limit and --max-per-file decide how many matching " +
            "lines are printed; neither shortens the scan, so the match count stays exact whichever of them " +
            "bites. --max-files decides how much is read, so when that one bites the count drops to a lower " +
            "bound ('at least N') and the answer says which trees it never reached.",
        Positionals = [new PositionalSpec { Name = "pattern", Help = ".NET regular expression." }],
        Options =
        [
            new OptionSpec
            {
                Name = "file-glob",
                // 同一个文件过滤意图被真实调用方拼出 9 种键名。归一化只吃大小写与分隔符的
                // 差异,剩下的换词写法列在这里有意接受。
                // 主名由 R13 定:自由命名 12/12 落在 file-glob,识别复测 10/10。旧主名
                // files 产出式一票没拿到,降为别名;path-glob 是实测的第二名(6/12)。
                Aliases = ["path-glob", "files", "file-filter", "glob", "file-pattern", "file-extension", "file-type", "path-filter", "include"],
                Placeholder = "<glob>",
                Help = "Only search files whose path matches this glob. A glob with no '/' matches the file name " +
                       "alone (*.cs is every .cs file at any depth); with a '/' it matches the path relative to " +
                       "the decompiled root, which begins with the source tree's name even under --source, and " +
                       "there '*' stops at a '/' while '**' crosses it. So */Verse/* is one level down, " +
                       "**/Verse/** is any.",
                Default = "*.cs",
                Narrows = true,
            },
            new OptionSpec
            {
                // 上限防一条正则扫穿整棵树;必须可调,否则单棵树本身超限时无路可走。
                Name = "max-files",
                Aliases = ["file-limit", "scan-limit", "max-scan"],
                Placeholder = "<n|all>",
                Help = "How many files the scan may read before it stops, counted after --file-glob has filtered. " +
                       "Pass 'all' to lift the cap. This is the only cap that can make the answer partial.",
                Default = Limits.CodeSearchMaxFiles.ToString(),
            },
            new OptionSpec
            {
                // 这道闸**只管印**:过上限的命中照样计数,于是总数保持准数,用不着降级成
                // 三态文法里的「at least」。
                Name = "max-per-file",
                Aliases = ["per-file", "matches-per-file", "max-matches-per-file", "file-preview"],
                Placeholder = "<n|all>",
                Help = "How many matching lines to print from any one file. Matches past it are still counted, " +
                       "so the total stays exact. Pass 'all' to print every one.",
                Default = Limits.CodeSearchMatchesPerFile.ToString(),
            },
            new OptionSpec
            {
                Name = "source",
                Aliases = ["root", "tree", "scope"],
                Placeholder = "<name>",
                Help = "Which decompiled source tree to search. Omit to search them all.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "context",
                Short = 'C',
                Aliases = ["context-lines", "around"],
                Placeholder = "<n>",
                Help = "Show this many lines above and below each match. Windows that overlap or touch are " +
                       "merged, so no line is printed twice.",
                Default = "0",
            },
            // 名词必须是「行」而不是「matches」:--limit 管印几行,命中数照样数全。
            CommonOptions.Limit("matching lines"),
            new OptionSpec
            {
                Name = "ignore-case",
                Short = 'i',
                Arity = Arity.Flag,
                Aliases = ["case-insensitive"],
                Help = "Match without regard to letter case.",
            },
            new OptionSpec
            {
                // 默认开:默认关等于把能力藏起来,而「这行打印的是什么字」正是拿着一行
                // .Translate() 的人下一句要问的。
                Name = "no-resolve-keys",
                Arity = Arity.Flag,
                // 主名与别名各由一头的实测定。识别测:no-resolve-keys 7/10,而危险的那种误读
                // (读成「按内容过滤命中、结果行数变少」)零例;旧主名 no-ui-text 只有 2/10,
                // 6/10 正落在那种误读上 —— 而加不加这个开关,命中计数逐字不变,输出不会纠正他。
                // 产出式:12/12 伸手去写的是 no-translations,于是它留作别名接住;可它的识别测
                // 只有 2/12,8/12 把它读成「搜索时排除翻译数据」,当不了主名。
                // ui-text 那一族两头都不占,不留 —— 真实调用记录里 832 次 code-search
                // 对这个开关的三种写法是 0 次,删掉不会有人撞上。
                Aliases = ["no-translations", "no-lookup-keys", "no-translate", "no-translation-lookup", "code-only"],
                Help = "Do not resolve translation keys found in the printed lines. By default, a printed line " +
                       "containing \"SomeKey\".Translate() gets its displayed text looked up in the snapshot and " +
                       "listed separately. This only removes that extra table — the matches themselves, and the " +
                       "match count, are the same either way.",
            },
        ],
        Examples =
        [
            "rimsearcher code-search \"class \\w+ : ThingComp\"",
            "rimsearcher code-search \"Notify_\\w+\\(\" --context 2",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "matches",
                Rows = true,
                What = "one row per printed line — file, line, is_match, group, text. Context lines come " +
                       "through with is_match false, and 'group' is the merged window they belong to, so the " +
                       "text form's '--' separator needs no counterpart here.",
            },
            new()
            {
                Key = "ui_text",
                What = "present only when a printed matching line calls .Translate() on a literal key that the " +
                       "snapshot can resolve — key, translated, original, one row per distinct key. Keys that " +
                       "resolve to nothing, and lines whose key is assembled at runtime, are reported in the " +
                       "notes rather than as empty rows. Suppressed entirely by --no-resolve-keys.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var pattern = ctx.Args.Positional(0)!;

        // 真实调用方发过 HTML 转义形态的 pattern(&lt;defName&gt;),必然零命中,直接说破。
        if (pattern.Contains("&lt;") || pattern.Contains("&gt;") || pattern.Contains("&amp;"))
            throw new CliUsageException(
                "The pattern contains HTML escapes (&lt; &gt; &amp;), which match those literal characters and " +
                $"therefore never match source code. Write it as: {pattern.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")}");

        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("search"));

        var sourceName = ctx.Args.Value("source");
        var glob = ctx.Args.Value("file-glob") ?? "*.cs";
        var contextLines = ctx.Args.Int("context", 0);
        var limit = ctx.Limit();
        var maxPerFile = PositiveOrAll(ctx, "max-per-file", Limits.CodeSearchMatchesPerFile);

        Regex regex;
        try
        {
            regex = new Regex(pattern,
                (ctx.Args.Flag("ignore-case") ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(Limits.CodeSearchRegexTimeoutMs));
        }
        catch (ArgumentException ex)
        {
            throw new CliUsageException($"The pattern is not a valid regular expression: {ex.Message}");
        }

        if (sourceName is { Length: > 0 } && !Directory.Exists(Path.Combine(root, sourceName)))
            throw new CliUsageException(NoSuchTree(sourceName, SourcesShared.TreeNames(root)));

        var matcher = GlobToRegex(glob);
        var maxFiles = PositiveOrAll(ctx, "max-files", Limits.CodeSearchMaxFiles);

        var lines = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var filesRead = 0;
        var filesCandidate = 0;     // 过了 --file-glob 的文件,不管读没读 —— 「N of M」的那个 M
        var filesWithMatches = 0;
        var totalMatches = 0;       // 找到多少
        var printed = 0;            // 印出来多少 —— 与上一个不是一件事
        var filesCapped = false;
        var perFileCapped = 0;
        var timedOut = new List<string>();
        var treesRead = 0;
        var treesTotal = 0;
        string? partialTree = null;
        var partialRead = 0;
        var partialTotal = 0;
        var unreached = new List<(string Tree, int Files)>();

        foreach (var (tree, files) in EnumerateTrees(root, sourceName))
        {
            // 树内顺序必须显式:文件数上限先到先得,靠文件系统枚举顺序会让同一条命令在两台
            // 机器上给出不同答案。树间顺序同样显式(vanilla 优先)。
            var treeFiles = files.Select(f => (Abs: f, Rel: Rel(root, f)))
                                 .Where(f => matcher.IsMatch(f.Rel))
                                 .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            // 一个匹配文件都没有的树既不算读过、也不算「没读到」:磁盘上有空目录树,
            // 把它们点进「没读到」名单会读成「还有一大片代码没看」。
            if (treeFiles.Count == 0) continue;
            treesTotal++;
            filesCandidate += treeFiles.Count;

            var readHere = 0;
            foreach (var (abs, rel) in treeFiles)
            {
                if (filesRead >= maxFiles) { filesCapped = true; break; }
                filesRead++;
                readHere++;

                string[] text;
                try { text = File.ReadAllLines(abs); }
                catch { continue; }

                var hitsHere = 0;
                var toPrint = new List<int>();
                for (var i = 0; i < text.Length; i++)
                {
                    bool hit;
                    try { hit = regex.IsMatch(text[i]); }
                    catch (RegexMatchTimeoutException) { timedOut.Add(rel); break; }
                    if (!hit) continue;

                    hitsHere++;
                    totalMatches++;
                    // 两道印刷闸只跳过**印**:continue 而非 break,否则总数变成「印满为止」的数。
                    if (toPrint.Count >= maxPerFile) continue;
                    if (printed >= limit.Effective) continue;
                    toPrint.Add(i);
                    printed++;
                }

                if (hitsHere > 0) filesWithMatches++;
                if (hitsHere > toPrint.Count && toPrint.Count >= maxPerFile) perFileCapped++;
                if (toPrint.Count > 0) Emit(lines, rows, rel, text, toPrint, contextLines, hitsHere);
            }

            if (readHere == 0) unreached.Add((tree, treeFiles.Count));
            else
            {
                treesRead++;
                if (readHere < treeFiles.Count)
                    { partialTree = tree; partialRead = readHere; partialTotal = treeFiles.Count; }
            }
        }

        // 扫描完不完整只由「读没读全」决定,印几行不影响:不完整时命中数降级成下界,
        // 完整时它是准数,哪怕只印出来其中几行。
        var incomplete = filesCapped || timedOut.Count > 0;
        var found = incomplete ? Tally.AtLeast(totalMatches) : Tally.Complete(totalMatches);

        if (lines.Count == 0)
        {
            // 零命中有四种成因,下一步完全不同,不许合并成一句:
            //   树在但一个文件都没有(程序集从没反编译)—— 该 sync,不是该改 glob;
            //   glob 一个文件都没打中 —— 该改 glob,不是该换数据源;
            //   没读完 —— 结论无效,该抬闸,且**不指路去别的数据源**;
            //   真读完了也没有 —— 这时才提示去 search / where 问 def。
            if (filesCandidate == 0 && sourceName is { Length: > 0 } && EmptyTree(root, sourceName))
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"The source tree '{sourceName}' exists but holds no decompiled files at all, so the glob " +
                    "never came into it. Its assemblies have not been decompiled (or the tree was emptied): " +
                    "'rimsearcher sources sync' rebuilds it from what the snapshot's mods load, and " +
                    "'rimsearcher sources list' shows which trees are in that state.");
            else if (filesCandidate == 0)
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No file matched --file-glob '{glob}', so nothing was read at all." +
                    (glob.Contains('/')
                        ? " A glob containing '/' is matched against the whole path relative to the decompiled " +
                          "root, which begins with the source tree's name: 'vanilla/**/Widgets.cs', not " +
                          "'Verse/Widgets.cs'. Without a '/' it matches the file name alone at any depth."
                        // 别名叫 extension 而值一律按 glob 解:`--file-extension cs` 要求整个
                        // 文件名就叫 cs,其零结果与「这里没有 .cs 文件」逐字同形。
                        : glob.Contains('*') || glob.Contains('?')
                            ? ""
                            : $" The value carries no wildcard, so it had to equal a whole file name: '{glob}' " +
                              $"matches a file literally named that. For an extension write '*.{glob.TrimStart('.')}', " +
                              $"for a name fragment write '*{glob}*'.") +
                    " 'rimsearcher sources list' names the trees.");
            else if (incomplete)
                ctx.Report.Notice(NoticeKind.Truncation,
                    $"No line matched in the {Tally.Complete(filesRead).Render("file")} that were read, " +
                    "but the scan did not finish, so this is not evidence that nothing matches.");
            else
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No line matched in {Tally.Complete(filesRead).Render("file")} under '{glob}'" +
                    Framing(root, sourceName, treesTotal, glob) + ". " +
                    // 「反编译时就抹掉了」排在 def 那句之前:它是唯一一种再怎么扫都不会有的成因。
                    (Erased(ctx.Args.Positional(0)!) is { } erased ? erased + " " : "") +
                    "If you were looking for a def rather than code, 'rimsearcher search' and 'rimsearcher where' " +
                    "answer that from the snapshot — the XML is not searched here.");
        }
        else
        {
            // 命中数放最前,否则读的人会把文件数当成命中数。「找到多少」与「印了多少」
            // 用括号并列不用动词:NounRegistry 只管名词复数,主谓一致得靠句子结构避开。
            var fileTally = filesRead < filesCandidate
                ? Tally.Of(filesRead, filesCandidate) : Tally.Complete(filesRead);
            var treeTally = treesRead < treesTotal
                ? Tally.Of(treesRead, treesTotal) : Tally.Complete(treesTotal);
            ctx.Report.Notice(found.IsTruncated || printed < totalMatches ? NoticeKind.Truncation : NoticeKind.Count,
                $"{found.Render("match")} in {Tally.Complete(filesWithMatches).Render("file")}" +
                (printed < totalMatches ? $" ({printed} printed)" : "") +
                $"; {fileTally.Render("file")} read" +
                (treesRead < treesTotal
                    ? $" across {treeTally.Render("source tree")}"
                    : Framing(root, sourceName, treesTotal, glob)) + ".",
                // 这句话里有四个数(命中 / 文件 / 读了几个文件 / 几棵树),进 JSON 结构化那对
                // 只能有一个口径 —— 全仓统一取「本命令那张表的行」,这里就是印出来的命中行。
                count: printed < totalMatches ? Tally.Of(printed, totalMatches) : Tally.Complete(printed));
        }

        // `--snapshot vanilla` 与 `--source vanilla` 逐字同形,而在这条命令上前者一寸范围都
        // 不收 —— 扫的是磁盘上的反编译树,快照只在解释 .Translate() 时才碰得到。实证:
        // `code-search "class \w+ : Pawn" --snapshot vanilla` 印出七条全在某个 mod 树里的
        // 命中,而调用方据此判定「指定了 vanilla 还是串了别的源」。
        //
        // **无条件发**,不看这次开没开库:哪怕印出来的行里有 .Translate()、快照真被查过,
        // 「它没有收窄这次搜索」照样成立,而那正是要说破的那一件。否定不许跟着分支。
        //
        // 位置在计数句之后、各条闸之前 —— 这是取景不是脚注。唯一与之并排的信号是计数句
        // 尾巴上那句「across 26 source trees」,它读起来是常规取景,不会纠正任何人。
        //
        // 只对 --snapshot 发,不对 --db:混淆是**名字形状**的(一个别名看起来就像一棵树名),
        // 而一条路径不会被当成树名。
        if (ctx.Args.Has("snapshot"))
            ctx.Report.Notice(NoticeKind.Boundary,
                $"--snapshot {ctx.Args.Value("snapshot")} did not narrow this search. A snapshot holds the " +
                "game's defs and translations; the C# read here comes from the decompiled trees on disk, and " +
                "--source is what picks among those. 'rimsearcher sources list' names them.");

        // 不带 '/' 也不带 '.' 的 glob 是**按命名空间取景**的写法落到了文件名上。
        // 盲测里六份里六份把「只搜 Verse 命名空间」写成 --file-glob '*Verse*',而它挑出的
        // 45 个文件没有一个在 Verse 下 —— Overseer、HediffGiverSet 这些名字里恰好含 verse。
        // 印出来是一份计数完整、语气笃定的正常答案,与「Verse 下就这么多」逐字同形。
        // 带扩展名的写法(*.cs / *Comp*.cs)不报:那种写法本身就在说文件名,没有这层歧义。
        if (filesCandidate > 0 && !glob.Contains('/') && !glob.Contains('.'))
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{glob}' carries no '/', so it selected by file name alone and ignored case: the files read " +
                $"are the ones whose name matches, not the ones inside a directory called " +
                // 建议必须给 '**':'*' 不跨 '/',而路径是 <tree>/<assembly>/<namespace>/<file>.cs
                // 四段起,'*/Verse/*' 一个文件都挑不出来。给一条敲了没用的命令比不给更坏。
                $"'{glob.Trim('*')}'. For a directory write '**/{glob.Trim('*')}/**'.");

        // 三个旋钮各自申报,因为被截的原因不同,该拧的也不同。
        // 两句都以旋钮自己作主语、计数放进从句:动词没有登记处,主谓一致只能靠句子结构避开。
        // 「印刷闸不缩短扫描,所以总数仍是准数」这条规则搬进了 SKILL.md —— 它逐字不随
        // 查询变。留下的是这一次的数:闸卡在几条、有几个文件超了。
        if (printed < totalMatches && printed >= limit.Effective)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"--limit stopped the printing at {Tally.Complete(limit.Effective).Render("match")}; " +
                "raise it to see more.");
        if (perFileCapped > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"--max-per-file allows {Tally.Complete(maxPerFile).Render("match")} from any one file, and " +
                $"{Tally.Complete(perFileCapped).Render("file")} had more than that. Raise it to see the rest.");
        if (filesCapped) SayFilesCapped(ctx, sourceName, glob, filesRead,
                                        partialTree, partialRead, partialTotal, unreached);
        if (timedOut.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The pattern took longer than {Limits.CodeSearchRegexTimeoutMs} ms on " +
                $"{Tally.Complete(timedOut.Count).Render("file")}, which were skipped part-way. " +
                "A pattern with nested quantifiers is the usual cause.");

        if (lines.Count == 0) return 1;

        ctx.Report.Text("matches", lines, rows);
        if (!ctx.Args.Flag("no-resolve-keys")) ResolveUiText(ctx, rows);
        return 0;
    }

    /// <summary>
    /// 印出来的命中行里那些 <c>"SomeKey".Translate()</c> 显示成什么字。
    ///
    /// 三条边界都要说破,因为它们的沉默各自与一句错结论同形:
    ///
    /// 1. **拼出来的 key**(<c>("Stat_" + x).Translate()</c>)静态取不到 —— 不点名的话,
    ///    「这一行没被解释」会被读成「这个 key 没有译文」。
    /// 2. **字面量 key 在 keyed 层里查不到**:可能是 def 的 label key(那层走 DefInjected)、
    ///    也可能是代码里留下的死 key。
    /// 3. **没有可用快照**时整节缺席。code-search 本身不需要快照,所以取不到库不能让命令
    ///    失败 —— 但也不能静默,静默就等于宣布这些 key 没有译文。
    /// </summary>
    private static void ResolveUiText(CommandContext ctx, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var literal = new List<string>();
        var assembled = 0;
        foreach (var row in rows)
        {
            if (row["is_match"] is not true) continue;          // 上下文行不算
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
            // 这里才第一次碰快照,于是「用哪个快照」那句播报不会出现在纯代码搜索的输出里。
            found = literal.Count > 0 ? ctx.Db.KeyedInEffect(literal) : new Dictionary<string, KeyedRow>();
        }
        catch (Exception ex) when (ex is SnapshotFormatError or CliUsageException)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(literal.Distinct(StringComparer.Ordinal).Count()).Render("translation key")} " +
                "appear in the printed lines, but no snapshot could be opened to say what they display, so " +
                "this answer says nothing either way about them. 'rimsearcher snapshot list' shows what is " +
                "registered; pass --no-resolve-keys to stop asking.");
            return;
        }

        var distinct = literal.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var resolved = distinct.Where(found.ContainsKey).ToList();
        if (resolved.Count > 0)
            ctx.Report.Table("ui_text", ["key", "translated", "original"],
                resolved.Select(k => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["key"] = k,
                    ["translated"] = found[k].Placeholder ? null : found[k].Translated,
                    ["original"] = found[k].Original ?? (found[k].Placeholder ? found[k].Translated : null),
                }).ToList());

        // 下面两句的主语都是固定单数(this snapshot / the key),计数进宾语或从句 ——
        // NounRegistry 管名词复数、**不管主谓一致**,「1 key … have」只能靠句子结构避开。
        var missing = distinct.Count - resolved.Count;
        if (missing > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"This snapshot has no keyed translation for " +
                $"{Tally.Complete(missing).Render("translation key")} in these lines. A def's own label goes " +
                "through DefInjected rather than a key ('rimsearcher get' and 'search' cover those), and a key " +
                "no language file declares is one the code no longer reaches.");

        if (assembled > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The key is not a literal in {Tally.Complete(assembled).Render("line")} here — assembled at " +
                "runtime, or held in a variable — so what those lines display cannot be resolved from the text. " +
                "'rimsearcher keyed' can still show such a key by name once you know it.");
    }

    /// <summary>
    /// <c>"SomeKey".Translate</c> 里那个 key。空白容忍是有意的:换行格式化过的调用
    /// (<c>"Key"\n  .Translate()</c>)在按行扫描时本来就分在两行,这里只多认同一行内的空格。
    /// </summary>
    private static readonly Regex TranslateKeyPattern =
        new("\"([A-Za-z0-9_.]+)\"\\s*\\.\\s*Translate", RegexOptions.Compiled);

    /// <summary>
    /// 文件数上限咬下去时说什么:说破「某棵树只读了一部分」并给出几分之几;点名没读到的树
    /// 与各自还剩多少文件;不点名空树;<c>--source</c> 已经给出时不再把它列成补救。
    /// </summary>
    private static void SayFilesCapped(CommandContext ctx, string? sourceName, string glob, int filesRead,
                                       string? partialTree, int partialRead, int partialTotal,
                                       IReadOnlyList<(string Tree, int Files)> unreached)
    {
        var parts = new List<string>
        {
            $"The scan stopped after reading {Tally.Complete(filesRead).Render("file")}, " +
            "so this answer is partial rather than complete.",
        };

        if (partialTree is not null)
            parts.Add($"'{Label(partialTree)}' was read only in part: " +
                      $"{Tally.Of(partialRead, partialTotal).Render("file")}.");

        if (unreached.Count > 0)
        {
            // 名单要有上限:树可以有几十棵,全点名会占满一屏。
            var names = unreached.Take(NamedTrees).Select(u => Label(u.Tree)).ToList();
            var more = unreached.Count - names.Count;
            parts.Add("Never read at all: " + string.Join(", ", names) +
                      (more > 0 ? $" and {more} more" : "") +
                      $" — {Tally.Complete(unreached.Count).Render("source tree")}, " +
                      $"{Tally.Complete(unreached.Sum(u => u.Files)).Render("file")}.");
        }

        var narrow = new List<string>();
        if (string.IsNullOrEmpty(sourceName)) narrow.Add("--source <tree>");
        if (!ctx.Args.Has("file-glob")) narrow.Add("--file-glob <glob>");
        parts.Add("Raise the cap with --max-files all" +
                  (narrow.Count > 0 ? $", or narrow with {string.Join(" or ", narrow)}." : "."));

        ctx.Report.Notice(NoticeKind.Truncation, string.Join(" ", parts));
    }

    /// <summary>
    /// 这个模式指的东西是不是**反编译时就被抹掉**的那一类 —— 是的话再怎么扫都不会有命中,
    /// 而「零命中」与「代码里没这回事」逐字同形。不是就回 null,一个字不说。
    ///
    /// 两条判据:
    ///   注释 —— 作者写的注释一条都不留,树里的 `//` 几乎全是 ILSpy 自己的备注
    ///           (「ILSpy generated this…」「try-fault」「yield-return decompiler failed」)。
    ///   局部变量 —— 方法体内的局部名留不住,ILSpy 按初始化表达式现编(num、list、flag);
    ///           **参数名留着**,它在元数据里。
    ///
    /// 裸标识符那一条要求模式里没有正则元字符且首字母小写:带元字符的模式是在找一种形状,
    /// 不是在找一个记得住名字的变量,对它说这句话就是每次落空都挂的免责声明。
    /// </summary>
    private static string? Erased(string pattern)
    {
        if (pattern.Contains("//", StringComparison.Ordinal) || pattern.Contains("/*", StringComparison.Ordinal))
            return "These are decompiler output: no comment written by the author survives, and the few '//' " +
                   "lines present are ILSpy's own notes about what it could not translate.";

        if (pattern.Length > 1 && char.IsLower(pattern[0]) &&
            pattern.All(c => char.IsLetterOrDigit(c) || c == '_'))
            // 举的名字是**生成规则**的例子而不是名单,所以不走 NameList,也不写省略号 ——
            // 省略号会读成「还有几条没列」。
            return "These are decompiler output. Local variable names do not survive it — ILSpy re-invents them " +
                   $"from the assignment, giving names like num, num2, list and flag, so '{pattern}' can only " +
                   "turn up here if it is a type, member, parameter or string literal name, never if it was a local.";

        return null;
    }

    /// <summary>没读到的树最多点几个名。</summary>
    private const int NamedTrees = 5;

    /// <summary>根目录下直接摆着的文件那棵伪树没有名字,得有个说法。</summary>
    private static string Label(string tree)
        => tree.Length > 0 ? tree : "the files directly under the decompiled root";

    /// <summary>
    /// 上下文窗口。**重叠或相邻的窗口合并**:每条命中各印一窗的话,<c>-C 2</c> 打在连着的
    /// 命中上会把同一行印好几遍,既浪费读 stdout 的上下文预算,又让人以为那里真有好几处命中。
    /// 分隔符只在两组之间出现,不留尾巴 —— 尾部空隔符会被读成「后面还有,被截了」。
    ///
    /// 路径**每文件说一次**,不逐行重复:一条深路径四十几个字符,乘上 <c>-C 3</c> 的一屏行数
    /// 就是几百字节的同一个字,而 <c>read</c> 早就是这个形态(路径一行,行号一列)。
    /// 标题带上该文件的命中数 —— 一屏 <c>-</c> 上下文行里有几条是真命中,数出来比看出来快;
    /// <c>--max-per-file</c> 咬下去时两个数分开报,否则「印了几行」会被当成「有几条」。
    /// 结构化侧不受影响:<c>file</c> 本来就在每一行上,JSON 消费方拿到的东西一个字没变。
    /// </summary>
    private static void Emit(List<string> lines, List<IReadOnlyDictionary<string, object?>> rows,
                             string rel, string[] text, List<int> hits, int context, int hitsHere)
    {
        var isHit = hits.ToHashSet();
        var group = rows.Count == 0 ? 0 : (int)rows[^1]["group"]! + 1;

        // 文件之间空一行,标题才不会粘在上一个文件的最后一行代码上。
        if (lines.Count > 0) lines.Add("");
        lines.Add($"{rel}  {Tally.Complete(hitsHere).Render("match")}" +
                  (hits.Count < hitsHere ? $", {hits.Count} printed" : ""));

        // 行号右对齐。宽度按**这次真印出来的**最大行号算,不按文件总行数:后者会让只印了
        // 第 3 行的千行文件补出一串前导空格,与旁边只印了第 3 行的短文件参差着排。
        // hits 升序,末项加上下文窗口的下沿就是最大的那个。
        var pad = Math.Min(text.Length, hits[^1] + 1 + context).ToString().Length;

        var i = 0;
        var firstGroup = true;
        while (i < hits.Count)
        {
            var start = Math.Max(0, hits[i] - context);
            var end = Math.Min(text.Length - 1, hits[i] + context);
            while (i + 1 < hits.Count && hits[i + 1] - context <= end + 1)
            {
                i++;
                end = Math.Max(end, Math.Min(text.Length - 1, hits[i] + context));
            }

            // 分隔符只在**同一文件内**的两组之间:换文件由标题自己隔开,那里再插一条 "--"
            // 会读成标题下面还漏印了什么。
            if (context > 0 && !firstGroup) lines.Add("--");
            firstGroup = false;
            for (var c = start; c <= end; c++)
            {
                lines.Add($"{(c + 1).ToString().PadLeft(pad)}{(isHit.Contains(c) ? ":" : "-")} {text[c].TrimEnd()}");
                // 文本侧那条 "--" 分隔符在结构化侧变成 group 序号:JSON 里不插假行表示断开。
                rows.Add(new Dictionary<string, object?>
                {
                    ["file"] = rel,
                    ["line"] = c + 1,
                    ["is_match"] = isHit.Contains(c),
                    ["group"] = group,
                    ["text"] = text[c].TrimEnd(),
                });
            }
            group++;
            i++;
        }
    }

    /// <summary>路径一律相对**根目录**,于是它带着树名,而且不随 --source 改变形状。</summary>
    private static string Rel(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>
    /// <c>--source</c> 打不中时说什么。
    ///
    /// 树名是 packageId,而人记得的往往是个外号(HAR、miho)—— 外号不在任何数据里,打分器
    /// 只能给出**看起来像但是错的**那一个(<c>HAR</c> → <c>brrainz.harmony</c>)。一个错的
    /// 独家建议比没有建议更坏,故永远同时指向名单,并说破「外号匹配不上任何东西」这条成因。
    /// </summary>
    public static string NoSuchTree(string? typed, IEnumerable<string> available)
    {
        var all = available.ToList();
        // 候选走 Suggestion,句子不走 —— 「树名是 packageId」是这条命令独有的成因,
        // 不该挤进公共措辞里。
        var close = Suggestion.Closest(all, typed);
        return $"No decompiled source tree named '{typed}'." +
               (close.Count > 0
                   ? $" Closest by spelling: {string.Join(", ", close)} — but tree names are packageIds, " +
                     "so a nickname matches nothing. "
                   : " ") +
               "'rimsearcher sources list' names every tree." +
               (all.Count > 0 && all.Count <= Limits.MaxSuggestions
                   ? $" Here they are: {string.Join(", ", all)}."
                   : "");
    }

    /// <summary>
    /// 按**确定顺序**逐棵源码树走,而不是把整个根目录一把 EnumerateFiles:文件数上限先到
    /// 先得,交给文件系统顺序就等于让「哪棵树被看见」取决于目录名的字母序。vanilla 排前面
    /// 是有意的,它是问题的默认语境。
    ///
    /// 什么算一棵树问 <see cref="SourcesShared.TreeNames"/>,这里不自己判。
    /// 路径一律相对根目录,<c>--source</c> 只是少走几棵树,不改变文件的名字。
    /// </summary>
    /// <summary>目录在,里面一个文件都没有 —— 与「目录不在」和「glob 没打中」是三件事。</summary>
    /// <summary>
    /// 取景 —— 这一次扫的是哪一片。
    ///
    /// 窄化时**说得更多**而不是更少:点名扫了哪一棵,以及有几棵没扫、怎么把它们扫上。
    /// 少说的话,「1 match in 1 file; 10222 files read.」与「全库扫完只有这一条」逐字同形。
    /// </summary>
    private static string Framing(string root, string? sourceName, int treesTotal, string glob)
    {
        var onDisk = SourcesShared.TreeNames(root).Count();

        if (string.IsNullOrEmpty(sourceName))
        {
            // 这里的树数与 `sources list` 的棵数谁也不解释谁,差额会被当成「几棵没扫的代码」。
            // 说清它数的是什么(按这个 --file-glob 挑得出文件的树),差额就不再是未知量;
            // 「the rest」不带数,避免主谓跟着数走(NounRegistry 只管名词)。
            //
            // 差额在就一定说,哪怕只剩一棵树被扫到:否则「2 files read.」与「全库就这么多」
            // 逐字同形。
            // 差额的两种成因要分得开:glob 挑不出文件(正常),与那棵树压根没反编译过
            // (该去 sync)。只说前者的话,一棵空树会被读成「那里的代码扫过了,没有」。
            //
            // 后半句是**指路**不是断言:这一次的差额里有没有空树,这里并不知道 ——
            // 写成「其中有些从没反编译过」在差额全是 glob 不匹配时就是一句假话。
            if (treesTotal < onDisk)
                return $" across {Tally.Of(treesTotal, onDisk).Render("source tree")} on disk — the rest hold " +
                       $"no file matching --file-glob '{glob}', and 'sources list' says which have never been decompiled";
            return onDisk > 1 ? $" across {Tally.Complete(treesTotal).Render("source tree")}" : "";
        }

        // 数目用「N of M」而不是相减出来的差:磁盘上的目录数与「有候选文件的树」数并排一放
        // 就有人去减,而减出来的那个数谁都验证不了。各自标清自己数的是什么,不请人做减法。
        return $" in the '{sourceName}' tree alone" +
               (onDisk > 1
                   ? $" ({Tally.Of(1, onDisk).Render("source tree")} on disk); drop --source to search them all"
                   : "");
    }

    private static bool EmptyTree(string root, string sourceName)
    {
        var dir = Path.Combine(root, sourceName);
        try { return Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static IEnumerable<(string Tree, IEnumerable<string> Files)> EnumerateTrees(string root, string? sourceName)
    {
        if (sourceName is { Length: > 0 })
        {
            yield return (sourceName,
                Directory.EnumerateFiles(Path.Combine(root, sourceName), "*", SearchOption.AllDirectories));
            yield break;
        }

        var trees = SourcesShared.TreeNames(root)
                                 .OrderBy(n => n is "vanilla" or "Core" ? 0 : 1)
                                 .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
        foreach (var t in trees)
            yield return (t, Directory.EnumerateFiles(Path.Combine(root, t), "*", SearchOption.AllDirectories));

        // 根目录下直接摆着的文件(没有分树的部署形态)。
        yield return ("", Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly));
    }

    /// <summary>
    /// 「一个正数或 all」这条取值规则的唯一产地。all / none / 0 / -1 四种写法都是真实调用
    /// 形态,所以一并收下。
    /// </summary>
    private static int PositiveOrAll(CommandContext ctx, string name, int fallback)
    {
        var raw = ctx.Args.Value(name);
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (raw is "all" or "none" or "0" or "-1") return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException($"--{name} takes a positive number or 'all'; got '{raw}'.");
    }

    /// <summary>
    /// glob 转正则。<c>**</c> 跨目录、<c>*</c> 不跨、<c>?</c> 单字符。
    /// 不含斜杠的 glob(如 <c>*.cs</c>)按文件名匹配,含斜杠的按相对路径整体匹配。
    /// </summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        var body = sb.ToString();
        var anchored = glob.Contains('/') ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
        return new Regex(anchored, RegexOptions.IgnoreCase);
    }
}
