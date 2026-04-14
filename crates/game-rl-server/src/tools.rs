//! MCP tool handlers for Game-RL protocol

use game_rl_core::{
    Action, ActionSpace, AgentConfig, AgentId, AgentType, GameRLError, Result, SpatialIntent,
};
use serde::{Deserialize, Serialize};

use crate::environment::GameEnvironment;
use crate::mcp::{RequestId, Response};
use crate::registry::AgentRegistry;
use std::sync::Arc;
use tokio::sync::RwLock;

/// MCP tool annotations — hints about tool behavior for clients
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolAnnotations {
    /// If true, the tool does not modify game state
    #[serde(skip_serializing_if = "Option::is_none")]
    pub read_only_hint: Option<bool>,
    /// If true, the tool may perform destructive operations
    #[serde(skip_serializing_if = "Option::is_none")]
    pub destructive_hint: Option<bool>,
    /// If true, calling repeatedly with same args has no additional effect
    #[serde(skip_serializing_if = "Option::is_none")]
    pub idempotent_hint: Option<bool>,
    /// If true, the tool interacts with the external game world
    #[serde(skip_serializing_if = "Option::is_none")]
    pub open_world_hint: Option<bool>,
}

/// Tool definition for MCP tools/list
#[derive(Debug, Clone, Serialize)]
pub struct ToolDef {
    pub name: String,
    pub description: String,
    #[serde(rename = "inputSchema")]
    pub input_schema: serde_json::Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub annotations: Option<ToolAnnotations>,
}

