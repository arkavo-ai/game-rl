//! Bridge to Project Zomboid via file-based IPC
//!
//! Uses file system for communication since PZ's Lua is sandboxed
//! and doesn't have socket access.

use game_bridge::{GameCapabilities, GameMessage, StepResultPayload};
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    Result, StepResult, StreamDescriptor,
};
use game_rl_server::environment::StateUpdate;
use game_rl_server::GameEnvironment;
use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;
use tokio::fs;
use tokio::sync::broadcast;
use tokio::time::sleep;
use tracing::{debug, info, warn};

/// Configuration for Zomboid bridge
#[derive(Debug, Clone)]
pub struct ZomboidConfig {
    /// Base path for IPC files (default: ~/Zomboid/gamerl/)
    pub ipc_path: PathBuf,
    /// Timeout waiting for game response
    pub response_timeout: Duration,
    /// Poll interval for file changes
    pub poll_interval: Duration,
}

impl Default for ZomboidConfig {
    fn default() -> Self {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_else(|_| "/tmp".to_string());

        Self {
            // PZ Lua uses ~/Zomboid/Lua/ for file I/O
            ipc_path: PathBuf::from(home).join("Zomboid").join("Lua"),
            response_timeout: Duration::from_secs(30),
            poll_interval: Duration::from_millis(50),
        }
    }
}

/// Bridge to Project Zomboid via file-based IPC
pub struct ZomboidBridge {
    /// Connection configuration
    config: ZomboidConfig,
    /// Path to command file (Rust writes, Lua reads)
    command_file: PathBuf,
    /// Path to response file (Lua writes, Rust reads)
    response_file: PathBuf,
    /// Path to status file
    status_file: PathBuf,
    /// Whether connected
    connected: bool,
    /// Broadcast channel for pushed state updates
    event_tx: broadcast::Sender<StateUpdate>,
    /// Game capabilities received during Ready
    capabilities: Option<GameCapabilities>,
    /// Game name
    game_name: String,
    /// Game version
    game_version: String,
}

impl ZomboidBridge {
    /// Create a new bridge with default configuration
    pub fn new() -> Self {
        Self::with_config(ZomboidConfig::default())
    }

    /// Create a new bridge with custom configuration
    pub fn with_config(config: ZomboidConfig) -> Self {
        // Use flat files with gamerl_ prefix (PZ Lua can't read subdirectories)
        let command_file = config.ipc_path.join("gamerl_command.json");
        let response_file = config.ipc_path.join("gamerl_response.json");
        let status_file = config.ipc_path.join("gamerl_status.json");
        let (event_tx, _) = broadcast::channel(64);

        Self {
            config,
            command_file,
            response_file,
            status_file,
            connected: false,
            event_tx,
            capabilities: None,
            game_name: "Project Zomboid".into(),
            game_version: "0.0.0".into(),
        }
    }

    /// Initialize IPC directory and wait for game to connect
    pub async fn init(&mut self) -> Result<()> {
        // Create IPC directory
        fs::create_dir_all(&self.config.ipc_path)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to create IPC directory: {}", e)))?;

        info!("IPC directory: {:?}", self.config.ipc_path);

        // Wait for Lua to create the status file first (PZ sandbox requirement)
        info!("Waiting for PZ Lua to create IPC files...");
        info!("Start the game with GameRL mod enabled, then load/start a game");

        let mut last_log = std::time::Instant::now();
        loop {
            if self.status_file.exists() {
                info!("Status file found, writing ready signal...");
                break;
            }
            if last_log.elapsed() > std::time::Duration::from_secs(10) {
                info!("Still waiting for PZ to create {:?}...", self.status_file);
                last_log = std::time::Instant::now();
            }
            sleep(self.config.poll_interval).await;
        }

        // Clear command file if exists, write status
        let _ = fs::write(&self.command_file, "").await;
        let status = r#"{"status":"ready","version":"0.5.0"}"#;
        fs::write(&self.status_file, status)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to write status file: {}", e)))?;

        info!("Waiting for Ready message from game...");

        // Wait for Ready message from game (no timeout - game may take a while to start)
        let ready_msg = self.wait_for_initial_response().await?;

        match ready_msg {
            GameMessage::Ready {
                name,
                version,
                capabilities,
            } => {
                info!("Game connected: {} v{}", name, version);
                self.game_name = name;
                self.game_version = version;
                self.capabilities = Some(capabilities);
                self.connected = true;
                Ok(())
            }
            _ => Err(GameRLError::ProtocolError(format!(
                "Expected Ready message, got {:?}",
                ready_msg
            ))),
        }
    }

    /// Send a command and wait for response
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        if !self.connected {
            return Err(GameRLError::IpcError("Not connected".into()));
        }

        // Serialize message
        let json = serde_json::to_string(&msg)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        debug!("[Rust→PZ] {}", &json[..json.len().min(200)]);

        // Write command file
        fs::write(&self.command_file, &json)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to write command: {}", e)))?;

