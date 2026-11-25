using RimSearcher.Cli;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class GetCommand : Command
{
    private const string GetFieldsInstead =
        "get 把所给字段路径显示为一张表的列：" +
        "'rimsearcher get <defName> --fields <路径>'。";

    internal static readonly CliOption LimitOption = CommonOptions.Limit("字段") with
    {
        Help = CommonOptions.Limit("字段").Help +
               "数的是每块内的字段；输出几块由所给名字或 --type 决定。配 --fields 时数的是行，每个 def 一行。",
    };

    internal static readonly CliOption PathContains = CliOption.Repeat("path-contains") with
    {
        Aliases = ["filter", "grep", "values", "path"],
        BarMeansOr = true,
        Placeholder = "<文本>",
        Help = "只显示含此文本的字段路径。重复给出或写成 'A|B' 以放宽筛选。" +
               "译文表也按 --path-contains 筛选，路径与键都参与匹配，列表下标的两种写法（'[0]' 与 '.0.'）都会尝试。" +
               CommonOptions.AnyIndexNote,
        Narrows = true,
    };

    internal static readonly CliOption ExactPath = CommonOptions.ExactPathFilter();

    private static readonly CliOption TypeOption = CommonOptions.Type with
    {
        Narrows = false,
        Help = CommonOptions.Type.Help,
    };

    private static readonly CliOption ScopeOption = CommonOptions.Scope with
    {
        Narrows = false,
        Help = CommonOptions.Scope.Help + "在这里 --scope 按声明 def 的 mod 选取 def。",
    };

    private static readonly CliOption AllOption = CliOption.Flag("all") with
    {
        Help = "配 --type 且不给 def 名时：该类型的每个 def，与不加 --all 相同。",
    };

    internal static readonly CliOption Defaults = CliOption.Flag("defaults") with
    {
        Help = "另列值与声明类型新建实例已有值相同的字段。"
             + "在这些行上，「取自」列区分自身 XML 写有该值的 def（XML=缺省，祖先写有相同文本时为 XML=parent）"
             + "与 XML 从未提及该字段的 def（缺省）；parent 与「在 <container> 下」保持说明中给出的含义。"
             + "--path-contains 点名的字段无论如何都会显示。",
    };

    internal static readonly CliOption FieldsOption = CliOption.Repeat("fields") with
    {
        BarMeansOr = true,
        Placeholder = "<路径>",
        Help = "改为输出一张表：每个 def 一行，此处点名的每条字段路径一列，按给出顺序。" +
               "重复给出或写成 'A,B' 或 'A|B' 可得多列。" +
               "列按整条路径匹配，同 --exact-path；'[]' 表示任意下标，" +
               "'statBases[stat=Mass].value' 读取 stat 为 Mass 的 statBases 条目。" +
               "'statBases.Mass' 也按这种方式读取，此时该列的表头显示实际读取的路径。" +
               "def_type、class、declared_in 与 source 也可作列。" +
               "单元格为值；多个值以 '; ' 连接；「（N 个字段）」表示该路径是一个容器，其下有 N 个字段；" +
               "空表示该 def 在此路径上没有值；「?」表示此处没有、但该 def 的导出被截断，因此可能有。" +
               "与新建实例相同的值照常输出，块形态的「取自」列不输出。" +
               "不能与 --path-contains、--exact-path 或 --defaults 同时传入，这三者选取的是块内的行。",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "get",
        Aliases = ["show", "inspect", "def"],
        Summary = "完整显示 def 的身份、字段与译文，给出多个名字或单独给出 --type 时每个 def 输出一块。",
        Remarks =
            "字段路径反映导出快照时游戏内存中合并且 patch 后的实际形态，PatchOperation 与继承均已应用。\n\n" +
            "「文件」行是游戏为该 def 报告的裸文件名，不含目录，" +
            "指该 mod 的 Defs 文件夹中的文件（哪个 mod 见上方的「声明于」行）；" +
            "本命令不确认该文件仍在。由代码生成的 def 没有 XML 文件，该行显示 ImpliedDefs（由代码生成）。\n\n" +
            "「取自」列说明每个值的来源。XML：该 def 自身的 XML 写有此路径，即 PatchOperationReplace 找到的节点。" +
            "XML=parent：该 def 写有此路径，文本与最近一个写有它的祖先相同，因此这一行不改变任何值。" +
            "XML=缺省：该 def 写有此路径，没有祖先写有它，且值与声明类型新建实例已有的值相同，" +
            "因此这一行同样不改变任何值。parent：只有祖先写有此路径，因此该节点不在这个 def 的 xpath 上，" +
            "只有 Add 能到达。缺省：没有 XML 写有它，值与新建实例的值相同。" +
            "运行时：没有 XML 写有它，值与新建实例不同，因此由加载 def 时运行的代码设定。" +
            "缺省|运行时：没有 XML 写有它，声明类型无法构造以作比较，因此两者皆有可能。" +
            "在 <container> 下：XML 写有该容器，但这一行无法对应到其中某一行。" +
            "costList[0].thingDef 这类下标路径的来源取自 costList.Steel 这类 def 名标签所在的 XML 行，" +
            "该行由该 def 或其祖先写出。" +
            "输出注明读的是哪份 XML。「读于全部 patch 运行之后」指每个 PatchOperation 运行后合并的 XML，" +
            "patch 写入的行带 +patch 后缀（XML+patch、parent+patch 等），Replace 仍能找到该节点。" +
            "「读于 patch 运行之前」指 mod 文件中原样写出的 XML，因此其中没有由 patch 加入的节点，" +
            "没有 XML 写的行一律读作未写。" +
            "XML 写出而内部为空的元素（空列表或空字典，或为清除祖先所设而留空的引用）没有值，" +
            "因此没有行；另一张带「空元素」列的表列出这些元素，取值同为 XML / XML=parent / parent / +patch，" +
            "导出丢失字段路径的 def 除外。\n\n" +
            "译文表的「出处」列说明每行的去向。「生效」为游戏显示的内容。" +
            "「语言包内，未应用」为语言包带有而游戏未注入的记录：键不对应该 def 的任何可注入槽位时为" +
            "「（键无对应槽位）」，此时该行的「字段路径」为改写后的键，与字段表中任何一行都不对应；" +
            "槽位存在但游戏标为不可译时为「（槽位不可译）」。单独的「语言包内」表示快照未记录游戏是否应用该记录。" +
            "「文件（<mod>）」读自该 mod 的语言文件；mod 已安装但未启用、游戏从未读取时带「，未启用」；" +
            "同一个键出现在其中多个文件里时带「，N 个文件」。两种槽位后缀以同样方式附在文件行上。\n\n" +
            IndexGap.HelpGet,
        Positionals =
        [
            new CliPositional
            {
                Name = "defName",
                NormalizeBackslashesInRefused = true,
                Required = false,
                Variadic = true,
                Help = "确切的 def 名。只知道一部分时见 'rimsearcher search'。给出多个名字时按给出顺序每个一块。" +
                       "不给名字而给出 --type 时输出该类型的每个 def" +
                       "（--all 是同一含义的显式写法）。",
            },
        ],
        Options =
        [
            LimitOption,
            PathContains,
            ExactPath,
            TypeOption,
            ScopeOption,
            AllOption,
            Defaults,
            FieldsOption,
        ],
        Refused =
        [
            new() { Name = "field", ValuePlaceholder = "<路径>", Where = GetFieldsInstead },
        ],
        Examples =
        [
            "rimsearcher get Apparel_ShieldBelt",
            "rimsearcher get Apparel_ShieldBelt --path-contains statBases",
            "rimsearcher get Bullet_Revolver",
            "rimsearcher get Bullet_Revolver --defaults",
            "rimsearcher get Gun_Autopistol Gun_Revolver --type ThingDef",
            "rimsearcher get --type GeneDef --defaults --json",
        ],
        JsonKeys =
        [
            new("defs",
                "每个带此名字的 def 一个对象，各含 def（身份）、fields（path/value/from 行）、" +
                "empty_in_xml（empty_element/from：该 def 的 XML 写出而内部为空、在 fields 中没有行的元素）" +
                "与 translations。三张内表恒在，为空时为空数组。ShaderDef 另带 shader_files（stage/file/path：" +
                "每个着色器阶段加载的 GLSL 文件，按导出该快照时游戏的定位方式定位）。" +
                "即使只有一个 def，defs 仍为数组，因为一个名字可同时属于多个 def 类型。" +
                "给出多个名字时对象按名字的给出顺序排列，单独给出 --type 时按 def 名顺序；" +
                "没有匹配的名字在这里没有对象，在 notes 中有一条引用它的提示。" +
                "路径筛选在该 def 上没有匹配时，若成因可判定，对象另带 index_gap（asked、state、next），" +
                "状态见上方说明；筛选使译文表为空时，另带该表的 empty_because。" +
                "配 --fields 时为空，答案在 rows 中。",
                Rows: true),
            new("rows",
                "仅配 --fields 时出现，每个 def 一行：def_name，行中含多个 def 类型时另有 def_type，" +
                "然后每列一个键，以实际读取的路径命名。单元格即表中输出的文本：值，多个值以 '; ' 连接，" +
                "容器为「（N 个字段）」，该 def 导出被截断处为「?」，该 def 在此路径上没有值时为 null。"),
            new("absent",
                "这些 def 会用到、而本快照中不齐备的每个层一行：layer、state、next；所有这类层都齐备时为空。" +
                "目前包括：未测定价格时 ThingDef 上的 economy（state 为 已跳过 / 无法采集）、" +
                "导入时未扫描 mod 语言文件时的 disk_translations（已跳过 / 未配置），" +
                "以及在译文表没有 key 列的快照上配 --path-contains 时的 injection_keys（空）；" +
                "next 为补齐该层的命令。",
                Rows: true),
            new(UnloadedTable,
                "所查名字没有已加载的 def 承载时，每条 DefInjected 条目一行：name、def_type、path、translated、" +
                "original、source；该名字的 found_as 行为「无对应 def 的译文」。每个名字都是 def 或哪里都没有时为空。",
                Rows: true),
            EmptyCause.JsonKey,
            NameLookup.JsonKey,
        ],
    };

    public override bool ReadsTranslations => true;

    private const string UnloadedTable = "unloaded_translations";
    private static readonly string[] UnloadedColumns = ["name", "def_type", "path", "translated", "original", "source"];

    public override int Run(CommandContext ctx)
    {
        var wantType = ctx.Args.Value(TypeOption);
        var given = ctx.Args.Positionals;
        var wantAll = ctx.Args.Flag(AllOption);
        if (given.Count == 0 && wantType is not { Length: > 0 })
            throw new CliUsageException(wantAll
                ? "get --all 缺少 --type <DefType>：--all 输出该类型的每个 def。"
                : "get 缺少 <defName>：至少给出一个，或单独给出 --type <DefType> 以输出该类型的每个 def。");
        if (wantAll && given.Count > 0)
            throw new CliUsageException(
                "get --all 输出 --type 所指类型的每个 def，不接受 def 名；去掉名字或去掉 --all。");

        var columns = FieldsTable.Columns(ctx);
        var asTable = columns.Count > 0;

        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in given)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"以下名字重复给出，只输出一次：{NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}。");

        var single = names.Count == 1;
        var blocks = new List<DefRow>();
        var wholeType = false;

        var scope = ctx.Scope();
        if (names.Count == 0)
        {
            var unscoped = ctx.Db.DefsOfType(wantType!);
            var all = unscoped.Where(d => scope.Includes(d.SourceMod)).ToList();
            if (all.Count == 0 && unscoped.Count > 0)
            {
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ScopeOption), unscoped.Count, ctx.Without(ScopeOption)));
                return 1;
            }
            if (all.Count == 0)
            {
                if (NameLookup.AsClass(ctx, wantType!, ctx.Unscoped()) is { } asClass)
                    NameLookup.Say(ctx, asClass);
                else
                    ctx.Report.Notice(NoticeKind.NextStep,
                        DefTypeMiss.Say(wantType!, ctx.Db.Types(ctx.Unscoped()).Select(t => t.Type), "get --type"));
                return 1;
            }
            blocks.AddRange(all);
            var where = all.Count < unscoped.Count ? "--scope 范围内" : "本快照中";
            var lead = wantAll
                ? $"{where}的全部 {all[0].DefType}"
                : Cjk.Join("未给出 def 名，因此以下为", where, $"的全部 {all[0].DefType}");
            ctx.Report.Notice(NoticeKind.Boundary, all.Count == 1
                ? $"{lead}：{Tally.Complete(1).Render("def")}。"
                : asTable
                    ? $"{lead}：{Tally.Complete(all.Count).Render("def")}，每个一行，按 def 名顺序。"
                    : $"{lead}：{Tally.Complete(all.Count).Render("def")}，每个一块，按 def 名顺序；" +
                      "块内的计数与提示只属于该块，块顶部的 defName 行标明是哪一个。");
            wholeType = true;
        }

        bool? typeUnknown = null;
        var keptNone = new List<string>();
        var collisions = new List<NameCollision.Entry>();
        var collisionsAt = ctx.Report.Mark;
        foreach (var name in names)
        {
            var allMatches = ctx.Db.GetDefsNamed(name);
            var matches = allMatches;

            if (wantType is { Length: > 0 } && matches.Count > 0)
            {
                var kept = matches.Where(d => string.Equals(d.DefType, wantType, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0)
                {
                    if (single)
                    {
                        if (ctx.UnknownDefType(wantType) is { } unknown) ctx.Report.Notice(NoticeKind.Filter, unknown);
                        ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(TypeOption), matches.Count, ctx.Without(TypeOption)));
                        return 1;
                    }
                    typeUnknown ??= !ctx.Db.Types(ctx.Unscoped()).Any(t => string.Equals(t.Type, wantType, StringComparison.OrdinalIgnoreCase));
                    if (typeUnknown == true)
                    {
                        keptNone.Add($"{name}（{string.Join("、", matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal))}）");
                        continue;
                    }
                    ctx.Report.Notice(NoticeKind.NextStep,
                        $"{name} 在本快照中存在，def 类型为 " +
                        $"{string.Join("、", matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal))}。");
                    continue;
                }
                matches = kept;
            }
            matches = matches.Where(d => scope.Includes(d.SourceMod)).ToList();

            if (matches.Count == 0)
            {
                var sighting = NameLookup.Locate(ctx, name, scope);
                if (sighting is not null)
                {
                    if (sighting.Where == NameLookup.Where.UnloadedTranslation)
                    {
                        NameLookup.Say(ctx, sighting with
                        {
                            Next = $"{CommandRegistry.ExeName} code-search {CommandContext.QuoteArg(name)}",
                        });
                        ctx.Report.AppendRows(UnloadedTable, UnloadedColumns,
                            ctx.Db.Translations(name).Select(t => (IReadOnlyDictionary<string, object?>)
                                new Dictionary<string, object?>
                                {
                                    ["name"] = t.DefName,
                                    ["def_type"] = t.DefType,
                                    ["path"] = t.Path,
                                    ["translated"] = t.Translated,
                                    ["original"] = t.Original,
                                    ["source"] = t.SourceFile,
                                }).ToList(), whole: ["translated", "original"]);
                    }
                    else NameLookup.Say(ctx, sighting);
                    if (single) return 1;
                    continue;
                }

                var known = ctx.Db.AllDefNames(Snapshot.ScopeFilter.Parse("all", ctx.Db.PackageIds(), ctx.Config));
                var close = Suggestion.Closest(known, name);

                ctx.Report.Notice(NoticeKind.NextStep,
                    $"本快照中没有名为 '{name}' 的 def、def 类型、Class、mod 或 Abstract 节点，其他已登记快照中也没有这个 def 名或 XML 节点名。" +
                    Suggestion.Say(close));
                if (single) return 1;
                continue;
            }

            if (allMatches.Count > 1)
            {
                var others = allMatches.Where(d => !matches.Contains(d))
                                       .Select(d => d.DefType)
                                       .ToList();
                collisions.Add(new NameCollision.Entry(
                    name, allMatches.Count,
                    matches.Select(d => d.DefType).Distinct(StringComparer.Ordinal).ToList(),
                    others,
                    allMatches.Select(d => d.DefName).Distinct(StringComparer.Ordinal).ToList()));
            }

            blocks.AddRange(matches);
        }

        foreach (var said in NameCollision.Say(collisions, names.Count).Reverse())
            ctx.Report.InsertNotice(collisionsAt, NoticeKind.Boundary, said);

        if (keptNone.Count > 0)
            ctx.Report.Notice(NoticeKind.NextStep, DefTypeMiss.KeepsNone(wantType!, keptNone));

        if (blocks.Count == 0) return 1;

        if (asTable) return FieldsTable.Render(ctx, blocks, columns, wholeType);

        var byType = blocks.Select(b => b.DefType).Distinct(StringComparer.Ordinal).Count() == blocks.Count;
        if (blocks.Count > 1 && !wholeType)
            ctx.Report.Notice(NoticeKind.Boundary, (single
                ? $"{Tally.Complete(blocks.Count).Render("def")}，每个一块；下面的每条计数与提示都属于其所在的块，" +
                  Cjk.Join("块顶部的", byType ? "def 类型" : "defName", "行标明是哪一个。")
                : $"{Tally.Complete(blocks.Count).Render("def")}，每个一块，按所给名字的顺序；" +
                  "块内的计数与提示只属于该块，块顶部的 defName 行标明是哪一个。"), teach: true);

        var limit = ctx.Limit(LimitOption);
        var (paths, exactPath) = ctx.Args.PathFilters(PathContains, ExactPath);
        var pathSpelling = exactPath ? "--exact-path" : "--path-contains";
        var pathOption = exactPath ? ExactPath : PathContains;
        var fieldPaths = paths.Select(InjectionKey.ToFieldPath).ToList();

        var alone = blocks.Count == 1;

        var shortLayers = new List<LayerRow>();
        void Short(LayerRow row)
        {
            if (!row.Complete && shortLayers.All(r => r.Layer != row.Layer)) shortLayers.Add(row);
        }

        var xmlAt = ctx.Report.Mark;
        string? xmlHoisted = null;
        if (blocks.Count > 1000) ctx.Db.PreloadXmlWritten();
        foreach (var def in blocks)
        {
            var namesakes = ctx.Db.GetDefsNamed(def.DefName).Count;
            ctx.Report.Item("defs");

            var pairs = new List<KeyValuePair<string, object?>>
            {
                new("def_name", def.DefName),
                new("def_type", def.DefType),
                new("label", def.Label),
                new("description", paths.Count > 0 ? Clip(def.Description) : def.Description),
                new("class", def.Class),
                new("declared_in", def.SourceMod),
                new("source", def.Generated
                    ? $"{def.SourceFile}（由代码生成）"
                    : def.SourceFile),
            };

            var named = ctx.Db.NodesNamed(def.DefName)
                              .Where(n => string.Equals(n.DefName, def.DefName, StringComparison.Ordinal))
                              .ToList();
            var xmlNode = named.FirstOrDefault(n => DefTypes.Same(n.DefType, def.DefType))
                       ?? (named.Count == 1 && namesakes == 1 ? named[0] : null);
            if (xmlNode?.ParentName is { Length: > 0 } parentName)
                pairs.Add(new("inherits_from", parentName));

            if (!alone) ctx.Report.Detail("def", pairs);

            var withDefaults = ctx.Args.Flag(Defaults) || paths.Count > 0;
            var (fields, matched, total, defaulted, matchedPaths) =
                ctx.Db.Fields(def.Id, limit.Effective, fieldPaths, includeDefaults: withDefaults,
                              exactPath: exactPath);

            var xmlLayer = "「取自」列依据的 XML " +
                           (ctx.Db.Meta.IndexesPostPatchXml ? "读于全部 patch 运行之后" : "读于 patch 运行之前") + "。";
            var xmlLayerSaid = false;
            void SayXmlLayer()
            {
                xmlLayerSaid = true;
                if (alone) ctx.Report.Notice(NoticeKind.Boundary, xmlLayer);
                else xmlHoisted = xmlLayer;
            }

            var xmlMarks = ctx.Db.XmlWrittenMarks(def.DefType, def.DefName, out var xmlContainers, out var xmlTexts,
                                                  out var xmlPatched, out var parentLists, out var xmlOccurrences);
            var allCells = ctx.Db.AllFieldCells(def.Id);
            var cellsByElement = XmlOrigin.CellsByElement(allCells);

            var truncatedBy = def.FieldsTruncated > 0 ? ctx.Db.TruncationCausesFor(def.Id) : null;
            var rowsMissing = truncatedBy?.Missing ?? 0;

            var allEmpty = rowsMissing > 0
                ? []
                : XmlOrigin.EmptyElements(xmlMarks, xmlTexts, xmlPatched, parentLists, allCells.Select(c => c.Path),
                                            cellsByElement);
            var empties = paths.Count > 0
                ? allEmpty.Where(e => SnapshotDb.PathFilterMatches(e.Path, fieldPaths, exactPath)).ToList()
                : allEmpty;
            var shownEmpties = limit.IsAll ? empties : empties.Take(limit.Effective).ToList();

            var owner = alone ? "" : $"{def.DefName}（{def.DefType}）";
            if (paths.Count > 0)
            {
                if (matched == 0)
                {
                    var asValue = paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList();
                    var asEntryKey = paths.Except(asValue)
                                          .Select(t => (Asked: t, List: EntryKeys.TailListOnDef(allCells, t)))
                                          .Where(x => x.List is not null).ToList();

                    var (kin, kinPaths) = asValue.Count > 0 || asEntryKey.Count > 0
                        ? (0, 0)
                        : ctx.Db.TypeDefsWithPath(def.DefType, fieldPaths, exactPath);

                    ctx.Report.Notice(NoticeKind.Boundary,
                        Cjk.Join(owner.Length > 0 ? Cjk.Join(owner, "上") : "", "没有", exactPath ? "恰好为" : "含",
                                 PathFilterText.Say(paths), "的字段路径；该 def 在索引中有", Tally.Complete(total).Render("field"),
                                 "。") +
                        (kin > 0
                            ? Cjk.Join("该类型的其他 def 则有含此文本的字段路径：", Tally.Complete(kin).Render("def"), "，分布在",
                                       Tally.Complete(kinPaths).Render("field path"), "上。")
                            : ""));
                    if (shownEmpties.Count > 0)
                        SayXmlLayer();
                    foreach (var t in asValue)
                        IndexGap.Say(ctx, t, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {t}");
                    foreach (var (t, list) in asEntryKey)
                        IndexGap.Say(ctx, t, IndexGap.ValueNotPath,
                                     $"{CommandRegistry.ExeName} get {CommandContext.QuoteArg(def.DefName)} --path-contains {list}");
                    var kinNext = kin > 0
                        ? $"{CommandRegistry.ExeName} fields {def.DefType} " +
                          string.Join(" ", paths.Select(p => $"{pathSpelling} {p}"))
                        : null;
                    if (empties.Count > 0)
                        IndexGap.Say(ctx, string.Join(", ", paths), IndexGap.EmptyInXml, kinNext);
                    else if (kin > 0)
                        IndexGap.Say(ctx, string.Join(", ", paths), IndexGap.NullOrEmptyOnThisDef, kinNext);
                    else if (asValue.Count == 0 && asEntryKey.Count == 0)
                        NameLookup.Say(ctx, paths.Where(p => !p.Contains('.'))
                                                 .SelectMany(p => NameLookup.Beyond(ctx, p, NameLookup.Side.Data, def.DefType)));
                }
                else
                {
                    var whole = matchedPaths.Count(x => PathSegments.IsWholeSegment(x, fieldPaths));

                    var alsoValue = whole == 0
                        ? paths.Where(t => ctx.Db.ValueHits(def.Id, t) > 0).ToList()
                        : [];

                    ctx.Report.Notice(NoticeKind.Filter,
                        PathFilterSummary.Say(paths, owner.Length > 0 ? Cjk.Join(owner, "上") : "该 def 上", "field",
                                              matched, total, whole) +
                        (alsoValue.Count > 0
                            ? $"该 def 也以 {PathFilterText.Say(alsoValue)} 作为某个字段的值。"
                            : ""));
                    foreach (var t in alsoValue)
                        IndexGap.Say(ctx, t, IndexGap.ValueNotPath, $"{CommandRegistry.ExeName} where --value {t}");
                    if (fields.Count < matched)
                        ctx.Report.Notice(NoticeKind.Truncation,
                            $"{Tally.Of(fields.Count, matched).RenderTotalFirst("field")}。", count: Tally.Of(fields.Count, matched));

                    Advisory.NoteAuthoredSiblings(ctx, fields.Where(f => f.Default != Contract.DefaultState.Same)
                                                             .Select(f => (def.DefName, def.Id, f.Path)));
                }
            }
            else
            {
                var listable = withDefaults ? total : total - defaulted;
                ctx.Report.CountNotice(Tally.Of(fields.Count, listable), "field",
                    alone ? "" : $"所属 def 为 {def.DefName}（{def.DefType}）。");

                if (!withDefaults && defaulted > 0)
                {
                    var shownIdx = fields.SelectMany(f => PathSegments.IndexPrefixes(f.Path))
                                         .ToHashSet(StringComparer.Ordinal);
                    var hiddenIdx = matchedPaths.SelectMany(PathSegments.IndexPrefixes)
                                                .Distinct(StringComparer.Ordinal)
                                                .Where(x => !shownIdx.Contains(x))
                                                .ToList();
                    xmlLayerSaid = true;
                    if (!alone) xmlHoisted = xmlLayer;
                    ctx.Report.Notice(NoticeKind.Filter,
                        Cjk.Join("未列出：值与声明类型新建实例已有值相同的", Tally.Complete(defaulted).Render("field")) + "。" +
                        Cjk.Join("快照为该 def 存有", Tally.Complete(total).Render("field path")) +
                        "；为 null 或空集合的字段从未进入索引。" + (alone ? xmlLayer : "") +
                        (hiddenIdx.Count > 0
                            ? "下列列表条目的字段均未显示：" +
                              $"{NameList.Render(hiddenIdx, Limits.MaxSuggestions)}。"
                            : ""));
                }
            }

            if (!xmlLayerSaid && (fields.Count > 0 || shownEmpties.Count > 0))
                SayXmlLayer();

            if (def.FieldsTruncated > 0)
            {
                var said = ExportCap.OnDef(truncatedBy!);
                var missing = rowsMissing;
                ctx.Report.Notice(NoticeKind.Boundary,
                    missing > 0
                        ? "该 def 的导出被截断：" + said + "。" +
                          Cjk.Join("加上已进入索引的", Tally.Complete(total).Render("field path"), "，该 def 合计",
                                   Tally.AtLeast(total + missing).Render("field path")) + "。"
                        : (total == 1
                              ? "该 def 唯一的字段路径已进入索引"
                              : Cjk.Join("该 def 的", Tally.Complete(total).Render("field path"), "全部已进入索引")) +
                          "；导出器截掉的是文本：" + said +
                          "，因此这些行只显示值的前段。");
            }

            if (alone) ctx.Report.Detail("def", pairs);

            var postPatch = ctx.Db.Meta.IndexesPostPatchXml;
            ctx.Report.Table("fields", ["path", "value", FieldFrom.Column],
                fields.Select(f =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["path"] = f.Path,
                        ["value"] = f.Value,
                        [FieldFrom.Column] = FieldFrom.Render(XmlOrigin.Resolve(
                            f.Path, xmlMarks, xmlContainers, cellsByElement, xmlTexts, xmlPatched, xmlOccurrences),
                            f.Default, postPatch),
                    };
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList(), whole: ["value"]);

            Completeness.NoteWidelySharedValues(ctx, def, fields, withDefaults, defaulted);

            ctx.Report.TruncationNotice(Tally.Of(shownEmpties.Count, empties.Count), "empty element");
            ctx.Report.Table("empty_in_xml", ["empty_element", FieldFrom.Column],
                shownEmpties.Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["empty_element"] = e.Path,
                    [FieldFrom.Column] = FieldFrom.Render(e.Mark, null, postPatch),
                }).ToList());

            if (def.DefType == "ShaderDef")
                ctx.Report.Table("shader_files", ["stage", "file", "path"],
                    ctx.Db.ShaderSources(def.DefName).Select(s =>
                        (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                        {
                            ["stage"] = s.Stage,
                            ["file"] = s.File,
                            ["path"] = s.ResolvedPath ?? (s.Unresolved == RimSearcher.Contract.IntermediateFormat.ShaderUnresolvedNoResolver
                                ? "（导出器无法调用游戏的着色器解析器）"
                                : "（游戏按此名无法解析到文件）"),
                        }).ToList(), whole: ["path"]);

            if (!ctx.Db.Meta.IndexesPostPatchXml)
            {
                var node = ctx.Db.NodesNamed(def.DefName)
                    .FirstOrDefault(n => string.Equals(n.DefType, def.DefType, StringComparison.Ordinal)
                                      && string.Equals(n.DefName, def.DefName, StringComparison.Ordinal));
                var xpaths = node is null
                    ? 0
                    : (string.IsNullOrEmpty(node.Name) ? 0 : node.PatchOps)
                      + node.PatchOpsDefName + node.PatchOpsLabel;
                if (xpaths > 0)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        Cjk.Join("有", Tally.Complete(xpaths).Render("patch xpath"), "指向该 def。") +
                        $"按各 xpath 指向该 def 的方式分列的计数见 'rimsearcher inherit {def.DefName}'。");
            }

            if (ctx.Db.EconomyState == Contract.IntermediateFormat.EconomyStateOk)
            {
                if (DefTypes.Same(def.DefType, "ThingDef") && ctx.Db.EconomyByName(def.DefName).Count > 0)
                    ctx.Report.Notice(NoticeKind.NextStep,
                        "marketValue、costToMake 与 workToProduce 属经济层：" +
                        $"见 'rimsearcher economy {def.DefName}'。", teach: true);
            }
            else if (DefTypes.Same(def.DefType, "ThingDef"))
                Short(DataLayers.EconomyRow(ctx.Db, ctx.SnapshotName ?? ""));

            var allTranslations = (IReadOnlyList<TranslationRow>)ctx.Db.Translations(def.DefName)
                .Where(t => t.DefType is null || DefTypes.Same(t.DefType, def.DefType))
                .ToList();
            var byNameOnly = allTranslations.Count > 0 && allTranslations.All(t => t.DefType is null);
            var beforePathFilter = allTranslations.Count;
            if (paths.Count > 0)
            {
                var needles = paths
                    .SelectMany(p => new[] { p, InjectionKey.ToFieldPath(p) })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                allTranslations = allTranslations
                    .Where(t => SnapshotDb.PathFilterMatches(t.Path, needles, exactPath)
                             || (t.Key is { } key && SnapshotDb.PathFilterMatches(key, needles, exactPath)))
                    .ToList();
            }
            var translations = limit.IsAll
                ? allTranslations
                : allTranslations.Take(limit.Effective).ToList();

            if (translations.Count > 0)
                ctx.Report.CountNotice(Tally.Of(translations.Count, allTranslations.Count),
                    "translation",
                    alone ? "" : $"所属 def 为 {def.DefName}（{def.DefType}）。");

            if (paths.Count > 0 && translations.Count == 0 && beforePathFilter > 0)
            {
                ctx.Report.EmptyBecause(
                    new EmptyCause(ctx.FilterAsGiven(pathOption), beforePathFilter, ctx.Without(pathOption)),
                    "translation");
                if (!ctx.Db.HasInjectionKeyRows)
                    Short(DataLayers.InjectionKeysRow(ctx.Db, ctx.SnapshotName ?? ""));
            }

            ctx.Report.Table("translations",
                ["path", "key", "translated", "original", "language", "origin"],
                translations.Select(t =>
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["path"] = t.Path,
                        ["translated"] = t.Translated,
                        ["original"] = t.Original,
                        ["language"] = t.Language,
                        ["origin"] = OriginCell(t),
                    };
                    row["key"] = t.Key is { } k && k != t.Path ? k : null;
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList(), whole: ["translated", "original"]);

            if (translations.Count > 0)
            {
                Short(DataLayers.DiskTranslationsRow(ctx.Db, ctx.Config, ctx.SnapshotName ?? ""));

                if (!ctx.Db.HasInjectionKeyRows && paths.Count > 0)
                    Short(DataLayers.InjectionKeysRow(ctx.Db, ctx.SnapshotName ?? ""));

                if (byNameOnly && namesakes > 1)
                    ctx.Report.Notice(NoticeKind.Boundary,
                        $"这些行不带 def 类型（语言文件的键为 {def.DefName}.<field>），而 " +
                        $"{Tally.Complete(namesakes).Render("def")} 同名；游戏同样按名字注入这些译文。");
            }
        }

        ctx.Report.EndItems();
        if (xmlHoisted is not null)
            ctx.Report.InsertNotice(xmlAt, NoticeKind.Boundary, xmlHoisted);
        ctx.Report.Absent(shortLayers);

        return 0;
    }

    private static string OriginCell(TranslationRow t)
    {
        var slot = t.KeyState switch
        {
            InjectionKey.State.NoSlot => "（键无对应槽位）",
            InjectionKey.State.Refused => "（槽位不可译）",
            _ => "",
        };
        if (t.Origin == TranslationOrigin.Runtime)
            return (t.Applied switch { true => InEffect, false => "语言包内，未应用", _ => "语言包内" }) + slot;
        var files = t.SourceFileCount is > 1 ? $"，{t.SourceFileCount} 个文件" : "";
        return $"文件（{t.SourceMod}{(OutsideSnapshot(t) ? NotEnabled : "")}{files}）" + slot;
    }

    public const string InEffect = "生效";
    public const string NotEnabled = "，未启用";
    public static bool OutsideSnapshot(TranslationRow t) => t.Origin == TranslationOrigin.HarvestedOutside;

    private static string? Clip(string? text)
    {
        if (text is null || text.Length <= 80) return text;
        return text[..77].TrimEnd() + "...";
    }
}
