using System.Text.RegularExpressions;
using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;

namespace RimSearcher.Commands;

/// <summary>
/// 跨文件正则 —— 从 master 的 SearchRegexAsync 带走扫描段(01 对 SourceIndexer「整个扔掉」
/// 的口径在此修正:正则扫描那一段带走)。对象是反编译落盘目录。
///
/// **三刀自证契约整体带走**:每文件预览上限 / 文件数上限 / 未扫全 → at least N。三刀分开
/// 声明,因为它们被截的原因不同,合并成一句话调用方就分不清该调哪个旋钮。
///
/// 与 DecompilerServer 的分工(05-9 实测后收窄):符号级的一切走 MCP;这里保留的独立价值是
/// **任意正则匹配方法体文本**,即 search_string_literals 覆盖不到的形状搜索。
///
/// 三轮盲测这一条命令独占六个场景(R3 fatal / R4 / R13 / R15,加一条十份轨迹零记录的
/// 沉默缺陷),而它此前一条输出基线都没有。上面那句「三刀分开声明」写在注释里是对的,
/// 落到实现里却各自走了形,所以这里把口径重述一遍并钉住:
///
///   **上限分两种,不许混。** <c>--limit</c> 与 <c>--max-per-file</c> 决定**印几行**,
///   都不缩短扫描,所以命中总数仍是准数;<c>--max-files</c> 决定**读多少**,只有它咬下去
///   总数才降级成下界。原先 <c>--limit</c> 一到就 break 掉整个扫描,于是
///   「25 条」既是印出来的行数、又是扫描停下的位置,而 SKILL 明写着它只管行 ——
///   文档承诺的语义在实现里不存在,和 R6 同形。
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
            "'find', 'values', and 'search', which answer them from the snapshot exactly.\n\n" +
            "Three caps apply, and they divide in two. --limit and --max-per-file decide how many matching " +
            "lines are printed; neither shortens the scan, so the match count stays exact whichever of them " +
            "bites. --max-files decides how much is read, so when that one bites the count drops to a lower " +
            "bound ('at least N') and the answer says which trees it never reached.",
        Positionals = [new PositionalSpec { Name = "pattern", Help = ".NET regular expression." }],
        Options =
        [
            new OptionSpec
            {
                Name = "files",
                // 07-② 实证:同一个文件过滤意图被真实调用方拼出 9 种键名。归一化吃掉大小写与
                // 分隔符的差异,剩下的换词写法列在这里有意接受。
                Aliases = ["file-filter", "file-glob", "glob", "file-pattern", "file-extension", "file-type", "path-filter", "include"],
                Placeholder = "<glob>",
                // 实测:help 只给了 `*.cs` 和 `*/Verse/*` 两个例子,而这两个恰好是**不能**区分
                // 语义的一对 —— 于是有人以为 `*` 跨目录,拿 `*/A*.cs` 扫了 26 轮几乎全空。
                // 规则本身写清,比再补一个例子管用。
                Help = "Only search files whose path matches this glob. A glob with no '/' matches the file name " +
                       "alone (*.cs is every .cs file at any depth); with a '/' it matches the path relative to " +
                       "the decompiled root, which begins with the source tree's name even under --source, and " +
                       "there '*' stops at a '/' while '**' crosses it. So */Verse/* is one level down, " +
                       "**/Verse/** is any.",
                Default = "*.cs",
            },
            new OptionSpec
            {
                // 上限本身是对的(防止一条正则扫穿整棵树),但不可调就等于「建议你换个更小的树」,
                // 而当那棵树本身就超过上限时,这个建议是空的 —— 实测里 --source vanilla 换来
                // 一模一样的警告。旋钮必须存在,声明才有落点。
                Name = "max-files",
                Aliases = ["file-limit", "scan-limit", "max-scan"],
                Placeholder = "<n|all>",
                Help = "How many files the scan may read before it stops, counted after --files has filtered. " +
                       "Pass 'all' to lift the cap. This is the only cap that can make the answer partial.",
                Default = Limits.CodeSearchMaxFiles.ToString(),
            },
            new OptionSpec
            {
                // R4:第三道闸原先没有开关,两份文档都没写它,而计数行读起来像完整的 ——
                // 「有多少个这种形状的方法」问到一个偷偷少了的数,是这条命令最贵的错法。
                // 给开关的同时把它降级成**只管印**:过上限的命中照样计数,于是总数保持准确,
                // 三态文法里那个「at least」也就用不着了 —— 比盲测要求的更强一档。
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
            // 「matches」在这里是错的词:--limit 管的是印出来的**行**,而命中数照样数全。
            // 一个字的差别,恰恰是这条命令被读错的那一处。
            CommonOptions.Limit("matching lines"),
            new OptionSpec
            {
                Name = "ignore-case",
                Short = 'i',
                Arity = Arity.Flag,
                Aliases = ["case-insensitive"],
                Help = "Match without regard to letter case.",
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
                What = "one row per printed line — file, line, is_match, group, text. Context lines come " +
                       "through with is_match false, and 'group' is the merged window they belong to, so the " +
                       "text form's '--' separator needs no counterpart here.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var pattern = ctx.Args.Positional(0)!;

        // 07-⑥ 实证:真实调用方发过 HTML 转义形态的 pattern(&lt;defName&gt;),必然零命中。
        // 错误消息是一等公民 —— 与其返回 0 条让调用方猜,不如直接说破。
        if (pattern.Contains("&lt;") || pattern.Contains("&gt;") || pattern.Contains("&amp;"))
            throw new CliUsageException(
                "The pattern contains HTML escapes (&lt; &gt; &amp;), which match those literal characters and " +
                $"therefore never match source code. Write it as: {pattern.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")}");

        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("search"));

        var sourceName = ctx.Args.Value("source");
        var glob = ctx.Args.Value("files") ?? "*.cs";
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
        var filesRead = 0;          // 真读进来过的文件
        var filesCandidate = 0;     // 过了 --files 的文件,不管读没读 —— 「N of M」的那个 M
        var filesWithMatches = 0;
        var totalMatches = 0;       // 找到多少
        var printed = 0;            // 印出来多少。两者不是一件事,所以是两个数
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
            // 过滤与排序都在这里定死。原先交给文件系统的枚举顺序 —— 而文件数上限是先到先得的,
            // 于是「哪个文件被看见」取决于目录项在磁盘上的排布,同一条命令在两台机器上可以
            // 给出不同答案。树间顺序早就是显式的(vanilla 优先),树内也必须是。
            var treeFiles = files.Select(f => (Abs: f, Rel: Rel(root, f)))
                                 .Where(f => matcher.IsMatch(f.Rel))
                                 .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            // 一个匹配文件都没有的树既不算读过、也不算「没读到」。R15:原先它会被点名进
            // 「没读到的树」名单 —— 实测那份名单里十棵是空目录(旧别名残留),读的人以为
            // 还有一大片代码没看,而那里一行都没有。
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
                    // 两道印刷闸,都只跳过**印**这一步,continue 而不是 break ——
                    // 数还得继续数下去,否则总数就成了「印满为止」的那个数。
                    if (toPrint.Count >= maxPerFile) continue;
                    if (printed >= limit.Effective) continue;
                    toPrint.Add(i);
                    printed++;
                }

                if (hitsHere > 0) filesWithMatches++;
                if (hitsHere > toPrint.Count && toPrint.Count >= maxPerFile) perFileCapped++;
                if (toPrint.Count > 0) Emit(lines, rows, rel, text, toPrint, contextLines);
            }

            if (readHere == 0) unreached.Add((tree, treeFiles.Count));
            else
            {
                treesRead++;
                if (readHere < treeFiles.Count)
                    { partialTree = tree; partialRead = readHere; partialTotal = treeFiles.Count; }
            }
        }

        // 扫描完不完整,只由「读没读全」决定 —— 印几行不影响它。这是本命令整条契约的枢轴:
        // 不完整时命中数降级成下界,完整时它是准数,哪怕只印出来其中几行。
        var incomplete = filesCapped || timedOut.Count > 0;
        var found = incomplete ? Tally.AtLeast(totalMatches) : Tally.Complete(totalMatches);

        if (lines.Count == 0)
        {
            // R3 fatal(六个场景):零命中与没读完是两件事,而原先它们说同一句话
            // 「No line matched in N files」,后面还紧跟一句「要找 def 的话去 search/find」——
            // 于是「这道闸把 80% 的代码挡在外面」被读成「代码里没有这东西,换个数据源吧」。
            // 六个 agent 里有人据此把 mod 里的类当成 vanilla 的答案交了出去。
            // 没读完时:说清结论无效,并且**不指路去别的数据源** —— 该做的是把闸抬开。
            // 第三种成因,写这条修复时自己一头撞上去的:glob 一个文件都没打中。
            // 输出是「No line matched in 0 files under '…'」—— 与「读了但没匹配」
            // 一字之差,而下一步完全不同(该改 glob,不是该换数据源)。R3 说的
            // 「零结果一律报最强的那种」在同一条命令里还有第三份。
            // 第四种成因,第四轮回归实测撞到的:`--source Milira` —— 那棵树在
            // `sources list` 里明明列着,目录也在磁盘上,里面却一个文件都没有(程序集从没
            // 反编译过)。原先它落进上面那条 glob 分支,答案变成「--files '*.cs' 没打中」,
            // 于是读的人去改 glob,而真因是这棵树该 sync 一遍。B11 的题面点名要求分清
            // 「mod 没装 / 快照没覆盖 / 源码树没同步」三种,这是第三种。
            if (filesCandidate == 0 && sourceName is { Length: > 0 } && EmptyTree(root, sourceName))
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"The source tree '{sourceName}' exists but holds no decompiled files at all, so the glob " +
                    "never came into it. Its assemblies have not been decompiled (or the tree was emptied): " +
                    "'rimsearcher sources sync' rebuilds it from what the snapshot's mods load, and " +
                    "'rimsearcher sources list' shows which trees are in that state.");
            else if (filesCandidate == 0)
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No file matched --files '{glob}', so nothing was read at all." +
                    (glob.Contains('/')
                        ? " A glob containing '/' is matched against the whole path relative to the decompiled " +
                          "root, which begins with the source tree's name: 'vanilla/**/Widgets.cs', not " +
                          "'Verse/Widgets.cs'. Without a '/' it matches the file name alone at any depth."
                        // 第四轮回归实测:`--file-extension cs` —— 别名收下了,值却按 glob 解,
                        // 于是 'cs' 要求整个文件名就叫 cs。别名叫 extension 而值的文法是 glob,
                        // 两种文法的零结果逐字同形,读的人会读成「这里没有 .cs 文件」。
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
                    // 第五种成因,而且是唯一一种「再怎么扫都不会有」的:模式指的东西在反编译时
                    // 就没了。它排在 def 那句之前 —— 原先这一支只提供「你要找的其实是 def 吧」
                    // 一种解释,于是读的人被推去换数据源,而真相是这棵树里本来就查不到这种东西。
                    (Erased(ctx.Args.Positional(0)!) is { } erased ? erased + " " : "") +
                    "If you were looking for a def rather than code, 'rimsearcher search' and 'rimsearcher find' " +
                    "answer that from the snapshot — the XML is not searched here.");
        }
        else
        {
            // 命中数放最前。问的是「有多少个这种形状的方法」,答的却是文件数 ——
            // 实测里有人差点把 140(文件)当成方法数报出去,还得自己 wc 一遍输出。
            // 「找到多少」与「印了多少」用括号并列,不用动词:NounRegistry 管名词不管主谓,
            // 「1 are printed」这种不一致靠加登记项修不掉(R6 同一课)。
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
                    : Framing(root, sourceName, treesTotal, glob)) + ".");
        }

        // 三个旋钮各自申报,因为被截的原因不同,该拧的也不同。
        // 两句都以旋钮自己作主语:计数放进从句,动词就不必跟着单复数变 —— 名词有登记处,
        // 动词没有,「the first 1 lines are printed」这种不一致靠加登记项修不掉(R6 同一课)。
        if (printed < totalMatches && printed >= limit.Effective)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"--limit stopped the printing at {Tally.Complete(limit.Effective).Render("match")}; " +
                "raise it to see more." +
                // 「扫描照样跑到底」是本条修复的卖点,但它只在**真的**跑到底时才成立。
                // --max-files 也咬下去时说这句话,就是拿一条修复去掩盖另一道闸 ——
                // 而那正是 R3 的形状(一句正确的话贴在错误的语境上)。
                (incomplete ? "" : " The scan itself ran to the end either way."));
        if (perFileCapped > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"--max-per-file allows {Tally.Complete(maxPerFile).Render("match")} from any one file, and " +
                $"{Tally.Complete(perFileCapped).Render("file")} had more than that; the count above still " +
                "includes every one of them. Raise it to see the rest.");
        if (filesCapped) SayFilesCapped(ctx, sourceName, glob, filesRead,
                                        partialTree, partialRead, partialTotal, unreached);
        if (timedOut.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The pattern took longer than {Limits.CodeSearchRegexTimeoutMs} ms on " +
                $"{Tally.Complete(timedOut.Count).Render("file")}, which were skipped part-way. " +
                "A pattern with nested quantifiers is the usual cause.");

        if (lines.Count == 0) return 1;

        ctx.Report.Text("matches", lines, rows);
        return 0;
    }

    /// <summary>
    /// 文件数上限咬下去时说什么。三轮 R3 要求的四件事都在这里:
    /// 说破「某棵树只读了一部分」(单树与多树同一句话,原先单树只有一句
    /// 「Files later in the tree were never read」,连读了几分之几都不说);
    /// 点名没读到的树并给出还剩多少文件;不点名空树;
    /// <c>--source</c> 已经给出时不再把它列成补救 —— 实测里
    /// <c>--source vanilla</c> 换来的是一模一样的警告,而那句话仍在建议加 <c>--source</c>。
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
            // 名单要有上限:实测那句话点了 33 个名字,占满一屏,而其中十个是空目录。
            var names = unreached.Take(NamedTrees).Select(u => Label(u.Tree)).ToList();
            var more = unreached.Count - names.Count;
            parts.Add("Never read at all: " + string.Join(", ", names) +
                      (more > 0 ? $" and {more} more" : "") +
                      $" — {Tally.Complete(unreached.Count).Render("source tree")}, " +
                      $"{Tally.Complete(unreached.Sum(u => u.Files)).Render("file")}.");
        }

        var narrow = new List<string>();
        if (string.IsNullOrEmpty(sourceName)) narrow.Add("--source <tree>");
        if (!ctx.Args.Has("files")) narrow.Add("--files <glob>");
        parts.Add("Raise the cap with --max-files all" +
                  (narrow.Count > 0 ? $", or narrow with {string.Join(" or ", narrow)}." : "."));

        ctx.Report.Notice(NoticeKind.Truncation, string.Join(" ", parts));
    }

    /// <summary>
    /// 这个模式指的东西是不是**反编译时就被抹掉**的那一类 —— 是的话再怎么扫都不会有命中,
    /// 而「零命中」与「代码里没这回事」逐字同形。不是就回 null,一个字不说。
    ///
    /// 两条判据都量过(本机 23 棵树、19467 个文件):
    ///   注释 —— `^\s*///` 零条;`^\s*//` 一共 1369 条,其中 1334 条是 ILSpy 自己的备注
    ///           (「ILSpy generated this…」「try-fault」「yield-return decompiler failed」)。
    ///           作者写的注释一条都没留下。
    ///   局部变量 —— `numN = ` 有 17212 条。**参数名留着**(它在元数据里,`Pawn pawn` 照旧),
    ///           留不住的是方法体内的局部;ILSpy 按初始化表达式现编一个,于是同一个变量
    ///           可能叫 num、list、flag,也可能叫 bossgroupCaller。
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
            // 举的这几个名字不是被截断的名单,是**生成规则**的例子(有多少个 num 取决于
            // 方法里有几个 int),所以不走 NameList,也不能写省略号 —— 那会读成「还有几条没列」。
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
    /// 上下文窗口。**重叠或相邻的窗口合并**(R13):原先每条命中各印一窗,于是
    /// <c>-C 2</c> 打在连着的命中上会把同一行印三遍 —— 实测 5 条命中印出 15 行,
    /// 其中 10 行是重复的。对读 stdout 的 LLM 来说这是直接的上下文预算浪费,
    /// 而且重复行让人以为那里真有好几处命中。
    /// 分隔符只在两组之间出现,不留尾巴 —— 尾部空隔符会被读成「后面还有,被截了」。
    /// </summary>
    private static void Emit(List<string> lines, List<IReadOnlyDictionary<string, object?>> rows,
                             string rel, string[] text, List<int> hits, int context)
    {
        var isHit = hits.ToHashSet();
        var group = rows.Count == 0 ? 0 : (int)rows[^1]["group"]! + 1;
        var i = 0;
        while (i < hits.Count)
        {
            var start = Math.Max(0, hits[i] - context);
            var end = Math.Min(text.Length - 1, hits[i] + context);
            while (i + 1 < hits.Count && hits[i + 1] - context <= end + 1)
            {
                i++;
                end = Math.Max(end, Math.Min(text.Length - 1, hits[i] + context));
            }

            if (context > 0 && lines.Count > 0) lines.Add("--");
            for (var c = start; c <= end; c++)
            {
                lines.Add($"{rel}:{c + 1}{(isHit.Contains(c) ? ":" : "-")}{text[c].TrimEnd()}");
                // 文本侧那条 "--" 分隔符在结构化侧变成 group 序号:JSON 里插一行假数据
                // 表示「这里断开了」,消费方要么被它绊倒,要么得知道该忽略它。
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
    /// 树名是 packageId,而人记得的往往是个外号(HAR、miho)—— 外号不在任何数据里,所以打分器
    /// 对它只能给出**看起来像但是错的**那一个(实测:<c>HAR</c> → <c>brrainz.harmony</c>)。
    /// 一个错的独家建议比没有建议更坏:它看着像答案,于是没人再去看名单。故永远同时指向名单,
    /// 并且说破「外号匹配不上任何东西」这条成因。
    /// </summary>
    public static string NoSuchTree(string? typed, IEnumerable<string> available)
    {
        var all = available.ToList();
        // 候选走 Suggestion,句子不走 —— 它在名单后面还要加一句「树名是 packageId,
        // 外号匹配不上任何东西」,那是这条命令独有的成因,不该挤进公共措辞里。
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
    /// 按**确定顺序**逐棵源码树走,而不是把整个根目录一把 EnumerateFiles。
    /// 文件数上限是先到先得的,所以枚举顺序决定了谁被截掉 —— 交给文件系统顺序,
    /// 就等于让「哪棵树被看见」取决于目录名的字母序。vanilla 排前面是有意的:
    /// 它是问题的默认语境,被截掉的代价最大。
    ///
    /// 什么算一棵树问 <see cref="SourcesShared.TreeNames"/>,这里不自己判(R15)。
    /// 路径一律相对根目录,<c>--source</c> 只是少走几棵树,不改变文件的名字。
    /// </summary>
    /// <summary>目录在,里面一个文件都没有 —— 与「目录不在」和「glob 没打中」是三件事。</summary>
    /// <summary>
    /// 取景 —— 这一次扫的是哪一片。
    ///
    /// 原先只在 <c>treesTotal > 1</c> 时才说「across N source trees」,于是
    /// <c>--source vanilla</c> 一加,这半句整个消失:输出变成「1 match in 1 file;
    /// 10222 files read.」,而它与「全库扫完只有这一条」逐字同形。四份实测把穷举论证
    /// 建在这句话上,而窄化把论证的射程悄悄改了。
    ///
    /// 所以窄化时**说得更多**而不是更少:点名扫了哪一棵,以及有几棵没扫、怎么把它们扫上。
    /// </summary>
    private static string Framing(string root, string? sourceName, int treesTotal, string glob)
    {
        var onDisk = SourcesShared.TreeNames(root).Count();

        if (string.IsNullOrEmpty(sourceName))
        {
            // 第六轮实测:不窄化那次报「across 23 source trees」,而 `sources list` 当场列 33 棵。
            // 两个数谁也不解释谁,于是「23 棵里一次都没出现」被当成「全库唯一」用掉了四次 ——
            // 差额到底是「十棵没扫的代码」还是「十棵空目录」,输出里一个字都没有。
            // 说清 23 数的是什么(按这个 --files 挑得出文件的树),差额就不再是未知量;
            // 「the rest」不带数,避免主谓跟着变数走(NounRegistry 只管名词)。
            //
            // 差额在就一定说,哪怕只剩一棵树被扫到 —— 原先的门槛是 treesTotal > 1,于是
            // `--files '*Comp*.cs'` 这种一挑只剩一棵的问法整句消失,输出「2 files read.」
            // 与「全库就这么多」逐字同形,正是这条修复要拆掉的那种同形。
            if (treesTotal < onDisk)
                return $" across {Tally.Of(treesTotal, onDisk).Render("source tree")} on disk — the rest hold " +
                       $"no file matching --files '{glob}', and 'rimsearcher sources list' says which of those " +
                       "have never been decompiled";
            return onDisk > 1 ? $" across {Tally.Complete(treesTotal).Render("source tree")}" : "";
        }

        // 数目用「N of M」而不是相减出来的差:磁盘上有 33 个目录,而不窄化那次报的
        // 「23 棵」数的是**有候选文件的**树 —— 两个数并排一放就有人去减,减出来的
        // 那个「10 棵没扫」谁都验证不了。各自标清自己数的是什么,不请人做减法。
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
    /// 「一个正数或 all」这条取值规则的唯一产地。两道闸同一个形状,分头写就会分头走形
    /// (<c>--limit</c> 的 all/none/0/-1 四种写法是 07-② 实证的真实调用形态)。
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
    /// 不含斜杠的 glob(如 <c>*.cs</c>)按文件名匹配,含斜杠的按相对路径整体匹配 ——
    /// 这正是调用方写 <c>*.cs</c> 时期望的意思。
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
