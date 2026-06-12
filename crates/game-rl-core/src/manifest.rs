//! Game manifest types

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::action::ActionSpace;
use crate::reward::RewardComponentDef;
use crate::stream::StreamProfile;

/// Game-RL protocol version implemented by this workspace (spec draft-02)
pub const PROTOCOL_VERSION: &str = "2.0.0-draft02";

/// Game manifest describing environment capabilities
///
/// Serializes PascalCase per spec draft-02 §1.5; snake_case draft-00 aliases
/// are accepted on deserialization for the migration window (REQ-ROB-04).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GameManifest {
    /// Game name
    #[serde(alias = "name")]
    pub name: String,
    /// Game version
    #[serde(alias = "version")]
    pub version: String,
    /// Game-RL protocol version
    #[serde(alias = "game_rl_version")]
    pub game_rl_version: String,
    /// Environment capabilities
    #[serde(alias = "capabilities")]
    pub capabilities: Capabilities,
    /// Default observation space
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "default_observation_space")]
    pub default_observation_space: Option<serde_json::Value>,
    /// Default action space
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "default_action_space")]
    pub default_action_space: Option<ActionSpace>,
    /// Available reward components
    #[serde(default)]
    #[serde(alias = "reward_components")]
    pub reward_components: Vec<RewardComponentDef>,
    /// Available stream profiles
    #[serde(default)]
    #[serde(alias = "stream_profiles")]
    pub stream_profiles: HashMap<String, StreamProfile>,
    /// Available scenarios
    #[serde(default)]
    #[serde(alias = "scenarios")]
    pub scenarios: Vec<Scenario>,
    /// Simulation tick rate
    #[serde(default = "default_tick_rate")]
    #[serde(alias = "tick_rate")]
    pub tick_rate: u32,
    /// Maximum episode length in ticks
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "max_episode_ticks")]
    pub max_episode_ticks: Option<u64>,
    /// Conformance level
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "compliance")]
    pub compliance: Option<Compliance>,
}

fn default_tick_rate() -> u32 {
    60
}

impl Default for Capabilities {
    fn default() -> Self {
        Self {
            multi_agent: false,
            max_agents: 1,
            agent_types: vec![],
            deterministic: false,
            save_replay: false,
            domain_randomization: false,
            headless: false,
            variable_timestep: false,
            spatial_intent: false,
        }
    }
}

impl Default for GameManifest {
    fn default() -> Self {
        Self {
            name: "Unknown".into(),
            version: "0.0.0".into(),
            game_rl_version: PROTOCOL_VERSION.into(),
            capabilities: Default::default(),
            default_observation_space: None,
            default_action_space: None,
            reward_components: vec![],
            stream_profiles: HashMap::new(),
            scenarios: vec![],
            tick_rate: 60,
            max_episode_ticks: None,
            compliance: None,
        }
    }
}

/// Environment capabilities
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Capabilities {
    /// Supports multiple agents
    #[serde(default)]
    #[serde(alias = "multi_agent")]
    pub multi_agent: bool,
    /// Maximum number of agents
    #[serde(default = "default_max_agents")]
    #[serde(alias = "max_agents")]
    pub max_agents: usize,
    /// Supported agent types
    #[serde(default)]
    #[serde(alias = "agent_types")]
    pub agent_types: Vec<String>,
    /// Deterministic simulation
    #[serde(default)]
    #[serde(alias = "deterministic")]
    pub deterministic: bool,
    /// Supports trajectory save/replay
    #[serde(default)]
    #[serde(alias = "save_replay")]
    pub save_replay: bool,
    /// Supports domain randomization
    #[serde(default)]
    #[serde(alias = "domain_randomization")]
    pub domain_randomization: bool,
    /// Supports headless operation
    #[serde(default)]
    #[serde(alias = "headless")]
    pub headless: bool,
    /// Supports variable timestep
    #[serde(default)]
    #[serde(alias = "variable_timestep")]
    pub variable_timestep: bool,
    /// Supports intent-based spatial actions (resolve_spatial)
    #[serde(default)]
    #[serde(alias = "spatial_intent")]
    pub spatial_intent: bool,
}

fn default_max_agents() -> usize {
    1
}

/// Scenario definition
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Scenario {
    /// Scenario name
    #[serde(alias = "name")]
    pub name: String,
    /// Human-readable description
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "description")]
    pub description: Option<String>,
    /// Scenario-specific configuration
    #[serde(default)]
    #[serde(alias = "config")]
    pub config: HashMap<String, serde_json::Value>,
}

/// Conformance declaration
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Compliance {
    /// Conformance level (1, 2, or 3)
    #[serde(alias = "level")]
    pub level: u8,
    /// Protocol version
    #[serde(alias = "version")]
    pub version: String,
    /// URL to test results
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "test_results_url")]
    pub test_results_url: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_capabilities_default_spatial_intent_false() {
        let caps = Capabilities::default();
        assert!(!caps.spatial_intent);
    }

    #[test]
    fn test_capabilities_spatial_intent_from_json() {
        let json = r#"{"SpatialIntent": true}"#;
        let caps: Capabilities = serde_json::from_str(json).unwrap();
        assert!(caps.spatial_intent);
    }

    #[test]
    fn test_capabilities_accepts_draft00_snake_case_alias() {
        let json = r#"{"spatial_intent": true, "multi_agent": true, "max_agents": 4}"#;
        let caps: Capabilities = serde_json::from_str(json).unwrap();
        assert!(caps.spatial_intent);
        assert!(caps.multi_agent);
        assert_eq!(caps.max_agents, 4);
    }

    #[test]
    fn test_manifest_serializes_pascal_case() {
        let manifest = GameManifest::default();
        let json = serde_json::to_value(&manifest).unwrap();
        assert!(json.get("Name").is_some());
        assert!(json.get("Version").is_some());
        assert_eq!(json["GameRlVersion"], PROTOCOL_VERSION);
        assert!(json["Capabilities"].get("MultiAgent").is_some());
        assert!(json.get("name").is_none(), "snake_case must not serialize");
    }

    #[test]
    fn test_manifest_round_trip() {
        let manifest = GameManifest::default();
        let json = serde_json::to_string(&manifest).unwrap();
        let back: GameManifest = serde_json::from_str(&json).unwrap();
        assert_eq!(back.game_rl_version, PROTOCOL_VERSION);
    }
}
