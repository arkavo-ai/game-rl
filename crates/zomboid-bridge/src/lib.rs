//! Bridge between Rust MCP server and Project Zomboid via file-based IPC
//!
//! This crate provides:
//! - File-based IPC with Project Zomboid Lua mod (PZ's Lua is sandboxed)
//! - Wire protocol for game state and action exchange (JSON via files)
//! - GameEnvironment implementation that proxies to the PZ game

pub mod bridge;

// Re-export protocol types from game-bridge
pub mod protocol {
    pub use game_bridge::protocol::*;
}

pub use bridge::{ZomboidBridge, ZomboidConfig};
pub use game_bridge::GameMessage;
