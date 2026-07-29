using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

public sealed class SearchCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "search",
        Aliases = ["find-def", "s"],
        Summary = "Find defs by name, label, description, or translated text.",
        Remarks =
            "Matching is in three stages and stops at the first one that finds anything: full-text search, " +
            "then a prefix pass, then fuzzy identifier matching that tolerates typos and CamelCase initials. " +
            "You never need to add '*' yourself. Translated text is indexed alongside the English, so a " +
            "Chinese label finds the def even though the label column keeps the value the game had at export time.",
        Positionals = [new PositionalSpec { Name = "query", Help = "Words, a def name, or part of one." }],
        Options = [CommonOptions.Limit("defs"), CommonOptions.Scope, CommonOptions.Type],
        Examples =
        [
            "rimsearcher search shield",
            "rimsearcher search \"psychic shock\" --type ThingDef",
            "rimsearcher search CompShield --scope all,-vanilla",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var query = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();
        var type = ctx.Args.Value("type");

        var (rows, total) = ctx.Db.SearchFts(query, scope, type, limit.Effective);
        var how = "full-text";

        if (rows.Count == 0)
        {
            // 02-7 的对策:调用方不该需要知道 '*' 才搜得到复合名,更不该知道打错一个字母就归零。
            var names = ctx.Db.AllDefNames(scope);
            var (bare, kind) = FuzzyMatcher.StripKindPrefix(query);
            var ranked = FuzzyMatcher.Rank(names, bare).Take(limit.Effective).Select(t => t.Text).ToList();
            if (ranked.Count > 0)
            {
                (rows, total) = ctx.Db.ByNames(ranked, limit.Effective);
                total = ranked.Count;
                how = kind is null ? "fuzzy" : $"fuzzy (ignoring the '{kind}:' prefix, which defs do not use)";
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"No def matched '{query}' as written; these are the closest names by spelling.");
            }
        }

        var tally = Tally.Of(rows.Count, Math.Max(total, rows.Count));
        ctx.Report.TruncationNotice(tally, "def",
            limit.IsAll ? "narrow the query." : "raise --limit or narrow the query.");
        if (rows.Count == 0)
        {
            // 值域必须说清。search 覆盖的是 defName / label / description / 译文 ——
            // **不含** C# 类名。实测里有人拿 CompShield 来搜,零结果被读成「模糊匹配坏了」,
            // 而错误消息当时把他指向 code-search:那条路找得到类,却永远找不到用它的 def。
            // 「像个类名」= 带命名空间点号,或者驼峰(首字母大写且内部还有大写)。
            var looksLikeClass = query.Contains('.') ||
                                 (query.Length > 2 && char.IsUpper(query[0]) && query.Skip(1).Any(char.IsUpper));
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing matched '{query}' in this snapshot" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                ". This command covers def names, labels, descriptions and translations, not C# class names." +
                (looksLikeClass
                    ? $" That looks like a class: 'rimsearcher find compClass {query}' (or thingClass, workerClass) " +
                      "finds the defs that use it, and 'rimsearcher code-search' searches the source text."
                    : " 'rimsearcher types' lists what kinds of def this snapshot holds."));
        }

        ctx.Report.Table("defs", ["def_name", "def_type", "label", "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["def_type"] = r.DefType,
                ["label"] = r.Label,
                ["mod"] = r.SourceMod,
            }).ToList());

        Advisory.NoteOutsideTranslations(ctx, rows.Select(r => r.DefName));
        _ = how;
        return rows.Count == 0 ? 1 : 0;
    }
}

