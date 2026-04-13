# Intent-Based Spatial Actions

## Problem

The current action space exposes coordinate-bearing primitives (`PlaceBlueprint(Building, X, Y)`, `CreateGrowingZone(X, Y, W, H)`) that demand spatial reasoning from the LLM agent. Spatial reasoning — picking valid coordinates on a game map — is not in the LLM's capability class. The result: agents collapse to a 2-3 action repertoire (SetSpeed, SelectResearch) and never attempt placement actions despite seeing them in ValidActions.

The fix is architectural: make spatial placement a tool, not an LLM output. The model decides **what** and **near what**. A game-side spatial planner resolves the intent to concrete coordinates.

## Design Principles

- **Thin intent layer** — the planner is a simple resolver, not a strategic AI. It finds valid positions near an anchor. The model retains all strategic decisions.
- **Game-agnostic** — the pattern lives in the `GameEnvironment` trait. Every game implements resolution using its own spatial APIs.
- **Game resolves intent** — each game knows its own spatial semantics. RimWorld uses `GenRadial` + reachability. A gridworld uses BFS. The MCP server doesn't need map data.
- **Anchor flexibility** — the model references anchors by entity ID (`"Stockpile_12345"`) or type name (`"Stockpile"`). ID resolution is tried first, type lookup is the fallback. `"MapCenter"` is a universal anchor.

## Core Types (game-rl-core)

### SpatialAnchor

How the model references "near what":

- Parsed from a single `Near` string parameter
- Resolution order: exact entity ID → zone label → type name (nearest instance) → `"MapCenter"`
- Descriptive error if nothing matches

### SpatialIntent

Intent-based actions replacing coordinate primitives:

| Intent Action | Parameters | Replaces |
|---|---|---|
| `PlaceBuildingNear` | Building (string), Near (string), Count (int, default 1), Stuff (string, optional) | `PlaceBlueprint(Building, X, Y, Rotation, Stuff)` |
| `EstablishFarm` | Near (string), Crop (string, optional), Size (int, optional) | `CreateGrowingZone(X, Y, Width, Height, Plant)` |
| `EstablishStorage` | Near (string), Size (int, optional) | `CreateStockpile(X, Y, Width, Height)` |
| `DesignateMiningNear` | Near (string), Count (int, optional) | `DesignateMine(X, Y, Radius)` |
| `DesignateClearNear` | Near (string), Radius (int, optional) | `DesignateCutPlants(X, Y, Radius)` |

### ResolvedPlacement

What the game returns after resolution:

- `actions: Vec<Action>` — the concrete coordinate-bearing actions that were executed
- `description: String` — human-readable summary (e.g., "Placed 3 Beds near Stockpile at (42,15), (44,15), (46,15)")
- `count: u32` — how many were successfully placed (may be less than requested)

Partial success is a valid result, not an error.

## Trait Extension (game-rl-core)

New method on `GameEnvironment`:

```rust
async fn resolve_spatial(
    &self,
    intent: SpatialIntent,
) -> Result<ResolvedPlacement>;
```

- No `agent_id` parameter — the game tracks active agent context (single-agent is the common case)
- Executes the actions internally — resolve and execute in one call, return what was done (same pattern as `MoveToEntity`)
- Default implementation returns `Err` — games that don't need spatial intent (e.g., trivial gridworlds) skip it
- `GameManifest` declares support so the MCP server knows whether to advertise intent tools

## MCP Server (game-rl-server)

- `list_tools` advertises intent-based tools when the manifest declares spatial intent support
- The `step` tool handler recognizes intent action types, parses them into `SpatialIntent`, and calls `resolve_spatial` instead of the normal `step` path
- The response includes `ResolvedPlacement.description` in the tool result content so the agent gets feedback
- Same `step` tool, same action format — just new action types that route through spatial resolution

Tool schema example (what the agent sees):

```json
{
  "Action": {
    "Type": "PlaceBuildingNear",
    "Building": "Bed",
    "Near": "Stockpile",
    "Count": 3,
    "Stuff": "WoodLog"
  }
}
```

