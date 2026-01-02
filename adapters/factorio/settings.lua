-- GameRL Mod Settings
-- These appear in: Main Menu → Settings → Mod Settings → Map

data:extend({
    -- RCON password hint (for documentation - actual RCON password is set in Factorio's config)
    {
        type = "string-setting",
        name = "gamerl-rcon-password",
        setting_type = "runtime-global",
        default_value = "gamerl",
        order = "a"
    },
    -- RCON port hint
    {
        type = "int-setting",
        name = "gamerl-rcon-port",
        setting_type = "runtime-global",
        default_value = 27015,
        minimum_value = 1024,
        maximum_value = 65535,
        order = "b"
    },
    -- Observation update interval (ticks)
    {
        type = "int-setting",
        name = "gamerl-observation-interval",
        setting_type = "runtime-global",
        default_value = 60,
        minimum_value = 1,
        maximum_value = 3600,
        order = "c"
    },
    -- Max entities per observation (performance)
    {
        type = "int-setting",
        name = "gamerl-max-entities",
        setting_type = "runtime-global",
        default_value = 1000,
        minimum_value = 100,
        maximum_value = 10000,
        order = "d"
    }
})
