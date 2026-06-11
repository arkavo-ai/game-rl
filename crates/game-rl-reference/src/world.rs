//! GridColony world state and scenario generation
//!
//! A deterministic colony-sim on an NxN grid. Scenarios are designed as
//! behavioral probes: `fertile-corner` places all fertile soil far from the
//! map center so center-biased spatial policies measurably fail.

use crate::rng::SplitMix64;
use game_rl_core::GridPos;

pub const GRID: i32 = 64;
pub const MAX_EPISODE_TICKS: u64 = 36_000;
/// Fertility at or above this supports farming
pub const FARM_FERTILITY_MIN: f32 = 0.7;
/// Food consumed per colonist per tick
pub const FOOD_PER_TICK: f64 = 0.002;
/// Ticks of starvation before a colonist dies
pub const STARVATION_TICKS: f64 = 1_800.0;

pub const SCENARIOS: &[&str] = &[
    "default",
    "fertile-corner",
    "threat-south",
    "scattered-resources",
];

#[derive(Debug, Clone)]
pub struct Colonist {
    pub id: String,
    pub pos: GridPos,
    pub alive: bool,
    pub hunger: f64,
}

#[derive(Debug, Clone)]
pub struct Building {
    pub id: String,
    pub kind: String,
    pub pos: GridPos,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ZoneKind {
    Farm,
    Storage,
}

impl ZoneKind {
    pub fn label(&self) -> &'static str {
        match self {
            ZoneKind::Farm => "Farm",
            ZoneKind::Storage => "Storage",
        }
    }
}

#[derive(Debug, Clone)]
pub struct Zone {
    pub id: String,
    pub kind: ZoneKind,
    pub cells: Vec<GridPos>,
    pub crop: Option<String>,
    pub mean_fertility: f64,
}

/// A connected cluster of like cells, exposed as a landmark anchor
#[derive(Debug, Clone)]
pub struct Cluster {
    pub id: String,
    pub centroid: GridPos,
    pub cell_count: usize,
    pub mean_fertility: f64,
}

#[derive(Debug, Clone)]
pub struct World {
    pub seed: u64,
    pub scenario: String,
    pub tick: u64,
    pub fertility: Vec<f32>,
    pub trees: Vec<bool>,
    pub rocks: Vec<bool>,
    pub berries: Vec<bool>,
    pub colonists: Vec<Colonist>,
    pub buildings: Vec<Building>,
    pub zones: Vec<Zone>,
    pub hostiles: Vec<GridPos>,
    pub food: f64,
    pub wood: f64,
    pub stone: f64,
    next_building_id: u32,
    next_zone_id: u32,
}

pub fn idx(p: GridPos) -> usize {
    (p.y * GRID + p.x) as usize
}

pub fn in_bounds(p: GridPos) -> bool {
    p.x >= 0 && p.x < GRID && p.y >= 0 && p.y < GRID
}

pub fn dist2(a: GridPos, b: GridPos) -> i64 {
    let dx = (a.x - b.x) as i64;
    let dy = (a.y - b.y) as i64;
    dx * dx + dy * dy
}

pub fn clamp_pos(p: GridPos) -> GridPos {
    GridPos {
        x: p.x.clamp(0, GRID - 1),
        y: p.y.clamp(0, GRID - 1),
    }
}

/// 8-wind compass unit vectors; y increases north
pub fn direction_vec(dir: &str) -> Option<(i32, i32)> {
    match dir.to_ascii_uppercase().as_str() {
        "N" => Some((0, 1)),
        "NE" => Some((1, 1)),
        "E" => Some((1, 0)),
        "SE" => Some((1, -1)),
        "S" => Some((0, -1)),
        "SW" => Some((-1, -1)),
        "W" => Some((-1, 0)),
        "NW" => Some((-1, 1)),
        _ => None,
    }
}

/// Centroids of the eight compass regions (3x3 partition of the grid, center excluded)
pub fn region_centroids() -> Vec<(&'static str, GridPos)> {
    let lo = GRID / 6; // 10
    let mid = GRID / 2; // 32
    let hi = GRID - GRID / 6 - 1; // 53
    vec![
        ("Region_N", GridPos { x: mid, y: hi }),
        ("Region_NE", GridPos { x: hi, y: hi }),
        ("Region_E", GridPos { x: hi, y: mid }),
        ("Region_SE", GridPos { x: hi, y: lo }),
        ("Region_S", GridPos { x: mid, y: lo }),
        ("Region_SW", GridPos { x: lo, y: lo }),
        ("Region_W", GridPos { x: lo, y: mid }),
        ("Region_NW", GridPos { x: lo, y: hi }),
    ]
}

