// Command executor for RimWorld actions

using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using GameRL.Harmony;
using RimWorld.GameRL.Patches;
using RimWorld.GameRL.Rewards;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Executes commands in RimWorld
    /// </summary>
    public class RimWorldCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, AgentInfo> _agents = new();
        private readonly SurvivalReward _rewardCalculator = new();
        private int _episodeStartTick;
        private const int MaxEpisodeTicks = 60000 * 15;  // 15 in-game days

        public bool RegisterAgent(string agentId, string agentType, Dictionary<string, object> config)
        {
            if (_agents.ContainsKey(agentId))
                return false;

            _agents[agentId] = new AgentInfo
            {
                AgentType = agentType,
                Config = config
            };

            Log.Message($"[GameRL] Agent registered: {agentId} ({agentType})");
            return true;
        }

        public void DeregisterAgent(string agentId)
        {
            _agents.Remove(agentId);
            Log.Message($"[GameRL] Agent deregistered: {agentId}");
        }

        public void ExecuteAction(string agentId, object action)
        {
            if (action == null)
            {
                Log.Warning("[GameRL] Null action received");
                return;
            }

            // Parse the action - it could be a dictionary or parameterized action
            var actionDict = action as Dictionary<string, object>;
            if (actionDict == null)
            {
                Log.Warning($"[GameRL] Unknown action format: {action.GetType()}");
                return;
            }

            var actionType = actionDict.TryGetValue("type", out var t) ? t?.ToString() : null;
            var actionParams = actionDict.TryGetValue("params", out var p) ? p as Dictionary<string, object> : actionDict;

            switch (actionType)
            {
                case "set_work_priority":
                    ExecuteSetWorkPriority(actionParams!);
                    break;
                case "draft":
                    ExecuteDraft(actionParams!);
                    break;
                case "undraft":
                    ExecuteUndraft(actionParams!);
                    break;
                case "move":
                    ExecuteMove(actionParams!);
                    break;
                case "wait":
                case null:
                    // No-op
                    break;
                default:
                    Log.Warning($"[GameRL] Unknown action type: {actionType}");
                    break;
            }
        }

        private void ExecuteSetWorkPriority(Dictionary<string, object> p)
        {
            var colonistId = GetString(p, "colonist_id");
            var workType = GetString(p, "work_type");
            var priority = GetInt(p, "priority");

            if (colonistId == null || workType == null)
            {
                Log.Warning("[GameRL] set_work_priority missing colonist_id or work_type");
                return;
            }

            var pawn = FindPawn(colonistId);
            if (pawn == null)
            {
                Log.Warning($"[GameRL] Pawn not found: {colonistId}");
                return;
            }

            var workDef = DefDatabase<WorkTypeDef>.GetNamed(workType, errorOnFail: false);
            if (workDef == null)
            {
                Log.Warning($"[GameRL] Work type not found: {workType}");
                return;
            }

            pawn.workSettings?.SetPriority(workDef, priority);
        }

        private void ExecuteDraft(Dictionary<string, object> p)
        {
            var colonistId = GetString(p, "colonist_id");
            if (colonistId == null) return;

            var pawn = FindPawn(colonistId);
            if (pawn?.drafter != null)
            {
                pawn.drafter.Drafted = true;
            }
        }

        private void ExecuteUndraft(Dictionary<string, object> p)
        {
            var colonistId = GetString(p, "colonist_id");
            if (colonistId == null) return;

            var pawn = FindPawn(colonistId);
            if (pawn?.drafter != null)
            {
                pawn.drafter.Drafted = false;
            }
        }

        private void ExecuteMove(Dictionary<string, object> p)
        {
            var colonistId = GetString(p, "colonist_id");
            var x = GetInt(p, "x");
            var z = GetInt(p, "z");

            if (colonistId == null) return;

            var pawn = FindPawn(colonistId);
            if (pawn == null || !pawn.Drafted) return;

            var target = new IntVec3(x, 0, z);
            var job = JobMaker.MakeJob(JobDefOf.Goto, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
        }

        private Pawn? FindPawn(string pawnId)
        {
            return Find.CurrentMap?.mapPawns.FreeColonists
                .FirstOrDefault(p => p.ThingID == pawnId);
        }

        private static string? GetString(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var val) ? val?.ToString() : null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var val)) return 0;
            return val switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s => int.TryParse(s, out var i) ? i : 0,
                _ => 0
            };
        }

        public void Reset(ulong? seed, string? scenario)
        {
            if (seed.HasValue)
            {
                RngManager.PendingSeed = seed;
            }

            _episodeStartTick = Find.TickManager?.TicksGame ?? 0;
            _rewardCalculator.Reset();

            Log.Message($"[GameRL] Reset (seed: {seed}, scenario: {scenario})");

            // Note: Full reset (generating new map) is complex and may require
            // loading a save or triggering new game flow. For now, we just
            // reset the episode tracking.
        }

        public (bool done, bool truncated, string? reason) CheckTermination()
        {
            var map = Find.CurrentMap;
            if (map == null)
                return (true, false, "no_map");

            // Check if all colonists dead
            if (map.mapPawns.FreeColonistsCount == 0)
                return (true, false, "colony_destroyed");

            // Check episode length
            var ticksElapsed = (Find.TickManager?.TicksGame ?? 0) - _episodeStartTick;
            if (ticksElapsed >= MaxEpisodeTicks)
                return (false, true, "timeout");

            return (false, false, null);
        }

        public Dictionary<string, double> ComputeReward(string agentId)
        {
            return _rewardCalculator.Compute();
        }

        public double GetTotalReward(string agentId)
        {
            var components = _rewardCalculator.Compute();
            return components.Values.Sum();
        }

        private class AgentInfo
        {
            public string AgentType { get; set; } = "";
            public Dictionary<string, object> Config { get; set; } = new();
        }
    }
}
