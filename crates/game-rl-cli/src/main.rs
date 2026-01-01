//! Game-RL MCP Server
//!
//! Unified server that auto-detects which game is running:
//! - RimWorld via Unix socket (/tmp/arkavo_game_mcp.sock)
//! - Project Zomboid via file IPC (~/Zomboid/Lua/gamerl_status.json)

use anyhow::Result;
use game_rl_server::{GameEnvironment, GameRLServer};
use harmony_bridge::HarmonyBridge;
use std::path::Path;
use std::time::Duration;
use tokio::time::sleep;
use tracing::{info, warn, Level};
use tracing_subscriber::FmtSubscriber;
use zomboid_bridge::{ZomboidBridge, ZomboidConfig};

enum DetectedGame {
    RimWorld(HarmonyBridge),
    Zomboid(ZomboidBridge),
}

const RIMWORLD_SOCKET: &str = "/tmp/arkavo_game_mcp.sock";

/// Run the MCP server with a game bridge
async fn run_with_bridge<E: GameEnvironment>(bridge: E) -> Result<()> {
    let manifest = bridge.manifest();
    info!("Connected to {} v{}", manifest.name, manifest.version);
    let server = GameRLServer::new(bridge, manifest);
    server.run_stdio().await?;
    Ok(())
}

#[tokio::main]
async fn main() -> Result<()> {
    // Initialize logging
    let subscriber = FmtSubscriber::builder()
        .with_max_level(Level::DEBUG)
        .with_writer(std::io::stderr)
        .finish();
    tracing::subscriber::set_global_default(subscriber)?;

    info!("Game-RL MCP server starting (auto-detecting game)...");

    // Detection paths
    let zomboid_config = ZomboidConfig::default();
    let zomboid_status = zomboid_config.ipc_path.join("gamerl_status.json");

    // Auto-detect game
    let game = loop {
        // Check RimWorld socket
        if Path::new(RIMWORLD_SOCKET).exists() {
            info!("RimWorld socket detected: {}", RIMWORLD_SOCKET);
            let mut bridge = HarmonyBridge::new(RIMWORLD_SOCKET);
            match bridge.connect().await {
                Ok(()) => break DetectedGame::RimWorld(bridge),
                Err(e) => warn!("RimWorld socket exists but connect failed: {}", e),
            }
        }

        // Check Zomboid status file
        if zomboid_status.exists() {
            info!("Project Zomboid IPC detected: {:?}", zomboid_status);
            let mut bridge = ZomboidBridge::with_config(zomboid_config.clone());
            match bridge.init().await {
                Ok(()) => break DetectedGame::Zomboid(bridge),
                Err(e) => warn!("Zomboid status exists but init failed: {}", e),
            }
        }

        info!(
            "Waiting for game... (RimWorld: {}, Zomboid: {:?})",
            RIMWORLD_SOCKET, zomboid_status
        );
        sleep(Duration::from_secs(2)).await;
    };

    // Run server with detected bridge
    match game {
        DetectedGame::RimWorld(bridge) => run_with_bridge(bridge).await?,
        DetectedGame::Zomboid(bridge) => run_with_bridge(bridge).await?,
    }

    Ok(())
}
