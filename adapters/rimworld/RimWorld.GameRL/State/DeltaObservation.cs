// Delta-based observation classes for efficient state updates
// Reduces observation size from 35-150KB to 1-10KB per step

using System.Collections.Generic;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Compact observation containing only changes since last step.
    /// Agent validates PreviousHash matches their last StateHash.
    /// If mismatch, call RequestFullState action.
    /// </summary>
    public class DeltaObservation
    {
        public ulong Tick { get; set; }
        public int Hour { get; set; }
        public string StateHash { get; set; } = "";        // Hash of current full state
        public string? PreviousHash { get; set; }          // Hash this delta is based on
        public ActionFeedback? LastAction { get; set; }
        public List<string> Alerts { get; set; } = new();
        public List<GameEvent> Events { get; set; } = new();  // Causality: why things changed
        public ObservationDelta Delta { get; set; } = new();
    }

    /// <summary>
    /// Container for all state changes since last observation
    /// </summary>
    public class ObservationDelta
    {
        public List<ColonistDelta> Colonists { get; set; } = new();       // Changed colonists only
        public List<string> RemovedColonists { get; set; } = new();       // Died or left map
        public Dictionary<string, int> Resources { get; set; } = new();   // Changed resource counts
        public List<ThreatInfo> AddedThreats { get; set; } = new();
        public List<string> RemovedThreats { get; set; } = new();         // ThingIDs of resolved threats
        public List<EntityRef> AddedEntities { get; set; } = new();
        public List<string> RemovedEntities { get; set; } = new();        // ThingIDs of removed entities
    }

    /// <summary>
    /// Colonist state changes. Null fields indicate no change.
    /// </summary>
    public class ColonistDelta
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }           // Only on first appearance or name change
        public float? Health { get; set; }
        public float? Mood { get; set; }
        public float? Hunger { get; set; }
        public float? Rest { get; set; }
        public string? CurrentJob { get; set; }
        public Position2D? Position { get; set; }
        public bool? IsDrafted { get; set; }
        public bool? IsDowned { get; set; }
        public string? MentalState { get; set; }
        public string? Weapon { get; set; }
    }

    /// <summary>
    /// Game event for causality - explains why state changed
    /// </summary>
    public class GameEvent
    {
        public string Type { get; set; } = "";      // RaidStarted, ColonistDowned, ResearchComplete, etc.
        public string Message { get; set; } = "";   // Human-readable description
        public string? EntityId { get; set; }       // Related entity ThingID if applicable
        public ulong Tick { get; set; }             // When event occurred
    }

    /// <summary>
    /// Configuration for delta thresholds. Can be customized per-agent.
    /// </summary>
    public class DeltaConfig
    {
        // Thresholds for reporting changes (0-1 scale)
        public float MoodThreshold { get; set; } = 0.05f;
        public float HealthThreshold { get; set; } = 0.05f;
        public float HungerThreshold { get; set; } = 0.05f;
        public float RestThreshold { get; set; } = 0.05f;

        // Position reporting
        public int PositionThreshold { get; set; } = 3;           // Tiles moved
        public bool PositionOnlyOnJobChange { get; set; } = false; // Only report if job changed

        // Resource thresholds
        public float ResourcePercentThreshold { get; set; } = 0.0f;  // 0 = any change, 0.05 = 5% change

        /// <summary>
        /// Preset for minimal mode - really quiet, only actionable changes
        /// </summary>
        public static DeltaConfig Minimal => new DeltaConfig
        {
            MoodThreshold = 0.10f,
            HealthThreshold = 0.10f,
            HungerThreshold = 0.10f,
            RestThreshold = 0.10f,
            PositionThreshold = 5,
            PositionOnlyOnJobChange = true,
            ResourcePercentThreshold = 0.05f
        };

        /// <summary>
        /// Preset for normal mode - balanced updates
        /// </summary>
        public static DeltaConfig Normal => new DeltaConfig
        {
            MoodThreshold = 0.05f,
            HealthThreshold = 0.05f,
            HungerThreshold = 0.05f,
            RestThreshold = 0.05f,
            PositionThreshold = 3,
            PositionOnlyOnJobChange = false,
            ResourcePercentThreshold = 0.0f
        };
    }

    /// <summary>
    /// Observation mode enum for clarity
    /// </summary>
    public enum ObservationMode
    {
        Minimal,  // ~1-3KB - only significant changes
        Normal,   // ~10-20KB - full colonists, delta entities
        Full      // ~35-150KB - complete state every step
    }

    /// <summary>
    /// Snapshot of colonist state for delta comparison
    /// </summary>
    internal class ColonistSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public float Health { get; set; }
        public float Mood { get; set; }
        public float Hunger { get; set; }
        public float Rest { get; set; }
        public string? CurrentJob { get; set; }
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public bool IsDrafted { get; set; }
        public bool IsDowned { get; set; }
        public string? MentalState { get; set; }
        public string? Weapon { get; set; }
    }
}
