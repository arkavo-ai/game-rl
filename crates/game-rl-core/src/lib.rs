//! # game-rl-core
//!
//! Core types and traits for the Game-RL protocol.
//!
//! This crate provides the foundational types used across all Game-RL implementations:
//! - Agent types and registration
//! - Observation and action schemas
//! - Reward components
//! - Vision stream descriptors
//! - Protocol messages

pub mod action;
pub mod agent;
pub mod error;
pub mod manifest;
pub mod observation;
pub mod reward;
pub mod stream;

pub use action::{Action, ActionSpace};
pub use agent::{AgentConfig, AgentEntry, AgentId, AgentManifest, AgentStatus, AgentType};
pub use error::{GameRLError, Result, error_codes};
pub use manifest::{Capabilities, GameManifest};
pub use observation::{GameEvent, Observation, StepResult};
pub use reward::{Reward, RewardComponents};
pub use stream::{PixelFormat, StreamDescriptor, StreamProfile};
