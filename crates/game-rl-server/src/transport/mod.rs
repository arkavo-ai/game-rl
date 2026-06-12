//! Transport layer for Game-RL MCP server

pub mod stdio;

#[cfg(feature = "http")]
pub mod http;
