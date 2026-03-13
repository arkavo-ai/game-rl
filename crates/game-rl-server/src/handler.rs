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
        "initialized" => Response::success(request.id.clone(), serde_json::json!({})),
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
            name: server.manifest().name.clone(),
            version: server.manifest().version.clone(),
            game_rl_version: server.manifest().game_rl_version.clone(),
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
        server.environment(),
        server.registry(),
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
        "game://manifest" => serde_json::to_value(server.manifest()).unwrap(),
        "game://agents" => {
            serde_json::json!({
                "agents": [],
                "limits": {
                    "max_agents": server.manifest().capabilities.max_agents,
                    "available_slots": server.manifest().capabilities.max_agents
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
