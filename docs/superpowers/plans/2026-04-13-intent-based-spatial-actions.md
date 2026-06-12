# Intent-Based Spatial Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace coordinate-bearing spatial actions (PlaceBlueprint X,Y) with intent-based alternatives (PlaceBuildingNear, EstablishFarm) where the game resolves spatial placement, making the action space language-native for LLM agents.

**Architecture:** New `SpatialIntent` types in game-rl-core define intent actions. A new `resolve_spatial` method on the `GameEnvironment` trait lets each game implement its own spatial resolution. The MCP server detects intent action types in the step handler and routes them to `resolve_spatial`. The harmony-bridge forwards these as a new IPC message type. RimWorld implements resolution in a new `SpatialActions.cs` file using `GenRadial` for proximity search.

**Tech Stack:** Rust (game-rl-core, game-rl-server, harmony-bridge, game-bridge), C# (.NET Framework 4.7.2, RimWorld mod)

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `crates/game-rl-core/src/spatial.rs` | Create | SpatialIntent, SpatialAnchor, ResolvedPlacement types |
| `crates/game-rl-core/src/lib.rs` | Modify | Add `pub mod spatial` and re-exports |
| `crates/game-rl-core/src/manifest.rs` | Modify | Add `spatial_intent: bool` to Capabilities |
| `crates/game-rl-server/src/environment.rs` | Modify | Add `resolve_spatial` to GameEnvironment trait |
| `crates/game-rl-server/src/tools.rs` | Modify | Route intent actions to resolve_spatial in handle_step |
| `crates/game-bridge/src/protocol.rs` | Modify | Add ResolveSpatial / SpatialResult message variants |
| `crates/harmony-bridge/src/ipc.rs` | Modify | Implement resolve_spatial for HarmonyBridge |
| `adapters/rimworld/RimWorld.GameRL/Actions/SpatialActions.cs` | Create | AnchorResolver + 5 intent action methods |
| `adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs` | Modify | Swap coordinate actions for intent actions in ValidActions |

---

### Task 1: Core Spatial Types

**Files:**
- Create: `crates/game-rl-core/src/spatial.rs`
- Modify: `crates/game-rl-core/src/lib.rs`

- [ ] **Step 1: Write tests for SpatialIntent serialization**

Add to the bottom of the new file:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_place_building_near_from_json() {
        let json = r#"{"Type":"PlaceBuildingNear","Building":"Bed","Near":"Stockpile","Count":3,"Stuff":"WoodLog"}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        match intent {
            SpatialIntent::PlaceBuildingNear { building, near, count, stuff } => {
                assert_eq!(building, "Bed");
                assert_eq!(near, "Stockpile");
                assert_eq!(count, 3);
                assert_eq!(stuff.unwrap(), "WoodLog");
            }
            _ => panic!("Wrong variant"),
        }
    }

    #[test]
    fn test_establish_farm_defaults() {
        let json = r#"{"Type":"EstablishFarm","Near":"MapCenter"}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        match intent {
            SpatialIntent::EstablishFarm { near, crop, size } => {
                assert_eq!(near, "MapCenter");
                assert!(crop.is_none());
                assert!(size.is_none());
            }
            _ => panic!("Wrong variant"),
        }
    }

    #[test]
    fn test_resolved_placement_serialization() {
        let rp = ResolvedPlacement {
            description: "Placed 2 Beds near Stockpile_4821 at (42,13), (44,13)".into(),
            count: 2,
            anchor_resolved: "Stockpile_4821".into(),
            anchor_position: (42, 15),
        };
        let json = serde_json::to_value(&rp).unwrap();
        assert_eq!(json["Count"], 2);
        assert_eq!(json["AnchorResolved"], "Stockpile_4821");
    }

    #[test]
    fn test_spatial_intent_names() {
        assert_eq!(
            SpatialIntent::VARIANTS,
            &["PlaceBuildingNear", "EstablishFarm", "EstablishStorage", "DesignateMiningNear", "DesignateClearNear"]
        );
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cargo test -p game-rl-core`
Expected: compilation error — `spatial.rs` doesn't exist yet

- [ ] **Step 3: Create spatial.rs with types**

Create `crates/game-rl-core/src/spatial.rs`:

```rust
//! Intent-based spatial action types
//!
//! These types allow agents to express spatial intent (e.g., "place a bed near the stockpile")
//! without specifying exact coordinates. The game environment resolves intent to concrete
//! placements using its own spatial APIs.

use serde::{Deserialize, Serialize};

/// Intent-based spatial action
///
/// Each variant corresponds to a coordinate-bearing primitive that is being replaced.
/// The game's `resolve_spatial` implementation converts these into concrete placements.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
pub enum SpatialIntent {
    /// Place building(s) near an anchor point
    /// Replaces: PlaceBlueprint(Building, X, Y, Rotation, Stuff)
    PlaceBuildingNear {
        #[serde(rename = "Building")]
        building: String,
        #[serde(rename = "Near")]
        near: String,
        #[serde(rename = "Count", default = "default_count")]
        count: u32,
        #[serde(rename = "Stuff", skip_serializing_if = "Option::is_none")]
        stuff: Option<String>,
    },

    /// Establish a growing zone on fertile soil near an anchor
    /// Replaces: CreateGrowingZone(X, Y, Width, Height, Plant)
    EstablishFarm {
        #[serde(rename = "Near")]
        near: String,
        #[serde(rename = "Crop", skip_serializing_if = "Option::is_none")]
        crop: Option<String>,
        #[serde(rename = "Size", skip_serializing_if = "Option::is_none")]
        size: Option<u32>,
    },

    /// Establish a stockpile zone near an anchor
    /// Replaces: CreateStockpile(X, Y, Width, Height)
    EstablishStorage {
        #[serde(rename = "Near")]
        near: String,
        #[serde(rename = "Size", skip_serializing_if = "Option::is_none")]
        size: Option<u32>,
    },

    /// Designate mining near an anchor (finds mineable rocks)
    /// Replaces: DesignateMine(X, Y, Radius)
    DesignateMiningNear {
        #[serde(rename = "Near")]
        near: String,
        #[serde(rename = "Count", skip_serializing_if = "Option::is_none")]
        count: Option<u32>,
    },

    /// Designate trees/plants for cutting near an anchor
    /// Replaces: DesignateCutPlants(X, Y, Radius)
    DesignateClearNear {
        #[serde(rename = "Near")]
        near: String,
        #[serde(rename = "Radius", skip_serializing_if = "Option::is_none")]
        radius: Option<u32>,
    },
}

fn default_count() -> u32 {
    1
}

impl SpatialIntent {
    /// All variant names (for ValidActions and tool schema generation)
    pub const VARIANTS: &'static [&'static str] = &[
        "PlaceBuildingNear",
        "EstablishFarm",
        "EstablishStorage",
        "DesignateMiningNear",
        "DesignateClearNear",
    ];

    /// Check if an action type name is a spatial intent
    pub fn is_spatial_action(action_type: &str) -> bool {
        Self::VARIANTS.iter().any(|v| v.eq_ignore_ascii_case(action_type))
    }
}

