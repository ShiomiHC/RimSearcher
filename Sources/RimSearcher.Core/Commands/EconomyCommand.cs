using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Storage;

namespace RimSearcher.Commands;

/// <summary>
/// 经济面 —— 游戏自己说一件东西值多少钱、造价多少、要多少工时。
///
/// **这一层与 def 字段无关**:这些数没有一个躺在 XML 里,它们是 vanilla 在 Debug Output
/// 「Economy」那张表上现算的(市场价可以是推算的、造价要递归展开成本链)。<c>get</c> 给的是
/// 字段值,而字段里根本没有「造价」这一项 —— 拿 <c>get</c> 问它得到的零与「查不到」同形。
///
/// 数字与游戏内那张表对齐,两处有意的偏离已在
/// <see cref="IntermediateFormat.KeyEconomyProfit"/>(游戏那一格印 0.0,这里给 null)
/// 与小数位数(F2 / F1)上写明。
/// </summary>
public sealed class EconomyCommand : Command
{
    /// <summary>
    /// 排序列的闭集。用户输入不拼进 SQL —— 这一条是硬的,不是风格问题。
    /// 键是给人看的名字,值是列名。
    /// </summary>
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

    /// <summary>
    /// <c>things</c> 一行的键集,**两条路(单条详情与整层列表)共用这一份**。
    ///
    /// 2026-08-15 之前是抄了两遍,而整层那一份少七个键 —— 消费侧照单条写的解析代码在整层
    /// 结果上拿到 null,而这一层处处以 null 表示「算不出」,于是缺席的键与一个真实的
    /// 「游戏算不出来」逐字同形。最贵的是 <c>costDifficultyInverted</c>:整层印着
    /// <c>costDifficultyVar</c> 却不发方向,而文本面早已把方向贴在数上(见 <see cref="Qual"/>),
    /// 于是同一份输出的两面说的不是一件事。
    ///
    /// 文本面的列可以是这份的子集(见 <see cref="ListColumns"/>),JSON 面不许 ——
    /// 键集是契约,列宽是排版。
    /// </summary>
    private static readonly string[] ThingKeys =
        ["defName", "label", "mod", "category", "marketValue", "marketValueDefined", "calcState",
         "fallbackMarketValue", "costToMake", "profit", "profitRate", "workToProduce", "costList",
         "costDifficultyVar", "costDifficultyInverted", "chainEndShare", "costDeep", "profitDeep",
         "producible", "madeFromStuff", "isWeapon", "isApparel"];

    /// <summary>
    /// 整层列表**文本面**印的列。<see cref="ThingKeys"/> 的子集,少掉的七个仍在 JSON 里。
    ///
    /// 不印全的理由是排版而不是数据:<c>costList</c> 一格能到 72 字符
    /// (<c>TextRenderer.MaxCellWidth</c> 的上限),四个布尔列各自只在筛选时有用,
    /// 而这条命令的常见调用是几十行一屏。单条详情那一路只有一行,印全不占地方。
    /// </summary>
    private static readonly string[] ListColumns =
        ["defName", "label", "mod", "category", "marketValue", "calcState", "fallbackMarketValue",
         "costToMake", "profit", "profitRate", "workToProduce", "costDifficultyVar", "chainEndShare",
         "costDeep", "profitDeep"];

