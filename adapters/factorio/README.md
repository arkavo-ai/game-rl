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

Edit your Factorio config to enable RCON for interactive play:

**macOS:** `~/Library/Application Support/factorio/config/config.ini`
**Linux:** `~/.factorio/config/config.ini`
**Windows:** `%APPDATA%\Factorio\config-path.cfg` (points to config location)

Find these lines and update them:
```ini
; Socket to host RCON on when launching MP server from the menu.
local-rcon-socket=127.0.0.1:27015

; Password for RCON when launching MP server from the menu.
local-rcon-password=gamerl
```

> **Note:** RCON is only active when hosting a multiplayer game (even solo).
> In Factorio: **Play → Multiplayer → Host new game** or **Host saved game**.

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

| Action | Parameters | Description |
|--------|------------|-------------|
| `Build` | `entity`, `position`, `direction` | Place an entity |
| `Mine` | `entity_id` or `position` | Remove an entity |
| `SetRecipe` | `entity_id`, `recipe` | Set assembler recipe |
| `RotateEntity` | `entity_id` | Rotate an entity |
| `StartResearch` | `technology` | Begin research |
| `Noop` | (none) | Take no action |

### Agent Types

| Type | Description |
|------|-------------|
| `StrategyController` | High-level factory planning |
| `ColonyManager` | Resource and logistics management |
| `CombatDirector` | Military operations |

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
