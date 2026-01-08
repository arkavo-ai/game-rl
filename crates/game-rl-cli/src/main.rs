//! Game-RL MCP Server
//!
//! Unified server that auto-detects which game is running:
//! - RimWorld via Unix socket (/tmp/gamerl-rimworld.sock)
//! - Project Zomboid via file IPC (~/Zomboid/Lua/gamerl_response.json)
//! - Factorio via RCON (localhost:27015)
//!
//! Detection checks all sources and picks the most recently active one.

use anyhow::Result;
use factorio_bridge::{FactorioBridge, FactorioConfig};
use game_rl_server::{GameEnvironment, GameRLServer};
use harmony_bridge::HarmonyBridge;
use std::path::Path;
use std::time::{Duration, SystemTime};
use tokio::net::TcpStream;
use tokio::time::sleep;
use tracing::{Level, debug, info, warn};
use tracing_subscriber::FmtSubscriber;
use zomboid_bridge::{ZomboidBridge, ZomboidConfig};

/// Candidate game with its freshness timestamp
struct GameCandidate {
    name: &'static str,
    modified: SystemTime,
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

/// Get file modification time, or None if file doesn't exist
fn get_mtime(path: &Path) -> Option<SystemTime> {
    std::fs::metadata(path).ok()?.modified().ok()
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

    // Auto-detect game - check all sources and pick most recent
    loop {
        let mut candidates: Vec<GameCandidate> = Vec::new();

        // Check all potential game sources
        if let Some(mtime) = get_mtime(Path::new(RIMWORLD_SOCKET)) {
            candidates.push(GameCandidate {
                name: "RimWorld",
                modified: mtime,
            });
        }

        if let Some(mtime) = get_mtime(&zomboid_response) {
            candidates.push(GameCandidate {
                name: "Zomboid",
                modified: mtime,
            });
        }

        // For Factorio, check if RCON port is open (use current time as "freshness")
        if TcpStream::connect(FACTORIO_RCON_ADDR).await.is_ok() {
            candidates.push(GameCandidate {
                name: "Factorio",
                modified: SystemTime::now(), // Active connection = most fresh
            });
        }

        // Sort by most recent first
        candidates.sort_by(|a, b| b.modified.cmp(&a.modified));

        // Try to connect to each candidate in order of freshness
        for candidate in &candidates {
            debug!(
                "Trying {} (modified: {:?})",
                candidate.name, candidate.modified
            );

            match candidate.name {
                "RimWorld" => {
                    info!("RimWorld socket detected: {}", RIMWORLD_SOCKET);
                    let mut bridge = HarmonyBridge::new(RIMWORLD_SOCKET);
                    match bridge.connect().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("RimWorld socket exists but connect failed: {}", e),
                    }
                }
                "Zomboid" => {
                    info!("Project Zomboid IPC detected: {:?}", zomboid_response);
                    let mut bridge = ZomboidBridge::with_config(zomboid_config.clone());
                    match bridge.init().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("Zomboid response exists but init failed: {}", e),
                    }
                }
                "Factorio" => {
                    info!("Factorio RCON detected at {}", FACTORIO_RCON_ADDR);
                    let mut bridge = FactorioBridge::with_config(factorio_config.clone());
                    match bridge.init().await {
                        Ok(()) => return run_with_bridge(bridge).await,
                        Err(e) => warn!("Factorio RCON exists but init failed: {}", e),
                    }
                }
                _ => {}
            }
        }

        if candidates.is_empty() {
            info!("Waiting for game connection...");
            info!("  RimWorld: socket at {}", RIMWORLD_SOCKET);
            info!("  Zomboid:  file at {:?}", zomboid_response);
            info!(
                "  Factorio: RCON at {} (enable in config.ini, host multiplayer)",
                FACTORIO_RCON_ADDR
            );
        } else {
            info!("No game connected successfully, retrying...");
        }
        sleep(Duration::from_secs(2)).await;
    }
}
