# Zomboid SwarmKit Live MCP Execution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `arkavo swarmkit play <kit>` drives a live MCP game loop — a multi-role survivor swarm that connects to the game-rl MCP server and plays Project Zomboid, with the game-rl server declared inside the content-addressed kit; plus the two spec amendments this work proves out.

**Architecture:** A kit-level `mcp_servers` registry (new schema in `arkavo-swarmkit`) lets a kit declare how to launch an MCP server; a new `arkavo swarmkit play` subcommand maps each role to the CLI `AgentConfig` and runs it through the *existing* `start_agent_server` runtime (the same path `arkavo agent` uses — proven to play games live). Hub-spoke: the `survivor` role holds the game-rl grant and ticks observe/step; advisor roles exchange A2A recommendations.

**Tech Stack:** Rust (arkavo-edge workspace: `arkavo-swarmkit`, `arkavo-cli`), YAML kits, the game-rl MCP server, Project Zomboid 41.78 + GameRL mod. Markdown specs (SwarmKit spec in `arkavo-edge/specs/`, Game-RL spec in `arkavo/specifications/game-rl/`).

**Cross-repo note:** code + SwarmKit spec live in `/Users/arkavo/Projects/arkavo/arkavo-edge`; the Game-RL spec lives in `/Users/arkavo/Projects/arkavo/specifications`. `cd` into the right repo per task.

---

## File structure

| File | Responsibility |
|---|---|
| `arkavo-edge/crates/arkavo-swarmkit/src/mcp.rs` (new) | `McpServerDef`, `Transport` types |
| `arkavo-edge/crates/arkavo-swarmkit/src/manifest.rs` (mod) | `mcp_servers: Vec<McpServerDef>` on `Manifest` |
| `arkavo-edge/crates/arkavo-swarmkit/src/validate.rs` (mod) | rule: `mcp_tools[].server` resolves to a declared server |
| `arkavo-edge/crates/arkavo-cli/src/commands/swarmkit.rs` (new) | `swarmkit play` command + role→`AgentConfig` adapter |
| `arkavo-edge/crates/arkavo-cli/src/commands/mod.rs` (mod) | register `swarmkit` module |
| `arkavo-edge/crates/arkavo-cli/src/lib.rs:64` (mod) | dispatch `"swarmkit"` |
| `arkavo-edge/examples/zomboid-survival-kit/zomboid-survival-kit.swarmkit.yaml` (new) | the kit |
| `arkavo-edge/examples/zomboid-survival-kit/{run-kit.sh,README.md}` (new) | launcher + docs |
| `arkavo-edge/specs/arkavo-edge/swarmkit.spec.yaml` (mod) | mcp_servers + interactive-role amendments |
| `arkavo/specifications/game-rl/draft-arkavo-game-rl-03.md` (new) | draft-03 conventions |
| `arkavo/specifications/game-rl/CHANGELOG.md` (mod) | draft-03 entry |

---

## Task 1: `McpServerDef` schema type

**Files:**
- Create: `arkavo-edge/crates/arkavo-swarmkit/src/mcp.rs`
- Modify: `arkavo-edge/crates/arkavo-swarmkit/src/lib.rs` (add `pub mod mcp;` and re-export)
- Test: inline `#[cfg(test)]` in `mcp.rs`

- [ ] **Step 1: Write the failing test**

In `arkavo-edge/crates/arkavo-swarmkit/src/mcp.rs`:
```rust
//! MCP server declarations for kits (spec §4.x): how a role's `mcp_tools`
//! grant is actually launched/connected at play time.

use serde::{Deserialize, Serialize};

/// Transport for an MCP server connection.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum Transport {
    #[default]
    Stdio,
    Http,
    Sse,
}

/// A named MCP server a kit can launch/connect. Roles reference it by `name`
/// from their `mcp_tools[].server`. `command`/`args` may contain `${VAR}`
/// placeholders expanded at play time (not parse time), keeping the kit
/// portable and its `kit.id` stable.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct McpServerDef {
    pub name: String,
    pub command: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default)]
    pub transport: Transport,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deserializes_minimal_server() {
        let yaml = r#"
name: game-rl
command: ${GAME_RL_SERVER}
"#;
        let def: McpServerDef = serde_yaml::from_str(yaml).unwrap();
        assert_eq!(def.name, "game-rl");
        assert_eq!(def.command, "${GAME_RL_SERVER}");
        assert!(def.args.is_empty());
        assert_eq!(def.transport, Transport::Stdio);
    }

    #[test]
    fn round_trips_with_args_and_transport() {
        let def = McpServerDef {
            name: "game-rl".into(),
            command: "game-rl-server".into(),
            args: vec!["--http".into()],
            transport: Transport::Http,
        };
        let yaml = serde_yaml::to_string(&def).unwrap();
        let back: McpServerDef = serde_yaml::from_str(&yaml).unwrap();
        assert_eq!(def, back);
    }
}
```

