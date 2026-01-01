# RimWorld GameRL Mod

Enables AI agents to observe and control RimWorld colonies through the Game-RL protocol. Connect with Claude Code for natural language colony management.

## Platform Support

| Platform | Status |
|----------|--------|
| macOS | Tested |
| SteamOS/Linux | Untested |
| Windows | Not yet supported |

## Installation

### Steam Workshop (Recommended)

1. Subscribe to [Arkavo Game-RL](https://steamcommunity.com/sharedfiles/filedetails/?id=3634065510)
2. Subscribe to [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) (required)

### Manual Installation

Copy the mod folder to your RimWorld Mods directory:
- **macOS**: `~/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods/`
- **Linux**: `~/.steam/steam/steamapps/common/RimWorld/Mods/`

## Connect with Claude Code

1. Install Claude Code: https://claude.ai/code
2. Load RimWorld and start a game (mod creates socket on map load)
3. Add the MCP server:

**macOS (Steam Workshop):**
```bash
mkdir -p ~/arkavo-rimworld && cd ~/arkavo-rimworld
claude mcp add game-rl -- "$HOME/Library/Application Support/Steam/steamapps/workshop/content/294100/3634065510/bin/macos/harmony-server" /tmp/gamerl-rimworld.sock
```

**macOS (Manual/Dev):**
```bash
# From game-rl repo root
cargo build --release -p harmony-bridge
claude mcp add game-rl -- ./target/release/harmony-server /tmp/gamerl-rimworld.sock
```

4. Start a Claude Code session and ask it to manage your colony!

## Example Commands

- "What's the colony status?"
- "Unforbid all the survival meals"
- "Draft Zion and move to position 50,50"
- "Set all colonists to priority 1 for hauling"
- "Set medical care to best for everyone"
- "Add a bill to cook 10 simple meals"

## Available Actions

| Action | Description |
|--------|-------------|
| Wait | Advance simulation without action |
| Draft/Undraft | Control colonist drafting |
| Move | Move drafted colonist to coordinates |
| MoveToEntity | Move toward a target entity |
| Attack | Order drafted colonist to attack |
| Haul | Order colonist to haul item |
| Chat | Initiate social interaction |
| Equip | Equip a weapon |
| SetWorkPriority | Set work type priority |
| SetMedicalCare | Set medical care level |
| Unforbid | Unforbid a specific item |
| UnforbidByType | Unforbid all items of a type |
| AddBill | Add production bill to workbench |
| CancelBill | Remove bill from workbench |
| ModifyBill | Change bill count or repeat mode |

## Troubleshooting

### Socket not found
The mod creates `/tmp/gamerl-rimworld.sock` when a map is loaded. Ensure:
1. RimWorld is running
2. A save game is loaded (not just the main menu)
3. The GameRL mod is enabled in the mod list

### Connection refused
Check RimWorld's log for `[GameRL]` messages:
- **macOS**: `~/Library/Logs/Unity/Player.log`

## Development

```bash
# Build the C# mod
cd adapters/rimworld
dotnet build RimWorld.GameRL/RimWorld.GameRL.csproj

# Build the Rust bridge
cargo build -p harmony-bridge

# Run tests
cargo test
```

## License

MIT - See [LICENSE](../../LICENSE)

## Links

- [GitHub Repository](https://github.com/arkavo-ai/game-rl)
- [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3634065510)
- [Arkavo](https://arkavo.com)
