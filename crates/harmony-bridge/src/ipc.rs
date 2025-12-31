//! IPC communication with .NET games

use crate::protocol::{GameCapabilities, GameMessage, StepResultPayload, deserialize, serialize};
use async_trait::async_trait;
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    Result, StepResult, StreamDescriptor,
};
use game_rl_server::environment::StateUpdate;
use game_rl_server::GameEnvironment;
use std::collections::HashMap;
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::{Mutex, broadcast, mpsc, oneshot};
use tracing::{debug, error, info, warn};

/// Bridge to a .NET game via IPC
pub struct HarmonyBridge {
    /// Path to the socket/pipe
    socket_path: String,
    /// Writer half of the connection
    writer: Arc<Mutex<Option<Box<dyn AsyncWriter>>>>,
    /// Channel to send requests to the reader task
    request_tx: mpsc::Sender<(GameMessage, oneshot::Sender<Result<GameMessage>>)>,
    /// Broadcast channel for pushed state updates
    event_tx: broadcast::Sender<StateUpdate>,
    /// Game capabilities received during Ready
    capabilities: Option<GameCapabilities>,
    /// Game name
    game_name: String,
    /// Game version
    game_version: String,
    /// Background reader task handle
    _reader_handle: Option<tokio::task::JoinHandle<()>>,
}

/// Trait for async reading
#[async_trait]
trait AsyncReader: Send {
    async fn read_message(&mut self) -> Result<Vec<u8>>;
}

/// Trait for async writing
#[async_trait]
trait AsyncWriter: Send + Sync {
    async fn write_message(&mut self, data: &[u8]) -> Result<()>;
}

impl HarmonyBridge {
    /// Create a new bridge (not connected yet)
    pub fn new(socket_path: &str) -> Self {
        // Create channels
        let (request_tx, _request_rx) = mpsc::channel(16);
        let (event_tx, _) = broadcast::channel(64);

        Self {
            socket_path: socket_path.to_string(),
            writer: Arc::new(Mutex::new(None)),
            request_tx,
            event_tx,
            capabilities: None,
            game_name: "Unknown".into(),
            game_version: "0.0.0".into(),
            _reader_handle: None,
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

            // Split into read/write halves
            let (read_half, write_half) = stream.into_split();

            // Store writer
            {
                let mut guard = self.writer.lock().await;
                *guard = Some(Box::new(UnixWriteWrapper(write_half)));
            }

            // Create new channels for this connection
            let (request_tx, request_rx) = mpsc::channel(16);
            self.request_tx = request_tx;

            // Spawn background reader task
            let event_tx = self.event_tx.clone();
            let handle = tokio::spawn(reader_task(
                UnixReadWrapper(read_half),
                request_rx,
                event_tx,
            ));
            self._reader_handle = Some(handle);
        }

        #[cfg(windows)]
        {
            return Err(GameRLError::IpcError(
                "Windows named pipes not yet implemented".into(),
            ));
        }

        // Wait for Ready message by sending a dummy request that expects Ready
        // The reader task will handle routing the response
        let (response_tx, response_rx) = oneshot::channel();
        // Send an empty marker - the first message from the game is Ready
        self.request_tx
            .send((GameMessage::GetStateHash, response_tx)) // Dummy, reader handles Ready specially
            .await
            .map_err(|_| GameRLError::IpcError("Failed to send to reader task".into()))?;

        // Wait for Ready response
        let msg = response_rx
            .await
            .map_err(|_| GameRLError::IpcError("Reader task died".into()))??;

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
            _ => Err(GameRLError::ProtocolError(format!(
                "Expected Ready message, got {:?}",
                msg
            ))),
        }
    }

    /// Send a message and wait for response
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        // Log outgoing message
        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        debug!("[Rust→C#] len={} json={}", data.len(), json_preview);

        // Send through writer
        {
            let mut guard = self.writer.lock().await;
            let writer = guard
                .as_mut()
                .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
            writer.write_message(&data).await?;
        }

        // Wait for response via channel
        let (response_tx, response_rx) = oneshot::channel();
        self.request_tx
            .send((msg, response_tx))
            .await
            .map_err(|_| GameRLError::IpcError("Reader task not running".into()))?;

        response_rx
            .await
            .map_err(|_| GameRLError::IpcError("Reader task died waiting for response".into()))?
    }

    /// Send a message without waiting for response (fire-and-forget)
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        debug!("[Rust→C#] len={} json={}", data.len(), json_preview);

        let mut guard = self.writer.lock().await;
        let writer = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
        writer.write_message(&data).await
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
        let mut guard = self.writer.lock().await;
        *guard = None;
        Ok(())
    }

    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        Some(self.event_tx.subscribe())
    }
}

