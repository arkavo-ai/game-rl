//! GridColony — the Game-RL reference environment (spec draft-02, Level 3)
//!
//! Implements every normative requirement so the conformance harness can
//! validate itself, and ships behavioral probes (the `fertile-corner`
//! scenario) that make center-biased spatial policies measurably fail.

use async_trait::async_trait;
use game_rl_core::action::{ActionDefinition, ParamDefinition};
use game_rl_core::observation::TerminationReason;
use game_rl_core::stream::StreamProfile;
use game_rl_core::{
    Action, ActionSpace, AgentConfig, AgentId, AgentManifest, AgentType, Anchor, Capabilities,
    Compliance, EpisodeSummary, GameEvent, GameManifest, GameRLError, GridPos, Observation,
    PROTOCOL_VERSION, ResolvedPlacement, Result, Scenario, SpatialIntent, StepResult,
    StreamDescriptor,
};
use game_rl_server::GameEnvironment;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::collections::HashMap;

use crate::world::{
    self, Cluster, FARM_FERTILITY_MIN, GRID, MAX_EPISODE_TICKS, SCENARIOS, World, ZoneKind,
    clamp_pos, dist2, idx,
};

/// What a spatial intent would do, resolved but not yet applied (dry-run support)
struct SpatialPlan {
    placement: ResolvedPlacement,
    effect: PlannedEffect,
    /// One-time reward components granted on application
    rewards: Vec<(&'static str, f64)>,
}

enum PlannedEffect {
    Buildings {
        kind: String,
        cells: Vec<GridPos>,
    },
    Farm {
        cells: Vec<GridPos>,
        crop: Option<String>,
    },
    Storage {
        cells: Vec<GridPos>,
    },
    Mine {
        cells: Vec<GridPos>,
    },
    Clear {
        cells: Vec<GridPos>,
    },
}

#[derive(serde::Serialize, serde::Deserialize)]
struct TrajectoryFile {
    seed: u64,
    scenario: String,
    steps: Vec<TrajectoryStep>,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct TrajectoryStep {
    agent_id: String,
    action: Value,
    ticks: u32,
}

pub struct ReferenceEnv {
    world: World,
    agents: HashMap<AgentId, AgentType>,
    step_id: u64,
    cum_reward: f64,
    reward_breakdown: HashMap<String, f64>,
    done: bool,
    truncated: bool,
    termination_reason: Option<TerminationReason>,
    trajectory: Vec<TrajectoryStep>,
    /// Conformance negative-test hook: GAMERL_REF_BREAK=silent_errors makes
    /// invalid actions return success-shaped responses (REQ-ERR-01 violation).
    break_mode: Option<String>,
}

impl ReferenceEnv {
    pub fn new(seed: u64, scenario: &str) -> Self {
        Self {
            world: World::generate(seed, scenario),
            agents: HashMap::new(),
            step_id: 0,
            cum_reward: 0.0,
            reward_breakdown: HashMap::new(),
            done: false,
            truncated: false,
            termination_reason: None,
            trajectory: Vec::new(),
            break_mode: std::env::var("GAMERL_REF_BREAK").ok(),
        }
    }

    pub fn build_manifest() -> GameManifest {
        let param_str = |options: Option<Vec<&str>>| ParamDefinition::String {
            options: options.map(|o| o.iter().map(|s| s.to_string()).collect()),
        };
        let actions = vec![
            ActionDefinition {
                name: "Wait".into(),
                description: Some("Do nothing".into()),
                params: HashMap::new(),
            },
            ActionDefinition {
                name: "Move".into(),
                description: Some("Move a colonist one cell in a compass direction".into()),
                params: HashMap::from([
                    ("ColonistId".to_string(), ParamDefinition::EntityId),
                    (
                        "Direction".to_string(),
                        param_str(Some(vec!["N", "NE", "E", "SE", "S", "SW", "W", "NW"])),
                    ),
                ]),
            },
            ActionDefinition {
                name: "Harvest".into(),
                description: Some("Harvest the nearest resource of the given type".into()),
                params: HashMap::from([(
                    "Target".to_string(),
                    param_str(Some(vec!["Wood", "Stone", "Berries"])),
                )]),
            },
            ActionDefinition {
                name: "DefendColony".into(),
                description: Some(
                    "Repel hostiles near the colony (valid while under attack)".into(),
                ),
                params: HashMap::new(),
            },
            ActionDefinition {
                name: "PlaceBuildingNear".into(),
                description: Some("Place building(s) near an anchor".into()),
                params: HashMap::from([
                    (
                        "Building".to_string(),
                        param_str(Some(vec!["Shelter", "Bed"])),
                    ),
                    ("Near".to_string(), param_str(None)),
                    (
                        "Count".to_string(),
                        ParamDefinition::Int {
                            min: Some(1),
                            max: Some(10),
                        },
                    ),
                ]),
            },
            ActionDefinition {
                name: "EstablishFarm".into(),
                description: Some("Create a growing zone on fertile soil near an anchor".into()),
                params: HashMap::from([
                    ("Near".to_string(), param_str(None)),
                    (
                        "Crop".to_string(),
                        param_str(Some(vec!["Rice", "Potatoes"])),
                    ),
                    (
                        "Size".to_string(),
                        ParamDefinition::Int {
                            min: Some(4),
                            max: Some(100),
                        },
                    ),
                ]),
            },
            ActionDefinition {
                name: "EstablishStorage".into(),
                description: Some("Create a storage zone near an anchor".into()),
                params: HashMap::from([("Near".to_string(), param_str(None))]),
            },
            ActionDefinition {
                name: "DesignateMiningNear".into(),
                description: Some("Mine rocks near an anchor".into()),
                params: HashMap::from([("Near".to_string(), param_str(None))]),
            },
            ActionDefinition {
                name: "DesignateClearNear".into(),
                description: Some("Clear trees near an anchor".into()),
                params: HashMap::from([("Near".to_string(), param_str(None))]),
            },
        ];

        GameManifest {
            name: "GridColony".into(),
            version: env!("CARGO_PKG_VERSION").into(),
            game_rl_version: PROTOCOL_VERSION.into(),
            capabilities: Capabilities {
                multi_agent: true,
                max_agents: 8,
                agent_types: vec![
                    "Observer".into(),
                    "Player".into(),
                    "Entity".into(),
                    "Controller".into(),
                    "System".into(),
                    "Director".into(),
                ],
                deterministic: true,
                save_replay: true,
                domain_randomization: true,
                headless: true,
                variable_timestep: true,
                spatial_intent: true,
            },
            default_observation_space: Some(json!({
                "Type": "structured",
                "Sections": ["colonists", "resources", "terrain", "entities", "zones",
                             "alerts", "actions", "landmarks", "map"]
            })),
            default_action_space: Some(ActionSpace::DiscreteParameterized { actions }),
            reward_components: vec![
                reward_def(
                    "survival",
                    "Per-tick survival of living colonists",
                    [0.0, 1.0],
                ),
                reward_def(
                    "food_production",
                    "Food gained from farms and foraging",
                    [0.0, 10.0],
                ),
                reward_def(
                    "farm_fertility_quality",
                    "Mean fertility of newly farmed cells — the spatial decision-quality probe",
                    [0.0, 1.4],
                ),
                reward_def("shelter", "Buildings constructed", [0.0, 5.0]),
                reward_def("threat_defense", "Hostiles repelled", [0.0, 10.0]),
            ],
            stream_profiles: HashMap::from([(
                "none".to_string(),
                StreamProfile {
                    name: "none".into(),
                    streams: vec![],
                },
            )]),
            scenarios: SCENARIOS
                .iter()
                .map(|s| Scenario {
                    name: s.to_string(),
                    description: Some(scenario_description(s).to_string()),
                    config: HashMap::new(),
                })
                .collect(),
            tick_rate: 60,
            max_episode_ticks: Some(MAX_EPISODE_TICKS),
            compliance: Some(Compliance {
                level: 3,
                version: PROTOCOL_VERSION.into(),
                test_results_url: None,
            }),
        }
    }

