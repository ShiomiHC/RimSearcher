using System.Text;
using System.Text.Json;
using RimSearcher.Core;
using RimSearcher.Server.Tools.Output;

namespace RimSearcher.Server.Tools;

public class InspectTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;

    private static readonly HashSet<string> ClassTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "thingClass", "workerClass", "jobClass", "hediffClass", "thoughtClass",
        "compClass", "incidentClass", "interactionWorkerClass", "mentalStateHandlerClass",
        "ritualBehaviorClass", "skillGiverClass", "worldObjectClass", "lifeStageWorkerClass",
        "traitWorkerClass", "selectionWorkerClass", "modExtension", "giverClass",
        "soundClass", "damageWorkerClass", "linkDrawerClass", "graphicClass",
        "blueprintClass", "scattererClass", "questClass", "verbClass",
        "roomRoleWorker", "statWorker", "moteClass", "thinkTree",
        "driverClass", "lordJob", "tabWindowClass", "pageClass", "comparerClass",
        "drawStyleType", "fleckSystemClass", "subEffecterClass", "needClass",
        "taleClass", "triggerClass", "inheritanceWorkerOverrideClass", "workerType",
        "eventClass", "worldDrawLayer", "designatorType", "scenPartClass", "stateClass"
    };

    private readonly ScopeCatalog _scopeCatalog;
    private readonly LocalizationIndex? _localization;

    // 译文描述整段塞进来会把下面的 Resolved XML 挤出视线，而它只是个参考
    private const int LocalizedDescriptionLimit = 300;

    // 渲染完整大纲的文件数上限。同名类型分散在多个源里时，第二份起只报路径——
    // 几份大纲通常高度重合，而体积是实打实地翻倍。
    private const int MaxOutlinedFiles = 1;

    // Linked C# Types 一次最多列几条。定长上限，不经过 ScopeFilter：没有任何参数放得宽它。
    // 此前这个数在同一段里手写了三遍（Take、判断、折叠行的减数），改一处漏一处时折叠行
    // 报的「还有几条」就与实际列出的条数对不上，而那是个纯算术错误、任何一道闸都不看。
    private const int MaxLinkedTypes = 10;

    private readonly ConditionalFolders _conditional;

    public InspectTool(
        SourceIndexer sourceIndexer,
        DefIndexer defIndexer,
        ScopeCatalog scopeCatalog,
        LocalizationIndex? localization = null,
        ConditionalFolders? conditional = null)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
        _localization = localization;
        _conditional = conditional ?? ConditionalFolders.None;
    }

    public string Name => "rimworld-searcher__inspect";

    // scope / limit 两族取 ScopeAndLimitArgs 的名单，不再在这里各抄一遍：抄漏的 `max` 与 `top`
    // 读得进来却被报成被忽略，同一份返回自相矛盾。
    public IEnumerable<string> ExtraAcceptedKeys =>
        [.. ScopeAndLimitArgs.ScopeKeys, .. ScopeAndLimitArgs.LimitKeys,
         "query", "defName", "typeName", "symbol", "defTypeName", "xmlStart", "startLine"];

    public string Description =>
        "Full detail for one exactly-named def or C# type; no fuzzy matching. " +
        "Def mode returns the XML merged down the whole ParentName chain within the current scope — inheritance " +
        "only, which no single XML file contains — plus the C# classes referenced from it. Mod PatchOperations " +
        "are never applied, so this is the merged definition, not the one the running game would see; if a mod " +
        "outside the current scope also defines this def, that copy is ignored and the in-scope one is returned. " +
        "Fields are not marked by origin — to " +
        "tell a def's own fields from inherited ones, read_code the `File:` path, which holds only its own " +
        "un-merged lines. " +
        "Type mode returns the base-class chain (interfaces are not on it — use trace mode:'inheritors' for those) "
        + "and a member outline of fields, properties and methods; constructors, indexers and operators are not "
        + "outlined but read_code can still read them by name. Enums are outlined as their values, delegates as "
        + $"their signature. The outline lists at most {RoslynHelper.DefaultMaxOutlineMembersPerKind} members per "
        + "kind, and when several sources declare the "
        + "same type only the highest-priority one is outlined; both cuts are stated inline where they happen. "
        + "Method bodies come from read_code. "
        // 「PatchOperation 从不被应用」上面已经说了，而条件目录是它的姊妹缺口：那一条说的是
        // 「这份 XML 会被别人改」，这一条说的是「这份 XML 未必会被加载」。两条都要有才闭合。
        + ConditionalReport.Contract;

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__inspect",
        "name (an exact DefName or C# type name, e.g. 'Apparel_ShieldBelt' / 'CompShield'). Aliases accepted: query, defName, typeName, symbol.",
        "name (required), defType (which def type to show when several share the name), xmlStartLine (continue reading a long merged XML), limit (members per kind in the outline; 'all' for every one), scope.",
        "A 'def:'/'type:' prefix is stripped automatically. Matching ignores case but needs the whole name — partial names and typos do not resolve; use rimworld-searcher__locate to get the exact name first.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            name = new
            {
                type = "string",
                minLength = 1,
                description = "Complete DefName or C# type name. Examples: 'Apparel_ShieldBelt', 'CompShield'. Matching ignores case — the response echoes the index's canonical spelling — but partial names and typos do not resolve. Aliases 'query'/'defName'/'typeName' are also accepted; a 'def:'/'type:' prefix is stripped."
            },
            defType = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Optional: which def type to show when several defs share the name — 'Human' is a ThingDef, "
                    + "a BodyDef and a HediffGiverSetDef at once. The response names every type sharing the name, "
                    + "so a first call without this parameter tells you what to pass. Alias 'defTypeName' is also "
                    + "accepted. This is a def type, not a C# type name — it does not narrow type mode."
            },
            xmlStartLine = new
            {
                type = "integer",
                minimum = 1,
                description =
                    "Def mode only. 1-based line to continue the merged XML from when it was truncated; the "
                    + $"response reads {XmlWindowLines} lines from there and tells you the next value to pass. "
                    + "Needed because the merged XML is the whole ParentName chain combined and therefore is not "
                    + "the content of any file — read_code on the `File:` path returns only the def's own "
                    + "un-merged lines, without the inherited ones. Ignored in type mode."
            },
            limit = new
            {
                description =
                    "Type mode only. How many members of each kind (properties, fields, methods) the outline lists "
                    + $"before folding the rest away. Default {RoslynHelper.DefaultMaxOutlineMembersPerKind}; pass a "
                    + "number, or 'all' to list every member. 'all' is the only way to see the folded ones — "
                    + $"read_code extractClass truncates at {ReadCodeTool.MaxLineCount} lines and will not show them all "
                    + "on a large file. "
                    + "Ignored in def mode."
            },
            scope = ScopeAndLimitArgs.ScopeSchemaProperty(_scopeCatalog)
        },
        required = new[] { "name" }
    };

    // 折叠掉的成员此前在整套 API 里没有任何取全途径：inspect 没有 limit、locate 只能按已知
    // 名字找、read_code extractClass 到 2000 行就二次截断。'all' 在这里必须是真无限，
    // 不能沿用 ScopeAndLimitArgs.HardLimit——单个类型的成员数超过 200 是常态。
    private static int OutlineLimit(JsonElement args)
    {
        var limit = ScopeAndLimitArgs.GetDisplayLimit(args, RoslynHelper.DefaultMaxOutlineMembersPerKind);
        return limit.Unlimited ? int.MaxValue : limit.Count;
    }

    // 译文描述里换行是常态（多段落说明），单行摆进头部区域会把格式冲散
    private static string Truncate(string text, int limit)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= limit ? single : single[..limit] + "…";
    }

    // 大纲取不到时说清是哪一种取不到：文件没了 / 文件太大不解析 / 文件里确实没这个类型
    // （最后一种通常意味着索引落后于磁盘，比如源刚被重新同步过）。
    private static string DescribeOutlineFailure(SourceLookupStatus status, string typeName) => status switch
    {
        SourceLookupStatus.FileNotFound =>
            "_(file no longer exists — sources may have just been re-synced; retry after sync_sources)_",
        SourceLookupStatus.FileTooLarge =>
            $"_(outline skipped: file exceeds the {RoslynHelper.MaxParseFileSize / (1024 * 1024)} MB parse limit; " +
            "use read_code with startLine/lineCount)_",
        _ =>
            $"_(no declaration of `{typeName}` in this file — the index may be stale; retry after sync_sources)_"
    };

    // 父链状态必须写进输出。合并是**静默**失败的：父 def 查不到时循环直接结束，
    // CleanupMetadata 又把 ParentName 属性删掉，于是「本来就没有父」「父已合进来」
    // 「父找不到所以少了一半」三种情形渲染得逐字同形。而本工具描述向调用方承诺的是
    // 「the complete effective definition」——它会把半成品当完整定义，据此断定某个 hediff
    // 没有 hediffClass、不关联任何 C# 类，然后去补一个根本不缺的字段。
    private static void AppendInheritanceChain(StringBuilder sb, DefInheritanceTrace trace, ScopeSelection scope)
    {
        // F39 给类型模式的同名行补了辖域（`inherited members are not in the outline below at
        // any limit`），而 def 模式的同名行语义**正好相反**——下面那份 XML 恰恰是合并过的、
        // 含继承字段的。两处同形而义反，只修了一半。第十三轮盲测里被测方因此把 def 模式的
        // 沉默读成了「无可声明」（判官记作 M4：F39 教会了读者「inspect 会就地声明取值域」，
        // 于是没声明的地方被读成「没有可说的」）。
        // 「这份 XML 是合并出来的、不是 File: 那个文件的内容」此前只写在 ContinueHint 里，
        // 而 ContinueHint **只在截断时**渲染——绝大多数 def 走的是整块分支，从头到尾没人说过。
        if (trace.Chain.Count > 1)
            sb.AppendLine($"Inheritance chain: {string.Join(" <- ", trace.Chain)}"
                          + " — the XML below is these merged together, so it is not the content of the"
                          + " `File:` path above, which holds only this def's own un-merged lines.");
        else if (trace.IsComplete)
            sb.AppendLine("Inheritance chain: none (this def declares no ParentName)");

        if (trace.UnresolvedParent != null)
        {
            sb.AppendLine(
                $"\n**Warning: parent '{trace.UnresolvedParent}' was not found in scope '{scope.Expression}', "
                + "so the XML below is NOT the complete effective definition — every field inherited from it "
                + "(and from anything above it) is missing.** Re-run with scope:'all'; if it is still missing, "
                + "that source is not in this server's config.");
        }
        else if (trace.StoppedByCycle)
        {
            sb.AppendLine(
                "\n**Warning: the ParentName chain loops back on itself, so the merge stopped early and the "
                + "XML below may be missing inherited fields.**");
        }
        else if (trace.StoppedAtDepthLimit)
        {
            sb.AppendLine(
                "\n**Warning: the ParentName chain is longer than this server merges, so the XML below may be "
                + "missing fields from the topmost ancestors.**");
        }
    }

    // 合并后的 XML 一屏放不下时的窗口大小；不带 xmlStartLine 的首次调用按 头 HeadLines +
    // 尾 TailLines 渲染（开头是字段主体，结尾是收尾结构，两头都比中段更常被需要）。
    private const int XmlHeadLines = 200;
    private const int XmlTailLines = 50;
    private const int XmlWindowLines = XmlHeadLines + XmlTailLines;

    // 合并 XML 的续读起点（1-based）。传了就渲染一段连续窗口，不传则走头尾两段。
    //
    // 这个参数存在的唯一理由：被截断的是**沿 ParentName 链合并后**的 XML，而它不对应磁盘上
    // 任何一个文件——上面那行 `File:` 指的是子 def 自己那份未合并的源文件，里面恰恰没有
    // 继承来的字段。此前截断提示写的是「use read_code on file path above」，照做拿回来的
    // 是另一份文档，且缺的正是 inspect def 模式唯一的存在理由。续读只能由本工具自己提供。
    private static int ResolveXmlStartLine(JsonElement args, int totalLines)
    {
        var requested = ToolArgs.GetInt(args, 0, "xmlStartLine", "xmlStart", "startLine");
        if (requested <= 0) return 0;
        return Math.Min(requested, Math.Max(1, totalLines));
    }

    private static void AppendResolvedXml(StringBuilder sb, string[] xmlLines, string defName, int startLine)
    {
        // R51 那句「PatchOperations 从不被应用」此前只在 tools/list 的 Description 里。第九轮
        // 盲测里它两次是整条链的转折点，但两次都靠调用方通读了 schema——返回本身一个字没说。
        // 而这一段恰恰是整份返回里唯一会被当作「游戏里的那个 def」读的东西：base 与 all 逐字
        // 相同时（HAR 的补丁改的是 Class 属性，不进这份 XML），读起来就是「没有 mod 改过」。
        // 能力边界不是缺陷，不说自己答不了才是——把那半句复制到它作用的那个块的标题上。
        const string PatchNote = "mod PatchOperations are not applied, so a mod patch against this def "
                                 + "is not reflected below";
        // 首次调用的表头此前不带任何行数，**截不截断都一样**。于是版面上只有两种表头：
        // 裸的（首次）与带 `lines X-Y of Z` 的（自己传了 xmlStartLine 才出现），
        // 唯一能归纳出来的规则就是「裸 = 完整」——而它是假的。第十三轮盲测里被测方
        // 一字不差地归纳出了这条，还把它当判别方法写进了交付给用户的答案。
        // 改用 F30 已经立起来的三态文法：裸 N = 完整集，N of M = 被截了，M 是范围总数。
        // 不新造记号，只是把这一处补进那套文法里。
        var firstCallTruncated = startLine <= 0 && xmlLines.Length > XmlWindowLines + XmlTailLines;
        // 三态都走计数记号的产地：区间形 Tally.Window（of = 取自）、截断形 Tally.Cell
        // （of = 没给全）、完整形直接构词。此前前两支是这里的两行裸插值，与 read_code 的位置行、
        // locate 的段头各自长得一样却互不相干。
        var extent = startLine > 0
            ? Tally.Window(
                CountedNoun.Lines, startLine, Math.Min(startLine + XmlWindowLines - 1, xmlLines.Length),
                xmlLines.Length)
            : firstCallTruncated
                ? Tally.Cell(XmlWindowLines, xmlLines.Length, CountedNoun.Lines)
                : CountedNoun.Lines.Quantity(xmlLines.Length);
        sb.AppendLine($"\n**Resolved XML** ({extent}; {PatchNote}):");

        // 明确点名续读要回到 inspect，且说清 File: 那一行不是这份 XML 的来源
        string ContinueHint(int nextStart) =>
            $"(Full merged XML: {CountedNoun.Lines.Quantity(xmlLines.Length)}. This is the merge of the whole ParentName chain, so it is "
            + $"not the content of any one file — the `File:` path above holds only {defName}'s own un-merged lines. "
            + $"For the rest call inspect again with xmlStartLine: {nextStart}.)";

        if (startLine > 0)
        {
            var from = startLine - 1;
            var to = Math.Min(from + XmlWindowLines, xmlLines.Length);

            sb.AppendLine("```xml");
            for (int i = from; i < to; i++) sb.AppendLine(xmlLines[i]);
            sb.AppendLine("```");

            sb.AppendLine(to < xmlLines.Length
                ? ContinueHint(to + 1)
                : $"(End of the merged XML, {CountedNoun.Lines.Quantity(xmlLines.Length)} total.)");
            return;
        }

        if (xmlLines.Length <= XmlWindowLines + XmlTailLines)
        {
            sb.AppendLine("```xml");
            sb.AppendLine(string.Join("\n", xmlLines).TrimEnd('\n'));
            sb.AppendLine("```");
            return;
        }

        sb.AppendLine("```xml");
        for (int i = 0; i < XmlHeadLines; i++) sb.AppendLine(xmlLines[i]);
        sb.AppendLine(
            "\n" + Fold.Elision(
                xmlLines.Length - XmlWindowLines, XmlHeadLines + 1, xmlLines.Length - XmlTailLines) + "\n");
        for (int i = xmlLines.Length - XmlTailLines; i < xmlLines.Length; i++) sb.AppendLine(xmlLines[i]);
        sb.AppendLine("```");
        sb.AppendLine(ContinueHint(XmlHeadLines + 1));
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var name = ToolArgs.StripLocateFilterPrefix(
            ToolArgs.GetRequiredFuzzyString(args, ArgSpec, "name", "query", "defName", "typeName", "symbol"));

        var scope = ScopeAndLimitArgs.Resolve(_scopeCatalog, args);

        // 拼错的 scope 被静默退回全域，两个分支的每条返回路径都要带上这行，
        // 否则调用方会把全域结果当成自己限定过的范围内结果。
        var scopeNotice = ScopeNotices.Unresolved(_scopeCatalog, scope) ?? string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();

        // 不收 'type' 作别名：本服务器里 'type' 到处都是 C# 类型名（read_code 的 className、
        // inspect 自己 name 支持的 'type:' 前缀），收了它等于把一个常见的误用变成一条假告警。
        var requestedDefType = ToolArgs.GetOptionalName(args, ArgSpec, "a def type name", "defType", "defTypeName");
        var lookup = _defIndexer.Lookup(name, scope, defType: requestedDefType);
        var def = lookup.Location;
        if (def != null)
        {
            // 标题回显索引里的 DefName，而不是调用方传进来的拼写：查找是 OrdinalIgnoreCase 的，
            // 原样回显等于把调用方的错拼盖章成真实 defName，它会照着这个名字继续往下查。
            // 下面的 Type / File / C# Class 本来就全部取自 def，标题跟着它才是一致的。
            sb.AppendLine($"## Def: {def.DefName}");

            // DefType 就是这个 def 的 C# 类名，故不再单起一行 `C# Class:` 把同一个词说第二遍
            // （R20 把文件名收进判据之后，那行在可推情形下整行零新增事实）。剩下的唯一事实是
            // 「这个类在不在索引里、在哪个文件里」，附在 Type 行末尾即可。「不在索引里」原先靠
            // 整行缺席表达——读者得先知道有这条规则才读得出来，改为明说；措辞点名 C# class，
            // 免得与紧下方 `File:` 行说的 def 自身文件混起来。
            var typePaths = _sourceIndexer.GetPathsByType(def.DefType);
            sb.AppendLine($"Type: {def.DefType}"
                + (typePaths.Count > 0
                    ? SymbolRow.FileNote(def.DefType, typePaths)
                    : " (C# class not indexed)"));

            // 译文只作为附注，下面的 Resolved XML 一个字不动——那是游戏真实数据，
            // 把译名掺进去会毁掉它「照着它就能改 mod」的用途。
            var localized = _localization?.Lookup(def.DefType, def.DefName);
            if (!string.IsNullOrEmpty(localized?.Label))
                sb.AppendLine($"Localized: {localized.Label}");
            if (!string.IsNullOrEmpty(localized?.Description))
                sb.AppendLine($"Localized description: {Truncate(localized.Description, LocalizedDescriptionLimit)}");

            var sourceName = scope.SourceNameOf(def.FilePath);
            if (!string.IsNullOrEmpty(sourceName)) sb.AppendLine($"Source: {sourceName}");

            sb.AppendLine($"File: `{def.FilePath}`");

            // 「这份 XML 会被 PatchOperation 改」下面 Resolved XML 的标题已经说了（R62），
            // 而「这份 XML 未必会被加载」是它的姊妹缺口——两者都让「照着这里读就是运行时」
            // 落空，且都不在返回的任何别处留痕。整份返回只讲一条 def，故合成一句说完。
            var defConditional = ConditionalReport.Explain(_conditional.Of(def.FilePath));
            if (defConditional != null) sb.AppendLine($"_Note: {defConditional}._");

            // 同名 def 散在多处时必须说清取的是哪一个，否则读者会把这份 XML 当成唯一定义。
            // 光说「有 3 条」没有可操作性：把类型一并列出来，调用方才看得出自己要的是不是这条
            // （Human 在 vanilla 里就是 ThingDef / BodyDef / HediffGiverSetDef 各一条）。
            if (lookup.AmbiguousInScope)
            {
                var types = lookup.InScopeDefTypes.Count > 0
                    ? $" ({string.Join(", ", lookup.InScopeDefTypes)})"
                    : string.Empty;
                // 下一步得按「是什么把这几条分开的」来给，三种情形的正确动作互不相同：
                //   - 每种类型各一条          → defType 分得开，且要把可选的类型名列出来，
                //                               否则调用方还得再查一次才知道能传什么；
                //   - 选中的类型自己就有多条  → 再传同一个 defType 拿回逐字相同的结果，
                //                               分开它们只能靠更窄的 scope；
                //   - scope 内只有这一种类型  → defType 完全无从下手。
                // 「pass defType to pick another」原先是这三种情形的统一答复，后两种照做都是死路，
                // 而已经传过 defType 的调用方读到它更是一句同义反复。
                var otherTypes = lookup.InScopeDefTypes
                    .Where(type => !string.Equals(type, def.DefType, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var howToPickAnother = otherTypes.Count == 0
                    ? "narrow scope to a single source to pick another"
                    : lookup.SameDefTypeCount > 1
                        ? $"pass defType for a different type ({string.Join(", ", otherTypes)}), or narrow scope "
                          + $"to a single source to pick among the {lookup.SameDefTypeCount} {def.DefType} ones"
                        : $"pass defType to pick another ({string.Join(", ", otherTypes)})";
                sb.AppendLine(
                    $"_Note: {lookup.InScopeCount} defs share this name within scope '{scope.Expression}'{types}; "
                    + $"showing the {def.DefType} one — {howToPickAnother}._");
            }

            // 点名的类型不存在却照常返回，读者会把手上这条当成自己要的那种
            if (lookup.RequestedDefTypeUnavailable)
            {
                var available = lookup.InScopeDefTypes.Count > 0
                    ? string.Join(", ", lookup.InScopeDefTypes)
                    : def.DefType;
                sb.AppendLine(
                    $"_Note: no '{requestedDefType}' named '{def.DefName}' in scope '{scope.Expression}'; "
                    + $"showing the {def.DefType} one instead (available: {available})._");
            }
            if (lookup.OtherSources.Count > 0)
                sb.AppendLine($"_Also defined in: {string.Join(", ", lookup.OtherSources)} (outside scope '{scope.Expression}')._");

            // def 与 C# 类型撞名时，`if (def != null)` 这一支无条件胜出并 return，类型索引
            // **从来没被查过**。而整份返回里唯一的同名披露是上面那句 AmbiguousInScope，
            // 它枚举的范围只有 def。第十三轮盲测里被测方据此把这份沉默当成了「不存在同名
            // C# 类型」的独立证据——`Fire` 就是现成反例（ThingDef 与 Verse.Fire 同时存在，
            // 两次调用都只回 ThingDef，连那句 Note 都不出现，因为 def 侧并不歧义）。
            //
            // **只在同名类型确实存在时印**。查不到就一个字不印——那时的沉默才真正代表「没有」，
            // 无条件挂一句「本次按 def 解析」是 R19 判掉的那种常亮。
            var sameNamedType = _sourceIndexer.GetPathsByType(def.DefName, scope);
            if (sameNamedType.Items.Count > 0)
            {
                var twinPaths = sameNamedType.Items.Select(e => e.Item).ToList();
                // 出路必须自带参数值。原先写的是「read_code extractClass on that path」，指望前面
                // 那个 FileNote 提供 path——可 FileNote 恰恰在**文件名推得出来**时返回空串
                // （Fire → Fire.cs），于是最常见的那一支里 "that path" 指向了不存在的东西（实测）。
                var typeFile = Path.GetFileName(twinPaths[0]);
                sb.AppendLine(
                    $"_Note: a C# type named '{def.DefName}' also exists"
                    + $"{SymbolRow.FileNote(def.DefName, twinPaths)}; inspect resolves def before "
                    + "type and no parameter overrides that (defType picks among defs, not between the two). "
                    + $"Reach the type with read_code path:'{typeFile}' extractClass:'{def.DefName}'._");
            }

            // limit 在 def 模式**从不被读**（OutlineLimit 只在类型模式调用）。schema 里写着
            // `Ignored in def mode.`，返回里此前零字——而调用方传它时指望的正是「别截断」，
            // 且 def 模式**确实会截断**，只是换了个参数（xmlStartLine）。指望的那件事恰好是
            // 本次任务的成败关键，故值得加字；只在真传了 limit 时印。
            // 探的是 GetDisplayLimit 认的**那一份**名单，不在这里手抄一份缩短版：抄的那份
            // 漏了 max / count / top，于是 `max:5` 一个字不印而 `limit:5` 会印——同一个意图
            // 两种披露，而调用方分不出自己碰到的是哪一种。这与 8ca8ed6 在取值 ↔
            // ExtraAcceptedKeys 之间收掉的是同一件事，当时没往「探测」这第三处看。
            if (ToolArgs.TryGetElement(args, out _, ScopeAndLimitArgs.LimitKeys))
            {
                sb.AppendLine(
                    "_Note: 'limit' applies to the C# type outline only and was ignored here; "
                    // 这条印在 XML **之前**（紧跟 File: 行），故不能写 above——实测就是这么错的
                    + "the merged XML below is paged with 'xmlStartLine'._");
            }

            // 传 def 而不是 name：上面已经用 defType 消过歧，按名字重查会落回默认胜者，
            // 表头说的是 ThingDef、正文却给出 BodyDef 的 XML。
            var (resolvedXml, chainTrace) = await XmlInheritanceHelper.ResolveDefXmlWithTraceAsync(
                def, _defIndexer, scope);
            if (resolvedXml == null)
            {
                sb.AppendLine("\n**Resolved XML:** Failed to load Def XML");
                sb.Append(scopeNotice);
                return new ToolResult(sb.ToString());
            }

            AppendInheritanceChain(sb, chainTrace, scope);

            // 先归一化再切：XElement.ToString() 走 XmlWriter，行尾是 CRLF，裸按 '\n' 切会给
            // 每行留一个尾随 '\r'，而下面是逐行 AppendLine 重新拼的——ToolResult 收口时那个
            // 孤立的 '\r' 被换成 '\n'，于是整份合并 XML 每行后面多一个空行（行数直接翻倍，
            // 而截断窗口与 xmlStartLine 都是按行数算的）。
            var resolvedXmlStr = resolvedXml.ToString().ReplaceLineEndings("\n");
            var xmlLines = resolvedXmlStr.Split('\n');
            AppendResolvedXml(sb, xmlLines, def.DefName, ResolveXmlStartLine(args, xmlLines.Length));

            try
            {
                var foundTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in resolvedXml.Descendants())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ClassTags.Contains(el.Name.LocalName) ||
                        el.Name.LocalName.EndsWith("Class", StringComparison.OrdinalIgnoreCase) ||
                        el.Name.LocalName.EndsWith("Worker", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = el.Value.Trim();
                        if (LooksLikeTypeName(val)) foundTypes.Add(val);
                    }

                    var classAttr = el.Attribute("Class");
                    if (classAttr != null)
                    {
                        var val = classAttr.Value.Trim();
                        if (LooksLikeTypeName(val)) foundTypes.Add(val);
                    }
                }

                if (foundTypes.Count > 0)
                {
                    sb.AppendLine("\n**Linked C# Types:**");
                    var typesArray = foundTypes.Take(MaxLinkedTypes).ToArray();
                    foreach (var cls in typesArray)
                    {
                        var paths = _sourceIndexer.GetPathsByType(cls);
                        sb.AppendLine($"- `{cls}`{SymbolRow.FileNote(cls, paths)}");
                    }
                    // 定长上限，不经过 ScopeFilter：没有任何参数放得宽这个 10，故下一步是换工具
                    // 而不是调 limit——落不进 Fold.Line 的三分支，走显式那一形。
                    if (foundTypes.Count > MaxLinkedTypes)
                        sb.AppendLine(Fold.Explicit(
                            foundTypes.Count - MaxLinkedTypes, CountedNoun.Types, "use locate to find them"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
            }

            sb.Append(scopeNotice);
            return new ToolResult(sb.ToString());
        }

        var csharpPaths = _sourceIndexer.GetPathsByType(name, scope);
        if (csharpPaths.Items.Count > 0)
        {
            sb.AppendLine($"## C# Type: {CanonicalTypeName(name)}");

            // C# 的基类链恒为线性，用 mermaid `graph TD` 画它是把一维信息塞进二维格式：
            // 三层链要 7 行 ~150 字符，而 `A <- B <- C` 一行 ~45 字符说的是同一件事，
            // 读者还得从箭头对里自己重建先后顺序。同一个工具的 def 模式早就在用一行式
            // （`Inheritance chain: X <- Y`），两处渲染同一个概念不该有两套写法。
            // 这一行必须自己说清底下那张表的**取值域**。def 模式的同名行下面是沿 ParentName
            // 合并过的结果，type 模式的下面不是——两处同形而语义相反，而返回里此前没有一处
            // 区分。第十二轮盲测：调用方在 `Pawn` 上找取地图的成员，`Map` 与 `MapHeld` 声明在
            // 基类 `Verse.Thing` 上，展开全部 118 条属性也看不到，而「不列」与「没有」在版面上
            // 逐字同形。这不是第三道截断（那两道是 40 条上限与多源同名），限定词也就不能挂到
            // 折叠行上——折叠行数的是这一类里已声明的那些，说的是另一件事。
            var chain = _sourceIndexer.GetInheritanceChain(name);
            if (chain.Count > 0)
                sb.AppendLine(
                    $"Inheritance chain: {string.Join(" <- ", LinearChain(chain))}"
                    + " — inherited members are not in the outline below at any limit; inspect a base name for its own.");

            // 同名类型在 vanilla 与各 mod 里各有一份是常态（HAR 之类的前置尤其如此），
            // 每份都全量渲染一次大纲，体积就按文件数线性放大。读者要的通常是作用域里
            // 优先级最高的那一份——Items 已按该顺序排好——其余只报路径，真要看就收窄
            // scope 或用 read_code extractClass 点名去取。
            // 类型可能只存在于某个条件目录的程序集里（Cinders 的 EmbergardenCE 就是），
            // 而反编译产物的路径上一点条件的痕迹都没有——见 AppConfig.DecompiledConditionalAreas。
            var typeConditional = new ConditionalReport(_conditional);

            var outlinesShown = 0;
            foreach (var entry in csharpPaths.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (outlinesShown >= MaxOutlinedFiles)
                {
                    sb.AppendLine(
                        $"\n**Also declared in** `{entry.Item}`{typeConditional.Tag(entry.Item)}"
                        + $"{SourceLabeling.Label(entry.SourceName)} "
                        // 两支出路必须各自带全自己的限定。原先是「narrow scope to this source,
                        // or use read_code extractClass」——第一支自带限定（narrow **scope**），
                        // 第二支没有，而不锁源的 read_code 按 scope 里排在前面的源取，拿回的正是
                        // 上面刚完整列过的那一份。第十二轮盲测里被测方是把第一支的限定顺延到第二支
                        // 才没走错。路径就在同一行的反引号里，指过去比让读者自己想起来便宜。
                        + "— outline omitted; narrow scope to this source, or pass this path to read_code "
                        + "with extractClass, to see it.");
                    continue;
                }

                // 分隔线画在两份大纲**之间**而不是每份之后：只有一份时（绝大多数调用）结尾那道
                // 横线分隔的是空气，读者却会把它读成「后面还有内容、被截断了」。
                sb.AppendLine(outlinesShown == 0 ? "" : "\n---\n");
                sb.AppendLine($"**Outline** (`{entry.Item}`)"
                              + $"{typeConditional.Tag(entry.Item)}{SourceLabeling.Label(entry.SourceName)}:");

                // 按状态判断而不是看正文——正文里出现 "not found" 之类的字面量是常态
                var outline = await RoslynHelper.GetClassOutlineAsync(entry.Item, name, OutlineLimit(args));
                sb.AppendLine(outline.IsOk ? outline.Content : DescribeOutlineFailure(outline.Status, name));
                outlinesShown++;
            }

            sb.Append(typeConditional.Render() ?? string.Empty);

            var typeFooter = new ScopeReport();
            typeFooter.Add(csharpPaths);
            var rendered = typeFooter.Render(scope);
            if (rendered != null) sb.Append(rendered);
            sb.Append(scopeNotice);

            return new ToolResult(sb.ToString());
        }

        // 「scope 内找不到」和「根本不存在」是两件事，混为一谈会让调用方断言符号不存在。
        // 上面那次 GetPathsByType(name, scope) 的 OutOfScope 已经是按 OutOfScopeLabel 归好类的
        // 落选来源，不必再查一遍索引——原先另外两次查询给出的是同一份数据。
        var elsewhere = new List<string>(lookup.OtherSources);
        elsewhere.AddRange(csharpPaths.OutOfScope.Select(x => x.Source));

        var distinctElsewhere = elsewhere
            .Where(source => !string.IsNullOrEmpty(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 这两条保持 isError=true：inspect 要的是精确名，传一个索引里没有的名字属于参数给错，
        // 而不是「搜了一圈没命中」。locate 的零命中是后者，故只有那一处改成了 false。
        if (distinctElsewhere.Count > 0)
        {
            return new ToolResult(
                $"'{name}' not found in scope '{scope.Expression}', but it exists in: {string.Join(", ", distinctElsewhere)}.\n" +
                $"Retry with scope:'{distinctElsewhere[0]}' or scope:'{ScopeCatalog.EverythingKeyword}'." +
                scopeNotice,
                true);
        }

        return new ToolResult(
            $"'{name}' not found. Use locate to find the exact name (matching ignores case, but the whole name is required)." +
            scopeNotice,
            true);
    }

    // 「以 Class / Worker 结尾的标签，其值是个类型名」是启发式，而 RimWorld 的 XML 里
    // 一样有以 Worker 结尾却装着数字的标签——技能需求 `<BasicWorker>3</BasicWorker>` 就让
    // Linked C# Types 里凭空多出一条 `3 (not indexed)`。类型名至少得是个 C# 标识符：
    // 首字符是字母或下划线，其余只能是标识符字符、命名空间点号或泛型记号。
    internal static bool LooksLikeTypeNameForTests(string value) => LooksLikeTypeName(value);

    private static bool LooksLikeTypeName(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!char.IsLetter(value[0]) && value[0] != '_') return false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '<' or '>' or ',' or '+' or ' ') continue;
            return false;
        }

        return true;
    }

    // GetInheritanceChain 给的是自下而上的 (child, parent) 对，且 child 全限定、parent 短名，
    // 直接 join 会把每个中间类型印两遍。取首个 child 再顺次取各 parent 即还原成一条线。
    // 命名空间在标题行已经给过，链上一律用短名——链读的是形状，短名也正是调用方转手
    // 喂回 locate/read_code 的那个写法。相邻两对接不上时（索引里同名类型解析歧义）
    // 把断点处的 child 也印出来，而不是悄悄接成一条假链。
    private static List<string> LinearChain(IReadOnlyList<(string Child, string Parent)> chain)
    {
        var names = new List<string> { ShortTypeName(chain[0].Child) };
        for (var i = 0; i < chain.Count; i++)
        {
            var child = ShortTypeName(chain[i].Child);
            if (i > 0 && !string.Equals(child, names[^1], StringComparison.OrdinalIgnoreCase))
                names.Add(child);
            names.Add(ShortTypeName(chain[i].Parent));
        }
        return names;
    }

    private static string ShortTypeName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    // 索引查找一律 OrdinalIgnoreCase，于是 inspect('compshield') 命中 CompShield 却把标题写成
    // 'compshield'——调用方会拿这个错拼当真实类型名继续喂给 read_code / trace。索引没有「按名
    // 取规范拼写」的入口，只能借模糊搜索：完全一致（忽略大小写）恒为满分，必在最前几条。
    // 这里只是纠正拼写、不换名字形态，与 scope 无关，故用全域查，免得类型在别的源里
    // 也有定义时被 scope 过滤掉、白白退回原样。取不到就原样返回，宁可不改也不改错。
    private string CanonicalTypeName(string name)
    {
        var matches = _sourceIndexer.FuzzySearchTypes(name, _scopeCatalog.Everything, limit: 5);
        return matches.Items
            .Select(entry => entry.Item)
            .FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            ?? name;
    }
}
