//! Game environment trait

use async_trait::async_trait;
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameEvent, GameManifest, Observation,
    Result, StepResult, StreamDescriptor,
};
use tokio::sync::broadcast;

/// Pushed state update from the game
#[derive(Debug, Clone)]
pub struct StateUpdate {
    /// Current game tick
    pub tick: u64,
    /// Game state (JSON)
    pub state: serde_json::Value,
    /// Events that occurred
    pub events: Vec<GameEvent>,
}

/// Trait for implementing game environments
///
/// Implement this trait to expose a game as a Game-RL environment.
#[async_trait]
pub trait GameEnvironment: Send + Sync + 'static {
    /// Register an agent with the environment
    async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    ) -> Result<AgentManifest>;

    /// Deregister an agent
    async fn deregister_agent(&mut self, agent_id: &AgentId) -> Result<()>;

    /// Execute an action and advance simulation
    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult>;

    /// Reset the environment
    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<Observation>;

    /// Get current state hash for determinism verification
    async fn state_hash(&mut self) -> Result<String>;

    /// Configure vision streams
    async fn configure_streams(
        &mut self,
        agent_id: &AgentId,
        profile: &str,
    ) -> Result<Vec<StreamDescriptor>>;

    /// Save trajectory to file
    async fn save_trajectory(&self, path: &str) -> Result<()>;

    /// Load and replay trajectory
    async fn load_trajectory(&mut self, path: &str) -> Result<()>;

    /// Called when environment should shut down
    async fn shutdown(&mut self) -> Result<()>;

    /// Get the game manifest describing capabilities
    fn manifest(&self) -> GameManifest;

    /// Subscribe to pushed state updates from the game.
    /// Returns None if push is not supported.
    fn subscribe_events(&self) -> Option<broadcast::Receiver<StateUpdate>> {
        None
    }
}
