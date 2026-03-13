//! # game-rl-server
//!
//! MCP server implementation for the Game-RL protocol.
//!
//! This crate provides:
//! - `GameEnvironment` trait for implementing game adapters
//! - MCP JSON-RPC protocol handling
//! - Agent registry and lifecycle management
//! - Tool implementations (sim_step, reset, etc.)

pub mod environment;
pub mod handler;
pub mod mcp;
pub mod registry;
pub mod tools;
pub mod transport;

pub use environment::{GameEnvironment, StateUpdate};
pub use mcp::Notification;
pub use registry::AgentRegistry;

use game_rl_core::{GameManifest, Result};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::RwLock;
use tracing::debug;

/// Game-RL MCP server
pub struct GameRLServer<E: GameEnvironment> {
    /// Game environment implementation
    environment: Arc<RwLock<E>>,
    /// Agent registry
    registry: Arc<RwLock<AgentRegistry>>,
    /// Game manifest
    manifest: GameManifest,
    /// Background observation refresh task
    _refresh_handle: Option<tokio::task::JoinHandle<()>>,
}

impl<E: GameEnvironment> GameRLServer<E> {
    /// Create a new server with the given environment.
    /// Spawns a background task that periodically calls observe() to keep the cache fresh.
    pub fn new(environment: E, manifest: GameManifest) -> Self {
        let environment = Arc::new(RwLock::new(environment));
        let registry = Arc::new(RwLock::new(AgentRegistry::new(
            manifest.capabilities.max_agents,
        )));

        // Spawn background observation refresh
        let env_ref = environment.clone();
        let refresh_handle = tokio::spawn(async move {
            // Wait for agents to register before starting refresh
            tokio::time::sleep(Duration::from_secs(5)).await;

            let mut interval = tokio::time::interval(Duration::from_millis(500));
            loop {
                interval.tick().await;
                // Try to observe — this refreshes the bridge's internal cache
                let result = {
                    let mut env = env_ref.write().await;
                    env.observe().await
                };
                match result {
                    Ok(obs) => {
                        debug!("Cache refreshed (tick={})", obs.tick);
                    }
                    Err(e) => {
                        // Don't spam logs — observe may not be supported
                        debug!("Cache refresh skipped: {}", e);
                        // Back off if observe isn't supported
                        tokio::time::sleep(Duration::from_secs(5)).await;
                    }
                }
            }
        });

        Self {
            environment,
            registry,
            manifest,
            _refresh_handle: Some(refresh_handle),
        }
    }

    /// Run the server on stdio transport
    pub async fn run_stdio(self) -> Result<()> {
        transport::stdio::run(self).await
    }

    /// Run the server on Streamable HTTP transport
    #[cfg(feature = "http")]
    pub async fn run_http(self, addr: std::net::SocketAddr) -> Result<()> {
        transport::http::run(self, addr).await
    }

    /// Get the game manifest
    pub fn manifest(&self) -> &GameManifest {
        &self.manifest
    }

    /// Get a reference to the environment
    pub fn environment(&self) -> &Arc<RwLock<E>> {
        &self.environment
    }

    /// Get a reference to the agent registry
    pub fn registry(&self) -> &Arc<RwLock<AgentRegistry>> {
        &self.registry
    }
}
