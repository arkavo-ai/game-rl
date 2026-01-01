//! Lua compatibility tests
//!
//! These tests run actual Lua code using the same JSON.lua that PZ uses,
//! verifying that Rust can deserialize Lua's output and vice versa.

#[cfg(test)]
mod tests {
    use crate::protocol::GameMessage;
    use mlua::{Lua, Result as LuaResult};

    /// Load our JSON.lua into a Lua state and register it for require()
    fn create_lua_with_json() -> LuaResult<Lua> {
        let lua = Lua::new();

        // Load the actual JSON.lua from the zomboid adapter
        let json_lua = include_str!("../../../adapters/zomboid/mod/media/lua/shared/GameRL/JSON.lua");

        // Execute JSON.lua and capture its return value
        let json_module: mlua::Value = lua.load(json_lua).eval()?;

        // Register it in package.loaded so require("JSON") works
        let package: mlua::Table = lua.globals().get("package")?;
        let loaded: mlua::Table = package.get("loaded")?;
        loaded.set("JSON", json_module)?;

        Ok(lua)
    }

    // ========== Lua → Rust tests ==========

    #[test]
    fn test_lua_step_result_to_rust() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        // Simulate what GameRL.lua does for StepResult
        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "StepResult",
                AgentId = "test-agent",
                Observation = { tick = 100 },
                Reward = 0.5,
                RewardComponents = {},
                Done = false,
                Truncated = false,
                StateHash = "abc123"
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        // Verify Rust can deserialize it
        let msg: GameMessage = serde_json::from_str(&json)
            .expect("Rust should deserialize Lua's StepResult");

        match msg {
            GameMessage::StepResult { result } => {
                assert_eq!(result.agent_id, "test-agent");
                assert_eq!(result.reward, 0.5);
                assert!(!result.done);
            }
            _ => panic!("Wrong message type: {:?}", msg),
        }