public sealed class GetCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "get",
        Aliases = ["show", "inspect", "def"],
        Summary = "Show one def in full: its identity, its fields, and any translations of it.",
        Remarks =
            "Field paths are the merged, post-patch shape the game actually had in memory when the snapshot was " +
            "taken, so PatchOperations and inheritance are already applied. A def created in code rather than XML " +
            "says so on its source line.",
        Positionals = [new PositionalSpec { Name = "defName", Help = "The exact def name. 'search' finds it if you only know part of it." }],
        Options =
        [
            CommonOptions.Limit("fields") with { Default = Limits.DefaultFieldsPerDef.ToString() },
            new OptionSpec
            {
                // 实测:一个 295 字段的 def 里找 statBases,唯一的办法是 --limit all 再 grep 输出。
                // 那既烧上下文,又正好是这套工具劝人别做的事。
                Name = "path",
                Arity = Arity.Multi,
                Aliases = ["paths", "field", "field-path", "only", "filter", "grep"],
                Placeholder = "<text>",
                Help = "Only show field paths containing this text. Repeat it to widen the selection.",
            },
            new OptionSpec
            {
                Name = "fields",
                Arity = Arity.Flag,
                Aliases = ["with-fields", "show-fields"],
                Help = "Deprecated no-op: fields are always shown. Kept so that scripts that pass it keep working.",
            },
        ],
        Examples =
        [
            "rimsearcher get Apparel_ShieldBelt",
            "rimsearcher get Apparel_ShieldBelt --path statBases",
            "rimsearcher get Bullet_Revolver --limit all",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var name = ctx.Args.Positional(0)!;
        var matches = ctx.Db.GetDefsNamed(name);

        if (matches.Count == 0)
        {
            var names = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
            var close = FuzzyMatcher.Rank(names, name).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def is named '{name}' in this snapshot." +
                (close.Count > 0 ? $" Closest names: {string.Join(", ", close)}." : " Try 'rimsearcher search' instead."));
            return 1;
        }

        var limit = ctx.Args.Limit(fallback: Limits.DefaultFieldsPerDef);
        var paths = ctx.Args.Values("path");

        foreach (var def in matches)
        {
            var pairs = new List<KeyValuePair<string, object?>>
            {
                new("def_name", def.DefName),
                new("def_type", def.DefType),
                new("label", def.Label),
                new("description", def.Description),
                new("class", def.Class),
                new("parent", def.Parent),
                new("mod", def.SourceMod),
                new("source", def.Generated
                    ? $"{def.SourceFile} (created in code, not from an XML file)"
                    : def.SourceFile),
            };
            ctx.Report.Detail(matches.Count == 1 ? "def" : $"def:{def.DefName}:{def.DefType}", pairs);

            var (fields, matched, total) = ctx.Db.Fields(def.Id, limit.Effective, paths);
            ctx.Report.Table(matches.Count == 1 ? "fields" : $"fields:{def.DefName}", ["path", "value"],
                fields.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                }).ToList());

            // 多个 def 同名时,截断声明必须指名道姓 —— 否则两条「Showing 5 of N fields」
            // 并排出现,读者无从知道哪条管哪个 def。
            var whose = matches.Count == 1 ? "" : $" of {def.DefName} ({def.DefType})";
            if (paths.Count > 0)
            {
                // 过滤后为空**不等于** def 没有这些字段,只等于没有路径含这段文本。
                // 这两件事在输出上长得一样,所以必须由声明区把它们分开。
                if (matched == 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"No field path{whose} contains {Join(paths)}; the def does have " +
                        $"{Tally.Complete(total).Render("field")}. Drop --path to see them.");
                else
                    ctx.Report.Notice(NoticeKind.Truncation,
                        $"{Tally.Of(fields.Count, matched).Render("field")}{whose} " +
                        $"match {Join(paths)}, out of {total} on the def." +
                        (fields.Count < matched ? " Raise --limit for the rest." : ""));
            }
            else
            {
                ctx.Report.TruncationNotice(Tally.Of(fields.Count, total), "field",
                    $"pass --limit all{(matches.Count == 1 ? "" : $" (this is {def.DefName})")} " +
                    "for the rest, or --path <text> to pick out the ones you want.");
            }

            // 02-3:「字段被截」与「没有该字段」必须可区分。上游把这件事整个略过了,
            // 于是深层字段查不到时调用方会得出「没有这个字段」的错误结论。
            if (def.FieldsTruncated > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The exporter stopped short on this def: {Tally.AtLeast(def.FieldsTruncated).Render("field")} " +
                    "were dropped at export time for depth or size, so a path missing from the list below is not " +
                    "proof that the def lacks it.");

            // --limit 说的是「这次调用我要多少行」,不是「字段表要多少行」。译文表不听它的话,
            // 就出现过 `get Muffalo --limit 5` 吐出八十行的实测 —— 限额说了不算,预算就是空话。
            var allTranslations = ctx.Db.Translations(def.DefName);
            var translations = limit.IsAll
                ? allTranslations
                : allTranslations.Take(limit.Effective).ToList();
            if (translations.Count > 0)
            {
                // original 是被替换掉的原文。它值得占一列:导出时刻 def 上留的是译文,
                // 原文只在注入记录里 —— 两者同时在场是运行时导出独有的便宜(06 层 2 翻译节)。
                ctx.Report.Table(matches.Count == 1 ? "translations" : $"translations:{def.DefName}",
                    ["path", "translated", "original", "language", "origin"],
                    translations.Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["path"] = t.Path,
                        ["translated"] = t.Translated,
                        ["original"] = t.Original,
                        ["language"] = t.Language,
                        ["origin"] = t.Origin == TranslationOrigin.Runtime ? "in effect"
                                   : t.Origin == TranslationOrigin.Harvested ? $"file ({t.SourceMod})"
                                   : $"file ({t.SourceMod}, outside this snapshot)",
                    }).ToList());

                ctx.Report.TruncationNotice(Tally.Of(translations.Count, allTranslations.Count),
                    "translation", $"pass --limit all to see the rest{whose}.");

                if (translations.Any(t => t.Origin == TranslationOrigin.HarvestedOutside))
                    ctx.Report.Notice(NoticeKind.Advisory,
                        "Rows marked 'outside this snapshot' come from language files of mods that were installed " +
                        "but not enabled when the snapshot was taken. They are searchable, but the game did not " +
                        "apply them.", footnote: true);
            }
        }

        if (matches.Count > 1)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(matches.Count).Render("def")} share the name '{name}' across different def types; " +
                "all of them are shown.");

        return 0;
    }

    private static string Join(IReadOnlyList<string> parts)
        => parts.Count == 1 ? $"'{parts[0]}'" : string.Join(" or ", parts.Select(p => $"'{p}'"));
}

