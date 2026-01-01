-- GameRL/Main.lua
-- Client-side entry point and event hooks for Project Zomboid

local GameRL = {}
GameRL.initialized = false
GameRL.javaLoaded = false

-- Try to get Java bridge reference
local function getJavaBridge()
    -- The Java class should be accessible via global
    local success, result = pcall(function()
        return getClass("com.arkavo.gamerl.zomboid.GameRLMod")
    end)
    if success and result then
        return result
    end
    return nil
end

-- Initialize mod
function GameRL.init()
    if GameRL.initialized then
        return
    end

    print("[GameRL] Initializing Lua layer...")

    -- Check if Java side is available
    local javaBridge = getJavaBridge()
    if javaBridge then
        -- Call Java initialization
        javaBridge:initialize()
        GameRL.javaLoaded = true
        print("[GameRL] Java bridge initialized successfully")
    else
        print("[GameRL] WARNING: Java bridge not available - running without IPC")
    end

    GameRL.initialized = true
    print("[GameRL] Lua initialization complete")
end

-- Called every game tick
function GameRL.onTick()
    if not GameRL.javaLoaded then
        return
    end

    local javaBridge = getJavaBridge()
    if javaBridge then
        -- Pump Java message queue on game thread
        javaBridge:onTick()
    end
end

-- Called when a player is created
function GameRL.onCreatePlayer(playerIndex, player)
    print("[GameRL] Player " .. playerIndex .. " created: " .. tostring(player:getDisplayName()))
end

-- Called on game start
function GameRL.onGameStart()
    print("[GameRL] Game started")
    GameRL.init()
end

-- Called every game hour
function GameRL.onEveryHours()
    -- Could push periodic state updates here
end

-- Debug key handler (F11)
function GameRL.onKeyPressed(key)
    if key == Keyboard.KEY_F11 then
        local status = GameRL.javaLoaded and "Connected" or "Not connected"
        print("[GameRL] Debug: " .. status)

        -- Print some state info
        local player = getPlayer()
        if player then
            print("[GameRL] Player: " .. tostring(player:getDisplayName()))
            print("[GameRL] Position: " .. player:getX() .. ", " .. player:getY() .. ", " .. player:getZ())
            print("[GameRL] Health: " .. player:getHealth())
        end
    end
end

-- Register event hooks
Events.OnGameStart.Add(GameRL.onGameStart)
Events.OnTick.Add(GameRL.onTick)
Events.OnCreatePlayer.Add(GameRL.onCreatePlayer)
Events.EveryHours.Add(GameRL.onEveryHours)
Events.OnKeyPressed.Add(GameRL.onKeyPressed)

print("[GameRL] Lua module loaded")

return GameRL