Add to `arkavo-edge/crates/arkavo-swarmkit/src/lib.rs` near the other `pub mod` lines:
```rust
pub mod mcp;
```
and to its re-export block (find the existing `pub use` group):
```rust
pub use mcp::{McpServerDef, Transport};
```

- [ ] **Step 2: Run test to verify it fails (compile error: module/test new)**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo test -p arkavo-swarmkit mcp:: 2>&1 | tail -15`
Expected: compiles and PASSES (this type is self-contained). If `serde_yaml` is not a dev-dependency of the crate, the test won't compile — see Step 3.

- [ ] **Step 3: Ensure `serde_yaml` is available for tests**

Check: `grep -n serde_yaml arkavo-edge/crates/arkavo-swarmkit/Cargo.toml`. If absent under `[dependencies]` or `[dev-dependencies]`, add to `[dev-dependencies]`:
```toml
serde_yaml = "0.9"
```
(The crate already parses YAML via `parse_yaml`, so a YAML lib is present — prefer reusing the same one; if it re-exports through `arkavo-swarmkit::parse_yaml`, write the test against `parse_yaml` of a full manifest instead. Use whichever YAML dependency the crate already declares.)

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo test -p arkavo-swarmkit mcp:: 2>&1 | tail -8`
Expected: `test result: ok. 2 passed`

- [ ] **Step 5: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add crates/arkavo-swarmkit/src/mcp.rs crates/arkavo-swarmkit/src/lib.rs crates/arkavo-swarmkit/Cargo.toml
git commit -m "swarmkit: McpServerDef type for kit-level mcp_servers registry"
```

---

## Task 2: `mcp_servers` on the manifest + validator rule

**Files:**
- Modify: `arkavo-edge/crates/arkavo-swarmkit/src/manifest.rs` (add field)
- Modify: `arkavo-edge/crates/arkavo-swarmkit/src/validate.rs` (add rule)
- Test: inline `#[cfg(test)]` in `validate.rs`

- [ ] **Step 1: Add the field to `Manifest`**

In `arkavo-edge/crates/arkavo-swarmkit/src/manifest.rs`, add to the `Manifest` struct (after `coordination` or near other top-level optional blocks), and import the type:
```rust
use crate::mcp::McpServerDef;
```
```rust
    /// MCP servers this kit can launch/connect (spec §4.x). Roles reference
    /// these by name from `mcp_tools[].server`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub mcp_servers: Vec<McpServerDef>,
```
(If `Manifest` derives `Default` or is built field-by-field in tests, this `#[serde(default)]` keeps existing kits — which have no `mcp_servers` — valid.)

- [ ] **Step 2: Write the failing validator test**

In `arkavo-edge/crates/arkavo-swarmkit/src/validate.rs` `#[cfg(test)]` module, add:
```rust
    #[test]
    fn rejects_grant_referencing_undeclared_mcp_server() {
        // Minimal manifest YAML with a role granting an undeclared server.
        let yaml = include_str!("../tests/fixtures/undeclared_mcp_server.yaml");
        let manifest = crate::parse_yaml(yaml).expect("parses");
        let err = crate::validate(&manifest).expect_err("must reject");
        assert!(
            format!("{err}").contains("unknown mcp server 'game-rl'"),
            "got: {err}"
        );
    }

    #[test]
    fn accepts_grant_with_declared_mcp_server() {
        let yaml = include_str!("../tests/fixtures/declared_mcp_server.yaml");
        let manifest = crate::parse_yaml(yaml).expect("parses");
        assert!(crate::validate(&manifest).is_ok());
    }
```

