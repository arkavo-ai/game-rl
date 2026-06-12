//! Intent-based spatial action types (spec draft-02 §7)
//!
//! These types allow agents to express spatial intent (e.g., "place a bed near the stockpile")
//! without specifying exact coordinates. The game environment resolves intent to concrete
//! placements using its own spatial APIs, subject to the resolution-integrity rules:
//!
//! - REQ-SPA-02: the resolver MUST NOT silently substitute a different anchor. Infeasible
//!   placements fail with alternatives unless the agent set `AllowFallback: true`, and any
//!   fallback MUST be reported via `ResolvedPlacement::fallback_applied`.
//! - REQ-SPA-03: ambiguous anchors resolve relative to the agent's activity centroid,
//!   never the geometric map center.

use serde::{Deserialize, Serialize};

/// A grid position, serialized as `{"X": .., "Y": ..}` per draft-02
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct GridPos {
    pub x: i32,
    pub y: i32,
}

impl From<(i32, i32)> for GridPos {
    fn from((x, y): (i32, i32)) -> Self {
        Self { x, y }
    }
}

/// Spatial anchor — the `Near` value of an intent (REQ-SPA-01 grammar)
///
/// Accepts, in order of specificity:
/// - an offset object: `{"Anchor": "ColonyCenter", "Direction": "N", "Distance": 15}`
/// - a coordinate escape hatch: `{"X": 42, "Y": 13}`
/// - a name: entity ID, zone label, type name, or landmark ID (`"Region_NE"`, `"MapCenter"`)
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Anchor {
    /// Resolved anchor displaced `Distance` cells toward `Direction` (8-wind compass)
    Offset {
        #[serde(rename = "Anchor")]
        anchor: String,
        #[serde(rename = "Direction")]
        direction: String,
        #[serde(rename = "Distance")]
        distance: i32,
    },
    /// Literal position
    Coord {
        #[serde(rename = "X")]
        x: i32,
        #[serde(rename = "Y")]
        y: i32,
    },
    /// Entity ID, zone label, type name, or landmark ID
    Named(String),
}

impl Anchor {
    /// Human-readable form for audit trails (`AnchorRequested`)
    pub fn describe(&self) -> String {
        match self {
            Anchor::Named(name) => name.clone(),
            Anchor::Offset {
                anchor,
                direction,
                distance,
            } => format!("{anchor}+{direction}{distance}"),
            Anchor::Coord { x, y } => format!("({x},{y})"),
        }
    }
}

impl From<&str> for Anchor {
    fn from(s: &str) -> Self {
        Anchor::Named(s.to_string())
    }
}

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
        near: Anchor,
        #[serde(rename = "Count", default = "default_count")]
        count: u32,
        #[serde(rename = "Stuff", skip_serializing_if = "Option::is_none")]
        stuff: Option<String>,
        #[serde(rename = "AllowFallback", default)]
        allow_fallback: bool,
    },

    /// Establish a growing zone on fertile soil near an anchor
    /// Replaces: CreateGrowingZone(X, Y, Width, Height, Plant)
    EstablishFarm {
        #[serde(rename = "Near")]
        near: Anchor,
        #[serde(rename = "Crop", skip_serializing_if = "Option::is_none")]
        crop: Option<String>,
        #[serde(rename = "Size", skip_serializing_if = "Option::is_none")]
        size: Option<u32>,
        #[serde(rename = "AllowFallback", default)]
        allow_fallback: bool,
    },

    /// Establish a stockpile zone near an anchor
    /// Replaces: CreateStockpile(X, Y, Width, Height)
    EstablishStorage {
        #[serde(rename = "Near")]
        near: Anchor,
        #[serde(rename = "Size", skip_serializing_if = "Option::is_none")]
        size: Option<u32>,
        #[serde(rename = "AllowFallback", default)]
        allow_fallback: bool,
    },

    /// Designate mining near an anchor (finds mineable rocks)
    /// Replaces: DesignateMine(X, Y, Radius)
    DesignateMiningNear {
        #[serde(rename = "Near")]
        near: Anchor,
        #[serde(rename = "Count", skip_serializing_if = "Option::is_none")]
        count: Option<u32>,
        #[serde(rename = "AllowFallback", default)]
        allow_fallback: bool,
    },

    /// Designate trees/plants for cutting near an anchor
    /// Replaces: DesignateCutPlants(X, Y, Radius)
    DesignateClearNear {
        #[serde(rename = "Near")]
        near: Anchor,
        #[serde(rename = "Radius", skip_serializing_if = "Option::is_none")]
        radius: Option<u32>,
        #[serde(rename = "AllowFallback", default)]
        allow_fallback: bool,
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
        "DefendColony",
    ];

    /// Check if an action type name is a spatial intent
    pub fn is_spatial_action(action_type: &str) -> bool {
        Self::VARIANTS
            .iter()
            .any(|v| v.eq_ignore_ascii_case(action_type))
    }

    /// The requested anchor, if this intent has one
    pub fn near(&self) -> Option<&Anchor> {
        match self {
            SpatialIntent::PlaceBuildingNear { near, .. }
            | SpatialIntent::EstablishFarm { near, .. }
            | SpatialIntent::EstablishStorage { near, .. }
            | SpatialIntent::DesignateMiningNear { near, .. }
            | SpatialIntent::DesignateClearNear { near, .. } => Some(near),
        }
    }

    /// Whether the agent permitted fallback relocation (REQ-SPA-02)
    pub fn allow_fallback(&self) -> bool {
        match self {
            SpatialIntent::PlaceBuildingNear { allow_fallback, .. }
            | SpatialIntent::EstablishFarm { allow_fallback, .. }
            | SpatialIntent::EstablishStorage { allow_fallback, .. }
            | SpatialIntent::DesignateMiningNear { allow_fallback, .. }
            | SpatialIntent::DesignateClearNear { allow_fallback, .. } => *allow_fallback,
        }
    }
}

