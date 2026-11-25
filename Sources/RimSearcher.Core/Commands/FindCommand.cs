using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class FindCommand : Command
{
    private static readonly CliOption LimitOption = CommonOptions.Limit("命中");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("命中");

    private static readonly CliOption Field = CliOption.Text("field") with
    {
        Aliases = ["path"],
        Placeholder = "<路径>",
        Help = "字段路径。'rimsearcher where ThingDef --field compClass --value CompShield' 等同于 " +
               "'rimsearcher where compClass CompShield --type ThingDef'。",
    };

    private static readonly CliOption ExactOption = CliOption.Flag("exact") with
    {
        Help = "要求整个值匹配，配字段路径或 --value 均可。" +
               "不加时按子串匹配值。",
        Narrows = true,
    };

    private static readonly CliOption ExactPath = CommonOptions.ExactPath();

    private static readonly CliOption PathContains = CommonOptions.PathContainsBeside();

    private static readonly CliOption ValueOption = CliOption.Text("value") with
    {
        Aliases = ["contains"],
        Placeholder = "<文本>",
        Help = "要查找的值。不给字段路径时搜索每个已索引的字段，" +
               "并在结果中列出带有该值的路径。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "where",
        Aliases = ["by-field"],
        Summary = "按字段值反查 def：从 C# 类型或值找回用到它的 def。",
        Remarks =
            "字段路径从末尾起逐段匹配，因此 'compClass' 无需知道下标即可找到 " +
            "'comps[3].compClass'，而 'graphicData.shaderType' 不会命中 " +
            "'swimmingGraphicData.shaderType'。值为合并且 patch 后的值，" +
            "类型引用只匹配该类型本身。\n\n" +
            "行中含游戏在加载时用代码生成的 def（Blueprint_*、Frame_*、肉、尸体等）时，" +
            "「写于」列注明各行属哪一种（代码 / XML）。由代码生成的 def 没有 XML 节点，" +
            "按其 defName 寻址的 PatchOperation 无从匹配。",
        Positionals =
        [
            new CliPositional { Name = "fieldPath", Option = Field, Help = "字段路径或仅其末段，如 compClass 或 defaultProjectile。" + CommonOptions.AnyIndexNote + EntryKeys.HelpNote + "'--field' 是同一参数的另一种写法。不给时改为搜索每个字段。", Required = false },
            new CliPositional { Name = "value", Help = "要查找的值。'--value' 是同一参数的另一种写法，两种写法择一。不给时列出带有该字段的每个 def。" +
                "数、范围或元组不论空格与补零如何书写都能匹配，因此 (1,1)、(1, 1) 与 (1.0, 1.0) 都能找到存储的 (1, 1)，0.30~0.4 能找到 0.3~0.4。", Required = false },
        ],
        Options =
        [
            LimitOption,
            OffsetOption,
            CommonOptions.Scope,
            CommonOptions.Type,
            Field,
            ExactOption,
            ExactPath,
            PathContains,
            ValueOption,
        ],
        Examples =
        [
            "rimsearcher where compClass RimWorld.CompShield",
            "rimsearcher where compClass --value RimWorld.CompShield",
            "rimsearcher where defaultProjectile Bullet_Revolver",
            "rimsearcher where --value World/WorldObjects/Expanding",
            "rimsearcher where \"statBases[stat=Mass].value\" --scope vanilla",
        ],
        JsonKeys =
        [
            new("matches",
                "给出字段路径时：每个 def 的每条匹配路径一行，含 def_name、def_type、path、value、" +
                "from、declared_in（声明或生成该 def 的 mod，" +
                "--scope 筛的也是这一列），行中有由代码生成的 def 时另有 written_in（XML / 代码）。"),
            new("paths",
                "不给字段路径时：每条带有该值的字段路径一行，含 path、def_type、example_value，" +
                "以及分成两列的 def 数：defs_exact（值恰好为所查值）与 defs_other（所查值只是更长值的一部分）。" +
                "加 --exact 时只有一种含义，该列合为一个 defs。这种问法产出此键，此时不出现 matches。"),
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
            Completeness.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        var limit = ctx.Limit(LimitOption);
        var scope = ctx.Scope();
        var type = ctx.Args.Value(CommonOptions.Type);

        var offset = ctx.Args.Offset(OffsetOption);

        var path = ctx.Args.Positional(0);
        if (path is not null) EntryKeys.Validate(path);

        if (ctx.Args.Value(ValueOption) is { Length: 0 })
            throw new CliUsageException(
                "--value 为空串，不是可查找的值。" +
                (path is null
                    ? "须给出要查找的文本（'rimsearcher where --value CompShield'）。"
                    : $"列出带有该字段的每个 def 时去掉 --value（'rimsearcher where {path}'）；" +
                      $"查找某个值时给出该值（'rimsearcher where {path} <值>'）。") +
                "未设值的字段无法查找，这里的任何查询都不会返回该字段。");

        var named = ctx.Args.Value(ValueOption) is { Length: > 0 } v ? v : null;

        if (path is null)
        {
            if (ctx.Args.Flag(ExactPath))
                throw new CliUsageException(
                    "--exact-path 改变作为参数给出的字段路径的匹配方式，而本次调用没有给出字段路径。" +
                    "给出路径（'rimsearcher where statBases[].stat MarketValue --exact-path'），或去掉该开关。" +
                    "按路径片段收窄搜索范围时，改用 --path-contains。");

            if (named is null)
            {
                ctx.Report.Promises("matches");
                ctx.Report.Notice(NoticeKind.NextStep,
                    "缺少字段路径或 --value：按字段查找用 'rimsearcher where compClass CompShield'，" +
                    "搜索每个字段用 'rimsearcher where --value CompShield'。");
                return 2;
            }
            ctx.Report.Promises("paths");
            return ByValue(ctx, named, scope, limit, ctx.Args.Flag(ExactOption), offset, type,
                           ctx.Args.Values(PathContains));
        }
        ctx.Report.Promises("matches");

        if (ctx.Args.Positional(1) is { } inline && named is not null && !string.Equals(inline, named, StringComparison.Ordinal))
            throw new CliUsageException(
                $"值给出两次且不同：参数为 '{inline}'，--value 为 '{named}'。" +
                $"给出字段路径时两者含义相同：'rimsearcher where {path} {inline}' 等同于 " +
                $"'rimsearcher where {path} --value {inline}'。去掉其中一个。");

        var value = ctx.Args.Positional(1) ?? named;
        var exact = ctx.Args.Flag(ExactOption);
        if (exact && value is not { Length: > 0 })
            throw new CliUsageException(
                "--exact 要求整个值匹配，须同时给出值；把字段路径按整条匹配用 --exact-path。");
        var pq = new PathQuery(path, ctx.Args.Flag(ExactPath), Contains: ctx.Args.Values(PathContains));

        var (rows, total, defs) = ctx.Db.FindByField(pq, value, exact, scope, limit.Effective, offset, type);

        string? rewritten = null;
        if (total == 0 && pq.CanTolerateIndex)
        {
            var tolerant = pq with { IndexTolerant = true };
            var shapes = ctx.Db.FindPathShapes(tolerant, value, exact, scope, type);
            if (shapes.Count > 0)
            {
                var retry = ctx.Db.FindByField(tolerant, value, exact, scope, limit.Effective, offset, type);
                var shown = shapes.Take(Limits.MaxSuggestions).ToList();
                rewritten =
                    $"按原样给出的 {path} 之下没有值，本次改按以下形状查询：" +
                    string.Join("、", shown.Select(x => $"{x.Shape}（{x.Count}）")) +
                    (shapes.Count > shown.Count
                        ? Cjk.Join("，另有", Tally.Complete(shapes.Count - shown.Count).Render("path shape"), "未列出")
                        : "") +
                    (shapes.Count > 1
                        ? "。其中任一种配 --exact-path 传回，即只合并那一种。"
                        : "。该形状配 --exact-path 传回即可固定为这一种。");
                pq = tolerant;
                (rows, total, defs) = retry;
            }
        }
        if (total == 0 && EntryKeys.ReadAs(ctx, pq, scope, type) is { } keyedPath)
        {
            rewritten = $"'{path}'{ReadCommand.ReadAsPhrase}'{keyedPath}'。";
            path = keyedPath;
            pq = pq with { Text = keyedPath };
            (rows, total, defs) = ctx.Db.FindByField(pq, value, exact, scope, limit.Effective, offset, type);
        }

        var noun = defs == total ? "def" : "match";

        void NoteRewrite()
        {
            if (rewritten is null) return;
            ctx.Report.Notice(NoticeKind.NextStep, rewritten);
            rewritten = null;
        }

        if (rows.Count > 0)
        {
            ctx.Report.PageNotice(noun, rows.Count, offset, total);
            if (defs < total)
                ctx.Report.Notice(NoticeKind.Count,
                    $"这 {Tally.Complete(total).Render("match")}来自 " +
                    $"{Tally.Complete(defs).Render("def")}：同一个 def 在多条路径上命中时，每条路径各占一行，" +
                    "因此 defName 列有重复。");
        }
        else if (offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"共 {Tally.Complete(total).Render(noun)}。");
            NoteRewrite();
            return 1;
        }

        NoteRewrite();

        if (type is not { Length: > 0 } &&
            ctx.Db.Types(ctx.Unscoped())
                  .FirstOrDefault(t => string.Equals(t.Type, path, StringComparison.Ordinal))
                is { Type: not null } asType)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{path} 在本快照中也是 def 类型（{Tally.Complete(asType.Count).Render("def")}），" +
                $"本查询按字段路径读取 {path}。按类型查询：'rimsearcher where <fieldPath>" +
                (value is null ? "" : $" {value}") + $" --type {asType.Type}'。");

        if (rows.Count == 0)
        {
            void NoteElsewhere()
            {
                if (NameLookup.Elsewhere(ctx, db => db.FindByField(
                        pq, value, exact,
                        Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0, 0, type).Defs, "def")
                    is { } line)
                    ctx.Report.Notice(NoticeKind.NextStep, line);
            }

            if (type is { Length: > 0 })
            {
                if (ctx.UnknownDefType(type) is { } unknown)
                {
                    ctx.Report.Notice(NoticeKind.Filter, unknown);
                    return 1;
                }

                var hiddenByType = ctx.Db.FindByField(pq, value, exact, scope, 0, 0).Defs;
                if (hiddenByType > 0)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Type), hiddenByType, ctx.Without(CommonOptions.Type)));
                    NoteElsewhere();
                    return 1;
                }
            }

            if (pq.Exact && !ctx.Db.FieldPathExists(pq, scope) &&
                PathWithout.Existing(ctx, pq with { Exact = false }, scope) is { } loose)
            {
                var shapes = ctx.Db.FindPathShapes(loose, value, exact, scope, type);
                var valueMissedThemAll = shapes.Count == 0;
                if (valueMissedThemAll) shapes = ctx.Db.FindPathShapes(loose, null, false, scope, type);
                var shown = shapes.Take(Limits.MaxSuggestions).ToList();
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"没有恰好为 {path} 的字段路径" +
                    (valueMissedThemAll
                        ? $"，{(loose.IndexTolerant ? "与所给路径相似" : "以所给路径结尾")}的路径也都没有设为 '{value}'。这些路径："
                        : loose.IndexTolerant ? "。放开列表下标后匹配：" : "。改按后缀匹配：") +
                    string.Join("、", shown.Select(x => $"{x.Shape}（{x.Count}）")) +
                    (shapes.Count > shown.Count
                        ? Cjk.Join("，另有", Tally.Complete(shapes.Count - shown.Count).Render("path shape"), "未列出")
                        : "") +
                    "。其中任一条都可直接传回 --exact-path（'[]' 表示任意下标）" +
                    (valueMissedThemAll ? "，但值也须更换。" : "。"));
                NoteElsewhere();
                return 1;
            }

            var pathKnown = false;
            if (pq.Contains is { Count: > 0 } && !ctx.Db.FieldPathExists(pq, scope) &&
                PathWithout.Existing(ctx, pq with { Contains = null }, scope) is { } uncontained)
            {
                var hiddenByContains = ctx.Db.FindByField(uncontained, value, exact, scope, 0, 0, type).Defs;
                if (hiddenByContains > 0)
                {
                    ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(PathContains), hiddenByContains,
                                                           ctx.Without(PathContains)));
                    NoteElsewhere();
                    return 1;
                }
                pathKnown = value is not null;
            }
            var fieldExists = pathKnown || ctx.Db.FieldPathExists(pq, scope);

            if (!fieldExists)
            {
                var identity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["class"] = $"按 def 自身的 Class 筛选用 'rimsearcher list {type ?? "<DefType>"} --class {value ?? "<ClassName>"}'",
                    ["def_type"] = $"整个类型的 def 见 'rimsearcher list {value ?? "<DefType>"}'",
                    ["deftype"] = $"整个类型的 def 见 'rimsearcher list {value ?? "<DefType>"}'",
                    ["mod"] = $"'--scope {value ?? "<packageId>"}' 把任何查询限定在一个 mod 内",
                    ["declared_in"] = $"'--scope {value ?? "<packageId>"}' 把任何查询限定在一个 mod 内",
                    ["source"] = "「文件」行的值只由 'rimsearcher get' 给出",
                    ["parent"] = $"Abstract 节点见 'rimsearcher inherit {value ?? "<名称>"}'",
                    ["parentname"] = $"Abstract 节点见 'rimsearcher inherit {value ?? "<名称>"}'",
                };

                if (!identity.ContainsKey(path) && !scope.IsAll &&
                    ctx.Db.FieldPathExists(pq, ctx.Unscoped()))
                {
                    OutsideScope.Say(ctx, pq, type);
                    NoteElsewhere();
                    return 1;
                }

                if (EntryKeys.SayKeyMissing(ctx, pq, scope, type))
                {
                    NoteElsewhere();
                    return 1;
                }

                var placed = value is null && !identity.ContainsKey(path) ? Placed(ctx, path, scope) : null;
                var keyedTail = value is null && placed is null && !identity.ContainsKey(path)
                    ? EntryKeys.TailHint(ctx, "where", path, scope, type) : null;
                var tailValue = keyedTail ?? (value is null && placed is null && !identity.ContainsKey(path)
                    ? TailIsValue(ctx, path, scope, type) : null);
                var deeper = identity.ContainsKey(path) ? null : Completeness.ValuesLiveDeeper(ctx, path, scope);
                var beyond = identity.ContainsKey(path) || keyedTail is not null
                    ? [] : NameLookup.Beyond(ctx, path, NameLookup.Side.Data, type);

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"本快照中没有 def 带以 {path} 结尾的字段路径" +
                    (scope.IsAll ? "" : $"（--scope {scope.Expression} 之外也没有）") + "。" +
                    (identity.TryGetValue(path, out var hint)
                        ? $"{path} 是 def 的身份：{hint}。"
                        : deeper is not null || placed is not null || tailValue is not null || beyond.Count > 0
                            ? ""
                            : value is not null && ctx.Db.PathsWithValue(value, ctx.Unscoped(), 1).Rows.Count > 0
                                ? $"该值所在的字段路径见 'rimsearcher where --value {value}'。"
                                : ""));

                NameLookup.Say(ctx, [.. placed ?? [], .. beyond]);
                if (tailValue is not null) ctx.Report.Notice(NoticeKind.NextStep, tailValue);
                if (deeper is not null) ctx.Report.Notice(NoticeKind.NextStep, deeper);
                NoteElsewhere();
                return 1;
            }

            if (value is null)
            {
                OutsideScope.Say(ctx, pq, type);
                NoteElsewhere();
                return 1;
            }

            var space = ctx.Db.DistinctValues(pq, scope, Limits.ValueSpaceSample, type).Rows.Select(v => v.Value).ToList();

            var alt = value.Contains("CompProperties_") ? value.Replace("CompProperties_", "Comp") : null;

            var close = space.Where(v => Tail(v).Equals(alt ?? value, StringComparison.OrdinalIgnoreCase))
                             .Concat(space.Where(v => alt is not null &&
                                                      Tail(v).Contains(Tail(alt), StringComparison.OrdinalIgnoreCase)))
                             .Distinct(StringComparer.Ordinal)
                             .Take(Limits.MaxSuggestions)
                             .ToList();

            if (close.Count == 0)
                close = FuzzyMatcher.Rank(space, alt ?? value).Take(Limits.MaxSuggestions).Select(t => t.Text).ToList();

            var cov = space.Count > 0 ? ctx.Db.ValueCoverage(pq, scope, 3, type) : default;
            var provenance = space.Count == 0 ? "" :
                Cjk.Join("（比对的是",
                         cov.Paths.Count > 0
                             ? string.Join("、", cov.Paths.Select(x => x.Path)) +
                               (cov.PathTotal > cov.Paths.Count
                                   ? Cjk.Join("及另外", Tally.Complete(cov.PathTotal - cov.Paths.Count).Render("field path"))
                                   : "")
                             : path,
                         "下的", Tally.Complete(space.Count).Render("value")) + "）";

            var hiddenByScope = scope.IsAll
                ? 0
                : ctx.Db.FindByField(pq, value, exact,
                                     Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config),
                                     0, 0, type).Defs;
            ctx.Report.Notice(NoticeKind.NextStep,
                (scope.IsAll ? "" : $"--scope {scope.Expression} 范围内") +
                $"没有 def 的 {path} {(exact ? "恰好为" : "为")} '{value}'{provenance}。" +
                (close.Count > 0
                    ? $"最接近的：{string.Join("、", close)}。" +
                      (alt is not null && close.FirstOrDefault(c => Tail(c).Equals(alt, StringComparison.OrdinalIgnoreCase)) is { } resolved
                          ? "XML 写的是 Class=\"CompProperties_X\"，该字段存储的是解析后的 CompX：" +
                            $"所要的查询为 'rimsearcher where {path} {resolved}'。"
                          : $"全部取值见 'rimsearcher values {path}'。")
                    : $"全部取值见 'rimsearcher values {path}'。" +
                      ""));

            if (hiddenByScope > 0)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), hiddenByScope, ctx.Without(CommonOptions.Scope)));

            if (value is { Length: > 0 }) Advisory.NoteTextIndexHasIt(ctx, value, "本次查询");
            NoteElsewhere();
            return 1;
        }

        static string Tail(string v)
        {
            var i = v.LastIndexOf('.');
            return i < 0 ? v : v[(i + 1)..];
        }

        static string? TailIsValue(CommandContext ctx, string path, Snapshot.ScopeFilter scope, string? type)
        {
            var cut = path.LastIndexOf('.');
            if (cut <= 0 || cut == path.Length - 1) return null;
            var head = path[..cut];
            var tail = path[(cut + 1)..];

            var rows = ctx.Db.PathsWithValue(tail, scope, 64, Storage.ValueMatch.Identifier, 0, type).Rows;
            if (rows.Count == 0) return null;

            var shapes = rows.Select(r => Search.PathSegments.Shape(r.Path))
                             .Distinct(StringComparer.Ordinal)
                             .OrderByDescending(s => Search.PathSegments.IsWholeSegment(s, head))
                             .ToList();
            var under = shapes.Where(s => Search.PathSegments.IsWholeSegment(s, head)).ToList();
            var pick = under.Count > 0 ? under : shapes;
            var shown = pick.Take(Limits.MaxSuggestions).ToList();

            return $"{tail} 在本快照中是值：该值位于 " +
                   string.Join("、", shown) +
                   (pick.Count > shown.Count
                       ? Cjk.Join("，另有", Tally.Complete(pick.Count - shown.Count).Render("path shape"), "未列出")
                       : "") +
                   (under.Count > 0
                       ? ""
                       : $"，这些路径都不在 {head} 下") +
                   (EntryKeys.SelectorFor(ctx, shown[0], tail) is { } keyed
                       ? $"。读取该值的查询为 'rimsearcher where {EntryKeys.Quote(keyed)}'。"
                       : $"。查询该值的命令为 'rimsearcher where {shown[0]} --value {tail}'。");
        }

        static NameLookup.Sighting[]? Placed(CommandContext ctx, string name, Snapshot.ScopeFilter scope)
        {
            var named = ctx.Db.GetDefsNamed(name);
            if (named.Count == 0)
                return NameLookup.Locate(ctx, name, scope) is { } sighting ? [sighting] : null;

            var referenced = ctx.Db.PathsWithValue(name, scope, Limits.MaxSuggestions).Rows
                                .Any(r => !string.Equals(r.Path, "defName", StringComparison.Ordinal));

            var asDef = NameLookup.AsDef(name, named);
            if (!referenced) return [asDef];
            return EntryKeys.AsEntryKey(ctx, name, scope) is { } keyed &&
                   EntrySelector.Parse(keyed.Path, out _) is { } sel
                ? [asDef, new NameLookup.Sighting(NameLookup.Where.FieldValue, name, "字段值",
                                                  Cjk.Join($"{sel.Before}[] 条目的 {sel.Key}，出现在",
                                                           Tally.Complete(keyed.Defs).Render("def"), "上"),
                                                  $"{CommandRegistry.ExeName} where {EntryKeys.Quote(keyed.Path)}")]
                : [asDef, new NameLookup.Sighting(NameLookup.Where.FieldValue, name, "字段值",
                                                  "指向它的字段", $"{CommandRegistry.ExeName} where --value {name}")];
        }

        var generated = ctx.Db.FindGeneratedDefs(pq, value, exact, scope, type);
        Advisory.NoteGeneratedDefs(ctx, generated, defs,
            rows.Count(r => r.Def.Generated));

        Advisory.NoteSubstringWidened(ctx, pq, value, exact, scope, defs, type);

        Advisory.NoteValueAuthorship(ctx, pq, value, exact, scope, type);

        ctx.AnnounceExcluded(scope, rest => ctx.Db.FindByField(pq, value, exact, rest, 0, 0, type).Defs, "def");

        Advisory.NoteValueElsewhere(ctx, pq, value, exact, scope, defs);

        var mixed = ctx.Db.FindPathShapes(pq, value, exact, scope, type);
        Advisory.NoteMixedPathShapes(ctx, pq.Selector is { } sel
            ? [.. mixed.Select(x => (EntryKeys.Quote(sel.Reinsert(x.Shape)), x.Count))]
            : mixed);

        Completeness.NoteIndexedPathsOnly(ctx, ctx.Db.TruncatedDefsSharingPath(pq, scope, type),
            type is { Length: > 0 }
                ? $"{type} 的全部 def"
                : "带有这条路径的每个 def 类型");

        var origins = new XmlOriginLookup(ctx.Db);
        if (rows.Select(r => r.Def.Id).Distinct().Count() > 1000) ctx.Db.PreloadXmlWritten();
        ctx.Report.Table("matches",
            generated > 0
                ? ["def_name", "def_type", "path", "value", FieldFrom.Column, WrittenIn.Column, "declared_in"]
                : new[] { "def_name", "def_type", "path", "value", FieldFrom.Column, "declared_in" },
            rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["def_name"] = r.Def.DefName,
                ["def_type"] = r.Def.DefType,
                ["path"] = r.Path,
                ["value"] = r.Value,
                [FieldFrom.Column] = origins.From(r.Def, r.Path, r.Default)
                                     ?? FieldFrom.Render(XmlOrigin.No, r.Default, ctx.Db.Meta.IndexesPostPatchXml),
                [WrittenIn.Column] = generated > 0 ? WrittenIn.Render(r.Def.Generated) : null,
                ["declared_in"] = r.Def.SourceMod,
            }).ToList(),
            given: new Dictionary<string, string?>
            {
                ["def_type"] = type,
                ["path"] = ctx.Args.Positional(0),
                ["value"] = bool.TryParse(value, out var flag) ? flag.ToString() : value,
            });

        Advisory.NoteAuthoredSiblings(ctx, rows.Where(r => r.Default != Contract.DefaultState.Same)
                                                .Select(r => (r.Def.DefName, r.Def.Id, r.Path)),
                                      pq.Selector?.Key);
        return 0;
    }

    private static int ByValue(CommandContext ctx, string value, Snapshot.ScopeFilter scope, LimitValue limit,
                               bool exact, int offset, string? type = null,
                               IReadOnlyList<string>? pathContains = null)
    {
        var (rows, total, exactTotal, defsTotal) = ctx.Db.PathsWithValue(value, scope, limit.Effective,
            exact ? ValueMatch.Exact : ValueMatch.Substring, offset, type, pathContains);

        if (rows.Count == 0 && offset > 0 && total > 0)
        {
            ctx.Report.PastEnd(offset, $"共有 {Tally.Complete(total).Render("field path")}带有 '{value}'。");
            return 1;
        }

        if (rows.Count == 0)
        {
            if (ctx.UnknownDefType(type) is { } unknown)
            {
                ctx.Report.Notice(NoticeKind.Filter, unknown);
                return 1;
            }

            ctx.Report.Notice(NoticeKind.NextStep,
                (scope.IsAll ? "本快照中" : $"本快照中 --scope {scope.Expression} 范围内") +
                (type is { Length: > 0 } ? $"def 类型 {type} 下" : "") +
                (pathContains is { Count: > 0 } has ? $"含 {PathFilterText.Say(has)} 的字段路径上" : "") +
                $"没有字段的值{(exact ? "等于" : "含有")} '{value}'。");

            int Without(Snapshot.ScopeFilter s, string? t, IReadOnlyList<string>? p, bool whole = true)
                => ctx.Db.PathsWithValue(value, s, 0, exact && whole ? ValueMatch.Exact : ValueMatch.Substring,
                                         defType: t, pathContains: p).Defs;
            if (!scope.IsAll && Without(ctx.Unscoped(), type, pathContains) is > 0 and var byScope)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), byScope, ctx.Without(CommonOptions.Scope)));
            if (type is { Length: > 0 } && Without(scope, null, pathContains) is > 0 and var byType)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Type), byType, ctx.Without(CommonOptions.Type)));
            if (pathContains is { Count: > 0 } && Without(scope, type, null) is > 0 and var byPath)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(PathContains), byPath, ctx.Without(PathContains)));

            if (exact && Without(scope, type, pathContains, whole: false) is > 0 and var byExact)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ExactOption), byExact, ctx.Without(ExactOption)));

            Advisory.NoteTextIndexHasIt(ctx, value);

            if (NameLookup.Elsewhere(ctx, db => db.PathsWithValue(
                    value, Snapshot.ScopeFilter.Parse("all", db.PackageIds(), ctx.Config), 0,
                    exact ? ValueMatch.Exact : ValueMatch.Substring, defType: type,
                    pathContains: pathContains).Total, "field path")
                is { } line)
                ctx.Report.Notice(NoticeKind.NextStep, line);
            return 1;
        }

        ctx.Report.PageNotice("field path", rows.Count, offset, total);

        ctx.Report.Notice(NoticeKind.Count,
            $"共涉及 {Tally.Complete(defsTotal).Render("def")}，每个 def 只计一次。",
            data: new Dictionary<string, object?> { ["defs"] = defsTotal });

        if (!exact && exactTotal < total)
            ctx.Report.Notice(NoticeKind.Filter,
                exactTotal == 0
                    ? $"这里没有恰好为 '{value}' 的值：每处命中都在更长的值里包含所查值，见示例值列。"
                    : $"其中 {Tally.Complete(exactTotal).Render("field path")}恰好为该值。",
                data: new Dictionary<string, object?> { ["paths_exact"] = exactTotal });

        ctx.AnnounceExcluded(scope, rest => ctx.Db.PathsWithValue(
            value, rest, 0, exact ? ValueMatch.Exact : ValueMatch.Substring, defType: type,
            pathContains: pathContains).Total, "field path");

        Completeness.NoteIndexedPathsOnly(ctx,
            ctx.Db.TruncatedDefsSharingValue(value, exact ? ValueMatch.Exact : ValueMatch.Substring,
                                             scope, type),
            type is { Length: > 0 }
                ? $"{type} 的全部 def"
                : "带有这个值的每个 def 类型");

        ctx.Report.Table("paths",
            exact
                ? ["path", "def_type", "defs", "example_value"]
                : ["path", "def_type", "defs_exact", "defs_other", "example_value"],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)(exact
                ? new Dictionary<string, object?>
                {
                    ["path"] = r.Path,
                    ["def_type"] = r.DefType,
                    ["defs"] = r.Defs,
                    ["example_value"] = r.Sample,
                }
                : new Dictionary<string, object?>
                {
                    ["path"] = r.Path,
                    ["def_type"] = r.DefType,
                    ["defs_exact"] = r.DefsExact,
                    ["defs_other"] = r.Defs - r.DefsExact,
                    ["example_value"] = r.Sample,
                })).ToList(),
            given: new Dictionary<string, string?> { ["def_type"] = type });
        return 0;
    }
}