/// Get list of available tools
pub fn list_tools() -> Vec<ToolDef> {
    let read_only = Some(ToolAnnotations {
        read_only_hint: Some(true),
        destructive_hint: None,
        idempotent_hint: Some(true),
        open_world_hint: Some(true),
    });
    let mutating = Some(ToolAnnotations {
        read_only_hint: Some(false),
        destructive_hint: Some(false),
        idempotent_hint: Some(false),
        open_world_hint: Some(true),
    });
    let destructive = Some(ToolAnnotations {
        read_only_hint: Some(false),
        destructive_hint: Some(true),
        idempotent_hint: Some(false),
        open_world_hint: Some(true),
    });

    vec![
        ToolDef {
            name: "registerAgent".into(),
            description: "Register an agent to control the game. MUST be called before step. Example: {\"AgentId\": \"player1\", \"AgentType\": \"Controller\"}".into(),
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
            annotations: Some(ToolAnnotations {
                read_only_hint: Some(false),
                destructive_hint: Some(false),
                idempotent_hint: Some(true),
                open_world_hint: Some(true),
            }),
        },
        ToolDef {
            name: "deregisterAgent".into(),
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
            annotations: destructive.clone(),
        },
        ToolDef {
            name: "step".into(),
            description: concat!(
                "Execute an action in the game. Returns observation + reward.\n",
                "\n",
                "IMPORTANT: Action MUST be a JSON object with a \"Type\" field (PascalCase).\n",
                "\n",
                "ColonistId must be a real colonist name from the observation (e.g. \"Lizzie\" or \"Fox\"), NOT a placeholder.\n",
                "\n",
                "Spatial actions use Near parameter (entity ID, type name, or 'MapCenter'):\n",
                "  {\"Action\": {\"Type\": \"PlaceBuildingNear\", \"Building\": \"Bed\", \"Near\": \"Stockpile\", \"Count\": 3}}\n",
                "  {\"Action\": {\"Type\": \"EstablishFarm\", \"Near\": \"Stockpile\", \"Crop\": \"Rice\"}}\n",
                "  {\"Action\": {\"Type\": \"EstablishStorage\", \"Near\": \"CookStove\"}}\n",
                "  {\"Action\": {\"Type\": \"DesignateMiningNear\", \"Near\": \"MapCenter\", \"Count\": 10}}\n",
                "  {\"Action\": {\"Type\": \"DesignateClearNear\", \"Near\": \"Stockpile\", \"Radius\": 15}}\n",
                "\n",
                "Combat (when UnderAttack alert fires — check ValidActions):\n",
                "  {\"Action\": {\"Type\": \"DefendColony\"}}\n",
                "\n",
                "Trade (when traders are present — check ValidActions):\n",
                "  {\"Action\": {\"Type\": \"ListTraderGoods\"}}\n",
                "  {\"Action\": {\"Type\": \"BuyFromTrader\", \"Item\": \"MealSurvivalPack\", \"Count\": 5}}\n",
                "  {\"Action\": {\"Type\": \"SellToTrader\", \"ItemId\": \"<id from entities>\", \"Count\": 1}}\n",
                "\n",
                "Other examples:\n",
                "  {\"Action\": {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"<name>\", \"WorkType\": \"Construction\", \"Priority\": 1}}\n",
                "  {\"Action\": {\"Type\": \"Draft\", \"ColonistId\": \"<name>\"}}\n",
            ).into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "Your registered agent ID (e.g. \"player1\")"
                    },
                    "Action": {
                        "type": "object",
                        "description": "Action to execute. MUST have a \"Type\" field.",
                        "properties": {
                            "Type": {
                                "type": "string",
                                "description": "Action type name (PascalCase). Spatial: PlaceBuildingNear, EstablishFarm, EstablishStorage, DesignateMiningNear, DesignateClearNear. Combat: DefendColony, Draft, Attack. Trade: ListTraderGoods, BuyFromTrader, SellToTrader. Other: Undraft, Move, SetWorkPriority, SetSpeed, DesignateHunt, Rescue, TendTo, Equip, AddBill, UnforbidByType, SaveCheckpoint, Unpause"
                            }
                        },
                        "required": ["Type"]
                    },
                    "Ticks": {
                        "type": "integer",
                        "description": "Game ticks to simulate (60 ticks = 1 second). Use 0 for action-only (no time advance). Default: 1.",
                        "default": 1,
                        "minimum": 0
                    }
                },
                "required": ["Action"],
                "additionalProperties": false
            }),
            annotations: mutating.clone(),
        },
        ToolDef {
            name: "reset".into(),
            description: concat!(
                "Reset environment for new episode. Returns initial observation.\n",
                "For scenarios that restart the game (new colony, checkpoint load), returns immediately with Status=Restarting. Poll with observe tool until the game is ready.\n",
                "\n",
                "Scenario modes:\n",
                "  \"checkpoint_name\" — load a saved game checkpoint\n",
                "  \"new\" — generate a fresh colony (Crashlanded, TemperateForest, defaults)\n",
                "  \"new:Scenario:Biome:Storyteller:Difficulty:MapSize\" — customized new colony\n",
                "\n",
                "Scenarios: Crashlanded, LostTribe, RichExplorer, Naked\n",
                "Biomes: TemperateForest, BorealForest, TropicalRainforest, AridShrubland, Desert, Tundra, IceSheet\n",
                "Storytellers: Cassandra, Phoebe, Randy\n",
                "Difficulties: Peaceful, Community, Adventure, Strive, Blood, Losing, Deathwish\n",
                "\n",
                "Examples:\n",
                "  {\"Scenario\": \"training_base\"} — reload checkpoint\n",
                "  {\"Scenario\": \"new\", \"Seed\": 42} — new colony with seed\n",
                "  {\"Scenario\": \"new:Crashlanded:BorealForest:Randy:Strive:250\"} — full customization\n",
            ).into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "Seed": {
                        "type": "integer",
                        "description": "Random seed for reproducibility. Used as world seed for new colonies."
                    },
                    "Scenario": {
                        "type": "string",
                        "description": "Checkpoint name to load, or 'new' to generate a fresh colony, or 'new:Scenario:Biome:Storyteller:Difficulty:MapSize' for customization."
                    }
                }
            }),
            annotations: destructive.clone(),
        },
        ToolDef {
            name: "observe".into(),
            description: concat!(
                "Get current game state without advancing time.\n",
                "\n",
                "Default: compact overview with alerts, research, zones, weather.\n",
                "Step responses are minimal (alerts + feedback only). Use observe to get full state.\n",
                "\n",
                "Sections: colonists, resources, entities, terrain, rooms, research, zones, threats, factions, prisoners, traders, power, beds, alerts, actions, map, landmarks\n",
                "\n",
                "Sub-sections: entities.animals, entities.buildings, entities.items, entities.weapons, entities.hostiles\n",
                "\n",
                "Examples:\n",
                "  {} → compact overview\n",
                "  {\"Include\": [\"colonists\"]} → full colonist detail (skills, traits, work priorities)\n",
                "  {\"Include\": [\"terrain\"], \"Limit\": 5} → top 5 fertile regions\n",
                "  {\"Include\": [\"entities.items\"]} → items on map\n",
            ).into(),
            input_schema: serde_json::json!({
                "type": "object",
                "properties": {
                    "AgentId": {
                        "type": "string",
                        "description": "Your registered agent ID"
                    },
                    "Include": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "Sections to include. Supports dot notation for sub-sections (e.g. entities.buildings)."
                    },
                    "Limit": {
                        "type": "integer",
                        "description": "Max items per list section (default: 20 for terrain, unlimited for others)",
                        "minimum": 1
                    }
                }
            }),
            annotations: read_only.clone(),
        },
        ToolDef {
            name: "stateHash".into(),
            description: "Get hash of current game state for debugging".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "additionalProperties": false
            }),
            annotations: read_only.clone(),
        },
        ToolDef {
            name: "episodeSummary".into(),
            description: "Get cumulative episode metrics (total reward, step count, ticks elapsed, reward breakdown). Call after done=true to assess episode quality before reset.".into(),
            input_schema: serde_json::json!({
                "type": "object",
                "additionalProperties": false
            }),
            annotations: read_only.clone(),
        },
        ToolDef {
            name: "configureStreams".into(),
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
            annotations: Some(ToolAnnotations {
                read_only_hint: Some(false),
                destructive_hint: Some(false),
                idempotent_hint: Some(true),
                open_world_hint: Some(true),
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

/// Parameters for step
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
        "registerAgent" | "register_agent" => {
            handle_register_agent(params, environment, registry).await
        }
        "deregisterAgent" | "deregister_agent" => {
            handle_deregister_agent(params, environment, registry).await
        }
        "step" | "sim_step" => handle_step(params, environment, registry).await,
        "observe" => handle_observe(params, environment, registry).await,
        "reset" => handle_reset(params, environment).await,
        "stateHash" | "get_state_hash" => handle_state_hash(environment).await,
        "episodeSummary" | "episode_summary" => handle_episode_summary(environment).await,
        "configureStreams" | "configure_streams" => {
            handle_configure_streams(params, environment).await
        }
        _ => Err(GameRLError::ProtocolError(format!(
            "Unknown tool: {}",
            name
        ))),
    };

    match result {
        Ok(value) => Response::success(
            id,
            serde_json::json!({
                "content": [{ "type": "text", "text": value.to_string() }],
                "isError": false
            }),
        ),
        Err(e) => {
            // Per MCP spec: tool execution errors (invalid action, agent not registered, etc.)
            // SHOULD be returned as content with isError: true, not as JSON-RPC protocol errors.
            // Only true protocol errors (unknown tool) use JSON-RPC error responses.
            match &e {
                GameRLError::ProtocolError(_) => Response::error(id, -32602, e.to_string()),
                _ => Response::success(
                    id,
                    serde_json::json!({
                        "content": [{ "type": "text", "text": e.to_string() }],
                        "isError": true
                    }),
                ),
            }
        }
    }
}

async fn handle_register_agent<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    let p: RegisterAgentParams = serde_json::from_value(params)?;

    // Register in registry — returns false if already registered (idempotent)
    let is_new = {
        let mut reg = registry.write().await;
        reg.register(p.agent_id.clone(), p.agent_type.clone())
            .map_err(|e| GameRLError::ResourceExhausted(e.to_string()))?
    };

    if !is_new {
        return Ok(serde_json::json!({
            "AgentId": p.agent_id,
            "AlreadyRegistered": true
        }));
    }

    // Register with environment only for new agents
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

/// Normalize top-level field name casing for step params.
/// Handles LLMs sending camelCase or snake_case instead of PascalCase.
fn normalize_step_fields(params: &mut serde_json::Value) {
    if let Some(map) = params.as_object_mut() {
        if !map.contains_key("AgentId") {
            for key in ["agent_id", "agentId", "agent"] {
                if let Some(val) = map.remove(key) {
                    map.insert("AgentId".to_string(), val);
                    break;
                }
            }
        }
        if !map.contains_key("Action") {
            if let Some(val) = map.remove("action") {
                map.insert("Action".to_string(), val);
            }
        }
        if !map.contains_key("Ticks") {
            for key in ["ticks", "tick", "Tick"] {
                if let Some(val) = map.remove(key) {
                    map.insert("Ticks".to_string(), val);
                    break;
                }
            }
        }
    }
}

/// Normalize common parameter name mistakes inside an action object.
/// LLMs often use "ColonistName" instead of "ColonistId", etc.
fn normalize_action_params(map: &mut serde_json::Map<String, serde_json::Value>) {
    // ColonistName → ColonistId (LLMs use name from few-shot examples)
    if !map.contains_key("ColonistId") {
        for key in [
            "ColonistName",
            "colonist_name",
            "colonistName",
            "colonist_id",
            "colonistId",
            "PawnId",
            "pawnId",
            "pawn_id",
        ] {
            if let Some(val) = map.remove(key) {
                map.insert("ColonistId".to_string(), val);
                break;
            }
        }
    }
}

/// Fuzzy match an action name against known actions using edit distance.
/// Returns the corrected name if a close match is found (distance <= 3 and < 40% of name length).
fn fuzzy_match_action(
    input: &str,
    actions: &[game_rl_core::action::ActionDefinition],
) -> Option<String> {
    let input_lower = input.to_lowercase();
    let mut best: Option<(&str, usize)> = None;

    for action in actions {
        let name_lower = action.name.to_lowercase();
        let dist = edit_distance(&input_lower, &name_lower);
        let threshold = (action.name.len() / 3).clamp(2, 3);
        if dist <= threshold && (best.is_none() || dist < best.unwrap().1) {
            best = Some((&action.name, dist));
        }
    }

    best.map(|(name, _)| name.to_string())
}

/// Simple Levenshtein edit distance
fn edit_distance(a: &str, b: &str) -> usize {
    let a: Vec<char> = a.chars().collect();
    let b: Vec<char> = b.chars().collect();
    let (m, n) = (a.len(), b.len());
    let mut dp = vec![vec![0usize; n + 1]; m + 1];
    for (i, row) in dp.iter_mut().enumerate().take(m + 1) {
        row[0] = i;
    }
    for (j, val) in dp[0].iter_mut().enumerate().take(n + 1) {
        *val = j;
    }
    for i in 1..=m {
        for j in 1..=n {
            let cost = if a[i - 1] == b[j - 1] { 0 } else { 1 };
            dp[i][j] = (dp[i - 1][j] + 1)
                .min(dp[i][j - 1] + 1)
                .min(dp[i - 1][j - 1] + cost);
        }
    }
    dp[m][n]
}

/// Normalize common LLM mistakes in the Action field before serde deserialization.
///
/// The Action enum expects: an integer, an array of floats, or an object with a "Type" field.
/// LLMs commonly send strings, empty objects, lowercase keys, or double-wrapped objects.
fn normalize_action(params: &mut serde_json::Value) -> std::result::Result<(), String> {
    let Some(action) = params.get_mut("Action") else {
        return Err("Missing \"Action\" field. Choose a specific action, e.g. {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"Lizzie\", \"WorkType\": \"Construction\", \"Priority\": 1}".to_string());
    };

    match action {
        // null → error, must choose a real action
        serde_json::Value::Null => {
            return Err("Action cannot be null. Choose a specific action, e.g. {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"Lizzie\", \"WorkType\": \"Construction\", \"Priority\": 1}".to_string());
        }

        // Bare string → wrap as {"Type": <string>}
        serde_json::Value::String(s) => {
            let action_type = s.clone();
            *action = serde_json::json!({"Type": action_type});
        }

        serde_json::Value::Object(map) => {
            // Empty object → error
            if map.is_empty() {
                return Err("Action cannot be empty. Choose a specific action, e.g. {\"Type\": \"SetWorkPriority\", \"ColonistId\": \"Lizzie\", \"WorkType\": \"Construction\", \"Priority\": 1}".to_string());
            }

            // Already has "Type" — correct format
            if map.contains_key("Type") {
                // Remove redundant "Action" key inside the action object
                map.remove("Action");
                // Normalize common param name mistakes
                normalize_action_params(map);
                return Ok(());
            }

            // lowercase "type" → rename to "Type"
            if let Some(type_val) = map.remove("type") {
                map.insert("Type".to_string(), type_val);
                map.remove("Action");
                normalize_action_params(map);
                return Ok(());
            }

            // Alternative key names → rename to "Type"
            for key in [
                "action_type",
                "ActionType",
                "name",
                "Name",
                "command",
                "Command",
            ] {
                if let Some(type_val) = map.remove(key) {
                    map.insert("Type".to_string(), type_val);
                    return Ok(());
                }
            }

            // Double-wrapped: {"Action": ...} inside the Action field
            if let Some(inner) = map.remove("Action") {
                match inner {
                    serde_json::Value::String(s) => {
                        map.insert("Type".to_string(), serde_json::Value::String(s));
                    }
                    serde_json::Value::Object(inner_map) => {
                        *action = serde_json::Value::Object(inner_map);
                        // Recurse once to normalize the unwrapped value
                        return normalize_action(params);
                    }
                    other => {
                        map.insert("Type".to_string(), other);
                    }
                }
                return Ok(());
            }

            // Object with keys but no type indicator
            return Err(format!(
                "Action object has no \"Type\" field. Got keys: {:?}. \
                 Expected: {{\"Type\": \"Wait\"}} or {{\"Type\": \"Draft\", \"ColonistId\": \"<name from observation>\"}}",
                map.keys().collect::<Vec<_>>()
            ));
        }

        // Number/Array are valid (Discrete/Continuous) — pass through
        serde_json::Value::Number(_) | serde_json::Value::Array(_) => {}

        serde_json::Value::Bool(b) => {
            return Err(format!(
                "Action cannot be a boolean (got {}). Expected: {{\"Type\": \"Wait\"}}",
                b
            ));
        }
    }

    Ok(())
}

async fn handle_step<E: GameEnvironment>(
    mut params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    // Normalize field names and Action format before deserialization
    normalize_step_fields(&mut params);

    if params.get("AgentId").is_none() {
        let reg = registry.read().await;
        let agent_ids: Vec<String> = reg.list().iter().map(|a| a.agent_id.clone()).collect();
        if agent_ids.len() == 1 {
            // Auto-fill AgentId when only one agent is registered
            if let Some(map) = params.as_object_mut() {
                map.insert(
                    "AgentId".to_string(),
                    serde_json::Value::String(agent_ids[0].clone()),
                );
            }
        } else {
            return Err(GameRLError::ProtocolError(format!(
                "Missing required field \"AgentId\" in step. Registered agents: {:?}. Example: {{\"AgentId\": \"{}\", \"Action\": {{\"Type\": \"Wait\"}}}}",
                agent_ids,
                agent_ids.first().unwrap_or(&"player1".to_string())
            )));
        }
    }

    if let Err(msg) = normalize_action(&mut params) {
        return Err(GameRLError::InvalidAction(msg));
    }

    let mut p: SimStepParams = serde_json::from_value(params).map_err(|e| {
        GameRLError::ProtocolError(format!(
            "Invalid step params: {}. Action must have a \"Type\" field, e.g. {{\"Type\": \"Wait\"}} or {{\"Type\": \"Draft\", \"ColonistId\": \"<name from observation>\"}}",
            e
        ))
    })?;

    // Intercept read-only observation requests — LLMs naturally try these as actions
    let is_observe = matches!(&p.action, Action::Parameterized { action_type, .. }
        if matches!(action_type.as_str(), "Observe" | "GetState" | "observe"));

    // Check if this is a spatial intent action (bypasses manifest validation)
    let is_spatial = matches!(&p.action, Action::Parameterized { action_type, .. }
        if SpatialIntent::is_spatial_action(action_type));

    let result = if is_observe {
        let mut env = environment.write().await;
        env.observe().await?
    } else if is_spatial {
        // Spatial intent actions bypass manifest validation — they're dispatched
        // directly to the game via the normal step path. The game's [GameRLAction]
        // handler resolves coordinates and executes the placement.
        let mut env = environment.write().await;
        env.step(&p.agent_id, p.action, p.ticks).await?
    } else {
        // Validate and auto-correct action type against manifest
        if let Action::Parameterized {
            ref mut action_type,
            ..
        } = p.action
        {
            let env = environment.read().await;
            let manifest = env.manifest();
            if let Some(ActionSpace::DiscreteParameterized { ref actions }) =
                manifest.default_action_space
            {
                // Exact match (case-insensitive)
                let exact = actions
                    .iter()
                    .find(|a| a.name.eq_ignore_ascii_case(action_type));
                if let Some(matched) = exact {
                    // Fix casing if needed
                    if *action_type != matched.name {
                        *action_type = matched.name.clone();
                    }
                } else {
                    // Fuzzy match: find closest by edit distance
                    if let Some(best) = fuzzy_match_action(action_type, actions) {
                        *action_type = best;
                    } else {
                        let names: Vec<&str> = actions.iter().map(|a| a.name.as_str()).collect();
                        return Err(GameRLError::InvalidAction(format!(
                            "Unknown action \"{}\". Valid actions: {}",
                            action_type,
                            names.join(", ")
                        )));
                    }
                }
            }
        }

        // Execute step
        let mut env = environment.write().await;
        env.step(&p.agent_id, p.action, p.ticks).await?
    };

    // Don't inject available_actions on every step — it's static and wastes context.
    // Agents get the action space from registerAgent manifest and can use observe(actions) to refresh.

    // Update registry
    {
        let mut reg = registry.write().await;
        reg.record_step(&p.agent_id, result.reward);
    }

    // Filter observation to step-compact view (strips slow-changing sections)
    let value = serde_json::to_value(result)?;
    Ok(filter_observation(value, &FilterConfig::step()))
}

/// Parameters for observe
#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct ObserveParams {
    #[serde(default)]
    #[allow(dead_code)]
    agent_id: Option<AgentId>,
    #[serde(default)]
    include: Option<Vec<String>>,
    #[serde(default)]
    limit: Option<usize>,
}

