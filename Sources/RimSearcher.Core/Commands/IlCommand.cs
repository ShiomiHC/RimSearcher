using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 一个方法的 IL。
///
/// 与 <c>read</c> 的分工不在「符号级 / 文件级」,而在**问的是哪一种东西**:反编译出来的 C#
/// 为了可读会把迭代器还原成 yield、闭包还原成 lambda、跳转表还原成 switch,而 transpiler
/// 匹配的正是还原之前那串指令。除此之外读逻辑一律看 C#:IL 不含更多语义,只是更啰嗦。
/// </summary>
public sealed class IlCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "il",
        Aliases = ["get-il", "disasm", "disassemble"],
        Summary = "Disassemble a method to IL.",
        Remarks =
            "Name the method as 'Verse.Pawn.Tick', 'Verse.Pawn::Tick', or 'M:Verse.Pawn.Tick' — all three " +
            "work, as does a bare type name plus member. A property is written by its C# name: 'Faction' " +
            "finds the get_Faction and set_Faction that IL actually holds. A constructor is '.ctor'. Every " +
            "overload of the name is disassembled, each under its own signature.\n\n" +
            "Iterators and async methods keep almost nothing in the method itself — the instructions live in " +
            "a compiler-generated state machine, and that is what a transpiler has to match. This command " +
            "says so and names the method to ask for instead; --state-machine goes there directly.\n\n" +
            "Page with --from/--to, which are IL offsets, not line numbers. A method with no body at all " +
            "(abstract, extern, an engine intrinsic) is reported as such rather than as an empty result.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "symbol",
                Variadic = true,
                Help = "The method to disassemble: 'Verse.Pawn.Tick', 'RimWorld.Need.CurLevel', " +
                       "'Verse.ThingDef..ctor'. Several methods go into the same table, in the order given; " +
                       "one that cannot be resolved is reported in a note and the others still print.",
            },
        ],
        Options =
        [
            CodeShared.Source,
            new OptionSpec
            {
                Name = "from",
                Aliases = ["start-offset", "offset-from"],
                Placeholder = "<offset>",
                Help = "Start at this IL offset. Decimal, or hex with an 0x prefix. The method header is " +
                       "always shown — instructions cannot be read without knowing the locals.",
            },
            new OptionSpec
            {
                Name = "to",
                Aliases = ["end-offset", "offset-to"],
                Placeholder = "<offset>",
                Help = "Stop after this IL offset.",
            },
            new OptionSpec
            {
                Name = "state-machine",
                Arity = Arity.Flag,
                Aliases = ["movenext", "follow"],
                Help = "Go straight to the state machine's MoveNext when the named method is an iterator or " +
                       "an async method. Without it the method itself is shown and the state machine is named.",
            },
            CommonOptions.Limit("lines"),
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
            new()
            {
                Key = "il",
                Rows = true,
                What = "one row per disassembled method: assembly, type, member, signature, bodyless, " +
                       "first_offset, last_offset, shown_from, shown_to, lines.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;
        if (asked.Count == 0)
            throw new CliUsageException("Name the method to disassemble, for example 'Verse.Pawn.Tick'.");

        ctx.Report.Promises("il");

        using var lookup = CodeShared.Open(ctx, out _);

        // 几个符号并成一串方法,下面的反汇编循环一个字没改 —— 行里带着 assembly / type /
        // member 三列,块与块分得开。Locate 落空时自己已经说了话(它分得清「这不是方法」
        // 「这是个类型」「这棵树没读过」),所以这里只管接着往下走。
        var methods = new List<MethodHit>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
            foreach (var m in Locate(ctx, lookup, SymbolRef.Parse(one)))
                if (seenMethods.Add($"{m.Type.Assembly} {m.Type.FullName} {m.Name} {m.Signature}"))
                    methods.Add(m);

        if (methods.Count == 0) return 1;

        var follow = ctx.Args.Flag("state-machine");
        var from = ParseOffset(ctx.Args.Value("from"), "--from");
        var to = ParseOffset(ctx.Args.Value("to"), "--to");
        var limit = ctx.Limit();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var printed = 0;

        foreach (var found in methods)
        {
            var method = follow && found.StateMachine is not null ? found.StateMachine : found;

            if (method.Bodyless)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"{method.Type.FullName}.{method.SourceName} has no method body — it is abstract, extern, " +
                    "or implemented by the runtime itself. There is no IL to show; that is a fact about the " +
                    "method, not an empty result. Its overriding implementations do have one: " +
                    $"'rimsearcher types {method.Type.FullName} --derived'.");
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

            if (listing.Trimmed)
                ctx.Report.Notice(NoticeKind.Truncation,
                    $"Showing IL_{listing.ShownFrom:x4} to IL_{listing.ShownTo:x4} of {method.SourceName}; the " +
                    $"body runs from IL_{listing.FirstOffset:x4} to IL_{listing.LastOffset:x4}. " +
                    $"'--from 0x{listing.ShownTo:x}' picks up at the last instruction shown here, so the two " +
                    "pages overlap by one rather than risk a gap.");

            if (!follow && found.StateMachine is { } sm)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"{found.Type.FullName}.{found.SourceName} is an iterator or async method, so what is above " +
                    "is only the shell that builds the state machine — the instructions that actually run are " +
                    $"in {sm.Type.FullName}::MoveNext, and that is what a transpiler has to match. " +
                    $"'rimsearcher il {found.Type.FullName}.{found.SourceName} --state-machine' shows them.");
        }

        ctx.Report.Table("il", ["assembly", "type", "member", "signature", "bodyless",
                               "first_offset", "last_offset", "shown_from", "shown_to", "lines"], rows);
        return 0;
    }

    private static IReadOnlyDictionary<string, object?> Row(MethodHit m, IlListing? l)
        => new Dictionary<string, object?>
        {
            ["assembly"] = m.Type.Assembly.Name,
            ["type"] = m.Type.FullName,
            ["member"] = m.Name,
            ["signature"] = m.Signature,
            ["bodyless"] = m.Bodyless,
            ["first_offset"] = l?.FirstOffset,
            ["last_offset"] = l?.LastOffset,
            ["shown_from"] = l?.ShownFrom,
            ["shown_to"] = l?.ShownTo,
            ["lines"] = l?.Lines.Count ?? 0,
        };

    /// <summary>
    /// 符号名 → 方法。写法有好几种,一种一种试,而每一种都可能命中多个类型 ——
    /// 同名类型分布在不同 mod 里很常见,挑一个等于替调用方做了它看不见的选择。
    /// </summary>
    internal static IReadOnlyList<MethodHit> Locate(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol)
    {
        if (!symbol.HasMember)
        {
            // 只写了一个词。当类型名它没有成员可读,当成员名倒可能有。
            var bare = lookup.FindMethodsAnywhere(symbol.TypeName, Limits.MaxSuggestions * 4);
            if (bare.Count > 0) return bare;

            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{symbol.Raw}' names no method. Write the type as well — 'Verse.Pawn.Tick' — or use " +
                "'rimsearcher members' to search member names across every assembly.");
            return [];
        }

        var types = lookup.FindTypes(symbol.TypeName);
        if (types.Count == 0)
        {
            // `A.B.C` 也可能整串就是一个类型全名,而上面把它切成了类型 A.B 加成员 C。
            var whole = lookup.FindTypes(symbol.AsWholeType().TypeName);
            if (whole.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{symbol.Raw}' is a type, not a method. 'rimsearcher members {symbol.Raw}' lists what is in it.");
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

        if (types.Count > 1)
            ctx.Report.Notice(NoticeKind.Filter,
                $"'{symbol.TypeName}' names {Tally.Complete(types.Count).Render("type")} across the assemblies " +
                $"read here: {NameList.Render([.. types.Select(t => t.FullName)], 6)}. Every match is shown; " +
                "writing the namespace picks one.");

        return found;
    }

    private static int? ParseOffset(string? raw, string flag)
    {
        if (raw is not { Length: > 0 }) return null;
        var s = raw.Trim();
        var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) s = s[2..];
        // IL_00a4 是输出里那一列的写法,粘回来时该认得。
        if (s.StartsWith("IL_", StringComparison.OrdinalIgnoreCase)) { s = s[3..]; hex = true; }

        var ok = hex
            ? int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                           System.Globalization.CultureInfo.InvariantCulture, out var v)
            : int.TryParse(s, out v);

        if (!ok)
            throw new CliUsageException(
                $"{flag} takes an IL offset: a decimal number, 0x-prefixed hex, or the 'IL_00a4' form the " +
                $"listing prints. '{raw}' is none of those.");
        return v;
    }
}
