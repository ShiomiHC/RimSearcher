using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;

namespace RimSearcher.Commands;

/// <summary>
/// 从反编译树里读一段源码 —— 一个成员、一个类型,或者一段裸行。
///
/// 符号级问题(谁调用它、派生了哪些)归 <c>callers</c> / <c>types</c>,它们读元数据。
/// 这里答的是反编译产物**落盘的那一份**逐字是什么。
///
/// 成员定位靠配平大括号,不是语法分析(<see cref="CsOutline"/> 里写着它做不到什么),
/// 于是这条能力边界必须跟着输出走:找不到一个名字,只说明文本扫描没看见它。
/// </summary>
public sealed class ReadCommand : Command
{
    /// <summary>
    /// 回声句的连接词。与 GrammarTests 那条反向断言共用同一个字面量 —— 「产地里找得到」
    /// 这条保证对它不成立:别处另有两句无关的话也含 `read as`,闸会在那里找到锚照绿。
    /// </summary>
    public const string ReadAsPhrase = " read as ";

    public override CommandSpec Spec => new()
    {
        Name = "read",
        Aliases = ["read-code", "cat", "show-source"],
        Summary = "Read source out of the decompiled tree — one member, one type, or a line range.",
        Remarks =
            "The file is named by its path relative to the decompiled root ('vanilla/Assembly-CSharp/Verse/" +
            "Pawn.cs'), by any tail of that path, by its bare name, or by a namespace-qualified type name " +
            "('RimWorld.Bullet') — the decompiler lays files out by namespace, so that name is the path " +
            "'RimWorld/Bullet.cs' written another way, and the namespace has to match. A path that is not " +
            "there falls back to the bare name and says so; when a bare name matches several files, the " +
            "answer lists them instead of picking one.\n\n" +
            "--member and --type find the declaration by matching braces, not by parsing C#. That is enough " +
            "for decompiled output, which is machine-formatted; 'code-search' searches the text and --lines " +
            "reads it raw.\n\n" +
            "For who calls a method, 'callers'; for what a type derives from, what derives from it, and " +
            "which of those override a member, 'types'. Both read the assembly's metadata. This command " +
            "answers a different question: what the decompiled file on disk actually says.\n\n" +
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
                Variadic = true,
                Help = "A path under the decompiled root, a tail of one, a bare file name such as 'Pawn.cs', " +
                       "or a namespace-qualified type name such as 'RimWorld.Bullet'. Several files read in " +
                       "one call, in the order given; every row carries the file it came from, and --limit " +
                       "counts lines inside each file rather than across the batch. A file that cannot be " +
                       "resolved is reported and the others still print.",
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
                Placeholder = "<a-b|a+n|a>",
                // 「不给就是整份文件」紧挨着写法列举,不放在 --limit 那句后面 ——
                // 放后面时它的 `it` 会被读成 --limit(撤掉 `--lines all` 后这句是要整份
                // 文件的人唯一的落点,读错方向就没了)。两处代词都点名到选项。
                Help = "Read raw lines instead: '400-460' is inclusive (',' and ':' work in place of the " +
                       "'-'), '400+60' is sixty lines from 400, '400' starts there and runs to the end of " +
                       "the file. Without --lines the whole file is read. Whatever --lines asks for is " +
                       "printed in full unless --limit says otherwise — that is also what shortens a " +
                       "start-only '400'.",
            },
            // 同一个区间的两个数各占一个选项。这是**别处工具的通行写法**,而不是这条
            // 命令的第二种口味:真实调用里 --start 与 --end 各 64 次 / 49 份会话、完全
            // 成对,重写十有八九落到 --lines a-b。挡在外面的话每次都要赔一轮往返,而
            // `--start 71 --end 130` 两个各带值的选项,靠别名表达不出来。
            // 与 --lines 同时给是用法错误,不排优先级 —— 见 Run。
            //
            // 翻页那句提示照旧只印 --lines 的写法(`Pass --lines 7+4 for the next page`),
            // 不跟着读者的拼法走:下一页是「同样大小的下一段」,而这一对写不出个数。
            // 印出去的记法粘得回来,只是换了个拼法 —— 这是一处判断,不是漏改。
            new OptionSpec
            {
                Name = "start",
                Placeholder = "<n>",
                Help = "Read from this line. With --end it is a range; on its own it runs to the end of " +
                       "the file, which --limit then shortens. Same read as --lines, spelled as two options.",
            },
            new OptionSpec
            {
                Name = "end",
                Placeholder = "<n>",
                Help = "Read up to and including this line. On its own it starts at line 1.",
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
                Placeholder = "<n>",
                Help = "How many lines to print at most, and on a raw read where the read stops. " +
                       "Left out, nothing is capped: the read prints whatever --lines, --outline or " +
                       "--member asked for, and the whole file if none of them was given. On a " +
                       "decompiled type that runs to thousands of lines.",
                Default = "every line",
            },
        ],
        Examples =
        [
            "rimsearcher read Pawn.cs --outline",
            "rimsearcher read CompShield.cs --member CompTick",
            "rimsearcher read RimWorld.CompShield",
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
                What = "with --outline: one row per declaration — file, kind, modifiers (the leading run of " +
                       "them, verbatim; null when there are none), name, in (the owner), lines, at " +
                       "(the 'start-end' range to hand back to --lines). file is in every row, matching " +
                       "'source'.",
            },
            EmptyCause.JsonKeyCounting("declaration"),
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var root = ctx.Config.DecompiledDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new CliUsageException(
                SourcesShared.NotConfiguredToRead("read"));

        var asked = ctx.Args.Positionals;
        var member = ctx.Args.Value("member");
        var type = ctx.Args.Value("type");
        var range = ctx.Args.Value("lines");
        var outline = ctx.Args.Flag("outline");

        // --start / --end 折进 --lines 的写法之后,下面一整条路只认 range 一个变量。
        // 校验在折叠**之前**做,报错才引得出读者敲的那个选项名和那个数:折完再报,
        // `--start 6 --end 3` 会被说成「--lines 6-3」,而那一串他没写过。
        var startAt = ctx.Args.Value("start");
        var endAt = ctx.Args.Value("end");
        // 折叠之后 range 说不出自己是哪种写法拼来的,而下面与 --member/--type 那条互斥
        // 报错要指名 —— 不带着这个标签,写 `--start 3 --member X` 的人会读到一句在说
        // `--lines`,那个选项他没敲过。
        var rangeSpelling = "--lines";
        if (startAt is { Length: > 0 } || endAt is { Length: > 0 })
        {
            rangeSpelling = startAt is { Length: > 0 } && endAt is { Length: > 0 }
                ? "--start/--end"
                : startAt is { Length: > 0 } ? "--start" : "--end";
            if (range is { Length: > 0 })
                throw new CliUsageException(
                    "--lines and --start/--end are two spellings of the same range; pass one or the " +
                    "other. --lines also has the one form this pair cannot write: '400+60' for a count.");

            static int Line(string? v, string name)
                => int.TryParse(v?.Trim(), out var n) && n > 0
                    ? n
                    : throw new CliUsageException(
                        $"{name} wants a line number from 1 up; '{v?.Trim()}' is not one.");

            if (startAt is { Length: > 0 } && endAt is { Length: > 0 })
            {
                var from = Line(startAt, "--start");
                var to = Line(endAt, "--end");
                if (to < from)
                    throw new CliUsageException(
                        $"--start {from} is past --end {to}; write the smaller line first.");
                range = $"{from}-{to}";
            }
            // 各自单飞的两种写法都不猜:只给起点就读到文件尾(与 --lines '400' 同一件事),
            // 只给终点就从第一行起。实测里这两种一次都没出现过,所以这里选的是**不发明**
            // 语义的那个读法,而不是照某种用量定的。
            else if (startAt is { Length: > 0 })
                range = $"{Line(startAt, "--start")}";
            else
                range = $"1-{Line(endAt, "--end")}";
        }

        // 「读哪一段」的三种说法互斥。不排优先级 —— 静默择一交出的是完全另一块代码,
        // 而这里当场就能说清。
        if (range is { Length: > 0 } && (member is { Length: > 0 } || type is { Length: > 0 }))
            throw new CliUsageException(
                $"{rangeSpelling} reads raw lines and --member/--type find a declaration; they are two " +
                "different reads, so pass one or the other. '--outline' lists the declarations with their " +
                "line ranges if you want to pick a range from them.");

        // 两张表互斥,「读哪一种」在开查之前就定了。不能交给声明层统一发
        // (见 JsonKeySpec.Rows):两个都发就等于说「另一路也查过了,没有」。
        ctx.Report.Promises(outline ? "declarations" : "source");

        var sourceName = ctx.Args.Value("source");
        if (sourceName is { Length: > 0 } && !Directory.Exists(Path.Combine(root, sourceName)))
            throw new CliUsageException(CodeSearchCommand.NoSuchTree(sourceName, SourcesShared.TreeNames(root)));

        // --limit 按**每个文件**计:跨文件计的话,第一个文件把额度吃光,后面几个印零行,
        // 而那与「那几个文件是空的」印出来同形。
        var lines = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var read = 0;
        var failed = 0;
        var braceMatched = false;

        foreach (var wanted in asked)
        {
            var status = ReadOne(ctx, root, wanted, sourceName, member, type, range, outline,
                                 asked.Count > 1, lines, rows, ref braceMatched);
            if (status == 0) read++; else failed++;
        }

        if (braceMatched) SayBraceMatched(ctx);
        if (read == 0) return 1;

        if (outline) ctx.Report.Table("declarations", OutlineColumns, rows);
        else ctx.Report.Text("source", lines, rows);
        return 0;
    }

    /// <summary>
    /// 一个名字:解析成一条路径,按选定的读法把行追加进共用的两个容器。
    /// 返回 0 表示读到了东西;非 0 时话已经说完(哪一句取决于是哪一种落空)。
    /// </summary>
    private static int ReadOne(CommandContext ctx, string root, string wanted, string? sourceName,
                               string? member, string? type, string? range, bool outline, bool batch,
                               List<string> lines, List<IReadOnlyDictionary<string, object?>> rows,
                               ref bool braceMatched)
    {
        var hits = Resolve(root, wanted, sourceName);

        // 路径的中间段写错、文件名对,是最常见的一种落空 —— 而这条命令**已经能**按裸文件名
        // 定位。不重试的话,答案是一句「没有这个文件」外加一个不能直接粘贴的裸名候选,
        // 调用方得再跑一次 code-search 才拿得到路径:一个文件三次往返,而路径就在手上。
        var slashNorm = wanted.Replace('\\', '/').TrimEnd('/');
        // 命名空间限定名(RimWorld.Bullet)是 get/where 自己印出的形态,而它**就是**一条
        // 路径:反编译开着 UseNestedDirectoriesForNamespaces(见 Decompiler.CreateSettings),
        // 目录由命名空间生成 —— IL 里没有源文件路径可用,反编译器只有命名空间可依。
        // 点号换斜杠再解一次,于是命名空间参与匹配:写错的命名空间在这里落空,而不是
        // 被剥掉后撞上某个同名的顶层类型(嵌套类型名尤其危险,它根本不单独成文件)。
        var looksLikeTypeName = !slashNorm.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                             && ClassNameShape.Looks(slashNorm)
                             && ClassNameShape.Tail(slashNorm) != slashNorm;
        if (hits.Count == 0 && looksLikeTypeName)
            hits = Resolve(root, slashNorm.Replace('.', '/') + ".cs", sourceName);

        // 命名空间没对上时才降到裸名 —— 那一步会自报,而那时确实有话要说:
        // 你给的命名空间下没有这个类型,但另有一处有个同名文件。
        var bare = looksLikeTypeName ? ClassNameShape.Tail(slashNorm) + ".cs" : Path.GetFileName(slashNorm);
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
        //
        // 类型全名走到这里,意味着命名空间没对上(对上就在 hits 里了),此时这句话
        // 说的正是要紧的那件事,不是客套。
        if (hits.Count == 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{wanted}' is not a path under the decompiled root, but exactly one file is named " +
                $"'{bare}', and that is the one read here.");
        string[] text;
        try { text = File.ReadAllLines(Path.Combine(root, rel)); }
        catch (Exception ex) { throw new CliUsageException($"'{rel}' could not be read: {ex.Message}"); }

        var cap = Cap(ctx);

        // 什么都没说 = 整个文件。2026-09-05 撤掉了这里的 150 行窗口:346 条裸 read 里
        // 236 条自己写着 `--limit all`,窗口本来就被routinely 掀开;剩下 110 条里
        // 全量后只有 6 条超过 harness 的 30000 字符,其中 5 条接了管道。
        var window = cap;

        // 那条能力边界(靠配平括号找声明)是**脚注**,几个文件一起读时按文件重复几遍
        // 只会被当成噪声,所以攒起来最后发一次。
        //
        // 只在真读到东西时挂:落空那条路自己那句话里已经说了同一件事(「匹配靠括号,
        // 所以这不是文件里没有它的证据」),再挂一遍是同一句话说两遍。
        if (outline)
        {
            var status = Outline(ctx, rel, text, cap, rows);
            if (status == 0) braceMatched = true;
            return status;
        }

        if (member is { Length: > 0 } || type is { Length: > 0 })
        {
            var status = Declaration(ctx, rel, text, member, type, cap, lines, rows);
            if (status == 0) braceMatched = true;
            return status;
        }

        return Raw(ctx, rel, text, range, cap, window, lines, rows, batch);
    }

    // ---- 三种读法 ----

    /// <summary>轮廓。读什么之前先知道有什么 —— 对上下文预算来说这是最便宜的一步。</summary>
    /// <summary>
    /// 轮廓表**文本面**的列。<c>file</c> 排在末尾而不是首位:第 0 列不参与常量列折叠
    /// (见 Renderers.Fold),放在首位的话读一个文件时每行都拖着一整条相对路径,而它恒定。
    /// JSON 面的键序不受这里影响,那边 file 仍是第一个 —— 消费方按名字取键。
    /// </summary>
    private static readonly string[] OutlineColumns =
        ["kind", "modifiers", "name", "in", "lines", "at", "file"];

    private static int Outline(CommandContext ctx, string rel, string[] text, int cap,
                               List<IReadOnlyDictionary<string, object?>> rows)
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
        rows.AddRange(
            shown.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["file"] = rel,
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

        return 0;
    }

    /// <summary>按名字读一段声明。同名的全给,每段自带来源行。</summary>
    private static int Declaration(CommandContext ctx, string rel, string[] text,
                                   string? member, string? type, int cap,
                                   List<string> lines, List<IReadOnlyDictionary<string, object?>> rows)
    {
        var decls = DeclarationsIn(text);

        var picked = member is { Length: > 0 }
            ? decls.Where(d => Same(d.Name, member) &&
                               (type is not { Length: > 0 } || Same(d.Owner ?? "", type))).ToList()
            : decls.Where(d => CsOutlineIsType(d.Kind) && Same(d.Name, type!)).ToList();

        if (picked.Count == 0) { SayNoDeclaration(ctx, rel, text, decls, member, type); return 1; }

        // 结构化侧不重复文本侧的排版件(分隔符、标题行):每一行自带它属于哪个声明,
        // 免得消费方从 "rel:12-40  method Foo.Bar" 里反解一遍。
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
                    : "--type cannot pick between them; read one alone with --lines " +
                      string.Join(" or --lines ", picked.Select(d => $"{d.StartLine}-{d.EndLine}")) + "."));
        }



        return 0;
    }

    /// <summary>裸行。翻页靠它,所以总行数与下一页的参数恒在。</summary>
    private static int Raw(CommandContext ctx, string rel, string[] text, string? range, int cap, int window,
                           List<string> lines, List<IReadOnlyDictionary<string, object?>> rows, bool batch)
    {
        var (from, to) = ParseRange(range, text.Length, window, out var rewritten);

        // 归一化改过写法就先说改成了什么。放在句首而不是句尾:后面每个行号的意思都由它决定。
        var echo = rewritten is null ? "" : $"--lines {range!.Trim()}{ReadAsPhrase}{rewritten}. ";

        if (from > text.Length)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                echo + $"{rel} has {Tally.Complete(text.Length).Render("line")}, so --lines {range} starts past " +
                "the end of it.");
            return 1;
        }

        to = Math.Min(to, text.Length);
        var clipped = 0;
        if (to - from + 1 > cap) { clipped = to - (from + cap - 1); to = from + cap - 1; }

        // 给了不止一个文件时,每一段正文都带标题行 —— 包括第一段。行号从 1 重新开始,
        // 没有标题的话两个文件的正文在文本面粘成一片,而计数句在另一个区里,对不上是哪一段;
        // 只给第二段起加标题则更糟:第一段成了唯一没署名的那一段。
        // 与 --member 那一路同形(它用同样的 `--` 与 `路径:起-止` 标题行)。
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

        // 「还剩一页」与「还剩几十页」在这条截断行里逐字同形,而两者的正确出路完全不同:
        // 前者翻一下就完了,后者盲翻是荒谬路径。而这条行给的唯一出路一直是 --lines ——
        // R10 的实证:657 次 read 只对 33 次 --outline,53 个会话里只有 2 个在第一次
        // read 时用它。页数摆出来,再点名另一条路,这条行才不再是唯一的出路。
        //
        // 只在还剩三页以上时说:翻一两页是正常分页,不值得换路子。
        var pageSize = to - from + 1;
        var morePages = pageSize > 0 ? (text.Length - to + pageSize - 1) / pageSize : 0;

        ctx.Report.Notice(complete ? NoticeKind.Count : NoticeKind.Truncation,
            echo +
            (complete
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
                      : "")));

        // 裸行读没有任何推断,不挂那条能力边界 —— 挂上去就成了每次返回的常驻免责声明。

        return 0;
    }

    // ---- 说清楚 ----

    /// <summary>
    /// 配平括号不是解析,这句话必须跟着每一次成员级返回走 —— 它限定的是这次返回的
    /// **完整性**(尤其 --outline 那句「文件里的声明都在这儿」),不是一条通用教学。
    /// 只在真用了轮廓的两条路上说;裸行读没有任何推断,不需要它。
    ///
    /// 压到一行:路径刚在上面印过,不再复述;「去 code-search」这条下一步写在 SKILL.md 里。
    ///
    /// **「找不到不等于没有」这半句单说是无效的,得说出漏的是哪一类。** 盲测三臂各 10 次,
    /// 抽象地点名同形 0/10,点名一个读者能去核对的具体类别 5/10。
    /// 举本地函数是核过的:AttackTargetFinder.cs 第 297 行的 <c>BestTargetOnCell</c> 住在方法体里,
    /// <c>--member</c> 落空 —— 扫描只在根、namespace、类型三处认声明,方法体内一律当语句。
    /// 这个例子已经换过三次(operators → namespace 下的 delegate → enum 成员 → 本地函数),
    /// 每次都是因为前一类修进来了;点名的类别必须是**此刻**真认不出的,否则整句是假话。
    /// event 不用提,它在(kind 标成 field,按名字找得到);显式接口实现也在,
    /// 只是名字被剥了前缀;委托两档都在,namespace 下的 Owner 为空;enum 成员 kind 是 enum-member。
    /// </summary>
    private static void SayBraceMatched(CommandContext ctx)
        => ctx.Report.Notice(NoticeKind.Boundary,
            "Found by matching braces, not by parsing C#: a declaration this scan does not recognise " +
            "and one that is not in the file look the same here. A local " +
            "function declared inside a method body is one kind it does not recognise.",
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
                // 谁声明了它照说(那是值域,下一步要填的名字就在里面);「拿掉 --type」是 empty_because 的一行。
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"'{member}' is in {rel}, declared in " +
                    string.Join(", ", owners.Select(d => $"{d.Owner ?? "the file itself"} (line {d.StartLine})")) +
                    $", not in a type called '{type}'.");
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven("type"), owners.Count, ctx.Without("type")), "declaration");
                return;
            }
        }

        // 名字对不上时给近似候选,候选池是这个文件自己的轮廓 —— 拼错成员名是最常见的落空成因。
        var pool = decls.Where(d => member is { Length: > 0 } ? !CsOutlineIsType(d.Kind) : CsOutlineIsType(d.Kind))
                        .Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList();
        var close = Suggestion.Closest(pool, name);

        // 元数据先问一次:那份 dll 知道这个名字长在谁身上,而这里只有花括号。
        //
        // 实测 59 次 read --member 落空里 37% 当场交卷,抽验 12 个被丢掉的符号有 10 个
        // 真的存在;另有 28% 跟着下面那条继承提示走 —— 而 `Need_Food.HungerMultiplier`
        // 那次顺着走到 Need.cs 仍是空,真答案 `HungerLevelUtility` 是个静态工具类,
        // 继承链上永远走不到。同一个问题元数据侧(`il`)早就答对了。
        var elsewhere = member is { Length: > 0 } ? WhoHasIt(ctx, member) : null;

        ctx.Report.Notice(NoticeKind.NextStep,
            $"No {(member is { Length: > 0 } ? "member" : "type")} named '{name}' was found in {rel} " +
            $"({Tally.Complete(text.Length).Render("line")}, " +
            $"{Tally.Complete(decls.Count).Render("declaration")})." +
            Suggestion.Say(close) +
            // 三件事换措辞时都不许丢:①「不是没有」这个否定无条件在场;②真出路是 code-search,
            // 排在前 —— --outline 与 --member 同一把花括号尺子,对这次落空没有诊断力;
            // ③ --outline 的能力不许写得比它自己的自述强(曾写 "every",而 RegionProcessor.cs
            // 整文件一份 `delegate` 声明,轮廓 0 条,于是那份清单被当成了「文件里没有」的证据)。
            // 为什么能压这么短:这三条的完整版常驻 SKILL.md,落空路径不必再讲一遍。
            //
            // **成因查明时整段撤掉**:它讲的是「这次落空可能是我没看见」,而下一句已经
            // 说出这个名字声明在哪个类型上了。两句并排时读者读不出 Need_Food.cs 里到底
            // 有没有,而那三条下一步全都指着与真答案相反的方向。这条免责留给「元数据里
            // 也没有」的那一支 —— 那时它才是这次落空唯一说得住的解释。
            // 口径与 fields 那侧同:查明了成因就不再列泛化的可能性。
            (elsewhere is null
                ? " The match runs on braces, not C# parsing; 'rimsearcher code-search' searches the text " +
                  "itself, and '--outline' lists what brace matching does find."
                : ""));

        if (elsewhere is not null)
        {
            ctx.Report.Notice(NoticeKind.NextStep, elsewhere);
            return;
        }

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
                    $": 'rimsearcher read {bases[0].Base}.cs --member {member}' looks one level up.");
        }
    }

    /// <summary>
    /// 元数据里谁声明了这个名字。答得出就说出来并回 <c>true</c>,答不出一个字不说。
    ///
    /// 纪律同 <see cref="DefTypeMiss.InSourceInstead"/>:当场算得出来才说。这里的
    /// 「算得出」是那份 dll —— 它没同步、没配、读不了,都退回原来那两句(<see
    /// cref="CodeShared.OpenQuietly"/> 一律回 null 而不是抛)。
    ///
    /// 只点名类型,不给行号:元数据可能来自比 .cs 新的一份 dll,而「这个名字长在谁身上」
    /// 这一级经得起那点差异,行号经不起。
    /// </summary>
    private static string? WhoHasIt(CommandContext ctx, string member)
    {
        using var lookup = CodeShared.OpenQuietly(ctx);
        if (lookup is null) return null;

        var hits = lookup.FindMembersAnywhere(member, 32);
        if (hits.Count == 0) return null;

        var types = hits.Select(h => h.Type.FullName).Distinct(StringComparer.Ordinal).ToList();
        var shown = types.Take(Limits.MaxSuggestions).ToList();

        // 种类点出来:问的人是拿 --member 来找的,而 read --member 只切得到花括号块 ——
        // 一个字段没有自己的块,这一句不说的话,下一步又会落回同一个空。
        var kinds = hits.Select(h => h.Kind).Distinct(StringComparer.Ordinal).ToList();

        return $"The assemblies do have '{member}': it is declared on " +
               NameList.Render(shown, Limits.MaxSuggestions) +
               (types.Count > shown.Count
                   ? $", plus {Tally.Complete(types.Count - shown.Count).Render("type")} more"
                   : "") +
               $" (as {NameList.Render(kinds, 3)}). " +
               $"'rimsearcher members {shown[0]} --name {member}' shows the declaration, and " +
               $"'rimsearcher read {shown[0].Split('.')[^1]}.cs --member {member}' reads it there.";
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
            $"'{wanted}' matches {Tally.Complete(hits.Count).Render("file")}: " +
            NameList.Render(hits, Limits.AmbiguousFiles) + ". Name one of those paths, or narrow with --source.");
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
        // 不给就是不限,read 这一侧现在一个缺省都不剩。返回 int.MaxValue,
        // 于是但凡有加法碰它就得防溢出,见 ParseRange 里那一处。
        if (string.IsNullOrEmpty(raw)) return int.MaxValue;
        if (int.TryParse(raw, out var n) && n > 0) return n;
        throw new CliUsageException(
            $"--limit takes a positive whole number (got '{raw}'). Leave it out to read the whole file.");
    }

    /// <summary>
    /// 区间写法归一:`–` `—` `−` `..` `:` `,` 都当分隔符,数字前的 `L` 丢掉。
    ///
    /// 这些形状在调用方那边是既成事实(Markdown 粘贴把 `-` 变 en dash,`L690-L725` 是
    /// GitHub 链接),而且没有第二种读法 —— 除了逗号,它可能是千分位。改写过就得回声,
    /// 因为 `1,234` 读成 1-234 之后,输出逐字看起来完全正常。
    /// </summary>
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

    /// <summary>
    /// <paramref name="rewritten"/>:归一化真的改动了写法时给出改动后的样子,否则 null。
    /// 调用方拿它印回声 —— 空格不算改动,`7 - 12` 与 `7-12` 是同一句话。
    ///
    /// <paramref name="window"/>:说不出终点的两种写法(什么都不给、只给一个起点)各取多少行。
    /// 调用方显式给过 --limit 时传的是那个上限,见 <see cref="Run"/>。
    /// </summary>
    internal static (int From, int To) ParseRange(string? spec, int total, int window, out string? rewritten)
    {
        rewritten = null;
        if (string.IsNullOrEmpty(spec)) return (1, Math.Min(total, window));

        var given = spec.Trim();
        var normalized = Normalize(given);
        if (!Same(normalized, given.Replace(" ", ""))) rewritten = normalized;
        spec = normalized;

        // what 只在写法分成两半时给。整个 spec 就是一个数时不补「(the start)」——
        // 那个括号存在的理由是指出坏的是哪一半,而此时没有另一半;敲 `--lines all`
        // 的人要的是整份文件,读到「start」只会去猜起点该填几。
        int At(string s, string? what)
            => int.TryParse(s.Trim(), out var v) && v > 0
                ? v
                : throw new CliUsageException(
                    $"--lines wants line numbers from 1 up; '{s.Trim()}' is not one" +
                    (what is null ? ". " : $" ({what}). ") +
                    "Write it as '400-460', '400+60', or '400'.");

        // 起点加个数,算在 long 上再收回来:window 可能是 int.MaxValue(没给 --limit),
        // 而 `--lines 1+2147483647` 的个数由调用方给。溢出会变成负的终点,那时
        // 「读到文件尾」与「起点在文件外」两条支路都走不到,报错也无从谈起。
        static int End(long from, long count) => (int)Math.Min(from + count - 1, int.MaxValue);

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
            return (from, End(from, count));
        }

        var only = At(spec, null);
        return (only, End(only, window));
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
