using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// A contiguous fertile area exposed as a stable spatial anchor (FertileCluster_0, ...)
    /// </summary>
    public class FertileCluster
    {
        public string Id = "";
        public IntVec3 Center;
        public int CellCount;
        public float Fertility;
    }

    /// <summary>
    /// Shared anchor geometry per spec draft-02 §7 (REQ-SPA-03/04):
    /// the reference point for spatial resolution is the colony's activity
    /// centroid — never the geometric map center — and agents discover
    /// anchors via a guaranteed landmark set (ColonyCenter, MapCenter,
    /// eight compass regions, fertile clusters).
    /// </summary>
    public static class SpatialAnchors
    {
        /// <summary>
        /// The colony's activity centroid: average position of colonist
        /// buildings and free colonists. Falls back to map center only when
        /// the colony has no presence at all.
        /// </summary>
        public static IntVec3 GetColonyCenter(Map map)
        {
            long sx = 0, sz = 0, n = 0;
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                sx += b.Position.x;
                sz += b.Position.z;
                n++;
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                sx += p.Position.x;
                sz += p.Position.z;
                n++;
            }
            if (n == 0)
                return map.Center;
            return new IntVec3((int)(sx / n), 0, (int)(sz / n));
        }

        /// <summary>
        /// Centroids of the eight compass regions (3x3 partition of the map,
        /// center block excluded). Names: Region_N, Region_NE, ... Region_NW.
        /// </summary>
        public static List<KeyValuePair<string, IntVec3>> GetRegionCentroids(Map map)
        {
            int lox = map.Size.x / 6;
            int midx = map.Size.x / 2;
            int hix = map.Size.x - map.Size.x / 6 - 1;
            int loz = map.Size.z / 6;
            int midz = map.Size.z / 2;
            int hiz = map.Size.z - map.Size.z / 6 - 1;

            var regions = new List<KeyValuePair<string, IntVec3>>();
            regions.Add(new KeyValuePair<string, IntVec3>("Region_N", new IntVec3(midx, 0, hiz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_NE", new IntVec3(hix, 0, hiz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_E", new IntVec3(hix, 0, midz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_SE", new IntVec3(hix, 0, loz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_S", new IntVec3(midx, 0, loz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_SW", new IntVec3(lox, 0, loz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_W", new IntVec3(lox, 0, midz)));
            regions.Add(new KeyValuePair<string, IntVec3>("Region_NW", new IntVec3(lox, 0, hiz)));
            return regions;
        }

        /// <summary>
        /// Fertile clusters with stable IDs, ordered by fertility then size
        /// (the same ordering the observation's FertileRegions uses, so
        /// "FertileCluster_0" in Landmarks matches what agents see in terrain).
        /// Uses 16x16 grid bucketing per terrain type, matching
        /// RimWorldStateExtractor.ExtractTerrainSummary.
        /// </summary>
        public static List<FertileCluster> GetFertileClusters(Map map)
        {
            var clusters = new List<FertileCluster>();
            var grid = map.terrainGrid;
            var byTypeAndBucket = new Dictionary<string, Dictionary<long, List<IntVec3>>>();

            for (int x = 0; x < map.Size.x; x++)
            {
                for (int z = 0; z < map.Size.z; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    var terrain = grid.TerrainAt(cell);
                    if (terrain == null || terrain.fertility <= 0f) continue;
                    if (cell.GetEdifice(map) != null) continue;

                    Dictionary<long, List<IntVec3>> buckets;
                    if (!byTypeAndBucket.TryGetValue(terrain.defName, out buckets))
                    {
                        buckets = new Dictionary<long, List<IntVec3>>();
                        byTypeAndBucket[terrain.defName] = buckets;
                    }
                    long bucket = ((long)(x / 16) << 32) | (uint)(z / 16);
                    List<IntVec3> cells;
                    if (!buckets.TryGetValue(bucket, out cells))
                    {
                        cells = new List<IntVec3>();
                        buckets[bucket] = cells;
                    }
                    cells.Add(cell);
                }
            }

            foreach (var typeEntry in byTypeAndBucket)
            {
                var terrainDef = DefDatabase<TerrainDef>.GetNamed(typeEntry.Key, errorOnFail: false);
                float fertility = terrainDef != null ? terrainDef.fertility : 1f;
                foreach (var bucket in typeEntry.Value.Values)
                {
                    if (bucket.Count < 4) continue; // skip tiny patches
                    int cx = (int)bucket.Average(c => c.x);
                    int cz = (int)bucket.Average(c => c.z);
                    clusters.Add(new FertileCluster
                    {
                        Center = new IntVec3(cx, 0, cz),
                        CellCount = bucket.Count,
                        Fertility = fertility
                    });
                }
            }

            clusters.Sort((a, b) =>
            {
                int cmp = b.Fertility.CompareTo(a.Fertility);
                return cmp != 0 ? cmp : b.CellCount.CompareTo(a.CellCount);
            });
            for (int i = 0; i < clusters.Count; i++)
                clusters[i].Id = "FertileCluster_" + i;
            return clusters;
        }

        /// <summary>
        /// Short list of feasible anchor names for loud-error messages (REQ-ERR-02).
        /// </summary>
        public static string DescribeAlternatives(Map map)
        {
            var names = new List<string> { "ColonyCenter", "MapCenter" };
            foreach (var c in GetFertileClusters(map).Take(2))
                names.Add(string.Format("{0} at ({1},{2})", c.Id, c.Center.x, c.Center.z));
            names.Add("Region_N..Region_NW");
            return string.Join(", ", names.ToArray());
        }
    }
}
