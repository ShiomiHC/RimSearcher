using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

public class ReadCodeTool : ITool
{
    // 裸行读取的缺省与硬上限。schema 里的 maximum 只是给 client 的提示，client 照样能传
    // lineCount:100000，所以夹紧必须在服务端做一次（见下面的 Math.Min）。
    private const int DefaultLineCount = 150;

    // public：inspect 的 limit 说明里要引它（「'all' 是看到被折叠成员的唯一途径，因为
    // read_code extractClass 到这个数就截」）。那句话此前手写着 2000，改这里漏那里时
    // 两个工具会对同一道闸报两个数——同一个数字在两处各写一遍，本仓反复清理的那类缺陷。
    public const int MaxLineCount = 2000;

    private readonly SourceIndexer _sourceIndexer;
    private readonly ScopeCatalog _scopeCatalog;
    private readonly ConditionalFolders _conditional;

    public ReadCodeTool(
        SourceIndexer sourceIndexer, ScopeCatalog scopeCatalog, ConditionalFolders? conditional = null)
    {
        _sourceIndexer = sourceIndexer;
        _scopeCatalog = scopeCatalog;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__read_code";

    public IEnumerable<string> ExtraAcceptedKeys => ["query", "file", "filePath", "fileName", "method", "member", "memberName", "class", "type", "typeName", "start", "offset", "lines", "count", "maxResults", "scopes", "source", "sources", "mod", "mods", "in"];

    // 三种模式的优先级此前只写在下面几个 if 的先后顺序里，调用方同时传 extractClass 和
    // methodName 时无从知道哪个生效，只能从返回内容倒推。契约就得写在 description 里。
    public string Description =>
        "Read C# or XML source out of one specific file — an indexed file name or an absolute path, not a search term. "
        + "Three exclusive modes: extractClass (the whole type), methodName (one member), or startLine/lineCount "
        + "(raw lines). If more than one is passed, extractClass wins over methodName, which wins over the line range. "
        + "The first two parse C#; on an XML file only the line range applies. extractClass output is capped at "
        + $"{MaxLineCount} lines — the same cap the line range has — and says so when it truncates. "
        // 第九轮盲测的两条链都撞上同一件没人说过的事：Cinders 的 `1.6/CE/Patches/Weapons_Mech.xml`
        // 与一份无条件补丁**完全同形**（裸 <Patch>、无 mod 守卫、正文照改 defaultProjectile），
        // 守卫在 loadFolders.xml 那一层，而那一层不在任何返回里。最省事的读法「补丁存在 → 一定生效」
        // 于是无从证伪：一条靠领域常识补上这一层、代价两轮调用加置信度下调，另一条完全没察觉。
        //
        // R59 当时只能在这里立一句常驻的能力边界（「条件目录一律收下、条件不判定」）——它对
        // 所有调用都成立，因而对**手上这一条**什么也没说，调用方仍得自己去比对路径。F34 把
        // 索引侧的条件目录建成了查表，于是这句收敛成契约：真落在条件目录里时返回自己会说。
        + ConditionalReport.Contract;

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__read_code",
        "path (a FILE: 'CompShield', 'CompShield.cs' or an absolute path). Aliases accepted: query, file, filePath, fileName.",
        // scope 必须列进来：path 传基名时，正是 scope 决定读哪个源的同名文件。漏掉它，
        // 调用方会以为 read_code 不支持 scope，多源撞名时就没有了唯一能锁定来源的手段。
        "path (required), methodName, className, extractClass, startLine, lineCount, scope "
        + "(scope decides which source wins when several have a file of this name).",
        "If you only have a search term rather than a file, call rimworld-searcher__locate first.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", minLength = 1, description = "File path or indexed file name. Examples: 'CompShield','CompShield.cs','/abs/path/CompShield.cs'. Aliases 'query'/'file'/'filePath' are also accepted." },
            methodName = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Member to extract: method ('CompTick'), property ('Label'), field or event ('energy'), "
                    + "constructor (class name or '.ctor'), indexer ('this'), operator ('+') or enum value "
                    + "('Resetting') — anything locate lists as a member. Every member of that name in the file is "
                    + "returned — pass className to get just one."
            },
            className = new
            {
                type = "string",
                minLength = 1,
                description = "Optional: The class name to resolve ambiguity if multiple classes have the same member name."
            },
            extractClass = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Optional: Extract the entire class/struct/interface/record body by name — an enum or delegate "
                    + "declaration works too. Example: 'CompShield'."
            },
            startLine = new
            {
                type = "integer",
                minimum = 0,
                @default = 0,
                description = "Optional 0-based start line for raw read mode (used when methodName/extractClass is not set)."
            },
            lineCount = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxLineCount,
                @default = DefaultLineCount,
                description =
                    $"Optional number of lines for raw read mode. Default is {DefaultLineCount}, " +
                    $"values above {MaxLineCount} are clamped to it."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog)
        },
        required = new[] { "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var path = ToolArgs.GetRequiredString(args, ArgSpec, "path", "query", "file", "filePath", "fileName");
        path = ToolArgs.StripLocateFilterPrefix(path);

        // 三个名字位在这里读，而不是在下面那个 try 里：参数契约错误要走 ToolArgumentException
        // 那条统一通道（RimSearcher.cs 有专门的渲染），落进下面的 catch(Exception) 就被包成
        // 一句 "Read failed: …" 了。顺带也不必为一次必然失败的调用先把路径解析和 IO 做完。
        var extractClass = ToolArgs.GetOptionalName(args, ArgSpec, "a type name", "extractClass", "class");
        var memberArg = ToolArgs.GetOptionalName(args, ArgSpec, "a member name", "methodName", "method", "member", "memberName");
        var className = ToolArgs.GetOptionalName(args, ArgSpec, "a type name", "className", "type", "typeName");

        var scope = ScopeArgs.Resolve(_scopeCatalog, args);

        var requestedPath = path;
        var resolution = ResolvePath(path, scope);

        // 「越权」与「不存在」必须分开说。文件明明在磁盘上、只是不在白名单里时回一句
        // 「File not found，去 locate 找找」，调用方会照做，而 locate 同样不会返回一个
        // 不在索引根下的文件——它只能反复试。list_directory 对同一情况说的就是越权。
        if (resolution.BlockedByPathSecurity)
            return WithUnresolvedScopeNotice(scope, new ToolResult(
                $"Path outside allowed directories: '{path}'. Only files under the server's indexed source roots "
                + "can be read; use 'locate' to find the indexed copy of what you are after.", true));

        if (resolution.IsDirectory)
            return WithUnresolvedScopeNotice(scope, new ToolResult(
                $"'{path}' is a directory, not a file. List what is in it with "
                + "rimworld-searcher__list_directory, then read_code one of the files it names.", true));

        // 报错必须回显调用方给的**整条** path。只印基名时，'/a/b/Pawn.cs' 和 'Pawn.cs'
        // 的失败长得一模一样，调用方无从判断该改路径还是该改 scope。
        if (resolution.Path == null)
            return WithUnresolvedScopeNotice(scope, new ToolResult(
                $"File not found: '{path}'"
                + (Path.IsPathRooted(path)
                    ? " — that path does not exist on disk, and no indexed file goes by that name either. "
                    : " — no indexed file goes by that name. ")
                + "Use 'locate' to find the correct file first.", true));

        path = resolution.Path;

        // 四条 note 先按裸文本攒着：成功分支要把它们包成 `// …` / `<!-- … -->` 塞进代码围栏，
        // 失败分支是纯文本、不能带注释标记，两处只有包装不同。原先只构造了包装后的那一份，
        // 于是错误返回一条 note 都带不上——而「这里没有这个东西」正是最需要说清读的是
        // 哪个文件的时刻（path 收基名，多源撞名时静默取优先级最高的那一份）。
        //
        // 现状：`plainNotice` 只挂在三条失败返回上（两处 Failure 与行区间越界）。XML 传错模式
        // 与 className 过滤掉光那两条还没挂——它们只印基名，恰好落在上面那句说的情形里。
        var notes = new List<string>();

        if (resolution.RootedInputRedirected)
            notes.Add($"note: '{requestedPath}' does not exist; "
                + $"reading the indexed file of the same name at {path}");

        // 按名解析到了 scope 之外的文件时必须说明，否则读者会以为读的是 scope 内那一份
        if (resolution.OutOfScopeFallback)
            notes.Add($"note: no file by this name inside scope '{scope.Expression}'; "
                + $"reading from {scope.OutOfScopeLabel(path)}");

        // scope 内有多份同名文件时 GetPath 静默取排序第一的那份。不说这件事，调用方会
        // 把 mod 的覆盖版当成 vanilla 原版，据此断言原版行为。
        var siblings = resolution.SameNameInScope;
        // 「优先级最高的那一份」说的是哪一份，判据就在同一句里的 scope 表达式上
        // （GetPathsByName 按 scope.RankOf 排序），不点明的话这个词只能靠猜。
        if (siblings is { Count: > 1 })
            notes.Add($"note: {siblings.Count} files share this name in scope '{scope.Expression}'; "
                + "reading the one from whichever source comes first in that scope. "
                + $"The others: {string.Join(", ", siblings.Skip(1))}");

        // 条件目录里的文件与无条件的那份在返回里逐字同形，而守卫在 loadFolders.xml 那一层。
        // 与上面三条同处：它们说的都是「你读到的这一份到底是什么」，都必须跟着失败分支一起走
        // ——「这里没有这个成员」正是最需要知道读的是不是一份条件性内容的时刻。
        var conditionalNote = ConditionalReport.Explain(_conditional.Of(path));
        if (conditionalNote != null) notes.Add($"note: {conditionalNote}");

        var scopeNotice = notes.Count == 0
            ? string.Empty
            : string.Concat(notes.Select(note => Comment(path, note) + "\n"));
        var plainNotice = notes.Count == 0 ? string.Empty : string.Join("\n", notes) + "\n";

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // XML 上这两个模式必然落空：Roslyn 把整份 XML 解析成一棵没有任何声明的 C# 语法树，
            // 于是走到 TargetNotFound，回一句「类/成员不在这个文件里，用 inspect 核对名字」。
            // 调用方照做会拿到一条 def，回来再传一次仍是同一句——两头都指着对方，而真正的原因
            // 是模式选错了，跟名字对不对无关。这里在解析之前就把原因说出来。
            if (IsXml(path) && (!string.IsNullOrEmpty(extractClass) || !string.IsNullOrEmpty(memberArg)))
            {
                var wrongMode = !string.IsNullOrEmpty(extractClass) ? "extractClass" : "methodName";
                var target = ToolArgs.StripLocateFilterPrefix(extractClass ?? memberArg!);
                return WithUnresolvedScopeNotice(scope, new ToolResult(
                    $"'{Path.GetFileName(path)}' is XML, and '{wrongMode}' parses C# only — no name resolves in it. "
                    + $"Read this file with startLine/lineCount, or call inspect with '{target}' as a defName to get "
                    + "the XML merged down its whole ParentName chain.", true));
            }

            // 三条模式互斥，择一在此前是**静默**的：同时传 extractClass + methodName +
            // startLine，返回的是整个类，而另外两组参数一个字都不提。唯一的线索是首行注释
            // `// Class X — path:N`——它在陈述**交付物**，不是在报告**丢弃**。
            // 后果不是「少了点什么」而是「拿回了完全另一块代码」：第十三轮复采 Pawn +
            // methodName:'Kill' 拿到的是类体前 2000 行，而 Kill 在 2088 行，根本不在里面。
            // 只在确实发生择一时印——没多传参数的调用一个字都不该多出来。
            static string Overridden(string winner, params (string Name, bool Passed)[] losers)
            {
                var dropped = losers.Where(l => l.Passed).Select(l => l.Name).ToList();
                return dropped.Count == 0
                    ? string.Empty
                    : $"// note: '{winner}' takes precedence — {string.Join(" and ", dropped)} "
                      + $"{(dropped.Count == 1 ? "was" : "were")} not applied" + "\n";
            }

            var linesPassed = ToolArgs.TryGetElement(args, out _, "startLine", "start", "offset")
                              || ToolArgs.TryGetElement(args, out _, "lineCount", "lines", "count");

            if (!string.IsNullOrEmpty(extractClass))
            {
                var extractClassName = ToolArgs.StripLocateFilterPrefix(extractClass);
                var classBody = await RoslynHelper.GetClassBodyAsync(path, extractClassName);

                // 按状态判断，不看正文内容。原先是 classBody.Contains("not found")——
                // 反编译产物里 Log.Error("... not found") 这类字面量遍地都是，取到的正文
                // 一旦含这段文本就会被误报成「类不存在」，而代码明明就在那里。
                if (!classBody.IsOk)
                    return WithUnresolvedScopeNotice(scope,
                        Failure(classBody, path, $"Class '{extractClassName}'", "Use inspect tool to verify the type name.", plainNotice));

                // 整个类型的实现体没有天然上限：反编译出来的巨型类动辄几千行，一次就能吃掉
                // 整个上下文预算。裸行模式早有 MaxLineCount 夹着，这里沿用同一个数，超出时
                // 指回 methodName——按成员精读本就比整类通读更贴这个工具的用法。
                // 数的是类自己的行（Body 已剔掉那行位置注释）。连注释行一起数时，
                // 「'Pawn' is 4729 lines」比真实行数多，而这些行还会占掉 MaxLineCount 的额度。
                var classLines = classBody.Body.Split('\n');
                var classContent = classBody.Content;
                var classNote = string.Empty;
                if (classLines.Length > MaxLineCount)
                {
                    classContent = classBody.LocationLine + "\n" + string.Join("\n", classLines.Take(MaxLineCount));
                    // 这个数量的是**类体自己的行**，而同一个工具的裸行模式对同一个文件报的是
                    // 文件行数（`of 4759`）。两个脚注同源不同数，且都不说自己量的是什么——
                    // 第十三轮里连出题的主会话都把 4746 当成了文件长度写进判据。加个限定词。
                    var fileLines = TotalLinesOrZero(path);
                    var span = fileLines > 0
                        ? $"'{extractClassName}' is {classLines.Length} lines of a {fileLines}-line file"
                        : $"'{extractClassName}' is {classLines.Length} lines";

                    // 调用方**已经传了** methodName、被 extractClass 静默压掉时，这句原先照样劝他
                    // 「pass methodName」——照做得到逐字相同的返回。一条会把人绕回原地的建议。
                    var next = !string.IsNullOrEmpty(memberArg)
                        ? $"drop extractClass to get just '{ToolArgs.StripLocateFilterPrefix(memberArg)}', "
                          + "or pass startLine to continue"
                        : "pass methodName for one member, or startLine to continue";

                    classNote = "\n" + Fold.Explicit(
                        classLines.Length - MaxLineCount, CountedNoun.Lines,
                        $"{span} and the cap is {MaxLineCount}; {next}",
                        indent: string.Empty);
                }

                // 目标名不在这里回显：classContent 的首行已经是 `// Class <全名> — <路径>:<行>`，
                // 再补一行只是把同一个名字说第二遍。
                var classOverride = Overridden("extractClass",
                    ($"methodName:'{ToolArgs.StripLocateFilterPrefix(memberArg ?? string.Empty)}'",
                        !string.IsNullOrEmpty(memberArg)),
                    ("startLine/lineCount", linesPassed));

                return WithUnresolvedScopeNotice(scope,
                    new ToolResult($"```{Fence(path)}\n{scopeNotice}{classOverride}{classContent}\n```{classNote}"));
            }

            if (!string.IsNullOrEmpty(memberArg))
            {
                var methodName = ToolArgs.StripLocateFilterPrefix(memberArg);
                var body = await RoslynHelper.GetMemberBodyAsync(path, methodName, className);

                // className 只是个过滤器。过滤后候选归零与「文件里根本没有这个成员」原先
                // 折叠成同一句话，且那句话只点 methodName、完全不提 className——调用方读到
                // 「Member 'ExposeData' not found in Pawn.cs」会直接断定 Pawn 没有 override
                // ExposeData（它其实在 4543 行），据此做出错的序列化判断。
                if (!body.IsOk && body.Status == SourceLookupStatus.TargetNotFound && !string.IsNullOrEmpty(className))
                {
                    var owners = await RoslynHelper.FindMemberOwnersAsync(path, methodName);
                    if (owners.Count > 0)
                        return WithUnresolvedScopeNotice(scope, new ToolResult(
                            $"'{methodName}' does exist in {Path.GetFileName(path)}, but not in a type named "
                            + $"'{className}'. It is declared in: "
                            + string.Join(", ", owners.Select(o => $"{o.Owner} (line {o.Line})"))
                            + ". Drop className, or pass one of those.", true));
                }

                if (!body.IsOk)
                    return WithUnresolvedScopeNotice(scope,
                        Failure(body, path, $"Member '{methodName}'", "Use inspect tool to see available members.", plainNotice));

                var memberOverride = Overridden("methodName", ("startLine/lineCount", linesPassed));

                return WithUnresolvedScopeNotice(scope,
                    new ToolResult($"```{Fence(path)}\n{scopeNotice}{memberOverride}{body.Content}\n```"));
            }

            int startLine = Math.Max(0, ToolArgs.GetInt(args, 0, "startLine", "start", "offset"));
            int lineCount = ToolArgs.GetInt(args, DefaultLineCount, "lineCount", "lines", "count", "limit", "maxResults");
            if (lineCount <= 0)
                return WithUnresolvedScopeNotice(scope, new ToolResult("lineCount must be greater than 0.", true));
            lineCount = Math.Min(lineCount, MaxLineCount);

            var allLines = File.ReadAllLines(path);
            int totalLines = allLines.Length;

            var resultLines = allLines.Skip(startLine).Take(lineCount).Select((line, idx) => $"L{startLine + idx + 1}: {line}").ToList();

            if (resultLines.Count == 0)
                // 失败分支同样要说清「这个数是哪个文件的」：path 可以是基名解析来的，
                // 光一个 `334 lines` 悬在半空，调用方分不出读的是 vanilla 那份还是覆盖版。
                return WithUnresolvedScopeNotice(scope, new ToolResult(
                    plainNotice
                    + $"Line range {startLine + 1}-{startLine + lineCount} exceeds "
                    + $"file length ({CountedNoun.Lines.Quantity(totalLines)}) in {path}.", true));

            var sb = new StringBuilder();
            sb.AppendLine($"```{Fence(path)}");
            if (scopeNotice.Length > 0) sb.Append(scopeNotice);
            // 印解析后的**绝对路径**，与 methodName / extractClass 两个模式的位置行对齐——
            // 三种模式都以恰好一行「读的是哪个文件的哪一段」开头。只印基名时，path 传的是
            // 基名、而索引里有多份同名文件的那种情形在返回里不留痕迹。
            sb.AppendLine(Comment(path,
                $"{path} (lines {startLine + 1}-{Math.Min(startLine + lineCount, totalLines)} of {totalLines})"));
            foreach (var line in resultLines) sb.AppendLine(line);
            sb.AppendLine("```");

            if (startLine + lineCount < totalLines)
            {
                // 总数要在这一行里给出，判据同 R47 的每文件折叠行（`+4 more of 7 matching lines`）：
                // 文件总行数只在顶部那行位置注释里出现过一次，而作答时它早已滚出视野——第十轮
                // 盲测里一条链差点在没读完的情况下下结论，同一轮里另一条链正是靠 search_regex
                // 那个 `of 7` 拦住了一次误算。有总数的那处救了一次，没总数的这处险些误一次。
                // 「给了 total 就走 `of M` 形」现在由 Fold.Explicit 一处决定，不再逐处手拼。
                sb.AppendLine("\n" + Fold.Explicit(
                    totalLines - (startLine + lineCount), CountedNoun.Lines,
                    $"pass startLine={startLine + lineCount}",
                    total: totalLines, indent: string.Empty));
            }

            return WithUnresolvedScopeNotice(scope, new ToolResult(sb.ToString()));
        }
        catch (Exception ex)
        {
            return WithUnresolvedScopeNotice(scope, new ToolResult($"Read failed: {ex.Message}", true));
        }
    }

    // 围栏的语言标记按扩展名选：raw 模式最常见的用途之一就是读 Defs 的 XML，一律标 csharp
    // 会让阅读端按 C# 高亮一份 XML。头部注释必须跟着换语法——`// ...` 留在 xml 块里就是一行
    // 非法内容，整块复制出去直接解析失败。
    private static bool IsXml(string path)
        => Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);

    private static string Fence(string path) => IsXml(path) ? "xml" : "csharp";

    // 只用于给 extractClass 的截断脚注补一个「类体行数 / 文件行数」的对照。读不到就退回不印
    // 那半句——一个数量不出来不该让整条能用的返回失败。
    private static int TotalLinesOrZero(string path)
    {
        try { return File.ReadAllLines(path).Length; }
        catch { return 0; }
    }

    private static string Comment(string path, string text) => IsXml(path) ? $"<!-- {text} -->" : $"// {text}";

    // 拼错的 scope 被 ScopeCatalog 静默退回全域，返回里不说一句，调用方就会以为自己限定过范围。
    // 一律追加在正文最末尾：正文通常是 ```csharp 代码块，提示混进块里就成了源码的一部分。
    private ToolResult WithUnresolvedScopeNotice(ScopeSelection scope, ToolResult result)
    {
        var notice = ScopeNotices.Unresolved(_scopeCatalog, scope);
        return notice == null ? result : result with { Content = result.Content + notice };
    }

    // 三种失败原因给三种不同的下一步：文件没了要重查、文件过大要改用裸行读、目标不存在才该去 inspect。
    // 原先它们都被折叠成一句「not found」，读者据此断言「类不存在」，而真相可能是文件被重新同步掉了。
    private static ToolResult Failure(
        SourceLookupResult result, string path, string target, string notFoundHint, string plainNotice = "")
    {
        // 报解析后的绝对路径而非基名：成功分支的位置行印的就是绝对路径，两边说的必须是同一件事。
        // 基名在 path 收基名 + 多源撞名时不足以定位，而这正是最需要定位的一刻。
        var message = result.Status switch
        {
            SourceLookupStatus.FileNotFound =>
                $"File disappeared while reading: {path}. Sources may have just been re-synced — call locate again.",
            SourceLookupStatus.FileTooLarge =>
                $"{path} is larger than {RoslynHelper.MaxParseFileSize / (1024 * 1024)} MB, so it is not parsed. " +
                "Read it with startLine/lineCount instead, or narrow down with search_regex.",
            _ => $"{target} not found in {path}. {notFoundHint}"
        };

        return new ToolResult(plainNotice + message, true);
    }

    // 解析结果的每一路都要能被调用方分辨：读到了哪条绝对路径、是不是几选一、
    // 传的到底是目录还是不存在的文件。塞进 out 参数会到六个，只能立成一个结果类型。
    private sealed record PathResolution(
        string? Path,
        bool OutOfScopeFallback = false,
        bool BlockedByPathSecurity = false,
        bool RootedInputRedirected = false,
        bool IsDirectory = false,
        IReadOnlyList<string>? SameNameInScope = null);

    private PathResolution ResolvePath(string input, ScopeSelection scope)
    {
        // 绝对路径是调用方自己给的，不受 scope 约束——它已经知道要读哪个文件了
        if (Path.IsPathRooted(input) && File.Exists(input))
        {
            if (PathSecurity.IsPathSafe(input)) return new PathResolution(input);

            // 文件确实存在、只是不在白名单内。按名再查一遍索引没有意义（调用方给的是
            // 一条完整绝对路径），直接把真实原因回上去。
            return new PathResolution(null, BlockedByPathSecurity: true);
        }

        // 目录被当成「文件不存在」回上去时，调用方唯一能照做的下一步是 locate，而它
        // 返回的是一堆文件名，仍然说不出「你传的是目录」。同一台 server 上的
        // list_directory 才是正解，判一次 Directory.Exists 就能说清楚。
        if (Directory.Exists(input)) return new PathResolution(null, IsDirectory: true);

        // 绝对路径打错时下面仍会按文件名去索引里另找一份同名文件，读的就不是调用方点名的
        // 那条路径了。这一点必须回上去说：返回里的头部注释打印的是解析后的文件名，
        // 光看返回没有任何线索表明发生过替换。
        var rootedButMissing = Path.IsPathRooted(input);

        var nameNoExt = Path.GetFileNameWithoutExtension(input);
        var rawName = Path.GetFileName(input);

        foreach (var key in rawName != nameNoExt ? new[] { nameNoExt, rawName } : [nameNoExt])
        {
            var indexPath = _sourceIndexer.GetPath(key, scope, out var outOfScope);
            if (indexPath == null || !File.Exists(indexPath)) continue;

            return new PathResolution(
                indexPath,
                OutOfScopeFallback: outOfScope,
                RootedInputRedirected: rootedButMissing,
                SameNameInScope: outOfScope ? null : _sourceIndexer.GetPathsByName(key, scope));
        }

        return new PathResolution(null);
    }
}
