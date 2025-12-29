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

# Run with example gridworld
cargo run --example gridworld
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

### Protocol Flow

1. Client connects via MCP (stdio transport)
2. `initialize` handshake
3. `register_agent` with type (EntityBehavior, ColonyManager, GameMaster, etc.)
4. Loop: `sim_step` sends action, receives observation + reward
5. `reset` for new episodes

### Wire Protocols

- **MCP Layer**: JSON-RPC 2.0 over stdio (agents ↔ game-rl-server)
- **IPC Layer**: JSON over Unix sockets (game-rl-server ↔ .NET games)

## Constraints

- No Ruby code (user preference)
- Rust edition 2024, requires rustc 1.85+
- Windows named pipe support is planned but not yet implemented
