# GameRL Factorio Adapter

Multi-agent RL training infrastructure for Factorio 2.0.

## Requirements

- Factorio 2.0.72 or later
- Factorio headless server (for training)
- game-rl CLI

## Installation

### 1. Install the Mod

Copy the `factorio` folder to your Factorio mods directory:

```bash
# macOS
cp -r adapters/factorio ~/Library/Application\ Support/factorio/mods/gamerl_0.5.0

# Linux
cp -r adapters/factorio ~/.factorio/mods/gamerl_0.5.0

# Windows
copy adapters\factorio %APPDATA%\Factorio\mods\gamerl_0.5.0
```

Or create a symlink for development:

```bash
# macOS
ln -s "$(pwd)/adapters/factorio" ~/Library/Application\ Support/factorio/mods/gamerl_0.5.0

# Linux
ln -s "$(pwd)/adapters/factorio" ~/.factorio/mods/gamerl_0.5.0
```

### 2. Configure Headless Server

Create a server configuration with RCON enabled:

```bash
# Create server settings
cat > server-settings.json << 'EOF'
{
    "name": "GameRL Training",
    "description": "RL training server",
    "visibility": {"public": false, "lan": false},
    "require_user_verification": false,
    "autosave_interval": 0,
    "autosave_slots": 1
}
EOF
```

### 3. Start Factorio with RCON

```bash
# Start headless server
./factorio --start-server save.zip \
    --rcon-port 27015 \
    --rcon-password gamerl \
    --server-settings server-settings.json

# Or with the GUI for debugging
./factorio --load save.zip \
    --rcon-port 27015 \
    --rcon-password gamerl
```

### 4. Start the MCP Server

```bash
cargo run --bin game-rl-cli -- --game factorio
```

### 5. Connect with Claude Code

```bash
claude mcp add --transport stdio game-rl -- /path/to/game-rl-cli --game factorio
```

## Usage

### Register an Agent

```lua
-- Via RCON
/c remote.call("gamerl", "register_agent", "factory_1", "FactoryManager", {})
```

### Execute Actions

```lua
-- Build an assembler
/c remote.call("gamerl", "step", "factory_1", '{"type":"Build","entity":"assembling-machine-1","position":[10,20]}', 60)

-- Set recipe
/c remote.call("gamerl", "step", "factory_1", '{"type":"SetRecipe","entity_id":123,"recipe":"iron-gear-wheel"}', 60)

-- Start research
/c remote.call("gamerl", "step", "factory_1", '{"type":"StartResearch","technology":"automation-2"}', 60)
```

### Observations

Observations are written to `script-output/gamerl/observation.json`:

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
            "enemies": [],
            "reward": 1.5,
            "reward_components": {"research_progress": 0.45, "power_satisfaction": 1.0}
        }
    }
}
```

## Agent Types

| Type | Description | Max Per Game |
|------|-------------|--------------|
| `FactoryManager` | High-level factory control | 1 |
| `SectionController` | Controls a factory section | 8 |
| `CombatController` | Military operations | 1 |

## Action Types

| Action | Parameters | Description |
|--------|------------|-------------|
| `Build` | `entity`, `position`, `direction` | Place an entity |
| `Mine` | `entity_id` or `position` | Remove an entity |
| `SetRecipe` | `entity_id`, `recipe` | Set assembler recipe |
| `RotateEntity` | `entity_id` | Rotate an entity |
| `StartResearch` | `technology` | Begin research |
| `Noop` | (none) | Take no action |

## Scenarios

| Scenario | Description |
|----------|-------------|
| `factory_optimization` | Maximize science per minute |
| `curriculum_mining` | Learn mining and smelting |
| `curriculum_automation` | Build assembly lines |
| `curriculum_logistics` | Set up belt networks |
| `defense_wave` | Survive biter attacks |

## Deterministic Training

Factorio provides deterministic simulation when:

1. Using the same save file
2. Same mod configuration
3. Same action sequence

Use `get_state_hash` to verify determinism across runs.

## Performance Tips

1. Use headless mode for faster training
2. Increase game speed: `game.speed = 10`
3. Limit observation scope with `bounds`
4. Use `tick_paused` + `ticks_to_run` for precise stepping
