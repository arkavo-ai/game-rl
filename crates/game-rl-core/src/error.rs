//! Error types for Game-RL

use thiserror::Error;

/// Result type for Game-RL operations
pub type Result<T> = std::result::Result<T, GameRLError>;

/// Game-RL error types
#[derive(Debug, Error)]
pub enum GameRLError {
    /// Agent not registered
    #[error("Agent not registered: {0}")]
    AgentNotRegistered(String),

    /// Invalid action for current state
    #[error("Invalid action: {0}")]
    InvalidAction(String),

    /// Action not in action space
    #[error("Action not in action space: {0}")]
    ActionSpaceViolation(String),

    /// Episode already terminated
    #[error("Episode terminated, call reset")]
    EpisodeTerminated,

    /// Agent missed step deadline
    #[error("Sync timeout: agent missed step deadline")]
    SyncTimeout,

    /// Too many agents or streams
    #[error("Resource exhausted: {0}")]
    ResourceExhausted(String),

    /// Vision stream error
    #[error("Stream error: {0}")]
    StreamError(String),

    /// IPC communication error
    #[error("IPC error: {0}")]
    IpcError(String),

    /// Serialization error
    #[error("Serialization error: {0}")]
    SerializationError(String),

    /// Game-specific error
    #[error("Game error: {0}")]
    GameError(String),

    /// Protocol error
    #[error("Protocol error: {0}")]
    ProtocolError(String),
}

impl From<serde_json::Error> for GameRLError {
    fn from(err: serde_json::Error) -> Self {
        GameRLError::SerializationError(err.to_string())
    }
}

/// JSON-RPC error codes for Game-RL
pub mod error_codes {
    pub const AGENT_NOT_REGISTERED: i32 = -32000;
    pub const INVALID_ACTION: i32 = -32001;
    pub const EPISODE_TERMINATED: i32 = -32002;
    pub const SYNC_TIMEOUT: i32 = -32003;
    pub const RESOURCE_EXHAUSTED: i32 = -32004;
}
