//! File-based observation reader for Factorio
//!
//! Reads observations written by the Factorio mod via `game.write_file()`
//! to the `script-output/gamerl/` directory.

use game_rl_core::{GameRLError, Observation, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use tokio::fs;
use tokio::time::{sleep, Duration};
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
            observation_dir: PathBuf::from(home)
                .join(".factorio/script-output/gamerl"),
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

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct ResearchState {
    pub current: Option<String>,
    pub progress: f64,
    pub completed: Vec<String>,
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

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct ProductionStats {
    /// Items produced per minute
    #[serde(default)]
    pub items_per_minute: HashMap<String, f64>,
    /// Science per minute
    #[serde(default)]
    pub spm: f64,
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

    /// Get the observation file path
    fn observation_file(&self) -> PathBuf {
        self.config.observation_dir.join("observation.json")
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

    /// Convert Factorio observation to game-rl Observation
    pub fn to_observation(
        factorio_obs: &FactorioObservation,
        agent_id: &str,
    ) -> Observation {
        let agent_obs = factorio_obs.agents.get(agent_id);

        let mut data = serde_json::Map::new();

        // Add tick
        data.insert("tick".to_string(), serde_json::json!(factorio_obs.tick));

        // Add global state
        data.insert("global".to_string(), serde_json::to_value(&factorio_obs.global).unwrap_or_default());

        // Add agent-specific data
        if let Some(agent) = agent_obs {
            data.insert("entities".to_string(), serde_json::to_value(&agent.entities).unwrap_or_default());
            data.insert("resources".to_string(), serde_json::to_value(&agent.resources).unwrap_or_default());
            data.insert("enemies".to_string(), serde_json::to_value(&agent.enemies).unwrap_or_default());
            if let Some(bounds) = &agent.bounds {
                data.insert("bounds".to_string(), serde_json::to_value(bounds).unwrap_or_default());
            }
        }

        // Add state hash if available
        if let Some(hash) = &factorio_obs.state_hash {
            data.insert("state_hash".to_string(), serde_json::json!(hash));
        }

        Observation::Custom(serde_json::Value::Object(data))
    }

    /// Extract reward and done flag for an agent
    pub fn get_step_info(factorio_obs: &FactorioObservation, agent_id: &str) -> (f64, bool, HashMap<String, f64>) {
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
            fs::write(&path, "")
                .await
                .map_err(|e| GameRLError::IpcError(format!("Failed to clear observation: {}", e)))?;
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
            .map_err(|e| GameRLError::IpcError(format!("Failed to create observation dir: {}", e)))?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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
}
