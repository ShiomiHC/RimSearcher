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
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing matched '{query}' in this snapshot" +
                (scope.IsAll ? "" : $" within --scope {scope.Expression}") +
                ". 'rimsearcher types' lists what kinds of def it holds; " +
                "if you are looking for C# rather than data, use 'rimsearcher code-search'.");

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
                Name = "fields",
                Arity = Arity.Flag,
                Aliases = ["with-fields", "show-fields"],
                Help = "Deprecated no-op: fields are always shown. Kept so that scripts that pass it keep working.",
            },
        ],
        Examples = ["rimsearcher get Apparel_ShieldBelt", "rimsearcher get Bullet_Revolver --limit all"],
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

            var (fields, total) = ctx.Db.Fields(def.Id, limit.Effective);
            ctx.Report.Table(matches.Count == 1 ? "fields" : $"fields:{def.DefName}", ["path", "value"],
                fields.Select(f => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                }).ToList());

            ctx.Report.TruncationNotice(Tally.Of(fields.Count, total), "field", "pass --limit all for the rest.");

            // 02-3:「字段被截」与「没有该字段」必须可区分。上游把这件事整个略过了,
            // 于是深层字段查不到时调用方会得出「没有这个字段」的错误结论。
            if (def.FieldsTruncated > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"The exporter stopped short on this def: {Tally.AtLeast(def.FieldsTruncated).Render("field")} " +
                    "were dropped at export time for depth or size, so a path missing from the list below is not " +
                    "proof that the def lacks it.");

            var translations = ctx.Db.Translations(def.DefName);
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
            var (paths, _) = ctx.Db.FieldPathsForType("ThingDef", 0);
            _ = paths;
            ctx.Report.Notice(NoticeKind.NextStep,
                value is null
                    ? $"No def in this snapshot has a field path ending in '{path}'. " +
                      "'rimsearcher fields <DefType>' lists the paths that a def type actually has."
                    : $"No def has '{path}' set to {(exact ? "exactly " : "")}'{value}'. " +
                      $"'rimsearcher values {path}' lists the values that do occur.");
            return 1;
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
        Options = [CommonOptions.Limit("field paths")],
        Examples = ["rimsearcher fields ThingDef", "rimsearcher fields HediffDef --limit all"],
    };

    public override int Run(CommandContext ctx)
    {
        var type = ctx.Args.Positional(0)!;
        var limit = ctx.Args.Limit();
        var (rows, total) = ctx.Db.FieldPathsForType(type, limit.Effective);

        if (rows.Count == 0)
        {
            var types = ctx.Db.Types(ctx.Scope()).Select(t => t.Type).ToList();
            var close = FuzzyMatcher.Rank(types, type).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();
            ctx.Report.Notice(NoticeKind.NextStep,
                $"No def type named '{type}' in this snapshot." +
                (close.Count > 0 ? $" Closest: {string.Join(", ", close)}." : " 'rimsearcher types' lists them all."));
            return 1;
        }

        ctx.Report.TruncationNotice(Tally.Of(rows.Count, total), "field path", "raise --limit to see the rest.");
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
        Remarks = "Answers 'what am I allowed to put here' and 'which classes are actually in use' without reading any XML.",
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
