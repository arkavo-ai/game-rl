# game-rl-server

MCP server implementation for the Game-RL protocol.

## Features

- `GameEnvironment` trait for implementing game adapters
- MCP JSON-RPC protocol handling
- Agent registry and lifecycle management
- Tool implementations (register_agent, sim_step, reset, get_state_hash, configure_streams)
- stdio transport with MCP handshake
- Resource endpoints (game://manifest, game://agents)