Create `arkavo-edge/crates/arkavo-swarmkit/tests/fixtures/declared_mcp_server.yaml` — a minimal VALID kit with one role and a matching `mcp_servers` entry. Base it on `examples/campaign-kit/campaign-kit.swarmkit.yaml` (copy its shape, reduce to one role `survivor`), and add:
```yaml
mcp_servers:
  - name: game-rl
    command: ${GAME_RL_SERVER}
roles:
  - id: survivor
    role_type: survivor
    agent_provisioning: {}
    mcp_tools:
      - server: game-rl
        tools: [observe, step]
        auth: none
```
Create `undeclared_mcp_server.yaml` identical but with the `mcp_servers` block removed (so `server: game-rl` resolves to nothing). Set each fixture's `kit.id` to empty/recompute-tolerant OR — simpler — have the test call a lower-level `validate` that does not enforce `kit.id` (check `validate.rs`: if `kit.id` BLAKE3 is enforced, set the fixture's `kit.id` to the computed value, or use a `validate` variant. If the only path enforces it, compute the id with `canonical::compute_kit_id` and paste it in; the fixture is static so the id is stable).

- [ ] **Step 3: Run test to verify it fails**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo test -p arkavo-swarmkit mcp_server 2>&1 | tail -15`
Expected: `rejects_grant_referencing_undeclared_mcp_server` FAILS (no such rule yet — validate returns Ok).

- [ ] **Step 4: Implement the rule**

In `arkavo-edge/crates/arkavo-swarmkit/src/validate.rs`, inside the `validate(manifest)` function alongside the handoff-target check, add:
```rust
    // Every mcp_tools grant must reference a declared mcp_servers entry.
    let known_servers: std::collections::HashSet<&str> =
        manifest.mcp_servers.iter().map(|s| s.name.as_str()).collect();
    for role in &manifest.roles {
        for grant in &role.mcp_tools {
            if !known_servers.contains(grant.server.as_str()) {
                return Err(ValidationError::new(format!(
                    "unknown mcp server '{}' referenced by role '{}'",
                    grant.server, role.id
                )));
            }
        }
    }
```
(Match the exact `ValidationError` constructor/`Err` shape used by the surrounding rules — read the function and mirror it; if errors are `String`, return `Err(format!(...).into())`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo test -p arkavo-swarmkit 2>&1 | tail -10`
Expected: both new tests pass; all existing swarmkit tests still pass.

- [ ] **Step 6: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add crates/arkavo-swarmkit/src/manifest.rs crates/arkavo-swarmkit/src/validate.rs crates/arkavo-swarmkit/tests/fixtures/
git commit -m "swarmkit: mcp_servers manifest block + validate grants resolve to a declared server"
```

---

## Task 3: role → `AgentConfig` adapter

**Files:**
- Create: `arkavo-edge/crates/arkavo-cli/src/commands/swarmkit.rs`
- Modify: `arkavo-edge/crates/arkavo-cli/src/commands/mod.rs` (add `pub mod swarmkit;`)
- Test: inline `#[cfg(test)]` in `swarmkit.rs`

- [ ] **Step 1: Write the adapter + failing test**

