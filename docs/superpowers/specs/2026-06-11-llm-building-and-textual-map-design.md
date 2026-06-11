# LLM Building Primitives + Textual Map — Design

**Date:** 2026-06-11 · **Status:** Approved (user) · **Scope:** RimWorld adapter now; GridColony + spec draft-03 convention later

## Problem

Live playtesting (RimWorld 1.6, Edge commander) shows bases "circling out of the
center": `PlaceBuildingNear` radial-fills cells with no structure, so wall spam
accretes into a blob around the colony centroid, with beds scattered outside.
The agent has no structural verb (room, door, interior) and no way to *see*
what it has built.

## Decision (approved)

1. **`BuildRoom` + composition** — one strong structural primitive, composed by
   the LLM, rather than canned macros or a blueprint DSL.
2. **`RenderMap` in RimWorld now** — prove the textual viewport live before
   porting to GridColony and writing it into the spec as a convention.

## Components

### 1. `BuildRoom` (new `Actions/ConstructionActions.cs`)

`{"Type":"BuildRoom","Width":7,"Height":5,"Door":"S","Near":"ColonyCenter","Stuff":"WoodLog","Label":"barracks"}`

- Width/Height are exterior dimensions, clamped 4–15. `Door` ∈ N/S/E/W
  (default S), centered on that side. `Stuff` defaults WoodLog. `Near` defaults
  ColonyCenter.
- Footprint search: `GenRadial.RadialCellsAround(anchor, 30)` as candidate rect
  centers, nearest-first (deterministic). A footprint is valid when every
  perimeter cell passes `GenConstruct.CanPlaceBlueprintAt(Wall)` and every
  interior cell is standable with no edifice. No valid footprint → loud error
  suggesting a smaller size or different anchor (REQ-ERR-02).
- Spawns walls instantly (same pattern as `PlaceBuildingNear`), door on the
  chosen side. Interior untouched.
- Registers `Room_<n>` in an in-memory `RoomRegistry` (id, rect, door pos,
  optional label). Registry is per-process; stale after save reload — entries
  are validated against map bounds on resolve, and rooms are rediscoverable
  via `RenderMap`. Accepted v1 limitation.
- Result: audited spatial JSON (Description with bounds + door position,
  AnchorRequested/Resolved, Count = walls+door).

### 2. Room ids become anchors

- `ResolveAnchor` resolves `Room_<n>` (and labels) to the room's rect center.
- `Landmarks` lists registered rooms (Id, Kind=Room, position, size).
- `PlaceBuildingNear` gains optional `Inside:"Room_1"`: candidate cells are the
  room's interior only (rect contracted by 1), nearest-to-center first.
  Unknown room id → loud error listing registered rooms.

### 3. `RenderMap` (new `Actions/MapRender.cs`)

`{"Type":"RenderMap","Near":"ColonyCenter","Radius":16}` — read-only (like
`ListBuildables`), returns plain text:

- ASCII viewport, north at top, one glyph per cell, absolute coordinate rulers
  (x ticks every 10 columns, z label per row) so the model can act on exact
  `(x,z)` coordinates it reads off the map.
- Glyph priority: `P` colonist > `H` hostile > `A` animal > `D` door > `#` wall
  > `M` mountain/rock wall > `B` bed > `=` other building > `i` item > `S`
  stockpile cell > `F` growing cell > `T` tree > `~` water > `,` plant cover >
  `.` ground.
- Footer: legend + registered rooms + colony center coords.
- Radius default 16, clamped 4–30 (worst case ~3.7 KB).

### 4. Commander prompt (arkavo-edge)

Building rule replaces the wall-spam reflex: build a room first, furnish with
`Inside`, then `RenderMap` to verify before building more. Alert table:
"Need colonist beds" → BuildRoom if no room yet, else beds `Inside` the newest
room; "Pen needed" → BuildRoom 8×8 Door S.

## Error handling

Every failure path throws with an actionable message (REQ-ERR-01/02): no
footprint found, unknown room id (lists rooms), bad door side (lists N/S/E/W),
unknown stuff.

## Testing

No C# unit harness in this repo; verification is live (repo practice):
1. Scripted MCP session against the running game (deterministic): BuildRoom at
   a known anchor → RenderMap shows `#` perimeter with `D`; beds `Inside` land
   within the rect; bad inputs error loudly.
2. Edge swarm session: commander uses the new verbs via the updated table.

## Out of scope (follow-ups)

GridColony `RenderMap`/`BuildRoom` parity; spec draft-03 viewport convention;
object-form anchors in C#; room persistence across save reload.
