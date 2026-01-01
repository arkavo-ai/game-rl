//! Agent registry and lifecycle management

use game_rl_core::{AgentEntry, AgentId, AgentStatus, AgentType};
use std::collections::HashMap;

/// Registry of active agents
pub struct AgentRegistry {
    agents: HashMap<AgentId, AgentEntry>,
    max_agents: usize,
}

impl AgentRegistry {
    /// Create a new registry with the given capacity
    pub fn new(max_agents: usize) -> Self {
        Self {
            agents: HashMap::new(),
            max_agents,
        }
    }

    /// Register a new agent
    pub fn register(
        &mut self,
        agent_id: AgentId,
        agent_type: AgentType,
    ) -> Result<(), RegistryError> {
        if self.agents.len() >= self.max_agents {
            return Err(RegistryError::CapacityExceeded);
        }
        // Idempotent: if already registered, just return success
        if self.agents.contains_key(&agent_id) {
            return Ok(());
        }

        let entry = AgentEntry {
            agent_id: agent_id.clone(),
            agent_type,
            status: AgentStatus::Registered,
            registered_at: chrono_lite::now_utc(),
            last_step: 0,
            total_reward: 0.0,
        };

        self.agents.insert(agent_id, entry);
        Ok(())
    }

    /// Deregister an agent
    pub fn deregister(&mut self, agent_id: &AgentId) -> Result<(), RegistryError> {
        self.agents
            .remove(agent_id)
            .map(|_| ())
            .ok_or_else(|| RegistryError::NotFound(agent_id.clone()))
    }

    /// Get an agent entry
    pub fn get(&self, agent_id: &AgentId) -> Option<&AgentEntry> {
        self.agents.get(agent_id)
    }

    /// Get a mutable agent entry
    pub fn get_mut(&mut self, agent_id: &AgentId) -> Option<&mut AgentEntry> {
        self.agents.get_mut(agent_id)
    }

    /// Update agent status
    pub fn set_status(&mut self, agent_id: &AgentId, status: AgentStatus) {
        if let Some(entry) = self.agents.get_mut(agent_id) {
            entry.status = status;
        }
    }

    /// Record a step for an agent
    pub fn record_step(&mut self, agent_id: &AgentId, reward: f64) {
        if let Some(entry) = self.agents.get_mut(agent_id) {
            entry.last_step += 1;
            entry.total_reward += reward;
        }
    }

    /// List all agents
    pub fn list(&self) -> Vec<&AgentEntry> {
        self.agents.values().collect()
    }

    /// Number of registered agents
    pub fn count(&self) -> usize {
        self.agents.len()
    }

    /// Available slots
    pub fn available_slots(&self) -> usize {
        self.max_agents - self.agents.len()
    }
}

/// Registry errors
#[derive(Debug, thiserror::Error)]
pub enum RegistryError {
    #[error("Agent already registered: {0}")]
    AlreadyRegistered(AgentId),
    #[error("Agent not found: {0}")]
    NotFound(AgentId),
    #[error("Maximum agent capacity exceeded")]
    CapacityExceeded,
}

/// Simple timestamp helper (no heavy chrono dependency)
mod chrono_lite {
    pub fn now_utc() -> String {
        // In production, use proper time crate
        "2025-01-01T00:00:00Z".to_string()
    }
}
