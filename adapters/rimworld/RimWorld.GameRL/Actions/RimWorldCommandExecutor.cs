// Command executor for RimWorld actions

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.RPC;
using RimWorld.GameRL.Patches;
using RimWorld.GameRL.Rewards;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Executes commands in RimWorld using HarmonyRPC
    /// </summary>
    public class RimWorldCommandExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, AgentInfo> _agents = new();
        private readonly SurvivalReward _rewardCalculator = new();
        private readonly HarmonyRPC _rpc;
        private int _episodeStartTick;
        private const int MaxEpisodeTicks = 60000 * 15;  // 15 in-game days

        public RimWorldCommandExecutor()
        {
            // Initialize HarmonyRPC with RimWorld logging
            _rpc = new HarmonyRPC(
                log: msg => Log.Message(msg),
                logError: msg => Log.Error(msg)
            );

            // Register type resolvers for automatic ID -> object conversion
            _rpc.RegisterResolver(new PawnResolver());
            _rpc.RegisterResolver(new ThingResolver());
            _rpc.RegisterResolver(new BuildingResolver());

            // Scan for [GameRLAction] methods in this assembly
            _rpc.RegisterAll(Assembly.GetExecutingAssembly());
        }

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

            // Parse the action dictionary
            var actionDict = action as Dictionary<string, object>;
            if (actionDict == null)
            {
                Log.Warning($"[GameRL] Unknown action format: {action.GetType()}");
                return;
            }

            // Extract action type and parameters (Type is PascalCase from Rust)
            var actionType = actionDict.TryGetValue("Type", out var t) ? t?.ToString() : null;
            // Params are flattened into the action dict (not nested)
            var actionParams = actionDict;

            if (string.IsNullOrEmpty(actionType))
            {
                // No action type = no-op (wait)
                return;
            }

            // Dispatch via HarmonyRPC - automatic method resolution and parameter binding
            _rpc.Dispatch(actionType!, actionParams);
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
