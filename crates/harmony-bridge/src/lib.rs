//! Bridge between Rust MCP server and .NET games via Harmony
//!
//! This crate provides:
//! - IPC communication with .NET games (Unix sockets / named pipes)
//! - Wire protocol for game state and action exchange
//! - GameEnvironment implementation that proxies to the .NET game

pub mod ipc;
pub mod protocol;

pub use ipc::HarmonyBridge;
pub use protocol::GameMessage;
