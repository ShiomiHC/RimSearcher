using System.Diagnostics;
using RimTooling.Cli;
using RimTooling.IO;
using RimTooling.RimWorld;
using RimSearcher.Commands;
using RimSearcher.Config;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Storage;

namespace RimSearcher.Cli;

public sealed class CommandRegistry
{
    public const string ExeName = "rimsearcher";

    public const string Tagline =
        "依据游戏实际加载内容的快照，回答 RimWorld 的 def 与 C# 问题。";

    internal const string DefLayer = "def（XML）";
    internal const string CodeLayer = "C# 代码";
    internal const string EnvironmentLayer = "快照与环境";

    private static readonly IReadOnlyDictionary<string, string> Layers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["get"] = DefLayer, ["list"] = DefLayer, ["search"] = DefLayer, ["where"] = DefLayer, ["values"] = DefLayer,
        ["fields"] = DefLayer, ["inherit"] = DefLayer, ["keyed"] = DefLayer, ["economy"] = DefLayer,
        ["read"] = CodeLayer, ["members"] = CodeLayer, ["types"] = CodeLayer, ["code-search"] = CodeLayer,
        ["callers"] = CodeLayer, ["usages"] = CodeLayer, ["il"] = CodeLayer,
        ["snapshot"] = EnvironmentLayer, ["sources"] = EnvironmentLayer, ["modlist"] = EnvironmentLayer,
        ["export"] = EnvironmentLayer, ["mods"] = EnvironmentLayer,
    };

    private static readonly IReadOnlyDictionary<string, string> GroupSummaries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["snapshot"] = "已登记的快照：list、status、diff、use、rename、truncated、import。",
        ["sources"] = "反编译源码树：list、sync。",
        ["modlist"] = "以 .toml 文件保存的 mod 名单：list、show、save。",
    };

    internal static readonly CliSibling RimRunner = new("rimrunner",
    [
        "build-save", "cache", "cases", "config", "engine", "film", "history", "judge", "live", "machine",
        "reports", "run", "samples", "showcase", "start", "status", "stop", "unmount", "vram",
    ]);

    public static CommandRegistry Instance { get; } = new();

    public IReadOnlyList<Command> Commands { get; } =
    [
        new SearchCommand(),
        new GetCommand(),
        new FindCommand(),
        new ListCommand(),
        new InheritCommand(),
        new KeyedCommand(),
        new EconomyCommand(),
        new FieldsCommand(),
        new ValuesCommand(),
        new ModsCommand(),
        new CodeSearchCommand(),
        new ReadCommand(),
        new IlCommand(),
        new TypesCommand(),
        new MembersCommand(),
        new CallersCommand(),
        new UsagesCommand(),
        new SourcesListCommand(),
        new SourcesSyncCommand(),
        new SnapshotListCommand(),
        new SnapshotStatusCommand(),
        new SnapshotDiffCommand(),
        new SnapshotUseCommand(),
        new SnapshotRenameCommand(),
        new SnapshotTruncatedCommand(),
        new SnapshotImportCommand(),
        new ModListListCommand(),
        new ModListShowCommand(),
        new ModListSaveCommand(),
        new ExportCommand(),
    ];

    public IReadOnlyList<CliOperation> Operations => Commands.Select(c => c.Operation).ToList();

    public CliRegistry Cli { get; }

    private readonly Dictionary<CliOperation, Command> _byOperation = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, (string Group, string Verb)> _topAliases = new(StringComparer.Ordinal);

    public CommandRegistry()
    {
        var singles = new Dictionary<string, CliCommand>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<CliOperation>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var command in Commands)
        {
            var operation = command.Operation;
            var top = OwningGroup(operation) ?? operation.Name;
            _byOperation[operation] = command;
            if (!order.Contains(top, StringComparer.Ordinal)) order.Add(top);

            if (operation.Verb is null)
            {
                singles[top] = new CliCommand
                {
                    Name = operation.Name,
                    Aliases = operation.Aliases,
                    Summary = operation.Summary,
                    Layer = Layers[top],
                    Operations = [operation],
                };
                continue;
            }

            (groups.TryGetValue(top, out var ops) ? ops : groups[top] = []).Add(operation);
            foreach (var alias in operation.Aliases.Where(a => !a.Contains(' ')))
                _topAliases[ArgText.Normalize(alias)] = (top, operation.Verb);
        }

        Cli = new CliRegistry
        {
            ExeName = ExeName,
            Tagline = Tagline,
            Commands = order.Select(name => singles.TryGetValue(name, out var single)
                ? single
                : new CliCommand
                {
                    Name = name,
                    Summary = GroupSummaries[name],
                    Layer = Layers[name],
                    IsGroup = true,
                    BareShowsHelp = true,
                    BareHelpExitCode = ExitCodes.Usage,
                    Operations = groups[name],
                }).ToList(),
            GlobalOptions = GlobalOptions.All,
            LayerOrder = [DefLayer, CodeLayer, EnvironmentLayer],
            Siblings = [RimRunner],
            OverviewExitCodes =
            [
                new(ExitCodes.Ok, "查询有结果。"),
                new(ExitCodes.EmptyOrFindings, "无结果；该结果经实测得出。"),
                new(ExitCodes.Usage, "用法错误：命令行、配置文件、mod 名单或快照无法使用。"),
                new(Runner.ExitFoundElsewhere, "无结果：名字另有所指（输出的表列出所指对象与位置）。"),
                new(Runner.ExitLayerAbsent, "无结果：缺少回答所需的层（输出的表列出该层）。"),
                new(ExitCodes.GameUnavailable, "export 未完成：游戏未写出导出文件，或已被停止。"),
                new(ExitCodes.InternalFault, "工具自身的缺陷。"),
            ],
            Policy = ParsePolicy.Forgiving with { MaxSuggestions = Limits.MaxSuggestions, RejectDuplicateSingleValue = true },
            Messages = CliMessages.Chinese with { OptionGivenTwice = flag => $"{flag} 只能指定一次。" },
            HelpListsAliases = false,
            HelpListsRefused = false,
        };
    }

    public Command Of(CliOperation operation) => _byOperation[operation];

    public static string? OwningGroup(CliOperation operation) =>
        operation.Verb is { } verb ? operation.Name[..^(verb.Length + 1)] : null;

    public (IReadOnlyList<string> Argv, string? Note) Rewrite(IReadOnlyList<string> argv)
    {
        var words = ExpandTopAlias(argv);
        var (command, rest) = Resolve(words);
        if (command is not null && Forward(command, rest) is (var target, var forwarded, { } note) &&
            !ReferenceEquals(target, command))
            return ([target.Operation.Name, .. forwarded], note);
        return (words, null);
    }

    private IReadOnlyList<string> ExpandTopAlias(IReadOnlyList<string> argv)
    {
        var first = argv.ToList().FindIndex(w => !w.StartsWith('-'));
        if (first < 0 || !_topAliases.TryGetValue(ArgText.Normalize(argv[first]), out var target) ||
            Cli.Resolve(argv).Command is not null)
            return argv;
        return [.. argv.Take(first), target.Group, target.Verb, .. argv.Skip(first + 1)];
    }

    public (Command? Command, IReadOnlyList<string> Remaining) Resolve(IReadOnlyList<string> argv)
    {
        var (_, operation, rest) = Cli.Resolve(ExpandTopAlias(argv));
        return operation is null ? (null, argv) : (Of(operation), rest);
    }

    public (Command Command, IReadOnlyList<string> Remaining, string? Note) Forward(Command command, IReadOnlyList<string> rest)
        => command is ListCommand
           && Commands.OfType<FindCommand>().FirstOrDefault() is { } where
           && ListCommand.AsWhere(command.Operation, where.Operation, GlobalOptions.All, rest) is { } asWhere
            ? (where, asWhere.Argv, asWhere.Note)
            : (command, rest, null);

    public string? GroupOf(string word) => Cli.FindCommand(word) is { IsGroup: true } group ? group.Name : null;
}

