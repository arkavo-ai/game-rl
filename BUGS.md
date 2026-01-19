# Game-RL MCP Bugs and Issues

## Status Summary (Updated 2026-01-07)

### RESOLVED
- **#0, #1**: Action schema now provided on `register_agent` response
- **#3**: Player agent returns rich observations (entities, enemies, resources)
- **#4**: Invalid actions now return error in `action_result` field
- **#8, #11**: Core actions now work (Build, Mine, RotateEntity, SetRecipe, etc.)
- **#12**: SpawnEnemy now working
- **#15**: research field now populated
- **#16**: production field now populated
- **#17**: Entity inventories now observable
- **#18**: Spawned enemies now appear in enemies observation
- **#19**: AttackArea error message fixed
- **#6**: All agent types now get consistent observations
- **#7**: Player character detection improved, Teleport works in headless mode
- **#13**: Parallel sim_step race conditions fixed with retry logic
- **#14**: Player positions now in global observation (SetSpeed already worked)

### STILL OPEN
- **#23**: game_speed not exposed in global observation
- **#24**: Factorio 2.0 API changes cause production stats errors
- **#26**: TransferItems inventory type mapping broken

### RESOLVED (2026-01-07)
- **#20**: InsertItems contradictory error - FIXED (Lua ternary pattern bug)
- **#21**: SpawnResource contradictory error - FIXED (Lua ternary pattern bug)
- **#22**: Reset breaks agent observations - FIXED (agents now persist across resets)
- **#25**: InsertItems doesn't support lab_input inventory - FIXED (added lab_input to inventory chain)
- **#27**: StartResearch fails silently - FIXED (auto-complete trigger techs, better diagnostics)
- **#28**: Factorio 2.0 trigger technologies not handled - FIXED (auto-detected and completed)

---

## Resolved Issues

### ~~0. Missing MCP Documentation~~ RESOLVED
`register_agent` now returns comprehensive ActionSpace with:
- All action types organized by category (core, combat, logistics, utility, etc.)
- Required parameters for each action
- Example usage
- Common entity names list

### ~~1. No Action Space Documentation~~ RESOLVED
Same as above - full action schema provided on registration.

### ~~3. Minimal Observation Space~~ RESOLVED (for Player agent)
Player agent now returns:
- `entities`: Array with id, name, position, health, direction, recipe, energy
- `enemies`: Array of nearby enemy units/structures
- `resources`: Map of resource counts in logistics network
- `global`: evolution_factor, pollution, power stats

### ~~4. No Feedback on Invalid Actions~~ RESOLVED
Error messages now returned in `action_result` field of observation.

**Fix**: Added `prototypes.entity[name]` validation + error messages in Lua.

### ~~8. All Build/Place Actions Silently Fail~~ RESOLVED
Build actions now work with correct format:
```json
{"Type": "Build", "entity": "solar-panel", "position": [0, 0]}
{"Type": "Build", "entity": "transport-belt", "position": [5, 0], "direction": 2}
```

### ~~11. No Actions Actually Implemented~~ RESOLVED
**Verified working actions:**
- `Build` - creates entities
- `Mine` - removes entities by ID or position
- `RotateEntity` - changes entity direction
- `SetRecipe` - sets assembler recipe
- `SpawnResource` - creates resource patches
- `DamageEntity` - reduces entity health
- `RepairEntity` - restores entity health
- `BuildTurret` - places turrets
- `SpawnEnemy` - spawns enemy units

### ~~12. SpawnEnemy Action Does Not Work~~ RESOLVED
**Fix**: Fixed to use count parameter correctly, added entity validation.

### ~~15. research Field Always Null~~ RESOLVED
Research field now returns structured data:
```json
{"completed": {}, "current": null, "progress": 0.0, "queue": {}, "researched_count": 0}
```

### ~~16. production Field Always Null~~ RESOLVED
Production field now returns structured data:
```json
{"items_produced": {}, "items_consumed": {}, "fluids_produced": {}}
```
Note: `api_errors` field indicates some statistics require additional Lua API access.

### ~~17. Entity Inventories Not Observable~~ RESOLVED
Entities now include `inventory` field showing contents:
```json
{"id": 786, "name": "iron-chest", "inventory": {"iron-plate": 100.0}}
```

### ~~18. Spawned Enemies Don't Appear in Enemies Observation~~ RESOLVED
**Fix**: Changed `extract_enemies` to search a 200-tile radius around player position (or origin) when no bounds specified, instead of searching the entire surface which found distant bases first.

Enemies now include `id` field for tracking:
```json
{"id": 123, "type": "small-biter", "position": {"x": 10, "y": 5}, "health": 1.0}
```

### ~~19. AttackArea Returns Misleading Error Message~~ RESOLVED
**Fix**: Fixed Lua ternary pattern `count > 0 and nil or "error"` which incorrectly returns the error when `nil` is the intended success value. Now uses explicit if/else.

