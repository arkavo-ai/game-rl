# RimWorld Integration Guide

## Overview

This guide covers integrating the game-rl Rust MCP server with RimWorld via Harmony runtime patching.

---

## Architecture

```
Agent Process (Python/Claude)
        │ MCP (stdio, JSON-RPC)
        ▼
harmony-server (Rust)
  ├── game-rl-server: MCP protocol
  └── harmony-bridge: IPC client
        │ Unix socket (MessagePack)
        ▼
RimWorld Process
  ├── GameRL.Harmony.dll (base library)
  └── RimWorld.GameRL.dll (game adapter)
```

---

## Project Structure

```
game-rl/
├── dotnet/
│   ├── GameRL.Harmony.sln
│   └── GameRL.Harmony/           # Base library (game-agnostic)
│       ├── Bridge.cs             # IPC server
│       ├── StateExtractor.cs     # Interface
│       ├── CommandExecutor.cs    # Interface
│       └── Protocol/Messages.cs  # Wire protocol
│
└── adapters/rimworld/
    ├── RimWorld.GameRL/          # Game adapter
    │   ├── Mod.cs                # Entry point
    │   ├── State/                # State extraction
    │   ├── Actions/              # Command execution
    │   ├── Rewards/              # Reward computation
    │   └── Patches/              # Harmony hooks
    ├── About/About.xml
    └── manifest.json
```

---

## Phase 1: Base Library

### GameRL.Harmony.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MessagePack" Version="2.5.140" />
    <PackageReference Include="Lib.Harmony" Version="2.3.3" />
  </ItemGroup>
</Project>
```

### Protocol Messages (must match Rust)

```csharp
// C# → Rust
[MessagePackObject]
public class StepComplete {
    [Key(0)] public string Type => "step_complete";
    [Key(1)] public ulong StepId { get; set; }
    [Key(2)] public ulong Tick { get; set; }
    [Key(3)] public byte[] Observation { get; set; }
    [Key(4)] public bool Done { get; set; }
    [Key(5)] public bool Truncated { get; set; }
}

// Rust → C#
[MessagePackObject]
public class ExecuteAction {
    [Key(0)] public string Type => "execute_action";
    [Key(1)] public string AgentId { get; set; }
    [Key(2)] public ulong StepId { get; set; }
    [Key(3)] public byte[] Action { get; set; }
    [Key(4)] public uint Ticks { get; set; }
}
```

### IPC Bridge

```csharp
public class Bridge : IDisposable {
    private Socket _socket;
    private ConcurrentQueue<GameMessage> _commandQueue;
    
    public bool Connect(string socketPath) {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Connect(endpoint);
        // Start receive thread...
    }
    
    // Call from main thread only
    public void ProcessCommands() {
        while (_commandQueue.TryDequeue(out var msg)) {
            // Handle message
        }
    }
}
```

---

## Phase 2: RimWorld Adapter

### Mod Entry Point

```csharp
[StaticConstructorOnStartup]
public static class GameRLMod {
    private static Bridge _bridge;
    private static uint _ticksRemaining;
    
    static GameRLMod() {
        var harmony = new Harmony("arkavo.gamerl.rimworld");
        harmony.PatchAll();
        
        _bridge = new Bridge(...);
        _bridge.Connect(GetSocketPath());
        _bridge.OnActionReceived += HandleAction;
    }
    
    internal static void OnTick() {
        _bridge.ProcessCommands();
        
        if (_ticksRemaining > 0 && --_ticksRemaining == 0) {
            // Send observation back to Rust
            _bridge.SendStepComplete(...);
        }
    }
}
```

### Critical Patches

```csharp
// Hook game loop
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
public static class TickPatch {
    static void Postfix() => GameRLMod.OnTick();
}

// Deterministic seeding
[HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
public static class RngPatch {
    internal static ulong? PendingSeed;
    static void Prefix(ref int value) {
        if (PendingSeed.HasValue) {
            value = (int)(PendingSeed.Value & 0x7FFFFFFF);
            PendingSeed = null;
        }
    }
}
```

### State Extraction

```csharp
public class RimWorldStateExtractor : IStateExtractor {
    public byte[] ExtractState(string agentId) {
        var map = Find.CurrentMap;
        var obs = new RimWorldObservation {
            Colonists = ColonistExtractor.Extract(map),
            Resources = ResourceExtractor.Extract(map),
            // ...
        };
        return MessagePackSerializer.Serialize(obs);
    }
}
```

### Action Execution

```csharp
public class RimWorldCommandExecutor : ICommandExecutor {
    public void Execute(string agentId, byte[] actionData) {
        var action = MessagePackSerializer.Deserialize<RimWorldAction>(actionData);
        switch (action.Type) {
            case "set_work_priority": /* ... */ break;
            case "draft": /* ... */ break;
            case "move": /* ... */ break;
        }
    }
}
```

---

## Phase 3: Integration

### Start Sequence

```bash
# 1. Rust server
cargo run --bin harmony-server -- --game rimworld

# 2. Launch RimWorld (connects automatically)

# 3. Test agent
python agent/examples/test_rimworld.py
```

### Test Cases

1. Agent registration
2. Reset with seed
3. Step with wait action
4. Step with draft action
5. Determinism (same seed → same hash)
6. Episode termination

---

## Key Rules

### Threading

- Harmony patches run on Unity main thread
- IPC receive runs on background thread
- **Never call Unity APIs from background thread**
- Queue commands, process in OnTick()

### Reward Synchronization

**CRITICAL**: Rewards must be in the same StepComplete message.

```
Agent sends: ExecuteAction
Game does:   Execute → Tick → Extract → Compute reward
Game sends:  StepComplete with observation AND reward
```

### Wire Protocol

```
[4 bytes: length (big-endian)] [N bytes: MessagePack]
```

---

## File Paths

### macOS
```
RimWorld: ~/Library/Application Support/Steam/steamapps/common/RimWorld/
Mods:     .../RimWorldMac.app/Mods/
Managed:  .../RimWorldMac.app/Contents/Resources/Data/Managed/
Logs:     ~/Library/Logs/Ludeon Studios/RimWorld/Player.log
```

### Linux
```
RimWorld: ~/.local/share/Steam/steamapps/common/RimWorld/
Mods:     .../RimWorld/Mods/
Managed:  .../RimWorldLinux_Data/Managed/
```

---

## Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| NullReferenceException | Unity call from background thread | Queue + process in OnTick |
| Connection refused | Server not running | Start harmony-server first |
| Mod not loading | Missing Harmony | Subscribe in Steam Workshop |
| Non-deterministic | Time-based RNG | Audit all Rand usage |
