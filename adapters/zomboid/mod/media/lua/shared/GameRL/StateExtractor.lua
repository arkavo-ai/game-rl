-- GameRL/StateExtractor.lua
-- Extract game state for observations in pure Lua

local StateExtractor = {}

-- Cache previous states for delta computation
StateExtractor.previousStates = {}

-- Extract single survivor state
function StateExtractor.extractSurvivor(player)
    if not player then return nil end

    local state = {
        Id = "Player" .. player:getPlayerNum(),
        Name = tostring(player:getDisplayName()),
        Position = {
            X = player:getX(),
            Y = player:getY(),
            Z = player:getZ()
        },
        Health = player:getHealth(),
        IsZombie = player:isZombie(),
        IsDead = player:isDead()
    }

    -- Stats (may not be available in all game states)
    pcall(function()
        local stats = player:getStats()
        if stats then
            state.Hunger = 1.0 - stats:getHunger()
            state.Thirst = 1.0 - stats:getThirst()
            state.Fatigue = 1.0 - stats:getFatigue()
            state.Stress = stats:getStress()
        end
    end)

    -- Body damage / infection
    pcall(function()
        local body = player:getBodyDamage()
        if body then
            state.Infected = body:IsInfected()
            state.Temperature = body:getTemperature()
        end
    end)

    -- Equipment
    pcall(function()
        local primary = player:getPrimaryHandItem()
        if primary then
            state.PrimaryWeapon = primary:getDisplayName()
            state.PrimaryWeaponType = primary:getType()
        end
        local secondary = player:getSecondaryHandItem()
        if secondary then
            state.SecondaryWeapon = secondary:getDisplayName()
        end
    end)

    -- Inventory summary
    pcall(function()
        local inv = player:getInventory()
        if inv then
            local inventory = {}
            local items = inv:getItems()
            for i = 0, math.min(items:size() - 1, 20) do
                local item = items:get(i)
                if item then
                    table.insert(inventory, {
                        Id = tostring(item:getID()),
                        Name = item:getDisplayName(),
                        Type = item:getType(),
                        IsWeapon = instanceof(item, "HandWeapon"),
                        IsFood = instanceof(item, "Food")
                    })
                end
            end
            state.Inventory = inventory
            state.InventoryCount = items:size()
        end
    end)

    return state
end

-- Extract nearby zombies
function StateExtractor.extractZombies(player, radius)
    local zombies = {}
    if not player then return zombies end

    radius = radius or 30

    pcall(function()
        local cell = getCell()
        if not cell then return end

        local zombieList = cell:getZombieList()
        if not zombieList then return end

        for i = 0, zombieList:size() - 1 do
            if #zombies >= 100 then break end

            local zombie = zombieList:get(i)
            if zombie then
                local dist = player:DistTo(zombie)
                if dist <= radius then
                    table.insert(zombies, {
                        Id = "Zombie" .. zombie:getID(),
                        X = zombie:getX(),
                        Y = zombie:getY(),
                        Z = zombie:getZ(),
                        Distance = dist,
                        Health = zombie:getHealth(),
                        IsCrawler = zombie:isCrawling()
                    })
                end
            end
        end
    end)

    return zombies
end

-- Extract nearby items on ground
function StateExtractor.extractItems(player, radius)
    local items = {}
    if not player then return items end

    radius = radius or 15

    pcall(function()
        local cell = getCell()
        if not cell then return end

        local px = math.floor(player:getX())
        local py = math.floor(player:getY())
        local pz = math.floor(player:getZ())

        for x = px - radius, px + radius do
            if #items >= 50 then break end
            for y = py - radius, py + radius do
                if #items >= 50 then break end
                local square = cell:getGridSquare(x, y, pz)
                if square then
                    local objects = square:getObjects()
                    if objects then
                        for i = 0, objects:size() - 1 do
                            local obj = objects:get(i)
                            -- Check if it's a world inventory object
                            if obj and obj:getType() and obj:getType() == "IsoWorldInventoryObject" then
                                local item = obj:getItem()
                                if item then
                                    table.insert(items, {
                                        Id = "Item" .. tostring(obj:hashCode()),
                                        Name = item:getDisplayName(),
                                        Type = item:getType(),
                                        X = x,
                                        Y = y,
                                        Z = pz
                                    })
                                end
                            end
                        end
                    end
                end
            end
        end
    end)

    return items
