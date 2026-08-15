using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using RimSearcher.Contract;
using RimWorld;
using Verse;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 经济面 —— 全库可生产物的市场价 / 造价 / 工时 / 利润率 / 成本链。
    ///
    /// 与 <see cref="DefExporter"/> 并列的第二个 emitter,不是往它的字段表里加字段:
    /// 那一个是纯字段反射,因此健壮;**这一个要调 vanilla 的 private 方法,RimWorld 版本
    /// 更新即可能改名或改签名**。两者的爆炸半径必须分开(见 <see cref="Build"/>)。
    ///
    /// 数字与游戏内 Debug Output「Economy」那张表对齐,两处有意的偏离已在
    /// <see cref="IntermediateFormat.KeyEconomyProfit"/> 与
    /// <see cref="IntermediateFormat.KeyEconomyProfitRate"/> 上写明。
    /// </summary>
    public static class EconomyExporter
    {
        // vanilla 的两个 private 静态算法。反射而不重写,是为了让这一层的数字恒等于游戏内
        // 那张表;自写一份等价实现会把 vanilla 日后的口径变更变成静默错数,而错数在平衡
        // 决策里没有任何指纹。
        //
        // **只有这两个走反射。** DebugOutputsEconomy.WorkToProduceBest / CostListString 与
        // StatWorker_MarketValue.CalculatedBaseMarketValue / CalculableRecipe 都是 public,
        // 直调即可 —— 它们改签名时是编译期错,而不是运行期才被发现。顺带避开一个自造的
        // 假阳性:这四个方法的首参是 BuildableDef 而不是 ThingDef,照 ThingDef 去 GetMethod
        // 会拿到 null,然后触发下面那句「缺失签名」,与真的 RimWorld 改签名逐字同形。
        private static MethodInfo _costToMake;
        private static MethodInfo _producible;

        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>
        /// 解析两个 private 句柄。成功回 null,失败回**点名了缺失签名**的整句。
        ///
        /// 失败不得回退到自写的等价实现:一旦回退,调用方拿到的是与游戏内表格不一致的数字,
        /// 而输出里没有任何迹象说明口径已经换了一套。
        ///
        /// 在任何遍历之前跑 —— 于是最常见的那种失败(RimWorld 更新)根本不会产出半份数据。
        /// </summary>
        public static string ResolveReflection()
        {
            _costToMake = typeof(DebugOutputsEconomy).GetMethod(
                "CostToMake", PrivateStatic, null, new[] { typeof(ThingDef), typeof(bool) }, null);
            _producible = typeof(DebugOutputsEconomy).GetMethod(
                "Producible", PrivateStatic, null, new[] { typeof(BuildableDef) }, null);

            var missing = new List<string>();
            if (_costToMake == null) missing.Add("Verse.DebugOutputsEconomy.CostToMake(ThingDef, bool)");
            if (_producible == null) missing.Add("Verse.DebugOutputsEconomy.Producible(BuildableDef)");
            if (missing.Count == 0) return null;

            return "The vanilla economy algorithms could not be found by reflection. Missing: "
                 + string.Join(", ", missing.ToArray())
                 + ". These are private methods, so a RimWorld update can rename them or change their "
                 + "signature; compare EconomyExporter's declarations against the current "
                 + "DebugOutputsEconomy source. No economy data was written — this layer does not fall back "
                 + "to a reimplementation, because numbers that no longer match the game's own table would "
                 + "carry no sign that the basis had changed.";
        }

        /// <summary>
        /// 整批攒好再交出去,**不是流式 yield**。
        ///
        /// 抽取跑到一半抛异常时,流式写法已经把前半截写进文件了,收场只剩「导入侧读到
        /// state != ok 再 DELETE」——那让「文件里有行、库里没行」成为常态,而那正是以后查
        /// 这类 bug 时最难信的一种状态。整批缓冲的代价是千余行 × 数百字节的内存,在一个
        /// 刚加载完全部 Def 的游戏进程里可以忽略。
        ///
        /// 抛出的异常由调用方转成 <see cref="IntermediateFormat.EconomyStateUnavailable"/>。
        /// </summary>
        public static List<string> Build()
        {
            // 外延与 vanilla ItemAndBuildingAcquisition 的 where 子句逐字一致。
            var defs = new List<ThingDef>();
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                var item = def.category == ThingCategory.Item && def.BaseMarketValue > 0.01f;
                var building = def.category == ThingCategory.Building
                               && (def.BuildableByPlayer || def.Minifiable);
                if (item || building) defs.Add(def);
            }

            var lines = new List<string>(defs.Count);
            foreach (var def in defs) lines.Add(BuildLine(def));
            return lines;
        }

        private static float CostToMake(ThingDef def)
            => (float)_costToMake.Invoke(null, new object[] { def, false });

        private static bool Producible(ThingDef def)
            => (bool)_producible.Invoke(null, new object[] { def });

        private static string BuildLine(ThingDef def)
        {
            var producible = Producible(def);
            var marketValue = def.BaseMarketValue;
            var work = DebugOutputsEconomy.WorkToProduceBest(def);
            var hasRecipeMaker = def.recipeMaker != null;

            var line = new JsonLine()
                .Str(IntermediateFormat.KeyKind, IntermediateFormat.KindEconomy)
                .Str(IntermediateFormat.KeyDefName, def.defName ?? "")
                .Str(IntermediateFormat.KeyLabel, def.label ?? "")
                // Patch 新增的顶层 def 拿不到 modContentPack,丢掉该行会让对照池静默缺项。
                // 兜底值与 def 行的 source_mod(空串)有意不同,理由在 KeyEconomyMod 上。
                .Str(IntermediateFormat.KeyEconomyMod,
                     def.modContentPack == null ? "unknown" : def.modContentPack.PackageId)
                .Str(IntermediateFormat.KeyEconomyCategory, def.category.ToString())
                .Num(IntermediateFormat.KeyEconomyMarketValue, marketValue)
                .Bool(IntermediateFormat.KeyEconomyProducible, producible)
                .Bool(IntermediateFormat.KeyEconomyMadeFromStuff, def.MadeFromStuff)
                .Bool(IntermediateFormat.KeyEconomyIsWeapon, def.IsWeapon)
                .Bool(IntermediateFormat.KeyEconomyIsApparel, def.IsApparel)
                // vanilla 那一列是 statBases.Any(…),statBases 为 null 时它会 NRE;
                // StatBaseDefined 是 null 安全的同一判据。一处有意的分歧,不是照抄。
                .Bool(IntermediateFormat.KeyEconomyMarketValueDefined,
                      def.StatBaseDefined(StatDefOf.MarketValue));

            // 四态。前两态是 vanilla 自己都算不出的两种「空」,合并会让消费侧把它们统计进
            // 分布、拉低整段分位。
            if (!producible)
            {
                line.Str(IntermediateFormat.KeyEconomyCalcState, IntermediateFormat.EconomyCalcNotProducible)
                    .Num(IntermediateFormat.KeyEconomyCalculatedMarketValue, null);
            }
            else if (!def.StatBaseDefined(StatDefOf.MarketValue))
            {
                line.Str(IntermediateFormat.KeyEconomyCalcState, IntermediateFormat.EconomyCalcUsed)
                    .Num(IntermediateFormat.KeyEconomyCalculatedMarketValue, null);
            }
            else
            {
                var calculated = StatWorker_MarketValue.CalculatedBaseMarketValue(def, null);
                var fromRecipe = StatWorker_MarketValue.CalculableRecipe(def) != null;
                line.Str(IntermediateFormat.KeyEconomyCalcState,
                         fromRecipe ? IntermediateFormat.EconomyCalcRecipe : IntermediateFormat.EconomyCalcOk)
                    .Num(IntermediateFormat.KeyEconomyCalculatedMarketValue, calculated);
            }

            if (hasRecipeMaker)
            {
                var cost = CostToMake(def);
                line.Num(IntermediateFormat.KeyEconomyCostToMake, cost)
                    .Num(IntermediateFormat.KeyEconomyProfit, marketValue - cost)
                    .Num(IntermediateFormat.KeyEconomyProfitRate,
                         work > 0f ? (float?)((marketValue - cost) / work * 10000f) : null);
            }
            else
            {
                line.Num(IntermediateFormat.KeyEconomyCostToMake, null)
                    .Num(IntermediateFormat.KeyEconomyProfit, null)
                    .Num(IntermediateFormat.KeyEconomyProfitRate, null);
            }

            line.Num(IntermediateFormat.KeyEconomyWorkToProduce, work > 0f ? (float?)work : null)
                .Str(IntermediateFormat.KeyEconomyCostList,
                     DebugOutputsEconomy.CostListString(def, false, false) ?? "");

            // 难度相关的成本变体。**读的是字段本身,不是 CostList 的结果** —— 结果里看不出
            // 这件事:Applies 在没有 storyteller 时无条件为假,于是变体存不存在都长成同一个数。
            var diff = def.costListForDifficulty;
            line.Str(IntermediateFormat.KeyEconomyCostDifficultyVar, diff == null ? null : diff.difficultyVar)
                .Bool(IntermediateFormat.KeyEconomyCostDifficultyInverted, diff != null && diff.invert);

            float? chainEndShare;
            var chain = BuildCostChain(def, out chainEndShare);
            line.Raw(IntermediateFormat.KeyEconomyCostChain, chain)
                .Num(IntermediateFormat.KeyEconomyChainEndShare, chainEndShare);

            var deep = CostDeep(def, new HashSet<ThingDef> { def });
            line.Num(IntermediateFormat.KeyEconomyCostDeep, deep)
                .Num(IntermediateFormat.KeyEconomyProfitDeep, marketValue - deep)
                .Raw(IntermediateFormat.KeyEconomyRecipeCandidates, BuildRecipeCandidates(def));

            return line.ToString();
        }

        /// <summary>
        /// 成本链逐项展开,每项标出它自己有没有 recipeMaker。没有的那项就是 CostToMake 递归的
        /// 链尾 —— 它在那一项身上第一句就返回 BaseMarketValue,于是上游的 profit 实际由该项
        /// **手填的**市场价决定,而不是由它自己的生产成本决定。只给 CostListString 那个字符串
        /// 看不出这件事。
        /// </summary>
        private static string BuildCostChain(ThingDef def, out float? chainEndShare)
        {
            var costList = def.CostList;
            var sb = new StringBuilder("[");
            var total = 0f;
            var chainEndValue = 0f;

            if (costList != null)
            {
                for (var i = 0; i < costList.Count; i++)
                {
                    if (i > 0) sb.Append(',');

                    var part = costList[i].thingDef;
                    var chainEnd = part.recipeMaker == null;
                    var partValue = costList[i].count * part.BaseMarketValue;
                    total += partValue;
                    if (chainEnd) chainEndValue += partValue;

                    sb.Append(new JsonLine()
                        .Str(IntermediateFormat.KeyEconomyThingDef, part.defName ?? "")
                        .Int(IntermediateFormat.KeyEconomyCount, costList[i].count)
                        .Num(IntermediateFormat.KeyEconomyUnitValue, part.BaseMarketValue)
                        .Bool(IntermediateFormat.KeyEconomyChainEnd, chainEnd)
                        .ToString());
                }
            }

            sb.Append(']');
            // total 为 0 的两种来源都不该给出一个看着像「没问题」的 0.00:无 costList 的物
            // 根本没有链,costList 全是零价物的则无从判断。两者都给 null。
            chainEndShare = total > 0f ? (float?)(chainEndValue / total) : null;
            return sb.ToString();
        }

        /// <summary>
        /// vanilla <c>CostToMake</c> 撞上无 <c>recipeMaker</c> 的物就返回它手填的
        /// <c>BaseMarketValue</c>,链条到此为止;这里唯一的改动是链尾改用
        /// <c>StatWorker_MarketValue.CalculatedBaseMarketValue</c> —— 那同样是 vanilla 自己的
        /// 算法,且对手写 <c>RecipeDef</c> 产出的物有效,正好补上 CostToMake 的盲区。
        ///
        /// 仍不是「重写 CostToMake」:它以自己的名字与 vanilla 值并列输出,两者差异本身即信息。
        /// </summary>
        private static float CostDeep(ThingDef def, HashSet<ThingDef> visiting)
        {
            var costList = def.CostList;
            var hasOwnCost = costList != null && costList.Count > 0;
            if (!hasOwnCost && def.CostStuffCount <= 0)
            {
                var calculated = StatWorker_MarketValue.CalculatedBaseMarketValue(def, null);
                return calculated > 0f ? calculated : def.BaseMarketValue;
            }

            var total = 0f;
            if (hasOwnCost)
            {
                for (var i = 0; i < costList.Count; i++)
                {
                    var part = costList[i].thingDef;
                    // 自引用配方(拿自己当原料的催化式)与成本环都会让递归不终止。撞到正在展开的
                    // 节点就取它的手填价收口 —— 这一处退化是有意的,且因为环上必有一个节点走
                    // 这条路,环本身不会让整棵树失去意义。
                    if (!visiting.Add(part))
                    {
                        total += costList[i].count * part.BaseMarketValue;
                        continue;
                    }

                    total += costList[i].count * CostDeep(part, visiting);
                    visiting.Remove(part);
                }
            }

            if (def.CostStuffCount > 0)
                total += def.CostStuffCount * GenStuff.DefaultStuffFor(def).BaseMarketValue;

            return total;
        }

        /// <summary>
        /// <c>CalculableRecipe</c> 在多个配方都能产出同一个物时返回 <c>DefDatabase</c> 里的
        /// 第一个匹配 —— 取哪个依赖 def 加载顺序,而不同配方的推算价并不相等(等比放大的 bulk
        /// 配方 workAmount 通常不等比,单位成本更低)。把候选全列出来,让「vanilla 挑了哪个」
        /// 不再是黑箱:这个数组长度 &gt; 1 即表示该行的 calculated_market_value 有加载顺序依赖。
        /// </summary>
        private static string BuildRecipeCandidates(ThingDef def)
        {
            var sb = new StringBuilder("[");
            var emitted = 0;
            var recipes = DefDatabase<RecipeDef>.AllDefsListForReading;

            for (var i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i];
                if (recipe.products == null || recipe.products.Count != 1
                    || recipe.products[0].thingDef != def) continue;

                if (emitted > 0) sb.Append(',');
                emitted++;

                // 自引用 = 配方的某个 ingredient 允许产物自己。这类配方算出来的价里混着产物
                // 自己的手填价,与「从原料推算」的前提相悖。
                var selfReferential = false;
                if (recipe.ingredients != null)
                {
                    for (var j = 0; j < recipe.ingredients.Count; j++)
                    {
                        if (recipe.ingredients[j].filter != null && recipe.ingredients[j].filter.Allows(def))
                        {
                            selfReferential = true;
                            break;
                        }
                    }
                }

                sb.Append(new JsonLine()
                    .Str(IntermediateFormat.KeyDefName, recipe.defName ?? "")
                    .Int(IntermediateFormat.KeyEconomyProductCount, recipe.products[0].count)
                    .Num(IntermediateFormat.KeyEconomyWorkAmount, recipe.workAmount)
                    .Bool(IntermediateFormat.KeyEconomySelfReferential, selfReferential)
                    .ToString());
            }

            sb.Append(']');
            return sb.ToString();
        }
    }
}
