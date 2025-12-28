//! stdio transport for MCP JSON-RPC

use crate::GameRLServer;
use crate::environment::GameEnvironment;
use crate::mcp::{
    InitializeParams, InitializeResult, Request, ResourcesCapability, Response, ServerCapabilities,
    ServerInfo, ToolsCapability,
};
use crate::tools::{handle_tool_call, list_tools};
use game_rl_core::Result;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tracing::{debug, error, info};

/// Run the MCP server on stdio
pub async fn run<E: GameEnvironment>(server: GameRLServer<E>) -> Result<()> {
    let stdin = tokio::io::stdin();
    let mut stdout = tokio::io::stdout();
    let mut reader = BufReader::new(stdin);
    let mut line = String::new();

    info!("Game-RL MCP server starting on stdio");

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

        stdout
            .write_all(response_json.as_bytes())
            .await
            .map_err(|e| {
                game_rl_core::GameRLError::IpcError(format!("Failed to write stdout: {}", e))
            })?;
        stdout.write_all(b"\n").await.map_err(|e| {
            game_rl_core::GameRLError::IpcError(format!("Failed to write newline: {}", e))
        })?;
        stdout.flush().await.map_err(|e| {
            game_rl_core::GameRLError::IpcError(format!("Failed to flush stdout: {}", e))
        })?;
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
