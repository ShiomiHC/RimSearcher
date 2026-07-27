using System.Text;
using System.Text.Json;
using RimSearcher.Core;

namespace RimSearcher.Server.Tools;

public class LocateTool : ITool
{
    private readonly SourceIndexer _sourceIndexer;
    private readonly DefIndexer _defIndexer;
    private readonly ScopeCatalog _scopeCatalog;
    private readonly LocalizationIndex? _localization;

    public LocateTool(
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

    public string Name => "rimworld-searcher__locate";

    public string Description =>
        "Fuzzy name lookup: turns a partial or misspelled name into the exact C# type / member / XML def name that other tools require — the only tool that accepts approximate input. " +
        "Results are split into C# Types, Members, XML Defs and Def content matches, each section capped by limit and folded independently. " +
        "Filters: type:, method:, field:, def:.";

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

        // 拼错的 scope 被静默退回全域，每条返回路径都要带上这行，
        // 否则调用方拿着全域结果却以为自己限定过范围。表头在全域时不打 scope 标注，
        // 正是这种情况下最没痕迹的地方。
        var scopeNotice = ScopeArgs.UnresolvedNotice(_scopeCatalog, scope) ?? string.Empty;

        var report = new ScopeReport();
        var sb = new StringBuilder();
        sb.AppendLine($"## '{rawQuery}'" + (scope.IncludesEverything ? "" : $" _(scope: {scope.Expression})_"));

        // 各段落自己置位。曾用 sb.Length 与表头长度比大小来推断，窄 scope 下表头恰好比
        // 阈值长，零命中也会被判成有结果——「查不到就提示换 scope」那条路径因此永远走不到。
        var hasResults = false;

        if (query.TypeFilter != null || (string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter) && string.IsNullOrEmpty(query.DefFilter)))
        {
            var typeSearchTerm = query.TypeFilter ?? QueryParser.GetCombinedSearchTerm(query);
            // 短名/全名的合并在索引层完成（见 SourceIndexer.CollapseNameAliases）——那里是截断
            // 之前，计数才对得上；在这里折叠只会把已经被 limit 砍过的一批再去一次重。
            var types = _sourceIndexer.FuzzySearchTypes(typeSearchTerm, scope, limit.Count);
            report.Add(types);

            if (types.Items.Count > 0)
            {
                hasResults = true;
                sb.AppendLine("\n**C# Types:**");
                foreach (var entry in types.Items)
                {
                    var paths = _sourceIndexer.GetPathsByType(entry.Item);
                    var firstPath = paths.FirstOrDefault() ?? "unknown";
                    var fileName = Path.GetFileName(firstPath);
                    sb.AppendLine($"- `{entry.Item}` ({entry.Score:F0}%) - {fileName}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(types, limit: limit);
                if (fold != null) sb.AppendLine(fold);
            }
        }

        if (query.MethodFilter != null || query.FieldFilter != null || query.Keywords.Count > 0)
        {
            var keywords = new List<string>();
            if (query.MethodFilter != null) keywords.Add(query.MethodFilter);
            if (query.FieldFilter != null) keywords.Add(query.FieldFilter);
            keywords.AddRange(query.Keywords);

            // 成员按 method/property/field 分组显示，每组各给一份配额，故这里要多取一些；
            // Scale 放大后仍夹在服务端硬上限内
            var members = _sourceIndexer.SearchMembersByKeywords(
                keywords.ToArray(), scope, limit.Scale(3).Count);
            report.Add(members);

            if (members.Items.Count > 0)
            {
                hasResults = true;
                sb.AppendLine("\n**Members:**");

                // 'all' 时不再按组折叠（总量已被硬上限约束住），否则每组各给一半配额
                var perGroup = limit.Unlimited ? limit.Count : Math.Max(3, limit.Count / 2);
                var groupedMembers = members.Items.GroupBy(m => m.Item.MemberType).ToList();
                var shown = 0;

                foreach (var group in groupedMembers)
                {
                    var groupItems = group.ToList();
                    sb.AppendLine($"  {Plural(group.Key)}:");
                    foreach (var entry in groupItems.Take(perGroup))
                    {
                        var (typeName, memberName, _, filePath) = entry.Item;
                        sb.AppendLine(
                            $"  - `{typeName}.{memberName}` ({entry.Score:F0}%) - {Path.GetFileName(filePath)}{ScopeArgs.Label(entry.SourceName)}");
                        shown++;
                    }
                }

                // 折叠行放在整段末尾、按 TotalInScope 计数。原先每组各打一行、只数「取回的这批里
                // 还剩几条」，而取回本身已被 limit.Scale(3) 砍过：method:CompTick 因此报 +25，
                // 实际有 186 条。组内那行还漏了「怎么拿到更多」，调用方连能展开都不知道。
                var memberFold = ScopeArgs.FoldLine(
                    Math.Max(0, members.TotalInScope - shown),
                    shown,
                    members.TruncatedByScoreGap,
                    truncatedByLimit: true,
                    indent: "  ",
                    limit: limit);
                if (memberFold != null) sb.AppendLine(memberFold);
            }
        }

        if (query.DefFilter != null || (string.IsNullOrEmpty(query.TypeFilter) && string.IsNullOrEmpty(query.MethodFilter) && string.IsNullOrEmpty(query.FieldFilter)))
        {
            var defSearchTerm = query.DefFilter ?? QueryParser.GetCombinedSearchTerm(query);
            var defs = _defIndexer.FuzzySearch(defSearchTerm, scope, limit.Count);
            report.Add(defs);

            if (defs.Items.Count > 0)
            {
                hasResults = true;
                sb.AppendLine("\n**XML Defs:**");
                foreach (var entry in defs.Items)
                {
                    var def = entry.Item;
                    var abstractTag = def.IsAbstract ? " [Abstract]" : "";
                    var label = !string.IsNullOrEmpty(def.Label) ? $" \"{def.Label}\"" : "";

                    // 译名接在英文 label 后面。locate 只给 label——description 长一到两个数量级，
                    // 一屏几十条结果每条都带上就没法看了，那是 inspect 的事。
                    var localized = _localization?.Lookup(def.DefType, def.DefName)?.Label;
                    var localizedTag = !string.IsNullOrEmpty(localized) ? $" / {localized}" : "";

                    sb.AppendLine(
                        $"- `{def.DefName}` ({entry.Score:F0}%) - {def.DefType}{abstractTag}{label}{localizedTag}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(defs, indent: "  ", limit: limit);
                if (fold != null) sb.AppendLine(fold);
            }

            if (query.Keywords.Count > 0)
            {
                var defsByContent = _defIndexer.SearchByContent(query.Keywords.ToArray(), scope, limit.Count);
                report.Add(defsByContent);

                if (defsByContent.Items.Count > 0)
                {
                    hasResults = true;
                    sb.AppendLine("\n**Content Matches:**");

                    foreach (var entry in defsByContent.Items)
                    {
                        var (location, matchedFields) = entry.Item;
                        var fieldSummary = string.Join(", ", matchedFields.Take(3));
                        var moreFields = matchedFields.Count > 3 ? $" +{matchedFields.Count - 3}" : "";
                        sb.AppendLine($"- `{location.DefName}` - {fieldSummary}{moreFields}{ScopeArgs.Label(entry.SourceName)}");
                    }

                    var fold = ScopeArgs.FoldLine(defsByContent, indent: "  ", limit: limit);
                    if (fold != null) sb.AppendLine(fold);
                }
            }
        }

        if (!hasResults)
        {
            var files = _sourceIndexer.Search(rawQuery, scope, limit.Count);
            report.Add(files);

            if (files.Items.Count > 0)
            {
                sb.AppendLine("\n**Files:**");
                foreach (var entry in files.Items)
                {
                    sb.AppendLine($"- {Path.GetFileName(entry.Item)} - {entry.Item}{ScopeArgs.Label(entry.SourceName)}");
                }

                var fold = ScopeArgs.FoldLine(files, limit: limit);
                if (fold != null) sb.AppendLine(fold);
                hasResults = true;
            }
        }

        var footer = report.Render(scope);

        if (!hasResults)
        {
            var message = new StringBuilder($"No results for '{rawQuery}' in scope '{scope.Expression}'.");
            message.Append(ScopeArgs.RetryWiderNotice(scope));
            if (footer != null) message.Append(footer);
            message.Append(scopeNotice);
            message.Append("\n\nTry: partial names, query filters (type:, method:, field:, def:), or search_regex for patterns.");

            // 零命中是一个正常结果，不是调用失败。isError 留给「工具没能执行」，置 true 会让
            // client 把这次搜索当成故障去重试或上报；同一个服务器里 trace 查不到子类、
            // search_regex 零命中都是 false，locate 独自为 true 只会让调用方两套判据。
            return Task.FromResult(new ToolResult(message.ToString()));
        }

        if (footer != null) sb.Append(footer);
        sb.Append(scopeNotice);

        return Task.FromResult(new ToolResult(sb.ToString()));
    }

    // MemberType 来自索引层，取值是 Method / Property / Field。直接加 's' 会写出 'Propertys'。
    private static string Plural(string memberType) =>
        memberType.EndsWith('y') ? $"{memberType[..^1]}ies" : $"{memberType}s";

}
