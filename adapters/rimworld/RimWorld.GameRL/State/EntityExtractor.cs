// Extract entity index from RimWorld map

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Verse;
using RimWorld;
using MessagePack;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Lightweight entity reference for the entity index
    /// </summary>
    [MessagePackObject]
    public class EntityRef
    {
        [Key("id")]
        public string Id { get; set; } = "";

        [Key("type")]
        public string Type { get; set; } = "";

        [Key("label")]
        public string Label { get; set; } = "";

        [Key("pos")]
        public int[] Position { get; set; } = new int[2];

        [Key("faction")]
        public string? Faction { get; set; }
    }

    /// <summary>
    /// Categorized index of all targetable entities on the map
    /// </summary>
    [MessagePackObject]
    public class EntityIndex
    {
        [Key("colonists")]
        public List<EntityRef> Colonists { get; set; } = new();

        [Key("visitors")]
        public List<EntityRef> Visitors { get; set; } = new();

        [Key("hostiles")]
        public List<EntityRef> Hostiles { get; set; } = new();

        [Key("animals")]
        public List<EntityRef> Animals { get; set; } = new();

        [Key("prisoners")]
        public List<EntityRef> Prisoners { get; set; } = new();

        [Key("corpses")]
        public List<EntityRef> Corpses { get; set; } = new();

        [Key("weapons")]
        public List<EntityRef> Weapons { get; set; } = new();

        [Key("items")]
        public List<EntityRef> Items { get; set; } = new();

        [Key("item_counts")]
        public Dictionary<string, int> ItemCounts { get; set; } = new();
    }

    /// <summary>
    /// Extracts categorized entity index from the map
    /// </summary>
    public static class EntityExtractor
    {
        private static Stopwatch _extractTimer = new();
        private static int _slowExtractCount = 0;

        public static EntityIndex Extract(Map? map)
        {
            _extractTimer.Restart();

            var index = new EntityIndex();
            if (map == null) return index;

            // Process all spawned pawns
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                var entityRef = CreateEntityRef(pawn);

                if (pawn.Faction == Faction.OfPlayer)
                {
                    if (pawn.IsPrisoner)
                    {
                        index.Prisoners.Add(entityRef);
                    }
                    else if (pawn.RaceProps.Humanlike)
                    {
                        index.Colonists.Add(entityRef);
                    }
                    else
                    {
                        // Player's animals
                        index.Animals.Add(entityRef);
                    }
                }
                else if (pawn.HostileTo(Faction.OfPlayer))
                {
                    index.Hostiles.Add(entityRef);
                }
                else if (pawn.RaceProps.Humanlike)
                {
                    // Non-hostile, non-player humanlike = visitor
                    index.Visitors.Add(entityRef);
                }
                else
                {
                    // Wild animals
                    index.Animals.Add(entityRef);
                }
            }

            // Process corpses
            var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            foreach (var corpse in corpses)
            {
                index.Corpses.Add(new EntityRef
                {
                    Id = corpse.ThingID,
                    Type = "Corpse",
                    Label = corpse.LabelShort,
                    Position = new[] { corpse.Position.x, corpse.Position.z },
                    Faction = (corpse as Corpse)?.InnerPawn?.Faction?.Name
                });
            }

            // Process weapons on ground
            var weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
            foreach (var weapon in weapons)
            {
                // Only include weapons not held by pawns (on ground or in storage)
                if (weapon.ParentHolder is Map || weapon.ParentHolder is Zone_Stockpile)
                {
                    index.Weapons.Add(new EntityRef
                    {
                        Id = weapon.ThingID,
                        Type = weapon.def.IsRangedWeapon ? "RangedWeapon" : "MeleeWeapon",
                        Label = weapon.LabelShort,
                        Position = new[] { weapon.Position.x, weapon.Position.z },
                        Faction = null
                    });
                }
            }

            // Process important items - aggregate food by type, list medicine individually
            var foodCounts = new Dictionary<string, int>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.def.IsNutritionGivingIngestible && thing.Spawned)
                {
                    // Aggregate food by def name
                    var key = thing.def.defName;
                    if (!foodCounts.ContainsKey(key)) foodCounts[key] = 0;
                    foodCounts[key] += thing.stackCount;
                }
                else if (thing.def.IsMedicine && thing.Spawned)
                {
                    // List medicine individually (usually few)
                    index.Items.Add(new EntityRef
                    {
                        Id = thing.ThingID,
                        Type = "Medicine",
                        Label = thing.LabelShort,
                        Position = new[] { thing.Position.x, thing.Position.z },
                        Faction = null
                    });
                }
            }
            index.ItemCounts = foodCounts;

            _extractTimer.Stop();
            var elapsed = _extractTimer.ElapsedMilliseconds;
            if (elapsed > 50) // More than 50ms is concerning
            {
                _slowExtractCount++;
                if (_slowExtractCount <= 5 || _slowExtractCount % 100 == 0)
                {
                    Log.Warning($"[GameRL] EntityExtractor took {elapsed}ms (items: {index.Items.Count}, weapons: {index.Weapons.Count}). Slow count: {_slowExtractCount}");
                }
            }

            return index;
        }

        private static EntityRef CreateEntityRef(Pawn pawn)
        {
            return new EntityRef
            {
                Id = pawn.ThingID,
                Type = pawn.RaceProps.Humanlike ? "Human" : "Animal",
                Label = pawn.LabelShort,
                Position = new[] { pawn.Position.x, pawn.Position.z },
                Faction = pawn.Faction?.Name
            };
        }

        /// <summary>
        /// Get all entity IDs from the index (for reachability checks)
        /// </summary>
        public static IEnumerable<string> GetAllEntityIds(EntityIndex index)
        {
            return index.Colonists.Select(e => e.Id)
                .Concat(index.Visitors.Select(e => e.Id))
                .Concat(index.Hostiles.Select(e => e.Id))
                .Concat(index.Animals.Select(e => e.Id))
                .Concat(index.Prisoners.Select(e => e.Id))
                .Concat(index.Corpses.Select(e => e.Id))
                .Concat(index.Weapons.Select(e => e.Id))
                .Concat(index.Items.Select(e => e.Id));
        }
    }
}
