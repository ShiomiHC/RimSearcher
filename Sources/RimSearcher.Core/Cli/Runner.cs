using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Cli;

/// <summary>命令注册表。命令名与声明都住在各自的命令类里,这里只做寻址。</summary>
public sealed class CommandRegistry
{
    public const string ExeName = "rimsearcher";

    public const string Tagline =
        "Answers questions about RimWorld's defs and C# from a snapshot of what the game actually loaded.";

    /// <summary>
    /// 移除掉的旧命令名 → 现在叫什么。
    ///
    /// 为什么不留成别名:这些名字正是**因为被读错**才换掉的,留作别名等于把那道选择题
    /// 永远留在原地(<c>find</c> 与 <c>search</c> 在英语里几乎同义,而两条命令做的是相反
    /// 方向的事)。这张表只负责接住敲旧名的那一次,不让旧名继续可用。
    ///
    /// 非有它不可:近似候选救不了这一档 —— <c>find</c> 与 <c>where</c> 一个字母都不像,
    /// 编辑距离恒空,于是「Unknown command 'find'. 去看 --help」是唯一会印出来的话,
    /// 而它与「这个词从来就不是一条命令」逐字同形。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Retired =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["find"] = "where",
        };

    public IReadOnlyList<Command> Commands { get; } =
    [
        new SearchCommand(),
        new GetCommand(),
        new FindCommand(),
        new ListCommand(),
        new InheritCommand(),
        new KeyedCommand(),
        new FieldsCommand(),
        new ValuesCommand(),
        new ModsCommand(),
        new CodeSearchCommand(),
        new ReadCommand(),
        new SourcesListCommand(),
        new SourcesSyncCommand(),
        new SnapshotListCommand(),
        new SnapshotStatusCommand(),
        new SnapshotUseCommand(),
        new SnapshotTruncatedCommand(),
        new SnapshotImportCommand(),
        new ModListListCommand(),
        new ModListShowCommand(),
        new ModListSaveCommand(),
        new ExportCommand(),
        new DataModStatusCommand(),
        new DataModAttachCommand(),
        new DataModDetachCommand(),
        new DocsCommand(),
    ];

    public IReadOnlyList<CommandSpec> Specs => Commands.Select(c => c.Spec).ToList();

    /// <summary>argv → (命令, 剩余参数)。两段式子命令优先于单词命令。</summary>
    public (Command? Command, IReadOnlyList<string> Remaining) Resolve(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) return (null, []);

        // 第二个词得自己带字母数字才配当子命令名。<see cref="Matches"/> 的归一化那一条
        // 只留字母数字,于是 `keyed *` 归一化成 `keyed` 恰好等于命令名本身,那个词被当作
        // 命令名的一部分吃掉 —— argv 短了一截,而没有一个字说过。位置参数可选的命令
        // (list / keyed)在那里与真·无参调用逐字同形。
        if (argv.Count >= 2 && ArgParser.Normalize(argv[1]).Length > 0)
        {
            var two = argv[0] + " " + argv[1];
            var hit2 = Commands.FirstOrDefault(c => Matches(c.Spec, two));
            if (hit2 is not null) return (hit2, argv.Skip(2).ToList());
        }

        var hit1 = Commands.FirstOrDefault(c => Matches(c.Spec, argv[0]));
        if (hit1 is not null) return (hit1, argv.Skip(1).ToList());

        // 子命令族被单独调用时(`snapshot` 不带子命令),给出该族的成员而不是「不认识」
        return (null, argv);
    }

    private static bool Matches(CommandSpec spec, string name)
        => string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase)
        || spec.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
        || string.Equals(ArgParser.Normalize(spec.Name), ArgParser.Normalize(name), StringComparison.Ordinal);

    public IReadOnlyList<string> SuggestCommands(string typed)
    {
        var names = Commands.Select(c => c.Spec.Name).ToList();

        // `snapshot` / `modlist` 单独打进来时,要的是这一族有哪些子命令,不是最像的别的命令。
        var family = names.Where(n => n.StartsWith(typed + " ", StringComparison.OrdinalIgnoreCase)).ToList();
        if (family.Count > 0) return family;

        // 打分器与编辑距离并列:字母换位(serach → search)在打分器上落在分数线下方。
        var norm = ArgParser.Normalize(typed);
        var byDistance = names
            .Select(n => (name: n, d: ArgParser.Distance(norm, ArgParser.Normalize(n))))
            .Where(t => t.d <= 2)
            .OrderBy(t => t.d).ThenBy(t => t.name, StringComparer.Ordinal)
            .Select(t => t.name).ToList();

        var ranked = FuzzyMatcher.Rank(names, typed, threshold: 60).Select(t => t.Text);
        return byDistance.Concat(ranked).Distinct(StringComparer.Ordinal).Take(Limits.MaxSuggestions).ToList();
    }
}

