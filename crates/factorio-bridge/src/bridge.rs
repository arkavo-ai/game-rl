//! Factorio bridge implementing GameEnvironment trait
//!
//! Uses RCON for commands and file-based IPC for observations.

use crate::observer::{ObservationReader, ObserverConfig};
use crate::rcon::RconClient;
use async_trait::async_trait;
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, Capabilities, GameManifest,
    GameRLError, Observation, Result, StepResult, StreamDescriptor,
};
use game_rl_server::environment::StateUpdate;
use game_rl_server::GameEnvironment;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::path::PathBuf;
use tokio::sync::broadcast;
use tokio::time::Duration;
use tracing::{debug, info};

/// Configuration for Factorio bridge
#[derive(Debug, Clone)]
pub struct FactorioConfig {
    /// RCON server address (host:port)
    pub rcon_address: String,
    /// RCON password
    pub rcon_password: String,
    /// Path to script-output/gamerl/ directory
    pub observation_dir: PathBuf,
    /// Timeout for operations
    pub timeout: Duration,
}

impl Default for FactorioConfig {
    fn default() -> Self {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_else(|_| "/tmp".to_string());

        Self {
            rcon_address: "127.0.0.1:27015".to_string(),
            rcon_password: "gamerl".to_string(),
            observation_dir: PathBuf::from(home)
                .join("Library/Application Support/factorio/script-output/gamerl"),
            timeout: Duration::from_secs(30),
        }
    }
}

impl FactorioConfig {
    /// Create config for Linux
    pub fn linux() -> Self {
        let home = std::env::var("HOME").unwrap_or_else(|_| "/tmp".to_string());
        Self {
            observation_dir: PathBuf::from(home).join(".factorio/script-output/gamerl"),
            ..Default::default()
        }
    }

    /// Create config with custom RCON settings
    pub fn with_rcon(address: String, password: String) -> Self {
        Self {
            rcon_address: address,
            rcon_password: password,
            ..Default::default()
        }
    }
}

/// Bridge to Factorio via RCON + file-based observation
pub struct FactorioBridge {
    /// Configuration
    config: FactorioConfig,
    /// RCON client for commands
    rcon: RconClient,
    /// Observation reader
    observer: ObservationReader,
    /// Whether connected
    connected: bool,
    /// Game version
    game_version: String,
    /// Registered agents
    agents: HashMap<AgentId, AgentType>,
    /// Event broadcast channel
    event_tx: broadcast::Sender<StateUpdate>,
}

impl FactorioBridge {
    /// Create a new bridge with default configuration
    pub fn new() -> Self {
        Self::with_config(FactorioConfig::default())
    }

    /// Create a new bridge with custom configuration
    pub fn with_config(config: FactorioConfig) -> Self {
        let rcon = RconClient::new(&config.rcon_address, &config.rcon_password);
        let observer_config = ObserverConfig::with_path(config.observation_dir.clone());
        let observer = ObservationReader::new(observer_config);
        let (event_tx, _) = broadcast::channel(64);

        Self {
            config,
            rcon,
            observer,
            connected: false,
            game_version: "2.0.0".to_string(),
            agents: HashMap::new(),
            event_tx,
        }
    }

    /// Initialize the bridge and connect to Factorio
    pub async fn init(&mut self) -> Result<()> {
        info!("Connecting to Factorio at {}", self.config.rcon_address);

        // Connect to RCON
        self.rcon.connect().await?;

        // Query game version (use rcon.print to get output)
        let version_response = self
            .rcon
            .lua("rcon.print(game.active_mods['base'])")
            .await?;
        if !version_response.is_empty() {
            self.game_version = version_response.trim().to_string();
        }

        // Check if GameRL mod is loaded
        let mod_check = self
            .rcon
            .lua("rcon.print(remote.interfaces['gamerl'] and 'ok' or 'no')")
            .await?;

        if !mod_check.contains("ok") {
            return Err(GameRLError::GameError(
                "GameRL mod not loaded in Factorio".to_string(),
            ));
        }

        // Initialize mod
        self.rcon.remote_call("gamerl", "init", "").await?;

        // Ensure observation directory exists
        self.observer.ensure_dir().await?;

        self.connected = true;
        info!("Connected to Factorio v{}", self.game_version);

        Ok(())
    }

    /// Check if connected
    pub fn is_connected(&self) -> bool {
        self.connected && self.rcon.is_connected()
    }

    /// Execute a Lua command that returns JSON
    async fn lua_json<T: serde::de::DeserializeOwned>(&self, lua: &str) -> Result<T> {
        let response = self.rcon.lua(lua).await?;
        serde_json::from_str(&response)
            .map_err(|e| GameRLError::SerializationError(format!("Failed to parse response: {}", e)))
    }
}

impl Default for FactorioBridge {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl GameEnvironment for FactorioBridge {
    async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    ) -> Result<AgentManifest> {
        info!("Registering agent {} as {:?}", agent_id, agent_type);

        let config_json = serde_json::to_string(&config)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        let lua = format!(
            r#"remote.call("gamerl", "register_agent", "{}", "{}", '{}')"#,
            agent_id,
            format!("{:?}", agent_type),
            config_json
        );

        let response = self.rcon.lua(&lua).await?;
        debug!("Register response: {}", response);

        self.agents.insert(agent_id.clone(), agent_type.clone());

        // Return agent manifest with default spaces (mod will refine)
        Ok(AgentManifest {
            agent_id,
            agent_type,
            observation_space: serde_json::json!({
                "type": "dict",
                "description": "Factorio game state observation"
            }),
            action_space: serde_json::json!({
                "type": "dict",
                "description": "Factorio action space"
            }),
            reward_components: vec![],
        })
    }