public sealed class FindCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "find",
        Aliases = ["by-field", "where"],
        Summary = "Find defs by the value of a field. This is the reverse lookup: from a C# class or a value back to the defs that use it.",
        Remarks =
            "The field path is matched from the end, so 'compClass' finds 'comps[3].compClass' without you knowing " +
            "the index. This replaces grepping the XML: the values here are the merged, post-patch ones, and a " +
            "class reference is an exact match rather than a text hit.",
        Positionals =
        [
            new PositionalSpec { Name = "fieldPath", Help = "A field path or just its last segment, such as compClass or defaultProjectile." },
            new PositionalSpec { Name = "value", Help = "The value to look for. Omit it to list every def that has the field at all.", Required = false },
        ],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Scope,
            new OptionSpec
            {
                Name = "exact",
                Arity = Arity.Flag,
                Aliases = ["exact-match", "whole"],
                Help = "Require the whole value to match. Without it, the value is matched as a substring.",
            },
        ],
        Examples =
        [
            "rimsearcher find compClass RimWorld.CompShield",
            "rimsearcher find defaultProjectile Bullet_Revolver",
            "rimsearcher find thingClass --limit all",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var path = ctx.Args.Positional(0)!;
        var value = ctx.Args.Positional(1);
        var exact = ctx.Args.Flag("exact");
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();

        var (rows, total) = ctx.Db.FindByField(path, value, exact, scope, limit.Effective);

        ctx.Report.TruncationNotice(Tally.Of(rows.Count, total), "def", "raise --limit to see the rest.");

        if (rows.Count == 0)
        {
            if (value is null)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"No def in this snapshot has a field path ending in '{path}'. " +
                    "'rimsearcher fields <DefType> --path <text>' lists the paths that a def type actually has.");
                return 1;
            }

            // 直接把真实值域里的近似项端出来。指一条「去跑 values」的路是不够的:
            // 实测里 compClass 有 175 个取值,默认只出 25 个,照着建议跑一遍照样看不见答案,
            // 只能 --limit all 再自己 grep。近似项本来就在库里,算一次比让人跑两趟便宜。
            var space = ctx.Db.DistinctValues(path, scope, Limits.MaxLimit).Rows.Select(v => v.Value).ToList();

            // RimWorld 的约定:XML 里写的是 `Class="CompProperties_X"`,而落到 def 上的
            // comps[N].compClass 存的是被解析出来的 `CompX`。照 XML 抄过来的名字必然查不到,
            // 而这恰恰是从 grep XML 迁过来的人最先敲的那一条 —— skill 自己的示范就栽在这。
            var alt = value.Contains("CompProperties_") ? value.Replace("CompProperties_", "Comp") : null;

            // 先做精确关系:值域里存的是全限定名,调用方给的常是末段。这是**同一个名字**,
            // 不是「长得像」,所以不该交给模糊打分 —— 实测里 CompAmbientSound 对
            // RimWorld.CompAmbientSound 打分低于阈值,近似项一条都没出来。
            var close = space.Where(v => Tail(v).Equals(alt ?? value, StringComparison.OrdinalIgnoreCase))
                             .Concat(space.Where(v => alt is not null &&
                                                      Tail(v).Contains(Tail(alt), StringComparison.OrdinalIgnoreCase)))
                             .Distinct(StringComparer.Ordinal)
                             .Take(Limits.MaxSuggestions)
                             .ToList();

            if (close.Count == 0)
                close = FuzzyMatcher.Rank(space, alt ?? value).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def has '{path}' set to {(exact ? "exactly " : "")}'{value}'" +
                (space.Count > 0 ? $", out of {Tally.Complete(space.Count).Render("value")} that the field does take" : "") +
                "." +
                (close.Count > 0
                    ? $" Closest: {string.Join(", ", close)}." +
                      (alt is not null && close.Any(c => Tail(c).Equals(alt, StringComparison.OrdinalIgnoreCase))
                          // 说破规律,不只是给一个名字 —— 否则同一个人下一个 comp 还会再敲错一次。
                          ? " The XML writes Class=\"CompProperties_X\"; this field holds the resolved CompX."
                          : "")
                    : $" 'rimsearcher values {path} --limit all' lists them."));
            return 1;
        }

        static string Tail(string v)
        {
            var i = v.LastIndexOf('.');
            return i < 0 ? v : v[(i + 1)..];
        }

        ctx.Report.Table("matches", ["def_name", "def_type", "path", "value", "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.Def.DefName,
                ["def_type"] = r.Def.DefType,
                ["path"] = r.Path,
                ["value"] = r.Value,
                ["mod"] = r.Def.SourceMod,
            }).ToList());

        return 0;
    }
}