public static class Runner
{
    /// <summary>用法错误。</summary>
    public const int ExitUsage = 2;
    /// <summary>命令跑通了,但没有结果。</summary>
    public const int ExitNoResults = 1;
    /// <summary>工具自己的缺陷。与「用法错」「没结果」分开,脚本才不会把故障当空集。</summary>
    public const int ExitInternal = 70;

    /// <summary>
    /// <paramref name="argv"/> 的第 1 位之后,去掉全局选项(及其取值)还剩下什么。
    ///
    /// 匹配规则跟着 <see cref="ArgParser"/> 走 —— 长短名、别名、<c>--db=path</c> 这种
    /// 内联取值都认。
    /// </summary>
    private static IReadOnlyList<string> NonGlobalArgs(IReadOnlyList<string> argv)
    {
        var rest = new List<string>();
        for (var i = 1; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg.Length < 2 || arg[0] != '-') { rest.Add(arg); continue; }

            var body = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg[1..];
            var eq = body.IndexOf('=');
            var inline = eq >= 0;
            if (inline) body = body[..eq];

            var key = ArgParser.Normalize(body);
            var opt = GlobalOptions.All.FirstOrDefault(o =>
                key == ArgParser.Normalize(o.Name) ||
                o.Aliases.Any(a => key == ArgParser.Normalize(a)) ||
                (body.Length == 1 && o.Short == body[0]));
            if (opt is null) { rest.Add(arg); continue; }

            // 取值型全局选项的值跟在下一位,它不是「多出来的一个词」。
            if (opt.Arity != Arity.Flag && !inline && i + 1 < argv.Count) i++;
        }
        return rest;
    }

    public static int Run(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var registry = new CommandRegistry();

        if (argv.Count == 0)
        {
            stdout.Write(HelpRenderer.RenderOverview(CommandRegistry.ExeName, registry.Specs,
                GlobalOptions.All, CommandRegistry.Tagline));
            return ExitUsage;
        }

        if (argv[0] is "-h" or "--help" or "help")
        {
            // 判据是「除全局选项之外还剩什么」,**不是** `argv.Count == 1`:后者会让
            // `rimsearcher --help --db foo.db` 掉进下面那条「Unknown command '--help'」。
            // 测试夹具也恒追加 --db/--config。
            var extra = NonGlobalArgs(argv);
            if (extra.Count == 0)
            {
                stdout.Write(HelpRenderer.RenderOverview(CommandRegistry.ExeName, registry.Specs,
                    GlobalOptions.All, CommandRegistry.Tagline));
                return 0;
            }

            // 剩下的多半是个命令名。`help <command>` 这个写法不接(真实调用方打的一律是
            // `<command> --help`),但该打的那一条要原样给出来,不能只说「不行」。
            var (named, _) = registry.Resolve(extra);
            stderr.Write(OutputText.Finish(
                $"'{argv[0]}' prints the command list and takes no command name. " +
                (named is not null
                    ? $"For the arguments of '{named.Spec.Name}', run " +
                      $"'{CommandRegistry.ExeName} {named.Spec.Name} --help'."
                    : $"For the arguments of one command, run '{CommandRegistry.ExeName} <command> --help'.")));
            return ExitUsage;
        }

        if (argv[0] is "--version")
        {
            stdout.Write(OutputText.Finish(BuildInfo.Version));
            return 0;
        }

        var (command, rest) = registry.Resolve(argv);
        if (command is null)
        {
            // 帮助里管它叫 "Global options",这个词本身暗示位置自由,放在命令前是很自然的
            // 写法 —— 于是要说破它是什么,而不是只说它不是命令。
            var asGlobal = argv[0].StartsWith('-')
                ? GlobalOptions.All.FirstOrDefault(o =>
                      ArgParser.Normalize(argv[0].TrimStart('-')) == ArgParser.Normalize(o.Name) ||
                      o.Aliases.Any(a => ArgParser.Normalize(argv[0].TrimStart('-')) == ArgParser.Normalize(a)))
                : null;
            if (asGlobal is not null)
            {
                stderr.Write(OutputText.Finish(
                    $"'{argv[0]}' is a global option, not a command, and it goes after the command: " +
                    // 开关型全局参数没有占位符,无条件拼接会给这条照抄用的命令留个尾空格。
                    $"'{CommandRegistry.ExeName} <command> ... --{asGlobal.Name}" +
                    (asGlobal.Placeholder is { Length: > 0 } ph ? " " + ph : "") + "'."));
                return ExitUsage;
            }

            // 退役名排在近似候选之前:这一档是**确知**的,而近似候选是猜的。
            // 参数不回显 —— 夹具会追加 --db <绝对路径>,照抄进基线就不可移植了;
            // 说清「参数原样不动」足够让人自己换掉那一个词。
            if (CommandRegistry.Retired.TryGetValue(argv[0], out var renamed))
            {
                stderr.Write(OutputText.Finish(
                    $"'{argv[0]}' was renamed to '{renamed}' and is no longer accepted. " +
                    $"Its arguments are unchanged, so the same call works with '{renamed}' in its place."));
                return ExitUsage;
            }

            var suggestions = registry.SuggestCommands(argv[0]);
            stderr.Write(OutputText.Finish(
                $"Unknown command '{argv[0]}'." +
                (suggestions.Count > 0 ? $" Did you mean {string.Join(" or ", suggestions.Select(s => $"'{s}'"))}?" : "") +
                $"\nRun '{CommandRegistry.ExeName} --help' for the full list."));
            return ExitUsage;
        }

        var parsed = ArgParser.Parse(command.Spec, GlobalOptions.All, rest, registry.Specs);

        if (parsed.WantsHelp)
        {
            stdout.Write(HelpRenderer.RenderCommand(CommandRegistry.ExeName, command.Spec, GlobalOptions.All));
            return 0;
        }

        if (parsed.HasErrors)
        {
            var lines = parsed.Errors.ToList();
            lines.Add($"Run '{CommandRegistry.ExeName} {command.Spec.Name} --help' to see what this command accepts.");
            stderr.Write(OutputText.Join(lines));
            return ExitUsage;
        }

        RimConfig config;
        try { config = RimConfig.Load(parsed.Value("config")); }
        catch (TomlError ex) { stderr.Write(OutputText.Finish(ex.Message)); return ExitUsage; }

        var ctx = new CommandContext(config, parsed) { Progress = stderr };
        try
        {
            // 显式点名的快照先验一遍,**不管这条命令后面用不用得上库**。寻址懒是有道理的,
            // 「参数合不合法」跟着懒没有:成因与两种命运见 SnapshotCatalog.ValidateExplicit。
            SnapshotCatalog.ValidateExplicit(config, parsed.Value("db"), parsed.Value("snapshot"));

            // 数据键恒在,产地在声明层(JsonKeySpec.Rows)。在**开查之前**发,而不是在
            // 零行分支里补。条件性的键(互斥的那几对)仍由命令在自己那条分支上认领。
            foreach (var key in command.Spec.JsonKeys.Where(k => k.Rows))
                ctx.Report.Promises(key.Key);

            var code = command.Run(ctx);
            // 位置等结果才定得下的那几条,在这里落位 —— 命令自己不必逐个记得收尾。
            ctx.Report.Settle();
            stdout.Write(ctx.Json ? JsonRenderer.Render(ctx.Report) : TextRenderer.Render(ctx.Report));
            return code;
        }
        catch (CliUsageException ex)
        {
            stderr.Write(OutputText.Finish(ex.Message));
            return ExitUsage;
        }
        catch (Exception ex) when (ex is SnapshotFormatError or SnapshotFormatException)
        {
            stderr.Write(OutputText.Finish(ex.Message));
            return ExitUsage;
        }
        catch (Exception ex)
        {
            // 兜底:调用方要分得清「我用错了」和「这工具坏了」,退出码也分开,
            // 否则脚本会把内部故障当成「没查到」。栈保留头几帧供复述缺陷。
            var frames = (ex.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim());
            stderr.Write(OutputText.Finish(
                $"rimsearcher {BuildInfo.Version} hit an internal error; this is a defect in the tool, " +
                $"not in what you asked for.\n{ex.GetType().Name}: {ex.Message}\n" +
                string.Join("\n", frames)));
            return ExitInternal;
        }
        finally
        {
            ctx.Dispose();
        }
    }
}

public static class BuildInfo
{
    public const string Version = "0.1.0";
}
