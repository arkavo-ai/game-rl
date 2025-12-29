# RimWorld Integration Guide

## Overview

This guide covers integrating the game-rl Rust MCP server with RimWorld via Harmony runtime patching. The integration enables AI agents to observe and control RimWorld colonies through the Game-RL protocol.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Agent Process                               │
│  (Python RL trainer, Claude via Arkavo Edge, etc.)             │
└───────────────────────────┬─────────────────────────────────────┘
                            │ MCP (stdio, JSON-RPC)
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│              harmony-server (Rust binary)                       │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ game-rl-server: MCP protocol, tool handlers             │   │
│  └─────────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ harmony-bridge: IPC client, state cache, reward compute │   │
│  └─────────────────────────────────────────────────────────┘   │
└───────────────────────────┬─────────────────────────────────────┘
                            │ IPC (Unix socket / Named pipe)
                            │ Protocol: MessagePack
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    RimWorld Process                             │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              GameRL.Harmony.dll (C# Mod)                │   │
│  │  ├── Bridge.cs        - IPC server                      │   │
│  │  ├── StateExtractor.cs - Game state → protocol          │   │
│  │  ├── CommandExecutor.cs - Protocol → game actions       │   │
│  │  └── Patches/*.cs      - Harmony hooks                  │   │
│  └─────────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │              RimWorld.GameRL.dll (Game Adapter)         │   │
│  │  ├── RimWorldState.cs  - RimWorld-specific extraction   │   │
│  │  ├── RimWorldActions.cs - RimWorld-specific actions     │   │
│  │  └── RimWorldRewards.cs - Reward computation            │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
game-rl/
├── dotnet/
│   ├── GameRL.Harmony.sln
│   ├── GameRL.Harmony/              # Base library (game-agnostic)
│   │   ├── GameRL.Harmony.csproj
│   │   ├── Bridge.cs                # IPC server
│   │   ├── StateExtractor.cs        # Abstract state extraction
│   │   ├── CommandExecutor.cs       # Abstract command execution
│   │   ├── Protocol/
│   │   │   ├── Messages.cs          # Wire protocol types
│   │   │   └── Serialization.cs     # MessagePack helpers
│   │   └── Patches/
│   │       └── BasePatch.cs         # Common patch utilities
│   │
│   └── GameRL.Harmony.Tests/
│       └── ProtocolTests.cs
│
└── adapters/
    └── rimworld/
        ├── RimWorld.GameRL/          # RimWorld-specific mod
        │   ├── RimWorld.GameRL.csproj
        │   ├── Mod.cs                # Entry point
        │   ├── State/
        │   │   ├── ColonistExtractor.cs
        │   │   ├── ResourceExtractor.cs
        │   │   ├── MapExtractor.cs
        │   │   └── ThreatExtractor.cs
        │   ├── Actions/
        │   │   ├── ColonyActions.cs
        │   │   ├── PawnActions.cs
        │   │   └── ZoneActions.cs
        │   ├── Rewards/
        │   │   └── SurvivalReward.cs
        │   └── Patches/
        │       ├── TickPatch.cs
        │       ├── RngPatch.cs
        │       └── CameraPatch.cs
        │
        ├── About/
        │   ├── About.xml
        │   └── Preview.png
        │
        ├── Assemblies/               # Build output (gitignored)
        │
        └── manifest.json             # Game-RL manifest for RimWorld
```

---

## Component Specifications

### 1. GameRL.Harmony (Base Library)

#### 1.1 Protocol Messages

Must match Rust `harmony-bridge/src/protocol.rs`:

```csharp
// dotnet/GameRL.Harmony/Protocol/Messages.cs

using MessagePack;

namespace GameRL.Harmony.Protocol
{
    [MessagePackObject]
    public abstract class GameMessage
    {
        [Key(0)]
        public abstract string Type { get; }
    }

    // ═══════════════════════════════════════════════════════════
    // C# → Rust Messages
    // ═══════════════════════════════════════════════════════════

    [MessagePackObject]
    public class StateUpdate : GameMessage
    {
        [Key(0)]
        public override string Type => "state_update";
        
        [Key(1)]
        public ulong Tick { get; set; }
        
        [Key(2)]
        public byte[] State { get; set; }  // MessagePack-encoded game state
        
        [Key(3)]
        public string StateHash { get; set; }
    }

    [MessagePackObject]
    public class StepComplete : GameMessage
    {
        [Key(0)]
        public override string Type => "step_complete";
        
        [Key(1)]
        public ulong StepId { get; set; }
        
        [Key(2)]
        public ulong Tick { get; set; }
        
        [Key(3)]
        public byte[] Observation { get; set; }
        
        [Key(4)]
        public bool Done { get; set; }
        
        [Key(5)]
        public bool Truncated { get; set; }
        
        [Key(6)]
        public List<GameEvent> Events { get; set; }
    }

    [MessagePackObject]
    public class GameEvent
    {
        [Key(0)]
        public string EventType { get; set; }
        
        [Key(1)]
        public ulong Tick { get; set; }
        
        [Key(2)]
        public byte Severity { get; set; }
        
        [Key(3)]
        public Dictionary<string, object> Details { get; set; }
    }

    // ═══════════════════════════════════════════════════════════
    // Rust → C# Messages
    // ═══════════════════════════════════════════════════════════

    [MessagePackObject]
    public class ExecuteAction : GameMessage
    {
        [Key(0)]
        public override string Type => "execute_action";
        
        [Key(1)]
        public string AgentId { get; set; }
        
        [Key(2)]
        public ulong StepId { get; set; }
        
        [Key(3)]
        public byte[] Action { get; set; }  // MessagePack-encoded action
        
        [Key(4)]
        public uint Ticks { get; set; }
    }

    [MessagePackObject]
    public class Reset : GameMessage
    {
        [Key(0)]
        public override string Type => "reset";
        
        [Key(1)]
        public ulong? Seed { get; set; }
        
        [Key(2)]
        public string Scenario { get; set; }
    }

    [MessagePackObject]
    public class Shutdown : GameMessage
    {
        [Key(0)]
        public override string Type => "shutdown";
    }

    [MessagePackObject]
    public class RegisterAgent : GameMessage
    {
        [Key(0)]
        public override string Type => "register_agent";
        
        [Key(1)]
        public string AgentId { get; set; }
        
        [Key(2)]
        public string AgentType { get; set; }
        
        [Key(3)]
        public Dictionary<string, object> Config { get; set; }
    }
}
```

#### 1.2 IPC Bridge

```csharp
// dotnet/GameRL.Harmony/Bridge.cs

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using MessagePack;

namespace GameRL.Harmony
{
    public class Bridge : IDisposable
    {
        private Socket _socket;
        private NetworkStream _stream;
        private Thread _receiveThread;
        private readonly ConcurrentQueue<GameMessage> _commandQueue;
        private readonly IStateExtractor _stateExtractor;
        private readonly ICommandExecutor _commandExecutor;
        private volatile bool _running;

        public event Action<ExecuteAction> OnActionReceived;
        public event Action<Reset> OnResetReceived;
        public event Action OnShutdownReceived;

        public Bridge(IStateExtractor extractor, ICommandExecutor executor)
        {
            _stateExtractor = extractor;
            _commandExecutor = executor;
            _commandQueue = new ConcurrentQueue<GameMessage>();
        }

        public bool Connect(string socketPath)
        {
            try
            {
                // Unix domain socket
                var endpoint = new UnixDomainSocketEndPoint(socketPath);
                _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _socket.Connect(endpoint);
                _stream = new NetworkStream(_socket);
                
                _running = true;
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                
                Log.Message("[GameRL] Connected to harmony-server");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Connection failed: {ex.Message}");
                return false;
            }
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[65536];
            while (_running)
            {
                try
                {
                    // Read length prefix (4 bytes, big-endian)
                    if (!ReadExact(buffer, 0, 4)) break;
                    int length = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                    
                    if (length > buffer.Length)
                        buffer = new byte[length];
                    
                    // Read message body
                    if (!ReadExact(buffer, 0, length)) break;
                    
                    // Deserialize
                    var message = MessagePackSerializer.Deserialize<GameMessage>(
                        new ReadOnlyMemory<byte>(buffer, 0, length));
                    
                    // Queue for main thread processing
                    _commandQueue.Enqueue(message);
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log.Error($"[GameRL] Receive error: {ex.Message}");
                    break;
                }
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, offset + read, count - read);
                if (n == 0) return false;  // Connection closed
                read += n;
            }
            return true;
        }

        /// <summary>
        /// Process pending commands. Call from main game thread.
        /// </summary>
        public void ProcessCommands()
        {
            while (_commandQueue.TryDequeue(out var message))
            {
                switch (message)
                {
                    case ExecuteAction action:
                        OnActionReceived?.Invoke(action);
                        break;
                    case Reset reset:
                        OnResetReceived?.Invoke(reset);
                        break;
                    case Shutdown _:
                        OnShutdownReceived?.Invoke();
                        break;
                }
            }
        }

        public void SendStepComplete(StepComplete step)
        {
            Send(step);
        }

        public void SendStateUpdate(StateUpdate update)
        {
            Send(update);
        }

        private void Send(GameMessage message)
        {
            if (!_running || _stream == null) return;
            
            try
            {
                var data = MessagePackSerializer.Serialize(message);
                var length = new byte[4];
                length[0] = (byte)(data.Length >> 24);
                length[1] = (byte)(data.Length >> 16);
                length[2] = (byte)(data.Length >> 8);
                length[3] = (byte)data.Length;
                
                lock (_stream)
                {
                    _stream.Write(length, 0, 4);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[GameRL] Send error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _running = false;
            _stream?.Dispose();
            _socket?.Dispose();
        }
    }
}
```

#### 1.3 State Extractor Interface

```csharp
// dotnet/GameRL.Harmony/StateExtractor.cs

namespace GameRL.Harmony
{
    /// <summary>
    /// Interface for game-specific state extraction
    /// </summary>
    public interface IStateExtractor
    {
        /// <summary>
        /// Extract current game state for an agent
        /// </summary>
        byte[] ExtractState(string agentId);
        
        /// <summary>
        /// Compute state hash for determinism verification
        /// </summary>
        string ComputeStateHash();
        
        /// <summary>
        /// Get current game tick
        /// </summary>
        ulong CurrentTick { get; }
    }
}
```

#### 1.4 Command Executor Interface

```csharp
// dotnet/GameRL.Harmony/CommandExecutor.cs

namespace GameRL.Harmony
{
    /// <summary>
    /// Interface for game-specific action execution
    /// </summary>
    public interface ICommandExecutor
    {
        /// <summary>
        /// Execute an action. Called on main game thread.
        /// </summary>
        void Execute(string agentId, byte[] action);
        
        /// <summary>
        /// Reset game state
        /// </summary>
        void Reset(ulong? seed, string scenario);
        
        /// <summary>
        /// Check if episode is complete
        /// </summary>
        (bool done, bool truncated, string reason) CheckTermination();
    }
}
```

---

### 2. RimWorld.GameRL (Game Adapter)

#### 2.1 Mod Entry Point

```csharp
// adapters/rimworld/RimWorld.GameRL/Mod.cs

using HarmonyLib;
using Verse;
using GameRL.Harmony;

namespace RimWorld.GameRL
{
    [StaticConstructorOnStartup]
    public static class GameRLMod
    {
        private static Bridge _bridge;
        private static RimWorldStateExtractor _stateExtractor;
        private static RimWorldCommandExecutor _commandExecutor;
        private static ulong _currentStepId;
        private static uint _ticksRemaining;
        
        static GameRLMod()
        {
            Log.Message("[GameRL] Initializing RimWorld adapter...");
            
            // Apply Harmony patches
            var harmony = new Harmony("arkavo.gamerl.rimworld");
            harmony.PatchAll();
            
            // Initialize components
            _stateExtractor = new RimWorldStateExtractor();
            _commandExecutor = new RimWorldCommandExecutor();
            _bridge = new Bridge(_stateExtractor, _commandExecutor);
            
            // Connect to harmony-server
            var socketPath = GetSocketPath();
            if (_bridge.Connect(socketPath))
            {
                _bridge.OnActionReceived += HandleAction;
                _bridge.OnResetReceived += HandleReset;
                _bridge.OnShutdownReceived += HandleShutdown;
            }
            
            Log.Message("[GameRL] RimWorld adapter initialized");
        }
        
        private static string GetSocketPath()
        {
            // Check environment variable first
            var path = Environment.GetEnvironmentVariable("GAMERL_SOCKET");
            if (!string.IsNullOrEmpty(path)) return path;
            
            // Default path
            return Path.Combine(Path.GetTempPath(), "gamerl-rimworld.sock");
        }
        
        private static void HandleAction(ExecuteAction action)
        {
            _currentStepId = action.StepId;
            _ticksRemaining = action.Ticks;
            _commandExecutor.Execute(action.AgentId, action.Action);
        }
        
        private static void HandleReset(Reset reset)
        {
            _commandExecutor.Reset(reset.Seed, reset.Scenario);
        }
        
        private static void HandleShutdown()
        {
            Log.Message("[GameRL] Shutdown requested");
            _bridge?.Dispose();
            GenCommandLine.Restart();  // Or Application.Quit()
        }
        
        /// <summary>
        /// Called by TickPatch after each game tick
        /// </summary>
        internal static void OnTick()
        {
            // Process any pending commands from Rust server
            _bridge?.ProcessCommands();
            
            // If we're in a step, decrement counter
            if (_ticksRemaining > 0)
            {
                _ticksRemaining--;
                
                if (_ticksRemaining == 0)
                {
                    // Step complete, send observation
                    var (done, truncated, reason) = _commandExecutor.CheckTermination();
                    
                    var step = new StepComplete
                    {
                        StepId = _currentStepId,
                        Tick = _stateExtractor.CurrentTick,
                        Observation = _stateExtractor.ExtractState("default"),
                        Done = done,
                        Truncated = truncated,
                        Events = _stateExtractor.CollectEvents()
                    };
                    
                    _bridge?.SendStepComplete(step);
                }
            }
        }
    }
}
```

#### 2.2 Tick Patch

```csharp
// adapters/rimworld/RimWorld.GameRL/Patches/TickPatch.cs

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Hook into the game tick loop
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class TickPatch
    {
        static void Postfix()
        {
            GameRLMod.OnTick();
        }
    }
}
```

#### 2.3 RNG Patch (Determinism)

```csharp
// adapters/rimworld/RimWorld.GameRL/Patches/RngPatch.cs

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Enable deterministic seeding
    /// </summary>
    [HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
    public static class RngSeedPatch
    {
        internal static ulong? PendingSeed;
        
        static void Prefix(ref int value)
        {
            if (PendingSeed.HasValue)
            {
                value = (int)(PendingSeed.Value & 0x7FFFFFFF);
                PendingSeed = null;
                Log.Message($"[GameRL] RNG seed set to {value}");
            }
        }
    }
    
    /// <summary>
    /// Hook game initialization to apply seed
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class GameInitPatch
    {
        static void Prefix()
        {
            // Seed will be applied by RngSeedPatch
        }
    }
}
```

#### 2.4 State Extractor

```csharp
// adapters/rimworld/RimWorld.GameRL/State/ColonistExtractor.cs

using System.Collections.Generic;
using System.Linq;
using MessagePack;
using RimWorld;
using Verse;

namespace RimWorld.GameRL.State
{
    [MessagePackObject]
    public class ColonistState
    {
        [Key(0)] public string Id { get; set; }
        [Key(1)] public string Name { get; set; }
        [Key(2)] public float[] Position { get; set; }
        [Key(3)] public float Health { get; set; }
        [Key(4)] public float Mood { get; set; }
        [Key(5)] public string CurrentJob { get; set; }
        [Key(6)] public Dictionary<string, float> Needs { get; set; }
        [Key(7)] public Dictionary<string, int> Skills { get; set; }
        [Key(8)] public bool IsDrafted { get; set; }
    }

    public static class ColonistExtractor
    {
        public static List<ColonistState> Extract(Map map)
        {
            if (map == null) return new List<ColonistState>();
            
            return map.mapPawns.FreeColonists
                .Select(pawn => new ColonistState
                {
                    Id = pawn.ThingID,
                    Name = pawn.Name?.ToStringFull ?? "Unknown",
                    Position = new[] { pawn.Position.x, pawn.Position.z },
                    Health = pawn.health.summaryHealth.SummaryHealthPercent,
                    Mood = pawn.needs?.mood?.CurLevelPercentage ?? 0f,
                    CurrentJob = pawn.CurJob?.def?.defName,
                    Needs = ExtractNeeds(pawn),
                    Skills = ExtractSkills(pawn),
                    IsDrafted = pawn.Drafted
                })
                .ToList();
        }
        
        private static Dictionary<string, float> ExtractNeeds(Pawn pawn)
        {
            var needs = new Dictionary<string, float>();
            if (pawn.needs == null) return needs;
            
            foreach (var need in pawn.needs.AllNeeds)
            {
                needs[need.def.defName] = need.CurLevelPercentage;
            }
            return needs;
        }
        
        private static Dictionary<string, int> ExtractSkills(Pawn pawn)
        {
            var skills = new Dictionary<string, int>();
            if (pawn.skills == null) return skills;
            
            foreach (var skill in pawn.skills.skills)
            {
                skills[skill.def.defName] = skill.Level;
            }
            return skills;
        }
    }
}
```

```csharp
// adapters/rimworld/RimWorld.GameRL/State/ResourceExtractor.cs

using System.Collections.Generic;
using System.Linq;
using MessagePack;
using RimWorld;
using Verse;

namespace RimWorld.GameRL.State
{
    [MessagePackObject]
    public class ResourceState
    {
        [Key(0)] public Dictionary<string, int> Stockpiles { get; set; }
        [Key(1)] public float Silver { get; set; }
        [Key(2)] public float TotalWealth { get; set; }
        [Key(3)] public int FoodDays { get; set; }  // Days of food remaining
    }

    public static class ResourceExtractor
    {
        public static ResourceState Extract(Map map)
        {
            if (map == null) return new ResourceState();
            
            var stockpiles = new Dictionary<string, int>();
            
            // Get counts of important resources
            var importantDefs = new[]
            {
                ThingDefOf.Steel,
                ThingDefOf.WoodLog,
                ThingDefOf.Plasteel,
                ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer,
                ThingDefOf.MedicineHerbal,
                ThingDefOf.MedicineIndustrial,
                ThingDefOf.Silver
            };
            
            foreach (var def in importantDefs)
            {
                stockpiles[def.defName] = map.resourceCounter.GetCount(def);
            }
            
            // Food calculation
            float foodCount = map.resourceCounter.TotalHumanEdibleNutrition;
            int colonistCount = map.mapPawns.FreeColonistsCount;
            int foodDays = colonistCount > 0 
                ? (int)(foodCount / (colonistCount * 1.6f))  // ~1.6 nutrition per day
                : 0;
            
            return new ResourceState
            {
                Stockpiles = stockpiles,
                Silver = map.resourceCounter.GetCount(ThingDefOf.Silver),
                TotalWealth = map.wealthWatcher.WealthTotal,
                FoodDays = foodDays
            };
        }
    }
}
```

```csharp
// adapters/rimworld/RimWorld.GameRL/State/RimWorldStateExtractor.cs

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MessagePack;
using GameRL.Harmony;
using RimWorld;
using Verse;

namespace RimWorld.GameRL.State
{
    [MessagePackObject]
    public class RimWorldObservation
    {
        [Key(0)] public List<ColonistState> Colonists { get; set; }
        [Key(1)] public ResourceState Resources { get; set; }
        [Key(2)] public ThreatState Threats { get; set; }
        [Key(3)] public EnvironmentState Environment { get; set; }
    }

    public class RimWorldStateExtractor : IStateExtractor
    {
        private List<GameEvent> _pendingEvents = new();
        
        public ulong CurrentTick => (ulong)(Find.TickManager?.TicksGame ?? 0);
        
        public byte[] ExtractState(string agentId)
        {
            var map = Find.CurrentMap;
            
            var observation = new RimWorldObservation
            {
                Colonists = ColonistExtractor.Extract(map),
                Resources = ResourceExtractor.Extract(map),
                Threats = ThreatExtractor.Extract(map),
                Environment = EnvironmentExtractor.Extract(map)
            };
            
            return MessagePackSerializer.Serialize(observation);
        }
        
        public string ComputeStateHash()
        {
            var map = Find.CurrentMap;
            if (map == null) return "no-map";
            
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
        
        public void RecordEvent(string type, byte severity, Dictionary<string, object> details)
        {
            _pendingEvents.Add(new GameEvent
            {
                EventType = type,
                Tick = CurrentTick,
                Severity = severity,
                Details = details
            });
        }
        
        public List<GameEvent> CollectEvents()
        {
            var events = _pendingEvents;
            _pendingEvents = new List<GameEvent>();
            return events;
        }
    }
}
```

#### 2.5 Command Executor

```csharp
// adapters/rimworld/RimWorld.GameRL/Actions/RimWorldCommandExecutor.cs

using System;
using System.Collections.Generic;
using MessagePack;
using GameRL.Harmony;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimWorld.GameRL.Actions
{
    [MessagePackObject]
    public class RimWorldAction
    {
        [Key(0)] public string Type { get; set; }
        [Key(1)] public Dictionary<string, object> Params { get; set; }
    }

    public class RimWorldCommandExecutor : ICommandExecutor
    {
        private int _episodeStartTick;
        private const int MaxEpisodeTicks = 60000 * 15;  // 15 in-game days
        
        public void Execute(string agentId, byte[] actionData)
        {
            var action = MessagePackSerializer.Deserialize<RimWorldAction>(actionData);
            
            switch (action.Type)
            {
                case "set_work_priority":
                    ExecuteSetWorkPriority(action.Params);
                    break;
                case "draft":
                    ExecuteDraft(action.Params);
                    break;
                case "undraft":
                    ExecuteUndraft(action.Params);
                    break;
                case "move":
                    ExecuteMove(action.Params);
                    break;
                case "designate_zone":
                    ExecuteDesignateZone(action.Params);
                    break;
                case "wait":
                    // No-op
                    break;
                default:
                    Log.Warning($"[GameRL] Unknown action type: {action.Type}");
                    break;
            }
        }
        
        private void ExecuteSetWorkPriority(Dictionary<string, object> p)
        {
            var pawnId = p["colonist_id"].ToString();
            var workType = p["work_type"].ToString();
            var priority = Convert.ToInt32(p["priority"]);
            
            var pawn = FindPawn(pawnId);
            if (pawn == null) return;
            
            var workDef = DefDatabase<WorkTypeDef>.GetNamed(workType, false);
            if (workDef == null) return;
            
            pawn.workSettings.SetPriority(workDef, priority);
        }
        
        private void ExecuteDraft(Dictionary<string, object> p)
        {
            var pawnId = p["colonist_id"].ToString();
            var pawn = FindPawn(pawnId);
            if (pawn?.drafter != null)
            {
                pawn.drafter.Drafted = true;
            }
        }
        
        private void ExecuteUndraft(Dictionary<string, object> p)
        {
            var pawnId = p["colonist_id"].ToString();
            var pawn = FindPawn(pawnId);
            if (pawn?.drafter != null)
            {
                pawn.drafter.Drafted = false;
            }
        }
        
        private void ExecuteMove(Dictionary<string, object> p)
        {
            var pawnId = p["colonist_id"].ToString();
            var targetX = Convert.ToInt32(p["x"]);
            var targetZ = Convert.ToInt32(p["z"]);
            
            var pawn = FindPawn(pawnId);
            if (pawn == null || !pawn.Drafted) return;
            
            var target = new IntVec3(targetX, 0, targetZ);
            var job = JobMaker.MakeJob(JobDefOf.Goto, target);
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
        }
        
        private void ExecuteDesignateZone(Dictionary<string, object> p)
        {
            // Zone designation logic
            var zoneType = p["zone_type"].ToString();
            var cells = p["cells"] as List<object>;
            // ... implementation
        }
        
        private Pawn FindPawn(string pawnId)
        {
            return Find.CurrentMap?.mapPawns.FreeColonists
                .FirstOrDefault(p => p.ThingID == pawnId);
        }
        
        public void Reset(ulong? seed, string scenario)
        {
            if (seed.HasValue)
            {
                Patches.RngSeedPatch.PendingSeed = seed;
            }
            
            _episodeStartTick = Find.TickManager?.TicksGame ?? 0;
            
            // If scenario specified, load specific save or generate new map
            if (!string.IsNullOrEmpty(scenario))
            {
                // Load scenario
            }
            else
            {
                // Generate new colony
                // This is complex - may need to hook into game's new game flow
            }
        }
        
        public (bool done, bool truncated, string reason) CheckTermination()
        {
            var map = Find.CurrentMap;
            if (map == null)
                return (true, false, "no_map");
            
            // Check if all colonists dead
            if (map.mapPawns.FreeColonistsCount == 0)
                return (true, false, "colony_destroyed");
            
            // Check episode length
            var ticksElapsed = Find.TickManager.TicksGame - _episodeStartTick;
            if (ticksElapsed >= MaxEpisodeTicks)
                return (false, true, "timeout");
            
            return (false, false, null);
        }
    }
}
```

#### 2.6 Reward Computation

```csharp
// adapters/rimworld/RimWorld.GameRL/Rewards/SurvivalReward.cs

using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorld.GameRL.Rewards
{
    public class SurvivalReward
    {
        private int _lastColonistCount;
        private float _lastWealth;
        private float _lastMoodAvg;
        
        public Dictionary<string, float> Compute()
        {
            var map = Find.CurrentMap;
            var components = new Dictionary<string, float>();
            
            if (map == null) return components;
            
            var colonists = map.mapPawns.FreeColonists.ToList();
            var colonistCount = colonists.Count;
            var wealth = map.wealthWatcher.WealthTotal;
            var moodAvg = colonists.Count > 0
                ? colonists.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f)
                : 0f;
            
            // Colonist survival (big negative for deaths)
            var colonistDelta = colonistCount - _lastColonistCount;
            components["colonist_death"] = colonistDelta < 0 ? colonistDelta * -100f : 0f;
            components["colonist_alive"] = colonistCount * 0.01f;  // Small per-tick bonus
            
            // Wealth progress
            var wealthDelta = wealth - _lastWealth;
            components["wealth_delta"] = wealthDelta / 10000f;  // Normalized
            
            // Mood (colony happiness)
            components["mood"] = (moodAvg - 0.5f) * 0.1f;  // Centered at 50%
            
            // Idle penalty
            var idleCount = colonists.Count(p => p.CurJob?.def == JobDefOf.Wait_Wander);
            components["idle_penalty"] = -idleCount * 0.05f;
            
            // Update state for next computation
            _lastColonistCount = colonistCount;
            _lastWealth = wealth;
            _lastMoodAvg = moodAvg;
            
            return components;
        }
        
        public void Reset()
        {
            var map = Find.CurrentMap;
            _lastColonistCount = map?.mapPawns.FreeColonistsCount ?? 0;
            _lastWealth = map?.wealthWatcher.WealthTotal ?? 0;
            _lastMoodAvg = 0.5f;
        }
    }
}
```

---

### 3. Project Files

#### 3.1 GameRL.Harmony.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AssemblyName>GameRL.Harmony</AssemblyName>
    <RootNamespace>GameRL.Harmony</RootNamespace>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MessagePack" Version="2.5.140" />
    <PackageReference Include="Lib.Harmony" Version="2.3.3" />
  </ItemGroup>
</Project>
```

#### 3.2 RimWorld.GameRL.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AssemblyName>RimWorld.GameRL</AssemblyName>
    <OutputPath>../Assemblies/</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <!-- Platform paths -->
  <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <RimWorldPath>$(HOME)/Library/Application Support/Steam/steamapps/common/RimWorld</RimWorldPath>
    <ManagedPath>$(RimWorldPath)/RimWorldMac.app/Contents/Resources/Data/Managed</ManagedPath>
  </PropertyGroup>
  
  <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <RimWorldPath>$(HOME)/.local/share/Steam/steamapps/common/RimWorld</RimWorldPath>
    <ManagedPath>$(RimWorldPath)/RimWorldLinux_Data/Managed</ManagedPath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../dotnet/GameRL.Harmony/GameRL.Harmony.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(ManagedPath)/Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>$(ManagedPath)/UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(ManagedPath)/UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Lib.Harmony" Version="2.3.3">
      <PrivateAssets>all</PrivateAssets>
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <PackageReference Include="MessagePack" Version="2.5.140" />
  </ItemGroup>
</Project>
```

#### 3.3 About.xml

```xml
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Game-RL: AI Training Interface</name>
  <author>Arkavo</author>
  <packageId>arkavo.gamerl.rimworld</packageId>
  <supportedVersions>
    <li>1.5</li>
  </supportedVersions>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
      <displayName>Harmony</displayName>
      <steamWorkshopUrl>steam://url/CommunityFilePage/2009463077</steamWorkshopUrl>
    </li>
  </modDependencies>
  <loadAfter>
    <li>brrainz.harmony</li>
  </loadAfter>
  <description>
Enables AI agents to observe and control RimWorld colonies through the Game-RL protocol.

For research and training purposes.

https://github.com/arkavo-ai/game-rl
  </description>
</ModMetaData>
```

#### 3.4 manifest.json (Game-RL Manifest)

```json
{
  "name": "RimWorld",
  "version": "1.5",
  "game_rl_version": "1.0.0",
  "capabilities": {
    "multi_agent": true,
    "max_agents": 8,
    "deterministic": true,
    "headless": false,
    "save_replay": true
  },
  "agent_types": [
    "ColonyManager",
    "PawnBehavior",
    "StoryTeller",
    "CombatDirector"
  ],
  "action_space": {
    "type": "discrete_parameterized",
    "actions": [
      {
        "name": "set_work_priority",
        "params": {
          "colonist_id": {"type": "entity_id"},
          "work_type": {"type": "string"},
          "priority": {"type": "int", "min": 0, "max": 4}
        }
      },
      {
        "name": "draft",
        "params": {"colonist_id": {"type": "entity_id"}}
      },
      {
        "name": "undraft",
        "params": {"colonist_id": {"type": "entity_id"}}
      },
      {
        "name": "move",
        "params": {
          "colonist_id": {"type": "entity_id"},
          "x": {"type": "int"},
          "z": {"type": "int"}
        }
      },
      {
        "name": "wait",
        "params": {}
      }
    ]
  },
  "reward_components": [
    {"name": "colonist_death", "range": [-100, 0]},
    {"name": "colonist_alive", "range": [0, 1]},
    {"name": "wealth_delta", "range": [-10, 10]},
    {"name": "mood", "range": [-0.1, 0.1]},
    {"name": "idle_penalty", "range": [-1, 0]}
  ],
  "stream_profiles": {
    "policy_topdown": {
      "streams": [
        {"name": "rgb", "width": 224, "height": 224}
      ]
    }
  },
  "tick_rate": 60,
  "max_episode_ticks": 900000
}
```

---

## Build & Test

### Build Commands

```bash
# From game-rl root
cd dotnet
dotnet build GameRL.Harmony.sln

# Build RimWorld adapter
cd ../adapters/rimworld
dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj
```

### Install Mod

```bash
# macOS
RIMWORLD_MODS="$HOME/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods"

# Create symlink for development
ln -s "$(pwd)/adapters/rimworld" "$RIMWORLD_MODS/GameRL"

# Or copy for release
cp -r adapters/rimworld "$RIMWORLD_MODS/GameRL"
```

### Run Integration Test

```bash
# Terminal 1: Start harmony-server
cargo run --bin harmony-server -- --game rimworld --socket /tmp/gamerl-rimworld.sock

# Terminal 2: Start RimWorld with mod enabled
# (RimWorld connects to socket on startup)

# Terminal 3: Run test agent
cd agent
python -c "
import asyncio
from game_rl import GameRLClient

async def test():
    client = GameRLClient('harmony-server --game rimworld')
    manifest = await client.connect()
    print(f'Connected: {manifest}')
    
    obs = await client.reset(seed=42)
    print(f'Initial observation: {obs}')
    
    for i in range(10):
        obs = await client.step('default', {'type': 'wait'})
        print(f'Step {i}: reward={obs[\"reward\"]}')

asyncio.run(test())
"
```

---

## Key Implementation Notes

### 1. Threading

- All Harmony patches run on Unity main thread
- IPC receive runs on background thread
- Commands are queued and processed in `OnTick()` on main thread
- Never call Unity/RimWorld APIs from background thread

### 2. Reward Synchronization

**CRITICAL**: Rewards must be computed and sent in the same `StepComplete` message as the observation. The Rust server expects:

```
Agent sends: ExecuteAction (step N)
Game does:   Execute action → advance ticks → extract state → compute reward
Game sends:  StepComplete with observation AND reward for step N
```

### 3. Determinism

For reproducible experiments:
- Set `Rand.Seed` before map generation
- Disable time-based events if possible
- Use `mode: "training"` in step calls
- Verify with `state_hash` between runs

### 4. Performance

- MessagePack is ~10x faster than JSON
- Extract only needed state (don't serialize entire map)
- Consider frame skip (ticks > 1) for faster training
- Vision streams use shared memory (not IPC)

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Mod not loading | Check `Player.log` for errors, verify Harmony dependency |
| Connection refused | Ensure harmony-server started first, check socket path |
| Actions not executing | Verify running on main thread, check pawn/target validity |
| Non-deterministic | Check for time-based code, ensure seed set before map gen |
| Slow step time | Reduce observation size, increase tick count per step |

---

## Next Steps

1. Implement missing extractors (ThreatExtractor, EnvironmentExtractor)
2. Add vision capture (render texture → shared memory)
3. Implement additional agent types (PawnBehavior for individual control)
4. Create benchmark scenarios
5. Write conformance tests
