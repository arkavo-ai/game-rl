// Wire protocol messages matching harmony-bridge/src/protocol.rs
// Uses JSON with serde-compatible tagging

using System;
using System.Collections.Generic;

namespace GameRL.Harmony.Protocol
{
    /// <summary>
    /// Base class for all protocol messages.
    /// Rust uses #[serde(tag = "type", rename_all = "snake_case")]
    /// </summary>
    public abstract class GameMessage
    {
        public abstract string Type { get; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // C# → Rust Messages (responses from game)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Game is ready and provides capabilities
    /// </summary>
    public class ReadyMessage : GameMessage
    {
        public override string Type => "ready";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public GameCapabilities Capabilities { get; set; } = new();
    }

    /// <summary>
    /// Game capabilities sent during Ready
    /// </summary>
    public class GameCapabilities
    {
        public bool MultiAgent { get; set; }
        public int MaxAgents { get; set; }
        public bool Deterministic { get; set; }
        public bool Headless { get; set; }
    }

    /// <summary>
    /// Current game state update (async notification)
    /// </summary>
    public class StateUpdateMessage : GameMessage
    {
        public override string Type => "state_update";
        public ulong Tick { get; set; }
        public object? State { get; set; }
        public List<GameEvent> Events { get; set; } = new();
    }

    /// <summary>
    /// Agent registration response
    /// </summary>
    public class AgentRegisteredMessage : GameMessage
    {
        public override string Type => "agent_registered";
        public string AgentId { get; set; } = "";
        public object? ObservationSpace { get; set; }
        public object? ActionSpace { get; set; }
    }

    /// <summary>
    /// Step result with observation and reward
    /// </summary>
    public class StepResultMessage : GameMessage
    {
        public override string Type => "step_result";
        public string AgentId { get; set; } = "";
        public object? Observation { get; set; }
        public double Reward { get; set; }
        public Dictionary<string, double> RewardComponents { get; set; } = new();
        public bool Done { get; set; }
        public bool Truncated { get; set; }
        public string? StateHash { get; set; }
    }

    /// <summary>
    /// Batch step result for multiple agents
    /// </summary>
    public class BatchStepResultMessage : GameMessage
    {
        public override string Type => "batch_step_result";
        public List<StepResultMessage> Results { get; set; } = new();
    }

    /// <summary>
    /// Reset complete with initial observation
    /// </summary>
    public class ResetCompleteMessage : GameMessage
    {
        public override string Type => "reset_complete";
        public object? Observation { get; set; }
        public string? StateHash { get; set; }
    }

    /// <summary>
    /// State hash response
    /// </summary>
    public class StateHashMessage : GameMessage
    {
        public override string Type => "state_hash";
        public string Hash { get; set; } = "";
    }

    /// <summary>
    /// Error response
    /// </summary>
    public class ErrorMessage : GameMessage
    {
        public override string Type => "error";
        public int Code { get; set; }
        public string Message { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Rust → C# Messages (commands from server)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Register an agent
    /// </summary>
    public class RegisterAgentMessage : GameMessage
    {
        public override string Type => "register_agent";
        public string AgentId { get; set; } = "";
        public string AgentType { get; set; } = "";
        public AgentConfig Config { get; set; } = new();
    }

    /// <summary>
    /// Agent configuration
    /// </summary>
    public class AgentConfig
    {
        public string? EntityId { get; set; }
        public string ObservationProfile { get; set; } = "default";
        public List<string> ActionMask { get; set; } = new();
        public RewardShaping? RewardShaping { get; set; }
        public Dictionary<string, object> Extra { get; set; } = new();
    }

    /// <summary>
    /// Reward shaping configuration
    /// </summary>
    public class RewardShaping
    {
        public List<string> Components { get; set; } = new();
        public Dictionary<string, float> Weights { get; set; } = new();
    }

    /// <summary>
    /// Deregister an agent
    /// </summary>
    public class DeregisterAgentMessage : GameMessage
    {
        public override string Type => "deregister_agent";
        public string AgentId { get; set; } = "";
    }

    /// <summary>
    /// Execute an action
    /// </summary>
    public class ExecuteActionMessage : GameMessage
    {
        public override string Type => "execute_action";
        public string AgentId { get; set; } = "";
        public object? Action { get; set; }
        public uint Ticks { get; set; }
    }

    /// <summary>
    /// Configure vision streams
    /// </summary>
    public class ConfigureStreamsMessage : GameMessage
    {
        public override string Type => "configure_streams";
        public string AgentId { get; set; } = "";
        public string Profile { get; set; } = "default";
    }

    /// <summary>
    /// Reset environment
    /// </summary>
    public class ResetMessage : GameMessage
    {
        public override string Type => "reset";
        public ulong? Seed { get; set; }
        public string? Scenario { get; set; }
    }

    /// <summary>
    /// Request state hash
    /// </summary>
    public class GetStateHashMessage : GameMessage
    {
        public override string Type => "get_state_hash";
    }

    /// <summary>
    /// Shutdown the game
    /// </summary>
    public class ShutdownMessage : GameMessage
    {
        public override string Type => "shutdown";
    }

    /// <summary>
    /// Vision stream configuration response
    /// </summary>
    public class StreamsConfiguredMessage : GameMessage
    {
        public override string Type => "streams_configured";
        public string AgentId { get; set; } = "";
        public List<Dictionary<string, object>> Descriptors { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared Types
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Game event that occurred during a step
    /// </summary>
    public class GameEvent
    {
        public string EventType { get; set; } = "";
        public ulong Tick { get; set; }
        public byte Severity { get; set; }
        public object? Details { get; set; }
    }

    /// <summary>
    /// Parameterized action format
    /// </summary>
    public class ParameterizedAction
    {
        public string ActionType { get; set; } = "";
        public Dictionary<string, object> Params { get; set; } = new();
    }
}