    // ----- anchors (REQ-SPA-01..03) -----

    fn landmark_position(&self, name: &str) -> Option<(String, GridPos)> {
        match name {
            "MapCenter" => Some((
                "MapCenter".into(),
                GridPos {
                    x: GRID / 2,
                    y: GRID / 2,
                },
            )),
            "ColonyCenter" | "AgentCenter" => {
                Some(("ColonyCenter".into(), self.world.colony_center()))
            }
            _ => {
                if let Some((id, pos)) = world::region_centroids()
                    .into_iter()
                    .find(|(n, _)| *n == name)
                {
                    return Some((id.to_string(), pos));
                }
                for clusters in [
                    self.world.fertile_clusters(),
                    self.world.rock_clusters(),
                    self.world.tree_clusters(),
                ] {
                    if let Some(c) = clusters.into_iter().find(|c| c.id == name) {
                        return Some((c.id, c.centroid));
                    }
                }
                None
            }
        }
    }

    fn entity_position(&self, name: &str) -> Option<(String, GridPos)> {
        if let Some(b) = self.world.buildings.iter().find(|b| b.id == name) {
            return Some((b.id.clone(), b.pos));
        }
        if let Some(z) = self.world.zones.iter().find(|z| z.id == name) {
            return Some((z.id.clone(), world::centroid(&z.cells)));
        }
        if let Some(c) = self.world.colonists.iter().find(|c| c.id == name) {
            return Some((c.id.clone(), c.pos));
        }
        None
    }

    /// Type-name anchors resolve to the instance nearest the Reference Point
    /// (the colony's activity centroid) — NEVER the map center (REQ-SPA-03).
    fn type_position(&self, name: &str) -> Option<(String, GridPos)> {
        let rp = self.world.colony_center();
        let lower = name.to_ascii_lowercase();
        match lower.as_str() {
            "shelter" | "bed" => self
                .world
                .buildings
                .iter()
                .filter(|b| b.kind.eq_ignore_ascii_case(&lower))
                .min_by_key(|b| (dist2(b.pos, rp), b.pos.y, b.pos.x))
                .map(|b| (b.id.clone(), b.pos)),
            "farm" | "growingzone" => self
                .world
                .zones
                .iter()
                .filter(|z| z.kind == ZoneKind::Farm)
                .min_by_key(|z| {
                    let c = world::centroid(&z.cells);
                    (dist2(c, rp), c.y, c.x)
                })
                .map(|z| (z.id.clone(), world::centroid(&z.cells))),
            "storage" | "stockpile" => self
                .world
                .zones
                .iter()
                .filter(|z| z.kind == ZoneKind::Storage)
                .min_by_key(|z| {
                    let c = world::centroid(&z.cells);
                    (dist2(c, rp), c.y, c.x)
                })
                .map(|z| (z.id.clone(), world::centroid(&z.cells))),
            "tree" | "trees" | "wood" => self
                .nearest_cell(rp, |i| self.world.trees[i])
                .map(|p| (format!("Tree({},{})", p.x, p.y), p)),
            "rock" | "rocks" | "stone" => self
                .nearest_cell(rp, |i| self.world.rocks[i])
                .map(|p| (format!("Rock({},{})", p.x, p.y), p)),
            "berries" | "berry" => self
                .nearest_cell(rp, |i| self.world.berries[i])
                .map(|p| (format!("Berries({},{})", p.x, p.y), p)),
            _ => None,
        }
    }

    fn nearest_cell(&self, origin: GridPos, pred: impl Fn(usize) -> bool) -> Option<GridPos> {
        let mut best: Option<(i64, GridPos)> = None;
        for y in 0..GRID {
            for x in 0..GRID {
                let p = GridPos { x, y };
                if pred(idx(p)) {
                    let d = dist2(p, origin);
                    if best.is_none_or(|(bd, bp)| (d, p.y, p.x) < (bd, bp.y, bp.x)) {
                        best = Some((d, p));
                    }
                }
            }
        }
        best.map(|(_, p)| p)
    }

    fn anchor_alternatives(&self) -> String {
        let mut names: Vec<String> = vec!["ColonyCenter".into(), "MapCenter".into()];
        names.extend(
            self.world
                .fertile_clusters()
                .iter()
                .take(2)
                .map(|c| format!("{} at ({},{})", c.id, c.centroid.x, c.centroid.y)),
        );
        names.extend(
            world::region_centroids()
                .iter()
                .take(2)
                .map(|(n, _)| n.to_string()),
        );
        names.join(", ")
    }

    fn resolve_anchor(&self, anchor: &Anchor) -> Result<(String, GridPos)> {
        match anchor {
            Anchor::Coord { x, y } => {
                let p = clamp_pos(GridPos { x: *x, y: *y });
                Ok((format!("({},{})", p.x, p.y), p))
            }
            Anchor::Offset {
                anchor,
                direction,
                distance,
            } => {
                let (_, base) = self.resolve_anchor(&Anchor::Named(anchor.clone()))?;
                let (dx, dy) = world::direction_vec(direction).ok_or_else(|| {
                    GameRLError::InvalidAction(format!(
                        "Unknown direction \"{direction}\". Valid: N, NE, E, SE, S, SW, W, NW"
                    ))
                })?;
                let p = clamp_pos(GridPos {
                    x: base.x + dx * distance,
                    y: base.y + dy * distance,
                });
                Ok((format!("{anchor}+{direction}{distance}"), p))
            }
            Anchor::Named(name) => self
                .landmark_position(name)
                .or_else(|| self.entity_position(name))
                .or_else(|| self.type_position(name))
                .ok_or_else(|| {
                    GameRLError::InvalidAction(format!(
                        "Unknown anchor \"{name}\". Alternatives: {}",
                        self.anchor_alternatives()
                    ))
                }),
        }
    }

    // ----- spatial planning (dry-run capable, REQ-SPA-02/05/06) -----

    /// Free cells matching `pred`, nearest-first from `origin`, within `radius`
    fn candidate_cells(
        &self,
        origin: GridPos,
        radius: i32,
        limit: usize,
        pred: impl Fn(GridPos) -> bool,
    ) -> Vec<GridPos> {
        let mut cells: Vec<GridPos> = Vec::new();
        for y in (origin.y - radius).max(0)..=(origin.y + radius).min(GRID - 1) {
            for x in (origin.x - radius).max(0)..=(origin.x + radius).min(GRID - 1) {
                let p = GridPos { x, y };
                if dist2(p, origin) <= (radius as i64) * (radius as i64) && pred(p) {
                    cells.push(p);
                }
            }
        }
        cells.sort_by_key(|&p| (dist2(p, origin), p.y, p.x));
        cells.truncate(limit);
        cells
    }