    async fn deregister_agent(&mut self, agent_id: &AgentId) -> Result<()> {
        info!("Deregistering agent {}", agent_id);

        let lua = format!(r#"remote.call("gamerl", "deregister_agent", "{}")"#, agent_id);
        self.rcon.lua(&lua).await?;

        self.agents.remove(agent_id);
        Ok(())
    }

    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult> {
        debug!("Step for agent {} with {} ticks", agent_id, ticks);

        // Serialize action
        let action_json = serde_json::to_string(&action)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        // Call step via RCON
        let lua = format!(
            r#"remote.call("gamerl", "step", "{}", '{}', {})"#,
            agent_id, action_json, ticks
        );
        self.rcon.lua(&lua).await?;

        // Wait for observation
        let obs = self.observer.wait_for_observation().await?;

        // Extract step result
        let observation = ObservationReader::to_observation(&obs, agent_id);
        let (reward, done, reward_components) = ObservationReader::get_step_info(&obs, agent_id);

        Ok(StepResult {
            agent_id: agent_id.clone(),
            step_id: obs.tick,
            tick: obs.tick,
            observation,
            reward,
            reward_components,
            done,
            truncated: false,
            termination_reason: None,
            events: vec![],
            frame_ids: HashMap::new(),
            available_actions: None,
            metrics: None,
            state_hash: obs.state_hash,
        })
    }

    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<Observation> {
        info!("Resetting environment (seed: {:?}, scenario: {:?})", seed, scenario);

        // Clear previous observation
        self.observer.clear().await?;

        // Call reset via RCON
        let seed_arg = seed.map(|s| s.to_string()).unwrap_or_else(|| "nil".to_string());
        let scenario_arg = scenario
            .map(|s| format!("\"{}\"", s))
            .unwrap_or_else(|| "nil".to_string());

        let lua = format!(
            r#"remote.call("gamerl", "reset", {}, {})"#,
            seed_arg, scenario_arg
        );
        self.rcon.lua(&lua).await?;

        // Wait for initial observation
        let obs = self.observer.wait_for_observation().await?;

        // Convert to generic observation (use first registered agent or empty)
        let agent_id = self.agents.keys().next().cloned().unwrap_or_default();
        Ok(ObservationReader::to_observation(&obs, &agent_id))
    }

    async fn state_hash(&mut self) -> Result<String> {
        // Get state hash from Factorio
        let lua = r#"remote.call("gamerl", "get_state_hash")"#;
        let response = self.rcon.lua(lua).await?;

        if response.is_empty() {
            // Compute hash ourselves from observation
            if let Some(obs) = self.observer.read_current().await? {
                let json = serde_json::to_string(&obs)
                    .map_err(|e| GameRLError::SerializationError(e.to_string()))?;
                let hash = Sha256::digest(json.as_bytes());
                return Ok(hex::encode(hash));
            }
            return Err(GameRLError::GameError("No state available".to_string()));
        }

        Ok(response.trim().to_string())
    }

    async fn configure_streams(
        &mut self,
        agent_id: &AgentId,
        profile: &str,
    ) -> Result<Vec<StreamDescriptor>> {
        info!("Configuring streams for {} with profile {}", agent_id, profile);

        let lua = format!(
            r#"remote.call("gamerl", "configure_streams", "{}", "{}")"#,
            agent_id, profile
        );
        self.rcon.lua(&lua).await?;

        // Factorio doesn't support vision streams (headless), return empty
        Ok(vec![])
    }

    async fn save_trajectory(&self, path: &str) -> Result<()> {
        info!("Saving trajectory to {}", path);

        let lua = format!(r#"remote.call("gamerl", "save_trajectory", "{}")"#, path);
        self.rcon.lua(&lua).await?;

        Ok(())
    }

    async fn load_trajectory(&mut self, path: &str) -> Result<()> {
        info!("Loading trajectory from {}", path);

        let lua = format!(r#"remote.call("gamerl", "load_trajectory", "{}")"#, path);
        self.rcon.lua(&lua).await?;

        Ok(())
    }

    async fn shutdown(&mut self) -> Result<()> {
        info!("Shutting down Factorio bridge");

        if self.connected {
            // Notify mod of shutdown
            let _ = self.rcon.remote_call("gamerl", "shutdown", "{}").await;
            self.rcon.disconnect().await;
        }

        self.connected = false;
        self.agents.clear();

        Ok(())
    }

    fn manifest(&self) -> GameManifest {
        GameManifest {
            name: "Factorio".to_string(),
            version: self.game_version.clone(),
            game_rl_version: "0.5.0".to_string(),
            capabilities: Capabilities {
                multi_agent: true,
                max_agents: 8,
                deterministic: true,
                headless: true,
                save_replay: true,
                domain_randomization: true,
                variable_timestep: true,
                ..Default::default()
            },
            ..Default::default()
        }
    }

    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        Some(self.event_tx.subscribe())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_config_defaults() {
        let config = FactorioConfig::default();
        assert_eq!(config.rcon_address, "127.0.0.1:27015");
        assert_eq!(config.rcon_password, "gamerl");
    }

    #[test]
    fn test_manifest() {
        let bridge = FactorioBridge::new();
        let manifest = bridge.manifest();

        assert_eq!(manifest.name, "Factorio");
        assert!(manifest.capabilities.deterministic);
        assert!(manifest.capabilities.headless);
        assert_eq!(manifest.capabilities.max_agents, 8);
    }
}
