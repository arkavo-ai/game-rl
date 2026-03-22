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
        private Dictionary<string, double>? _cachedReward;

        // Cumulative episode tracking
        private double _cumulativeTotalReward;
        private ulong _episodeStepCount;
        private Dictionary<string, double> _cumulativeRewardBreakdown = new();
        private string? _lastTerminationReason;

        /// <summary>
        /// Last action result for RL feedback
        /// </summary>
        public ActionResult? LastActionResult { get; private set; }

        /// <summary>
        /// Force full state on next observation (set by RequestFullState action)
        /// </summary>
        public bool ForceFullState { get; set; }


        public RimWorldCommandExecutor()
        {
            // Initialize HarmonyRPC with RimWorld logging
            _rpc = new HarmonyRPC(
                log: msg => Log.Message(msg),
                logError: msg => Log.Message(msg)
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

            // Parse RewardShaping from config
            if (config.TryGetValue("RewardShaping", out var shapingObj) && shapingObj is Dictionary<string, object> shapingDict)
            {
                // Components filter: only include these reward components
                if (shapingDict.TryGetValue("Components", out var compObj))
                {
                    if (compObj is System.Collections.IEnumerable compList)
                    {
                        info.RewardComponents = new HashSet<string>();
                        foreach (var c in compList)
                            info.RewardComponents.Add(c?.ToString() ?? "");
                    }
                }

                // Weights: multiply specific reward components
                if (shapingDict.TryGetValue("Weights", out var weightsObj) && weightsObj is Dictionary<string, object> weightsDict)
                {
                    info.RewardWeights = new Dictionary<string, double>();
                    foreach (var kvp in weightsDict)
                    {
                        try { info.RewardWeights[kvp.Key] = Convert.ToDouble(kvp.Value); }
                        catch { }
                    }
                }
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
            _cachedReward = null;

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
                Log.Message($"[GameRL] Unknown action format: {action.GetType()}");
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
                var message = result.ReturnValue is string returnStr && !string.IsNullOrEmpty(returnStr)
                    ? returnStr
                    : "Action executed successfully";
                LastActionResult = ActionResult.Ok(actionType!, message);
            }
            else
            {
                // Determine error code based on error message content
                var msg = result.ErrorMessage ?? "Action failed";
                ActionErrorCode errorCode;
                if (msg.Contains("Unknown action"))
                    errorCode = ActionErrorCode.UnknownAction;
                else if (msg.Contains("not found") || msg.Contains("Failed to resolve"))
                {
                    errorCode = ActionErrorCode.TargetNotFound;
                    // Append valid colonist IDs so the LLM agent can self-correct
                    try
                    {
                        var map = Find.CurrentMap;
                        if (map != null)
                        {
                            var validIds = string.Join(", ",
                                map.mapPawns.FreeColonists
                                    .Where(p => p != null && !p.Destroyed && p.Spawned)
                                    .Select(p => $"'{p.Name?.ToStringShort ?? p.ThingID}'"));
                            if (!string.IsNullOrEmpty(validIds))
                                msg += $" Use a colonist name: [{validIds}]";
                        }
                    }
                    catch { }
                }
                else
                    errorCode = ActionErrorCode.InternalError;
                LastActionResult = ActionResult.Fail(actionType!, errorCode, msg);
                Log.Message($"[GameRL] {msg}");
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
            _cachedReward = null;
            _cumulativeTotalReward = 0;
            _episodeStepCount = 0;
            _cumulativeRewardBreakdown.Clear();
            _lastTerminationReason = null;
            ResetAgentStates();  // Reset first observation flag for all agents

            if (!string.IsNullOrEmpty(scenario))
            {
                // "new" or "new:Scenario:Biome" — generate a fresh colony
                if (scenario.StartsWith("new", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = scenario.Split(':');
                    var scenarioName = parts.Length > 1 ? parts[1] : "Crashlanded";
                    var biomeName = parts.Length > 2 ? parts[2] : "TemperateForest";
                    var storyteller = parts.Length > 3 ? parts[3] : "Cassandra";
                    var difficulty = parts.Length > 4 ? parts[4] : "Strive";
                    var mapSize = 250;
                    if (parts.Length > 5 && int.TryParse(parts[5], out var ms))
                        mapSize = ms;

                    var worldSeed = seed.HasValue ? seed.Value.ToString() : Rand.Int.ToString();
                    Log.Message($"[GameRL] Reset: Generating new colony (scenario={scenarioName}, biome={biomeName}, seed={worldSeed})");
                    GameActions.NewColony(worldSeed, scenarioName, storyteller, difficulty, biomeName, mapSize);
                    return;
                }

                // Otherwise try to load a saved checkpoint
                var saveFiles = GenFilePaths.AllSavedGameFiles.ToList();
                var match = saveFiles.FirstOrDefault(f =>
                    System.IO.Path.GetFileNameWithoutExtension(f.Name) == scenario);

                if (match != null)
                {
                    Log.Message($"[GameRL] Reset: Loading checkpoint '{scenario}'");
                    GameActions.LoadRequested = true;
                    GameDataSaveLoader.LoadGame(match);
                    return;
                }
                else
                {
                    Log.Warning($"[GameRL] Reset: Save '{scenario}' not found, doing soft reset");
                }
            }

            Log.Message($"[GameRL] Reset (seed: {seed}, scenario: {scenario})");
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
            var ticksElapsed = Math.Max(0, (Find.TickManager?.TicksGame ?? 0) - _episodeStartTick);
            if (ticksElapsed >= MaxEpisodeTicks)
                return (false, true, "timeout");

            return (false, false, null);
        }

        public Dictionary<string, double> ComputeReward(string agentId)
        {
            if (_cachedReward == null)
            {
                _rewardCalculator.SetLastActionResult(LastActionResult);
                _cachedReward = _rewardCalculator.Compute();
            }

            // Apply per-agent reward shaping if configured
            if (_agents.TryGetValue(agentId, out var agentInfo)
                && (agentInfo.RewardWeights != null || agentInfo.RewardComponents != null))
            {
                var shaped = new Dictionary<string, double>();
                foreach (var kvp in _cachedReward)
                {
                    // Filter: only include specified components
                    if (agentInfo.RewardComponents != null && !agentInfo.RewardComponents.Contains(kvp.Key))
                        continue;

                    // Weight: multiply by agent-specific weight
                    double value = kvp.Value;
                    if (agentInfo.RewardWeights != null && agentInfo.RewardWeights.TryGetValue(kvp.Key, out var weight))
                        value *= weight;

                    shaped[kvp.Key] = value;
                }
                return shaped;
            }

            return _cachedReward;
        }

        public double GetTotalReward(string agentId)
        {
            var components = ComputeReward(agentId);
            var stepReward = components.Values.Sum();

            // Accumulate into episode totals
            _cumulativeTotalReward += stepReward;
            _episodeStepCount++;
            foreach (var kvp in components)
            {
                if (_cumulativeRewardBreakdown.ContainsKey(kvp.Key))
                    _cumulativeRewardBreakdown[kvp.Key] += kvp.Value;
                else
                    _cumulativeRewardBreakdown[kvp.Key] = kvp.Value;
            }

            // Track termination
            var (done, truncated, reason) = CheckTermination();
            if (done || truncated)
                _lastTerminationReason = reason;

            return stepReward;
        }

        /// <summary>
        /// Get cumulative episode metrics for training assessment
        /// </summary>
        public EpisodeSummaryData GetEpisodeSummary()
        {
            var ticksElapsed = (ulong)Math.Max(0, (Find.TickManager?.TicksGame ?? 0) - _episodeStartTick);
            return new EpisodeSummaryData
            {
                TotalReward = _cumulativeTotalReward,
                StepCount = _episodeStepCount,
                TicksElapsed = ticksElapsed,
                RewardBreakdown = new Dictionary<string, double>(_cumulativeRewardBreakdown),
                TerminationReason = _lastTerminationReason
            };
        }

        public class EpisodeSummaryData
        {
            public double TotalReward { get; set; }
            public ulong StepCount { get; set; }
            public ulong TicksElapsed { get; set; }
            public Dictionary<string, double> RewardBreakdown { get; set; } = new();
            public string? TerminationReason { get; set; }
        }

        private class AgentInfo
        {
            public string AgentType { get; set; } = "";
            public Dictionary<string, object> Config { get; set; } = new();
            public ObservationMode ObservationMode { get; set; } = ObservationMode.Minimal;
            public bool FirstObservation { get; set; } = true;
            public string? LastStateHash { get; set; }
            public DeltaConfig DeltaConfig { get; set; } = DeltaConfig.Minimal;
            public Dictionary<string, double>? RewardWeights { get; set; }
            public HashSet<string>? RewardComponents { get; set; }
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
