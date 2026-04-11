//! IPC communication with .NET games

use async_trait::async_trait;
#[cfg(unix)]
use game_bridge::reader_task;
#[cfg(unix)]
use game_bridge::unix::{UnixReadWrapper, UnixWriteWrapper};
use game_bridge::{AsyncWriter, GameCapabilities, GameMessage, StepResultPayload, serialize};
use game_rl_core::{
    Action, ActionSpace, AgentConfig, AgentId, AgentManifest, AgentType, EpisodeSummary,
    GameManifest, GameRLError, Observation, Result, StepResult, StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use game_rl_server::environment::StateUpdate;
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;
use tokio::sync::{Mutex, broadcast, mpsc, oneshot, watch};
use tracing::{info, trace, warn};

/// Cached observation snapshot
#[derive(Debug, Clone)]
pub struct CachedObservation {
    pub result: StepResult,
    pub cached_at: Instant,
}

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
    /// Action space learned from agent registration
    action_space: Option<ActionSpace>,
    /// Observation cache — written by observe(), read by step()
    cache_tx: watch::Sender<Option<CachedObservation>>,
    /// Receiver for cache reads (cloneable for external consumers)
    cache_rx: watch::Receiver<Option<CachedObservation>>,
    /// True when a reset triggered a game teardown and we're waiting for reconnection
    restarting: bool,
}

impl HarmonyBridge {
    /// Create a new bridge (not connected yet)
    pub fn new(socket_path: &str) -> Self {
        // Create channels
        let (request_tx, _request_rx) = mpsc::channel(16);
        let (event_tx, _) = broadcast::channel(64);
        let (cache_tx, cache_rx) = watch::channel(None);

        Self {
            socket_path: socket_path.to_string(),
            writer: Arc::new(Mutex::new(None)),
            request_tx,
            event_tx,
            capabilities: None,
            game_name: "Unknown".into(),
            game_version: "0.0.0".into(),
            _reader_handle: None,
            action_space: None,
            cache_tx,
            cache_rx,
            restarting: false,
        }
    }

    /// Get a receiver for the observation cache (used by background refresh tasks)
    pub fn cache_receiver(&self) -> watch::Receiver<Option<CachedObservation>> {
        self.cache_rx.clone()
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

        #[cfg(not(unix))]
        {
            Err(GameRLError::IpcError(
                "Only Unix sockets are supported (Windows named pipes not yet implemented)".into(),
            ))
        }

        #[cfg(unix)]
        {
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
    }

    /// Ensure the IPC connection is alive, reconnecting if needed.
    /// This is transparent to agents — they never see infrastructure errors
    /// unless the game is truly unreachable.
    async fn ensure_connected(&mut self) -> Result<()> {
        if !self.request_tx.is_closed() {
            return Ok(());
        }

        // During game restart after teardown, try once and return quickly.
        // The model should poll with observe until the game is back.
        if self.restarting {
            match self.connect().await {
                Ok(()) => {
                    info!("Game reconnected after restart");
                    self.restarting = false;
                    return Ok(());
                }
                Err(_) => {
                    return Err(GameRLError::IpcError(
                        "Game is still restarting after reset. Try again in a few seconds.".into(),
                    ));
                }
            }
        }

        warn!("Game IPC connection lost, attempting reconnect...");

        // Try reconnecting with brief backoff
        for attempt in 1..=3 {
            match self.connect().await {
                Ok(()) => {
                    info!("Reconnected to game successfully (attempt {})", attempt);
                    return Ok(());
                }
                Err(e) => {
                    if attempt < 3 {
                        warn!("Reconnect attempt {} failed: {}, retrying...", attempt, e);
                        tokio::time::sleep(std::time::Duration::from_millis(500 * attempt)).await;
                    } else {
                        return Err(GameRLError::IpcError(format!(
                            "Game not reachable after {} attempts. Is the game still running? Last error: {}",
                            attempt, e
                        )));
                    }
                }
            }
        }
        unreachable!()
    }

    /// Send a message and wait for response, reconnecting on IPC failure.
    async fn request(&mut self, msg: GameMessage) -> Result<GameMessage> {
        self.ensure_connected().await?;

        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        // Log outgoing message
        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        trace!("[Rust→C#] len={} json={}", data.len(), json_preview);

        // Use longer timeout for actions that trigger game reloads (LoadCheckpoint, Reset)
        let timeout_secs = match &msg {
            GameMessage::Reset { .. } => 120,
            GameMessage::ExecuteAction { action, .. } => {
                match action {
                    Action::Parameterized { action_type, .. } if action_type == "LoadCheckpoint" => 120,
                    _ => 30,
                }
            }
            _ => 30,
        };

        // Register response channel BEFORE sending to avoid race where
        // the game responds before the reader task has a pending channel
        let (response_tx, response_rx) = oneshot::channel();
        self.request_tx
            .send((msg, response_tx))
            .await
            .map_err(|_| GameRLError::IpcError("Reader task not running".into()))?;

        // Send through writer
        {
            let mut guard = self.writer.lock().await;
            let writer = guard
                .as_mut()
                .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
            writer.write_message(&data).await?;
        }

        match tokio::time::timeout(std::time::Duration::from_secs(timeout_secs), response_rx).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err(GameRLError::IpcError("Response channel closed: reader task may have died".into())),
            Err(_) => Err(GameRLError::IpcError(format!("Response timeout after {}s: game may be unresponsive", timeout_secs))),
        }
    }

