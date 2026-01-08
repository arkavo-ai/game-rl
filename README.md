# game-rl

> Multi-agent AI infrastructure for games

[![Rust](https://img.shields.io/badge/rust-1.85+-orange.svg)](https://www.rust-lang.org/)
[![.NET](https://img.shields.io/badge/.NET-4.7.2+-purple.svg)](https://dotnet.microsoft.com/)

Turn any game into a multi-agent AI environment. Train RL policies, orchestrate LLM-powered NPCs, or build AI game masters.

## Features

- **Multi-Agent Support** — Heterogeneous agents with different observation/action spaces
- **Deterministic Reproducibility** — Seeded episodes for scientific research
- **Zero-Copy Vision** — Shared memory streams for high-performance pixel observations
- **Game Adapters** — RimWorld, Project Zomboid, and more
- **Protocol Standard** — Built on MCP, integrates with Claude Code

## Supported Games

| Game | Status | Platform | Distribution |
|------|--------|----------|--------------|
| RimWorld | ✅ Working | macOS (tested), Windows/Linux (untested) | [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3634065510) |
| Project Zomboid | ✅ Working | macOS (tested), Windows/Linux (untested) | [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3637032835) |
| Factorio | 🚧 In Development | All platforms | [Mod Portal](https://mods.factorio.com/) (coming soon) |

## Quick Start with Claude Code

### RimWorld

1. Subscribe to [Arkavo Game-RL](https://steamcommunity.com/sharedfiles/filedetails/?id=3634065510) and [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) on Steam Workshop
2. Install [Claude Code](https://claude.ai/code)
3. Launch RimWorld and load a save
4. Add the MCP server:

**macOS:**
```bash
mkdir -p ~/arkavo-rimworld && cd ~/arkavo-rimworld
claude mcp add game-rl -- ~/Library/Application\ Support/Steam/steamapps/workshop/content/294100/3634065510/bin/macos/game-rl-server
claude
```

**Linux:**
```bash
mkdir -p ~/arkavo-rimworld && cd ~/arkavo-rimworld
claude mcp add game-rl -- ~/.steam/steam/steamapps/workshop/content/294100/3634065510/bin/linux/game-rl-server
claude
```

**Windows (PowerShell):**
```powershell
mkdir ~/arkavo-rimworld; cd ~/arkavo-rimworld
claude mcp add game-rl -- "$env:ProgramFiles\Steam\steamapps\workshop\content\294100\3634065510\bin\windows\game-rl-server.exe"
claude
```

Then ask Claude: *"What's the colony status?"*

### Project Zomboid

1. Subscribe to [Arkavo Game-RL](https://steamcommunity.com/sharedfiles/filedetails/?id=3637032835) on Steam Workshop
2. Install [Claude Code](https://claude.ai/code)
3. Launch Project Zomboid and load a game
4. Add the MCP server:

**macOS:**
```bash
mkdir -p ~/arkavo-zomboid && cd ~/arkavo-zomboid
claude mcp add game-rl -- ~/Library/Application\ Support/Steam/steamapps/workshop/content/108600/3637032835/bin/macos/game-rl-server
claude
```

**Linux:**
```bash
mkdir -p ~/arkavo-zomboid && cd ~/arkavo-zomboid
claude mcp add game-rl -- ~/.steam/steam/steamapps/workshop/content/108600/3637032835/bin/linux/game-rl-server
claude
```

**Windows (PowerShell):**
```powershell
mkdir ~/arkavo-zomboid; cd ~/arkavo-zomboid
claude mcp add game-rl -- "$env:ProgramFiles\Steam\steamapps\workshop\content\108600\3637032835\bin\windows\game-rl-server.exe"
claude
```

Then ask Claude: *"What's my survivor's status?"*

## Building from Source

```bash
git clone https://github.com/arkavo-ai/game-rl
cd game-rl
cargo build --release -p game-rl-cli
```

The unified `game-rl-server` binary auto-detects which game is running:
- RimWorld via Unix socket (`/tmp/gamerl-rimworld.sock`)
- Project Zomboid via file IPC (`~/Zomboid/Lua/gamerl_response.json`)

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      Agent Processes                            │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐               │
│  │ Claude  │ │RL Agent │ │  Game   │ │  Human  │               │
│  │  Code   │ │(policy) │ │ Master  │ │ Player  │               │
│  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘               │
└───────┼───────────┼───────────┼───────────┼─────────────────────┘
        │    MCP    │    MCP    │    MCP    │
        └───────────┴─────┬─────┴───────────┘
                          ▼
┌─────────────────────────────────────────────────────────────────┐
│                 game-rl-server (Rust)                           │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐               │
│  │  Protocol   │ │   Agent     │ │   Vision    │               │
│  │  Handler    │ │  Registry   │ │  Streams    │               │
│  └─────────────┘ └─────────────┘ └─────────────┘               │
└───────────────────────────┬─────────────────────────────────────┘
                            │ IPC (Unix socket / File-based / RCON)
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Game Runtime                                 │
│  RimWorld (.NET/Harmony) | Project Zomboid (Lua) | Factorio    │
└─────────────────────────────────────────────────────────────────┘
```

## Repository Structure

```
game-rl/
├── crates/                      # Rust workspace
│   ├── game-rl-core/            # Shared types and traits
│   ├── game-rl-server/          # MCP server implementation
│   ├── game-rl-cli/             # Unified CLI (game-rl-server binary)
│   ├── game-bridge/             # Shared IPC protocol
│   ├── harmony-bridge/          # RimWorld bridge (Unix socket)
│   ├── zomboid-bridge/          # Project Zomboid bridge (file IPC)
│   └── factorio-bridge/         # Factorio bridge (RCON + file)
│
├── dotnet/                      # C# libraries
│   └── GameRL.Harmony/          # Base library for .NET games
│
├── adapters/                    # Game-specific implementations
│   ├── rimworld/                # RimWorld mod (C#)
│   ├── zomboid/                 # Project Zomboid mod (Lua)
│   └── factorio/                # Factorio mod (Lua)
│
└── .github/workflows/           # CI for all platforms
```

## Protocol

game-rl implements the [Game-RL Protocol](https://github.com/arkavo-org/specifications/tree/main/game-rl), built on [MCP](https://modelcontextprotocol.io/).

### Core Tools

| Tool | Description |
|------|-------------|
| `register_agent` | Register agent with specific capabilities |
| `sim_step` | Execute action, advance simulation, receive observation + reward |
| `reset` | Start new episode with deterministic seeding |
| `get_state_hash` | Verify determinism for reproducibility |

### Example Message

```json
{
  "method": "tools/call",
  "params": {
    "name": "sim_step",
    "arguments": {
      "AgentId": "survivor",
      "Action": {"Type": "Walk", "Direction": "North", "Distance": 5},
      "Ticks": 60
    }
  }
}
```

## Acknowledgments

Built with insights from:
- [Gymnasium](https://gymnasium.farama.org/) (Farama Foundation)
- [Unity ML-Agents](https://unity.com/products/machine-learning-agents)
- [Harmony](https://harmony.pardeike.net/) (Andreas Pardeike)

---

**Created by [Arkavo](https://arkavo.com)**
