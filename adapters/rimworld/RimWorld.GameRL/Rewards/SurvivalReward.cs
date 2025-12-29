// Survival-focused reward computation for RimWorld

using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Rewards
{
    /// <summary>
    /// Computes reward components focused on colony survival
    /// </summary>
    public class SurvivalReward
    {
        private int _lastColonistCount;
        private float _lastWealth;
        private float _lastMoodAvg;

        public SurvivalReward()
        {
            Reset();
        }

        public void Reset()
        {
            var map = Find.CurrentMap;
            _lastColonistCount = map?.mapPawns.FreeColonistsCount ?? 0;
            _lastWealth = map?.wealthWatcher.WealthTotal ?? 0f;
            _lastMoodAvg = 0.5f;
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
            var moodAvg = colonists.Count > 0
                ? colonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f)
                : 0f;

            // Colonist survival (big negative for deaths)
            var colonistDelta = colonistCount - _lastColonistCount;
            if (colonistDelta < 0)
            {
                components["colonist_death"] = colonistDelta * -100.0;
            }

            // Small bonus for keeping colonists alive
            components["colonist_alive"] = colonistCount * 0.01;

            // Wealth progress (normalized)
            var wealthDelta = wealth - _lastWealth;
            components["wealth_delta"] = wealthDelta / 10000.0;

            // Mood (centered at 50%)
            components["mood"] = (moodAvg - 0.5) * 0.1;

            // Idle penalty
            var idleCount = colonists.Count(p =>
                p.CurJob?.def == JobDefOf.Wait_Wander ||
                p.CurJob?.def == JobDefOf.Wait);
            if (idleCount > 0)
            {
                components["idle_penalty"] = -idleCount * 0.05;
            }

            // Health penalty for injured colonists
            var injuredCount = colonists.Count(p =>
                p.health?.summaryHealth?.SummaryHealthPercent < 0.9f);
            if (injuredCount > 0)
            {
                components["injury_penalty"] = -injuredCount * 0.02;
            }

            // Hunger penalty
            var hungryCount = colonists.Count(p =>
                p.needs?.food?.CurLevelPercentage < 0.3f);
            if (hungryCount > 0)
            {
                components["hunger_penalty"] = -hungryCount * 0.05;
            }

            // Update state for next computation
            _lastColonistCount = colonistCount;
            _lastWealth = wealth;
            _lastMoodAvg = moodAvg;

            return components;
        }
    }
}