public sealed class ListCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "list",
        Aliases = ["ls"],
        Summary = "List every def of one type.",
        Positionals = [new PositionalSpec { Name = "defType", Help = "A def type such as ThingDef. 'rimsearcher types' lists them." }],
        Options =
        [
            CommonOptions.Limit("defs"),
            CommonOptions.Scope,
            new OptionSpec
            {
                Name = "offset",
                Aliases = ["skip", "start"],
                Placeholder = "<n>",
                Help = "Skip this many defs before listing. The total is always reported, so you can tell when you have reached the end.",
                Default = "0",
            },
        ],
        Examples = ["rimsearcher list HediffDef", "rimsearcher list ThingDef --scope all,-vanilla --limit all"],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var offset = ctx.Args.Int("offset", 0);
        var scope = ctx.Scope();

        var (rows, total) = ctx.Db.ListByType(type, scope, limit.Effective, offset);

        if (rows.Count == 0)
        {
            var types = ctx.Db.Types(scope).Select(t => t.Type).ToList();
            var close = FuzzyMatcher.Rank(types, type).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                offset > 0 && total > 0
                    ? $"--offset {offset} is past the end; this snapshot has {Tally.Complete(total).Render("def")} of type {type}."
                    : $"No def type named '{type}' in this snapshot." +
                      (close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : " 'rimsearcher types' lists them all."));
            return 1;
        }

        // 上游有 --offset 分页却拿不到总数,不知道翻到哪算到头(02-1)。这里总数恒在。
        var shownSoFar = offset + rows.Count;
        ctx.Report.TruncationNotice(
            shownSoFar < total ? Tally.Of(rows.Count, total) : Tally.Complete(rows.Count),
            "def",
            $"{shownSoFar} of {total} listed so far; pass --offset {shownSoFar} for the next page.");

        ctx.Report.Table("defs", ["def_name", "label", "mod"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.DefName,
                ["label"] = r.Label,
                ["mod"] = r.SourceMod,
            }).ToList());

        return 0;
    }
}