    fn plan_spatial(&self, intent: &SpatialIntent) -> Result<SpatialPlan> {
        let near = intent.near().expect("all intents carry an anchor");
        let requested = near.describe();
        let (resolved, anchor_pos) = self.resolve_anchor(near)?;
        let allow_fallback = intent.allow_fallback();

        match intent {
            SpatialIntent::PlaceBuildingNear {
                building, count, ..
            } => {
                let kind = match building.to_ascii_lowercase().as_str() {
                    "shelter" => "Shelter",
                    "bed" => "Bed",
                    other => {
                        return Err(GameRLError::InvalidAction(format!(
                            "Unknown building \"{other}\". Valid: Shelter, Bed"
                        )));
                    }
                };
                let want = (*count).max(1) as usize;
                let cells = self.candidate_cells(anchor_pos, 10, want, |p| self.world.is_free(p));
                if cells.len() < want && !allow_fallback {
                    return Err(GameRLError::InvalidAction(format!(
                        "PlaceBuildingNear '{requested}': only {} free cells within radius 10 of {resolved} (need {want}). Alternatives: {}. Set AllowFallback=true to search farther.",
                        cells.len(),
                        self.anchor_alternatives()
                    )));
                }
                let (cells, fallback) = if cells.len() < want {
                    (
                        self.candidate_cells(anchor_pos, GRID, want, |p| self.world.is_free(p)),
                        true,
                    )
                } else {
                    (cells, false)
                };
                Ok(SpatialPlan {
                    placement: placement(
                        format!(
                            "Placed {} {kind}(s) near {resolved} at {}",
                            cells.len(),
                            fmt_cells(&cells)
                        ),
                        &cells,
                        &requested,
                        &resolved,
                        anchor_pos,
                        fallback,
                    ),
                    rewards: vec![("shelter", if kind == "Shelter" { 5.0 } else { 1.0 })],
                    effect: PlannedEffect::Buildings {
                        kind: kind.to_string(),
                        cells,
                    },
                })
            }

            SpatialIntent::EstablishFarm { crop, size, .. } => {
                let want = size.unwrap_or(25).clamp(4, 100) as usize;
                let farmable = |p: GridPos| {
                    self.world.is_free(p) && self.world.fertility[idx(p)] >= FARM_FERTILITY_MIN
                };
                let cells = self.candidate_cells(anchor_pos, 8, want, farmable);

                let (cells, used_anchor, used_pos, fallback) = if cells.len() >= want {
                    (cells, resolved.clone(), anchor_pos, false)
                } else if allow_fallback {
                    // Relocation is permitted and AUDITED: pick the best fertile
                    // cluster (largest, most fertile), never silently.
                    let best = self
                        .world
                        .fertile_clusters()
                        .into_iter()
                        .find(|c| {
                            self.candidate_cells(c.centroid, 8, want, farmable).len() >= want / 2
                        })
                        .ok_or_else(|| {
                            GameRLError::InvalidAction(
                                "EstablishFarm: no fertile region on the map can host a farm"
                                    .to_string(),
                            )
                        })?;
                    let fallback_cells = self.candidate_cells(best.centroid, 8, want, farmable);
                    (fallback_cells, best.id, best.centroid, true)
                } else {
                    let alts: Vec<String> = self
                        .world
                        .fertile_clusters()
                        .iter()
                        .take(3)
                        .map(|c| {
                            format!(
                                "{} at ({},{}) — {} cells, fertility {:.2}",
                                c.id, c.centroid.x, c.centroid.y, c.cell_count, c.mean_fertility
                            )
                        })
                        .collect();
                    return Err(GameRLError::InvalidAction(format!(
                        "EstablishFarm near '{requested}': only {} fertile cells within radius 8 of {resolved} (need {want}). Alternatives: {}. Set AllowFallback=true to relocate automatically.",
                        cells.len(),
                        if alts.is_empty() {
                            "none — no fertile soil on this map".to_string()
                        } else {
                            alts.join("; ")
                        }
                    )));
                };

                let mean_fert = cells
                    .iter()
                    .map(|&p| self.world.fertility[idx(p)] as f64)
                    .sum::<f64>()
                    / cells.len().max(1) as f64;

                Ok(SpatialPlan {
                    placement: placement(
                        format!(
                            "Established {}-cell farm near {used_anchor} (mean fertility {mean_fert:.2}){}",
                            cells.len(),
                            if fallback {
                                format!(" — FALLBACK from requested '{requested}'")
                            } else {
                                String::new()
                            }
                        ),
                        &cells,
                        &requested,
                        &used_anchor,
                        used_pos,
                        fallback,
                    ),
                    rewards: vec![("farm_fertility_quality", mean_fert)],
                    effect: PlannedEffect::Farm {
                        cells,
                        crop: crop.clone(),
                    },
                })
            }

            SpatialIntent::EstablishStorage { size, .. } => {
                let want = size.unwrap_or(16).clamp(4, 100) as usize;
                let cells = self.candidate_cells(anchor_pos, 6, want, |p| self.world.is_free(p));
                if cells.len() < want && !allow_fallback {
                    return Err(GameRLError::InvalidAction(format!(
                        "EstablishStorage near '{requested}': only {} free cells within radius 6 (need {want}). Alternatives: {}. Set AllowFallback=true to search farther.",
                        cells.len(),
                        self.anchor_alternatives()
                    )));
                }
                let (cells, fallback) = if cells.len() < want {
                    (
                        self.candidate_cells(anchor_pos, GRID, want, |p| self.world.is_free(p)),
                        true,
                    )
                } else {
                    (cells, false)
                };
                Ok(SpatialPlan {
                    placement: placement(
                        format!("Established {}-cell storage near {resolved}", cells.len()),
                        &cells,
                        &requested,
                        &resolved,
                        anchor_pos,
                        fallback,
                    ),
                    rewards: vec![],
                    effect: PlannedEffect::Storage { cells },
                })
            }

            SpatialIntent::DesignateMiningNear { count, .. } => {
                let want = count.unwrap_or(5).max(1) as usize;
                let cells =
                    self.candidate_cells(anchor_pos, 12, want, |p| self.world.rocks[idx(p)]);
                if cells.is_empty() {
                    let alts: Vec<String> = self
                        .world
                        .rock_clusters()
                        .iter()
                        .take(2)
                        .map(|c| format!("{} at ({},{})", c.id, c.centroid.x, c.centroid.y))
                        .collect();
                    return Err(GameRLError::InvalidAction(format!(
                        "DesignateMiningNear '{requested}': no rocks within radius 12 of {resolved}. Alternatives: {}",
                        if alts.is_empty() {
                            "none — no rocks on this map".to_string()
                        } else {
                            alts.join("; ")
                        }
                    )));
                }
                Ok(SpatialPlan {
                    placement: placement(
                        format!(
                            "Designated {} rock(s) for mining near {resolved}",
                            cells.len()
                        ),
                        &cells,
                        &requested,
                        &resolved,
                        anchor_pos,
                        false,
                    ),
                    rewards: vec![],
                    effect: PlannedEffect::Mine { cells },
                })
            }

            SpatialIntent::DesignateClearNear { radius, .. } => {
                let r = radius.unwrap_or(5).clamp(1, 32) as i32;
                let cells =
                    self.candidate_cells(anchor_pos, r, usize::MAX, |p| self.world.trees[idx(p)]);
                if cells.is_empty() {
                    return Err(GameRLError::InvalidAction(format!(
                        "DesignateClearNear '{requested}': no trees within radius {r} of {resolved}. Alternatives: {}",
                        self.anchor_alternatives()
                    )));
                }
                Ok(SpatialPlan {
                    placement: placement(
                        format!("Cleared {} tree(s) near {resolved}", cells.len()),
                        &cells,
                        &requested,
                        &resolved,
                        anchor_pos,
                        false,
                    ),
                    rewards: vec![],
                    effect: PlannedEffect::Clear { cells },
                })
            }
        }
    }

