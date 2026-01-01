//! zomboid-server: MCP server for Project Zomboid
//!
//! This binary connects to Project Zomboid via TCP and exposes the game
//! environment through the Model Context Protocol (MCP) over stdio.

use anyhow::Result;
use game_rl_server::GameRLServer;
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
        // Parse host:port from first argument
        let addr = &args[1];
        let parts: Vec<&str> = addr.split(':').collect();
        match parts.as_slice() {
            [host, port] => ZomboidConfig {
                host: host.to_string(),
                port: port.parse().unwrap_or(19731),
                ..Default::default()
            },
            [port] => ZomboidConfig {
                port: port.parse().unwrap_or(19731),
                ..Default::default()
            },
            _ => ZomboidConfig::default(),
        }
    } else {
        ZomboidConfig::default()
    };

    info!(
        "Starting zomboid-server, connecting to {}:{}",
        config.host, config.port
    );

    // Create bridge and connect
    let mut bridge = ZomboidBridge::with_config(config.clone());

    // Keep trying to connect
    loop {
        info!("Waiting for Project Zomboid at {}:{}...", config.host, config.port);
        match bridge.connect().await {
            Ok(()) => break,
            Err(e) => {
                info!("Connection failed: {}, retrying in 2 seconds...", e);
                tokio::time::sleep(std::time::Duration::from_secs(2)).await;
            }
        }
    }

    // Get manifest and start MCP server
    let manifest = bridge.manifest();
    info!("Connected to {}, starting MCP server", manifest.name);

    let server = GameRLServer::new(bridge, manifest);
    server.run_stdio().await?;

    Ok(())
}
