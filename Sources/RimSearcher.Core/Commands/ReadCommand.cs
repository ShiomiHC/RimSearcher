using RimSearcher.Cli;
using RimSearcher.Output;
using RimTooling.Cli;
using System.Text.RegularExpressions;
using RimSearcher.Search;

namespace RimSearcher.Commands;

public sealed class ReadCommand : Command
{
    public const string ReadAsPhrase = " 读作 ";

    private static readonly CliOption Member = CliOption.Repeat("member") with
    {
        Aliases = ["method"],
        Placeholder = "<名称>",
        Help = "读取该成员的声明。可多次指定或写成 'A|B'，按给出的顺序读取多个成员。" +
               "'members' 输出的编译名同样可用：'get_Label' 读取该属性，" +
               "'.ctor' 读取构造函数，'op_Equality' 读取该运算符。",
    };

    internal static readonly CliOption Type = CliOption.Text("type") with
    {
        Aliases = ["class-name", "type-name", "extract-class"],
        Placeholder = "<名称>",
        Help = "读取整个类型，'ThingOwner' 包含同名的泛型类型，'ThingOwner<T>' 只取一个类型参数的那一个。" +
               "与 --member 一起指定时，改为限定该成员所属的类型。",
    };

    private static readonly CliOption LinesOption = CliOption.Text("lines") with
    {
        Aliases = ["line", "range"],
        Placeholder = "<a-b|a+n|a>",
        Help = "改为读取原始行：'400-460' 含两端（'-' 也可写作 ',' 或 ':'），'400+60' 为从第 400 行起的 60 行，" +
               "'400' 从该行读到文件末尾。",
    };

    private static readonly CliOption FromOption = CliOption.Text("from") with
    {
        Aliases = ["start"],
        Placeholder = "<n>",
        Help = "从该行起读取。与 --to 一起指定时为区间；单独指定时读到文件末尾。",
    };

    private static readonly CliOption ToOption = CliOption.Text("to") with
    {
        Aliases = ["end"],
        Placeholder = "<n>",
        Help = "读到该行为止（含该行）。单独指定时从第 1 行起。",
    };

    private static readonly CliOption Source = CliOption.Repeat("source") with
    {
        Aliases = ["tree", "mod", "mods"],
        Placeholder = "<名称>",
        Help = "只在该源码树内解析文件名。全部源码树见 'rimsearcher sources list'。" +
               $"不指定时，名称在每棵源码树中查找；{Sources.SourcePlanner.Listed([Sources.SourcePlanner.EngineTree, .. Sources.SourcePlanner.LastResortTrees])} " +
               "只在其他源码树都没有该名称时读取。路径以 '<源码树>/' 开头时，只在该源码树内解析。",
    };

    private static readonly CliOption AssemblyOption = CodeShared.AssemblyOption("只在该程序集的文件中解析文件名。");

    private static readonly CliOption MaxLineChars = CliOption.Text("max-line-chars") with
    {
        Placeholder = "<n>",
        Hidden = true,
    };

    internal static readonly CliOption Matching = CliOption.Text("matching") with
    {
        Aliases = ["grep", "pattern", "search", "match"],
        Placeholder = "<regex>",
        Help = "只输出匹配该 .NET 正则表达式的行，范围为本次调用其余选项所读的内容：整个文件、--lines 区间，" +
               "或 --member/--type 的声明。匹配行在行号后标 ':'，上下文行标 '-'。此时 --limit 计的是匹配行。",
    };

    internal static readonly CliOption Context = CliOption.Text("context") with
    {
        Short = 'C',
        Placeholder = "<n|a+b>",
        Help = "与 --matching 一起指定时，显示每处命中周围的行：N 为上下各 N 行，'2+8' 为上方 2 行、下方 8 行。" +
               "窗口不超出所读的范围，相接的窗口合并。",
        Default = "0",
    };

    private static readonly CliOption OutlineOption = CliOption.Flag("outline") with
    {
        Aliases = ["members"],
        Help = "列出文件中的类型与成员及其修饰符和行范围，不读取其中任何一个。",
    };

