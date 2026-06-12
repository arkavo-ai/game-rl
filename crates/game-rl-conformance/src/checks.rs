//! Conformance checks, mapped to spec draft-02 requirement IDs (§14.2)

use crate::client::{McpChild, ToolOutcome};
use anyhow::Result;
use serde::Serialize;
use serde_json::{Value, json};

#[derive(Debug, Clone, Serialize)]
pub struct CheckResult {
    pub id: String,
    pub level: u8,
    pub pass: bool,
    pub skipped: bool,
    pub detail: String,
}

impl CheckResult {
    fn pass(id: &str, level: u8, detail: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            level,
            pass: true,
            skipped: false,
            detail: detail.into(),
        }
    }
    fn fail(id: &str, level: u8, detail: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            level,
            pass: false,
            skipped: false,
            detail: detail.into(),
        }
    }
    fn skip(id: &str, level: u8, detail: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            level,
            pass: true,
            skipped: true,
            detail: detail.into(),
        }
    }
}

pub struct Suite<'a> {
    pub client: &'a mut McpChild,
    pub seed: u64,
    pub scenario: String,
    pub agent_id: String,
    manifest: Option<Value>,
}

fn outcome_payload(outcome: &ToolOutcome) -> Value {
    outcome.payload().cloned().unwrap_or(Value::Null)
}

impl<'a> Suite<'a> {
    pub fn new(client: &'a mut McpChild, seed: u64, scenario: &str) -> Self {
        Self {
            client,
            seed,
            scenario: scenario.to_string(),
            agent_id: "conformance".to_string(),
            manifest: None,
        }
    }

    fn spatial_supported(&self) -> bool {
        self.manifest
            .as_ref()
            .and_then(|m| m.pointer("/Capabilities/SpatialIntent"))
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
    }

    fn is_reference_env(&self) -> bool {
        self.manifest
            .as_ref()
            .and_then(|m| m.get("Name"))
            .and_then(|v| v.as_str())
            == Some("GridColony")
    }

    async fn state_hash(&mut self) -> Result<Option<String>> {
        let outcome = self.client.call_tool("stateHash", json!({})).await?;
        Ok(outcome
            .payload()
            .and_then(|p| p.get("Hash").or_else(|| p.get("hash")))
            .and_then(|h| h.as_str())
            .map(str::to_string))
    }

    async fn reset(&mut self, seed: u64) -> Result<ToolOutcome> {
        self.client
            .call_tool(
                "reset",
                json!({"Seed": seed, "Scenario": self.scenario.clone()}),
            )
            .await
    }

    async fn step_wait(&mut self, ticks: u64) -> Result<ToolOutcome> {
        self.client
            .call_tool(
                "step",
                json!({"AgentId": self.agent_id.clone(), "Action": {"Type": "Wait"}, "Ticks": ticks}),
            )
            .await
    }

    // ----- Level 1 -----

    pub async fn c_init(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-INIT";
        let result = match self.client.initialize().await {
            Ok(r) => r,
            Err(e) => return Ok(CheckResult::fail(ID, 1, format!("handshake failed: {e}"))),
        };
        let version = result
            .get("protocolVersion")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        let game_rl = result
            .pointer("/serverInfo/gameRlVersion")
            .and_then(|v| v.as_str());
        match (version.is_empty(), game_rl) {
            (false, Some(grl)) => Ok(CheckResult::pass(
                ID,
                1,
                format!("protocolVersion={version}, gameRlVersion={grl}"),
            )),
            (false, None) => Ok(CheckResult::fail(
                ID,
                1,
                "serverInfo.gameRlVersion missing (spec §3.1)",
            )),
            _ => Ok(CheckResult::fail(ID, 1, "no protocolVersion negotiated")),
        }
    }

