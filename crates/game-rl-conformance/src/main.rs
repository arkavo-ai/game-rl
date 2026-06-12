//! game-rl-conformance — runnable Game-RL draft-02 conformance suite
//!
//! Usage:
//!   game-rl-conformance --level 3 --scenario fertile-corner -- <server-cmd> [args...]
//!   game-rl-conformance --json report.json -- cargo run -p game-rl-reference

mod checks;
mod client;

use anyhow::{Result, bail};
use clap::Parser;
use serde_json::json;

#[derive(Parser)]
#[command(
    name = "game-rl-conformance",
    about = "Game-RL protocol conformance suite (spec draft-02 §14.2)"
)]
struct Cli {
    /// Target conformance level (1-3)
    #[arg(long, default_value = "3")]
    level: u8,

    /// Scenario passed to reset (use `fertile-corner` against the reference
    /// env to enable the C-SPA-INTEGRITY probe)
    #[arg(long, default_value = "default")]
    scenario: String,

    /// Seed for determinism checks
    #[arg(long, default_value = "42")]
    seed: u64,

    /// Write a JSON report to this path
    #[arg(long)]
    json: Option<String>,

    /// Environment variables to set on the server process (KEY=VALUE)
    #[arg(long = "env", value_name = "KEY=VALUE")]
    env_vars: Vec<String>,

    /// Server command (after `--`)
    #[arg(trailing_var_arg = true, required = true)]
    server_cmd: Vec<String>,
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    if !(1..=3).contains(&cli.level) {
        bail!("--level must be 1, 2, or 3");
    }
    let extra_env: Vec<(String, String)> = cli
        .env_vars
        .iter()
        .filter_map(|kv| {
            kv.split_once('=')
                .map(|(k, v)| (k.to_string(), v.to_string()))
        })
        .collect();

    eprintln!(
        "game-rl-conformance: level {} | scenario {} | seed {} | server: {}",
        cli.level,
        cli.scenario,
        cli.seed,
        cli.server_cmd.join(" ")
    );

    let mut child = client::McpChild::spawn(&cli.server_cmd, &extra_env).await?;
    let results = checks::run_suite(&mut child, cli.level, cli.seed, &cli.scenario).await;
    let _ = child.shutdown().await;
    let results = results?;

    // Human-readable table
    println!("\n┌──────────────────┬───────┬────────┐");
    println!("│ Check            │ Level │ Result │");
    println!("├──────────────────┼───────┼────────┤");
    for r in &results {
        let status = if r.skipped {
            "SKIP"
        } else if r.pass {
            "PASS"
        } else {
            "FAIL"
        };
        println!("│ {:<16} │ {:^5} │ {:^6} │", r.id, r.level, status);
    }
    println!("└──────────────────┴───────┴────────┘");
    for r in &results {
        let marker = if r.skipped {
            "○"
        } else if r.pass {
            "✓"
        } else {
            "✗"
        };
        println!("  {marker} {}: {}", r.id, r.detail);
    }

    // Level achieved = highest level L such that all checks at levels <= L pass
    let mut achieved = 0u8;
    for level in 1..=cli.level {
        let level_ok = results.iter().filter(|r| r.level == level).all(|r| r.pass);
        if level_ok {
            achieved = level;
        } else {
            break;
        }
    }
    let failed: Vec<&str> = results
        .iter()
        .filter(|r| !r.pass)
        .map(|r| r.id.as_str())
        .collect();
    let skipped: Vec<&str> = results
        .iter()
        .filter(|r| r.skipped)
        .map(|r| r.id.as_str())
        .collect();

    println!(
        "\nLevel achieved: {achieved} (target {}){}{}",
        cli.level,
        if failed.is_empty() {
            String::new()
        } else {
            format!(" — FAILED: {failed:?}")
        },
        if skipped.is_empty() {
            String::new()
        } else {
            format!(" — skipped: {skipped:?}")
        },
    );

    if let Some(path) = cli.json {
        let report = json!({
            "GameRlConformance": {
                "SpecVersion": "2.0.0-draft02",
                "TargetLevel": cli.level,
                "LevelAchieved": achieved,
                "Scenario": cli.scenario,
                "Seed": cli.seed,
                "ServerCmd": cli.server_cmd,
                "Checks": results,
            }
        });
        std::fs::write(&path, serde_json::to_string_pretty(&report)?)?;
        eprintln!("JSON report written to {path}");
    }

    if achieved < cli.level {
        std::process::exit(1);
    }
    Ok(())
}
