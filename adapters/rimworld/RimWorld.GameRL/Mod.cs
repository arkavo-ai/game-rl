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

        // Step tracking
        private static ulong _currentStepId;
        private static string? _currentAgentId;
        private static uint _ticksRemaining;
        private static bool _stepInProgress;

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
                        _stateExtractor.GetActionSpace(msg.AgentType));
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
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Action error: {ex}");
                _stepInProgress = false;
                _bridge?.SendError(-32001, ex.Message);
            }
        }

        private static void HandleReset(ResetMessage msg)
        {
            Log.Message($"[GameRL] Reset requested (seed: {msg.Seed}, scenario: {msg.Scenario})");

            try
            {
                _commandExecutor?.Reset(msg.Seed, msg.Scenario);

                var observation = _stateExtractor!.ExtractObservation("default");
                var stateHash = _stateExtractor.ComputeStateHash();
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
            var hash = _stateExtractor?.ComputeStateHash() ?? "unknown";
            _bridge?.SendStateUpdate(
                _stateExtractor?.CurrentTick ?? 0,
                new { state_hash = hash });
        }

        private static void HandleShutdown()
        {
            Log.Message("[GameRL] Shutdown requested");
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
                var observation = _stateExtractor.ExtractObservation(_currentAgentId);
                var rewardComponents = _commandExecutor.ComputeReward(_currentAgentId);
                var totalReward = _commandExecutor.GetTotalReward(_currentAgentId);
                var stateHash = _stateExtractor.ComputeStateHash();

                _bridge?.SendStepResult(
                    _currentAgentId,
                    observation,
                    totalReward,
                    rewardComponents,
                    done,
                    truncated,
                    stateHash);
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
