-- GameRL control.lua
-- Multi-agent RL training infrastructure for Factorio 2.0
-- https://github.com/arkavo-ai/game-rl

local VERSION = "0.5.0"

-- ============================================================================
-- State Management
-- ============================================================================

-- Global state stored in `storage` (Factorio 2.0 renamed from `global`)
local function init_storage()
    storage.gamerl = storage.gamerl or {
        agents = {},           -- Registered agents
        step_pending = false,  -- Whether a step is in progress
        current_tick = 0,      -- Last processed tick
        episode_seed = nil,    -- Current episode seed
        scenario = nil,        -- Current scenario
        observation_profile = "normal",
    }
end

-- ============================================================================
-- Observation Extraction
-- ============================================================================

local function get_force_stats(force)
    local stats = {
        research = nil,
        technologies_researched = {},
        items_produced = {},
        items_consumed = {},
    }

    -- Current research
    local current = force.current_research
    if current then
        stats.research = {
            current = current.name,
            progress = force.research_progress,
        }
    end

    -- Count researched technologies
    local count = 0
    for name, tech in pairs(force.technologies) do
        if tech.researched then
            count = count + 1
        end
    end
    stats.technologies_researched_count = count

    return stats
end

local function get_power_stats(surface, force)
    local stats = {
        production = 0,
        consumption = 0,
        satisfaction = 1.0,
    }

    -- Get electric network statistics from the first accumulator or pole
    local entities = surface.find_entities_filtered{
        force = force,
        type = {"electric-pole", "accumulator"},
        limit = 1
    }

    if #entities > 0 and entities[1].electric_network_id then
        local network = entities[1].electric_network_statistics
        if network then
            -- These are flow statistics over time
            stats.production = network.get_flow_count{
                name = "electricity",
                input = true,
                precision_index = defines.flow_precision_index.one_second
            } or 0
            stats.consumption = network.get_flow_count{
                name = "electricity",
                input = false,
                precision_index = defines.flow_precision_index.one_second
            } or 0
        end
    end

    -- Calculate satisfaction ratio
    if stats.consumption > 0 then
        stats.satisfaction = math.min(1.0, stats.production / stats.consumption)
    end

    return stats
end

local function get_pollution_stats(surface)
    local total = 0
    local chunks_counted = 0

    -- Sample pollution from chunks
    for chunk in surface.get_chunks() do
        total = total + surface.get_pollution{chunk.x * 32, chunk.y * 32}
        chunks_counted = chunks_counted + 1
        if chunks_counted > 100 then break end  -- Limit for performance
    end

    return {
        total = total,
        rate = 0,  -- Would need to track over time
    }
end

local function get_entity_state(entity)
    local state = {
        id = entity.unit_number or 0,
        type = entity.type,
        name = entity.name,
        position = {x = entity.position.x, y = entity.position.y},
        direction = entity.direction or 0,
    }

    -- Health
    if entity.health then
        state.health = entity.health / entity.max_health
    end

    -- Recipe for assemblers
    if entity.type == "assembling-machine" or entity.type == "furnace" then
        local recipe = entity.get_recipe()
        if recipe then
            state.recipe = recipe.name
        end
        state.crafting_progress = entity.crafting_progress or 0
    end

    -- Energy for powered entities
    if entity.energy then
        state.energy = entity.energy
    end

    return state
end

local function get_max_entities()
    return settings.global["gamerl-max-entities"].value
end

local function extract_entities(surface, force, bounds)
    local entities = {}
    local max_entities = get_max_entities()
    local filter = {
        force = force,
        area = bounds and {{bounds.x_min, bounds.y_min}, {bounds.x_max, bounds.y_max}} or nil,
    }

    for _, entity in pairs(surface.find_entities_filtered(filter)) do
        if entity.unit_number then  -- Only numbered entities
            table.insert(entities, get_entity_state(entity))
            if #entities >= max_entities then break end  -- Limit for performance
        end
    end

    return entities
end

local function extract_enemies(surface, force, bounds)
    local enemies = {}
    local filter = {
        force = "enemy",
        type = {"unit", "unit-spawner", "turret"},
        area = bounds and {{bounds.x_min, bounds.y_min}, {bounds.x_max, bounds.y_max}} or nil,
        limit = 100,
    }

    for _, entity in pairs(surface.find_entities_filtered(filter)) do
        table.insert(enemies, {
            type = entity.name,
            position = {x = entity.position.x, y = entity.position.y},
            health = entity.health / entity.max_health,
        })
    end

    return enemies
end

