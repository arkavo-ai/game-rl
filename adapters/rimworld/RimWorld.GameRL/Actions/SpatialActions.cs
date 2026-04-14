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
    [GameRLComponent]
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
        /// Resolve a size parameter from string. Accepts "Small"/"Medium"/"Large" or an integer.
        /// </summary>
        private static int ResolveSize(string sizeStr, int defaultSize)
        {
            if (string.IsNullOrEmpty(sizeStr))
                return defaultSize;

            // Named sizes
            switch (sizeStr.ToLowerInvariant())
            {
                case "tiny": case "xs": return 9;
                case "small": case "s": return 16;
                case "medium": case "m": case "med": return 25;
                case "large": case "l": case "big": return 36;
                case "huge": case "xl": return 49;
                default:
                    if (int.TryParse(sizeStr, out int parsed) && parsed > 0)
                        return parsed;
                    return defaultSize;
            }
        }

        /// <summary>
        /// Resolve a crop name to a RimWorld ThingDef.
        /// Accepts PascalCase names (Rice, Potato, Corn) and maps to RimWorld defNames (Plant_Rice, etc.).
        /// </summary>
        private static ThingDef ResolveCrop(string crop)
        {
            if (string.IsNullOrEmpty(crop))
                return DefDatabase<ThingDef>.GetNamed("Plant_Rice", errorOnFail: false)
                    ?? DefDatabase<ThingDef>.GetNamed("Plant_Potato", errorOnFail: false);

            // Try direct defName match first (e.g. "Plant_Rice")
            var direct = DefDatabase<ThingDef>.GetNamed(crop, errorOnFail: false);
            if (direct != null && direct.plant != null)
                return direct;

            // Normalize: strip "Plant" prefix if present, then look up as "Plant_<Name>"
            string normalized = crop;
            if (normalized.StartsWith("Plant", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(5).TrimStart('_');

            // Map common short names to RimWorld defNames
            string defName;
            switch (normalized.ToLowerInvariant())
            {
                case "rice": defName = "Plant_Rice"; break;
                case "potato": case "potatoes": defName = "Plant_Potato"; break;
                case "corn": defName = "Plant_Corn"; break;
                case "strawberry": case "strawberries": defName = "Plant_Strawberry"; break;
                case "healroot": defName = "Plant_Healroot"; break;
                case "cotton": defName = "Plant_Cotton"; break;
                case "devilstrand": defName = "Plant_Devilstrand"; break;
                case "haygrass": case "hay": defName = "Plant_Haygrass"; break;
                case "smokeleaf": defName = "Plant_Smokeleaf"; break;
                case "psychoid": defName = "Plant_Psychoid"; break;
                case "hops": defName = "Plant_Hops"; break;
                case "tinctoria": defName = "Plant_Tinctoria"; break;
                default:
                    // Try as "Plant_<input>" with original casing
                    defName = $"Plant_{normalized}";
                    break;
            }

            var resolved = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
            if (resolved != null)
                return resolved;

            throw new InvalidOperationException(
                $"EstablishFarm: Unknown crop '{crop}'. " +
                $"Examples: Rice, Potato, Corn, Healroot, Cotton, Haygrass, Strawberry");
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

            // Adaptive search: start at 15, expand to 40 if obstructed (mountains, buildings)
            float[] searchRadii = new[] { 15f, 25f, 40f };
            foreach (float searchRadius in searchRadii)
            {
                foreach (var cell in GenRadial.RadialCellsAround(anchor, searchRadius, true))
                {
                    if (placed.Count >= count) break;
                    if (!cell.InBounds(map)) continue;

                    // Try all 4 rotations
                    foreach (var rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
                    {
                        var report = GenConstruct.CanPlaceBlueprintAt(buildingDef, cell, rot, map, godMode: false, thing: null, stuffDef: stuffDef);
                        if (report.Accepted)
                        {
                            var thing = ThingMaker.MakeThing(buildingDef, stuffDef);
                            thing.SetFaction(Verse.Find.FactionManager.OfPlayer);
                            GenSpawn.Spawn(thing, cell, map, rot);
                            placed.Add($"({cell.x},{cell.z})");
                            break;
                        }
                    }
                }
                if (placed.Count >= count) break; // found enough, stop expanding
            }

            if (placed.Count == 0)
            {
                throw new InvalidOperationException(
                    $"PlaceBuildingNear: Could not find valid placement for {buildingDefName} near {near} " +
                    $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z}). Area may be mountainous or fully built up.");
            }

            var desc = $"Placed {placed.Count} {buildingDefName} near {near} (resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z}) at {string.Join(", ", placed)}";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)placed.Count, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("EstablishFarm", Description = "Create a growing zone on fertile soil near a landmark. Crop: Rice, Potato, Corn, Healroot, Cotton, Haygrass, Strawberry. Size: Small/Medium/Large.")]
        public static string EstablishFarm(
            [GameRLParam("Near")] string near,
            [GameRLParam("Crop")] string plantDefName = null,
            [GameRLParam("Size")] string sizeStr = null)
        {
            int size = ResolveSize(sizeStr, defaultSize: 25);

            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("EstablishFarm: No map loaded");

            var anchor = ResolveAnchor(near, map);
            var anchorInfo = DescribeAnchor(near, anchor, map);

            // Find fertile cells — anchor is a preference, not a hard constraint.
            // Strategy: search near anchor first, but if barren (mountains, rock),
            // find the best fertile region on the map closest to colony activity.
            var candidates = new List<IntVec3>();

            // Pass 1: search outward from anchor
            foreach (var cell in GenRadial.RadialCellsAround(anchor, 40f, true))
            {
                if (candidates.Count >= size * 2) break;
                if (!cell.InBounds(map)) continue;
                if (cell.GetFertility(map) <= 0) continue;
                if (map.zoneManager.ZoneAt(cell) != null) continue;
                if (cell.GetEdifice(map) != null) continue;
                candidates.Add(cell);
            }

            // Pass 2: if anchor area is barren, find best fertile region on map
            // Re-anchor to the densest fertile area closest to colony buildings
            if (candidates.Count < size)
            {
                candidates.Clear();
                // Find colony center (average of all colonist buildings, or map center)
                var colonyCenter = map.Center;
                var buildings = map.listerBuildings.allBuildingsColonist;
                if (buildings.Count > 0)
                {
                    int bx = (int)buildings.Average(b => b.Position.x);
                    int bz = (int)buildings.Average(b => b.Position.z);
                    colonyCenter = new IntVec3(bx, 0, bz);
                }

                // Scan all fertile cells, score by fertility and proximity to colony
                var allFertile = new List<(IntVec3 cell, float score)>();
                foreach (var cell in map.AllCells)
                {
                    float fertility = cell.GetFertility(map);
                    if (fertility <= 0) continue;
                    if (map.zoneManager.ZoneAt(cell) != null) continue;
                    if (cell.GetEdifice(map) != null) continue;
                    // Score: high fertility + close to colony = best
                    float dist = cell.DistanceTo(colonyCenter);
                    float score = fertility * 100f - dist;
                    allFertile.Add((cell, score));
                }

                candidates = allFertile
                    .OrderByDescending(x => x.score)
                    .Take(size)
                    .Select(x => x.cell)
                    .ToList();

                // Update anchor info to reflect where we actually placed
                if (candidates.Count > 0)
                {
                    int cx = (int)candidates.Average(c => c.x);
                    int cz = (int)candidates.Average(c => c.z);
                    anchorInfo = ($"FertileRegion", cx, cz);
                }
            }
            else
            {
                // Sort by fertility descending to prefer rich soil, then re-take size
                candidates = candidates
                    .OrderByDescending(c => c.GetFertility(map))
                    .Take(size)
                    .ToList();
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"EstablishFarm: No fertile soil found on map. This biome may not support farming.");
            }

            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var cell in candidates)
            {
                zone.AddCell(cell);
            }

            // Always set a plant — zone with null plant def causes NullReferenceException
            // in WorkGiver_GrowerSow when colonists try to sow
            zone.SetPlantDefToGrow(ResolveCrop(plantDefName));

            float avgFertility = candidates.Average(c => c.GetFertility(map));
            var desc = $"Established farm ({candidates.Count} cells, avg fertility {avgFertility:F1}) near {near} " +
                       $"(resolved to {anchorInfo.id} at {anchorInfo.x},{anchorInfo.z})";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)candidates.Count, anchorInfo.id, anchorInfo.x, anchorInfo.z);
        }

        [GameRLAction("EstablishStorage", Description = "Create a stockpile zone near a landmark. Size: Small/Medium/Large or a number of cells.")]
        public static string EstablishStorage(
            [GameRLParam("Near")] string near,
            [GameRLParam("Size")] string sizeStr = null)
        {
            int size = ResolveSize(sizeStr, defaultSize: 25);

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

        [GameRLAction("DefendColony", Description = "Auto-draft all able colonists and position them between threats and the colony center. Use when UnderAttack alert fires.")]
        public static string DefendColony()
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("DefendColony: No map loaded");

            // Find hostiles
            var hostiles = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer))
                .ToList();

            if (hostiles.Count == 0)
                throw new InvalidOperationException("DefendColony: No hostile threats detected");

            // Threat centroid
            int tx = (int)hostiles.Average(p => p.Position.x);
            int tz = (int)hostiles.Average(p => p.Position.z);
            var threatCenter = new IntVec3(tx, 0, tz);

            // Colony center (average of colonist buildings)
            var colonyCenter = map.Center;
            var buildings = map.listerBuildings.allBuildingsColonist;
            if (buildings.Count > 0)
            {
                int bx = (int)buildings.Average(b => b.Position.x);
                int bz = (int)buildings.Average(b => b.Position.z);
                colonyCenter = new IntVec3(bx, 0, bz);
            }

            // Defensive position: 1/3 of the way from colony center toward threats
            int dx = colonyCenter.x + (threatCenter.x - colonyCenter.x) / 3;
            int dz = colonyCenter.z + (threatCenter.z - colonyCenter.z) / 3;
            var defenseLine = new IntVec3(dx, 0, dz);

            // Draft all able colonists and move them to defensive positions
            var colonists = map.mapPawns.FreeColonists
                .Where(p => p != null && !p.Destroyed && !p.Downed && !p.InMentalState)
                .ToList();

            int drafted = 0;
            var positions = new List<string>();

            foreach (var pawn in colonists)
            {
                // Draft if not already
                if (pawn.drafter != null && !pawn.Drafted)
                {
                    pawn.drafter.Drafted = true;
                }

                // Find a standable cell near the defense line for each pawn
                foreach (var cell in GenRadial.RadialCellsAround(defenseLine, 8f + drafted, true))
                {
                    if (!cell.InBounds(map)) continue;
                    if (!cell.Standable(map)) continue;
                    if (!map.reachability.CanReach(pawn.Position, cell, Verse.AI.PathEndMode.OnCell,
                        TraverseParms.For(pawn)))
                        continue;

                    var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job);
                    positions.Add($"{pawn.LabelShort}→({cell.x},{cell.z})");
                    drafted++;
                    break;
                }
            }

            if (drafted == 0)
                throw new InvalidOperationException("DefendColony: No colonists available to defend");

            // Identify threat factions for description
            var factions = hostiles
                .Where(p => p.Faction != null)
                .Select(p => p.Faction.Name)
                .Distinct()
                .ToList();
            string threatDesc = factions.Count > 0 ? string.Join(", ", factions) : $"{hostiles.Count} hostiles";

            var desc = $"Defending against {threatDesc}: drafted {drafted} colonists to defensive positions between colony ({colonyCenter.x},{colonyCenter.z}) and threats ({tx},{tz}). {string.Join(", ", positions)}";
            Log.Message($"[GameRL] {desc}");

            return BuildSpatialResult(desc, (uint)drafted, "DefenseLine", dx, dz);
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
