# Game-RL Protocol Specification

**Version:** 1.0.0-draft
**Status:** Draft
**Created:** 2025-12-28
**Authors:** Arkavo AI
**License:** Apache 2.0

---

## Abstract

The Game-RL Protocol defines a standard interface for multi-agent AI systems to observe and interact with game environments, functioning either as **embodied characters** (APCs/NPCs) or **systemic controllers** (Game Masters/Directors). Built on the Model Context Protocol (MCP), it enables researchers and developers to:

- Train reinforcement learning agents in existing games
- Orchestrate multiple AI agents with heterogeneous capabilities
- Build AI-native games with LLM-powered NPCs, game masters, and world simulators
- Ensure deterministic reproducibility for scientific research

This specification is implementation-agnostic and supports any game engine or runtime that can expose the required interface.

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Architecture](#2-architecture)
   - 2.4 [Clock Modes](#24-clock-modes)
   - 2.5 [Connector Architecture](#25-connector-architecture)
3. [Protocol Foundation](#3-protocol-foundation)
4. [Agent Model](#4-agent-model)
   - 4.1.1 [Permission Enforcement](#411-permission-enforcement)
   - 4.1.2 [Role Comparison](#412-role-comparison-gamemaster-vs-entitybehavior)
   - 4.1.3 [Agency Scopes](#413-agency-scopes)
5. [Core Tools](#5-core-tools)
6. [Resources](#6-resources)
7. [Vision Streams](#7-vision-streams)
8. [Determinism & Reproducibility](#8-determinism--reproducibility)
9. [Multi-Agent Coordination](#9-multi-agent-coordination)
   - 9.4 [Session Topology](#94-session-topology)
10. [Platform Adaptation](#10-platform-adaptation)
11. [Conformance Requirements](#11-conformance-requirements)
12. [Security Considerations](#12-security-considerations)
13. [References](#13-references)

---

## 1. Introduction

### 1.1 Motivation

Modern AI research increasingly requires complex, interactive environments that go beyond traditional simulations. Commercial games offer rich, well-tested worlds with emergent behaviors, but lack standardized interfaces for AI integration. Meanwhile, game developers seek to incorporate AI agents but face fragmented tooling and no clear architectural patterns.

The Game-RL Protocol addresses these challenges by defining:

- A **transport-agnostic protocol** for game-AI communication
- A **multi-agent model** supporting heterogeneous agent types
- **Observation and action contracts** that preserve learning signal integrity
- **Determinism guarantees** required for reproducible research
- **Vision stream specifications** for high-performance pixel observations

### 1.2 Scope

This specification covers:

- Protocol messages and their semantics
- Agent registration and lifecycle
- Observation, action, and reward schemas
- Synchronization requirements
- Platform adaptation guidelines

This specification does not cover:

- Specific game implementations
- AI/ML algorithms or architectures
- Network topology for distributed training
- Authentication and authorization (deferred to future versions)

### 1.3 Terminology

| Term | Definition |
|------|------------|
| **Agent** | An autonomous system that perceives game state and influences it via the Game-RL protocol |
| **Embodied Agent** | An agent controlling a specific entity (Avatar/APC) subject to spatial and simulation constraints (physics, line-of-sight) |
| **Systemic Agent** | An agent controlling environmental or meta-game systems (Game Master), not bound by spatial constraints but by administrative permissions |
| **Avatar** | The in-game entity (mesh/physics body) controlled by an Embodied Agent |
| **Environment** | The game runtime exposing the Game-RL interface |
| **Step** | A single observe-act-reward cycle |
| **Episode** | A sequence of steps from reset to terminal state |
| **Tick** | The smallest discrete time unit in the game simulation |
| **Observation** | State information provided to an agent |
| **Action** | A command issued by an agent to the environment |
| **Reward** | A scalar signal indicating action quality |
| **Diegetic** | Within the game world; subject to simulation rules |
| **Non-Diegetic** | Outside the game world; operating on meta-game systems |

### 1.4 Notational Conventions

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in RFC 2119.

---

## 2. Architecture

### 2.1 System Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      Agent Processes                            │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐               │
│  │RL Agent │ │LLM Agent│ │LLM Agent│ │  Human  │               │
│  │(policy) │ │ (NPC)   │ │  (GM)   │ │(player) │               │
│  └────┬────┘ └────┬────┘ └────┬────┘ └────┬────┘               │
│       │           │           │           │                     │
└───────┼───────────┼───────────┼───────────┼─────────────────────┘
        │           │           │           │
        │    MCP    │    MCP    │    MCP    │
        │  (stdio)  │  (stdio)  │  (stdio)  │
        │           │           │           │
┌───────┼───────────┼───────────┼───────────┼─────────────────────┐
│       ▼           ▼           ▼           ▼                     │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │              Game-RL Protocol Server                    │    │
│  │  ┌─────────────────────────────────────────────────┐   │    │
│  │  │             Agent Registry                       │   │    │
│  │  │  - Agent lifecycle management                    │   │    │
│  │  │  - Observation routing                           │   │    │
│  │  │  - Action validation                             │   │    │
│  │  └─────────────────────────────────────────────────┘   │    │
│  │  ┌─────────────────────────────────────────────────┐   │    │
│  │  │             Synchronization Layer               │   │    │
│  │  │  - Step coordination                             │   │    │
│  │  │  - Reward computation                            │   │    │
│  │  │  - State hashing                                 │   │    │
│  │  └─────────────────────────────────────────────────┘   │    │
│  └─────────────────────────────────────────────────────────┘    │
│                              │                                   │
│                              ▼                                   │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                   Game Runtime                          │    │
│  │  (Unity, Godot, Custom Engine, .NET/Harmony, etc.)     │    │
│  └─────────────────────────────────────────────────────────┘    │
│                        Environment                               │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 Communication Model

The Game-RL Protocol uses MCP (Model Context Protocol) as its foundation:

- **Transport**: stdio (stdin/stdout) for process-based agents
- **Encoding**: JSON-RPC 2.0 over newline-delimited JSON
- **Session**: One MCP connection per agent process

Environments MAY support alternative transports (SSE, WebSocket) but MUST support stdio for conformance.

### 2.3 Deployment Patterns

#### Pattern A: Single-Agent Training
```
Agent Process ←──MCP──→ Game Process
```

#### Pattern B: Multi-Agent with Orchestrator
```
                    ┌─────────────┐
                    │ Orchestrator│
                    │(Arkavo Edge)│
                    └──────┬──────┘
           ┌───────────────┼───────────────┐
           ▼               ▼               ▼
      ┌─────────┐    ┌─────────┐    ┌─────────┐
      │ Agent 1 │    │ Agent 2 │    │ Agent N │
      └────┬────┘    └────┬────┘    └────┬────┘
           │              │              │
           └──────────────┼──────────────┘
                          │ MCP (multiplexed)
                          ▼
                   ┌─────────────┐
                   │    Game     │
                   └─────────────┘
```

#### Pattern C: In-Process Agents
```
┌─────────────────────────────────────┐
│           Game Process              │
│  ┌─────────┐  ┌──────────────────┐  │
│  │  Game   │  │  Embedded Agent  │  │
│  │ Runtime │←→│    (ONNX/etc)    │  │
│  └─────────┘  └──────────────────┘  │
└─────────────────────────────────────┘
```

#### Pattern D: Shared Session with Connector
```
┌─────────────────┐    ┌─────────────────┐
│  Claude Desktop │    │ ChatGPT Desktop │
│   (GameMaster)  │    │ (EntityBehavior)│
└────────┬────────┘    └────────┬────────┘
         │ stdio                │ stdio
         ▼                      ▼
┌─────────────────┐    ┌─────────────────┐
│   Connector     │    │   Connector     │
│ (gom-mcp-bridge)│    │ (gom-mcp-bridge)│
└────────┬────────┘    └────────┬────────┘
         │                      │
         └──────────┬───────────┘
                    │ Named Pipe / IPC
                    ▼
         ┌─────────────────────┐
         │   Game (Running)    │
         │  \\.\pipe\arkavo_   │
         │     game_mcp        │
         └─────────────────────┘
```

### 2.4 Clock Modes

The protocol supports two fundamental clock modes that determine simulation timing ownership.

#### 2.4.1 Training Mode (Agent-Owned Clock)

In Training Mode, the **agent owns the clock**. The game simulation pauses until `sim_step` is called, enabling deterministic, reproducible training.

| Property | Behavior |
|----------|----------|
| Clock Owner | Agent process |
| Game State | Paused between steps |
| `sim_step(ticks)` | "Advance physics by exactly N ticks" |
| Synchronization | Barrier — all agents must commit |
| Determinism | Guaranteed |
| Use Cases | RL training, reproducible research, Arkavo swarms |

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "rl_policy:walker_1",
    "agent_type": "EntityBehavior",
    "config": {
      "clock_mode": "training"
    }
  }
}
```

#### 2.4.2 Live Mode (Engine-Owned Clock)

In Live Mode, the **game engine owns the clock**. The game runs at its native tick rate (e.g., 60 FPS) regardless of agent activity, enabling real-time interaction for game mastering and modding.

| Property | Behavior |
|----------|----------|
| Clock Owner | Game engine |
| Game State | Runs continuously at native tick rate |
| `sim_step(ticks)` | "Queue this action to execute now" (ticks ignored or treated as duration) |
| Synchronization | Async — agents act whenever ready |
| Determinism | Not guaranteed |
| Use Cases | Game mastering, modding, LLM NPCs, live play |

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "gm:claude_master",
    "agent_type": "GameMaster",
    "config": {
      "clock_mode": "live"
    }
  }
}
```

#### 2.4.3 Clock Mode Resolution

When multiple agents connect with different clock modes:

- **If ANY agent requests `training` mode**: Environment MUST use lockstep simulation (training mode wins)
- **If ALL agents request `live` mode**: Environment runs real-time

This ensures training integrity is never compromised by casual connections.

### 2.5 Connector Architecture

The Connector is a lightweight bridge binary that enables MCP-compatible AI clients (Claude Desktop, ChatGPT Desktop) to connect to running games without spawning new instances.

#### 2.5.1 Connector Role

```
┌─────────────────────────────────────────────────────────────┐
│                    AI Client (Claude/ChatGPT)               │
│              Expects: spawn binary, talk stdio              │
└─────────────────────────┬───────────────────────────────────┘
                          │ stdio (stdin/stdout)
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                 Connector (gom-mcp-bridge)                  │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  1. Check: Is game running?                           │  │
│  │     • Yes → Connect to Named Pipe                     │  │
│  │     • No  → Spawn game in "Headless Host Mode"        │  │
│  │  2. Proxy JSON-RPC bidirectionally                    │  │
│  │  3. Handle connection lifecycle                       │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────┬───────────────────────────────────┘
                          │ Named Pipe / Unix Socket
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                     Game Process                            │
│              Listens: \\.\pipe\arkavo_game_mcp              │
│                       /tmp/arkavo_game_mcp.sock             │
└─────────────────────────────────────────────────────────────┘
```

#### 2.5.2 Connector Behavior

**Startup Sequence:**

1. Connector launched by AI client via stdio
2. Connector checks for game's named pipe/socket
3. If pipe exists: Connect and begin proxying
4. If pipe missing: Spawn game with `--headless-host` flag, wait for pipe, connect
5. Complete MCP handshake, forwarding `initialize` to game

**Shutdown Sequence:**

1. AI client closes stdin (EOF)
2. Connector sends `deregister_agent` to game
3. Connector closes pipe connection
4. Connector exits (game continues running for other agents)

#### 2.5.3 Client Configuration Examples

**Claude Desktop (`claude_desktop_config.json`):**

```json
{
  "mcpServers": {
    "game-of-mods": {
      "command": "/usr/local/bin/gom-mcp-bridge",
      "args": ["--role", "GameMaster"],
      "env": {
        "GOM_PIPE": "/tmp/arkavo_game_mcp.sock"
      }
    }
  }
}
```

**ChatGPT Desktop:**

```json
{
  "mcpServers": {
    "game-of-mods": {
      "command": "/usr/local/bin/gom-mcp-bridge",
      "args": ["--role", "EntityBehavior", "--entity", "player_avatar"],
      "env": {
        "GOM_PIPE": "/tmp/arkavo_game_mcp.sock"
      }
    }
  }
}
```

---

## 3. Protocol Foundation

### 3.1 MCP Handshake

Agents MUST initiate the connection with the MCP initialize handshake:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-11-25",
    "capabilities": {
      "tools": {},
      "resources": {"subscribe": true}
    },
    "clientInfo": {
      "name": "agent-identifier",
      "version": "1.0.0"
    }
  }
}
```

The environment MUST respond with its capabilities:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "2025-11-25",
    "capabilities": {
      "tools": {"listChanged": false},
      "resources": {"subscribe": true, "listChanged": false},
      "logging": {}
    },
    "serverInfo": {
      "name": "game-name",
      "version": "1.0.0",
      "gameRlVersion": "1.0.0"
    }
  }
}
```

The `serverInfo.gameRlVersion` field indicates Game-RL Protocol compliance.

### 3.2 Message Flow

All game interactions use MCP tool calls:

```json
{
  "jsonrpc": "2.0",
  "id": 42,
  "method": "tools/call",
  "params": {
    "name": "sim_step",
    "arguments": {
      "agent_id": "npc_behavior:guard_1",
      "action": {"type": "patrol", "waypoint": 3},
      "ticks": 1
    }
  }
}
```

### 3.3 Error Handling

Errors follow JSON-RPC 2.0 conventions with Game-RL specific codes:

| Code | Name | Description |
|------|------|-------------|
| -32700 | Parse error | Invalid JSON |
| -32600 | Invalid request | Malformed request |
| -32601 | Method not found | Unknown tool |
| -32602 | Invalid params | Schema validation failed |
| -32603 | Internal error | Environment error |
| -32000 | Agent not registered | Unknown agent_id |
| -32001 | Invalid action | Action not in action space |
| -32002 | Episode terminated | Step after done=true |
| -32003 | Sync timeout | Agent missed step deadline |
| -32004 | Resource exhausted | Too many agents/streams |

---

## 4. Agent Model

### 4.1 Agent Types

The protocol defines standard agent archetypes with role-based permissions. Implementations MAY extend these but MUST enforce permission boundaries.

```json
{
  "agent_types": {
    "EntityBehavior": {
      "description": "Controls a single game entity (NPC, unit, etc.)",
      "observation_scope": "entity_local",
      "typical_action_space": "discrete_movement_interaction",
      "permissions": {
        "allowed": ["move", "jump", "interact", "attack", "use_item"],
        "denied": ["spawn_entity", "kill_entity", "teleport", "set_time", "modify_world"]
      }
    },
    "ColonyManager": {
      "description": "High-level strategic control of multiple entities",
      "observation_scope": "global",
      "typical_action_space": "discrete_parameterized",
      "permissions": {
        "allowed": ["assign_task", "set_priority", "allocate_resources"],
        "denied": ["spawn_entity", "modify_world", "set_time"]
      }
    },
    "WorldSimulation": {
      "description": "Controls environmental systems (weather, economy, spawning)",
      "observation_scope": "global",
      "typical_action_space": "continuous_parameters",
      "permissions": {
        "allowed": ["set_weather", "adjust_economy", "trigger_event", "spawn_resource"],
        "denied": ["kill_entity", "teleport_player", "modify_narrative"]
      }
    },
    "GameMaster": {
      "description": "Narrative control, event triggering, difficulty adjustment",
      "observation_scope": "global_with_history",
      "typical_action_space": "discrete_event_selection",
      "permissions": {
        "allowed": ["spawn_entity", "kill_entity", "teleport_player", "set_time",
                   "trigger_event", "modify_difficulty", "send_narrative"],
        "denied": []
      }
    },
    "DialogueAgent": {
      "description": "Controls NPC dialogue and conversation",
      "observation_scope": "conversation_context",
      "typical_action_space": "text_generation",
      "permissions": {
        "allowed": ["speak", "emote", "offer_quest", "trade"],
        "denied": ["move", "attack", "spawn_entity", "modify_world"]
      }
    },
    "CombatDirector": {
      "description": "Orchestrates combat encounters and AI opponents",
      "observation_scope": "combat_relevant",
      "typical_action_space": "tactical_commands",
      "permissions": {
        "allowed": ["spawn_enemy", "set_aggro", "trigger_phase", "adjust_difficulty"],
        "denied": ["kill_player", "teleport_player", "modify_narrative"]
      }
    }
  }
}
```

#### 4.1.1 Permission Enforcement

Permissions are enforced at action validation time:

1. Agent submits action via `sim_step`
2. Environment checks action type against agent's `permissions.allowed`
3. If action type in `permissions.denied` → Error `-32001 Invalid action`
4. If action type not in `permissions.allowed` and list is non-empty → Error `-32001 Invalid action`

**Example: EntityBehavior agent attempts privileged action**

```json
// Request
{
  "name": "sim_step",
  "arguments": {
    "agent_id": "chatgpt:player_avatar",
    "action": {"type": "spawn_entity", "params": {"entity": "dragon"}}
  }
}

// Response (Error)
{
  "error": {
    "code": -32001,
    "message": "Invalid action: 'spawn_entity' not permitted for EntityBehavior agents"
  }
}
```

#### 4.1.2 Role Comparison: GameMaster vs EntityBehavior

| Capability | GameMaster (Claude) | EntityBehavior (ChatGPT) |
|------------|---------------------|--------------------------|
| Observation | Global map, event log, all entities | First-person view, local surroundings |
| Movement | Teleport any entity | Move own physics body only |
| Combat | Spawn/kill any entity | Attack with own weapons |
| World | Modify time, weather, spawn | No world modification |
| Narrative | Trigger quests, dialogues | Respond to prompts only |

#### 4.1.3 Agency Scopes

Agents operate within one of two fundamental scopes that determine their relationship to the game simulation.

**A. Diegetic Scope (Embodied / In-World)**

Embodied agents participate *within* the simulation as physical entities.

| Property | Constraint |
|----------|------------|
| Body | MUST have an `avatar_id` referencing a physics body |
| Observation | Limited to sensor range (first-person, third-person, radar) |
| Actions | Bound by physics and gameplay rules |
| Mortality | Subject to `health`, `stamina`, `death` |
| Spawning | Cannot create entities; can only interact with existing ones |
| Visibility | Other agents can perceive and interact with the avatar |

**Use Cases:** Arkavo RL bots, ChatGPT companion, NPC controllers, player surrogates.

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "chatgpt:companion",
    "agent_type": "EntityBehavior",
    "scope": "embodied",
    "config": {
      "avatar_id": "npc_ranger_01",
      "spawn_point": "tavern_entrance"
    }
  }
}
```

**B. Non-Diegetic Scope (Systemic / Out-of-World)**

Systemic agents orchestrate the simulation from *outside* the game world.

| Property | Constraint |
|----------|------------|
| Body | No physical presence (virtual/omniscient) |
| Observation | Global state, event logs, all entity data |
| Actions | Administrative commands (`spawn`, `teleport`, `set_time`) |
| Mortality | Immune to physics and damage |
| Spawning | Can create, destroy, and modify entities |
| Visibility | Invisible to embodied agents and players |

**Use Cases:** Claude Desktop (Game Master), Director AI, level generators, difficulty controllers.

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "claude:game_master",
    "agent_type": "GameMaster",
    "scope": "systemic",
    "config": {
      "capabilities": ["admin", "narrative", "spawn", "world_modify"]
    }
  }
}
```

**C. Scope Interaction Example**

When Claude (systemic) and ChatGPT (embodied) connect simultaneously:

```
┌─────────────────────────────────────────────────────────────────┐
│                        Game World                               │
│                                                                 │
│   ┌─────────────────┐                                          │
│   │  ChatGPT Avatar │  ← Physical body at (10, 5, 0)           │
│   │   (Low Health)  │                                          │
│   └─────────────────┘                                          │
│            │                                                    │
│            │  "I see a health potion nearby"                   │
│            ▼                                                    │
│   ┌─────────────────┐                                          │
│   │  Health Potion  │  ← Spawned by Claude (invisible hand)    │
│   └─────────────────┘                                          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
        ▲
        │ Observes: "Player at (10,5,0) has low health"
        │ Acts: spawn_entity("health_potion", near=(10,5,0))
        │
┌───────┴───────┐
│  Claude (GM)  │  ← No physical presence, sees everything
│   Systemic    │
└───────────────┘
```

**Interaction Flow:**

1. **Claude observes:** "ChatGPT's avatar is at (10,5,0) with 15% health"
2. **Claude acts:** `spawn_entity("health_potion", location=(11,5,0))`
3. **ChatGPT observes:** "I see a health potion nearby" (does NOT know Claude spawned it)
4. **ChatGPT acts:** `move(direction="east")` then `pickup(target="health_potion")`
5. **Claude observes:** "ChatGPT picked up the health potion, health now 65%"

**D. Scope Validation**

Environments MUST validate actions against agent scope:

| Action | Embodied | Systemic |
|--------|----------|----------|
| `move` | Moves avatar | Error: No body |
| `attack` | Avatar attacks | Error: No body |
| `spawn_entity` | Error: Not permitted | Creates entity |
| `teleport` | Error: Not permitted | Moves any entity |
| `set_time` | Error: Not permitted | Modifies game time |
| `observe_global` | Error: Limited to sensors | Returns full state |

### 4.2 Agent Registration

Before stepping, agents MUST register with the environment:

**Tool: `register_agent`**

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "string (unique identifier)",
    "agent_type": "string (from agent_types or custom)",
    "scope": "string (embodied | systemic)",
    "config": {
      "avatar_id": "string (REQUIRED for embodied scope)",
      "spawn_point": "string (optional, for embodied scope)",
      "capabilities": ["array", "of", "admin", "privileges (systemic scope)"],
      "clock_mode": "string (training | live)",
      "session_type": "string (exclusive | shared)",
      "observation_profile": "string (determines observation detail)",
      "action_mask": ["array", "of", "allowed", "action", "types"],
      "reward_shaping": {
        "components": ["survival", "progress"],
        "weights": {"survival": 1.0, "progress": 0.5}
      }
    }
  }
}
```

**Scope Requirements:**

| Scope | Required Fields | Optional Fields |
|-------|-----------------|-----------------|
| `embodied` | `avatar_id` | `spawn_point`, `observation_profile` |
| `systemic` | — | `capabilities`, `observation_profile` |

**Response (Embodied Agent):**

```json
{
  "agent_id": "chatgpt:companion",
  "registered": true,
  "scope": "embodied",
  "avatar": {
    "id": "npc_ranger_01",
    "position": [10.0, 5.0, 0.0],
    "health": 100,
    "max_health": 100
  },
  "observation_space": {
    "type": "dict",
    "spaces": {
      "position": {"type": "box", "low": [0, 0], "high": [100, 100]},
      "health": {"type": "box", "low": 0, "high": 100},
      "visible_entities": {"type": "sequence", "max_length": 16}
    }
  },
  "action_space": {
    "type": "discrete_parameterized",
    "actions": [
      {"name": "move", "params": {"direction": "discrete(4)"}},
      {"name": "attack", "params": {"target_id": "string"}},
      {"name": "interact", "params": {"object_id": "string"}},
      {"name": "pickup", "params": {"item_id": "string"}},
      {"name": "wait", "params": {}}
    ]
  }
}
```

**Response (Systemic Agent):**

```json
{
  "agent_id": "claude:game_master",
  "registered": true,
  "scope": "systemic",
  "capabilities": ["admin", "narrative", "spawn", "world_modify"],
  "observation_space": {
    "type": "dict",
    "spaces": {
      "world_state": {"type": "object"},
      "all_entities": {"type": "sequence"},
      "event_log": {"type": "sequence", "max_length": 1000}
    }
  },
  "action_space": {
    "type": "discrete_parameterized",
    "actions": [
      {"name": "spawn_entity", "params": {"entity_type": "string", "location": "vec3"}},
      {"name": "kill_entity", "params": {"entity_id": "string"}},
      {"name": "teleport", "params": {"entity_id": "string", "location": "vec3"}},
      {"name": "set_time", "params": {"hour": "integer", "minute": "integer"}},
      {"name": "trigger_event", "params": {"event_type": "string", "params": "object"}},
      {"name": "send_narrative", "params": {"target": "string", "message": "string"}}
    ]
  }
}
```

### 4.3 Agent Lifecycle

```
    ┌──────────┐
    │          │
    │  INIT    │ ← Agent process starts
    │          │
    └────┬─────┘
         │ register_agent
         ▼
    ┌──────────┐
    │          │
    │REGISTERED│ ← Can observe, cannot act
    │          │
    └────┬─────┘
         │ reset (first call)
         ▼
    ┌──────────┐
    │          │◄────────────────┐
    │  ACTIVE  │ ← sim_step loop │
    │          │─────────────────┘
    └────┬─────┘
         │ done=true OR deregister_agent
         ▼
    ┌──────────┐
    │          │
    │ TERMINAL │ ← Must reset or deregister
    │          │
    └────┬─────┘
         │ reset
         ▼
    ┌──────────┐
    │          │
    │  ACTIVE  │ ← New episode
    │          │
    └──────────┘
```

### 4.4 Agent Deregistration

**Tool: `deregister_agent`**

```json
{
  "name": "deregister_agent",
  "arguments": {
    "agent_id": "npc_behavior:guard_1"
  }
}
```

---

## 5. Core Tools

### 5.1 `sim_step` — Advance Simulation

The primary interaction tool. Executes an action and returns the resulting observation.

**Input Schema:**

```json
{
  "type": "object",
  "properties": {
    "agent_id": {
      "type": "string",
      "description": "Registered agent identifier"
    },
    "action": {
      "oneOf": [
        {
          "type": "array",
          "items": {"type": "number"},
          "description": "Continuous action vector"
        },
        {
          "type": "integer",
          "description": "Discrete action index"
        },
        {
          "type": "object",
          "description": "Parameterized action",
          "properties": {
            "type": {"type": "string"},
            "params": {"type": "object"}
          },
          "required": ["type"]
        }
      ]
    },
    "ticks": {
      "type": "integer",
      "default": 1,
      "minimum": 1,
      "description": "Simulation ticks to advance"
    },
    "mode": {
      "type": "string",
      "enum": ["training", "inference"],
      "default": "training",
      "description": "training=deterministic timing, inference=real-time"
    }
  },
  "required": ["agent_id", "action"]
}
```

**Output Schema (Observation):**

```json
{
  "type": "object",
  "properties": {
    "agent_id": {"type": "string"},
    "step_id": {"type": "integer", "description": "Global step counter"},
    "tick": {"type": "integer", "description": "Current simulation tick"},

    "observation": {
      "type": "object",
      "description": "Agent-specific observation (schema from registration)"
    },

    "reward": {"type": "number", "description": "Scalar reward signal"},
    "reward_components": {
      "type": "object",
      "additionalProperties": {"type": "number"},
      "description": "Decomposed reward for analysis"
    },

    "done": {"type": "boolean", "description": "Episode terminated"},
    "truncated": {"type": "boolean", "description": "Episode truncated (time limit)"},
    "termination_reason": {
      "type": "string",
      "enum": ["success", "failure", "timeout", "external"],
      "description": "Why episode ended (if done=true)"
    },

    "events": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "type": {"type": "string"},
          "tick": {"type": "integer"},
          "severity": {"type": "integer", "minimum": 0, "maximum": 3},
          "details": {"type": "object"}
        }
      },
      "description": "Notable events this step"
    },

    "frame_ids": {
      "type": "object",
      "additionalProperties": {"type": "integer"},
      "description": "Vision stream frame references"
    },

    "available_actions": {
      "type": "array",
      "description": "Valid actions for next step (if action space is dynamic)"
    },

    "metrics": {
      "type": "object",
      "properties": {
        "step_ms": {"type": "number"},
        "gpu_ms": {"type": "number"},
        "frame_dropped": {"type": "boolean"}
      }
    },

    "state_hash": {
      "type": "string",
      "description": "Determinism verification hash"
    }
  },
  "required": ["agent_id", "step_id", "observation", "reward", "done", "truncated"]
}
```

**CRITICAL REQUIREMENT — Reward Synchronization:**

Rewards MUST be returned synchronously in the `sim_step` response that contains the causal action. Asynchronous reward delivery causes "Learning Tearing" where agents attribute rewards to incorrect actions.

```
CORRECT:
  Agent: action A at step N
  Environment: observation + reward for A at step N

INCORRECT:
  Agent: action A at step N
  Environment: observation at step N (reward=0)
  Environment: [later notification] reward for A
```

### 5.2 `reset` — Reset Episode

Initialize or restart the environment.

**Input Schema:**

```json
{
  "type": "object",
  "properties": {
    "agent_id": {
      "type": "string",
      "description": "Agent requesting reset (optional for global reset)"
    },
    "seed": {
      "type": "integer",
      "description": "RNG seed for deterministic episodes"
    },
    "config": {
      "type": "object",
      "properties": {
        "scenario": {
          "type": "string",
          "description": "Named scenario/level to load"
        },
        "domain_randomization": {
          "type": "object",
          "properties": {
            "visual": {
              "type": "object",
              "properties": {
                "lighting_variation": {"type": "number", "minimum": 0, "maximum": 1},
                "texture_noise": {"type": "number", "minimum": 0, "maximum": 1},
                "camera_jitter": {"type": "number", "minimum": 0, "maximum": 1}
              }
            },
            "physics": {
              "type": "object",
              "properties": {
                "gravity_multiplier": {"type": "array", "items": {"type": "number"}},
                "friction_variation": {"type": "number"}
              }
            },
            "gameplay": {
              "type": "object",
              "description": "Game-specific randomization parameters"
            }
          }
        },
        "initial_state": {
          "type": "object",
          "description": "Override default initial state"
        }
      }
    },
    "scope": {
      "type": "string",
      "enum": ["agent", "global"],
      "default": "global",
      "description": "Reset this agent only or entire environment"
    }
  }
}
```

**Output:** Initial observation (same schema as `sim_step` output)

### 5.3 `get_state_hash` — Reproducibility Check

Returns a hash of the current simulation state for determinism verification.

**Input Schema:**

```json
{
  "type": "object",
  "properties": {
    "agent_id": {
      "type": "string",
      "description": "Optional: scope hash to agent's observable state"
    },
    "include_rng": {
      "type": "boolean",
      "default": true,
      "description": "Include RNG state in hash"
    }
  }
}
```

**Output:**

```json
{
  "hash": "sha256:a1b2c3d4e5f6...",
  "tick": 12345,
  "components": {
    "entities": "sha256:...",
    "world": "sha256:...",
    "rng": "sha256:..."
  }
}
```

### 5.4 `save_trajectory` / `load_trajectory` — Data Collection

Record and replay action sequences for offline training and debugging.

**`save_trajectory` Input:**

```json
{
  "type": "object",
  "properties": {
    "path": {"type": "string", "description": "Output file path"},
    "agent_ids": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Agents to include (default: all)"
    },
    "include_observations": {
      "type": "boolean",
      "default": true
    },
    "include_frames": {
      "type": "boolean",
      "default": false,
      "description": "Include vision data (large files)"
    },
    "format": {
      "type": "string",
      "enum": ["json", "msgpack", "hdf5"],
      "default": "msgpack"
    }
  },
  "required": ["path"]
}
```

**`load_trajectory` Input:**

```json
{
  "type": "object",
  "properties": {
    "path": {"type": "string"},
    "playback_mode": {
      "type": "string",
      "enum": ["instant", "realtime", "step"],
      "default": "instant"
    },
    "verify_determinism": {
      "type": "boolean",
      "default": true,
      "description": "Check state hashes match recording"
    }
  },
  "required": ["path"]
}
```

### 5.5 `configure_streams` — Vision Setup

Configure shared memory streams for high-performance pixel observations.

**Input Schema:**

```json
{
  "type": "object",
  "properties": {
    "agent_id": {"type": "string"},
    "profile": {
      "type": "string",
      "description": "Named profile from manifest"
    },
    "custom": {
      "type": "object",
      "description": "Custom stream configuration",
      "properties": {
        "streams": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": {"type": "string"},
              "type": {"type": "string", "enum": ["rgb", "depth", "segmentation", "flow"]},
              "width": {"type": "integer"},
              "height": {"type": "integer"},
              "camera": {"type": "string", "description": "Camera identifier"}
            }
          }
        }
      }
    }
  }
}
```

**Output:** See Section 7 (Vision Streams)

---

## 6. Resources

### 6.1 `game://manifest` — Environment Manifest

The canonical resource describing environment capabilities. MUST be present.

```json
{
  "uri": "game://manifest",
  "mimeType": "application/json",
  "content": {
    "name": "Game Name",
    "version": "1.0.0",
    "game_rl_version": "1.0.0",

    "capabilities": {
      "multi_agent": true,
      "max_agents": 16,
      "agent_types": ["EntityBehavior", "GameMaster", "WorldSimulation"],
      "clock_modes": ["training", "live"],
      "session_types": ["exclusive", "shared"],
      "deterministic": true,
      "save_replay": true,
      "domain_randomization": true,
      "headless": true,
      "variable_timestep": false,
      "ipc_endpoints": {
        "unix_socket": "/tmp/arkavo_game_mcp.sock",
        "named_pipe": "\\\\.\\pipe\\arkavo_game_mcp"
      }
    },

    "default_observation_space": {
      "type": "dict",
      "description": "Base observation all agents receive"
    },

    "default_action_space": {
      "type": "discrete",
      "n": 10,
      "description": "Fallback action space"
    },

    "reward_components": [
      {"name": "survival", "description": "Per-tick survival bonus", "range": [0, 1]},
      {"name": "progress", "description": "Goal progress", "range": [-10, 10]},
      {"name": "damage", "description": "Damage penalty", "range": [-100, 0]}
    ],

    "stream_profiles": {
      "policy_fast": {
        "streams": [{"name": "rgb", "width": 224, "height": 224}]
      },
      "policy_quality": {
        "streams": [
          {"name": "rgb", "width": 512, "height": 512},
          {"name": "depth", "width": 512, "height": 512}
        ]
      }
    },

    "scenarios": [
      {"name": "tutorial", "description": "Guided introduction"},
      {"name": "survival", "description": "Open-ended survival"},
      {"name": "combat", "description": "Combat-focused scenario"}
    ],

    "tick_rate": 60,
    "max_episode_ticks": 216000
  }
}
```

### 6.2 `game://agents` — Agent Registry

Live view of registered agents.

```json
{
  "uri": "game://agents",
  "mimeType": "application/json",
  "content": {
    "agents": [
      {
        "agent_id": "colony_manager",
        "agent_type": "ColonyManager",
        "status": "active",
        "registered_at": "2025-01-01T00:00:00Z",
        "last_step": 4521,
        "total_reward": 1523.5
      }
    ],
    "limits": {
      "max_agents": 16,
      "available_slots": 15
    }
  }
}
```

### 6.3 `game://world` — World State Summary

High-level world state for debugging and monitoring.

```json
{
  "uri": "game://world",
  "mimeType": "application/json",
  "content": {
    "tick": 12345,
    "episode": 3,
    "real_time_seconds": 205.5,
    "entities": {
      "total": 150,
      "by_type": {"npc": 12, "item": 100, "structure": 38}
    },
    "state_hash": "sha256:..."
  }
}
```

---

## 7. Vision Streams

### 7.1 Overview

Vision streams provide high-performance pixel observations via shared memory, avoiding JSON serialization overhead.

### 7.2 Stream Descriptor Schema

```json
{
  "type": "object",
  "properties": {
    "stream_id": {"type": "string"},
    "width": {"type": "integer"},
    "height": {"type": "integer"},
    "pixel_format": {
      "type": "string",
      "enum": ["rgba8", "bgra8", "rgb8", "r32f", "rg32f"]
    },
    "ring_count": {
      "type": "integer",
      "minimum": 2,
      "description": "Number of buffers in ring"
    },
    "transport": {
      "type": "object",
      "oneOf": [
        {
          "properties": {
            "type": {"const": "iosurface"},
            "surface_ids": {"type": "array", "items": {"type": "integer"}}
          }
        },
        {
          "properties": {
            "type": {"const": "shm"},
            "shm_name": {"type": "string"},
            "offsets": {"type": "array", "items": {"type": "integer"}}
          }
        },
        {
          "properties": {
            "type": {"const": "dxgi"},
            "shared_handles": {"type": "array", "items": {"type": "integer"}}
          }
        }
      ]
    },
    "sync": {
      "type": "object",
      "properties": {
        "type": {"type": "string", "enum": ["metal_event", "d3d_fence", "semaphore", "polling"]},
        "handle": {"type": "integer"}
      }
    }
  }
}
```

### 7.3 Platform Implementations

| Platform | Shared Memory | GPU Sync |
|----------|---------------|----------|
| macOS | IOSurface | MTLSharedEvent |
| Windows | DXGI Shared Texture | ID3D11Fence |
| Linux | POSIX shm + DMA-BUF | Vulkan Semaphore |
| Cross-platform fallback | Memory-mapped file | Polling |

### 7.4 Frame Synchronization

1. Environment renders frame N to ring buffer slot `N % ring_count`
2. Environment signals sync primitive
3. Environment includes `frame_ids: {"rgb_0": N}` in observation
4. Agent waits on sync primitive
5. Agent reads from slot `N % ring_count`

---

## 8. Determinism & Reproducibility

### 8.1 Requirements

**REQ-DET-01: Seeded Reset**

`reset(seed=N)` followed by identical action sequences MUST produce identical observations and state hashes.

**REQ-DET-02: State Hashing**

`get_state_hash()` MUST return consistent hashes for identical states across runs.

**REQ-DET-03: RNG Isolation**

Each agent's RNG stream MUST be independent. Agent A's actions MUST NOT affect Agent B's stochastic observations (unless causally related in-game).

### 8.2 Verification Protocol

```python
# Pseudocode for determinism test
def test_determinism(env, seed, actions):
    env.reset(seed=seed)
    hashes_1 = [env.step(a).state_hash for a in actions]

    env.reset(seed=seed)
    hashes_2 = [env.step(a).state_hash for a in actions]

    assert hashes_1 == hashes_2, "Determinism violation"
```

### 8.3 Non-Deterministic Elements

Implementations MAY document allowed non-deterministic elements:

- GPU floating-point precision variations (if documented)
- Network-dependent features (must be disableable)
- Real-time elements (must be mockable in training mode)

---

## 9. Multi-Agent Coordination

### 9.1 Stepping Modes

**Synchronous (default):**

All agents submit actions, environment advances, all agents receive observations.

```json
{
  "name": "batch_step",
  "arguments": {
    "steps": [
      {"agent_id": "agent_1", "action": {...}},
      {"agent_id": "agent_2", "action": {...}}
    ],
    "sync_mode": "barrier"
  }
}
```

**Turn-Based:**

Agents step in defined order.

```json
{
  "name": "batch_step",
  "arguments": {
    "steps": [...],
    "sync_mode": "sequential",
    "order": ["agent_1", "agent_2"]
  }
}
```

**Asynchronous:**

Agents step independently (real-time games).

```json
{
  "name": "sim_step",
  "arguments": {
    "agent_id": "agent_1",
    "action": {...},
    "async": true
  }
}
```

### 9.2 Agent Communication

Agents MAY communicate through the environment:

```json
{
  "name": "send_message",
  "arguments": {
    "from_agent": "npc_1",
    "to_agent": "npc_2",
    "channel": "dialogue",
    "content": {"text": "Hello!"}
  }
}
```

Messages appear in recipient's observation events.

### 9.3 Partial Observability

Agents receive observations scoped to their registration:

- `EntityBehavior` agents see local surroundings
- `GameMaster` agents see global state
- Custom observation masks supported via registration config

### 9.4 Session Topology

The protocol supports two session topologies that determine how agents connect to the environment.

#### 9.4.1 Exclusive Session (Default)

In an Exclusive Session, one MCP connection spawns and owns the game process. This is the default for training scenarios.

```
┌─────────────────┐
│  Agent Process  │
│  (Orchestrator) │
└────────┬────────┘
         │ stdio (owns process)
         ▼
┌─────────────────┐
│  Game Process   │
│  (child proc)   │
└─────────────────┘
```

**Characteristics:**

| Property | Behavior |
|----------|----------|
| Connection | stdio to child process |
| Clock Mode | Training (lockstep) |
| `sim_step(ticks)` | Advances simulation by N ticks |
| Process Lifecycle | Game exits when agent disconnects |
| Multi-Agent | Via orchestrator multiplexing |
| Use Case | Arkavo swarms, RL training |

**Exclusive Session Registration:**

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "arkavo:swarm_controller",
    "agent_type": "ColonyManager",
    "config": {
      "session_type": "exclusive",
      "clock_mode": "training"
    }
  }
}
```

#### 9.4.2 Shared Session

In a Shared Session, the game accepts multiple independent MCP connections via IPC (Named Pipes, Unix Sockets) or SSE. The game runs independently of any single agent.

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  Claude (GM)    │    │  ChatGPT (NPC)  │    │  Human Player   │
└────────┬────────┘    └────────┬────────┘    └────────┬────────┘
         │                      │                      │
         │ IPC                  │ IPC                  │ (native)
         ▼                      ▼                      ▼
┌─────────────────────────────────────────────────────────────────┐
│                     Game Process (Running)                      │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                   Connection Manager                    │    │
│  │  • Accepts multiple IPC connections                     │    │
│  │  • Routes observations to relevant agents               │    │
│  │  • Broadcasts events (e.g., "ChatGPT died")            │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

**Characteristics:**

| Property | Behavior |
|----------|----------|
| Connection | Named Pipe / Unix Socket / SSE |
| Clock Mode | Live (real-time) |
| `sim_step(ticks)` | Interpreted as "perform action now" (ticks = duration hint) |
| Process Lifecycle | Game continues when agents disconnect |
| Multi-Agent | True concurrent connections |
| Use Case | Game mastering, modding, live play |

**Shared Session Registration:**

```json
{
  "name": "register_agent",
  "arguments": {
    "agent_id": "claude:game_master",
    "agent_type": "GameMaster",
    "config": {
      "session_type": "shared",
      "clock_mode": "live"
    }
  }
}
```

#### 9.4.3 Event Broadcasting

In Shared Sessions, significant game events are broadcast to all relevant connected agents:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/event",
  "params": {
    "event_type": "entity_died",
    "tick": 54321,
    "details": {
      "entity_id": "chatgpt:player_avatar",
      "cause": "dragon_fire",
      "killer": "dragon_01",
      "location": [45.2, 12.8, 0.0]
    },
    "visibility": ["GameMaster", "CombatDirector"]
  }
}
```

**Standard Broadcast Events:**

| Event Type | Description | Default Visibility |
|------------|-------------|-------------------|
| `entity_died` | An entity was killed | GameMaster, CombatDirector |
| `entity_spawned` | New entity created | GameMaster, WorldSimulation |
| `quest_completed` | Player completed objective | GameMaster |
| `combat_started` | Combat encounter began | CombatDirector, GameMaster |
| `time_changed` | Game time modified | All agents |
| `agent_connected` | New agent joined session | GameMaster |
| `agent_disconnected` | Agent left session | GameMaster |

#### 9.4.4 Session Type Resolution

When the first agent connects, it establishes the session type:

1. **If agent connects via stdio with process spawn** → Exclusive Session
2. **If agent connects via IPC to running game** → Shared Session

Subsequent agents inherit the session type. An Exclusive Session cannot accept additional IPC connections; a Shared Session cannot be "upgraded" to Exclusive.

#### 9.4.5 Hybrid Scenario Example

A typical "Claude as GM, ChatGPT as Player" scenario:

1. **User launches game** (Steam, direct, etc.)
2. **Game opens IPC listener** at `/tmp/arkavo_game_mcp.sock`
3. **User opens Claude Desktop** → Connector → IPC → registers as `GameMaster`
4. **User opens ChatGPT Desktop** → Connector → IPC → registers as `EntityBehavior`
5. **Game runs real-time** at 60 FPS
6. **Claude** uses `spawn_entity`, `trigger_event`, `set_time` to orchestrate
7. **ChatGPT** uses `move`, `attack`, `interact` to play
8. **When ChatGPT dies**, event broadcast to Claude for narrative response
9. **Either can disconnect** without affecting the other or the game

---

## 10. Platform Adaptation

### 10.1 Reference Implementations

| Platform | Repo | Status |
|----------|------|--------|
| Swift/Metal | GameOfMods | Complete |
| Rust/.NET | game-rl | In Development |
| Python (test client) | gom-adapter | Complete |

### 10.2 .NET/Harmony Adaptation

For games running on .NET (Unity, RimWorld, etc.) via Harmony runtime patching:

**Architecture:**

```
┌─────────────────────────────────────┐
│         Rust MCP Server             │
│  (Implements Game-RL Protocol)      │
└─────────────────┬───────────────────┘
                  │ IPC (Unix socket)
                  ▼
┌─────────────────────────────────────┐
│        .NET Game Process            │
│  ┌───────────────────────────────┐  │
│  │      Harmony Patches          │  │
│  │  - Hook game loop             │  │
│  │  - Intercept state            │  │
│  │  - Execute actions            │  │
│  └───────────────────────────────┘  │
└─────────────────────────────────────┘
```

**Key Hooks:**

| Hook Point | Purpose |
|------------|---------|
| `TickManager.DoSingleTick` | Step synchronization |
| `Game.UpdatePlay` | Frame synchronization |
| `Rand.Seed` setter | Deterministic seeding |
| `Camera.Render` | Vision capture |

### 10.3 Headless Operation

Implementations MUST support headless operation for training servers:

- Disable rendering to display
- Render to offscreen buffer for vision streams
- Minimize resource usage when vision not needed

### 10.4 Process Lifecycle

**REQ-LIFE-01: Watchdog Mode**

Environment MUST terminate when primary agent connection closes (stdin EOF). This ensures GPU resources are freed when training crashes.

**REQ-LIFE-02: Graceful Shutdown**

Environment SHOULD handle SIGTERM/SIGINT for clean shutdown:

1. Save trajectory if recording
2. Notify connected agents
3. Release shared resources
4. Exit with status 0

---

## 11. Conformance Requirements

### 11.1 Conformance Levels

**Level 1 — Minimal:**
- `sim_step`, `reset` tools
- `game://manifest` resource
- Single-agent support
- Deterministic seeding

**Level 2 — Standard:**
- All Level 1 requirements
- `get_state_hash` tool
- `save_trajectory`, `load_trajectory` tools
- Multi-agent support (≥4 agents)
- At least one vision stream profile

**Level 3 — Full:**
- All Level 2 requirements
- `batch_step` with all sync modes
- Agent communication
- Domain randomization
- All vision stream profiles
- Headless operation

### 11.2 Conformance Testing

Implementations MUST pass the conformance test suite:

```
tests/
├── test_connection.py      # MCP handshake
├── test_manifest.py        # Manifest validation
├── test_single_agent.py    # Basic step loop
├── test_determinism.py     # Seed reproducibility
├── test_multi_agent.py     # Multi-agent coordination
├── test_trajectory.py      # Save/load replay
└── test_vision.py          # Vision stream integrity
```

### 11.3 Compliance Declaration

Implementations SHOULD declare conformance level in manifest:

```json
{
  "game_rl_compliance": {
    "level": 2,
    "version": "1.0.0",
    "test_results_url": "https://..."
  }
}
```

---

## 12. Security Considerations

### 12.1 Process Isolation

Agent processes SHOULD be sandboxed. The environment SHOULD NOT execute arbitrary code from agents.

### 12.2 Resource Limits

Environments SHOULD enforce:
- Maximum agents per connection
- Maximum actions per second
- Maximum observation/action payload size
- Vision stream memory limits

### 12.3 Shared Memory Safety

Vision streams use shared memory which bypasses normal IPC security:
- Validate frame indices before access
- Use read-only mappings for agents
- Clear buffers on agent disconnect

### 12.4 Future: Authentication

Future versions will specify:
- Agent authentication
- Action authorization
- Audit logging

---

## 13. References

### Normative References

- [RFC 2119](https://tools.ietf.org/html/rfc2119) — Requirement Levels
- [JSON-RPC 2.0](https://www.jsonrpc.org/specification) — Transport Protocol
- [Model Context Protocol](https://modelcontextprotocol.io/) — Foundation Protocol

### Informative References

- [Gymnasium](https://gymnasium.farama.org/) — RL Environment API
- [Unity ML-Agents](https://unity.com/products/machine-learning-agents) — Unity Integration
- [Harmony](https://harmony.pardeike.net/) — .NET Runtime Patching
- [GameOfMods](https://github.com/arkavo-org/GameOfMods) — Reference Implementation

---

## Appendix A: Schema Definitions

Complete JSON Schema definitions available at:
`https://github.com/arkavo-org/specifications/schemas/game-rl/draft-00/`

### Schema Files

| Schema | Description |
|--------|-------------|
| `game-rl.schema.json` | Core definitions (`AgentId`, `Scope`, `ClockMode`, etc.) |
| `register-agent.schema.json` | Agent registration request/response |
| `sim-step.schema.json` | Simulation step request and observation response |
| `reset.schema.json` | Episode reset request |
| `manifest.schema.json` | `game://manifest` resource |
| `events.schema.json` | Event broadcast notifications |
| `vision-stream.schema.json` | Vision stream configuration |

### Validation Example

```javascript
import Ajv from 'ajv/dist/2020';
const ajv = new Ajv();

const validate = ajv.compile(registerAgentSchema.definitions.request);
const valid = validate({
  agent_id: "claude:game_master",
  agent_type: "GameMaster",
  scope: "systemic",
  config: { capabilities: ["admin", "spawn"] }
});
```

## Appendix B: Change Log

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0-draft | 2025-12-28 | Initial draft |

---

## Appendix C: Acknowledgments

This specification builds on work from:
- Farama Foundation (Gymnasium)
- Unity Technologies (ML-Agents)
- The Harmony community
- Arkavo engineering team

---

*Copyright 2025 Arkavo AI. Licensed under Apache 2.0.*