impl World {
    pub fn generate(seed: u64, scenario: &str) -> Self {
        // Optional domain randomization: "<scenario>+dr" perturbs fertility
        let (base_scenario, domain_randomize) = match scenario.strip_suffix("+dr") {
            Some(base) => (base, true),
            None => (scenario, false),
        };

        let mut rng = SplitMix64::new(seed ^ 0x47524944); // "GRID"
        let n = (GRID * GRID) as usize;
        let mut world = World {
            seed,
            scenario: scenario.to_string(),
            tick: 0,
            fertility: vec![0.3; n],
            trees: vec![false; n],
            rocks: vec![false; n],
            berries: vec![false; n],
            colonists: Vec::new(),
            buildings: Vec::new(),
            zones: Vec::new(),
            hostiles: Vec::new(),
            food: 10.0,
            wood: 0.0,
            stone: 0.0,
            next_building_id: 1,
            next_zone_id: 0,
        };

        let center = GridPos {
            x: GRID / 2,
            y: GRID / 2,
        };

        match base_scenario {
            "fertile-corner" => {
                // ALL fertile soil in the NE corner — the anti-center-bias probe.
                world.fertile_blob(GridPos { x: 52, y: 52 }, 6, 1.4);
                world.fertile_blob(GridPos { x: 46, y: 56 }, 4, 1.2);
                world.scatter_trees(&mut rng, 50, None);
                world.rock_blob(GridPos { x: 10, y: 50 }, 4);
                world.scatter_berries(&mut rng, 10, center, 8);
            }
            "threat-south" => {
                world.fertile_blob(GridPos { x: 26, y: 38 }, 5, 1.4);
                world.fertile_blob(GridPos { x: 40, y: 24 }, 4, 1.0);
                world.scatter_trees(&mut rng, 60, None);
                world.rock_blob(GridPos { x: 10, y: 50 }, 4);
                world.rock_blob(GridPos { x: 54, y: 8 }, 3);
                world.scatter_berries(&mut rng, 10, center, 8);
                for i in 0..5 {
                    world.hostiles.push(GridPos { x: 30 + i, y: 3 });
                }
            }
            "scattered-resources" => {
                world.fertile_blob(GridPos { x: 12, y: 32 }, 3, 1.2);
                world.fertile_blob(GridPos { x: 32, y: 12 }, 3, 1.2);
                world.scatter_trees(&mut rng, 80, None);
                for _ in 0..30 {
                    let p = GridPos {
                        x: rng.range(0, GRID),
                        y: rng.range(0, GRID),
                    };
                    world.rocks[idx(p)] = true;
                }
                world.scatter_berries(&mut rng, 16, center, 28);
            }
            _ => {
                // "default"
                world.fertile_blob(GridPos { x: 26, y: 38 }, 5, 1.4);
                world.fertile_blob(GridPos { x: 40, y: 24 }, 4, 1.0);
                world.fertile_blob(GridPos { x: 18, y: 18 }, 3, 1.4);
                world.scatter_trees(&mut rng, 60, None);
                world.rock_blob(GridPos { x: 10, y: 50 }, 4);
                world.rock_blob(GridPos { x: 54, y: 8 }, 3);
                world.scatter_berries(&mut rng, 10, center, 8);
            }
        }

        if domain_randomize {
            let mut dr_rng = SplitMix64::new(seed ^ 0x44524e44); // "DRND"
            for f in world.fertility.iter_mut() {
                // ±20% multiplicative noise
                let noise = 0.8 + 0.4 * dr_rng.unit_f32();
                *f *= noise;
            }
        }

        // Colonists spawn at the map center; their activity defines ColonyCenter.
        for (i, offset) in [(0, 0), (1, 0)].iter().enumerate() {
            world.colonists.push(Colonist {
                id: format!("Colonist_{}", i + 1),
                pos: GridPos {
                    x: center.x + offset.0,
                    y: center.y + offset.1,
                },
                alive: true,
                hunger: 0.0,
            });
        }

        world
    }