    private static readonly CliOption Limit = CliOption.Text("limit") with
    {
        Short = 'n',
        Aliases = ["count", "head"],
        Placeholder = "<n>",
        Help = "最多输出多少行。不指定时不设上限：输出 --lines、--outline、--member 或 " +
               "--type 所要求的内容，四者都未指定时输出整个文件。",
        Default = "全部行",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "read",
        Aliases = ["read-code", "cat", "show-source"],
        Summary = "从反编译源码中读取一个成员、一个类型或一段行范围的源码。",
        Positionals =
        [
            new CliPositional
            {
                Name = "file",
                NormalizeBackslashesInRefused = true,
                Variadic = true,
                Help = "反编译根目录下的路径（'vanilla/Verse/Pawn.cs'）、该路径的任意尾段、裸文件名（如 'Pawn.cs'），或带命名空间的类型名（如 'RimWorld.Bullet'）。" +
                       "一次调用可按给出的顺序读取多个文件。JSON 的每条记录、大纲表的「文件」列、文本的每个数据块标题行都带出处文件。--limit 计的是每个文件内的行。",
            },
        ],
        Options =
        [
            LoadedCode.IncludeOption,
            Member,
            Type,
            LinesOption,
            FromOption,
            ToOption,
            Source,
            AssemblyOption,
            MaxLineChars,
            Matching,
            Context,
            OutlineOption,
            Limit,
        ],
        Refused =
        [
            new()
            {
                Name = "regex",
                Aliases = ["find"],
                ValuePlaceholder = "<regex>",
                Where = "按模式筛选行在本命令写作 '--matching <regex>'。",
            },
            new()
            {
                Name = "after",
                Aliases = ["A", "B", "before", "after-context", "before-context"],
                ValuePlaceholder = "<n>",
                Where = "只取一侧的行写在 --context 里：'-C 0+<n>' 为上方 0 行、下方 <n> 行，'-C <n>+0' 反之。",
            },
        ],
        Examples =
        [
            "rimsearcher read Pawn.cs --outline",
            "rimsearcher read CompShield.cs --member CompTick",
            "rimsearcher read RimWorld/Ability.cs --member CanCast --member Activate",
            "rimsearcher read RimWorld.CompShield",
            "rimsearcher read vanilla/Verse/ThingComp.cs --lines 1-40",
            "rimsearcher read Pawn_HealthTracker.cs --matching Notify_ -C 2",
        ],
        JsonKeys =
        [
            new("source",
                "不带 --outline 时：每行源码对应一条记录，含 file、line、text；来自 --member/--type 的记录另有 kind 与 declaration，" +
                "--matching 下另有 is_match。整个文件、--lines 区间与 --member/--type 的声明都产出这个键，此时没有 'declarations'。"),
            new("declarations",
                "带 --outline 时：每处声明一行，含 file、kind、modifiers（声明开头的修饰符，逐字照录；没有时为 null）、" +
                "name、in（所属）、lines、at（可直接用于 --lines 的 'start-end' 区间）。"),
            EmptyCause.JsonKeyCounting("declaration"),
            NameLookup.CodeJsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("读取"));

        var asked = ctx.Args.Positionals;
        var members = ctx.Args.Values(Member)
            .SelectMany(m => m.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        members = PlainMembers(ctx, members);
        var type = ctx.Args.Value(Type);
        var range = ctx.Args.Value(LinesOption);
        var outline = ctx.Args.Flag(OutlineOption);
        var grep = ReadGrep.From(ctx, outline);

        var startAt = ctx.Args.Value(FromOption);
        var endAt = ctx.Args.Value(ToOption);
        var fromName = ctx.Args.Spelled(FromOption);
        var toName = ctx.Args.Spelled(ToOption);
        if (endAt is not { Length: > 0 }) toName = fromName == "--start" ? "--end" : "--to";
        if (startAt is not { Length: > 0 }) fromName = toName == "--end" ? "--start" : "--from";
        var rangeSpelling = "--lines";
        if (startAt is { Length: > 0 } || endAt is { Length: > 0 })
        {
            rangeSpelling = startAt is { Length: > 0 } && endAt is { Length: > 0 }
                ? $"{fromName}/{toName}"
                : startAt is { Length: > 0 } ? fromName : toName;
            if (range is { Length: > 0 })
                throw new CliUsageException(
                    $"--lines 与 {fromName}/{toName} 不能同时传入：二者是同一区间的两种写法。");

            static int Line(string? v, string name)
                => int.TryParse(v?.Trim(), out var n) && n > 0
                    ? n
                    : throw new CliUsageException(
                        $"{name} 须为从 1 起的行号；'{v?.Trim()}' 不是行号。");

            if (startAt is { Length: > 0 } && endAt is { Length: > 0 })
            {
                var from = Line(startAt, fromName);
                var to = Line(endAt, toName);
                if (to < from)
                    throw new CliUsageException(
                        $"{fromName} {from} 在 {toName} {to} 之后；较小的行号须写在前面。");
                range = $"{from}-{to}";
            }
            else if (startAt is { Length: > 0 })
                range = $"{Line(startAt, fromName)}";
            else
                range = $"1-{Line(endAt, toName)}";
        }

        if (range is { Length: > 0 } && (members.Count > 0 || type is { Length: > 0 }))
            throw new CliUsageException(
                $"{rangeSpelling} 与 --member/--type 不能同时传入：{rangeSpelling} 读取原始行，" +
                "--member/--type 定位声明。'--outline' 列出各声明及其行范围，可从中挑选区间。");

        if (outline && (range is { Length: > 0 } || members.Count > 0 || type is { Length: > 0 }))
            throw new CliUsageException(
                $"--outline 与 {(range is { Length: > 0 } ? rangeSpelling : members.Count > 0 ? ctx.Args.Spelled(Member) : ctx.Args.Spelled(Type))} " +
                "不能同时传入：--outline 列出整个文件的声明。");
        if (ctx.Args.Value(MaxLineChars) is { } cut && cut.Trim() != "0")
            throw new CliUsageException($"--max-line-chars 在 read 中只接受 0（收到 '{cut}'）：read 总是输出整行。");

        ctx.Report.Promises(outline ? "declarations" : "source");

        var sources = CodeShared.Trees(ctx);
        if (sources.Count > 1)
            throw new CliUsageException(
                Cjk.Join("read 只在一棵源码树内解析文件，而 --source 指定", Tally.Complete(sources.Count).Render("source tree")) + "：" +
                $"{string.Join("、", sources)}。指定一棵，或去掉 --source 以在每棵源码树中查找。");
        var selection = CodeShared.Select(ctx, root);
        if (selection.Trees.Count > 1)
            throw new CliUsageException(
                Cjk.Join("read 只在一棵源码树内解析文件，而 --assembly 指定的程序集分布在",
                         Tally.Complete(selection.Trees.Count).Render("source tree")) + "：" +
                $"{string.Join("、", selection.Trees)}。须指定位于同一棵源码树的程序集。");
        var sourceName = selection.Trees.Count == 1 ? selection.Trees[0] : null;
        if (sourceName is { Length: > 0 } && !Directory.Exists(Path.Combine(root, sourceName)))
            throw new CliUsageException(CodeSearchCommand.NoSuchTree(sourceName, SourcesShared.TreeNames(root)));

        var lines = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var read = 0;
        var failed = 0;

        using var oneListing = SourcePaths.OneListing();
        (asked, members) = SplitTypeMember(ctx, root, sourceName, asked, members, range, type, outline);
        (asked, members) = AbsorbStrayMembers(ctx, root, sourceName, asked, members);

        var files = new List<(string Rel, string[] Text)>();
        var load = new Lazy<LoadedCode?>(() => LoadedCode.Open(ctx, root));
        var scope = selection.Scope(ctx);
        var readAnyway = new List<(string Rel, CodeLoad State, string? Assembly)>();
        foreach (var wanted in asked)
        {
            if (Resolve(ctx, root, wanted, sourceName, selection, load, scope, readAnyway) is not { } rel) { failed++; continue; }
            try { files.Add((rel, File.ReadAllLines(Path.Combine(root, rel)))); }
            catch (Exception ex) { throw new CliUsageException($"无法读取 '{rel}'：{ex.Message}"); }
        }

        var batchMisses = files.Count > 1 && members.Count > 0 && !outline && type is not { Length: > 0 };
        var foundIn = members.Distinct(StringComparer.Ordinal)
                             .ToDictionary(m => m, _ => new List<string>(), StringComparer.Ordinal);
        if (batchMisses)
            foreach (var (rel, text) in files)
            {
                var decls = DeclarationsIn(text);
                foreach (var (m, at) in foundIn)
                    if (MemberName.Hits(decls, m).Count > 0) at.Add(rel);
            }

        foreach (var (rel, text) in files)
        {
            var status = grep is not null
                ? grep.Read(ctx, rel, text, members, type, range, asked.Count > 1, batchMisses, lines, rows)
                : Read(ctx, rel, text, members, type, range, outline, asked.Count > 1, batchMisses, lines, rows);
            if (status == 0) read++; else failed++;
        }

        if (batchMisses)
            foreach (var (m, at) in foundIn)
                if (at.Count == 0)
                    DeclarationMiss.Explain(ctx, [.. files.Select(f => (f.Rel, f.Text, DeclarationsIn(f.Text)))], m, null);
                else if (at.Count < files.Count)
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"'{m}' 声明于 {NameList.Render(at, files.Count)}；" +
                        $"{NameList.Render([.. files.Select(f => f.Rel).Except(at)], files.Count)} 中没有。");

        if (read == 0) return 1;

        if (readAnyway.Count > 0 && load.Value is { } opened)
            ctx.Report.Notice(NoticeKind.Boundary, NotLoaded(opened.Snapshot, readAnyway, files.Count == 1));
        if (outline) ctx.Report.Table("declarations", OutlineColumns, rows);
        else ctx.Report.Text("source", lines, rows);
        return 0;
    }