In `arkavo-edge/crates/arkavo-cli/src/commands/swarmkit.rs`:
```rust
//! `arkavo swarmkit play <kit>` — run a SwarmKit as a live agent flight by
//! mapping each role to the existing agent runtime (`start_agent_server`).

use crate::commands::agent::{AgentConfig, McpServerConfig};
use arkavo_protocol::agent_config::{expand_env, AgentMode};
use arkavo_swarmkit::manifest::Manifest;

const BASE_PORT: u16 = 8450;

/// Build the per-role agent configs for a parsed, validated kit.
/// Index order is the manifest role order (deterministic ports).
pub fn kit_to_agent_configs(manifest: &Manifest) -> Vec<AgentConfig> {
    // Resolve mcp server name -> def once.
    let servers: std::collections::HashMap<&str, &arkavo_swarmkit::McpServerDef> =
        manifest.mcp_servers.iter().map(|s| (s.name.as_str(), s)).collect();

    let listens: Vec<String> = manifest
        .roles
        .iter()
        .enumerate()
        .map(|(i, _)| format!("0.0.0.0:{}", BASE_PORT + i as u16))
        .collect();

    manifest
        .roles
        .iter()
        .enumerate()
        .map(|(i, role)| {
            let has_grant = !role.mcp_tools.is_empty();

            // mcp_servers for this role: resolve each grant to a server def,
            // expanding ${VAR} at build (play) time.
            let mcp_servers = role
                .mcp_tools
                .iter()
                .filter_map(|grant| servers.get(grant.server.as_str()).map(|def| McpServerConfig {
                    name: def.name.clone(),
                    command: Some(expand_env(&def.command)),
                    args: def.args.iter().map(|a| expand_env(a)).collect(),
                    url: None,
                }))
                .collect();

            // peers: hub-spoke. Hub (has grant) peers every advisor; advisors
            // peer the hub. Single-hub kits only.
            let hub_idx = manifest.roles.iter().position(|r| !r.mcp_tools.is_empty());
            let peers: Vec<String> = match (has_grant, hub_idx) {
                (true, _) => listens
                    .iter()
                    .enumerate()
                    .filter(|(j, _)| *j != i)
                    .map(|(_, l)| format!("http://localhost:{}", l.rsplit(':').next().unwrap()))
                    .collect(),
                (false, Some(h)) => vec![format!(
                    "http://localhost:{}",
                    listens[h].rsplit(':').next().unwrap()
                )],
                (false, None) => vec![],
            };

            // purpose: role's first inline skill instructions, else role_type.
            let purpose = role
                .skills
                .iter()
                .find_map(|s| s.payload.as_ref().map(|p| p.instructions.clone()))
                .unwrap_or_else(|| format!("Role: {}", role.role_type));

            let model = role
                .agent_provisioning
                .model
                .as_ref()
                .map(|m| match &m.size {
                    Some(sz) => format!("{}-{}", m.family, sz),
                    None => m.family.clone(),
                })
                .unwrap_or_default();

            AgentConfig {
                name: role.id.clone(),
                purpose,
                model,
                mode: if has_grant { AgentMode::Orchestrator } else { AgentMode::Specialist },
                listen: listens[i].clone(),
                mdns_enabled: true,
                mcp_servers,
                api_keys: std::collections::HashMap::new(),
                quiet: true,
                peers,
                a2a_enabled: true,
                a2a_service_type: None,
                swarm: Some(slug(&manifest.kit.name)),
            }
        })
        .collect()
}

fn slug(name: &str) -> String {
    name.to_lowercase()
        .chars()
        .map(|c| if c.is_alphanumeric() { c } else { '-' })
        .collect::<String>()
        .trim_matches('-')
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_hub_and_advisor_roles() {
        let yaml = include_str!(
            "../../../arkavo-swarmkit/tests/fixtures/declared_mcp_server.yaml"
        );
        // declared_mcp_server.yaml has one role `survivor` with a game-rl grant.
        let manifest = arkavo_swarmkit::parse_yaml(yaml).unwrap();
        let configs = kit_to_agent_configs(&manifest);
        assert_eq!(configs.len(), manifest.roles.len());
        let survivor = configs.iter().find(|c| c.name == "survivor").unwrap();
        assert_eq!(survivor.mode, AgentMode::Orchestrator);
        assert_eq!(survivor.listen, "0.0.0.0:8450");
        assert_eq!(survivor.mcp_servers.len(), 1);
        assert_eq!(survivor.mcp_servers[0].name, "game-rl");
    }
}
```

Add to `arkavo-edge/crates/arkavo-cli/src/commands/mod.rs`:
```rust
pub mod swarmkit;
```

- [ ] **Step 2: Reconcile field/type names with the real code**

Read these and fix any name mismatches in the adapter (the plan uses the names from the design survey; verify against source):
- `arkavo-edge/crates/arkavo-cli/src/commands/agent.rs` — the `AgentConfig` and `McpServerConfig` struct field names (confirmed: name, purpose, model, mode, listen, mdns_enabled, mcp_servers, api_keys, quiet, peers, a2a_enabled, a2a_service_type, swarm).
- `arkavo-edge/crates/arkavo-swarmkit/src/role.rs` — `RoleSpec` fields (`id`, `role_type`, `agent_provisioning`, `skills`, `mcp_tools`), `Skill.payload` shape (`instructions`), `Model` (`family`, `size`).
- `arkavo-protocol::agent_config::expand_env` is `pub` (added in an earlier session) and `AgentMode` has `Orchestrator`/`Specialist`.

- [ ] **Step 3: Run test to verify it compiles + passes**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo test -p arkavo-cli swarmkit::tests 2>&1 | tail -15`
Expected: `maps_hub_and_advisor_roles` passes. Fix field-name compile errors per Step 2 until green.

- [ ] **Step 4: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add crates/arkavo-cli/src/commands/swarmkit.rs crates/arkavo-cli/src/commands/mod.rs
git commit -m "swarmkit: role -> AgentConfig adapter (hub-spoke, mcp grant => orchestrator)"
```

---

## Task 4: `swarmkit play` command + dispatch

**Files:**
- Modify: `arkavo-edge/crates/arkavo-cli/src/commands/swarmkit.rs` (add `execute`)
- Modify: `arkavo-edge/crates/arkavo-cli/src/lib.rs:64` (dispatch)

