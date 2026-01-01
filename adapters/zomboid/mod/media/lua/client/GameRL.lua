-- GameRL.lua (top-level client entry point)
-- Event registration happens HERE, at load time, in flat file
-- All logic modules stay in shared/GameRL/

print("[GameRL] Loading client entry point...")

-- State (global for simplicity)
GameRL = GameRL or {}
GameRL.initialized = false
GameRL.connected = false
GameRL.tickCount = 0
GameRL.updateCount = 0
GameRL.dangerThreshold = 8  -- tiles - send alert when zombies this close
GameRL.lastDangerAlert = 0  -- timestamp of last alert
GameRL.dangerAlertCooldown = 500  -- ms between alerts
GameRL.autoPauseOnDanger = true  -- auto-pause when in danger
GameRL.pendingDangerAlert = nil  -- buffered alert for next response

-- Lazy-loaded modules
local IPC, StateExtractor, ActionDispatcher, Shared, JSON

local function loadModules()
    if IPC then return true end
    local ok, err = pcall(function()
        Shared = require("GameRL/Shared")
        JSON = require("GameRL/JSON")
        IPC = require("GameRL/IPC")
        StateExtractor = require("GameRL/StateExtractor")
        ActionDispatcher = require("GameRL/ActionDispatcher")
    end)
    if not ok then
        print("[GameRL] Module load failed: " .. tostring(err))
        return false
    end
    return true
end

local function sendReady()
    if not IPC then return false end
    -- Use PascalCase to match game-bridge protocol
    return IPC.send({
        Type = "Ready",
        Name = "Project Zomboid",
        Version = Shared.VERSION,
        Capabilities = {
            MultiAgent = true,
            MaxAgents = 4,
            Deterministic = false,
            Headless = false
        }
    })
end

local function handleMessage(msg)
    if not msg or not msg.Type then return end
    print("[GameRL] Received: " .. msg.Type)

    if msg.Type == "RegisterAgent" then
        print("[GameRL] Agent registered: " .. msg.AgentId)
        IPC.send({
            Type = "AgentRegistered",
            AgentId = msg.AgentId,
            ObservationSpace = {},
            ActionSpace = ActionDispatcher.getActionSpace()
        })

    elseif msg.Type == "ExecuteAction" then
        local action = msg.Action or {}
        -- Action params are at top level (Type, Direction, X, Y, etc), not nested in Params
        local result = ActionDispatcher.dispatch(action.Type or "Wait", action)
        local obs = StateExtractor.extractObservation(false)
        local player = getPlayer()
        local done = player and player:isDead() or false

        -- Include danger alert if pending
        local dangerAlert = GameRL.pendingDangerAlert
        GameRL.pendingDangerAlert = nil  -- consume it

        -- Fields flattened (no Result wrapper) to match Rust #[serde(flatten)]
        IPC.send({
            Type = "StepResult",
            AgentId = msg.AgentId,
            Observation = obs,
            Reward = result.success and 0.1 or -0.1,
            RewardComponents = {},
            Done = done,
            Truncated = false,
            StateHash = StateExtractor.computeStateHash(),
            DangerAlert = dangerAlert  -- nil if no danger, object if danger detected
        })

    elseif msg.Type == "GetStateHash" then
        IPC.send({ Type = "StateHash", Hash = StateExtractor.computeStateHash() })

    elseif msg.Type == "Reset" then
        IPC.send({
            Type = "ResetComplete",
            Observation = StateExtractor.extractObservation(true),
            StateHash = StateExtractor.computeStateHash()
        })

    elseif msg.Type == "Shutdown" then
        print("[GameRL] Shutdown")
        IPC.disconnect()
        GameRL.connected = false
    end
end

local function tryConnect()
    if not loadModules() then return false end
    if IPC.connect() then
        GameRL.connected = true
        sendReady()
        print("[GameRL] Connected!")
        return true
    end
    return false
end