        Ok(())
    }

    #[test]
    fn test_lua_empty_reward_components() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        // Empty table {} should serialize as {} not []
        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "StepResult",
                AgentId = "agent1",
                Observation = {},
                Reward = 0.0,
                RewardComponents = {},
                Done = false,
                Truncated = false
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        // This should NOT contain "[]" for RewardComponents
        assert!(!json.contains("\"RewardComponents\":[]"),
            "Empty table should be {{}} not []: {}", json);

        // Verify Rust can deserialize
        let msg: GameMessage = serde_json::from_str(&json)
            .expect("Rust should deserialize empty RewardComponents as {}");

        match msg {
            GameMessage::StepResult { result } => {
                assert!(result.reward_components.is_empty());
            }
            _ => panic!("Wrong message type"),
        }

        Ok(())
    }

    #[test]
    fn test_lua_reward_components_with_values() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "StepResult",
                AgentId = "agent1",
                Observation = {},
                Reward = 1.5,
                RewardComponents = { survival = 1.0, combat = 0.5 },
                Done = false,
                Truncated = false
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        let msg: GameMessage = serde_json::from_str(&json).unwrap();

        match msg {
            GameMessage::StepResult { result } => {
                assert_eq!(result.reward_components.len(), 2);
                assert_eq!(result.reward_components.get("survival"), Some(&1.0));
                assert_eq!(result.reward_components.get("combat"), Some(&0.5));
            }
            _ => panic!("Wrong message type"),
        }

        Ok(())
    }

    #[test]
    fn test_lua_ready_message() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "Ready",
                Name = "Project Zomboid",
                Version = "0.5.0",
                Capabilities = {
                    MultiAgent = true,
                    MaxAgents = 4,
                    Deterministic = false,
                    Headless = false
                }
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        let msg: GameMessage = serde_json::from_str(&json)
            .expect("Rust should deserialize Lua's Ready message");

        match msg {
            GameMessage::Ready { name, version, capabilities } => {
                assert_eq!(name, "Project Zomboid");
                assert_eq!(version, "0.5.0");
                assert!(capabilities.multi_agent);
                assert_eq!(capabilities.max_agents, 4);
            }
            _ => panic!("Wrong message type"),
        }

        Ok(())
    }

    #[test]
    fn test_lua_state_hash() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "StateHash",
                Hash = "00b866e0"
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        let msg: GameMessage = serde_json::from_str(&json).unwrap();

        match msg {
            GameMessage::StateHash { hash } => {
                assert_eq!(hash, "00b866e0");
            }
            _ => panic!("Wrong message type"),
        }

        Ok(())
    }

    #[test]
    fn test_lua_agent_registered() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "AgentRegistered",
                AgentId = "player1",
                ObservationSpace = {},
                ActionSpace = {
                    type = "discrete_parameterized",
                    actions = {
                        { name = "Wait", description = "Do nothing" },
                        { name = "Move", description = "Move to position" }
                    }
                }
            })
        "#).eval()?;

        println!("Lua produced: {}", json);

        let msg: GameMessage = serde_json::from_str(&json).unwrap();

        match msg {
            GameMessage::AgentRegistered { agent_id, .. } => {
                assert_eq!(agent_id, "player1");
            }
            _ => panic!("Wrong message type"),
        }

        Ok(())
    }

    // ========== Rust → Lua tests ==========

    #[test]
    fn test_rust_execute_action_to_lua() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        // Serialize from Rust
        let msg = GameMessage::ExecuteAction {
            agent_id: "agent1".into(),
            action: game_rl_core::Action::Wait,
            ticks: 60,
        };
        let json = serde_json::to_string(&msg).unwrap();

        println!("Rust produced: {}", json);

        // Verify Lua can decode it
        lua.globals().set("rust_json", json.clone())?;

        let result: mlua::Table = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.decode(rust_json)
        "#).eval()?;

        assert_eq!(result.get::<String>("Type")?, "ExecuteAction");
        assert_eq!(result.get::<String>("AgentId")?, "agent1");
        assert_eq!(result.get::<i32>("Ticks")?, 60);

        Ok(())
    }

    #[test]
    fn test_rust_register_agent_to_lua() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let msg = GameMessage::RegisterAgent {
            agent_id: "test".into(),
            agent_type: game_rl_core::AgentType::ColonyManager,
            config: game_rl_core::AgentConfig::default(),
        };
        let json = serde_json::to_string(&msg).unwrap();

        println!("Rust produced: {}", json);

        lua.globals().set("rust_json", json)?;

        let result: mlua::Table = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.decode(rust_json)
        "#).eval()?;

        assert_eq!(result.get::<String>("Type")?, "RegisterAgent");
        assert_eq!(result.get::<String>("AgentId")?, "test");
        assert_eq!(result.get::<String>("AgentType")?, "ColonyManager");

        Ok(())
    }

    #[test]
    fn test_rust_get_state_hash_to_lua() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let msg = GameMessage::GetStateHash;
        let json = serde_json::to_string(&msg).unwrap();

        println!("Rust produced: {}", json);

        lua.globals().set("rust_json", json)?;

        let result: mlua::Table = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.decode(rust_json)
        "#).eval()?;

        assert_eq!(result.get::<String>("Type")?, "GetStateHash");

        Ok(())
    }

    #[test]
    fn test_rust_reset_to_lua() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        let msg = GameMessage::Reset {
            seed: Some(42),
            scenario: Some("tutorial".into()),
        };
        let json = serde_json::to_string(&msg).unwrap();

        println!("Rust produced: {}", json);

        lua.globals().set("rust_json", json)?;

        let result: mlua::Table = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.decode(rust_json)
        "#).eval()?;

        assert_eq!(result.get::<String>("Type")?, "Reset");
        assert_eq!(result.get::<i64>("Seed")?, 42);
        assert_eq!(result.get::<String>("Scenario")?, "tutorial");

        Ok(())
    }

    // ========== Round-trip tests ==========

    #[test]
    fn test_roundtrip_step_result() -> LuaResult<()> {
        let lua = create_lua_with_json()?;

        // Lua creates a StepResult
        let lua_json: String = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.encode({
                Type = "StepResult",
                AgentId = "roundtrip-agent",
                Observation = { tick = 999, data = "test" },
                Reward = 42.5,
                RewardComponents = { a = 1.0, b = 2.0 },
                Done = true,
                Truncated = false,
                StateHash = "roundtrip-hash"
            })
        "#).eval()?;

        // Rust deserializes
        let msg: GameMessage = serde_json::from_str(&lua_json).unwrap();

        // Rust serializes back
        let rust_json = serde_json::to_string(&msg).unwrap();

        // Lua deserializes Rust's output
        lua.globals().set("rust_json", rust_json)?;

        let result: mlua::Table = lua.load(r#"
            local JSON = require("JSON") or _G.JSON
            return JSON.decode(rust_json)
        "#).eval()?;

        // Verify values survived the round-trip
        assert_eq!(result.get::<String>("Type")?, "StepResult");
        assert_eq!(result.get::<String>("AgentId")?, "roundtrip-agent");
        assert_eq!(result.get::<f64>("Reward")?, 42.5);
        assert_eq!(result.get::<bool>("Done")?, true);

        Ok(())
    }
}
