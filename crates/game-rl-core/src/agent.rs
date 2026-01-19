//! Agent types and registration

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Unique identifier for an agent
pub type AgentId = String;

/// Standard agent archetypes (game-agnostic)
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "PascalCase")]
pub enum AgentType {
    /// Passive observer - receives observations but takes no actions
    Observer,
    /// Controls a player character or avatar
    Player,
    /// Controls a single game entity (NPC, unit, pawn)
    Entity,
    /// High-level strategic control (RTS commander, factory planner)
    #[serde(alias = "StrategyController", alias = "ColonyManager")]
    Controller,
    /// Controls game systems (weather, economy, spawning, physics)
    System,
    /// Meta-level control (narrative, difficulty, events)
    Director,
    /// Custom agent type for game-specific needs
    Custom(String),
}

/// Configuration for agent registration
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AgentConfig {
    /// Entity ID for EntityBehavior agents
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entity_id: Option<String>,

    /// Observation detail level
    #[serde(default = "default_observation_profile")]
    pub observation_profile: String,

    /// Allowed action types
    #[serde(default)]
    pub action_mask: Vec<String>,

    /// Custom reward shaping
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reward_shaping: Option<RewardShaping>,

    /// Additional game-specific configuration
    #[serde(default)]
    pub extra: HashMap<String, serde_json::Value>,
}

fn default_observation_profile() -> String {
    "default".to_string()
}

/// Reward shaping configuration
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct RewardShaping {
    /// Which reward components to include
    pub components: Vec<String>,
    /// Weight multipliers for each component
    #[serde(default)]
    pub weights: HashMap<String, f32>,
}

/// Agent manifest returned after registration
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AgentManifest {
    pub agent_id: AgentId,
    pub agent_type: AgentType,
    pub observation_space: serde_json::Value,
    pub action_space: serde_json::Value,
    pub reward_components: Vec<String>,
}

impl Default for AgentConfig {
    fn default() -> Self {
        Self {
            entity_id: None,
            observation_profile: "default".to_string(),
            action_mask: Vec::new(),
            reward_shaping: None,
            extra: HashMap::new(),
        }
    }
}

/// Agent status in the registry
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "PascalCase")]
pub enum AgentStatus {
    Registered,
    Active,
    Terminal,
    Disconnected,
}

/// Agent registry entry
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct AgentEntry {
    pub agent_id: AgentId,
    pub agent_type: AgentType,
    pub status: AgentStatus,
    pub registered_at: String,
    pub last_step: u64,
    pub total_reward: f64,
}