    pub async fn c_tools(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-TOOLS";
        let tools = self.client.list_tools().await?;
        let names: Vec<&str> = tools
            .iter()
            .filter_map(|t| t.get("name").and_then(|n| n.as_str()))
            .collect();
        let required = [
            "manifest",
            "registerAgent",
            "deregisterAgent",
            "step",
            "observe",
            "reset",
        ];
        let missing: Vec<&&str> = required.iter().filter(|r| !names.contains(*r)).collect();
        if !missing.is_empty() {
            return Ok(CheckResult::fail(
                ID,
                1,
                format!("missing L1 tools: {missing:?}; found: {names:?}"),
            ));
        }
        for tool in &tools {
            let schema_type = tool.pointer("/inputSchema/type").and_then(|t| t.as_str());
            if schema_type != Some("object") {
                let name = tool.get("name").and_then(|n| n.as_str()).unwrap_or("?");
                return Ok(CheckResult::fail(
                    ID,
                    1,
                    format!("tool {name} has invalid inputSchema"),
                ));
            }
        }
        Ok(CheckResult::pass(
            ID,
            1,
            format!("{} tools, L1 set complete", tools.len()),
        ))
    }

    pub async fn c_manifest(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-MANIFEST";
        let outcome = self.client.call_tool("manifest", json!({})).await?;
        if let Some(msg) = outcome.error_message() {
            return Ok(CheckResult::fail(
                ID,
                1,
                format!("manifest tool failed: {msg}"),
            ));
        }
        let payload = outcome_payload(&outcome);
        let mut missing = Vec::new();
        for field in ["Name", "GameRlVersion", "Capabilities"] {
            if payload.get(field).is_none() {
                missing.push(field);
            }
        }
        if !missing.is_empty() {
            return Ok(CheckResult::fail(
                ID,
                1,
                format!("manifest missing fields {missing:?} (PascalCase required)"),
            ));
        }
        self.manifest = Some(payload.clone());
        Ok(CheckResult::pass(
            ID,
            1,
            format!(
                "{} (gameRl {})",
                payload["Name"].as_str().unwrap_or("?"),
                payload["GameRlVersion"].as_str().unwrap_or("?")
            ),
        ))
    }

    pub async fn c_lifecycle(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-LIFECYCLE";
        let register = self
            .client
            .call_tool(
                "registerAgent",
                json!({"AgentId": self.agent_id.clone(), "AgentType": "Controller"}),
            )
            .await?;
        if let Some(msg) = register.error_message() {
            return Ok(CheckResult::fail(
                ID,
                1,
                format!("registerAgent failed: {msg}"),
            ));
        }
        let reset = self.reset(self.seed).await?;
        if let Some(msg) = reset.error_message() {
            return Ok(CheckResult::fail(ID, 1, format!("reset failed: {msg}")));
        }
        let observe = self.client.call_tool("observe", json!({})).await?;
        if let Some(msg) = observe.error_message() {
            return Ok(CheckResult::fail(ID, 1, format!("observe failed: {msg}")));
        }
        let step = self.step_wait(60).await?;
        if let Some(msg) = step.error_message() {
            return Ok(CheckResult::fail(ID, 1, format!("step failed: {msg}")));
        }
        // Default elision (spec §5.2): Reward/Done/Truncated may be absent when
        // default-valued, but if present they must be the right JSON type.
        let payload = outcome_payload(&step);
        if let Some(r) = payload.get("Reward") {
            if !r.is_number() {
                return Ok(CheckResult::fail(ID, 1, "Reward is not a number"));
            }
        }
        if payload.get("reward").is_some() || payload.get("agent_id").is_some() {
            return Ok(CheckResult::fail(
                ID,
                1,
                "step result uses snake_case fields — draft-02 requires PascalCase",
            ));
        }
        Ok(CheckResult::pass(
            ID,
            1,
            "register → reset → observe → step OK",
        ))
    }

