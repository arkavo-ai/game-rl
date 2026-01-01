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

### 9. Food production collapse during gameplay
**Severity:** Critical
**Status:** ONGOING (2025-12-29 - Gameplay Session 2)
**Description:** Food supply has collapsed from 5-6 days down to 1 day of meals despite active hunting and butchering setup.

**Evidence:**
- Tick 58,143: Food = 5 days, 42 meal packs
- Tick 348,719: Food = 1 day, 13 meal packs
- Colonists showing "Hungry" status with Food need ~0.25-0.45
- "Need meal source" alert active
- Paramedic actively doing "TendPatient" instead of cooking
- Butcher spot exists and was functional but no recent butchering visible

**Suspected Causes:**
1. Colonists consuming meals faster than production/hunting can supply
2. No active cooking/meals being produced despite having meal packs
3. Hunting designated but kills not being processed into meals
4. Work priorities may not be properly balanced

**Impact:** Colonists approaching starvation; moods degrading (0.4-0.48 range); immediate food crisis.

### 10. Recreation variety alert persists despite infrastructure
**Severity:** Medium
**Status:** ONGOING (2025-12-29)
**Description:** "Need recreation variety" alert remains active even after placing Table and attempting ChessTable/Horseshoes.

**Evidence:**
- Placed Table1x2c at (110, 120) - built successfully
- Placed Chair at (109, 119) - unknown result
- Attempted ChessTable at (95, 115) - failed silently
- Attempted Horseshoes at (100, 125) - failed silently
- Alert still shows after multiple recreation items placed

**Suspected Cause:**
- ChessTable and Horseshoes building defNames may be incorrect or unavailable
- Game may require specific recreation types not yet identified
- Alert may not update properly in real-time

### 11. Colonist moods degrading over time
**Severity:** Medium
**Status:** ONGOING (2025-12-29)
**Description:** Colonist mood scores declining as game progresses despite food/comfort infrastructure.

**Progression:**
- Tick 1,068: Mood 0.64-0.68 (Happy/Content)
- Tick 7,718: Mood 0.48-0.56 (Content)
- Tick 258,525: Mood 0.40-0.48 (Content, trending low)

**Current colonist status (Tick 348,719):**
- Paramedic: Mood 0.4764 (Content), Health 0.88, Comfort 0.0 (CRITICAL)
- Monroe: Mood 0.4 (Content), Comfort 0.66, Rest 0.55
- Marine: Mood 0.4076 (Content), DrugDesire 0.199 (CRITICAL), Comfort 0.75

**Issues:**
- Paramedic has 0% comfort despite infrastructure
- Marine has critical drug desire (unusual)
- All colonists showing "Miserable" joy status (0.04-0.13)
- Beauty status "Critical" on multiple colonists

**Impact:** Moods still positive but trending downward; risk of mental breaks if trend continues.

### 12. DiningChair blueprints not appearing or being built
**Severity:** Medium
**Status:** NEW (2025-12-29 - Gameplay Session 3)
**Description:** Placed 3 DiningChair blueprints via PlaceBlueprint but they never appear in Buildings array and don't get constructed.

**Reproduction:**
```json
{"Type": "PlaceBlueprint", "Building": "DiningChair", "X": 96, "Y": 119, "Rotation": 0, "Stuff": "WoodLog"}
{"Type": "PlaceBlueprint", "Building": "DiningChair", "X": 97, "Y": 119, "Rotation": 0, "Stuff": "WoodLog"}
{"Type": "PlaceBlueprint", "Building": "DiningChair", "X": 99, "Y": 119, "Rotation": 0, "Stuff": "WoodLog"}
```

**Evidence:**
- All three PlaceBlueprint calls returned success
- Buildings array never shows the chairs
- Stockpile shows WoodLog: 0, so materials unavailable
- Colonists never get construction jobs for chairs
- Comfort remains critical (0-20%) suggesting chairs weren't built

**Suspected Cause:** DiningChair requires wood and construction. Unlike instant-build spots (ButcherSpot, Campfire), these need materials. Blueprints may have been placed but can't be built due to lack of wood, and blueprint entities aren't exposed in observations.

**Impact:** Can't track blueprint status; can't tell if placement failed or if awaiting materials.

### 13. Distant corpses not being hauled or butchered
**Severity:** Medium
**Status:** NEW (2025-12-29 - Gameplay Session 3)
**Description:** 3 animal corpses remain at distant locations for extended time despite butcher bill being active and colonists being idle.

**Evidence:**
- Corpse_Hare43262 at (98, 141) - distance ~24 cells from base
- Corpse_Hare43305 at (38, 80) - distance ~70 cells from base
- Corpse_Emu43351 at (16, 176) - distance ~100 cells from base
- Butcher bill successfully added to ButcherSpot42595
- Colonists showing "3 colonists idle" alert
- IsForbidden: false on all corpses
- Low food situation (1 day remaining)

**Suspected Causes:**
1. Corpses too far away for colonists to pathfind/prioritize
2. Hauling work priority too low
3. Corpses may have rotted/become invalid
4. Game may require explicit haul jobs for distant items

**Impact:** Potential food going to waste; colonists idle while food critical.

### 14. Wall and room data not exposed in observations
**Severity:** Medium
**Status:** NEW (2025-12-29 - Gameplay Session 3)
**Description:** Game state provides Buildings array but no information about walls, doors, or room boundaries.

**Missing Data:**
- Walls (no wall entities in any observation category)
- Doors
- Room definitions or boundaries
- Room quality/impressiveness scores
- Whether buildings are indoors or outdoors

**Evidence:**
- Buildings array shows sleeping spots, campfires, sandbags, traps
- No wall structures appear in any entity list
- Can't determine if colonists are in enclosed rooms
- User confirmed walls may exist in-game but aren't in observation data

**Impact:**
- Can't build or manage rooms strategically
- Can't optimize room quality for mood
- Can't tell if temperature control (from rooms) is working
- Limited ability to plan base layout

### 15. Blueprints and construction status not visible
**Severity:** Medium
**Status:** NEW (2025-12-29 - Gameplay Session 3)
**Description:** When PlaceBlueprint is called for buildings requiring construction (non-instant), the blueprint itself doesn't appear in observations.

**Expected:** Blueprints should appear as entities with construction progress (0-100%)

**Actual:**
- Instant-build items (ButcherSpot, Campfire, SleepingSpot) spawn immediately and appear
- Construction-required items (DiningChair, Bed, Table) don't appear until fully built
- No way to track construction progress or verify blueprint placement

**Impact:** Can't tell if blueprint placement succeeded or if construction is in progress.

### 16. Colonists idle with work available
**Severity:** Low
**Status:** NEW (2025-12-29 - Gameplay Session 3)
**Description:** Colonists reported as idle despite available work and low food situation.

**Evidence (Tick 443,864, Hour 15):**
- Alert: "3 colonists idle"
- 3 corpses available to butcher
- Low food alert active (1 day remaining)
- All colonists have Hauling: priority 3
- All colonists have Hunting: priority 3 (Patrick only)
- Patrick has Cooking: priority 3

**Suspected Causes:**
1. Work items (corpses) too far away to be considered
2. No jobs available that match colonist work settings
3. Game considers work but colonists deprioritize due to distance/danger
4. Temperature too high (37°C) affecting work behavior

**Impact:** Lost productivity during critical food shortage.

## Environment
- RimWorld version: (check in-game)
- GameRL adapter: latest from feature/vision branch
- Session dates: 2025-12-29 (Multiple gameplay sessions)