public sealed class FieldsCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "fields",
        Aliases = ["paths", "schema"],
        Summary = "List the field paths that a def type actually uses, with how often each occurs.",
        Remarks =
            "Use this before 'find' when you are not sure what a field is called. The counts tell you whether a " +
            "path is universal for the type or only present on a handful of defs.",
        Positionals = [new PositionalSpec { Name = "defType", Help = "A def type such as ThingDef." }],
        Options =
        [
            CommonOptions.Limit("field paths"),
            new OptionSpec
            {
                // ThingDef 有 2973 条路径,默认只出 25 条。没有这个开关,调用方只能
                // `fields ThingDef | grep comps` —— 而 grep 会连同截断声明一起滤掉,
                // 于是「被截了」变成「没有」。管道会把声明区吃掉,所以筛选必须在工具里做。
                Name = "path",
                Arity = Arity.Multi,
                Aliases = ["paths", "contains", "match", "filter", "grep", "only"],
                Placeholder = "<text>",
                Help = "Only list paths containing this text. Repeat it to widen the selection.",
            },
        ],
        Examples =
        [
            "rimsearcher fields ThingDef",
            "rimsearcher fields ThingDef --path comps",
            "rimsearcher fields HediffDef --limit all",
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var filters = ctx.Args.Values("path");
        var (rows, total) = ctx.Db.FieldPathsForType(type, limit.Effective, filters.FirstOrDefault());

        if (rows.Count == 0)
        {
            if (filters.Count > 0 && ctx.Db.FieldPathsForType(type, 1).Rows.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"'{type}' has field paths, but none contains '{filters[0]}'. Drop --path to see them all.");
                return 1;
            }
            var types = ctx.Db.Types(ctx.Scope()).Select(t => t.Type).ToList();
            var close = FuzzyMatcher.Rank(types, type).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def type named '{type}' in this snapshot." +
                (close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : " 'rimsearcher types' lists them all."));
            return 1;
        }

        ctx.Report.TruncationNotice(Tally.Of(rows.Count, total), "field path",
            "raise --limit, or narrow with --path <text>.");
        ctx.Report.Table("fields", ["path", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = r.Path,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }
}