/// Background reader task that handles incoming messages
async fn reader_task<R: AsyncReader>(
    mut reader: R,
    mut request_rx: mpsc::Receiver<(GameMessage, oneshot::Sender<Result<GameMessage>>)>,
    event_tx: broadcast::Sender<StateUpdate>,
) {
    // Queue of pending response channels (FIFO - responses come in order)
    let mut pending: Vec<oneshot::Sender<Result<GameMessage>>> = Vec::new();

    loop {
        tokio::select! {
            // New request from main task
            req = request_rx.recv() => {
                match req {
                    Some((_msg, response_tx)) => {
                        pending.push(response_tx);
                    }
                    None => {
                        // Channel closed, exit
                        debug!("Request channel closed, reader task exiting");
                        break;
                    }
                }
            }

            // Message from game
            msg_result = reader.read_message() => {
                match msg_result {
                    Ok(data) => {
                        // Log incoming message
                        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
                        debug!("[C#→Rust] len={} json={}", data.len(), json_preview);

                        match deserialize(&data) {
                            Ok(msg) => {
                                match msg {
                                    // Push notification - broadcast to subscribers
                                    GameMessage::StateUpdate { tick, state, events } => {
                                        let update = StateUpdate {
                                            tick,
                                            state,
                                            events,
                                        };
                                        // Ignore send errors (no subscribers)
                                        let _ = event_tx.send(update);
                                    }

                                    // Response to a pending request
                                    _ => {
                                        if let Some(response_tx) = pending.pop() {
                                            let _ = response_tx.send(Ok(msg));
                                        } else {
                                            warn!("Received response but no pending request: {:?}", msg);
                                        }
                                    }
                                }
                            }
                            Err(e) => {
                                error!("Failed to deserialize message: {}", e);
                                // Send error to pending request if any
                                if let Some(response_tx) = pending.pop() {
                                    let _ = response_tx.send(Err(GameRLError::SerializationError(e.to_string())));
                                }
                            }
                        }
                    }
                    Err(e) => {
                        error!("Reader task failed: {}", e);
                        // Notify all pending requests of failure
                        for response_tx in pending.drain(..) {
                            let _ = response_tx.send(Err(GameRLError::IpcError("Connection lost".into())));
                        }
                        break;
                    }
                }
            }
        }
    }
}

// Unix read wrapper (for split stream)
#[cfg(unix)]
struct UnixReadWrapper(tokio::net::unix::OwnedReadHalf);

#[cfg(unix)]
#[async_trait]
impl AsyncReader for UnixReadWrapper {
    async fn read_message(&mut self) -> Result<Vec<u8>> {
        // No timeout - wait indefinitely for messages.
        // Dead connections are detected via socket close/EOF.
        // This supports both request/response and push events.

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
}

// Unix write wrapper (for split stream)
#[cfg(unix)]
struct UnixWriteWrapper(tokio::net::unix::OwnedWriteHalf);

#[cfg(unix)]
#[async_trait]
impl AsyncWriter for UnixWriteWrapper {
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