local function extract_resources(surface, bounds)
    local resources = {}
    local filter = {
        type = "resource",
        area = bounds and {{bounds.x_min, bounds.y_min}, {bounds.x_max, bounds.y_max}} or nil,
    }

    for _, entity in pairs(surface.find_entities_filtered(filter)) do
        local name = entity.name
        resources[name] = (resources[name] or 0) + entity.amount
    end

    return resources
end

local function compute_reward(agent_id, agent_data)
    local reward = 0
    local components = {}

    local force = game.forces.player
    local surface = game.surfaces[1]

    -- SPM (Science Per Minute) - simplified
    local force_stats = get_force_stats(force)
    if force_stats.research and force_stats.research.progress then
        components.research_progress = force_stats.research.progress
        reward = reward + force_stats.research.progress * 0.1
    end

    -- Power satisfaction
    local power = get_power_stats(surface, force)
    components.power_satisfaction = power.satisfaction
    if power.satisfaction < 1.0 then
        reward = reward - (1.0 - power.satisfaction) * 0.5
    end

    -- Evolution factor penalty
    local evolution = game.forces.enemy.get_evolution_factor(surface)
    components.evolution_factor = -evolution
    reward = reward - evolution * 0.1

    return reward, components
end

local function extract_observation()
    local surface = game.surfaces[1]
    local force = game.forces.player

    local obs = {
        tick = game.tick,
        global = {
            evolution_factor = game.forces.enemy.get_evolution_factor(surface),
            research = get_force_stats(force).research,
            power = get_power_stats(surface, force),
            pollution = get_pollution_stats(surface),
        },
        agents = {},
        state_hash = nil,
    }

    -- Extract per-agent observations
    for agent_id, agent_data in pairs(storage.gamerl.agents) do
        local bounds = agent_data.bounds
        local reward, reward_components = compute_reward(agent_id, agent_data)

        obs.agents[agent_id] = {
            bounds = bounds,
            entities = extract_entities(surface, force, bounds),
            resources = extract_resources(surface, bounds),
            enemies = extract_enemies(surface, force, bounds),
            reward = reward,
            reward_components = reward_components,
            done = false,
        }
    end

    -- Compute state hash for determinism verification
    obs.state_hash = tostring(game.tick) .. "-" .. tostring(#surface.find_entities_filtered{force = force})

    return obs
end

local function write_observation(obs)
    local json = helpers.table_to_json(obs)
    helpers.write_file("gamerl/observation.json", json, false)
end

-- ============================================================================
-- Action Execution
-- ============================================================================

local function execute_action(agent_id, action)
    -- Handle both PascalCase (from JSON) and lowercase keys
    local action_type = action and (action.Type or action.type)
    if not action_type then
        return false, "Invalid action: missing type"
    end

    local surface = game.surfaces[1]
    local force = game.forces.player

    if action_type == "Noop" or action_type == "Wait" then
        return true, nil

    elseif action_type == "Build" then
        local entity_name = action.entity or action.Entity
        local position = action.position or action.Position
        local direction = action.direction or action.Direction or 0

        if not entity_name or not position then
            return false, "Build requires entity and position"
        end

        local can_place = surface.can_place_entity{
            name = entity_name,
            position = {position[1], position[2]},
            direction = direction,
            force = force,
        }

        if can_place then
            local entity = surface.create_entity{
                name = entity_name,
                position = {position[1], position[2]},
                direction = direction,
                force = force,
            }
            return entity ~= nil, entity and nil or "Failed to create entity"
        else
            return false, "Cannot place entity at position"
        end

    elseif action_type == "Mine" then
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position

        local entity = nil
        if entity_id then
            -- Find by unit number
            for _, e in pairs(surface.find_entities_filtered{force = force}) do
                if e.unit_number == entity_id then
                    entity = e
                    break
                end
            end
        elseif position then
            local entities = surface.find_entities_filtered{
                position = {position[1], position[2]},
                force = force,
                limit = 1,
            }
            entity = entities[1]
        end

        if entity and entity.valid then
            entity.destroy()
            return true, nil
        else
            return false, "Entity not found"
        end

    elseif action_type == "SetRecipe" then
        local entity_id = action.entity_id or action.EntityId
        local recipe_name = action.recipe or action.Recipe

        if not entity_id or not recipe_name then
            return false, "SetRecipe requires entity_id and recipe"
        end

        -- Find entity
        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = {"assembling-machine", "furnace"}}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if entity and entity.valid then
            local success = entity.set_recipe(recipe_name)
            return success ~= nil, success and nil or "Failed to set recipe"
        else
            return false, "Entity not found"
        end

    elseif action_type == "StartResearch" then
        local technology = action.technology or action.Technology
        if not technology then
            return false, "StartResearch requires technology"
        end

        local tech = force.technologies[technology]
        if tech and not tech.researched then
            force.research_queue_enabled = true
            force.add_research(technology)
            return true, nil
        else
            return false, "Technology not available or already researched"
        end

    elseif action_type == "RotateEntity" then
        local entity_id = action.entity_id or action.EntityId
        if not entity_id then
            return false, "RotateEntity requires entity_id"
        end

        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id and e.rotatable then
                e.rotate()
                return true, nil
            end
        end
        return false, "Entity not found or not rotatable"

    else
        return false, "Unknown action type: " .. tostring(action_type)
    end
