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
        "DefendColony",
    ];

    /// Check if an action type name is a spatial intent
    pub fn is_spatial_action(action_type: &str) -> bool {
        Self::VARIANTS
            .iter()
            .any(|v| v.eq_ignore_ascii_case(action_type))
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
            } => {
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
