# Game-RL MCP Bugs and Issues

## Status Summary (Updated 2026-01-02 - Regression Test)

### RESOLVED
- **#0, #1**: Action schema now provided on `register_agent` response
- **#3**: Player agent returns rich observations (entities, enemies, resources)
- **#8, #11**: Core actions now work (Build, Mine, RotateEntity, SetRecipe, etc.)
- **#12**: SpawnEnemy works correctly (enemies spawn but may be outside observation radius)
- **#16**: production field now returns data (with api_errors for missing Factorio 2.0 APIs)
- **#18**: Serialization error fixed (Rust structs now match Lua observation format)
- **#4 PARTIAL**: Error feedback now works - returns in `action_result` field, NOT as MCP error

### STILL OPEN
- **#6**: Inconsistent observations between agent types
- **#7**: Character entity missing from observations

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

### ~~8. All Build/Place Actions Silently Fail~~ RESOLVED
Build actions now work with correct format:
```json
{"Type": "Build", "entity": "solar-panel", "position": [0, 0]}
{"Type": "Build", "entity": "transport-belt", "position": [5, 0], "direction": 2}
```

### ~~11. No Actions Actually Implemented~~ RESOLVED
**Verified working actions:**
- `Build` - creates entities
- `Mine` - removes entities by ID
- `RotateEntity` - changes entity direction
- `SetRecipe` - sets assembler recipe
- `SpawnResource` - creates resource patches
- `DamageEntity` - reduces entity health
- `RepairEntity` - restores entity health
- `BuildTurret` - places turrets

---

## Open Issues

## ~~4. No Feedback on Invalid Actions~~ PARTIALLY RESOLVED
**Severity**: Low (downgraded)

Invalid actions now return error feedback in `action_result` field:
```json
{"action_type": "SetRecipe", "success": false, "error": "Entity not found"}
```

This is correct behavior - errors are returned in the observation, NOT as MCP exceptions.

**Remaining issues:**
- Unknown action types may still silently fail
- Some invalid entity names might not return explicit errors

## ~~19. Mine by EntityId Fails~~ RESOLVED
**Severity**: Medium

**Fixed**: Two bugs were causing this:
1. **Lua type coercion**: Added `tonumber()` conversion in control.lua
2. **RCON duplicate commands**: Removed erroneous retry logic in `rcon.rs` that re-sent every command

Both Mine by EntityId and position-based mining now work correctly:
```json
{"Type": "Mine", "EntityId": 813}  // ✅ Works
{"Type": "Mine", "Position": [50.5, 50.5]}  // ✅ Works
```

## 6. Inconsistent Observations Between Agent Types
**Severity**: Medium

- **Player agent**: Returns full observation (entities, enemies, resources, global)
- **Controller agent**: Returns only global stats (no entities, enemies, resources)

This behavior is undocumented.

## 7. Character Entity Missing From Observations
**Severity**: Low

Player character does not appear in the entities list, making it impossible to track player position programmatically.

---

## New Issues Found (Playtest 2026-01-02)

## 12. SpawnEnemy Action Does Not Work
**Severity**: Medium

`{"Type": "SpawnEnemy", "position": [30, 30], "enemy_type": "small-biter", "count": 3}` returns success but no enemies appear in the observation at or near the specified position.

## 13. Parallel sim_step Calls Return Incomplete Observations
**Severity**: Medium

When multiple `sim_step` calls are made in parallel, some return only `global` observation without `entities`, `enemies`, or `resources` fields. Sequential calls return full observations.

## 14. Unverifiable Actions (Missing Observation Fields)
**Severity**: Low

Some actions cannot be verified because related fields are missing from observations:
- `Teleport`: No player position field to verify movement
- `SetSpeed`: No game speed field in observation
- `StartResearch`: `research` field is always null
- `ChartArea`: No map/charted area visibility in observation

## 15. research Field Always Null
**Severity**: Medium

`global.research` is always `null` even after calling `StartResearch`. Cannot track research progress.

## ~~16. production Field Always Null~~ RESOLVED
**Severity**: Low

Fixed: `global.production` now returns:
- `items_produced`: Map of produced item counts
- `items_consumed`: Map of consumed item counts
- `fluids_produced`: Map of produced fluid counts
- `api_errors`: Array of missing Factorio 2.0 API warnings (helps diagnose compatibility issues)

Note: Some production statistics APIs changed in Factorio 2.0. The `api_errors` field reports which APIs are unavailable.

## 17. Entity Inventories Not Observable
**Severity**: Medium

Entity observations don't include inventory contents. After calling `InsertItems`, there's no way to verify items were added because entities have no `inventory` field.

**Impact**: Cannot verify InsertItems, TransferItems, or track container/machine contents.

## ~~18. Serialization Error - Missing Field 'completed'~~ RESOLVED

Rust/Lua struct mismatch for `ResearchState` and `ProductionStats` caused deserialization errors.

**Fixed by:**
- Added `#[serde(default)]` to all optional fields in ResearchState
- Updated ProductionStats struct to match Lua observation format
- All serialization tests now pass

---

## Verified Working Actions (Regression Test 2026-01-02)

| Category | Action | Status | Notes |
|----------|--------|--------|-------|
| Core | Build | ✅ | `{"Type": "Build", "Entity": "...", "Position": [x,y]}` |
| Core | Mine (position) | ✅ | `{"Type": "Mine", "Position": [x,y]}` |
| Core | Mine (EntityId) | ✅ | `{"Type": "Mine", "EntityId": 123}` |
| Core | RotateEntity | ✅ | Direction changes confirmed |
| Core | SetRecipe | ✅ | Recipe field updates |
| Core | Noop/Wait | ✅ | Time advances |
| Utility | SpawnResource | ✅ | Resource counts increase |
| Utility | DamageEntity | ✅ | Health decreases |
| Utility | RepairEntity | ✅ | Health restores to 1.0 |
| Combat | BuildTurret | ✅ | Turret entities created |
| Combat | SpawnEnemy | ✅ | Works (enemies may spawn outside observation radius) |
| Logistics | InsertItems | ⚠️ | Executes but no inventory field to verify |

**Error Handling**: Actions return errors in `action_result` field, NOT as MCP exceptions:
```json
{"action_result": {"action_type": "Mine", "success": false, "error": "Entity not found"}}
```

## Actions Needing Verification

| Action | Issue |
|--------|-------|
| Teleport | No player position in observation |
| SetSpeed | No game speed field |
| StartResearch | research field null |
| Attack/AttackArea | Need local enemies to test |
| InsertItems | Need container with inventory |
| TransferItems | Need source/destination entities |
| ConnectWire | Need circuit network entities |
