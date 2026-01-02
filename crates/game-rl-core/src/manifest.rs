//! Game manifest types

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::action::ActionSpace;
use crate::reward::RewardComponentDef;
use crate::stream::StreamProfile;

/// Game manifest describing environment capabilities
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameManifest {
    /// Game name
    pub name: String,
    /// Game version
    pub version: String,
    /// Game-RL protocol version
    pub game_rl_version: String,
    /// Environment capabilities
    pub capabilities: Capabilities,
    /// Default observation space
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_observation_space: Option<serde_json::Value>,
    /// Default action space
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_action_space: Option<ActionSpace>,
    /// Available reward components
    #[serde(default)]
    pub reward_components: Vec<RewardComponentDef>,
    /// Available stream profiles
    #[serde(default)]
    pub stream_profiles: HashMap<String, StreamProfile>,
    /// Available scenarios
    #[serde(default)]
    pub scenarios: Vec<Scenario>,
    /// Simulation tick rate
    #[serde(default = "default_tick_rate")]
    pub tick_rate: u32,
    /// Maximum episode length in ticks
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_episode_ticks: Option<u64>,
    /// Conformance level
    #[serde(skip_serializing_if = "Option::is_none")]
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
        }
    }
}

impl Default for GameManifest {
    fn default() -> Self {
        Self {
            name: "Unknown".into(),
            version: "0.0.0".into(),
            game_rl_version: env!("CARGO_PKG_VERSION").into(),
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
pub struct Capabilities {
    /// Supports multiple agents
    #[serde(default)]
    pub multi_agent: bool,
    /// Maximum number of agents
    #[serde(default = "default_max_agents")]
    pub max_agents: usize,
    /// Supported agent types
    #[serde(default)]
    pub agent_types: Vec<String>,
    /// Deterministic simulation
    #[serde(default)]
    pub deterministic: bool,
    /// Supports trajectory save/replay
    #[serde(default)]
    pub save_replay: bool,
    /// Supports domain randomization
    #[serde(default)]
    pub domain_randomization: bool,
    /// Supports headless operation
    #[serde(default)]
    pub headless: bool,
    /// Supports variable timestep
    #[serde(default)]
    pub variable_timestep: bool,
}

fn default_max_agents() -> usize {
    1
}

/// Scenario definition
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Scenario {
    /// Scenario name
    pub name: String,
    /// Human-readable description
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    /// Scenario-specific configuration
    #[serde(default)]
    pub config: HashMap<String, serde_json::Value>,
}

/// Conformance declaration
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Compliance {
    /// Conformance level (1, 2, or 3)
    pub level: u8,
    /// Protocol version
    pub version: String,
    /// URL to test results
    #[serde(skip_serializing_if = "Option::is_none")]
    pub test_results_url: Option<String>,
}