public static class Runner
{
    public const int ExitUsage = ExitCodes.Usage;
    public const int ExitNoResults = ExitCodes.EmptyOrFindings;
    public const int ExitFoundElsewhere = ExitCodes.FoundElsewhere;
    public const int ExitLayerAbsent = ExitCodes.Incomplete;
    public const int ExitInternal = ExitCodes.InternalFault;
    public const int ExitGameRun = ExitCodes.GameUnavailable;

    public static int Refine(int code, Report report)
    {
        if (code != ExitNoResults) return code;
        if (Printed(report, Report.AbsentTable)) return ExitLayerAbsent;
        if (report.Entries.OfType<TableBlock>().Any(t => t.Name == NameLookup.Table && t.Collection is null &&
                                                         t.Rows.Any(r => !report.StaysEmpty.Contains(r))))
            return ExitFoundElsewhere;
        return code;
    }

    private static bool Printed(Report report, string table)
        => report.Entries.OfType<TableBlock>().Any(t => t.Name == table && t.Collection is null && t.Rows.Count > 0);

    public static int Run(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr)
    {
        var mirror = new StringWriter();
        var clock = Stopwatch.StartNew();
        var code = Execute(argv, stdout, new TeeWriter(stderr, mirror), out var report, out var snapshot,
                           out var quiet);
        RunLog.TryWrite(argv, code, report, snapshot, quiet, code == ExitUsage ? mirror.ToString() : null, stderr,
                        clock.ElapsedMilliseconds);
        return code;
    }

