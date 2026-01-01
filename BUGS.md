# GameRL RimWorld Adapter - Bug List

## Discovered During Gameplay Session

### 1. Buildings array not populated (but buildings DO work!)
**Severity:** Medium (functional, just missing from state)
**Description:** Buildings placed via `PlaceBlueprint` are NOT showing in the `Buildings` array of the observation state, but they DO exist in-game and function correctly.

**Evidence:**
- Lottie has Comfort: 57% while sleeping at (108, 92) - the bed location
- Pheanox and Matt have Comfort: 0% - sleeping on ground
- Colonists did "FinishFrame" jobs, indicating construction occurred
- "Need colonist beds" alert still shows (need 2 more beds)

**Actual behavior:** Blueprints ARE being placed and built, they just aren't reported in the Buildings observation array.

**Reproduction:**
```json
{"Type": "PlaceBlueprint", "Building": "Bed", "X": 108, "Y": 92, "Rotation": 0, "Stuff": "WoodLog"}
```
- Returns success response
- `Buildings` array in observation remains empty
- Colonists don't get construction jobs for beds

**Expected:** Blueprint should appear in game and colonists should build it.

**Notes:** Matt's job showed `PlaceNoCostFrame` at one point, suggesting partial blueprint system engagement.

### 2. ButcherSpot blueprint also fails
**Severity:** High
**Description:** Attempting to place a ButcherSpot using PlaceBlueprint doesn't create a usable butcher spot.

**Reproduction:**
```json
{"Type": "PlaceBlueprint", "Building": "ButcherSpot", "X": 140, "Y": 120, "Rotation": 0}
```

