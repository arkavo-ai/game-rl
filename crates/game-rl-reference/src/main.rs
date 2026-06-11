//! GridColony reference environment — Game-RL MCP server on stdio

use anyhow::Result;
use clap::Parser;
use game_rl_reference::ReferenceEnv;
use game_rl_reference::env;
use game_rl_server::GameRLServer;
use tracing_subscriber::FmtSubscriber;

#[derive(Parser)]
#[command(
    name = "game-rl-reference",
    about = "GridColony — Game-RL draft-02 reference environment (Level 3)"
)]
struct Cli {
    /// Initial scenario: default, fertile-corner, threat-south, scattered-resources
    /// (append +dr for domain randomization)
    #[arg(long, default_value = "default")]
    scenario: String,

    /// World seed
    #[arg(long, default_value = "0")]
    seed: u64,
}

#[tokio::main]
async fn main() -> Result<()> {
    // Logs go to stderr — stdout is the MCP transport
    let subscriber = FmtSubscriber::builder()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .with_writer(std::io::stderr)
        .finish();
    tracing::subscriber::set_global_default(subscriber)?;

    let cli = Cli::parse();
    let environment = ReferenceEnv::new(cli.seed, &cli.scenario);
    let manifest = env::ReferenceEnv::build_manifest();
    tracing::info!(
        "GridColony reference environment: scenario={}, seed={}",
        cli.scenario,
        cli.seed
    );

    let server = GameRLServer::new(environment, manifest);
    server.run_stdio().await?;
    Ok(())
}