/// Sections excluded by default (large/specialized — request via Include)
const DRILLDOWN_SECTIONS: &[&str] = &[
    "Entities",
    "Terrain",
    "Rooms",
    "BedAssignments",
    "PowerGrid",
    "FactionRelations",
    "Prisoners",
    "ActiveTraders",
    "Map",
    "Visitors",
    "Threats",
];

/// Map user-facing section name to PascalCase JSON field
fn section_to_field(s: &str) -> &str {
    match s {
        "colonists" => "Colonists",
        "resources" => "Resources",
        "entities" => "Entities",
        "terrain" => "Terrain",
        "rooms" => "Rooms",
        "research" => "Research",
        "zones" => "Zones",
        "threats" => "Threats",
        "factionRelations" | "factions" => "FactionRelations",
        "prisoners" => "Prisoners",
        "traders" => "ActiveTraders",
        "powerGrid" | "power" => "PowerGrid",
        "bedAssignments" | "beds" => "BedAssignments",
        "alerts" => "Alerts",
        "validActions" | "actions" => "ValidActions",
        "map" => "Map",
        "visitors" => "Visitors",
        "landmarks" => "Landmarks",
        other => other,
    }
}

/// Map entities sub-section to PascalCase field
fn entities_sub_field(sub: &str) -> &str {
    match sub {
        "animals" => "Animals",
        "buildings" => "Buildings",
        "items" => "Items",
        "weapons" => "Weapons",
        "hostiles" => "Hostiles",
        "colonists" => "Colonists",
        "corpses" => "Corpses",
        "prisoners" => "Prisoners",
        "visitors" => "Visitors",
        "itemCounts" | "counts" => "ItemCounts",
        "forbiddenItemCounts" | "forbidden" => "ForbiddenItemCounts",
        other => other,
    }
}

