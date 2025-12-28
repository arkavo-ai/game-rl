//! Observation types

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::agent::AgentId;
use crate::reward::RewardComponents;

/// Result of a simulation step
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StepResult {
    /// Agent that took the action
    pub agent_id: AgentId,

    /// Global step counter
    pub step_id: u64,

    /// Current simulation tick
    pub tick: u64,

    /// Agent-specific observation
    pub observation: Observation,

    /// Scalar reward signal
    pub reward: f64,

    /// Decomposed reward for analysis
    #[serde(default)]
    pub reward_components: RewardComponents,

    /// Episode terminated (goal reached or failed)
    pub done: bool,

    /// Episode truncated (time limit)
    pub truncated: bool,

    /// Why episode ended
    #[serde(skip_serializing_if = "Option::is_none")]
    pub termination_reason: Option<TerminationReason>,

    /// Notable events this step
    #[serde(default)]
    pub events: Vec<GameEvent>,

    /// Vision stream frame references
    #[serde(default)]
    pub frame_ids: HashMap<String, u64>,

    /// Valid actions for next step (if dynamic action space)
    #[serde(skip_serializing_if = "Option::is_none")]
    pub available_actions: Option<Vec<serde_json::Value>>,

    /// Performance metrics
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metrics: Option<StepMetrics>,

    /// Determinism verification hash
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state_hash: Option<String>,
}

/// Agent observation (game-specific contents)
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Observation {
    /// Structured observation
    Structured(HashMap<String, serde_json::Value>),
    /// Raw vector observation
    Vector(Vec<f64>),
    /// Custom observation format
    Custom(serde_json::Value),
}

/// Why an episode ended
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum TerminationReason {
    Success,
    Failure,
    Timeout,
    External,
}

/// Game event that occurred during a step
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameEvent {
    /// Event type identifier
    #[serde(rename = "type")]
    pub event_type: String,

    /// Tick when event occurred
    pub tick: u64,

    /// Severity level (0-3)
    #[serde(default)]
    pub severity: u8,

    /// Event-specific details
    #[serde(default)]
    pub details: serde_json::Value,
}

/// Performance metrics for a step
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StepMetrics {
    /// Total step time in milliseconds
    pub step_ms: f64,

    /// GPU time in milliseconds
    #[serde(skip_serializing_if = "Option::is_none")]
    pub gpu_ms: Option<f64>,

    /// Whether a vision frame was dropped
    #[serde(default)]
    pub frame_dropped: bool,
}