    private sealed class TeeWriter(TextWriter primary, TextWriter copy) : TextWriter
    {
        public override System.Text.Encoding Encoding => primary.Encoding;
        public override void Write(char value) { primary.Write(value); copy.Write(value); }
        public override void Write(string? value) { primary.Write(value); copy.Write(value); }
        public override void Flush() { primary.Flush(); copy.Flush(); }
    }

    public static CommandLine Parse(IReadOnlyList<string> argv)
    {
        var registry = CommandRegistry.Instance;
        var cli = registry.Cli;

        var (words, forwarded) = registry.Rewrite(argv);

        TokenizedCommand tokenized;
        switch (CliParser.Tokenize(cli, words))
        {
            case TokenizeResult.Help help:
                return new CommandLine { Answered = help.Result.ExitCode, Stdout = HelpRenderer.Render(cli, help.Result) };
            case TokenizeResult.Version:
                return new CommandLine { Answered = 0, Stdout = OutputText.Finish(BuildInfo.Version) };
            case TokenizeResult.UsageError error:
                return new CommandLine { Answered = ExitUsage, Errors = error.Result.Errors };
            case TokenizeResult.Ready ready:
                tokenized = ready.Command;
                break;
            default:
                throw new UnreachableException();
        }

        var command = registry.Of(tokenized.Operation);
        var quiet = tokenized.Values.Keys.Any(o => string.Equals(o.Name, GlobalOptions.Quiet.Name, StringComparison.Ordinal));

        var lead = TypeLead.Apply(tokenized, CommandRegistry.ExeName);
        return CliParser.Validate(lead.Tokenized) switch
        {
            ParseResult.Success success => new CommandLine
            {
                Command = command,
                Args = new CommandArgs(success.Parsed),
                Deferred = lead.Deferred,
                Forwarded = forwarded,
                Quiet = quiet,
            },
            ParseResult.UsageError error => new CommandLine
            {
                Answered = ExitUsage,
                Errors = error.Errors.Select(e => lead.Unexpected is { } u && e == u.Base ? u.Base + u.Hint : e).ToList(),
                HelpCommand = tokenized.Operation.Name,
                Quiet = quiet,
            },
            _ => throw new UnreachableException(),
        };
    }

    internal static string HelpHint(string command) =>
        $"运行 '{CommandRegistry.ExeName} {command} --help' 查看本命令的用法与参数。";

