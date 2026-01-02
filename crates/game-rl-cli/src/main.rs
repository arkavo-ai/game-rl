//! Game-RL MCP Server
//!
//! Unified server that auto-detects which game is running:
//! - RimWorld via Unix socket (/tmp/gamerl-rimworld.sock)
//! - Project Zomboid via file IPC (~/Zomboid/Lua/gamerl_response.json)
//! - Factorio via RCON (localhost:27015)

use anyhow::Result;
use factorio_bridge::{FactorioBridge, FactorioConfig};
use game_rl_server::{GameEnvironment, GameRLServer};
use harmony_bridge::HarmonyBridge;
use std::path::Path;
use std::time::Duration;
use tokio::net::TcpStream;
use tokio::time::sleep;
use tracing::{info, warn, Level};
use tracing_subscriber::FmtSubscriber;
use zomboid_bridge::{ZomboidBridge, ZomboidConfig};

enum DetectedGame {
    RimWorld(HarmonyBridge),
    Zomboid(ZomboidBridge),
    Factorio(FactorioBridge),
}

const RIMWORLD_SOCKET: &str = "/tmp/gamerl-rimworld.sock";
const FACTORIO_RCON_ADDR: &str = "127.0.0.1:27015";

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
    let zomboid_response = zomboid_config.ipc_path.join("gamerl_response.json");
    let factorio_config = FactorioConfig::default();

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

        // Check Zomboid response file (Lua writes Ready message here)
        if zomboid_response.exists() {
            info!("Project Zomboid IPC detected: {:?}", zomboid_response);
            let mut bridge = ZomboidBridge::with_config(zomboid_config.clone());
            match bridge.init().await {
                Ok(()) => break DetectedGame::Zomboid(bridge),
                Err(e) => warn!("Zomboid response exists but init failed: {}", e),
            }
        }

        // Check Factorio RCON
        if let Ok(_stream) = TcpStream::connect(FACTORIO_RCON_ADDR).await {
            info!("Factorio RCON detected at {}", FACTORIO_RCON_ADDR);
            let mut bridge = FactorioBridge::with_config(factorio_config.clone());
            match bridge.init().await {
                Ok(()) => break DetectedGame::Factorio(bridge),
                Err(e) => warn!("Factorio RCON exists but init failed: {}", e),
            }
        }

        info!(
            "Waiting for game... (RimWorld: {}, Zomboid: {:?}, Factorio: {})",
            RIMWORLD_SOCKET, zomboid_response, FACTORIO_RCON_ADDR
        );
        sleep(Duration::from_secs(2)).await;
    };

    // Run server with detected bridge
    match game {
        DetectedGame::RimWorld(bridge) => run_with_bridge(bridge).await?,
        DetectedGame::Zomboid(bridge) => run_with_bridge(bridge).await?,
        DetectedGame::Factorio(bridge) => run_with_bridge(bridge).await?,
    }

    Ok(())
}
