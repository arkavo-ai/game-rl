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

-- Find zombie by ID (e.g., "Zombie123")
local function findZombieById(targetId)
    if not targetId then return nil end

    local match = string.match(targetId, "Zombie(%d+)")
    if not match then return nil end

    local targetIndex = tonumber(match)
    if not targetIndex then return nil end

    -- Get zombies near player
    local player = getPlayer()
    if not player then return nil end

    local cell = getCell()
    if not cell then return nil end

    -- Search in nearby squares for the zombie
    local px, py, pz = player:getX(), player:getY(), player:getZ()
    local searchRadius = 30

    -- Use cell's zombie list for efficiency
    local zombieList = cell:getZombieList()
    if zombieList then
        for i = 0, zombieList:size() - 1 do
            local zombie = zombieList:get(i)
            if zombie and zombie:getID() == targetIndex then
                local dist = player:DistTo(zombie)
                if dist <= searchRadius then
                    return zombie
                end
            end
        end
    end

    return nil
end

-- Get closest zombie to player
local function getClosestZombie(player, maxDist)
    maxDist = maxDist or 2.0
    local cell = getCell()
    if not cell or not player then return nil end

    local px, py, pz = player:getX(), player:getY(), player:getZ()
    local closest = nil
    local closestDist = maxDist

    -- Search nearby squares
    for dx = -3, 3 do
        for dy = -3, 3 do
            local sq = cell:getGridSquare(px + dx, py + dy, pz)
            if sq then
                local movingObjects = sq:getMovingObjects()
                if movingObjects then
                    for i = 0, movingObjects:size() - 1 do
                        local obj = movingObjects:get(i)
                        if obj and instanceof(obj, "IsoZombie") and not obj:isDead() then
                            local dist = math.sqrt((obj:getX() - px)^2 + (obj:getY() - py)^2)
                            if dist < closestDist then
                                closestDist = dist
                                closest = obj
                            end
                        end
                    end
                end
            end
        end
    end

    return closest, closestDist
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

    -- Get target square
    local targetSquare = getCell():getGridSquare(x, y, z)
    if not targetSquare then
        return fail("Move", "INVALID_TARGET", "Target square not found at " .. x .. "," .. y .. "," .. z)
    end

    -- Clear any existing actions and queue walk-to
    ISTimedActionQueue.clear(player)
    ISTimedActionQueue.add(ISWalkToTimedAction:new(player, targetSquare))

    return ok("Move", "Walking to (" .. x .. "," .. y .. "," .. z .. ")")
end

