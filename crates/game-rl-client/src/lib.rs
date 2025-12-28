//! Game-RL MCP client for connecting to environments
//!
//! This crate provides a client for connecting to Game-RL environments
//! via the MCP protocol over stdio.

use game_rl_core::{
    Action, AgentConfig, AgentId, AgentManifest, AgentType, GameManifest, GameRLError, Observation,
    Result, StepResult,
};
use serde::{Deserialize, Serialize};
use std::process::Stdio;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::process::{Child, Command};
use tracing::debug;

/// Client for connecting to Game-RL environments
pub struct GameRLClient {
    child: Child,
    request_id: i64,
}

#[derive(Debug, Serialize)]
struct Request {
    jsonrpc: &'static str,
    id: i64,
    method: String,
    params: serde_json::Value,
}

#[derive(Debug, Deserialize)]
struct Response {
    #[allow(dead_code)]
    jsonrpc: String,
    #[allow(dead_code)]
    id: serde_json::Value,
    result: Option<serde_json::Value>,
    error: Option<RpcError>,
}

#[derive(Debug, Deserialize)]
struct RpcError {
    code: i32,
    message: String,
}

impl GameRLClient {
    /// Spawn a new environment process and connect to it
    pub async fn spawn(command: &str, args: &[&str]) -> Result<Self> {
        let child = Command::new(command)
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(|e| GameRLError::IpcError(format!("Failed to spawn process: {}", e)))?;

        Ok(Self {
            child,
            request_id: 0,
        })
    }

    /// Initialize the MCP connection and return the game manifest
    pub async fn connect(
        &mut self,
        client_name: &str,
        client_version: &str,
    ) -> Result<GameManifest> {
        // Send initialize request
        let _init_result = self
            .send_request(
                "initialize",
                serde_json::json!({
                    "protocolVersion": "2025-11-25",
                    "capabilities": {
                        "tools": {},
                        "resources": { "subscribe": true }
                    },
                    "clientInfo": {
                        "name": client_name,
                        "version": client_version
                    }
                }),
            )
            .await?;

        // Send initialized notification
        self.send_request("initialized", serde_json::json!({}))
            .await?;

        // Read manifest from resources
        let manifest_result = self
            .send_request(
                "resources/read",
                serde_json::json!({ "uri": "game://manifest" }),
            )
            .await?;

        // Parse manifest from response
        let contents = manifest_result
            .get("contents")
            .and_then(|c| c.as_array())
            .and_then(|a| a.first())
            .and_then(|c| c.get("text"))
            .and_then(|t| t.as_str())
            .ok_or_else(|| GameRLError::ProtocolError("Invalid manifest response".into()))?;

        let manifest: GameManifest = serde_json::from_str(contents)?;
        Ok(manifest)
    }

    /// Register an agent with the environment
    pub async fn register_agent(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
        config: AgentConfig,
    ) -> Result<AgentManifest> {
        let result = self
            .call_tool(
                "register_agent",
                serde_json::json!({
                    "agent_id": agent_id,
                    "agent_type": agent_type,
                    "config": config
                }),
            )
            .await?;

        serde_json::from_value(result).map_err(Into::into)
    }

    /// Execute an action and get the observation
    pub async fn step(
        &mut self,
        agent_id: &AgentId,
        action: Action,
        ticks: u32,
    ) -> Result<StepResult> {
        let result = self
            .call_tool(
                "sim_step",
                serde_json::json!({
                    "agent_id": agent_id,
                    "action": action,
                    "ticks": ticks
                }),
            )
            .await?;

        serde_json::from_value(result).map_err(Into::into)
    }

    /// Reset the environment
    pub async fn reset(
        &mut self,
        seed: Option<u64>,
        scenario: Option<String>,
    ) -> Result<Observation> {
        let result = self
            .call_tool(
                "reset",
                serde_json::json!({
                    "seed": seed,
                    "scenario": scenario
                }),
            )
            .await?;

        serde_json::from_value(result).map_err(Into::into)
    }

    /// Get state hash for determinism verification
    pub async fn state_hash(&mut self) -> Result<String> {
        let result = self
            .call_tool("get_state_hash", serde_json::json!({}))
            .await?;

        result
            .get("hash")
            .and_then(|h| h.as_str())
            .map(|s| s.to_string())
            .ok_or_else(|| GameRLError::ProtocolError("Invalid state_hash response".into()))
    }

    /// Call an MCP tool
    async fn call_tool(
        &mut self,
        name: &str,
        arguments: serde_json::Value,
    ) -> Result<serde_json::Value> {
        let result = self
            .send_request(
                "tools/call",
                serde_json::json!({
                    "name": name,
                    "arguments": arguments
                }),
            )
            .await?;

        // Extract text content from MCP tool response
        let content = result
            .get("content")
            .and_then(|c| c.as_array())
            .and_then(|a| a.first())
            .and_then(|c| c.get("text"))
            .and_then(|t| t.as_str())
            .ok_or_else(|| GameRLError::ProtocolError("Invalid tool response".into()))?;

        serde_json::from_str(content).map_err(Into::into)
    }

    /// Send a JSON-RPC request and wait for response
    async fn send_request(
        &mut self,
        method: &str,
        params: serde_json::Value,
    ) -> Result<serde_json::Value> {
        self.request_id += 1;
        let request = Request {
            jsonrpc: "2.0",
            id: self.request_id,
            method: method.to_string(),
            params,
        };

        let stdin = self
            .child
            .stdin
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("No stdin".into()))?;
        let stdout = self
            .child
            .stdout
            .as_mut()
            .ok_or_else(|| GameRLError::IpcError("No stdout".into()))?;

        // Write request
        let request_json = serde_json::to_string(&request)?;
        debug!("Sending: {}", request_json);
        stdin
            .write_all(request_json.as_bytes())
            .await
            .map_err(|e| GameRLError::IpcError(format!("Write failed: {}", e)))?;
        stdin
            .write_all(b"\n")
            .await
            .map_err(|e| GameRLError::IpcError(format!("Write newline failed: {}", e)))?;
        stdin
            .flush()
            .await
            .map_err(|e| GameRLError::IpcError(format!("Flush failed: {}", e)))?;

        // Read response
        let mut reader = BufReader::new(stdout);
        let mut line = String::new();
        reader
            .read_line(&mut line)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Read failed: {}", e)))?;

        debug!("Received: {}", line.trim());

        let response: Response = serde_json::from_str(&line)?;

        if let Some(err) = response.error {
            return Err(GameRLError::ProtocolError(format!(
                "RPC error {}: {}",
                err.code, err.message
            )));
        }

        response
            .result
            .ok_or_else(|| GameRLError::ProtocolError("No result in response".into()))
    }

    /// Shutdown the environment
    pub async fn shutdown(&mut self) -> Result<()> {
        if let Some(stdin) = self.child.stdin.take() {
            drop(stdin); // Close stdin to signal EOF
        }
        let _ = self.child.wait().await;
        Ok(())
    }
}

impl Drop for GameRLClient {
    fn drop(&mut self) {
        // Try to kill the child process if still running
        let _ = self.child.start_kill();
    }
}
