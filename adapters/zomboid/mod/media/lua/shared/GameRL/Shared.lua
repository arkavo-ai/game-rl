-- GameRL/Shared.lua
-- Shared utilities for GameRL mod

local GameRLShared = {}

-- Version info
GameRLShared.VERSION = "0.5.0"
GameRLShared.PROTOCOL_VERSION = "0.5.0"

-- Default configuration
GameRLShared.Config = {
    port = 19731,
    visionPort = 19732,
    observationRadius = 30,
    maxZombiesInObservation = 100,
    maxItemsInObservation = 50,
    ticksPerStep = 60
}

-- Load config from environment or file
function GameRLShared.loadConfig()
    -- Could load from a config file here
    local port = os.getenv("GAMERL_PORT")
    if port then
        GameRLShared.Config.port = tonumber(port) or 19731
    end
end

print("[GameRL] Shared module loaded v" .. GameRLShared.VERSION)

return GameRLShared
