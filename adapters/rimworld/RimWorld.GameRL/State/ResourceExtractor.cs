// Extract resource state from RimWorld

using System.Collections.Generic;
using Verse;
using RimWorld;
using MessagePack;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Resource state for observations
    /// </summary>
    [MessagePackObject]
    public class ResourceState
    {
        [Key("stockpiles")]
        public Dictionary<string, int> Stockpiles { get; set; } = new();

        [Key("silver")]
        public int Silver { get; set; }

        [Key("total_wealth")]
        public float TotalWealth { get; set; }

        [Key("food_days")]
        public int FoodDays { get; set; }

        [Key("medicine_count")]
        public int MedicineCount { get; set; }
    }

    /// <summary>
    /// Extracts resource information from the game
    /// </summary>
    public static class ResourceExtractor
    {
        public static ResourceState Extract(Map? map)
        {
            if (map == null)
                return new ResourceState();

            var stockpiles = new Dictionary<string, int>();

            // Get counts of important resources
            var importantDefs = new[]
            {
                ThingDefOf.Steel,
                ThingDefOf.WoodLog,
                ThingDefOf.Plasteel,
                ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer,
                ThingDefOf.Gold,
                ThingDefOf.Uranium,
                ThingDefOf.Chemfuel
            };

            foreach (var def in importantDefs)
            {
                stockpiles[def.defName] = map.resourceCounter.GetCount(def);
            }

            // Food calculation
            float foodCount = map.resourceCounter.TotalHumanEdibleNutrition;
            int colonistCount = map.mapPawns.FreeColonistsCount;
            int foodDays = colonistCount > 0
                ? (int)(foodCount / (colonistCount * 1.6f))  // ~1.6 nutrition per day per colonist
                : 0;

            // Medicine count (all types)
            int medicineCount = 0;
            if (ThingDefOf.MedicineHerbal != null)
                medicineCount += map.resourceCounter.GetCount(ThingDefOf.MedicineHerbal);
            if (ThingDefOf.MedicineIndustrial != null)
                medicineCount += map.resourceCounter.GetCount(ThingDefOf.MedicineIndustrial);
            if (ThingDefOf.MedicineUltratech != null)
                medicineCount += map.resourceCounter.GetCount(ThingDefOf.MedicineUltratech);

            return new ResourceState
            {
                Stockpiles = stockpiles,
                Silver = map.resourceCounter.GetCount(ThingDefOf.Silver),
                TotalWealth = map.wealthWatcher.WealthTotal,
                FoodDays = foodDays,
                MedicineCount = medicineCount
            };
        }
    }
}
