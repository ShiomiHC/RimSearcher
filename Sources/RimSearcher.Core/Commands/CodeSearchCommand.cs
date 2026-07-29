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
                Help = "Only search files whose path matches this glob, for example *.cs or */Verse/*.",
                Default = "*.cs",
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
            var close = FuzzyMatcher.Rank(available.Select(a => a!), sourceName ?? "").Take(Limits.MaxSuggestions)
                                    .Select(t => t.Text).ToList();
            throw new CliUsageException(
                $"No decompiled source tree named '{sourceName}'." +
                (close.Count > 0 ? $" Closest: {string.Join(", ", close)}."
                                 : available.Count > 0 ? $" Available: {string.Join(", ", available)}." : ""));
        }

        var matcher = GlobToRegex(glob);
        var lines = new List<string>();
        var filesScanned = 0;
        var filesWithMatches = 0;
        var totalMatches = 0;
        var filesCapped = false;
        var perFileCapped = 0;
        var timedOut = new List<string>();

        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(searchRoot, file).Replace('\\', '/');
            if (!matcher.IsMatch(rel)) continue;

            if (filesScanned >= Limits.CodeSearchMaxFiles) { filesCapped = true; break; }
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

        // 三刀分开声明:被 --limit 截、被单文件上限截、被文件数上限截,原因不同,旋钮也不同。
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
                $"The scan stopped after {Limits.CodeSearchMaxFiles} files, so files later in the tree were never " +
                "read. Narrow it with --files or --source.");
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

        ctx.Report.Notice(NoticeKind.Boundary,
            $"{Tally.Complete(filesWithMatches).Render("file")} matched out of {filesScanned} scanned.");
        ctx.Report.Text("matches", lines);
        return 0;
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
