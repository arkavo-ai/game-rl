// Extract resource state from RimWorld

using System.Collections.Generic;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Resource state for observations
    /// </summary>
    public class ResourceState
    {
        public Dictionary<string, int> Stockpiles { get; set; } = new();

        public int Silver { get; set; }

        public float TotalWealth { get; set; }

        /// <summary>
        /// Days of food colonists can actually access (unforbidden)
        /// </summary>
        public int AccessibleFoodDays { get; set; }

        /// <summary>
        /// Days of food counting ALL items on map (including forbidden)
        /// </summary>
        public int TotalFoodDays { get; set; }

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

            // Count ALL items on the map (including forbidden) via listerThings
            // resourceCounter.GetCount() only counts non-forbidden items in stockpiles,
            // which reads as 0 at game start when everything is forbidden
            foreach (var def in importantDefs)
            {
                int total = 0;
                foreach (var thing in map.listerThings.ThingsOfDef(def))
                {
                    if (thing != null && !thing.Destroyed && thing.Spawned)
                        total += thing.stackCount;
                }
                stockpiles[def.defName] = total;
            }

            // Food calculation - count accessible vs total separately
            float accessibleFood = 0f;
            float totalFood = 0f;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing != null && !thing.Destroyed && thing.Spawned && thing.def.IsNutritionGivingIngestible)
                {
                    float nutrition = thing.GetStatValue(RimWorld.StatDefOf.Nutrition) * thing.stackCount;
                    totalFood += nutrition;
                    if (thing is ThingWithComps twc && !twc.IsForbidden(Faction.OfPlayer))
                        accessibleFood += nutrition;
                    else if (!(thing is ThingWithComps))
                        accessibleFood += nutrition;  // Non-comp things can't be forbidden
                }
            }
            int colonistCount = map.mapPawns.FreeColonistsCount;
            int accessibleFoodDays = colonistCount > 0
                ? (int)(accessibleFood / (colonistCount * 1.6f))
                : 0;
            int totalFoodDays = colonistCount > 0
                ? (int)(totalFood / (colonistCount * 1.6f))
                : 0;

            // Medicine count (all types) - count ALL including forbidden
            int medicineCount = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing != null && !thing.Destroyed && thing.Spawned && thing.def.IsMedicine)
                    medicineCount += thing.stackCount;
            }

            // Silver - count all on map
            int silver = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
            {
                if (thing != null && !thing.Destroyed && thing.Spawned)
                    silver += thing.stackCount;
            }

            return new ResourceState
            {
                Stockpiles = stockpiles,
                Silver = silver,
                TotalWealth = map.wealthWatcher.WealthTotal,
                AccessibleFoodDays = accessibleFoodDays,
                TotalFoodDays = totalFoodDays,
                MedicineCount = medicineCount
            };
        }
    }
}
