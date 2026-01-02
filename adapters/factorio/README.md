# GameRL Factorio Adapter

Multi-agent RL training infrastructure for Factorio 2.0.

## Quick Start (Interactive Play)

### 1. Install the Mod

**Option A: Symlink (for development)**
```bash
# macOS
ln -s "$(pwd)/adapters/factorio" ~/Library/Application\ Support/factorio/mods/gamerl_0.5.0

# Linux
ln -s "$(pwd)/adapters/factorio" ~/.factorio/mods/gamerl_0.5.0
```

**Option B: Copy**
```bash
# macOS
cp -r adapters/factorio ~/Library/Application\ Support/factorio/mods/gamerl_0.5.0

# Linux
cp -r adapters/factorio ~/.factorio/mods/gamerl_0.5.0

# Windows
copy adapters\factorio %APPDATA%\Factorio\mods\gamerl_0.5.0
```

### 2. Enable RCON

In Factorio's **main menu**, access the hidden settings:

| Platform | How to open |
|----------|-------------|
| **Windows/Linux** | Hold **Ctrl+Alt** and click **Settings** |
| **macOS** | Hold **Cmd+Option** and click **Settings** |

Then:
1. Go to the **"The rest"** tab
2. Set `local-rcon-socket` = `127.0.0.1:27015`
3. Set `local-rcon-password` = `gamerl`
4. Click OK

> **Note:** RCON is only active when hosting a multiplayer game (even solo).
> In Factorio: **Play → Multiplayer → Host new game** or **Host saved game**.

### 2b. Configure Mod Settings (Optional)

In **Settings → Mod Settings → Map**, you can configure:
- **RCON Password** - Must match what you set in "The rest" settings
- **RCON Port** - Must match `local-rcon-socket` port
- **Observation Interval** - How often to write game state (default: 60 ticks = 1 second)
- **Max Entities** - Limit entities per observation for performance

<details>
<summary>Alternative: Edit config.ini directly</summary>

**macOS:** `~/Library/Application Support/factorio/config/config.ini`
**Linux:** `~/.factorio/config/config.ini`
**Windows:** `%APPDATA%\Factorio\config\config.ini`

```ini
local-rcon-socket=127.0.0.1:27015
local-rcon-password=gamerl
```
</details>

### 3. Start Claude Code with Game-RL

```bash
# Add the MCP server (one time)
claude mcp add game-rl -- cargo run -p game-rl-cli --release

# Start Claude Code
claude
```

### 4. Play!

Start Factorio, host a multiplayer game (can be solo), and Claude can now observe and interact with your factory.

---

## Headless Server (Training)

For RL training without GUI:

```bash
# Start headless server with RCON
factorio --start-server save.zip \
    --rcon-port 27015 \
    --rcon-password gamerl

# Or with full paths on macOS:
"/Users/$USER/Library/Application Support/Steam/steamapps/common/Factorio/factorio.app/Contents/MacOS/factorio" \
    --start-server "/Users/$USER/Library/Application Support/factorio/saves/mysave.zip" \
    --rcon-port 27015 \
    --rcon-password gamerl
```

---

## Troubleshooting

### "Waiting for game..." message

The MCP server can't connect to Factorio. Check:

1. **Is Factorio running?** Start the game first
2. **Is RCON enabled?** Edit `config.ini` as shown above
3. **Are you hosting multiplayer?** RCON only works in multiplayer mode
4. **Is GameRL mod enabled?** Check Mods menu in Factorio

### Test RCON manually

```bash
# Check if RCON port is open
nc -zv 127.0.0.1 27015

# If "Connection refused" - RCON is not enabled
# If "succeeded" - RCON is working
```

### Check mod is loaded

In Factorio console (~ key):
```
/c rcon.print(remote.interfaces['gamerl'] and 'ok' or 'no')
```

---

## Usage

### Observations

The mod writes game state to `script-output/gamerl/observation.json`:

```json
{
    "tick": 12345,
    "global": {
        "evolution_factor": 0.15,
        "research": {"current": "automation-2", "progress": 0.45},
        "power": {"production": 5000, "consumption": 4500, "satisfaction": 1.0}
    },
    "agents": {
        "factory_1": {
            "entities": [...],
            "resources": {"iron-ore": 10000, "copper-ore": 8000},
            "reward": 1.5
        }
    }
}
```

### Action Types

Actions use `{"Type": "ActionName", ...params}`. Parameters accept both PascalCase and lowercase.