struct FilterConfig {
    include: Option<Vec<String>>,
    limit: Option<usize>,
    /// If true, strip slow-changing sections (Research, ValidActions, Zones) for step responses
    step_mode: bool,
}

impl FilterConfig {
    fn compact() -> Self {
        Self {
            include: None,
            limit: None,
            step_mode: false,
        }
    }
    fn step() -> Self {
        Self {
            include: None,
            limit: None,
            step_mode: true,
        }
    }
}

fn filter_observation(mut value: serde_json::Value, config: &FilterConfig) -> serde_json::Value {
    // Phase 1: Filter the observation content
    filter_observation_content(&mut value, config);
    // Phase 2: Clean up StepResult wrapper
    if let Some(obj) = value.as_object_mut() {
        if obj.contains_key("Observation") || obj.contains_key("observation") {
            strip_step_wrapper(obj);
        }
    }
    // Phase 3: Recursively strip nulls, empty arrays/objects from entire tree
    strip_noise(&mut value);
    value
}

/// Filter observation content (sections, colonists, research, delta).
fn filter_observation_content(value: &mut serde_json::Value, config: &FilterConfig) {
    let obj = match value.as_object_mut() {
        Some(obj) => obj,
        None => return,
    };

    // Get the observation object (might be nested under "Observation" in StepResult)
    let obs_obj = if let Some(obs) = obj.get_mut("Observation").and_then(|v| v.as_object_mut()) {
        obs
    } else if let Some(obs) = obj.get_mut("observation").and_then(|v| v.as_object_mut()) {
        obs
    } else {
        obj
    };

    match &config.include {
        Some(sections) => {
            let always_keep = [
                "Tick",
                "ColonistCount",
                "Hour",
                "Season",
                "Weather",
                "Temperature",
            ];
            let mut keep_fields: std::collections::HashSet<String> =
                std::collections::HashSet::new();
            let mut entity_subs: Vec<String> = Vec::new();

            for s in sections {
                if let Some(sub) = s.strip_prefix("entities.") {
                    keep_fields.insert("Entities".to_string());
                    entity_subs.push(sub.to_string());
                } else {
                    keep_fields.insert(section_to_field(s).to_string());
                }
            }

            // Remove fields not requested
            let keys_to_remove: Vec<String> = obs_obj
                .keys()
                .filter(|k| !always_keep.contains(&k.as_str()) && !keep_fields.contains(k.as_str()))
                .cloned()
                .collect();
            for key in keys_to_remove {
                obs_obj.remove(&key);
            }

            // Filter entities sub-sections if dot notation was used
            if !entity_subs.is_empty() {
                if let Some(entities) = obs_obj.get_mut("Entities").and_then(|v| v.as_object_mut())
                {
                    let entity_keep: std::collections::HashSet<&str> =
                        entity_subs.iter().map(|s| entities_sub_field(s)).collect();
                    let entity_remove: Vec<String> = entities
                        .keys()
                        .filter(|k| !entity_keep.contains(k.as_str()))
                        .cloned()
                        .collect();
                    for key in entity_remove {
                        entities.remove(&key);
                    }
                }
            }

            // Terrain: limit regions (default top 20)
            if keep_fields.contains("Terrain") {
                apply_terrain_limit(obs_obj, config.limit.unwrap_or(20));
            }
        }
        None => {
            // Compact overview: remove large/specialized sections
            for section in DRILLDOWN_SECTIONS {
                obs_obj.remove(*section);
            }
            // Strip stable/rarely-changing fields from colonists to save context
            strip_colonist_stable_fields(obs_obj);
            // Research: only show startable projects in compact mode
            strip_unstartable_research(obs_obj);
            // Strip zero-value resources (Chemfuel: 0, Gold: 0, etc.)
            strip_zero_resources(obs_obj);

            if config.step_mode {
                // Step responses: strip slow-changing sections available via observe()
                for section in STEP_EXCLUDE_SECTIONS {
                    obs_obj.remove(*section);
                }
            }
        }
    }
}