- [ ] **Step 1: Add `execute` that parses, validates, and runs roles**

Append to `swarmkit.rs`:
```rust
/// `arkavo swarmkit play <kit.yaml> [--role <id>]`
pub fn execute(args: &[String]) -> Result<(), Box<dyn std::error::Error>> {
    let mut kit_path: Option<String> = None;
    let mut only_role: Option<String> = None;
    let mut i = 0;
    let mut sub: Option<&str> = None;
    while i < args.len() {
        match args[i].as_str() {
            "play" => sub = Some("play"),
            "--role" if i + 1 < args.len() => { only_role = Some(args[i + 1].clone()); i += 1; }
            "-h" | "--help" | "help" => { print_usage(); return Ok(()); }
            p if !p.starts_with('-') && kit_path.is_none() => kit_path = Some(p.to_string()),
            other => return Err(format!("Unknown swarmkit arg '{other}'").into()),
        }
        i += 1;
    }
    if sub != Some("play") {
        print_usage();
        return Err("expected: arkavo swarmkit play <kit.yaml>".into());
    }
    let kit_path = kit_path.ok_or("missing <kit.yaml> path")?;

    let yaml = std::fs::read_to_string(&kit_path)
        .map_err(|e| format!("cannot read kit '{kit_path}': {e}"))?;
    let manifest = arkavo_swarmkit::parse_yaml(&yaml)
        .map_err(|e| format!("kit parse error: {e}"))?;
    arkavo_swarmkit::validate(&manifest)
        .map_err(|e| format!("kit invalid: {e}"))?;

    let mut configs = kit_to_agent_configs(&manifest);
    if let Some(role) = &only_role {
        configs.retain(|c| &c.name == role);
        if configs.is_empty() {
            return Err(format!("no role named '{role}' in kit").into());
        }
    }

    println!(
        "[swarmkit] {} v{} — launching {} role(s): {}",
        manifest.kit.name,
        manifest.kit.version,
        configs.len(),
        configs.iter().map(|c| c.name.as_str()).collect::<Vec<_>>().join(", ")
    );

    // Run all roles concurrently on a tokio runtime, reusing the proven
    // agent runtime (`start_agent_server`) — the same path `arkavo agent` uses.
    let rt = tokio::runtime::Runtime::new()?;
    rt.block_on(async move {
        let mut handles = Vec::new();
        for cfg in configs {
            handles.push(tokio::spawn(async move {
                if let Err(e) = crate::commands::agent::start_agent_server(&cfg, false).await {
                    eprintln!("[swarmkit] role '{}' exited: {e}", cfg.name);
                }
            }));
        }
        for h in handles { let _ = h.await; }
    });
    Ok(())
}

fn print_usage() {
    eprintln!("Usage: arkavo swarmkit play <kit.yaml> [--role <id>]");
}
```
(If `start_agent_server` is not `pub`, make it `pub` in `agent.rs`. If the role configs must outlive the spawn, the loop already moves each `cfg` into its task — confirm `AgentConfig: Send + 'static`; it holds only owned data, so it is.)

- [ ] **Step 2: Dispatch the subcommand**

In `arkavo-edge/crates/arkavo-cli/src/lib.rs` at the `match args[0].as_str()` block (line ~64), add an arm:
```rust
        "swarmkit" => commands::swarmkit::execute(&args[1..]),
```

- [ ] **Step 3: Build and smoke-test the CLI surface**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo build -p arkavo 2>&1 | grep -E "^error" | head; ./target/debug/arkavo swarmkit 2>&1 | head -3`
Expected: builds; `arkavo swarmkit` prints the usage line and exits non-zero.

- [ ] **Step 4: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add crates/arkavo-cli/src/commands/swarmkit.rs crates/arkavo-cli/src/lib.rs crates/arkavo-cli/src/commands/agent.rs
git commit -m "swarmkit: arkavo swarmkit play runs a kit via the agent runtime"
```

---

## Task 5: author + validate the Zomboid kit

**Files:**
- Create: `arkavo-edge/examples/zomboid-survival-kit/zomboid-survival-kit.swarmkit.yaml`

- [ ] **Step 1: Write the kit**

