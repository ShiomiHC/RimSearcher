using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class LocateTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;
    private readonly ScopeCatalog _scopeCatalog;

    public LocateTool(SourceIndexer sourceIndexer, DefIndexer defIndexer, ScopeCatalog scopeCatalog)
    {
        _sourceIndexer = sourceIndexer;
        _defIndexer = defIndexer;
        _scopeCatalog = scopeCatalog;
    }

    public string Name => "rimworld-searcher__locate";

    public string Description =>
        "Fuzzy locate RimWorld C# types/members and XML defs. Supports filters: type:, method:, field:, def:, and a scope parameter.";

    public object JsonSchema => new
    {
        type = "object",
        properties = new
        {
            query = new
            {
                type = "string",
                minLength = 1,
                description =
                    "Search text or filtered query. Examples: 'Apparel_ShieldBelt', 'RimWorld.Pawn', 'def:Apparel_ShieldBelt', 'method:CompTick'."
            },
            scope = ScopeArgs.ScopeSchemaProperty(_scopeCatalog),
            limit = ScopeArgs.LimitSchemaProperty()
        },
        required = new[] { "query" }
    };

    private static readonly ToolArgSpec ArgSpec = new(
        "rimworld-searcher__locate",
        "query (search text, optionally filtered: 'def:Apparel_ShieldBelt', 'method:CompTick'). Aliases accepted: name, symbol, pattern, search.",
        "query (required), scope, limit.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var rawQuery = ToolArgs.GetRequiredString(args, ArgSpec, "query", "name", "symbol", "pattern", "search");

        cancellationToken.ThrowIfCancellationRequested();

        var query = QueryParser.Parse(rawQuery);

        // 'scope:xxx' 混进 query 是必然会发生的（调用方已经在用 type:/def: 前缀），
        // 这里把它当作 scope 参数吸收，而不是让它变成一个搜不到东西的关键词。
        var scope = query.ScopeFilter != null
            ? _scopeCatalog.Resolve(query.ScopeFilter)
            : ScopeArgs.Resolve(_scopeCatalog, args);
        var limit = ScopeArgs.GetDisplayLimit(args);

        var report = new ScopeReport();
        var sb = new StringBuilder();
        sb.AppendLine($"## '{rawQuery}'" + (scope.IncludesEverything ? "" : $" _(scope: {scope.Expression})_"));

        if (query.TypeFilter != null || (string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter) && string.IsNullOrEmpty(query.DefFilter)))
        {
            var typeSearchTerm = query.TypeFilter ?? QueryParser.GetCombinedSearchTerm(query);
            var types = CollapseTypeAliases(_sourceIndexer.FuzzySearchTypes(typeSearchTerm, scope, limit));
            report.Add(types);

            if (types.Items.Count > 0)
            {
                sb.AppendLine("\n**C# Types:**");
                foreach (var entry in types.Items)
                {
                    var paths = _sourceIndexer.GetPathsByType(entry.Item);
                    var firstPath = paths.FirstOrDefault() ?? "unknown";
                    var fileName = Path.GetFileName(firstPath);
                    sb.AppendLine($"- `{entry.Item}` ({entry.Score:F0}%) - {fileName}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(types);
                if (fold != null) sb.AppendLine(fold);
            }
        }

        if (query.MethodFilter != null || query.FieldFilter != null || query.Keywords.Count > 0)
        {
            var keywords = new List<string>();
            if (query.MethodFilter != null) keywords.Add(query.MethodFilter);
            if (query.FieldFilter != null) keywords.Add(query.FieldFilter);
            keywords.AddRange(query.Keywords);

            // 成员按 method/property/field 分组显示，每组各给一份配额，故这里要多取一些
            var members = _sourceIndexer.SearchMembersByKeywords(
                keywords.ToArray(), scope, limit == 0 ? 0 : limit * 3);
            report.Add(members);

            if (members.Items.Count > 0)
            {
                sb.AppendLine("\n**Members:**");

                var perGroup = limit == 0 ? int.MaxValue : Math.Max(3, limit / 2);
                var groupedMembers = members.Items.GroupBy(m => m.Item.MemberType).ToList();

                foreach (var group in groupedMembers)
                {
                    var groupItems = group.ToList();
                    sb.AppendLine($"  {group.Key}s:");
                    foreach (var entry in groupItems.Take(perGroup))
                    {
                        var (typeName, memberName, _, filePath) = entry.Item;
                        sb.AppendLine(
                            $"  - `{typeName}.{memberName}` ({entry.Score:F0}%) - {Path.GetFileName(filePath)}{ScopeArgs.Label(entry.SourceName)}");
                    }
                    if (groupItems.Count > perGroup)
                        sb.AppendLine($"    ... +{groupItems.Count - perGroup} more");
                }
            }
        }

        if (query.DefFilter != null || (string.IsNullOrEmpty(query.TypeFilter) && string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter)))
        {
            var defSearchTerm = query.DefFilter ?? QueryParser.GetCombinedSearchTerm(query);
            var defs = _defIndexer.FuzzySearch(defSearchTerm, scope, limit);
            report.Add(defs);

            if (defs.Items.Count > 0)
            {
                sb.AppendLine("\n**XML Defs:**");
                foreach (var entry in defs.Items)
                {
                    var def = entry.Item;
                    var abstractTag = def.IsAbstract ? " [Abstract]" : "";
                    var label = !string.IsNullOrEmpty(def.Label) ? $" \"{def.Label}\"" : "";
                    sb.AppendLine(
                        $"- `{def.DefName}` ({entry.Score:F0}%) - {def.DefType}{abstractTag}{label}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(defs, indent: "  ");
                if (fold != null) sb.AppendLine(fold);
            }

            if (query.Keywords.Count > 0)
            {
                var defsByContent = _defIndexer.SearchByContent(query.Keywords.ToArray(), scope, limit);
                report.Add(defsByContent);

                if (defsByContent.Items.Count > 0)
                {
                    sb.AppendLine("\n**Content Matches:**");

                    foreach (var entry in defsByContent.Items)
                    {
                        var (location, matchedFields) = entry.Item;
                        var fieldSummary = string.Join(", ", matchedFields.Take(3));
                        var moreFields = matchedFields.Count > 3 ? $" +{matchedFields.Count - 3}" : "";
                        sb.AppendLine($"- `{location.DefName}` - {fieldSummary}{moreFields}{ScopeArgs.Label(entry.SourceName)}");
                    }

                    var fold = ScopeArgs.FoldLine(defsByContent, indent: "  ");
                    if (fold != null) sb.AppendLine(fold);
                }
            }
        }

        bool hasResults = sb.Length > rawQuery.Length + 10 + scope.Expression.Length;
        if (!hasResults)
        {
            var files = _sourceIndexer.Search(rawQuery, scope, limit);
            report.Add(files);

            if (files.Items.Count > 0)
            {
                sb.AppendLine("\n**Files:**");
                foreach (var entry in files.Items)
                {
                    sb.AppendLine($"- {Path.GetFileName(entry.Item)} - {entry.Item}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(files);
                if (fold != null) sb.AppendLine(fold);
                hasResults = true;
            }
        }

        var footer = report.Render(scope);

        if (!hasResults)
        {
            var message = new StringBuilder($"No results for '{rawQuery}' in scope '{scope.Expression}'.");
            if (footer != null) message.Append(footer);
            message.Append("\n\nTry: partial names, query filters (type:, method:, field:, def:), or search_regex for patterns.");
            return Task.FromResult(new ToolResult(message.ToString(), true));
        }

        if (footer != null) sb.Append(footer);

        return Task.FromResult(new ToolResult(sb.ToString()));
    }

    // 同一类型的短名与全名会各自命中，折叠成全名一条；折叠后仍要保留原来的来源与分数。
    private static ScopedResult<string> CollapseTypeAliases(ScopedResult<string> types)
    {
        var fullNameByShortName = types.Items
            .Where(t => t.Item.Contains('.'))
            .GroupBy(
                t => t.Item[(t.Item.LastIndexOf('.') + 1)..],
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Item.Length)
                    .First()
                    .Item,
                StringComparer.OrdinalIgnoreCase);

        var collapsed = types.Items
            .Select(entry =>
            {
                var canonicalName = entry.Item.Contains('.')
                    ? entry.Item
                    : fullNameByShortName.TryGetValue(entry.Item, out var fullName)
                        ? fullName
                        : entry.Item;

                return new { CanonicalName = canonicalName, entry.Score, entry.SourceName };
            })
            .GroupBy(x => x.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Score).First();
                return new ScopedEntry<string>(g.Key, best.Score, best.SourceName);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Length)
            .ToList();

        var collapsedAway = types.Items.Count - collapsed.Count;

        return new ScopedResult<string>(
            collapsed,
            Math.Max(collapsed.Count, types.TotalInScope - collapsedAway),
            types.OutOfScope,
            types.TruncatedByScoreGap);
    }
}