    /// Send a message without waiting for response (fire-and-forget)
    async fn send(&mut self, msg: GameMessage) -> Result<()> {
        self.ensure_connected().await?;

        let data = serialize(&msg).map_err(|e| GameRLError::SerializationError(e.to_string()))?;

        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
        trace!("[Rust→C#] len={} json={}", data.len(), json_preview);

        let mut guard = self.writer.lock().await;
        let writer = guard
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("Not connected".into()))?;
        writer.write_message(&data).await
    }
}

fn build_step_result(payload: StepResultPayload) -> StepResult {
    // Extract tick from observation regardless of variant
    let tick = match &payload.observation {
        game_rl_core::Observation::Structured(map) => {
            map.get("Tick").and_then(|v| v.as_u64())
        }
        game_rl_core::Observation::Custom(val) => {
            val.get("Tick").and_then(|v| v.as_u64())
        }
        game_rl_core::Observation::Vector(_) => None,
    }.unwrap_or(0);
    trace!("build_step_result: observation variant={}, tick={}",
        match &payload.observation {
            game_rl_core::Observation::Structured(_) => "Structured",
            game_rl_core::Observation::Custom(_) => "Custom",
            game_rl_core::Observation::Vector(_) => "Vector",
        }, tick);
    StepResult {
        agent_id: payload.agent_id,
        step_id: 0,
        tick,
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
            } => {
                // Capture action space for manifest so validation works
                if self.action_space.is_none() {
                    if let Ok(space) = serde_json::from_value::<ActionSpace>(action_space.clone()) {
                        self.action_space = Some(space);
                    }
                }
                Ok(AgentManifest {
                    agent_id,
                    agent_type,
                    observation_space,
                    action_space,
                    reward_components: vec![],
                })
            }
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

    async fn observe(&mut self) -> Result<StepResult> {
        self.ensure_connected().await?;

        let response = self.request(GameMessage::Observe).await?;

        match response {
            GameMessage::StepResult { result } => {
                let step = build_step_result(result);
                // Update the cache
                let _ = self.cache_tx.send(Some(CachedObservation {
                    result: step.clone(),
                    cached_at: Instant::now(),
                }));
                Ok(step)
            }
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult> {
        self.ensure_connected().await?;

        // RequestFullState must bypass cache — it needs a fresh full observation from the game
        let is_full_state_request = matches!(&action, Action::Parameterized { action_type, .. }
            if action_type == "RequestFullState");

        // Check cache: if fresh AND caller requested no ticks, send action-only (ticks=0)
        // and return cached observation. Never use cache when ticks > 0 — the game must
        // actually advance, and CompleteStep() must run for reward accumulation.
        let cache_fresh = ticks == 0 && !is_full_state_request && self
            .cache_rx
            .borrow()
            .as_ref()
            .map(|c| c.cached_at.elapsed() < std::time::Duration::from_millis(500))
            .unwrap_or(false);

        if cache_fresh {
            // Action-only mode: send action with ticks=0, game executes but skips observation extraction
            let response = self
                .request(GameMessage::ExecuteAction {
                    agent_id: agent_id.clone(),
                    action,
                    ticks: 0,
                })
                .await?;

            // Handle errors from the action
            if let GameMessage::Error { code, message } = response {
                return Err(GameRLError::GameError(format!(
                    "Error {}: {}",
                    code, message
                )));
            }

            // Merge LastAction from the game's response into the cached observation
            let result = if let GameMessage::StepResult { result: payload } = response {
                let fresh = build_step_result(payload);
                let cached = self.cache_rx.borrow().clone().unwrap();
                let mut merged = cached.result;
                merged.agent_id = agent_id.clone();
                // Replace observation with the fresh one (contains LastAction)
                merged.observation = fresh.observation;
                merged
            } else {
                let cached = self.cache_rx.borrow().clone().unwrap();
                let mut r = cached.result;
                r.agent_id = agent_id.clone();
                r
            };
            return Ok(result);
        }

        // Cache stale — full step with observation
        let response = self
            .request(GameMessage::ExecuteAction {
                agent_id: agent_id.clone(),
                action,
                ticks,
            })
            .await?;

        match response {
            GameMessage::StepResult { result } => {
                let step = build_step_result(result);
                // Update cache
                let _ = self.cache_tx.send(Some(CachedObservation {
                    result: step.clone(),
                    cached_at: Instant::now(),
                }));
                Ok(step)
            }
            GameMessage::BatchStepResult { results } => {
                let now = Instant::now();
                let mut my_result = None;
                for payload in results {
                    let step = build_step_result(payload);
                    if &step.agent_id == agent_id {
                        // Update cache with this agent's observation
                        let _ = self.cache_tx.send(Some(CachedObservation {
                            result: step.clone(),
                            cached_at: now,
                        }));
                        my_result = Some(step);
                    }
                }
                my_result.ok_or_else(|| {
                    GameRLError::ProtocolError("BatchStepResult missing requested agent".into())
                })
            }
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError("Unexpected response".into())),
        }
    }

    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<Observation> {
        // Determine if this reset will tear down the game (checkpoint load or new colony)
        let will_teardown = scenario.as_ref().map_or(false, |s| {
            s.starts_with("new") || {
                // Check if it matches a save file name (LoadCheckpoint path)
                // Any non-empty scenario that isn't "new" attempts a checkpoint load
                !s.is_empty()
            }
        });

        let response = self
            .request(GameMessage::Reset {
                seed,
                scenario: scenario.clone(),
            })
            .await;

        match response {
            Ok(GameMessage::ResetComplete { observation, .. }) => Ok(observation),
            Ok(GameMessage::Error { code, message }) => Err(GameRLError::GameError(format!(
                "Error {}: {}",
                code, message
            ))),
            Ok(_) => Err(GameRLError::ProtocolError("Unexpected response".into())),
            Err(_) if will_teardown => {
                // Game teardown expected — socket died during reload.
                // Return immediately so the model isn't blocked. It should
                // poll with observe until the game finishes restarting.
                warn!("Reset triggered game teardown — returning immediately (poll with observe)");
                self.restarting = true;
                let _ = self.cache_tx.send(None);

                let mut obs = HashMap::new();
                obs.insert("Status".to_string(), serde_json::json!("Restarting"));
                obs.insert("Message".to_string(), serde_json::json!(
                    "Game is restarting after reset. Poll with observe tool until ready."
                ));
                Ok(Observation::Structured(obs))
            }
            Err(e) => Err(e),
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

    async fn episode_summary(&mut self) -> Result<EpisodeSummary> {
        let response = self.request(GameMessage::GetEpisodeSummary).await?;

        match response {
            GameMessage::EpisodeSummary {
                total_reward,
                step_count,
                ticks_elapsed,
                reward_breakdown,
                termination_reason,
            } => {
                let reason = termination_reason.map(|r| match r.as_str() {
                    "Success" => game_rl_core::observation::TerminationReason::Success,
                    "Failure" => game_rl_core::observation::TerminationReason::Failure,
                    "Timeout" => game_rl_core::observation::TerminationReason::Timeout,
                    _ => game_rl_core::observation::TerminationReason::External,
                });
                Ok(EpisodeSummary {
                    total_reward,
                    step_count,
                    ticks_elapsed,
                    reward_breakdown,
                    termination_reason: reason,
                })
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

    fn manifest(&self) -> GameManifest {
        let caps = self.capabilities.clone().unwrap_or(GameCapabilities {
            multi_agent: false,
            max_agents: 1,
            deterministic: false,
            headless: false,
        });

        GameManifest {
            name: self.game_name.clone(),
            version: self.game_version.clone(),
            game_rl_version: env!("CARGO_PKG_VERSION").into(),
            capabilities: game_rl_core::Capabilities {
                multi_agent: caps.multi_agent,
                max_agents: caps.max_agents,
                deterministic: caps.deterministic,
                headless: caps.headless,
                ..Default::default()
            },
            default_action_space: self.action_space.clone(),
            ..Default::default()
        }
    }

    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        Some(self.event_tx.subscribe())
    }
}
