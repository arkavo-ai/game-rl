//! Factorio bridge for game-rl
//!
//! Provides communication between the game-rl MCP server and Factorio
//! using a hybrid RCON + file-based IPC approach:
//!
//! - **Commands**: Sent via RCON using `/c remote.call("gamerl", ...)`
//! - **Observations**: Read from `script-output/gamerl/` directory
//!
//! This approach leverages Factorio's deterministic simulation for
//! reproducible RL training episodes.

mod bridge;
mod observer;
mod rcon;

pub use bridge::{FactorioBridge, FactorioConfig};
pub use observer::ObservationReader;
pub use rcon::RconClient;