end

-- ============================================================================
-- Remote Interface
-- ============================================================================

remote.add_interface("gamerl", {
    -- Initialize the mod
    init = function()
        init_storage()
        game.print("[GameRL] Initialized v" .. VERSION)
        return true
    end,

    -- Register an agent
    register_agent = function(agent_id, agent_type, config)
        init_storage()

        storage.gamerl.agents[agent_id] = {
            agent_type = agent_type,
            config = config or {},
            bounds = config and config.bounds or nil,
            registered_tick = game.tick,
        }

        game.print("[GameRL] Agent '" .. agent_id .. "' registered as " .. tostring(agent_type))
        return true
    end,

    -- Deregister an agent
    deregister_agent = function(agent_id)
        if storage.gamerl and storage.gamerl.agents then
            storage.gamerl.agents[agent_id] = nil
            game.print("[GameRL] Agent '" .. agent_id .. "' deregistered")
        end
        return true
    end,

    -- Execute a step: action + advance simulation
    step = function(agent_id, action_json, ticks)
        init_storage()

        -- Parse action if string
        local action = action_json
        if type(action_json) == "string" then
            action = helpers.json_to_table(action_json)
        end

        -- Execute action
        local success, error_msg = execute_action(agent_id, action)
        if not success and error_msg then
            game.print("[GameRL] Action failed: " .. error_msg)
        end

        -- Advance simulation by ticks
        ticks = ticks or 1
        if ticks > 0 then
            -- Use tick_paused + ticks_to_run for deterministic stepping
            game.tick_paused = true
            game.ticks_to_run = ticks
        end

        -- Extract and write observation
        local obs = extract_observation()
        write_observation(obs)

        return success
    end,

    -- Reset the environment
    reset = function(seed, scenario)
        init_storage()

        storage.gamerl.episode_seed = seed
        storage.gamerl.scenario = scenario

        -- Clear agents
        storage.gamerl.agents = {}

        -- If seed provided, we could potentially reset RNG
        -- Factorio's determinism is based on game state, not external seed
        if seed then
            game.print("[GameRL] Reset with seed: " .. tostring(seed))
        end

        -- Extract initial observation
        local obs = extract_observation()
        write_observation(obs)

        game.print("[GameRL] Environment reset")
        return true
    end,

    -- Get state hash for determinism verification
    get_state_hash = function()
        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Simple hash based on game state
        local entity_count = #surface.find_entities_filtered{force = force}
        local hash = string.format("%d-%d-%f",
            game.tick,
            entity_count,
            game.forces.enemy.get_evolution_factor(surface)
        )

        return hash
    end,

    -- Configure observation profile
    configure_streams = function(agent_id, profile)
        init_storage()
        storage.gamerl.observation_profile = profile or "normal"
        game.print("[GameRL] Observation profile set to: " .. profile)
        return true
    end,

    -- Save trajectory (placeholder)
    save_trajectory = function(path)
        game.print("[GameRL] Trajectory saving not yet implemented")
        return false
    end,

    -- Load trajectory (placeholder)
    load_trajectory = function(path)
        game.print("[GameRL] Trajectory loading not yet implemented")
        return false
    end,

    -- Shutdown
    shutdown = function()
        game.print("[GameRL] Shutting down")
        if storage.gamerl then
            storage.gamerl.agents = {}
        end
        return true
    end,

    -- Get version
    version = function()
        return VERSION
    end,
})

-- ============================================================================
-- Event Handlers
-- ============================================================================

script.on_init(function()
    init_storage()
    game.print("[GameRL] Mod initialized v" .. VERSION)
end)

script.on_load(function()
    -- Restore state from storage
end)

script.on_configuration_changed(function(data)
    init_storage()
    if data.mod_changes and data.mod_changes["gamerl"] then
        game.print("[GameRL] Configuration updated")
    end
end)

-- Periodic observation writing (configurable interval)
script.on_event(defines.events.on_tick, function(event)
    local interval = settings.global["gamerl-observation-interval"].value
    if event.tick % interval == 0 then
        if storage.gamerl and next(storage.gamerl.agents) then
            local obs = extract_observation()
            write_observation(obs)
        end
    end
end)