    pub async fn c_err_loud(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-ERR-LOUD";
        // Probe (a): an action type that cannot exist
        let bogus = self
            .client
            .call_tool(
                "step",
                json!({"AgentId": self.agent_id.clone(),
                       "Action": {"Type": "DefinitelyNotARealActionXyz"}, "Ticks": 0}),
            )
            .await?;
        let payload_error = bogus
            .payload()
            .and_then(|p| p.get("Error"))
            .map(|e| !e.is_null())
            .unwrap_or(false);
        if !bogus.is_err() && !payload_error {
            return Ok(CheckResult::fail(
                ID,
                1,
                "unknown action returned success with no Error field (REQ-ERR-01)",
            ));
        }

        // Probe (b), reference env only: an action that exists in the manifest
        // but is contextually invalid — catches environments that silently
        // swallow in-game failures even when schema validation passes.
        if self.is_reference_env() && self.scenario != "threat-south" {
            let contextual = self
                .client
                .call_tool(
                    "step",
                    json!({"AgentId": self.agent_id.clone(),
                           "Action": {"Type": "DefendColony"}, "Ticks": 0}),
                )
                .await?;
            let ctx_error = contextual
                .payload()
                .and_then(|p| p.get("Error"))
                .map(|e| !e.is_null())
                .unwrap_or(false);
            if !contextual.is_err() && !ctx_error {
                return Ok(CheckResult::fail(
                    ID,
                    1,
                    "contextually-invalid action (DefendColony with no hostiles) returned silent success (REQ-ERR-01)",
                ));
            }
        }
        Ok(CheckResult::pass(ID, 1, "failed actions error loudly"))
    }

    pub async fn c_det_reset(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-DET-RESET";
        self.reset(12345).await?;
        let h1 = self.state_hash().await?;
        self.reset(12345).await?;
        let h2 = self.state_hash().await?;
        match (h1, h2) {
            (Some(a), Some(b)) if a == b => Ok(CheckResult::pass(
                ID,
                1,
                format!("seeded reset reproducible ({})", &a[..23.min(a.len())]),
            )),
            (Some(a), Some(b)) => Ok(CheckResult::fail(
                ID,
                1,
                format!("REQ-DET-01 violated: {a} != {b}"),
            )),
            _ => Ok(CheckResult::skip(
                ID,
                1,
                "stateHash unavailable; cannot verify",
            )),
        }
    }

    // ----- Level 2 -----

    pub async fn c_det_seq(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-DET-SEQ";
        let mut sequences = Vec::new();
        for _ in 0..2 {
            self.reset(777).await?;
            let mut hashes = Vec::new();
            for _ in 0..3 {
                let step = self.step_wait(60).await?;
                if let Some(msg) = step.error_message() {
                    return Ok(CheckResult::fail(ID, 2, format!("step failed: {msg}")));
                }
                match self.state_hash().await? {
                    Some(h) => hashes.push(h),
                    None => return Ok(CheckResult::skip(ID, 2, "stateHash unavailable")),
                }
            }
            sequences.push(hashes);
        }
        if sequences[0] == sequences[1] {
            Ok(CheckResult::pass(
                ID,
                2,
                "3-step hash sequence reproducible (REQ-DET-02)",
            ))
        } else {
            Ok(CheckResult::fail(
                ID,
                2,
                format!(
                    "hash sequences diverge: {:?} vs {:?}",
                    sequences[0], sequences[1]
                ),
            ))
        }
    }

    pub async fn c_episode(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-EPISODE";
        let outcome = self.client.call_tool("episodeSummary", json!({})).await?;
        if let Some(msg) = outcome.error_message() {
            return Ok(CheckResult::fail(
                ID,
                2,
                format!("episodeSummary failed: {msg}"),
            ));
        }
        let payload = outcome_payload(&outcome);
        if payload.get("TotalReward").map(|v| v.is_number()) == Some(true)
            && payload.get("StepCount").map(|v| v.is_number()) == Some(true)
        {
            Ok(CheckResult::pass(
                ID,
                2,
                format!(
                    "TotalReward={}, StepCount={}",
                    payload["TotalReward"], payload["StepCount"]
                ),
            ))
        } else {
            Ok(CheckResult::fail(
                ID,
                2,
                format!("missing TotalReward/StepCount: {payload}"),
            ))
        }
    }

    pub async fn c_valid_actions(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-VALIDACTIONS";
        let outcome = self
            .client
            .call_tool("observe", json!({"Include": ["actions"]}))
            .await?;
        let payload = outcome_payload(&outcome);
        let actions = payload
            .get("ValidActions")
            .or_else(|| payload.pointer("/Observation/ValidActions"))
            .and_then(|v| v.as_array());
        match actions {
            Some(list) if !list.is_empty() => Ok(CheckResult::pass(
                ID,
                2,
                format!("{} valid action types exposed (REQ-OBS-01)", list.len()),
            )),
            _ => Ok(CheckResult::fail(
                ID,
                2,
                "observe(Include=[actions]) returned no ValidActions",
            )),
        }
    }

