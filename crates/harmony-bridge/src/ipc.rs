//! IPC communication with .NET games

use crate::protocol::{GameCapabilities, GameMessage, deserialize, serialize};
use async_trait::async_trait;
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    Result, StepResult, StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::Mutex;
use tracing::info;

/// Bridge to a .NET game via IPC
pub struct HarmonyBridge {
    /// Path to the socket/pipe
    socket_path: String,
    /// Connection stream (wrapped for async access)
    stream: Arc<Mutex<Option<Box<dyn AsyncStream>>>>,
    /// Game capabilities received during Ready
    capabilities: Option<GameCapabilities>,
    /// Game name
    game_name: String,
    /// Game version
    game_version: String,
}

/// Trait for async read/write streams
#[async_trait]
trait AsyncStream: Send + Sync {
    async fn read_message(&mut self) -> Result<Vec<u8>>;
    async fn write_message(&mut self, data: &[u8]) -> Result<()>;
}

impl HarmonyBridge {
    /// Create a new bridge (not connected yet)
    pub fn new(socket_path: &str) -> Self {
        Self {
            socket_path: socket_path.to_string(),
            stream: Arc::new(Mutex::new(None)),
            capabilities: None,
            game_name: "Unknown".into(),
            game_version: "0.0.0".into(),
        }
    }

    /// Connect to the game process
    pub async fn connect(&mut self) -> Result<()> {
        info!("Connecting to game at {}", self.socket_path);

        // Platform-specific connection
        #[cfg(unix)]
        {
            use tokio::net::UnixStream;
            let stream = UnixStream::connect(&self.socket_path)
                .await
                .map_err(|e| GameRLError::IpcError(format!("Failed to connect: {}", e)))?;

            let mut guard = self.stream.lock().await;
            *guard = Some(Box::new(UnixStreamWrapper(stream)));
        }

        #[cfg(windows)]
        {
            // Windows named pipe support would go here
            return Err(GameRLError::IpcError(
                "Windows named pipes not yet implemented".into(),
            ));
        }

        // Wait for Ready message
        let msg = self.recv().await?;
        match msg {
            GameMessage::Ready {
                name,
                version,
                capabilities,
            } => {
                info!("Connected to {} v{}", name, version);
                self.game_name = name;
                self.game_version = version;
                self.capabilities = Some(capabilities);
                Ok(())
            }
            _ => Err(GameRLError::ProtocolError("Expected Ready message".into())),
        }
    }

    /// Send a message to the game
    async fn send(&self, msg: GameMessage) -> Result<()> {
        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;

        stream.write_message(&data).await
    }

    /// Receive a message from the game
    async fn recv(&self) -> Result<GameMessage> {
        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;

        let data = stream.read_message().await?;
        deserialize(&data).map_err(|e| GameRLError::SerializationError(e.to_string()))
    }

    /// Send and wait for response
    async fn request(&self, msg: GameMessage) -> Result<GameMessage> {
        self.send(msg).await?;
        self.recv().await
    }

    /// Get game manifest from capabilities
    pub fn manifest(&self) -> GameManifest {
        let caps = self.capabilities.clone().unwrap_or(GameCapabilities {
            multi_agent: false,
            max_agents: 1,
            deterministic: false,
            headless: false,
        });

        GameManifest {
            name: self.game_name.clone(),
            version: self.game_version.clone(),
            game_rl_version: "1.0.0".into(),
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

#[async_trait]
impl GameEnvironment for HarmonyBridge {
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

        match response {
            GameMessage::StepResult {
                agent_id,
                observation,
                reward,
                reward_components,
                done,
                truncated,
                state_hash,
            } => Ok(StepResult {
                agent_id,
                step_id: 0, // TODO: track step count
                tick: 0,    // TODO: track tick
                observation,
                reward,
                reward_components,
                done,
                truncated,
                termination_reason: None,
                events: vec![],
                frame_ids: HashMap::new(),
                available_actions: None,
                metrics: None,
                state_hash,
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

    async fn state_hash(&self) -> Result<String> {
        // Need to use interior mutability for this const method
        // For now, return placeholder
        Ok("sha256:0000000000000000000000000000000000000000000000000000000000000000".into())
    }

    async fn configure_streams(
        &mut self,
        _agent_id: &AgentId,
        _profile: &str,
    ) -> Result<Vec<StreamDescriptor>> {
        // Vision streams not yet implemented for Harmony bridge
        Ok(vec![])
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
        let mut guard = self.stream.lock().await;
        *guard = None;
        Ok(())
    }
}

// Unix stream wrapper
#[cfg(unix)]
struct UnixStreamWrapper(tokio::net::UnixStream);

#[cfg(unix)]
#[async_trait]
impl AsyncStream for UnixStreamWrapper {
    async fn read_message(&mut self) -> Result<Vec<u8>> {
        // Read length-prefixed message
        let mut len_bytes = [0u8; 4];
        self.0
            .read_exact(&mut len_bytes)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Read length failed: {}", e)))?;
        let len = u32::from_le_bytes(len_bytes) as usize;

        let mut data = vec![0u8; len];
        self.0
            .read_exact(&mut data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Read data failed: {}", e)))?;

        Ok(data)
    }

    async fn write_message(&mut self, data: &[u8]) -> Result<()> {
        let len = (data.len() as u32).to_le_bytes();
        self.0
            .write_all(&len)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Write length failed: {}", e)))?;
        self.0
            .write_all(data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Write data failed: {}", e)))?;
        self.0
            .flush()
            .await
            .map_err(|e| GameRLError::IpcError(format!("Flush failed: {}", e)))?;
        Ok(())
    }
}
