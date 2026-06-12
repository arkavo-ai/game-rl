//! Minimal MCP client over a spawned server's stdio (line-delimited JSON-RPC)

use anyhow::{Context, Result, anyhow, bail};
use serde_json::{Value, json};
use std::process::Stdio;
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::process::{Child, ChildStdin, ChildStdout, Command};

pub struct McpChild {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    next_id: u64,
}

/// A tool call outcome: MCP-level error, tool error (isError), or parsed payload
#[derive(Debug)]
#[allow(dead_code)] // `code` is carried for report detail even when unread
pub enum ToolOutcome {
    /// JSON-RPC error response
    RpcError { code: i64, message: String },
    /// Tool executed and reported failure (content with isError: true)
    ToolError { message: String },
    /// Tool succeeded; payload parsed from content[0].text when JSON, else raw text
    Ok { payload: Value },
}

impl ToolOutcome {
    pub fn is_err(&self) -> bool {
        !matches!(self, ToolOutcome::Ok { .. })
    }

    pub fn error_message(&self) -> Option<&str> {
        match self {
            ToolOutcome::RpcError { message, .. } => Some(message),
            ToolOutcome::ToolError { message } => Some(message),
            ToolOutcome::Ok { .. } => None,
        }
    }

    pub fn payload(&self) -> Option<&Value> {
        match self {
            ToolOutcome::Ok { payload } => Some(payload),
            _ => None,
        }
    }
}

impl McpChild {
    pub async fn spawn(cmd: &[String], extra_env: &[(String, String)]) -> Result<Self> {
        let (program, args) = cmd.split_first().context("empty server command")?;
        let mut command = Command::new(program);
        command
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .kill_on_drop(true);
        for (k, v) in extra_env {
            command.env(k, v);
        }
        let mut child = command
            .spawn()
            .with_context(|| format!("failed to spawn server: {program}"))?;
        let stdin = child.stdin.take().context("no stdin")?;
        let stdout = BufReader::new(child.stdout.take().context("no stdout")?);
        Ok(Self {
            child,
            stdin,
            stdout,
            next_id: 1,
        })
    }

    pub async fn request(&mut self, method: &str, params: Value) -> Result<Value> {
        let id = self.next_id;
        self.next_id += 1;
        let request = json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params});
        let line = format!("{request}\n");
        self.stdin.write_all(line.as_bytes()).await?;
        self.stdin.flush().await?;

        // Read lines until we see the response with our id (skip notifications)
        loop {
            let mut buf = String::new();
            let read =
                tokio::time::timeout(Duration::from_secs(15), self.stdout.read_line(&mut buf))
                    .await
                    .map_err(|_| anyhow!("timeout waiting for response to {method}"))??;
            if read == 0 {
                bail!("server closed stdout during {method}");
            }
            let trimmed = buf.trim();
            if trimmed.is_empty() {
                continue;
            }
            let value: Value = match serde_json::from_str(trimmed) {
                Ok(v) => v,
                Err(_) => continue, // tolerate non-JSON noise on stdout
            };
            if value.get("id").and_then(|v| v.as_u64()) == Some(id) {
                return Ok(value);
            }
        }
    }

    pub async fn initialize(&mut self) -> Result<Value> {
        let response = self
            .request(
                "initialize",
                json!({
                    "protocolVersion": "2025-11-25",
                    "capabilities": {"tools": {}},
                    "clientInfo": {"name": "game-rl-conformance", "version": env!("CARGO_PKG_VERSION")}
                }),
            )
            .await?;
        // Send initialized notification (no id, no response)
        let note = json!({"jsonrpc": "2.0", "method": "notifications/initialized"});
        self.stdin.write_all(format!("{note}\n").as_bytes()).await?;
        self.stdin.flush().await?;
        response
            .get("result")
            .cloned()
            .ok_or_else(|| anyhow!("initialize returned no result: {response}"))
    }

    pub async fn list_tools(&mut self) -> Result<Vec<Value>> {
        let response = self.request("tools/list", json!({})).await?;
        response
            .pointer("/result/tools")
            .and_then(|v| v.as_array())
            .cloned()
            .ok_or_else(|| anyhow!("tools/list returned no tools array"))
    }

    pub async fn call_tool(&mut self, name: &str, arguments: Value) -> Result<ToolOutcome> {
        let response = self
            .request("tools/call", json!({"name": name, "arguments": arguments}))
            .await?;

        if let Some(err) = response.get("error") {
            return Ok(ToolOutcome::RpcError {
                code: err.get("code").and_then(|c| c.as_i64()).unwrap_or(0),
                message: err
                    .get("message")
                    .and_then(|m| m.as_str())
                    .unwrap_or("")
                    .to_string(),
            });
        }

        let result = response
            .get("result")
            .ok_or_else(|| anyhow!("tool call returned neither result nor error"))?;
        let text = result
            .pointer("/content/0/text")
            .and_then(|t| t.as_str())
            .unwrap_or("")
            .to_string();
        let is_error = result
            .get("isError")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);

        if is_error {
            return Ok(ToolOutcome::ToolError { message: text });
        }
        let payload = serde_json::from_str(&text).unwrap_or(Value::String(text));
        Ok(ToolOutcome::Ok { payload })
    }

    pub async fn shutdown(mut self) -> Result<()> {
        drop(self.stdin);
        let _ = tokio::time::timeout(Duration::from_secs(3), self.child.wait()).await;
        let _ = self.child.kill().await;
        Ok(())
    }
}
