using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 一个类型里有什么。
///
/// 走元数据而不是读那份 .cs,是因为问的形状不一样:「哪些是 virtual 的」「哪些覆写了基类」
/// 在文本里只能靠正则猜关键字,而元数据上它们是位。继承来的成员在派生类的源文件里一个字
/// 都没有,那正是「查无此成员」最常见的假阴 —— <c>--inherited</c> 把基类链一起摊平。
/// </summary>
public sealed class MembersCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "members",
        Aliases = ["search-members", "member-search", "list-members", "find-member"],
        Summary = "List the members of a C# type, filtered by kind and by the modifiers on them.",
        Remarks =
            "Members are read from the assembly, not from the decompiled C#, so the filters below are the " +
            "metadata bits themselves rather than a guess at keywords in the text.\n\n" +
            "Only what the type declares is listed. Inherited members live on the base type and are not " +
            "repeated here — --inherited walks the chain and lists those too, each under the type that " +
            "declares it.\n\n" +
            "A property appears twice over: once as itself under the C# name, and once as the get_/set_ " +
            "methods IL actually holds. 'rimsearcher il' takes the latter.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "type",
                Variadic = true,
                // 可选,但不是「什么都不给也行」:Run 里要求它与 --name 至少有一个。
                // 必填时跨类型找一个成员名只剩 code-search 那条全文扫描,而那份信息
                // 就在元数据里。
                Required = false,
                Help = "A type name: 'Verse.ThingComp', 'ThingComp', or a fragment of one. Several names go " +
                       "into the same table — the 'type' and 'assembly' columns say which row belongs to " +
                       "which — and a name that matches nothing is reported in a note while the others " +
                       "still print. Leave it out and give --name instead to ask which types declare a " +
                       "member of that name.",
            },
        ],
        Options =
        [
            CodeShared.Source,
            new OptionSpec
            {
                Name = "name",
                // "member" 不在这里:read 有一个真的 --member(读哪个成员的源码)。
                Aliases = ["contains", "member-name"],
                Placeholder = "<text>",
                Help = "Only members whose name contains this text.",
                Narrows = true,
            },
            new OptionSpec
            {
                // 正名不叫 "kind":def 侧五条命令的 --type 收着 "kind" 当别名,而那里的
                // kind 指 def 类型。同一个词在两处指不同的东西时,先学会一处的人会把它带到
                // 另一处,而调用不报错。
                Name = "member-kind",
                Aliases = ["kinds", "of-kind"],
                Placeholder = "<kind>",
                Help = "Only one kind: method, constructor, property, field, or event. " +
                       "Comma-separated for several.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "static",
                Arity = Arity.Flag,
                Aliases = ["statics"],
                Help = "Only static members.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "instance",
                Arity = Arity.Flag,
                Aliases = ["non-static"],
                Help = "Only instance members.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "virtual",
                Arity = Arity.Flag,
                Aliases = ["virtuals", "patchable"],
                Help = "Only virtual members — the ones a subclass can override.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "abstract",
                Arity = Arity.Flag,
                Aliases = ["abstracts"],
                Help = "Only abstract members. These have no body of their own; the implementations are on " +
                       "the types that derive from this one.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "overrides",
                Arity = Arity.Flag,
                Aliases = ["overriding", "override"],
                Help = "Only members that override a base one. The test is the metadata bit, not a name " +
                       "match — a member that merely shares a name with a base member is not an override.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "access",
                Aliases = ["accessibility", "visibility"],
                Placeholder = "<level>",
                Help = "Only members at this accessibility: public, protected, internal, private, " +
                       "'protected internal', or 'private protected'.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "inherited",
                Arity = Arity.Flag,
                Aliases = ["with-inherited", "include-base"],
                Help = "Also list what the base types declare, each row saying which type declares it.",
            },
            CommonOptions.Limit("members"),
        ],
        UsesGlobals = true,
        Examples =
        [
            "rimsearcher members Verse.ThingComp",
            "rimsearcher members Verse.Pawn --virtual",
            "rimsearcher members Verse.Pawn --name Faction --inherited",
            "rimsearcher members RimWorld.GenRecipe --member-kind method --static",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "members",
                Rows = true,
                What = "one row per member: assembly, type, kind, member, signature, static, virtual, " +
                       "abstract, override, accessibility.",
            },
            EmptyCause.JsonKeyCounting("member"),
            Snapshot.DataLayers.AssemblyCopyJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var asked = ctx.Args.Positionals;

        // 两个都不给会变成把每个类型的每个成员印一遍 —— 那不是一个答案,是把「问什么」
        // 整个丢回去。名字必给,类型是可选的收窄:形状同数据侧的
        // `where --value X [--type T]`,那边早就是这样。
        var byName = ctx.Args.Value("name");
        if (asked.Count == 0 && byName is not { Length: > 0 })
            // 动词是 declare 不是 have:这条命令按**声明**匹配,而继承来的成员算在基类上。
            // 写 have 的话,问「哪些类型有这个成员」的人会拿一份只含声明处的名单当全集。
            // 同一个词在 --help、SKILL 与下面那两句里也都是 declare。
            throw new CliUsageException(
                "Name a type, for example 'Verse.ThingComp' — or, to ask which types declare a member of a " +
                "given name, give --name on its own: 'rimsearcher members --name HungerMultiplier'.");

        ctx.Report.Promises("members");

        using var lookup = CodeShared.Open(ctx, out _);

        // 不给类型:问的是「谁有这个成员」。元数据一直答得出,只是此前没有入口 ——
        // 跨类型找一个成员名只剩 code-search 那条 4 秒的全文扫描,而这条 0.3 秒。
        if (asked.Count == 0)
        {
            var found = lookup.FindMembersAnywhere(byName!, int.MaxValue, substring: true);
            if (found.Count == 0)
            {
                // 「继承的成员算在基类上」那句这里**不说**:它在有命中时承重(告诉读者
                // 名单为什么不含派生类),而零命中时解释不了这次的空 —— 真是继承来的,
                // 基类那条声明照样会命中。摆在这里等于给一个不成立的安慰。
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No type in the assemblies read here declares a member whose name contains '{byName}'. " +
                    $"'rimsearcher code-search {byName}' searches the C# text instead.");
                ctx.Report.Table("members", Columns, []);
                return 1;
            }
            return Emit(ctx, [.. found], byName!);
        }

        // 几个名字并成一串类型 —— 行里带着 type 与 assembly 两列,读的人分得出哪一行
        // 属于哪个类型,所以不必按名字切块。
        // 去重带上程序集:同名类型在两个 mod 里各有一份是常事,而两个名字(一个全名、
        // 一个片段)指到同一个类型时,印两遍看着像两个类型。
        var types = new List<TypeHit>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var one in asked)
        {
            var symbol = SymbolRef.Parse(one).AsWholeType();
            var hit = lookup.FindTypes(symbol.TypeName);
            if (hit.Count == 0) { CodeShared.SayNoType(ctx, lookup, symbol); continue; }

            // 「这个名字指到不止一个类型」是按**名字**说的话:几个名字一起给时,类型总数
            // 大于 1 是理所当然的,而这句要说的是「你写的这一个词有歧义」。
            if (hit.Count > 1)
                ctx.Report.Notice(NoticeKind.Filter,
                    $"'{symbol.TypeName}' names {Tally.Complete(hit.Count).Render("type")} across the assemblies " +
                    $"read here: {NameList.Render([.. hit.Select(t => t.FullName)], 6)}. Every one is listed; " +
                    "writing the namespace picks one.");

            foreach (var t in hit)
                if (seenTypes.Add(t.Assembly + " " + t.FullName))
                    types.Add(t);
        }

        if (types.Count == 0)
        {
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var inherited = ctx.Args.Flag("inherited");
        var hierarchy = inherited ? new Hierarchy(lookup) : null;

        var all = new List<MemberRow>();
        foreach (var t in types)
        {
            all.AddRange(lookup.Members(t));
            if (hierarchy is null) continue;

            foreach (var b in hierarchy.BaseChain(t.FullName))
                foreach (var bt in lookup.FindTypes(b).Take(1))
                    all.AddRange(lookup.Members(bt));
        }

        var declared = all.Count;
        var kept = Filter(ctx, all);

        if (kept.Count == 0)
        {
            SayNothingPassed(ctx, types[0], all, inherited, hierarchy);
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var limit = ctx.Limit();
        var rows = kept.Take(limit.Effective).Select(Row).ToList();

        if (kept.Count > limit.Effective)
            ctx.Report.TruncationNotice(Tally.Of(limit.Effective, kept.Count), "member",
                                        "Leave --limit out to get every one");
        else
            ctx.Report.CountNotice(Tally.Complete(kept.Count), "member");

        if (!inherited && types.Count == 1)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"Declared by {types[0].FullName} itself; --inherited adds what it inherits from its base types.", teach: true);

        ctx.Report.Table("members", Columns, rows);
        return 0;
    }

    /// <summary>
    /// 不给类型那条路的输出。与按类型问的那条共用 <see cref="Filter"/> 与同一张表 ——
    /// 分头渲染的话,同一个 --member-kind 在两条路上会长出两种行为,而调用不报错。
    ///
    /// 多出来的一句是**横跨了几个类型**:一个不带这个数的「12 members」读起来像某一个
    /// 类型有 12 个,而这条路上它们分属不同类型,type 列才是承重的那一列。
    /// </summary>
    private static int Emit(CommandContext ctx, List<MemberRow> found, string byName)
    {
        var declared = found.Count;
        var kept = Filter(ctx, found);

        if (kept.Count == 0)
        {
            // 每个给了的筛子单独拿掉能回来几个,各是 empty_because 的一行;一个都救不回来
            // (得同时拿掉两个)才退回那句话。
            if (!ctx.Report.EmptyBecause(SingleDrops(ctx, found), "member"))
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{byName}' matches {Tally.Complete(declared).Render("member")}, and the other filters " +
                    $"on this command kept none of them — no single one of them alone. '{ctx.Without(Sieves)}' " +
                    "is the name by itself.");
            ctx.Report.Table("members", Columns, []);
            return 1;
        }

        var limit = ctx.Limit();
        var rows = kept.Take(limit.Effective).Select(Row).ToList();

        if (kept.Count > limit.Effective)
            ctx.Report.TruncationNotice(Tally.Of(limit.Effective, kept.Count), "member",
                                        "Leave --limit out to get every one");
        else
            ctx.Report.CountNotice(Tally.Complete(kept.Count), "member");

        var types = kept.Select(r => r.Type.FullName).Distinct(StringComparer.Ordinal).Count();
        ctx.Report.Notice(NoticeKind.Boundary,
            $"These sit on {Tally.Complete(types).Render("type")} (the 'type' column), each listed under the " +
            "type that declares it.");

        ctx.Report.Table("members", Columns, rows);
        return 0;
    }

    /// <summary>
    /// 结构化过滤。每一条都是元数据上的一位,不是对名字的猜测。
    ///
    /// 顺序无所谓 —— 全是合取,而每一条落空的后果都由下面那句话统一交代。
    /// </summary>
    /// <summary>这条命令上会筛行的选项,按声明顺序 —— 零行成因按它逐个「单独拿掉」重算。</summary>
    private static readonly string[] Sieves =
        ["name", "member-kind", "static", "instance", "virtual", "abstract", "overrides", "access"];

    /// <summary>给了的每个筛子一行:单独拿掉它、别的照旧,能回来几个 member(Docs/25 乙1)。</summary>
    private static List<EmptyCause> SingleDrops(CommandContext ctx, List<MemberRow> all)
        => Sieves.Where(ctx.Args.Has)
                 .Select(o => new EmptyCause(ctx.FilterAsGiven(o), Filter(ctx, all, except: o).Count, ctx.Without(o)))
                 .ToList();

    private static List<MemberRow> Filter(CommandContext ctx, List<MemberRow> rows, string? except = null)
    {
        bool On(string option) => !string.Equals(option, except, StringComparison.Ordinal);

        var text = ctx.Args.Value("name");
        if (text is { Length: > 0 } && On("name"))
            rows = rows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        var kinds = ctx.Args.Value("member-kind");
        if (kinds is { Length: > 0 } && On("member-kind"))
        {
            var wanted = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = wanted.Where(k => !Kinds.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                throw new CliUsageException(
                    $"--member-kind does not know {NameList.Render(unknown, 4)}. It takes " +
                    $"{NameList.Render(Kinds, Kinds.Length)}.");
            rows = rows.Where(r => wanted.Contains(r.Kind, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (ctx.Args.Flag("static") && ctx.Args.Flag("instance"))
            throw new CliUsageException(
                "--static and --instance ask for opposite halves of the same set, so together they select " +
                "nothing. Leave both out to get all of them.");

        if (ctx.Args.Flag("static") && On("static")) rows = rows.Where(r => r.IsStatic).ToList();
        if (ctx.Args.Flag("instance") && On("instance")) rows = rows.Where(r => !r.IsStatic).ToList();
        if (ctx.Args.Flag("virtual") && On("virtual")) rows = rows.Where(r => r.IsVirtual).ToList();
        if (ctx.Args.Flag("abstract") && On("abstract")) rows = rows.Where(r => r.IsAbstract).ToList();
        if (ctx.Args.Flag("overrides") && On("overrides")) rows = rows.Where(r => r.IsOverride).ToList();

        var access = ctx.Args.Value("access");
        if (access is { Length: > 0 } && On("access"))
            rows = rows.Where(r => string.Equals(r.Accessibility, access, StringComparison.OrdinalIgnoreCase))
                       .ToList();

        return rows;
    }

    /// <summary>
    /// 一个也没剩下。**「这个类型是空的」与「过滤器筛光了」不是一回事**,而两者的下一步相反:
    /// 前者去基类找,后者松开过滤器。分不清时说前者,就是把一次筛选说成一件关于这个类型的事实。
    /// </summary>
    private static void SayNothingPassed(
        CommandContext ctx, TypeHit type, List<MemberRow> all, bool inherited, Hierarchy? hierarchy)
    {
        var declared = all.Count;
        if (declared == 0)
        {
            var chain = hierarchy?.BaseChain(type.FullName) ?? [];
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{type.FullName} declares no member at all — an empty marker type, or one whose contents are " +
                "all inherited." +
                (inherited && chain.Count == 0
                    ? " Its base types are outside the assemblies read here, so there is nothing further to walk."
                    : inherited ? "" : " '--inherited' lists what its base types declare."));
            return;
        }

        // 每个给了的筛子单独拿掉能回来几个,各是 empty_because 的一行;一个都救不回来才退回那句话。
        if (ctx.Report.EmptyBecause(SingleDrops(ctx, all), "member")) return;
        ctx.Report.Notice(NoticeKind.Filter,
            $"{type.FullName} declares {Tally.Complete(declared).Render("member")}" +
            (inherited ? " counting its base types" : "") +
            $", and none of them passed the filters given here — no single one of them alone. '{ctx.Without(Sieves)}' " +
            "lists every one.");
    }

    private static readonly string[] Kinds = ["method", "constructor", "property", "field", "event"];

    private static readonly string[] Columns =
        ["assembly", "type", "kind", "member", "signature", "static", "virtual", "abstract",
         "override", "accessibility"];

    private static IReadOnlyDictionary<string, object?> Row(MemberRow m)
        => new Dictionary<string, object?>
        {
            ["assembly"] = m.Type.Assembly.Name,
            ["type"] = m.Type.FullName,
            ["kind"] = m.Kind,
            ["member"] = m.Name,
            ["signature"] = m.Signature.Length > 0 ? m.Signature : null,
            ["static"] = m.IsStatic,
            ["virtual"] = m.IsVirtual,
            ["abstract"] = m.IsAbstract,
            ["override"] = m.IsOverride,
            ["accessibility"] = m.Accessibility.Length > 0 ? m.Accessibility : null,
        };
}
