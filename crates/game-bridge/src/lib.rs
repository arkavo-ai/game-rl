//! Shared bridge infrastructure for game-rl adapters
//!
//! This crate provides:
//! - Wire protocol for game state and action exchange
//! - Transport abstractions (AsyncReader/AsyncWriter traits)
//! - TCP and Unix socket transports
//! - Background reader task for handling messages

pub mod protocol;
pub mod transport;
pub mod tcp;
#[cfg(unix)]
pub mod unix;

pub use protocol::{GameCapabilities, GameMessage, StepResultPayload, deserialize, serialize};
pub use transport::{AsyncReader, AsyncWriter, reader_task};