/// Sections excluded from step responses (slow-changing, available via observe)
const STEP_EXCLUDE_SECTIONS: &[&str] = &[
    "Research",     // Only changes on SelectResearch action
    "ValidActions", // Essentially static (available from manifest)
    "Zones",        // Only changes on zone create/delete
];

/// Fields to strip from each colonist in compact mode.
/// These are stable (rarely change) or redundant — use observe(colonists) for full detail.
const COLONIST_STABLE_FIELDS: &[&str] = &[
    "Needs",             // Duplicates top-level Health/Mood/Hunger/Rest + marginal needs
    "Skills",            // Rarely changes mid-session
    "SkillPassions",     // Never changes
    "Schedule",          // 24 entries, almost never changes
    "DisabledWorkTypes", // Never changes
    "Apparel",           // Rarely changes, 7 fields per item
    "Relations",         // Rarely changes
    "Traits",            // Never changes
    "WorkPriorities",    // Rarely changes (agent sets explicitly)
];

/// Strip stable/redundant fields from colonist objects in compact mode.
/// Keeps only volatile per-step fields: Health, Mood, Hunger, Rest, Position,
/// CurrentJob, IsDrafted, IsDowned, MentalState, Weapon, etc.
fn strip_colonist_stable_fields(obs_obj: &mut serde_json::Map<String, serde_json::Value>) {
    let colonists = match obs_obj.get_mut("Colonists").and_then(|v| v.as_array_mut()) {
        Some(arr) => arr,
        None => return,
    };
    for colonist in colonists.iter_mut() {
        if let Some(obj) = colonist.as_object_mut() {
            for field in COLONIST_STABLE_FIELDS {
                obj.remove(*field);
            }
        }
    }
}

