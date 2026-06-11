//! Shared MCP request handler used by all transports

use crate::GameRLServer;
use crate::environment::GameEnvironment;
use crate::mcp::{
    InitializeParams, InitializeResult, Request, ResourcesCapability, Response, ServerCapabilities,
    ServerInfo, ToolsCapability,
};
use crate::tools::{handle_tool_call, list_tools};

/// Handle a single MCP JSON-RPC request.
/// Used by both stdio and HTTP transports.
pub async fn handle_request<E: GameEnvironment>(
    request: &Request,
    server: &GameRLServer<E>,
) -> Response {
    match request.method.as_str() {
        "initialize" => handle_initialize(request, server),
        // "initialized" is a notification (no id) — handled by the transport layer, never reaches here
        "ping" => Response::success(request.id.clone(), serde_json::json!({})),
        "tools/list" => handle_tools_list(request),
        "tools/call" => handle_tools_call(request, server).await,
        "resources/list" => handle_resources_list(request, server),
        "resources/read" => handle_resources_read(request, server).await,
        _ => Response::error(
            request.id.clone(),
            -32601,
            format!("Method not found: {}", request.method),
        ),
    }
}

/// Supported protocol versions, newest first
const SUPPORTED_VERSIONS: &[&str] = &["2025-11-25", "2025-06-18", "2025-03-26"];

fn handle_initialize<E: GameEnvironment>(request: &Request, server: &GameRLServer<E>) -> Response {
    let params: InitializeParams = match serde_json::from_value(request.params.clone()) {
        Ok(p) => p,
        Err(e) => {
            return Response::error(
                request.id.clone(),
                -32602,
                format!("Invalid initialize params: {}", e),
            );
        }
    };

    // Spec: server SHOULD select the highest version it supports that the client also supports.
    // The client sends the version it wants; if we support it, use it. Otherwise use our latest.
    let negotiated_version = if SUPPORTED_VERSIONS.contains(&params.protocol_version.as_str()) {
        params.protocol_version.clone()
    } else {
        SUPPORTED_VERSIONS[0].to_string()
    };

    let result = InitializeResult {
        protocol_version: negotiated_version,
        capabilities: ServerCapabilities {
            tools: ToolsCapability {
                list_changed: false,
            },
            resources: ResourcesCapability {
                subscribe: false,
                list_changed: false,
            },
        },
        server_info: ServerInfo {
            name: server.manifest().name.clone(),
            version: server.manifest().version.clone(),
            game_rl_version: Some(server.manifest().game_rl_version.clone()),
        },
        instructions: Some(format!(
            "Game-RL MCP server for {}. Use registerAgent to connect, then observe/step to interact with the game. \
             Actions use PascalCase Type field. Use observe to get game state without advancing time.",
            server.manifest().name
        )),
    };

    Response::success(request.id.clone(), serde_json::to_value(result).unwrap())
}

fn handle_tools_list(request: &Request) -> Response {
    // Spec: tools/list accepts optional cursor param for pagination.
    // Our tool list is small and static — return all tools, no nextCursor.
    let _cursor = request.params.get("cursor").and_then(|v| v.as_str());
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
        server.environment(),
        server.registry(),
    )
    .await
}

fn handle_resources_list<E: GameEnvironment>(
    request: &Request,
    _server: &GameRLServer<E>,
) -> Response {
    // Spec: resources/list accepts optional cursor param for pagination.
    // Our resource list is small and static — return all, no nextCursor.
    let _cursor = request.params.get("cursor").and_then(|v| v.as_str());
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

async fn handle_resources_read<E: GameEnvironment>(
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
        "game://manifest" => serde_json::to_value(server.manifest()).unwrap(),
        "game://agents" => {
            let reg = server.registry().read().await;
            let agents: Vec<serde_json::Value> = reg
                .list()
                .iter()
                .map(|entry| serde_json::to_value(entry).unwrap_or_default())
                .collect();
            let count = reg.count();
            let available = reg.available_slots();
            serde_json::json!({
                "Agents": agents,
                "Limits": {
                    "MaxAgents": server.manifest().capabilities.max_agents,
                    "Registered": count,
                    "AvailableSlots": available
                }
            })
        }
        _ => {
            // MCP spec: -32002 for resource not found
            return Response::error(
                request.id.clone(),
                -32002,
                format!("Resource not found: {}", params.uri),
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
