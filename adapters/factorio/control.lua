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
        production = nil,
    }

    -- Current research (always return object with all required fields)
    local current = force.current_research
    stats.research = {
        current = current and current.name or nil,
        progress = current and force.research_progress or 0,
        completed = {},  -- Required by Rust - list of completed tech names
        queue = {},      -- Research queue (Factorio 2.0)
        researched_count = 0,
    }

    -- Count researched technologies and optionally build completed list
    local researched_count = 0
    local completed = {}
    for name, tech in pairs(force.technologies) do
        if tech.researched then
            researched_count = researched_count + 1
            -- Only include first 50 to avoid huge payloads
            if researched_count <= 50 then
                table.insert(completed, name)
            end
        end
    end
    stats.research.researched_count = researched_count
    stats.research.completed = completed

    -- Research queue (Factorio 2.0 API)
    pcall(function()
        if force.research_queue then
            for i, tech in ipairs(force.research_queue) do
                table.insert(stats.research.queue, tech.name)
                if i >= 5 then break end  -- Limit queue display
            end
        end
    end)

    -- Production statistics (Factorio 2.0 API changed - using safe access)
    stats.production = {
        items_produced = {},
        items_consumed = {},
        fluids_produced = {},
        api_errors = {},  -- Track missing APIs for debugging
    }

    -- Safely try to get production stats and report missing APIs
    -- Check which production stat APIs exist (each check wrapped separately)
    local apis_to_check = {
        "item_production_statistics",
        "fluid_production_statistics",
        "get_item_production_statistics",
        "get_fluid_production_statistics",
    }

    for _, api_name in ipairs(apis_to_check) do
        local exists_ok, exists = pcall(function() return force[api_name] ~= nil end)
        if not exists_ok or not exists then
            table.insert(stats.production.api_errors, "LuaForce missing: " .. api_name)
        end
    end

    -- Try to get actual production data
    local ok, err = pcall(function()
        -- Try the method-style API (Factorio 2.0)
        local get_stats_ok, item_stats = pcall(function()
            if type(force.get_item_production_statistics) == "function" then
                return force.get_item_production_statistics()
            end
            return nil
        end)

        if get_stats_ok and item_stats and item_stats.input_counts then
            for name, count in pairs(item_stats.input_counts) do
                if count > 0 then
                    stats.production.items_produced[name] = count
                end
            end
        end
    end)

    if not ok and err then
        table.insert(stats.production.api_errors, "pcall error: " .. tostring(err))
    end

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

    -- Inventory contents (for containers, machines, inserters, etc.)
    local inventory = entity.get_inventory(defines.inventory.chest)
        or entity.get_inventory(defines.inventory.furnace_result)
        or entity.get_inventory(defines.inventory.assembling_machine_output)
    if inventory and #inventory > 0 then
        state.inventory = {}
        for i = 1, #inventory do
            local stack = inventory[i]
            if stack and stack.valid_for_read then
                state.inventory[stack.name] = (state.inventory[stack.name] or 0) + stack.count
            end
        end
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

    -- Include player character(s) first
    for _, player in pairs(game.connected_players) do
        if player.character and player.character.valid then
            local char = player.character
            table.insert(entities, {
                id = char.unit_number or 0,
                type = "character",
                name = "character",
                position = {x = char.position.x, y = char.position.y},
                direction = char.direction or 0,
                health = char.health / char.max_health,
                player_name = player.name,
            })
        end
    end

    for _, entity in pairs(surface.find_entities_filtered(filter)) do
        if entity.unit_number and entity.type ~= "character" then  -- Skip characters (already added)
            table.insert(entities, get_entity_state(entity))
            if #entities >= max_entities then break end  -- Limit for performance
        end
    end

    return entities
end

