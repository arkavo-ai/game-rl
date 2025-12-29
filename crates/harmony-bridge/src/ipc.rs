//! IPC communication with .NET games

use crate::protocol::{GameCapabilities, GameMessage, StepResultPayload, deserialize, serialize};
use async_trait::async_trait;
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    Result, StepResult, StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::Mutex;
use tokio::time::sleep;
use tracing::{info, warn};

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
        self.connect_internal().await
    }

    /// Internal connect implementation
    async fn connect_internal(&mut self) -> Result<()> {
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
        let msg = self.recv_internal().await?;
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

    /// Check if connected
    async fn is_connected(&self) -> bool {
        let guard = self.stream.lock().await;
        guard.is_some()
    }

    /// Disconnect (clear the stream)
    async fn disconnect(&self) {
        let mut guard = self.stream.lock().await;
        *guard = None;
    }

    /// Ensure we're connected, attempting reconnection if needed
    async fn ensure_connected(&mut self) -> Result<()> {
        if self.is_connected().await {
            return Ok(());
        }

        // Try to reconnect with exponential backoff
        let max_attempts = 5;
        let mut delay = Duration::from_millis(100);

        for attempt in 1..=max_attempts {
            warn!("Connection lost, attempting reconnect ({}/{})", attempt, max_attempts);

            match self.connect_internal().await {
                Ok(()) => {
                    info!("Reconnected successfully");
                    return Ok(());
                }
                Err(e) => {
                    if attempt < max_attempts {
                        warn!("Reconnect failed: {}, retrying in {:?}", e, delay);
                        sleep(delay).await;
                        delay = std::cmp::min(delay * 2, Duration::from_secs(5));
                    } else {
                        return Err(e);
                    }
                }
            }
        }

        Err(GameRLError::IpcError("Failed to reconnect after max attempts".into()))
    }

    /// Send a message to the game (internal, no reconnection)
    async fn send_internal(&self, msg: &GameMessage) -> Result<()> {
        let data = serialize(msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        // Diagnostic logging
        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        info!("[Rust→C#] len={} json={}", data.len(), json_preview);

        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;

        stream.write_message(&data).await
    }

    /// Receive a message from the game (internal, no reconnection)
    async fn recv_internal(&self) -> Result<GameMessage> {
        let mut guard = self.stream.lock().await;
        let stream = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;

        let data = stream.read_message().await?;

        // Diagnostic logging - show raw bytes and JSON
        let first_bytes: Vec<u8> = data.iter().take(20).cloned().collect();
        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        info!("[C#→Rust] len={} first_bytes={:?} json={}", data.len(), first_bytes, json_preview);

        deserialize(&data).map_err(|e| {
            warn!("[C#→Rust] Deserialize failed: {}", e);
            GameRLError::SerializationError(e.to_string())
        })
    }

    /// Send a message to the game with reconnection support
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        // Try to send, reconnect on failure
        if let Err(e) = self.send_internal(&msg).await {
            warn!("Send failed: {}, attempting reconnect", e);
            self.disconnect().await;
            self.ensure_connected().await?;
            self.send_internal(&msg).await
        } else {
            Ok(())
        }
    }

    /// Receive a message from the game with reconnection support
    /// Used for async notifications (state updates, events) - not yet implemented in protocol
    #[allow(dead_code)]
    async fn recv(&mut self) -> Result<GameMessage> {
        // Try to receive, reconnect on failure
        match self.recv_internal().await {
            Ok(msg) => Ok(msg),
            Err(e) => {
                warn!("Recv failed: {}, attempting reconnect", e);
                self.disconnect().await;
                self.ensure_connected().await?;
                self.recv_internal().await
            }
        }
    }

    /// Send and wait for response with reconnection support
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        // Try the full request, reconnect on any failure
        match self.request_internal(&msg).await {
            Ok(response) => Ok(response),
            Err(e) => {
                warn!("Request failed: {}, attempting reconnect", e);
                self.disconnect().await;
                self.ensure_connected().await?;
                self.request_internal(&msg).await
            }
        }
    }

    /// Send and wait for response (internal, no reconnection)
    async fn request_internal(&self, msg: &GameMessage) -> Result<GameMessage> {
        self.send_internal(msg).await?;
        self.recv_internal().await
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

        fn build_step_result(payload: StepResultPayload) -> StepResult {
            StepResult {
                agent_id: payload.agent_id,
                step_id: 0, // TODO: track step count
                tick: 0,    // TODO: track tick
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
        use tokio::time::timeout;

        // Timeout for IPC reads - 120 seconds to allow for large tick counts
        const READ_TIMEOUT: Duration = Duration::from_secs(120);

        // Read length-prefixed message
        let mut len_bytes = [0u8; 4];
        timeout(READ_TIMEOUT, self.0.read_exact(&mut len_bytes))
            .await
            .map_err(|_| GameRLError::IpcError("Read timeout (120s) - game may be processing large tick count".into()))?
            .map_err(|e| GameRLError::IpcError(format!("Read length failed: {}", e)))?;
        let len = u32::from_le_bytes(len_bytes) as usize;

        let mut data = vec![0u8; len];
        timeout(READ_TIMEOUT, self.0.read_exact(&mut data))
            .await
            .map_err(|_| GameRLError::IpcError("Read timeout (120s) - game may be processing large tick count".into()))?
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
