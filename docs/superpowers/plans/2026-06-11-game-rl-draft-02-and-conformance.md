# Game-RL Draft-02 Spec + Conformance + Edge Playtesting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce Game-RL spec draft-02 (the standard all implementations target), a runnable conformance harness with a reference environment, fixed arkavo-edge wiring, and conformance + playtest results across game scenarios.

**Architecture:** The spec (in `arkavo/specifications`) becomes the single source of truth, codifying the *released* surface (camelCase tools, PascalCase fields — chosen because small local models mangle snake_case) and a redesigned Spatial Intent v2 that eliminates the center-bias failure. A new `game-rl-reference` crate provides a deterministic headless environment (with deliberately off-center optima as a behavioral probe); a new `game-rl-conformance` crate validates any server over stdio against draft-02 levels. arkavo-edge gains `${VAR}` expansion so examples stop hardcoding machine-specific paths, and its rimworld/gridworld scenarios run against the reference env headlessly.

**Tech Stack:** Rust (edition 2024, workspace game-rl), JSON Schema 2020-12, arkavo-edge (Rust), Markdown spec.

---

## Context: why draft-02 (findings driving the design)

1. **Naming divergence.** draft-00 + GameOfMods use snake_case; released game-rl + arkavo-edge use camelCase tools / PascalCase fields. Decision (user): camelCase/PascalCase is canonical — small local models have difficulty with snake_case. draft-02 codifies it; draft-00 is left untouched (no revision).
2. **Spec is missing the tools agents actually use.** `observe`, `episodeSummary` absent from draft-00; spatial intents absent; `ValidActions`/`Alerts` absent.
3. **Center bias is a contract failure, not just a code bug.** Root causes (verified, file:line):
   - `SpatialActions.cs:56` — multi-instance anchors resolve to instance nearest `map.Center`.
   - `SpatialActions.cs:289-331` — `EstablishFarm` fallback silently re-anchors to colony/map center, ignoring the agent's anchor (corrupts credit assignment).
   - `RimWorldStateExtractor.cs:2030-2050` — `MapCenter` is the only guaranteed landmark; zones/buildings capped at 10; sparse early-game → agents only ever know "MapCenter".
   - No dry-run: agents can't preview a placement; failed anchors throw with no alternatives offered.
4. **Loud errors matter.** Playtesting found 162 silent `Log.Warning + return` failures; the spec must require structured errors for failed actions.
5. **No runnable conformance.** draft-00 lists 7 hypothetical Python tests; nothing exists. CLAUDE.md's `cargo run --example gridworld` is stale — no example env exists in the repo.
6. **arkavo-edge hardcodes** `/Users/arkavo/Projects/intelligence/game-rl/target/release/game-rl-server` (commander AGENTS.md:104); `agent_config.rs` does no env expansion.

---

## Task 1: Spec draft-02 document

**Files:**
- Create: `/Users/arkavo/Projects/arkavo/specifications/game-rl/draft-arkavo-game-rl-02.md`
- Create: `/Users/arkavo/Projects/arkavo/specifications/game-rl/CHANGELOG.md`