## IPC Bridge (harmony-bridge)

- New IPC message type: `ResolveSpatial { intent }` sent over the Unix socket to C#
- C# side dispatches to resolution methods via the same `[GameRLAction]` reflection pattern
- Response carries back `ResolvedPlacement` as JSON
- No new transport mechanism — flows through existing JSON-over-Unix-socket IPC

## C# Implementation (RimWorld)

New file: `Actions/SpatialActions.cs`

### Anchor Resolution

Shared helper used by all intent actions:

```
ResolveAnchor(string near, Map map) -> IntVec3
```

Resolution order:
1. `ThingResolver` — exact entity ID match → return `thing.Position`
2. `ZoneManager` — zone label match → return zone centroid
3. `DefDatabase` type match → find nearest instance on map → return `position`
4. `"MapCenter"` → return `map.Center`
5. Fail with descriptive error

### Per-Intent Resolution

| Method | Resolution Logic |
|---|---|
| `PlaceBuildingNear` | Resolve anchor → `GenRadial.RadialCellsAround` expanding outward → filter by `CanPlaceBlueprintAt` → place up to Count, spacing for building footprint + rotation |
| `EstablishFarm` | Resolve anchor → search outward for fertile cells (`GetFertility > 0`) → prefer contiguous clusters → prefer higher fertility (SoilRich 1.4 over regular) → create `Zone_Growing`, set plant |
| `EstablishStorage` | Resolve anchor → search outward for `Standable` cells with no existing zone → prefer contiguous clusters → create `Zone_Stockpile` |
| `DesignateMiningNear` | Resolve anchor → `RadialCellsAround` → filter for `Mineable` things → designate up to Count |
| `DesignateClearNear` | Resolve anchor → `RadialCellsAround` → filter for trees/plants → designate within Radius |

Key behaviors:
- All methods expand outward from anchor (closest first) using `GenRadial`
- `PlaceBuildingNear` checks full building footprint, not just one cell
- `EstablishFarm` prefers `SoilRich` when nearby — fertile-region strategy baked into resolver
- Partial success is valid — "requested 5 beds, placed 3 (insufficient space)" returns count=3
- Dispatch via `[GameRLAction]` attributes, same reflection-based parameter binding as existing actions

## ValidActions Changes

In `RimWorldStateExtractor.ComputeValidActions`:

- **Replace** coordinate-bearing actions with intent equivalents in the advertised set:
  - `PlaceBlueprint` → `PlaceBuildingNear`
  - `CreateGrowingZone` → `EstablishFarm`
  - `CreateStockpile` → `EstablishStorage`
  - `DesignateMine` → `DesignateMiningNear`
  - `DesignateCutPlants` → `DesignateClearNear`
- **Keep** coordinate primitives callable but not advertised
- **Smarter preconditions** — `EstablishFarm` only if fertile soil exists, `DesignateMiningNear` only if mineable rocks exist

## Observation Changes

No structural change to observations. The model already sees entities, zones, and buildings with IDs and types.

One addition: a **Landmarks** section in `observe` responses — a curated short list of spatial anchors the model can reference:
- Named zones ("Main Stockpile", "Farm")
- Key buildings by type ("CookStove", "ResearchBench")
- "MapCenter"

This is a convenience subset of existing observation data, optimized for copy-paste into `Near` fields.

## Implications for Learning

1. **Thompson Sampling works** — can reinforce "when Starvation alert fires, EstablishFarm succeeds" because the action is language-native (same complexity as SetWorkPriority)
2. **Curiosity-driven exploration becomes tractable** — enumerate untried action *types* (small set) instead of untried coordinate combinations (infinite). Synaptic Consolidation can generate "you've never tried EstablishFarm" as a curiosity signal.
3. **Model selection shifts** — better tool-calling models win (explains Ministral-8B result), because spatial reasoning expressed as tool invocation is a language task again
