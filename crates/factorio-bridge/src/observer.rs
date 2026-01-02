//! File-based observation reader for Factorio
//!
//! Reads observations written by the Factorio mod via `game.write_file()`
//! to the `script-output/gamerl/` directory.

use game_rl_core::{GameRLError, Observation, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use tokio::fs;
use tokio::time::{Duration, sleep};
use tracing::debug;

/// Configuration for the observation reader
#[derive(Debug, Clone)]
pub struct ObserverConfig {
    /// Base path for observation files
    pub observation_dir: PathBuf,
    /// Timeout waiting for new observation
    pub timeout: Duration,
    /// Poll interval for file changes
    pub poll_interval: Duration,
}

impl Default for ObserverConfig {
    fn default() -> Self {
        // Default Factorio script-output location
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_else(|_| "/tmp".to_string());

        Self {
            observation_dir: PathBuf::from(home)
                .join("Library/Application Support/factorio/script-output/gamerl"),
            timeout: Duration::from_secs(30),
            poll_interval: Duration::from_millis(50),
        }
    }
}

impl ObserverConfig {
    /// Create config for Linux
    pub fn linux() -> Self {
        let home = std::env::var("HOME").unwrap_or_else(|_| "/tmp".to_string());
        Self {
            observation_dir: PathBuf::from(home).join(".factorio/script-output/gamerl"),
            ..Default::default()
        }
    }

    /// Create config with custom path
    pub fn with_path(path: PathBuf) -> Self {
        Self {
            observation_dir: path,
            ..Default::default()
        }
    }
}

/// Raw observation data from Factorio
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct FactorioObservation {
    /// Current game tick
    pub tick: u64,

    /// Global game state
    #[serde(default)]
    pub global: GlobalState,

    /// Per-agent observations
    #[serde(default)]
    pub agents: HashMap<String, AgentObservation>,

    /// State hash for determinism verification
    #[serde(default)]
    pub state_hash: Option<String>,

    /// Result of the last action (success/error feedback)
    #[serde(default)]
    pub action_result: Option<ActionResult>,
}

/// Result of an action execution
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct ActionResult {
    /// Whether the action succeeded
    pub success: bool,
    /// Error message if action failed
    #[serde(default)]
    pub error: Option<String>,
    /// The action type that was attempted
    #[serde(default)]
    pub action_type: Option<String>,
}

/// Global game state
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct GlobalState {
    /// Current research
    #[serde(default)]
    pub research: Option<ResearchState>,

    /// Power grid state
    #[serde(default)]
    pub power: Option<PowerState>,

    /// Pollution stats
    #[serde(default)]
    pub pollution: Option<PollutionState>,

    /// Biter evolution factor
    #[serde(default)]
    pub evolution_factor: f64,

    /// Force production statistics
    #[serde(default)]
    pub production: Option<ProductionStats>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub struct ResearchState {
    #[serde(default)]
    pub current: Option<String>,
    #[serde(default)]
    pub progress: f64,
    /// Completed technologies - uses Value to handle Lua empty tables {} vs arrays []
    #[serde(default)]
    pub completed: serde_json::Value,
    /// Number of researched technologies (Factorio 2.0)
    #[serde(default)]
    pub researched_count: u32,
    /// Research queue (Factorio 2.0) - uses Value to handle Lua empty tables {} vs arrays []
    #[serde(default)]
    pub queue: serde_json::Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct PowerState {
    pub production: f64,
    pub consumption: f64,
    pub satisfaction: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct PollutionState {
    pub total: f64,
    pub rate: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub struct ProductionStats {
    /// Items produced (raw counts from force statistics)
    #[serde(default)]
    pub items_produced: HashMap<String, f64>,
    /// Items consumed (raw counts from force statistics)
    #[serde(default)]
    pub items_consumed: HashMap<String, f64>,
    /// Fluids produced (raw counts from force statistics)
    #[serde(default)]
    pub fluids_produced: HashMap<String, f64>,
    /// API errors encountered when trying to read production stats
    /// Uses Value to handle Lua empty tables {} vs arrays []
    #[serde(default)]
    pub api_errors: serde_json::Value,
}

/// Per-agent observation
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct AgentObservation {
    /// Agent's controlled section bounds (if applicable)
    #[serde(default)]
    pub bounds: Option<Bounds>,

    /// Entities in the agent's observation area
    #[serde(default)]
    pub entities: Vec<EntityState>,

    /// Resources in area
    #[serde(default)]
    pub resources: HashMap<String, u64>,

    /// Nearby enemy units
    #[serde(default)]
    pub enemies: Vec<EnemyState>,

    /// Agent-specific reward components
    #[serde(default)]
    pub reward_components: HashMap<String, f64>,

    /// Whether this agent's episode is done
    #[serde(default)]
    pub done: bool,

    /// Total reward for this step
    #[serde(default)]
    pub reward: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct Bounds {
    pub x_min: f64,
    pub y_min: f64,
    pub x_max: f64,
    pub y_max: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct EntityState {
    pub id: u64,
    #[serde(rename = "type")]
    pub entity_type: String,
    pub name: String,
    pub position: Position,
    #[serde(default)]
    pub direction: u8,
    #[serde(default)]
    pub health: Option<f64>,
    #[serde(default)]
    pub recipe: Option<String>,
    #[serde(default)]
    pub crafting_progress: Option<f64>,
    #[serde(default)]
    pub energy: Option<f64>,
    /// Entity inventory contents (if applicable)
    #[serde(default)]
    pub inventory: Option<HashMap<String, f64>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct Position {
    pub x: f64,
    pub y: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct EnemyState {
    #[serde(rename = "type")]
    pub enemy_type: String,
    pub position: Position,
    pub health: f64,
}

/// Reads observations from Factorio's script-output directory
pub struct ObservationReader {
    config: ObserverConfig,
    /// Last observed tick (to detect new observations)
    last_tick: u64,
}

impl ObservationReader {
    /// Create a new observation reader
    pub fn new(config: ObserverConfig) -> Self {
        Self {
            config,
            last_tick: 0,
        }
    }

    /// Get the observation file path (shared)
    fn observation_file(&self) -> PathBuf {
        self.config.observation_dir.join("observation.json")
    }

    /// Get the per-agent observation file path
    fn agent_observation_file(&self, agent_id: &str) -> PathBuf {
        self.config.observation_dir.join(format!("observation_{}.json", agent_id))
    }

    /// Read the current observation (non-blocking)
    pub async fn read_current(&mut self) -> Result<Option<FactorioObservation>> {
        let path = self.observation_file();

        match fs::read_to_string(&path).await {
            Ok(content) if !content.is_empty() => {
                let obs: FactorioObservation = serde_json::from_str(&content)
                    .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

                if obs.tick > self.last_tick {
                    self.last_tick = obs.tick;
                    debug!("Read observation at tick {}", obs.tick);
                    Ok(Some(obs))
                } else {
                    Ok(None) // No new observation
                }
            }
            Ok(_) => Ok(None), // Empty file
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(GameRLError::IpcError(format!(
                "Failed to read observation: {}",
                e
            ))),
        }
    }

    /// Wait for a new observation (blocking with timeout)
    pub async fn wait_for_observation(&mut self) -> Result<FactorioObservation> {
        let start = std::time::Instant::now();

        loop {
            if start.elapsed() > self.config.timeout {
                return Err(GameRLError::IpcError(
                    "Timeout waiting for observation".to_string(),
                ));
            }

            if let Some(obs) = self.read_current().await? {
                return Ok(obs);
            }

            sleep(self.config.poll_interval).await;
        }
    }

    /// Read agent-specific observation directly (non-blocking, returns current state)
    pub async fn read_agent_observation(&self, agent_id: &str) -> Result<FactorioObservation> {
        let path = self.agent_observation_file(agent_id);

        match fs::read_to_string(&path).await {
            Ok(content) if !content.is_empty() => {
                let obs: FactorioObservation = serde_json::from_str(&content)
                    .map_err(|e| GameRLError::SerializationError(e.to_string()))?;
                debug!("Read agent {} observation at tick {}", agent_id, obs.tick);
                Ok(obs)
            }
            Ok(_) => Err(GameRLError::IpcError(format!(
                "Agent {} observation file is empty",
                agent_id
            ))),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Err(GameRLError::IpcError(
                format!("Agent {} observation file not found", agent_id),
            )),
            Err(e) => Err(GameRLError::IpcError(format!(
                "Failed to read agent observation: {}",
                e
            ))),
        }
    }

    /// Wait for agent-specific observation (avoids race conditions with parallel steps)
    pub async fn wait_for_agent_observation(&self, agent_id: &str) -> Result<FactorioObservation> {
        let path = self.agent_observation_file(agent_id);
        let start = std::time::Instant::now();
        let mut last_tick: u64 = 0;

        // Try to read current tick to detect new observations
        if let Ok(content) = fs::read_to_string(&path).await {
            if !content.is_empty() {
                if let Ok(obs) = serde_json::from_str::<FactorioObservation>(&content) {
                    last_tick = obs.tick;
                }
            }
        }

        loop {
            if start.elapsed() > self.config.timeout {
                return Err(GameRLError::IpcError(
                    format!("Timeout waiting for observation for agent {}", agent_id),
                ));
            }

            match fs::read_to_string(&path).await {
                Ok(content) if !content.is_empty() => {
                    match serde_json::from_str::<FactorioObservation>(&content) {
                        Ok(obs) if obs.tick > last_tick => {
                            debug!("Read agent {} observation at tick {}", agent_id, obs.tick);
                            return Ok(obs);
                        }
                        Ok(_) => {} // Same tick, keep waiting
                        Err(e) => {
                            debug!("Failed to parse observation: {}", e);
                        }
                    }
                }
                Ok(_) => {} // Empty file
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {} // File not yet created
                Err(e) => {
                    return Err(GameRLError::IpcError(format!(
                        "Failed to read agent observation: {}",
                        e
                    )));
                }
            }

            sleep(self.config.poll_interval).await;
        }
    }

    /// Convert Factorio observation to game-rl Observation
    pub fn to_observation(factorio_obs: &FactorioObservation, agent_id: &str) -> Observation {
        let agent_obs = factorio_obs.agents.get(agent_id);

        let mut data = serde_json::Map::new();

        // Add tick
        data.insert("tick".to_string(), serde_json::json!(factorio_obs.tick));

        // Add global state
        data.insert(
            "global".to_string(),
            serde_json::to_value(&factorio_obs.global).unwrap_or_default(),
        );

        // Add agent-specific data
        if let Some(agent) = agent_obs {
            data.insert(
                "entities".to_string(),
                serde_json::to_value(&agent.entities).unwrap_or_default(),
            );
            data.insert(
                "resources".to_string(),
                serde_json::to_value(&agent.resources).unwrap_or_default(),
            );
            data.insert(
                "enemies".to_string(),
                serde_json::to_value(&agent.enemies).unwrap_or_default(),
            );
            if let Some(bounds) = &agent.bounds {
                data.insert(
                    "bounds".to_string(),
                    serde_json::to_value(bounds).unwrap_or_default(),
                );
            }
        }

        // Add state hash if available
        if let Some(hash) = &factorio_obs.state_hash {
            data.insert("state_hash".to_string(), serde_json::json!(hash));
        }

        // Add action result for feedback on success/failure
        if let Some(action_result) = &factorio_obs.action_result {
            data.insert(
                "action_result".to_string(),
                serde_json::to_value(action_result).unwrap_or_default(),
            );
        }

        Observation::Custom(serde_json::Value::Object(data))
    }

    /// Extract reward and done flag for an agent
    pub fn get_step_info(
        factorio_obs: &FactorioObservation,
        agent_id: &str,
    ) -> (f64, bool, HashMap<String, f64>) {
        if let Some(agent) = factorio_obs.agents.get(agent_id) {
            (agent.reward, agent.done, agent.reward_components.clone())
        } else {
            (0.0, false, HashMap::new())
        }
    }

    /// Clear the observation file
    pub async fn clear(&self) -> Result<()> {
        let path = self.observation_file();
        if path.exists() {
            fs::write(&path, "").await.map_err(|e| {
                GameRLError::IpcError(format!("Failed to clear observation: {}", e))
            })?;
        }
        Ok(())
    }

    /// Check if observation directory exists
    pub async fn check_ready(&self) -> bool {
        self.config.observation_dir.exists()
    }

    /// Create observation directory if needed
    pub async fn ensure_dir(&self) -> Result<()> {
        fs::create_dir_all(&self.config.observation_dir)
            .await
            .map_err(|e| {
                GameRLError::IpcError(format!("Failed to create observation dir: {}", e))
            })?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use game_rl_core::Action;

    #[test]
    fn test_parse_observation() {
        let json = r#"{
            "tick": 12345,
            "global": {
                "evolution_factor": 0.15,
                "research": {
                    "current": "automation-2",
                    "progress": 0.45,
                    "completed": ["automation"]
                },
                "power": {
                    "production": 5000,
                    "consumption": 4500,
                    "satisfaction": 1.0
                }
            },
            "agents": {
                "factory_1": {
                    "entities": [
                        {"id": 1, "type": "assembling-machine", "name": "assembling-machine-1", "position": {"x": 10, "y": 20}}
                    ],
                    "resources": {"iron-ore": 1000},
                    "enemies": [],
                    "reward": 1.5,
                    "done": false,
                    "reward_components": {"spm": 1.0, "efficiency": 0.5}
                }
            },
            "state_hash": "abc123"
        }"#;

        let obs: FactorioObservation = serde_json::from_str(json).unwrap();

        assert_eq!(obs.tick, 12345);
        assert_eq!(obs.global.evolution_factor, 0.15);
        assert!(obs.agents.contains_key("factory_1"));

        let agent = &obs.agents["factory_1"];
        assert_eq!(agent.entities.len(), 1);
        assert_eq!(agent.reward, 1.5);
        assert!(!agent.done);
    }

    // =========================================================================
    // Action serialization tests - verify Rust JSON matches what Lua expects
    // =========================================================================
    //
    // Factorio control.lua expects actions with PascalCase "Type" field.
    // These tests ensure Rust serializes actions correctly for Lua consumption.

    #[test]
    fn test_noop_action_serialization() {
        // MCP sends this format
        let json = r#"{"Type": "Noop"}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        // Verify it deserializes correctly
        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "Noop");
                assert!(params.is_empty());
            }
            _ => panic!("Expected Parameterized action, got {:?}", action),
        }

        // Verify serialization produces Lua-compatible JSON
        let serialized = serde_json::to_string(&action).unwrap();
        assert!(
            serialized.contains(r#""Type":"Noop""#),
            "Must have PascalCase Type for Lua: {}",
            serialized
        );
    }

    #[test]
    fn test_build_action_serialization() {
        // Test Build action as MCP sends it
        let json =
            r#"{"Type": "Build", "entity": "iron-chest", "position": [10, 20], "direction": 0}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "Build");
                assert_eq!(params.get("entity").unwrap(), "iron-chest");
                assert_eq!(
                    params.get("position").unwrap(),
                    &serde_json::json!([10, 20])
                );
                assert_eq!(params.get("direction").unwrap(), &serde_json::json!(0));
            }
            _ => panic!("Expected Parameterized action, got {:?}", action),
        }

        // Verify round-trip produces Lua-compatible JSON
        let serialized = serde_json::to_string(&action).unwrap();
        assert!(
            serialized.contains(r#""Type":"Build""#),
            "Must have PascalCase Type: {}",
            serialized
        );
        assert!(
            serialized.contains(r#""entity":"iron-chest""#),
            "Must have entity field: {}",
            serialized
        );
    }

    #[test]
    fn test_mine_action_serialization() {
        let json = r#"{"Type": "Mine", "entity_id": 42}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "Mine");
                assert_eq!(params.get("entity_id").unwrap(), &serde_json::json!(42));
            }
            _ => panic!("Expected Parameterized action"),
        }
    }

    #[test]
    fn test_set_recipe_action_serialization() {
        let json = r#"{"Type": "SetRecipe", "entity_id": 100, "recipe": "iron-gear-wheel"}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "SetRecipe");
                assert_eq!(params.get("entity_id").unwrap(), &serde_json::json!(100));
                assert_eq!(params.get("recipe").unwrap(), "iron-gear-wheel");
            }
            _ => panic!("Expected Parameterized action"),
        }
    }

    #[test]
    fn test_start_research_action_serialization() {
        let json = r#"{"Type": "StartResearch", "technology": "automation-2"}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "StartResearch");
                assert_eq!(params.get("technology").unwrap(), "automation-2");
            }
            _ => panic!("Expected Parameterized action"),
        }
    }

    #[test]
    fn test_rotate_entity_action_serialization() {
        let json = r#"{"Type": "RotateEntity", "entity_id": 55}"#;
        let action: Action = serde_json::from_str(json).unwrap();

        match &action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "RotateEntity");
                assert_eq!(params.get("entity_id").unwrap(), &serde_json::json!(55));
            }
            _ => panic!("Expected Parameterized action"),
        }
    }

    #[test]
    fn test_action_to_lua_json_format() {
        // This test verifies that when we serialize an action to JSON for Lua,
        // it produces the exact format Lua control.lua expects

        let action = Action::Parameterized {
            action_type: "Build".to_string(),
            params: {
                let mut p = std::collections::HashMap::new();
                p.insert("entity".to_string(), serde_json::json!("transport-belt"));
                p.insert("position".to_string(), serde_json::json!([5, 10]));
                p.insert("direction".to_string(), serde_json::json!(2));
                p
            },
        };

        let json = serde_json::to_string(&action).unwrap();

        // Parse back to verify structure
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();

        // Lua expects: action.Type or action.type
        assert!(parsed.get("Type").is_some(), "Must have Type field for Lua");
        assert_eq!(parsed["Type"], "Build");

        // Lua expects: action.entity or action.Entity
        assert!(parsed.get("entity").is_some(), "Must have entity field");
        assert_eq!(parsed["entity"], "transport-belt");

        // Lua expects: action.position or action.Position
        assert!(parsed.get("position").is_some(), "Must have position field");
        assert_eq!(parsed["position"], serde_json::json!([5, 10]));
    }

    // =========================================================================
    // Lua → Rust serialization tests
    // These test that Rust can deserialize the exact JSON format Lua produces
    // =========================================================================

    #[test]
    fn test_lua_observation_format_full() {
        // This is the exact format Lua control.lua produces
        let lua_json = r#"{
            "tick": 216004234,
            "global": {
                "evolution_factor": 0.014,
                "research": {
                    "current": null,
                    "progress": 0,
                    "completed": ["automation", "logistics"],
                    "queue": [],
                    "researched_count": 2
                },
                "production": {
                    "items_produced": {"iron-plate": 100},
                    "items_consumed": {"iron-ore": 50},
                    "fluids_produced": {},
                    "api_errors": []
                },
                "power": {
                    "production": 5000,
                    "consumption": 4500,
                    "satisfaction": 1.0
                },
                "pollution": {
                    "total": 100.5,
                    "rate": 0.5
                },
                "game_speed": 1,
                "game_tick": 216004234
            },
            "agents": {},
            "state_hash": "216004234-abc"
        }"#;

        let obs: FactorioObservation = serde_json::from_str(lua_json)
            .expect("Should deserialize full Lua observation");

        assert_eq!(obs.tick, 216004234);
        assert_eq!(obs.global.evolution_factor, 0.014);
        assert_eq!(obs.global.research.as_ref().unwrap().researched_count, 2);
        assert!(obs.global.research.as_ref().unwrap().completed.as_array().map_or(false, |a| a.len() == 2));
    }

    #[test]
    fn test_lua_empty_tables_as_objects() {
        // In Lua, empty tables {} serialize as JSON objects {}, not arrays []
        // This tests that Rust can handle empty objects where arrays are expected
        let lua_json = r#"{
            "tick": 1000,
            "global": {
                "evolution_factor": 0,
                "research": {
                    "progress": 0,
                    "queue": {},
                    "researched_count": 0,
                    "completed": []
                },
                "production": {
                    "items_produced": {},
                    "items_consumed": {},
                    "fluids_produced": {},
                    "api_errors": {}
                },
                "power": {
                    "production": 0,
                    "consumption": 0,
                    "satisfaction": 1
                },
                "pollution": {
                    "total": 0,
                    "rate": 0
                },
                "game_speed": 1,
                "game_tick": 1000
            },
            "agents": {},
            "state_hash": "1000-x"
        }"#;

        let obs: FactorioObservation = serde_json::from_str(lua_json)
            .expect("Should handle empty Lua tables as objects");

        assert_eq!(obs.tick, 1000);
    }

    #[test]
    fn test_lua_missing_optional_fields() {
        // Lua may omit optional fields entirely
        let lua_json = r#"{
            "tick": 500,
            "global": {
                "evolution_factor": 0,
                "power": {
                    "production": 0,
                    "consumption": 0,
                    "satisfaction": 1
                },
                "pollution": {
                    "total": 0,
                    "rate": 0
                },
                "game_speed": 1,
                "game_tick": 500
            },
            "agents": {},
            "state_hash": "500-y"
        }"#;

        let obs: FactorioObservation = serde_json::from_str(lua_json)
            .expect("Should handle missing optional fields");

        assert!(obs.global.research.is_none());
        assert!(obs.global.production.is_none());
    }

    #[test]
    fn test_lua_research_state_variants() {
        // Test various research state formats Lua might produce

        // With active research
        let with_research = r#"{
            "current": "automation-2",
            "progress": 0.45,
            "completed": ["automation"],
            "queue": ["logistics"],
            "researched_count": 1
        }"#;
        let state: ResearchState = serde_json::from_str(with_research)
            .expect("Should parse research with active tech");
        assert_eq!(state.current, Some("automation-2".to_string()));
        assert_eq!(state.progress, 0.45);
        assert!(state.completed.as_array().map_or(false, |a| a.len() == 1));

        // No research, empty queue as object (Lua behavior)
        let no_research = r#"{
            "current": null,
            "progress": 0,
            "completed": [],
            "queue": {},
            "researched_count": 0
        }"#;
        let state: ResearchState = serde_json::from_str(no_research)
            .expect("Should parse research with empty queue as object");
        assert!(state.current.is_none());

        // Empty completed as object (Lua empty table {} serializes as object)
        let empty_completed_obj = r#"{
            "current": null,
            "progress": 0,
            "completed": {},
            "queue": {},
            "researched_count": 0
        }"#;
        let state: ResearchState = serde_json::from_str(empty_completed_obj)
            .expect("Should parse research with completed as empty object");
        // completed can be {} or [] - both are valid
        assert!(state.completed.is_object() || state.completed.is_array());

        // Minimal - only required fields
        let minimal = r#"{
            "progress": 0,
            "researched_count": 5
        }"#;
        let state: ResearchState = serde_json::from_str(minimal)
            .expect("Should parse minimal research state");
        assert_eq!(state.researched_count, 5);
        // Default is Null
        assert!(state.completed.is_null() || state.completed.is_array() || state.completed.is_object());
    }

    #[test]
    fn test_lua_empty_tables_critical() {
        // CRITICAL: This tests the exact format Lua produces when there are no researched technologies
        // Lua empty tables {} serialize as JSON objects {}, NOT arrays []
        // This caused serialization errors in production
        let lua_research = r#"{
            "progress": 0,
            "completed": {},
            "queue": {},
            "researched_count": 0
        }"#;
        let state: ResearchState = serde_json::from_str(lua_research)
            .expect("CRITICAL: Must handle Lua empty table {} for completed field");
        assert_eq!(state.researched_count, 0);
    }

    #[test]
    fn test_lua_production_stats_variants() {
        // Full production stats
        let full = r#"{
            "items_produced": {"iron-plate": 1000, "copper-plate": 500},
            "items_consumed": {"iron-ore": 1000, "copper-ore": 500},
            "fluids_produced": {"petroleum-gas": 100},
            "api_errors": []
        }"#;
        let stats: ProductionStats = serde_json::from_str(full)
            .expect("Should parse full production stats");
        assert_eq!(stats.items_produced.get("iron-plate"), Some(&1000.0));

        // Empty (Lua empty tables as objects)
        let empty = r#"{
            "items_produced": {},
            "items_consumed": {},
            "fluids_produced": {},
            "api_errors": {}
        }"#;
        let stats: ProductionStats = serde_json::from_str(empty)
            .expect("Should parse empty production stats");
        assert!(stats.items_produced.is_empty());

        // With API errors
        let with_errors = r#"{
            "items_produced": {},
            "items_consumed": {},
            "fluids_produced": {},
            "api_errors": ["LuaForce missing: item_production_statistics"]
        }"#;
        let stats: ProductionStats = serde_json::from_str(with_errors)
            .expect("Should parse production stats with API errors");
        assert!(stats.api_errors.as_array().map_or(false, |a| a.len() == 1));
    }

    #[test]
    fn test_lua_agent_observation_format() {
        let agent_json = r#"{
            "entities": [
                {
                    "id": 1,
                    "type": "assembling-machine",
                    "name": "assembling-machine-1",
                    "position": {"x": 10.5, "y": 20.5},
                    "health": 0.8,
                    "direction": 2,
                    "recipe": "iron-gear-wheel",
                    "energy": 0.9
                }
            ],
            "resources": {"iron-ore": 5000, "copper-ore": 3000},
            "enemies": [],
            "reward": 1.5,
            "done": false,
            "reward_components": {"production": 1.0, "efficiency": 0.5}
        }"#;

        let obs: AgentObservation = serde_json::from_str(agent_json)
            .expect("Should parse agent observation");

        assert_eq!(obs.entities.len(), 1);
        assert_eq!(obs.entities[0].name, "assembling-machine-1");
        assert_eq!(obs.entities[0].recipe, Some("iron-gear-wheel".to_string()));
        assert_eq!(obs.reward, 1.5);
    }

    #[test]
    fn test_lua_entity_state_variants() {
        // Full entity
        let full = r#"{
            "id": 42,
            "type": "furnace",
            "name": "steel-furnace",
            "position": {"x": 0, "y": 0},
            "health": 1.0,
            "direction": 0,
            "recipe": "steel-plate",
            "energy": 0.75,
            "inventory": {"iron-plate": 10}
        }"#;
        let entity: EntityState = serde_json::from_str(full)
            .expect("Should parse full entity");
        assert_eq!(entity.id, 42);
        assert_eq!(entity.recipe, Some("steel-plate".to_string()));

        // Minimal entity (no optional fields)
        let minimal = r#"{
            "id": 1,
            "type": "transport-belt",
            "name": "transport-belt",
            "position": {"x": 5, "y": 10}
        }"#;
        let entity: EntityState = serde_json::from_str(minimal)
            .expect("Should parse minimal entity");
        assert!(entity.recipe.is_none());
        assert!(entity.inventory.is_none());
    }
}
