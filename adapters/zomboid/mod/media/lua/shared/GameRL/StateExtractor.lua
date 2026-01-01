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
        end
        local secondary = player:getSecondaryHandItem()
        if secondary then
            state.SecondaryWeapon = secondary:getDisplayName()
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

-- Extract weather
function StateExtractor.extractWeather()
    local weather = { Condition = "Clear", Temperature = 20.0 }

    pcall(function()
        local climateManager = getClimateManager()
        if climateManager then
            weather.Temperature = climateManager:getAirTemperatureForCharacter()
            -- Could add more weather info here
        end
    end)

    return weather
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
    local obs = {
        Tick = StateExtractor.getTick(),
        GameTime = StateExtractor.extractGameTime(),
        SurvivorCount = #survivors,
        Survivors = survivors,
        Weather = StateExtractor.extractWeather(),
        VisibleZombies = StateExtractor.extractZombies(primary, 30),
        NearbyItems = StateExtractor.extractItems(primary, 15)
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