- [ ] **Step 1.1: Read draft-00 fully** (`draft-arkavo-game-rl-00.md`, 1873 lines) to carry forward: architecture layers, clock modes, session topologies, scopes/agent types/permissions, determinism reqs, error codes, vision streams, deployment patterns.
- [ ] **Step 1.2: Read the implementation surface** to transcribe exactly: `crates/game-rl-server/src/tools.rs` (tool schemas), `crates/game-rl-core/src/{manifest,action,observation,spatial}.rs`, `crates/game-bridge/src/protocol.rs` (IPC appendix).
- [ ] **Step 1.3: Write draft-02** with this structure (status `1.0.0-draft02`, date 2026-06-11, supersedes draft-00):
  1. Intro + Conventions — **Naming Rationale** section: tools camelCase, payload fields PascalCase; normative statement + rationale (small-model reliability, .NET interop, shipped surface).
  2. Architecture (carried from draft-00, terminology updated).
  3. Core tools (normative): `registerAgent`, `deregisterAgent`, `step`, `observe`, `reset`, `stateHash`, `episodeSummary`, `configureStreams`, `manifest` (new tool: manifest MUST be reachable as a tool, resource `game://manifest` OPTIONAL), `resolveSpatial` (optional, dry-run), `saveTrajectory`/`loadTrajectory`, `batchStep`, `sendMessage` (optional).
  4. Action model: Discrete / Continuous / Parameterized (PascalCase `Type` + flattened params) / Wait; input-normalization (SHOULD: alias common field casings, fuzzy action names with edit distance ≤ 3, report corrections).
  5. Observation contract: `ValidActions` (REQUIRED L2), `Alerts` (severity-ranked), `Landmarks` (REQUIRED when spatial intents declared), section filtering via `Include`/`Limit`.
  6. **Spatial Intent v2** (the centerpiece — see normative rules below).
  7. Rewards (synchronous delivery, components), episodes (`episodeSummary` is the episode boundary for lesson synthesis).
  8. Determinism (carried), error semantics (loud-error requirement REQ-ERR-01: a requested action that cannot be performed MUST produce a structured error or `Error` field in the step result — silent no-ops are non-conformant).
  9. Conformance levels (revised, see matrix below) + runnable suite reference (`game-rl-conformance`).
  10. Appendices: IPC bridge protocol (informative, from game-bridge), migration from draft-00 names, GameOfMods alias guidance.

**Spatial Intent v2 normative rules (REQ-SPA-01..07):**
- REQ-SPA-01 (Anchor grammar): `Near` accepts: entity ID; zone label; type name; landmark ID from `Landmarks`; object form `{"Anchor": "...", "Direction": "N|NE|E|SE|S|SW|W|NW", "Distance": n}`; coordinate escape hatch `{"X": n, "Y": n}`.
- REQ-SPA-02 (Resolution integrity): resolver MUST NOT silently substitute a different anchor. Infeasible → structured error listing ≥1 feasible alternative anchor, OR relocate only when agent set `"AllowFallback": true`, and then MUST set `FallbackApplied: true` + `AnchorUsed` in the result.
- REQ-SPA-03 (Reference point): multi-instance anchor disambiguation MUST use the agent's activity centroid (e.g., colony centroid) or the anchor itself — NEVER the map center.
- REQ-SPA-04 (Landmark discovery): when spatial intents are declared, `observe` MUST expose `Landmarks` including at minimum: colony/agent centroid, 8 compass-region centroids, and SHOULD include terrain features/resource clusters; entries carry stable `Id`, `Position`, `Kind`. Caps only via agent-supplied `Limit`.
- REQ-SPA-05 (Dry-run): servers SHOULD expose `resolveSpatial` returning the placement that *would* occur, without mutation.
- REQ-SPA-06 (Audit): `ResolvedPlacement` MUST include `AnchorRequested`, `AnchorResolved`, `Positions`, `Count`, `FallbackApplied`.
- REQ-SPA-07 (Determinism): identical state + identical intent → identical resolution.

**Conformance level matrix (draft-02):**
- L1 Minimal: initialize handshake, `manifest` tool, `registerAgent`, `step`, `observe`, `reset`, loud errors, seeded determinism of reset.
- L2 Standard: + `stateHash` determinism over action sequences, `episodeSummary`, `ValidActions`, `Alerts`, multi-agent ≥ 4, scenarios.
- L3 Full: + Spatial Intent v2 (REQ-SPA-01..07), streams, trajectories, `batchStep`, domain randomization, headless.

- [ ] **Step 1.4: CHANGELOG.md** — draft-00 → draft-02 decisions (naming, new tools, spatial v2, loud errors, conformance), note draft-01 intentionally skipped (draft-00 preserved unrevised; version-number gap marks the dialect break).
- [ ] **Step 1.5: Self-review** against findings 1–6 above; fix gaps.

## Task 2: draft-02 JSON schemas

**Files:** Create `/Users/arkavo/Projects/arkavo/specifications/schemas/game-rl/draft-02/{README.md, game-rl.schema.json, manifest.schema.json, register-agent.schema.json, step.schema.json, observe.schema.json, reset.schema.json, spatial.schema.json, episode-summary.schema.json, events.schema.json, vision-stream.schema.json}`