    pub async fn c_alerts(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-ALERTS";
        let outcome = self
            .client
            .call_tool("observe", json!({"Include": ["alerts"]}))
            .await?;
        if let Some(msg) = outcome.error_message() {
            return Ok(CheckResult::fail(
                ID,
                2,
                format!("observe(alerts) failed: {msg}"),
            ));
        }
        let payload = outcome_payload(&outcome);
        let alerts = payload
            .get("Alerts")
            .or_else(|| payload.pointer("/Observation/Alerts"));
        match alerts {
            Some(Value::Array(list)) => {
                let has_severity = list
                    .iter()
                    .all(|a| a.get("Severity").map(|s| s.is_number()).unwrap_or(false));
                if has_severity {
                    Ok(CheckResult::pass(
                        ID,
                        2,
                        format!("{} alerts with Severity (REQ-OBS-02)", list.len()),
                    ))
                } else {
                    Ok(CheckResult::fail(ID, 2, "alerts missing Severity field"))
                }
            }
            // Default elision strips empty arrays; accept absence as "no alerts now"
            None => Ok(CheckResult::pass(
                ID,
                2,
                "no active alerts (section elided)",
            )),
            Some(other) => Ok(CheckResult::fail(
                ID,
                2,
                format!("Alerts is not an array: {other}"),
            )),
        }
    }

    // ----- Level 3 (spatial; requires Capabilities.SpatialIntent) -----

    pub async fn c_spa_grammar(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-SPA-GRAMMAR";
        if !self.spatial_supported() {
            return Ok(CheckResult::skip(ID, 3, "SpatialIntent not declared"));
        }
        let anchors = [
            json!("ColonyCenter"),
            json!({"Anchor": "ColonyCenter", "Direction": "NE", "Distance": 8}),
            json!({"X": 5, "Y": 5}),
        ];
        for (label, near) in ["named", "offset", "coordinate"].iter().zip(anchors) {
            let outcome = self
                .client
                .call_tool(
                    "resolveSpatial",
                    json!({"Intent": {"Type": "EstablishStorage", "Near": near, "Size": 4, "AllowFallback": true}}),
                )
                .await?;
            if let Some(msg) = outcome.error_message() {
                return Ok(CheckResult::fail(
                    ID,
                    3,
                    format!("{label} anchor form rejected (REQ-SPA-01): {msg}"),
                ));
            }
        }
        Ok(CheckResult::pass(
            ID,
            3,
            "named, offset, and coordinate anchors accepted",
        ))
    }

    pub async fn c_spa_landmarks(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-SPA-LANDMARKS";
        if !self.spatial_supported() {
            return Ok(CheckResult::skip(ID, 3, "SpatialIntent not declared"));
        }
        let outcome = self
            .client
            .call_tool("observe", json!({"Include": ["landmarks"], "Limit": 50}))
            .await?;
        let payload = outcome_payload(&outcome);
        let landmarks = payload
            .get("Landmarks")
            .or_else(|| payload.pointer("/Observation/Landmarks"))
            .and_then(|v| v.as_array())
            .cloned()
            .unwrap_or_default();
        let ids: Vec<&str> = landmarks
            .iter()
            .filter_map(|l| l.get("Id").and_then(|i| i.as_str()))
            .collect();
        let has_centroid = ids.contains(&"ColonyCenter") || ids.contains(&"AgentCenter");
        let has_map_center = ids.contains(&"MapCenter");
        let region_count = ids.iter().filter(|i| i.starts_with("Region_")).count();
        if has_centroid && has_map_center && region_count >= 8 {
            Ok(CheckResult::pass(
                ID,
                3,
                format!(
                    "{} landmarks incl. centroids + {region_count} compass regions (REQ-SPA-04)",
                    ids.len()
                ),
            ))
        } else {
            Ok(CheckResult::fail(
                ID,
                3,
                format!(
                    "minimum landmark set missing (centroid={has_centroid}, mapCenter={has_map_center}, regions={region_count}); ids: {ids:?}"
                ),
            ))
        }
    }