/// Result of spatial resolution — what the game actually placed
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ResolvedPlacement {
    /// Human-readable description with full audit trail
    pub description: String,
    /// How many items were successfully placed (may be less than requested)
    pub count: u32,
    /// The entity/zone that was resolved as the anchor
    pub anchor_resolved: String,
    /// The resolved anchor's coordinates (x, y)
    pub anchor_position: (i32, i32),
}
```

- [ ] **Step 4: Add module to lib.rs**

In `crates/game-rl-core/src/lib.rs`, add after `pub mod stream;`:

```rust
pub mod spatial;
```

And add re-exports after the existing `pub use stream::...` line:

```rust
pub use spatial::{ResolvedPlacement, SpatialIntent};
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cargo test -p game-rl-core`
Expected: all tests pass, including the 4 new spatial tests

- [ ] **Step 6: Commit**

```bash
git add crates/game-rl-core/src/spatial.rs crates/game-rl-core/src/lib.rs
git commit -m "feat(core): add SpatialIntent and ResolvedPlacement types"
```

---

### Task 2: Manifest Capability Flag

**Files:**
- Modify: `crates/game-rl-core/src/manifest.rs:85-112` (Capabilities struct)

- [ ] **Step 1: Write test for spatial_intent capability**

Add to `crates/game-rl-core/src/manifest.rs` at the bottom (create `#[cfg(test)] mod tests` block):

```rust
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_capabilities_default_spatial_intent_false() {
        let caps = Capabilities::default();
        assert!(!caps.spatial_intent);
    }

    #[test]
    fn test_capabilities_spatial_intent_from_json() {
        let json = r#"{"SpatialIntent": true}"#;
        let caps: Capabilities = serde_json::from_str(json).unwrap();
        assert!(caps.spatial_intent);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cargo test -p game-rl-core`
Expected: compilation error — `spatial_intent` field doesn't exist

- [ ] **Step 3: Add spatial_intent field to Capabilities**

In `crates/game-rl-core/src/manifest.rs`, add to the `Capabilities` struct after the `variable_timestep` field (line 111):

```rust
    /// Supports intent-based spatial actions (resolve_spatial)
    #[serde(default)]
    pub spatial_intent: bool,
```

And in the `Default` impl for `Capabilities` (line 63), add before the closing brace:

```rust
            spatial_intent: false,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cargo test -p game-rl-core`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add crates/game-rl-core/src/manifest.rs
