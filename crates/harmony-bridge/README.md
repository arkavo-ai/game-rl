# harmony-bridge

Bridge between Rust MCP server and .NET games via Harmony.

## Features

- IPC communication via Unix sockets (macOS/Linux) and named pipes (Windows)
- JSON wire protocol for Rust-C# serialization
- `GameEnvironment` implementation proxying to .NET games
- `harmony-server` binary for standalone bridge operation
- Bidirectional message passing (state updates, action execution)
- Support for agent registration, stepping, reset, and shutdown