Create the kit mirroring `examples/campaign-kit/campaign-kit.swarmkit.yaml`'s shape, with:
- `spec_version: "1.0.0"`, `kit` block (name "Zomboid Survival Kit", version 0.1.0, one author DID, `created`/`expires` ≤ 1 year, a 16-char `nonce`, `id: ""` placeholder).
- `objective.goal` = survive and scavenge; `success_criteria` = ["survivor alive at episode end", "weapon equipped", "food secured"].
- `inputs: [{name: game_state, type: json, required: true}]`, `deliverables: [{name: survival_log, type: json}]`.
- `mcp_servers: [{name: game-rl, command: "${GAME_RL_SERVER}", args: [], transport: stdio}]`.
- `roles`: `survivor` (Orchestrator; `mcp_tools: [{server: game-rl, tools: [registerAgent, observe, step, episodeSummary, reset], auth: none}]`; inline skill = the survival prompt from `examples/zomboid/agents/survivor/AGENTS.md`; handoffs to threat/scavenger/medic), `threat`/`scavenger`/`medic` (Specialist advisors; inline skills; handoff back to survivor), `critic` (Specialist; rubric scorer).
- `coordination: {topology: hub-spoke, protocol: a2a-jsonrpc-2.0, routing: {strategy: static}}`.
- `evaluation: {rubric: {dimensions: [{name: survival_trajectory, weight: 0.4, threshold: 0.6}, {name: threat_response, weight: 0.3, threshold: 0.6}, {name: resource_security, weight: 0.2, threshold: 0.5}, {name: coordination, weight: 0.1, threshold: 0.5}]}, critic_role: critic}`.
- `constraints: {global_budget: {max_wallclock_seconds: 1800, max_total_tokens: 400000, max_cost_usd: 2.0}, data_classifications: [game], network: {egress_allowed: false}}` and per-role budgets ≤ those.
- `completion: {rules: ["survivor registered and alive"], on_failure: retry, max_retries: 1}`.
- `provenance.signatures: []` initially (fill in Step 3 if required).

For each inline skill, set `source: inline`, a `payload` (`name`, `description`, `instructions`, `resources: []`), and — if the validator/`validate_kit` requires it — a `signature`/`signed_by`. Inspect a working example with inline skills (`grep -rl "source: inline" examples/*/*.swarmkit.yaml`) and copy its signature approach. If skill signatures are dev-optional (VerifyMode::Optional), a present-but-unverified signature is acceptable; if strict, sign with the repo's dev key utility.

- [ ] **Step 2: Compute `kit.id` and validate**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo run -p arkavo-swarmkit --example validate_kit -- examples/zomboid-survival-kit/zomboid-survival-kit.swarmkit.yaml 2>&1 | tail -20`
Expected first run: prints a computed `kit.id` that differs from the declared empty one, and/or a list of validation errors.

- [ ] **Step 3: Fix until VALID**

Paste the computed `kit.id` into the kit's `kit.id` field. Resolve any validation errors (rubric weights sum to 1.0 — they do: 0.4+0.3+0.2+0.1; per-role budgets ≤ global; critic_role is a known role; handoff targets known; mcp grant resolves to the declared `game-rl` server). Add the provenance/skill signatures if strict. Re-run validate_kit.
Expected: prints `VALID` with matching declared/computed `kit.id` and `roles: 5 (survivor, threat, scavenger, medic, critic)`.

- [ ] **Step 4: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add examples/zomboid-survival-kit/zomboid-survival-kit.swarmkit.yaml
git commit -m "swarmkit: zomboid-survival-kit (validated; game-rl declared in mcp_servers)"
```

---

## Task 6: launcher + README

**Files:**
- Create: `arkavo-edge/examples/zomboid-survival-kit/run-kit.sh`
- Create: `arkavo-edge/examples/zomboid-survival-kit/README.md`

- [ ] **Step 1: `run-kit.sh`**

```bash
#!/bin/bash
# Zomboid Survival Kit — run the SwarmKit live against Project Zomboid.
set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BINARY="${BINARY:-${PROJECT_ROOT}/target/debug/arkavo}"
[ -f "$BINARY" ] || BINARY="${PROJECT_ROOT}/target/release/arkavo"

# game-rl-server auto-detects Project Zomboid via ~/Zomboid/Lua/ file IPC.
export GAME_RL_SERVER="${GAME_RL_SERVER:-$(command -v game-rl-server || echo "$HOME/Projects/intelligence/game-rl/target/release/game-rl-server")}"

KIT="$SCRIPT_DIR/zomboid-survival-kit.swarmkit.yaml"
[ -f "$HOME/Zomboid/Lua/gamerl_response.json" ] || echo "[WARN] Start Project Zomboid (GameRL mod, save loaded) first."
echo "[INFO] server: $GAME_RL_SERVER"
exec "$BINARY" swarmkit play "$KIT"
```
`chmod +x run-kit.sh`.