end

-- Extract game time
function StateExtractor.extractGameTime()
    local time = { Day = 0, Hour = 0 }

    pcall(function()
        local gameTime = getGameTime()
        if gameTime then
            time.Day = gameTime:getNightsSurvived()
            time.Hour = math.floor(gameTime:getTimeOfDay())
        end
    end)

    return time
end

-- Extract weather (simplified - climate API has compatibility issues)
function StateExtractor.extractWeather()
    -- Return defaults; climate API calls vary by PZ version
    return { Condition = "Clear", Temperature = 20.0 }
end

-- Get current tick
function StateExtractor.getTick()
    local tick = 0
    pcall(function()
        local world = getWorld()
        if world then
            tick = math.floor(world:getWorldAgeDays() * 24 * 3600)
        end
    end)
    return tick
end

-- Severity-ranked alerts (spec draft-02 REQ-OBS-02): the agent's attention
-- mechanism. Severity: 0 info, 1 low, 2 warning, 3 critical.
function StateExtractor.extractAlerts(player, zombies)
    local alerts = {}
    if not player then return alerts end

    pcall(function()
        local closest = nil
        for _, z in ipairs(zombies or {}) do
            if not closest or z.Distance < closest then closest = z.Distance end
        end
        if closest and closest <= 5 then
            table.insert(alerts, { Severity = 3, Label = "UnderAttack",
                Detail = string.format("%d zombies visible, closest %.1f tiles", #zombies, closest) })
        elseif closest and closest <= 15 then
            table.insert(alerts, { Severity = 2, Label = "Zombies nearby",
                Detail = string.format("%d zombies visible, closest %.1f tiles", #zombies, closest) })
        end

        local health = player:getBodyDamage() and player:getBodyDamage():getOverallBodyHealth() or 100
        if health < 35 then
            table.insert(alerts, { Severity = 3, Label = "Critically injured",
                Detail = string.format("health %.0f%%", health) })
        elseif health < 70 then
            table.insert(alerts, { Severity = 2, Label = "Injured",
                Detail = string.format("health %.0f%%", health) })
        end

        local stats = player:getStats()
        if stats then
            if stats:getHunger() > 0.6 then
                table.insert(alerts, { Severity = 2, Label = "Hungry" })
            end
            if stats:getThirst() > 0.6 then
                table.insert(alerts, { Severity = 2, Label = "Thirsty" })
            end
            if stats:getFatigue() > 0.8 then
                table.insert(alerts, { Severity = 1, Label = "Exhausted" })
            end
        end

        if player:getPrimaryHandItem() == nil then
            table.insert(alerts, { Severity = 1, Label = "Unarmed",
                Detail = "no weapon equipped - EquipBest or find one" })
        end
    end)
    return alerts
end

-- Contextually valid actions (spec draft-02 REQ-OBS-01): only advertise what
-- can actually work right now.
function StateExtractor.extractValidActions(player, zombies, items)
    local valid = {
        { Type = "Wait" },
        { Type = "Walk", Params = { Direction = { "North", "South", "East", "West", "NE", "NW", "SE", "SW" }, Distance = "int" } },
        { Type = "Move", Params = { X = "int", Y = "int" } },
        { Type = "RenderMap", Params = { Radius = "int 4-30" } },
    }
    if not player then return valid end

    pcall(function()
        local closest = nil
        for _, z in ipairs(zombies or {}) do
            if not closest or z.Distance < closest then closest = z.Distance end
        end
        if closest and closest <= 3 then
            table.insert(valid, { Type = "AttackNearest" })
            table.insert(valid, { Type = "Shove" })
        end
        if closest and closest <= 30 then
            table.insert(valid, { Type = "Attack", Params = { TargetId = "zombie id from VisibleZombies" } })
        end

        local inv = player:getInventory()
        if inv then
            local hasWeapon, hasFood = false, false
            local invItems = inv:getItems()
            for i = 0, math.min(invItems:size() - 1, 40) do
                local item = invItems:get(i)
                if instanceof(item, "HandWeapon") then hasWeapon = true end
                if item.IsFood and item:IsFood() then hasFood = true end
            end
            if hasWeapon then
                table.insert(valid, { Type = "EquipBest" })
                table.insert(valid, { Type = "Equip", Params = { ItemName = "string" } })
            end
            if hasFood then
                table.insert(valid, { Type = "Eat", Params = { ItemId = "from Inventory" } })
            end
        end

        if items and #items > 0 then
            table.insert(valid, { Type = "PickUp", Params = { ItemName = "from NearbyItems" } })
            table.insert(valid, { Type = "PickUpAll" })
        end
    end)
    return valid
end

-- Landmarks (spec draft-02 REQ-SPA-04, embodied variant): the reference point
-- is the PLAYER, not any map center. Compass landmarks are positions 20 tiles
-- out from the player, directly usable as Move targets.
function StateExtractor.extractLandmarks(player)
    local landmarks = {}
    if not player then return landmarks end

    pcall(function()
        local px = math.floor(player:getX())
        local py = math.floor(player:getY())
        table.insert(landmarks, { Id = "PlayerPosition", Kind = "Centroid", X = px, Y = py })
        -- Note: screen north = -Y in Zomboid world coords
        local dirs = {
            { "North", 0, -1 }, { "NE", 1, -1 }, { "East", 1, 0 }, { "SE", 1, 1 },
            { "South", 0, 1 }, { "SW", -1, 1 }, { "West", -1, 0 }, { "NW", -1, -1 },
        }
        for _, d in ipairs(dirs) do
            table.insert(landmarks, {
                Id = "Region_" .. (d[1] == "North" and "N" or d[1] == "South" and "S"
                    or d[1] == "East" and "E" or d[1] == "West" and "W" or d[1]),
                Kind = "Region",
                X = px + d[2] * 20,
                Y = py + d[3] * 20,
            })
        end
    end)
    return landmarks
end

-- Extract full observation
function StateExtractor.extractObservation(fullState)
    local survivors = {}

    -- Get all players
    for i = 0, 3 do
        local player = getSpecificPlayer(i)
        if player then
            table.insert(survivors, StateExtractor.extractSurvivor(player))
        end
    end

    local primary = getPlayer()
    local zombies = StateExtractor.extractZombies(primary, 30)
    local items = StateExtractor.extractItems(primary, 15)
    local obs = {
        Tick = StateExtractor.getTick(),
        GameTime = StateExtractor.extractGameTime(),
        SurvivorCount = #survivors,
        Survivors = survivors,
        Weather = StateExtractor.extractWeather(),
        VisibleZombies = zombies,
        NearbyItems = items,
        Alerts = StateExtractor.extractAlerts(primary, zombies),
        ValidActions = StateExtractor.extractValidActions(primary, zombies, items),
        Landmarks = StateExtractor.extractLandmarks(primary)
    }

    return obs
end

-- Compute simple state hash
function StateExtractor.computeStateHash()
    local hash = 0
    pcall(function()
        local player = getPlayer()
        if player then
            hash = math.floor(player:getX() * 1000 + player:getY() * 100 + player:getHealth() * 10)
        end
    end)
    return string.format("%08x", hash % 0xFFFFFFFF)
end

print("[GameRL] StateExtractor module loaded")
return StateExtractor
