//! # game-rl-reference
//!
//! GridColony: the Game-RL reference environment (spec draft-02, conformance Level 3).
//!
//! Purposes:
//! - Validate the `game-rl-conformance` harness against a known-good server
//! - Give orchestrators (Arkavo Edge) a deterministic, headless playtest target
//! - Provide behavioral probes: the `fertile-corner` scenario puts all fertile
//!   soil far from the map center, so center-biased spatial policies score zero
//!   on the `farm_fertility_quality` reward component.
//!
//! Run as an MCP server on stdio:
//! ```bash
//! cargo run -p game-rl-reference -- --scenario fertile-corner --seed 42
//! ```

pub mod env;
pub mod rng;
pub mod world;

pub use env::ReferenceEnv;