/// Recursively strip null values, empty arrays, empty objects, and false booleans
/// from all objects in the tree. Also strips zero-value numbers in certain contexts.
fn strip_noise(value: &mut serde_json::Value) {
    match value {
        serde_json::Value::Object(map) => {
            // Recurse first
            for v in map.values_mut() {
                strip_noise(v);
            }
            // Then remove empty/null/false fields
            let remove: Vec<String> = map
                .iter()
                .filter(|(_, v)| {
                    v.is_null()
                        || v.as_array().is_some_and(|a| a.is_empty())
                        || v.as_object().is_some_and(|o| o.is_empty())
                })
                .map(|(k, _)| k.clone())
                .collect();
            for key in remove {
                map.remove(&key);
            }
        }
        serde_json::Value::Array(arr) => {
            for item in arr.iter_mut() {
                strip_noise(item);
            }
        }
        _ => {}
    }
}

/// Strip zero values from resource stockpile maps.
fn strip_zero_resources(obs_obj: &mut serde_json::Map<String, serde_json::Value>) {
    let resources = match obs_obj.get_mut("Resources").and_then(|v| v.as_object_mut()) {
        Some(r) => r,
        None => return,
    };
    if let Some(stockpiles) = resources
        .get_mut("Stockpiles")
        .and_then(|v| v.as_object_mut())
    {
        let zeros: Vec<String> = stockpiles
            .iter()
            .filter(|(_, v)| v.as_f64() == Some(0.0) || v.as_i64() == Some(0))
            .map(|(k, _)| k.clone())
            .collect();
        for key in zeros {
            stockpiles.remove(&key);
        }
    }
}

/// In compact mode, filter Research.Available to only CanStart=true projects.
/// Agents rarely need to see prerequisites they can't start yet.
/// Also strips null/zero fields from the Research object itself.
fn strip_unstartable_research(obs_obj: &mut serde_json::Map<String, serde_json::Value>) {
    let research = match obs_obj.get_mut("Research").and_then(|v| v.as_object_mut()) {
        Some(r) => r,
        None => return,
    };
    if let Some(available) = research.get_mut("Available").and_then(|v| v.as_array_mut()) {
        available.retain(|item| {
            item.get("CanStart")
                .and_then(|v| v.as_bool())
                .unwrap_or(false)
        });
        // Remove the CanStart field itself since they're all true now
        for item in available.iter_mut() {
            if let Some(obj) = item.as_object_mut() {
                obj.remove("CanStart");
                obj.remove("MissingPrereqs");
            }
        }
    }
    // Strip null/zero-value fields (CurrentProject: null, Progress: 0.0)
    let nulls: Vec<String> = research
        .iter()
        .filter(|(_, v)| v.is_null() || v.as_f64() == Some(0.0))
        .map(|(k, _)| k.clone())
        .collect();
    for key in nulls {
        research.remove(&key);
    }
}

