using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using RimSearcher.Contract;
using RimWorld;
using Verse;

namespace RimSearcher.DataMod
{
    public static class EconomyExporter
    {
        private static MethodInfo _costToMake;
        private static MethodInfo _producible;

        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

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

        public static List<string> Build()
        {
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
                .Str(IntermediateFormat.KeyEconomyMod,
                     def.modContentPack == null ? "" : def.modContentPack.PackageId)
                .Str(IntermediateFormat.KeyEconomyCategory, def.category.ToString())
                .Num(IntermediateFormat.KeyEconomyMarketValue, marketValue)
                .Bool(IntermediateFormat.KeyEconomyProducible, producible)
                .Bool(IntermediateFormat.KeyEconomyMadeFromStuff, def.MadeFromStuff)
                .Bool(IntermediateFormat.KeyEconomyIsWeapon, def.IsWeapon)
                .Bool(IntermediateFormat.KeyEconomyIsApparel, def.IsApparel)
                .Bool(IntermediateFormat.KeyEconomyMarketValueDefined,
                      def.StatBaseDefined(StatDefOf.MarketValue));

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

            var hasCostEntries = (def.CostList != null && def.CostList.Count > 0) || def.MadeFromStuff;
            line.Num(IntermediateFormat.KeyEconomyWorkToProduce, work > 0f ? (float?)work : null)
                .Str(IntermediateFormat.KeyEconomyCostList,
                     hasCostEntries ? (DebugOutputsEconomy.CostListString(def, false, false) ?? "") : "");

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
            chainEndShare = total > 0f ? (float?)(chainEndValue / total) : null;
            return sb.ToString();
        }

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
