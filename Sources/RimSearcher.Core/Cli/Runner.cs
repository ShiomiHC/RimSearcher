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

    public IReadOnlyList<Command> Commands { get; } =
    [
        new SearchCommand(),
        new GetCommand(),
        new FindCommand(),
        new ListCommand(),
        new InheritCommand(),
        new FieldsCommand(),
        new ValuesCommand(),
        new TypesCommand(),
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

        if (argv.Count >= 2)
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

        // 打分器与编辑距离并列。字母换位(serach → search)在打分器上恰好落在分数线下方,
        // 而它正是最常见的一类手误 —— 只用一把尺子就会漏掉。
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

    public static int Run(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var registry = new CommandRegistry();

        if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help" && argv.Count == 1)
        {
            stdout.Write(HelpRenderer.RenderOverview(CommandRegistry.ExeName, registry.Specs,
                GlobalOptions.All, CommandRegistry.Tagline));
            return argv.Count == 0 ? ExitUsage : 0;
        }

        if (argv[0] is "--version")
        {
            stdout.Write(OutputText.Finish(BuildInfo.Version));
            return 0;
        }

        var (command, rest) = registry.Resolve(argv);
        if (command is null)
        {
            // 帮助里管它叫 "Global options",那这个词本身就在暗示位置自由。放在命令前是
            // 很自然的写法,而「Unknown command --snapshot」只说了它不是命令,没说它是什么。
            var asGlobal = argv[0].StartsWith('-')
                ? GlobalOptions.All.FirstOrDefault(o =>
                      ArgParser.Normalize(argv[0].TrimStart('-')) == ArgParser.Normalize(o.Name) ||
                      o.Aliases.Any(a => ArgParser.Normalize(argv[0].TrimStart('-')) == ArgParser.Normalize(a)))
                : null;
            if (asGlobal is not null)
            {
                stderr.Write(OutputText.Finish(
                    $"'{argv[0]}' is a global option, not a command, and it goes after the command: " +
                    // 开关型全局参数没有占位符,拼上去就多一个空格 —— 一条给人照抄的命令,
                    // 尾巴上挂个空格是它自己不该有的东西。
                    $"'{CommandRegistry.ExeName} <command> ... --{asGlobal.Name}" +
                    (asGlobal.Placeholder is { Length: > 0 } ph ? " " + ph : "") + "'."));
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
            var code = command.Run(ctx);
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
            // 兜底。没有它,一个内部错误就是一堆裸栈追踪直接糊到调用方脸上,而调用方
            // 分不清「我用错了」和「这工具坏了」—— 两者的下一步完全不同。退出码也要分开,
            // 否则脚本会把内部故障当成「没查到」。栈保留头几帧:读输出的是要复述缺陷的人。
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