/// Strip redundant/default fields from the StepResult wrapper.
/// Tick, StateHash, Events are duplicated inside Observation — remove from wrapper.
/// Default-value fields (Reward=0, Done=false, etc.) add no information on observe.
fn strip_step_wrapper(obj: &mut serde_json::Map<String, serde_json::Value>) {
    // Only strip wrapper if there's a nested Observation (i.e. this is a StepResult)
    if !obj.contains_key("Observation") {
        return;
    }
    // Remove fields duplicated inside Observation
    const DUPLICATED: &[&str] = &["Tick", "StateHash", "Events"];
    for field in DUPLICATED {
        obj.remove(*field);
    }
    // Remove default-value wrapper fields that add no info
    if obj.get("Reward").and_then(|v| v.as_f64()) == Some(0.0) {
        obj.remove("Reward");
    }
    if obj
        .get("RewardComponents")
        .and_then(|v| v.as_object())
        .is_some_and(|o| o.is_empty())
    {
        obj.remove("RewardComponents");
    }
    if obj.get("Done") == Some(&serde_json::Value::Bool(false)) {
        obj.remove("Done");
    }
    if obj.get("Truncated") == Some(&serde_json::Value::Bool(false)) {
        obj.remove("Truncated");
    }
    if obj.get("StepId").and_then(|v| v.as_u64()) == Some(0) {
        obj.remove("StepId");
    }
    if obj
        .get("FrameIds")
        .and_then(|v| v.as_object())
        .is_some_and(|o| o.is_empty())
    {
        obj.remove("FrameIds");
    }
    if obj
        .get("Events")
        .and_then(|v| v.as_array())
        .is_some_and(|a| a.is_empty())
    {
        obj.remove("Events");
    }
}

/// Limit terrain regions to top N by cell count, then by fertility
fn apply_terrain_limit(obs_obj: &mut serde_json::Map<String, serde_json::Value>, limit: usize) {
    if let Some(terrain) = obs_obj.get_mut("Terrain").and_then(|v| v.as_object_mut()) {
        // Sort and truncate regions, capturing metadata
        let (total, showing) = if let Some(regions) = terrain
            .get_mut("FertileRegions")
            .and_then(|v| v.as_array_mut())
        {
            regions.sort_by(|a, b| {
                let fert_cmp = b
                    .get("Fertility")
                    .and_then(|v| v.as_f64())
                    .unwrap_or(0.0)
                    .partial_cmp(&a.get("Fertility").and_then(|v| v.as_f64()).unwrap_or(0.0))
                    .unwrap_or(std::cmp::Ordering::Equal);
                if fert_cmp == std::cmp::Ordering::Equal {
                    b.get("CellCount")
                        .and_then(|v| v.as_u64())
                        .unwrap_or(0)
                        .cmp(&a.get("CellCount").and_then(|v| v.as_u64()).unwrap_or(0))
                } else {
                    fert_cmp
                }
            });
            let total = regions.len();
            regions.truncate(limit);
            (total, regions.len())
        } else {
            return;
        };
        terrain.insert("TotalRegions".to_string(), serde_json::json!(total));
        terrain.insert("Showing".to_string(), serde_json::json!(showing));
    }
}

/// Check if any requested sections require full state (not available in deltas)
fn needs_full_state(sections: &[String]) -> bool {
    sections.iter().any(|s| {
        let base = s.split('.').next().unwrap_or(s);
        matches!(
            section_to_field(base),
            "Entities"
                | "Terrain"
                | "Rooms"
                | "BedAssignments"
                | "PowerGrid"
                | "Map"
                | "Visitors"
                | "Colonists"
        )
    })
}

async fn handle_observe<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
    registry: &Arc<RwLock<AgentRegistry>>,
) -> Result<serde_json::Value> {
    let p: ObserveParams = serde_json::from_value(params).unwrap_or(ObserveParams {
        agent_id: None,
        include: None,
        limit: None,
    });

    let mut env = environment.write().await;

    // If drilldown sections are requested, force full state via RequestFullState
    // and use the step result directly (the flag gets consumed by the step)
    let result = if let Some(ref sections) = p.include {
        if needs_full_state(sections) {
            let agent_id = if let Some(id) = p.agent_id.clone() {
                id
            } else {
                let reg = registry.read().await;
                reg.list()
                    .first()
                    .map(|e| e.agent_id.clone())
                    .unwrap_or_else(|| "default".into())
            };
            match env
                .step(
                    &agent_id,
                    Action::Parameterized {
                        action_type: "RequestFullState".into(),
                        params: Default::default(),
                    },
                    0,
                )
                .await
            {
                Ok(step_result) => step_result,
                Err(e) => {
                    tracing::debug!("RequestFullState failed, falling back to observe: {}", e);
                    env.observe().await?
                }
            }
        } else {
            env.observe().await?
        }
    } else {
        env.observe().await?
    };

    let value = serde_json::to_value(result)?;
    let config = FilterConfig {
        include: p.include,
        limit: p.limit,
        step_mode: false,
    };
    Ok(filter_observation(value, &config))
}

async fn handle_reset<E: GameEnvironment>(
    params: serde_json::Value,
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let p: ResetParams = serde_json::from_value(params)?;

    let mut env = environment.write().await;
    let obs = env.reset(p.seed, p.scenario).await?;

    // Filter to compact view
    let value = serde_json::to_value(obs)?;
    Ok(filter_observation(value, &FilterConfig::compact()))
}

async fn handle_state_hash<E: GameEnvironment>(
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let mut env = environment.write().await;
    let hash = env.state_hash().await?;

    Ok(serde_json::json!({ "hash": hash }))
}