- [ ] **Step 2.1:** Author schemas with PascalCase properties matching `tools.rs`/`spatial.rs` serde output exactly (e.g. `AgentId`, `Action`, `Ticks`, `AnchorRequested`). JSON Schema 2020-12, `$id` under `https://arkavo.org/schemas/game-rl/draft-02/`.
- [ ] **Step 2.2:** Validate every schema parses (`python3 -c "import json,glob; [json.load(open(f)) for f in glob.glob('draft-02/*.json')]"`) and sample payloads from the spec validate (spot-check with `check-jsonschema` if available, else a small Python validator using `jsonschema` — skip silently if lib missing, structural parse is the floor).

## Task 3: `manifest` tool in game-rl-server

**Files:** Modify `crates/game-rl-server/src/tools.rs`, `crates/game-rl-server/src/handler.rs`; Test in `crates/game-rl-server/src/tools.rs` (unit) or existing test module.

- [ ] **3.1:** Failing test: `tools/list` includes `manifest`; dispatching `manifest` returns PascalCase JSON with `Name`, `Capabilities`, `GameRlVersion`.
- [ ] **3.2:** Implement: add tool descriptor + dispatch arm calling `env.manifest()`, serialize PascalCase (manifest already serializes — verify casing; if snake_case serde, add a PascalCase view for the tool response to honor the payload convention).
- [ ] **3.3:** `cargo test -p game-rl-server` green; `cargo clippy` clean.

## Task 4: `game-rl-reference` crate (reference env + center-bias probe)

**Files:** Create `crates/game-rl-reference/{Cargo.toml, src/lib.rs, src/env.rs, src/terrain.rs, src/actions.rs, src/main.rs}`; Modify root `Cargo.toml` workspace members.

Design: "GridColony" — NxN grid (default 64), deterministic ChaCha8 RNG from seed. Terrain fertility map generated per scenario; **scenario `fertile-corner` places all rich soil in NE quadrant** (center placement scores ~0). Entities: colonists (2), wood, stone, berry bushes, a hostile camp (scenario `threat-south`). Actions: `Wait`, `Move`, `Harvest`, `BuildShelter` (parameterized), spatial intents `EstablishFarm`, `PlaceBuildingNear`, `EstablishStorage` honoring REQ-SPA-01..07. Observation sections: `Colonists`, `Resources`, `Terrain` (FertileRegions), `Landmarks` (centroid + 8 compass regions + fertile clusters), `Alerts` (LowFood), `ValidActions`. Rewards: `food_production`, `shelter`, `survival`, with `farm_fertility_quality` component = mean fertility of farm cells (the center-bias metric). Scenarios: `default`, `fertile-corner`, `threat-south`, `scattered-resources`. Implements `GameEnvironment`; binary serves stdio via `GameRLServer::run_stdio()`.

- [ ] **4.1:** Failing unit tests first: seeded determinism (same seed → same state hash after same actions); `fertile-corner` has zero fertile cells within radius 16 of center; `EstablishFarm{Near:"FertileCluster_0"}` places on fertile cells; `EstablishFarm{Near:"MapCenter"}` *without* `AllowFallback` errors on `fertile-corner` (REQ-SPA-02); with `AllowFallback:true` relocates and sets `FallbackApplied`.
- [ ] **4.2:** Implement env to green. `cargo test -p game-rl-reference`.
- [ ] **4.3:** Binary: `cargo run -p game-rl-reference` speaks MCP on stdio; smoke-test with a piped initialize + tools/list.

## Task 5: `game-rl-conformance` crate

**Files:** Create `crates/game-rl-conformance/{Cargo.toml, src/main.rs, src/checks.rs, src/client.rs, src/report.rs}`; Modify root `Cargo.toml`.

CLI: `game-rl-conformance --level 3 --scenario fertile-corner -- <server-cmd> [args...]` → human table + `--json` report `{level_achieved, checks: [{id, level, pass, detail}]}`. Checks (IDs match spec REQ ids):
- C-INIT: initialize handshake, protocolVersion accepted.
- C-TOOLS: tools/list contains L1 set with camelCase names; inputSchemas are valid JSON Schema (structural).
- C-MANIFEST: `manifest` tool returns `Name`/`GameRlVersion`/`Capabilities`.
- C-LIFECYCLE: registerAgent → observe → step(Wait) → reward/done fields present (PascalCase).
- C-ERR-LOUD: bogus action type → JSON-RPC error or structured Error (not silent success).
- C-DET-RESET (L1) / C-DET-SEQ (L2): reset(seed)+actions twice → identical stateHash sequences.
- C-EPISODE (L2): episodeSummary returns TotalReward/StepCount.
- C-VALIDACTIONS, C-ALERTS (L2): sections present in observe.
- C-SPA-GRAMMAR, C-SPA-INTEGRITY (no silent fallback), C-SPA-AUDIT, C-SPA-LANDMARKS (L3) — exercised only if manifest declares `SpatialIntent`.

