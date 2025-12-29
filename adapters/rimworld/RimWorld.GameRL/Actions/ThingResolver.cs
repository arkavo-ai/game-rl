// Thing/Pawn resolvers for RimWorld GameRL

using System;
using System.Linq;
using GameRL.Harmony.RPC;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Resolves string ThingIDs to Pawn objects
    /// </summary>
    public class PawnResolver : ITypeResolver
    {
        public Type TargetType => typeof(Pawn);

        public object? Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var map = Find.CurrentMap;
            if (map == null)
                return null;

            // Search free colonists first (most common case)
            var pawn = map.mapPawns.FreeColonists.FirstOrDefault(p => p.ThingID == id);
            if (pawn != null)
                return pawn;

            // Fall back to all pawns on map
            return map.mapPawns.AllPawns.FirstOrDefault(p => p.ThingID == id);
        }
    }

    /// <summary>
    /// Resolves string ThingIDs to Thing objects
    /// </summary>
    public class ThingResolver : ITypeResolver
    {
        public Type TargetType => typeof(Thing);

        public object? Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var map = Find.CurrentMap;
            if (map == null)
                return null;

            // Search all things on the map
            return map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == id);
        }
    }

    /// <summary>
    /// Resolves string ThingIDs to Building objects
    /// </summary>
    public class BuildingResolver : ITypeResolver
    {
        public Type TargetType => typeof(Building);

        public object? Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var map = Find.CurrentMap;
            if (map == null)
                return null;

            return map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(b => b.ThingID == id);
        }
    }
}
