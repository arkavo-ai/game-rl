-- GameRL/Shared.lua
-- Shared configuration and utilities for GameRL mod

local GameRLShared = {}

-- Version info
GameRLShared.VERSION = "0.5.0"
GameRLShared.PROTOCOL_VERSION = "0.5.0"

-- Default configuration
GameRLShared.Config = {
    host = "127.0.0.1",
    port = 19731,
    visionPort = 19732,
    observationRadius = 30,
    maxZombiesInObservation = 100,
    maxItemsInObservation = 50,
    ticksPerStep = 60,
    reconnectDelay = 2000, -- ms between reconnect attempts
    maxReconnectAttempts = 30
}

-- Game capabilities reported to zomboid-server
GameRLShared.Capabilities = {
    multi_agent = true,
    max_agents = 4,
    deterministic = false,
    headless = false
}

-- Load config from environment if available
function GameRLShared.loadConfig()
    -- Environment variables may not be accessible in PZ
    -- Could load from a config file in the future
end

print("[GameRL] Shared module loaded v" .. GameRLShared.VERSION)

return GameRLShared
