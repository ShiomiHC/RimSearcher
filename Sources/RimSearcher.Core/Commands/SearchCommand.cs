using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class SearchCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("def");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("def");

    public override CliOperation Operation { get; } = new()
    {
        Name = "search",
        Aliases = ["find-def", "s"],
        Summary = "按名字、label、description 或译文查找 def。",
        Remarks =
            "每个查询词按词首匹配；只给一个含下划线或内部大写的词时，另在 def 名中段匹配。",
        Positionals = [new CliPositional { Name = "query", Help = "单词、def 名或其一部分。" }],
        Options = [LimitOption, OffsetOption, CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher search shield",
            "rimsearcher search \"psychic shock\" --type ThingDef",
            "rimsearcher search CompShield --scope all,-vanilla",
        ],
        JsonKeys =
        [
            new("defs",
                "每个匹配的 def 一行：def_name、def_type、label、matched_on、declared_in。",
                Rows: true),
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
        ],
    };

    public override bool ReadsTranslations => true;

    public override int Run(CommandContext ctx)
    {
        var query = ctx.Args.Positional(0)!;
        var limit = ctx.Limit(LimitOption);
        var scope = ctx.Scope();
        var type = ctx.Args.Value(CommonOptions.Type);

        var offset = ctx.Args.Offset(OffsetOption);

        var (rows, total) = ctx.Db.SearchFts(query, scope, type, limit.Effective, offset);
        var ftsTotal = total;
        var how = "full-text";
        var addedBySubstring = 0;

        if (IsCompoundToken(query))
        {
            var (more, extra) = ctx.Db.ContainingUnmatched(query, scope, type, Math.Max(0, limit.Effective - rows.Count),
                                                           Math.Max(0, offset - ftsTotal));
            total += extra;
            rows = [.. rows, .. more];
            addedBySubstring = more.Count;
        }

        if (query.Length == 0)
            throw new CliUsageException(
                "search 须给出查询词。逐个列出某类型的 def 见 'rimsearcher list <DefType>'。");
        var nothingToMatch = FtsText.HasNothingToMatch(query);
        if (nothingToMatch)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{query}' 不含字母或数字。");
        else if (query.Contains('*', StringComparison.Ordinal))
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{query}' 实际按 {NameList.Render(FtsText.EffectiveTerms(query), Limits.MaxSuggestions)} 查询。" +
                "这里的 '*' 不是通配符。");

        if (total == 0)
        {
            var (byOriginal, originals) = ctx.Db.ByTranslationOriginal(query, scope, type, limit.Effective, offset);
            (rows, total) = (byOriginal, originals);
            if (rows.Count > 0)
            {
                how = "translation original";
                ctx.Report.Notice(NoticeKind.Boundary,
                    (nothingToMatch
                        ? ""
                        : $"本快照中没有名字、label 或译文按词首匹配 '{query}'；") +
                    "以下 def 在被译文替换的原文中含有该查询词。");
            }
        }

        if (total == 0)
        {
            var names = ctx.Db.AllDefNames(scope, type).Distinct(StringComparer.Ordinal).ToList();
            var (bare, kind) = FuzzyMatcher.StripKindPrefix(query);
            var ranked = FuzzyMatcher.Rank(names, bare).Select(t => t.Text).ToList();
            if (ranked.Count > 0)
            {
                (rows, total) = ctx.Db.ByNames(ranked, scope, type, limit.Effective, offset);
                how = kind is null ? "fuzzy" : $"fuzzy (ignoring the '{kind}:' prefix, which defs do not use)";
                if (rows.Count > 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                    $"没有 def 与 '{query}' 原样匹配；以下为拼写最接近的名字。");
            }
        }

        if (rows.Count > 0)
        {
            ctx.Report.PageNotice("def", rows.Count, offset, Math.Max(total, offset + rows.Count));

            if (addedBySubstring > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"其中 {Tally.Complete(addedBySubstring).Render("def")} 因名字中含有子串 '{query}' 而列入。");
        }
        if (rows.Count == 0 && offset > 0)
        {
            ctx.Report.PastEnd(offset, $"'{query}' 共匹配 {Tally.Complete(total).Render("def")}。");
        }
        else if (rows.Count == 0)
        {
            if (ctx.UnknownDefType(type) is { } unknown) ctx.Report.Notice(NoticeKind.Filter, unknown);
            ctx.Report.Notice(NoticeKind.NextStep,
                (scope.IsAll ? "本快照中" : $"本快照中 --scope {scope.Expression} 范围内") +
                $"没有与 '{query}' 匹配的结果。");

            if (!nothingToMatch)
            {
                var causes = new List<EmptyCause>();
                if (!scope.IsAll)
                    causes.Add(new(ctx.FilterAsGiven(CommonOptions.Scope), CountIn(ctx, query, ctx.Unscoped(), type), ctx.Without(CommonOptions.Scope)));
                if (type is not null)
                    causes.Add(new(ctx.FilterAsGiven(CommonOptions.Type), CountIn(ctx, query, scope, null), ctx.Without(CommonOptions.Type)));
                ctx.Report.EmptyBecause(causes);
            }

            var sighting = NameLookup.Locate(ctx, query, scope);
            var looksLikeClass = ClassNameShape.Looks(query);
            if (sighting is not null) NameLookup.Say(ctx, sighting);
            else if (looksLikeClass)
                ctx.Report.Notice(NoticeKind.NextStep,
                    "本快照的 def 类型、Class 与 mod 中都没有这个名字。");
        }

        ctx.AnnounceExcluded(scope, rest => CountIn(ctx, query, rest, type), "def");

        ctx.Report.Table("defs", ["def_name", "def_type", "label", "matched_on", "declared_in"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["label"] = r.Label,
                ["matched_on"] = how.StartsWith("fuzzy", StringComparison.Ordinal)
                                 ? "近似拼写"
                                 : MatchedOn(ctx, r, query),
                ["declared_in"] = r.SourceMod,
            }).ToList());

        Advisory.NoteSameLabel(ctx, rows);
        return rows.Count == 0 ? 1 : 0;
    }

    private static int CountIn(CommandContext ctx, string query, ScopeFilter scope, string? type)
    {
        var (_, total) = ctx.Db.SearchFts(query, scope, type, 0, 0);
        if (IsCompoundToken(query)) total += ctx.Db.ContainingUnmatched(query, scope, type, 0).Total;
        if (total == 0) total = ctx.Db.ByTranslationOriginal(query, scope, type, 0).Total;
        return total;
    }

    private static string MatchedOn(CommandContext ctx, DefRow r, string query)
    {
        bool Has(string? s) => s is { Length: > 0 } && s.Contains(query, StringComparison.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (Has(r.DefName)) parts.Add("defName");
        if (Has(r.Label)) parts.Add("label");
        if (Has(r.Description)) parts.Add("description");
        if (parts.Count > 0) return string.Join("+", parts);

        var all = ctx.Db.Translations(r.DefName);
        var t = all.Where(x => x.DefType is null || DefTypes.Same(x.DefType, r.DefType))
                   .FirstOrDefault(x => Has(x.Translated) || Has(x.Original));
        if (t is not null) return GetCommand.OutsideSnapshot(t) ? $"{t.Path}（文件{GetCommand.NotEnabled}）" : t.Path;

        if (all.Any(x => Has(x.Translated) || Has(x.Original))) return "同名 def 的译文";

        return "索引文本";
    }

    private static bool IsCompoundToken(string q)
        => q.Length > 2 && !q.Any(char.IsWhiteSpace) &&
           (q.Contains('_') || q.Skip(1).Any(char.IsUpper));
}