        // Wait for response
        self.wait_for_response().await
    }

    /// Wait for a response in the response file
    /// If `initial_wait` is true, waits indefinitely (for game startup)
    async fn wait_for_response_impl(&self, initial_wait: bool) -> Result<GameMessage> {
        let start = std::time::Instant::now();
        let mut last_log = std::time::Instant::now();

        loop {
            // For normal requests, use timeout; for initial connection, wait forever
            if !initial_wait && start.elapsed() > self.config.response_timeout {
                return Err(GameRLError::IpcError("Response timeout".into()));
            }

            // Periodic status logging during initial wait
            if initial_wait && last_log.elapsed() > Duration::from_secs(10) {
                info!("Still waiting for Project Zomboid... (start/load a game, press F11)");
                last_log = std::time::Instant::now();
            }

            // Try to read response file
            match fs::read_to_string(&self.response_file).await {
                Ok(content) if !content.is_empty() => {
                    // Clear response file
                    let _ = fs::write(&self.response_file, "").await;

                    debug!("[PZ→Rust] {}", &content[..content.len().min(200)]);

                    // Parse JSON
                    let msg: GameMessage = serde_json::from_str(&content)
                        .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

                    return Ok(msg);
                }
                _ => {
                    // Wait and retry
                    sleep(self.config.poll_interval).await;
                }
            }
        }
    }

    /// Wait for a response (with timeout for normal operations)
    async fn wait_for_response(&self) -> Result<GameMessage> {
        self.wait_for_response_impl(false).await
    }

    /// Wait for initial connection (no timeout, for game startup)
    async fn wait_for_initial_response(&self) -> Result<GameMessage> {
        self.wait_for_response_impl(true).await
    }

    /// Send a message without waiting for response
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        if !self.connected {
            return Err(GameRLError::IpcError("Not connected".into()));
        }

        let json = serde_json::to_string(&msg)
            .map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        debug!("[Rust→PZ] {}", &json[..json.len().min(200)]);

        fs::write(&self.command_file, &json)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Failed to write command: {}", e)))?;

        Ok(())
    }

    /// Get game manifest from capabilities
    pub fn manifest(&self) -> GameManifest {
        let caps = self.capabilities.clone().unwrap_or(GameCapabilities {
            multi_agent: true,
            max_agents: 4,
            deterministic: false,
            headless: false,
        });

        GameManifest {
            name: self.game_name.clone(),
            version: self.game_version.clone(),
            game_rl_version: "0.5.0".into(),
            capabilities: game_rl_core::Capabilities {
                multi_agent: caps.multi_agent,
                max_agents: caps.max_agents,
                deterministic: caps.deterministic,
                headless: caps.headless,
                ..Default::default()
            },
            ..Default::default()
        }
    }
}

impl Default for ZomboidBridge {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait::async_trait]
impl GameEnvironment for ZomboidBridge {
    async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    ) -> Result<AgentManifest> {
        let response = self
            .request(GameMessage::RegisterAgent {
                agent_id: agent_id.clone(),
                agent_type: agent_type.clone(),
                config,
            })
            .await?;

        match response {
            GameMessage::AgentRegistered {
                agent_id,
                observation_space,
                action_space,
            } => Ok(AgentManifest {
                agent_id,
                agent_type,
                observation_space,
                action_space,
                reward_components: vec![],
            }),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn deregister_agent(&mut self, agent_id: &AgentId) -> Result<()> {
        self.send(GameMessage::DeregisterAgent {
            agent_id: agent_id.clone(),
        })
        .await
    }

    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult> {
        let response = self
            .request(GameMessage::ExecuteAction {
                agent_id: agent_id.clone(),
                action,
                ticks,
            })
            .await?;

        fn build_step_result(payload: StepResultPayload) -> StepResult {
            StepResult {
                agent_id: payload.agent_id,
                step_id: 0,
                tick: 0,
                observation: payload.observation,
                reward: payload.reward,
                reward_components: payload.reward_components,
                done: payload.done,
                truncated: payload.truncated,
                termination_reason: None,
                events: vec![],
                frame_ids: HashMap::new(),
                available_actions: None,
                metrics: None,
                state_hash: payload.state_hash,
            }
        }

        match response {
            GameMessage::StepResult { result } => Ok(build_step_result(result)),
            GameMessage::BatchStepResult { results } => results
                .into_iter()
                .find(|result| &result.agent_id == agent_id)
                .map(build_step_result)
                .ok_or_else(|| {
                    GameRLError::ProtocolError("BatchStepResult missing requested agent".into())
                }),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<Observation> {
        let response = self.request(GameMessage::Reset { seed, scenario }).await?;

        match response {
            GameMessage::ResetComplete { observation, .. } => Ok(observation),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn state_hash(&mut self) -> Result<String> {
        let response = self.request(GameMessage::GetStateHash).await?;

        match response {
            GameMessage::StateHash { hash } => Ok(hash),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn configure_streams(
        &mut self,
        agent_id: &AgentId,
        profile: &str,
    ) -> Result<Vec<StreamDescriptor>> {
        let response = self
            .request(GameMessage::ConfigureStreams {
                agent_id: agent_id.clone(),
                profile: profile.to_string(),
            })
            .await?;

        match response {
            GameMessage::StreamsConfigured {
                agent_id: response_agent_id,
                descriptors,
            } => {
                if &response_agent_id != agent_id {
                    warn!(
                        "StreamsConfigured agent mismatch: expected {}, got {}",
                        agent_id, response_agent_id
                    );
                }
                Ok(descriptors)
            }
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn save_trajectory(&self, _path: &str) -> Result<()> {
        Err(GameRLError::GameError(
            "Trajectory saving not implemented".into(),
        ))
    }

    async fn load_trajectory(&mut self, _path: &str) -> Result<()> {
        Err(GameRLError::GameError(
            "Trajectory loading not implemented".into(),
        ))
    }

    async fn shutdown(&mut self) -> Result<()> {
        self.send(GameMessage::Shutdown).await?;
        self.connected = false;

        // Clean up status file
        let _ = fs::remove_file(&self.status_file).await;

        Ok(())
    }

    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        Some(self.event_tx.subscribe())
    }
}