    fn fertile_blob(&mut self, c: GridPos, r: i32, peak: f32) {
        for y in (c.y - r).max(0)..=(c.y + r).min(GRID - 1) {
            for x in (c.x - r).max(0)..=(c.x + r).min(GRID - 1) {
                let p = GridPos { x, y };
                let d2 = dist2(p, c);
                if d2 <= (r * r) as i64 {
                    let inner = (r * r / 4).max(1) as i64;
                    let v = if d2 <= inner { peak } else { peak * 0.75 };
                    let cell = &mut self.fertility[idx(p)];
                    if v > *cell {
                        *cell = v;
                    }
                }
            }
        }
    }

    fn rock_blob(&mut self, c: GridPos, r: i32) {
        for y in (c.y - r).max(0)..=(c.y + r).min(GRID - 1) {
            for x in (c.x - r).max(0)..=(c.x + r).min(GRID - 1) {
                let p = GridPos { x, y };
                if dist2(p, c) <= (r * r) as i64 {
                    self.rocks[idx(p)] = true;
                }
            }
        }
    }

    fn scatter_trees(&mut self, rng: &mut SplitMix64, count: usize, _avoid: Option<GridPos>) {
        let mut placed = 0;
        while placed < count {
            let p = GridPos {
                x: rng.range(0, GRID),
                y: rng.range(0, GRID),
            };
            let i = idx(p);
            if !self.trees[i] && !self.rocks[i] {
                self.trees[i] = true;
                placed += 1;
            }
        }
    }

    fn scatter_berries(&mut self, rng: &mut SplitMix64, count: usize, near: GridPos, radius: i32) {
        let mut placed = 0;
        let mut attempts = 0;
        while placed < count && attempts < 10_000 {
            attempts += 1;
            let p = GridPos {
                x: near.x + rng.range(-radius, radius + 1),
                y: near.y + rng.range(-radius, radius + 1),
            };
            if !in_bounds(p) {
                continue;
            }
            let i = idx(p);
            if !self.trees[i] && !self.rocks[i] && !self.berries[i] {
                self.berries[i] = true;
                placed += 1;
            }
        }
    }

    /// The agent's activity centroid (REQ-SPA-03 Reference Point):
    /// centroid of living colonists and buildings, never the bare map center
    /// unless the colony actually lives there.
    pub fn colony_center(&self) -> GridPos {
        let mut sx: i64 = 0;
        let mut sy: i64 = 0;
        let mut n: i64 = 0;
        for c in self.colonists.iter().filter(|c| c.alive) {
            sx += c.pos.x as i64;
            sy += c.pos.y as i64;
            n += 1;
        }
        for b in &self.buildings {
            sx += b.pos.x as i64;
            sy += b.pos.y as i64;
            n += 1;
        }
        if n == 0 {
            return GridPos {
                x: GRID / 2,
                y: GRID / 2,
            };
        }
        GridPos {
            x: (sx / n) as i32,
            y: (sy / n) as i32,
        }
    }

    /// Cell is free for building/zoning
    pub fn is_free(&self, p: GridPos) -> bool {
        if !in_bounds(p) {
            return false;
        }
        let i = idx(p);
        !self.trees[i]
            && !self.rocks[i]
            && !self.berries[i]
            && !self.buildings.iter().any(|b| b.pos == p)
            && !self.zones.iter().any(|z| z.cells.contains(&p))
    }

    pub fn add_building(&mut self, kind: &str, pos: GridPos) -> String {
        let id = format!("{}_{}", kind, self.next_building_id);
        self.next_building_id += 1;
        self.buildings.push(Building {
            id: id.clone(),
            kind: kind.to_string(),
            pos,
        });
        id
    }

    pub fn add_zone(
        &mut self,
        kind: ZoneKind,
        cells: Vec<GridPos>,
        crop: Option<String>,
    ) -> String {
        let mean_fertility = if cells.is_empty() {
            0.0
        } else {
            cells
                .iter()
                .map(|&p| self.fertility[idx(p)] as f64)
                .sum::<f64>()
                / cells.len() as f64
        };
        let id = format!("{}_{}", kind.label(), self.next_zone_id);
        self.next_zone_id += 1;
        self.zones.push(Zone {
            id: id.clone(),
            kind,
            cells,
            crop,
            mean_fertility,
        });
        id
    }

