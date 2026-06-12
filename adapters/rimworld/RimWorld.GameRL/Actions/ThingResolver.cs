// Thing/Pawn resolvers for RimWorld GameRL

using System;
using System.Collections.Generic;
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

            // Search free colonists first by ThingID (most common case)
            var pawn = map.mapPawns.FreeColonists.FirstOrDefault(p => p.ThingID == id);
            if (pawn != null)
                return pawn;

            // Fall back: match by name (LLMs often use names instead of ThingIDs).
            // Short names are NOT unique — multiple colonists/animals can share a
            // first or nick name — so resolve a name only when it is unambiguous,
            // else return null and let the caller fail loudly rather than silently
            // acting on the wrong pawn.
            var byName = ResolveUniquePawnByName(map.mapPawns.FreeColonists, id);
            if (byName != null)
                return byName;

            // Fall back to all pawns on map by ThingID
            pawn = map.mapPawns.AllPawns.FirstOrDefault(p => p.ThingID == id);
            if (pawn != null)
                return pawn;

            // Fall back to all pawns by name (also uniqueness-guarded)
            return ResolveUniquePawnByName(map.mapPawns.AllPawns, id);
        }

        /// <summary>
        /// Resolve a pawn by name, preferring an exact full-name match. A short-name
        /// match is accepted only when exactly one pawn carries that short name;
        /// an ambiguous short name returns null so the action fails loudly instead
        /// of resolving to an unintended pawn.
        /// </summary>
        private static Pawn? ResolveUniquePawnByName(IEnumerable<Pawn> pawns, string id)
        {
            var list = pawns.ToList();

            // Exact full name first (e.g. "Lizzie 'Fox' Carter") — effectively unique.
            var full = list.Where(p =>
                p.Name?.ToStringFull?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            if (full.Count == 1)
                return full[0];
            if (full.Count > 1)
                return null; // ambiguous even on full name — refuse

            // Short name only when unique across the candidate set.
            var shortMatches = list.Where(p =>
                p.Name?.ToStringShort?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            return shortMatches.Count == 1 ? shortMatches[0] : null;
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

            // Search colonist buildings first, then all buildings on map
            var building = map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(b => b.ThingID == id);
            if (building != null)
                return building;

            return map.listerBuildings.allBuildingsNonColonist
                .FirstOrDefault(b => b.ThingID == id);
        }
    }
}