    private static (IReadOnlyList<string> Files, List<string> Members) SplitTypeMember(
        CommandContext ctx, string root, string? sourceName, IReadOnlyList<string> asked, List<string> members,
        string? range, string? type, bool outline)
    {
        if (asked.Count != 1 || members.Count > 0 || outline || range is { Length: > 0 } || type is { Length: > 0 })
            return (asked, members);
        var whole = GenericName.Canonical(asked[0]);
        var dot = whole.LastIndexOf('.');
        if (dot <= 0 || whole.Contains('/') || whole.Contains('\\') ||
            whole.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return (asked, members);
        var head = whole[..dot];
        var member = GenericName.Strip(whole[(dot + 1)..]);
        if (!Identifier.IsMatch(member)) return (asked, members);

        bool Found(string name) =>
            SourcePaths.Files(root, name, sourceName).Count > 0 ||
            SourcePaths.Files(root, name.Replace('.', '/') + ".cs", sourceName).Count > 0 ||
            SourcePaths.Files(root, ClassNameShape.Tail(name) + ".cs", sourceName).Count > 0;
        if (Found(whole) || !Found(head)) return (asked, members);

        if (GenericName.Split(head).Arity is not null) member = $"{ClassNameShape.Tail(head)}.{member}";
        head = GenericName.Strip(head);
        ctx.Report.Notice(NoticeKind.NextStep, $"'{asked[0]}' 按 '{head} --member {member}' 读取。");
        return ([head.Contains('.') ? head : head + ".cs"], [member]);
    }

    private static (IReadOnlyList<string> Files, List<string> Members) AbsorbStrayMembers(
        CommandContext ctx, string root, string? sourceName, IReadOnlyList<string> asked, List<string> members)
    {
        if (members.Count == 0) return (asked, members);
        var stray = asked.Where(a => Identifier.IsMatch(a) && SourcePaths.Files(root, a, sourceName).Count == 0)
                         .ToList();
        if (stray.Count == 0 || stray.Count == asked.Count) return (asked, members);

        var all = members.Concat(stray).Distinct(StringComparer.Ordinal).ToList();
        ctx.Report.Notice(NoticeKind.NextStep,
            $"{NameList.Render([.. stray.Select(s => $"'{s}'")], stray.Count)} " +
            (all.Count > 1
                ? $"不是文件，因此也按 --member 读取；多个成员可写在一个 --member 中：'{string.Join('|', all)}'。"
                : "不是文件，因此也按 --member 读取。"));
        return ([.. asked.Except(stray)], all);
    }

    private static List<string> PlainMembers(CommandContext ctx, List<string> given)
    {
        var plain = given.Select(m => (Given: m, Names: MemberName.Plain(m))).ToList();
        var rewritten = plain.Where(p => p.Names.Count != 1 || p.Names[0] != GenericName.Canonical(p.Given)).ToList();
        if (rewritten.Count > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                Cjk.Join("--member 只接受裸名称，因此",
                         string.Join("，", rewritten.Select(p => $"'{p.Given}'{ReadAsPhrase}'{string.Join('|', p.Names)}'"))) + "。");
        return [.. plain.SelectMany(p => p.Names)];
    }

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static string? Resolve(CommandContext ctx, string root, string wanted, string? sourceName, CodeShared.Selection selection,
                                   Lazy<LoadedCode?> load, LoadedCode.ReadScope scope,
                                   List<(string Rel, CodeLoad State, string? Assembly)> readAnyway)
    {
        TreeAssemblies? owners = selection.Assemblies is null ? null : new TreeAssemblies(root);
        var outside = new List<(string File, IReadOnlyList<string> Owners)>();
        var dropped = new List<(string Tree, CodeLoad State, string? Assembly)>();
        var norm = wanted.Replace('\\', '/').Trim('/');
        var pathTree = norm.IndexOf('/') is > 0 and var cut ? norm[..cut] : null;
        IReadOnlyList<string> Visible(IReadOnlyList<string> files, bool asWritten)
        {
            var kept = new List<string>();
            foreach (var f in files)
            {
                var slash = f.IndexOf('/');
                IReadOnlyList<string> of = [];
                if (owners is not null)
                {
                    of = slash > 0 ? owners.Owners(f[..slash], f[(slash + 1)..]) : [];
                    if (!of.Any(selection.Keeps)) { outside.Add((f, of)); continue; }
                }
                if (slash > 0 && !RimSearcher.Sources.SourcePlanner.IsReserved(f[..slash]) &&
                    load.Value?.Of(f[..slash], f[(slash + 1)..]) is { } st && LoadedCode.Unloaded(st.State))
                {
                    var tree = f[..slash];
                    var named = scope.Covers(tree, of) ||
                                asWritten && string.Equals(tree, pathTree, StringComparison.OrdinalIgnoreCase);
                    if (!named)
                    {
                        dropped.Add((tree, st.State, st.Assembly));
                        continue;
                    }
                }
                kept.Add(f);
            }
            return kept;
        }

        var hits = Visible(SourcePaths.Files(root, wanted, sourceName), asWritten: true);

        var slashNorm = GenericName.Strip(wanted.Replace('\\', '/').TrimEnd('/'));
        var looksLikeTypeName = !slashNorm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                             && ClassNameShape.Looks(slashNorm)
                             && ClassNameShape.Tail(slashNorm) != slashNorm;
        if (hits.Count == 0 && looksLikeTypeName)
            hits = Visible(SourcePaths.Files(root, slashNorm.Replace('.', '/') + ".cs", sourceName), asWritten: false);

        var bare = looksLikeTypeName ? ClassNameShape.Tail(slashNorm) + ".cs" : Path.GetFileName(slashNorm);
        var byName = hits.Count == 0 && bare.Length > 0 && bare != wanted
            ? Visible(SourcePaths.Files(root, bare, sourceName), asWritten: false)
            : [];

        if (hits.Count == 0 && byName.Count == 0 && load.IsValueCreated && load.Value is { } opened && dropped.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary, LoadedCode.LeftOut(opened, dropped.Distinct().ToList(), "file")!);
            return null;
        }
        if (hits.Count == 0 && byName.Count == 0 && outside.Count > 0)
        {
            var files = outside.Select(o => o.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var of = outside.SelectMany(o => o.Owners).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var into = of.Count == 0 ? "未列在该源码树项目文件中的程序集" : RimSearcher.Sources.SourcePlanner.Listed(of);
            ctx.Report.Notice(NoticeKind.Boundary, files.Count == 1
                ? Cjk.Join($"'{files[0]}' 匹配，编译进", into, "；本次 --assembly 保留的是", selection.Phrase) + "。"
                : $"'{wanted}' 匹配 {Tally.Complete(files.Count).Render("file")}：{NameList.Render(files, Limits.AmbiguousFiles)}；" +
                  Cjk.Join("这些文件编译进", into, "；本次 --assembly 保留的是", selection.Phrase) + "。");
            return null;
        }
        if (hits.Count == 0 && byName.Count == 0) { SayNoFile(ctx, root, wanted, sourceName); return null; }
        if (hits.Count > 1) { SayAmbiguous(ctx, wanted, hits); return null; }
        if (hits.Count == 0 && byName.Count > 1) { SayAmbiguous(ctx, bare, byName); return null; }

        var rel = hits.Count == 1 ? hits[0] : byName[0];
        var relSlash = rel.IndexOf('/');
        if (relSlash > 0 && !RimSearcher.Sources.SourcePlanner.IsReserved(rel[..relSlash]) &&
            load.Value?.Of(rel[..relSlash], rel[(relSlash + 1)..]) is { } relState && LoadedCode.Unloaded(relState.State))
            readAnyway.Add((rel, relState.State, relState.Assembly));

        if (hits.Count == 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{wanted}' 按路径找不到；恰有一个文件名为 " +
                $"'{bare}'，此处读取该文件。");
        return rel;
    }

    private static string NotLoaded(string snapshot, List<(string Rel, CodeLoad State, string? Assembly)> files, bool alone)
    {
        static string Why((string Rel, CodeLoad State, string? Assembly) f)
            => f.State == CodeLoad.ModNotInSnapshot ? "该 mod 不在其中" : $"程序集 {f.Assembly} 未加载";
        return $"快照 '{snapshot}' 中未加载：" +
               (alone ? Why(files[0]) : string.Join("；", files.Select(f => $"{f.Rel}（{Why(f)}）"))) + "。";
    }

    private static int Read(CommandContext ctx, string rel, string[] text,
                            IReadOnlyList<string> members, string? type, string? range, bool outline, bool batch,
                            bool batchMisses,
                            List<string> lines, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var cap = Cap(ctx);

        var window = cap;

        if (outline) return Outline(ctx, rel, text, cap, rows);

        if (members.Count > 0 || type is { Length: > 0 })
            return Declaration(ctx, rel, text, members, type, cap, batchMisses, lines, rows);

        return Raw(ctx, rel, text, range, cap, window, lines, rows, batch);
    }

    private static readonly string[] OutlineColumns =
        ["kind", "modifiers", "name", "in", "lines", "at", "file"];

    private static int Outline(CommandContext ctx, string rel, string[] text, int cap,
                               List<IReadOnlyDictionary<string, object?>> rows)
    {
        var decls = DeclarationsIn(text);
        if (decls.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{rel}（{Tally.Complete(text.Length).Render("line")}）中没有找到声明。");
            return 1;
        }

        var shown = decls.Take(cap).ToList();
        var tally = Tally.Of(shown.Count, decls.Count);
        ctx.Report.Notice(tally.IsTruncated ? NoticeKind.Truncation : NoticeKind.Count,
            tally.IsTruncated
                ? $"{tally.RenderTotalFirst("declaration", scope: $"{rel} 中")}。"
                : $"{tally.Render("declaration", scope: $"{rel} 中")}。", count: tally);
        rows.AddRange(
            shown.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["file"] = rel,
                ["kind"] = CsKind.Of(d.Kind),
                ["modifiers"] = d.Modifiers is { Length: > 0 } ? d.Modifiers : null,
                ["name"] = d.Display,
                ["in"] = d.Owner is { Length: > 0 } ? d.Owner + d.OwnerTypeParams : null,
                ["lines"] = d.Lines,
                ["at"] = $"{d.StartLine}-{d.EndLine}",
            }).ToList());

