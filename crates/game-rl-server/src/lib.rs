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
pub mod mcp;
pub mod registry;
pub mod tools;
pub mod transport;

pub use environment::GameEnvironment;
pub use registry::AgentRegistry;

use game_rl_core::{GameManifest, Result};
use std::sync::Arc;
use tokio::sync::RwLock;

/// Game-RL MCP server
pub struct GameRLServer<E: GameEnvironment> {
    /// Game environment implementation
    environment: Arc<RwLock<E>>,
    /// Agent registry
    registry: Arc<RwLock<AgentRegistry>>,
    /// Game manifest
    manifest: GameManifest,
}

impl<E: GameEnvironment> GameRLServer<E> {
    /// Create a new server with the given environment
    pub fn new(environment: E, manifest: GameManifest) -> Self {
        Self {
            environment: Arc::new(RwLock::new(environment)),
            registry: Arc::new(RwLock::new(AgentRegistry::new(
                manifest.capabilities.max_agents,
            ))),
            manifest,
        }
    }

    /// Run the server on stdio transport
    pub async fn run_stdio(self) -> Result<()> {
        transport::stdio::run(self).await
    }

    /// Get the game manifest
    pub fn manifest(&self) -> &GameManifest {
        &self.manifest
    }
}
