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
/// 符号级的一切归 <c>types</c> / <c>members</c> / <c>callers</c> / <c>il</c>;这里的独立价值是
/// **任意正则匹配方法体文本**,即元数据答不了的形状搜索。
///
/// **上限分两种,不许混。** <c>--limit</c> 与 <c>--max-per-file</c> 决定**印几行**,
/// 都不缩短扫描,所以命中总数仍是准数;<c>--max-files</c> 决定**读多少**,只有它咬下去
/// 总数才降级成下界。三刀分开声明,合并成一句话调用方就分不清该拧哪个旋钮。
///
/// 三把刀都没有默认值 —— 不给就是全读全印。<c>--max-files</c> 曾有个 50000,而语料
/// 只有两万五千个文件,那道闸够不着;它留下来的价值是**能造出部分答案**,不是拦住谁。
///
/// **第四把刀是横向的**,与上面三把不在一个维度上:<c>--max-line-chars</c> 决定一行
/// 印几个字符。反编译产物把整张调试表压成一个表达式(真语料里最长 6026 字符),而
/// 上面三把数的都是**行**,一刀都咬不到它 —— 一次 <c>--limit 40</c> 里五行巨行就能占掉
/// 约七成输出。命中行只印匹配处附近,上下文行没有落点可对中、从行首留一截,缺口都写成
/// <c>…</c>;两种取法在输出里分开说,合成一句就有一种会被讲成假的。
///
/// 它**只裁文本形态**:<c>--json</c> 那侧的 <c>text</c> 永远是整行。裁过的行粘不回源码,
/// 而结构化那一侧的消费方要的正是能对得上的原文。
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
            // 这句以前把整个符号层都推给 MCP,而本 CLI 自己就有 types / members / callers /
            // read —— 推错了地方,读者会为一个能在这里答的问去开另一个程序。
            "For anything symbol-level this reads the text where a command reads the metadata, and the metadata " +
            "answer is both faster and exact: 'types' for a type and what derives from it, 'members' for what is " +
            "in one, 'callers' for who calls a method, 'read --member' for one body.\n\n" +
            "It does not search Defs: the game's XML is not on disk in the form the game ended up with. " +
            "Data questions ('which defs use this class', 'what values does this field take') belong to " +
            "'where', 'values', and 'search', which answer them from the snapshot exactly.\n\n" +
            "Three switches cut down the list of lines, and they divide in two. --limit and --max-per-file decide " +
            "how many matching lines are printed; neither shortens the scan, so the match count stays exact " +
            "whichever of them bites. --max-files decides how much is read, so when that one bites the count " +
            "drops to a lower bound ('at least N') and the answer says which trees it never reached. None of " +
            "the three carries a default; each option below says what happens when it is left out.\n\n" +
            "A fourth shortens over-long lines instead of dropping them: --max-line-chars. Decompiled code " +
            "puts a whole table on one line, and such a line prints as the neighbourhood of its matches. " +
            "This one does carry a default, and it only shortens the text form — under --json every line " +
            "arrives whole.",
        // 叫 regex 不叫 pattern:这个参数的语言此前只写在下面那句描述里,而读者写命令时
        // 看的是用法行,于是 29 次 / 26 份会话伸手加一个 `--regex` 去断言默认值(实参多带
        // `|` 择一 —— 他们怕的是那个竖线被当字面)。名字里说出语言,断言就没有位置可站。
        //
        // 顺带把 --regex 接进「它是位置参数而不是选项」那一支:MatchPositional 认前缀,
        // regex 对 regex 直接命中,不需要给 PositionalSpec 加同义名表。代价是 --pattern
        // (2 次)反过来落回选项清单,29 换 2。
        //
        // 不做的那件事记在这儿,免得下轮重提:一份模式语言够用,**不必加 --literal**。
        // 实测照字面写、含正则元字符的模式占 1.9%(100 次 / 78 会话),取其中最常用的
        // 22 个在真源码树上原样跑 vs 把点转义,21 个逐条相同、剩下一个差 1 行 ——
        // 「静默匹配变宽」在真实模式上几乎不发生,那对选项收益近零。
        // 产地 tools/scan-codesearch-patterns.py(带 --verify 复跑)。
        Positionals = [new PositionalSpec
        {
            Name = "regex",
            Help = ".NET regular expression. Matching gives up on a file after " +
                   $"{Limits.CodeSearchRegexTimeoutMs} ms and the answer names the files it skipped; " +
                   "nested quantifiers are the usual cause.",
        }],
        Options =
        [
            new OptionSpec
            {
                Name = "file-glob",
                // 同一个文件过滤意图被真实调用方拼出 9 种键名。归一化只吃大小写与分隔符的
                // 差异,剩下的换词写法列在这里有意接受。
                // 主名由 R13 定:自由命名 12/12 落在 file-glob,识别复测 10/10。旧主名
                // files 产出式一票没拿到,降为别名;path-glob 是实测的第二名(6/12)。
                // file 是 2026-09-19 加的:被拒六周、占比 W35→W38 0.15%→3.8% 一路涨,取值全是
                // `Foo.cs` 这种裸文件名 —— 正是无 '/' 的 glob 的语义。此前它的提示是
                // --file-glob / --file-limit / --file-preview 三选一,后两个死别名已摘掉。
                Aliases = ["file", "path-glob", "files", "file-filter", "glob", "file-pattern", "file-extension", "file-type", "path-filter", "include"],
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
                // 2026-09-05 撤掉默认值 50000:反编译语料是 25065 个 .cs(任意后缀 25197),
                // 全量扫一遍 3.0 秒,那个数够不着;历史上 136 次咬下去全是手写的 N,没有一次
                // 是它。留着选项本身,因为只有它能把答案变成部分答案,而部分答案要能造得出来。
                Name = "max-files",
                Aliases = ["scan-limit", "max-scan"],
                Placeholder = "<n>",
                Help = "How many files the scan may read before it stops, counted after --file-glob has filtered. " +
                       "Left out, every file the glob selects is read. This is the only switch that can make " +
                       "the answer partial: pass it a number and the match count drops to a lower bound.",
                Default = "every file",
            },
            new OptionSpec
            {
                // 这道闸**只管印**:过上限的命中照样计数,于是总数保持准数,用不着降级成
                // 三态文法里的「at least」。2026-09-05 撤掉它的默认值 20:363 条真实
                // code-search 全量重放,上限解除后最大输出 13233 字符,不带管道的 60 条里
                // 最大值也是这一条 —— 它一次也没挡住过会撑爆的输出。
                Name = "max-per-file",
                Aliases = ["per-file", "matches-per-file", "max-matches-per-file"],
                Placeholder = "<n>",
                Help = "How many matching lines to print from any one file, at most. Left out, every one is " +
                       "printed — there is no cap to lift. Matches past it are still counted, so the total " +
                       "stays exact.",
                Default = "every one",
            },
            new OptionSpec
            {
                // 唯一**横向**的闸。它有默认值而上面三把没有,理由也相反:那三把撤掉默认值
                // 是因为语料够不着它们,而这一把在真语料上每次都够得着 —— 巨行是反编译的
                // 常态形状,默认不裁就等于把预算交给语料的排版。
                Name = "max-line-chars",
                // --max-line 不留:实测读者伸手写 `--max-line 40` 时想的是「最多 40 行」,
                // 而它是 40 个字符 —— 一个会静默给出另一种答案的写法,比没有这个别名更坏。
                Aliases = ["max-columns", "max-line-length", "max-chars-per-line"],
                Placeholder = "<n>",
                // 首句必须是**行为**。写成「一行最多印几个字符」时读者照 `cut -c` 理解,
                // 以为是从行首数 N 个字符,而真正的取法要读到第二句才出现。
                Help = "A line longer than this prints as the neighbourhood of its matches, with '…' for the " +
                       "characters left out. A context line has no match to centre on, so it shows the start " +
                       "of the line instead. Pass 0 to print every line whole. It does not touch --json: the " +
                       "text there is always the whole line.",
                Default = Limits.CodeSearchLineChars.ToString(),
            },
            new OptionSpec
            {
                Name = "source",
                // "scope" 不在这里 —— 见 CommandBase.Scope 的别名注释。mod / mods 在:见 CodeShared.Source。
                Aliases = ["root", "tree", "mod", "mods"],
                Placeholder = "<name>",
                Help = "Which decompiled source tree to search. Omit to search them all. This is the only " +
                       "switch that picks where the C# is read from: snapshots hold defs and translations, so " +
                       "--snapshot does not narrow a code search.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "context",
                Short = 'C',
                Aliases = ["context-lines", "around"],
                Placeholder = "<n|a-b|a+n>",
                Help = "Show lines around each match. A number N is N above and N below; '0-20' is 0 above " +
                       "and 20 below, '10+4' is 10 above and 4 below. Windows that overlap or touch are " +
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
            "rimsearcher code-search \"PostSpawnSetup\\(\" --context 0+20",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "matches",
                Rows = true,
                What = "one row per printed line — file, line, is_match, group, text. Context lines come " +
                       "through with is_match false, and 'group' is the merged window they belong to, so the " +
                       "text form's '--' separator needs no counterpart here. 'text' is the whole line even " +
                       "when the text form shortened it to the neighbourhood of its matches.",
            },
            new()
            {
                Key = "absent",
                Rows = true,
                What = "one row when the --source tree exists but holds no decompiled file — layer " +
                       "('source_tree:' + the tree), state empty, next (the sync command that fills it); " +
                       "empty otherwise. Nothing was read in that case, so 'matches' is empty too.",
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
                "What you gave contains HTML escapes (&lt; &gt; &amp;), which match those literal characters and " +
                $"therefore never match source code. Write it as: {pattern.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")}");

        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("search"));

        var sourceName = ctx.Args.Value("source");
        var glob = ctx.Args.Value("file-glob") ?? "*.cs";
        var contextSpec = ctx.Args.Value("context");
        var (before, after) = ParseContext(contextSpec, out var contextRewritten);
        var limit = ctx.Limit();
        var maxPerFile = PositiveOrEveryOne(ctx, "max-per-file");
        // 值照收照校验(写错了要报错,不因为换了个输出形态就静默),但 --json 那侧不裁,
        // 于是这一刀连同它的申报在那里一起不发生 —— 说破一件没发生的事已经够坏,而这一句
        // 挂在 truncation 上,机器侧会把它读成「结果不全」。
        var lineChars = LineCharCap(ctx);
        if (ctx.Json) lineChars = 0;
        var elision = new Elision();

        Regex regex;
        try
        {
            regex = new Regex(pattern,
                (ctx.Args.Flag("ignore-case") ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(Limits.CodeSearchRegexTimeoutMs));
        }
        catch (ArgumentException ex)
        {
            throw new CliUsageException($"That is not a valid regular expression: {ex.Message}");
        }

        if (sourceName is { Length: > 0 } && !Directory.Exists(Path.Combine(root, sourceName)))
            throw new CliUsageException(NoSuchTree(sourceName, SourcesShared.TreeNames(root)));

        var matcher = GlobToRegex(glob);
        var maxFiles = PositiveOrEveryOne(ctx, "max-files");

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
                if (toPrint.Count > 0)
                    Emit(lines, rows, rel, text, toPrint, before, after, hitsHere, regex, lineChars, elision);
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
                ctx.Report.Absent(DataLayers.EmptySourceTreeRow(sourceName));
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
                    // 模式那句排最前:它在,下一步就不是抬闸。没读完的那句照旧,读完也不会有。
                    (BreAlternation(ctx.Args.Positional(0)!) is { } bre ? bre + " " : "") +
                    // 「but the scan did not finish, so this is not evidence that nothing matches」
                    // 2026-09-18 删掉:下一句就是「The scan stopped after reading N files … Leave --max-files out」。
                    $"No line matched in the {Tally.Complete(filesRead).Render("file")} that were read.");
            else
                ctx.Report.Notice(NoticeKind.NextStep,
                    (BreAlternation(ctx.Args.Positional(0)!) is { } bre ? bre + " " : "") +
                    $"No line matched in {Tally.Complete(filesRead).Render("file")} under '{glob}'" +
                    Framing(root, sourceName, treesTotal, glob) + ". " +
                    // 「反编译时就抹掉了」排在 def 那句之前:它是唯一一种再怎么扫都不会有的成因。
                    (Erased(ctx.Args.Positional(0)!) is { } erased ? erased + " " : "") +
                    "The XML is not searched here: 'rimsearcher search' and 'rimsearcher where' answer from the snapshot.");
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
                ContextEcho(contextSpec, before, after, contextRewritten) +
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
                $"--snapshot {ctx.Args.Value("snapshot")} did not narrow this search; --source is what picks " +
                "among the decompiled trees, and 'rimsearcher sources list' names them.");

        // 不带 '/' 也不带 '.' 的 glob 是**按命名空间取景**的写法落到了文件名上。
        // 盲测里六份里六份把「只搜 Verse 命名空间」写成 --file-glob '*Verse*',而它挑出的
        // 45 个文件没有一个在 Verse 下 —— Overseer、HediffGiverSet 这些名字里恰好含 verse。
        // 印出来是一份计数完整、语气笃定的正常答案,与「Verse 下就这么多」逐字同形。
        // 带扩展名的写法(*.cs / *Comp*.cs)不报:那种写法本身就在说文件名,没有这层歧义。
        if (filesCandidate > 0 && !glob.Contains('/') && !glob.Contains('.'))
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{glob}' has no '/', so it selected by file name alone, ignoring case. " +
                // 建议必须给 '**':'*' 不跨 '/',而路径是 <tree>/<assembly>/<namespace>/<file>.cs
                // 四段起,'*/Verse/*' 一个文件都挑不出来。给一条敲了没用的命令比不给更坏。
                $"For the directory '{glob.Trim('*')}' write '**/{glob.Trim('*')}/**'.");

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
        // 横向那一刀自己申报。名词是 printed line 而不是 line:「又少了 N 行」那个读法
        // 站不住。哪一截(命中行对着匹配、上下文行从行首)是取法,住 --max-line-chars 的
        // help;这里只留数与出路(2026-09-18,Docs/25 §20)。
        if (elision.Lines > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"{Tally.Complete(elision.Lines).Render("printed line")} ran past --max-line-chars " +
                $"{lineChars}, with '{Gap}' for what was left out; the longest is {elision.Longest} " +
                "characters. '--max-line-chars 0' prints them whole; --json carries the whole line.");
        if (filesCapped) SayFilesCapped(ctx, sourceName, glob, filesRead,
                                        partialTree, partialRead, partialTotal, unreached);
        // 点名被跳过的文件;成因(嵌套量词)是机制,住 regex 那个位置参数的 help。
        if (timedOut.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Matching took longer than {Limits.CodeSearchRegexTimeoutMs} ms on " +
                $"{Tally.Complete(timedOut.Count).Render("file")}, which were skipped part-way: " +
                $"{NameList.Render(timedOut, 6)}.");

        if (lines.Count == 0) return 1;

        ctx.Report.Text("matches", lines, rows);
        if (!ctx.Args.Flag("no-resolve-keys")) ResolveUiText(ctx, rows, elision);
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
    private static void ResolveUiText(CommandContext ctx, List<IReadOnlyDictionary<string, object?>> rows,
                                      Elision elision)
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
                "appear in the printed lines, but no snapshot could be opened to say what they display. " +
                "'rimsearcher snapshot list' shows what is " +
                "registered; pass --no-resolve-keys to stop asking.");
            return;
        }

        var distinct = literal.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var resolved = distinct.Where(found.ContainsKey).ToList();
        // 横向裁剪把一处 .Translate() 收进了省略号:表里于是有一个在它上面那行可见文本里
        // 找不到出处的 key,而「表里多了一行」与「表算错了」在读者那儿同形。
        //
        // 条件钉在**表真的印出来**这一支上:这句唯一改变下一步的信息就是「表在下面」,
        // 而键解析不到时表根本不在,这时说它就是把人领去对一张不存在的表。
        if (elision.HidTranslate && resolved.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "A shortened line took a .Translate() call into the '…'; keys below are read from the whole " +
                "line, so one can be missing from the line printed above it.");
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
        var missing = distinct.Where(k => !found.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 末句曾给这个零补一个死因(「没人声明的 key 就是代码够不着的 key」)——
                // 站不住:查的是 TranslationOrigin.Runtime,即**导出时游戏实际加载了的**那层,
                // 而快照的语言、当时启用的 mod、语言文件有没有跟上新 key,每一条都能造出同一个
                // 零。归因留给 keyed / snapshot status,那边按快照量到哪一步说话。
                // 点名:只给个数时读者对不出表里少的是哪几个。「def 的 label 走 DefInjected」是
                // keyed 的 Remarks 里的机制,不在这里说。
                $"This snapshot has no keyed translation for " +
                $"{Tally.Complete(missing.Count).Render("translation key")} in these lines: " +
                $"{NameList.Render(missing, Limits.MaxSuggestions)}.");

        if (assembled > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(assembled).Render("line")} here {(assembled == 1 ? "calls" : "call")} " +
                ".Translate() on something other than a string literal, so that key is not resolved; " +
                "'rimsearcher keyed <key>' shows it once you know the name.");
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
            parts.Add($"'{partialTree}' was read only in part: " +
                      $"{Tally.Of(partialRead, partialTotal).Render("file")}.");

        if (unreached.Count > 0)
        {
            // 名单要有上限:树可以有几十棵,全点名会占满一屏。
            var names = unreached.Take(NamedTrees).Select(u => u.Tree).ToList();
            var more = unreached.Count - names.Count;
            parts.Add("Never read at all: " + string.Join(", ", names) +
                      (more > 0 ? $" and {more} more" : "") +
                      $" — {Tally.Complete(unreached.Count).Render("source tree")}, " +
                      $"{Tally.Complete(unreached.Sum(u => u.Files)).Render("file")}.");
        }

        var narrow = new List<string>();
        if (string.IsNullOrEmpty(sourceName)) narrow.Add("--source <tree>");
        if (!ctx.Args.Has("file-glob")) narrow.Add("--file-glob <glob>");
        parts.Add("Leave --max-files out to read every file" +
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
    /// <summary>
    /// grep 的 BRE 用 <c>\|</c> 表「或」,.NET 正则里它是字面竖线,而 C# 源码里没有
    /// <c>word|word</c> 这样的文本 —— 这种模式落空与「真没有」同形,却是再扫也不会有的那种。
    /// 前面再有一个反斜杠的 <c>\\|</c> 是字面反斜杠接正当的「或」,不算。
    /// </summary>
    private static string? BreAlternation(string pattern) =>
        BrePipe.IsMatch(pattern)
            ? @"In a .NET regular expression '\|' is a literal '|', not 'or'."
            : null;

    private static readonly Regex BrePipe = new(@"(?<!\\)\\\|", RegexOptions.Compiled);

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
                             string rel, string[] text, List<int> hits, int before, int after, int hitsHere,
                             Regex regex, int lineChars, Elision elision)
    {
        var isHit = hits.ToHashSet();
        var group = rows.Count == 0 ? 0 : (int)rows[^1]["group"]! + 1;

        // 文件之间空一行,标题才不会粘在上一个文件的最后一行代码上。
        if (lines.Count > 0) lines.Add("");
        lines.Add($"{rel}  {Tally.Complete(hitsHere).Render("match")}" +
                  (hits.Count < hitsHere ? $", {hits.Count} printed" : ""));

        // 行号右对齐。宽度按**这次真印出来的**最大行号算,不按文件总行数:后者会让只印了
        // 第 3 行的千行文件补出一串前导空格,与旁边只印了第 3 行的短文件参差着排。
        // hits 升序,末项加窗口下沿就是最大的那个。
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

            // 分隔符只在**同一文件内**的两组之间:换文件由标题自己隔开,那里再插一条 "--"
            // 会读成标题下面还漏印了什么。
            if (hasWindow && !firstGroup) lines.Add("--");
            firstGroup = false;
            for (var c = start; c <= end; c++)
            {
                // 文本形态裁,结构化形态不裁 —— 同一份行,两种读者。
                var whole = text[c].TrimEnd();
                var shown = Elide(whole, regex, isHit.Contains(c), lineChars, elision);
                lines.Add($"{(c + 1).ToString().PadLeft(pad)}{(isHit.Contains(c) ? ":" : "-")} {shown}");
                // 文本侧那条 "--" 分隔符在结构化侧变成 group 序号:JSON 里不插假行表示断开。
                rows.Add(new Dictionary<string, object?>
                {
                    ["file"] = rel,
                    ["line"] = c + 1,
                    ["is_match"] = isHit.Contains(c),
                    ["group"] = group,
                    ["text"] = whole,
                });
            }
            group++;
            i++;
        }
    }

    /// <summary>
    /// 横向裁剪的现场统计:裁了几行、最长那行原本多少字符、有没有把一处
    /// <c>.Translate()</c> 藏进省略号里。
    /// </summary>
    private sealed class Elision
    {
        /// <summary>命中行与上下文行**分开记**:两种行印出来的那一截取法不同,合并成一个数
        /// 就只能用一个取法去讲两种行,而其中一种会被讲成假的。</summary>
        public int MatchLines;

        public int ContextLines;
        public int Lines => MatchLines + ContextLines;
        public int Longest;
        public bool HidTranslate;
    }

    /// <summary>缺口记号。单字符,且不是 C# 里写得出的东西 —— 省略号不会与源码本身同形。</summary>
    private const string Gap = "…";

    /// <summary>匹配两侧至少留几个字符,免得预算很小时只剩匹配本身、看不出它在什么中间。</summary>
    private const int MinElideMargin = 8;

    /// <summary>
    /// 超长行只印匹配处附近。**只作用于文本形态** —— 结构化那一侧的 <c>text</c> 是整行,
    /// 因为裁过的行粘不回源码,而机器侧的消费方要的正是能对得上的原文。
    ///
    /// 一行里多处命中时,各自的窗口重叠就合并,于是缺口只出现在真的跳过了字符的地方。
    /// 上下文行(<c>-C</c> 窗口里没命中的那些)没有落点可对中,退回行首:那里有缩进与
    /// 语句开头,是这种行上唯一能自证身份的一截。
    /// </summary>
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
            // 逐行扫描那侧已经用同一个超时判过这一行,这里是第二次跑同一个正则(要的是位置
            // 不是有没有)。真在这儿超时就当没有落点,不许把一行印成半个异常。
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
        tally.Longest = Math.Max(tally.Longest, line.Length);
        // 整行里有 .Translate() 而印出来的那截没有:ui_text 表会多出一个在可见文本里找不到
        // 出处的 key,而「表里多了一行」与「表算错了」在读者那儿同形。
        //
        // **只看命中行**:解释那一层本来就只读命中行,上下文行里的 .Translate() 一次都没有
        // 进过表 —— 拿它触发这句话,就是把读者领去对一张不存在的表。
        if (isHit
            && line.Contains(".Translate", StringComparison.Ordinal)
            && !shown.Contains(".Translate", StringComparison.Ordinal))
            tally.HidTranslate = true;
        return shown;
    }

    /// <summary>
    /// <c>--max-line-chars</c> 收 0,而印几行的那三把刀不收 —— 分开写就是为了这个差别:
    /// 印零行、读零个文件没有意义,「一个字符都不裁」有。
    /// </summary>
    private static int LineCharCap(CommandContext ctx)
    {
        var raw = ctx.Args.Value("max-line-chars");
        if (string.IsNullOrEmpty(raw)) return Limits.CodeSearchLineChars;
        if (int.TryParse(raw, out var n) && n >= 0) return n;
        throw new CliUsageException(
            $"--max-line-chars expects a whole number of characters, or 0 to print every line whole " +
            $"(got '{raw}').");
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

        // 根目录顶层的文件**不算一棵树**。这里曾多走一趟顶层,为的是「源码直接铺在根下、
        // 不分子目录」那种摆法 —— 而这棵树是 sources sync 自己写的,SourcePlanner 给每个
        // 可跟随源都开一个以 packageId 命名的子目录,那种摆法产生不出来。它实际收进来的
        // 是仓库家什(本机是 README.md 与 .gitattributes),而它们让计数句自相矛盾:
        // --file-glob '*' 报 36 棵而 sources list 报 35,--file-glob 'README.md' 报
        // 「1 of 35 source trees on disk」而命中的那一个不在那 35 里。
    }

    /// <summary>
    /// <c>N</c> / <c>B-A</c> / <c>B+A</c>。单数是前后各 N 行(旧含义,逐字节不变);
    /// 两数是上面 B 行、下面 A 行。两侧是独立计数,所以允许 B &gt; A —— 这与
    /// <c>--lines</c> 的 <c>a-b</c> 不同,<c>--lines</c> 的两端是行号,这里是两个半径。
    /// </summary>
    internal static (int Before, int After) ParseContext(string? spec)
        => ParseContext(spec, out _);

    /// <summary>
    /// <paramref name="rewritten"/>:归一化真的改动了写法时给出改动后的样子,否则 null。
    /// 空格不算改动,与 <see cref="ReadCommand.ParseRange"/> 同一条规则。
    /// </summary>
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
                    $"--context wants a whole number of lines, zero or more; '{s.Trim()}' is not one ({what}). " +
                    "Write it as '8', '0-20', or '10+4'.");

        var dash = spec.IndexOf('-');
        if (dash > 0)
            return (At(spec[..dash], "above"), At(spec[(dash + 1)..], "below"));

        var plus = spec.IndexOf('+');
        if (plus > 0)
            return (At(spec[..plus], "above"), At(spec[(plus + 1)..], "below"));

        var n = At(spec, "the window");
        return (n, n);
    }

    /// <summary>
    /// 与 <c>--lines</c> 同一套分隔符:<c>–</c> <c>—</c> <c>−</c> <c>..</c> <c>:</c> <c>,</c>
    /// 都当 <c>-</c>,数字前的 <c>L</c> 丢掉。改写过就得回声,因为 <c>1,20</c> 读成 1-20
    /// 之后,输出逐字看起来完全正常。
    /// </summary>
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

    /// <summary>
    /// 两数形式才自报窗口:纯 <c>N</c> 必须与现在逐字节相同,多一句就会把回归闸弄红。
    /// </summary>
    private static string ContextEcho(string? spec, int before, int after, string? rewritten)
    {
        if (string.IsNullOrEmpty(spec)) return "";
        var given = spec.Trim();
        var form = rewritten ?? NormalizeWindow(given);
        if (!form.Contains('-', StringComparison.Ordinal) && !form.Contains('+', StringComparison.Ordinal))
            return "";

        var window = $"{Tally.Complete(before).Render("line")} above and {Tally.Complete(after).Render("line")} below";
        return rewritten is not null
            ? $"--context {given}{ReadCommand.ReadAsPhrase}{rewritten}: {window}. "
            : $"--context {given} is {window}. ";
    }

    /// <summary>
    /// 不给就是全部,给了只收正整数 —— 与 <c>--limit</c> 同一条规则,报错也是同一句。
    /// </summary>
    private static int PositiveOrEveryOne(CommandContext ctx, string name)
    {
        var raw = ctx.Args.Value(name);
        if (string.IsNullOrEmpty(raw)) return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException(
            $"--{name} expects a positive whole number (got '{raw}').");
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
                // `**/` 是零段或多段目录:`**/RimWorld/Ability.cs` 也得认根下就是 RimWorld 的路径
                // (read 的被拒句把读者写的尾路径填成这个形状,他写的可能已经是从树名起的整条)。
                if (i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
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
