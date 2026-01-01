//! Bridge between Rust MCP server and .NET games via Harmony
//!
//! This crate provides:
//! - IPC communication with .NET games (Unix sockets / named pipes)
//! - Wire protocol for game state and action exchange (via game-bridge)
//! - GameEnvironment implementation that proxies to the .NET game

pub mod ipc;

// Re-export protocol types from game-bridge for backward compatibility
pub mod protocol {
    pub use game_bridge::protocol::*;
}

pub use ipc::HarmonyBridge;
pub use game_bridge::GameMessage;