- [ ] **Step 2: `README.md`**

Document: what the kit is (the validated declarative artifact, `kit.id`), the hub-spoke role topology, that `arkavo swarmkit play` reuses the agent runtime to drive a live game loop (vs the panel-only `ARKAVO_SWARMKIT_PATH` path), prerequisites (PZ + GameRL mod + a loaded save; `game-rl-server` built), and the run command. Note `examples/zomboid/` remains the single-agent path.

- [ ] **Step 3: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add examples/zomboid-survival-kit/run-kit.sh examples/zomboid-survival-kit/README.md
git commit -m "swarmkit: zomboid-survival-kit launcher + README"
```

---

## Task 7: SwarmKit spec amendments

**Files:**
- Modify: `arkavo-edge/specs/arkavo-edge/swarmkit.spec.yaml`

- [ ] **Step 1: Read the spec's structure**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && sed -n '1,60p' specs/arkavo-edge/swarmkit.spec.yaml; grep -n "section\|§4\|mcp_tools\|roles:\|coordination" specs/arkavo-edge/swarmkit.spec.yaml | head -20`
Note the spec's own format (it's a YAML spec with sections/scenarios SK-001..).

- [ ] **Step 2: Add the `mcp_servers` normative section**

Add a section (mirror the existing section style) defining: kits MAY declare top-level `mcp_servers: [{name, command, args, transport}]`; `transport ∈ {stdio, http, sse}`; `command`/`args` MAY contain `${VAR}` expanded by the runtime at launch; every `roles[*].mcp_tools[*].server` MUST resolve to a declared `mcp_servers[*].name` (validation MUST reject otherwise); `mcp_servers` is part of the canonical form and therefore covered by `kit.id`. Add a conformance scenario (next SK-0xx id) asserting the validator rejects an unresolved grant.

- [ ] **Step 3: Add interactive/live-role semantics**

Add to the roles/coordination section: a role with a non-empty `mcp_tools` grant is a **live/orchestrator role** that drives an MCP tool loop (observe→act) terminated by `completion.rules`, as opposed to a role that produces a single deliverable; roles without grants are advisory. State that `coordination.topology: hub-spoke` with exactly one live role is the canonical "embodied agent + advisors" pattern, and that a conformant runtime MAY realize a kit either as a one-shot deliverable flight or a live tool loop (reference impl: `arkavo swarmkit play`).

- [ ] **Step 4: Bump spec version/changelog note within the spec file**

If the spec file has a version or changelog field, note these additions (e.g. "0.2: mcp_servers registry, live-role semantics"). Keep `spec_version` compatibility at 1.x.

