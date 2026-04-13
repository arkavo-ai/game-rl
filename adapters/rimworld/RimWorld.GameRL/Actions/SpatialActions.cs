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
