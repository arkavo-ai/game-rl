// RimWorld GameRL Mod Entry Point

using System;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.Protocol;
using RimWorld.GameRL.State;
using RimWorld.GameRL.Actions;

namespace RimWorld.GameRL
{
    /// <summary>
    /// Main mod class - initializes on game startup
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GameRLMod
    {
        private static Bridge? _bridge;
        private static RimWorldStateExtractor? _stateExtractor;
        private static RimWorldCommandExecutor? _commandExecutor;
        private static readonly Dictionary<string, AgentState> _agents = new();
        private static readonly Dictionary<string, VisionStreamManager> _visionStreams = new();

        // Step tracking
        private static ulong _currentStepId;
        private static string? _currentAgentId;
        private static uint _ticksRemaining;
        private static bool _stepInProgress;
        private static bool _forcingTicks;

        static GameRLMod()
        {
            Log.Message("[GameRL] Initializing RimWorld adapter...");

            try
            {
                // Apply Harmony patches
                var harmony = new HarmonyLib.Harmony("arkavo.gamerl.rimworld");
                harmony.PatchAll();
                Log.Message("[GameRL] Harmony patches applied");

                // Initialize components
                _stateExtractor = new RimWorldStateExtractor();
                _commandExecutor = new RimWorldCommandExecutor();

                // Start listening for harmony-server connection
                var socketPath = GetSocketPath();
                Log.Message($"[GameRL] Starting IPC server on {socketPath}...");

                _bridge = new RimWorldBridge();

                // Wire up event handlers
                _bridge.OnRegisterAgent += HandleRegisterAgent;
                _bridge.OnDeregisterAgent += HandleDeregisterAgent;
                _bridge.OnExecuteAction += HandleExecuteAction;
                _bridge.OnConfigureStreams += HandleConfigureStreams;
                _bridge.OnReset += HandleReset;
                _bridge.OnGetStateHash += HandleGetStateHash;
                _bridge.OnShutdown += HandleShutdown;
                _bridge.OnClientConnected += HandleClientConnected;

                if (_bridge.Listen(socketPath))
                {
                    Log.Message("[GameRL] IPC server started, waiting for harmony-bridge connection...");
                }
                else
                {
                    Log.Warning("[GameRL] Could not start IPC server. Running in standalone mode.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Initialization failed: {ex}");
            }
        }

        private static string GetSocketPath()
        {
            // Check environment variable first
            var envPath = Environment.GetEnvironmentVariable("GAMERL_SOCKET");
            if (!string.IsNullOrEmpty(envPath))
                return envPath;

            // Default path - use /tmp for consistency across platforms
            return "/tmp/gamerl-rimworld.sock";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Message Handlers
        // ═══════════════════════════════════════════════════════════════════════════

        private static void HandleClientConnected()
        {
            Log.Message("[GameRL] Rust harmony-bridge connected, sending Ready...");

            // Send Ready message when client connects
            _bridge?.SendReady(
                name: "RimWorld",
                version: VersionControl.CurrentVersionString,
                capabilities: new GameCapabilities
                {
                    MultiAgent = true,
                    MaxAgents = 8,
                    Deterministic = true,
                    Headless = false
                });

            Log.Message("[GameRL] Ready message sent");
        }

        private static void HandleRegisterAgent(RegisterAgentMessage msg)
        {
            Log.Message($"[GameRL] Registering agent: {msg.AgentId} ({msg.AgentType})");

            try
            {
                var config = new Dictionary<string, object>();
                if (msg.Config.EntityId != null)
                    config["entity_id"] = msg.Config.EntityId;
                config["observation_profile"] = msg.Config.ObservationProfile;

                if (_commandExecutor!.RegisterAgent(msg.AgentId, msg.AgentType, config))
                {
                    _agents[msg.AgentId] = new AgentState { AgentType = msg.AgentType };

                    _bridge!.SendAgentRegistered(
                        msg.AgentId,
                        _stateExtractor!.GetObservationSpace(msg.AgentType),
                        _stateExtractor!.GetActionSpace(msg.AgentType));
                }
                else
                {
                    _bridge!.SendError(-32000, $"Failed to register agent: {msg.AgentId}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Register error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleDeregisterAgent(DeregisterAgentMessage msg)
        {
            Log.Message($"[GameRL] Deregistering agent: {msg.AgentId}");
            _commandExecutor?.DeregisterAgent(msg.AgentId);
            _agents.Remove(msg.AgentId);
        }

        private static void HandleExecuteAction(ExecuteActionMessage msg)
        {
            if (_stepInProgress)
            {
                Log.Warning("[GameRL] Step already in progress, ignoring action");
                return;
            }

            _currentStepId++;
            _currentAgentId = msg.AgentId;
            _ticksRemaining = msg.Ticks > 0 ? msg.Ticks : 1;
            _stepInProgress = true;

            try
            {
                _commandExecutor?.ExecuteAction(msg.AgentId, msg.Action!);

                // Force ticks to advance immediately
                ForceTicks(_ticksRemaining);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Action error: {ex}");
                _stepInProgress = false;
                _bridge?.SendError(-32001, ex.Message);
            }
        }

        private static void ForceTicks(uint count)
        {
            if (_forcingTicks) return;  // Prevent recursion

            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                Log.Warning("[GameRL] No TickManager, cannot force ticks");
                CompleteStep();
                return;
            }

            _forcingTicks = true;
            try
            {
                // Force the game to advance by calling DoSingleTick directly
                // This will trigger OnTick via the Harmony patch, which decrements _ticksRemaining
                for (uint i = 0; i < count && _stepInProgress; i++)
                {
                    tickManager.DoSingleTick();
                }
            }
            finally
            {
                _forcingTicks = false;
            }
        }

        private static void HandleReset(ResetMessage msg)
        {
            Log.Message($"[GameRL] Reset requested (seed: {msg.Seed}, scenario: {msg.Scenario})");

            try
            {
                _commandExecutor?.Reset(msg.Seed, msg.Scenario);

                object observation;
                if (_agents.Count == 0)
                {
                    observation = _stateExtractor!.ExtractObservation("default");
                }
                else if (_agents.Count == 1)
                {
                    var enumerator = _agents.Keys.GetEnumerator();
                    enumerator.MoveNext();
                    observation = _stateExtractor!.ExtractObservation(enumerator.Current);
                }
                else
                {
                    var observations = new Dictionary<string, object>();
                    foreach (var agentId in _agents.Keys)
                    {
                        observations[agentId] = _stateExtractor!.ExtractObservation(agentId);
                    }
                    observation = observations;
                }
                var stateHash = _stateExtractor!.ComputeStateHash();
                _bridge?.SendResetComplete(observation, stateHash);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Reset error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleGetStateHash(GetStateHashMessage msg)
        {
            var hash = _stateExtractor?.ComputeStateHash() ?? "sha256:0000000000000000000000000000000000000000000000000000000000000000";
            _bridge?.SendStateHash(hash);
        }

        private static void HandleConfigureStreams(ConfigureStreamsMessage msg)
        {
            Log.Message($"[GameRL] Configure streams for agent: {msg.AgentId} ({msg.Profile})");

            try
            {
                var profile = string.IsNullOrEmpty(msg.Profile) ? "default" : msg.Profile;
                var stream = GetOrCreateVisionStream(profile);
                var descriptors = new List<Dictionary<string, object>>
                {
                    stream.BuildDescriptor()
                };

                _bridge?.SendStreamsConfigured(msg.AgentId, descriptors);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Configure streams error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        private static void HandleShutdown()
        {
            Log.Message("[GameRL] Shutdown requested");
            foreach (var stream in _visionStreams.Values)
            {
                stream.Dispose();
            }
            _visionStreams.Clear();
            _bridge?.Dispose();
            _bridge = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Game Loop Integration
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by TickPatch after each game tick
        /// </summary>
        internal static void OnTick()
        {
            // Process any pending commands from server
            _bridge?.ProcessCommands();

            // If we're in a step, decrement counter
            if (_stepInProgress && _ticksRemaining > 0)
            {
                _ticksRemaining--;

                if (_ticksRemaining == 0)
                {
                    CompleteStep();
                }
            }
        }

        private static void CompleteStep()
        {
            _stepInProgress = false;

            if (_currentAgentId == null || _stateExtractor == null || _commandExecutor == null)
                return;

            try
            {
                var (done, truncated, reason) = _commandExecutor.CheckTermination();
                foreach (var stream in _visionStreams.Values)
                {
                    stream.Capture();
                }
                var stateHash = _stateExtractor!.ComputeStateHash();

                if (_agents.Count <= 1)
                {
                    var observation = _stateExtractor!.ExtractObservation(_currentAgentId);
                    var rewardComponents = _commandExecutor.ComputeReward(_currentAgentId);
                    var totalReward = _commandExecutor.GetTotalReward(_currentAgentId);

                    _bridge?.SendStepResult(
                        _currentAgentId,
                        observation,
                        totalReward,
                        rewardComponents,
                        done,
                        truncated,
                        stateHash);
                    return;
                }

                var results = new List<StepResultMessage>();
                foreach (var agentId in _agents.Keys)
                {
                    var observation = _stateExtractor!.ExtractObservation(agentId);
                    var rewardComponents = _commandExecutor.ComputeReward(agentId);
                    var totalReward = _commandExecutor.GetTotalReward(agentId);

                    results.Add(new StepResultMessage
                    {
                        AgentId = agentId,
                        Observation = observation,
                        Reward = totalReward,
                        RewardComponents = rewardComponents,
                        Done = done,
                        Truncated = truncated,
                        StateHash = stateHash
                    });
                }

                _bridge?.SendBatchStepResult(results);
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Step complete error: {ex}");
                _bridge?.SendError(-32603, ex.Message);
            }
        }

        /// <summary>
        /// Track agent state
        /// </summary>
        private class AgentState
        {
            public string AgentType { get; set; } = "";
        }

        private static VisionStreamManager GetOrCreateVisionStream(string profile)
        {
            if (_visionStreams.TryGetValue(profile, out var stream))
            {
                return stream;
            }

            var (width, height) = ParseVisionProfile(profile);
            var streamId = $"camera_{profile}";
            var shmPath = Path.Combine(Path.GetTempPath(), $"gamerl-rimworld-{streamId}.shm");
            stream = new VisionStreamManager(streamId, width, height, shmPath);
            _visionStreams[profile] = stream;
            return stream;
        }

        /// <summary>
        /// Parse vision profile string (e.g., "256x256", "512x512").
        /// Falls back to 256x256 for "default" or invalid profiles.
        /// </summary>
        private static (int width, int height) ParseVisionProfile(string profile)
        {
            var parts = profile.Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var width)
                && int.TryParse(parts[1], out var height)
                && width > 0
                && height > 0)
            {
                return (width, height);
            }

            // Default resolution for named profiles like "default"
            return (256, 256);
        }
    }

    /// <summary>
    /// RimWorld-specific bridge with proper logging
    /// </summary>
    internal class RimWorldBridge : Bridge
    {
        protected override void Log(string message)
        {
            Verse.Log.Message($"[GameRL] {message}");
        }

        protected override void LogError(string message)
        {
            Verse.Log.Error($"[GameRL] {message}");
        }
    }
}