    pub async fn c_spa_integrity(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-SPA-INTEGRITY";
        if !self.spatial_supported() {
            return Ok(CheckResult::skip(ID, 3, "SpatialIntent not declared"));
        }
        if !self.is_reference_env() || self.scenario != "fertile-corner" {
            return Ok(CheckResult::skip(
                ID,
                3,
                "needs a known-infeasible probe (run reference env with --scenario fertile-corner)",
            ));
        }
        // On fertile-corner, a farm at MapCenter is infeasible by construction.
        let strict = self
            .client
            .call_tool(
                "resolveSpatial",
                json!({"Intent": {"Type": "EstablishFarm", "Near": "MapCenter"}}),
            )
            .await?;
        let Some(msg) = strict.error_message() else {
            return Ok(CheckResult::fail(
                ID,
                3,
                "infeasible farm at MapCenter resolved without error — silent relocation (REQ-SPA-02)",
            ));
        };
        if !msg.contains("Alternative") {
            return Ok(CheckResult::fail(
                ID,
                3,
                format!("infeasibility error offers no alternatives: {msg}"),
            ));
        }
        // With explicit opt-in, fallback must be flagged.
        let fallback = self
            .client
            .call_tool(
                "resolveSpatial",
                json!({"Intent": {"Type": "EstablishFarm", "Near": "MapCenter", "AllowFallback": true}}),
            )
            .await?;
        let payload = outcome_payload(&fallback);
        if payload.get("FallbackApplied").and_then(|v| v.as_bool()) != Some(true) {
            return Ok(CheckResult::fail(
                ID,
                3,
                "AllowFallback relocation not flagged via FallbackApplied (REQ-SPA-06)",
            ));
        }
        Ok(CheckResult::pass(
            ID,
            3,
            "no silent relocation; opt-in fallback audited",
        ))
    }

    pub async fn c_spa_audit(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-SPA-AUDIT";
        if !self.spatial_supported() {
            return Ok(CheckResult::skip(ID, 3, "SpatialIntent not declared"));
        }
        let outcome = self
            .client
            .call_tool(
                "resolveSpatial",
                json!({"Intent": {"Type": "EstablishStorage", "Near": "ColonyCenter", "Size": 4}}),
            )
            .await?;
        if let Some(msg) = outcome.error_message() {
            return Ok(CheckResult::fail(
                ID,
                3,
                format!("resolveSpatial failed: {msg}"),
            ));
        }
        let payload = outcome_payload(&outcome);
        let mut missing = Vec::new();
        for field in ["AnchorRequested", "AnchorResolved", "Positions"] {
            if payload.get(field).is_none() {
                missing.push(field);
            }
        }
        // FallbackApplied=false may be elided by serde default — present or false both fine
        if missing.is_empty() {
            Ok(CheckResult::pass(
                ID,
                3,
                "ResolvedPlacement carries full audit trail (REQ-SPA-06)",
            ))
        } else {
            Ok(CheckResult::fail(
                ID,
                3,
                format!("ResolvedPlacement missing {missing:?}: {payload}"),
            ))
        }
    }

    // ----- Level 3 (infrastructure) -----

    pub async fn c_streams(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-STREAMS";
        let profile = self
            .manifest
            .as_ref()
            .and_then(|m| m.get("StreamProfiles"))
            .and_then(|p| p.as_object())
            .and_then(|p| p.keys().min().cloned());
        let Some(profile) = profile else {
            return Ok(CheckResult::fail(
                ID,
                3,
                "manifest declares no StreamProfiles",
            ));
        };
        let outcome = self
            .client
            .call_tool(
                "configureStreams",
                json!({"AgentId": self.agent_id.clone(), "Profile": profile}),
            )
            .await?;
        match outcome.error_message() {
            None => Ok(CheckResult::pass(
                ID,
                3,
                format!("profile \"{profile}\" configured"),
            )),
            Some(msg) => Ok(CheckResult::fail(
                ID,
                3,
                format!("configureStreams failed: {msg}"),
            )),
        }
    }

