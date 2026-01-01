//! stdio transport for MCP JSON-RPC

use crate::GameRLServer;
use crate::environment::GameEnvironment;
use crate::mcp::{
    InitializeParams, InitializeResult, Notification, Request, ResourcesCapability, Response,
    ServerCapabilities, ServerInfo, ToolsCapability,
};
use crate::tools::{handle_tool_call, list_tools};
use game_rl_core::Result;
use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::sync::Mutex;
use tracing::{debug, error, info, warn};

/// Run the MCP server on stdio
pub async fn run<E: GameEnvironment>(server: GameRLServer<E>) -> Result<()> {
    let stdin = tokio::io::stdin();
    let stdout = Arc::new(Mutex::new(tokio::io::stdout()));
    let mut reader = BufReader::new(stdin);
    let mut line = String::new();

    info!("Game-RL MCP server starting on stdio");

    // Subscribe to pushed events if the environment supports it
    let event_rx = {
        let env = server.environment.read().await;
        env.subscribe_events()
    };

    // Spawn event forwarder task if push is supported
    let stdout_for_events = stdout.clone();
    let _event_task = if let Some(mut rx) = event_rx {
        Some(tokio::spawn(async move {
            loop {
                match rx.recv().await {
                    Ok(update) => {
                        let event_count = update.events.len();
                        let notification =
                            Notification::state_update(update.tick, update.state, update.events);
                        match serde_json::to_string(&notification) {
                            Ok(json) => {
                                let mut out = stdout_for_events.lock().await;
                                if let Err(e) = out.write_all(json.as_bytes()).await {
                                    error!("Failed to write event notification: {}", e);
                                    break;
                                }
                                if let Err(e) = out.write_all(b"\n").await {
                                    error!("Failed to write newline: {}", e);
                                    break;
                                }
                                if let Err(e) = out.flush().await {
                                    error!("Failed to flush: {}", e);
                                    break;
                                }
                                debug!("Sent event notification: {} events", event_count);
                            }
                            Err(e) => {
                                warn!("Failed to serialize notification: {}", e);
                            }
                        }
                    }
                    Err(tokio::sync::broadcast::error::RecvError::Closed) => {
                        debug!("Event channel closed");
                        break;
                    }
                    Err(tokio::sync::broadcast::error::RecvError::Lagged(n)) => {
                        warn!("Event forwarder lagged, missed {} events", n);
                    }
                }
            }
        }))
    } else {
        None
    };

    loop {
        line.clear();
        let bytes_read = reader.read_line(&mut line).await.map_err(|e| {
            game_rl_core::GameRLError::IpcError(format!("Failed to read stdin: {}", e))
        })?;

        if bytes_read == 0 {
            // EOF - client disconnected
            info!("Client disconnected (EOF)");
            break;
        }

        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }

        debug!("Received: {}", trimmed);

        let request: Request = match serde_json::from_str(trimmed) {
            Ok(r) => r,
            Err(e) => {
                error!("Failed to parse request: {}", e);
                continue;
            }
        };

        let response = handle_request(&request, &server).await;
        let response_json = serde_json::to_string(&response)
            .map_err(|e| game_rl_core::GameRLError::SerializationError(e.to_string()))?;

        debug!("Sending: {}", response_json);

        {
            let mut out = stdout.lock().await;
            out.write_all(response_json.as_bytes())
                .await
                .map_err(|e| {
                    game_rl_core::GameRLError::IpcError(format!("Failed to write stdout: {}", e))
                })?;
            out.write_all(b"\n").await.map_err(|e| {
                game_rl_core::GameRLError::IpcError(format!("Failed to write newline: {}", e))
            })?;
            out.flush().await.map_err(|e| {
                game_rl_core::GameRLError::IpcError(format!("Failed to flush stdout: {}", e))
            })?;
        }
    }

    // Shutdown environment
    {
        let mut env = server.environment.write().await;
        let _ = env.shutdown().await;
    }

    Ok(())
}

