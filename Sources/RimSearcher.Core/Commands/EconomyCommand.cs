using RimSearcher.Cli;
using RimSearcher.Contract;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Snapshot;
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
        ["defName", "label", "declaredIn", "category", "marketValue", "marketValueDefined", "calcState",
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
        ["defName", "label", "declaredIn", "category", "marketValue", "calcState", "fallbackMarketValue",
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
            "cost is a cost list expanded recursively; 'get' has no field for any of them.\n\n" +
            "The rows are the same set the game's own table covers: items with a market value above 0.01, " +
            "plus buildings the player can build or minify. Nothing else in the snapshot is priced.\n\n" +
            "marketValue is the price the game actually uses. fallbackMarketValue is the ingredient-and-work " +
            "figure it falls back to only when no MarketValue is declared, so that column is empty for two " +
            "opposite reasons — nothing produces the thing, or the fallback is already the marketValue in " +
            "that same row — and when it does print, it is the counterfactual price, not the one in effect. " +
            "Read any other empty cell as 'the game cannot work this out', not as zero: a profit needs a " +
            "recipeMaker, and a profit rate needs a positive work amount.\n\n" +
            // 这句原先写成「本命令会说明原因,而不是报成游戏什么都不标价」—— 那是在为一个
            // 设计选择辩护。改成直接点那两个状态:少一层与什么都不标价是两个不同的答案。
            "A snapshot need not hold this layer at all. A snapshot without it and a game that prices " +
            "nothing are two different answers, and this command says which one you hit; the answer is " +
            "never a number in that case. There is no " +
            "second road to these numbers on such a snapshot — a field called marketValue is still indexed, " +
            "but that is the base value written in XML, not the price the game computes from it, and " +
            "nothing anywhere holds cost or profit.\n\n" +
            "A number printed as '635 (holds when classicMortars=on)' depends on a difficulty setting that " +
            "an export cannot read: the setting lives on the storyteller, and no storyteller exists while " +
            "the game is loading. The tag names the setting and the position the printed number holds under; " +
            "put the setting the other way and the def swaps in a second cost list, which 'rimsearcher get " +
            "<defName> --path-contains costListForDifficulty' shows.\n\n" +
            "chainEndShare is the share of a thing's cost that came from ingredients with no recipe of their " +
            "own, so at 1 the whole cost is hand-written market values and 'profit' is a difference between " +
            "those, not what producing the thing consumes. A thing with more than one recipe has a " +
            "fallbackMarketValue that depends on def load order: the game takes whichever recipe comes " +
            "first in its database, and a bulk recipe usually scales its ingredients but not its work " +
            "amount. A recipe that accepts its own product as an ingredient folds that product's " +
            "hand-written price into its fallback market value.\n\n" +
            NameLookup.Help,
        Positionals =
        [
            new PositionalSpec
            {
                Name = "defName",
                Required = false,
                Variadic = true,
                Help = "A thing's defName, for the full picture including its cost chain and every recipe " +
                       "that can produce it. Several names put every one of them in the same three tables, " +
                       "so the rows line up for comparison; a name that matches nothing is reported in a " +
                       "note and the others still print. Leave the names out to list the layer, highest " +
                       "market value first.",
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
                Placeholder = "<ok|empty-sum|recipe|used|not_producible>",
                Help = "Keep only rows whose fallback market value came about a particular way. " +
                       "'ok' and 'recipe' both mean the thing declares its own market value, so the " +
                       "fallback is only the counterfactual — 'recipe' derives it from a recipe, 'ok' " +
                       "from the cost list. 'empty-sum' is 'ok' whose cost list summed to nothing: the " +
                       "fallback prints as 0 and is not a price, the game could not work one out. 'used' " +
                       "means nothing is declared, so the fallback is the market value itself. " +
                       "'not_producible' means nothing produces the thing, so there is no fallback at all.",
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
            "rimsearcher economy Gun_Autopistol Gun_Revolver Gun_BoltActionRifle",
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
                       // 「少七列」不点名的话,想要其中一列的读者读不出该不该改走 --json。
                       "Every key above is present either way; when listing the layer the text table leaves " +
                       "out marketValueDefined, costList, costDifficultyInverted, producible, madeFromStuff, " +
                       "isWeapon and isApparel, and the JSON never does. " +
                       "The text output tags cost numbers whose def declares a difficulty variant; here " +
                       "the numbers stay bare and costDifficultyVar/costDifficultyInverted carry that " +
                       "instead, so a number never arrives as a string.",
            },
            new()
            {
                Key = "costChain",
                // product 这一列在单名调用上也有,取值恒定。文本面把恒定列折进表头。
                What = "with defNames: one row per ingredient — product, thingDef, count, unitValue, " +
                       "chainEnd. product is the priced thing this row is an ingredient of, in every row " +
                       "even when only one name was given. chainEnd marks an ingredient with no recipe of " +
                       "its own, where the cost recursion stops and falls back to that ingredient's " +
                       "hand-written market value.",
            },
            new()
            {
                Key = "recipes",
                What = "with defNames: every recipe that produces each named thing — product, defName, " +
                       "productCount, workAmount, selfReferential. product carries which thing the recipe " +
                       "makes, in every row even when only one name was given. More than one row for the " +
                       "same product means that thing's fallback market value depends on def load order.",
            },
            new()
            {
                Key = "absent",
                Rows = true,
                What = "only when the economy layer is short in this snapshot: one row — layer, state, next. " +
                       "state is pre-measure (exported before prices were measured), skipped (--no-economy) " +
                       "or unavailable (the exporter could not measure on that game build); next is the " +
                       "command that fills it. 'things' is then an empty array. Empty when the layer is " +
                       "complete.",
            },
            EmptyCause.JsonKeyCounting("thing"),
            NameLookup.JsonKey,
        ],
    };

    public override int Run(CommandContext ctx)
    {
        // 在场判定走 meta 里那一格,**不是 COUNT(*)** —— 零行有四种成因,而其中一种
        // (量过了、这个名单下确实没有可生产物)是完整的肯定回答,不能与另外三种同形。
        if (Absent(ctx)) return 1;

        var given = ctx.Args.Positionals;
        return given.Count == 0 ? RunAll(ctx) : RunNamed(ctx, given);
    }

    /// <summary>
    /// 四态里的三种「答不了」。返回 true 表示已经说完话了,调用方直接收场。
    ///
    /// 说的方式是一张 <c>absent</c> 表(层 / 状态 / 出路),三种缺席各是一个状态词,
    /// 出路各不相同 —— 合成一句「这份快照没有经济数据」会让最刺眼的那种(签名对不上,
    /// 得去看源码)被读成最无害的那种(重导出就行)。判据在 <see cref="DataLayers.EconomyRow"/>。
    ///
    /// 此前每态一句散文,拒绝后还带一句机制(marketValue 是 XML 基值)。第十七轮(Docs/24)
    /// 量到常见档把机制句读成规格 —— 告诉它算法不同,它就把算法复现一遍交卷;挡得住的是
    /// SKILL.md 里的禁令,不是输出里的任何一句。机制于是只住 --help 与 SKILL。
    /// </summary>
    private static bool Absent(CommandContext ctx)
    {
        var economy = DataLayers.EconomyRow(ctx.Db, ctx.SnapshotName ?? "");
        if (economy.Complete) return false;
        // things 不用在这里认领:行式键由 Runner 在开查前统一认领,拒绝时它是一个空数组。
        ctx.Report.Absent(economy);
        return true;
    }

    /// <summary>
    /// 一个或几个名字。几个名字**不各出一块**,而是并进同一张 things / costChain / recipes ——
    /// 三张表本来就同构(整层那一路的 things 就是这个形状),并排才比得起来。
    ///
    /// 代价是 costChain / recipes 得多一列说明这一行属于哪个物。那一列在单名调用上照出,
    /// 取值恒定;文本面由常量列折叠收进表头,所以单名那一路的表体一行没变。
    /// </summary>
    private static int RunNamed(CommandContext ctx, IReadOnlyList<string> given)
    {
        // 这一路恒发 things,另外两张只在这一路上存在 —— 在开查之前认领,而不是在有行的
        // 分支里补:后者漏一条分支就漏一个形状。
        ctx.Report.Promises("things");

        // 同一个名字给两遍只查一遍。名字按 NOCASE 比,与 EconomyByName 的判据一致。
        var names = new List<string>();
        var repeated = new List<string>();
        foreach (var n in given)
            (names.Contains(n, StringComparer.OrdinalIgnoreCase) ? repeated : names).Add(n);
        if (repeated.Count > 0)
            ctx.Report.Notice(NoticeKind.Filter,
                $"Given more than once, counted once: {NameList.Render(repeated.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Limits.MaxSuggestions)}.");

        var single = names.Count == 1;
        var rows = new List<EconomyRow>();
        var chains = new List<IReadOnlyDictionary<string, object?>>();
        var recipeRows = new List<IReadOnlyDictionary<string, object?>>();
        var allRecipes = new List<(string Product, IReadOnlyList<EconomyRecipeRow> Rows)>();
        var missing = new List<string>();

        foreach (var defName in names)
        {
            var hit = ctx.Db.EconomyByName(defName);
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

        // 一个都没落到才收场。落空的名字与查到的名字同时存在时,落空那些只留一句 ——
        // 单名那一路的两段详细说破(它是不是个 def、这一层收什么)在这里会按名字重复
        // 好几遍,而读的人要的是「哪几个没有」。
        if (rows.Count == 0)
        {
            if (!single)
            {
                ctx.Report.Notice(NoticeKind.NextStep,
                    $"None of these is priced in this snapshot: {NameList.Render(missing, Limits.MaxSuggestions)}.");
                if (!SayWhereElse(ctx, missing))
                    ctx.Report.Notice(NoticeKind.NextStep,
                        "'rimsearcher search <name>' matches on labels and translated text as well as defNames.", teach: true);
            }
            return 1;
        }

        // 这一层收什么是机制,住 help;「回来的行不受影响」是情景假设,2026-09-18 删(Docs/25 §18)。
        if (missing.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"Not priced in this snapshot, so absent from the tables below: " +
                $"{NameList.Render(missing, Limits.MaxSuggestions)}.");
            SayWhereElse(ctx, missing);
        }

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

    /// <summary>
    /// 一个名字在这一层落空。单名那一路照旧把两段说破印全 —— 打错名字与「它是个 def,
    /// 只是这一层不收」的下一步完全不同。返回 true 表示话说完了。
    /// </summary>
    private static bool Miss(CommandContext ctx, string defName)
    {
        var close = Suggestion.Closest(ctx.Db.AllEconomyNames(), defName);
        ctx.Report.Notice(NoticeKind.NextStep,
            $"Nothing named '{defName}' is priced in this snapshot." + Suggestion.Say(close));

        // 「它是个 def,只是不在这一层里」是这条命令最常见的落空成因,而它与打错名字
        // 的下一步完全不同 —— found_as 的一行(is = def,next = get),与 inherit / keyed 撞上
        // def 名同形。这一层收什么是机制,住 help。名字在快照里根本不存在时走九档;
        // 九档也落空才给 search 那条出路。
        var sighting = WhereElse(ctx, defName);
        if (sighting is not null) NameLookup.Say(ctx, sighting);
        else ctx.Report.Notice(NoticeKind.NextStep,
            $"No def is named '{defName}' either. 'rimsearcher search {defName}' matches on labels and " +
            "translated text as well as defNames.");
        return true;
    }

    /// <summary>名字不在这一层时它是什么:先问是不是 def,再走九档。</summary>
    private static NameLookup.Sighting? WhereElse(CommandContext ctx, string name)
    {
        var defs = ctx.Db.GetDefsNamed(name);
        return defs.Count > 0 ? NameLookup.AsDef(name, defs) : NameLookup.Locate(ctx, name);
    }

    /// <summary>落空的名字各自一行 found_as;一行都没有回 false,调用方才给 search 那条出路。</summary>
    private static bool SayWhereElse(CommandContext ctx, IReadOnlyList<string> names)
    {
        var any = false;
        foreach (var n in names)
            if (WhereElse(ctx, n) is { } sighting) { NameLookup.Say(ctx, sighting); any = true; }
        return any;
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
    /// 查询面上 calcState 的第五个取值:导出器写的是 ok(可生产 + 声明了 MarketValue),而四个
    /// 加数全为零时推算价是 0 —— 那是**算不出**,不是「值零」。vanilla 自己大量如此(Steel /
    /// Bioferrite 都是),读成一个数就会被当成极便宜的东西统计进分布。此前靠一句散文说破,
    /// 现在取值自陈(Docs/25 丁2);库里仍是 ok,分岔只在这里与 <c>--calc-state</c> 的筛子上。
    /// </summary>
    public const string CalcEmptySum = "empty-sum";

    private static string CalcStateShown(EconomyRow row)
        => row.CalcState == IntermediateFormat.EconomyCalcOk && row.CalculatedMarketValue is <= 0
            ? CalcEmptySum : row.CalcState;

    /// <summary>
    /// 一行 <c>things</c>。**两条路唯一的产地** —— 键与值都在这里定,调用方只挑印哪几列。
    /// </summary>
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
                // 给了的每个开关单独收回能回来几个,各是 empty_because 的一行(Docs/25 乙1)—— 一次
                // 调用里可以同时挂四个,而收回哪一个是读的人下一步要敲的东西。一个都救不回来
                // (得同时收回两个)才退回那句话。
                var causes = new List<EmptyCause>();
                int Left(ScopeFilter s, string? cat, string? state, bool prod)
                    => ctx.Db.EconomyAll(s, cat, state, prod, sortColumn, 1, 0).Total;
                if (!scope.IsAll)
                    causes.Add(new(ctx.FilterAsGiven("scope"), Left(ctx.Unscoped(), category, calcState, producible), ctx.Without("scope")));
                if (category is not null)
                    causes.Add(new(ctx.FilterAsGiven("category"), Left(scope, null, calcState, producible), ctx.Without("category")));
                if (calcState is not null)
                    causes.Add(new(ctx.FilterAsGiven("calc-state"), Left(scope, category, null, producible), ctx.Without("calc-state")));
                if (producible)
                    causes.Add(new(ctx.FilterAsGiven("producible"), Left(scope, category, calcState, false), ctx.Without("producible")));
                if (!ctx.Report.EmptyBecause(causes, "thing"))
                    ctx.Report.Notice(NoticeKind.Filter,
                        $"No priced thing is left after {NameList.Render(causes.Select(c => c.Filter).ToList(), 4)}, and no " +
                        $"single one of them alone: this snapshot prices {Tally.Complete(layerTotal).Render("thing")} " +
                        $"in all — '{ctx.Without("scope", "category", "calc-state", "producible")}' lists them.");
                return 1;
            }

            // 整层为零而 state 是 ok:量过了,这个 mod 列表下确实没有可生产物。exit 1 自己就说
            // 「量了是空」(没量过的是 3),这里不再解释退出码。
            ctx.Report.Notice(NoticeKind.Boundary,
                "The economy layer was measured for this snapshot and came out empty: nothing in these mods " +
                "is an item with a market value or a building the player can build.");
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
                                IReadOnlyList<(string Product, IReadOnlyList<EconomyRecipeRow> Rows)>? recipes)
    {
        // 1. 造价全部来自链尾物手填的市场价 —— 那一行的 profit 不反映真实生产消耗。
        //    判据是这个数,**不是「看着像掉落物」**:用手写 RecipeDef 生产的物同样是链尾,
        //    而它们明明可造。
        //    2026-09-18 起这一档不再挂句:chainEndShare 那一格自己印着 1,它的含义住 Remarks(Docs/25 丁2)。

        // 2. ok 且推算价为零 = 算不出:2026-09-18 起那一格自陈 empty-sum(CalcStateShown),
        //    此前这里有一句「Read that as 'the game could not work it out'」。

        // 3. 成本表有难度变体 —— 而导出那一刻判不了那个条件。这条的严重度分两档,
        //    因为 invert 决定了导出取到的是常见的那一支还是罕见的那一支,而后者印出来的数
        //    是玩家基本见不到的。两档分开说:合并的话最刺眼的那种会被读成无害的那种。
        //    (2026-08-15 与 Vethara /economy 端点的交叉校验里,2822 个共有 def 上唯一那处
        //    不一致就是这个,而它在数字上与一个正常的数逐字同形。)
        //    措辞在 2026-08-15 盲测后大幅收短:两档严重度、以及「哪一支在跑」原先全写在
        //    这段话里,而实测表明这段话会被整段丢掉。现在条件贴在数上(见 Qual),这里只留
        //    那两件贴不进单元格的事:**为什么导出判不了**,以及**去哪看另一支**。
        //    2026-09-18 起只剩出路:标签贴在数上,它的读法与「导出为什么判不了」住 Remarks。
        var withVariant = shown.Count(r => r.CostDifficultyVar is not null);
        if (withVariant > 0)
            ctx.Report.Notice(NoticeKind.NextStep,
                "A '(holds when …)' tag above marks a number that a difficulty setting can swap out; " +
                "'rimsearcher get <defName> --path-contains costListForDifficulty' shows the other cost list.", teach: true);

        // 4. 多配方 = fallbackMarketValue 有加载顺序依赖(CalculableRecipe 取 DefDatabase 里
        //    第一个匹配,而等比放大的 bulk 配方 workAmount 通常不等比)。
        //    只有单条详情那一路手上有配方表;列表那一路不逐行查,那要 N 次查询。
        // 判据是**每个物各自有几条配方**,不是配方总数:几个名字一起问时,三个物各一条
        // 配方加起来也是 3,而它们一个都没有加载顺序依赖。点名是哪几个物,读的人才知道
        // 上面哪几行的 fallbackMarketValue 不稳。
        var manyWays = recipes?.Where(g => g.Rows.Count > 1).ToList() ?? [];
        if (manyWays.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                string.Join("; ", manyWays.Select(
                    g => $"{Tally.Complete(g.Rows.Count).Render("recipe")} can produce {g.Product}")) +
                ", so that thing's fallbackMarketValue is the one the recipe loaded first gives.");

        var selfFed = recipes?.Where(g => g.Rows.Any(r => r.SelfReferential)).Select(g => g.Product).ToList() ?? [];
        if (selfFed.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                $"A recipe above accepts {NameList.Render(selfFed, Limits.MaxSuggestions)} as one of its own " +
                "ingredients (selfReferential), so that thing's fallback market value folds in its own price.");
    }
}
