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
using RimWorld.GameRL.State;

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

        /// <summary>
        /// Last action result for RL feedback
        /// </summary>
        public ActionResult? LastActionResult { get; private set; }

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
            {
                Log.Message($"[GameRL] Agent already registered: {agentId} ({agentType})");
                return true;
            }

            var info = new AgentInfo
            {
                AgentType = agentType,
                Config = config
            };

            // Parse ObservationMode from config
            if (config.TryGetValue("ObservationMode", out var modeObj) && modeObj is string modeStr)
            {
                info.ObservationMode = modeStr.ToLowerInvariant() switch
                {
                    "minimal" => ObservationMode.Minimal,
                    "normal" => ObservationMode.Normal,
                    "full" => ObservationMode.Full,
                    _ => ObservationMode.Minimal
                };
            }

            // Parse DeltaConfig from config
            if (config.TryGetValue("DeltaConfig", out var deltaObj) && deltaObj is Dictionary<string, object> deltaDict)
            {
                var deltaConfig = info.ObservationMode == ObservationMode.Minimal
                    ? DeltaConfig.Minimal
                    : DeltaConfig.Normal;

                if (deltaDict.TryGetValue("MoodThreshold", out var mood))
                    deltaConfig.MoodThreshold = Convert.ToSingle(mood);
                if (deltaDict.TryGetValue("HealthThreshold", out var health))
                    deltaConfig.HealthThreshold = Convert.ToSingle(health);
                if (deltaDict.TryGetValue("HungerThreshold", out var hunger))
                    deltaConfig.HungerThreshold = Convert.ToSingle(hunger);
                if (deltaDict.TryGetValue("RestThreshold", out var rest))
                    deltaConfig.RestThreshold = Convert.ToSingle(rest);
                if (deltaDict.TryGetValue("PositionThreshold", out var pos))
                    deltaConfig.PositionThreshold = Convert.ToInt32(pos);
                if (deltaDict.TryGetValue("PositionOnlyOnJobChange", out var posJob))
                    deltaConfig.PositionOnlyOnJobChange = Convert.ToBoolean(posJob);
                if (deltaDict.TryGetValue("ResourcePercentThreshold", out var res))
                    deltaConfig.ResourcePercentThreshold = Convert.ToSingle(res);

                info.DeltaConfig = deltaConfig;
            }
            else
            {
                // Set default based on observation mode
                info.DeltaConfig = info.ObservationMode == ObservationMode.Minimal
                    ? DeltaConfig.Minimal
                    : DeltaConfig.Normal;
            }

            _agents[agentId] = info;

            Log.Message($"[GameRL] Agent registered: {agentId} ({agentType}, mode={info.ObservationMode})");
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
                LastActionResult = ActionResult.NoOp();
                return;
            }

            // Parse the action dictionary
            var actionDict = action as Dictionary<string, object>;
            if (actionDict == null)
            {
                LastActionResult = ActionResult.Fail("Unknown", ActionErrorCode.InternalError,
                    $"Unknown action format: {action.GetType()}");
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
                LastActionResult = ActionResult.NoOp();
                return;
            }

            // Dispatch via HarmonyRPC - automatic method resolution and parameter binding
            var result = _rpc.Dispatch(actionType!, actionParams);
            if (result.Success)
            {
                LastActionResult = ActionResult.Ok(actionType!, "Action executed successfully");
            }
            else
            {
                // Determine error code based on message
                var errorCode = result.ErrorMessage?.Contains("Unknown action") == true
                    ? ActionErrorCode.UnknownAction
                    : ActionErrorCode.InternalError;
                LastActionResult = ActionResult.Fail(actionType!, errorCode, result.ErrorMessage ?? "Action failed");
                Log.Warning($"[GameRL] {result.ErrorMessage}");
            }
        }

        public void Reset(ulong? seed, string? scenario)
        {
            if (seed.HasValue)
            {
                RngManager.PendingSeed = seed;
            }

            _episodeStartTick = Find.TickManager?.TicksGame ?? 0;
            _rewardCalculator.Reset();
            ResetAgentStates();  // Reset first observation flag for all agents

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
            public ObservationMode ObservationMode { get; set; } = ObservationMode.Minimal;
            public bool FirstObservation { get; set; } = true;
            public string? LastStateHash { get; set; }
            public DeltaConfig DeltaConfig { get; set; } = DeltaConfig.Minimal;
        }

        /// <summary>
        /// Gets observation mode for an agent
        /// </summary>
        public ObservationMode GetObservationMode(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.ObservationMode : ObservationMode.Minimal;
        }

        /// <summary>
        /// Checks if this is the agent's first observation (requires full state)
        /// </summary>
        public bool IsFirstObservation(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.FirstObservation : true;
        }

        /// <summary>
        /// Marks first observation as sent for an agent
        /// </summary>
        public void MarkFirstObservationSent(string agentId)
        {
            if (_agents.TryGetValue(agentId, out var info))
            {
                info.FirstObservation = false;
            }
        }

        /// <summary>
        /// Gets the last state hash for an agent
        /// </summary>
        public string? GetLastStateHash(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.LastStateHash : null;
        }

        /// <summary>
        /// Updates the last state hash for an agent
        /// </summary>
        public void SetLastStateHash(string agentId, string hash)
        {
            if (_agents.TryGetValue(agentId, out var info))
            {
                info.LastStateHash = hash;
            }
        }

        /// <summary>
        /// Gets delta config for an agent
        /// </summary>
        public DeltaConfig GetDeltaConfig(string agentId)
        {
            return _agents.TryGetValue(agentId, out var info) ? info.DeltaConfig : DeltaConfig.Minimal;
        }

        /// <summary>
        /// Resets first observation flag for all agents (called on Reset)
        /// </summary>
        public void ResetAgentStates()
        {
            foreach (var agent in _agents.Values)
            {
                agent.FirstObservation = true;
                agent.LastStateHash = null;
            }
        }
    }
}
