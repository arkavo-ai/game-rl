//! Wire protocol for Rust <-> C# communication
//!
//! Messages are serialized using MessagePack for efficiency.
//! Custom serialization ensures {"type": "...", ...} format that C# expects.

use game_rl_core::{Action, AgentConfig, AgentId, AgentType, GameEvent, Observation};
use serde::{Deserialize, Serialize, Serializer, Deserializer};
use serde::de::{self, MapAccess, Visitor};
use serde::ser::SerializeMap;
use std::collections::HashMap;
use std::fmt;

/// Messages sent between Rust bridge and C# game
#[derive(Debug, Clone)]
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

impl Serialize for GameMessage {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        match self {
            GameMessage::Ready { name, version, capabilities } => {
                let mut map = serializer.serialize_map(Some(4))?;
                map.serialize_entry("type", "ready")?;
                map.serialize_entry("name", name)?;
                map.serialize_entry("version", version)?;
                map.serialize_entry("capabilities", capabilities)?;
                map.end()
            }
            GameMessage::StateUpdate { tick, state, events } => {
                let mut map = serializer.serialize_map(Some(4))?;
                map.serialize_entry("type", "state_update")?;
                map.serialize_entry("tick", tick)?;
                map.serialize_entry("state", state)?;
                map.serialize_entry("events", events)?;
                map.end()
            }
            GameMessage::AgentRegistered { agent_id, observation_space, action_space } => {
                let mut map = serializer.serialize_map(Some(4))?;
                map.serialize_entry("type", "agent_registered")?;
                map.serialize_entry("agent_id", agent_id)?;
                map.serialize_entry("observation_space", observation_space)?;
                map.serialize_entry("action_space", action_space)?;
                map.end()
            }
            GameMessage::StepResult { agent_id, observation, reward, reward_components, done, truncated, state_hash } => {
                let field_count = if state_hash.is_some() { 8 } else { 7 };
                let mut map = serializer.serialize_map(Some(field_count))?;
                map.serialize_entry("type", "step_result")?;
                map.serialize_entry("agent_id", agent_id)?;
                map.serialize_entry("observation", observation)?;
                map.serialize_entry("reward", reward)?;
                map.serialize_entry("reward_components", reward_components)?;
                map.serialize_entry("done", done)?;
                map.serialize_entry("truncated", truncated)?;
                if let Some(hash) = state_hash {
                    map.serialize_entry("state_hash", hash)?;
                }
                map.end()
            }
            GameMessage::ResetComplete { observation, state_hash } => {
                let field_count = if state_hash.is_some() { 3 } else { 2 };
                let mut map = serializer.serialize_map(Some(field_count))?;
                map.serialize_entry("type", "reset_complete")?;
                map.serialize_entry("observation", observation)?;
                if let Some(hash) = state_hash {
                    map.serialize_entry("state_hash", hash)?;
                }
                map.end()
            }
            GameMessage::StateHash { hash } => {
                let mut map = serializer.serialize_map(Some(2))?;
                map.serialize_entry("type", "state_hash")?;
                map.serialize_entry("hash", hash)?;
                map.end()
            }
            GameMessage::Error { code, message } => {
                let mut map = serializer.serialize_map(Some(3))?;
                map.serialize_entry("type", "error")?;
                map.serialize_entry("code", code)?;
                map.serialize_entry("message", message)?;
                map.end()
            }
            GameMessage::RegisterAgent { agent_id, agent_type, config } => {
                let mut map = serializer.serialize_map(Some(4))?;
                map.serialize_entry("type", "register_agent")?;
                map.serialize_entry("agent_id", agent_id)?;
                map.serialize_entry("agent_type", agent_type)?;
                map.serialize_entry("config", config)?;
                map.end()
            }
            GameMessage::DeregisterAgent { agent_id } => {
                let mut map = serializer.serialize_map(Some(2))?;
                map.serialize_entry("type", "deregister_agent")?;
                map.serialize_entry("agent_id", agent_id)?;
                map.end()
            }
            GameMessage::ExecuteAction { agent_id, action, ticks } => {
                let mut map = serializer.serialize_map(Some(4))?;
                map.serialize_entry("type", "execute_action")?;
                map.serialize_entry("agent_id", agent_id)?;
                map.serialize_entry("action", action)?;
                map.serialize_entry("ticks", ticks)?;
                map.end()
            }
            GameMessage::Reset { seed, scenario } => {
                let mut map = serializer.serialize_map(Some(3))?;
                map.serialize_entry("type", "reset")?;
                map.serialize_entry("seed", seed)?;
                map.serialize_entry("scenario", scenario)?;
                map.end()
            }
            GameMessage::GetStateHash => {
                let mut map = serializer.serialize_map(Some(1))?;
                map.serialize_entry("type", "get_state_hash")?;
                map.end()
            }
            GameMessage::Shutdown => {
                let mut map = serializer.serialize_map(Some(1))?;
                map.serialize_entry("type", "shutdown")?;
                map.end()
            }
        }
    }
}

impl<'de> Deserialize<'de> for GameMessage {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct GameMessageVisitor;

        impl<'de> Visitor<'de> for GameMessageVisitor {
            type Value = GameMessage;

            fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
                formatter.write_str("a map with a 'type' field")
            }

            fn visit_map<M>(self, mut map: M) -> Result<GameMessage, M::Error>
            where
                M: MapAccess<'de>,
            {
                let mut msg_type: Option<String> = None;
                let mut fields: HashMap<String, serde_json::Value> = HashMap::new();

                while let Some(key) = map.next_key::<String>()? {
                    let value: serde_json::Value = map.next_value()?;
                    if key == "type" {
                        msg_type = value.as_str().map(|s| s.to_string());
                    } else {
                        fields.insert(key, value);
                    }
                }

                let msg_type = msg_type.ok_or_else(|| de::Error::missing_field("type"))?;

                match msg_type.as_str() {
                    "ready" => Ok(GameMessage::Ready {
                        name: get_string(&fields, "name")?,
                        version: get_string(&fields, "version")?,
                        capabilities: get_field(&fields, "capabilities")?,
                    }),
                    "state_update" => Ok(GameMessage::StateUpdate {
                        tick: get_field(&fields, "tick")?,
                        state: fields.get("state").cloned().unwrap_or(serde_json::Value::Null),
                        events: get_field::<Vec<GameEvent>, M::Error>(&fields, "events").unwrap_or_default(),
                    }),
                    "agent_registered" => Ok(GameMessage::AgentRegistered {
                        agent_id: get_string(&fields, "agent_id")?,
                        observation_space: fields.get("observation_space").cloned().unwrap_or(serde_json::Value::Null),
                        action_space: fields.get("action_space").cloned().unwrap_or(serde_json::Value::Null),
                    }),
                    "step_result" => Ok(GameMessage::StepResult {
                        agent_id: get_string(&fields, "agent_id")?,
                        observation: get_field(&fields, "observation")?,
                        reward: get_field(&fields, "reward")?,
                        reward_components: get_field::<HashMap<String, f64>, M::Error>(&fields, "reward_components").unwrap_or_default(),
                        done: get_field(&fields, "done")?,
                        truncated: get_field(&fields, "truncated")?,
                        state_hash: fields.get("state_hash").and_then(|v| v.as_str()).map(|s| s.to_string()),
                    }),
                    "reset_complete" => Ok(GameMessage::ResetComplete {
                        observation: get_field(&fields, "observation")?,
                        state_hash: fields.get("state_hash").and_then(|v| v.as_str()).map(|s| s.to_string()),
                    }),
                    "state_hash" => Ok(GameMessage::StateHash {
                        hash: get_string(&fields, "hash")?,
                    }),
                    "error" => Ok(GameMessage::Error {
                        code: get_field(&fields, "code")?,
                        message: get_string(&fields, "message")?,
                    }),
                    "register_agent" => Ok(GameMessage::RegisterAgent {
                        agent_id: get_string(&fields, "agent_id")?,
                        agent_type: get_field(&fields, "agent_type")?,
                        config: get_field::<AgentConfig, M::Error>(&fields, "config").unwrap_or_default(),
                    }),
                    "deregister_agent" => Ok(GameMessage::DeregisterAgent {
                        agent_id: get_string(&fields, "agent_id")?,
                    }),
                    "execute_action" => Ok(GameMessage::ExecuteAction {
                        agent_id: get_string(&fields, "agent_id")?,
                        action: get_field(&fields, "action")?,
                        ticks: get_field::<u32, M::Error>(&fields, "ticks").unwrap_or(1),
                    }),
                    "reset" => Ok(GameMessage::Reset {
                        seed: get_field::<u64, M::Error>(&fields, "seed").ok(),
                        scenario: fields.get("scenario").and_then(|v| v.as_str()).map(|s| s.to_string()),
                    }),
                    "get_state_hash" => Ok(GameMessage::GetStateHash),
                    "shutdown" => Ok(GameMessage::Shutdown),
                    _ => Err(de::Error::unknown_variant(&msg_type, &[
                        "ready", "state_update", "agent_registered", "step_result",
                        "reset_complete", "state_hash", "error", "register_agent",
                        "deregister_agent", "execute_action", "reset", "get_state_hash", "shutdown"
                    ])),
                }
            }
        }

        deserializer.deserialize_map(GameMessageVisitor)
    }
}

fn get_string<E: de::Error>(fields: &HashMap<String, serde_json::Value>, key: &'static str) -> Result<String, E> {
    fields.get(key)
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
        .ok_or_else(|| de::Error::missing_field(key))
}

fn get_field<T, E>(fields: &HashMap<String, serde_json::Value>, key: &'static str) -> Result<T, E>
where
    T: for<'de> Deserialize<'de>,
    E: de::Error,
{
    fields.get(key)
        .ok_or_else(|| de::Error::missing_field(key))
        .and_then(|v| serde_json::from_value(v.clone()).map_err(|e| de::Error::custom(e.to_string())))
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

        // Should be a map with just {"type": "get_state_hash"}
        println!("GetStateHash bytes: {:02x?}", bytes);

        // First byte should be 0x81 (fixmap with 1 element)
        assert_eq!(bytes[0], 0x81, "Should be fixmap with 1 element");

        // Verify it can roundtrip
        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::GetStateHash => {},
            _ => panic!("Wrong message type"),
        }
    }
}
