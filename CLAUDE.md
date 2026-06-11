# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build all crates
cargo build

# Build for release (LTO enabled)
cargo build --release

# Run tests
cargo test

# Run tests for specific crate
cargo test -p game-rl-core

# Check code without building
cargo check

# Run the harmony-bridge server (requires socket path argument)
cargo run -p harmony-bridge -- /tmp/game-rl.sock

# Run the reference environment (GridColony) as an MCP server on stdio
cargo run -p game-rl-reference -- --scenario fertile-corner --seed 42

# Run the conformance suite against any Game-RL server
cargo run -p game-rl-conformance -- --level 3 --scenario fertile-corner -- ./target/debug/game-rl-reference
```

### C# Build Commands (RimWorld mod)

```bash
# Requires .NET SDK 6.0+ (install via: brew install dotnet)
cd dotnet && dotnet build GameRL.Harmony.sln
cd adapters/rimworld && dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj

# Install mod (symlink for development)
ln -s "$(pwd)/adapters/rimworld" ~/Library/Application\ Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/GameRL
```

## Architecture

This is a Rust workspace for multi-agent AI infrastructure in games. The project uses the Model Context Protocol (MCP) for agent-server communication.

### Crate Structure

- **game-rl-core** - Foundation types and traits. Protocol-agnostic definitions for agents, actions, observations, rewards, and the `GameManifest` capability descriptor.

- **game-rl-server** - MCP server implementation. Exposes `GameRLServer<E>` generic over `GameEnvironment` trait implementations. Handles JSON-RPC 2.0 over stdio, agent registry, and tool dispatch.

- **game-rl-client** - Reference client for spawning and connecting to game environments. Used for testing and examples.

- **harmony-bridge** - IPC bridge between Rust MCP server and .NET games (via Harmony mod framework). Uses JSON over Unix sockets (named pipes on Windows).

- **game-rl-reference** - GridColony, the spec draft-02 reference environment (conformance Level 3). Deterministic, headless; the `fertile-corner` scenario is a behavioral probe for spatial center bias.

- **game-rl-conformance** - Runnable conformance suite. Spawns any server over stdio and verifies the draft-02 requirement checklist (C-INIT … C-BATCH).

### Key Traits

```rust
// Implement this trait to add support for a new game
#[async_trait]
pub trait GameEnvironment: Send + Sync {
    fn manifest(&self) -> &GameManifest;
    async fn register_agent(&mut self, id: AgentId, agent_type: AgentType, config: AgentConfig) -> Result<()>;
    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: Option<u64>) -> Result<StepResult>;
    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<()>;
    // ...
}
```

### Protocol Flow (spec draft-02)

1. Client connects via MCP (stdio transport)
2. `initialize` handshake (`serverInfo.gameRlVersion` declares the protocol dialect)
3. `manifest` to discover capabilities, then `registerAgent` with type (Observer, Player, Entity, Controller, System, Director)
4. Loop: `step` sends action, receives observation + reward; `observe` reads state without advancing time
5. `reset` for new episodes; `episodeSummary` at episode boundaries

The normative spec is `draft-arkavo-game-rl-02.md` in the arkavo/specifications repo (snake_case draft-00 tool names are accepted as deprecated aliases). Spatial intents follow REQ-SPA-01..07: never silently re-anchor, expose Landmarks, resolve ambiguity against the colony centroid — never the map center.

### Wire Protocols

- **MCP Layer**: JSON-RPC 2.0 over stdio (agents ↔ game-rl-server)
- **IPC Layer**: JSON over Unix sockets (game-rl-server ↔ .NET games)

## MCP Compliance

This project is an MCP server. Models and agents are trained on the MCP standard — do not deviate from it.

- **Tool schemas** must use standard JSON Schema conventions (`type`, `description`, `properties` — lowercase per JSON Schema spec)
- **Tool responses** (game data payloads) use PascalCase for all field names, matching C#/.NET conventions
- Do not invent custom schema formats — use MCP's `tools/list` with proper JSON Schema `inputSchema` definitions
- The MCP spec is the source of truth for protocol-level naming and structure

## Constraints

- No Ruby code (user preference)
- Rust edition 2024, requires rustc 1.85+
- Windows named pipe support is planned but not yet implemented
