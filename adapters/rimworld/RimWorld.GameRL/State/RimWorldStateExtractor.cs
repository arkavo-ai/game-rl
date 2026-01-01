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

        public List<string> Alerts { get; set; } = new();

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

        public int Severity { get; set; }

        public int Count { get; set; }
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
                IdleColonists = CountIdleColonists(map)
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
                Events = ConvertToObservationEvents(CollectEvents())
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

        private List<string> ExtractAlerts()
        {
            var alerts = new List<string>();
            try
            {
                var uiRoot = Find.UIRoot as UIRoot_Play;
                var alertsReadout = uiRoot?.alerts;
                if (alertsReadout == null) return alerts;

                // Access active alerts via reflection (AlertsReadout.activeAlerts is private)
                var activeAlertsField = typeof(AlertsReadout).GetField("activeAlerts",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (activeAlertsField?.GetValue(alertsReadout) is List<Alert> activeAlerts)
                {
                    // CRITICAL: Take a snapshot to avoid collection modified during iteration
                    // The GUI thread can modify activeAlerts while we're reading it
                    var alertSnapshot = activeAlerts.ToList();

                    foreach (var alert in alertSnapshot)
                    {
                        if (alert == null) continue;

                        try
                        {
                            // GetLabel() can trigger GUI code that accesses null state
                            var label = alert.GetLabel();
                            if (!string.IsNullOrEmpty(label))
                            {
                                alerts.Add(label);
                            }
                        }
                        catch
                        {
                            // Skip alerts that throw during GetLabel()
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to extract alerts: {ex.Message}");
            }
            return alerts;
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

        private List<ThreatInfo> ExtractThreats(Map? map)
        {
            var threats = new List<ThreatInfo>();
            if (map == null) return threats;

            try
            {
                // Count hostile pawns - snapshot to avoid collection modification
                var pawnSnapshot = map.mapPawns.AllPawnsSpawned.ToList();
                var hostileCount = pawnSnapshot
                    .Count(p => p != null && !p.Destroyed && p.Spawned && p.HostileTo(Faction.OfPlayer));

                if (hostileCount > 0)
                {
                    threats.Add(new ThreatInfo
                    {
                        Type = "hostile_pawns",
                        Severity = hostileCount > 10 ? 3 : hostileCount > 5 ? 2 : 1,
                        Count = hostileCount
                    });
                }

                // Check for fires - snapshot to avoid collection modification
                var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire)?.ToList();
                var fireCount = fires?.Count ?? 0;
                if (fireCount > 0)
                {
                    threats.Add(new ThreatInfo
                    {
                        Type = "fire",
                        Severity = fireCount > 20 ? 3 : fireCount > 5 ? 2 : 1,
                        Count = fireCount
                    });
                }
            }
            catch
            {
                // Return empty threats on error
            }

            return threats;
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
                    ["threats"] = new { type = "sequence" }
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
                ["type"] = "discrete_parameterized",
                ["actions"] = new object[]
                {
                    // Basic actions
                    new { name = "Wait", description = "Do nothing, advance simulation", @params = new Dictionary<string, object>() },
                    new { name = "Draft", description = "Draft a colonist for direct control", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" } } },
                    new { name = "Undraft", description = "Undraft a colonist", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" } } },
                    new { name = "Move", description = "Move a drafted pawn to coordinates", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["X"] = new { type = "int" }, ["Y"] = new { type = "int" } } },
                    new { name = "MoveToEntity", description = "Move toward a target entity", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["TargetId"] = new { type = "entity_id" } } },

                    // Work management
                    new { name = "SetWorkPriority", description = "Set work priority (0=disabled, 1=highest, 4=lowest)", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["WorkType"] = new { type = "string" }, ["Priority"] = new { type = "int", min = 0, max = 4 } } },
                    new { name = "SetMedicalCare", description = "Set medical care level (nocare/nomeds/herbal/normal/best)", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["CareLevel"] = new { type = "string" } } },

                    // Combat
                    new { name = "Attack", description = "Force a drafted pawn to attack", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["TargetId"] = new { type = "entity_id" } } },
                    new { name = "DesignateHunt", description = "Mark an animal for hunting", @params = new Dictionary<string, object> { ["TargetId"] = new { type = "entity_id" } } },
                    new { name = "CancelHunt", description = "Remove hunting designation", @params = new Dictionary<string, object> { ["TargetId"] = new { type = "entity_id" } } },

                    // Items
                    new { name = "Equip", description = "Have a pawn equip a weapon", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["WeaponId"] = new { type = "entity_id" } } },
                    new { name = "Haul", description = "Force a pawn to haul an item", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["ItemId"] = new { type = "entity_id" } } },
                    new { name = "Unforbid", description = "Unforbid a specific item", @params = new Dictionary<string, object> { ["ThingId"] = new { type = "entity_id" } } },
                    new { name = "UnforbidByType", description = "Unforbid all items of a type (e.g., MealSurvivalPack)", @params = new Dictionary<string, object> { ["DefName"] = new { type = "string" } } },
                    new { name = "UnforbidArea", description = "Unforbid all items in a radius", @params = new Dictionary<string, object> { ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Radius"] = new { type = "int" } } },

                    // Production
                    new { name = "AddBill", description = "Add a production bill to a workbench", @params = new Dictionary<string, object> { ["BuildingId"] = new { type = "entity_id" }, ["Recipe"] = new { type = "string" }, ["Count"] = new { type = "int" } } },
                    new { name = "CancelBill", description = "Remove a bill from a workbench", @params = new Dictionary<string, object> { ["BuildingId"] = new { type = "entity_id" }, ["BillIndex"] = new { type = "int" } } },
                    new { name = "ModifyBill", description = "Modify a bill's count or repeat mode", @params = new Dictionary<string, object> { ["BuildingId"] = new { type = "entity_id" }, ["BillIndex"] = new { type = "int" }, ["Count"] = new { type = "int", optional = true }, ["RepeatForever"] = new { type = "bool", optional = true } } },

                    // Construction & Zones
                    new { name = "PlaceBlueprint", description = "Place a building blueprint", @params = new Dictionary<string, object> { ["Building"] = new { type = "string" }, ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Rotation"] = new { type = "int", optional = true }, ["Stuff"] = new { type = "string", optional = true } } },
                    new { name = "CreateStockpile", description = "Create a stockpile zone", @params = new Dictionary<string, object> { ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Width"] = new { type = "int" }, ["Height"] = new { type = "int" } } },
                    new { name = "CreateGrowingZone", description = "Create a growing zone", @params = new Dictionary<string, object> { ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Width"] = new { type = "int" }, ["Height"] = new { type = "int" }, ["Plant"] = new { type = "string", optional = true } } },
                    new { name = "DesignateMine", description = "Designate area for mining", @params = new Dictionary<string, object> { ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Radius"] = new { type = "int" } } },
                    new { name = "DesignateCutPlants", description = "Designate plants for cutting", @params = new Dictionary<string, object> { ["X"] = new { type = "int" }, ["Y"] = new { type = "int" }, ["Radius"] = new { type = "int" } } },

                    // Social
                    new { name = "Chat", description = "Initiate social interaction", @params = new Dictionary<string, object> { ["ColonistId"] = new { type = "entity_id" }, ["TargetId"] = new { type = "entity_id" } } }
                }
            };
        }

        private object GetEntityBehaviorActionSpace()
        {
            return new Dictionary<string, object>
            {
                ["type"] = "discrete_parameterized",
                ["actions"] = new[]
                {
                    new {
                        name = "move",
                        @params = new Dictionary<string, object>
                        {
                            ["x"] = new { type = "int" },
                            ["z"] = new { type = "int" }
                        }
                    },
                    new {
                        name = "interact",
                        @params = new Dictionary<string, object>
                        {
                            ["target_id"] = new { type = "entity_id" }
                        }
                    },
                    new {
                        name = "wait",
                        @params = new Dictionary<string, object>()
                    }
                }
            };
        }
    }
}