git commit -m "feat(core): add spatial_intent capability flag to manifest"
```

---

### Task 3: GameEnvironment Trait Extension

**Files:**
- Modify: `crates/game-rl-server/src/environment.rs:24-85` (GameEnvironment trait)

- [ ] **Step 1: Add resolve_spatial to the trait**

In `crates/game-rl-server/src/environment.rs`, add this import at the top alongside existing imports:

```rust
use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, EpisodeSummary, GameEvent,
    GameManifest, Observation, Result, StepResult, StreamDescriptor, SpatialIntent, ResolvedPlacement,
};
```

Then add this method inside the `GameEnvironment` trait, after the `episode_summary` method (after line 72):

```rust
    /// Resolve a spatial intent into concrete placements
    ///
    /// Intent-based actions (PlaceBuildingNear, EstablishFarm, etc.) express what to place
    /// and where relative to an anchor, without specifying coordinates. The game resolves
    /// the intent using its own spatial APIs and executes the placements.
    ///
    /// Default: returns error (game does not support spatial intent).
    /// Games that set `capabilities.spatial_intent = true` must implement this.
    async fn resolve_spatial(&mut self, intent: SpatialIntent) -> Result<ResolvedPlacement> {
        let _ = intent;
        Err(game_rl_core::GameRLError::GameError(
            "Spatial intent not supported by this environment".into(),
        ))
    }
```

- [ ] **Step 2: Verify compilation**

Run: `cargo check -p game-rl-server`
Expected: compiles cleanly (default impl means no downstream breakage)

- [ ] **Step 3: Commit**

```bash
git add crates/game-rl-server/src/environment.rs
git commit -m "feat(server): add resolve_spatial to GameEnvironment trait"
```

---

### Task 4: MCP Server — Route Intent Actions to resolve_spatial

**Files:**
- Modify: `crates/game-rl-server/src/tools.rs:606-704` (handle_step function)

- [ ] **Step 1: Add SpatialIntent import**

In `crates/game-rl-server/src/tools.rs`, add `SpatialIntent` and `ResolvedPlacement` to the existing `game_rl_core` import at the top of the file. Find the import block and add them. The exact import lines vary — find the `use game_rl_core::` statement and add `SpatialIntent, ResolvedPlacement` to it.

- [ ] **Step 2: Add spatial intent detection and routing in handle_step**

In `crates/game-rl-server/src/tools.rs`, replace the block at lines 686-690 (the `// Execute step` block inside `handle_step`):

```rust
        // Execute step
        let mut env = environment.write().await;
        env.step(&p.agent_id, p.action, p.ticks).await?
```

With:

```rust
        // Check if this is a spatial intent action
        let is_spatial = matches!(&p.action, Action::Parameterized { action_type, .. }
            if SpatialIntent::is_spatial_action(action_type));

        if is_spatial {
            // Parse the action as a SpatialIntent and route to resolve_spatial
            let intent_json = match &p.action {
                Action::Parameterized { action_type, params } => {
                    let mut map = params.clone();
                    map.insert("Type".to_string(), serde_json::Value::String(action_type.clone()));
                    serde_json::Value::Object(map.into_iter().collect())
                }
                _ => unreachable!(),
            };
            let intent: SpatialIntent = serde_json::from_value(intent_json).map_err(|e| {
                GameRLError::InvalidAction(format!("Invalid spatial intent: {}", e))
            })?;

            let mut env = environment.write().await;
            let placement = env.resolve_spatial(intent).await?;

            // Return placement result as a StepResult-shaped response
            // so the agent gets the same response format as a normal step
            let mut obs = std::collections::HashMap::new();
            obs.insert("SpatialResult".to_string(), serde_json::to_value(&placement)?);
            // Fetch current state to merge with spatial result
            let step = env.observe().await?;
            let mut result = step;
            // Inject spatial result into observation
            if let Observation::Structured(ref mut map) = result.observation {
                map.insert("SpatialResult".to_string(), serde_json::to_value(&placement)?);
            }
            result
        } else {
            // Normal step execution
            let mut env = environment.write().await;
            env.step(&p.agent_id, p.action, p.ticks).await?
        }
```

- [ ] **Step 3: Add Observation import**

Ensure `Observation` is imported. Check the existing imports at the top of `tools.rs` — if `Observation` is not imported from `game_rl_core`, add it.

- [ ] **Step 4: Verify compilation**

Run: `cargo check -p game-rl-server`
Expected: compiles cleanly

- [ ] **Step 5: Commit**

```bash
git add crates/game-rl-server/src/tools.rs
git commit -m "feat(server): route spatial intent actions to resolve_spatial"
```

---

### Task 5: IPC Protocol — Add ResolveSpatial Message

**Files:**
- Modify: `crates/game-bridge/src/protocol.rs:33-177` (GameMessage enum)

- [ ] **Step 1: Write test for ResolveSpatial roundtrip**

Add to the existing `#[cfg(test)] mod tests` block in `crates/game-bridge/src/protocol.rs`:

```rust
    #[test]
    fn test_resolve_spatial_roundtrip() {
        let msg = GameMessage::ResolveSpatial {
            intent: serde_json::json!({
                "Type": "PlaceBuildingNear",
                "Building": "Bed",
                "Near": "Stockpile",
                "Count": 3
            }),
        };
        let bytes = serialize(&msg).unwrap();
        let json = String::from_utf8_lossy(&bytes);
        assert!(json.contains("\"Type\":\"ResolveSpatial\""));

        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::ResolveSpatial { intent } => {
                assert_eq!(intent["Building"], "Bed");
            }
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_spatial_result_roundtrip() {
        let msg = GameMessage::SpatialResult {
            description: "Placed 3 Beds near Stockpile_4821 at (42,13), (44,13), (46,13)".into(),
            count: 3,
            anchor_resolved: "Stockpile_4821".into(),
            anchor_position: (42, 15),
        };
        let bytes = serialize(&msg).unwrap();
        let decoded: GameMessage = deserialize(&bytes).unwrap();
        match decoded {
            GameMessage::SpatialResult { count, anchor_resolved, .. } => {
                assert_eq!(count, 3);
                assert_eq!(anchor_resolved, "Stockpile_4821");
            }
            _ => panic!("Wrong message type"),
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cargo test -p game-bridge`
Expected: compilation error — `ResolveSpatial` variant doesn't exist

- [ ] **Step 3: Add message variants**

In `crates/game-bridge/src/protocol.rs`, add these two variants to the `GameMessage` enum. Add after the `Shutdown` variant (line 176), before the closing brace:

```rust
    /// Resolve a spatial intent (Rust -> Game)
    ResolveSpatial {
        #[serde(rename = "Intent")]
        intent: serde_json::Value,
    },

    /// Spatial resolution result (Game -> Rust)
    SpatialResult {
        #[serde(rename = "Description")]
        description: String,
        #[serde(rename = "Count")]
        count: u32,
        #[serde(rename = "AnchorResolved")]
        anchor_resolved: String,
        #[serde(rename = "AnchorPosition")]
        anchor_position: (i32, i32),
    },
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cargo test -p game-bridge`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add crates/game-bridge/src/protocol.rs
git commit -m "feat(bridge): add ResolveSpatial/SpatialResult IPC message types"
```

---

### Task 6: Harmony Bridge — Implement resolve_spatial

**Files:**
- Modify: `crates/harmony-bridge/src/ipc.rs` (HarmonyBridge GameEnvironment impl)

- [ ] **Step 1: Add imports**

Add `SpatialIntent` and `ResolvedPlacement` to the `game_rl_core` imports at the top of `crates/harmony-bridge/src/ipc.rs`. Find the existing `use game_rl_core::` statement and add them.

- [ ] **Step 2: Implement resolve_spatial**

In `crates/harmony-bridge/src/ipc.rs`, find the `impl GameEnvironment for HarmonyBridge` block. Add this method after the `observe` method (after line 394):

```rust
    async fn resolve_spatial(&mut self, intent: SpatialIntent) -> Result<ResolvedPlacement> {
        self.ensure_connected().await?;

        let intent_json = serde_json::to_value(&intent)?;
        let response = self
            .request(GameMessage::ResolveSpatial {
                intent: intent_json,
            })
            .await?;

        match response {
            GameMessage::SpatialResult {
                description,
                count,
                anchor_resolved,
                anchor_position,
            } => Ok(ResolvedPlacement {
                description,
                count,
                anchor_resolved,
                anchor_position,
            }),
            GameMessage::Error { code, message } => Err(GameRLError::GameError(format!(
                "Spatial resolution error {}: {}",
                code, message
            ))),
            _ => Err(GameRLError::ProtocolError(
                "Unexpected response to ResolveSpatial".into(),
            )),
        }
    }