### ~~6. Inconsistent Observations Between Agent Types~~ RESOLVED
**Fix**: Updated Rust `to_observation()` to always include `entities`, `resources`, and `enemies` fields. Falls back to any available agent data or empty arrays if agent not found.

### ~~7. Character Entity Missing From Observations~~ RESOLVED
**Fix**:
1. Added `players` array to global observation with position, health, connected status for ALL players (not just connected)
2. Fixed `Teleport` action to handle headless/god mode - tries `player.teleport()` if character unavailable
3. Players now visible even without active character

Global observation now includes:
```json
{"players": [{"index": 1, "name": "player", "connected": false, "position": {"x": 0, "y": 0}}]}
```

### ~~13. Parallel sim_step Calls Return Incomplete Observations~~ RESOLVED
**Fix**:
1. Lua: Uses per-agent observation files (`observation_{agent_id}.json`)
2. Rust: Added retry logic (3 attempts with 10ms delay) for JSON parse failures due to partial writes
3. Factorio's `write_file` with `append=false` is atomic, but read timing could still cause issues

### ~~14. Unverifiable Actions~~ RESOLVED
**Fix**:
- `SetSpeed`: Already worked - `game_speed` was in global observation
- `Teleport`: Now verifiable via `global.players[].position`
- `ChartArea`: Low priority - requires tracking charted chunks

---

## Infrastructure Fixes (2026-01-02)

### RCON Duplicate Commands
**Location**: `rcon.rs:235-247`
**Issue**: Erroneous retry on empty response caused duplicate command execution.
**Fix**: Removed retry logic.

### Mine by EntityId Type Safety
**Location**: `control.lua`
**Issue**: EntityId passed as string instead of number.
**Fix**: Added `tonumber()` conversion.

---

## Verified Working Actions

| Category | Action | Status | Notes |
|----------|--------|--------|-------|
| Core | Build | ✅ | `{"Type": "Build", "entity": "...", "position": [x,y]}` |
| Core | Mine (position) | ✅ | `{"Type": "Mine", "position": [x,y]}` |
| Core | Mine (EntityId) | ✅ | `{"Type": "Mine", "entity_id": 123}` |
| Core | RotateEntity | ✅ | Direction changes confirmed |
| Core | SetRecipe | ✅ | Recipe field updates |
| Core | Noop/Wait | ✅ | Time advances |
| Utility | SpawnResource | ✅ | Resource counts increase |
| Utility | DamageEntity | ✅ | Health decreases |
| Utility | RepairEntity | ✅ | Health restores to 1.0 |
| Combat | BuildTurret | ✅ | Turret entities created |
| Combat | SpawnEnemy | ✅ | Enemies spawn and attack (verified by turret combat) |
| Combat | AttackArea | ✅ | Damages enemies in radius |
| Logistics | InsertItems | ✅ | Items added, verified via inventory field |
| Utility | Teleport | ✅ | Verify via global.players[].position |
| Utility | SetSpeed | ✅ | Verify via global.game_speed |

## Actions Needing Verification

| Action | Issue |
|--------|-------|
| TransferItems | Need source/destination entities |
| ConnectWire | Need circuit network entities |
| ChartArea | No charted_chunks observation field (low priority) |

## Verified Working Actions (cont.)

| Category | Action | Status | Notes |
|----------|--------|--------|-------|
| Research | StartResearch | ✅ | Auto-completes trigger techs, queues regular techs |

---

## Issues Found and Fixed (2026-01-07)

### ~~20. InsertItems Returns Contradictory Error Message~~ RESOLVED
**Status**: FIXED (2026-01-07)
**Location**: `control.lua` InsertItems handler (line 867)

**Root Cause**: Lua ternary pattern `condition and nil or "error"` fails when the "true" value is `nil` because `nil` is falsy. The expression `true and nil or "error"` evaluates to `"error"`.

**Fix**: Replaced all 28 instances of this pattern with explicit if/else blocks.

### ~~21. SpawnResource Returns Contradictory Error Message~~ RESOLVED
**Status**: FIXED (2026-01-07)
**Location**: `control.lua` SpawnResource handler (line 1537)

**Root Cause**: Same Lua ternary pattern bug as #20.

**Fix**: Same fix as #20 - replaced with explicit if/else.

### ~~22. Reset Breaks Agent Observations~~ RESOLVED
**Status**: FIXED (2026-01-07)
**Location**: `control.lua:2401` reset handler

**Root Cause**: The reset function was clearing `storage.gamerl.agents = {}`, which removed all agent registrations from Lua while Rust still had them registered.

**Fix**: Removed the agent clearing line. Agents now persist across episode resets, which is the correct behavior - only episode-related state (seed, scenario) should be reset.

**File Changed**: `adapters/factorio/control.lua`

### 23. game_speed Not Exposed in Observation
**Severity**: Low (informational)
**Location**: `control.lua` get_force_stats or observation extraction

