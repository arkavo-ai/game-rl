using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using GameRL.Harmony.RPC;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// A room built via BuildRoom, addressable as an anchor ("Room_1") and as
    /// a placement constraint (PlaceBuildingNear Inside="Room_1").
    /// Registry is in-memory: stale entries are skipped when out of bounds,
    /// and rooms remain rediscoverable via RenderMap after a save reload.
    /// </summary>
    public class RoomRecord
    {
        public string Id = "";
        public string Label = "";
        public CellRect Rect;
        public IntVec3 DoorPos;
        public string DoorSide = "";
    }

    public static class RoomRegistry
    {
        private static readonly List<RoomRecord> Rooms = new List<RoomRecord>();
        private static int _nextId = 1;

        public static RoomRecord Register(CellRect rect, string doorSide, IntVec3 doorPos, string label)
        {
            var record = new RoomRecord
            {
                Id = "Room_" + _nextId,
                Label = label ?? "",
                Rect = rect,
                DoorPos = doorPos,
                DoorSide = doorSide
            };
            _nextId++;
            Rooms.Add(record);
            return record;
        }

        public static RoomRecord Find(string idOrLabel, Map map)
        {
            foreach (var room in Rooms)
            {
                if (!room.Rect.FullyContainedWithin(new CellRect(0, 0, map.Size.x, map.Size.z)))
                    continue; // stale entry from another map
                if (room.Id.Equals(idOrLabel, StringComparison.OrdinalIgnoreCase)
                    || (room.Label.Length > 0 && room.Label.Equals(idOrLabel, StringComparison.OrdinalIgnoreCase)))
                    return room;
            }
            return null;
        }

        public static List<RoomRecord> All(Map map)
        {
            var bounds = new CellRect(0, 0, map.Size.x, map.Size.z);
            return Rooms.Where(r => r.Rect.FullyContainedWithin(bounds)).ToList();
        }

        public static string DescribeAll(Map map)
        {
            var rooms = All(map);
            if (rooms.Count == 0) return "none — use BuildRoom first";
            return string.Join("; ", rooms.Select(r =>
                string.Format("{0}{1} {2}x{3} at ({4},{5})",
                    r.Id,
                    r.Label.Length > 0 ? " '" + r.Label + "'" : "",
                    r.Rect.Width, r.Rect.Height,
                    r.Rect.CenterCell.x, r.Rect.CenterCell.z)).ToArray());
        }
    }

    /// <summary>
    /// Structural building primitives. BuildRoom gives the agent one reliable
    /// verb for enclosed structures (walls + door) so bases stop accreting as
    /// wall blobs; rooms compose into bases via Room_* anchors and the
    /// Inside parameter on PlaceBuildingNear.
    /// </summary>
    [GameRLComponent]
    public static class ConstructionActions
    {
        [GameRLAction("BuildRoom", Description = "Build a rectangular room: wall perimeter with one door, interior left clear. Width/Height are exterior cells (4-15). Door: N, S, E or W (centered). Returns a Room id usable as an anchor and with PlaceBuildingNear Inside=\"Room_1\".")]
        public static string BuildRoom(
            [GameRLParam("Width")] int width = 7,
            [GameRLParam("Height")] int height = 5,
            [GameRLParam("Door")] string door = "S",
            [GameRLParam("Near")] string near = "ColonyCenter",
            [GameRLParam("Stuff")] string stuffDefName = null,
            [GameRLParam("Label")] string label = null)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("BuildRoom: No map loaded");

            width = Math.Max(4, Math.Min(15, width));
            height = Math.Max(4, Math.Min(15, height));

            string doorSide = (door ?? "S").Trim().ToUpperInvariant();
            if (doorSide != "N" && doorSide != "S" && doorSide != "E" && doorSide != "W")
                throw new InvalidOperationException(
                    $"BuildRoom: Invalid Door '{door}'. Valid sides: N, S, E, W");

            var wallDef = DefDatabase<ThingDef>.GetNamed("Wall", errorOnFail: false);
            var doorDef = DefDatabase<ThingDef>.GetNamed("Door", errorOnFail: false);
            if (wallDef == null || doorDef == null)
                throw new InvalidOperationException("BuildRoom: Wall/Door defs not found");

            ThingDef stuffDef;
            if (stuffDefName != null)
            {
                stuffDef = DefDatabase<ThingDef>.GetNamed(stuffDefName, errorOnFail: false);
                if (stuffDef == null)
                    throw new InvalidOperationException(
                        $"BuildRoom: Unknown material '{stuffDefName}'. Common: WoodLog, Steel, BlocksSandstone");
            }
            else
            {
                stuffDef = ThingDefOf.WoodLog;
            }

            var anchor = SpatialActions.ResolveAnchorFor(near, map);

            // Find the nearest valid footprint: candidate rect centers spiral
            // outward from the anchor (deterministic nearest-first).
            CellRect? footprint = null;
            foreach (var center in GenRadial.RadialCellsAround(anchor, 30f, true))
            {
                var rect = CellRect.CenteredOn(center, width, height);
                if (FootprintIsClear(rect, map, wallDef, stuffDef))
                {
                    footprint = rect;
                    break;
                }
            }

            if (footprint == null)
            {
                throw new InvalidOperationException(
                    $"BuildRoom: No clear {width}x{height} footprint within 30 cells of '{near}'. " +
                    $"Try a smaller room, a different anchor (rooms built so far: {RoomRegistry.DescribeAll(map)}), " +
                    $"or DesignateClearNear to clear trees first.");
            }

            var rectFinal = footprint.Value;
            var doorPos = DoorCell(rectFinal, doorSide);

            int wallsPlaced = 0;
            foreach (var cell in rectFinal.EdgeCells)
            {
                if (cell == doorPos) continue;
                var wall = ThingMaker.MakeThing(wallDef, stuffDef);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, cell, map);
                wallsPlaced++;
            }
            var doorThing = ThingMaker.MakeThing(doorDef, stuffDef);
            doorThing.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(doorThing, doorPos, map);

            var record = RoomRegistry.Register(rectFinal, doorSide, doorPos, label);

            var desc = $"Built {record.Id}{(record.Label.Length > 0 ? $" '{record.Label}'" : "")} " +
                       $"({width}x{height}) walls ({rectFinal.minX}-{rectFinal.maxX},{rectFinal.minZ}-{rectFinal.maxZ}), " +
                       $"door {doorSide} at ({doorPos.x},{doorPos.z}). " +
                       $"Furnish with PlaceBuildingNear Inside=\"{record.Id}\"; verify with RenderMap.";
            Log.Message($"[GameRL] {desc}");

            return SpatialActions.BuildSpatialResultFor(
                desc, (uint)(wallsPlaced + 1), near, record.Id,
                rectFinal.CenterCell.x, rectFinal.CenterCell.z, false);
        }

        private static IntVec3 DoorCell(CellRect rect, string side)
        {
            int midX = (rect.minX + rect.maxX) / 2;
            int midZ = (rect.minZ + rect.maxZ) / 2;
            switch (side)
            {
                case "N": return new IntVec3(midX, 0, rect.maxZ);
                case "S": return new IntVec3(midX, 0, rect.minZ);
                case "E": return new IntVec3(rect.maxX, 0, midZ);
                default: return new IntVec3(rect.minX, 0, midZ);
            }
        }

        private static bool FootprintIsClear(CellRect rect, Map map, ThingDef wallDef, ThingDef stuffDef)
        {
            if (rect.minX < 1 || rect.minZ < 1
                || rect.maxX >= map.Size.x - 1 || rect.maxZ >= map.Size.z - 1)
                return false;

            foreach (var cell in rect.EdgeCells)
            {
                var report = GenConstruct.CanPlaceBlueprintAt(wallDef, cell, Rot4.North, map,
                    godMode: false, thing: null, stuffDef: stuffDef);
                if (!report.Accepted)
                    return false;
            }
            foreach (var cell in rect.ContractedBy(1))
            {
                if (!cell.Standable(map)) return false;
                if (cell.GetEdifice(map) != null) return false;
            }
            return true;
        }
    }
}
