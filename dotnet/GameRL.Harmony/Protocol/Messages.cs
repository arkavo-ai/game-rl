// Wire protocol messages matching harmony-bridge/src/protocol.rs
// Uses MessagePack with serde-compatible tagging

using System;
using System.Collections.Generic;
using MessagePack;

namespace GameRL.Harmony.Protocol
{
    /// <summary>
    /// Base class for all protocol messages.
    /// Rust uses #[serde(tag = "type", rename_all = "snake_case")]
    /// </summary>
    [MessagePackObject]
    public abstract class GameMessage
    {
        [Key("type")]
        public abstract string Type { get; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // C# → Rust Messages (responses from game)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Game is ready and provides capabilities
    /// </summary>
    [MessagePackObject]
    public class ReadyMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "ready";

        [Key("name")]
        public string Name { get; set; } = "";

        [Key("version")]
        public string Version { get; set; } = "";

        [Key("capabilities")]
        public GameCapabilities Capabilities { get; set; } = new();
    }

    /// <summary>
    /// Game capabilities sent during Ready
    /// </summary>
    [MessagePackObject]
    public class GameCapabilities
    {
        [Key("multi_agent")]
        public bool MultiAgent { get; set; }

        [Key("max_agents")]
        public int MaxAgents { get; set; }

        [Key("deterministic")]
        public bool Deterministic { get; set; }

        [Key("headless")]
        public bool Headless { get; set; }
    }

    /// <summary>
    /// Current game state update (async notification)
    /// </summary>
    [MessagePackObject]
    public class StateUpdateMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "state_update";

        [Key("tick")]
        public ulong Tick { get; set; }

        [Key("state")]
        public object? State { get; set; }

        [Key("events")]
        public List<GameEvent> Events { get; set; } = new();
    }

    /// <summary>
    /// Agent registration response
    /// </summary>
    [MessagePackObject]
    public class AgentRegisteredMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "agent_registered";

        [Key("agent_id")]
        public string AgentId { get; set; } = "";

        [Key("observation_space")]
        public object? ObservationSpace { get; set; }

        [Key("action_space")]
        public object? ActionSpace { get; set; }
    }

    /// <summary>
    /// Step result with observation and reward
    /// </summary>
    [MessagePackObject]
    public class StepResultMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "step_result";

        [Key("agent_id")]
        public string AgentId { get; set; } = "";

        [Key("observation")]
        public object? Observation { get; set; }

        [Key("reward")]
        public double Reward { get; set; }

        [Key("reward_components")]
        public Dictionary<string, double> RewardComponents { get; set; } = new();

        [Key("done")]
        public bool Done { get; set; }

        [Key("truncated")]
        public bool Truncated { get; set; }

        [Key("state_hash")]
        public string? StateHash { get; set; }
    }

    /// <summary>
    /// Reset complete with initial observation
    /// </summary>
    [MessagePackObject]
    public class ResetCompleteMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "reset_complete";

        [Key("observation")]
        public object? Observation { get; set; }

        [Key("state_hash")]
        public string? StateHash { get; set; }
    }

    /// <summary>
    /// State hash response
    /// </summary>
    [MessagePackObject]
    public class StateHashMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "state_hash";

        [Key("hash")]
        public string Hash { get; set; } = "";
    }

    /// <summary>
    /// Error response
    /// </summary>
    [MessagePackObject]
    public class ErrorMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "error";

        [Key("code")]
        public int Code { get; set; }

        [Key("message")]
        public string Message { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Rust → C# Messages (commands from server)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Register an agent
    /// </summary>
    [MessagePackObject]
    public class RegisterAgentMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "register_agent";

        [Key("agent_id")]
        public string AgentId { get; set; } = "";

        [Key("agent_type")]
        public string AgentType { get; set; } = "";

        [Key("config")]
        public AgentConfig Config { get; set; } = new();
    }

    /// <summary>
    /// Agent configuration
    /// </summary>
    [MessagePackObject]
    public class AgentConfig
    {
        [Key("entity_id")]
        public string? EntityId { get; set; }

        [Key("observation_profile")]
        public string ObservationProfile { get; set; } = "default";

        [Key("action_mask")]
        public List<string> ActionMask { get; set; } = new();

        [Key("reward_shaping")]
        public RewardShaping? RewardShaping { get; set; }

        [Key("extra")]
        public Dictionary<string, object> Extra { get; set; } = new();
    }

    /// <summary>
    /// Reward shaping configuration
    /// </summary>
    [MessagePackObject]
    public class RewardShaping
    {
        [Key("components")]
        public List<string> Components { get; set; } = new();

        [Key("weights")]
        public Dictionary<string, float> Weights { get; set; } = new();
    }

    /// <summary>
    /// Deregister an agent
    /// </summary>
    [MessagePackObject]
    public class DeregisterAgentMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "deregister_agent";

        [Key("agent_id")]
        public string AgentId { get; set; } = "";
    }

    /// <summary>
    /// Execute an action
    /// </summary>
    [MessagePackObject]
    public class ExecuteActionMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "execute_action";

        [Key("agent_id")]
        public string AgentId { get; set; } = "";

        [Key("action")]
        public object? Action { get; set; }

        [Key("ticks")]
        public uint Ticks { get; set; }
    }

    /// <summary>
    /// Reset environment
    /// </summary>
    [MessagePackObject]
    public class ResetMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "reset";

        [Key("seed")]
        public ulong? Seed { get; set; }

        [Key("scenario")]
        public string? Scenario { get; set; }
    }

    /// <summary>
    /// Request state hash
    /// </summary>
    [MessagePackObject]
    public class GetStateHashMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "get_state_hash";
    }

    /// <summary>
    /// Shutdown the game
    /// </summary>
    [MessagePackObject]
    public class ShutdownMessage : GameMessage
    {
        [IgnoreMember]
        public override string Type => "shutdown";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared Types
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Game event that occurred during a step
    /// </summary>
    [MessagePackObject]
    public class GameEvent
    {
        [Key("type")]
        public string EventType { get; set; } = "";

        [Key("tick")]
        public ulong Tick { get; set; }

        [Key("severity")]
        public byte Severity { get; set; }

        [Key("details")]
        public object? Details { get; set; }
    }

    /// <summary>
    /// Parameterized action format
    /// </summary>
    [MessagePackObject]
    public class ParameterizedAction
    {
        [Key("type")]
        public string ActionType { get; set; } = "";

        [Key("params")]
        public Dictionary<string, object> Params { get; set; } = new();
    }
}
