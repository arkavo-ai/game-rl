//! Harmony bridge server binary
//!
//! This binary connects to a .NET game via IPC and exposes it as an MCP server
//! on stdio for AI agents to connect to.

use anyhow::Result;
use game_rl_server::GameRLServer;
use harmony_bridge::HarmonyBridge;
use std::time::Duration;
use tokio::time::sleep;
use tracing::{Level, info, warn};
use tracing_subscriber::FmtSubscriber;

#[tokio::main]
async fn main() -> Result<()> {
    // Initialize logging
    let subscriber = FmtSubscriber::builder()
        .with_max_level(Level::INFO)
        .with_writer(std::io::stderr)
        .finish();
    tracing::subscriber::set_global_default(subscriber)?;

    // Parse command line arguments
    let args: Vec<String> = std::env::args().collect();
    let socket_path = args
        .get(1)
        .map(|s| s.as_str())
        .unwrap_or("/tmp/arkavo_game_mcp.sock");

    info!("Harmony bridge starting");
    info!("Socket path: {}", socket_path);

    // Connect to game with polling - wait for game to start
    let mut bridge = HarmonyBridge::new(socket_path);
    let mut delay = Duration::from_secs(1);
    let max_delay = Duration::from_secs(10);

    loop {
        match bridge.connect().await {
            Ok(()) => break,
            Err(e) => {
                warn!("Waiting for game: {} (retrying in {:?})", e, delay);
                sleep(delay).await;
                delay = std::cmp::min(delay * 2, max_delay);
            }
        }
    }

    // Get manifest from bridge
    let manifest = bridge.manifest();
    info!("Connected to {} v{}", manifest.name, manifest.version);

    // Create and run MCP server
    let server = GameRLServer::new(bridge, manifest);
    server
        .run_stdio()
        .await
        .map_err(|e| anyhow::anyhow!("Server error: {}", e))?;

    info!("Harmony bridge shutting down");
    Ok(())
}
