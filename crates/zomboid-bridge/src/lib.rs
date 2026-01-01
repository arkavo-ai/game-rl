//! Bridge between Rust MCP server and Project Zomboid via Java/Lua mod
//!
//! This crate provides:
//! - TCP communication with Project Zomboid Java mod
//! - Wire protocol for game state and action exchange (via game-bridge)
//! - GameEnvironment implementation that proxies to the PZ game

pub mod bridge;

// Re-export protocol types from game-bridge
pub mod protocol {
    pub use game_bridge::protocol::*;
}

pub use bridge::ZomboidBridge;
pub use game_bridge::GameMessage;
