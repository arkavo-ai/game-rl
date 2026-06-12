using System;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using GameRL.Harmony.RPC;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Textual map viewport: lets LLM agents SEE local structure (walls,
    /// doors, beds, zones, terrain, pawns) and act on exact (x,z) coordinates
    /// read off the rulers. Read-only — never mutates game state.
    /// </summary>
    [GameRLComponent]
    public static class MapRender
    {
        [GameRLAction("RenderMap", Description = "Render an ASCII map viewport around an anchor (read-only, no time advance). North is up; coordinate rulers give exact (x,z) cells usable as anchors. Legend: P colonist, H hostile, A animal, # wall, M mountain, D door, B bed, = building, i item, S stockpile, F farm, T tree, ~ water, . ground, , plants.")]
        public static string RenderMap(
            [GameRLParam("Near")] string near = "ColonyCenter",
            [GameRLParam("Radius")] int radius = 16)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("RenderMap: No map loaded");

            radius = Math.Max(4, Math.Min(30, radius));
            var center = SpatialActions.ResolveAnchorFor(near, map);

            int minX = Math.Max(0, center.x - radius);
            int maxX = Math.Min(map.Size.x - 1, center.x + radius);
            int minZ = Math.Max(0, center.z - radius);
            int maxZ = Math.Min(map.Size.z - 1, center.z + radius);

            var sb = new StringBuilder();
            sb.AppendLine($"Map view: center {near} ({center.x},{center.z}), x {minX}..{maxX}, z {minZ}..{maxZ}, N=up, 1 char = 1 cell");

            // x-axis ruler: absolute labels every 10 columns, tick row beneath
            var labels = new StringBuilder("      ");
            var ticks = new StringBuilder("      ");
            for (int x = minX; x <= maxX; x++)
            {
                if (x % 10 == 0)
                {
                    string label = x.ToString();
                    // write label left-aligned at this column if it fits
                    while (labels.Length < 6 + (x - minX)) labels.Append(' ');
                    if (labels.Length == 6 + (x - minX)) labels.Append(label);
                    while (ticks.Length < 6 + (x - minX)) ticks.Append(' ');
                    ticks.Append('|');
                }
            }
            sb.AppendLine(labels.ToString());
            sb.AppendLine(ticks.ToString());

            // Rows: north (max z) at top
            for (int z = maxZ; z >= minZ; z--)
            {
                sb.Append(z.ToString().PadLeft(5)).Append(' ');
                for (int x = minX; x <= maxX; x++)
                {
                    sb.Append(GlyphAt(new IntVec3(x, 0, z), map));
                }
                sb.AppendLine();
            }

            sb.AppendLine("Legend: P colonist, H hostile, A animal, # wall, M mountain, D door, B bed, = building, i item, S stockpile, F farm, T tree, ~ water, . ground, , plants");
            sb.AppendLine($"Rooms: {RoomRegistry.DescribeAll(map)}");
            var colonyCenter = SpatialAnchors.GetColonyCenter(map);
            sb.Append($"ColonyCenter: ({colonyCenter.x},{colonyCenter.z})");
            return sb.ToString();
        }

        private static char GlyphAt(IntVec3 cell, Map map)
        {
            // Pawns first — the things that move matter most
            var pawn = cell.GetFirstPawn(map);
            if (pawn != null)
            {
                if (pawn.IsColonist) return 'P';
                if (pawn.HostileTo(Faction.OfPlayer)) return 'H';
                return 'A';
            }

            var edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                if (edifice.def.building != null && edifice.def.building.isNaturalRock) return 'M';
                if (edifice is Building_Door) return 'D';
                if (edifice.def.defName == "Wall") return '#';
                if (edifice is Building_Bed) return 'B';
                return '=';
            }

            var items = cell.GetThingList(map);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].def.category == ThingCategory.Item) return 'i';
            }

            var zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Stockpile) return 'S';
            if (zone is Zone_Growing) return 'F';

            var plant = cell.GetPlant(map);
            if (plant != null)
            {
                if (plant.def.plant != null && plant.def.plant.IsTree) return 'T';
                return ',';
            }

            var terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain != null && terrain.IsWater) return '~';
            return '.';
        }
    }
}