-- Check for danger and send alert
local function checkDanger()
    if not GameRL.connected or not IPC then return end

    local player = getPlayer()
    if not player or player:isDead() then return end

    local now = getTimestampMs()
    if now - GameRL.lastDangerAlert < GameRL.dangerAlertCooldown then return end

    -- Find closest zombie
    local cell = getCell()
    if not cell then return end

    local px, py, pz = player:getX(), player:getY(), player:getZ()
    local closestDist = GameRL.dangerThreshold + 1
    local closestZombie = nil
    local dangerousZombies = {}

    local zombieList = cell:getZombieList()
    if zombieList then
        for i = 0, zombieList:size() - 1 do
            local zombie = zombieList:get(i)
            if zombie and not zombie:isDead() then
                local dist = player:DistTo(zombie)
                if dist <= GameRL.dangerThreshold then
                    table.insert(dangerousZombies, {
                        Id = "Zombie" .. zombie:getID(),
                        Distance = dist,
                        X = zombie:getX(),
                        Y = zombie:getY()
                    })
                    if dist < closestDist then
                        closestDist = dist
                        closestZombie = zombie
                    end
                end
            end
        end
    end

    -- Send danger alert if threats detected
    if #dangerousZombies > 0 then
        GameRL.lastDangerAlert = now

        -- Auto-pause if enabled
        if GameRL.autoPauseOnDanger then
            pcall(function()
                local speedControls = UIManager.getSpeedControls()
                if speedControls then
                    speedControls:SetCurrentGameSpeed(0)
                end
            end)
        end

        -- Buffer alert for next response
        GameRL.pendingDangerAlert = {
            Type = "DangerAlert",
            ThreatCount = #dangerousZombies,
            ClosestDistance = closestDist,
            Threats = dangerousZombies,
            PlayerPosition = { X = px, Y = py, Z = pz },
            AutoPaused = GameRL.autoPauseOnDanger
        }

        print("[GameRL] DANGER! " .. #dangerousZombies .. " zombies within " .. GameRL.dangerThreshold .. " tiles!")
    end
end

-- EVENT HANDLERS (direct, no indirection)

print("[GameRL] Registering OnGameStart...")
Events.OnGameStart.Add(function()
    print("[GameRL] >>> OnGameStart fired!")
    if loadModules() then
        GameRL.initialized = true
        tryConnect()
    end
end)

print("[GameRL] Registering OnTick...")
Events.OnTick.Add(function()
    GameRL.tickCount = GameRL.tickCount + 1
    if GameRL.tickCount == 1 then
        print("[GameRL] >>> First OnTick!")
    end
    if GameRL.tickCount % 600 == 0 then
        print("[GameRL] Tick " .. GameRL.tickCount .. " connected=" .. tostring(GameRL.connected))
    end

    -- Auto-init if missed OnGameStart
    if not GameRL.initialized then
        local player = getPlayer()
        if player then
            print("[GameRL] Late init (player exists)")
            if loadModules() then
                GameRL.initialized = true
                tryConnect()
            end
        end
        return
    end

    -- Retry connection every 60 ticks (~1 second) if not connected
    if not GameRL.connected then
        if GameRL.tickCount % 60 == 0 then
            tryConnect()
        end
        return
    end

    -- Process IPC
    if IPC and IPC.available() then
        local msg = IPC.receive()
        if msg then handleMessage(msg) end
    end

    -- Check for danger every 10 ticks (~6 times per second)
    if GameRL.tickCount % 10 == 0 then
        checkDanger()
    end
end)

print("[GameRL] Registering OnPlayerUpdate...")
Events.OnPlayerUpdate.Add(function(player)
    GameRL.updateCount = GameRL.updateCount + 1
    if GameRL.updateCount == 1 then
        print("[GameRL] >>> First OnPlayerUpdate!")
    end
    if GameRL.updateCount % 300 == 0 then
        print("[GameRL] Update #" .. GameRL.updateCount)
    end
end)

-- Process IPC even when paused (OnPreUIDraw fires regardless of pause state)
print("[GameRL] Registering OnPreUIDraw for pause-safe IPC...")
GameRL.lastIPCCheck = 0
Events.OnPreUIDraw.Add(function()
    if not GameRL.connected or not IPC then return end

    -- Throttle to every 100ms to avoid excessive polling
    local now = getTimestampMs()
    if now - GameRL.lastIPCCheck < 100 then return end
    GameRL.lastIPCCheck = now

    -- Always check for IPC (handles paused state)
    if IPC.available() then
        local msg = IPC.receive()
        if msg then handleMessage(msg) end
    end
end)

print("[GameRL] Registering OnKeyPressed...")
Events.OnKeyPressed.Add(function(key)
    if key == Keyboard.KEY_F9 then
        print("[GameRL] F9 pressed - connected=" .. tostring(GameRL.connected))
        local player = getPlayer()
        if player then
            print("[GameRL] Player: " .. player:getX() .. "," .. player:getY())
        end
        if not GameRL.connected then
            tryConnect()
        end
    end
end)

print("[GameRL] Client entry point loaded - events registered")