public sealed class ValuesCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "values",
        Aliases = ["distinct"],
        Summary = "List the distinct values a field takes, most common first.",
        Remarks =
            "Answers 'what am I allowed to put here' and 'which classes are actually in use' without reading any XML. " +
            "A bare name such as compClass matches every path ending in it, so the table above the values tells you " +
            "which full paths and which def types actually contributed, and how many defs are covered.",
        Positionals = [new PositionalSpec { Name = "fieldPath", Help = "A field path or its last segment, such as compClass." }],
        Options = [CommonOptions.Limit("values"), CommonOptions.Scope],
        Examples = ["rimsearcher values compClass", "rimsearcher values thingClass --scope vanilla"],
    };

    public override int Run(CommandContext ctx)
    {
        var path = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var scope = ctx.Scope();
        var (rows, total) = ctx.Db.DistinctValues(path, scope, limit.Effective);

        if (rows.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def in this snapshot has a field path ending in '{path}'. " +
                "'rimsearcher fields <DefType>' lists the paths a type actually has.");
            return 1;
        }

        // 值的产地。后缀匹配天然会把语义不同的路径并进一张表,不说清就会被读成
        // 「这个字段到处都是这个值」—— 实测里 `values damageAmountBase` 正是这样险些骗到人。
        var cov = ctx.Db.ValueCoverage(path, scope, Limits.MaxSuggestions);

        var pathList = string.Join(", ", cov.Paths.Select(x => $"{x.Path} ({x.Count})"));
        if (cov.PathTotal > cov.Paths.Count) pathList += $", and {cov.PathTotal - cov.Paths.Count} more";

        // 「N of M」是覆盖率的分母。少了它,一条 `Verse.Thing (7)` 分不清是「只有 7 个 def
        // 这么写」还是「导出漏了一千多个」—— 实测里有人靠手工把 110 行加起来才敢下结论。
        var typeList = string.Join(", ", cov.DefTypes.Select(x =>
            $"{x.DefType} ({x.Count} of {ctx.Db.CountDefsOfType(x.DefType, scope)})"));

        ctx.Report.Detail("field", [
            new("matched_paths", pathList),
            new("def_types", typeList),
            new("defs_with_field", (object)cov.DefsCovered),
        ]);

        ctx.Report.TruncationNotice(Tally.Of(rows.Count, total), "value", "raise --limit to see the rest.");
        ctx.Report.Table("values", ["value", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["value"] = r.Value,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }
}

public sealed class TypesCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "types",
        Aliases = ["def-types"],
        Summary = "List every def type in the snapshot with how many defs it has.",
        Options = [CommonOptions.Limit("def types") with { Default = "all" }, CommonOptions.Scope],
        Examples = ["rimsearcher types", "rimsearcher types --scope all,-vanilla"],
    };

    public override int Run(CommandContext ctx)
    {
        var scope = ctx.Scope();
        var all = ctx.Db.Types(scope);
        var limit = ctx.Args.Value("limit") is null ? LimitValue.All : ctx.Args.Limit();
        var rows = limit.IsAll ? all : all.Take(limit.Effective).ToList();

        ctx.Report.TruncationNotice(Tally.Of(rows.Count, all.Count), "def type", "pass --limit all for the rest.");
        ctx.Report.Table("types", ["def_type", "defs"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_type"] = r.Type,
                ["defs"] = r.Count,
            }).ToList());
        return 0;
    }
}

public sealed class ModsCommand : Command
{
    public override CommandSpec Spec => new()
    {
        Name = "mods",
        Summary = "List the mods that were active when the snapshot was taken, in load order.",
        Remarks = "Load order matters: it is the order in which PatchOperations were applied, so it is part of the snapshot's identity.",
        Options = [],
        Examples = ["rimsearcher mods"],
    };

    public override int Run(CommandContext ctx)
    {
        var mods = ctx.Db.Mods;
        var counts = ctx.Db.Types(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
        _ = counts;

        ctx.Report.Table("mods", ["order", "package_id", "name", "version"],
            mods.Select((m, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["order"] = i,
                ["package_id"] = m.PackageId,
                ["name"] = m.Name,
                ["version"] = m.Version,
            }).ToList());
        return 0;
    }
}

internal static class Advisory
{
    /// <summary>
    /// 环境外翻译的聚合尾注(06 上下文预算:逐条标注聚合成一行,不是每行挂一句)。
    /// </summary>
    public static void NoteOutsideTranslations(CommandContext ctx, IEnumerable<string> defNames)
    {
        var n = ctx.Db.CountTranslationsOutside(defNames);
        if (n == 0) return;
        ctx.Report.Notice(NoticeKind.Advisory,
            $"{Tally.Complete(n).Render("def")} above also matched language files from mods that are installed " +
            "but were not enabled in this snapshot; those translations are searchable but were not in effect. " +
            "'rimsearcher get <defName>' shows which.", footnote: true);
    }
}