    pub async fn c_trajectory(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-TRAJECTORY";
        let path = std::env::temp_dir().join(format!(
            "gamerl-conformance-{}.traj.json",
            std::process::id()
        ));
        let path_str = path.to_string_lossy().to_string();

        self.reset(999).await?;
        self.step_wait(60).await?;
        self.step_wait(60).await?;
        let hash_before = self.state_hash().await?;

        let save = self
            .client
            .call_tool("saveTrajectory", json!({"Path": path_str.clone()}))
            .await?;
        if let Some(msg) = save.error_message() {
            return Ok(CheckResult::fail(
                ID,
                3,
                format!("saveTrajectory failed: {msg}"),
            ));
        }
        let load = self
            .client
            .call_tool("loadTrajectory", json!({"Path": path_str.clone()}))
            .await?;
        let _ = std::fs::remove_file(&path);
        if let Some(msg) = load.error_message() {
            return Ok(CheckResult::fail(
                ID,
                3,
                format!("loadTrajectory failed: {msg}"),
            ));
        }
        let hash_after = self.state_hash().await?;
        match (hash_before, hash_after) {
            (Some(a), Some(b)) if a == b => Ok(CheckResult::pass(
                ID,
                3,
                "trajectory replay reproduces state hash",
            )),
            (Some(a), Some(b)) => Ok(CheckResult::fail(
                ID,
                3,
                format!("replay diverged: {a} != {b}"),
            )),
            _ => Ok(CheckResult::pass(
                ID,
                3,
                "trajectory save/load OK (hash unavailable)",
            )),
        }
    }

    pub async fn c_batch(&mut self) -> Result<CheckResult> {
        const ID: &str = "C-BATCH";
        // Multi-agent: register a second agent, then batch step both
        let second = self
            .client
            .call_tool(
                "registerAgent",
                json!({"AgentId": "conformance2", "AgentType": "Observer"}),
            )
            .await?;
        if let Some(msg) = second.error_message() {
            return Ok(CheckResult::fail(
                ID,
                3,
                format!("second registerAgent failed: {msg}"),
            ));
        }
        let outcome = self
            .client
            .call_tool(
                "batchStep",
                json!({"Steps": [
                    {"AgentId": self.agent_id.clone(), "Action": {"Type": "Wait"}, "Ticks": 1},
                    {"AgentId": "conformance2", "Action": {"Type": "Wait"}, "Ticks": 1}
                ], "SyncMode": "barrier"}),
            )
            .await?;
        if let Some(msg) = outcome.error_message() {
            return Ok(CheckResult::fail(ID, 3, format!("batchStep failed: {msg}")));
        }
        let count = outcome_payload(&outcome)
            .get("Results")
            .and_then(|r| r.as_array())
            .map(|r| r.len())
            .unwrap_or(0);
        if count == 2 {
            Ok(CheckResult::pass(ID, 3, "2-agent barrier batch step OK"))
        } else {
            Ok(CheckResult::fail(
                ID,
                3,
                format!("expected 2 results, got {count}"),
            ))
        }
    }
}

/// Run all checks up to `level`; returns results in execution order.
pub async fn run_suite(
    client: &mut McpChild,
    level: u8,
    seed: u64,
    scenario: &str,
) -> Result<Vec<CheckResult>> {
    let mut suite = Suite::new(client, seed, scenario);
    let mut results = Vec::new();

    results.push(suite.c_init().await?);
    results.push(suite.c_tools().await?);
    results.push(suite.c_manifest().await?);
    results.push(suite.c_lifecycle().await?);
    results.push(suite.c_err_loud().await?);
    results.push(suite.c_det_reset().await?);

    if level >= 2 {
        results.push(suite.c_det_seq().await?);
        results.push(suite.c_episode().await?);
        results.push(suite.c_valid_actions().await?);
        results.push(suite.c_alerts().await?);
    }
    if level >= 3 {
        results.push(suite.c_spa_grammar().await?);
        results.push(suite.c_spa_landmarks().await?);
        results.push(suite.c_spa_integrity().await?);
        results.push(suite.c_spa_audit().await?);
        results.push(suite.c_streams().await?);
        results.push(suite.c_trajectory().await?);
        results.push(suite.c_batch().await?);
    }
    Ok(results)
}
