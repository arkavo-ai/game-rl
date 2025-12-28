# game-rl

> Multi-agent AI infrastructure for games

[![Rust](https://img.shields.io/badge/rust-1.75+-orange.svg)](https://www.rust-lang.org/)
[![.NET](https://img.shields.io/badge/.NET-4.7.2+-purple.svg)](https://dotnet.microsoft.com/)

Turn any game into a multi-agent AI environment. Train RL policies, orchestrate LLM-powered NPCs, or build AI game masters.

## Features

- **Multi-Agent Support** — Heterogeneous agents with different observation/action spaces
- **Deterministic Reproducibility** — Seeded episodes for scientific research
- **Zero-Copy Vision** — Shared memory streams for high-performance pixel observations
- **Game Adapters** — RimWorld, Stardew Valley, and more
- **Protocol Standard** — Built on MCP, integrates with Claude and Arkavo Edge

## Quick Start

### For Researchers (Python)

```bash
# Clone and setup
git clone https://github.com/arkavo-ai/game-rl
cd game-rl
pip install -e ./agent

# Run example with gridworld
cargo run --example gridworld &
python agent/examples/train_gridworld.py
```

### For RimWorld

```bash
# 1. Build and install the mod
cd adapters/rimworld
./install.sh

# 2. Start the MCP server
cargo run --bin harmony-server -- --game rimworld

# 3. Launch RimWorld with mod enabled
# 4. Train an agent
python agent/examples/train_rimworld.py
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      Agent Processes                            │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐               │
│  │RL Agent │ │LLM NPC  │ │  Game   │ │  Human  │               │
│  │(policy) │ │ Agent   │ │ Master  │ │ Player  │               │
│  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘               │
└───────┼───────────┼───────────┼───────────┼─────────────────────┘
        │    MCP    │    MCP    │    MCP    │
        └───────────┴─────┬─────┴───────────┘
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                    game-rl Server (Rust)                        │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐               │
│  │  Protocol   │ │   Agent     │ │   Vision    │               │
│  │  Handler    │ │  Registry   │ │  Streams    │               │
│  └─────────────┘ └─────────────┘ └─────────────┘               │
└───────────────────────────┬─────────────────────────────────────┘
                            │ IPC
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Game Runtime                                 │
│  (Unity, Godot, .NET/Harmony, Custom Engine)                   │
└─────────────────────────────────────────────────────────────────┘
```

## Repository Structure

```
game-rl/
├── crates/                      # Rust workspace
│   ├── game-rl-core/            # Shared types and traits
│   ├── game-rl-server/          # MCP server implementation
│   ├── game-rl-client/          # Test/reference client
│   └── harmony-bridge/          # Rust ↔ C# IPC bridge
│
├── dotnet/                      # C# libraries
│   └── GameRL.Harmony/          # Base library for .NET games
│
├── adapters/                    # Game-specific implementations
│   ├── rimworld/                # RimWorld mod
│   ├── stardew/                 # Stardew Valley (planned)
│   └── template/                # Starter for new games
│
├── agent/                       # Python reference agent
│   └── game_rl/                 # pip installable package
│
├── examples/                    # Working examples
│   ├── gridworld/               # Self-contained Rust environment
│   └── rimworld-survival/       # Full RimWorld tutorial
│
└── docs/                        # Documentation
```

## Supported Games

| Game | Status | Agent Types |
|------|--------|-------------|
| RimWorld | 🚧 In Development | ColonyManager, PawnBehavior, StoryTeller |
| Stardew Valley | 📋 Planned | FarmManager, NPCBehavior |

## Protocol

game-rl implements the [Game-RL Protocol](https://github.com/arkavo-org/specifications/tree/main/game-rl), built on [MCP](https://modelcontextprotocol.io/).

### Core Tools

| Tool | Description |
|------|-------------|
| `sim_step` | Execute action, advance simulation, receive observation + reward |
| `reset` | Start new episode with deterministic seeding |
| `register_agent` | Register agent with specific capabilities |
| `configure_streams` | Setup zero-copy vision streams |
| `get_state_hash` | Verify determinism for reproducibility |

### Example Message

```json
{
  "method": "tools/call",
  "params": {
    "name": "sim_step",
    "arguments": {
      "agent_id": "colony_manager",
      "action": {"type": "SetWorkPriority", "colonist": "pawn_1", "work": "mining", "priority": 1},
      "ticks": 60
    }
  }
}
```

## Integration with Arkavo Edge

For production multi-agent orchestration, game-rl integrates with [Arkavo Edge](https://arkavo.com):

- **HRM Orchestration** — Hierarchical task routing across agents
- **LLM Fleet** — Managed inference for NPC dialogue and game masters  
- **Observability** — Replay, debugging, and analytics


## Acknowledgments

Built with insights from:
- [Gymnasium](https://gymnasium.farama.org/) (Farama Foundation)
- [Unity ML-Agents](https://unity.com/products/machine-learning-agents)
- [Harmony](https://harmony.pardeike.net/) (Andreas Pardeike)
- The Arkavo community

---

**Created by [Arkavo](https://arkavo.com)**

