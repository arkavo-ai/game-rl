//! Wire protocol for Rust <-> game communication
//!
//! Messages are serialized as JSON with internally-tagged enums.
//! Format: {"Type": "MessageType", ...fields}
//!
//! Uses PascalCase throughout for LLM-friendly natural language readability.

use game_rl_core::{
    Action, AgentConfig, AgentId, AgentType, GameEvent, Observation, StreamDescriptor,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Step result payload for single-agent or batch responses
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StepResultPayload {
    pub agent_id: AgentId,
    pub observation: Observation,
    pub reward: f64,
    #[serde(default)]
    pub reward_components: HashMap<String, f64>,
    pub done: bool,
    pub truncated: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state_hash: Option<String>,
}

/// Messages sent between Rust bridge and game process
///
/// Note: `rename_all` on enums only affects variant names, not field names inside variants.
/// Each field must be explicitly renamed using `#[serde(rename = "...")]` for PascalCase.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
pub enum GameMessage {
    // === Game -> Rust ===
    /// Game is ready and provides manifest
    Ready {
        #[serde(rename = "Name")]
        name: String,
        #[serde(rename = "Version")]
        version: String,
        #[serde(rename = "Capabilities")]
        capabilities: GameCapabilities,
    },

    /// Current game state update
    StateUpdate {
        #[serde(rename = "Tick")]
        tick: u64,
        #[serde(rename = "State")]
        state: serde_json::Value,
        #[serde(rename = "Events")]
        events: Vec<GameEvent>,
    },

    /// Agent registration response
    AgentRegistered {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "ObservationSpace")]
        observation_space: serde_json::Value,
        #[serde(rename = "ActionSpace")]
        action_space: serde_json::Value,
    },

    /// Step result
    StepResult {
        #[serde(flatten)]
        result: StepResultPayload,
    },

    /// Batch step results for multiple agents
    BatchStepResult {
        #[serde(rename = "Results")]
        results: Vec<StepResultPayload>,
    },

    /// Reset complete
    ResetComplete {
        #[serde(rename = "Observation")]
        observation: Observation,
        #[serde(rename = "StateHash")]
        state_hash: Option<String>,
    },

    /// State hash response
    StateHash {
        #[serde(rename = "Hash")]
        hash: String,
    },

    /// Vision streams configured
    StreamsConfigured {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Descriptors")]
        descriptors: Vec<StreamDescriptor>,
    },

    /// Error response
    Error {
        #[serde(rename = "Code")]
        code: i32,
        #[serde(rename = "Message")]
        message: String,
    },

    // === Rust -> Game ===
    /// Register an agent
    RegisterAgent {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "AgentType")]
        agent_type: AgentType,
        #[serde(rename = "Config")]
        config: AgentConfig,
    },

    /// Deregister an agent
    DeregisterAgent {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
    },

    /// Execute an action
    ExecuteAction {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Action")]
        action: Action,
        #[serde(rename = "Ticks")]
        ticks: u32,
    },

    /// Reset environment
    Reset {
        #[serde(rename = "Seed")]
        seed: Option<u64>,
        #[serde(rename = "Scenario")]
        scenario: Option<String>,
    },

    /// Request state hash
    GetStateHash,

    /// Configure vision streams
    ConfigureStreams {
        #[serde(rename = "AgentId")]
        agent_id: AgentId,
        #[serde(rename = "Profile")]
        profile: String,
    },

    /// Shutdown the game
    Shutdown,
}

/// Game capabilities sent during Ready
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
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
    fn test_ready_from_game() {
        // Exact JSON format expected from games
        let json = r#"{"Type":"Ready","Name":"ProjectZomboid","Version":"41.78","Capabilities":{"MultiAgent":true,"MaxAgents":8,"Deterministic":false,"Headless":false}}"#;

        let result: Result<GameMessage, _> = serde_json::from_str(json);
        match result {
            Ok(msg) => match msg {
                GameMessage::Ready {
                    name,
                    version,
                    capabilities,
                } => {
                    assert_eq!(name, "ProjectZomboid");
                    assert_eq!(version, "41.78");
                    assert!(capabilities.multi_agent);
                    assert_eq!(capabilities.max_agents, 8);
                    assert!(!capabilities.deterministic);
                }
                _ => panic!("Wrong message type"),
            },
            Err(e) => panic!("Deserialization failed: {}", e),
        }
    }

    #[test]
    fn test_get_state_hash_format() {
        let msg = GameMessage::GetStateHash;
        let bytes = serialize(&msg).unwrap();
        let json = String::from_utf8_lossy(&bytes);

        assert!(json.contains("\"Type\":\"GetStateHash\""));

        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::GetStateHash => {}
            _ => panic!("Wrong message type"),
        }
    }
}
