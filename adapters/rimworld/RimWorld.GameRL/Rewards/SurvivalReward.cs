// Survival-focused reward computation for RimWorld

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using RimWorld;
using RimWorld.GameRL.Actions;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// Computes reward components focused on colony survival.
    /// Rewards are computed as deltas between steps to encourage progress.
    /// </summary>
    public class SurvivalReward
    {
        // Action result for invalid action penalty
        private ActionResult? _lastActionResult;

        // Previous state for delta computation
        private int _lastColonistCount;
        private float _lastWealth;
        private int _lastFoodDays;
        private int _lastHostileCount;
        private int _lastFireCount;
        private int _lastConditionCount;
        private int _lastTraderCount;
        private int _lastWoodCount;
        private int _lastSteelCount;
        private int _lastComponentCount;
        private int _lastStoneCount;
        private int _lastSilverCount;
        private int _lastTamedAnimalCount;
        private float _lastResearchProgress;
        private int _lastBuildingCount;

        // New gap-fill tracking fields
        private Dictionary<string, int> _lastFactionGoodwill = new();
        private Dictionary<string, float> _lastSkillXP = new();
        private int _lastPrisonerCount;
        private int _lastDefensiveCount;
        private float _lastAvgRoomImpressiveness;

        // Delta-based needs tracking
        private float _lastAvgMood;
        private float _lastAvgHunger;
        private float _lastAvgHealth;
        private int _lastMentalCount;
        private int _lastExhaustedCount;
        private float _lastTemperatureComfortRatio;

        // Reward rebalancing fields
        private int _lastBlueprintCount;
        private int _traderPresentTicks;

        public SurvivalReward()
        {
            Reset();
        }

        /// <summary>
        /// Set last action result before computing reward
        /// </summary>
        public void SetLastActionResult(ActionResult? result)
        {
            _lastActionResult = result;
        }

        public void Reset()
        {
            var map = Find.CurrentMap;
            _lastColonistCount = map?.mapPawns.FreeColonistsCount ?? 0;
            _lastWealth = map?.wealthWatcher.WealthTotal ?? 0f;
            _lastFoodDays = ComputeFoodDays(map);
            _lastHostileCount = CountHostiles(map);
            _lastFireCount = CountFires(map);
            _lastConditionCount = CountActiveConditions(map);
            _lastTraderCount = CountTraders(map);
            _lastWoodCount = map?.resourceCounter.GetCount(ThingDefOf.WoodLog) ?? 0;
            _lastSteelCount = map?.resourceCounter.GetCount(ThingDefOf.Steel) ?? 0;
            _lastComponentCount = map?.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial) ?? 0;
            _lastStoneCount = CountStoneBlocks(map);
            _lastSilverCount = map?.resourceCounter.GetCount(ThingDefOf.Silver) ?? 0;
            _lastTamedAnimalCount = CountTamedAnimals(map);
            _lastResearchProgress = GetResearchProgress();
            _lastBuildingCount = map?.listerBuildings.allBuildingsColonist.Count ?? 0;
            _lastFactionGoodwill = CaptureFactionGoodwill();
            _lastSkillXP = CaptureSkillXP(map);
            _lastPrisonerCount = CountPrisoners(map);
            _lastDefensiveCount = CountDefensiveStructures(map);
            _lastAvgRoomImpressiveness = GetAverageBedroomImpressiveness(map);
            _lastBlueprintCount = CountBlueprints(map);
            _traderPresentTicks = 0;

            // Initialize delta-based needs tracking
            var resetColonists = map?.mapPawns.FreeColonists.ToList();
            if (resetColonists != null && resetColonists.Count > 0)
            {
                _lastAvgMood = resetColonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                _lastAvgHunger = resetColonists.Average(p => 1.0f - (p.needs?.food?.CurLevelPercentage ?? 1f));
                _lastAvgHealth = resetColonists.Average(p => p.health?.summaryHealth?.SummaryHealthPercent ?? 1f);
                _lastMentalCount = resetColonists.Count(p => p.InMentalState);
                _lastExhaustedCount = resetColonists.Count(p =>
                    p.needs?.rest?.CurLevelPercentage < 0.15f
                    && p.CurJob?.def != JobDefOf.LayDown);
            }
            _lastTemperatureComfortRatio = 1.0f;
        }

        /// <summary>
        /// Reward design principles:
        ///   - All components are delta-based: reward improvement, penalize deterioration
        ///   - Needs (mood, hunger, health) use deltas so correct actions get credit
        ///   - Action success is meaningful (+0.10) relative to delta signals
        ///   - Catastrophic events (death) are large but not gradient-crushing (~10)
        ///   - Small absolute signals remain for infrastructure nudges (no_power, idle)
        /// </summary>
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
            // TIME COST: Small per-step penalty to discourage passivity
            // ~15000 steps per episode → total ~-150 if agent does nothing
            // ═══════════════════════════════════════════════════════════════
            components["time"] = -0.01;

            // ═══════════════════════════════════════════════════════════════
            // CRITICAL: Colonist survival
            // Death = -10 (recoverable in ~1 game day of good play)
            // Recruit = +5 (significant but less than a death)
            // ═══════════════════════════════════════════════════════════════
            var colonistDelta = colonistCount - _lastColonistCount;
            if (colonistDelta < 0)
            {
                components["colonist_death"] = colonistDelta * 10.0;
            }
            else if (colonistDelta > 0)
            {
                components["colonist_recruited"] = colonistDelta * 5.0;
            }

            // ═══════════════════════════════════════════════════════════════
            // NEEDS: Delta-based signals — reward improvement, penalize deterioration
            // ═══════════════════════════════════════════════════════════════
            if (colonistCount > 0)
            {
                // Mood: reward mood improvement, penalize decline
                var moodAvg = colonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                var moodDelta = moodAvg - _lastAvgMood;
                if (System.Math.Abs(moodDelta) > 0.01f)
                    components["mood"] = System.Math.Clamp(moodDelta * 3.0, -0.3, 0.3);
                _lastAvgMood = moodAvg;

                // Hunger: reward feeding (hunger decrease), penalize starvation increase
                var avgHunger = colonists.Average(p => 1.0f - (p.needs?.food?.CurLevelPercentage ?? 1f));
                var hungerDelta = avgHunger - _lastAvgHunger;
                if (System.Math.Abs(hungerDelta) > 0.01f)
                    components["hunger"] = System.Math.Clamp(-hungerDelta * 3.0, -0.5, 0.5);
                _lastAvgHunger = avgHunger;

                // Exhaustion: reward rest recovery, penalize new exhaustion
                var exhaustedCount = colonists.Count(p =>
                    p.needs?.rest?.CurLevelPercentage < 0.15f
                    && p.CurJob?.def != JobDefOf.LayDown);
                var exhaustedDelta = exhaustedCount - _lastExhaustedCount;
                if (exhaustedDelta != 0)
                    components["exhaustion"] = System.Math.Clamp(-exhaustedDelta * 0.15, -0.3, 0.3);
                _lastExhaustedCount = exhaustedCount;

                // Health: reward healing, penalize injury
                var avgHealth = colonists.Average(p => p.health?.summaryHealth?.SummaryHealthPercent ?? 1f);
                var healthDelta = avgHealth - _lastAvgHealth;
                if (System.Math.Abs(healthDelta) > 0.01f)
                    components["health"] = System.Math.Clamp(healthDelta * 5.0, -0.5, 0.5);
                _lastAvgHealth = avgHealth;

                // Mental break: reward recovery, penalize new breaks
                var mentalCount = colonists.Count(p => p.InMentalState);
                var mentalDelta = mentalCount - _lastMentalCount;
                if (mentalDelta != 0)
                    components["mental_break"] = System.Math.Clamp(-mentalDelta * 0.3, -0.6, 0.3);
                _lastMentalCount = mentalCount;
            }

            // ═══════════════════════════════════════════════════════════════
            // PRODUCTIVITY: Idle penalty, wealth growth
            // Only penalize sustained wandering, not brief job transitions
            // ═══════════════════════════════════════════════════════════════
            if (colonistCount > 0)
            {
                var idleCount = colonists.Count(p =>
                    p.CurJob?.def == JobDefOf.Wait_Wander);
                // Only penalize if more than 1/3 of colony is wandering
                var idleFraction = (float)idleCount / colonistCount;
                if (idleFraction > 0.33f)
                {
                    components["idle"] = -(idleFraction - 0.33) * 0.3;
                }
            }

            // Wealth progress (capped)
            var wealthDelta = wealth - _lastWealth;
            var wealthReward = System.Math.Clamp(wealthDelta / 5000.0, -0.3, 0.3);
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
                components["food_security"] = System.Math.Clamp(foodDelta * 0.05, -0.3, 0.3);
            }

            // Penalty for no food production infrastructure
            if (colonistCount > 0 && foodDays < 5)
            {
                try
                {
                    var hasGrowingZone = map.zoneManager.AllZones.Any(z => z is Zone_Growing);
                    var hasHuntDesignation = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Hunt).Any();
                    var hasCookBill = map.listerBuildings.allBuildingsColonist
                        .OfType<IBillGiver>()
                        .Any(bg => bg.BillStack.Bills.Any(b =>
                            b.recipe?.products?.Any(p => p.thingDef?.IsNutritionGivingIngestible == true) == true));

                    if (!hasGrowingZone && !hasHuntDesignation && !hasCookBill)
                    {
                        components["no_food_production"] = -0.10;
                    }
                }
                catch { }
            }

            // ═══════════════════════════════════════════════════════════════
            // THREATS: Combat, fire, conditions
            // ═══════════════════════════════════════════════════════════════
            var hostileCount = CountHostiles(map);
            var hostileDelta = hostileCount - _lastHostileCount;
            if (hostileDelta < 0)
            {
                components["threat_eliminated"] = System.Math.Min(-hostileDelta * 0.3, 1.0);
            }
            else if (hostileDelta > 0)
            {
                // Small penalty — raids aren't the agent's fault
                components["threat_appeared"] = System.Math.Max(-hostileDelta * 0.05, -0.3);
            }

            // Fire: continuous penalty, bonus for putting out
            var fireCount = CountFires(map);
            if (fireCount > 0)
            {
                components["fire_active"] = -System.Math.Min(fireCount * 0.05, 0.50);
            }
            var fireDelta = fireCount - _lastFireCount;
            if (fireDelta < 0 && _lastFireCount > 0)
            {
                components["fire_extinguished"] = System.Math.Min(-fireDelta * 0.05, 0.3);
            }

            // Game conditions (toxic fallout, solar flare, etc.)
            var conditionCount = CountActiveConditions(map);
            if (conditionCount > _lastConditionCount)
            {
                components["condition_started"] = -(conditionCount - _lastConditionCount) * 0.1;
            }

            // ═══════════════════════════════════════════════════════════════
            // VISITORS & TRADERS: Opportunity awareness
            // Moved after RESOURCES section so silverDelta is available
            // ═══════════════════════════════════════════════════════════════
            var traderCount = CountTraders(map);

            // ═══════════════════════════════════════════════════════════════
            // RESOURCES: Gathering and mining materials
            // ═══════════════════════════════════════════════════════════════
            var woodCount = map.resourceCounter.GetCount(ThingDefOf.WoodLog);
            var steelCount = map.resourceCounter.GetCount(ThingDefOf.Steel);
            var componentCount = map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial);
            var stoneCount = CountStoneBlocks(map);
            var silverCount = map.resourceCounter.GetCount(ThingDefOf.Silver);

            var woodDelta = woodCount - _lastWoodCount;
            var steelDelta = steelCount - _lastSteelCount;
            var componentDelta = componentCount - _lastComponentCount;
            var stoneDelta = stoneCount - _lastStoneCount;
            var silverDelta = silverCount - _lastSilverCount;

            if (woodDelta > 0)
                components["wood_gathered"] = System.Math.Min(woodDelta * 0.001, 0.1);
            if (steelDelta > 0)
                components["steel_gathered"] = System.Math.Min(steelDelta * 0.002, 0.1);
            if (componentDelta > 0)
                components["components_gained"] = System.Math.Min(componentDelta * 0.01, 0.2);
            if (stoneDelta > 0)
                components["stone_mined"] = System.Math.Min(stoneDelta * 0.001, 0.1);
            if (silverDelta > 0)
                components["silver_gained"] = System.Math.Min(silverDelta * 0.0005, 0.1);

            // ═══════════════════════════════════════════════════════════════
            // TRADERS: Full opportunity cost logic (silverDelta now available)
            // ═══════════════════════════════════════════════════════════════
            if (traderCount > _lastTraderCount)
            {
                components["trader_arrived"] = (traderCount - _lastTraderCount) * 0.15;
                _traderPresentTicks = 0;
            }

            if (traderCount > 0)
            {
                _traderPresentTicks++;
                // Escalating urgency while trader is present
                components["trader_opportunity"] = -System.Math.Min(_traderPresentTicks * 0.02, 0.10);
            }
            else if (_lastTraderCount > 0 && traderCount == 0)
            {
                // Trader just left — penalize if no trade occurred (silver unchanged)
                if (silverDelta == 0)
                {
                    components["trader_missed"] = -0.50;
                }
                _traderPresentTicks = 0;
            }

            // ═══════════════════════════════════════════════════════════════
            // HUSBANDRY: Taming and managing animals
            // ═══════════════════════════════════════════════════════════════
            var tamedCount = CountTamedAnimals(map);
            var tamedDelta = tamedCount - _lastTamedAnimalCount;
            if (tamedDelta > 0)
            {
                components["animal_tamed"] = System.Math.Min(tamedDelta * 0.2, 0.5);
            }
            else if (tamedDelta < 0)
            {
                // Animal died or was sold — mild signal, not always bad
                components["animal_lost"] = System.Math.Max(tamedDelta * 0.05, -0.2);
            }

            // ═══════════════════════════════════════════════════════════════
            // RESEARCH: Progress on current project (capped)
            // Penalty for idle research bench with no active project
            // ═══════════════════════════════════════════════════════════════
            var currentProgress = GetResearchProgress();
            if (currentProgress > 0)
            {
                var progressDelta = currentProgress - _lastResearchProgress;
                if (progressDelta > 0)
                {
                    components["research"] = System.Math.Min(progressDelta * 1.0, 0.5);
                }
                if (currentProgress >= 1.0f && _lastResearchProgress < 1.0f)
                {
                    components["research_complete"] = 2.0;
                }
            }
            else
            {
                // No active research — nudge agent to select a project
                try
                {
                    var hasResearchBench = map.listerBuildings.allBuildingsColonist
                        .Any(b => b.def.defName.Contains("Research"));
                    var hasAvailableProjects = DefDatabase<ResearchProjectDef>.AllDefs
                        .Any(p => !p.IsFinished);
                    if (hasAvailableProjects)
                    {
                        if (hasResearchBench)
                        {
                            // Bench exists but no project — clear missed opportunity
                            components["no_research"] = -0.08;
                        }
                        else
                        {
                            // No bench yet — gentle nudge to build one
                            components["no_research"] = -0.03;
                        }
                    }
                }
                catch { }
            }
            _lastResearchProgress = currentProgress;

            // ═══════════════════════════════════════════════════════════════
            // CONSTRUCTION: Building new structures
            // ═══════════════════════════════════════════════════════════════
            var buildingCount = map.listerBuildings.allBuildingsColonist.Count;
            var buildingDelta = buildingCount - _lastBuildingCount;
            if (buildingDelta > 0)
            {
                components["construction"] = System.Math.Min(buildingDelta * 0.05, 0.3);
            }

            // ═══════════════════════════════════════════════════════════════
            // BLUEPRINT BACKLOG: Penalize queuing work without completing it
            // ═══════════════════════════════════════════════════════════════
            var blueprintCount = CountBlueprints(map);
            if (blueprintCount > 3)
            {
                // Escalating: 4 = -0.02, 10 = -0.14, 18+ capped at -0.3
                components["blueprint_backlog"] = -System.Math.Min((blueprintCount - 3) * 0.02, 0.3);
            }
            _lastBlueprintCount = blueprintCount;

            // ═══════════════════════════════════════════════════════════════
            // POWER INFRASTRUCTURE: Nudge toward building generators
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var generatorCount = map.listerBuildings.allBuildingsColonist
                    .Count(b => b.TryGetComp<CompPowerPlant>() != null);
                var hasPowerConsumers = map.listerBuildings.allBuildingsColonist
                    .Any(b => b.TryGetComp<CompPowerTrader>() != null
                            && b.TryGetComp<CompPowerTrader>()!.PowerOutput < 0);

                if (hasPowerConsumers && generatorCount == 0)
                {
                    // Have things that NEED power but no generators
                    components["no_power"] = -0.12;
                }
                else if (generatorCount == 0 && buildingCount > 5)
                {
                    // Colony is growing but hasn't built power yet
                    components["no_power"] = -0.04;
                }
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // FACTION GOODWILL: Diplomacy changes
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var currentGoodwill = CaptureFactionGoodwill();
                double goodwillDelta = 0;
                foreach (var kvp in currentGoodwill)
                {
                    var prev = _lastFactionGoodwill.TryGetValue(kvp.Key, out var p) ? p : 0;
                    goodwillDelta += (kvp.Value - prev);
                }
                if (System.Math.Abs(goodwillDelta) > 0)
                {
                    components["faction_goodwill"] = System.Math.Clamp(goodwillDelta * 0.005, -0.3, 0.3);
                }
                _lastFactionGoodwill = currentGoodwill;
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // SKILL PROGRESSION: XP gains across colonists
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var currentXP = CaptureSkillXP(map);
                double xpGain = 0;
                foreach (var kvp in currentXP)
                {
                    var prev = _lastSkillXP.TryGetValue(kvp.Key, out var p) ? p : 0f;
                    xpGain += System.Math.Max(0, kvp.Value - prev);
                }
                if (xpGain > 0)
                {
                    components["skill_progression"] = System.Math.Min(xpGain * 0.0001, 0.2);
                }
                _lastSkillXP = currentXP;
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // TRADE PROFIT: Silver changes when traders present
            // ═══════════════════════════════════════════════════════════════
            if (silverDelta != 0 && traderCount > 0)
            {
                if (silverDelta > 0)
                    components["trade_profit"] = System.Math.Min(silverDelta * 0.001, 0.2);
            }

            // ═══════════════════════════════════════════════════════════════
            // PRISONER RECRUITED: Prisoner count drops + colonist count rises
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var prisonerCount = CountPrisoners(map);
                var prisonerDelta = prisonerCount - _lastPrisonerCount;
                if (prisonerDelta < 0 && colonistDelta > 0)
                {
                    components["prisoner_recruited"] = System.Math.Min(-prisonerDelta * 1.0, 3.0);
                }
                _lastPrisonerCount = prisonerCount;
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // DEFENSIVE STRUCTURES: Turrets, walls, sandbags, traps
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var defenseCount = CountDefensiveStructures(map);
                var defenseDelta = defenseCount - _lastDefensiveCount;
                if (defenseDelta > 0)
                {
                    components["defensive_structures"] = System.Math.Min(defenseDelta * 0.05, 0.3);
                }
                _lastDefensiveCount = defenseCount;
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // TEMPERATURE DISCOMFORT: Colonists outside comfort range
            // ═══════════════════════════════════════════════════════════════
            if (colonistCount > 0)
            {
                try
                {
                    int comfortableCount = 0;
                    foreach (var c in colonists)
                    {
                        var ambientTemp = c.AmbientTemperature;
                        var comfyMin = c.GetStatValue(StatDefOf.ComfyTemperatureMin);
                        var comfyMax = c.GetStatValue(StatDefOf.ComfyTemperatureMax);
                        if (ambientTemp >= comfyMin && ambientTemp <= comfyMax)
                            comfortableCount++;
                    }
                    var comfortRatio = (float)comfortableCount / colonistCount;
                    var tempDelta = comfortRatio - _lastTemperatureComfortRatio;
                    if (System.Math.Abs(tempDelta) > 0.01f)
                        components["temperature_comfort"] = System.Math.Clamp(tempDelta * 1.0, -0.2, 0.2);
                    _lastTemperatureComfortRatio = comfortRatio;
                }
                catch { }
            }

            // ═══════════════════════════════════════════════════════════════
            // ROOM QUALITY: Average bedroom impressiveness improvement
            // ═══════════════════════════════════════════════════════════════
            try
            {
                var avgImpressiveness = GetAverageBedroomImpressiveness(map);
                var impressDelta = avgImpressiveness - _lastAvgRoomImpressiveness;
                if (impressDelta > 0.5f)
                {
                    components["room_quality"] = System.Math.Min(impressDelta * 0.02, 0.2);
                }
                _lastAvgRoomImpressiveness = avgImpressiveness;
            }
            catch { }

            // ═══════════════════════════════════════════════════════════════
            // ACTION FEEDBACK: Gentle invalid penalty, small success bonus
            // ═══════════════════════════════════════════════════════════════
            if (_lastActionResult != null)
            {
                if (_lastActionResult.Success
                    && _lastActionResult.ActionType != "RequestFullState")
                {
                    // No reward for queueing work — only reward actions that DO something
                    if (_lastActionResult.ActionType == "PlaceBlueprint")
                    {
                        // Blueprint placement gets no action_success bonus
                    }
                    else
                    {
                        components["action_success"] = 0.10;
                    }
                }
                else if (!_lastActionResult.Success)
                {
                    double penalty = _lastActionResult.ErrorCode switch
                    {
                        ActionErrorCode.UnknownAction => -0.05,
                        ActionErrorCode.TargetNotFound => -0.03,
                        ActionErrorCode.InvalidTarget => -0.03,
                        ActionErrorCode.PreconditionFailed => -0.02,
                        ActionErrorCode.NoMap => -0.05,
                        ActionErrorCode.InvalidPosition => -0.02,
                        ActionErrorCode.InsufficientResources => -0.01,
                        ActionErrorCode.NoEffect => -0.005,
                        ActionErrorCode.InternalError => -0.02,
                        _ => -0.02
                    };
                    components["invalid_action"] = penalty;
                }
            }
            _lastActionResult = null;

            // ═══════════════════════════════════════════════════════════════
            // Update state for next computation
            // ═══════════════════════════════════════════════════════════════
            _lastColonistCount = colonistCount;
            _lastWealth = wealth;
            _lastFoodDays = foodDays;
            _lastHostileCount = hostileCount;
            _lastFireCount = fireCount;
            _lastConditionCount = conditionCount;
            _lastTraderCount = traderCount;
            _lastWoodCount = woodCount;
            _lastSteelCount = steelCount;
            _lastComponentCount = componentCount;
            _lastStoneCount = stoneCount;
            _lastSilverCount = silverCount;
            _lastTamedAnimalCount = tamedCount;
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

        private static int CountTamedAnimals(Map? map)
        {
            if (map == null) return 0;
            try
            {
                return map.mapPawns.PawnsInFaction(Faction.OfPlayer)
                    .Count(p => p != null && !p.Destroyed && p.Spawned && p.RaceProps.Animal);
            }
            catch { return 0; }
        }

        private static int CountStoneBlocks(Map? map)
        {
            if (map == null) return 0;
            try
            {
                int count = 0;
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.IsStuff && def.stuffProps?.categories != null
                        && def.stuffProps.categories.Any(c => c.defName == "Stony"))
                    {
                        count += map.resourceCounter.GetCount(def);
                    }
                }
                return count;
            }
            catch { return 0; }
        }

        private static int CountFires(Map? map)
        {
            if (map == null) return 0;
            try { return map.listerThings.ThingsOfDef(ThingDefOf.Fire)?.Count ?? 0; }
            catch { return 0; }
        }

        private static int CountActiveConditions(Map? map)
        {
            if (map == null) return 0;
            try { return map.gameConditionManager?.ActiveConditions?.Count ?? 0; }
            catch { return 0; }
        }

        private static int CountTraders(Map? map)
        {
            if (map == null) return 0;
            try
            {
                int count = 0;
                // Orbital traders
                var ships = map.passingShipManager?.passingShips;
                if (ships != null)
                    count += ships.Count(s => s is TradeShip);
                // Visitor traders on map
                count += map.mapPawns.AllPawnsSpawned
                    .Count(p => p != null && !p.Destroyed && p.Spawned
                        && p.TraderKind != null && p.Faction != Faction.OfPlayer);
                return count;
            }
            catch { return 0; }
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

        private static Dictionary<string, int> CaptureFactionGoodwill()
        {
            var result = new Dictionary<string, int>();
            try
            {
                var player = Faction.OfPlayer;
                foreach (var faction in Find.FactionManager.AllFactionsVisibleInViewOrder)
                {
                    if (faction == player || faction.Hidden) continue;
                    var rel = faction.RelationWith(player, allowNull: true);
                    if (rel != null) result[faction.Name] = rel.baseGoodwill;
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, float> CaptureSkillXP(Map? map)
        {
            var result = new Dictionary<string, float>();
            if (map == null) return result;
            try
            {
                foreach (var pawn in map.mapPawns.FreeColonists)
                {
                    if (pawn?.skills == null) continue;
                    float total = pawn.skills.skills.Sum(s => s.XpTotalEarned);
                    result[pawn.ThingID] = total;
                }
            }
            catch { }
            return result;
        }

        private static int CountPrisoners(Map? map)
        {
            if (map == null) return 0;
            try { return map.mapPawns.PrisonersOfColony?.Count() ?? 0; }
            catch { return 0; }
        }

        private static int CountBlueprints(Map? map)
        {
            if (map == null) return 0;
            try
            {
                return map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                    ?.Count(b => b.Faction == Faction.OfPlayer) ?? 0;
            }
            catch { return 0; }
        }

        private static int CountDefensiveStructures(Map? map)
        {
            if (map == null) return 0;
            try
            {
                return map.listerBuildings.allBuildingsColonist.Count(b =>
                    b.def.defName.Contains("Turret") ||
                    b.def == ThingDefOf.Wall ||
                    b.def.defName.Contains("Trap") ||
                    b.def.defName.Contains("Sandbag") ||
                    b.def.defName.Contains("Barricade"));
            }
            catch { return 0; }
        }

        private static float GetAverageBedroomImpressiveness(Map? map)
        {
            if (map == null) return 0f;
            try
            {
                var bedrooms = map.listerBuildings.allBuildingsColonist
                    .OfType<Building_Bed>()
                    .Where(b => !b.Medical && !b.ForPrisoners && b.GetRoom() != null)
                    .Select(b => b.GetRoom())
                    .Distinct()
                    .ToList();
                if (bedrooms.Count == 0) return 0f;
                return bedrooms.Average(r => r.GetStat(RoomStatDefOf.Impressiveness));
            }
            catch { return 0f; }
        }
    }
}