/// Result of spatial resolution — what the game actually placed (REQ-SPA-06)
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ResolvedPlacement {
    /// Human-readable description with full audit trail
    pub description: String,
    /// How many items were successfully placed (may be less than requested)
    pub count: u32,
    /// The anchor the agent asked for (audit trail)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub anchor_requested: Option<String>,
    /// The entity/zone that was resolved as the anchor
    pub anchor_resolved: String,
    /// The resolved anchor's coordinates
    pub anchor_position: GridPos,
    /// Concrete cells used by the placement
    #[serde(default)]
    pub positions: Vec<GridPos>,
    /// True iff the environment relocated away from the requested anchor
    /// (only permitted when the agent set `AllowFallback: true`)
    #[serde(default)]
    pub fallback_applied: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_place_building_near_from_json() {
        let json = r#"{"Type":"PlaceBuildingNear","Building":"Bed","Near":"Stockpile","Count":3,"Stuff":"WoodLog"}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        match intent {
            SpatialIntent::PlaceBuildingNear {
                building,
                near,
                count,
                stuff,
                allow_fallback,
            } => {
                assert_eq!(building, "Bed");
                assert!(matches!(near, Anchor::Named(ref n) if n == "Stockpile"));
                assert_eq!(count, 3);
                assert_eq!(stuff.unwrap(), "WoodLog");
                assert!(!allow_fallback, "AllowFallback must default to false");
            }
            _ => panic!("Wrong variant"),
        }
    }

    #[test]
    fn test_establish_farm_defaults() {
        let json = r#"{"Type":"EstablishFarm","Near":"MapCenter"}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        match intent {
            SpatialIntent::EstablishFarm {
                near,
                crop,
                size,
                allow_fallback,
            } => {
                assert!(matches!(near, Anchor::Named(ref n) if n == "MapCenter"));
                assert!(crop.is_none());
                assert!(size.is_none());
                assert!(!allow_fallback);
            }
            _ => panic!("Wrong variant"),
        }
    }

    #[test]
    fn test_anchor_offset_form() {
        let json = r#"{"Type":"EstablishFarm","Near":{"Anchor":"ColonyCenter","Direction":"NE","Distance":15},"AllowFallback":true}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        let near = intent.near().unwrap();
        match near {
            Anchor::Offset {
                anchor,
                direction,
                distance,
            } => {
                assert_eq!(anchor, "ColonyCenter");
                assert_eq!(direction, "NE");
                assert_eq!(*distance, 15);
            }
            _ => panic!("Expected offset anchor, got {near:?}"),
        }
        assert!(intent.allow_fallback());
    }

    #[test]
    fn test_anchor_coordinate_form() {
        let json = r#"{"Type":"EstablishStorage","Near":{"X":42,"Y":13}}"#;
        let intent: SpatialIntent = serde_json::from_str(json).unwrap();
        match intent.near().unwrap() {
            Anchor::Coord { x, y } => {
                assert_eq!(*x, 42);
                assert_eq!(*y, 13);
            }
            other => panic!("Expected coord anchor, got {other:?}"),
        }
    }

    #[test]
    fn test_resolved_placement_serialization() {
        let rp = ResolvedPlacement {
            description: "Placed 2 Beds near Stockpile_4821 at (42,13), (44,13)".into(),
            count: 2,
            anchor_requested: Some("Stockpile".into()),
            anchor_resolved: "Stockpile_4821".into(),
            anchor_position: (42, 15).into(),
            positions: vec![(42, 13).into(), (44, 13).into()],
            fallback_applied: false,
        };
        let json = serde_json::to_value(&rp).unwrap();
        assert_eq!(json["Count"], 2);
        assert_eq!(json["AnchorRequested"], "Stockpile");
        assert_eq!(json["AnchorResolved"], "Stockpile_4821");
        assert_eq!(json["AnchorPosition"]["X"], 42);
        assert_eq!(json["Positions"][1]["X"], 44);
        assert_eq!(json["FallbackApplied"], false);
    }

    #[test]
    fn test_resolved_placement_v1_compat() {
        // v1 producers (no audit fields) must still deserialize
        let json =
            r#"{"Description":"d","Count":1,"AnchorResolved":"Z","AnchorPosition":{"X":1,"Y":2}}"#;
        let rp: ResolvedPlacement = serde_json::from_str(json).unwrap();
        assert!(rp.anchor_requested.is_none());
        assert!(rp.positions.is_empty());
        assert!(!rp.fallback_applied);
    }

    #[test]
    fn test_spatial_intent_names() {
        assert_eq!(
            SpatialIntent::VARIANTS,
            &[
                "PlaceBuildingNear",
                "EstablishFarm",
                "EstablishStorage",
                "DesignateMiningNear",
                "DesignateClearNear",
                "DefendColony"
            ]
        );
    }
}