        return 0;
    }

    private static int Declaration(CommandContext ctx, string rel, string[] text,
                                   IReadOnlyList<string> members, string? type, int cap,
                                   bool batchMisses,
                                   List<string> lines, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var groups = Pick(ctx, rel, text, members, type, batchMisses);
        var picked = groups.SelectMany(g => g.Hits).ToList();
        if (picked.Count == 0) return 1;

        var printed = 0;
        var clipped = 0;
        (int From, int To)? resume = null;
        foreach (var d in picked)
        {
            if (lines.Count > 0) lines.Add("--");
            lines.Add($"{rel}:{d.StartLine}-{d.EndLine}  {CsKind.Of(d.Kind)} {d.Qualified}");
            for (var i = d.StartLine; i <= d.EndLine; i++)
            {
                if (printed >= cap)
                {
                    clipped += d.EndLine - i + 1;
                    resume ??= (i, d.EndLine);
                    break;
                }
                lines.Add(Numbered(i, text[i - 1]));
                rows.Add(new Dictionary<string, object?>
                {
                    ["file"] = rel,
                    ["line"] = i,
                    ["kind"] = CsKind.Of(d.Kind),
                    ["declaration"] = d.Qualified,
                    ["text"] = text[i - 1].TrimEnd(),
                });
                printed++;
            }
        }

        ctx.Report.Notice(clipped > 0 ? NoticeKind.Truncation : NoticeKind.Count,
            Tally.Complete(picked.Count).Render("declaration", scope: rel) +
            (clipped > 0
                ? Cjk.Join("；--limit 使输出止于", Tally.Complete(cap).Render("line"), "，另有",
                           Tally.Complete(clipped).Render("line"), "未输出") + "。" +
                  $"用 --lines {resume!.Value.From}-{resume.Value.To} 继续读取。"
                : Cjk.Join("，共输出", Tally.Complete(printed).Render("line")) + "。"));

        foreach (var (what, same) in groups.Where(g => g.Hits.Count > 1))
            ctx.Report.Notice(NoticeKind.Filter,
                $"'{what}' 在此处声明多次：" +
                string.Join("、", same.Select(d => $"{d.Qualified}（第 {d.StartLine}-{d.EndLine} 行）")) + "。");

        return 0;
    }

    internal static List<(string Name, List<CsDecl> Hits)> Pick(CommandContext ctx, string rel, string[] text,
                                                               IReadOnlyList<string> members, string? type,
                                                               bool batchMisses)
    {
        var decls = DeclarationsIn(text);
        var groups = new List<(string Name, List<CsDecl> Hits)>();
        if (members.Count > 0)
            foreach (var m in members.Distinct(StringComparer.Ordinal))
            {
                var hits = MemberName.Hits(decls, m, type);
                if (MemberName.MatchedByCase(hits, m))
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"'{m}'{ReadAsPhrase}'{hits[0].Name}'：C# 名称区分大小写，而 {rel} 中只有这一种拼写。");
                groups.Add((m, hits));
            }
        else
            groups.Add((type!, decls.Where(d => CsOutline.IsType(d.Kind) && GenericName.Is(type!, d.Name, d.TypeParams)).ToList()));

        if (!batchMisses)
            foreach (var (name, hits) in groups)
                if (hits.Count == 0)
                    DeclarationMiss.Explain(ctx, [(rel, text, decls)], members.Count > 0 ? name : null, type);
        return groups;
    }

    private static int Raw(CommandContext ctx, string rel, string[] text, string? range, int cap, int window,
                           List<string> lines, List<IReadOnlyDictionary<string, object?>> rows, bool batch)
    {
        var (from, to) = ParseRange(range, text.Length, window, out var rewritten);

        var echo = rewritten is null ? "" : $"--lines {range!.Trim()}{ReadAsPhrase}{rewritten}。";

        if (from > text.Length)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                echo + $"{rel} 有 {Tally.Complete(text.Length).Render("line")}，因此 --lines {range} 的起点超出文件末尾。");
            return 1;
        }

        to = Math.Min(to, text.Length);
        var clipped = 0;
        if (to - from + 1 > cap) { clipped = to - (from + cap - 1); to = from + cap - 1; }

        if (batch)
        {
            if (lines.Count > 0) lines.Add("--");
            lines.Add($"{rel}:{from}-{to}");
        }

        for (var i = from; i <= to; i++)
        {
            lines.Add(Numbered(i, text[i - 1]));
            rows.Add(new Dictionary<string, object?>
            {
                ["file"] = rel,
                ["line"] = i,
                ["text"] = text[i - 1].TrimEnd(),
            });
        }

        var complete = from == 1 && to == text.Length;

        ctx.Report.Notice(complete ? NoticeKind.Count : NoticeKind.Truncation,
            echo +
            (complete
                ? Cjk.Join($"{rel}，全部", Tally.Complete(text.Length).Render("line")) + "。"
                : Cjk.Join($"{rel}:{from}-{to}，共", Tally.Complete(text.Length).Render("line")) + "。" +
                  (clipped > 0
                      ? Cjk.Join("--limit 使输出比 --lines 所要求的少", Tally.Complete(clipped).Render("line")) + "。"
                      : "") +
                  (to < text.Length ? $"下一页用 --lines {to + 1}+{to - from + 1}。" : "")));

        return 0;
    }

    private static void SayNoFile(CommandContext ctx, string root, string wanted, string? sourceName)
    {
        var names = SourcePaths.AllFiles(root, sourceName).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
                                              .Select(n => n!).ToList();
        var close = Suggestion.Closest(names, GenericName.Strip(Path.GetFileName(wanted) ?? wanted));
        var members = sourceName is { Length: > 0 } || wanted.Contains('/') || wanted.Contains('\\')
            ? [] : NameLookup.Beyond(ctx, GenericName.Strip(wanted), NameLookup.Side.Code);

        ctx.Report.Notice(NoticeKind.NextStep,
            (sourceName is { Length: > 0 } ? $"源码树 {sourceName} 中没有" : "未找到") +
            $"名为 '{wanted}' 的文件。" +
            Suggestion.Say(close));
        NameLookup.Say(ctx, members);
    }

    private static void SayAmbiguous(CommandContext ctx, string wanted, IReadOnlyList<string> hits)
    {
        ctx.Report.Notice(NoticeKind.NextStep,
            $"'{wanted}' 匹配 {Tally.Complete(hits.Count).Render("file")}：" +
            NameList.Render(hits, Limits.AmbiguousFiles) + "。");
    }

    private static IReadOnlyList<CsDecl> DeclarationsIn(string[] text)
        => CsOutline.Scan(text).Where(d => d.Kind != "namespace").ToList();

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static string Numbered(int n, string text) => $"{n,6}  {text.TrimEnd()}".TrimEnd();

    internal static int Cap(CommandContext ctx)
    {
        var raw = ctx.Args.Value(Limit);
        if (string.IsNullOrEmpty(raw)) return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException(
            $"--limit 须为正整数（收到 '{raw}'）。");
    }

    private static string Normalize(string spec)
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

    internal static (int From, int To) ParseRange(string? spec, int total, int window, out string? rewritten)
    {
        rewritten = null;
        if (string.IsNullOrEmpty(spec)) return (1, Math.Min(total, window));

        var given = spec.Trim();
        var normalized = Normalize(given);
        if (!Same(normalized, given.Replace(" ", ""))) rewritten = normalized;
        spec = normalized;

        int At(string s, string? what)
            => int.TryParse(s.Trim(), out var v) && v > 0
                ? v
                : throw new CliUsageException(
                    $"--lines 须为从 1 起的行号；'{s.Trim()}' 不是行号" +
                    (what is null ? "。" : $"（{what}）。") +
                    "写作 '400-460'、'400+60' 或 '400'。");

        static int End(long from, long count) => (int)Math.Min(from + count - 1, int.MaxValue);

        var dash = spec.IndexOf('-');
        if (dash > 0)
        {
            var from = At(spec[..dash], "起点");
            var to = At(spec[(dash + 1)..], "终点");
            if (to < from)
                throw new CliUsageException($"--lines {spec} 的终点在起点之前；较小的行号须写在前面。");
            return (from, to);
        }

        var plus = spec.IndexOf('+');
        if (plus > 0)
        {
            var from = At(spec[..plus], "起点");
            var count = At(spec[(plus + 1)..], "行数");
            return (from, End(from, count));
        }

        var only = At(spec, null);
        return (only, End(only, window));
    }
}
