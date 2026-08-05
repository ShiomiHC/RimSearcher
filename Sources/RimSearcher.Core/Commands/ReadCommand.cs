using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;

namespace RimSearcher.Commands;

/// <summary>
/// 从反编译树里读一段源码 —— 一个成员、一个类型,或者一段裸行。
///
/// 与 DecompilerServer MCP 的分工:符号级问题(谁调用它、它覆写了谁、派生了哪些)归 MCP,
/// 它读元数据,又快又准。这里管的是 MCP 不在场时的那条底线,以及 MCP 给不了的那件事 ——
/// 反编译产物**落盘的那一份**逐字是什么。
///
/// 成员定位靠配平大括号,不是语法分析(<see cref="CsOutline"/> 里写着它做不到什么),
/// 于是这条能力边界必须跟着输出走:找不到一个名字,只说明文本扫描没看见它。
/// </summary>
public sealed class ReadCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "read",
        Aliases = ["read-code", "cat", "show-source"],
        Summary = "Read source out of the decompiled tree — one member, one type, or a line range.",
        Remarks =
            "The file is named by its path relative to the decompiled root ('vanilla/Assembly-CSharp/Verse/" +
            "Pawn.cs'), by any tail of that path, or by its bare name. A path that is not there falls back " +
            "to the bare name and says so; when a bare name matches several files, the answer lists them " +
            "instead of picking one.\n\n" +
            "--member and --type find the declaration by matching braces, not by parsing C#. That is enough " +
            "for decompiled output, which is machine-formatted, but it means a name this command cannot see " +
            "is not proof the file lacks it — 'code-search' searches the text and --lines reads it raw.\n\n" +
            "For who calls a method, what it overrides, and what derives from a type, the DecompilerServer " +
            "MCP answers from metadata and is both faster and exact. This command answers a different " +
            "question: what the decompiled file on disk actually says.\n\n" +
            // 「不要拿 head 截这条命令的输出」写在声明层,因为声明层同时渲染 --help 与
            // cli-reference.md —— 拼命令时打开的正是那一份。
            "Page with --lines, never with a pipe. The first line of the answer says which lines these are " +
            "and how many the file has ('lines 1-150 of 330'), and a shell pipe that trims the output leaves " +
            "that line untouched — so the answer keeps claiming a range it no longer contains, and nothing " +
            "downstream can tell.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "file",
                Help = "A path under the decompiled root, a tail of one, or a bare file name such as 'Pawn.cs'.",
            },
        ],
        Options =
        [
            new OptionSpec
            {
                Name = "member",
                // 不收 "field":那个词在 get/inherit 上指 def 的字段路径,是另一个概念。
                Aliases = ["method", "method-name", "member-name", "property"],
                Placeholder = "<name>",
                Help = "Read the declaration of this member. Every member of that name in the file is " +
                       "returned; --type narrows it to one declaring type.",
            },
            new OptionSpec
            {
                Name = "type",
                // 不收 "class":那个词是 list 的主名,在那里指 def 自身的实现类。
                Aliases = ["class-name", "type-name", "extract-class"],
                Placeholder = "<name>",
                Help = "Read this whole type. With --member it instead says which type the member must " +
                       "belong to.",
            },
            new OptionSpec
            {
                Name = "lines",
                Aliases = ["line", "range", "line-range"],
                Placeholder = "<a-b|a+n|a|all>",
                Help = "Read raw lines instead: '400-460' is inclusive, '400+60' is sixty lines from 400, " +
                       "'400' starts there and takes the default window, 'all' is the whole file. " +
                       $"Without it the read starts at line 1 and takes {Limits.ReadWindow}.",
            },
            new OptionSpec
            {
                Name = "source",
                Aliases = ["root", "tree"],
                Placeholder = "<name>",
                Help = "Only resolve the file name inside this source tree. 'rimsearcher sources list' " +
                       "names them.",
            },
            new OptionSpec
            {
                Name = "outline",
                Arity = Arity.Flag,
                Aliases = ["members", "toc"],
                Help = "List the file's types and members with their modifiers and line ranges instead " +
                       "of reading any of them. This is the cheap way to find out what to ask for.",
            },
            new OptionSpec
            {
                Name = "limit",
                Short = 'n',
                Aliases = ["max-lines", "max-results", "count", "rows", "head"],
                Placeholder = "<n|all>",
                Help = $"How many lines to print at most. Values above {Limits.ReadMaxLines} are clamped to " +
                       $"it, because one type can be thousands of lines and this output is read whole.",
                Default = Limits.ReadMaxLines.ToString(),
            },
        ],
        Examples =
        [
            "rimsearcher read Pawn.cs --outline",
            "rimsearcher read CompShield.cs --member CompTick",
            "rimsearcher read vanilla/Assembly-CSharp/Verse/ThingComp.cs --lines 1-40",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "source",
                What = "without --outline: one row per source line — file, line, text, plus kind and " +
                       "declaration when the line came from --member/--type. The text form's line-number " +
                       "gutter is not repeated here. This is the key the three reading modes produce; " +
                       "'declarations' is absent then.",
            },
            new()
            {
                Key = "declarations",
                What = "with --outline: one row per declaration — kind, modifiers (the leading run of " +
                       "them, verbatim; null when there are none), name, in (the owner), lines, at " +
                       "(the 'start-end' range to hand back to --lines).",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("read"));

        var wanted = ctx.Args.Positional(0)!;
        var member = ctx.Args.Value("member");
        var type = ctx.Args.Value("type");
        var range = ctx.Args.Value("lines");
        var outline = ctx.Args.Flag("outline");

        // 「读哪一段」的三种说法互斥。不排优先级 —— 静默择一交出的是完全另一块代码,
        // 而这里当场就能说清。
        if (range is { Length: > 0 } && (member is { Length: > 0 } || type is { Length: > 0 }))
            throw new CliUsageException(
                "--lines reads raw lines and --member/--type find a declaration; they are two different " +
                "reads, so pass one or the other. '--outline' lists the declarations with their line ranges " +
                "if you want to pick a range from them.");

        // 两张表互斥,「读哪一种」在开查之前就定了。不能交给声明层统一发
        // (见 JsonKeySpec.Rows):两个都发就等于说「另一路也查过了,没有」。
        ctx.Report.Promises(outline ? "declarations" : "source");

        var sourceName = ctx.Args.Value("source");
        if (sourceName is { Length: > 0 } && !Directory.Exists(Path.Combine(root, sourceName)))
            throw new CliUsageException(CodeSearchCommand.NoSuchTree(sourceName, SourcesShared.TreeNames(root)));

        var hits = Resolve(root, wanted, sourceName);

        // 路径的中间段写错、文件名对,是最常见的一种落空 —— 而这条命令**已经能**按裸文件名
        // 定位。不重试的话,答案是一句「没有这个文件」外加一个不能直接粘贴的裸名候选,
        // 调用方得再跑一次 code-search 才拿得到路径:一个文件三次往返,而路径就在手上。
        var bare = Path.GetFileName(wanted.Replace('\\', '/').TrimEnd('/'));
        var byName = hits.Count == 0 && bare.Length > 0 && bare != wanted
            ? Resolve(root, bare, sourceName)
            : [];

        if (hits.Count == 0 && byName.Count == 0) { SayNoFile(ctx, root, wanted, sourceName); return 1; }
        if (hits.Count > 1) { SayAmbiguous(ctx, wanted, hits); return 1; }
        // 名字还是撞车就照旧不选。此时连「这条路径不存在」都不必单说 —— 名单里一条都不是
        // 调用方写的那条,这件事名单自己就说清了。
        if (hits.Count == 0 && byName.Count > 1) { SayAmbiguous(ctx, bare, byName); return 1; }

        var rel = hits.Count == 1 ? hits[0] : byName[0];

        // 说破是硬要求,不是礼貌:下面每一句印的都是解析出来的 rel,不说的话这次输出与
        // 「路径本来就写对了」逐字同形,而调用方会把那条错路径记下来接着用。
        // 真路径不在这句里复述 —— 紧接着的计数句就以它开头。
        if (hits.Count == 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{wanted}' is not a path under the decompiled root, but exactly one file is named " +
                $"'{bare}', and that is the one read here.");
        string[] text;
        try { text = File.ReadAllLines(Path.Combine(root, rel)); }
        catch (Exception ex) { throw new CliUsageException($"'{rel}' could not be read: {ex.Message}"); }

        var cap = Cap(ctx);

        if (outline) return Outline(ctx, rel, text, cap);
        if (member is { Length: > 0 } || type is { Length: > 0 })
            return Declaration(ctx, rel, text, member, type, cap);
        return Raw(ctx, rel, text, range, cap);
    }

    // ---- 三种读法 ----

    /// <summary>轮廓。读什么之前先知道有什么 —— 对上下文预算来说这是最便宜的一步。</summary>
    private static int Outline(CommandContext ctx, string rel, string[] text, int cap)
    {
        var decls = DeclarationsIn(text);
        if (decls.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No declaration was found in {rel} ({Tally.Complete(text.Length).Render("line")}). " +
                "Brace matching sees nothing here, which is what an XML file or a file of pure statements " +
                "looks like; '--lines all' reads it as it is.");
            return 1;
        }

        var shown = decls.Take(cap).ToList();
        // 路径进计数句,与 --lines 那一路同形。轮廓是「先看看有什么」那一步,而它此前
        // 只报个数 —— 名字是按裸文件名解析出来的时候,读的人手上没有一条能粘回去的路径,
        // 下一条 --member 只好再赌一次同样的名字。
        var tally = Tally.Of(shown.Count, decls.Count);
        // 截断态与 where / list / search 同文法(总数占锚点位)—— 这条原先自己拼句子,
        // 于是 ece5f54 换掉的是那三个 helper,漏了这里,同一个工具出现了两种截断文法。
        //
        // 截断态把路径挪到总数**后面**:只换语序而路径仍占句首的话,319 落到第三个语块,
        // 而那句话赢的机制正是「总数进主语位」—— 等于看着像修好了、锚点没拿到。
        // 完整态不动:只有一个数,没有锚点之争,而 `{rel}, 9 declarations.` 是常见形态。
        //
        // 这一格是**推出来的**,不是测出来的:盲测覆盖的是 where/list/search/keyed 那一支。
        // 曝光面也小得多 —— 这条命令不带 --limit 时全印,截断句只在用户自己传了数字上限时
        // 出现(全史 42 次 --outline 里 15 次带 limit,其中 7 次是 --limit all,不截断),
        // 而那三条是缺省 25、没要求就被截断。**两种风险不同级:后者的读者不知道自己被截了。**
        ctx.Report.Notice(tally.IsTruncated ? NoticeKind.Truncation : NoticeKind.Count,
            tally.IsTruncated
                ? $"{tally.RenderTotalFirst("declaration", qualifier: $" in {rel}")}; " +
                  "raise --limit to see the rest."
                : $"{rel}, {tally.Render("declaration")}.", count: tally);
        ctx.Report.Table("declarations", ["kind", "modifiers", "name", "in", "lines", "at"],
            shown.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["kind"] = d.Kind,
                // override 与 virtual 分不开的时候,一份轮廓能让人得出「这个类覆写了
                // 基类的 A、B、C」,而其中某个其实是它自己新引入的。
                ["modifiers"] = d.Modifiers is { Length: > 0 } ? d.Modifiers : null,
                // 带元数的显示名。裸名留给匹配 —— 反编译树的文件名不带元数,调用方
                // 无从知道该写几个类型参数,--type ThingOwner 要能同时命中 ThingOwner<T>。
                ["name"] = d.Display,
                ["in"] = d.Owner is { Length: > 0 } ? d.Owner + d.OwnerTypeParams : null,
                ["lines"] = d.Lines,
                ["at"] = $"{d.StartLine}-{d.EndLine}",
            }).ToList());
        SayBraceMatched(ctx);
        return 0;
    }

    /// <summary>按名字读一段声明。同名的全给,每段自带来源行。</summary>
    private static int Declaration(CommandContext ctx, string rel, string[] text,
                                   string? member, string? type, int cap)
    {
        var decls = DeclarationsIn(text);

        var picked = member is { Length: > 0 }
            ? decls.Where(d => Same(d.Name, member) &&
                               (type is not { Length: > 0 } || Same(d.Owner ?? "", type))).ToList()
            : decls.Where(d => CsOutlineIsType(d.Kind) && Same(d.Name, type!)).ToList();

        if (picked.Count == 0) { SayNoDeclaration(ctx, rel, text, decls, member, type); return 1; }

        var lines = new List<string>();
        // 结构化侧不重复文本侧的排版件(分隔符、标题行):每一行自带它属于哪个声明,
        // 免得消费方从 "rel:12-40  method Foo.Bar" 里反解一遍。
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var printed = 0;
        var clipped = 0;
        (int From, int To)? resume = null;
        foreach (var d in picked)
        {
            if (lines.Count > 0) lines.Add("--");
            lines.Add($"{rel}:{d.StartLine}-{d.EndLine}  {d.Kind} {d.Qualified}");
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
                    ["kind"] = d.Kind,
                    ["declaration"] = d.Qualified,
                    ["text"] = text[i - 1].TrimEnd(),
                });
                printed++;
            }
        }

        ctx.Report.Notice(clipped > 0 ? NoticeKind.Truncation : NoticeKind.Count,
            $"{Tally.Complete(picked.Count).Render("declaration")} in {rel}" +
            (clipped > 0
                ? $"; --limit stopped the printing at {Tally.Complete(cap).Render("line")}, " +
                  $"{clipped} short of the whole. " +
                  $"Raise it, or read on with --lines {resume!.Value.From}-{resume.Value.To}."
                : $", {Tally.Complete(printed).Render("line")}."));

        // 同名多份时说破这是**同一个文件里的**几份,而不是几个文件 —— 重载与嵌套类里的同名
        // 成员长得一样,不点名归属就分不出手里这段属于谁。
        //
        // 判据是「--type 还能不能收敛」,不是「--type 在不在场」:vanilla 里
        // ThingOwner<T> 与 ThingOwner 同住一个文件、Count 的归属逐字相同,--type 写什么
        // 都同时命中两条 —— 收敛不了就得换一条走得通的下一步,而不是把警告收掉。
        if (picked.Count > 1)
        {
            var ownersDiffer = picked.Select(d => d.Owner ?? "")
                                     .Distinct(StringComparer.Ordinal).Count() > 1;
            var typeCanHelp = ownersDiffer && type is not { Length: > 0 };
            // --type 那条路上 member 是 null。
            var what = member is { Length: > 0 } ? member : type;
            ctx.Report.Notice(NoticeKind.Filter,
                $"'{what}' is declared more than once here: " +
                string.Join(", ", picked.Select(d => $"{d.Qualified} (line {d.StartLine})")) + ". " +
                (typeCanHelp
                    ? "'--type <name>' narrows it to one."
                    : "They differ by more than the name, so --type cannot pick between them — " +
                      "read one alone with --lines " +
                      string.Join(" or --lines ", picked.Select(d => $"{d.StartLine}-{d.EndLine}")) + "."));
        }

        ctx.Report.Text("source", lines, rows);
        SayBraceMatched(ctx);
        return 0;
    }

    /// <summary>裸行。翻页靠它,所以总行数与下一页的参数恒在。</summary>
    private static int Raw(CommandContext ctx, string rel, string[] text, string? range, int cap)
    {
        var (from, to) = ParseRange(range, text.Length);

        if (from > text.Length)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{rel} has {Tally.Complete(text.Length).Render("line")}, so --lines {range} starts past " +
                "the end of it.");
            return 1;
        }

        to = Math.Min(to, text.Length);
        var clipped = 0;
        if (to - from + 1 > cap) { clipped = to - (from + cap - 1); to = from + cap - 1; }

        var lines = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
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

        // 「还剩一页」与「还剩几十页」在这条截断行里逐字同形,而两者的正确出路完全不同:
        // 前者翻一下就完了,后者盲翻是荒谬路径。而这条行给的唯一出路一直是 --lines ——
        // R10 的实证:657 次 read 只对 33 次 --outline,53 个会话里只有 2 个在第一次
        // read 时用它。页数摆出来,再点名另一条路,这条行才不再是唯一的出路。
        //
        // 只在还剩三页以上时说:翻一两页是正常分页,不值得换路子。
        var pageSize = to - from + 1;
        var morePages = pageSize > 0 ? (text.Length - to + pageSize - 1) / pageSize : 0;

        ctx.Report.Notice(complete ? NoticeKind.Count : NoticeKind.Truncation,
            complete
                ? $"{rel}, all {Tally.Complete(text.Length).Render("line")}."
                : $"{rel}, lines {from}-{to} of {text.Length}." +
                  (clipped > 0
                      ? $" --limit stopped it {Tally.Complete(clipped).Render("line")} short of what --lines asked for."
                      : "") +
                  (to < text.Length ? $" Pass --lines {to + 1}+{pageSize} for the next page." : "") +
                  // 不写「一屏看完」:Verse/Pawn.cs 的 outline 是 329 条声明、334 行,
                  // 压的是 14 倍不是压成一屏。说得出口的是它的 at 列能直接回传 --lines。
                  (morePages >= 3
                      ? $" Reaching the end that way takes {Tally.Complete(morePages).Render("page")} at this size; " +
                        "--outline instead lists the file's declarations with each one's line range, to pass back to --lines."
                      : ""));

        // 裸行读没有任何推断,不挂那条能力边界 —— 挂上去就成了每次返回的常驻免责声明。
        ctx.Report.Text("source", lines, rows);
        return 0;
    }

    // ---- 说清楚 ----

    /// <summary>
    /// 配平括号不是解析,这句话必须跟着每一次成员级返回走 —— 它限定的是这次返回的
    /// **完整性**(尤其 --outline 那句「文件里的声明都在这儿」),不是一条通用教学。
    /// 只在真用了轮廓的两条路上说;裸行读没有任何推断,不需要它。
    ///
    /// 压到一行:路径刚在上面印过,不再复述;「去 code-search」这条下一步写在 SKILL.md 里。
    /// 留下的是推不出来的那半句 —— 找不到不等于没有。
    /// </summary>
    private static void SayBraceMatched(CommandContext ctx)
        => ctx.Report.Notice(NoticeKind.Boundary,
            "Found by matching braces, not by parsing C#: a name not listed here may still be in the file.",
            footnote: true);

    private static void SayNoDeclaration(CommandContext ctx, string rel, string[] text,
                                         IReadOnlyList<CsDecl> decls, string? member, string? type)
    {
        var name = member is { Length: > 0 } ? member : type!;

        // 「有这个成员,但不在你说的那个类型里」与「整个文件都没有」要分开说:合成一句
        // 「not found in Pawn.cs」会被读成 Pawn 没覆写它,而它可能在同文件另一个嵌套类型里。
        if (member is { Length: > 0 } && type is { Length: > 0 })
        {
            var owners = decls.Where(d => Same(d.Name, member)).ToList();
            if (owners.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{member}' is in {rel} after all, just not in a type called '{type}'. It is declared in " +
                    string.Join(", ", owners.Select(d => $"{d.Owner ?? "the file itself"} (line {d.StartLine})")) +
                    ". Drop --type, or name one of those.");
                return;
            }
        }

        // 名字对不上时给近似候选,候选池是这个文件自己的轮廓 —— 拼错成员名是最常见的落空成因。
        var pool = decls.Where(d => member is { Length: > 0 } ? !CsOutlineIsType(d.Kind) : CsOutlineIsType(d.Kind))
                        .Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList();
        var close = Suggestion.Closest(pool, name);

        ctx.Report.Notice(NoticeKind.NextStep,
            $"No {(member is { Length: > 0 } ? "member" : "type")} named '{name}' was found in {rel} " +
            $"({Tally.Complete(text.Length).Render("line")}, " +
            $"{Tally.Complete(decls.Count).Render("declaration")})." +
            Suggestion.Say(close) +
            // 出路不许比它自己的自述更自信。此前这里写 "lists every declaration",而
            // --outline 自己的末尾写的是「a name not listed here may still be in the file」——
            // 同一个能力,在自述处诚实、在被推荐处被夸大,而读者是**先**读到推荐的那句、
            // 带着一个更强的预期去看那份清单的。实证:CostListCalculator.cs 里
            // `operator ==` 两边都列不出来,而 "every" 让那份清单成了「文件里没有」的证据。
            //
            // 顺序也调了:--outline 与 --member 共用花括号匹配,对这次落空**没有诊断力**——
            // 拿同一把尺子去校验它自己量出来的结果。真出路是 code-search,排它在前。
            " 'rimsearcher code-search' searches the text itself and does not go through brace matching, " +
            "which is what just came up empty — 'rimsearcher read " + rel + " --outline' lists what that " +
            "same matching does find, so a name missing there is missing for the same reason.");

        // 「这个文件里没有」会被读成「这个类型没有这个成员」。反编译产物**不重复父类的成员**:
        // `read MapPortal.cs --member Destroy` 落空,而 Destroy 在再上一层的 Thing 里。
        // 基类型就写在类声明那一行,算得出来就算,给一条走得到的下一步命令。
        if (member is { Length: > 0 })
        {
            var bases = decls.Where(d => CsOutlineIsType(d.Kind))
                             .Where(d => type is not { Length: > 0 } || Same(d.Name, type))
                             .Select(d => (Type: d.Name, Base: BaseClassOf(text, d)))
                             .Where(b => b.Base is not null)
                             .DistinctBy(b => b.Type, StringComparer.Ordinal)
                             .ToList();
            if (bases.Count > 0)
                ctx.Report.Notice(NoticeKind.NextStep,
                    NameList.Render([.. bases.Select(b => $"{b.Type} extends {b.Base}")], Limits.MaxSuggestions) +
                    ". Inherited members are not repeated by the decompiler, so one declared further up the " +
                    $"chain is not in this file at all: 'rimsearcher read {bases[0].Base}.cs --member {member}' " +
                    "looks one level up, and these trees hold one file per type.");
        }
    }

    /// <summary>
    /// 一个类型声明的**基类**,没有就回 null。
    ///
    /// 只取基类,不取接口 —— C# 的基类型表里基类必在首位,而接口带不来成员实现。
    /// 首位那个若按 .NET 约定长得像接口(<c>I</c> 接大写字母)就当没有基类:反编译产物
    /// 一律守这条约定,而 Ideo、IntVec3 这些真类型的第二个字母是小写,分得开。
    ///
    /// 与 <see cref="CsOutline"/> 同一个赌注:对象是 ILSpy 生成的 C#,格式规整,
    /// 不接语法分析。取**声明头到第一个 '{' 为止**的文本,冒号后、<c>where</c> 前的那一段,
    /// 按顶层逗号切开(尖括号里的逗号不算)。判不出来就回 null,一个字不说。
    /// </summary>
    private static string? BaseClassOf(string[] text, CsDecl type)
    {
        // StartLine 被 Backfill 往上收编过注释与特性行,所以从它起往下找带关键字的那一行。
        var header = "";
        for (var i = type.StartLine - 1; i < Math.Min(text.Length, type.StartLine + 8); i++)
        {
            header += " " + text[i];
            if (text[i].Contains('{')) break;
        }

        var at = header.IndexOf($"{type.Kind} {type.Name}", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = header[(at + type.Kind.Length + 1 + type.Name.Length)..];

        var colon = -1;
        var angle = 0;
        for (var i = 0; i < rest.Length && colon < 0; i++)
            switch (rest[i])
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '{': return null;
                case ':' when angle == 0: colon = i; break;
            }
        if (colon < 0) return null;

        rest = rest[(colon + 1)..];
        var stop = rest.IndexOf('{');
        if (stop >= 0) rest = rest[..stop];
        var where = rest.IndexOf(" where ", StringComparison.Ordinal);
        if (where >= 0) rest = rest[..where];

        // 首位那一个就是基类,后面的全是接口。
        angle = 0;
        var end = rest.Length;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == '<') angle++;
            else if (rest[i] == '>') { if (angle > 0) angle--; }
            else if (rest[i] == ',' && angle == 0) { end = i; break; }
        }

        var one = rest[..end].Trim();
        // 反编译树的文件名是末段裸名字,不带泛型实参与命名空间限定。
        var cut = one.IndexOf('<');
        if (cut > 0) one = one[..cut];
        var dot = one.LastIndexOf('.');
        if (dot >= 0) one = one[(dot + 1)..];

        if (one.Length == 0 || !one.All(c => char.IsLetterOrDigit(c) || c == '_')) return null;
        if (one.Length > 1 && one[0] == 'I' && char.IsUpper(one[1])) return null;   // 接口,没有基类
        return one;
    }

    private static void SayNoFile(CommandContext ctx, string root, string wanted, string? sourceName)
    {
        // 拼错文件名与「这棵树里没有」下一步不同。近似候选取全体文件名,拼错是常见成因。
        var names = AllFiles(root, sourceName).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
                                              .Select(n => n!).ToList();
        var close = Suggestion.Closest(names, Path.GetFileName(wanted) ?? wanted);

        ctx.Report.Notice(NoticeKind.NextStep,
            $"No file named '{wanted}' is under the decompiled root" +
            (sourceName is { Length: > 0 } ? $" in tree '{sourceName}'" : "") + "." +
            Suggestion.Say(close) +
            (sourceName is { Length: > 0 }
                ? " Drop --source to look in every tree."
                : " 'rimsearcher code-search' finds which file a symbol lives in."));
    }

    private static void SayAmbiguous(CommandContext ctx, string wanted, IReadOnlyList<string> hits)
    {
        // 重名不替调用方选:mod 的覆盖版被当成 vanilla 原版读下去,输出里逐字看不出区别,
        // 而选错的代价是整条结论作废。
        ctx.Report.Notice(NoticeKind.NextStep,
            $"'{wanted}' matches {Tally.Complete(hits.Count).Render("file")}, and reading the wrong one gives " +
            "an answer that looks right: " + NameList.Render(hits, Limits.AmbiguousFiles) +
            ". Name one of those paths, or narrow with --source.");
    }

    // ---- 零件 ----

    /// <summary>
    /// 这个文件里有哪些声明。namespace 在这里没有用处,而**滤掉它的地方必须只有一处** ——
    /// 一处滤一处不滤,同一个文件的声明数会在两句话里各说一个。
    /// </summary>
    private static IReadOnlyList<CsDecl> DeclarationsIn(string[] text)
        => CsOutline.Scan(text).Where(d => d.Kind != "namespace").ToList();

    private static bool CsOutlineIsType(string kind)
        => kind is "class" or "struct" or "interface" or "record" or "enum";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// 行号右对齐,正文竖着对齐。空行只留行号 —— 「行号 + 两个空格 + 空」会留下行尾空格,
    /// 而这套输出禁止行尾空格。
    /// </summary>
    private static string Numbered(int n, string text) => $"{n,6}  {text.TrimEnd()}".TrimEnd();

    private static int Cap(CommandContext ctx)
    {
        var raw = ctx.Args.Value("limit");
        if (string.IsNullOrEmpty(raw)) return Limits.ReadMaxLines;
        if (raw is "all" or "none" or "0" or "-1") return Limits.ReadMaxLines;
        if (int.TryParse(raw, out var n) && n > 0) return Math.Min(n, Limits.ReadMaxLines);
        throw new CliUsageException($"--limit takes a positive number or 'all'; got '{raw}'.");
    }

    /// <summary>`a-b` / `a+n` / `a` / `all` / 不给。行号 1 起,两端都含。</summary>
    internal static (int From, int To) ParseRange(string? spec, int total)
    {
        if (string.IsNullOrEmpty(spec)) return (1, Math.Min(total, Limits.ReadWindow));
        if (spec is "all") return (1, Math.Max(total, 1));

        int At(string s, string what)
            => int.TryParse(s.Trim(), out var v) && v > 0
                ? v
                : throw new CliUsageException(
                    $"--lines wants line numbers from 1 up; '{s.Trim()}' is not one ({what}). " +
                    "Write it as '400-460', '400+60', '400', or 'all'.");

        var dash = spec.IndexOf('-');
        if (dash > 0)
        {
            var from = At(spec[..dash], "the start");
            var to = At(spec[(dash + 1)..], "the end");
            if (to < from)
                throw new CliUsageException($"--lines {spec} ends before it starts; write the smaller line first.");
            return (from, to);
        }

        var plus = spec.IndexOf('+');
        if (plus > 0)
        {
            var from = At(spec[..plus], "the start");
            var count = At(spec[(plus + 1)..], "the count");
            return (from, from + count - 1);
        }

        var only = At(spec, "the start");
        return (only, only + Limits.ReadWindow - 1);
    }

    /// <summary>
    /// 文件名 → 相对根目录的路径。三种写法都收:整条相对路径、它的任意一段尾巴、光一个文件名。
    ///
    /// 收尾巴会同时打中好几棵树里的同名文件,代价由 <see cref="SayAmbiguous"/> 付。
    /// 但不收不行:code-search 印的是相对路径,复制过来必然带着树名。
    /// </summary>
    private static IReadOnlyList<string> Resolve(string root, string wanted, string? sourceName)
    {
        var norm = wanted.Replace('\\', '/').Trim('/');

        // 整条相对路径先试一次:最确定,而且免掉一次全树枚举。
        var direct = Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return [norm];

        var withCs = norm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? null : norm + ".cs";
        var all = AllFiles(root, sourceName)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        bool Tail(string rel, string want)
            => string.Equals(rel, want, StringComparison.OrdinalIgnoreCase) ||
               rel.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase);

        var hits = all.Where(rel => Tail(rel, norm) || (withCs is not null && Tail(rel, withCs)))
                      .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                      .ToList();
        return hits;
    }

    private static IEnumerable<string> AllFiles(string root, string? sourceName)
    {
        if (sourceName is { Length: > 0 })
            return Directory.EnumerateFiles(Path.Combine(root, sourceName), "*", SearchOption.AllDirectories);

        // 什么算一棵树问 SourcesShared —— 直接枚举根目录会把 .git 之类也当成一棵树。
        return SourcesShared.TreeNames(root)
                            .SelectMany(t => Directory.EnumerateFiles(Path.Combine(root, t), "*",
                                                                      SearchOption.AllDirectories))
                            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly));
    }
}
