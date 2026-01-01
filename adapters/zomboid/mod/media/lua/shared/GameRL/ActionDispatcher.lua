-- GameRL/ActionDispatcher.lua
-- Execute actions received from the RL agent

local ActionDispatcher = {}

-- Action result helper
local function ok(actionType, message)
    return { success = true, action = actionType, message = message or "OK" }
end

local function fail(actionType, code, message)
    return { success = false, action = actionType, errorCode = code, message = message }
end

-- Resolve survivor from params
local function resolveSurvivor(params)
    local survivorId = params and params.SurvivorId
    if survivorId then
        local match = string.match(survivorId, "Player(%d+)")
        if match then
            local idx = tonumber(match)
            if idx and idx >= 0 and idx <= 3 then
                return getSpecificPlayer(idx)
            end
        end
    end
    return getPlayer()
end

-- === Movement Actions ===

function ActionDispatcher.Move(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Move", "INVALID_SURVIVOR", "Survivor not found")
    end

    local x = params.X or player:getX()
    local y = params.Y or player:getY()
    local z = params.Z or player:getZ()

    -- Calculate direction to target
    local dx = x - player:getX()
    local dy = y - player:getY()
    local dist = math.sqrt(dx * dx + dy * dy)

    if dist > 0.1 then
        -- Normalize and set movement direction
        local moveDir = player:getPlayerMoveDir()
        moveDir:set(dx / dist, dy / dist)
        player:setPlayerMoveDir(moveDir)
        return ok("Move", "Moving toward (" .. x .. "," .. y .. "," .. z .. ")")
    else
        return ok("Move", "Already at target location")
    end
end

function ActionDispatcher.Sprint(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Sprint", "INVALID_SURVIVOR", "Survivor not found")
    end
    player:setSprinting(true)
    return ActionDispatcher.Move(params)
end

function ActionDispatcher.Sneak(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Sneak", "INVALID_SURVIVOR", "Survivor not found")
    end
    player:setSneaking(true)
    return ActionDispatcher.Move(params)
end

function ActionDispatcher.Wait(params)
    return ok("Wait", "Waiting")
end

-- === Combat Actions ===

function ActionDispatcher.Attack(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Attack", "INVALID_SURVIVOR", "Survivor not found")
    end

    local targetId = params.TargetId
    if not targetId then
        return fail("Attack", "NO_TARGET", "TargetId required")
    end

    -- TODO: Resolve target and initiate attack
    -- For now, trigger a basic attack
    player:setAttackAnim(true)
    return ok("Attack", "Attacking " .. targetId)
end

function ActionDispatcher.Shove(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Shove", "INVALID_SURVIVOR", "Survivor not found")
    end
    player:setDoShove(true)
    return ok("Shove", "Shoving")
end

function ActionDispatcher.Equip(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Equip", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId
    if not itemId then
        return fail("Equip", "NO_ITEM", "ItemId required")
    end

    -- TODO: Find item in inventory and equip
    return ok("Equip", "Equipping " .. itemId)
end

-- === Interaction Actions ===

function ActionDispatcher.PickUp(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("PickUp", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId
    if not itemId then
        return fail("PickUp", "NO_ITEM", "ItemId required")
    end

    -- TODO: Find world item and pick up
    return ok("PickUp", "Picking up " .. itemId)
end

function ActionDispatcher.Drop(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Drop", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId
    if not itemId then
        return fail("Drop", "NO_ITEM", "ItemId required")
    end

    -- TODO: Find item in inventory and drop
    return ok("Drop", "Dropping " .. itemId)
end

function ActionDispatcher.UseItem(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("UseItem", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId
    if not itemId then
        return fail("UseItem", "NO_ITEM", "ItemId required")
    end

    -- TODO: Find item and use
    return ok("UseItem", "Using " .. itemId)
end

-- === Survival Actions ===

function ActionDispatcher.Eat(params)
    return ActionDispatcher.UseItem(params)
end

function ActionDispatcher.Drink(params)
    return ActionDispatcher.UseItem(params)
end

-- === Dispatch ===

function ActionDispatcher.dispatch(actionType, params)
    local handler = ActionDispatcher[actionType]
    if not handler then
        return fail(actionType, "UNKNOWN_ACTION", "Unknown action: " .. tostring(actionType))
    end

    local success, result = pcall(handler, params or {})
    if not success then
        return fail(actionType, "INTERNAL_ERROR", tostring(result))
    end

    return result
end

-- Get action space definition
function ActionDispatcher.getActionSpace()
    return {
        type = "discrete_parameterized",
        actions = {
            { name = "Wait", description = "Do nothing, advance simulation" },
            { name = "Move", description = "Move to target position", params = { X = { type = "int" }, Y = { type = "int" }, Z = { type = "int", min = 0, max = 7 } } },
            { name = "Sprint", description = "Sprint to position", params = { X = { type = "int" }, Y = { type = "int" } } },
            { name = "Sneak", description = "Sneak to position", params = { X = { type = "int" }, Y = { type = "int" } } },
            { name = "Attack", description = "Attack target", params = { TargetId = { type = "string" } } },
            { name = "Shove", description = "Push zombies away" },
            { name = "Equip", description = "Equip weapon", params = { ItemId = { type = "string" } } },
            { name = "PickUp", description = "Pick up item", params = { ItemId = { type = "string" } } },
            { name = "Drop", description = "Drop item", params = { ItemId = { type = "string" } } },
            { name = "UseItem", description = "Use item", params = { ItemId = { type = "string" } } },
            { name = "Eat", description = "Eat food", params = { ItemId = { type = "string" } } },
            { name = "Drink", description = "Drink beverage", params = { ItemId = { type = "string" } } }
        }
    }
end

print("[GameRL] ActionDispatcher module loaded")
return ActionDispatcher
