// Survival-focused reward computation for RimWorld

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// Computes reward components focused on colony survival.
    /// Rewards are computed as deltas between steps to encourage progress.
    /// </summary>
    public class SurvivalReward
    {
        // Previous state for delta computation
        private int _lastColonistCount;
        private float _lastWealth;
        private int _lastFoodDays;
        private int _lastHostileCount;
        private int _lastWoodCount;
        private int _lastSteelCount;
        private float _lastResearchProgress;
        private int _lastBuildingCount;

        public SurvivalReward()
        {
            Reset();
        }

        public void Reset()
        {
            var map = Find.CurrentMap;
            _lastColonistCount = map?.mapPawns.FreeColonistsCount ?? 0;
            _lastWealth = map?.wealthWatcher.WealthTotal ?? 0f;
            _lastFoodDays = ComputeFoodDays(map);
            _lastHostileCount = CountHostiles(map);
            _lastWoodCount = map?.resourceCounter.GetCount(ThingDefOf.WoodLog) ?? 0;
            _lastSteelCount = map?.resourceCounter.GetCount(ThingDefOf.Steel) ?? 0;
            _lastResearchProgress = GetResearchProgress();
            _lastBuildingCount = map?.listerBuildings.allBuildingsColonist.Count ?? 0;
        }

        public Dictionary<string, double> Compute()
        {
            var components = new Dictionary<string, double>();
            var map = Find.CurrentMap;

            if (map == null)
                return components;

            var colonists = map.mapPawns.FreeColonists.ToList();
            var colonistCount = colonists.Count;
            var wealth = map.wealthWatcher.WealthTotal;

            // ═══════════════════════════════════════════════════════════════
            // CRITICAL: Colonist survival (large magnitude)
            // ═══════════════════════════════════════════════════════════════
            var colonistDelta = colonistCount - _lastColonistCount;
            if (colonistDelta < 0)
            {
                // Major penalty for deaths
                components["colonist_death"] = colonistDelta * 100.0;
            }
            else if (colonistDelta > 0)
            {
                // Bonus for recruiting new colonists
                components["colonist_recruited"] = colonistDelta * 10.0;
            }

            // ═══════════════════════════════════════════════════════════════
            // NEEDS: Hunger, mood, health (continuous signals)
            // ═══════════════════════════════════════════════════════════════
            if (colonistCount > 0)
            {
                // Mood: reward for happy colonists, penalty for unhappy
                var moodAvg = colonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                components["mood"] = (moodAvg - 0.5) * 0.2;

                // Hunger: penalty scales with severity
                var avgHunger = colonists.Average(p => 1.0f - (p.needs?.food?.CurLevelPercentage ?? 1f));
                if (avgHunger > 0.3f)
                {
                    components["hunger"] = -avgHunger * 0.3;
                }

                // Rest: penalty for exhausted colonists
                var exhaustedCount = colonists.Count(p => p.needs?.rest?.CurLevelPercentage < 0.2f);
                if (exhaustedCount > 0)
                {
                    components["exhaustion"] = -exhaustedCount * 0.1;
                }

                // Health: penalty for injuries
                var avgHealth = colonists.Average(p => p.health?.summaryHealth?.SummaryHealthPercent ?? 1f);
                if (avgHealth < 0.9f)
                {
                    components["health"] = (avgHealth - 1.0) * 0.2;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // PRODUCTIVITY: Idle penalty, wealth growth
            // ═══════════════════════════════════════════════════════════════
            var idleCount = colonists.Count(p =>
                p.CurJob?.def == JobDefOf.Wait_Wander ||
                p.CurJob?.def == JobDefOf.Wait);
            if (idleCount > 0)
            {
                components["idle"] = -idleCount * 0.05;
            }

            // Wealth progress (capped to avoid exploitation)
            var wealthDelta = wealth - _lastWealth;
            var wealthReward = System.Math.Clamp(wealthDelta / 5000.0, -0.5, 0.5);
            if (System.Math.Abs(wealthReward) > 0.001)
            {
                components["wealth"] = wealthReward;
            }

            // ═══════════════════════════════════════════════════════════════
            // FOOD SECURITY: Days of food stockpiled
            // ═══════════════════════════════════════════════════════════════
            var foodDays = ComputeFoodDays(map);
            var foodDelta = foodDays - _lastFoodDays;
            if (foodDelta != 0)
            {
                // Reward for building food reserves, penalty for losing them
                components["food_security"] = System.Math.Clamp(foodDelta * 0.1, -0.5, 0.5);
            }
            // Continuous penalty if food is critically low
            if (foodDays < 2)
            {
                components["food_critical"] = -0.2;
            }

            // ═══════════════════════════════════════════════════════════════
            // THREATS: Bonus for eliminating hostiles
            // ═══════════════════════════════════════════════════════════════
            var hostileCount = CountHostiles(map);
            var hostileDelta = hostileCount - _lastHostileCount;
            if (hostileDelta < 0)
            {
                // Bonus for killing hostiles
                components["threat_eliminated"] = -hostileDelta * 0.5;
            }
            else if (hostileDelta > 0)
            {
                // Penalty for new threats appearing (raid started)
                components["threat_appeared"] = -hostileDelta * 0.1;
            }

            // ═══════════════════════════════════════════════════════════════
            // RESOURCES: Gathering wood, steel, etc.
            // ═══════════════════════════════════════════════════════════════
            var woodCount = map.resourceCounter.GetCount(ThingDefOf.WoodLog);
            var steelCount = map.resourceCounter.GetCount(ThingDefOf.Steel);

            var woodDelta = woodCount - _lastWoodCount;
            var steelDelta = steelCount - _lastSteelCount;

            if (woodDelta > 0)
            {
                components["wood_gathered"] = System.Math.Min(woodDelta * 0.002, 0.2);
            }
            if (steelDelta > 0)
            {
                components["steel_gathered"] = System.Math.Min(steelDelta * 0.003, 0.2);
            }

            // ═══════════════════════════════════════════════════════════════
            // RESEARCH: Progress on current project
            // ═══════════════════════════════════════════════════════════════
            var currentProgress = GetResearchProgress();
            if (currentProgress > 0)
            {
                var progressDelta = currentProgress - _lastResearchProgress;
                if (progressDelta > 0)
                {
                    components["research"] = progressDelta * 2.0;
                }
                // Bonus for completing research
                if (currentProgress >= 1.0f && _lastResearchProgress < 1.0f)
                {
                    components["research_complete"] = 1.0;
                }
            }
            _lastResearchProgress = currentProgress;

            // ═══════════════════════════════════════════════════════════════
            // CONSTRUCTION: Building new structures
            // ═══════════════════════════════════════════════════════════════
            var buildingCount = map.listerBuildings.allBuildingsColonist.Count;
            var buildingDelta = buildingCount - _lastBuildingCount;
            if (buildingDelta > 0)
            {
                components["construction"] = buildingDelta * 0.1;
            }

            // ═══════════════════════════════════════════════════════════════
            // Update state for next computation
            // ═══════════════════════════════════════════════════════════════
            _lastColonistCount = colonistCount;
            _lastWealth = wealth;
            _lastFoodDays = foodDays;
            _lastHostileCount = hostileCount;
            _lastWoodCount = woodCount;
            _lastSteelCount = steelCount;
            _lastBuildingCount = buildingCount;

            return components;
        }

        private static int ComputeFoodDays(Map? map)
        {
            if (map == null) return 0;
            float foodCount = map.resourceCounter.TotalHumanEdibleNutrition;
            int colonistCount = map.mapPawns.FreeColonistsCount;
            return colonistCount > 0 ? (int)(foodCount / (colonistCount * 1.6f)) : 0;
        }

        private static int CountHostiles(Map? map)
        {
            if (map == null) return 0;
            try
            {
                return map.mapPawns.AllPawnsSpawned
                    .Count(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer));
            }
            catch
            {
                return 0;
            }
        }

        private static float GetResearchProgress()
        {
            try
            {
                var researchManager = Find.ResearchManager;
                if (researchManager == null) return 0f;

                // Use reflection to safely get current project (API varies by RimWorld version)
                var field = typeof(ResearchManager).GetField("currentProj",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null)
                {
                    // Try public property
                    var prop = typeof(ResearchManager).GetProperty("CurrentProject");
                    if (prop != null)
                    {
                        var proj = prop.GetValue(researchManager) as ResearchProjectDef;
                        return proj?.ProgressPercent ?? 0f;
                    }
                    return 0f;
                }

                var currentProj = field.GetValue(researchManager) as ResearchProjectDef;
                return currentProj?.ProgressPercent ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
