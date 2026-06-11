// Main state extractor for RimWorld observations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Verse;
using Verse.AI;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.Protocol;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Feedback from last action execution
    /// </summary>
    public class ActionFeedback
    {
        public bool Success { get; set; }

        public string ActionType { get; set; } = "";

        public string? Message { get; set; }

        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// Complete observation state
    /// </summary>
    public class RimWorldObservation
    {
        public ulong Tick { get; set; }

        public int ColonistCount { get; set; }

        public List<ColonistState> Colonists { get; set; } = new();

        public ResourceState Resources { get; set; } = new();

        public string? Weather { get; set; }

        public string? Season { get; set; }

        public int Hour { get; set; }

        public List<ThreatInfo> Threats { get; set; } = new();

        public List<VisitorState> Visitors { get; set; } = new();

        public EntityIndex Entities { get; set; } = new();

        public List<AlertState> Alerts { get; set; } = new();

        public float Temperature { get; set; }

        public int IdleColonists { get; set; }

        /// <summary>
        /// Feedback from the last action (for RL agents)
        /// </summary>
        public ActionFeedback? LastAction { get; set; }

        /// <summary>
        /// Episode metadata for RL context
        /// </summary>
        public EpisodeInfo? Episode { get; set; }

        /// <summary>
        /// Actions valid at current game state (for action masking)
        /// </summary>
        public List<string> ValidActions { get; set; } = new();

        /// <summary>
        /// Current research state
        /// </summary>
        public ResearchInfo? Research { get; set; }

        /// <summary>
        /// Zones on the map (stockpiles, growing zones, etc.)
        /// </summary>
        public List<ZoneInfo> Zones { get; set; } = new();

        /// <summary>
        /// Faction diplomacy state
        /// </summary>
        public List<FactionRelationInfo> FactionRelations { get; set; } = new();

        /// <summary>
        /// Prisoner details (recruitment progress, interaction mode)
        /// </summary>
        public List<PrisonerInfo> Prisoners { get; set; } = new();

        /// <summary>
        /// Active traders on the map or in orbit
        /// </summary>
        public List<TraderInfo> ActiveTraders { get; set; } = new();

        /// <summary>
        /// Map metadata (size, biome) - static per map
        /// </summary>
        public MapInfo? Map { get; set; }

        /// <summary>
        /// Bed assignments (colonist-to-bed mapping)
        /// </summary>
        public List<BedAssignment> BedAssignments { get; set; } = new();

        /// <summary>
        /// Power grid status
        /// </summary>
        public PowerGridStatus? PowerGrid { get; set; }

        /// <summary>
        /// Room quality data (full observations only)
        /// </summary>
        public List<RoomInfo> Rooms { get; set; } = new();

        /// <summary>
        /// Terrain fertility summary for farming placement (full observations only)
        /// </summary>
        public TerrainSummary? Terrain { get; set; }

        /// <summary>
        /// Spatial anchors the agent can reference in Near parameters
        /// </summary>
        public List<object> Landmarks { get; set; } = new();
    }

    /// <summary>
    /// Research state for observations
    /// </summary>
    public class ResearchInfo
    {
        public string? CurrentProject { get; set; }
        public string? CurrentProjectLabel { get; set; }
        public float Progress { get; set; }
        public List<ResearchOption> Available { get; set; } = new();
    }

    public class ResearchOption
    {
        public string DefName { get; set; } = "";
        public string Label { get; set; } = "";
        public float Cost { get; set; }
        public List<string> MissingPrereqs { get; set; } = new();
        public bool CanStart { get; set; }
    }

    /// <summary>
    /// Zone information (stockpile, growing, etc.)
    /// </summary>
    public class ZoneInfo
    {
        public string Label { get; set; } = "";
        public string Type { get; set; } = "";
        public int CellCount { get; set; }
        public Position2D Center { get; set; } = new();
        public string? PlantType { get; set; }
        public int? Priority { get; set; }
    }

    /// <summary>
    /// Faction diplomacy state
    /// </summary>
    public class FactionRelationInfo
    {
        public string Name { get; set; } = "";
        public string Relation { get; set; } = "";
        public int Goodwill { get; set; }
        public bool CanTrade { get; set; }
    }

    /// <summary>
    /// Prisoner details for recruitment decisions
    /// </summary>
    public class PrisonerInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public float Health { get; set; }
        public string? InteractionMode { get; set; }
        public float RecruitDifficulty { get; set; }
        public float Mood { get; set; }
        public float Resistance { get; set; }
    }

    /// <summary>
    /// Active trader on map or in orbit
    /// </summary>
    public class TraderInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string? Faction { get; set; }
        public int Silver { get; set; }
    }

    /// <summary>
    /// Map metadata (static per map)
    /// </summary>
    public class MapInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Biome { get; set; } = "";
        public float Elevation { get; set; }
        public float Rainfall { get; set; }
        public float Temperature { get; set; }
    }

    /// <summary>
    /// Bed assignment info
    /// </summary>
    public class BedAssignment
    {
        public string BedId { get; set; } = "";
        public string BedType { get; set; } = "";
        public Position2D Position { get; set; } = new();
        public bool IsMedical { get; set; }
        public List<string> OwnerIds { get; set; } = new();
    }

    /// <summary>
    /// Power grid status
    /// </summary>
    public class PowerGridStatus
    {
        public float TotalProduction { get; set; }
        public float TotalConsumption { get; set; }
        public float StoredEnergy { get; set; }
        public float MaxStoredEnergy { get; set; }
        public int GeneratorCount { get; set; }
        public int BatteryCount { get; set; }
        public bool HasSufficientPower { get; set; }
    }

    /// <summary>
    /// Room quality info
    /// </summary>
    public class RoomInfo
    {
        public string Role { get; set; } = "";
        public float Impressiveness { get; set; }
        public float Beauty { get; set; }
        public float Cleanliness { get; set; }
        public float Temperature { get; set; }
        public int CellCount { get; set; }
    }

    /// <summary>
    /// A contiguous region of fertile terrain suitable for growing
    /// </summary>
    public class FertileRegion
    {
        public string TerrainType { get; set; } = "";
        public float Fertility { get; set; }
        public int CellCount { get; set; }
        public Position2D Center { get; set; } = new();
        public int MinX { get; set; }
        public int MaxX { get; set; }
        public int MinY { get; set; }
        public int MaxY { get; set; }
        public bool Roofed { get; set; }
    }

    /// <summary>
    /// Terrain summary for farming/building placement decisions
    /// </summary>
    public class TerrainSummary
    {
        public List<FertileRegion> FertileRegions { get; set; } = new();
        public int TotalFertileCells { get; set; }
        public int TotalRichSoilCells { get; set; }
    }

    /// <summary>
    /// Episode tracking info for RL
    /// </summary>
    public class EpisodeInfo
    {
        /// <summary>
        /// Ticks since episode start
        /// </summary>
        public int TicksElapsed { get; set; }

        /// <summary>
        /// Maximum ticks before truncation (15 in-game days)
        /// </summary>
        public int MaxTicks { get; set; } = 60000 * 15;

        /// <summary>
        /// Progress through episode (0.0 to 1.0)
        /// </summary>
        public float Progress { get; set; }
    }

    /// <summary>
    /// Visitor/guest state for observations
    /// </summary>
    public class VisitorState
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public float[] Position { get; set; } = new float[2];

        public string? Faction { get; set; }

        public string? Relation { get; set; }  // ally, neutral, etc.

        public float Health { get; set; }
    }

    /// <summary>
    /// Threat information
    /// </summary>
    public class ThreatInfo
    {
        public string Type { get; set; } = "";

        public string? SubType { get; set; }

        public string? Faction { get; set; }

        public int Severity { get; set; }

        public int Count { get; set; }

        public float[]? Position { get; set; }
    }

    public class AlertState
    {
        public string Label { get; set; } = "";

        public int Severity { get; set; }  // 1=Normal, 2=High, 3=Critical
    }

    /// <summary>
    /// Implements IStateExtractor for RimWorld
    /// </summary>
    public class RimWorldStateExtractor : IStateExtractor
    {
        // Use alias to avoid conflict with DeltaObservation.GameEvent
        // Using type alias defined at top of file
        private readonly List<global::GameRL.Harmony.Protocol.GameEvent> _pendingEvents = new();
        private readonly Dictionary<string, ulong> _lastEventTick = new();

        // Rate limiting: minimum ticks between events of same type
        private const int MinTicksBetweenSameEvent = 60;  // ~1 second at normal speed

        // TTL: maximum age of events before they're dropped (in ticks)
        private const int MaxEventAgeTicks = 2500;  // ~42 seconds at normal speed

        // Maximum pending events to prevent memory bloat
        private const int MaxPendingEvents = 100;

        // Alert change tracking
        private Dictionary<string, byte> _previousAlerts = new();  // label -> severity
        private int _ticksSinceLastAlertCheck;
        private const int AlertCheckIntervalTicks = 120;  // ~2 seconds at normal speed
        private static bool _alertFieldDiscovered;
        private static string? _discoveredAlertFieldName;

        /// <summary>
        /// Set to true when a high/critical alert appears, signaling immediate push.
        /// </summary>
        public bool HasUrgentEvents { get; set; }

        // Delta state tracking - per-agent snapshots
        private readonly Dictionary<string, Dictionary<string, ColonistSnapshot>> _previousColonistStates = new();
        private readonly Dictionary<string, Dictionary<string, int>> _previousResources = new();
        private readonly Dictionary<string, HashSet<string>> _previousEntityIds = new();
        private readonly Dictionary<string, List<string>> _previousThreatIds = new();

        /// <summary>
        /// Force full state on next observation (set by RequestFullState action)
        /// </summary>
        public bool ForceFullState { get; set; }

        /// <summary>
        /// Last action result to include in observation (set by executor)
        /// </summary>
        public Actions.ActionResult? LastActionResult { get; set; }

        /// <summary>
        /// Episode start tick (set by executor on reset)
        /// </summary>
        public int EpisodeStartTick { get; set; }

        /// <summary>
        /// Max ticks per episode before truncation
        /// </summary>
        public const int MaxEpisodeTicks = 60000 * 15;  // 15 in-game days

        public ulong CurrentTick => (ulong)(Find.TickManager?.TicksGame ?? 0);

        public object ExtractObservation(string agentId)
        {
            var map = Find.CurrentMap;

            var entities = EntityExtractor.Extract(map);
            var observation = new RimWorldObservation
            {
                Tick = CurrentTick,
                ColonistCount = map?.mapPawns.FreeColonistsCount ?? 0,
                Colonists = ColonistExtractor.Extract(map, entities),
                Resources = ResourceExtractor.Extract(map),
                Weather = map?.weatherManager.curWeather?.defName,
                Season = map != null ? GenLocalDate.Season(map).ToString() : null,
                Hour = map != null ? GenLocalDate.HourOfDay(map) : 0,
                Threats = ExtractThreats(map),
                Visitors = ExtractVisitors(map),
                Entities = entities,
                Alerts = ExtractAlerts(),
                Temperature = map != null ? map.mapTemperature.OutdoorTemp : 0f,
                IdleColonists = CountIdleColonists(map),
                ValidActions = ComputeValidActions(map),
                Research = ExtractResearch(),
                Zones = ExtractZones(map),
                FactionRelations = ExtractFactionRelations(),
                Prisoners = ExtractPrisoners(map),
                ActiveTraders = ExtractTraders(map),
                Map = ExtractMapInfo(map),
                BedAssignments = ExtractBedAssignments(map),
                PowerGrid = ExtractPowerGrid(map),
                Rooms = ExtractRooms(map),
                Terrain = ExtractTerrainSummary(map),
                Landmarks = ExtractLandmarks(map)
            };

            // Include last action feedback for RL
            if (LastActionResult != null)
            {
                observation.LastAction = new ActionFeedback
                {
                    Success = LastActionResult.Success,
                    ActionType = LastActionResult.ActionType,
                    Message = LastActionResult.Message,
                    ErrorCode = LastActionResult.ErrorCode?.ToString()
                };
            }

            // Include episode metadata
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            var ticksElapsed = currentTick - EpisodeStartTick;
            observation.Episode = new EpisodeInfo
            {
                TicksElapsed = ticksElapsed,
                MaxTicks = MaxEpisodeTicks,
                Progress = (float)ticksElapsed / MaxEpisodeTicks
            };

            return observation;
        }

        /// <summary>
        /// Extract observation for an agent based on their observation mode.
        /// Returns full state on first observation, then deltas based on mode.
        /// </summary>
        public object ExtractForAgent(string agentId, ObservationMode mode, bool isFirstObservation, string? previousHash, DeltaConfig config)
        {
            // Force full state if requested or first observation
            if (ForceFullState || isFirstObservation || mode == ObservationMode.Full)
            {
                ForceFullState = false;
                var fullObs = ExtractObservation(agentId) as RimWorldObservation;
                UpdateSnapshots(agentId, fullObs);
                return fullObs!;
            }

            // Extract delta observation
            return ExtractDeltaObservation(agentId, previousHash, config);
        }

        /// <summary>
        /// Extract a delta observation containing only changes since last observation
        /// </summary>
        private DeltaObservation ExtractDeltaObservation(string agentId, string? previousHash, DeltaConfig config)
        {
            var map = Find.CurrentMap;
            var stateHash = ComputeStateHash();

            var delta = new DeltaObservation
            {
                Tick = CurrentTick,
                Hour = map != null ? GenLocalDate.HourOfDay(map) : 0,
                StateHash = stateHash,
                PreviousHash = previousHash,
                Alerts = ExtractAlerts(),
                Events = ConvertToObservationEvents(CollectEvents()),
                ValidActions = ComputeValidActions(map),
                Research = ExtractResearch(),
                Zones = ExtractZones(map),
                FactionRelations = ExtractFactionRelations(),
                Prisoners = ExtractPrisoners(map),
                ActiveTraders = ExtractTraders(map),
                BedAssignments = ExtractBedAssignments(map),
                PowerGrid = ExtractPowerGrid(map)
            };

            // Include last action feedback
            if (LastActionResult != null)
            {
                delta.LastAction = new ActionFeedback
                {
                    Success = LastActionResult.Success,
                    ActionType = LastActionResult.ActionType,
                    Message = LastActionResult.Message,
                    ErrorCode = LastActionResult.ErrorCode?.ToString()
                };
            }

            // Get previous snapshots for this agent
            if (!_previousColonistStates.TryGetValue(agentId, out var prevColonists))
                prevColonists = new Dictionary<string, ColonistSnapshot>();
            if (!_previousResources.TryGetValue(agentId, out var prevResources))
                prevResources = new Dictionary<string, int>();
            if (!_previousEntityIds.TryGetValue(agentId, out var prevEntities))
                prevEntities = new HashSet<string>();
            if (!_previousThreatIds.TryGetValue(agentId, out var prevThreats))
                prevThreats = new List<string>();

            // Extract current state
            var entities = EntityExtractor.Extract(map);
            var currentColonists = ColonistExtractor.Extract(map, entities);
            var currentResources = ResourceExtractor.Extract(map);
            var currentThreats = ExtractThreats(map);

            // Compute colonist deltas
            var colonistDeltas = new List<ColonistDelta>();
            var removedColonists = new List<string>();
            var currentColonistIds = new HashSet<string>();

            foreach (var colonist in currentColonists)
            {
                currentColonistIds.Add(colonist.Id);

                if (prevColonists.TryGetValue(colonist.Id, out var prev))
                {
                    var colDelta = ComputeColonistDelta(colonist, prev, config);
                    if (colDelta != null)
                    {
                        colonistDeltas.Add(colDelta);
                    }
                }
                else
                {
                    // New colonist - include full info
                    colonistDeltas.Add(new ColonistDelta
                    {
                        Id = colonist.Id,
                        Name = colonist.Name,
                        Health = colonist.Health,
                        Mood = colonist.Mood,
                        Hunger = colonist.Hunger,
                        Rest = colonist.Rest,
                        CurrentJob = colonist.CurrentJob,
                        Position = new Position2D(colonist.Position?.X ?? 0, colonist.Position?.Y ?? 0),
                        IsDrafted = colonist.IsDrafted,
                        IsDowned = colonist.IsDowned,
                        MentalState = colonist.MentalState,
                        Weapon = colonist.Weapon
                    });
                }
            }

            // Find removed colonists
            foreach (var prevId in prevColonists.Keys)
            {
                if (!currentColonistIds.Contains(prevId))
                {
                    removedColonists.Add(prevId);
                }
            }

            delta.Delta.Colonists = colonistDeltas;
            delta.Delta.RemovedColonists = removedColonists;

            // Compute resource deltas
            var resourceDeltas = new Dictionary<string, int>();
            var allResourceKeys = new HashSet<string>(prevResources.Keys);

            // Check main stockpile resources
            if (currentResources.Stockpiles != null)
            {
                foreach (var kvp in currentResources.Stockpiles)
                {
                    allResourceKeys.Add(kvp.Key);
                    var currentCount = kvp.Value;
                    var prevCount = prevResources.TryGetValue(kvp.Key, out var p) ? p : 0;

                    if (ShouldReportResourceChange(prevCount, currentCount, config.ResourcePercentThreshold))
                    {
                        resourceDeltas[kvp.Key] = currentCount;
                    }
                }
            }

            // Check for resources that went to zero
            foreach (var key in prevResources.Keys)
            {
                var currentCount = currentResources.Stockpiles?.TryGetValue(key, out var c) == true ? c : 0;
                if (currentCount == 0 && prevResources[key] > 0)
                {
                    resourceDeltas[key] = 0;
                }
            }

            delta.Delta.Resources = resourceDeltas;

            // Compute threat deltas
            var currentThreatIds = currentThreats.Select(t => $"{t.Type}:{t.Count}").ToList();
            delta.Delta.AddedThreats = currentThreats.Where(t =>
                !prevThreats.Contains($"{t.Type}:{t.Count}")).ToList();
            delta.Delta.RemovedThreats = prevThreats.Where(id =>
                !currentThreatIds.Contains(id)).ToList();

            // Compute entity deltas (simplified - just track added/removed)
            var currentEntityIds = new HashSet<string>();
            CollectEntityIds(entities, currentEntityIds);

            var addedEntities = currentEntityIds.Except(prevEntities).ToList();
            var removedEntities = prevEntities.Except(currentEntityIds).ToList();

            // Only include entity changes if significant
            if (addedEntities.Count <= 50)  // Don't overwhelm with too many entities
            {
                delta.Delta.AddedEntities = addedEntities.Select(id => new EntityRef { Id = id }).ToList();
            }
            delta.Delta.RemovedEntities = removedEntities;

            // Update snapshots for next delta
            UpdateSnapshotsFromCurrent(agentId, currentColonists, currentResources, currentEntityIds, currentThreatIds);

            return delta;
        }

        private ColonistDelta? ComputeColonistDelta(ColonistState current, ColonistSnapshot prev, DeltaConfig config)
        {
            var delta = new ColonistDelta { Id = current.Id };
            bool hasChanges = false;

            // Check health change
            if (Math.Abs(current.Health - prev.Health) >= config.HealthThreshold)
            {
                delta.Health = current.Health;
                hasChanges = true;
            }

            // Check mood change
            if (Math.Abs(current.Mood - prev.Mood) >= config.MoodThreshold)
            {
                delta.Mood = current.Mood;
                hasChanges = true;
            }

            // Check hunger change
            if (Math.Abs(current.Hunger - prev.Hunger) >= config.HungerThreshold)
            {
                delta.Hunger = current.Hunger;
                hasChanges = true;
            }

            // Check rest change
            if (Math.Abs(current.Rest - prev.Rest) >= config.RestThreshold)
            {
                delta.Rest = current.Rest;
                hasChanges = true;
            }

            // Check job change
            if (current.CurrentJob != prev.CurrentJob)
            {
                delta.CurrentJob = current.CurrentJob;
                hasChanges = true;
            }

            // Check position change
            var currentX = current.Position?.X ?? 0;
            var currentY = current.Position?.Y ?? 0;
            var posDiff = Math.Abs(currentX - prev.PositionX) + Math.Abs(currentY - prev.PositionY);

            bool shouldReportPosition = posDiff >= config.PositionThreshold;
            if (config.PositionOnlyOnJobChange)
            {
                shouldReportPosition = shouldReportPosition && (current.CurrentJob != prev.CurrentJob);
            }

            if (shouldReportPosition)
            {
                delta.Position = new Position2D(currentX, currentY);
                hasChanges = true;
            }

            // Check draft state change
            if (current.IsDrafted != prev.IsDrafted)
            {
                delta.IsDrafted = current.IsDrafted;
                hasChanges = true;
            }

            // Check downed state change
            if (current.IsDowned != prev.IsDowned)
            {
                delta.IsDowned = current.IsDowned;
                hasChanges = true;
            }

            // Check mental state change
            if (current.MentalState != prev.MentalState)
            {
                delta.MentalState = current.MentalState;
                hasChanges = true;
            }

            // Check weapon change
            if (current.Weapon != prev.Weapon)
            {
                delta.Weapon = current.Weapon;
                hasChanges = true;
            }

            return hasChanges ? delta : null;
        }

        private bool ShouldReportResourceChange(int prev, int current, float threshold)
        {
            if (prev == current) return false;
            if (threshold <= 0) return true;  // Report any change

            if (prev == 0) return true;  // New resource
            var percentChange = Math.Abs((float)(current - prev) / prev);
            return percentChange >= threshold;
        }

        private void UpdateSnapshots(string agentId, RimWorldObservation? obs)
        {
            if (obs == null) return;

            var colonistSnapshots = new Dictionary<string, ColonistSnapshot>();
            foreach (var colonist in obs.Colonists)
            {
                colonistSnapshots[colonist.Id] = new ColonistSnapshot
                {
                    Id = colonist.Id,
                    Name = colonist.Name,
                    Health = colonist.Health,
                    Mood = colonist.Mood,
                    Hunger = colonist.Hunger,
                    Rest = colonist.Rest,
                    CurrentJob = colonist.CurrentJob,
                    PositionX = colonist.Position?.X ?? 0,
                    PositionY = colonist.Position?.Y ?? 0,
                    IsDrafted = colonist.IsDrafted,
                    IsDowned = colonist.IsDowned,
                    MentalState = colonist.MentalState,
                    Weapon = colonist.Weapon
                };
            }
            _previousColonistStates[agentId] = colonistSnapshots;

            var resourceSnapshots = new Dictionary<string, int>();
            if (obs.Resources.Stockpiles != null)
            {
                foreach (var kvp in obs.Resources.Stockpiles)
                {
                    resourceSnapshots[kvp.Key] = kvp.Value;
                }
            }
            _previousResources[agentId] = resourceSnapshots;

            var entityIds = new HashSet<string>();
            CollectEntityIds(obs.Entities, entityIds);
            _previousEntityIds[agentId] = entityIds;

            _previousThreatIds[agentId] = obs.Threats.Select(t => $"{t.Type}:{t.Count}").ToList();
        }

        private void UpdateSnapshotsFromCurrent(string agentId, List<ColonistState> colonists,
            ResourceState resources, HashSet<string> entityIds, List<string> threatIds)
        {
            var colonistSnapshots = new Dictionary<string, ColonistSnapshot>();
            foreach (var colonist in colonists)
            {
                colonistSnapshots[colonist.Id] = new ColonistSnapshot
                {
                    Id = colonist.Id,
                    Name = colonist.Name,
                    Health = colonist.Health,
                    Mood = colonist.Mood,
                    Hunger = colonist.Hunger,
                    Rest = colonist.Rest,
                    CurrentJob = colonist.CurrentJob,
                    PositionX = colonist.Position?.X ?? 0,
                    PositionY = colonist.Position?.Y ?? 0,
                    IsDrafted = colonist.IsDrafted,
                    IsDowned = colonist.IsDowned,
                    MentalState = colonist.MentalState,
                    Weapon = colonist.Weapon
                };
            }
            _previousColonistStates[agentId] = colonistSnapshots;

            var resourceSnapshots = new Dictionary<string, int>();
            if (resources.Stockpiles != null)
            {
                foreach (var kvp in resources.Stockpiles)
                {
                    resourceSnapshots[kvp.Key] = kvp.Value;
                }
            }
            _previousResources[agentId] = resourceSnapshots;

            _previousEntityIds[agentId] = entityIds;
            _previousThreatIds[agentId] = threatIds;
        }

        private void CollectEntityIds(EntityIndex entities, HashSet<string> ids)
        {
            if (entities.Animals != null)
                foreach (var e in entities.Animals) ids.Add(e.Id);
            if (entities.Items != null)
                foreach (var e in entities.Items) ids.Add(e.Id);
            if (entities.Buildings != null)
                foreach (var e in entities.Buildings) ids.Add(e.Id);
            if (entities.Colonists != null)
                foreach (var e in entities.Colonists) ids.Add(e.Id);
            if (entities.Visitors != null)
                foreach (var e in entities.Visitors) ids.Add(e.Id);
            if (entities.Hostiles != null)
                foreach (var e in entities.Hostiles) ids.Add(e.Id);
            if (entities.Weapons != null)
                foreach (var e in entities.Weapons) ids.Add(e.Id);
        }

        private List<GameEvent> ConvertToObservationEvents(List<global::GameRL.Harmony.Protocol.GameEvent> internalEvents)
        {
            return internalEvents.Select(e => new GameEvent
            {
                Type = e.EventType,
                Message = e.Details?.ToString() ?? e.EventType,
                Tick = e.Tick
            }).ToList();
        }

        /// <summary>
        /// Clear agent state snapshots (called on reset or deregister)
        /// </summary>
        public void ClearAgentState(string agentId)
        {
            _previousColonistStates.Remove(agentId);
            _previousResources.Remove(agentId);
            _previousEntityIds.Remove(agentId);
            _previousThreatIds.Remove(agentId);
        }

        private List<VisitorState> ExtractVisitors(Map? map)
        {
            var visitors = new List<VisitorState>();
            if (map == null) return visitors;

            // Snapshot to avoid collection modification during iteration
            List<Pawn> pawnSnapshot;
            try
            {
                pawnSnapshot = map.mapPawns.AllPawnsSpawned.ToList();
            }
            catch
            {
                return visitors;
            }

            // Get all non-hostile, non-colonist humanlike pawns on the map
            foreach (var pawn in pawnSnapshot)
            {
                if (pawn == null || pawn.Destroyed || !pawn.Spawned) continue;

                try
                {
                    if (pawn.RaceProps.Humanlike
                        && pawn.Faction != null
                        && pawn.Faction != Faction.OfPlayer
                        && !pawn.HostileTo(Faction.OfPlayer)
                        && !pawn.IsPrisoner)
                    {
                        var factionRelation = pawn.Faction?.RelationKindWith(Faction.OfPlayer);
                        visitors.Add(new VisitorState
                        {
                            Id = pawn.ThingID,
                            Name = pawn.LabelShort,
                            Position = new float[] { pawn.Position.x, pawn.Position.z },
                            Faction = pawn.Faction?.Name,
                            Relation = factionRelation?.ToString(),
                            Health = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f
                        });
                    }
                }
                catch
                {
                    // Skip pawns that throw during extraction
                }
            }

            return visitors;
        }

        private struct AlertInfo
        {
            public string Label;
            public byte Severity;  // 1=Normal, 2=High, 3=Critical
        }

        private static byte MapAlertSeverity(Alert alert)
        {
            try
            {
                return alert.Priority switch
                {
                    AlertPriority.Critical => 3,
                    AlertPriority.High => 2,
                    _ => 1
                };
            }
            catch { return 1; }
        }

        private List<AlertInfo> ExtractAlertsWithSeverity()
        {
            var alerts = new List<AlertInfo>();
            try
            {
                var uiRoot = Find.UIRoot as UIRoot_Play;
                var alertsReadout = uiRoot?.alerts;
                if (alertsReadout == null) return alerts;

                List<Alert>? activeAlerts = null;
                var bindingFlags = System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance;

                // Use cached field name if previously discovered
                if (_discoveredAlertFieldName != null)
                {
                    var field = typeof(AlertsReadout).GetField(_discoveredAlertFieldName, bindingFlags);
                    if (field?.GetValue(alertsReadout) is List<Alert> found)
                        activeAlerts = found;
                    else
                    {
                        var prop = typeof(AlertsReadout).GetProperty(_discoveredAlertFieldName, bindingFlags);
                        if (prop?.GetValue(alertsReadout) is List<Alert> foundProp)
                            activeAlerts = foundProp;
                    }
                }

                // Try multiple field/property names across RimWorld versions
                if (activeAlerts == null)
                {
                    foreach (var fieldName in new[] { "activeAlerts", "curAlerts", "AllActiveAlerts", "AllAlerts", "AlertsListForReading" })
                    {
                        var field = typeof(AlertsReadout).GetField(fieldName, bindingFlags);
                        if (field?.GetValue(alertsReadout) is List<Alert> found)
                        {
                            activeAlerts = found;
                            if (!_alertFieldDiscovered)
                            {
                                _discoveredAlertFieldName = fieldName;
                                _alertFieldDiscovered = true;
                                Log.Message($"[GameRL] Alert field discovered: {fieldName}");
                            }
                            break;
                        }
                        var prop = typeof(AlertsReadout).GetProperty(fieldName, bindingFlags);
                        if (prop?.GetValue(alertsReadout) is List<Alert> foundProp)
                        {
                            activeAlerts = foundProp;
                            if (!_alertFieldDiscovered)
                            {
                                _discoveredAlertFieldName = fieldName;
                                _alertFieldDiscovered = true;
                                Log.Message($"[GameRL] Alert property discovered: {fieldName}");
                            }
                            break;
                        }
                    }
                }

                // Fallback: scan all fields
                if (activeAlerts == null)
                {
                    var fields = typeof(AlertsReadout).GetFields(bindingFlags);
                    foreach (var f in fields)
                    {
                        if (f.GetValue(alertsReadout) is List<Alert> found)
                        {
                            activeAlerts = found;
                            if (!_alertFieldDiscovered)
                            {
                                _discoveredAlertFieldName = f.Name;
                                _alertFieldDiscovered = true;
                                Log.Message($"[GameRL] Alert field discovered via scan: {f.Name}");
                            }
                            break;
                        }
                    }
                }

                if (!_alertFieldDiscovered)
                {
                    _alertFieldDiscovered = true;
                    var allFields = typeof(AlertsReadout).GetFields(bindingFlags);
                    Log.Warning($"[GameRL] Could not find alerts field. Available fields: {string.Join(", ", allFields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                }

                if (activeAlerts != null)
                {
                    // Filter for active alerts only — AllAlerts contains inactive alerts too
                    var alertSnapshot = activeAlerts.ToList();

                    foreach (var alert in alertSnapshot)
                    {
                        if (alert == null) continue;
                        try
                        {
                            if (!alert.Active) continue;
                            var label = alert.GetLabel();
                            if (!string.IsNullOrEmpty(label))
                            {
                                alerts.Add(new AlertInfo { Label = label, Severity = MapAlertSeverity(alert) });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to extract alerts: {ex.Message}");
            }
            return alerts;
        }

        private List<AlertState> ExtractAlerts()
        {
            var alerts = ExtractAlertsWithSeverity()
                .Select(a => new AlertState { Label = a.Label, Severity = a.Severity })
                .ToList();

            // Synthetic "UnderAttack" Severity 3 alert when hostiles are on the map
            try
            {
                var map = Find.CurrentMap;
                if (map != null)
                {
                    var hostileCount = map.mapPawns.AllPawnsSpawned
                        .Count(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer));
                    if (hostileCount > 0 && !alerts.Any(a => a.Label == "UnderAttack"))
                    {
                        alerts.Insert(0, new AlertState { Label = "UnderAttack", Severity = 3 });
                    }
                }
            }
            catch { }

            return alerts;
        }

        /// <summary>
        /// Check for alert changes and record events for new/resolved alerts.
        /// </summary>
        public void CheckAlertChanges()
        {
            try
            {
                var currentAlerts = ExtractAlertsWithSeverity();
                var currentByLabel = new Dictionary<string, byte>();
                foreach (var a in currentAlerts)
                    currentByLabel[a.Label] = a.Severity;

                foreach (var alert in currentAlerts)
                {
                    if (!_previousAlerts.ContainsKey(alert.Label))
                    {
                        RecordEvent($"AlertAppeared:{alert.Label}", alert.Severity, new
                        {
                            Alert = alert.Label,
                            Severity = (int)alert.Severity
                        });
                        if (alert.Severity >= 2)
                            HasUrgentEvents = true;
                    }
                }

                foreach (var prev in _previousAlerts)
                {
                    if (!currentByLabel.ContainsKey(prev.Key))
                    {
                        RecordEvent($"AlertResolved:{prev.Key}", 1, new
                        {
                            Alert = prev.Key
                        });
                    }
                }

                _previousAlerts = currentByLabel;
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to check alert changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Call every tick to check alerts at the configured interval.
        /// </summary>
        public void TickAlertCheck()
        {
            _ticksSinceLastAlertCheck++;
            if (_ticksSinceLastAlertCheck >= AlertCheckIntervalTicks)
            {
                _ticksSinceLastAlertCheck = 0;
                CheckAlertChanges();
            }
        }

        /// <summary>
        /// Returns the last-known active alerts with severity (cached, no re-extraction).
        /// </summary>
        public List<AlertState> GetCurrentAlerts()
        {
            return _previousAlerts.Select(kv => new AlertState { Label = kv.Key, Severity = kv.Value }).ToList();
        }

        /// <summary>
        /// Clear alert tracking state (called on reset).
        /// </summary>
        public void ClearAlertState()
        {
            _previousAlerts.Clear();
            _ticksSinceLastAlertCheck = 0;
        }

        private int CountIdleColonists(Map? map)
        {
            if (map == null) return 0;

            try
            {
                // Take snapshot to avoid collection modification
                var colonists = map.mapPawns.FreeColonists.ToList();
                return colonists.Count(p => p != null && !p.Destroyed && !p.Downed && !p.InMentalState && p.CurJob?.def == JobDefOf.Wait_Wander);
            }
            catch
            {
                return 0;
            }
        }

        internal List<ThreatInfo> ExtractThreats(Map? map)
        {
            var threats = new List<ThreatInfo>();
            if (map == null) return threats;

            try
            {
                // Hostile pawns — split into raiders vs manhunters
                var pawnSnapshot = map.mapPawns.AllPawnsSpawned.ToList();
                var hostilePawns = pawnSnapshot
                    .Where(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer))
                    .ToList();

                var manhunters = hostilePawns
                    .Where(p => p.MentalStateDef?.defName?.Contains("Manhunter") == true)
                    .ToList();
                var raiders = hostilePawns.Except(manhunters).ToList();

                if (raiders.Count > 0)
                {
                    // Identify faction(s)
                    var factions = raiders
                        .Where(p => p.Faction != null)
                        .Select(p => p.Faction.Name)
                        .Distinct()
                        .ToList();
                    string factionStr = factions.Count > 0 ? string.Join(", ", factions) : "Unknown";

                    threats.Add(new ThreatInfo
                    {
                        Type = "hostile_pawns",
                        SubType = raiders.Any(p => p.RaceProps?.IsMechanoid == true) ? "Mechanoid"
                            : raiders.Any(p => p.RaceProps?.Animal == true) ? "Animal"
                            : "Humanlike",
                        Faction = factionStr,
                        Severity = raiders.Count > 10 ? 3 : raiders.Count > 5 ? 2 : 1,
                        Count = raiders.Count,
                        Position = Centroid(raiders)
                    });
                }

                if (manhunters.Count > 0)
                {
                    threats.Add(new ThreatInfo
                    {
                        Type = "manhunter",
                        SubType = manhunters.First().def?.defName,
                        Faction = "Wildlife",
                        Severity = manhunters.Count > 5 ? 3 : manhunters.Count > 2 ? 2 : 1,
                        Count = manhunters.Count,
                        Position = Centroid(manhunters)
                    });
                }

                // Mental breaks among colonists
                var mentalBreakPawns = map.mapPawns.FreeColonists?
                    .Where(p => p != null && !p.Destroyed && p.InMentalState)
                    .ToList();
                if (mentalBreakPawns != null)
                {
                    foreach (var p in mentalBreakPawns)
                    {
                        threats.Add(new ThreatInfo
                        {
                            Type = "mental_break",
                            SubType = p.MentalStateDef?.defName,
                            Severity = 2,
                            Count = 1,
                            Position = new float[] { p.Position.x, p.Position.z }
                        });
                    }
                }

                // Fire
                var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
                var fireCount = fires?.Count ?? 0;
                if (fireCount > 0)
                {
                    threats.Add(new ThreatInfo
                    {
                        Type = "fire",
                        Severity = fireCount > 20 ? 3 : fireCount > 5 ? 2 : 1,
                        Count = fireCount,
                        Position = Centroid(fires)
                    });
                }

                // Blight on crops
                var blights = map.listerThings.ThingsOfDef(ThingDefOf.Blight);
                var blightCount = blights?.Count ?? 0;
                if (blightCount > 0)
                {
                    threats.Add(new ThreatInfo
                    {
                        Type = "blight",
                        Severity = blightCount > 20 ? 2 : 1,
                        Count = blightCount,
                        Position = Centroid(blights)
                    });
                }

                // Active game conditions (toxic fallout, psychic drone, solar flare, etc.)
                if (map.gameConditionManager != null)
                {
                    foreach (var condition in map.gameConditionManager.ActiveConditions)
                    {
                        if (condition?.def == null) continue;
                        var defName = condition.def.defName;

                        int severity = 1;
                        if (defName.Contains("ToxicFallout") || defName.Contains("ToxicSpewer"))
                            severity = 3;
                        else if (defName.Contains("PsychicDrone") || defName.Contains("PsychicSuppression"))
                            severity = 2;
                        else if (defName.Contains("SolarFlare"))
                            severity = 2;
                        else if (defName.Contains("VolcanicWinter") || defName.Contains("ColdSnap") || defName.Contains("HeatWave"))
                            severity = 2;

                        threats.Add(new ThreatInfo
                        {
                            Type = defName,
                            Severity = severity,
                            Count = 1
                        });
                    }
                }

                // Mech clusters (Royalty DLC)
                var mechClusterDef = DefDatabase<ThingDef>.GetNamed("MechCluster", errorOnFail: false);
                if (mechClusterDef != null)
                {
                    var mechs = map.listerThings.ThingsOfDef(mechClusterDef);
                    var mechCount = mechs?.Count ?? 0;
                    if (mechCount > 0)
                    {
                        threats.Add(new ThreatInfo
                        {
                            Type = "mech_cluster",
                            Severity = 3,
                            Count = mechCount,
                            Position = Centroid(mechs)
                        });
                    }
                }

                // Insect hives
                var hiveDef = DefDatabase<ThingDef>.GetNamed("Hive", errorOnFail: false);
                if (hiveDef != null)
                {
                    var hives = map.listerThings.ThingsOfDef(hiveDef);
                    var hiveCount = hives?.Count ?? 0;
                    if (hiveCount > 0)
                    {
                        threats.Add(new ThreatInfo
                        {
                            Type = "infestation",
                            Severity = hiveCount > 3 ? 3 : 2,
                            Count = hiveCount,
                            Position = Centroid(hives)
                        });
                    }
                }
            }
            catch
            {
                // Return partial threats on error
            }

            return threats;
        }

        private static float[]? Centroid(IEnumerable<Thing>? things)
        {
            if (things == null) return null;
            float sumX = 0, sumZ = 0;
            int count = 0;
            foreach (var t in things)
            {
                if (t == null) continue;
                sumX += t.Position.x;
                sumZ += t.Position.z;
                count++;
            }
            if (count == 0) return null;
            return new float[] { sumX / count, sumZ / count };
        }

        private ResearchInfo ExtractResearch()
        {
            var info = new ResearchInfo();
            try
            {
                var manager = Find.ResearchManager;
                if (manager == null) return info;

                // Get current project via reflection (API varies by RimWorld version)
                ResearchProjectDef? currentProj = null;
                var field = typeof(ResearchManager).GetField("currentProj",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                    currentProj = field.GetValue(manager) as ResearchProjectDef;
                else
                {
                    var prop = typeof(ResearchManager).GetProperty("CurrentProject");
                    if (prop != null)
                        currentProj = prop.GetValue(manager) as ResearchProjectDef;
                }

                if (currentProj != null)
                {
                    info.CurrentProject = currentProj.defName;
                    info.CurrentProjectLabel = currentProj.label;
                    info.Progress = currentProj.ProgressPercent;
                }

                // Available research (cap at 30 for performance)
                int count = 0;
                foreach (var proj in DefDatabase<ResearchProjectDef>.AllDefs)
                {
                    if (proj.IsFinished) continue;
                    if (count >= 30) break;

                    var missingPrereqs = new List<string>();
                    bool canStart = true;

                    if (proj.prerequisites != null)
                    {
                        foreach (var prereq in proj.prerequisites)
                        {
                            if (!prereq.IsFinished)
                            {
                                missingPrereqs.Add(prereq.defName);
                                canStart = false;
                            }
                        }
                    }

                    info.Available.Add(new ResearchOption
                    {
                        DefName = proj.defName,
                        Label = proj.label,
                        Cost = proj.baseCost,
                        MissingPrereqs = missingPrereqs,
                        CanStart = canStart
                    });
                    count++;
                }
            }
            catch
            {
                // Return partial results
            }
            return info;
        }

        /// <summary>
        /// Compute which actions are currently valid based on game state.
        /// Returns action names from the action space. Must be fast (&lt;5ms).
        /// </summary>
        private List<string> ComputeValidActions(Map? map)
        {
            var valid = new List<string>();

            // Always valid: meta/game actions
            valid.Add("SetSpeed");
            valid.Add("Unpause");
            valid.Add("RequestFullState");
            valid.Add("ListWorkbenches");
            valid.Add("ListBuildables");
            valid.Add("SaveCheckpoint");
            valid.Add("LoadCheckpoint");
            valid.Add("DismissLetters");

            if (map == null) return valid;

            List<Pawn> colonists;
            try
            {
                colonists = map.mapPawns.FreeColonists.ToList();
            }
            catch
            {
                return valid;
            }

            if (colonists.Count == 0) return valid;

            bool hasDraftedColonist = colonists.Any(p => p.Drafted);
            bool hasDraftableColonist = colonists.Any(p => p.drafter != null && !p.Downed && !p.InMentalState && !p.Drafted);
            bool hasNonDownedColonist = colonists.Any(p => !p.Downed);

            // Pawn control
            if (hasDraftableColonist) valid.Add("Draft");
            if (hasDraftedColonist)
            {
                valid.Add("Undraft");
                valid.Add("Move");
                valid.Add("MoveToEntity");
            }
            if (hasNonDownedColonist)
            {
                valid.Add("SetWorkPriority");
                valid.Add("SetMedicalCare");
                valid.Add("Haul");
                valid.Add("Equip");
            }

            // Combat: DefendColony (auto-draft + position) when hostiles present
            // Attack requires drafted pawn AND hostile targets
            try
            {
                var hasHostiles = map.mapPawns.AllPawnsSpawned
                    .Any(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer));
                if (hasHostiles)
                {
                    valid.Add("DefendColony");
                    if (hasDraftedColonist) valid.Add("Attack");
                }
            }
            catch { }

            // Arrest requires drafted pawn AND non-hostile humanlike non-colonist on map
            if (hasDraftedColonist)
            {
                try
                {
                    var hasArrestable = map.mapPawns.AllPawnsSpawned
                        .Any(p => p != null && !p.Destroyed && p.Spawned && p.RaceProps.Humanlike
                            && p.Faction != Faction.OfPlayer && !p.HostileTo(Faction.OfPlayer)
                            && !p.IsPrisoner);
                    if (hasArrestable) valid.Add("Arrest");
                }
                catch { }
            }

            // Animals on map — hunting and taming
            try
            {
                var spawnedAnimals = map.mapPawns.AllPawnsSpawned
                    .Where(p => p != null && !p.Destroyed && p.Spawned && p.RaceProps.Animal).ToList();
                if (spawnedAnimals.Count > 0)
                {
                    valid.Add("DesignateHunt");
                    valid.Add("CancelHunt");
                    // Taming requires wild (non-player) animals
                    if (spawnedAnimals.Any(p => p.Faction != Faction.OfPlayer))
                        valid.Add("DesignateTame");
                    // Training/area/slaughter require tamed animals
                    if (spawnedAnimals.Any(p => p.Faction == Faction.OfPlayer))
                    {
                        valid.Add("SetAnimalTraining");
                        valid.Add("SetAnimalArea");
                        valid.Add("DesignateSlaughter");
                    }
                }
            }
            catch { }

            // Chat requires 2+ non-downed colonists
            if (colonists.Count(p => !p.Downed) >= 2) valid.Add("Chat");

            // Intent-based spatial actions (game resolves coordinates)
            valid.Add("PlaceBuildingNear");
            valid.Add("EstablishStorage");
            // Only advertise EstablishFarm if fertile soil exists
            try
            {
                bool hasFertileSoil = map.AllCells.Any(c => c.GetFertility(map) > 0
                    && map.zoneManager.ZoneAt(c) == null);
                if (hasFertileSoil) valid.Add("EstablishFarm");
            }
            catch { valid.Add("EstablishFarm"); } // fallback: always advertise
            // Only advertise mining if mineable rocks exist
            try
            {
                bool hasMineable = map.listerThings.AllThings.Any(t => t.def.mineable);
                if (hasMineable) valid.Add("DesignateMiningNear");
            }
            catch { valid.Add("DesignateMiningNear"); }
            // Only advertise clearing if trees exist
            try
            {
                bool hasTrees = map.listerThings.AllThings.Any(t => t.def.plant?.IsTree == true);
                if (hasTrees) valid.Add("DesignateClearNear");
            }
            catch { valid.Add("DesignateClearNear"); }

            // Bill management (requires workbenches)
            try
            {
                var hasWorkbenches = map.listerBuildings.allBuildingsColonist
                    .Any(b => b is IBillGiver);
                if (hasWorkbenches)
                {
                    valid.Add("AddBill");
                    valid.Add("CancelBill");
                    valid.Add("ModifyBill");
                }
            }
            catch { }

            // Unforbid (always available when map exists — per-item check too expensive)
            valid.Add("Unforbid");
            valid.Add("UnforbidByType");
            valid.Add("UnforbidArea");

            // Research
            try
            {
                if (Find.ResearchManager != null)
                {
                    var hasAvailable = DefDatabase<ResearchProjectDef>.AllDefs
                        .Any(p => !p.IsFinished && (p.prerequisites == null || p.prerequisites.All(pr => pr.IsFinished)));
                    if (hasAvailable) valid.Add("SelectResearch");
                }
            }
            catch { }

            // Zone management (always available when zones exist)
            try
            {
                var hasZones = map.zoneManager.AllZones.Count > 0;
                if (hasZones)
                {
                    valid.Add("DeleteZone");
                    if (map.zoneManager.AllZones.Any(z => z is Zone_Stockpile))
                        valid.Add("SetStockpilePriority");
                    if (map.zoneManager.AllZones.Any(z => z is Zone_Growing))
                        valid.Add("SetGrowingPlant");
                }
            }
            catch { }

            // Prisoner interaction
            try
            {
                var hasPrisoners = map.mapPawns.PrisonersOfColony?.Any() ?? false;
                if (hasPrisoners) valid.Add("SetPrisonerInteraction");
            }
            catch { }

            // Medical actions
            if (hasNonDownedColonist)
            {
                // Rescue requires a downed pawn
                try
                {
                    var hasDownedPawn = colonists.Any(p => p.Downed)
                        || map.mapPawns.AllPawnsSpawned.Any(p => p != null && p.Downed && !p.HostileTo(Faction.OfPlayer));
                    if (hasDownedPawn) valid.Add("Rescue");
                }
                catch { }

                // TendTo requires a pawn with tendable conditions
                try
                {
                    var hasTendable = map.mapPawns.AllPawnsSpawned
                        .Any(p => p != null && !p.Destroyed && p.Faction == Faction.OfPlayer
                            && p.health?.hediffSet?.hediffs?.Any(h => h.TendableNow()) == true);
                    if (hasTendable) valid.Add("TendTo");
                }
                catch { }

                // Surgery
                valid.Add("OperateSurgery");
            }

            // Schedule and bed management
            if (hasNonDownedColonist)
            {
                valid.Add("SetSchedule");
                try
                {
                    var hasBeds = map.listerBuildings.allBuildingsColonist
                        .Any(b => b is Building_Bed bed && !bed.ForPrisoners && !bed.Medical);
                    if (hasBeds) valid.Add("AssignBed");
                }
                catch { }
            }

            // Deconstruct (requires any deconstructible building on map, not just colonist-owned)
            try
            {
                var hasDeconstructible = map.listerBuildings.allBuildingsColonist.Count > 0
                    || map.listerBuildings.allBuildingsNonColonist.Any(b => b.DeconstructibleBy(Faction.OfPlayer));
                if (hasDeconstructible) valid.Add("Deconstruct");
            }
            catch { }

            // Repair (requires damaged buildings and non-downed colonist)
            if (hasNonDownedColonist)
            {
                try
                {
                    var hasDamaged = map.listerBuildings.allBuildingsColonist
                        .Any(b => b.HitPoints < b.MaxHitPoints);
                    if (hasDamaged) valid.Add("DesignateRepair");
                }
                catch { }
            }

            // SmoothFloor (always valid when map exists)
            valid.Add("SmoothFloor");

            // Trade (requires active traders and silver)
            try
            {
                var hasTraders = map.mapPawns.AllPawnsSpawned
                    .Any(p => p != null && !p.Destroyed && p.Spawned && p.TraderKind != null && p.Faction != Faction.OfPlayer)
                    || (map.passingShipManager?.passingShips?.Any(s => s is TradeShip) ?? false);
                if (hasTraders)
                {
                    var hasSilver = map.resourceCounter.GetCount(ThingDefOf.Silver) > 0;
                    if (hasSilver) valid.Add("Trade");
                }
            }
            catch { }

            // FormCaravan (requires at least 1 non-downed colonist)
            if (colonists.Any(p => !p.Downed))
            {
                valid.Add("FormCaravan");
            }

            return valid;
        }

        private List<ZoneInfo> ExtractZones(Map? map)
        {
            var zones = new List<ZoneInfo>();
            if (map?.zoneManager == null) return zones;

            try
            {
                foreach (var zone in map.zoneManager.AllZones)
                {
                    if (zone == null) continue;

                    var info = new ZoneInfo
                    {
                        Label = zone.label ?? "",
                        CellCount = zone.Cells.Count
                    };

                    // Compute center from cells
                    if (zone.Cells.Count > 0)
                    {
                        var avgX = (int)zone.Cells.Average(c => c.x);
                        var avgZ = (int)zone.Cells.Average(c => c.z);
                        info.Center = new Position2D(avgX, avgZ);
                    }

                    if (zone is Zone_Stockpile stockpile)
                    {
                        info.Type = "Stockpile";
                        info.Priority = (int)stockpile.settings.Priority;
                    }
                    else if (zone is Zone_Growing growing)
                    {
                        info.Type = "Growing";
                        // Repair zones with null plant def (prevents NullReferenceException in WorkGiver_GrowerSow)
                        if (growing.GetPlantDefToGrow() == null)
                        {
                            var fallback = DefDatabase<ThingDef>.GetNamed("Plant_Rice", errorOnFail: false)
                                ?? DefDatabase<ThingDef>.GetNamed("Plant_Potato", errorOnFail: false);
                            if (fallback != null) growing.SetPlantDefToGrow(fallback);
                        }
                        info.PlantType = growing.GetPlantDefToGrow()?.defName;
                    }
                    else
                    {
                        info.Type = zone.GetType().Name.Replace("Zone_", "");
                    }

                    zones.Add(info);
                }
            }
            catch
            {
                // Return partial results
            }
            return zones;
        }

        private List<FactionRelationInfo> ExtractFactionRelations()
        {
            var relations = new List<FactionRelationInfo>();
            try
            {
                var playerFaction = Faction.OfPlayer;
                if (playerFaction == null) return relations;

                foreach (var faction in Find.FactionManager.AllFactionsVisibleInViewOrder)
                {
                    if (faction == null || faction == playerFaction || faction.Hidden) continue;

                    var rel = faction.RelationWith(playerFaction, allowNull: true);
                    relations.Add(new FactionRelationInfo
                    {
                        Name = faction.Name,
                        Relation = faction.RelationKindWith(playerFaction).ToString(),
                        Goodwill = rel?.baseGoodwill ?? 0,
                        CanTrade = !faction.HostileTo(playerFaction)
                    });
                }
            }
            catch
            {
                // Return partial results
            }
            return relations;
        }

        private List<PrisonerInfo> ExtractPrisoners(Map? map)
        {
            var prisoners = new List<PrisonerInfo>();
            if (map == null) return prisoners;

            try
            {
                var prisonerPawns = map.mapPawns.PrisonersOfColony?.ToList();
                if (prisonerPawns == null) return prisoners;

                foreach (var pawn in prisonerPawns)
                {
                    if (pawn == null || pawn.Destroyed) continue;

                    // Use reflection for API-version-varying fields
                    string? interactionMode = null;
                    try
                    {
                        var modeProp = typeof(Pawn_GuestTracker).GetProperty("interactionMode")
                            ?? typeof(Pawn_GuestTracker).GetProperty("ExclusiveInteractionMode");
                        if (modeProp != null)
                        {
                            var modeVal = modeProp.GetValue(pawn.guest);
                            interactionMode = modeVal?.ToString();
                        }
                    }
                    catch { }

                    float recruitDifficulty = 0.5f;
                    try
                    {
                        var diffMethod = typeof(Pawn).GetMethod("RecruitDifficulty");
                        if (diffMethod != null)
                            recruitDifficulty = (float)(diffMethod.Invoke(pawn, new object[] { Faction.OfPlayer }) ?? 0.5f);
                    }
                    catch { }

                    prisoners.Add(new PrisonerInfo
                    {
                        Id = pawn.ThingID,
                        Name = pawn.Name?.ToStringShort ?? "Unknown",
                        Health = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                        InteractionMode = interactionMode,
                        RecruitDifficulty = recruitDifficulty,
                        Mood = pawn.needs?.mood?.CurLevelPercentage ?? 0.5f,
                        Resistance = pawn.guest?.resistance ?? 0f
                    });
                }
            }
            catch
            {
                // Return partial results
            }
            return prisoners;
        }

        private List<TraderInfo> ExtractTraders(Map? map)
        {
            var traders = new List<TraderInfo>();
            if (map == null) return traders;

            try
            {
                // Orbital traders
                var passingShips = map.passingShipManager?.passingShips;
                if (passingShips != null)
                {
                    foreach (var ship in passingShips)
                    {
                        if (ship == null) continue;
                        // TradeShip is the tradeable subclass
                        if (ship is TradeShip tradeShip)
                        {
                            int silver = 0;
                            try
                            {
                                var goods = tradeShip.Goods;
                                if (goods != null)
                                {
                                    foreach (var thing in goods)
                                    {
                                        if (thing?.def == ThingDefOf.Silver)
                                            silver += thing.stackCount;
                                    }
                                }
                            }
                            catch { }

                            traders.Add(new TraderInfo
                            {
                                Name = tradeShip.name ?? "Unknown",
                                Type = "Orbital",
                                Faction = tradeShip.Faction?.Name,
                                Silver = silver
                            });
                        }
                    }
                }

                // Visitor traders on map (pawns with TraderKind)
                var pawnSnapshot = map.mapPawns.AllPawnsSpawned.ToList();
                foreach (var pawn in pawnSnapshot)
                {
                    if (pawn == null || pawn.Destroyed || !pawn.Spawned) continue;
                    if (pawn.TraderKind == null) continue;
                    if (pawn.Faction == Faction.OfPlayer) continue;

                    var traderName = pawn.LabelShort;
                    if (!traders.Any(t => t.Name == traderName))
                    {
                        int silver = 0;
                        try
                        {
                            var goods = pawn.trader?.Goods;
                            if (goods != null)
                            {
                                foreach (var thing in goods)
                                {
                                    if (thing?.def == ThingDefOf.Silver)
                                        silver += thing.stackCount;
                                }
                            }
                        }
                        catch { }

                        traders.Add(new TraderInfo
                        {
                            Name = traderName,
                            Type = "Visitor",
                            Faction = pawn.Faction?.Name,
                            Silver = silver
                        });
                    }
                }
            }
            catch
            {
                // Return partial results
            }
            return traders;
        }

        private TerrainSummary? ExtractTerrainSummary(Map? map)
        {
            if (map == null) return null;
            try
            {
                var summary = new TerrainSummary();
                var fertileByType = new Dictionary<string, List<IntVec3>>();

                // Scan map for fertile cells (fertility > 0)
                var mapGrid = map.terrainGrid;
                var roofGrid = map.roofGrid;
                for (int x = 0; x < map.Size.x; x++)
                {
                    for (int z = 0; z < map.Size.z; z++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        var terrain = mapGrid.TerrainAt(cell);
                        if (terrain == null || terrain.fertility <= 0f) continue;

                        // Skip cells with buildings on them
                        if (cell.GetEdifice(map) != null) continue;

                        var key = terrain.defName;
                        if (!fertileByType.ContainsKey(key))
                            fertileByType[key] = new List<IntVec3>();
                        fertileByType[key].Add(cell);
                    }
                }

                // Build regions per terrain type using spatial clustering
                foreach (var kvp in fertileByType)
                {
                    var terrainDef = DefDatabase<TerrainDef>.GetNamed(kvp.Key, errorOnFail: false);
                    float fertility = terrainDef?.fertility ?? 1f;
                    var cells = kvp.Value;

                    summary.TotalFertileCells += cells.Count;
                    if (kvp.Key == "SoilRich")
                        summary.TotalRichSoilCells = cells.Count;

                    // Cluster cells into regions using simple grid bucketing (16x16 chunks)
                    var buckets = new Dictionary<(int, int), List<IntVec3>>();
                    foreach (var cell in cells)
                    {
                        var bucket = (cell.x / 16, cell.z / 16);
                        if (!buckets.ContainsKey(bucket))
                            buckets[bucket] = new List<IntVec3>();
                        buckets[bucket].Add(cell);
                    }

                    foreach (var bucket in buckets.Values)
                    {
                        if (bucket.Count < 4) continue; // Skip tiny patches

                        var minX = bucket.Min(c => c.x);
                        var maxX = bucket.Max(c => c.x);
                        var minZ = bucket.Min(c => c.z);
                        var maxZ = bucket.Max(c => c.z);
                        var centerX = (minX + maxX) / 2;
                        var centerZ = (minZ + maxZ) / 2;

                        // Check if mostly roofed
                        int roofedCount = 0;
                        foreach (var c in bucket)
                        {
                            if (roofGrid.Roofed(c)) roofedCount++;
                        }

                        summary.FertileRegions.Add(new FertileRegion
                        {
                            TerrainType = kvp.Key,
                            Fertility = fertility,
                            CellCount = bucket.Count,
                            Center = new Position2D(centerX, centerZ),
                            MinX = minX,
                            MaxX = maxX,
                            MinY = minZ,
                            MaxY = maxZ,
                            Roofed = roofedCount > bucket.Count / 2
                        });
                    }
                }

                // Sort by fertility descending, then by cell count
                summary.FertileRegions.Sort((a, b) =>
                {
                    int cmp = b.Fertility.CompareTo(a.Fertility);
                    return cmp != 0 ? cmp : b.CellCount.CompareTo(a.CellCount);
                });

                return summary;
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to extract terrain summary: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract a curated list of spatial anchors the agent can reference in Near parameters.
        /// </summary>
        private static List<object> ExtractLandmarks(Map? map)
        {
            var landmarks = new List<object>();
            if (map == null) return landmarks;

            try
            {
                // Guaranteed minimum set (spec draft-02 REQ-SPA-04):
                // colony centroid, map center, eight compass regions, fertile clusters.
                var colonyCenter = Actions.SpatialAnchors.GetColonyCenter(map);
                landmarks.Add(new { Id = "ColonyCenter", Name = "ColonyCenter", Kind = "Centroid", Type = "Centroid", X = colonyCenter.x, Y = colonyCenter.z });
                landmarks.Add(new { Id = "MapCenter", Name = "MapCenter", Kind = "Centroid", Type = "MapCenter", X = map.Center.x, Y = map.Center.z });

                foreach (var region in Actions.SpatialAnchors.GetRegionCentroids(map))
                {
                    landmarks.Add(new { Id = region.Key, Name = region.Key, Kind = "Region", Type = "Region", X = region.Value.x, Y = region.Value.z });
                }

                foreach (var cluster in Actions.SpatialAnchors.GetFertileClusters(map).Take(5))
                {
                    landmarks.Add(new
                    {
                        Id = cluster.Id,
                        Name = cluster.Id,
                        Kind = "Cluster",
                        Type = "FertileCluster",
                        X = cluster.Center.x,
                        Y = cluster.Center.z,
                        CellCount = cluster.CellCount,
                        Fertility = cluster.Fertility
                    });
                }

                // Named zones (stockpiles, growing zones)
                foreach (var zone in map.zoneManager.AllZones.Take(10))
                {
                    var cells = zone.Cells.ToList();
                    if (cells.Count == 0) continue;
                    int cx = (int)cells.Average(c => c.x);
                    int cz = (int)cells.Average(c => c.z);
                    string zoneType = zone is Zone_Stockpile ? "Stockpile"
                        : zone is Zone_Growing ? "Farm"
                        : "Zone";
                    landmarks.Add(new { Name = zone.label ?? zoneType, Type = zoneType, X = cx, Y = cz });
                }

                // Key buildings (one per type, deduplicated)
                var seenTypes = new HashSet<string>();
                foreach (var building in map.listerBuildings.allBuildingsColonist
                    .OrderBy(b => b.Position.DistanceTo(map.Center)))
                {
                    if (seenTypes.Count >= 10) break;
                    if (seenTypes.Add(building.def.defName))
                    {
                        landmarks.Add(new {
                            Name = building.def.defName,
                            Id = building.ThingID,
                            Type = "Building",
                            X = building.Position.x,
                            Y = building.Position.z
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to extract landmarks: {ex.Message}");
            }

            return landmarks;
        }

        private MapInfo? ExtractMapInfo(Map? map)
        {
            if (map == null) return null;
            try
            {
                return new MapInfo
                {
                    Width = map.Size.x,
                    Height = map.Size.z,
                    Biome = map.Biome?.defName ?? "Unknown",
                    Elevation = map.TileInfo?.elevation ?? 0f,
                    Rainfall = map.TileInfo?.rainfall ?? 0f,
                    Temperature = map.TileInfo?.temperature ?? 0f
                };
            }
            catch { return null; }
        }

        private List<BedAssignment> ExtractBedAssignments(Map? map)
        {
            var assignments = new List<BedAssignment>();
            if (map == null) return assignments;

            try
            {
                foreach (var building in map.listerBuildings.allBuildingsColonist)
                {
                    if (building is Building_Bed bed)
                    {
                        var ownerIds = new List<string>();
                        try
                        {
                            foreach (var owner in bed.OwnersForReading)
                            {
                                if (owner != null)
                                    ownerIds.Add(owner.ThingID);
                            }
                        }
                        catch { }

                        assignments.Add(new BedAssignment
                        {
                            BedId = bed.ThingID,
                            BedType = bed.def.defName,
                            Position = new Position2D(bed.Position.x, bed.Position.z),
                            IsMedical = bed.Medical,
                            OwnerIds = ownerIds
                        });
                    }
                }
            }
            catch { }
            return assignments;
        }

        private PowerGridStatus? ExtractPowerGrid(Map? map)
        {
            if (map == null) return null;

            try
            {
                var status = new PowerGridStatus();
                var powerNets = map.powerNetManager.AllNetsListForReading;

                foreach (var net in powerNets)
                {
                    foreach (var comp in net.powerComps)
                    {
                        if (comp.PowerOutput > 0)
                            status.TotalProduction += comp.PowerOutput;
                        else
                            status.TotalConsumption += -comp.PowerOutput;
                    }

                    foreach (var battery in net.batteryComps)
                    {
                        status.StoredEnergy += battery.StoredEnergy;
                        status.MaxStoredEnergy += battery.Props.storedEnergyMax;
                        status.BatteryCount++;
                    }
                }

                status.GeneratorCount = map.listerBuildings.allBuildingsColonist
                    .Count(b => b.TryGetComp<CompPowerPlant>() != null);
                status.HasSufficientPower = status.TotalProduction >= status.TotalConsumption;

                return status;
            }
            catch { return null; }
        }

        private List<RoomInfo> ExtractRooms(Map? map)
        {
            var rooms = new List<RoomInfo>();
            if (map == null) return rooms;

            try
            {
                // Collect unique rooms from colonist buildings (beds, workbenches, etc.)
                var seenRooms = new HashSet<int>();
                foreach (var building in map.listerBuildings.allBuildingsColonist)
                {
                    if (rooms.Count >= 50) break;  // Cap for performance
                    var room = building.GetRoom();
                    if (room == null || room.IsHuge || room.TouchesMapEdge) continue;
                    if (!seenRooms.Add(room.ID)) continue;  // Skip duplicates

                    var role = room.Role;
                    if (role == null || role == RoomRoleDefOf.None) continue;

                    rooms.Add(new RoomInfo
                    {
                        Role = role.defName,
                        Impressiveness = room.GetStat(RoomStatDefOf.Impressiveness),
                        Beauty = room.GetStat(RoomStatDefOf.Beauty),
                        Cleanliness = room.GetStat(RoomStatDefOf.Cleanliness),
                        Temperature = room.Temperature,
                        CellCount = room.CellCount
                    });
                }
            }
            catch { }
            return rooms;
        }

        public string ComputeStateHash()
        {
            var map = Find.CurrentMap;
            if (map == null) return "sha256:no-map";

            try
            {
                var sb = new StringBuilder();

                // Hash colonist positions and health - snapshot to avoid collection modification
                var colonists = map.mapPawns.FreeColonists.ToList();
                foreach (var pawn in colonists)
                {
                    if (pawn == null || pawn.Destroyed) continue;
                    try
                    {
                        sb.Append($"{pawn.ThingID}:{pawn.Position}:{pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f:F2};");
                    }
                    catch
                    {
                        // Skip pawns that throw
                    }
                }

                // Hash key resources
                sb.Append($"wealth:{map.wealthWatcher?.WealthTotal ?? 0:F0};");
                sb.Append($"tick:{Find.TickManager?.TicksGame ?? 0};");

                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return $"sha256:{BitConverter.ToString(hash).Replace("-", "").ToLower()}";
            }
            catch
            {
                return "sha256:error";
            }
        }

        public List<global::GameRL.Harmony.Protocol.GameEvent> CollectEvents()
        {
            var now = CurrentTick;

            // Filter out expired events (TTL enforcement)
            var validEvents = _pendingEvents
                .Where(e => now - e.Tick <= MaxEventAgeTicks)
                .ToList();

            _pendingEvents.Clear();
            return validEvents;
        }

        public void RecordEvent(string type, byte severity, object? details = null)
        {
            var now = CurrentTick;

            // Rate limiting: check if we've recorded this event type too recently
            if (_lastEventTick.TryGetValue(type, out var lastTick))
            {
                if (now - lastTick < MinTicksBetweenSameEvent)
                {
                    // Skip this event due to rate limiting
                    return;
                }
            }

            // Enforce max pending events limit
            if (_pendingEvents.Count >= MaxPendingEvents)
            {
                // Remove oldest event to make room
                _pendingEvents.RemoveAt(0);
            }

            _pendingEvents.Add(new global::GameRL.Harmony.Protocol.GameEvent
            {
                EventType = type,
                Tick = now,
                Severity = severity,
                Details = details
            });

            _lastEventTick[type] = now;
        }

        public object GetObservationSpace(string agentType)
        {
            // Return observation space schema
            return new Dictionary<string, object>
            {
                ["type"] = "dict",
                ["spaces"] = new Dictionary<string, object>
                {
                    ["tick"] = new { type = "int" },
                    ["colonist_count"] = new { type = "int", min = 0, max = 50 },
                    ["colonists"] = new { type = "sequence", max_length = 50 },
                    ["resources"] = new { type = "dict" },
                    ["weather"] = new { type = "string" },
                    ["season"] = new { type = "string" },
                    ["hour"] = new { type = "int", min = 0, max = 23 },
                    ["threats"] = new { type = "sequence" },
                    ["valid_actions"] = new { type = "sequence", description = "Action names valid at current state" },
                    ["research"] = new { type = "dict", description = "Current research state and available projects" },
                    ["zones"] = new { type = "sequence", description = "Map zones (stockpiles, growing zones)" },
                    ["faction_relations"] = new { type = "sequence", description = "Faction diplomacy state" },
                    ["prisoners"] = new { type = "sequence", description = "Prisoner recruitment details" },
                    ["active_traders"] = new { type = "sequence", description = "Active traders on map or in orbit" }
                }
            };
        }

        public object GetActionSpace(string agentType)
        {
            // Return action space schema based on agent type
            return agentType switch
            {
                "colony_manager" => GetColonyManagerActionSpace(),
                "entity_behavior" => GetEntityBehaviorActionSpace(),
                _ => GetColonyManagerActionSpace()
            };
        }

        private object GetColonyManagerActionSpace()
        {
            return new Dictionary<string, object>
            {
                ["Format"] = "Action is a flat JSON object with \"Type\" as the action name and all parameters as top-level keys. Example: {\"Type\": \"Draft\", \"ColonistId\": \"Human123\"} or {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"Lizzie\", \"WorkType\": \"Construction\", \"Priority\": 1}",
                ["Actions"] = new object[]
                {
                    // Basic actions
                    new { Type = "Draft", Description = "Draft a colonist for direct control", ColonistId = "EntityId" },
                    new { Type = "Undraft", Description = "Undraft a colonist", ColonistId = "EntityId" },
                    new { Type = "Move", Description = "Move a drafted pawn to coordinates", ColonistId = "EntityId", X = "int", Y = "int" },
                    new { Type = "MoveToEntity", Description = "Move toward a target entity", ColonistId = "EntityId", TargetId = "EntityId" },

                    // Work management
                    new { Type = "SetWorkPriority", Description = "Set work priority (0=disabled, 1=highest, 4=lowest)", ColonistId = "EntityId", WorkType = "string", Priority = "int 0-4" },
                    new { Type = "SetMedicalCare", Description = "Set medical care level (nocare/nomeds/herbal/normal/best)", ColonistId = "EntityId", CareLevel = "string" },

                    // Combat
                    new { Type = "Attack", Description = "Force a drafted pawn to attack", ColonistId = "EntityId", TargetId = "EntityId" },
                    new { Type = "DesignateHunt", Description = "Mark an animal for hunting", TargetId = "EntityId" },
                    new { Type = "CancelHunt", Description = "Remove hunting designation", TargetId = "EntityId" },

                    // Items
                    new { Type = "Equip", Description = "Have a pawn equip a weapon", ColonistId = "EntityId", WeaponId = "EntityId" },
                    new { Type = "Haul", Description = "Force a pawn to haul an item", ColonistId = "EntityId", ItemId = "EntityId" },
                    new { Type = "Unforbid", Description = "Unforbid a specific item", ThingId = "EntityId" },
                    new { Type = "UnforbidByType", Description = "Unforbid all items of a type (e.g., MealSurvivalPack)", DefName = "string" },
                    new { Type = "UnforbidArea", Description = "Unforbid all items in a radius", X = "int", Y = "int", Radius = "int" },

                    // Production
                    new { Type = "AddBill", Description = "Add a production bill to a workbench", BuildingId = "EntityId", Recipe = "string", Count = "int" },
                    new { Type = "CancelBill", Description = "Remove a bill from a workbench", BuildingId = "EntityId", BillIndex = "int" },
                    new { Type = "ModifyBill", Description = "Modify a bill's count or repeat mode", BuildingId = "EntityId", BillIndex = "int", Count = "int (optional)", RepeatForever = "bool (optional)" },

                    // Construction & Zones
                    new { Type = "PlaceBlueprint", Description = "Place a building blueprint", Building = "string", X = "int", Y = "int", Rotation = "int (optional)", Stuff = "string (optional)" },
                    new { Type = "CreateStockpile", Description = "Create a stockpile zone", X = "int", Y = "int", Width = "int", Height = "int" },
                    new { Type = "CreateGrowingZone", Description = "Create a growing zone", X = "int", Y = "int", Width = "int", Height = "int", Plant = "string (optional)" },
                    new { Type = "DesignateMine", Description = "Designate area for mining", X = "int", Y = "int", Radius = "int" },
                    new { Type = "DesignateCutPlants", Description = "Designate plants for cutting", X = "int", Y = "int", Radius = "int" },

                    // Social
                    new { Type = "Chat", Description = "Initiate social interaction", ColonistId = "EntityId", TargetId = "EntityId" },

                    // Game control
                    new { Type = "SetSpeed", Description = "Set game speed (0=paused, 1=normal, 2=fast, 3=superfast)", Speed = "int 0-3" },
                    new { Type = "Unpause", Description = "Resume the game at normal speed" },

                    // Research
                    new { Type = "SelectResearch", Description = "Select a research project to work on", ProjectDefName = "string" },

                    // Zone management
                    new { Type = "DeleteZone", Description = "Delete a zone by label", ZoneLabel = "string" },
                    new { Type = "SetStockpilePriority", Description = "Set stockpile priority (1-5)", ZoneLabel = "string", Priority = "int 1-5" },
                    new { Type = "SetGrowingPlant", Description = "Change growing zone plant type", ZoneLabel = "string", PlantDefName = "string" },

                    // Prisoner management
                    new { Type = "SetPrisonerInteraction", Description = "Set prisoner interaction mode", PrisonerId = "EntityId", Mode = "string" },

                    // Medical
                    new { Type = "Rescue", Description = "Rescue a downed pawn to a bed", ColonistId = "EntityId", TargetId = "EntityId" },
                    new { Type = "TendTo", Description = "Have a doctor tend to an injured/sick pawn", ColonistId = "EntityId", TargetId = "EntityId" },

                    // Animals
                    new { Type = "DesignateTame", Description = "Mark a wild animal for taming", TargetId = "EntityId" },
                    new { Type = "SetAnimalTraining", Description = "Toggle training for a tamed animal (Obedience, Release, Rescue, Haul)", AnimalId = "EntityId", TrainingDef = "string", Enabled = "bool" },
                    new { Type = "SetAnimalArea", Description = "Restrict animal to an area (or 'Unrestricted')", AnimalId = "EntityId", AreaLabel = "string" },
                    new { Type = "DesignateSlaughter", Description = "Mark a tamed animal for slaughter", AnimalId = "EntityId" },

                    // Episode management
                    new { Type = "SaveCheckpoint", Description = "Save game state for episode reset", Name = "string" },
                    new { Type = "LoadCheckpoint", Description = "Load a saved game checkpoint", Name = "string" },
                    new { Type = "DismissLetters", Description = "Dismiss all pending letter notifications" }
                }
            };
        }

        private object GetEntityBehaviorActionSpace()
        {
            return new Dictionary<string, object>
            {
                ["Format"] = "Action is a flat JSON object with \"Type\" as the action name and all parameters as top-level keys. Example: {\"Type\": \"Move\", \"X\": 50, \"Y\": 30}",
                ["Actions"] = new object[]
                {
                    new { Type = "Move", Description = "Move to coordinates", X = "int", Y = "int" },
                    new { Type = "Interact", Description = "Interact with a target", TargetId = "EntityId" }
                }
            };
        }
    }
}
