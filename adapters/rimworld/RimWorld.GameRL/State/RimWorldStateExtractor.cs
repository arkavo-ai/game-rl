// Main state extractor for RimWorld observations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Verse;
using RimWorld;
using GameRL.Harmony;
using GameRL.Harmony.Protocol;
using MessagePack;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Complete observation state
    /// </summary>
    [MessagePackObject]
    public class RimWorldObservation
    {
        [Key("tick")]
        public ulong Tick { get; set; }

        [Key("colonist_count")]
        public int ColonistCount { get; set; }

        [Key("colonists")]
        public List<ColonistState> Colonists { get; set; } = new();

        [Key("resources")]
        public ResourceState Resources { get; set; } = new();

        [Key("weather")]
        public string? Weather { get; set; }

        [Key("season")]
        public string? Season { get; set; }

        [Key("hour")]
        public int Hour { get; set; }

        [Key("threats")]
        public List<ThreatInfo> Threats { get; set; } = new();
    }

    /// <summary>
    /// Threat information
    /// </summary>
    [MessagePackObject]
    public class ThreatInfo
    {
        [Key("type")]
        public string Type { get; set; } = "";

        [Key("severity")]
        public int Severity { get; set; }

        [Key("count")]
        public int Count { get; set; }
    }

    /// <summary>
    /// Implements IStateExtractor for RimWorld
    /// </summary>
    public class RimWorldStateExtractor : IStateExtractor
    {
        private readonly List<GameEvent> _pendingEvents = new();

        public ulong CurrentTick => (ulong)(Find.TickManager?.TicksGame ?? 0);

        public object ExtractObservation(string agentId)
        {
            var map = Find.CurrentMap;

            var observation = new RimWorldObservation
            {
                Tick = CurrentTick,
                ColonistCount = map?.mapPawns.FreeColonistsCount ?? 0,
                Colonists = ColonistExtractor.Extract(map),
                Resources = ResourceExtractor.Extract(map),
                Weather = map?.weatherManager.curWeather?.defName,
                Season = map != null ? GenLocalDate.Season(map).ToString() : null,
                Hour = map != null ? GenLocalDate.HourOfDay(map) : 0,
                Threats = ExtractThreats(map)
            };

            return observation;
        }

        private List<ThreatInfo> ExtractThreats(Map? map)
        {
            var threats = new List<ThreatInfo>();
            if (map == null) return threats;

            // Count hostile pawns
            var hostileCount = map.mapPawns.AllPawnsSpawned
                .Where(p => p.HostileTo(Faction.OfPlayer))
                .Count();

            if (hostileCount > 0)
            {
                threats.Add(new ThreatInfo
                {
                    Type = "hostile_pawns",
                    Severity = hostileCount > 10 ? 3 : hostileCount > 5 ? 2 : 1,
                    Count = hostileCount
                });
            }

            // Check for fires
            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
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

            return threats;
        }

        public string ComputeStateHash()
        {
            var map = Find.CurrentMap;
            if (map == null) return "sha256:no-map";

            var sb = new StringBuilder();

            // Hash colonist positions and health
            foreach (var pawn in map.mapPawns.FreeColonists)
            {
                sb.Append($"{pawn.ThingID}:{pawn.Position}:{pawn.health.summaryHealth.SummaryHealthPercent:F2};");
            }

            // Hash key resources
            sb.Append($"wealth:{map.wealthWatcher.WealthTotal:F0};");
            sb.Append($"tick:{Find.TickManager.TicksGame};");

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return $"sha256:{BitConverter.ToString(hash).Replace("-", "").ToLower()}";
        }

        public List<GameEvent> CollectEvents()
        {
            var events = new List<GameEvent>(_pendingEvents);
            _pendingEvents.Clear();
            return events;
        }

        public void RecordEvent(string type, byte severity, object? details = null)
        {
            _pendingEvents.Add(new GameEvent
            {
                EventType = type,
                Tick = CurrentTick,
                Severity = severity,
                Details = details
            });
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
                ["actions"] = new[]
                {
                    new {
                        name = "set_work_priority",
                        @params = new Dictionary<string, object>
                        {
                            ["colonist_id"] = new { type = "entity_id" },
                            ["work_type"] = new { type = "string" },
                            ["priority"] = new { type = "int", min = 0, max = 4 }
                        }
                    },
                    new {
                        name = "draft",
                        @params = new Dictionary<string, object>
                        {
                            ["colonist_id"] = new { type = "entity_id" }
                        }
                    },
                    new {
                        name = "undraft",
                        @params = new Dictionary<string, object>
                        {
                            ["colonist_id"] = new { type = "entity_id" }
                        }
                    },
                    new {
                        name = "wait",
                        @params = new Dictionary<string, object>()
                    }
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