#### Core Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `Noop` / `Wait` | - | Do nothing |
| `Build` | `entity`, `position`, `direction?` | Place entity at [x,y]. direction: 0=N, 2=E, 4=S, 6=W |
| `Mine` | `entity_id` or `position` | Remove an entity |
| `SetRecipe` | `entity_id`, `recipe` | Set assembler/furnace recipe |
| `RotateEntity` | `entity_id` | Rotate entity 90° |
| `StartResearch` | `technology` | Begin researching technology |

#### Combat Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `Attack` | `position?`, `damage?`, `damage_type?` | Attack enemy (physical/fire/explosion) |
| `AttackArea` | `position`, `radius?`, `damage?` | AoE damage (default radius 10) |
| `SpawnEnemy` | `position`, `enemy_type?`, `count?` | Spawn biters for testing |
| `BuildTurret` | `position`, `turret_type?` | Place turret (gun/laser/flamethrower) |

#### Logistics Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `TransferItems` | `from_id`, `to_id`, `item?`, `count?` | Move items between inventories |
| `InsertItems` | `entity_id`, `item`, `count?` | Add items to entity |
| `SetFilter` | `entity_id`, `slot`, `item` | Set inserter/container filter |
| `ConnectWire` | `from_id`, `to_id`, `wire_type?` | Connect circuit wire (red/green) |
| `DeconstructArea` | `position`, `radius` | Mark area for deconstruction |

#### Train Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `AddTrainSchedule` | `train_id`, `station` | Add station to schedule |
| `ClearTrainSchedule` | `train_id` | Clear train schedule |
| `SetTrainManual` | `train_id`, `manual` | Toggle manual mode |

#### Blueprint Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `CreateBlueprint` | `position`, `radius` | Capture blueprint from area |
| `PlaceBlueprint` | `blueprint`, `position`, `direction?` | Place blueprint |

#### Utility Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `Teleport` | `position` | Move player character |
| `ChartArea` | `position`, `radius?` | Reveal map (default radius 100) |
| `SetSpeed` | `speed` | Set game speed multiplier |
| `SpawnResource` | `position`, `resource_type`, `amount?` | Create resource patch |
| `RepairEntity` | `entity_id` | Restore entity to full health |

#### Circuit Network Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `ReadCircuitSignals` | `entity_id` | Read circuit network signals |
| `SetCombinatorSignal` | `entity_id`, `signals` | Set constant combinator |
| `ConfigureDecider` | `entity_id`, `conditions`, `output` | Configure decider combinator |
| `ConfigureArithmetic` | `entity_id`, `operation`, `first`, `second`, `output` | Configure arithmetic combinator |

#### Vehicle Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `EnterVehicle` | `vehicle_id` | Player enters vehicle |
| `ExitVehicle` | - | Player exits vehicle |
| `SetSpidertronWaypoint` | `entity_id`, `position` | Add spidertron waypoint |

#### Advanced Actions
| Action | Parameters | Description |
|--------|------------|-------------|
| `LaunchRocket` | `silo_id` | Launch rocket from silo |
| `FireArtillery` | `entity_id`, `position` | Fire artillery at target |
| `InsertModule` | `entity_id`, `module` | Insert module into machine |
| `PlaceTiles` | `tiles` | Place multiple tiles |

### Common Entity Names

```
transport-belt, fast-transport-belt, express-transport-belt
inserter, fast-inserter, stack-inserter
assembling-machine-1, assembling-machine-2, assembling-machine-3
electric-mining-drill, stone-furnace, steel-furnace, electric-furnace
solar-panel, accumulator, small-electric-pole, medium-electric-pole
pipe, pipe-to-ground, offshore-pump, boiler, steam-engine
lab, radar, roboport, logistic-chest-passive-provider
```

### Agent Types

| Type | Description |
|------|-------------|
| `Observer` | Watch game state without acting |
| `Player` | Control player character directly |
| `Controller` | High-level factory planning and logistics (recommended) |
| `Director` | Event and scenario management |

---

## Architecture

```
Claude Code ──MCP──► game-rl-server ──RCON──► Factorio
                            │                    │
                            │                    ▼
                            ◄────file──── script-output/gamerl/observation.json
```

- **Commands:** Sent via RCON (`remote.call("gamerl", ...)`)
- **Observations:** Written to file by the mod, read by the bridge

## Requirements

- Factorio 2.0.72+
- Rust toolchain (for building game-rl)
- Claude Code