local function extract_enemies(surface, force, bounds)
    local enemies = {}

    -- Determine search area: use bounds, or search around player, or around origin
    local search_area = nil
    if bounds then
        search_area = {{bounds.x_min, bounds.y_min}, {bounds.x_max, bounds.y_max}}
    else
        -- Find player character position for centered search
        local center = {0, 0}
        for _, player in pairs(game.connected_players) do
            if player.character and player.character.valid then
                center = {player.character.position.x, player.character.position.y}
                break
            end
        end
        -- Default search radius of 200 tiles around center
        local radius = 200
        search_area = {{center[1] - radius, center[2] - radius}, {center[1] + radius, center[2] + radius}}
    end

    local filter = {
        force = "enemy",
        type = {"unit", "unit-spawner", "turret"},
        area = search_area,
        limit = 100,
    }

    for _, entity in pairs(surface.find_entities_filtered(filter)) do
        table.insert(enemies, {
            id = entity.unit_number or 0,
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

-- Get player info for observations (works in headless mode too)
local function get_player_info()
    local players = {}

    -- Try all players, not just connected (handles headless/sandbox)
    for player_index, player in pairs(game.players) do
        local info = {
            index = player_index,
            name = player.name,
            connected = player.connected,
            position = nil,
            surface_index = nil,
        }

        if player.character and player.character.valid then
            info.position = {x = player.character.position.x, y = player.character.position.y}
            info.surface_index = player.character.surface.index
            info.health = player.character.health / player.character.max_health
        elseif player.position then
            -- Player exists but has no character (spectator mode or dead)
            info.position = {x = player.position.x, y = player.position.y}
        end

        table.insert(players, info)
    end

    return players
end

local function extract_observation()
    local surface = game.surfaces[1]
    local force = game.forces.player

    -- Increment observation sequence to ensure unique tick values
    storage.gamerl.obs_seq = (storage.gamerl.obs_seq or 0) + 1

    local force_stats = get_force_stats(force)
    local obs = {
        tick = game.tick * 1000 + storage.gamerl.obs_seq,  -- Unique tick = game_tick * 1000 + seq
        global = {
            evolution_factor = game.forces.enemy.get_evolution_factor(surface),
            research = force_stats.research,
            production = force_stats.production,
            power = get_power_stats(surface, force),
            pollution = get_pollution_stats(surface),
            game_speed = game.speed,
            game_tick = game.tick,
            players = get_player_info(),  -- Bug #7, #14: Add player positions for verification
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

local function write_observation(obs, agent_id)
    local json = helpers.table_to_json(obs)

    -- Bug #13: Use atomic writes to prevent race conditions
    -- Write to temp file first, then rename (Factorio doesn't support rename,
    -- so we write complete JSON atomically by ensuring single write_file call)

    -- Write to per-agent file to avoid race conditions with parallel steps
    if agent_id then
        -- Write unique temp file, then final file
        -- Factorio's write_file with append=false overwrites atomically
        local agent_file = "gamerl/observation_" .. agent_id .. ".json"
        helpers.write_file(agent_file, json, false)
    end

    -- Also write to shared file for backwards compatibility
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

        -- Validate entity prototype exists
        if not prototypes.entity[entity_name] then
            return false, "Unknown entity: " .. tostring(entity_name)
        end

        -- Try to create entity directly (works in sandbox/creative mode even if can_place returns false)
        local entity = surface.create_entity{
            name = entity_name,
            position = {position[1], position[2]},
            direction = direction,
            force = force,
        }

        if entity and entity.valid then
            return true, nil
        else
            return false, "Cannot place " .. entity_name .. " at [" .. position[1] .. "," .. position[2] .. "]"
        end

    elseif action_type == "Mine" then
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position

        -- Convert entity_id to number for safe comparison
        if entity_id then
            entity_id = tonumber(entity_id)
        end

        local entity = nil
        if entity_id then
            -- Find by unit number - search in a reasonable area around origin
            -- Search all entities in chunks that have been generated
            local found_count = 0
            for _, e in pairs(surface.find_entities_filtered{}) do
                found_count = found_count + 1
                if e.unit_number and e.unit_number == entity_id then
                    entity = e
                    break
                end
            end
        elseif position then
            local entities = surface.find_entities_filtered{
                position = {position[1], position[2]},
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
            if success ~= nil then
                return true, nil
            else
                return false, "Failed to set recipe"
            end
        else
            return false, "Entity not found"
        end

    elseif action_type == "StartResearch" then
        -- Factorio 2.0 Research System:
        -- 1. "Trigger technologies" (steam-power, electronics) complete via in-game actions, not labs
        --    They have research_trigger instead of research_unit_ingredients
        -- 2. Regular technologies require science packs in labs
        -- 3. Tech tree: steam-power + electronics -> automation-science-pack -> automation
        -- 4. Labs only accept science packs when relevant research is queued

        local technology = action.technology or action.Technology
        local force_complete = action.force_complete or action.ForceComplete
        if not technology then
            return false, "StartResearch requires technology"
        end

        local tech = force.technologies[technology]
        if not tech then
            return false, "Technology '" .. technology .. "' does not exist"
        end

        if tech.researched then
            return false, "Technology already researched"
        end

        -- Check if this is a trigger technology (Factorio 2.0)
        -- Trigger techs complete via actions (build boiler, craft circuit), not via labs
        local prototype = prototypes.technology[technology]
        local is_trigger_tech = prototype and prototype.research_trigger ~= nil
        local has_science_cost = prototype and prototype.research_unit_ingredients and #prototype.research_unit_ingredients > 0

        -- Auto-complete trigger technologies since they can't be researched via labs
        if is_trigger_tech and not has_science_cost then
            -- First check prerequisites for trigger techs too
            local missing_prereqs = {}
            for name, prereq in pairs(tech.prerequisites) do
                if not prereq.researched then
                    table.insert(missing_prereqs, name)
                end
            end
            if #missing_prereqs > 0 then
                return false, "Trigger tech '" .. technology .. "' missing prerequisites: " .. table.concat(missing_prereqs, ", ") .. " (use force_complete=true to bypass)"
            end

            -- Auto-complete the trigger tech
            local ok, err = pcall(function()
                tech.researched = true
            end)
            if ok then
                return true, "trigger_tech_completed: " .. technology .. " (normally unlocked by in-game action)"
            else
                return false, "Failed to complete trigger tech: " .. tostring(err)
            end
        end

        -- Force complete mode: directly mark as researched (for testing/debugging)
        if force_complete then
            local ok, err = pcall(function()
                tech.researched = true
            end)
            if ok then
                return true, "force_complete: " .. technology .. " researched"
            else
                return false, "force_complete failed: " .. tostring(err)
            end
        end

        -- Check prerequisites for regular techs
        local missing_prereqs = {}
        for name, prereq in pairs(tech.prerequisites) do
            if not prereq.researched then
                table.insert(missing_prereqs, name)
            end
        end
        if #missing_prereqs > 0 then
            return false, "Missing prerequisites: " .. table.concat(missing_prereqs, ", ")
        end

        -- Check if technology is enabled
        if not tech.enabled then
            return false, "Technology is disabled (enabled=false)"
        end

        -- Factorio 2.0: use research queue
        pcall(function() force.research_queue_enabled = true end)
        local ok, err = pcall(function()
            if force.add_research then
                force.add_research(technology)
            else
                force.current_research = tech
            end
        end)
        if ok then
            -- Verify it actually started
            local current = force.current_research
            if current and current.name == technology then
                return true, nil
            else
                -- Check if it was added to queue instead
                local in_queue = false
                if force.research_queue then
                    for _, queued in pairs(force.research_queue) do
                        if queued.name == technology then
                            in_queue = true
                            break
                        end
                    end
                end
                if in_queue then
                    return true, "queued (current=" .. tostring(current and current.name or "nil") .. ")"
                else
                    return true, "add_research succeeded but current_research=" .. tostring(current and current.name or "nil")
                end
            end
        else
            return false, "Failed to start research: " .. tostring(err)
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

        if count > 0 then
            return true, nil
        else
            return false, "No enemies in area"
        end

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
        -- Spawn enemy units (for testing)
        local position = action.position or action.Position
        local enemy_type = action.enemy_type or action.EnemyType or "small-biter"
        local count = action.count or action.Count or 1

        if not position then
            return false, "SpawnEnemy requires position"
        end

        -- Validate enemy type exists
        if not prototypes.entity[enemy_type] then
            return false, "Unknown enemy type: " .. tostring(enemy_type)
        end

        local spawned = 0
        for i = 1, count do
            -- Offset each enemy slightly to prevent stacking
            local offset_x = (i - 1) % 3
            local offset_y = math.floor((i - 1) / 3)
            local entity = surface.create_entity{
                name = enemy_type,
                position = {position[1] + offset_x, position[2] + offset_y},
                force = "enemy",
            }
            if entity then spawned = spawned + 1 end
        end

        if spawned == 0 then
            return false, "Failed to spawn any enemies"
        end
        return true, nil

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
            if entity ~= nil then
                return true, nil
            else
                return false, "Failed to build turret"
            end
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

        if transferred > 0 then
            return true, nil
        else
            return false, "No items transferred"
        end

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
        elseif inv_type == "lab" or inv_type == "lab_input" then
            inv = entity.get_inventory(defines.inventory.lab_input)
        else
            -- Try multiple inventory types based on entity type
            inv = entity.get_inventory(defines.inventory.chest)
            if not inv then inv = entity.get_inventory(defines.inventory.assembling_machine_input) end
            if not inv then inv = entity.get_inventory(defines.inventory.furnace_source) end
            if not inv then inv = entity.get_inventory(defines.inventory.lab_input) end
            if not inv then inv = entity.get_inventory(defines.inventory.lab_modules) end
        end

        if not inv then
            return false, "Entity has no suitable inventory for " .. entity.name .. " (type: " .. entity.type .. ")"
        end

        -- Debug: check can_insert first
        local can = inv.can_insert{name = item_name, count = count}
        if not can then
            -- Check each slot individually for labs
            local slot_info = ""
            if entity.type == "lab" then
                for i = 1, #inv do
                    local filter = inv.get_filter(i)
                    slot_info = slot_info .. string.format(" slot%d=%s", i, filter or "nil")
                end
            end
            return false, "can_insert=false for " .. item_name .. " inv_size=" .. tostring(#inv) .. slot_info
        end

        local inserted = inv.insert{name = item_name, count = count}
        if inserted > 0 then
            return true, nil
        else
            return false, "Insert returned 0 for " .. item_name .. " into " .. entity.name .. " inv_size=" .. tostring(#inv) .. " can_insert=true"
        end

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

        if count > 0 then
            return true, nil
        else
            return false, "No entities marked for deconstruction"
        end

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

        if count > 0 then
            return true, nil
        else
            return false, "No deconstruction orders cancelled"
        end

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
            if ok then
                return true, nil
            else
                return false, "Failed to set filter: " .. tostring(err)
            end
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
                if ok then
                    return true, nil
                else
                    return false, "Failed to set logistic filter: " .. tostring(err)
                end
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
        if ok then
            return true, nil
        else
            return false, "Failed to set stack size: " .. tostring(err)
        end

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

        if ok then
            return true, nil
        else
            return false, "Failed to connect wire: " .. tostring(err)
        end

    -- ========== TRAIN ACTIONS ==========

    elseif action_type == "AddTrainSchedule" then
        -- Add a station to a train's schedule
        local train_id = action.train_id or action.TrainId
        local station = action.station or action.Station
        local wait_conditions = action.wait_conditions or action.WaitConditions

        if not train_id or not station then
            return false, "AddTrainSchedule requires train_id and station"
        end

        local surface = game.surfaces[1]

        -- Find train by locomotive unit number
        local train = nil
        for _, loco in pairs(surface.find_entities_filtered{type = "locomotive"}) do
            if loco.unit_number == train_id and loco.train then
                train = loco.train
                break
            end
        end

        if not train then
            return false, "Train not found"
        end

        local schedule = train.schedule or {current = 1, records = {}}
        local new_record = {station = station}

        -- Parse wait conditions
        if wait_conditions then
            new_record.wait_conditions = {}
            for _, cond in pairs(wait_conditions) do
                table.insert(new_record.wait_conditions, {
                    type = cond.type or "time",
                    compare_type = cond.compare_type or "and",
                    ticks = cond.ticks or 1800,  -- 30 seconds default
                })
            end
        else
            -- Default: wait 30 seconds
            new_record.wait_conditions = {{type = "time", compare_type = "and", ticks = 1800}}
        end

        table.insert(schedule.records, new_record)
        train.schedule = schedule

        return true, nil

    elseif action_type == "ClearTrainSchedule" then
        -- Clear a train's schedule
        local train_id = action.train_id or action.TrainId

        if not train_id then
            return false, "ClearTrainSchedule requires train_id"
        end

        local surface = game.surfaces[1]

        local train = nil
        for _, loco in pairs(surface.find_entities_filtered{type = "locomotive"}) do
            if loco.unit_number == train_id and loco.train then
                train = loco.train
                break
            end
        end

        if not train then
            return false, "Train not found"
        end

        train.schedule = nil
        return true, nil

    elseif action_type == "SetTrainManual" then
        -- Toggle train between automatic and manual mode
        local train_id = action.train_id or action.TrainId
        local manual = action.manual or action.Manual

        if not train_id then
            return false, "SetTrainManual requires train_id"
        end
        if manual == nil then
            manual = true
        end

        local surface = game.surfaces[1]

        local train = nil
        for _, loco in pairs(surface.find_entities_filtered{type = "locomotive"}) do
            if loco.unit_number == train_id and loco.train then
                train = loco.train
                break
            end
        end

        if not train then
            return false, "Train not found"
        end

        train.manual_mode = manual
        return true, nil

    -- ========== MACHINE CONTROL ==========

    elseif action_type == "SetEntityActive" then
        -- Enable or disable a machine/entity
        local entity_id = action.entity_id or action.EntityId
        local active = action.active or action.Active

        if not entity_id then
            return false, "SetEntityActive requires entity_id"
        end
        if active == nil then
            active = true
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local ok, err = pcall(function()
            entity.active = active
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to set active: " .. tostring(err)
        end

    elseif action_type == "SetCombinatorSignal" then
        -- Set a constant combinator signal
        local entity_id = action.entity_id or action.EntityId
        local slot = action.slot or action.Slot or 1
        local signal_type = action.signal_type or action.SignalType or "item"
        local signal_name = action.signal or action.Signal
        local count = action.count or action.Count or 1

        if not entity_id then
            return false, "SetCombinatorSignal requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "constant-combinator"}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Constant combinator not found"
        end

        local ok, err = pcall(function()
            local behavior = entity.get_or_create_control_behavior()
            if signal_name then
                behavior.set_signal(slot, {
                    signal = {type = signal_type, name = signal_name},
                    count = count
                })
            else
                behavior.set_signal(slot, nil)  -- Clear slot
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to set signal: " .. tostring(err)
        end

    -- ========== BLUEPRINT ACTIONS ==========

    elseif action_type == "CreateBlueprint" then
        -- Create a blueprint from an area (returns blueprint string)
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 10

        if not position then
            return false, "CreateBlueprint requires position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local area = {
            {position[1] - radius, position[2] - radius},
            {position[1] + radius, position[2] + radius}
        }

        -- Create temporary blueprint item
        local bp_stack = game.create_inventory(1)[1]
        bp_stack.set_stack{name = "blueprint"}

        local ok, err = pcall(function()
            bp_stack.create_blueprint{
                surface = surface,
                force = force,
                area = area,
            }
        end)

        if ok and bp_stack.is_blueprint_setup() then
            local bp_string = bp_stack.export_stack()
            storage.gamerl.last_blueprint = bp_string
            return true, nil
        else
            return false, "Failed to create blueprint: " .. tostring(err or "no entities in area")
        end

    elseif action_type == "PlaceBlueprint" then
        -- Place a blueprint at a position
        local position = action.position or action.Position
        local blueprint_string = action.blueprint or action.Blueprint or storage.gamerl.last_blueprint
        local direction = action.direction or action.Direction or 0

        if not position then
            return false, "PlaceBlueprint requires position"
        end
        if not blueprint_string then
            return false, "PlaceBlueprint requires blueprint string (or use CreateBlueprint first)"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Create temporary blueprint item
        local bp_stack = game.create_inventory(1)[1]
        bp_stack.set_stack{name = "blueprint"}

        local ok, err = pcall(function()
            bp_stack.import_stack(blueprint_string)
        end)

        if not ok then
            return false, "Invalid blueprint string: " .. tostring(err)
        end

        -- Build the blueprint
        local ghosts = bp_stack.build_blueprint{
            surface = surface,
            force = force,
            position = {position[1], position[2]},
            direction = direction,
            force_build = true,
        }

        -- Revive ghosts immediately (instant build)
        local built = 0
        for _, ghost in pairs(ghosts) do
            if ghost.valid then
                local _, entity = ghost.revive()
                if entity then built = built + 1 end
            end
        end

        if built > 0 then
            return true, nil
        else
            return false, "No entities built"
        end

    -- ========== UTILITY / TESTING ACTIONS ==========

    elseif action_type == "Teleport" then
        -- Teleport player character (for testing)
        -- Bug #7: Handle headless mode and various player states
        local position = action.position or action.Position
        local player_index = action.player or action.Player or 1

        if not position then
            return false, "Teleport requires position"
        end

        local surface = game.surfaces[1]

        -- Try to find a valid player
        local player = game.players[player_index]

        -- If specific index not found, try first available player
        if not player then
            for _, p in pairs(game.players) do
                player = p
                break
            end
        end

        if not player then
            return false, "No players exist in game"
        end

        -- If player has no character, try to create one (sandbox/god mode)
        if not player.character or not player.character.valid then
            -- Try to use the player's teleport directly (works in god mode)
            local ok = pcall(function()
                player.teleport({position[1], position[2]}, surface)
            end)
            if ok then
                return true, nil
            end
            return false, "Player has no character (headless/god mode) - teleported cursor position"
        end

        local ok = player.character.teleport({position[1], position[2]}, surface)
        if ok then
            return true, nil
        else
            return false, "Failed to teleport"
        end

    elseif action_type == "UpgradeEntity" then
        -- Mark an entity for upgrade to a different type
        local entity_id = action.entity_id or action.EntityId
        local target_type = action.target or action.Target

        if not entity_id or not target_type then
            return false, "UpgradeEntity requires entity_id and target"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local ok, err = pcall(function()
            entity.order_upgrade{
                force = force,
                target = target_type,
            }
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to order upgrade: " .. tostring(err)
        end

    elseif action_type == "RepairEntity" then
        -- Instantly repair an entity to full health
        local entity_id = action.entity_id or action.EntityId
        local amount = action.amount or action.Amount  -- nil = full repair

        if not entity_id then
            return false, "RepairEntity requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        if not entity.health then
            return false, "Entity has no health"
        end

        if amount then
            entity.health = math.min(entity.max_health, entity.health + amount)
        else
            entity.health = entity.max_health
        end

        return true, nil

    elseif action_type == "DamageEntity" then
        -- Damage a player entity (for testing)
        local entity_id = action.entity_id or action.EntityId
        local damage = action.damage or action.Damage or 50

        if not entity_id then
            return false, "DamageEntity requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        if not entity.health then
            return false, "Entity has no health"
        end

        entity.damage(damage, game.forces.enemy, "physical")
        return true, nil

    elseif action_type == "ChartArea" then
        -- Reveal/chart an area of the map (exploration)
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 32

        if not position then
            return false, "ChartArea requires position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local area = {
            {position[1] - radius, position[2] - radius},
            {position[1] + radius, position[2] + radius}
        }

        force.chart(surface, area)
        return true, nil

    elseif action_type == "SetSpeed" then
        -- Set game speed (for fast-forward training)
        local speed = action.speed or action.Speed or 1.0

        game.speed = speed
        return true, nil

    elseif action_type == "SpawnResource" then
        -- Spawn resource patches (for testing scenarios)
        local position = action.position or action.Position
        local resource_type = action.resource or action.Resource or "iron-ore"
        local amount = action.amount or action.Amount or 1000000
        local radius = action.radius or action.Radius or 5

        if not position then
            return false, "SpawnResource requires position"
        end

        local surface = game.surfaces[1]
        local count = 0

        for dx = -radius, radius do
            for dy = -radius, radius do
                if dx * dx + dy * dy <= radius * radius then
                    local pos = {position[1] + dx, position[2] + dy}
                    local existing = surface.find_entity(resource_type, pos)
                    if not existing then
                        local entity = surface.create_entity{
                            name = resource_type,
                            position = pos,
                            amount = amount,
                        }
                        if entity then count = count + 1 end
                    end
                end
            end
        end

        if count > 0 then
            return true, nil
        else
            return false, "No resources spawned"
        end

    -- ========== TIER 3: LOGISTICS NETWORK ==========

    elseif action_type == "SetLogisticRequest" then
        -- Set a logistic request slot on a requester/buffer chest or character
        local entity_id = action.entity_id or action.EntityId
        local slot = action.slot or action.Slot or 1
        local item_name = action.item or action.Item
        local min_count = action.min or action.Min or 0
        local max_count = action.max or action.Max or (item_name and 100 or 0)

        if not entity_id then
            return false, "SetLogisticRequest requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local ok, err = pcall(function()
            if item_name then
                entity.set_request_slot({name = item_name, count = max_count}, slot)
            else
                entity.clear_request_slot(slot)
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to set request: " .. tostring(err)
        end

    elseif action_type == "ReadLogisticNetwork" then
        -- Read contents of a logistic network
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position

        local surface = game.surfaces[1]
        local force = game.forces.player

        local network = nil
        if entity_id then
            for _, e in pairs(surface.find_entities_filtered{force = force}) do
                if e.unit_number == entity_id then
                    network = e.logistic_network
                    break
                end
            end
        elseif position then
            network = force.find_logistic_network_by_position({position[1], position[2]}, surface)
        end

        if not network then
            return false, "No logistic network found"
        end

        -- Store network contents in observation
        local contents = {}
        for name, count in pairs(network.get_contents()) do
            contents[name] = count
        end

        storage.gamerl.last_logistic_contents = {
            items = contents,
            robots = network.all_logistic_robots,
            available_robots = network.available_logistic_robots,
            charging_robots = network.charging_robots,
        }

        return true, nil

    elseif action_type == "SetLogisticCondition" then
        -- Set logistic/circuit condition on an entity
        local entity_id = action.entity_id or action.EntityId
        local condition_type = action.condition_type or action.ConditionType or "logistic"
        local signal_name = action.signal or action.Signal
        local comparator = action.comparator or action.Comparator or ">"
        local constant = action.constant or action.Constant or 0

        if not entity_id then
            return false, "SetLogisticCondition requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local ok, err = pcall(function()
            local behavior = entity.get_or_create_control_behavior()
            if condition_type == "logistic" then
                behavior.connect_to_logistic_network = true
                if signal_name then
                    behavior.logistic_condition = {
                        condition = {
                            first_signal = {type = "item", name = signal_name},
                            comparator = comparator,
                            constant = constant,
                        }
                    }
                end
            elseif condition_type == "circuit" then
                behavior.circuit_condition = {
                    condition = {
                        first_signal = {type = signal_name and "item" or "virtual", name = signal_name or "signal-anything"},
                        comparator = comparator,
                        constant = constant,
                    }
                }
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to set condition: " .. tostring(err)
        end

    -- ========== TIER 3: CIRCUIT NETWORK ADVANCED ==========

    elseif action_type == "ReadCircuitSignals" then
        -- Read all circuit signals from an entity
        local entity_id = action.entity_id or action.EntityId
        local wire_type = action.wire or action.Wire or "red"

        if not entity_id then
            return false, "ReadCircuitSignals requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local wire_def = wire_type == "green" and defines.wire_type.green or defines.wire_type.red
        local signals = {}

        local ok, err = pcall(function()
            local network = entity.get_circuit_network(wire_def)
            if network and network.signals then
                for _, signal in pairs(network.signals) do
                    signals[signal.signal.name] = signal.count
                end
            end
        end)

        storage.gamerl.last_circuit_signals = signals
        if ok then
            return true, nil
        else
            return false, "Failed to read signals: " .. tostring(err)
        end

    elseif action_type == "ConfigureDecider" then
        -- Configure a decider combinator
        local entity_id = action.entity_id or action.EntityId
        local first_signal = action.first_signal or action.FirstSignal
        local second_signal = action.second_signal or action.SecondSignal
        local constant = action.constant or action.Constant
        local comparator = action.comparator or action.Comparator or ">"
        local output_signal = action.output_signal or action.OutputSignal
        local copy_count = action.copy_count or action.CopyCount or false

        if not entity_id then
            return false, "ConfigureDecider requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "decider-combinator"}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Decider combinator not found"
        end

        local ok, err = pcall(function()
            local behavior = entity.get_or_create_control_behavior()
            local params = behavior.parameters

            -- Set first signal
            if first_signal then
                params.first_signal = {type = "item", name = first_signal}
            end

            -- Set second signal or constant
            if second_signal then
                params.second_signal = {type = "item", name = second_signal}
                params.constant = nil
            elseif constant then
                params.constant = constant
                params.second_signal = nil
            end

            params.comparator = comparator

            -- Set output
            if output_signal then
                params.output_signal = {type = "item", name = output_signal}
            end
            params.copy_count_from_input = copy_count

            behavior.parameters = params
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to configure decider: " .. tostring(err)
        end

    elseif action_type == "ConfigureArithmetic" then
        -- Configure an arithmetic combinator
        local entity_id = action.entity_id or action.EntityId
        local first_signal = action.first_signal or action.FirstSignal
        local second_signal = action.second_signal or action.SecondSignal
        local constant = action.constant or action.Constant
        local operation = action.operation or action.Operation or "+"
        local output_signal = action.output_signal or action.OutputSignal

        if not entity_id then
            return false, "ConfigureArithmetic requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "arithmetic-combinator"}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Arithmetic combinator not found"
        end

        local ok, err = pcall(function()
            local behavior = entity.get_or_create_control_behavior()
            local params = behavior.parameters

            if first_signal then
                params.first_signal = {type = "item", name = first_signal}
            end

            if second_signal then
                params.second_signal = {type = "item", name = second_signal}
                params.second_constant = nil
            elseif constant then
                params.second_constant = constant
                params.second_signal = nil
            end

            params.operation = operation

            if output_signal then
                params.output_signal = {type = "item", name = output_signal}
            end

            behavior.parameters = params
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to configure arithmetic: " .. tostring(err)
        end

    -- ========== TIER 3: MODULE MANAGEMENT ==========

    elseif action_type == "InsertModule" then
        -- Insert a module into an entity
        local entity_id = action.entity_id or action.EntityId
        local module_name = action.module or action.Module
        local slot = action.slot or action.Slot  -- nil = first available

        if not entity_id or not module_name then
            return false, "InsertModule requires entity_id and module"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local module_inv = entity.get_module_inventory()
        if not module_inv then
            return false, "Entity does not support modules"
        end

        local ok, err = pcall(function()
            if slot then
                module_inv.set_stack({index = slot, name = module_name, count = 1})
            else
                module_inv.insert{name = module_name, count = 1}
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to insert module: " .. tostring(err)
        end

    elseif action_type == "RemoveModule" then
        -- Remove a module from an entity
        local entity_id = action.entity_id or action.EntityId
        local slot = action.slot or action.Slot or 1
        local module_name = action.module or action.Module  -- specific module to remove

        if not entity_id then
            return false, "RemoveModule requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local module_inv = entity.get_module_inventory()
        if not module_inv then
            return false, "Entity does not support modules"
        end

        local ok, err = pcall(function()
            if module_name then
                module_inv.remove{name = module_name, count = 1}
            else
                local stack = module_inv[slot]
                if stack and stack.valid_for_read then
                    stack.clear()
                end
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to remove module: " .. tostring(err)
        end

    elseif action_type == "GetModules" then
        -- Get list of modules in an entity
        local entity_id = action.entity_id or action.EntityId

        if not entity_id then
            return false, "GetModules requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local module_inv = entity.get_module_inventory()
        if not module_inv then
            return false, "Entity does not support modules"
        end

        local modules = {}
        for i = 1, #module_inv do
            local stack = module_inv[i]
            if stack and stack.valid_for_read then
                table.insert(modules, {slot = i, name = stack.name, count = stack.count})
            end
        end

        storage.gamerl.last_modules = modules
        return true, nil

    -- ========== TIER 3: VEHICLE CONTROL ==========

    elseif action_type == "EnterVehicle" then
        -- Player enters a vehicle
        local vehicle_id = action.vehicle_id or action.VehicleId
        local player_index = action.player or action.Player or 1

        if not vehicle_id then
            return false, "EnterVehicle requires vehicle_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player
        local player = game.players[player_index]

        if not player or not player.character then
            return false, "Player not found"
        end

        local vehicle = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = {"car", "tank", "locomotive", "cargo-wagon", "spider-vehicle"}}) do
            if e.unit_number == vehicle_id then
                vehicle = e
                break
            end
        end

        if not vehicle or not vehicle.valid then
            return false, "Vehicle not found"
        end

        local ok = pcall(function()
            vehicle.set_driver(player.character)
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to enter vehicle"
        end

    elseif action_type == "ExitVehicle" then
        -- Player exits current vehicle
        local player_index = action.player or action.Player or 1

        local player = game.players[player_index]
        if not player or not player.character then
            return false, "Player not found"
        end

        if not player.vehicle then
            return false, "Player not in a vehicle"
        end

        local ok = pcall(function()
            player.vehicle.set_driver(nil)
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to exit vehicle"
        end

    elseif action_type == "SetSpidertronWaypoint" then
        -- Add waypoint to spidertron
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position
        local add_to_queue = action.add_to_queue or action.AddToQueue or true

        if not entity_id or not position then
            return false, "SetSpidertronWaypoint requires entity_id and position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local spider = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "spider-vehicle"}) do
            if e.unit_number == entity_id then
                spider = e
                break
            end
        end

        if not spider or not spider.valid then
            return false, "Spidertron not found"
        end

        local ok, err = pcall(function()
            if add_to_queue then
                spider.add_autopilot_destination({position[1], position[2]})
            else
                -- Clear existing and set single destination
                while spider.autopilot_destination do
                    spider.autopilot_destination = nil
                end
                spider.autopilot_destination = {position[1], position[2]}
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to set waypoint: " .. tostring(err)
        end

    elseif action_type == "ClearSpidertronWaypoints" then
        -- Clear all spidertron waypoints
        local entity_id = action.entity_id or action.EntityId

        if not entity_id then
            return false, "ClearSpidertronWaypoints requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local spider = nil
        for _, e in pairs(surface.find_entities_filtered{force = force, type = "spider-vehicle"}) do
            if e.unit_number == entity_id then
                spider = e
                break
            end
        end

        if not spider or not spider.valid then
            return false, "Spidertron not found"
        end

        local ok = pcall(function()
            while spider.autopilot_destination do
                spider.autopilot_destination = nil
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to clear waypoints"
        end

    -- ========== TIER 3: COPY/PASTE SETTINGS ==========

    elseif action_type == "CopySettings" then
        -- Copy entity settings to storage
        local entity_id = action.entity_id or action.EntityId
        local slot_name = action.slot or action.Slot or "default"

        if not entity_id then
            return false, "CopySettings requires entity_id"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        storage.gamerl.copied_settings = storage.gamerl.copied_settings or {}

        local settings = {
            entity_type = entity.type,
            entity_name = entity.name,
        }

        local ok = pcall(function()
            -- Copy recipe
            if entity.get_recipe then
                local recipe = entity.get_recipe()
                settings.recipe = recipe and recipe.name
            end

            -- Copy control behavior
            if entity.get_control_behavior then
                local behavior = entity.get_control_behavior()
                if behavior then
                    settings.control_behavior = {}
                    if behavior.parameters then
                        settings.control_behavior.parameters = behavior.parameters
                    end
                end
            end

            -- Copy inserter settings
            if entity.type == "inserter" then
                settings.inserter = {
                    filter_mode = entity.inserter_filter_mode,
                    stack_size_override = entity.inserter_stack_size_override,
                }
            end

            -- Copy bar setting for containers
            if entity.get_inventory and entity.get_inventory(defines.inventory.chest) then
                local inv = entity.get_inventory(defines.inventory.chest)
                if inv.supports_bar then
                    settings.bar = inv.get_bar()
                end
            end
        end)

        storage.gamerl.copied_settings[slot_name] = settings
        if ok then
            return true, nil
        else
            return false, "Failed to copy settings"
        end

    elseif action_type == "PasteSettings" then
        -- Paste settings from storage to entity
        local entity_id = action.entity_id or action.EntityId
        local slot_name = action.slot or action.Slot or "default"

        if not entity_id then
            return false, "PasteSettings requires entity_id"
        end

        storage.gamerl.copied_settings = storage.gamerl.copied_settings or {}
        local settings = storage.gamerl.copied_settings[slot_name]

        if not settings then
            return false, "No settings copied to slot: " .. slot_name
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        local entity = nil
        for _, e in pairs(surface.find_entities_filtered{force = force}) do
            if e.unit_number == entity_id then
                entity = e
                break
            end
        end

        if not entity or not entity.valid then
            return false, "Entity not found"
        end

        local ok, err = pcall(function()
            -- Paste recipe if compatible
            if settings.recipe and entity.set_recipe then
                entity.set_recipe(settings.recipe)
            end

            -- Paste control behavior
            if settings.control_behavior and entity.get_or_create_control_behavior then
                local behavior = entity.get_or_create_control_behavior()
                if settings.control_behavior.parameters and behavior.parameters ~= nil then
                    behavior.parameters = settings.control_behavior.parameters
                end
            end

            -- Paste inserter settings
            if settings.inserter and entity.type == "inserter" then
                if settings.inserter.stack_size_override then
                    entity.inserter_stack_size_override = settings.inserter.stack_size_override
                end
            end

            -- Paste bar setting
            if settings.bar and entity.get_inventory then
                local inv = entity.get_inventory(defines.inventory.chest)
                if inv and inv.supports_bar then
                    inv.set_bar(settings.bar)
                end
            end
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to paste settings: " .. tostring(err)
        end

    -- ========== TIER 3: ARTILLERY ==========

    elseif action_type == "FireArtillery" then
        -- Fire artillery at a position
        local entity_id = action.entity_id or action.EntityId
        local position = action.position or action.Position

        if not position then
            return false, "FireArtillery requires position"
        end

        local surface = game.surfaces[1]
        local force = game.forces.player

        -- Find artillery
        local artillery = nil
        if entity_id then
            for _, e in pairs(surface.find_entities_filtered{force = force, type = {"artillery-turret", "artillery-wagon"}}) do
                if e.unit_number == entity_id then
                    artillery = e
                    break
                end
            end
        else
            local arty = surface.find_entities_filtered{force = force, type = {"artillery-turret", "artillery-wagon"}, limit = 1}
            artillery = arty[1]
        end

        if not artillery or not artillery.valid then
            return false, "Artillery not found"
        end

        local ok, err = pcall(function()
            surface.create_entity{
                name = "artillery-projectile",
                position = artillery.position,
                target = {position[1], position[2]},
                speed = 1,
                force = force,
            }
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to fire artillery: " .. tostring(err)
        end

    -- ========== TIER 3: ROCKET LAUNCH ==========

    elseif action_type == "LaunchRocket" then
        -- Launch rocket from a rocket silo
        local entity_id = action.entity_id or action.EntityId

        local surface = game.surfaces[1]
        local force = game.forces.player

        local silo = nil
        if entity_id then
            for _, e in pairs(surface.find_entities_filtered{force = force, type = "rocket-silo"}) do
                if e.unit_number == entity_id then
                    silo = e
                    break
                end
            end
        else
            local silos = surface.find_entities_filtered{force = force, type = "rocket-silo", limit = 1}
            silo = silos[1]
        end

        if not silo or not silo.valid then
            return false, "Rocket silo not found"
        end

        if not silo.rocket_silo_status then
            return false, "Silo has no rocket ready"
        end

        -- Check if rocket is ready
        if silo.rocket_silo_status ~= defines.rocket_silo_status.rocket_ready then
            return false, "Rocket not ready (status: " .. tostring(silo.rocket_silo_status) .. ")"
        end

        local ok = pcall(function()
            silo.launch_rocket()
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to launch rocket"
        end

    -- ========== TIER 3: LANDFILL / TERRAIN ==========

    elseif action_type == "PlaceLandfill" then
        -- Place landfill on water
        local position = action.position or action.Position
        local tile_name = action.tile or action.Tile or "landfill"

        if not position then
            return false, "PlaceLandfill requires position"
        end

        local surface = game.surfaces[1]

        local ok, err = pcall(function()
            surface.set_tiles({{name = tile_name, position = {position[1], position[2]}}})
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to place landfill: " .. tostring(err)
        end

    elseif action_type == "PlaceTiles" then
        -- Place multiple tiles in an area
        local position = action.position or action.Position
        local radius = action.radius or action.Radius or 1
        local tile_name = action.tile or action.Tile or "concrete"

        if not position then
            return false, "PlaceTiles requires position"
        end

        local surface = game.surfaces[1]
        local tiles = {}

        for dx = -radius, radius do
            for dy = -radius, radius do
                table.insert(tiles, {name = tile_name, position = {position[1] + dx, position[2] + dy}})
            end
        end

        local ok, err = pcall(function()
            surface.set_tiles(tiles)
        end)

        if ok then
            return true, nil
        else
            return false, "Failed to place tiles: " .. tostring(err)
        end

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

        -- Include action result in observation for feedback
        obs.action_result = {
            success = success,
            ["error"] = error_msg,
            action_type = action and (action.Type or action.type) or nil
        }

        -- Write per-agent observation to avoid race conditions
        write_observation(obs, agent_id)

        return success
    end,

    -- Reset the environment (agents persist across resets)
    reset = function(seed, scenario)
        init_storage()

        storage.gamerl.episode_seed = seed
        storage.gamerl.scenario = scenario

        -- Note: agents are NOT cleared on reset - they persist across episodes
        -- Only episode-related state is reset

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
