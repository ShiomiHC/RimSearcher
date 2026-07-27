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

    public InspectTool(SourceIndexer sourceIndexer, DefIndexer defIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__inspect";

    public string Description =>
        "Inspect a RimWorld def or C# type. Def mode resolves inherited XML; type mode shows inheritance and outline.";

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__inspect",
        "name (an exact DefName or C# type name, e.g. 'Apparel_ShieldBelt' / 'CompShield'). Aliases accepted: query, defName, typeName, symbol.",
        "name (required), scope.",
        "A 'def:'/'type:' prefix is stripped automatically. Names are case-sensitive — use rimworld-searcher__locate if unsure of the exact spelling.");

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            name = new
            {
                type = "string",
                minLength = 1,
                description = "Exact DefName or C# type name. Examples: 'Apparel_ShieldBelt', 'CompShield'. Aliases 'query'/'defName'/'typeName' are also accepted; a 'def:'/'type:' prefix is stripped."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog)
        },
        required = new[] { "name" }
    };

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

    public async Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var name = ToolArgs.StripLocateFilterPrefix(
            ToolArgs.GetRequiredString(args, ArgSpec, "name", "query", "defName", "typeName", "symbol"));

        var scope = ScopeArgs.Resolve(_scopeCatalog, args);

        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();

        var lookup = _defIndexer.Lookup(name, scope);
        var def = lookup.Location;
        if (def != null)
        {
            sb.AppendLine($"## Def: {name}");
            sb.AppendLine($"Type: {def.DefType}");

            var sourceName = scope.SourceNameOf(def.FilePath);
            if (!string.IsNullOrEmpty(sourceName)) sb.AppendLine($"Source: {sourceName}");

            var typePaths = _sourceIndexer.GetPathsByType(def.DefType);
            if (typePaths.Count > 0)
                sb.AppendLine($"C# Class: `{def.DefType}` ({string.Join(", ", typePaths.Select(Path.GetFileName))})");

            sb.AppendLine($"File: `{def.FilePath}`");

            // 同名 def 散在多处时必须说清取的是哪一个，否则读者会把这份 XML 当成唯一定义
            if (lookup.AmbiguousInScope)
                sb.AppendLine($"_Note: {lookup.InScopeCount} defs share this name within scope '{scope.Expression}'; showing the highest-priority one._");
            if (lookup.OtherSources.Count > 0)
                sb.AppendLine($"_Also defined in: {string.Join(", ", lookup.OtherSources)} (outside scope '{scope.Expression}')._");

            var resolvedXml = await XmlInheritanceHelper.ResolveDefXmlElementAsync(name, _defIndexer, scope);
            if (resolvedXml == null)
            {
                sb.AppendLine("\n**Resolved XML:** Failed to load Def XML");
                return new ToolResult(sb.ToString());
            }

            var resolvedXmlStr = resolvedXml.ToString();
            sb.AppendLine("\n**Resolved XML:**");

            var xmlLines = resolvedXmlStr.Split('\n');
            if (xmlLines.Length > 300)
            {
                sb.AppendLine("```xml");
                for (int i = 0; i < 200; i++) sb.AppendLine(xmlLines[i]);
                sb.AppendLine($"\n... [Truncated {xmlLines.Length - 250} lines] ...\n");
                for (int i = xmlLines.Length - 50; i < xmlLines.Length; i++) sb.AppendLine(xmlLines[i]);
                sb.AppendLine("```");
                sb.AppendLine($"(Full XML: {xmlLines.Length} lines, use read_code on file path above)");
            }
            else
            {
                sb.AppendLine("```xml");
                sb.AppendLine(resolvedXmlStr);
                sb.AppendLine("```");
            }

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
                        if (!string.IsNullOrEmpty(val)) foundTypes.Add(val);
                    }

                    var classAttr = el.Attribute("Class");
                    if (classAttr != null)
                    {
                        var val = classAttr.Value.Trim();
                        if (!string.IsNullOrEmpty(val)) foundTypes.Add(val);
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

            return new ToolResult(sb.ToString());
        }

        var csharpPaths = _sourceIndexer.GetPathsByType(name, scope);
        if (csharpPaths.Items.Count > 0)
        {
            sb.AppendLine($"## C# Type: {name}");

            var chain = _sourceIndexer.GetInheritanceChain(name);
            if (chain.Count > 0)
            {
                sb.AppendLine("\n**Inheritance:**");
                sb.AppendLine("```mermaid\ngraph TD");
                foreach (var (child, parent) in chain) sb.AppendLine($"    {child} --> {parent}");
                sb.AppendLine("```\n");
            }

            foreach (var entry in csharpPaths.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sb.AppendLine($"**Outline** (`{entry.Item}`){ScopeArgs.Label(entry.SourceName)}:");

                // 按状态判断而不是看正文——正文里出现 "not found" 之类的字面量是常态
                var outline = await RoslynHelper.GetClassOutlineAsync(entry.Item, name);
                sb.AppendLine(outline.IsOk ? outline.Content : DescribeOutlineFailure(outline.Status, name));
                sb.AppendLine("---");
            }

            var typeFooter = new ScopeReport();
            typeFooter.Add(csharpPaths);
            var rendered = typeFooter.Render(scope);
            if (rendered != null) sb.Append(rendered);

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

        if (distinctElsewhere.Count > 0)
        {
            return new ToolResult(
                $"'{name}' not found in scope '{scope.Expression}', but it exists in: {string.Join(", ", distinctElsewhere)}.\n" +
                $"Retry with scope:'{distinctElsewhere[0]}' or scope:'{ScopeCatalog.EverythingKeyword}'.",
                true);
        }

        return new ToolResult(
            $"'{name}' not found. Use locate tool first to find exact names (case-sensitive).",
            true);
    }
}
