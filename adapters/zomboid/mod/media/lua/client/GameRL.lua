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
        -- Fields flattened (no Result wrapper) to match Rust #[serde(flatten)]
        IPC.send({
            Type = "StepResult",
            AgentId = msg.AgentId,
            Observation = obs,
            Reward = result.success and 0.1 or -0.1,
            RewardComponents = {},
            Done = done,
            Truncated = false,
            StateHash = StateExtractor.computeStateHash()
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
