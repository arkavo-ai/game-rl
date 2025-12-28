//! Action types and action spaces

use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// An action to execute in the environment
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Action {
    /// Discrete action index
    Discrete(i64),
    /// Continuous action vector
    Continuous(Vec<f64>),
    /// Parameterized action
    Parameterized {
        #[serde(rename = "type")]
        action_type: String,
        #[serde(default)]
        params: HashMap<String, serde_json::Value>,
    },
    /// No-op action
    Wait,
}

/// Description of an action space
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ActionSpace {
    /// Discrete action space
    Discrete {
        /// Number of discrete actions
        n: usize,
        /// Optional action names
        #[serde(skip_serializing_if = "Option::is_none")]
        names: Option<Vec<String>>,
    },
    /// Continuous action space (Box)
    Continuous {
        /// Action vector shape
        shape: Vec<usize>,
        /// Lower bounds
        low: Vec<f64>,
        /// Upper bounds
        high: Vec<f64>,
    },
    /// Parameterized discrete actions
    DiscreteParameterized {
        /// Available action types
        actions: Vec<ActionDefinition>,
    },
    /// Multi-discrete action space
    MultiDiscrete {
        /// Number of options for each dimension
        nvec: Vec<usize>,
    },
}

/// Definition of a parameterized action
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActionDefinition {
    /// Action name
    pub name: String,
    /// Action description
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    /// Parameter definitions
    #[serde(default)]
    pub params: HashMap<String, ParamDefinition>,
}

/// Definition of an action parameter
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ParamDefinition {
    /// String parameter
    String {
        #[serde(skip_serializing_if = "Option::is_none")]
        options: Option<Vec<String>>,
    },
    /// Integer parameter
    Int {
        #[serde(skip_serializing_if = "Option::is_none")]
        min: Option<i64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        max: Option<i64>,
    },
    /// Float parameter
    Float {
        #[serde(skip_serializing_if = "Option::is_none")]
        min: Option<f64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        max: Option<f64>,
    },
    /// Position parameter
    Position { dimensions: usize },
    /// Entity reference
    EntityId,
}
