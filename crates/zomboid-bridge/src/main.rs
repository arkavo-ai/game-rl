//! zomboid-server: MCP server for Project Zomboid
//!
//! This binary uses file-based IPC to communicate with Project Zomboid,
//! then exposes the game environment through the Model Context Protocol (MCP) over stdio.

use anyhow::Result;
use game_rl_server::GameRLServer;
use std::path::PathBuf;
use tracing::{info, Level};
use tracing_subscriber::FmtSubscriber;
use zomboid_bridge::{ZomboidBridge, bridge::ZomboidConfig};

#[tokio::main]
async fn main() -> Result<()> {
    // Initialize logging
    let subscriber = FmtSubscriber::builder()
        .with_max_level(Level::DEBUG)
        .with_writer(std::io::stderr)
        .finish();
    tracing::subscriber::set_global_default(subscriber)?;

    // Parse command line arguments
    let args: Vec<String> = std::env::args().collect();
    let config = if args.len() > 1 {
        // Custom IPC path
        ZomboidConfig {
            ipc_path: PathBuf::from(&args[1]),
            ..Default::default()
        }
    } else {
        ZomboidConfig::default()
    };

    info!(
        "Starting zomboid-server, IPC path: {:?}",
        config.ipc_path
    );

    // Create bridge and initialize
    let mut bridge = ZomboidBridge::with_config(config);

    // Initialize and wait for game to connect
    bridge.init().await?;

    // Get manifest and start MCP server
    let manifest = bridge.manifest();
    info!("Game connected: {}, starting MCP server", manifest.name);

    let server = GameRLServer::new(bridge, manifest);
    server.run_stdio().await?;

    Ok(())
}
