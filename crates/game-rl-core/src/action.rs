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
    /// Parameterized action with Type field and flattened params
    Parameterized {
        #[serde(rename = "Type")]
        action_type: String,
        /// All other fields become params (flattened for natural JSON format)
        #[serde(flatten, default)]
        params: HashMap<String, serde_json::Value>,
    },
    /// No-op action
    Wait,
}

/// Description of an action space
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
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
#[serde(rename_all = "PascalCase")]
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
#[serde(tag = "Type", rename_all = "PascalCase")]
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parameterized_action_flat_params() {
        // MCP sends flat format with PascalCase Type field - params should be captured via flatten
        let json = r#"{"Type": "set_work_priority", "colonist_id": "Human917", "work_type": "Hunting", "priority": 1}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "set_work_priority");
                assert_eq!(params.get("colonist_id").unwrap(), "Human917");
                assert_eq!(params.get("work_type").unwrap(), "Hunting");
                assert_eq!(params.get("priority").unwrap(), 1);
            }
            _ => panic!("Expected Parameterized action, got {:?}", action),
        }

        // Verify serialization preserves flat format with PascalCase Type
        let serialized = serde_json::to_string(&action).unwrap();
        println!("Serialized: {}", serialized);
        assert!(
            serialized.contains("\"Type\":\"set_work_priority\""),
            "Should have PascalCase Type field"
        );
        assert!(
            serialized.contains("\"colonist_id\":\"Human917\""),
            "Should have flat colonist_id"
        );
        assert!(
            !serialized.contains("\"params\":"),
            "Should NOT have nested params"
        );
    }

    #[test]
    fn test_wait_action() {
        let json = r#"{"Type": "wait"}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "wait");
                assert!(params.is_empty());
            }
            _ => panic!("Expected Parameterized action for wait, got {:?}", action),
        }
    }
}
