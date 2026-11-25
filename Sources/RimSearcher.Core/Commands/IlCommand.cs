using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class IlCommand : Command
{
    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只读取该程序集中的方法。");

    private static readonly CliOption From = CliOption.Text("from") with
    {
        Aliases = ["start-offset", "offset-from"],
        Placeholder = "<偏移>",
        Help = "从该 IL 偏移起。可写十进制、带 0x 前缀的十六进制，或反汇编列表中的 'IL_00a4' 形式。" +
               "方法头总是显示。",
    };

    private static readonly CliOption To = CliOption.Text("to") with
    {
        Aliases = ["end-offset", "offset-to"],
        Placeholder = "<偏移>",
        Help = "在该 IL 偏移之后停止。",
    };

    private static readonly CliOption StateMachineOption = CliOption.Flag("state-machine") with
    {
        Aliases = ["movenext", "follow"],
        Help = "所指方法为迭代器或异步方法时，直接转到状态机的 MoveNext。",
    };

    private static readonly CliOption LimitOption = CommonOptions.Limit("行");

    public override CliOperation Operation { get; } = new()
    {
        Name = "il",
        Aliases = ["get-il", "disasm", "disassemble"],
        Summary = "把方法反汇编为 IL。",
        Remarks =
            "「无方法体」列说明哪一种方法完全没有 IL：" +
            "abstract、extern（方法体为原生代码）" +
            "或运行时实现（由引擎提供）。",
        Positionals =
        [
            new CliPositional
            {
                Name = "symbol",
                Variadic = true,
                Help = "要反汇编的方法：'Verse.Pawn.Tick'、'RimWorld.Need.CurLevel'、'Verse.ThingDef..ctor'。" +
                       "带参数列表写出时（'SceneManager.LoadScene(string)'），只反汇编该重载。" +
                       "多个方法按给出的顺序进入同一张表。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            CodeShared.Source,
            AssemblyOption,
            From,
            To,
            StateMachineOption,
            LimitOption,
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher il Verse.Pawn.Tick",
            "rimsearcher il Verse.Pawn.GetGizmos --state-machine",
            "rimsearcher il RimWorld.MainTabWindow_Research.DrawProjectInfo --from 0x160 --to 0x3e0",
        ],
        JsonKeys =
        [
            new("il",
                "每个反汇编的方法一行：assembly、type、member、signature、" +
                "bodyless（否 / abstract / extern / 运行时实现 / 成因不明）、" +
                "first_offset、last_offset、shown_from、shown_to、lines。",
                Rows: true),
            Snapshot.DataLayers.AssemblyCopyJsonKey,
            NameLookup.CodeJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        if (asked.Count == 0)
            throw new CliUsageException("缺少要反汇编的方法，如 'Verse.Pawn.Tick'。");

        ctx.Report.Promises("il");

        using var lookup = CodeShared.Open(ctx, out _);

        var methods = new List<MethodHit>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
            foreach (var m in Locate(ctx, lookup, SymbolRef.Parse(one)))
                if (seenMethods.Add($"{m.Type.Assembly} {m.Type.FullName} {m.Name} {m.Signature}"))
                    methods.Add(m);

        if (methods.Count == 0) return 1;

        var follow = ctx.Args.Flag(StateMachineOption);
        var from = ParseOffset(ctx.Args.Value(From), "--from");
        var to = ParseOffset(ctx.Args.Value(To), "--to");
        if (from is { } f && to is { } t && t < f)
            throw new CliUsageException($"--from IL_{f:x4} 在 --to IL_{t:x4} 之后；较小的偏移须写在前面。");
        var limit = ctx.Limit(LimitOption);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var printed = 0;

        foreach (var found in methods)
        {
            var method = follow && found.StateMachine is not null ? found.StateMachine : found;

            if (method.Bodyless)
            {
                ctx.Report.Notice(NoticeKind.Boundary, method.BodyKind == "abstract"
                    ? $"{method.Type.FullName}.{method.SourceName} 为 abstract，因此没有 IL：" +
                      $"实现该方法的类型由 'rimsearcher types {method.Type.FullName} --derived --transitive' 列出。"
                    : $"{method.Type.FullName}.{method.SourceName} 在该程序集中没有 IL" +
                      $"（无方法体={Bodyless(method.BodyKind)}）。");
                rows.Add(Row(method, null));
                continue;
            }

            var file = lookup.FileOf(method.Type.Assembly);
            if (file is null) continue;

            var listing = IlText.Disassemble(file, method, from, to, limit.Count);
            printed++;

            var header = $"{method.Type.FullName}::{method.Name}  —  {method.Signature}";
            ctx.Report.Text($"il_{printed}", [header, .. listing.Lines], null);
            rows.Add(Row(method, listing));

            if (listing.Trimmed && listing.ShownFrom is null)
                ctx.Report.Notice(NoticeKind.Truncation,
                    $"未显示 {method.SourceName} 的任何指令；方法体从 IL_{listing.FirstOffset:x4} 到 IL_{listing.LastOffset:x4}。");
            else if (listing.Trimmed)
                ctx.Report.Notice(NoticeKind.Truncation,
                    $"显示 {method.SourceName} 的 IL_{listing.ShownFrom:x4} 至 IL_{listing.ShownTo:x4}；" +
                    $"方法体从 IL_{listing.FirstOffset:x4} 到 IL_{listing.LastOffset:x4}。" +
                    $"'--from 0x{listing.ShownTo:x}' 可从本次显示的最后一条指令接着读取。");

            if (!follow && found.StateMachine is { } sm)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"{found.Type.FullName}.{found.SourceName} 是{found.StateMachineKind}方法：上面的 IL " +
                    $"只构造状态机，方法体在 {sm.Type.FullName}::MoveNext 中运行。" +
                    $"MoveNext 的 IL 见 'rimsearcher il {found.Type.FullName}.{found.SourceName} --state-machine'。");
        }

        ctx.Report.Table("il", ["assembly", "type", "member", "signature", "bodyless",
                               "first_offset", "last_offset", "shown_from", "shown_to", "lines"], rows);
        return 0;
    }

    internal static string Bodyless(string? kind) => kind switch
    {
        null => "否",
        "runtime" => "运行时实现",
        "no-body" => "成因不明",
        _ => kind,
    };

    private static IReadOnlyDictionary<string, object?> Row(MethodHit m, IlListing? l)
        => new Dictionary<string, object?>
        {
            ["assembly"] = m.Type.Assembly.Name,
            ["type"] = m.Type.FullName,
            ["member"] = m.Name,
            ["signature"] = m.Signature,
            ["bodyless"] = Bodyless(m.BodyKind),
            ["first_offset"] = l?.FirstOffset,
            ["last_offset"] = l?.LastOffset,
            ["shown_from"] = l?.ShownFrom,
            ["shown_to"] = l?.ShownTo,
            ["lines"] = l?.Lines.Count ?? 0,
        };

    internal static IReadOnlyList<MethodHit> Locate(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol,
                                                    bool typeGoesToUsages = false)
    {
        if (!symbol.HasMember)
        {
            lookup.Ask([], () => lookup.FindMethodsAnywhere(symbol.TypeName, 1).Count > 0);
            var bare = lookup.FindMethodsAnywhere(symbol.TypeName, Limits.MaxSuggestions * 4);
            if (bare.Count > 0)
            {
                lookup.TreesInAnswer.UnionWith(bare.Select(m => m.Type.Assembly.Tree));
                return bare;
            }
            lookup.AnsweredFromEveryTree();
            if (CodeShared.LeftOutOnly(lookup)) return [];

            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{symbol.Raw}' 不是方法名。须一并写出类型，如 'Verse.Pawn.Tick'。");
            return [];
        }

        lookup.Ask([symbol.TypeName, symbol.AsWholeType().TypeName]);
        var types = lookup.FindTypes(symbol.TypeName);
        if (types.Count == 0)
        {
            var whole = lookup.FindTypes(symbol.AsWholeType().TypeName);
            lookup.TreesInAnswer.UnionWith(whole.Select(t => t.Assembly.Tree));
            if (whole.Count > 0 && typeGoesToUsages)
            {
                NameLookup.Say(ctx, new NameLookup.Sighting(
                    NameLookup.Where.CodeMember, symbol.Raw, "C# 类型",
                    string.Join("、", whole.Select(t => $"{t.Assembly.Tree}/{t.Assembly.Name}").Distinct()),
                    $"{CommandRegistry.ExeName} usages {whole[0].FullName}"));
                return [];
            }
            if (whole.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"il 只反汇编方法，而 '{symbol.Raw}' 是类型。该类型的成员见 'rimsearcher members {symbol.Raw}'。");
                return [];
            }

            CodeShared.SayNoType(ctx, lookup, symbol);
            return [];
        }

        var found = new List<MethodHit>();
        foreach (var t in types) found.AddRange(lookup.FindMethods(t, symbol.MemberName!));

        if (found.Count == 0)
        {
            CodeShared.SayNoMember(ctx, lookup, types[0], symbol.MemberName!);
            return [];
        }

        if (symbol.Parameters is { } written)
        {
            var picked = found.Where(m => SignatureText.ParametersMatch(m.ParameterTypes, written)).ToList();
            if (picked.Count == 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{symbol.TypeName}.{symbol.MemberName}' 没有接受 ({written}) 的重载。现有重载：" +
                    $"{NameList.Render([.. found.Select(m => $"{m.Name}({string.Join(", ", m.ParameterTypes)})").Distinct()], 6)}。");
                lookup.TreesInAnswer.UnionWith(found.Select(m => m.Type.Assembly.Tree));
                return [];
            }
            found = picked;
        }

        if (types.Count > 1)
            ctx.Report.Notice(NoticeKind.Filter,
                $"'{symbol.TypeName}' 在本次读取的程序集中对应 {Tally.Complete(types.Count).Render("type")}：" +
                $"{NameList.Render([.. types.Select(t => t.FullName)], 6)}。已全部列出；" +
                "写出命名空间可选定其中一个。");

        lookup.TreesInAnswer.UnionWith(found.Select(m => m.Type.Assembly.Tree));
        return found;
    }

    private static int? ParseOffset(string? raw, string flag)
    {
        if (raw is not { Length: > 0 }) return null;
        var s = raw.Trim();
        var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) s = s[2..];
        if (s.StartsWith("IL_", StringComparison.OrdinalIgnoreCase)) { s = s[3..]; hex = true; }

        var ok = hex
            ? int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                           System.Globalization.CultureInfo.InvariantCulture, out var v)
            : int.TryParse(s, out v);

        if (!ok || v < 0)
            throw new CliUsageException(
                $"{flag} 须为 IL 偏移：十进制数、带 0x 前缀的十六进制，或反汇编列表中的 'IL_00a4' 形式。" +
                $"'{raw}' 不符合以上任何一种写法。");
        return v;
    }
}