    public override CommandSpec Spec => new()
    {
        Name = "economy",
        Aliases = ["price", "value", "cost", "market", "prices"],
        Summary = "Show what the game says a thing is worth, what it costs to make, and how long it takes.",
        Remarks =
            "These numbers are not def fields — none of them is written in any XML. The game computes them " +
            "in its own Debug Output 'Economy' table: a market value can be derived from a recipe, and a " +
            "cost is a cost list expanded recursively. Asking 'get' for a cost therefore returns nothing, " +
            "and that nothing looks exactly like a thing having no cost.\n\n" +
            "The rows are the same set the game's own table covers: items with a market value above 0.01, " +
            "plus buildings the player can build or minify. Nothing else in the snapshot is priced.\n\n" +
            "marketValue is the price the game actually uses. fallbackMarketValue is the ingredient-and-work " +
            "figure it falls back to only when no MarketValue is declared, so that column is empty for two " +
            "opposite reasons — nothing produces the thing, or the fallback is already the marketValue in " +
            "that same row — and when it does print, it is the counterfactual price, not the one in effect. " +
            "Read any other empty cell as 'the game cannot work this out', not as zero: a profit needs a " +
            "recipeMaker, and a profit rate needs a positive work amount.\n\n" +
            "A snapshot need not hold this layer at all. This command then says why, rather " +
            "than reporting that the game prices nothing, and the answer is never a number. There is no " +
            "second road to these numbers on such a snapshot — a field called marketValue is still indexed, " +
            "but that is the base value written in XML, not the price the game computes from it, and " +
            "nothing anywhere holds cost or profit.\n\n" +
            "A number printed as '635 (holds when classicMortars=on)' depends on a difficulty setting that " +
            "an export cannot read: the setting lives on the storyteller, and no storyteller exists while " +
            "the game is loading. The tag names the setting and the position the printed number holds under.",
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defName",
                Required = false,
                Help = "A thing's defName, for the full picture including its cost chain and every recipe " +
                       "that can produce it. Leave it out to list the layer, highest market value first.",
            },
        ],
        Options =
        [
            CommonOptions.Limit("things"),
            CommonOptions.Offset("things"),
            CommonOptions.Scope,
            new OptionSpec
            {
                Name = "category",
                Aliases = ["cat"],
                Placeholder = "<Item|Building>",
                Help = "Keep only items or only buildings. The two are not comparable: a building's market " +
                       "value is what you get back for deconstructing it, not what it sells for.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "calc-state",
                Aliases = ["state"],
                Placeholder = "<ok|recipe|used|not_producible>",
                Help = "Keep only rows whose fallback market value came about a particular way. " +
                       "'ok' and 'recipe' both mean the thing declares its own market value, so the " +
                       "fallback is only the counterfactual — 'recipe' derives it from a recipe, 'ok' " +
                       "from the cost list. 'used' means nothing is declared, so the fallback is the " +
                       "market value itself. 'not_producible' means nothing produces the thing, so " +
                       "there is no fallback at all.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "producible",
                Arity = Arity.Flag,
                Aliases = ["makeable"],
                Help = "Keep only things that some recipe produces or that the player can build. Without it " +
                       "the list also holds things you can only find or be given, which are priced but " +
                       "have no cost to compare against.",
                Narrows = true,
            },
            new OptionSpec
            {
                Name = "sort",
                Aliases = ["order", "by"],
                Placeholder = "<field>",
                Help = "Sort by market-value (the default), profit, profit-rate, cost, work, cost-deep, " +
                       "profit-deep, or chain-end-share. Highest first; rows the game cannot work out sort " +
                       "last rather than mixing in with the low end.",
                Default = "market-value",
            },
        ],
        Examples =
        [
            "rimsearcher economy Gun_Autopistol",
            "rimsearcher economy --sort profit-rate --limit 20",
            "rimsearcher economy --scope vethara --category Item",
            "rimsearcher economy --calc-state recipe --sort chain-end-share",
        ],
        JsonKeys =
        [
            new()
            {
                Key = "things",
                Rows = true,
                // 键名不在这里手抄第三遍 —— 上一次抄出来的差额就是这次修的 bug。
                What = "one row per priced thing — " + string.Join(", ", ThingKeys) + ". " +
                       "marketValue is the price the game actually uses. fallbackMarketValue is the " +
                       "ingredient-and-work figure it falls back to only when no MarketValue is declared, " +
                       "so it is non-empty only as the counterfactual beside a declared value, and empty " +
                       "when it already is the marketValue. Any other null means the game cannot work " +
                       "that number out; it is never a stand-in for zero. Always an array, including when " +
                       "one defName matched exactly, so the shape does not change with the kind of match. " +
                       "Every key above is present either way; the text table drops seven of them when " +
                       "listing the layer, to keep the rows readable, but the JSON never does. " +
                       "The text output tags cost numbers whose def declares a difficulty variant; here " +
                       "the numbers stay bare and costDifficultyVar/costDifficultyInverted carry that " +
                       "instead, so a number never arrives as a string.",
            },
            new()
            {
                Key = "costChain",
                What = "with a defName: one row per ingredient — thingDef, count, unitValue, chainEnd. " +
                       "chainEnd marks an ingredient with no recipe of its own, where the cost recursion " +
                       "stops and falls back to that ingredient's hand-written market value.",
            },
            new()
            {
                Key = "recipes",
                What = "with a defName: every recipe that produces this thing — defName, productCount, " +
                       "workAmount, selfReferential. More than one row means the fallback market value " +
                       "depends on def load order.",
            },
        ],
    };

    public override int Run(CommandContext ctx)
    {
        // 在场判定走 meta 里那一格,**不是 COUNT(*)** —— 零行有四种成因,而其中一种
        // (量过了、这个名单下确实没有可生产物)是完整的肯定回答,不能与另外三种同形。
        if (Absent(ctx)) return 1;

        var query = ctx.Args.Positional(0);
        return query is null ? RunAll(ctx) : RunOne(ctx, query);
    }

    /// <summary>
    /// 四态里的三种「答不了」。返回 true 表示已经说完话了,调用方直接收场。
    ///
    /// 分开说而不是合成一句「这份快照没有经济数据」:三种的下一步各不相同,而合并之后
    /// 最刺眼的那种(vanilla 签名对不上,得去看源码)会被读成最无害的那种(重导出就行)。
    /// </summary>
    private static bool Absent(CommandContext ctx)
    {
        switch (ctx.Db.EconomyState)
        {
            case IntermediateFormat.EconomyStateOk:
                return false;

            case null:
                ctx.Report.Notice(NoticeKind.Boundary,
                    "This snapshot was built before this tool measured prices at all, so it has no answer " +
                    "here — that is a property of the snapshot, not evidence about the game. Export again " +
                    "('rimsearcher export'); 'rimsearcher snapshot status' names the snapshot in use.");
                NoteTheDetour(ctx);
                return true;

            case IntermediateFormat.EconomyStateSkipped:
                ctx.Report.Notice(NoticeKind.Boundary,
                    "The export that produced this snapshot was told to skip the economy layer, so nothing " +
                    "here was measured. Export again without that switch; every other layer in this " +
                    "snapshot is complete.");
                NoteTheDetour(ctx);
                return true;

            default:
                // 点名的那句原样端出,不概括 —— 它是唯一的下一步,而这一层不回退到自写实现:
                // 回退能让命令继续出数,但出的是与游戏内表格不一致的数,且没有任何迹象说明
                // 口径已经换了一套。
                ctx.Report.Notice(NoticeKind.Boundary,
                    "The economy layer could not be measured when this snapshot was exported, so there are " +
                    "no prices in it. Everything else in the snapshot is complete and usable — the export " +
                    "did not fail. " +
                    (ctx.Db.EconomyError ?? "The export recorded no reason, which should not happen."));
                NoteTheDetour(ctx);
                return true;
        }
    }

    /// <summary>
    /// 拒绝之后还得说一句邻居的事。实测:被拒之后并不停下,而是转去
    /// 'values marketValue' 取 statBases 里那个数,把它排个序当成「最赚钱的」交卷 ——
    /// 那一步没读到任何错误,因为那个数是真的、那条命令是对的,只是它回答的不是这个问题。
    ///
    /// 所以这句话不能写成「这份快照上按价格排序的结论都不成立」:能排,排出来也没算错。
    /// 要说破的是**排的不是同一个量**。
    /// </summary>
    private static void NoteTheDetour(CommandContext ctx)
        => ctx.Report.Notice(NoticeKind.Boundary,
            "Fields called marketValue are still in this snapshot, but that is the base value written in " +
            "XML, not the price the game computes from it, and no field anywhere holds cost to make or " +
            "profit. Ranking defs by that field answers a different question, and its output does not say so.");

    private static int RunOne(CommandContext ctx, string defName)
    {
        // 这一路恒发 things,另外两张只在这一路上存在 —— 在开查之前认领,而不是在有行的
        // 分支里补:后者漏一条分支就漏一个形状。
        ctx.Report.Promises("things");
        var rows = ctx.Db.EconomyByName(defName);
        if (rows.Count == 0)
        {
            var close = Suggestion.Closest(ctx.Db.AllEconomyNames(), defName);
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Nothing named '{defName}' is priced in this snapshot." + Suggestion.Say(close));

            // 「它是个 def,只是不在这一层里」是这条命令最常见的落空成因,而它与打错名字
            // 的下一步完全不同。这一层只收 ThingDef,且只收有市场价的物与可建的建筑。
            var defs = ctx.Db.GetDefsNamed(defName);
            ctx.Report.Notice(NoticeKind.Boundary, defs.Count > 0
                ? $"'{defName}' is a def in this snapshot, so this is not a spelling problem: the game " +
                  "prices only items with a market value above 0.01 and buildings the player can build " +
                  $"or minify. 'rimsearcher get {defName}' shows what it does have."
                // 名字在快照里根本不存在,而这条命令的候选池只有几千个被定价的物 ——
                // 「这一层没有」与「这个快照没有」是两件事,不说破就会被读成后者。
                : $"No def is named '{defName}' either, so this is not just a thing the game leaves " +
                  $"unpriced. 'rimsearcher search {defName}' matches on labels and translated text as " +
                  "well as defNames, which is the way in when you have the in-game name rather than the " +
                  "defName.");
            return 1;
        }

        // 计数恒在,单条命中也报 —— 靠沉默传达「就这一条」一定会被读错。数的是这个名字下
        // 有几行,而这一层只收 ThingDef,所以实际上恒为 1;报它是为了形状不随命中数变。
        ctx.Report.PageNotice("thing", rows.Count, 0, rows.Count);
        foreach (var row in rows) EmitOne(ctx, row);
        return 0;
    }

    /// <summary>
    /// 给受难度变体影响的那几格贴上限定词。**贴在数上,不贴在旁边一列**:第十五轮盲测里
    /// 相邻的 <c>costDifficultyVar</c> 一列与表下那整段说破被同一个受测模型一起丢掉了,
    /// 只有 <c>635</c> 被抄走。
    ///
    /// 措辞不预设那个难度开关的缺省值。<c>invert</c> 只说得出「变体在开关为何值时生效」,
    /// 而开关自己的缺省是每个 difficultyVar 各自的事(vanilla 的 classicMortars 缺省关,
    /// 但 mod 可以定义别的),所以这里只陈述条件,不替读者判断哪一支在跑。
    ///
    /// **必须短**,且**必须描述被印出来的这个数,不是变体。** 两版都栽在后一句上:
    /// 第一版 "variant applies when classicMortars is off" 在长文本格上被表宽截成
    /// "…when classic…"(截断的限定词比没有更糟 —— 不带含义,还长得像数据坏了);
    /// 第二版 "variant when off" 短到没被截,却被 sonnet 与 opus **同向读反** ——
    /// 那句话说的是变体的条件,而它贴在另一支的数上,于是「off」被顺理成章地挂到了这个数身上。
    ///
    /// 第三版去掉了「变体」这个名词,写成 <c>值 (开关=位置)</c>,不再有人把条件挂错地方 ——
    /// 但也没人挂对:三档模型无一说出方向,最好的一次退成「导出数据无法判定实际取哪套」。
    /// <c>=</c> 本身没主语,读作「变体是 on」与读作「此数成立于 on」一样通顺。
    /// 于是第四版把主语补进句子里(<c>holds when …=on</c>)—— 谓语指向被印出来的这个数,
    /// 而这正是前三版每次都漏掉的那一半。方向由 <c>invert</c> 反推:变体在 <c>invert</c>
    /// 时于开关为假处生效,所以无条件那支反过来,在开关为真处成立。
    /// </summary>
    private static object? Qual(object? value, EconomyRow row)
        => row.CostDifficultyVar is null || value is null
            ? value
            : new Qualified(value,
                "holds when " + row.CostDifficultyVar + "=" + (row.CostDifficultyInverted ? "on" : "off"));

    /// <summary>
    /// marketValue 受不受这件事影响,取决于它是不是**推出来的**。
    /// def 自己声明了 MarketValue 时那是个手填数,与成本表无关;没声明时
    /// <c>StatWorker_MarketValue</c> 回退到 <c>CalculatedBaseMarketValue</c>,而那条路把
    /// 成本表加进去 —— 于是同一个难度变体连带把 marketValue 也换掉了。
    /// 说破原先只说 "every cost number",读起来不含这一列,而 Turret_Mortar 的 635 正是这一列。
    /// </summary>
    private static object? QualMarketValue(EconomyRow row)
        => row.MarketValueDefined ? row.MarketValue : Qual(row.MarketValue, row);

    /// <summary>
    /// 一行 <c>things</c>。**两条路唯一的产地** —— 键与值都在这里定,调用方只挑印哪几列。
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ThingRow(EconomyRow row) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["defName"] = row.DefName,
            ["label"] = row.Label,
            ["mod"] = row.Mod,
            ["category"] = row.Category,
            ["marketValue"] = QualMarketValue(row),
            ["marketValueDefined"] = row.MarketValueDefined,
            ["calcState"] = row.CalcState,
            // 输出键与存储键(calculated_market_value)有意不同:fallback 点破这个数的身份 ——
            // 只在没声明 MarketValue 时才被采用,而那时它已经折进 marketValue、这一格反而不印。
            ["fallbackMarketValue"] = row.CalculatedMarketValue,
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

    private static void EmitOne(CommandContext ctx, EconomyRow row)
    {
        // 单条命中照样走表,不走 detail 块:形状不随命中方式变,消费侧的解析代码就不必
        // 分两支写(与 keyed 同一条纪律)。这一路只有一行,所以文本面也印全。
        ctx.Report.Table("things", ThingKeys, [ThingRow(row)]);

        var chain = ctx.Db.EconomyChain(row.Id);
        if (chain.Count > 0)
            ctx.Report.Table("costChain", ["thingDef", "count", "unitValue", "chainEnd"],
                chain.Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["thingDef"] = c.ThingDef,
                    ["count"] = c.Count,
                    ["unitValue"] = c.UnitValue,
                    ["chainEnd"] = c.ChainEnd,
                }).ToList());

        var recipes = ctx.Db.EconomyRecipes(row.Id);
        if (recipes.Count > 0)
            ctx.Report.Table("recipes", ["defName", "productCount", "workAmount", "selfReferential"],
                recipes.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["defName"] = r.DefName,
                    ["productCount"] = r.ProductCount,
                    ["workAmount"] = r.WorkAmount,
                    ["selfReferential"] = r.SelfReferential,
                }).ToList());

        Caveats(ctx, [row], recipes);
    }

    private static int RunAll(CommandContext ctx)
    {
        var limit = ctx.Limit();
        var offset = ctx.Args.Int("offset", 0);
        var scope = ctx.Scope();
        var category = ctx.Args.Value("category");
        var calcState = ctx.Args.Value("calc-state");
        var producible = ctx.Args.Flag("producible");

        var sortName = ctx.Args.Value("sort") ?? "market-value";
        if (!SortColumns.TryGetValue(sortName, out var sortColumn))
            throw new CliUsageException(
                $"--sort does not know '{sortName}'. It takes one of: " +
                NameList.Render([.. SortColumns.Keys.Distinct(StringComparer.OrdinalIgnoreCase)], 12) + ".");

        var layerTotal = ctx.Db.EconomyCount();
        var (rows, total) = ctx.Db.EconomyAll(scope, category, calcState, producible,
                                              sortColumn, limit.Effective, offset);

        if (rows.Count == 0)
        {
            // 翻过头排在最前:它与「没有」差得最远。
            if (offset > 0 && total > 0)
            {
                ctx.Report.PastEnd(offset, $"{Tally.Complete(total).Render("thing")} match in all.");
                return 1;
            }

            if (total == 0 && layerTotal > 0)
            {
                // 点名是哪几个开关筛空的,而不是笼统说「那些筛子」—— 一次调用里可以同时挂
                // 四个,而收回哪一个是读的人下一步要敲的东西。
                var applied = new List<string>();
                if (!scope.IsAll) applied.Add("--scope");
                if (category is not null) applied.Add("--category");
                if (calcState is not null) applied.Add("--calc-state");
                if (producible) applied.Add("--producible");

                ctx.Report.Notice(NoticeKind.Filter,
                    $"No priced thing is left after {NameList.Render(applied, 4)}. This snapshot prices " +
                    $"{Tally.Complete(layerTotal).Render("thing")} in all — drop one of those to see them.");
                return 1;
            }

            // 整层为零而 state 是 ok:量过了,这个 mod 列表下确实没有可生产物。
            // **这是完整的肯定回答**,不是一次落空 —— 零行照约定仍走 exit 1,所以句子必须
            // 自己说清,读退出码的脚本会读成失败。
            ctx.Report.Notice(NoticeKind.Boundary,
                "The economy layer was measured for this snapshot and came out empty: nothing in these mods " +
                "is an item with a market value or a building the player can build. That is a complete " +
                "answer rather than a lookup that came up short — the exit code is still non-zero because " +
                "no rows were printed.");
            return 1;
        }

        ctx.Report.PageNotice("thing", rows.Count, offset, total);

        // 行照单条那一路整份造,只是文本面挑 ListColumns 印 —— JSON 面不受列参数约束,
        // 拿到的是整份(见 JsonRenderer 里 TableBlock 那一支)。
        ctx.Report.Table("things", ListColumns, rows.Select(ThingRow).ToList());

        Caveats(ctx, rows, null);
        return 0;
    }

    /// <summary>
    /// 三条只有这一层说得出口的说破。它们属于 vanilla 的语义 —— 任何消费方都会踩,
    /// 所以是这条命令的义务,不是下游各自去发现。
    ///
    /// 判据一律取自**印出来的那些行**,不是整层:说「其中 N 条」时那个 N 必须是读的人
    /// 数得出来的那个数。
    /// </summary>
    private static void Caveats(CommandContext ctx, IReadOnlyList<EconomyRow> shown,
                                IReadOnlyList<EconomyRecipeRow>? recipes)
    {
        // 1. 造价全部来自链尾物手填的市场价 —— 那一行的 profit 不反映真实生产消耗。
        //    判据是这个数,**不是「看着像掉落物」**:用手写 RecipeDef 生产的物同样是链尾,
        //    而它们明明可造。
        var allHandWritten = shown.Count(r => r.ChainEndShare is >= 0.999);
        if (allHandWritten > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                // 印出来那一格是 `1`,不是 `1.00` —— 渲染侧不补零。说破时引用的数必须是
                // 读的人眼睛看得见的那个,否则这句话会被当成在说另一行。
                $"chainEndShare is 1 for {Tally.Complete(allHandWritten).Render("row")} above, which means " +
                "the whole cost came from ingredients that have no recipe of their own. For those rows " +
                "'profit' is the market value minus a few other hand-written market values, not minus what " +
                "producing the thing actually consumes.");

        // 2. ok 且推算价为零 = **算不出**,不是「推算价是零」。calc_state 只反映「可生产 +
        //    声明了 MarketValue」,四个加数全为零时它照样是 ok。vanilla 自己大量如此
        //    (Steel / Bioferrite 都是),读成一个数就会被当成极便宜的东西统计进分布。
        var zeroCalc = shown.Count(r => r.CalcState == IntermediateFormat.EconomyCalcOk
                                        && r.CalculatedMarketValue is <= 0);
        if (zeroCalc > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"fallbackMarketValue is 0 on {Tally.Complete(zeroCalc).Render("row")} above whose calcState " +
                "is 'ok'. Read that as 'the game could not work it out', not as a price of zero: calcState " +
                "only says the thing is producible and declares a market value, and the sum underneath can " +
                "still come out empty. Vanilla does this a lot — Steel and Bioferrite among them.");

        // 3. 成本表有难度变体 —— 而导出那一刻判不了那个条件。这条的严重度分两档,
        //    因为 invert 决定了导出取到的是常见的那一支还是罕见的那一支,而后者印出来的数
        //    是玩家基本见不到的。两档分开说:合并的话最刺眼的那种会被读成无害的那种。
        //    (2026-08-15 与 Vethara /economy 端点的交叉校验里,2822 个共有 def 上唯一那处
        //    不一致就是这个,而它在数字上与一个正常的数逐字同形。)
        //    措辞在 2026-08-15 盲测后大幅收短:两档严重度、以及「哪一支在跑」原先全写在
        //    这段话里,而实测表明这段话会被整段丢掉。现在条件贴在数上(见 Qual),这里只留
        //    那两件贴不进单元格的事:**为什么导出判不了**,以及**去哪看另一支**。
        var withVariant = shown.Count(r => r.CostDifficultyVar is not null);
        if (withVariant > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "A tag like '(holds when classicMortars=on)' above names the difficulty setting a number " +
                "depends on, and the position it holds under. Put the setting the other way and the def " +
                "swaps in a second cost list, which the tagged number is not from. An export cannot tell " +
                "which way a given game has it: the setting is read off the storyteller, and no storyteller " +
                "exists while the game is loading. 'rimsearcher get <defName> --path-contains " +
                "costListForDifficulty' shows the other list.");

        // 4. 多配方 = fallbackMarketValue 有加载顺序依赖(CalculableRecipe 取 DefDatabase 里
        //    第一个匹配,而等比放大的 bulk 配方 workAmount 通常不等比)。
        //    只有单条详情那一路手上有配方表;列表那一路不逐行查,那要 N 次查询。
        if (recipes is { Count: > 1 })
            ctx.Report.Notice(NoticeKind.Boundary,
                $"{Tally.Complete(recipes.Count).Render("recipe")} can produce this thing, so its " +
                "fallbackMarketValue depends on def load order: the game takes whichever of them comes " +
                "first in the database. A bulk recipe usually scales its ingredients but not its work " +
                "amount, so which one wins changes the number.");

        if (recipes is not null && recipes.Any(r => r.SelfReferential))
            ctx.Report.Notice(NoticeKind.Boundary,
                "A recipe above accepts this very thing as one of its own ingredients, so the fallback " +
                "market value has the thing's own hand-written price folded into it — which is the opposite " +
                "of deriving a price from ingredients.");
    }
}