```

- [ ] **Step 3: Add GameMessage::ResolveSpatial import**

Ensure the `GameMessage` import includes the new variants. The `GameMessage` type is imported from `game_bridge::protocol` — check that the existing import covers it (it should, since it's the same enum).

- [ ] **Step 4: Verify compilation**

Run: `cargo check -p harmony-bridge`
Expected: compiles cleanly

- [ ] **Step 5: Commit**

```bash
git add crates/harmony-bridge/src/ipc.rs
git commit -m "feat(harmony-bridge): implement resolve_spatial over IPC"
```

---

### Task 7: C# Spatial Actions — Anchor Resolution & Intent Methods

**Files:**
- Create: `adapters/rimworld/RimWorld.GameRL/Actions/SpatialActions.cs`

- [ ] **Step 1: Create SpatialActions.cs with AnchorResolver**

Create `adapters/rimworld/RimWorld.GameRL/Actions/SpatialActions.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using GameRL.Harmony.RPC;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Intent-based spatial actions. The agent specifies what to place and near what anchor;
    /// these methods resolve to concrete coordinates using RimWorld's spatial APIs.
    /// </summary>
    public static class SpatialActions
    {
        /// <summary>
        /// Resolve an anchor string to a map position.
        /// Resolution order: entity ID → zone label → type name (nearest) → MapCenter.
        /// </summary>
        private static IntVec3 ResolveAnchor(string near, Map map)
        {
            if (string.IsNullOrEmpty(near))
                throw new InvalidOperationException("Near parameter is required");

            // Special: MapCenter
            if (near.Equals("MapCenter", StringComparison.OrdinalIgnoreCase))
                return map.Center;

            // 1. Try exact entity ID match (ThingID like "Stockpile_4821" or "Building_123")
            var thing = map.listerThings.AllThings
                .FirstOrDefault(t => t.ThingID == near);
            if (thing != null)
                return thing.Position;

            // 2. Try zone label match
            var zone = map.zoneManager.AllZones
                .FirstOrDefault(z => z.label != null &&
                    z.label.Equals(near, StringComparison.OrdinalIgnoreCase));
            if (zone != null)
            {
                // Zone centroid
                var cells = zone.Cells.ToList();
                if (cells.Count > 0)
                {
                    int cx = (int)cells.Average(c => c.x);
                    int cz = (int)cells.Average(c => c.z);
                    return new IntVec3(cx, 0, cz);
                }
            }

            // 3. Try type name match — find nearest colonist building of that type
            var byType = map.listerBuildings.allBuildingsColonist
                .Where(b => b.def.defName.Equals(near, StringComparison.OrdinalIgnoreCase)
                    || b.def.label != null && b.def.label.Equals(near, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Position.DistanceTo(map.Center))
                .FirstOrDefault();
            if (byType != null)
                return byType.Position;

            // 3b. Try zone type match (e.g., "Stockpile" matches Zone_Stockpile, "Growing" or "Farm" matches Zone_Growing)
            Zone zoneByType = null;
            if (near.Equals("Stockpile", StringComparison.OrdinalIgnoreCase)
                || near.Equals("Storage", StringComparison.OrdinalIgnoreCase))
            {
                zoneByType = map.zoneManager.AllZones
                    .OfType<Zone_Stockpile>()
                    .OrderByDescending(z => z.Cells.Count())
                    .FirstOrDefault();
            }
            else if (near.Equals("Farm", StringComparison.OrdinalIgnoreCase)
                || near.Equals("Growing", StringComparison.OrdinalIgnoreCase)
                || near.Equals("GrowingZone", StringComparison.OrdinalIgnoreCase))
            {
                zoneByType = map.zoneManager.AllZones
                    .OfType<Zone_Growing>()
                    .OrderByDescending(z => z.Cells.Count())
                    .FirstOrDefault();
            }
            if (zoneByType != null)
            {
                var cells = zoneByType.Cells.ToList();
                int cx = (int)cells.Average(c => c.x);
                int cz = (int)cells.Average(c => c.z);
                return new IntVec3(cx, 0, cz);
            }

            // 4. Try as a ThingDef type match — find any thing of that def on map
            var anyThing = map.listerThings.AllThings
                .Where(t => t.def.defName.Equals(near, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Position.DistanceTo(map.Center))
                .FirstOrDefault();
            if (anyThing != null)
                return anyThing.Position;

            throw new InvalidOperationException(
                $"Cannot resolve anchor '{near}'. Provide an entity ID (e.g. 'Building_123'), " +
                $"zone name, building type (e.g. 'CookStove'), zone type ('Stockpile', 'Farm'), " +
                $"or 'MapCenter'.");
        }

        /// <summary>
        /// Get the resolved anchor description for audit trail
        /// </summary>
        private static (string id, int x, int z) DescribeAnchor(string near, IntVec3 pos, Map map)
        {
            // Try to find the actual entity for ID
            var thing = map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == near);
            string resolvedId = thing != null ? thing.ThingID : near;
            return (resolvedId, pos.x, pos.z);
        }

        [GameRLAction("PlaceBuildingNear", Description = "Place building(s) near a landmark or entity")]
        public static string PlaceBuildingNear(
            [GameRLParam("Building")] string buildingDefName,
            [GameRLParam("Near")] string near,
            [GameRLParam("Count")] int count = 1,
            [GameRLParam("Stuff")] string stuffDefName = null)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("PlaceBuildingNear: No map loaded");

            var buildingDef = DefDatabase<ThingDef>.GetNamed(buildingDefName, errorOnFail: false);
            if (buildingDef == null)
                throw new InvalidOperationException($"PlaceBuildingNear: Unknown building '{buildingDefName}'. Use ListBuildables to see available buildings.");

            ThingDef stuffDef = null;
            if (stuffDefName != null)
            {
                stuffDef = DefDatabase<ThingDef>.GetNamed(stuffDefName, errorOnFail: false);
                if (stuffDef == null)
                    throw new InvalidOperationException($"PlaceBuildingNear: Unknown material '{stuffDefName}'. Common: WoodLog, Steel, BlocksSandstone");
            }
            else if (buildingDef.MadeFromStuff)
            {
                stuffDef = ThingDefOf.WoodLog;
            }

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            var placed = new List<string>();
            float searchRadius = 15f;

            foreach (var cell in GenRadial.RadialCellsAround(anchor, searchRadius, true))
            {
                if (placed.Count >= count) break;
                if (!cell.InBounds(map)) continue;

                // Try all 4 rotations
                bool didPlace = false;
                foreach (var rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
                {
                    var report = GenConstruct.CanPlaceBlueprintAt(buildingDef, cell, rot, map, godMode: false, thing: null, stuffDef: stuffDef);
                    if (report.Accepted)
                    {
                        var thing = ThingMaker.MakeThing(buildingDef, stuffDef);
                        GenSpawn.Spawn(thing, cell, map, rot);
                        placed.Add($"({cell.x},{cell.z})");
                        didPlace = true;
                        break;
                    }
                }
            }

            if (placed.Count == 0)
            {
                throw new InvalidOperationException(
                    $"PlaceBuildingNear: Could not find valid placement for {buildingDefName} near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z}). Check for obstructions or insufficient space.");
            }

            var desc = $"Placed {placed.Count} {buildingDefName} near {near} (resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z}) at {string.Join(", ", placed)}";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)placed.Count, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("EstablishFarm", Description = "Create a growing zone on fertile soil near a landmark")]
        public static string EstablishFarm(
            [GameRLParam("Near")] string near,
            [GameRLParam("Crop")] string plantDefName = null,
            [GameRLParam("Size")] int size = 25)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("EstablishFarm: No map loaded");

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            // Find fertile cells expanding outward from anchor, prefer higher fertility
            var candidates = new List<IntVec3>();
            float searchRadius = 30f;

            foreach (var cell in GenRadial.RadialCellsAround(anchor, searchRadius, true))
            {
                if (candidates.Count >= size) break;
                if (!cell.InBounds(map)) continue;
                if (cell.GetFertility(map) <= 0) continue;
                if (map.zoneManager.ZoneAt(cell) != null) continue;
                if (cell.GetEdifice(map) != null) continue;

                candidates.Add(cell);
            }

            // Sort by fertility descending to prefer rich soil, then re-take size
            candidates = candidates
                .OrderByDescending(c => c.GetFertility(map))
                .Take(size)
                .ToList();

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"EstablishFarm: No fertile soil found near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})");
            }

            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var cell in candidates)
            {
                zone.AddCell(cell);
            }

            if (plantDefName != null)
            {
                var plantDef = DefDatabase<ThingDef>.GetNamed(plantDefName, errorOnFail: false);
                if (plantDef != null)
                {
                    zone.SetPlantDefToGrow(plantDef);
                }
            }

            float avgFertility = candidates.Average(c => c.GetFertility(map));
            var desc = $"Established farm ({candidates.Count} cells, avg fertility {avgFertility:F1}) near {near} " +
                       $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)candidates.Count, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("EstablishStorage", Description = "Create a stockpile zone near a landmark")]
        public static string EstablishStorage(
            [GameRLParam("Near")] string near,
            [GameRLParam("Size")] int size = 25)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("EstablishStorage: No map loaded");

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            var candidates = new List<IntVec3>();
            float searchRadius = 20f;

            foreach (var cell in GenRadial.RadialCellsAround(anchor, searchRadius, true))
            {
                if (candidates.Count >= size) break;
                if (!cell.InBounds(map)) continue;
                if (!cell.Standable(map)) continue;
                if (map.zoneManager.ZoneAt(cell) != null) continue;

                candidates.Add(cell);
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"EstablishStorage: No valid space found near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})");
            }

            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var cell in candidates)
            {
                zone.AddCell(cell);
            }

            var desc = $"Established stockpile ({candidates.Count} cells) near {near} " +
                       $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)candidates.Count, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("DesignateMiningNear", Description = "Designate mineable rocks near a landmark for mining")]
        public static string DesignateMiningNear(
            [GameRLParam("Near")] string near,
            [GameRLParam("Count")] int count = 20)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("DesignateMiningNear: No map loaded");

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            int designated = 0;
            float searchRadius = 25f;

            foreach (var cell in GenRadial.RadialCellsAround(anchor, searchRadius, true))
            {
                if (designated >= count) break;
                if (!cell.InBounds(map)) continue;

                var mineable = cell.GetFirstMineable(map);
                if (mineable != null &&
                    map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) == null)
                {
                    map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.Mine));
                    designated++;
                }
            }

            if (designated == 0)
            {
                throw new InvalidOperationException(
                    $"DesignateMiningNear: No mineable rocks found near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})");
            }

            var desc = $"Designated {designated} rocks for mining near {near} " +
                       $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)designated, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("DesignateClearNear", Description = "Designate trees/plants for cutting near a landmark")]
        public static string DesignateClearNear(
            [GameRLParam("Near")] string near,
            [GameRLParam("Radius")] int radius = 10)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("DesignateClearNear: No map loaded");

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            int designated = 0;

            foreach (var cell in GenRadial.RadialCellsAround(anchor, radius, true))
            {
                if (!cell.InBounds(map)) continue;

                var plant = cell.GetPlant(map);
                if (plant != null && plant.def.plant.IsTree &&
                    map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) == null)
                {
                    map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                    designated++;
                }
            }

            if (designated == 0)
            {
                throw new InvalidOperationException(
                    $"DesignateClearNear: No trees found near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})");
            }

            var desc = $"Designated {designated} trees for cutting near {near} " +
                       $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)designated, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        /// <summary>
        /// Build a JSON result matching the ResolvedPlacement format expected by the Rust bridge
        /// </summary>
        private static string BuildSpatialResult(string description, uint count, string anchorResolved, int anchorX, int anchorZ)
        {
            return $"{{\"Description\":\"{EscapeJson(description)}\",\"Count\":{count}," +
                   $"\"AnchorResolved\":\"{EscapeJson(anchorResolved)}\",\"AnchorPosition\":[{anchorX},{anchorZ}]}}";
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
```

- [ ] **Step 2: Verify C# compilation**

Run: `cd adapters/rimworld && dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj`
Expected: build succeeds (the `[GameRLAction]` attributes auto-register via assembly scanning)

- [ ] **Step 3: Commit**

```bash
git add adapters/rimworld/RimWorld.GameRL/Actions/SpatialActions.cs
git commit -m "feat(rimworld): add SpatialActions with anchor resolution and 5 intent methods"
```

---

### Task 8: ValidActions — Swap Coordinate Actions for Intent Actions

**Files:**
- Modify: `adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs:1482-1487`

- [ ] **Step 1: Replace coordinate actions with intent actions in ComputeValidActions**

In `adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs`, find lines 1482-1487:

```csharp
            // Map actions (always possible if map exists)
            valid.Add("PlaceBlueprint");
            valid.Add("CreateStockpile");
            valid.Add("CreateGrowingZone");
            valid.Add("DesignateMine");
            valid.Add("DesignateCutPlants");
```

Replace with:

```csharp
            // Intent-based spatial actions (game resolves coordinates)
            valid.Add("PlaceBuildingNear");
            valid.Add("EstablishStorage");
            // Only advertise EstablishFarm if fertile soil exists
            try
            {
                bool hasFertileSoil = map.AllCells.Any(c => c.GetFertility(map) > 0
                    && map.zoneManager.ZoneAt(c) == null);
                if (hasFertileSoil) valid.Add("EstablishFarm");
            }
            catch { valid.Add("EstablishFarm"); } // fallback: always advertise
            // Only advertise mining if mineable rocks exist
            try
            {
                bool hasMineable = map.listerThings.AllThings.Any(t => t.def.mineable);
                if (hasMineable) valid.Add("DesignateMiningNear");
            }
            catch { valid.Add("DesignateMiningNear"); }
            // Only advertise clearing if trees exist
            try
            {
                bool hasTrees = map.listerThings.AllThings.Any(t => t.def.plant?.IsTree == true);
                if (hasTrees) valid.Add("DesignateClearNear");
            }
            catch { valid.Add("DesignateClearNear"); }
```

- [ ] **Step 2: Verify C# compilation**

Run: `cd adapters/rimworld && dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj`
Expected: build succeeds

- [ ] **Step 3: Commit**

```bash
git add adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs
git commit -m "feat(rimworld): replace coordinate actions with intent actions in ValidActions"
```

---

### Task 8b: Landmarks Section in Observations

**Files:**
- Modify: `adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs`

- [ ] **Step 1: Add Landmarks extraction method**

In `adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs`, find the class body and add this method (near the other extraction methods):

```csharp
        /// <summary>
        /// Extract a curated list of spatial anchors the agent can reference in Near parameters.
        /// </summary>
        private static List<object> ExtractLandmarks(Map map)
        {
            var landmarks = new List<object>();

            // MapCenter is always available
            landmarks.Add(new { Name = "MapCenter", Type = "MapCenter", X = map.Center.x, Y = map.Center.z });

            // Named zones (stockpiles, growing zones)
            foreach (var zone in map.zoneManager.AllZones.Take(10))
            {
                var cells = zone.Cells.ToList();
                if (cells.Count == 0) continue;
                int cx = (int)cells.Average(c => c.x);
                int cz = (int)cells.Average(c => c.z);
                string zoneType = zone is Zone_Stockpile ? "Stockpile"
                    : zone is Zone_Growing ? "Farm"
                    : "Zone";
                landmarks.Add(new { Name = zone.label ?? zoneType, Type = zoneType, X = cx, Y = cz });
            }

            // Key buildings (one per type, deduplicated)
            var seenTypes = new HashSet<string>();
            foreach (var building in map.listerBuildings.allBuildingsColonist
                .OrderBy(b => b.Position.DistanceTo(map.Center)))
            {
                if (seenTypes.Count >= 10) break;
                if (seenTypes.Add(building.def.defName))
                {
                    landmarks.Add(new {
                        Name = building.def.defName,
                        Id = building.ThingID,
                        Type = "Building",
                        X = building.Position.x,
                        Y = building.Position.z
                    });
                }
            }

            return landmarks;
        }
```

- [ ] **Step 2: Wire Landmarks into observation output**

Find the method that builds the observation JSON (the `ExtractObservation` or equivalent method that populates the observation dictionary). Add the Landmarks section to the observation:

```csharp
            // Landmarks — spatial anchors for intent-based actions
            try
            {
                obs["Landmarks"] = ExtractLandmarks(map);
            }
            catch { }
```

Add this in the main observation extraction method, in the section that adds top-level keys to the observation. Place it near the existing zone/entity sections.

- [ ] **Step 3: Register "landmarks" in the observe tool's section mapping**

In `crates/game-rl-server/src/tools.rs`, find the `section_to_field` function and add:

```rust
        "landmarks" => "Landmarks",
```

And add "landmarks" to the observe tool's section list in the description string (around line 197):

```rust
"Sections: colonists, resources, entities, terrain, rooms, research, zones, threats, factions, prisoners, traders, power, beds, alerts, actions, map, landmarks\n",
```

- [ ] **Step 4: Verify builds**

Run: `cargo check -p game-rl-server && cd adapters/rimworld && dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj`
Expected: both compile

- [ ] **Step 5: Commit**

```bash
git add adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs crates/game-rl-server/src/tools.rs
git commit -m "feat: add Landmarks section to observations for spatial action anchors"
```

---

### Task 9: Update Step Tool Description with Intent Action Examples

**Files:**
- Modify: `crates/game-rl-server/src/tools.rs:110-152` (step tool description)

- [ ] **Step 1: Update step tool description**

In `crates/game-rl-server/src/tools.rs`, find the step tool description (lines 112-122) and replace with:

```rust
        ToolDef {
            name: "step".into(),
            description: concat!(
                "Execute an action in the game. Returns observation + reward.\n",
                "\n",
                "IMPORTANT: Action MUST be a JSON object with a \"Type\" field (PascalCase).\n",
                "\n",
                "ColonistId must be a real colonist name from the observation (e.g. \"Lizzie\" or \"Fox\"), NOT a placeholder.\n",
                "\n",
                "Spatial actions use Near parameter (entity ID, type name, or 'MapCenter'):\n",
                "  {\"Action\": {\"Type\": \"PlaceBuildingNear\", \"Building\": \"Bed\", \"Near\": \"Stockpile\", \"Count\": 3}}\n",
                "  {\"Action\": {\"Type\": \"EstablishFarm\", \"Near\": \"MapCenter\", \"Crop\": \"PlantRice\"}}\n",
                "  {\"Action\": {\"Type\": \"EstablishStorage\", \"Near\": \"CookStove\"}}\n",
                "  {\"Action\": {\"Type\": \"DesignateMiningNear\", \"Near\": \"MapCenter\", \"Count\": 10}}\n",
                "  {\"Action\": {\"Type\": \"DesignateClearNear\", \"Near\": \"Stockpile\", \"Radius\": 15}}\n",
                "\n",
                "Other examples:\n",
                "  {\"Action\": {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"<name>\", \"WorkType\": \"Construction\", \"Priority\": 1}}\n",
                "  {\"Action\": {\"Type\": \"Draft\", \"ColonistId\": \"<name>\"}}\n",
            ).into(),
```

- [ ] **Step 2: Update the Type enum description in the schema**

In the same file, find the Type property description in the step tool's input schema (around line 136):

```rust
                            "Type": {
                                "type": "string",
                                "description": "Action type name (PascalCase). e.g. Draft, Undraft, Move, SetWorkPriority, Attack, SetSpeed, DesignateHunt, DesignateMine, PlaceBlueprint, CreateZone, Rescue, TendTo, Equip, SaveCheckpoint, LoadCheckpoint, Unpause"
                            }
```

Replace with:

```rust
                            "Type": {
                                "type": "string",
                                "description": "Action type name (PascalCase). Spatial: PlaceBuildingNear, EstablishFarm, EstablishStorage, DesignateMiningNear, DesignateClearNear. Other: Draft, Undraft, Move, SetWorkPriority, Attack, SetSpeed, DesignateHunt, Rescue, TendTo, Equip, SaveCheckpoint, LoadCheckpoint, Unpause"
                            }
```

- [ ] **Step 3: Verify compilation**

Run: `cargo check -p game-rl-server`
Expected: compiles cleanly

- [ ] **Step 4: Commit**

```bash
git add crates/game-rl-server/src/tools.rs
git commit -m "feat(server): update step tool description with spatial intent examples"
```

---

### Task 10: Full Build Verification

- [ ] **Step 1: Run full Rust build**

Run: `cargo build`
Expected: all crates compile successfully

- [ ] **Step 2: Run all Rust tests**

Run: `cargo test`
Expected: all tests pass

- [ ] **Step 3: Run C# build**

Run: `cd adapters/rimworld && dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj`
Expected: build succeeds

- [ ] **Step 4: Commit any fixups if needed**

If any issues were found and fixed, commit them:

```bash
git add -A && git commit -m "fix: address build issues from spatial actions implementation"
```
