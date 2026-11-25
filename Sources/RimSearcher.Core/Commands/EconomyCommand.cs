using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
using RimSearcher.Storage;
using RimTooling.Cli;

namespace RimSearcher.Commands;

public sealed class EconomyCommand : Command
{
    private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["market-value"] = "market_value",
        ["value"] = "market_value",
        ["profit"] = "profit",
        ["profit-rate"] = "profit_rate",
        ["cost"] = "cost_to_make",
        ["cost-to-make"] = "cost_to_make",
        ["work"] = "work_to_produce",
        ["cost-deep"] = "cost_deep",
        ["profit-deep"] = "profit_deep",
        ["chain-end-share"] = "chain_end_share",
    };

    private static readonly string[] ThingKeys =
        ["defName", "label", "declaredIn", "category", "marketValue", "marketValueDefined", "calcState",
         "fallbackMarketValue", "costToMake", "profit", "profitRate", "workToProduce", "costList",
         "costDifficultyVar", "costDifficultyInverted", "chainEndShare", "costDeep", "profitDeep",
         "producible", "madeFromStuff", "isWeapon", "isApparel"];

    private static readonly string[] ListColumns =
        ["defName", "label", "declaredIn", "category", "marketValue", "calcState", "fallbackMarketValue",
         "costToMake", "profit", "profitRate", "workToProduce", "costDifficultyVar", "chainEndShare",
         "costDeep", "profitDeep"];

    private static readonly CliOption LimitOption = CommonOptions.Limit("thing");

    private static readonly CliOption OffsetOption = CommonOptions.Offset("thing");

    private static readonly string[] Categories = ["Item", "Building"];

    private static readonly CliOption CategoryOption = CliOption.Text("category") with
    {
        Aliases = ["cat"],
        Placeholder = "<Item|Building>",
        Help = "只保留物品或只保留建筑。",
        Narrows = true,
    };

    private static readonly CliOption CalcStateOption = CliOption.Text("calc-state") with
    {
        Aliases = ["state"],
        Placeholder = "<costList|costList 为空|recipe|used|not_producible>",
        Help = "只保留 fallbackMarketValue 按所给计算状态得出的行。" +
               "「costList」与「recipe」都表示该 thing 自己声明有 MarketValue，因此 fallbackMarketValue " +
               "只是反事实价格：「recipe」由 recipe 推算，「costList」由 costList 推算。" +
               "「costList 为空」表示 costList 各项加总为零，游戏无法算出 fallbackMarketValue。「used」表示没有声明 MarketValue，marketValue 即由原料与工时算出的数，fallbackMarketValue 为空。" +
               "「not_producible」表示该 thing 无法生产，因此没有 fallbackMarketValue。",
        Narrows = true,
    };

    private static readonly CliOption ProducibleOption = CliOption.Flag("producible") with
    {
        Aliases = ["makeable"],
        Help = "只保留某个 recipe 能生产、或玩家能建造的 thing。不加时列表还包括只能拾得或获赠的 thing，这些 thing 有定价，但没有可供比较的成本。",
        Narrows = true,
    };

    private static readonly CliOption Sort = CliOption.Text("sort") with
    {
        Aliases = ["order", "by"],
        Placeholder = "<field>",
        Help = "按 market-value、profit、profit-rate、cost、work、cost-deep、profit-deep " +
               "或 chain-end-share 排序；value 与 cost-to-make 分别是 market-value 与 cost 的别名。" +
               "从高到低排列；游戏无法算出的行排在最后，不混入低值一端。",
        Default = "market-value",
    };

    public override CliOperation Operation { get; } = new()
    {
        Name = "economy",
        Aliases = ["price", "value", "cost", "market", "prices"],
        Summary = "列出游戏为 thing 算出的 marketValue、costToMake 与 workToProduce。",
        Remarks =
            "本命令列出的行与原版开发模式的 Economy 调试表（DebugOutputsEconomy.ItemAndBuildingAcquisition）相同：marketValue 高于 0.01 的物品，加上玩家可建造或可打包的建筑。" +
            "快照中的其他内容都没有定价。\n\n" +
            "marketValue 是游戏实际使用的价格。fallbackMarketValue 是由原料与工时算出、只在没有声明 MarketValue " +
            "时才采用的数，因此输出时为反事实价格；为空时，成因见计算状态。" +
            "profit 为空表示没有 recipeMaker，profitRate 为空表示 workToProduce 不大于零，chainEndShare 为空表示没有成本，" +
            "costDifficultyVar 为空表示成本没有难度变体；其余为空的格是游戏无法算出的数。\n\n" +
            "chainEndShare 是自身没有 recipe 的原料在 thing 成本中占的份额，因此为 1 时全部成本都是自己声明的 marketValue，profit 是这些声明值之间的差额，不反映生产该 thing 的实际消耗。",
        Positionals =
        [
            new CliPositional
            {
                Name = "defName",
                Required = false,
                Variadic = true,
                Help = "thing 的 defName，列出该 thing 的完整信息，包括成本链与每个能生产该 thing 的 recipe。" +
                       "给出几个名字时全部放进同样的三张表，行与行对齐以便比较。" +
                       "省略名字时列出整层，按 marketValue 从高到低排列。",
            },
        ],
        Options =
        [
            LimitOption,
            OffsetOption,
            CommonOptions.Scope,
            CategoryOption,
            CalcStateOption,
            ProducibleOption,
            Sort,
        ],
        Examples =
        [
            "rimsearcher economy Gun_Autopistol",
            "rimsearcher economy Gun_Autopistol Gun_Revolver Gun_BoltActionRifle",
            "rimsearcher economy --sort profit-rate --limit 20",
            "rimsearcher economy --scope ludeon.rimworld.biotech --category Item",
            "rimsearcher economy --calc-state recipe --sort chain-end-share",
        ],
        JsonKeys =
        [
            new("things",
                "每个有定价的 thing 一行：" + string.Join("、", ThingKeys) + "。" +
                "没有 mod 声明的 thing，declaredIn 为空。" +
                "null 从不代表零。" +
                "恒为数组，一个 defName 恰好匹配时同样如此，因此形状不随匹配方式变化。" +
                "无论是否给出 defName，上面每个键都会出现；列出整层时文本表省去 marketValueDefined、costList、" +
                "costDifficultyInverted、producible、madeFromStuff、isWeapon 与 isApparel，JSON 从不省去。" +
                "文本输出会给所属 def 声明有难度变体的成本数附加标注；这里的数保持原样，" +
                "改由 costDifficultyVar / costDifficultyInverted 承载这一信息，因此数从不以字符串形式出现。",
                Rows: true),
            new("costChain",
                "给出 defName 时出现：每种原料一行，含 product、thingDef、count、unitValue、chainEnd。" +
                "product 是这一行原料所属的定价 thing，只给出一个名字时也每行都有。" +
                "chainEnd 标出自身没有 recipe 的原料：成本链在此终止，退回该原料自己声明的 marketValue。"),
            new("recipes",
                "给出 defName 时出现：生产每个所查 thing 的全部 recipe，含 product、defName、productCount、" +
                "workAmount、selfReferential。product 写明该 recipe 生产哪个 thing，只给出一个名字时也每行都有。"),
            new("absent",
                "仅当本快照缺经济层时出现：一行，含 layer、state、next。" +
                "state 为「已跳过」（--no-economy）或「无法采集」（导出器在该游戏构建上无法测量）；" +
                "next 为补齐该层的命令，「无法采集」时则为导出器自己给出的原因，没有命令能补齐。" +
                "此时 things 为空数组。该层齐备时为空。",
                Rows: true),
            EmptyCause.JsonKeyCounting("thing"),
            NameLookup.JsonKey,
        ],
    };

    public override bool ReadsTranslations => true;

    public override int Run(CommandContext ctx)
    {
        if (Absent(ctx)) return 1;

        var given = ctx.Args.Positionals;
        if (given.Count == 0) return RunAll(ctx);
        foreach (var listOnly in (CliOption[])[CategoryOption, CalcStateOption, ProducibleOption, Sort, LimitOption, OffsetOption])
            if (ctx.Args.Has(listOnly))
                throw new CliUsageException($"{ctx.Args.Spelled(listOnly)} 只用于不指定 defName 的整层列表。");
        return RunNamed(ctx, given);
    }

    private static bool Absent(CommandContext ctx)
    {
        var economy = DataLayers.EconomyRow(ctx.Db, ctx.SnapshotName ?? "");
        if (economy.Complete) return false;
        ctx.Report.Absent(economy);
        return true;
    }

    private static int RunNamed(CommandContext ctx, IReadOnlyList<string> given)
    {
        ctx.Report.Promises("things");

        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in given)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"重复给出的名字只输出一次：{NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}。");

        var single = names.Count == 1;
        var rows = new List<EconomyRow>();
        var chains = new List<IReadOnlyDictionary<string, object?>>();
        var recipeRows = new List<IReadOnlyDictionary<string, object?>>();
        var allRecipes = new List<(string Product, IReadOnlyList<EconomyRecipeRow> Rows)>();
        var missing = new List<string>();
        var scope = ctx.Scope();
        var outside = new List<EconomyRow>();

        foreach (var defName in names)
        {
            var hit = ctx.Db.EconomyByName(defName);
            if (hit.Count > 0 && !hit.Any(r => scope.Includes(r.Mod)))
            {
                outside.AddRange(hit);
                continue;
            }
            hit = [.. hit.Where(r => scope.Includes(r.Mod))];
            if (hit.Count == 0)
            {
                missing.Add(defName);
                if (single && Miss(ctx, defName)) return 1;
                continue;
            }

            rows.AddRange(hit);
            foreach (var row in hit)
            {
                foreach (var c in ctx.Db.EconomyChain(row.Id))
                    chains.Add(new Dictionary<string, object?>
                    {
                        ["product"] = row.DefName,
                        ["thingDef"] = c.ThingDef,
                        ["count"] = c.Count,
                        ["unitValue"] = c.UnitValue,
                        ["chainEnd"] = c.ChainEnd,
                    });

                var recipes = ctx.Db.EconomyRecipes(row.Id);
                if (recipes.Count > 0) allRecipes.Add((row.DefName, recipes));
                foreach (var r in recipes)
                    recipeRows.Add(new Dictionary<string, object?>
                    {
                        ["product"] = row.DefName,
                        ["defName"] = r.DefName,
                        ["productCount"] = r.ProductCount,
                        ["workAmount"] = r.WorkAmount,
                        ["selfReferential"] = r.SelfReferential,
                    });
            }
        }

        if (rows.Count == 0)
        {
            if (outside.Count > 0)
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(CommonOptions.Scope), outside.Count,
                                                       ctx.Without(CommonOptions.Scope)), "thing");
            if (!single && missing.Count > 0)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"以下名字在本快照中都没有定价：{NameList.Render(missing, Limits.MaxSuggestions)}。");
                SayWhereElse(ctx, missing);
            }
            return 1;
        }

        if (missing.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"以下名字在本快照中没有定价，因此不在下表中：{NameList.Render(missing, Limits.MaxSuggestions)}。");
            SayWhereElse(ctx, missing);
        }

        if (outside.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"--scope {scope.Expression} 之外，未列出：{NameList.Render([.. outside.Select(r => r.DefName).Distinct()], Limits.MaxSuggestions)}。");

        ctx.Report.PageNotice("thing", rows.Count, 0, rows.Count);
        ctx.Report.Table("things", ThingKeys, rows.Select(ThingRow).ToList());
        if (chains.Count > 0)
            ctx.Report.Table("costChain", ["product", "thingDef", "count", "unitValue", "chainEnd"], chains);
        if (recipeRows.Count > 0)
            ctx.Report.Table("recipes", ["product", "defName", "productCount", "workAmount", "selfReferential"],
                             recipeRows);

        Caveats(ctx, rows, allRecipes);
        return 0;
    }

    private static bool Miss(CommandContext ctx, string defName)
    {
        var close = Suggestion.Closest(ctx.Db.AllEconomyNames(), defName);
        ctx.Report.Notice(NoticeKind.NextStep,
            $"本快照中没有名为 '{defName}' 的定价 thing。" + Suggestion.Say(close));

        var sighting = WhereElse(ctx, defName);
        if (sighting is not null) NameLookup.Say(ctx, sighting);
        else ctx.Report.Notice(NoticeKind.NextStep,
            $"也没有名为 '{defName}' 的 def。");
        return true;
    }

    private static NameLookup.Sighting? WhereElse(CommandContext ctx, string name)
    {
        var defs = ctx.Db.GetDefsNamed(name);
        return defs.Count > 0 ? NameLookup.AsDef(name, defs) : NameLookup.Locate(ctx, name);
    }

    private static bool SayWhereElse(CommandContext ctx, IReadOnlyList<string> names)
    {
        var any = false;
        foreach (var n in names)
            if (WhereElse(ctx, n) is { } sighting) { NameLookup.Say(ctx, sighting); any = true; }
        return any;
    }

    private static object? Qual(object? value, EconomyRow row)
        => row.CostDifficultyVar is null || value is null
            ? value
            : new Qualified(value,
                row.CostDifficultyVar + "=" + (row.CostDifficultyInverted ? "on" : "off") + " 时成立");

    private static object? QualMarketValue(EconomyRow row)
        => row.MarketValueDefined ? row.MarketValue : Qual(row.MarketValue, row);

    public const string CalcEmptySum = "costList 为空";

    public const string CalcFromCostList = "costList";

    private static readonly string[] CalcStates =
    [
        CalcFromCostList, CalcEmptySum, IntermediateFormat.EconomyCalcRecipe,
        IntermediateFormat.EconomyCalcUsed, IntermediateFormat.EconomyCalcNotProducible,
    ];

    private static string CalcStateShown(EconomyRow row)
        => row.CalcState != IntermediateFormat.EconomyCalcOk ? row.CalcState
         : row.CalculatedMarketValue is <= 0 ? CalcEmptySum : CalcFromCostList;

    private static string? CalcStateFilter(string? given)
    {
        if (given is null) return null;
        var known = CalcStates.FirstOrDefault(s => string.Equals(s, given.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CliUsageException(
                $"--calc-state 不接受 '{given}'。可选值：{string.Join("、", CalcStates)}。");
        return known == CalcFromCostList ? IntermediateFormat.EconomyCalcOk : known;
    }

    private static IReadOnlyDictionary<string, object?> ThingRow(EconomyRow row) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["defName"] = row.DefName,
            ["label"] = row.Label,
            ["declaredIn"] = row.Mod,
            ["category"] = row.Category,
            ["marketValue"] = QualMarketValue(row),
            ["marketValueDefined"] = row.MarketValueDefined,
            ["calcState"] = CalcStateShown(row),
            ["fallbackMarketValue"] = CalcStateShown(row) == CalcEmptySum ? null : row.CalculatedMarketValue,
            ["costToMake"] = Qual(row.CostToMake, row),
            ["profit"] = row.Profit,
            ["profitRate"] = row.ProfitRate,
            ["workToProduce"] = row.WorkToProduce,
            ["costList"] = row.CostList,
            ["costDifficultyVar"] = row.CostDifficultyVar,
            ["costDifficultyInverted"] = row.CostDifficultyInverted,
            ["chainEndShare"] = row.ChainEndShare,
            ["costDeep"] = row.CostDeep,
            ["profitDeep"] = row.ProfitDeep,
            ["producible"] = row.Producible,
            ["madeFromStuff"] = row.MadeFromStuff,
            ["isWeapon"] = row.IsWeapon,
            ["isApparel"] = row.IsApparel,
        };

    private static int RunAll(CommandContext ctx)
    {
        var limit = ctx.Limit(LimitOption);
        var offset = ctx.Args.Offset(OffsetOption);
        var scope = ctx.Scope();
        var category = ctx.Args.Value(CategoryOption);
        if (category is not null && !Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new CliUsageException($"--category 不接受 '{category}'。可选值：{string.Join("、", Categories)}。");
        var calcState = CalcStateFilter(ctx.Args.Value(CalcStateOption));
        var producible = ctx.Args.Flag(ProducibleOption);

        var sortName = ctx.Args.Value(Sort) ?? "market-value";
        if (!SortColumns.TryGetValue(sortName, out var sortColumn))
            throw new CliUsageException(
                $"--sort 不接受 '{sortName}'。可选值：" +
                NameList.Render([.. SortColumns.Keys.Distinct(StringComparer.OrdinalIgnoreCase)], 12) + "。");

        var layerTotal = ctx.Db.EconomyCount();
        var (rows, total) = ctx.Db.EconomyAll(scope, category, calcState, producible,
                                              sortColumn, limit.Effective, offset);

        if (rows.Count == 0)
        {
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, Cjk.Join("共匹配", Tally.Complete(total).Render("thing"), "。"));
                return 1;
            }

            if (total == 0 && layerTotal > 0)
            {
                var causes = new List<EmptyCause>();
                int Left(ScopeFilter s, string? cat, string? state, bool prod)
                    => ctx.Db.EconomyAll(s, cat, state, prod, sortColumn, 1, 0).Total;
                if (!scope.IsAll)
                    causes.Add(new(ctx.FilterAsGiven(CommonOptions.Scope), Left(ctx.Unscoped(), category, calcState, producible), ctx.Without(CommonOptions.Scope)));
                if (category is not null)
                    causes.Add(new(ctx.FilterAsGiven(CategoryOption), Left(scope, null, calcState, producible), ctx.Without(CategoryOption)));
                if (calcState is not null)
                    causes.Add(new(ctx.FilterAsGiven(CalcStateOption), Left(scope, category, null, producible), ctx.Without(CalcStateOption)));
                if (producible)
                    causes.Add(new(ctx.FilterAsGiven(ProducibleOption), Left(scope, category, calcState, false), ctx.Without(ProducibleOption)));
                if (!ctx.Report.EmptyBecause(causes, "thing"))
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"经 {NameList.Render(causes.Select(c => c.Filter).ToList(), 4)} 筛选后没有剩下任何定价 thing，" +
                        $"须同时去掉其中至少两项，才可能有结果：本快照共为 {Tally.Complete(layerTotal).Render("thing")} 定价，" +
                        $"全部见 '{ctx.Without(CommonOptions.Scope, CategoryOption, CalcStateOption, ProducibleOption)}'。");
                return 1;
            }

            ctx.Report.Notice(NoticeKind.Boundary,
                "本快照的经济层状态为齐备，但结果为空：这些 mod 中没有带 marketValue 的 thing，也没有玩家可建造或可打包的建筑。");
            return 1;
        }

        ctx.Report.PageNotice("thing", rows.Count, offset, total);

        ctx.Report.Table("things", ListColumns, rows.Select(ThingRow).ToList());

        Caveats(ctx, rows, null);
        return 0;
    }

    private static void Caveats(CommandContext ctx, IReadOnlyList<EconomyRow> shown,
                                IReadOnlyList<(string Product, IReadOnlyList<EconomyRecipeRow> Rows)>? recipes)
    {
        var withVariant = shown.Count(r => r.CostDifficultyVar is not null);
        if (withVariant > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                "上方带「（… 时成立）」标注的数会随难度设置改变；" +
                "另一份 costList 见 'rimsearcher get <defName> --path-contains costListForDifficulty'。", teach: true);

        var manyWays = recipes?.Where(g => g.Rows.Count > 1).ToList() ?? [];
        if (manyWays.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                string.Join("；", manyWays.Select(
                    g => $"{Tally.Complete(g.Rows.Count).Render("recipe")} 能生产 {g.Product}")) +
                (manyWays.Count > 1 ? "，因此各 thing 的 fallbackMarketValue 取自各自最先加载的 recipe。"
                    : "，因此该 thing 的 fallbackMarketValue 取自最先加载的 recipe。"));

        var selfFed = recipes?.Where(g => g.Rows.Any(r => r.SelfReferential)).Select(g => g.Product).ToList() ?? [];
        if (selfFed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"上方 {NameList.Render(selfFed, Limits.MaxSuggestions)} 有 recipe 把自身产物作为原料之一（selfReferential），" +
                (selfFed.Count > 1 ? "因此各 thing 的 fallbackMarketValue 包含各自的 marketValue。"
                    : "因此该 thing 的 fallbackMarketValue 包含其自身的 marketValue。"));
    }
}
