//! stdio transport for MCP JSON-RPC

use crate::GameRLServer;
use crate::environment::GameEnvironment;
use crate::handler;
use crate::mcp::{Notification, Request};
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
        let env = server.environment().read().await;
        env.subscribe_events()
    };

    // Spawn event forwarder task if push is supported
    let stdout_for_events = stdout.clone();
    let _event_task = event_rx.map(|mut rx| {
        tokio::spawn(async move {
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
        })
    });

    loop {
        line.clear();
        let bytes_read = reader.read_line(&mut line).await.map_err(|e| {
            game_rl_core::GameRLError::IpcError(format!("Failed to read stdin: {}", e))
        })?;

        if bytes_read == 0 {
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

        let response = handler::handle_request(&request, &server).await;
        let response_json = serde_json::to_string(&response)
            .map_err(|e| game_rl_core::GameRLError::SerializationError(e.to_string()))?;

        debug!("Sending: {}", response_json);

        {
            let mut out = stdout.lock().await;
            out.write_all(response_json.as_bytes()).await.map_err(|e| {
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
        let mut env = server.environment().write().await;
        let _ = env.shutdown().await;
    }

    Ok(())
}