**Issue**: `SetSpeed` action works correctly, but `game_speed` is not included in `global` observation, making it unverifiable without external means.

**Suggestion**: Add `game_speed` field to global observation:
```json
{"global": {"game_speed": 2.0, "evolution_factor": 0.06, ...}}
```

### 24. Factorio 2.0 API Changes Cause Production Stats Errors
**Severity**: Low (gracefully handled)
**Location**: `control.lua` get_force_stats

**Symptoms**: `api_errors` field in production observation shows:
```json
{"api_errors": ["LuaForce missing: item_production_statistics", "LuaForce missing: fluid_production_statistics"]}
```

**Cause**: Factorio 2.0 changed the production statistics API. The code gracefully handles this with empty objects, but stats are unavailable.

**Fix**: Update to use Factorio 2.0's new production statistics API.

### 25. InsertItems Doesn't Support Lab Input Inventory
**Severity**: Medium
**Status**: FIXED (2026-01-07, pending reload)
**Location**: `control.lua:870-873` InsertItems handler

**Symptoms**: InsertItems fails with "Failed to insert items" when trying to insert science packs into labs.

**Root Cause**: The InsertItems action only checks for `defines.inventory.chest`, `defines.inventory.assembling_machine_input`, and `defines.inventory.furnace_source` inventories. Labs use `defines.inventory.lab_input` which was not included.

**Fix**: Added `defines.inventory.lab_input` to the inventory type chain:
```lua
inv = entity.get_inventory(defines.inventory.chest) or
      entity.get_inventory(defines.inventory.assembling_machine_input) or
      entity.get_inventory(defines.inventory.furnace_source) or
      entity.get_inventory(defines.inventory.lab_input)
```

**Note**: Fix requires Factorio mod reload to take effect.

### 26. TransferItems Inventory Type Mapping Broken
**Severity**: Medium
**Location**: `control.lua:775-784` TransferItems handler

**Symptoms**: TransferItems fails with "Source entity has no inventory" even when source entity (like iron-chest) clearly has items.

**Root Cause**: The `inv_map` table uses Lua `or` chains incorrectly:
```lua
input = defines.inventory.assembling_machine_input or defines.inventory.furnace_source or defines.inventory.chest,
```
This always returns the first truthy value (`assembling_machine_input`), not a fallback chain. When used on a chest, it fails because chests don't have `assembling_machine_input` inventory.

**Workaround**: Explicitly specify `from_inventory: "chest"` when transferring from chests.

**Fix**: Rewrite to try multiple inventory types like InsertItems does, or use a function that tests entity type.

### ~~27. StartResearch Returns Success But Research Doesn't Start~~ RESOLVED
**Severity**: Medium
**Status**: FIXED (2026-01-07)
**Location**: `control.lua:542-656` StartResearch handler

**Symptoms**: StartResearch action returns `success: true` but `research.current` remains null and `research.progress` stays at 0.

**Root Cause**: Factorio 2.0 introduced "trigger technologies" that don't use science packs - they complete when the player performs specific in-game actions (build boiler for steam-power, craft circuit for electronics). These techs have `research_trigger` instead of `research_unit_ingredients` in their prototype.

**Tech Tree**: steam-power + electronics → automation-science-pack → automation

**Fix**:
1. Auto-detect trigger technologies via `prototypes.technology[name].research_trigger`
2. Auto-complete trigger techs when prerequisites are met (since they can't be researched via labs)
3. Added detailed diagnostic messages showing missing prerequisites
4. Added queue verification after `add_research()` call

**New Behavior**:
- Trigger techs: Auto-completed with message "trigger_tech_completed: X (normally unlocked by in-game action)"
- Regular techs: Added to research queue, labs consume science packs
- `force_complete=true` parameter available to bypass all checks

### ~~28. Factorio 2.0 Trigger Technologies Not Handled~~ RESOLVED
**Severity**: High
**Status**: FIXED (2026-01-07)
**Location**: `control.lua:542-656` StartResearch handler

**Symptoms**: Early-game research (automation) shows as "Unavailable" in Factorio UI. StartResearch for prerequisite techs like "steam-power" silently succeeds but doesn't actually start research.

**Root Cause**: Factorio 2.0 redesigned early-game tech tree with "trigger technologies":
- `steam-power` - Unlocks when player builds a boiler/steam-engine
- `electronics` - Unlocks when player crafts an electronic circuit

These are tutorial techs that guide new players. They don't require science packs and can't be researched via labs.

**Fix**: StartResearch now:
1. Detects trigger techs via `prototypes.technology[name].research_trigger`
2. Auto-completes them since they can't be researched normally
3. Checks prerequisites before auto-completing
4. Returns clear message indicating what happened

**Example**:
```json
// Request
{"Type": "StartResearch", "technology": "steam-power"}

// Response (trigger tech auto-completed)
{"success": true, "error": "trigger_tech_completed: steam-power (normally unlocked by in-game action)"}
```