    /// Connected clusters (4-neighbor) of cells matching `pred`, sorted by size
    /// descending then centroid order — deterministic landmark IDs.
    pub fn clusters(&self, prefix: &str, pred: impl Fn(usize) -> bool) -> Vec<Cluster> {
        let n = (GRID * GRID) as usize;
        let mut visited = vec![false; n];
        let mut raw: Vec<(Vec<GridPos>, f64)> = Vec::new();

        for y in 0..GRID {
            for x in 0..GRID {
                let start = GridPos { x, y };
                let si = idx(start);
                if visited[si] || !pred(si) {
                    continue;
                }
                let mut stack = vec![start];
                let mut cells = Vec::new();
                visited[si] = true;
                while let Some(p) = stack.pop() {
                    cells.push(p);
                    for (dx, dy) in [(0, 1), (1, 0), (0, -1), (-1, 0)] {
                        let q = GridPos {
                            x: p.x + dx,
                            y: p.y + dy,
                        };
                        if in_bounds(q) {
                            let qi = idx(q);
                            if !visited[qi] && pred(qi) {
                                visited[qi] = true;
                                stack.push(q);
                            }
                        }
                    }
                }
                let mean_fert = cells
                    .iter()
                    .map(|&p| self.fertility[idx(p)] as f64)
                    .sum::<f64>()
                    / cells.len() as f64;
                raw.push((cells, mean_fert));
            }
        }

        raw.sort_by(|a, b| {
            b.0.len().cmp(&a.0.len()).then_with(|| {
                let ca = centroid(&a.0);
                let cb = centroid(&b.0);
                (ca.y, ca.x).cmp(&(cb.y, cb.x))
            })
        });

        raw.into_iter()
            .enumerate()
            .map(|(i, (cells, mean_fertility))| Cluster {
                id: format!("{prefix}_{i}"),
                centroid: centroid(&cells),
                cell_count: cells.len(),
                mean_fertility,
            })
            .collect()
    }

    pub fn fertile_clusters(&self) -> Vec<Cluster> {
        self.clusters("FertileCluster", |i| self.fertility[i] >= 1.0)
    }

    pub fn rock_clusters(&self) -> Vec<Cluster> {
        self.clusters("RockCluster", |i| self.rocks[i])
    }

    pub fn tree_clusters(&self) -> Vec<Cluster> {
        self.clusters("TreeCluster", |i| self.trees[i])
    }
}

pub fn centroid(cells: &[GridPos]) -> GridPos {
    if cells.is_empty() {
        return GridPos { x: 0, y: 0 };
    }
    let sx: i64 = cells.iter().map(|p| p.x as i64).sum();
    let sy: i64 = cells.iter().map(|p| p.y as i64).sum();
    GridPos {
        x: (sx / cells.len() as i64) as i32,
        y: (sy / cells.len() as i64) as i32,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_generation_deterministic() {
        let a = World::generate(42, "default");
        let b = World::generate(42, "default");
        assert_eq!(a.fertility, b.fertility);
        assert_eq!(a.trees, b.trees);
        assert_eq!(a.berries, b.berries);
    }

    #[test]
    fn test_fertile_corner_probe_geometry() {
        // The probe guarantee: no farmable soil within radius 16 of map center.
        let w = World::generate(7, "fertile-corner");
        let center = GridPos {
            x: GRID / 2,
            y: GRID / 2,
        };
        for y in 0..GRID {
            for x in 0..GRID {
                let p = GridPos { x, y };
                if w.fertility[idx(p)] >= FARM_FERTILITY_MIN {
                    assert!(
                        dist2(p, center) > 16 * 16,
                        "fertile cell {p:?} too close to center"
                    );
                }
            }
        }
        assert!(
            !w.fertile_clusters().is_empty(),
            "probe needs at least one fertile cluster"
        );
    }

    #[test]
    fn test_domain_randomization_changes_fertility() {
        let plain = World::generate(42, "default");
        let dr = World::generate(42, "default+dr");
        assert_ne!(plain.fertility, dr.fertility);
        // But DR itself is deterministic
        let dr2 = World::generate(42, "default+dr");
        assert_eq!(dr.fertility, dr2.fertility);
    }

    #[test]
    fn test_region_centroids_compass() {
        let regions = region_centroids();
        assert_eq!(regions.len(), 8);
        let ne = regions.iter().find(|(n, _)| *n == "Region_NE").unwrap().1;
        assert!(ne.x > GRID / 2 && ne.y > GRID / 2);
    }
}
