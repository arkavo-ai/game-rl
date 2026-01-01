//! Transport abstractions for game bridges
//!
//! Provides AsyncReader/AsyncWriter traits that can be implemented
//! for different transport mechanisms (Unix sockets, TCP, named pipes).

use crate::protocol::{GameMessage, deserialize};
use async_trait::async_trait;
use game_rl_core::{GameRLError, Result};
use game_rl_server::environment::StateUpdate;
use tokio::sync::{broadcast, mpsc, oneshot};
use tracing::{debug, error, warn};

/// Trait for async reading from a transport
#[async_trait]
pub trait AsyncReader: Send {
    /// Read a complete message from the transport
    /// Messages are length-prefixed: 4-byte little-endian length + JSON payload
    async fn read_message(&mut self) -> Result<Vec<u8>>;
}

/// Trait for async writing to a transport
#[async_trait]
pub trait AsyncWriter: Send + Sync {
    /// Write a complete message to the transport
    /// Messages are length-prefixed: 4-byte little-endian length + JSON payload
    async fn write_message(&mut self, data: &[u8]) -> Result<()>;
}

/// Background reader task that handles incoming messages
///
/// This task:
/// - Receives messages from the game via the transport
/// - Routes StateUpdate messages to broadcast subscribers
/// - Routes response messages to pending request channels (FIFO order)
///
/// # Arguments
/// - `reader`: The transport reader
/// - `request_rx`: Channel receiving (request, response_channel) pairs from main task
/// - `event_tx`: Broadcast sender for StateUpdate events
pub async fn reader_task<R: AsyncReader>(
    mut reader: R,
    mut request_rx: mpsc::Receiver<(GameMessage, oneshot::Sender<Result<GameMessage>>)>,
    event_tx: broadcast::Sender<StateUpdate>,
) {
    // Queue of pending response channels (FIFO - responses come in order)
    let mut pending: Vec<oneshot::Sender<Result<GameMessage>>> = Vec::new();

    loop {
        tokio::select! {
            // New request from main task
            req = request_rx.recv() => {
                match req {
                    Some((_msg, response_tx)) => {
                        pending.push(response_tx);
                    }
                    None => {
                        // Channel closed, exit
                        debug!("Request channel closed, reader task exiting");
                        break;
                    }
                }
            }

            // Message from game
            msg_result = reader.read_message() => {
                match msg_result {
                    Ok(data) => {
                        // Log incoming message
                        let json_preview: String = String::from_utf8_lossy(&data).chars().take(200).collect();
                        debug!("[Game→Rust] len={} json={}", data.len(), json_preview);

                        match deserialize(&data) {
                            Ok(msg) => {
                                match msg {
                                    // Push notification - broadcast to subscribers
                                    GameMessage::StateUpdate { tick, state, events } => {
                                        let update = StateUpdate {
                                            tick,
                                            state,
                                            events,
                                        };
                                        // Ignore send errors (no subscribers)
                                        let _ = event_tx.send(update);
                                    }

                                    // Response to a pending request
                                    _ => {
                                        if let Some(response_tx) = pending.pop() {
                                            let _ = response_tx.send(Ok(msg));
                                        } else {
                                            warn!("Received response but no pending request: {:?}", msg);
                                        }
                                    }
                                }
                            }
                            Err(e) => {
                                error!("Failed to deserialize message: {}", e);
                                // Send error to pending request if any
                                if let Some(response_tx) = pending.pop() {
                                    let _ = response_tx.send(Err(GameRLError::SerializationError(e.to_string())));
                                }
                            }
                        }
                    }
                    Err(e) => {
                        error!("Reader task failed: {}", e);
                        // Notify all pending requests of failure
                        for response_tx in pending.drain(..) {
                            let _ = response_tx.send(Err(GameRLError::IpcError("Connection lost".into())));
                        }
                        break;
                    }
                }
            }
        }
    }
}
