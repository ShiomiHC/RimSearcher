using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Snapshot;
using RimSearcher.Search;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class InheritCommand : Command
{
    public const string SameValue = "same_value";
    public const string SameAsMode = "same_as_mode";

    public const string PatchOpsName = "patch_ops_name";

    public const string NotInSnapshot = "不在快照";

    private static readonly CliOption LimitOption = CommonOptions.Limit("直接子节点");

    private static readonly CliOption PathContains = CliOption.Text("path-contains") with
    {
        Aliases = ["filter", "grep", "path"],
        Placeholder = "<文本>",
        Help = "对链上每一层，数出该层自身的 XML 中路径含此文本的行数，以及从该层派生、字段路径含此文本的其他 def 数，和其中取同一值的个数。" +
               "匹配方式与 'get --path-contains' 相同，为子串匹配，但只收一段文本。",
    };

    private static readonly CliOption ExactPath = CommonOptions.ExactPathFilter(single: true) with { Narrows = false };

    private static readonly CliOption Type = CommonOptions.Type with
    {
        Help = "一个名字由几个 def 类型共用时，只保留所给 def 类型下的节点（Bomb 既是 ThingDef 也是 DamageDef）。" +
               "列出保留下来的节点的祖先与直接子节点。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "inherit",
        Aliases = ["inheritance", "parent", "parents", "children", "tree"],
        Summary = "列出一个 XML 节点继承自哪些节点、哪些节点继承自该节点，包括 Abstract 节点。",
        Remarks =
            "这里显示的是全部 PatchOperation 执行之后的 XML；缺 post_patch_inheritance 层的快照，节点取自 mod 的 XML 文件。由 patch 添加的节点没有「声明于」与「文件」。" +
            "「Name 的 patch 数」数的是以 @Name= 指向该节点的 xpath；" +
            "「defName 的 patch 数」与「label 的 patch 数」数的是以 defName= 与 label= 指向该节点的 xpath。" +
            "经由 thingClass 或通配符到达节点的 xpath 在这一层里不计入任何一格。" +
            "没有 Name= 的节点，「Name 的 patch 数」记为「未统计」；" +
            "defName 与 label 两个数仍会统计。" +
            "后代自身的 patch 数" +
            "不含改写祖先的 patch。" +
            "快照中没有任何 mod 声明该祖先时，「声明于」显示为「不在快照」，该祖先带来的内容不在本快照中。\n\n" +
            "加 --path-contains 或 --exact-path 时，每个节点另有一张表，链上每层一行：" +
            "「本层 XML 写有行数」（大于 0 即该层写有此字段）、" +
            "「其他 def 数」（从该层派生的其他 def）、「带此路径数」（其中带有匹配字段路径的个数），" +
            "有参照值时另有「同值数」（其中读到所查 def 自身取值的个数）或「同众数数」（其中读到 Abstract 节点下众数的个数）。" +
            "表上方的块写明参照值是哪个值、取自何处：取自所查 def 自身（「自身匹配字段数」为其匹配的字段数；" +
            "该 def 没有 Name= 时另有「自身 XML 写有行数」），" +
            "或取自 Abstract 节点下的众数（「众数 def 数」为带有该值的 def 数，「不同取值数」为该处取值的种数）。" +
            "「参照值」为「无」表示该 def 没有匹配的字段，或有几个取值不同的匹配字段（「自身取值」列出这些值，" +
            "「下一步」收窄到其中一条）；此时「同值数」与「同众数数」都不输出。" +
            "所查节点为 Abstract 时，「本层 XML 写有行数」按 XML 的写法匹配：以 defName 作标签的列表条目在 XML 中为 statBases.Mass，" +
            "在字段路径中为 statBases[2].value。" +
            "覆写该字段的后代计入「带此路径数」，" +
            "不计入「同值数」。「全类型带此路径数」与「全类型 def 数」是在整个 def 类型上的同一比例。" +
            "有「被截断数」列时，数的是「其他 def 数」中字段表在导出时被截断的 def，这些 def 可能仅因此未计入「带此路径数」。",
        Positionals =
        [
            new CliPositional
            {
                Name = "name",
                Variadic = true,
                Help = "XML 节点的 Name=，或 def 的 defName，两者都会查找。给出几个名字时按给出顺序各输出一块。",
            },
        ],
        Options =
        [
            LimitOption,
            PathContains,
            ExactPath,
            Type,
        ],
        Examples =
        [
            "rimsearcher inherit BaseBullet",
            "rimsearcher inherit Bullet_Revolver",
            "rimsearcher inherit BaseHumanlike",
            "rimsearcher inherit Bullet_Revolver --path-contains damageAmountBase",
        ],
        JsonKeys =
        [
            new("nodes",
                "每个与所查名字对应的 XML 节点一个对象，各含 node（身份与 patch 计数）、ancestors、" +
                "有直接子节点时的 children，以及给出 --path-contains 或 --exact-path 时的 reference 与 witnesses。" +
                "给出几个名字时，对象按名字的给出顺序排列；无匹配的名字在这里没有对象，在 notes 里有一条引用该名字的提示。",
                Rows: true),
            NameLookup.JsonKey,
            new("absent",
                "仅当快照的继承层取自 mod 的 XML 文件（patch 执行之前）时出现。一行：layer 为 post_patch_inheritance，" +
                "state 为「无法采集」（导出时未找到 patch 之后的 XML），next 为导出命令。其余情况为空。",
                Rows: true),
        ],
    };

    private static int Miss(CommandContext ctx, string name)
    {
        var close = Suggestion.Closest(ctx.Db.AllXmlNodeNames(), name);

        ctx.Report.Notice(NoticeKind.NextStep,
            $"本快照中没有名为 '{name}' 的 XML 节点。" + Suggestion.Say(close));
        var sighting = Sighting(ctx, name);
        if (sighting is not null) NameLookup.Say(ctx, sighting);
        return 1;
    }

    private static NameLookup.Sighting? Sighting(CommandContext ctx, string name)
    {
        var defs = ctx.Db.GetDefsNamed(name);
        return defs.Count > 0 ? NameLookup.AsDef(name, defs) : NameLookup.Locate(ctx, name);
    }

    public override int Run(CommandContext ctx)
    {
        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in ctx.Args.Positionals)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"重复给出的名字只输出一次：{NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}。");

        var single = names.Count == 1;
        var defType = ctx.Args.Value(Type);
        var found = new List<(string Name, IReadOnlyList<XmlNodeRow> Nodes)>();
        var missing = new List<string>();
        var otherType = false;
        foreach (var n in names)
        {
            var hit = ctx.Db.NodesNamed(n);
            if (hit.Count == 0) { missing.Add(n); if (single) return Miss(ctx, n); continue; }
            if (defType is { Length: > 0 } && hit.Count > 0)
            {
                var kept = hit.Where(h => string.Equals(h.DefType, defType, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0)
                {
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"'{n}' 是 {NameList.Render([.. hit.Select(h => h.DefType).Distinct()], Limits.MaxSuggestions)} 下的 XML 节点，" +
                        $"不在 --type {defType} 下。");
                    if (single) return 1;
                    otherType = true;
                    continue;
                }
                hit = kept;
            }
            found.Add((n, hit));
        }
        var nodes = found.SelectMany(f => f.Nodes).ToList();

        if (nodes.Count == 0 && missing.Count == 0 && otherType) return 1;
        if (nodes.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"以下名字都没有对应的 XML 节点：{NameList.Render(missing, Limits.MaxSuggestions)}。");
            foreach (var n in missing)
                if (Sighting(ctx, n) is { } sighting) NameLookup.Say(ctx, sighting);
            return 1;
        }

        if (missing.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"{NameList.Render(missing, Limits.MaxSuggestions)} 没有对应的 XML 节点，下文与之无关。");
            foreach (var n in missing)
                if (Sighting(ctx, n) is { } sighting)
                    NameLookup.Say(ctx, sighting);
        }

        var limit = ctx.Limit(LimitOption);
        var (pathFilters, exactPath) = ctx.Args.PathFilters(PathContains, ExactPath);
        var pathFilter = pathFilters.Count > 0 ? pathFilters[0] : null;
        if (pathFilter?.Contains('|') == true)
            throw new CliUsageException(
                $"{(exactPath ? "--exact-path" : "--path-contains")} 只收一条，'{pathFilter}' 含 '|'。");

        foreach (var node in nodes)
        {
            ctx.Report.Item("nodes");

            var named = node.Name is { Length: > 0 };
            var label = named ? node.Name : node.DefName ?? "";
            var identity = new List<KeyValuePair<string, object?>>
            {
                new("name", node.Name),
                new("def_name", node.DefName),
                new("def_type", node.DefType),
                new("abstract", node.Abstract),
                new("inherits_from", node.ParentName),
                new("declared_in", node.SourceMod),
                new("source", node.SourceFile),
                new(PatchOpsName, named ? node.PatchOps : "未统计"),
            };
            identity.Add(new("patch_ops_defname", node.PatchOpsDefName));
            identity.Add(new("patch_ops_label", node.PatchOpsLabel));
            ctx.Report.Detail("node", identity);

            var chain = new List<XmlNodeRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? unresolved = null;
            var cursor = node.ParentName;
            while (cursor is { Length: > 0 })
            {
                if (!seen.Add(cursor)) break;
                var up = ctx.Db.NodeWithName(cursor);
                if (up is null) { unresolved = cursor; break; }
                chain.Add(up);
                cursor = up.ParentName;
            }

            if (chain.Count > 0)
                ctx.Report.CountNotice(Tally.Complete(chain.Count), "ancestor", "");

            var patchedUp = chain.Where(n => n.PatchOps > 0).ToList();
            var ancestors = chain.Select(n =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["name"] = n.Name,
                    ["def_type"] = n.DefType,
                    ["abstract"] = n.Abstract,
                    ["declared_in"] = n.SourceMod,
                    ["source"] = n.SourceFile,
                };
                if (patchedUp.Count > 0) row[PatchOpsName] = n.PatchOps;
                return (IReadOnlyDictionary<string, object?>)row;
            }).ToList();
            if (unresolved is not null)
                ancestors.Add(new Dictionary<string, object?>
                {
                    ["name"] = unresolved,
                    ["def_type"] = null,
                    ["abstract"] = null,
                    ["declared_in"] = NotInSnapshot,
                    ["source"] = null,
                });
            if (ancestors.Count > 0)
                ctx.Report.Table("ancestors",
                    patchedUp.Count > 0
                        ? ["name", "def_type", "abstract", PatchOpsName, "declared_in", "source"]
                        : ["name", "def_type", "abstract", "declared_in", "source"],
                    ancestors);

            if (patchedUp.Count > 0)
                ctx.Report.Notice(NoticeKind.Boundary,
                    $"上方另有 {Tally.Complete(patchedUp.Count).Render("ancestor")}被 patch 点名（「Name 的 patch 数」列）；" +
                    $"{label} 从这些祖先继承的内容为这些 patch 修改后的结果。");

            if (pathFilter is { Length: > 0 }) Witnesses(ctx, node, chain, pathFilter, exactPath);

            if (node.Name is { Length: > 0 })
            {
                var children = ctx.Db.NodesInheritingFrom(node.Name);
                var shown = limit.IsAll ? children : children.Take(limit.Effective).ToList();

                ctx.Report.CountNotice(Tally.Of(shown.Count, children.Count), "direct child");

                if (shown.Count > 0)
                    ctx.Report.Table("children", ["name", "def_name", "def_type", "abstract", "declared_in"],
                        shown.Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["name"] = n.Name,
                            ["def_name"] = n.DefName,
                            ["def_type"] = n.DefType,
                            ["abstract"] = n.Abstract,
                            ["declared_in"] = n.SourceMod,
                        }).ToList());

                if (node.Abstract && children.Count > 0)
                {
                    var concrete = children.FirstOrDefault(c => !c.Abstract && c.DefName is { Length: > 0 });
                    if (concrete is not null)
                        ctx.Report.Notice(NoticeKind.NextStep,
                            $"该节点的字段在合并进非 Abstract 子节点、运行 patch 之后的样子，见 'rimsearcher get {concrete.DefName}'。", teach: true);
                }
            }
        }

        ctx.Report.EndItems();
        ctx.Report.Absent(DataLayers.PostPatchInheritanceRow(ctx.Db, ctx.SnapshotName ?? ""));

        foreach (var (askedFor, hit) in found.Where(f => f.Nodes.Count > 1))
            ctx.Report.Notice(NoticeKind.Boundary,
                $"有 {Tally.Complete(hit.Count).Render("XML node")}对应 '{askedFor}'，已全部列出。");

        foreach (var (askedFor, hit) in found)
        {
            var sameName = ctx.Db.GetDefsNamed(askedFor);
            var inLayer = hit.Count(n => string.Equals(n.DefName, askedFor, StringComparison.OrdinalIgnoreCase));
            var outside = sameName.Count - inLayer;
            if (outside == 0) continue;

            var types = sameName
                .Where(d => !hit.Any(n => string.Equals(n.DefName, askedFor, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(n.DefType, d.DefType, StringComparison.OrdinalIgnoreCase)))
                .Select(d => d.DefType)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ctx.Report.Notice(NoticeKind.Boundary,
                $"继承层之外另有 {Tally.Complete(outside).Render("def")} 同名 '{askedFor}'（def 类型：{NameList.Render(types, Limits.MaxSuggestions)}），未声明 " +
                $"Name=、ParentName= 或 Abstract=。普通 " +
                $"def 不参与继承，因此 'rimsearcher get {askedFor}' 数出 {Tally.Complete(sameName.Count).Render("def")}，" +
                $"本命令数出 {Tally.Complete(hit.Count).Render("XML node")}。");
        }

        return 0;
    }

    private static void Witnesses(CommandContext ctx, XmlNodeRow node,
                                  IReadOnlyList<XmlNodeRow> chain, string pathFilter, bool exactPath)
    {
        var holding = exactPath ? "exactly at" : "containing";

        var layers = new List<XmlNodeRow>();
        if (node.Name is { Length: > 0 }) layers.Add(node);
        layers.AddRange(chain.Where(n => n.Name is { Length: > 0 }));

        if (layers.Count == 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{node.DefName ?? node.Name} 未声明 Name=，也不继承自任何声明 Name= 的节点，因此该节点上方没有可检验的层。");
            return;
        }

        string? reference = null;
        (string DefName, string DefType)? exclude = null;
        IReadOnlyCollection<string>? labels = null;
        var about = new List<KeyValuePair<string, object?>>();
        if (node.DefName is { Length: > 0 })
        {
            var self = ctx.Db.GetDefsNamed(node.DefName)
                          .FirstOrDefault(d => string.Equals(d.DefType, node.DefType, StringComparison.OrdinalIgnoreCase));
            if (self is not null)
            {
                exclude = (self.DefName, self.DefType);
                var fields = ctx.Db.Fields(self.Id, int.MaxValue, [pathFilter], exactPath: exactPath);
                var values = fields.Rows.Select(r => r.Value ?? "").Distinct(StringComparer.Ordinal).ToList();
                labels = LabelPaths(fields.Rows, ctx.Db.Fields(self.Id, int.MaxValue).Rows);

                if (values.Count == 1) reference = values[0];
                about.Add(new(ReferenceKey, reference ?? "无"));
                about.Add(new("reference_from", self.DefName + " 自身"));
                about.Add(new("self_fields", fields.Rows.Count));
                if (node.Name is not { Length: > 0 })
                    about.Add(new(SelfWritten, ctx.Db.XmlLinesWritten(
                        node.DefType, null, node.DefName, pathFilter, exactPath, labels)));
                if (values.Count > 1)
                {
                    about.Add(new("distinct_values", values.Count));
                    about.Add(new("self_values", NameList.Render(values.Select(Quote).ToList(), 4)));
                    about.Add(new("next", $"{ctx.Without(PathContains, ExactPath)} --exact-path {fields.Rows[0].Path}"));
                }
            }
        }

        var byMode = false;
        if (reference is null && exclude is null && node.Name is { Length: > 0 })
        {
            var dom = ctx.Db.DominantValue(node.Name, pathFilter, exactPath);
            if (dom.Value is not null)
            {
                reference = dom.Value;
                byMode = true;
                about.Add(new(ReferenceKey, reference));
                about.Add(new("reference_from", node.Name + " 下的众数"));
                about.Add(new("mode_defs", dom.Defs));
                about.Add(new("distinct_values", dom.Distinct));
            }
        }
        if (about.Count > 0) ctx.Report.Detail(ReferenceKey, about);

        var sameColumn = byMode ? SameAsMode : SameValue;

        var (typeWithPath, typeDefs) = node.DefType is { Length: > 0 }
            ? (ctx.Db.TypeDefsWithPath(node.DefType, [pathFilter], exactPath).Defs,
               ctx.Db.CountDefsOfType(node.DefType, ctx.Unscoped()))
            : (0, 0);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = 0;
        foreach (var layer in layers)
        {
            var w = ctx.Db.Witnesses(layer.Name!, pathFilter, reference, exclude, exactPath);
            truncated = Math.Max(truncated, w.Truncated);
            var row = new Dictionary<string, object?>
            {
                ["layer"] = layer.Name,
                ["other_defs"] = w.Descendants,
                ["with_path"] = w.WithPath,
                [Written] = ctx.Db.XmlLinesWritten(
                    layer.DefType, layer.Name!, layer.Abstract ? null : layer.DefName, pathFilter, exactPath, labels),
            };
            if (reference is not null) row[sameColumn] = w.SameValue;
            row[CutShort] = w.Truncated;
            row["type_with_path"] = typeWithPath;
            row["type_defs"] = typeDefs;
            rows.Add(row);
        }

        var columns = new List<string> { "layer", Written, "other_defs", "with_path" };
        if (reference is not null) columns.Add(sameColumn);
        if (truncated > 0) columns.Add(CutShort);
        if (typeDefs > 0) columns.AddRange(["type_with_path", "type_defs"]);
        var keep = columns.ToHashSet(StringComparer.Ordinal);
        ctx.Report.Table("witnesses", columns,
            rows.Select(r => (IReadOnlyDictionary<string, object?>)r.Where(kv => keep.Contains(kv.Key))
                                .ToDictionary(kv => kv.Key, kv => kv.Value)).ToList());
    }

    private static HashSet<string> LabelPaths(IReadOnlyList<FieldRow> matched, IReadOnlyList<FieldRow> all)
    {
        var elements = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in matched)
        {
            var close = row.Path.IndexOf(']', StringComparison.Ordinal);
            if (close > 0) elements.Add(row.Path[..(close + 1)]);
        }
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (elements.Count == 0) return labels;
        foreach (var cell in all)
        {
            var close = cell.Path.IndexOf(']', StringComparison.Ordinal);
            if (close <= 0 || !elements.Contains(cell.Path[..(close + 1)])) continue;
            if (cell.Value is not { Length: > 0 } v || !v.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
            labels.Add(cell.Path[..cell.Path.IndexOf('[', StringComparison.Ordinal)] + "." + v);
        }
        return labels;
    }

    public const string Written = "written";

    public const string SelfWritten = "self_written";

    public const string CutShort = "cut_short";

    public const string ReferenceKey = "reference";

    private static string Quote(string v) => v.Length == 0 ? "空值" : $"'{v}'";
}
