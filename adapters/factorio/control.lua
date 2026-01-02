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
            -- Sum all input/output counts (Factorio 2.0 API)
            pcall(function()
                for name, _ in pairs(network.input_counts) do
                    stats.production = stats.production + (network.get_input_count(name) or 0)
                end
                for name, _ in pairs(network.output_counts) do
                    stats.consumption = stats.consumption + (network.get_output_count(name) or 0)
                end
            end)
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

    -- Increment observation sequence to ensure unique tick values
    storage.gamerl.obs_seq = (storage.gamerl.obs_seq or 0) + 1

    local obs = {
        tick = game.tick * 1000 + storage.gamerl.obs_seq,  -- Unique tick = game_tick * 1000 + seq
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
            -- Factorio 2.0: use current_research directly
            pcall(function() force.research_queue_enabled = true end)
            local ok, err = pcall(function()
                if force.add_research then
                    force.add_research(technology)
                else
                    force.current_research = tech
                end
            end)
            if ok then
                return true, nil
            else
                return false, "Failed to start research: " .. tostring(err)
            end
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

    -- ========== COMBAT ACTIONS ==========

    elseif action_type == "Attack" then
        -- Attack enemy at position or nearest enemy
        local position = action.position or action.Position
        local target_type = action.target_type or action.TargetType  -- optional filter
        local damage = action.damage or action.Damage or 100
        local damage_type = action.damage_type or action.DamageType or "physical"

        local target = nil
        if position then
            -- Find enemy at position
            local enemies = surface.find_entities_filtered{
                position = {position[1], position[2]},
                force = "enemy",
                limit = 1,
            }
            target = enemies[1]
        else
            -- Find nearest enemy to spawn point
            local filter = {force = "enemy", limit = 1}
            if target_type then
                filter.type = target_type
            end
            local enemies = surface.find_entities_filtered(filter)
            if #enemies > 0 then
                target = surface.find_nearest_enemy{
                    position = {0, 0},
                    max_distance = 1000,
                    force = force,
                }
            end
        end

        if target and target.valid then
            target.damage(damage, force, damage_type)
            return true, nil
        else
            return false, "No enemy target found"
        end

    elseif action_type == "AttackArea" then
        -- Deal damage to all enemies in an area
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 5
        local damage = action.damage or action.Damage or 50
        local damage_type = action.damage_type or action.DamageType or "explosion"

        if not position then
            return false, "AttackArea requires position"
        end

        local enemies = surface.find_entities_filtered{
            position = {position[1], position[2]},
            radius = radius,
            force = "enemy",
        }

        local count = 0
        for _, enemy in pairs(enemies) do
            if enemy.valid and enemy.health then
                enemy.damage(damage, force, damage_type)
                count = count + 1
            end
        end

        return count > 0, count > 0 and nil or "No enemies in area"

    elseif action_type == "DestroyEnemy" then
        -- Instantly destroy an enemy (for testing/cheats)
        local position = action.position or action.Position

        if not position then
            return false, "DestroyEnemy requires position"
        end

        local enemies = surface.find_entities_filtered{
            position = {position[1], position[2]},
            force = "enemy",
            limit = 1,
        }

        if #enemies > 0 and enemies[1].valid then
            enemies[1].destroy()
            return true, nil
        else
            return false, "No enemy at position"
        end

    elseif action_type == "SpawnEnemy" then
        -- Spawn an enemy unit (for testing)
        local position = action.position or action.Position
        local enemy_type = action.enemy_type or action.EnemyType or "small-biter"

        if not position then
            return false, "SpawnEnemy requires position"
        end

        local entity = surface.create_entity{
            name = enemy_type,
            position = {position[1], position[2]},
            force = "enemy",
        }

        return entity ~= nil, entity and nil or "Failed to spawn enemy"

    elseif action_type == "BuildTurret" then
        -- Convenience action for building defensive turrets
        local position = action.position or action.Position
        local turret_type = action.turret_type or action.TurretType or "gun-turret"
        local direction = action.direction or action.Direction or 0

        if not position then
            return false, "BuildTurret requires position"
        end

        local can_place = surface.can_place_entity{
            name = turret_type,
            position = {position[1], position[2]},
            direction = direction,
            force = force,
        }

        if can_place then
            local entity = surface.create_entity{
                name = turret_type,
                position = {position[1], position[2]},
                direction = direction,
                force = force,
            }
            return entity ~= nil, entity and nil or "Failed to build turret"
        else
            return false, "Cannot place turret at position"
        end

    -- ========== LOGISTICS / INVENTORY ACTIONS ==========

    elseif action_type == "TransferItems" then
        -- Transfer items between two entity inventories
        local from_id = action.from_entity_id or action.FromEntityId
        local to_id = action.to_entity_id or action.ToEntityId
        local item_name = action.item or action.Item
        local count = action.count or action.Count or 1
        local from_inv_type = action.from_inventory or action.FromInventory or "output"
        local to_inv_type = action.to_inventory or action.ToInventory or "input"

        if not from_id or not to_id then
            return false, "TransferItems requires from_entity_id and to_entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Find entities
        local from_entity, to_entity = nil, nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == from_id then from_entity = e end
            if e.unit_number == to_id then to_entity = e end
            if from_entity and to_entity then break end
        end

        if not from_entity or not from_entity.valid then
            return false, "Source entity not found"
        end
        if not to_entity or not to_entity.valid then
            return false, "Destination entity not found"
        end

        -- Map inventory type strings to defines
        local inv_map = {
            input = defines.inventory.assembling_machine_input or defines.inventory.furnace_source or defines.inventory.chest,
            output = defines.inventory.assembling_machine_output or defines.inventory.furnace_result or defines.inventory.chest,
            chest = defines.inventory.chest,
            fuel = defines.inventory.fuel,
            burnt_result = defines.inventory.burnt_result,
        }

        local from_inv = from_entity.get_inventory(inv_map[from_inv_type] or defines.inventory.chest)
        local to_inv = to_entity.get_inventory(inv_map[to_inv_type] or defines.inventory.chest)

        if not from_inv then
            return false, "Source entity has no inventory"
        end
        if not to_inv then
            return false, "Destination entity has no inventory"
        end

        -- Transfer items
        local transferred = 0
        if item_name then
            -- Transfer specific item
            local available = from_inv.get_item_count(item_name)
            local to_transfer = math.min(count, available)
            if to_transfer > 0 then
                local inserted = to_inv.insert{name = item_name, count = to_transfer}
                if inserted > 0 then
                    from_inv.remove{name = item_name, count = inserted}
                    transferred = inserted
                end
            end
        else
            -- Transfer any items up to count
            local contents = from_inv.get_contents()
            for _, item in pairs(contents) do
                if transferred >= count then break end
                local to_transfer = math.min(item.count, count - transferred)
                local inserted = to_inv.insert{name = item.name, count = to_transfer}
                if inserted > 0 then
                    from_inv.remove{name = item.name, count = inserted}
                    transferred = transferred + inserted
                end
            end
        end

        return transferred > 0, transferred > 0 and nil or "No items transferred"

    elseif action_type == "InsertItems" then
        -- Insert items into an entity (spawns items, useful for testing)
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position
        local item_name = action.item or action.Item
        local count = action.count or action.Count or 1
        local inv_type = action.inventory or action.Inventory or "input"

        if not item_name then
            return false, "InsertItems requires item name"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Find entity
        local entity = nil
        if entity_id then
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

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        -- Get appropriate inventory
        local inv = nil
        if inv_type == "fuel" then
            inv = entity.get_fuel_inventory()
        elseif inv_type == "output" then
            inv = entity.get_output_inventory()
        else
            inv = entity.get_inventory(defines.inventory.chest) or
                  entity.get_inventory(defines.inventory.assembling_machine_input) or
                  entity.get_inventory(defines.inventory.furnace_source)
        end

        if not inv then
            return false, "Entity has no suitable inventory"
        end

        local inserted = inv.insert{name = item_name, count = count}
        return inserted > 0, inserted > 0 and nil or "Failed to insert items"

    elseif action_type == "DeconstructArea" then
        -- Mark an area for deconstruction
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 5

        if not position then
            return false, "DeconstructArea requires position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local area = {
            {position[1] - radius, position[2] - radius},
            {position[1] + radius, position[2] + radius}
        }

        local entities = surface.find_entities_filtered{
            area = area,
            force = force,
        }

        local count = 0
        for _, entity in pairs(entities) do
            if entity.valid and entity.to_be_deconstructed() == false then
                -- Order deconstruction
                local ok = pcall(function()
                    entity.order_deconstruction(force)
                end)
                if ok then count = count + 1 end
            end
        end

        return count > 0, count > 0 and nil or "No entities marked for deconstruction"

    elseif action_type == "CancelDeconstruct" then
        -- Cancel deconstruction orders in area
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 5

        if not position then
            return false, "CancelDeconstruct requires position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local area = {
            {position[1] - radius, position[2] - radius},
            {position[1] + radius, position[2] + radius}
        }

        local entities = surface.find_entities_filtered{
            area = area,
            force = force,
        }

        local count = 0
        for _, entity in pairs(entities) do
            if entity.valid and entity.to_be_deconstructed() then
                entity.cancel_deconstruction(force)
                count = count + 1
            end
        end

        return count > 0, count > 0 and nil or "No deconstruction orders cancelled"

    elseif action_type == "SetFilter" then
        -- Set filter on inserter or container slot
        local entity_id = action.entity_id or action.EntityId
        local slot = action.slot or action.Slot or 1
        local item_name = action.item or action.Item  -- nil to clear filter

        if not entity_id then
            return false, "SetFilter requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Find entity
        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = {"inserter", "container", "logistic-container"}}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        -- Set filter based on entity type
        if entity.type == "inserter" then
            -- Inserters have filter slots
            local ok, err = pcall(function()
                entity.set_filter(slot, item_name)
            end)
            return ok, ok and nil or ("Failed to set filter: " .. tostring(err))
        elseif entity.type == "container" or entity.type == "logistic-container" then
            -- Containers with bar setting or logistic filters
            if entity.prototype.logistic_mode then
                -- Logistic container - set request slot
                local ok, err = pcall(function()
                    if item_name then
                        entity.set_request_slot({name = item_name, count = action.count or 100}, slot)
                    else
                        entity.clear_request_slot(slot)
                    end
                end)
                return ok, ok and nil or ("Failed to set logistic filter: " .. tostring(err))
            else
                return false, "Container does not support filters"
            end
        else
            return false, "Entity type does not support filters"
        end

    elseif action_type == "SetInserterStack" then
        -- Set inserter stack size override
        local entity_id = action.entity_id or action.EntityId
        local stack_size = action.stack_size or action.StackSize

        if not entity_id then
            return false, "SetInserterStack requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "inserter"}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Inserter not found"
        end

        local ok, err = pcall(function()
            entity.inserter_stack_size_override = stack_size or 0
        end)
        return ok, ok and nil or ("Failed to set stack size: " .. tostring(err))

    elseif action_type == "ConnectWire" then
        -- Connect two entities with wire (circuit network)
        local from_id = action.from_entity_id or action.FromEntityId
        local to_id = action.to_entity_id or action.ToEntityId
        local wire_type = action.wire or action.Wire or "red"  -- "red" or "green"

        if not from_id or not to_id then
            return false, "ConnectWire requires from_entity_id and to_entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local from_entity, to_entity = nil, nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == from_id then from_entity = e end
            if e.unit_number == to_id then to_entity = e end
            if from_entity and to_entity then break end
        end

        if not from_entity or not from_entity.valid then
            return false, "Source entity not found"
        end
        if not to_entity or not to_entity.valid then
            return false, "Target entity not found"
        end

        local wire_def = wire_type == "green" and defines.wire_type.green or defines.wire_type.red

        local ok, err = pcall(function()
            from_entity.connect_neighbour{
                wire = wire_def,
                target_entity = to_entity,
            }
        end)

        return ok, ok and nil or ("Failed to connect wire: " .. tostring(err))

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

        -- Write observation immediately (ticks_to_run doesn't work reliably in headless)
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

        rcon.print(hash)
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
    if not storage.gamerl then return end

    local interval = settings.global["gamerl-observation-interval"].value
    if event.tick % interval == 0 then
        if next(storage.gamerl.agents) then
            local obs = extract_observation()
            write_observation(obs)
        end
    end
end)