async fn handle_episode_summary<E: GameEnvironment>(
    environment: &Arc<RwLock<E>>,
) -> Result<serde_json::Value> {
    let mut env = environment.write().await;
    let summary = env.episode_summary().await?;

    Ok(serde_json::to_value(summary)?)
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

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // --- normalize_action tests ---

    #[test]
    fn test_correct_format_unchanged() {
        let mut params = json!({"AgentId": "p1", "Action": {"Type": "Wait"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_parameterized_with_params() {
        let mut params = json!({"AgentId": "p1", "Action": {"Type": "Draft", "ColonistId": "H1"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Draft");
        assert_eq!(params["Action"]["ColonistId"], "H1");
    }

    #[test]
    fn test_string_action() {
        let mut params = json!({"AgentId": "p1", "Action": "Wait"});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_null_action_fails() {
        let mut params = json!({"AgentId": "p1", "Action": null});
        assert!(normalize_action(&mut params).is_err());
    }

    #[test]
    fn test_missing_action_fails() {
        let mut params = json!({"AgentId": "p1"});
        assert!(normalize_action(&mut params).is_err());
    }

    #[test]
    fn test_empty_object_fails() {
        let mut params = json!({"AgentId": "p1", "Action": {}});
        assert!(normalize_action(&mut params).is_err());
    }

    #[test]
    fn test_lowercase_type() {
        let mut params = json!({"AgentId": "p1", "Action": {"type": "Wait"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_lowercase_type_with_params() {
        let mut params = json!({"AgentId": "p1", "Action": {"type": "Draft", "ColonistId": "H1"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Draft");
        assert_eq!(params["Action"]["ColonistId"], "H1");
    }

    #[test]
    fn test_redundant_action_key() {
        let mut params = json!({"AgentId": "p1", "Action": {"Type": "Wait", "Action": "Wait"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
        assert!(params["Action"].get("Action").is_none());
    }

    #[test]
    fn test_action_type_key() {
        let mut params = json!({"AgentId": "p1", "Action": {"action_type": "Wait"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_double_wrapped_object() {
        let mut params = json!({"AgentId": "p1", "Action": {"Action": {"Type": "Wait"}}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_double_wrapped_string() {
        let mut params = json!({"AgentId": "p1", "Action": {"Action": "Wait"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["Type"], "Wait");
    }

    #[test]
    fn test_boolean_action_fails() {
        let mut params = json!({"AgentId": "p1", "Action": true});
        assert!(normalize_action(&mut params).is_err());
    }

    #[test]
    fn test_object_no_type_fails() {
        let mut params = json!({"AgentId": "p1", "Action": {"ColonistId": "H1"}});
        assert!(normalize_action(&mut params).is_err());
    }

    #[test]
    fn test_discrete_passthrough() {
        let mut params = json!({"AgentId": "p1", "Action": 42});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"], 42);
    }

    #[test]
    fn test_continuous_passthrough() {
        let mut params = json!({"AgentId": "p1", "Action": [1.0, 2.5]});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"], json!([1.0, 2.5]));
    }

    // --- normalize_step_fields tests ---

    #[test]
    fn test_fields_snake_case() {
        let mut params = json!({"agent_id": "p1", "action": {"Type": "Wait"}, "ticks": 5});
        normalize_step_fields(&mut params);
        assert_eq!(params["AgentId"], "p1");
        assert!(params["Action"].is_object());
        assert_eq!(params["Ticks"], 5);
    }

    #[test]
    fn test_fields_camel_case() {
        let mut params = json!({"agentId": "p1", "Action": {"Type": "Wait"}});
        normalize_step_fields(&mut params);
        assert_eq!(params["AgentId"], "p1");
    }

    #[test]
    fn test_fields_already_correct() {
        let mut params = json!({"AgentId": "p1", "Action": {"Type": "Wait"}, "Ticks": 1});
        let original = params.clone();
        normalize_step_fields(&mut params);
        assert_eq!(params, original);
    }

    // --- End-to-end deserialization tests ---

    #[test]
    fn test_e2e_string_action() {
        let mut params = json!({"AgentId": "p1", "Action": "Draft", "Ticks": 1});
        normalize_step_fields(&mut params);
        normalize_action(&mut params).unwrap();
        let p: SimStepParams = serde_json::from_value(params).unwrap();
        assert_eq!(p.agent_id, "p1");
        match p.action {
            Action::Parameterized { action_type, .. } => assert_eq!(action_type, "Draft"),
            _ => panic!("Expected Parameterized"),
        }
    }

    #[test]
    fn test_e2e_lowercase_type() {
        let mut params = json!({"AgentId": "p1", "Action": {"type": "Draft", "ColonistId": "H1"}});
        normalize_step_fields(&mut params);
        normalize_action(&mut params).unwrap();
        let p: SimStepParams = serde_json::from_value(params).unwrap();
        match p.action {
            Action::Parameterized {
                action_type,
                params,
            } => {
                assert_eq!(action_type, "Draft");
                assert_eq!(params.get("ColonistId").unwrap(), "H1");
            }
            _ => panic!("Expected Parameterized"),
        }
    }

    #[test]
    fn test_e2e_snake_case_fields() {
        let mut params = json!({"agent_id": "p1", "action": {"Type": "Draft"}});
        normalize_step_fields(&mut params);
        normalize_action(&mut params).unwrap();
        let p: SimStepParams = serde_json::from_value(params).unwrap();
        assert_eq!(p.agent_id, "p1");
    }

    #[test]
    fn test_colonist_name_normalized() {
        let mut params =
            json!({"AgentId": "p1", "Action": {"Type": "Draft", "ColonistName": "Lizzie"}});
        normalize_action(&mut params).unwrap();
        assert_eq!(params["Action"]["ColonistId"], "Lizzie");
        assert!(params["Action"].get("ColonistName").is_none());
    }
}