- [ ] **5.1:** Write `client.rs` (spawn child, line-delimited JSON-RPC over stdio, request/response with timeout). Unit-test against `game-rl-reference` binary (integration test spawning `cargo run -p game-rl-reference` or the built binary path via `env!("CARGO_BIN_EXE_...")`-style — use `assert_cmd`-free approach: locate via `target/debug`).
- [ ] **5.2:** Implement checks; integration test: reference env achieves **Level 3** with all checks pass.
- [ ] **5.3:** Negative test: a deliberately broken stub (flag on reference env, e.g. `GAMERL_REF_BREAK=silent_errors`) fails C-ERR-LOUD — proves the harness detects violations.
- [ ] **5.4:** `cargo test -p game-rl-conformance` green; workspace `cargo clippy` clean; `cargo fmt`.

## Task 6: arkavo-edge env expansion + example fix

**Files:** Modify `crates/arkavo-protocol/src/agent_config.rs` (+ its tests); Modify `examples/rimworld/agents/commander/AGENTS.md`, `examples/rimworld/launch_rimworld.sh`. Branch: new `game-rl/draft-02-convergence` off `main` in arkavo-edge.

- [ ] **6.1:** Failing test in agent_config tests: `command: ${GAME_RL_SERVER}` with env set expands; unset var leaves literal (or empty + warning — match repo conventions); expansion applies to command, args, url.
- [ ] **6.2:** Implement `expand_env(s: &str) -> String` handling `${VAR}` (no `$VAR` bare form), apply at parse time.
- [ ] **6.3:** Update commander AGENTS.md → `command: ${GAME_RL_SERVER}`; launch script exports `GAME_RL_SERVER="${GAME_RL_SERVER:-$(command -v game-rl-server || echo "$HOME/Projects/intelligence/game-rl/target/release/game-rl-server")}"`.
- [ ] **6.4:** `cargo test -p arkavo-protocol` green.

## Task 7: Conformance runs + Edge playtest scenarios

- [ ] **7.1:** `cargo build --release` in game-rl (fresh server + reference + conformance binaries).
- [ ] **7.2:** Run conformance vs reference env on all 4 scenarios; save reports to `docs/conformance/2026-06-11-reference.json`.
- [ ] **7.3:** Run conformance vs `game-rl-server` (auto-detect). If no game is live, record the documented invocation + expected gaps (e.g., RimWorld lacks REQ-SPA-02 → known finding feeding adapter work).
- [ ] **7.4:** Edge playtest suite: `examples/gridcolony/` in arkavo-edge (or scenarios under examples/rimworld/) — commander AGENTS.md pointed at `game-rl-reference` via `GAME_RL_SERVER`; scenarios = the 4 reference scenarios; success metric = `farm_fertility_quality` reward component (center-biased agents score ~0 on `fertile-corner`). Run headless with a local/cheap model if Edge builds; otherwise script the MCP loop directly (scripted-policy playtest) and record results.
- [ ] **7.5:** Feed findings back into draft-02 (contract gaps discovered during runs) before declaring it done.

## Task 8: Report + follow-ups

- [ ] **8.1:** Final report: deliverables, conformance results table, playtest metrics, RimWorld adapter gaps vs REQ-SPA (file:line), GameOfMods alias insertion points (`ArkavoEdgeMCP/Sources/ArkavoEdgeMCP/AgentTools.swift` — add camelCase aliases mapping `sim_step`→`step` etc.), version alignment status (game-rl 0.6.0 → manifest `GameRlVersion: "2.0.0-draft02"` target).

## Out of scope (documented as follow-ups)
- Implementing Spatial Intent v2 inside the RimWorld C# adapter (requires live-game playtesting to verify; conformance run will enumerate exact violations).
- GameOfMods Swift alias implementation.
- Publishing crates / spec to a registry.
