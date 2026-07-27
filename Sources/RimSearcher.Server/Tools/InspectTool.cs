using System.Text;
using System.Text.Json;
using RimSearcher.Core;

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

    public InspectTool(
        SourceIndexer sourceIndexer,
        DefIndexer defIndexer,
        ScopeCatalog scopeCatalog,
        LocalizationIndex? localization = null)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
        _localization = localization;
    }

    public string Name => "rimworld-searcher__inspect";

    public string Description =>
        "Full detail for one exactly-named def or C# type; no fuzzy matching. " +
        "Def mode returns the XML merged down the whole ParentName chain — the complete effective definition, which no single XML file contains — plus the C# classes referenced from it. " +
        "Type mode returns the base-class chain (interfaces are not on it — use trace mode:'inheritors' for those) "
        + "and a member outline of fields, properties and methods; constructors, indexers and operators are not "
        + "outlined but read_code can still read them by name. Enums are outlined as their values, delegates as "
        + "their signature. The outline lists at most 40 members per kind, and when several sources declare the "
        + "same type only the highest-priority one is outlined; both cuts are stated inline where they happen. "
        + "Method bodies come from read_code.";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__inspect",
        "name (an exact DefName or C# type name, e.g. 'Apparel_ShieldBelt' / 'CompShield'). Aliases accepted: query, defName, typeName, symbol.",
        "name (required), defType (which def type to show when several share the name), xmlStartLine (continue reading a long merged XML), scope.",
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
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog)
        },
        required = new[] { "name" }
    };

    // 大纲取不到时说清是哪一种取不到：文件没了 / 文件太大不解析 / 文件里确实没这个类型
    // （最后一种通常意味着索引落后于磁盘，比如源刚被重新同步过）。
    // 译文描述里换行是常态（多段落说明），单行摆进头部区域会把格式冲散
    private static string Truncate(string text, int limit)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= limit ? single : single[..limit] + "…";
    }

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
        sb.AppendLine(startLine > 0
            ? $"\n**Resolved XML** (lines {startLine}-{Math.Min(startLine + XmlWindowLines - 1, xmlLines.Length)} of {xmlLines.Length}):"
            : "\n**Resolved XML:**");

        // 明确点名续读要回到 inspect，且说清 File: 那一行不是这份 XML 的来源
        string ContinueHint(int nextStart) =>
            $"(Full merged XML: {xmlLines.Length} lines. This is the merge of the whole ParentName chain, so it is "
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
                : $"(End of the merged XML, {xmlLines.Length} lines total.)");
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
        sb.AppendLine($"\n... [Truncated {xmlLines.Length - XmlWindowLines} lines: {XmlHeadLines + 1}-{xmlLines.Length - XmlTailLines}] ...\n");
        for (int i = xmlLines.Length - XmlTailLines; i < xmlLines.Length; i++) sb.AppendLine(xmlLines[i]);
        sb.AppendLine("```");
        sb.AppendLine(ContinueHint(XmlHeadLines + 1));
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var name = ToolArgs.StripLocateFilterPrefix(
            ToolArgs.GetRequiredFuzzyString(args, ArgSpec, "name", "query", "defName", "typeName", "symbol"));

        var scope = ScopeArgs.Resolve(_scopeCatalog, args);

        // 拼错的 scope 被静默退回全域，两个分支的每条返回路径都要带上这行，
        // 否则调用方会把全域结果当成自己限定过的范围内结果。
        var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();

        // 不收 'type' 作别名：本服务器里 'type' 到处都是 C# 类型名（read_code 的 className、
        // inspect 自己 name 支持的 'type:' 前缀），收了它等于把一个常见的误用变成一条假告警。
        var requestedDefType = ToolArgs.GetOptionalString(args, "defType", "defTypeName");
        var lookup = _defIndexer.Lookup(name, scope, defType: requestedDefType);
        var def = lookup.Location;
        if (def != null)
        {
            // 标题回显索引里的 DefName，而不是调用方传进来的拼写：查找是 OrdinalIgnoreCase 的，
            // 原样回显等于把调用方的错拼盖章成真实 defName，它会照着这个名字继续往下查。
            // 下面的 Type / File / C# Class 本来就全部取自 def，标题跟着它才是一致的。
            sb.AppendLine($"## Def: {def.DefName}");
            sb.AppendLine($"Type: {def.DefType}");

            // 译文只作为附注，下面的 Resolved XML 一个字不动——那是游戏真实数据，
            // 把译名掺进去会毁掉它「照着它就能改 mod」的用途。
            var localized = _localization?.Lookup(def.DefType, def.DefName);
            if (!string.IsNullOrEmpty(localized?.Label))
                sb.AppendLine($"Localized: {localized.Label}");
            if (!string.IsNullOrEmpty(localized?.Description))
                sb.AppendLine($"Localized description: {Truncate(localized.Description, LocalizedDescriptionLimit)}");

            var sourceName = scope.SourceNameOf(def.FilePath);
            if (!string.IsNullOrEmpty(sourceName)) sb.AppendLine($"Source: {sourceName}");

            var typePaths = _sourceIndexer.GetPathsByType(def.DefType);
            if (typePaths.Count > 0)
                sb.AppendLine($"C# Class: `{def.DefType}` ({string.Join(", ", typePaths.Select(Path.GetFileName))})");

            sb.AppendLine($"File: `{def.FilePath}`");

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

            // 传 def 而不是 name：上面已经用 defType 消过歧，按名字重查会落回默认胜者，
            // 表头说的是 ThingDef、正文却给出 BodyDef 的 XML。
            var resolvedXml = await XmlInheritanceHelper.ResolveDefXmlElementAsync(def, _defIndexer, scope);
            if (resolvedXml == null)
            {
                sb.AppendLine("\n**Resolved XML:** Failed to load Def XML");
                sb.Append(scopeNotice);
                return new ToolResult(sb.ToString());
            }

            var resolvedXmlStr = resolvedXml.ToString();
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
                    var typesArray = foundTypes.Take(10).ToArray();
                    foreach (var cls in typesArray)
                    {
                        var paths = _sourceIndexer.GetPathsByType(cls);
                        if (paths.Count > 0)
                            sb.AppendLine($"- `{cls}` ({string.Join(", ", paths.Select(Path.GetFileName))})");
                        else
                            sb.AppendLine($"- `{cls}` (not indexed)");
                    }
                    if (foundTypes.Count > 10)
                        sb.AppendLine($"  ... +{foundTypes.Count - 10} more types (use locate to find them)");
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

            var chain = _sourceIndexer.GetInheritanceChain(name);
            if (chain.Count > 0)
            {
                sb.AppendLine("\n**Inheritance:**");
                sb.AppendLine("```mermaid\ngraph TD");
                foreach (var (child, parent) in chain) sb.AppendLine($"    {child} --> {parent}");
                sb.AppendLine("```\n");
            }

            // 同名类型在 vanilla 与各 mod 里各有一份是常态（HAR 之类的前置尤其如此），
            // 每份都全量渲染一次大纲，体积就按文件数线性放大。读者要的通常是作用域里
            // 优先级最高的那一份——Items 已按该顺序排好——其余只报路径，真要看就收窄
            // scope 或用 read_code extractClass 点名去取。
            var outlinesShown = 0;
            foreach (var entry in csharpPaths.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (outlinesShown >= MaxOutlinedFiles)
                {
                    sb.AppendLine(
                        $"**Also declared in** `{entry.Item}`{ScopeArgs.Label(entry.SourceName)} "
                        + "— outline omitted; narrow scope to this source, or use read_code extractClass, to see it.");
                    continue;
                }

                sb.AppendLine($"**Outline** (`{entry.Item}`){ScopeArgs.Label(entry.SourceName)}:");

                // 按状态判断而不是看正文——正文里出现 "not found" 之类的字面量是常态
                var outline = await RoslynHelper.GetClassOutlineAsync(entry.Item, name);
                sb.AppendLine(outline.IsOk ? outline.Content : DescribeOutlineFailure(outline.Status, name));
                sb.AppendLine("---");
                outlinesShown++;
            }

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