async fn handle_request<E: GameEnvironment>(
    request: &Request,
    server: &GameRLServer<E>,
) -> Response {
    match request.method.as_str() {
        "initialize" => handle_initialize(request, server),
        "initialized" => {
            // Notification, no response needed but we return success
            Response::success(request.id.clone(), serde_json::json!({}))
        }
        "tools/list" => handle_tools_list(request),
        "tools/call" => handle_tools_call(request, server).await,
        "resources/list" => handle_resources_list(request, server),
        "resources/read" => handle_resources_read(request, server),
        _ => Response::error(
            request.id.clone(),
            -32601,
            format!("Method not found: {}", request.method),
        ),
    }
}

fn handle_initialize<E: GameEnvironment>(request: &Request, server: &GameRLServer<E>) -> Response {
    let _params: InitializeParams = match serde_json::from_value(request.params.clone()) {
        Ok(p) => p,
        Err(e) => {
            return Response::error(
                request.id.clone(),
                -32602,
                format!("Invalid initialize params: {}", e),
            );
        }
    };

    let result = InitializeResult {
        protocol_version: "2025-11-25".to_string(),
        capabilities: ServerCapabilities {
            tools: ToolsCapability {
                list_changed: false,
            },
            resources: ResourcesCapability {
                subscribe: true,
                list_changed: false,
            },
            logging: serde_json::json!({}),
        },
        server_info: ServerInfo {
            name: server.manifest.name.clone(),
            version: server.manifest.version.clone(),
            game_rl_version: server.manifest.game_rl_version.clone(),
        },
    };

    Response::success(request.id.clone(), serde_json::to_value(result).unwrap())
}

fn handle_tools_list(request: &Request) -> Response {
    let tools = list_tools();
    Response::success(request.id.clone(), serde_json::json!({ "tools": tools }))
}

async fn handle_tools_call<E: GameEnvironment>(
    request: &Request,
    server: &GameRLServer<E>,
) -> Response {
    #[derive(serde::Deserialize)]
    struct ToolCallParams {
        name: String,
        #[serde(default)]
        arguments: serde_json::Value,
    }

    let params: ToolCallParams = match serde_json::from_value(request.params.clone()) {
        Ok(p) => p,
        Err(e) => {
            return Response::error(
                request.id.clone(),
                -32602,
                format!("Invalid tool call params: {}", e),
            );
        }
    };

    handle_tool_call(
        &params.name,
        params.arguments,
        request.id.clone(),
        &server.environment,
        &server.registry,
    )
    .await
}

fn handle_resources_list<E: GameEnvironment>(
    request: &Request,
    _server: &GameRLServer<E>,
) -> Response {
    let resources = vec![
        serde_json::json!({
            "uri": "game://manifest",
            "name": "Game Manifest",
            "description": "Environment capabilities and configuration",
            "mimeType": "application/json"
        }),
        serde_json::json!({
            "uri": "game://agents",
            "name": "Agent Registry",
            "description": "Currently registered agents",
            "mimeType": "application/json"
        }),
    ];

    Response::success(
        request.id.clone(),
        serde_json::json!({ "resources": resources }),
    )
}

fn handle_resources_read<E: GameEnvironment>(
    request: &Request,
    server: &GameRLServer<E>,
) -> Response {
    #[derive(serde::Deserialize)]
    struct ReadParams {
        uri: String,
    }

    let params: ReadParams = match serde_json::from_value(request.params.clone()) {
        Ok(p) => p,
        Err(e) => {
            return Response::error(
                request.id.clone(),
                -32602,
                format!("Invalid read params: {}", e),
            );
        }
    };

    let content = match params.uri.as_str() {
        "game://manifest" => serde_json::to_value(&server.manifest).unwrap(),
        "game://agents" => {
            // Would need async access to registry, for now return empty
            serde_json::json!({
                "agents": [],
                "limits": {
                    "max_agents": server.manifest.capabilities.max_agents,
                    "available_slots": server.manifest.capabilities.max_agents
                }
            })
        }
        _ => {
            return Response::error(
                request.id.clone(),
                -32602,
                format!("Unknown resource: {}", params.uri),
            );
        }
    };

    Response::success(
        request.id.clone(),
        serde_json::json!({
            "contents": [{
                "uri": params.uri,
                "mimeType": "application/json",
                "text": content.to_string()
            }]
        }),
    )
}