    fn apply_spatial(&mut self, plan: SpatialPlan) -> (String, Vec<(&'static str, f64)>) {
        match plan.effect {
            PlannedEffect::Buildings { kind, cells } => {
                for p in cells {
                    self.world.add_building(&kind, p);
                }
            }
            PlannedEffect::Farm { cells, crop } => {
                self.world.add_zone(ZoneKind::Farm, cells, crop);
            }
            PlannedEffect::Storage { cells } => {
                self.world.add_zone(ZoneKind::Storage, cells, None);
            }
            PlannedEffect::Mine { cells } => {
                for p in &cells {
                    self.world.rocks[idx(*p)] = false;
                }
                self.world.stone += cells.len() as f64 * 5.0;
            }
            PlannedEffect::Clear { cells } => {
                for p in &cells {
                    self.world.trees[idx(*p)] = false;
                }
                self.world.wood += cells.len() as f64 * 3.0;
            }
        }
        (plan.placement.description.clone(), plan.rewards)
    }

    // ----- non-spatial actions -----

    fn apply_action(
        &mut self,
        action_type: &str,
        params: &HashMap<String, Value>,
    ) -> Result<(String, Vec<(&'static str, f64)>)> {
        match action_type {
            "Wait" => Ok(("Waited".into(), vec![])),

            "Move" => {
                let dir = params
                    .get("Direction")
                    .and_then(|v| v.as_str())
                    .ok_or_else(|| {
                        GameRLError::InvalidAction(
                            "Move requires Direction (N, NE, E, SE, S, SW, W, NW)".into(),
                        )
                    })?;
                let (dx, dy) = world::direction_vec(dir).ok_or_else(|| {
                    GameRLError::InvalidAction(format!(
                        "Unknown direction \"{dir}\". Valid: N, NE, E, SE, S, SW, W, NW"
                    ))
                })?;
                let colonist_id = params
                    .get("ColonistId")
                    .and_then(|v| v.as_str())
                    .map(str::to_string);
                let colonist = match &colonist_id {
                    Some(id) => self
                        .world
                        .colonists
                        .iter_mut()
                        .find(|c| c.alive && c.id == *id)
                        .ok_or_else(|| {
                            GameRLError::InvalidAction(format!(
                                "Move failed: no living colonist \"{id}\""
                            ))
                        })?,
                    None => self
                        .world
                        .colonists
                        .iter_mut()
                        .find(|c| c.alive)
                        .ok_or_else(|| {
                            GameRLError::InvalidAction("Move failed: no living colonists".into())
                        })?,
                };
                colonist.pos = clamp_pos(GridPos {
                    x: colonist.pos.x + dx,
                    y: colonist.pos.y + dy,
                });
                Ok((
                    format!(
                        "{} moved {dir} to ({},{})",
                        colonist.id, colonist.pos.x, colonist.pos.y
                    ),
                    vec![],
                ))
            }

            "Harvest" => {
                let target = params
                    .get("Target")
                    .and_then(|v| v.as_str())
                    .unwrap_or("Berries");
                let origin = self
                    .world
                    .colonists
                    .iter()
                    .find(|c| c.alive)
                    .map(|c| c.pos)
                    .ok_or_else(|| {
                        GameRLError::InvalidAction("Harvest failed: no living colonists".into())
                    })?;
                let (cell, desc, rewards): (Option<GridPos>, &str, Vec<(&'static str, f64)>) =
                    match target.to_ascii_lowercase().as_str() {
                        "wood" | "tree" | "trees" => (
                            self.nearest_cell(origin, |i| self.world.trees[i]),
                            "tree (+3 wood)",
                            vec![],
                        ),
                        "stone" | "rock" | "rocks" => (
                            self.nearest_cell(origin, |i| self.world.rocks[i]),
                            "rock (+5 stone)",
                            vec![],
                        ),
                        "berries" | "berry" | "food" => (
                            self.nearest_cell(origin, |i| self.world.berries[i]),
                            "berry bush (+5 food)",
                            vec![("food_production", 0.5)],
                        ),
                        other => {
                            return Err(GameRLError::InvalidAction(format!(
                                "Harvest: unknown target \"{other}\". Valid: Wood, Stone, Berries"
                            )));
                        }
                    };
                let cell = cell.ok_or_else(|| {
                    GameRLError::InvalidAction(format!(
                        "Harvest failed: no {target} left on the map"
                    ))
                })?;
                let i = idx(cell);
                if self.world.trees[i] {
                    self.world.trees[i] = false;
                    self.world.wood += 3.0;
                } else if self.world.rocks[i] {
                    self.world.rocks[i] = false;
                    self.world.stone += 5.0;
                } else if self.world.berries[i] {
                    self.world.berries[i] = false;
                    self.world.food += 5.0;
                }
                if let Some(c) = self.world.colonists.iter_mut().find(|c| c.alive) {
                    c.pos = cell;
                }
                Ok((
                    format!("Harvested {desc} at ({},{})", cell.x, cell.y),
                    rewards,
                ))
            }

            "DefendColony" => {
                if self.world.hostiles.is_empty() {
                    return Err(GameRLError::InvalidAction(
                        "DefendColony: no hostiles present".into(),
                    ));
                }
                let cc = self.world.colony_center();
                let before = self.world.hostiles.len();
                self.world.hostiles.retain(|&h| dist2(h, cc) > 15 * 15);
                let repelled = before - self.world.hostiles.len();
                if repelled == 0 {
                    return Err(GameRLError::InvalidAction(
                        "DefendColony: hostiles are not within defensive range (15) yet".into(),
                    ));
                }
                Ok((
                    format!("Repelled {repelled} hostile(s)"),
                    vec![("threat_defense", 10.0)],
                ))
            }

            other => Err(GameRLError::InvalidAction(format!(
                "Unknown action \"{other}\". Valid actions: Wait, Move, Harvest, DefendColony, PlaceBuildingNear, EstablishFarm, EstablishStorage, DesignateMiningNear, DesignateClearNear"
            ))),
        }
    }

    // ----- simulation -----

    fn advance(&mut self, ticks: u32) -> (f64, Vec<GameEvent>) {
        let mut events = Vec::new();
        if ticks == 0 {
            return (0.0, events);
        }
        let t = ticks as f64;

        // Farms produce food proportional to total fertility of farmed cells
        let farm_fertility: f64 = self
            .world
            .zones
            .iter()
            .filter(|z| z.kind == ZoneKind::Farm)
            .map(|z| z.mean_fertility * z.cells.len() as f64)
            .sum();
        let produced = farm_fertility * 0.0005 * t;
        self.world.food += produced;

        // Colonists eat or starve
        let alive = self.world.colonists.iter().filter(|c| c.alive).count() as f64;
        let needed = alive * world::FOOD_PER_TICK * t;
        if self.world.food >= needed {
            self.world.food -= needed;
            for c in self.world.colonists.iter_mut().filter(|c| c.alive) {
                c.hunger = (c.hunger - t).max(0.0);
            }
        } else {
            self.world.food = 0.0;
            for c in self.world.colonists.iter_mut().filter(|c| c.alive) {
                c.hunger += t;
                if c.hunger > world::STARVATION_TICKS {
                    c.alive = false;
                    events.push(event(
                        "ColonistDied",
                        self.world.tick,
                        3,
                        &c.id,
                        "starvation",
                    ));
                }
            }
        }

        // Hostiles advance on the colony: one cell per 30 ticks
        if !self.world.hostiles.is_empty() {
            let cc = self.world.colony_center();
            let moves = (ticks / 30).max(if ticks > 0 { 1 } else { 0 });
            for _ in 0..moves {
                for h in self.world.hostiles.iter_mut() {
                    let dx = (cc.x - h.x).signum();
                    let dy = (cc.y - h.y).signum();
                    h.x += dx;
                    h.y += dy;
                }
            }
            let hostiles = self.world.hostiles.clone();
            for c in self.world.colonists.iter_mut().filter(|c| c.alive) {
                if hostiles.iter().any(|&h| dist2(h, c.pos) <= 4) {
                    c.alive = false;
                    events.push(event("ColonistDied", self.world.tick, 3, &c.id, "hostiles"));
                }
            }
        }

        self.world.tick += ticks as u64;

        // Terminal conditions
        if self.world.colonists.iter().all(|c| !c.alive) {
            self.done = true;
            self.termination_reason = Some(TerminationReason::Failure);
            events.push(event(
                "ColonyLost",
                self.world.tick,
                3,
                "colony",
                "all colonists dead",
            ));
        } else if self.world.tick >= MAX_EPISODE_TICKS {
            self.truncated = true;
            self.termination_reason = Some(TerminationReason::Timeout);
        }

        (produced, events)
    }

    // ----- observation -----

    fn alerts(&self) -> Vec<Value> {
        let mut alerts = Vec::new();
        if self.world.food < 1.5 {
            alerts.push(json!({"Severity": 3, "Label": "Starvation imminent",
                "Detail": format!("{:.1} food left", self.world.food)}));
        } else if self.world.food < 5.0 {
            alerts.push(json!({"Severity": 2, "Label": "Low food",
                "Detail": format!("{:.1} food left", self.world.food)}));
        }
        let cc = self.world.colony_center();
        if self.world.hostiles.iter().any(|&h| dist2(h, cc) <= 20 * 20) {
            alerts.push(json!({"Severity": 3, "Label": "UnderAttack",
                "Detail": format!("{} hostiles approaching", self.world.hostiles.len())}));
        } else if !self.world.hostiles.is_empty() {
            alerts.push(json!({"Severity": 1, "Label": "Hostiles sighted",
                "Detail": format!("{} hostiles on the map", self.world.hostiles.len())}));
        }
        if !self.world.zones.iter().any(|z| z.kind == ZoneKind::Farm) {
            alerts.push(json!({"Severity": 1, "Label": "No farm established"}));
        }
        alerts
    }

    fn valid_actions(&self) -> Vec<Value> {
        let mut actions = vec![
            json!({"Type": "Wait", "Params": {}}),
            json!({"Type": "Move", "Params": {"ColonistId": self.world.colonists.iter().filter(|c| c.alive).map(|c| c.id.clone()).collect::<Vec<_>>(), "Direction": ["N","NE","E","SE","S","SW","W","NW"]}}),
            json!({"Type": "PlaceBuildingNear", "Params": {"Building": ["Shelter", "Bed"], "Near": "anchor"}}),
            json!({"Type": "EstablishFarm", "Params": {"Near": "anchor", "Crop": ["Rice", "Potatoes"], "Size": "int"}}),
            json!({"Type": "EstablishStorage", "Params": {"Near": "anchor"}}),
        ];
        let mut harvest = Vec::new();
        if self.world.trees.iter().any(|&t| t) {
            harvest.push("Wood");
        }
        if self.world.rocks.iter().any(|&r| r) {
            harvest.push("Stone");
        }
        if self.world.berries.iter().any(|&b| b) {
            harvest.push("Berries");
        }
        if !harvest.is_empty() {
            actions.push(json!({"Type": "Harvest", "Params": {"Target": harvest}}));
        }
        if self.world.rocks.iter().any(|&r| r) {
            actions.push(json!({"Type": "DesignateMiningNear", "Params": {"Near": "anchor"}}));
        }
        if self.world.trees.iter().any(|&t| t) {
            actions.push(json!({"Type": "DesignateClearNear", "Params": {"Near": "anchor"}}));
        }
        // Context-sensitive: only while hostiles are present (C-VALIDACTIONS probe)
        if !self.world.hostiles.is_empty() {
            actions.push(json!({"Type": "DefendColony", "Params": {}}));
        }
        actions
    }

    /// Landmarks per REQ-SPA-04: centroids + 8 compass regions + clusters + zones/buildings
    fn landmarks(&self) -> Vec<Value> {
        let mut out = Vec::new();
        let cc = self.world.colony_center();
        out.push(json!({"Id": "ColonyCenter", "Kind": "Centroid", "Position": cc}));
        out.push(json!({"Id": "MapCenter", "Kind": "Centroid",
            "Position": GridPos { x: GRID / 2, y: GRID / 2 }}));
        for (name, pos) in world::region_centroids() {
            out.push(json!({"Id": name, "Kind": "Region", "Position": pos}));
        }
        let cluster_json = |c: &Cluster, fert: bool| {
            let mut v = json!({"Id": c.id, "Kind": "Cluster", "Position": c.centroid,
                "CellCount": c.cell_count});
            if fert {
                v["Fertility"] = json!((c.mean_fertility * 100.0).round() / 100.0);
            }
            v
        };
        for c in self.world.fertile_clusters().iter().take(5) {
            out.push(cluster_json(c, true));
        }
        for c in self.world.rock_clusters().iter().take(3) {
            out.push(cluster_json(c, false));
        }
        for c in self.world.tree_clusters().iter().take(3) {
            out.push(cluster_json(c, false));
        }
        for z in &self.world.zones {
            out.push(json!({"Id": z.id, "Kind": "Zone",
                "Position": world::centroid(&z.cells), "CellCount": z.cells.len()}));
        }
        for b in &self.world.buildings {
            out.push(json!({"Id": b.id, "Kind": "Building", "Position": b.pos}));
        }
        out
    }

    fn observation(&self) -> Observation {
        let mut map = HashMap::new();
        map.insert("Tick".to_string(), json!(self.world.tick));
        map.insert(
            "ColonistCount".to_string(),
            json!(self.world.colonists.iter().filter(|c| c.alive).count()),
        );
        map.insert(
            "Colonists".to_string(),
            json!(
                self.world
                    .colonists
                    .iter()
                    .map(|c| json!({"Id": c.id, "Position": c.pos, "Alive": c.alive,
                        "Hunger": (c.hunger / world::STARVATION_TICKS * 100.0).round()}))
                    .collect::<Vec<_>>()
            ),
        );
        map.insert(
            "Resources".to_string(),
            json!({
                "Food": (self.world.food * 100.0).round() / 100.0,
                "Wood": self.world.wood,
                "Stone": self.world.stone
            }),
        );
        map.insert(
            "Terrain".to_string(),
            json!({"FertileRegions": self.world.fertile_clusters().iter().take(20)
                .map(|c| json!({"Id": c.id, "Position": c.centroid,
                    "CellCount": c.cell_count,
                    "Fertility": (c.mean_fertility * 100.0).round() / 100.0}))
                .collect::<Vec<_>>()}),
        );
        map.insert(
            "Entities".to_string(),
            json!({
                "Trees": {"Count": self.world.trees.iter().filter(|&&t| t).count()},
                "Rocks": {"Count": self.world.rocks.iter().filter(|&&r| r).count()},
                "Berries": {"Count": self.world.berries.iter().filter(|&&b| b).count()},
                "Buildings": self.world.buildings.iter()
                    .map(|b| json!({"Id": b.id, "Kind": b.kind, "Position": b.pos}))
                    .collect::<Vec<_>>(),
                "Hostiles": self.world.hostiles.iter()
                    .map(|h| json!({"Position": h}))
                    .collect::<Vec<_>>()
            }),
        );
        map.insert(
            "Zones".to_string(),
            json!(
                self.world
                    .zones
                    .iter()
                    .map(|z| json!({"Id": z.id, "Kind": z.kind.label(),
                        "CellCount": z.cells.len(), "Crop": z.crop,
                        "MeanFertility": (z.mean_fertility * 100.0).round() / 100.0}))
                    .collect::<Vec<_>>()
            ),
        );
        map.insert("Alerts".to_string(), json!(self.alerts()));
        map.insert("ValidActions".to_string(), json!(self.valid_actions()));
        map.insert("Landmarks".to_string(), json!(self.landmarks()));
        map.insert(
            "Map".to_string(),
            json!({"Width": GRID, "Height": GRID, "Scenario": self.world.scenario}),
        );
        Observation::Structured(map)
    }

    fn step_result(
        &self,
        agent_id: &str,
        reward: f64,
        components: HashMap<String, f64>,
        events: Vec<GameEvent>,
    ) -> StepResult {
        StepResult {
            agent_id: agent_id.to_string(),
            step_id: self.step_id,
            tick: self.world.tick,
            observation: self.observation(),
            reward,
            reward_components: components,
            done: self.done,
            truncated: self.truncated,
            termination_reason: self.termination_reason.clone(),
            events,
            frame_ids: HashMap::new(),
            available_actions: Some(self.valid_actions()),
            metrics: None,
            state_hash: Some(self.compute_hash()),
        }
    }

    fn compute_hash(&self) -> String {
        let mut h = Sha256::new();
        h.update(self.world.scenario.as_bytes());
        h.update(self.world.seed.to_le_bytes());
        h.update(self.world.tick.to_le_bytes());
        h.update(self.step_id.to_le_bytes());
        h.update(((self.world.food * 1000.0) as i64).to_le_bytes());
        h.update(((self.world.wood * 1000.0) as i64).to_le_bytes());
        h.update(((self.world.stone * 1000.0) as i64).to_le_bytes());
        for f in &self.world.fertility {
            h.update(f.to_le_bytes());
        }
        for grid in [&self.world.trees, &self.world.rocks, &self.world.berries] {
            let bytes: Vec<u8> = grid.iter().map(|&b| b as u8).collect();
            h.update(&bytes);
        }
        for c in &self.world.colonists {
            h.update(c.id.as_bytes());
            h.update(c.pos.x.to_le_bytes());
            h.update(c.pos.y.to_le_bytes());
            h.update([c.alive as u8]);
            h.update((c.hunger as i64).to_le_bytes());
        }
        for b in &self.world.buildings {
            h.update(b.id.as_bytes());
            h.update(b.pos.x.to_le_bytes());
            h.update(b.pos.y.to_le_bytes());
        }
        for z in &self.world.zones {
            h.update(z.id.as_bytes());
            for p in &z.cells {
                h.update(p.x.to_le_bytes());
                h.update(p.y.to_le_bytes());
            }
        }
        for p in &self.world.hostiles {
            h.update(p.x.to_le_bytes());
            h.update(p.y.to_le_bytes());
        }
        format!("sha256:{}", hex::encode(h.finalize()))
    }
}

fn reward_def(
    name: &str,
    description: &str,
    range: [f64; 2],
) -> game_rl_core::reward::RewardComponentDef {
    game_rl_core::reward::RewardComponentDef {
        name: name.to_string(),
        description: Some(description.to_string()),
        range: Some(range),
        default_weight: 1.0,
    }
}

fn scenario_description(name: &str) -> &'static str {
    match name {
        "fertile-corner" => {
            "All fertile soil is in the NE corner, far from the map center — a behavioral probe: center-biased spatial policies score zero farm fertility"
        }
        "threat-south" => "A hostile camp approaches from the south; defend the colony",
        "scattered-resources" => "Resources spread across all compass regions",
        _ => "Balanced starter colony",
    }
}

fn event(event_type: &str, tick: u64, severity: u8, subject: &str, detail: &str) -> GameEvent {
    GameEvent {
        event_type: event_type.to_string(),
        tick,
        severity,
        details: json!({"Subject": subject, "Detail": detail}),
    }
}

fn placement(
    description: String,
    cells: &[GridPos],
    requested: &str,
    resolved: &str,
    anchor_pos: GridPos,
    fallback: bool,
) -> ResolvedPlacement {
    ResolvedPlacement {
        description,
        count: cells.len() as u32,
        anchor_requested: Some(requested.to_string()),
        anchor_resolved: resolved.to_string(),
        anchor_position: anchor_pos,
        positions: cells.to_vec(),
        fallback_applied: fallback,
    }
}

fn fmt_cells(cells: &[GridPos]) -> String {
    cells
        .iter()
        .take(4)
        .map(|p| format!("({},{})", p.x, p.y))
        .collect::<Vec<_>>()
        .join(", ")
}

#[async_trait]
impl GameEnvironment for ReferenceEnv {
    async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        _config: AgentConfig,
    ) -> Result<AgentManifest> {
        self.agents.insert(agent_id.clone(), agent_type.clone());
        let manifest = Self::build_manifest();
        Ok(AgentManifest {
            agent_id,
            agent_type,
            observation_space: manifest.default_observation_space.unwrap_or(Value::Null),
            action_space: serde_json::to_value(manifest.default_action_space)?,
            reward_components: manifest
                .reward_components
                .iter()
                .map(|c| c.name.clone())
                .collect(),
        })
    }

    async fn deregister_agent(&mut self, agent_id: &AgentId) -> Result<()> {
        self.agents.remove(agent_id);
        Ok(())
    }

    async fn step(&mut self, agent_id: &AgentId, action: Action, ticks: u32) -> Result<StepResult> {
        // Read-only full-state request used by the server's observe drilldown
        if let Action::Parameterized { action_type, .. } = &action {
            if action_type == "RequestFullState" {
                return Ok(self.step_result(agent_id, 0.0, HashMap::new(), vec![]));
            }
        }

        if !self.agents.contains_key(agent_id) {
            return Err(GameRLError::AgentNotRegistered(agent_id.clone()));
        }
        if self.done || self.truncated {
            return Err(GameRLError::EpisodeTerminated);
        }

        let action_json = serde_json::to_value(&action)?;

        // Execute the action
        let (feedback, mut one_time): (String, Vec<(&'static str, f64)>) = match &action {
            Action::Wait => ("Waited".to_string(), vec![]),
            Action::Discrete(0) => ("Waited".to_string(), vec![]),
            Action::Discrete(n) => {
                return Err(GameRLError::InvalidAction(format!(
                    "Discrete action {n} out of range; this environment is parameterized — use {{\"Type\": \"Wait\"}}"
                )));
            }
            Action::Continuous(_) => {
                return Err(GameRLError::InvalidAction(
                    "Continuous actions not supported; use parameterized actions".into(),
                ));
            }
            Action::Parameterized {
                action_type,
                params,
            } => {
                if SpatialIntent::is_spatial_action(action_type) && action_type != "DefendColony" {
                    let intent: SpatialIntent = serde_json::from_value(action_json.clone())
                        .map_err(|e| {
                            GameRLError::InvalidAction(format!(
                                "Malformed spatial intent {action_type}: {e}"
                            ))
                        })?;
                    let plan = self.plan_spatial(&intent)?;
                    self.apply_spatial(plan)
                } else {
                    match self.apply_action(action_type, params) {
                        Ok(v) => v,
                        // Conformance negative-test hook: simulate the
                        // log-warning-and-return-success bug class that
                        // REQ-ERR-01 exists to forbid.
                        Err(_) if self.break_mode.as_deref() == Some("silent_errors") => {
                            (format!("{action_type} ok"), vec![])
                        }
                        Err(e) => return Err(e),
                    }
                }
            }
        };

        // Advance the simulation
        let (food_produced, events) = self.advance(ticks);

        // Rewards (REQ-RWD-01: synchronous, decomposed)
        let alive = self.world.colonists.iter().filter(|c| c.alive).count() as f64;
        let mut components: HashMap<String, f64> = HashMap::new();
        components.insert("survival".into(), 0.001 * ticks as f64 * alive);
        if food_produced > 0.0 {
            *components.entry("food_production".into()).or_default() += food_produced * 0.1;
        }
        for (name, value) in one_time.drain(..) {
            *components.entry(name.to_string()).or_default() += value;
        }
        let reward: f64 = components.values().sum();

        self.step_id += 1;
        self.cum_reward += reward;
        for (k, v) in &components {
            *self.reward_breakdown.entry(k.clone()).or_default() += v;
        }
        self.trajectory.push(TrajectoryStep {
            agent_id: agent_id.clone(),
            action: action_json,
            ticks,
        });

        let mut result = self.step_result(agent_id, reward, components, events);
        // Feedback rides in the observation for LLM agents (draft-02 §5.2)
        if let Observation::Structured(ref mut map) = result.observation {
            map.insert("Feedback".to_string(), json!(feedback));
        }
        Ok(result)
    }

    async fn reset(&mut self, seed: Option<u64>, scenario: Option<String>) -> Result<Observation> {
        let seed = seed.unwrap_or(self.world.seed);
        let scenario = scenario.unwrap_or_else(|| self.world.scenario.clone());
        if !SCENARIOS.contains(&scenario.trim_end_matches("+dr"))
            && !scenario.trim_end_matches("+dr").is_empty()
        {
            return Err(GameRLError::InvalidAction(format!(
                "Unknown scenario \"{scenario}\". Valid: {}",
                SCENARIOS.join(", ")
            )));
        }
        self.world = World::generate(seed, &scenario);
        self.step_id = 0;
        self.cum_reward = 0.0;
        self.reward_breakdown.clear();
        self.done = false;
        self.truncated = false;
        self.termination_reason = None;
        self.trajectory.clear();
        Ok(self.observation())
    }

    async fn observe(&mut self) -> Result<StepResult> {
        Ok(self.step_result("observer", 0.0, HashMap::new(), vec![]))
    }

    async fn state_hash(&mut self) -> Result<String> {
        Ok(self.compute_hash())
    }

    async fn configure_streams(
        &mut self,
        _agent_id: &AgentId,
        profile: &str,
    ) -> Result<Vec<StreamDescriptor>> {
        if profile == "none" {
            Ok(vec![])
        } else {
            Err(GameRLError::StreamError(format!(
                "Unknown stream profile \"{profile}\". Available: none (GridColony is headless-only)"
            )))
        }
    }

    async fn save_trajectory(&self, path: &str) -> Result<()> {
        let file = TrajectoryFile {
            seed: self.world.seed,
            scenario: self.world.scenario.clone(),
            steps: self
                .trajectory
                .iter()
                .map(|s| TrajectoryStep {
                    agent_id: s.agent_id.clone(),
                    action: s.action.clone(),
                    ticks: s.ticks,
                })
                .collect(),
        };
        let json = serde_json::to_string_pretty(&file)?;
        std::fs::write(path, json).map_err(|e| GameRLError::GameError(e.to_string()))
    }

    async fn load_trajectory(&mut self, path: &str) -> Result<()> {
        let content =
            std::fs::read_to_string(path).map_err(|e| GameRLError::GameError(e.to_string()))?;
        let file: TrajectoryFile = serde_json::from_str(&content)?;
        let agents: Vec<String> = file.steps.iter().map(|s| s.agent_id.clone()).collect();
        self.reset(Some(file.seed), Some(file.scenario)).await?;
        for agent in agents.iter().collect::<std::collections::BTreeSet<_>>() {
            self.agents
                .entry((*agent).clone())
                .or_insert(AgentType::Controller);
        }
        for step in file.steps {
            let action: Action = serde_json::from_value(step.action)?;
            self.step(&step.agent_id, action, step.ticks).await?;
        }
        Ok(())
    }

    async fn episode_summary(&mut self) -> Result<EpisodeSummary> {
        Ok(EpisodeSummary {
            total_reward: self.cum_reward,
            step_count: self.step_id,
            ticks_elapsed: self.world.tick,
            reward_breakdown: self.reward_breakdown.clone(),
            termination_reason: self.termination_reason.clone(),
        })
    }

    /// DRY RUN per draft-02 REQ-SPA-05 — never mutates
    async fn resolve_spatial(&mut self, intent: SpatialIntent) -> Result<ResolvedPlacement> {
        Ok(self.plan_spatial(&intent)?.placement)
    }

    async fn shutdown(&mut self) -> Result<()> {
        Ok(())
    }

    fn manifest(&self) -> GameManifest {
        Self::build_manifest()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn registered(seed: u64, scenario: &str) -> ReferenceEnv {
        let mut env = ReferenceEnv::new(seed, scenario);
        env.agents.insert("p1".to_string(), AgentType::Controller);
        env
    }

    fn farm_action(near: &str, allow_fallback: bool) -> Action {
        serde_json::from_value(json!({
            "Type": "EstablishFarm", "Near": near, "AllowFallback": allow_fallback
        }))
        .unwrap()
    }

    #[tokio::test]
    async fn test_seeded_determinism_over_sequence() {
        let mut hashes = Vec::new();
        for _ in 0..2 {
            let mut env = registered(42, "default");
            let mut run = Vec::new();
            for action in [
                json!({"Type": "Wait"}),
                json!({"Type": "Harvest", "Target": "Berries"}),
                json!({"Type": "EstablishFarm", "Near": "FertileCluster_0"}),
                json!({"Type": "Wait"}),
            ] {
                let a: Action = serde_json::from_value(action).unwrap();
                let r = env.step(&"p1".to_string(), a, 60).await.unwrap();
                run.push(r.state_hash.unwrap());
            }
            hashes.push(run);
        }
        assert_eq!(hashes[0], hashes[1], "REQ-DET-02 violated");
    }

    #[tokio::test]
    async fn test_farm_on_fertile_cluster_succeeds() {
        let mut env = registered(7, "fertile-corner");
        let r = env
            .step(&"p1".to_string(), farm_action("FertileCluster_0", false), 0)
            .await
            .unwrap();
        let quality = r.reward_components.get("farm_fertility_quality").unwrap();
        assert!(
            *quality >= 1.0,
            "farm on the fertile cluster should score high fertility, got {quality}"
        );
    }

    #[tokio::test]
    async fn test_farm_at_center_fails_loudly_on_probe_scenario() {
        // REQ-SPA-02: no silent relocation. The center has no fertile soil.
        let mut env = registered(7, "fertile-corner");
        let err = env
            .step(&"p1".to_string(), farm_action("MapCenter", false), 0)
            .await
            .unwrap_err();
        let msg = err.to_string();
        assert!(
            msg.contains("FertileCluster"),
            "error must offer alternatives, got: {msg}"
        );
        assert!(
            msg.contains("AllowFallback"),
            "error must mention the opt-in"
        );
    }

    #[tokio::test]
    async fn test_farm_fallback_is_audited() {
        let mut env = registered(7, "fertile-corner");
        let intent: SpatialIntent = serde_json::from_value(json!({
            "Type": "EstablishFarm", "Near": "MapCenter", "AllowFallback": true
        }))
        .unwrap();
        let plan = env.plan_spatial(&intent).unwrap();
        assert!(
            plan.placement.fallback_applied,
            "REQ-SPA-06: fallback must be flagged"
        );
        assert_eq!(
            plan.placement.anchor_requested.as_deref(),
            Some("MapCenter")
        );
        assert!(plan.placement.anchor_resolved.starts_with("FertileCluster"));
    }

    #[tokio::test]
    async fn test_resolve_spatial_is_dry_run() {
        let mut env = registered(7, "default");
        let intent: SpatialIntent = serde_json::from_value(json!({
            "Type": "EstablishFarm", "Near": "FertileCluster_0"
        }))
        .unwrap();
        let before = env.compute_hash();
        env.resolve_spatial(intent).await.unwrap();
        assert_eq!(
            env.compute_hash(),
            before,
            "REQ-SPA-05: dry run must not mutate"
        );
        assert!(env.world.zones.is_empty());
    }

    #[tokio::test]
    async fn test_offset_and_coordinate_anchors() {
        let mut env = registered(7, "default");
        let offset: SpatialIntent = serde_json::from_value(json!({
            "Type": "EstablishStorage",
            "Near": {"Anchor": "ColonyCenter", "Direction": "NE", "Distance": 10}
        }))
        .unwrap();
        let p = env.plan_spatial(&offset).unwrap().placement;
        let cc = env.world.colony_center();
        assert!(p.anchor_position.x > cc.x && p.anchor_position.y > cc.y);

        let coord: SpatialIntent = serde_json::from_value(json!({
            "Type": "EstablishStorage", "Near": {"X": 10, "Y": 50}
        }))
        .unwrap();
        let p = env.plan_spatial(&coord).unwrap().placement;
        assert_eq!((p.anchor_position.x, p.anchor_position.y), (10, 50));
    }

    #[tokio::test]
    async fn test_unknown_action_fails_loudly() {
        let mut env = registered(7, "default");
        let a: Action = serde_json::from_value(json!({"Type": "FlyToMoon"})).unwrap();
        let err = env.step(&"p1".to_string(), a, 1).await.unwrap_err();
        assert!(err.to_string().contains("Valid actions"));
    }

    #[tokio::test]
    async fn test_landmarks_minimum_set() {
        let env = registered(7, "default");
        let landmarks = env.landmarks();
        for required in [
            "ColonyCenter",
            "MapCenter",
            "Region_N",
            "Region_NE",
            "Region_SW",
        ] {
            assert!(
                landmarks.iter().any(|l| l["Id"] == required),
                "REQ-SPA-04: missing landmark {required}"
            );
        }
    }

    #[tokio::test]
    async fn test_defend_colony_context_sensitivity() {
        let mut env = registered(7, "threat-south");
        assert!(
            env.valid_actions()
                .iter()
                .any(|a| a["Type"] == "DefendColony"),
            "DefendColony must be valid while hostiles exist"
        );
        let mut calm = registered(7, "default");
        assert!(
            !calm
                .valid_actions()
                .iter()
                .any(|a| a["Type"] == "DefendColony"),
            "DefendColony must be hidden without hostiles"
        );
        let a: Action = serde_json::from_value(json!({"Type": "DefendColony"})).unwrap();
        let err = calm.step(&"p1".to_string(), a, 1).await.unwrap_err();
        assert!(err.to_string().contains("no hostiles"));
    }

    #[tokio::test]
    async fn test_episode_summary_accumulates() {
        let mut env = registered(7, "default");
        for _ in 0..3 {
            let a: Action = serde_json::from_value(json!({"Type": "Wait"})).unwrap();
            env.step(&"p1".to_string(), a, 60).await.unwrap();
        }
        let summary = env.episode_summary().await.unwrap();
        assert_eq!(summary.step_count, 3);
        assert!(summary.total_reward > 0.0);
        assert!(summary.reward_breakdown.contains_key("survival"));
    }

    #[tokio::test]
    async fn test_trajectory_round_trip() {
        let dir = std::env::temp_dir().join("gamerl-ref-test-traj.json");
        let path = dir.to_string_lossy().to_string();
        let mut env = registered(42, "default");
        for action in [
            json!({"Type": "Harvest", "Target": "Berries"}),
            json!({"Type": "EstablishFarm", "Near": "FertileCluster_0"}),
        ] {
            let a: Action = serde_json::from_value(action).unwrap();
            env.step(&"p1".to_string(), a, 30).await.unwrap();
        }
        let hash = env.compute_hash();
        env.save_trajectory(&path).await.unwrap();

        let mut replay = registered(42, "default");
        replay.load_trajectory(&path).await.unwrap();
        assert_eq!(replay.compute_hash(), hash, "replay must reproduce state");
        let _ = std::fs::remove_file(&path);
    }
}