**Additional Evidence (Session 2):**
- Placed ButcherSpot at (140, 120)
- Jane's job showed `PlaceNoCostFrame` suggesting spot was being created
- `ListWorkbenches` returns empty - spot not registered as a workbench
- 2 alpaca corpses + 1 chinchilla corpse sitting on map un-butchered
- `Buildings` array remains empty (bug #1 related)
- **User confirmed butcher spot IS placed in-game** - issue is extraction not placement

**Root Cause:** EntityExtractor.cs only extracts `IBillGiver` buildings but they're not showing. Likely the building is there but extraction fails silently.

**Impact:** Cannot add butcher bill because building ID is unknown

### 3. Caribou corpses not being processed
**Severity:** Medium
**Description:** 2 caribou corpses have been on the ground for extended time (since tick ~80000, now at 140000+). No butchering or hauling occurs despite:
- Stockpile zone existing
- Colonists with hauling enabled
- "Need meal source" alert showing

**Possible cause:** No butcher spot/table available, or butchering bill not set up.

### 4. NullReferenceException in OnGUI (CRASH) - FIXED
**Severity:** High
**Status:** FIXED (2025-12-29)
**Description:** Game throws repeated NullReferenceException errors in the Unity GUI loop after extended gameplay session.

**Error:**
```
Root level exception in OnGUI(): System.NullReferenceException: Object reference not set to an instance of an object
[Ref 58F75CB0] Duplicate stacktrace, see ref for original
UnityEngine.StackTraceUtility:ExtractStackTrace ()
Verse.Log:Error (string)
Verse.Root:OnGUI ()
```

**Root Cause:** Multiple issues identified:
1. `ExtractAlerts()` was iterating over GUI's activeAlerts list while it could be modified
2. `alert.GetLabel()` can trigger GUI code with null state
3. `VisionStreamManager.Capture()` was calling `camera.Render()` during OnGUI phase
4. All collection iterations in state extractors could be modified mid-iteration

**Fix Applied:**
- All collection iterations now use `.ToList()` snapshots before iterating
- Added try-catch blocks around individual item extractions
- Added null/destroyed/spawned checks for all game objects
- `ExtractAlerts()` now catches exceptions from `GetLabel()`
- `VisionStreamManager.Capture()` now checks `Event.current` to avoid OnGUI conflicts

### 5. Missing Unforbid action - items start forbidden - FIXED
**Severity:** High
**Status:** FIXED (2025-12-29)
**Description:** Items in RimWorld start as "Forbidden" by default. Colonists won't pick up forbidden items.

**Fix Applied:**
Added two new actions:
- `Unforbid`: Unforbid a single item by ThingId
- `UnforbidArea`: Unforbid all items in a radius

```json
{"Type": "Unforbid", "ThingId": "Gun_BoltActionRifle3232"}
{"Type": "UnforbidArea", "X": 120, "Y": 130, "Radius": 20}
```

**Additional Fix (2025-12-29):**
Added `IsForbidden` field to EntityRef for Weapons, Items (medicine), and Corpses.

### 6. Wood stockpile shows 0 after having 58
**Severity:** Low
**Description:** `Stockpiles.WoodLog` showed 58, then went back to 0. Wood items still appear in the `Weapons` list at various positions but aren't counted in stockpile.

**Possible cause:** Wood might be outside stockpile zone, or stockpile accounting issue.

### 7. Animals appearing in Buildings array - FIXED
**Severity:** Medium
**Status:** FIXED (2025-12-29)
**Description:** The `Buildings` array in observations contains animals (squirrels, muffalo, colonists, etc.) instead of actual buildings. Animals show `CanAcceptBills: true`.

**Root Cause:** Pawns (colonists, animals) implement `IBillGiver` interface for surgery operations. The EntityExtractor scans for `IBillGiver` things to find workbenches but inadvertently includes all Pawns.

**Fix Applied:**
Added filter `!(t is Pawn)` to exclude Pawns from IBillGiver scan in EntityExtractor.cs:
```csharp
allThingsForBills = map.listerThings.AllThings
    .Where(t => t is IBillGiver && !(t is Pawn) && t.Spawned && !t.Destroyed)
    .ToList();
```

**Additional Fix (2025-12-29):**
Also added `!(t is Corpse)` filter since Corpses implement IBillGiver for surgery as well.

### 8. ButcherSpot still not appearing in Buildings - FIXED
**Severity:** High
**Status:** FIXED (2025-12-29)
**Description:** After multiple fix attempts, ButcherSpot placed via `PlaceBlueprint` still does not appear in the Buildings observation array.

**Root Cause:** The `PlaceBlueprint` action was using `GenConstruct.PlaceBlueprintForBuild` for ALL buildings, including instant-build spots like ButcherSpot, Campfire, SleepingSpot, etc. These spots have no construction cost and should be placed instantly.

**Fix Applied:**
Modified `PlaceBlueprint` in MapActions.cs to detect instant-build buildings:
```csharp
// Check if this is a "spot" type building with no construction cost - place instantly
bool isInstantBuild = (buildingDef.costList == null || buildingDef.costList.Count == 0)
    && buildingDef.costStuffCount <= 0;

if (isInstantBuild)
{
    // Spawn the building directly (for ButcherSpot, Campfire, SleepingSpot, etc.)
    var building = ThingMaker.MakeThing(buildingDef, stuffDef);
    building.SetFaction(Faction.OfPlayer);
    GenSpawn.Spawn(building, pos, map, rot);
}
else
{
    // Normal construction - create blueprint
    GenConstruct.PlaceBlueprintForBuild(buildingDef, pos, map, rot, Faction.OfPlayer, stuffDef);
}
```

**Verified:** Full hunt → butcher pipeline works correctly. ButcherSpot appears in Buildings with `CanAcceptBills: true`.

---

## Working Features (Confirmed)

- `DesignateHunt` - Works correctly
- `SetWorkPriority` - Works correctly
- `CreateStockpile` - Works correctly
- `DesignateCutPlants` - Works correctly
- `Draft` / `Undraft` / `Equip` - Work correctly
- `CreateGrowingZone` - Not tested
- `AddBill` - Not tested
- `DesignateMine` - Not tested

---

## Suggested Improvements

### 1. Inconsistent casing in MCP protocol
**Type:** Consistency
**Description:** Agent registration uses snake_case (`agent_id`, `agent_type`) while the rest of the protocol uses PascalCase.

**Current:**
```json
{"agent_id": "claude-manager", "agent_type": "ColonyManager"}
```

**Expected (for consistency):**
```json
{"AgentId": "claude-manager", "AgentType": "ColonyManager"}
```

---

## Environment
- RimWorld version: (check in-game)
- GameRL adapter: latest from feature/vision branch
- Session date: 2025-12-29