    private static int Execute(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr,
                               out Report? report, out string? snapshot, out bool quiet)
    {
        report = null;
        snapshot = null;
        var line = Parse(argv);
        quiet = line.Quiet;
        if (line.Answered is { } answered)
        {
            stdout.Write(line.Stdout);
            if (line.Errors.Count > 0)
                stderr.Write(OutputText.Join(line.HelpCommand is { } named ? [.. line.Errors, HelpHint(named)] : line.Errors));
            return answered;
        }

        var (command, args, deferred, forwarded) = (line.Command!, line.Args!, line.Deferred, line.Forwarded);
        var parsed = args.Parsed;

        RimConfig config;
        try { config = RimConfig.Load(args.Value(GlobalOptions.Config)); }
        catch (TomlException ex) { stderr.Write(OutputText.Finish(ex.Message)); return ExitUsage; }
        RunLog.ArchiveDir = Path.Combine(
            Path.GetDirectoryName(config.Path) is { Length: > 0 } d ? d : Path.GetDirectoryName(RimConfig.DefaultPath)!,
            "runlog");

        var ctx = new CommandContext(config, args) { Progress = stderr, ReadsTranslations = command.ReadsTranslations };
        report = ctx.Report;
        snapshot = SnapshotOf(args, ctx);
        try
        {
            if (args.Has(GlobalOptions.Db) && args.Has(GlobalOptions.Snapshot))
                throw new CliUsageException("--db 与 --snapshot 不能同时传入：二者都指定查询哪份快照。");
            if (command.SidesByPosition && (args.Has(GlobalOptions.Db) || args.Has(GlobalOptions.Snapshot)))
                throw new CliUsageException(
                    $"'{parsed.Operation.Name}' 不接受 {(args.Has(GlobalOptions.Db) ? "--db" : "--snapshot")}：" +
                    "快照由位置参数 <old> 与 <new> 指定。");
            SnapshotCatalog.ValidateExplicit(config, args.Value(GlobalOptions.Db), args.Value(GlobalOptions.Snapshot));

            foreach (var key in parsed.Operation.JsonKeys.Where(k => k.Rows))
                ctx.Report.Promises(key.Key);

            var leadNote = deferred?.Note;
            if (deferred is { } candidate)
            {
                if (!ctx.Db.Types(ctx.Unscoped()).Any(t => string.Equals(t.Type, candidate.Lead, StringComparison.OrdinalIgnoreCase)))
                {
                    leadNote = null;
                    if (candidate.MovedTo is { } moved)
                        args.Parsed = parsed.With(
                            positionals: [candidate.Lead, .. parsed.Positionals],
                            values: parsed.Values.Where(kv => !ReferenceEquals(kv.Key, moved))
                                                 .ToDictionary(kv => kv.Key, kv => kv.Value));
                }
                else if (candidate.Conflict is { } conflict)
                {
                    stderr.Write(OutputText.Join([conflict, HelpHint(args.Operation.Name)]));
                    return ExitUsage;
                }
            }

            if (leadNote is not null)
                ctx.Report.Notice(NoticeKind.Filter, leadNote);
            if (forwarded is not null) ctx.Report.Notice(NoticeKind.Filter, forwarded);
            foreach (var note in args.Notes)
                ctx.Report.Notice(NoticeKind.Filter, note);

            var code = Refine(command.Run(ctx), ctx.Report);
            ctx.Report.Settle();
            snapshot = SnapshotOf(args, ctx);
            stdout.Write(ctx.Json
                ? JsonRenderer.Render(ctx.Report, ctx.Quiet)
                : TextRenderer.Render(ctx.Report, ctx.Quiet));
            return code;
        }
        catch (CliUsageException ex)
        {
            snapshot = SnapshotOf(args, ctx);
            stderr.Write(OutputText.Finish(ex.Message));
            return ExitUsage;
        }
        catch (GameRunException ex)
        {
            snapshot = SnapshotOf(args, ctx);
            stderr.Write(OutputText.Finish(ex.Message));
            return ExitGameRun;
        }
        catch (Exception ex) when (ex is SnapshotFormatError or SnapshotFormatException or ModListException or TomlException)
        {
            snapshot = SnapshotOf(args, ctx);
            stderr.Write(OutputText.Finish(ex.Message));
            return ExitUsage;
        }
        catch (Exception ex)
        {
            snapshot = SnapshotOf(args, ctx);
            var frames = (ex.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim());
            stderr.Write(OutputText.Finish(
                $"rimsearcher {BuildInfo.Version} 内部出错。\n" +
                $"{ex.GetType().Name}: {ex.Message}\n" +
                string.Join("\n", frames)));
            return ExitInternal;
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static string? SnapshotOf(CommandArgs args, CommandContext ctx)
    {
        if (ctx.SnapshotName is { Length: > 0 } name) return name;
        if (args.Value(GlobalOptions.Snapshot) is { Length: > 0 } alias) return alias;
        if (args.Value(GlobalOptions.Db) is { Length: > 0 } db) return Path.GetFileNameWithoutExtension(db);
        return null;
    }
}

public sealed record CommandLine
{
    public int? Answered { get; init; }

    public string Stdout { get; init; } = "";

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? HelpCommand { get; init; }

    public Command? Command { get; init; }
    public CommandArgs? Args { get; init; }

    public TypeLeadCandidate? Deferred { get; init; }

    public string? Forwarded { get; init; }

    public bool Quiet { get; init; }
}

public static class BuildInfo
{
    public const string Version = "0.1.0";
}