- [ ] **Step 5: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/arkavo-edge
git add specs/arkavo-edge/swarmkit.spec.yaml
git commit -m "spec(swarmkit): mcp_servers registry + interactive/live-role semantics"
```

---

## Task 8: Game-RL spec draft-03

**Files:**
- Create: `arkavo/specifications/game-rl/draft-arkavo-game-rl-03.md`
- Modify: `arkavo/specifications/game-rl/CHANGELOG.md`

- [ ] **Step 1: Create draft-03 from draft-02**

Run: `cd /Users/arkavo/Projects/arkavo/specifications && cp game-rl/draft-arkavo-game-rl-02.md game-rl/draft-arkavo-game-rl-03.md`
Update the header: Version `3.0.0-draft03`, Supersedes draft-02, date 2026-06-12.

- [ ] **Step 2: Add the four conventions (each as a numbered REQ in the relevant section)**

1. **RenderMap viewport (new tool, in §5 Core Tools / §6 Observation):** a read-only `RenderMap` returning an ASCII viewport with absolute coordinate rulers, a glyph-per-cell legend, north-up (the game declares its axis convention, e.g. Zomboid's `-y` = north); coordinates read off the rulers are valid action anchors/targets. No time advance. Note it as the standard "textual perception" channel for LLM agents; SHOULD at Level 2, with the precise glyph priority left to the game.
2. **Structured-build / one-shot-action principle (§7 or new §8):** where a game supports construction, provide a structural primitive over coordinate spam (e.g. `BuildRoom`), and prefer one-shot actions that cannot strand intermediate state (e.g. `BuildRoom Furnish="Bed:3"`). Normative finding: rule-driven conductors do not reliably read error text or follow prompt conditionals, so sequencing constraints MUST be enforceable adapter-side (loud errors / one-shot actions), not only via agent prompting.
3. **Partial-failure feedback (strengthen REQ-ERR-01/02):** when an action partially succeeds (N of M), the result MUST state the shortfall and SHOULD include the engine's rejection reasons. Silent shortfalls are non-conformant.
4. **Embodied vs systemic reference point (strengthen REQ-SPA-03/04):** the spatial reference point is the agent's locus — colony/activity centroid for a systemic controller, the avatar's position for an embodied agent — never the map center. Landmarks (compass regions, centroids) are relative to that locus. Cite the two-adapter proof (RimWorld systemic + Zomboid embodied).

- [ ] **Step 3: Update the conformance + "better/stronger/faster" framing**

In the conformance section, add the RenderMap and partial-failure checks. Add a short "Field-validated across two adapters" note (RimWorld systemic + Zomboid embodied) establishing these as proven, not aspirational. Mention the observation diet (compact step responses + default elision) + RenderMap as the efficiency ("faster") story.

- [ ] **Step 4: CHANGELOG entry**

Append a `3.0.0-draft03 (2026-06-12)` section to `game-rl/CHANGELOG.md` summarizing the four additions and the second-adapter validation.

- [ ] **Step 5: Commit**

```bash
cd /Users/arkavo/Projects/arkavo/specifications
git add game-rl/draft-arkavo-game-rl-03.md game-rl/CHANGELOG.md
git commit -m "spec(game-rl): draft-03 — RenderMap, structured build/one-shot, partial-failure feedback, embodied reference point"
```

---

## Task 9: live play against Project Zomboid

**Files:** none (verification)

- [ ] **Step 1: Build release binaries**

Run: `cd /Users/arkavo/Projects/intelligence/game-rl && cargo build --release -p game-rl-cli; cd /Users/arkavo/Projects/arkavo/arkavo-edge && cargo build -p arkavo 2>&1 | grep -E "^error" | head`
Expected: both build clean.

- [ ] **Step 2: Ensure Project Zomboid is running with the mod + a loaded save**

Confirm `ls ~/Zomboid/Lua/gamerl_response.json` exists (mod connected). If not, launch PZ via Steam (`open "steam://rungameid/108600"`), load/continue a save.

- [ ] **Step 3: Run the kit live**

Run: `cd /Users/arkavo/Projects/arkavo/arkavo-edge && GAME_RL_SERVER="$HOME/Projects/intelligence/game-rl/target/release/game-rl-server" ./examples/zomboid-survival-kit/run-kit.sh > /tmp/zomboid-kit.log 2>&1 &`
Wait ~20s, then:
`grep -E "swarmkit|Tool: |registerAgent" /tmp/zomboid-kit.log | tail -15`
Expected: `[swarmkit]` launch line listing 5 roles; the survivor role registers and calls observe/step.

- [ ] **Step 4: Confirm game-side action receipt**

Run: `grep -E "GameRL\] Received: ExecuteAction" ~/Zomboid/console.txt | tail -3`
Expected: ExecuteAction entries (the survivor is driving the live loop via the kit).

- [ ] **Step 5: Record result + stop**

Capture the tool-call summary into the kit README's "verified live" note. Stop: `pkill -f "swarmkit play"`.

---

## Self-review

**Spec coverage:** mcp_servers schema (T1–T2) ✓; validator rule (T2) ✓; swarmkit play + adapter reusing agent loop (T3–T4) ✓; the kit with game-rl in mcp_servers, rubric, completion, kit.id (T5) ✓; launcher/README (T6) ✓; SwarmKit spec amendments — mcp_servers + interactive-role (T7) ✓; Game-RL draft-03 — RenderMap, structured-build/one-shot, partial-failure, embodied reference point (T8) ✓; live play (T9) ✓.

**Type consistency:** `kit_to_agent_configs` (T3) is called by `execute` (T4); `AgentConfig`/`McpServerConfig` fields match `agent.rs` (verified by Step 3.2 read); `McpServerDef`/`Transport` (T1) referenced by manifest (T2) and adapter (T3); `start_agent_server(&AgentConfig, bool)` (T4) confirmed in `agent.rs:1162`.

**Known soft spots flagged for the implementer:** (a) the validator's `Err` type — mirror the surrounding rule's exact shape; (b) skill/provenance signatures — copy a working inline-skill kit's approach, VerifyMode is Optional in dev; (c) `start_agent_server` visibility — make `pub` if needed. Each is called out in its task.
