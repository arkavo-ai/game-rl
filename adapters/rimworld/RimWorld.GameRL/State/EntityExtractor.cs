// Extract entity index from RimWorld map

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Lightweight entity reference for the entity index
    /// </summary>
    public class EntityRef
    {
        public string Id { get; set; } = "";

        public string Type { get; set; } = "";

        public string Label { get; set; } = "";

        public Position2D Position { get; set; } = new();

        public string? Faction { get; set; }

        /// <summary>
        /// For buildings: true if this building can accept bills (workbenches, butcher spots, etc.)
        /// </summary>
        public bool? CanAcceptBills { get; set; }

        /// <summary>
        /// For items: true if this item is forbidden (colonists won't interact with it)
        /// </summary>
        public bool? IsForbidden { get; set; }
    }

    /// <summary>
    /// Categorized index of all targetable entities on the map
    /// </summary>
    public class EntityIndex
    {
        public List<EntityRef> Colonists { get; set; } = new();

        public List<EntityRef> Visitors { get; set; } = new();

        public List<EntityRef> Hostiles { get; set; } = new();

        public List<EntityRef> Animals { get; set; } = new();

        public List<EntityRef> Prisoners { get; set; } = new();

        public List<EntityRef> Corpses { get; set; } = new();

        public List<EntityRef> Weapons { get; set; } = new();

        public List<EntityRef> Items { get; set; } = new();

        public Dictionary<string, int> ItemCounts { get; set; } = new();

        public List<EntityRef> Buildings { get; set; } = new();
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

            // CRITICAL: Take snapshots of all collections to avoid modification during iteration
            // The game can spawn/despawn things while we're extracting state

            // Process all spawned pawns
            List<Pawn> pawnSnapshot;
            try
            {
                pawnSnapshot = map.mapPawns.AllPawnsSpawned.ToList();
            }
            catch
            {
                pawnSnapshot = new List<Pawn>();
            }

            foreach (var pawn in pawnSnapshot)
            {
                // Skip null or despawned pawns
                if (pawn == null || pawn.Destroyed || !pawn.Spawned) continue;

                try
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
                catch
                {
                    // Skip pawns that throw during extraction
                }
            }

            // Process corpses - snapshot to avoid collection modification
            List<Thing> corpseSnapshot;
            try
            {
                corpseSnapshot = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList();
            }
            catch
            {
                corpseSnapshot = new List<Thing>();
            }

            foreach (var corpse in corpseSnapshot)
            {
                if (corpse == null || corpse.Destroyed || !corpse.Spawned) continue;

                try
                {
                    index.Corpses.Add(new EntityRef
                    {
                        Id = corpse.ThingID,
                        Type = "Corpse",
                        Label = corpse.LabelShort,
                        Position = new Position2D { X = corpse.Position.x, Y = corpse.Position.z },
                        Faction = (corpse as Corpse)?.InnerPawn?.Faction?.Name,
                        IsForbidden = corpse.IsForbidden(Faction.OfPlayer)
                    });
                }
                catch
                {
                    // Skip corpses that throw during extraction
                }
            }

            // Process weapons on ground - snapshot to avoid collection modification
            List<Thing> weaponSnapshot;
            try
            {
                weaponSnapshot = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).ToList();
            }
            catch
            {
                weaponSnapshot = new List<Thing>();
            }

            foreach (var weapon in weaponSnapshot)
            {
                if (weapon == null || weapon.Destroyed || !weapon.Spawned) continue;

                try
                {
                    // Only include weapons not held by pawns (on ground or in storage)
                    if (weapon.ParentHolder is Map || weapon.ParentHolder is Zone_Stockpile)
                    {
                        index.Weapons.Add(new EntityRef
                        {
                            Id = weapon.ThingID,
                            Type = weapon.def.IsRangedWeapon ? "RangedWeapon" : "MeleeWeapon",
                            Label = weapon.LabelShort,
                            Position = new Position2D { X = weapon.Position.x, Y = weapon.Position.z },
                            Faction = null,
                            IsForbidden = weapon.IsForbidden(Faction.OfPlayer)
                        });
                    }
                }
                catch
                {
                    // Skip weapons that throw during extraction
                }
            }

            // Process important items - aggregate food by type, list medicine individually
            // Snapshot to avoid collection modification
            List<Thing> allThingsSnapshot;
            try
            {
                allThingsSnapshot = map.listerThings.AllThings.ToList();
            }
            catch
            {
                allThingsSnapshot = new List<Thing>();
            }

            var foodCounts = new Dictionary<string, int>();
            foreach (var thing in allThingsSnapshot)
            {
                if (thing == null || thing.Destroyed) continue;

                try
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
                            Position = new Position2D { X = thing.Position.x, Y = thing.Position.z },
                            Faction = null,
                            IsForbidden = thing.IsForbidden(Faction.OfPlayer)
                        });
                    }
                }
                catch
                {
                    // Skip things that throw during extraction
                }
            }
            index.ItemCounts = foodCounts;

            // Process player-owned buildings - focus on useful buildings, not ancient ruins
            var processedIds = new HashSet<string>();

            // Source 1: allBuildingsColonist (main colonist buildings)
            List<Building> buildingSnapshot;
            try
            {
                buildingSnapshot = map.listerBuildings.allBuildingsColonist.ToList();
            }
            catch
            {
                buildingSnapshot = new List<Building>();
            }

            foreach (var building in buildingSnapshot)
            {
                if (building == null || building.Destroyed || !building.Spawned) continue;
                if (processedIds.Contains(building.ThingID)) continue;

                try
                {
                    processedIds.Add(building.ThingID);
                    index.Buildings.Add(new EntityRef
                    {
                        Id = building.ThingID,
                        Type = building.def.defName,
                        Label = building.LabelShort,
                        Position = new Position2D { X = building.Position.x, Y = building.Position.z },
                        Faction = building.Faction?.Name,
                        CanAcceptBills = building is IBillGiver
                    });
                }
                catch
                {
                    // Skip buildings that throw during extraction
                }
            }

            // Source 2: All IBillGiver buildings (workbenches, butcher spots, etc.)
            // These are critical for gameplay and may not be in allBuildingsColonist
            // NOTE: Exclude Pawns and Corpses - they implement IBillGiver for surgery but aren't buildings
            List<Thing> allThingsForBills;
            try
            {
                allThingsForBills = map.listerThings.AllThings
                    .Where(t => t is IBillGiver && !(t is Pawn) && !(t is Corpse) && t.Spawned && !t.Destroyed)
                    .ToList();
            }
            catch
            {
                allThingsForBills = new List<Thing>();
            }

            foreach (var thing in allThingsForBills)
            {
                if (thing == null) continue;
                if (processedIds.Contains(thing.ThingID)) continue;

                // Only include player faction or unowned workbenches
                if (thing.Faction != Faction.OfPlayer && thing.Faction != null) continue;

                try
                {
                    processedIds.Add(thing.ThingID);
                    index.Buildings.Add(new EntityRef
                    {
                        Id = thing.ThingID,
                        Type = thing.def.defName,
                        Label = thing.LabelShort,
                        Position = new Position2D { X = thing.Position.x, Y = thing.Position.z },
                        Faction = thing.Faction?.Name,
                        CanAcceptBills = true  // We filtered for IBillGiver
                    });
                }
                catch
                {
                    // Skip things that throw during extraction
                }
            }

            // Source 3: Interesting structures (cryptosleep caskets, doors, power, common player buildings)
            // Skip noise like walls, columns, urns
            var interestingTypes = new HashSet<string>
            {
                // Ancient structures
                "AncientCryptosleepCasket", "CryptosleepCasket",
                "Door", "Autodoor",
                "ShipChunk",
                // Power
                "SolarGenerator", "WindTurbine", "GeothermalGenerator",
                "Battery", "PowerConduit",
                // Common player buildings (in case allBuildingsColonist misses them)
                "ButcherSpot", "Campfire", "TorchLamp",
                "SleepingSpot", "Bed", "Bedroll",
                "Stool", "DiningChair", "Table1x2c", "Table2x2c",
                "Shelf", "Grave",
                "Frame" // Construction frames
            };

            List<Thing> interestingThings;
            try
            {
                interestingThings = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                    .Where(t => t.Spawned && !t.Destroyed &&
                        (interestingTypes.Contains(t.def.defName) ||
                         t.def.defName.StartsWith("Frame_") ||  // Construction frames
                         t.Faction == Faction.OfPlayer))  // All player buildings
                    .ToList();
            }
            catch
            {
                interestingThings = new List<Thing>();
            }

            foreach (var thing in interestingThings)
            {
                if (thing == null) continue;
                if (processedIds.Contains(thing.ThingID)) continue;

                try
                {
                    processedIds.Add(thing.ThingID);
                    index.Buildings.Add(new EntityRef
                    {
                        Id = thing.ThingID,
                        Type = thing.def.defName,
                        Label = thing.LabelShort,
                        Position = new Position2D { X = thing.Position.x, Y = thing.Position.z },
                        Faction = thing.Faction?.Name,
                        CanAcceptBills = thing is IBillGiver
                    });
                }
                catch
                {
                    // Skip things that throw during extraction
                }
            }

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
                Position = new Position2D { X = pawn.Position.x, Y = pawn.Position.z },
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
                .Concat(index.Items.Select(e => e.Id))
                .Concat(index.Buildings.Select(e => e.Id));
        }
    }
}
