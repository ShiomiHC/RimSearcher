using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class KeyedCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("Keyed 译文");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("Keyed 译文");

    private static readonly CliOption EmptyTranslation = CliOption.Flag("empty-translation") with
    {
        Aliases = ["empty-translations", "untranslated", "todo"],
        Help = "只列出语言文件里的占位条目：键存在但没有译文，游戏因此退回英文。翻译覆盖率一类的问题用这一项；这一项不需要查询词，单独使用时筛选整层。",
        Narrows = true,
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "keyed",
        Aliases = ["ui", "ui-text", "strings", "key", "translation"],
        Summary = "查询 Keyed 键对应的界面文本，或由一段界面文本反查对应的 Keyed 键。",
        Remarks =
            "每行标为「生效」或「文件」。只有「生效」行是游戏显示的文本；" +
            "「文件」行是存在于某个 mod 语言文件中的译文，未必是游戏显示的那一条。" +
            "「占位符」列只在有占位条目时输出；该列为「是」的行只声明键、没有译文，因此无论「译文」列显示什么，游戏在该处都显示英文原文。\n\n" +
            "--scope 匹配「声明于」，即该行的语言文件所属的 mod；对「生效」行而言，即该行译文胜出的 mod。",
        Positionals =
        [
            new CliPositional
            {
                Name = "query",
                Required = false,
                Variadic = true,
                Help = "一个 Keyed 键，或快照所含任一语言中的一段界面文本。" +
                       "查询词按键前缀匹配，或按界面文本匹配。" +
                       "一次可给出几个查询词；--limit 与 --offset " +
                       "对每个查询词分别生效，每个查询词各有一行计数，「查询词」列写明每行回答的是哪一个。" +
                       "全部省略查询词时列出整层：全部 Keyed 译文，加 --empty-translation 时只列占位条目。",
            },
        ],
        Options =
        [
            LimitOption,
            OffsetOption,
            CommonOptions.Scope,
            EmptyTranslation,
        ],
        Examples =
        [
            "rimsearcher keyed CannotUseNoPower",
            "rimsearcher keyed 没有电力",
            "rimsearcher keyed Command",
            "rimsearcher keyed --empty-translation",
        ],
        JsonKeys =
        [
            new("keys",
                "每条 Keyed 译文一行：key、translated、original、origin（生效 / 文件）、placeholder、declared_in、" +
                "source，以及 query（该行回答的是哪个查询词，只有一个查询词时也有这个键）。恒为数组，单个键恰好匹配时同样如此，" +
                "因此形状不随匹配方式变化。随调用变化的只有 query 键：列出整层时不带查询词，那时各行没有这个键。" +
                "source 以「（另有 N 个文件内容相同）」结尾时，表示该 mod 另有 N 个语言文件带有相同文本，已并入这一行。",
                Rows: true),
            new("absent",
                "本次查询需要而本快照没有的层，每层一行：layer、state、next；keyed 与 disk_translations 两层都存在时为空。" +
                "快照完全没有 Keyed 译文时为 keyed 一行，state 为「空」（此时 keys 也为空）；" +
                "mod 语言文件未扫描时为 disk_translations 一行（state 为「已跳过」「未配置」或「未采集」），" +
                "此时 origin 键只有「生效」这一个取值。next 为补齐该层的命令，需要先改配置时，命令前面附上该配置改动。",
                Rows: true),
            new("siblings",
                "仅当查询词本身就是一个键、另有不超过 " + SiblingsShown + " 个键以它开头时出现：这些键的 Keyed 译文，每条一行，键与 keys 相同。"),
            EmptyCause.JsonKeyCounting("keyed translation"),
            NameLookup.JsonKey,
        ],
    };

    public override bool ReadsTranslations => true;

    public override int Run(CommandContext ctx)
    {
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in ctx.Args.Positionals)
            if (seen.Add(q)) asked.Add(q);
        var query = asked.Count > 0 ? asked[0] : null;
        var limit = ctx.Limit(LimitOption);
        var offset = ctx.Args.Offset(OffsetOption);
        var placeholdersOnly = ctx.Args.Flag(EmptyTranslation);
        var scope = ctx.Scope();

        var total = ctx.Db.KeyedCount();
        if (total == 0)
        {
            ctx.Report.Absent(DataLayers.KeyedRow(ctx.Db, ctx.SnapshotName ?? ""));
            return 1;
        }

        if (query is null)
            return RunAll(ctx, limit, offset, placeholdersOnly, scope);

        foreach (var q in asked)
            if (FtsText.HasNothingToMatch(q))
                throw new CliUsageException(
                    $"'{q}' 不含字母或数字，而只有字母与数字参与匹配。" +
                    (q.Contains('*', StringComparison.Ordinal) ? WildcardNote : "") +
                    $"不带参数的 'rimsearcher keyed' 列出全部 {Tally.Complete(total).Render("keyed translation")}。");

        var table = new List<IReadOnlyDictionary<string, object?>>();
        var answered = 0;
        var anyPlaceholder = false;
        var siblings = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var one in asked)
            if (RunOne(ctx, one, limit, offset, placeholdersOnly, scope, table, siblings, ref anyPlaceholder, asked) == 0)
                answered++;

        if (answered == 0) return 1;

        string[] query1 = asked.Count == 1 ? [] : ["query"];
        ctx.Report.Table("keys", [.. Columns(table), .. query1], table, whole: TextColumns);
        if (siblings.Count > 0)
            ctx.Report.Table("siblings", [.. Columns(siblings), .. query1], siblings,
                caption: asked.Count == 1 ? $"以 '{asked[0]}' 开头的其他键：" : "以查询词开头的其他键：", whole: TextColumns);
        AfterTable(ctx, anyPlaceholder);
        return 0;
    }

    private static int RunOne(CommandContext ctx, string query, LimitValue limit, int offset,
                              bool placeholdersOnly, ScopeFilter scope,
                              List<IReadOnlyDictionary<string, object?>> table,
                              List<IReadOnlyDictionary<string, object?>> siblingRows, ref bool anyPlaceholder,
                              IReadOnlyList<string> asked)
    {
        var single = asked.Count == 1;
        var exactAll = ctx.Db.KeyedByKey(query);
        IReadOnlyList<Storage.KeyedRow> exact = exactAll.Where(r => scope.Includes(r.SourceMod)).ToList();
        var rows = exact;
        var matchedOn = "key";
        var ftsTotal = exact.Count;
        var matchedTotal = exact.Count;
        var distinctKeys = exact.Count > 0 ? 1 : 0;

        int CountUnder(ScopeFilter? s, bool noMod) => exactAll.Count > 0
            ? exactAll.Count(r => (!placeholdersOnly || r.Placeholder) &&
                                  (noMod ? r.SourceMod is null : s is null || s.Includes(r.SourceMod)))
            : ctx.Db.KeyedSearch(query, 1, 0, placeholdersOnly, s, noMod).Total;

        if (exactAll.Count == 0)
        {
            var (hits, hitTotal, hitMatched, hitDistinct) =
                ctx.Db.KeyedSearch(query, limit.Effective, offset, placeholdersOnly, scope);
            rows = hits;
            ftsTotal = hitTotal;
            matchedTotal = hitMatched;
            distinctKeys = hitDistinct;
            matchedOn = "text";

            var terms = FtsText.EffectiveTerms(query);
            if (!string.Equals(string.Join(" ", terms), query.Trim(), StringComparison.Ordinal))
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{query}' 实际按 {NameList.Render(terms, Limits.MaxSuggestions)} 查询。" +
                    (query.Contains('*', StringComparison.Ordinal) ? WildcardNote : ""));
        }
        else if (placeholdersOnly)
        {
            rows = exact.Where(r => r.Placeholder).ToList();
            ftsTotal = rows.Count;
        }

        if (matchedOn == "key" && offset > 0)
            rows = rows.Skip(offset).ToList();

        if (rows.Count == 0)
        {
            if (offset > 0 && ftsTotal > 0)
            {
                ctx.Report.PastEnd(offset,
                    $"共有 {Tally.Complete(ftsTotal).Render("keyed translation")}匹配 '{query}'。");
                return 1;
            }

            if (!scope.IsAll && CountUnder(null, false) is var unscoped and > 0)
            {
                if (single)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), unscoped,
                                                           ctx.Without(CommonOptions.Scope)), "keyed translation");
                    return 1;
                }
                ctx.Report.Notice(NoticeKind.Filter,
                    $"--scope 排除全部匹配：'{query}' 匹配 {Tally.Complete(unscoped).Render("keyed translation")}。");
                return 1;
            }

            if (placeholdersOnly && matchedTotal > 0)
            {
                if (single)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(EmptyTranslation), matchedTotal,
                                                           ctx.Without(EmptyTranslation)), "keyed translation");
                    return 1;
                }
                ctx.Report.Notice(NoticeKind.Filter,
                    $"--empty-translation 排除全部匹配：'{query}' 匹配 {Tally.Complete(matchedTotal).Render("keyed translation")}。");
                return 1;
            }

            var close = Suggestion.Closest(ctx.Db.AllKeyedKeys(), query);

            ctx.Report.Notice(NoticeKind.NextStep,
                $"没有 Keyed 译文匹配 '{query}'。" + Suggestion.Say(close));

            var defs = ctx.Db.GetDefsNamed(query);
            if (defs.Count > 0)
            {
                NameLookup.Say(ctx, NameLookup.AsDef(query, defs));
                return 1;
            }

            var sighting = NameLookup.Locate(ctx, query);
            if (sighting is not null)
                NameLookup.Say(ctx, sighting);
            return 1;
        }

        var shown = limit.IsAll ? rows : rows.Take(limit.Effective).ToList();

        var unit = matchedOn == "text" && distinctKeys == ftsTotal ? "key" : "keyed translation";
        ctx.Report.PageNotice(unit, shown.Count, offset, ftsTotal, $"'{query}'");
        if (matchedOn == "text" && distinctKeys < ftsTotal)
            ctx.Report.Notice(NoticeKind.Count,
                Cjk.Join($"'{query}' 的", Tally.Complete(ftsTotal).Render("keyed translation"),
                         "分属", Tally.Complete(distinctKeys).Render("key")) + "。");
        AnnounceOutOfScope(ctx, scope, CountUnder);
        foreach (var r in shown)
        {
            var row = RowOf(r);
            row["query"] = query;
            table.Add(row);
            anyPlaceholder |= r.Placeholder;
        }

        if (matchedOn == "key")
        {
            var (siblings, siblingTotal) = ctx.Db.KeyedPrefixSiblings(query, SiblingsShown);
            if (siblingTotal <= SiblingsShown)
                foreach (var r in siblings.Where(k => !asked.Contains(k, StringComparer.OrdinalIgnoreCase))
                                          .SelectMany(ctx.Db.KeyedByKey)
                                          .Where(r => scope.Includes(r.SourceMod) && (!placeholdersOnly || r.Placeholder)))
                {
                    var row = RowOf(r);
                    row["query"] = query;
                    siblingRows.Add(row);
                    anyPlaceholder |= r.Placeholder;
                }
            else
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{query}' 本身就是一个键，因此上面的回答只有这一个键。" +
                    $"本层另有 {Tally.Complete(siblingTotal).Render("key")}以 '{query}' 开头" +
                    $"（{NameList.Render(siblings, Limits.MaxSuggestions)}）。");
        }

        if (matchedOn == "text" && shown.Count > 0)
        {
            var keys = shown.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
            ctx.Report.Notice(NoticeKind.NextStep, keys.Count == 1
                ? $"输出这段文本的代码见 'rimsearcher code-search \"\\\"{keys[0]}\\\"\"'（按字面量搜索该键）。"
                : "输出某一行文本的代码见 'rimsearcher code-search \"\\\"<key>\\\"\"'，<key> 为该行的键。", teach: true);
        }

        return 0;
    }

    private static int RunAll(CommandContext ctx, LimitValue limit, int offset, bool placeholdersOnly,
                              ScopeFilter scope)
    {
        var (rows, total, layerTotal) = ctx.Db.KeyedAll(limit.Effective, offset, placeholdersOnly, scope);
        int CountUnder(ScopeFilter? s, bool noMod) => ctx.Db.KeyedAll(0, 0, placeholdersOnly, s, noMod).Total;

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, placeholdersOnly
                    ? $"本快照中有 {Tally.Complete(total).Render("keyed translation")}为占位条目。"
                    : $"本快照共有 {Tally.Complete(total).Render("keyed translation")}。");
                return 1;
            }

            if (!scope.IsAll && CountUnder(null, false) is var unscoped and > 0)
            {
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), unscoped,
                                                       ctx.Without(CommonOptions.Scope)), "keyed translation");
                return 1;
            }

            ctx.Report.Notice(NoticeKind.Filter,
                $"本快照中的 {Tally.Complete(layerTotal).Render("keyed translation")}都带有实际译文，占位条目为零。" +
                "def 自身 label 与 description 的覆盖情况属另一层：它们经 DefInjected 注入，" +
                "这些内容见 'rimsearcher get <defName>'。");
            return 1;
        }

        ctx.Report.PageNotice("keyed translation", rows.Count, offset, total);
        AnnounceOutOfScope(ctx, scope, CountUnder);
        Emit(ctx, rows);
        return 0;
    }

    private static void AnnounceOutOfScope(CommandContext ctx, ScopeFilter scope, Func<ScopeFilter?, bool, int> count)
    {
        if (scope.IsAll) return;
        ctx.AnnounceExcluded(scope, rest => count(rest, false), "keyed translation");
        var noMod = count(null, true);
        if (noMod > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"没有「声明于」的 {Tally.Complete(noMod).Render("matching keyed translation")}只有 --scope all 能选中。");
    }

    private static void Emit(CommandContext ctx, IReadOnlyList<Storage.KeyedRow> shown)
    {
        var rows = shown.Select(r => (IReadOnlyDictionary<string, object?>)RowOf(r)).ToList();
        ctx.Report.Table("keys", Columns(rows), rows, whole: TextColumns);

        AfterTable(ctx, shown.Any(r => r.Placeholder));
    }

    private const int SiblingsShown = 5;

    private static readonly string[] TextColumns = ["translated", "original"];

    private static readonly string[] KeyColumns =
        ["key", "translated", "original", "origin", "placeholder", "declared_in", "source"];

    private static string[] Columns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => rows.Any(r => r["placeholder"] is true) ? KeyColumns : [.. KeyColumns.Where(c => c != "placeholder")];

    private static Dictionary<string, object?> RowOf(Storage.KeyedRow r) => new()
    {
        ["key"] = r.Key,
        ["translated"] = r.Translated,
        ["original"] = r.Original,
        ["origin"] = r.Origin == TranslationOrigin.Runtime ? GetCommand.InEffect : "文件",
        ["placeholder"] = r.Placeholder,
        ["declared_in"] = r.SourceMod,
        ["source"] = (r.SourceLine > 0 ? $"{r.SourceFile}:{r.SourceLine}" : r.SourceFile)
                     + (r.SourceFileCount is > 1 ? $"（另有 {Tally.Complete(r.SourceFileCount.Value - 1).Render("file")}内容相同）" : ""),
    };

    private const string WildcardNote = "这里的 '*' 不是通配符。";

    private static void AfterTable(CommandContext ctx, bool anyPlaceholder)
    {
        ctx.Report.Absent(DataLayers.DiskTranslationsRow(ctx.Db, ctx.Config, ctx.SnapshotName ?? ""));

        _ = anyPlaceholder;
    }
}
