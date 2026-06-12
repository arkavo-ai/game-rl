//! Reward types and reward shaping

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Scalar reward with optional decomposition
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Reward {
    /// Total scalar reward
    pub value: f64,
    /// Decomposed components for analysis
    #[serde(default)]
    pub components: RewardComponents,
}

/// Decomposed reward components
pub type RewardComponents = HashMap<String, f64>;

/// Definition of a reward component
///
/// Serializes PascalCase per spec draft-02 §1.5; snake_case aliases accepted.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct RewardComponentDef {
    /// Component name
    #[serde(alias = "name")]
    pub name: String,
    /// Human-readable description
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "description")]
    pub description: Option<String>,
    /// Expected range
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(alias = "range")]
    pub range: Option<[f64; 2]>,
    /// Default weight
    #[serde(default = "default_weight")]
    #[serde(alias = "default_weight")]
    pub default_weight: f64,
}

fn default_weight() -> f64 {
    1.0
}

/// Trait for computing rewards from game state
pub trait RewardFunction: Send + Sync {
    /// State type for this reward function
    type State;

    /// Compute reward from state transition
    fn compute(&self, prev: &Self::State, current: &Self::State) -> Reward;

    /// List available reward components
    fn components(&self) -> Vec<RewardComponentDef>;
}
