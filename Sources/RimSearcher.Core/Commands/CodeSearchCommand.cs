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
            "'find', 'values', and 'search', which answer them from the snapshot exactly.",
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
                       "alone (*.cs is every .cs file at any depth); with a '/' it matches the whole relative path, " +
                       "where '*' stops at a '/' and '**' crosses it. So */Verse/* is one level down, **/Verse/** is any.",
                Default = "*.cs",
            },
            new OptionSpec
            {
                // 上限本身是对的(防止一条正则扫穿整棵树),但不可调就等于「建议你换个更小的树」,
                // 而当那棵树本身就超过上限时,这个建议是空的 —— 实测里 --source vanilla 换来
                // 一模一样的警告。旋钮必须存在,声明才有落点。
                Name = "max-files",
                Aliases = ["file-limit", "scan-limit", "max-scan"],
                Placeholder = "<n>",
                Help = "How many files the scan may read before it stops. Pass 'all' to lift the cap.",
                Default = Limits.CodeSearchMaxFiles.ToString(),
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
                Help = "Show this many lines above and below each match.",
                Default = "0",
            },
            CommonOptions.Limit("matches"),
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
                "No decompiled source tree is configured, so there is nothing to search. " +
                "Set 'decompiled_dir' in the config file to the directory holding the decompiled C#. " +
                "Symbol-level questions do not need it: the DecompilerServer MCP reads the assemblies directly.");

        var sourceName = ctx.Args.Value("source");
        var glob = ctx.Args.Value("files") ?? "*.cs";
        var contextLines = ctx.Args.Int("context", 0);
        var limit = ctx.Args.Limit();

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

        var searchRoot = sourceName is { Length: > 0 } ? Path.Combine(root, sourceName) : root;
        if (!Directory.Exists(searchRoot))
        {
            var available = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => n is not null).ToList()!
                : new List<string?>();
            throw new CliUsageException(NoSuchTree(sourceName, available.Select(a => a!)));
        }

        var matcher = GlobToRegex(glob);
        var maxFiles = ParseMaxFiles(ctx);
        var lines = new List<string>();
        var filesScanned = 0;
        var filesWithMatches = 0;
        var totalMatches = 0;
        var filesCapped = false;
        var perFileCapped = 0;
        var timedOut = new List<string>();
        var reached = new List<string>();
        var unreached = new List<string>();

        foreach (var (tree, files) in EnumerateTrees(searchRoot, sourceName))
        {
            // 空名字是「根目录下直接摆着的文件」那棵伪树,它没有名字可报,列进去只是个空占位。
            if (filesCapped) { if (tree.Length > 0) unreached.Add(tree); continue; }
            var before = filesScanned;

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(searchRoot, file).Replace('\\', '/');
            if (!matcher.IsMatch(rel)) continue;

            if (filesScanned >= maxFiles) { filesCapped = true; break; }
            filesScanned++;

            string[] text;
            try { text = File.ReadAllLines(file); }
            catch { continue; }

            var inFile = 0;
            var emitted = false;
            for (var i = 0; i < text.Length; i++)
            {
                bool hit;
                try { hit = regex.IsMatch(text[i]); }
                catch (RegexMatchTimeoutException) { timedOut.Add(rel); break; }
                if (!hit) continue;

                totalMatches++;
                inFile++;
                if (inFile > Limits.CodeSearchMatchesPerFile) { perFileCapped++; break; }
                if (totalMatches > limit.Effective) break;

                if (!emitted) { emitted = true; filesWithMatches++; }

                for (var c = Math.Max(0, i - contextLines); c <= Math.Min(text.Length - 1, i + contextLines); c++)
                    lines.Add($"{rel}:{c + 1}{(c == i ? ":" : "-")}{text[c].TrimEnd()}");
                if (contextLines > 0) lines.Add("--");
            }

            if (totalMatches > limit.Effective) break;
        }

            if (filesScanned > before) { if (tree.Length > 0) reached.Add(tree); }
            else if (filesCapped && tree.Length > 0) unreached.Add(tree);
            if (totalMatches > limit.Effective) break;
        }

        // 四刀分开声明:被 --limit 截、被单文件上限截、被文件数上限截、被正则超时截,
        // 原因不同,旋钮也不同。
        var shownMatches = Math.Min(totalMatches, limit.Effective);
        if (totalMatches > limit.Effective)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"Stopped after {Tally.AtLeast(shownMatches).Render("match")}; raise --limit to see more.");
        if (perFileCapped > 0)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"{Tally.Complete(perFileCapped).Render("file")} had more than {Limits.CodeSearchMatchesPerFile} " +
                "matches; only the first ones from each are shown.");
        if (filesCapped)
            ctx.Report.Notice(NoticeKind.Truncation,
                $"The scan stopped after reading {maxFiles} files." +
                // 说清**哪些树没被读到**。只说「后面的文件没读」等于没说 —— 实测里首屏
                // 全是 mod 代码、vanilla 一条没有,而输出没有任何迹象表明 vanilla 还在后面,
                // 一个不细看的人就会拿 mod 里的类当答案。
                (unreached.Count > 0
                    ? $" These source trees were never reached: {string.Join(", ", unreached)}."
                    : " Files later in the tree were never read.") +
                $" Raise it with --max-files all, or narrow with --source <tree> or --files <glob>.");
        if (timedOut.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"The pattern took longer than {Limits.CodeSearchRegexTimeoutMs} ms on " +
                $"{Tally.Complete(timedOut.Count).Render("file")}, which were skipped part-way. " +
                "A pattern with nested quantifiers is the usual cause.");

        if (lines.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No line matched in {Tally.Complete(filesScanned).Render("file")} under '{glob}'. " +
                "If you were looking for a def rather than code, 'rimsearcher search' and 'rimsearcher find' " +
                "answer that from the snapshot — the XML is not searched here.");
            return 1;
        }

        // 命中数放前面。问的是「有多少个这种形状的方法」,答的却是文件数 ——
        // 实测里有人差点把 140(文件)当成方法数报出去,还得自己 wc 一遍输出。
        var matchTally = totalMatches > limit.Effective
            ? Tally.AtLeast(shownMatches)
            : Tally.Complete(totalMatches);
        ctx.Report.Notice(NoticeKind.Boundary,
            $"{matchTally.Render("match")} in {Tally.Complete(filesWithMatches).Render("file")}, " +
            $"out of {filesScanned} scanned" +
            (reached.Count > 1 ? $" across {Tally.Complete(reached.Count).Render("source tree")}" : "") + ".");
        ctx.Report.Text("matches", lines);
        return 0;
    }

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
        var close = FuzzyMatcher.Rank(all, typed ?? "").Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
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
    /// </summary>
    private static IEnumerable<(string Tree, IEnumerable<string> Files)> EnumerateTrees(string searchRoot, string? sourceName)
    {
        if (sourceName is { Length: > 0 })
        {
            yield return (sourceName, Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories));
            yield break;
        }

        var dirs = Directory.EnumerateDirectories(searchRoot)
                            .OrderBy(d => Path.GetFileName(d) is "vanilla" or "Core" ? 0 : 1)
                            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                            .ToList();
        foreach (var d in dirs)
            yield return (Path.GetFileName(d), Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories));

        // 根目录下直接摆着的文件(没有分树的部署形态)。
        yield return ("", Directory.EnumerateFiles(searchRoot, "*", SearchOption.TopDirectoryOnly));
    }

    private static int ParseMaxFiles(CommandContext ctx)
    {
        var raw = ctx.Args.Value("max-files");
        if (string.IsNullOrEmpty(raw)) return Limits.CodeSearchMaxFiles;
        if (raw is "all" or "none" or "0" or "-1") return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException(
            $"--max-files takes a positive number or 'all'; got '{raw}'.");
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
