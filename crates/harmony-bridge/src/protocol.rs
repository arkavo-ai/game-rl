//! Wire protocol for Rust <-> C# communication
//!
//! Messages are serialized as JSON with internally-tagged enums.
//! Format: {"type": "message_type", ...fields}

use game_rl_core::{Action, AgentConfig, AgentId, AgentType, GameEvent, Observation};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Messages sent between Rust bridge and C# game
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum GameMessage {
    // === C# -> Rust ===
    /// Game is ready and provides manifest
    Ready {
        name: String,
        version: String,
        capabilities: GameCapabilities,
    },

    /// Current game state update
    StateUpdate {
        tick: u64,
        state: serde_json::Value,
        events: Vec<GameEvent>,
    },

    /// Agent registration response
    AgentRegistered {
        agent_id: AgentId,
        observation_space: serde_json::Value,
        action_space: serde_json::Value,
    },

    /// Step result
    StepResult {
        agent_id: AgentId,
        observation: Observation,
        reward: f64,
        reward_components: HashMap<String, f64>,
        done: bool,
        truncated: bool,
        state_hash: Option<String>,
    },

    /// Reset complete
    ResetComplete {
        observation: Observation,
        state_hash: Option<String>,
    },

    /// State hash response
    StateHash { hash: String },

    /// Error response
    Error { code: i32, message: String },

    // === Rust -> C# ===
    /// Register an agent
    RegisterAgent {
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    },

    /// Deregister an agent
    DeregisterAgent { agent_id: AgentId },

    /// Execute an action
    ExecuteAction {
        agent_id: AgentId,
        action: Action,
        ticks: u32,
    },

    /// Reset environment
    Reset {
        seed: Option<u64>,
        scenario: Option<String>,
    },

    /// Request state hash
    GetStateHash,

    /// Shutdown the game
    Shutdown,
}

/// Game capabilities sent during Ready
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameCapabilities {
    pub multi_agent: bool,
    pub max_agents: usize,
    pub deterministic: bool,
    pub headless: bool,
}

/// Serialize a message to JSON bytes
pub fn serialize(msg: &GameMessage) -> Result<Vec<u8>, serde_json::Error> {
    serde_json::to_vec(msg)
}

/// Deserialize a message from JSON bytes
pub fn deserialize(bytes: &[u8]) -> Result<GameMessage, serde_json::Error> {
    serde_json::from_slice(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_roundtrip() {
        let msg = GameMessage::Ready {
            name: "TestGame".into(),
            version: "1.0.0".into(),
            capabilities: GameCapabilities {
                multi_agent: true,
                max_agents: 4,
                deterministic: true,
                headless: true,
            },
        };

        let bytes = serialize(&msg).unwrap();
        let decoded: GameMessage = deserialize(&bytes).unwrap();

        match decoded {
            GameMessage::Ready { name, .. } => assert_eq!(name, "TestGame"),
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_get_state_hash_format() {
        let msg = GameMessage::GetStateHash;
        let bytes = serialize(&msg).unwrap();
        let json = String::from_utf8_lossy(&bytes);

        // Should be JSON with type field
        println!("GetStateHash json: {}", json);
        assert!(json.contains("\"type\":\"get_state_hash\""), "Should contain type field");

        // Verify it can roundtrip
        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::GetStateHash => {},
            _ => panic!("Wrong message type"),
        }
    }
}
