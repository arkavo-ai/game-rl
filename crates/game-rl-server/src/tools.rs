//! MCP tool handlers for Game-RL protocol

use game_rl_core::{Action, AgentConfig, AgentId, AgentType, GameRLError, Result, error_codes};
use serde::{Deserialize, Serialize};

use crate::environment::GameEnvironment;
use crate::mcp::{RequestId, Response};
use crate::registry::AgentRegistry;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Tool definition for MCP tools/list
#[derive(Debug, Clone, Serialize)]
pub struct ToolDef {
    pub name: String,
    pub description: String,
    #[serde(rename = "inputSchema")]
    pub input_schema: serde_json::Value,
}

/// Get list of available tools
pub fn list_tools() -> Vec<ToolDef> {
    vec![
        ToolDef {
            name: "register_agent".into(),
            description: "Register an agent to control the game. MUST be called before sim_step. Example: {\"AgentId\": \"player1\", \"AgentType\": \"Controller\"}".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "Your agent's unique ID. Example: \"player1\" or \"factory-ai\""
                    },
                    "AgentType": {
                        "type": "string",
                        "description": "Agent role: Observer (watch only), Player (avatar control), Entity (single unit), Controller (strategic/factory), System (game systems), Director (narrative/events)",
                        "enum": ["Observer", "Player", "Entity", "Controller", "System", "Director"],
                        "default": "Controller"
                    },
                    "Config": {
                        "type": "object",
                        "description": "Optional configuration"
                    }
                },
                "required": ["AgentId", "AgentType"]
            }),
        },
        ToolDef {
            name: "deregister_agent".into(),
            description: "Remove an agent. Example: {\"AgentId\": \"commander\"}".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "The AgentId you registered with"
                    }
                },
                "required": ["AgentId"]
            }),
        },
        ToolDef {
            name: "sim_step".into(),
            description: "Execute an action and advance the game. Returns observation with colonists, resources, and reward.".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "Your registered AgentId"
                    },
                    "Action": {
                        "type": "object",
                        "description": "Action object with Type field. Example: {\"Type\": \"Wait\"}"
                    },
                    "Ticks": {
                        "type": "integer",
                        "description": "Game ticks to simulate (60 ticks = 1 second)",
                        "default": 1
                    }
                },
                "required": ["AgentId", "Action"]
            }),
        },
        ToolDef {
            name: "reset".into(),
            description: "Reset environment for new episode. Returns initial observation.".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "Seed": {
                        "type": "integer",
                        "description": "Random seed for reproducibility"
                    },
                    "Scenario": {
                        "type": "string",
                        "description": "Scenario name to load"
                    }
                }
            }),
        },
        ToolDef {
            name: "get_state_hash".into(),
            description: "Get hash of current game state for debugging".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {}
            }),
        },
        ToolDef {
            name: "configure_streams".into(),
            description: "Configure vision streams for an agent".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "Your registered AgentId"
                    },
                    "Profile": {
                        "type": "string",
                        "description": "Stream profile (e.g., \"256x256\")"
                    }
                },
                "required": ["AgentId", "Profile"]
            }),
        },
    ]
}

/// Parameters for register_agent
#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct RegisterAgentParams {
    pub agent_id: AgentId,
    pub agent_type: AgentType,
    #[serde(default)]
    pub config: AgentConfig,
}

/// Parameters for sim_step
#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct SimStepParams {
    pub agent_id: AgentId,
    pub action: Action,
    #[serde(default = "default_ticks")]
    pub ticks: u32,
}

fn default_ticks() -> u32 {
    1
}

/// Parameters for reset
#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ResetParams {
    pub seed: Option<u64>,
    pub scenario: Option<String>,
}

/// Parameters for configure_streams
#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ConfigureStreamsParams {
    pub agent_id: AgentId,
    pub profile: String,
}

/// Handle a tools/call request
pub async fn handle_tool_call<E: GameEnvironment>(
    name: &str,
    params: serde_json::Value,
    id: RequestId,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Response {
    let result = match name {
        "register_agent" => handle_register_agent(params, environment, registry).await,
        "deregister_agent" => handle_deregister_agent(params, environment, registry).await,
        "sim_step" => handle_sim_step(params, environment, registry).await,
        "reset" => handle_reset(params, environment).await,
        "get_state_hash" => handle_state_hash(environment).await,
        "configure_streams" => handle_configure_streams(params, environment).await,
        _ => Err(GameRLError::ProtocolError(format!(
            "Unknown tool: {}",
            name
        ))),
    };

    match result {
        Ok(value) => Response::success(
            id,
            serde_json::json!({ "content": [{ "type": "text", "text": value.to_string() }] }),
        ),
        Err(e) => {
            let code = match &e {
                GameRLError::AgentNotRegistered(_) => error_codes::AGENT_NOT_REGISTERED,
                GameRLError::InvalidAction(_) => error_codes::INVALID_ACTION,
                GameRLError::EpisodeTerminated => error_codes::EPISODE_TERMINATED,
                GameRLError::SyncTimeout => error_codes::SYNC_TIMEOUT,
                GameRLError::ResourceExhausted(_) => error_codes::RESOURCE_EXHAUSTED,
                _ => -32603, // Internal error
            };
            Response::error(id, code, e.to_string())
        }
    }
}

async fn handle_register_agent<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    let p: RegisterAgentParams = serde_json::from_value(params)?;

    // Register in registry first
    {
        let mut reg = registry.write().await;
        reg.register(p.agent_id.clone(), p.agent_type.clone())
            .map_err(|e| GameRLError::ResourceExhausted(e.to_string()))?;
    }

    // Then register with environment
    let mut env = environment.write().await;
    let manifest = env
        .register_agent(p.agent_id.clone(), p.agent_type, p.config)
        .await?;

    Ok(serde_json::to_value(manifest)?)
}

async fn handle_deregister_agent<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    #[derive(Deserialize)]
    #[serde(rename_all = "PascalCase")]
    struct Params {
        agent_id: AgentId,
    }
    let p: Params = serde_json::from_value(params)?;

    // Deregister from environment
    {
        let mut env = environment.write().await;
        env.deregister_agent(&p.agent_id).await?;
    }

    // Deregister from registry
    {
        let mut reg = registry.write().await;
        let _ = reg.deregister(&p.agent_id);
    }

    Ok(serde_json::json!({ "deregistered": true }))
}

async fn handle_sim_step<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    let p: SimStepParams = serde_json::from_value(params)?;

    // Execute step
    let result = {
        let mut env = environment.write().await;
        env.step(&p.agent_id, p.action, p.ticks).await?
    };

    // Update registry
    {
        let mut reg = registry.write().await;
        reg.record_step(&p.agent_id, result.reward);
    }

    Ok(serde_json::to_value(result)?)
}

async fn handle_reset<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let p: ResetParams = serde_json::from_value(params)?;

    let mut env = environment.write().await;
    let obs = env.reset(p.seed, p.scenario).await?;

    Ok(serde_json::to_value(obs)?)
}

async fn handle_state_hash<E: GameEnvironment>(
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let mut env = environment.write().await;
    let hash = env.state_hash().await?;

    Ok(serde_json::json!({ "hash": hash }))
}

async fn handle_configure_streams<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let p: ConfigureStreamsParams = serde_json::from_value(params)?;

    let mut env = environment.write().await;
    let descriptors = env.configure_streams(&p.agent_id, &p.profile).await?;

    Ok(serde_json::to_value(descriptors)?)
}