function ActionDispatcher.Sprint(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Sprint", "INVALID_SURVIVOR", "Survivor not found")
    end
    player:setSprinting(true)
    local result = ActionDispatcher.Move(params)
    result.action = "Sprint"
    return result
end

function ActionDispatcher.Sneak(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Sneak", "INVALID_SURVIVOR", "Survivor not found")
    end
    player:setSneaking(true)
    local result = ActionDispatcher.Move(params)
    result.action = "Sneak"
    return result
end

-- Simple directional walk (N/S/E/W) - moves 5 tiles in direction
function ActionDispatcher.Walk(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Walk", "INVALID_SURVIVOR", "Survivor not found")
    end

    local dir = params.Direction or "North"
    local distance = params.Distance or 5
    local px, py, pz = player:getX(), player:getY(), player:getZ()
    local tx, ty = px, py

    if dir == "North" or dir == "N" then
        ty = py - distance
    elseif dir == "South" or dir == "S" then
        ty = py + distance
    elseif dir == "East" or dir == "E" then
        tx = px + distance
    elseif dir == "West" or dir == "W" then
        tx = px - distance
    elseif dir == "NE" or dir == "Northeast" then
        tx, ty = px + distance, py - distance
    elseif dir == "NW" or dir == "Northwest" then
        tx, ty = px - distance, py - distance
    elseif dir == "SE" or dir == "Southeast" then
        tx, ty = px + distance, py + distance
    elseif dir == "SW" or dir == "Southwest" then
        tx, ty = px - distance, py + distance
    else
        return fail("Walk", "INVALID_DIRECTION", "Unknown direction: " .. tostring(dir))
    end

    return ActionDispatcher.Move({ X = tx, Y = ty, Z = pz })
end

function ActionDispatcher.Wait(params)
    return ok("Wait", "Waiting")
end

-- === Game Control Actions ===

function ActionDispatcher.Pause(params)
    local success = pcall(function()
        local speedControls = UIManager.getSpeedControls()
        if speedControls then
            speedControls:SetCurrentGameSpeed(0)
        end
    end)
    return ok("Pause", success and "Game paused" or "Pause attempted")
end

function ActionDispatcher.Unpause(params)
    local success = pcall(function()
        local speedControls = UIManager.getSpeedControls()
        if speedControls then
            speedControls:SetCurrentGameSpeed(1)
        end
    end)
    return ok("Unpause", success and "Game unpaused" or "Unpause attempted")
end

function ActionDispatcher.SetSpeed(params)
    local speed = params.Speed or 1
    if speed < 0 then speed = 0 end
    if speed > 3 then speed = 3 end

    local success = pcall(function()
        local speedControls = UIManager.getSpeedControls()
        if speedControls then
            speedControls:SetCurrentGameSpeed(speed)
        end
    end)
    return ok("SetSpeed", "Game speed set to " .. speed)
end

-- Configure danger alert system
function ActionDispatcher.SetDangerConfig(params)
    if params.Threshold then
        GameRL.dangerThreshold = params.Threshold
    end
    if params.AutoPause ~= nil then
        GameRL.autoPauseOnDanger = params.AutoPause
    end
    if params.Cooldown then
        GameRL.dangerAlertCooldown = params.Cooldown
    end
    return ok("SetDangerConfig", "Danger threshold=" .. GameRL.dangerThreshold ..
              " autoPause=" .. tostring(GameRL.autoPauseOnDanger) ..
              " cooldown=" .. GameRL.dangerAlertCooldown .. "ms")
end

-- === Combat Actions ===

function ActionDispatcher.Attack(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Attack", "INVALID_SURVIVOR", "Survivor not found")
    end

    local targetId = params.TargetId
    local zombie = nil

    if targetId then
        -- Try to find specific zombie
        zombie = findZombieById(targetId)
    end

    -- If no specific target or not found, attack closest
    if not zombie then
        zombie = getClosestZombie(player, 2.5)
    end

    if not zombie then
        return fail("Attack", "NO_TARGET", "No zombie in melee range")
    end

    -- Face the zombie using direction
    local dx = zombie:getX() - player:getX()
    local dy = zombie:getY() - player:getY()
    local dir = IsoDirections.fromAngle(math.atan2(dy, dx))
    player:setDir(dir)

    -- Get weapon - use equipped or create bare hands weapon
    local weapon = player:getPrimaryHandItem()
    local weaponName = "bare hands"
    local damage = 0.3  -- base punch damage

    if weapon and instanceof(weapon, "HandWeapon") then
        weaponName = weapon:getDisplayName()
        damage = weapon:getMaxDamage() or 0.5
    else
        -- Create a temporary bare hands weapon for the Hit call
        weapon = player:getInventory():AddItem("Base.BareHands")
        if not weapon then
            -- Fallback: just use nil and let the game handle it
            weapon = nil
        end
    end

    -- Trigger attack animation
    player:setAttackAnim(true)

    -- Actually deal damage to the zombie using Hit method
    -- Hit(weapon, wielder, damageSplit, bIgnoreDamage, modDelta)
    local success, err = pcall(function()
        if weapon then
            zombie:Hit(weapon, player, damage, false, 1.0)
        else
            -- Direct health reduction as fallback
            local newHealth = zombie:getHealth() - damage
            zombie:setHealth(newHealth)
            if newHealth <= 0 then
                zombie:setHealth(0)
                zombie:changeState(ZombieDeadState.instance())
            end
        end
    end)

    if not success then
        -- Fallback: try direct health manipulation
        pcall(function()
            local newHealth = zombie:getHealth() - damage
            zombie:setHealth(newHealth)
        end)
    end

    local zombieHealth = zombie:getHealth()
    return ok("Attack", "Hit with " .. weaponName .. " (zombie HP: " .. string.format("%.2f", zombieHealth) .. ")")
end

-- Attack closest zombie automatically
function ActionDispatcher.AttackNearest(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("AttackNearest", "INVALID_SURVIVOR", "Survivor not found")
    end

    local zombie, dist = getClosestZombie(player, 2.5)
    if not zombie then
        return fail("AttackNearest", "NO_TARGET", "No zombie in melee range")
    end

    -- Face the zombie using direction
    local dx = zombie:getX() - player:getX()
    local dy = zombie:getY() - player:getY()
    local dir = IsoDirections.fromAngle(math.atan2(dy, dx))
    player:setDir(dir)

    -- Get weapon
    local weapon = player:getPrimaryHandItem()
    local weaponName = "bare hands"
    local damage = 0.3

    if weapon and instanceof(weapon, "HandWeapon") then
        weaponName = weapon:getDisplayName()
        damage = weapon:getMaxDamage() or 0.5
    end

    -- Trigger attack animation
    player:setAttackAnim(true)

    -- Deal damage using Hit or direct health manipulation
    local success, err = pcall(function()
        if weapon and instanceof(weapon, "HandWeapon") then
            zombie:Hit(weapon, player, damage, false, 1.0)
        else
            local newHealth = zombie:getHealth() - damage
            zombie:setHealth(newHealth)
        end
    end)

    if not success then
        pcall(function()
            local newHealth = zombie:getHealth() - damage
            zombie:setHealth(newHealth)
        end)
    end

    local zombieHealth = zombie:getHealth()
    return ok("AttackNearest", "Hit zombie at " .. string.format("%.1f", dist) .. " tiles (HP: " .. string.format("%.2f", zombieHealth) .. ")")
end

function ActionDispatcher.Shove(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Shove", "INVALID_SURVIVOR", "Survivor not found")
    end

    -- Face closest zombie before shoving
    local zombie = getClosestZombie(player, 2.0)
    if zombie then
        local dx = zombie:getX() - player:getX()
        local dy = zombie:getY() - player:getY()
        local dir = IsoDirections.fromAngle(math.atan2(dy, dx))
        player:setDir(dir)
    end

    player:setDoShove(true)
    return ok("Shove", zombie and "Shoved zombie" or "Shoved (no target)")
end

function ActionDispatcher.Equip(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Equip", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId
    local itemName = params.ItemName

    local inv = player:getInventory()
    if not inv then
        return fail("Equip", "NO_INVENTORY", "Could not access inventory")
    end

    local itemToEquip = nil

    -- Search by ID or name
    local items = inv:getItems()
    for i = 0, items:size() - 1 do
        local item = items:get(i)
        if item then
            if itemId and tostring(item:getID()) == itemId then
                itemToEquip = item
                break
            elseif itemName and item:getDisplayName():lower():find(itemName:lower()) then
                itemToEquip = item
                break
            end
        end
    end

    -- If no specific item, find first weapon
    if not itemToEquip and not itemId and not itemName then
        for i = 0, items:size() - 1 do
            local item = items:get(i)
            if item and instanceof(item, "HandWeapon") then
                itemToEquip = item
                break
            end
        end
    end

    if not itemToEquip then
        return fail("Equip", "ITEM_NOT_FOUND", "Item not found in inventory")
    end

    -- Equip the item
    if instanceof(itemToEquip, "HandWeapon") then
        player:setPrimaryHandItem(itemToEquip)
        return ok("Equip", "Equipped " .. itemToEquip:getDisplayName())
    else
        return fail("Equip", "NOT_EQUIPPABLE", itemToEquip:getDisplayName() .. " is not a weapon")
    end
end

-- Equip best available weapon
function ActionDispatcher.EquipBest(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("EquipBest", "INVALID_SURVIVOR", "Survivor not found")
    end

    local inv = player:getInventory()
    if not inv then
        return fail("EquipBest", "NO_INVENTORY", "Could not access inventory")
    end

    local bestWeapon = nil
    local bestDamage = 0

    local items = inv:getItems()
    for i = 0, items:size() - 1 do
        local item = items:get(i)
        if item and instanceof(item, "HandWeapon") then
            local damage = item:getMaxDamage() or 0
            if damage > bestDamage then
                bestDamage = damage
                bestWeapon = item
            end
        end
    end

    if not bestWeapon then
        return fail("EquipBest", "NO_WEAPONS", "No weapons in inventory")
    end

    player:setPrimaryHandItem(bestWeapon)
    return ok("EquipBest", "Equipped " .. bestWeapon:getDisplayName() .. " (damage: " .. bestDamage .. ")")
end

-- === Interaction Actions ===

function ActionDispatcher.PickUp(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("PickUp", "INVALID_SURVIVOR", "Survivor not found")
    end

    local cell = getCell()
    if not cell then
        return fail("PickUp", "NO_CELL", "Could not access game cell")
    end

    local px = math.floor(player:getX())
    local py = math.floor(player:getY())
    local pz = math.floor(player:getZ())

    -- Search current and adjacent squares for items
    for dx = -1, 1 do
        for dy = -1, 1 do
            local square = cell:getGridSquare(px + dx, py + dy, pz)
            if square then
                local objects = square:getObjects()
                if objects then
                    for i = 0, objects:size() - 1 do
                        local obj = objects:get(i)
                        if obj and instanceof(obj, "IsoWorldInventoryObject") then
                            local item = obj:getItem()
                            if item then
                                -- Check if matches requested item
                                local matches = true
                                if params.ItemId then
                                    matches = tostring(obj:hashCode()) == params.ItemId
                                elseif params.ItemName then
                                    matches = item:getDisplayName():lower():find(params.ItemName:lower())
                                end

                                if matches then
                                    -- Pick up the item
                                    player:getInventory():AddItem(item)
                                    square:transmitRemoveItemFromSquare(obj)
                                    return ok("PickUp", "Picked up " .. item:getDisplayName())
                                end
                            end
                        end
                    end
                end
            end
        end
    end

    return fail("PickUp", "NO_ITEM", "No matching item found nearby")
end

-- Pick up all nearby items
function ActionDispatcher.PickUpAll(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("PickUpAll", "INVALID_SURVIVOR", "Survivor not found")
    end

    local cell = getCell()
    if not cell then
        return fail("PickUpAll", "NO_CELL", "Could not access game cell")
    end

    local px = math.floor(player:getX())
    local py = math.floor(player:getY())
    local pz = math.floor(player:getZ())
    local count = 0

    -- Search current and adjacent squares for items
    for dx = -1, 1 do
        for dy = -1, 1 do
            local square = cell:getGridSquare(px + dx, py + dy, pz)
            if square then
                local objects = square:getObjects()
                if objects then
                    local toRemove = {}
                    for i = 0, objects:size() - 1 do
                        local obj = objects:get(i)
                        if obj and instanceof(obj, "IsoWorldInventoryObject") then
                            local item = obj:getItem()
                            if item then
                                player:getInventory():AddItem(item)
                                table.insert(toRemove, obj)
                                count = count + 1
                            end
                        end
                    end
                    for _, obj in ipairs(toRemove) do
                        square:transmitRemoveItemFromSquare(obj)
                    end
                end
            end
        end
    end

    if count > 0 then
        return ok("PickUpAll", "Picked up " .. count .. " items")
    else
        return fail("PickUpAll", "NO_ITEMS", "No items found nearby")
    end
end

function ActionDispatcher.Drop(params)
    local player = resolveSurvivor(params)
    if not player then
        return fail("Drop", "INVALID_SURVIVOR", "Survivor not found")
    end

    local itemId = params.ItemId

    local inv = player:getInventory()
    if not inv then
        return fail("Drop", "NO_INVENTORY", "Could not access inventory")
    end

    local items = inv:getItems()
    for i = 0, items:size() - 1 do
        local item = items:get(i)
        if item and tostring(item:getID()) == itemId then
            -- Drop the item
            inv:Remove(item)
            player:getCurrentSquare():AddWorldInventoryItem(item, 0, 0, 0)
            return ok("Drop", "Dropped " .. item:getDisplayName())
        end
    end

    return fail("Drop", "ITEM_NOT_FOUND", "Item not found in inventory")
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
            { name = "Pause", description = "Pause the game (for LLM thinking time)" },
            { name = "Unpause", description = "Unpause the game" },
            { name = "SetSpeed", description = "Set game speed 1-3", params = { Speed = { type = "int", min = 1, max = 3, default = 1 } } },
            { name = "SetDangerConfig", description = "Configure danger alerts", params = { Threshold = { type = "int", default = 8, description = "Distance in tiles" }, AutoPause = { type = "bool", default = true }, Cooldown = { type = "int", default = 500, description = "ms between alerts" } } },
            { name = "Walk", description = "Walk in direction", params = { Direction = { type = "string", values = {"North", "South", "East", "West", "NE", "NW", "SE", "SW"} }, Distance = { type = "int", default = 5 } } },
            { name = "Move", description = "Move to exact position", params = { X = { type = "int" }, Y = { type = "int" }, Z = { type = "int", min = 0, max = 7 } } },
            { name = "Sprint", description = "Sprint to position", params = { X = { type = "int" }, Y = { type = "int" } } },
            { name = "Sneak", description = "Sneak to position", params = { X = { type = "int" }, Y = { type = "int" } } },
            { name = "Attack", description = "Attack specific target or closest zombie", params = { TargetId = { type = "string", optional = true } } },
            { name = "AttackNearest", description = "Attack the closest zombie in melee range" },
            { name = "Shove", description = "Push closest zombie away" },
            { name = "Equip", description = "Equip weapon by ID or name", params = { ItemId = { type = "string", optional = true }, ItemName = { type = "string", optional = true } } },
            { name = "EquipBest", description = "Equip best weapon from inventory" },
            { name = "PickUp", description = "Pick up nearby item", params = { ItemId = { type = "string", optional = true }, ItemName = { type = "string", optional = true } } },
            { name = "PickUpAll", description = "Pick up all nearby items" },
            { name = "Drop", description = "Drop item from inventory", params = { ItemId = { type = "string" } } },
            { name = "UseItem", description = "Use item", params = { ItemId = { type = "string" } } },
            { name = "Eat", description = "Eat food", params = { ItemId = { type = "string" } } },
            { name = "Drink", description = "Drink beverage", params = { ItemId = { type = "string" } } }
        }
    }
end

print("[GameRL] ActionDispatcher module loaded")
return ActionDispatcher
