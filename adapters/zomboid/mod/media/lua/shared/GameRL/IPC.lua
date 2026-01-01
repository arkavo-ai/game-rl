-- GameRL/IPC.lua
-- File-based IPC for communicating with zomboid-server (Rust)
-- Uses file system for communication since PZ Lua doesn't have socket access

local IPC = {}
IPC.connected = false
IPC.commandFile = nil
IPC.responseFile = nil
IPC.statusFile = nil
IPC.basePath = nil

-- IPC uses flat files in ~/Zomboid/ (PZ doesn't allow subdirectories)
-- Prefix all files with "gamerl_" to namespace them

-- Ensure directory exists (best effort)
local function ensureDir(path)
    -- PZ Lua doesn't have os.execute or lfs, so we just try to use the directory
    -- The Rust side will create it
    return true
end

-- Initialize IPC paths (flat files in ~/Zomboid/Lua/)
function IPC.init()
    print("[GameRL] IPC.init() called")
    -- Flat files with gamerl_ prefix (PZ uses ~/Zomboid/Lua/)
    IPC.basePath = ""
    IPC.commandFile = "gamerl_command.json"
    IPC.responseFile = "gamerl_response.json"
    IPC.statusFile = "gamerl_status.json"
    print("[GameRL] IPC files: " .. IPC.statusFile)

    -- Create files first so PZ owns them (can read external writes)
    local files = {IPC.commandFile, IPC.responseFile, IPC.statusFile}
    for _, fname in ipairs(files) do
        local f = getFileWriter(fname, true, false)
        if f then
            f:write("")
            f:close()
            print("[GameRL] Created: " .. fname)
        end
    end

    return true
end

-- Check if zomboid-server is ready (status file exists and has "ready")
function IPC.checkServerReady()
    local file = getFileReader(IPC.statusFile, true)
    if not file then
        return false
    end

    local content = ""
    local line = file:readLine()
    while line do
        content = content .. line
        line = file:readLine()
    end
    file:close()

    if content:find('"status":"ready"') or content:find('"status": "ready"') then
        print("[GameRL] Server ready!")
        return true
    end

    return false
end

-- Connect (check if server is ready)
function IPC.connect()
    print("[GameRL] IPC.connect() called")
    if not IPC.basePath then
        IPC.init()
    end

    print("[GameRL] Checking server ready at: " .. tostring(IPC.statusFile))
    local ready = IPC.checkServerReady()
    print("[GameRL] Server ready: " .. tostring(ready))

    if ready then
        IPC.connected = true
        print("[GameRL] Connected via file IPC!")
        return true
    end

    return false
end

-- Disconnect
function IPC.disconnect()
    IPC.connected = false
    print("[GameRL] Disconnected")
end

-- Write a response to the response file
function IPC.send(message)
    if not IPC.connected then
        return false, "Not connected"
    end

    local JSON = require("GameRL/JSON")
    local data = JSON.encode(message)
    if not data then
        return false, "JSON encode failed"
    end

    -- Second param `true` = use Zomboid folder, third `false` = overwrite
    local file = getFileWriter(IPC.responseFile, true, false)
    if not file then
        print("[GameRL] Failed to open response file for writing")
        return false, "Failed to open response file"
    end

    file:write(data)
    file:close()
    print("[GameRL] Sent: " .. tostring(message.Type or "?"))

    return true
end

-- Read command from command file (returns nil if no new command)
function IPC.receive()
    if not IPC.connected then
        return nil, "Not connected"
    end

    local file = getFileReader(IPC.commandFile, true)
    if not file then
        return nil
    end

    local content = ""
    local line = file:readLine()
    while line do
        content = content .. line
        line = file:readLine()
    end
    file:close()

    if content == "" then
        return nil
    end

    -- Clear the command file after reading
    local clearFile = getFileWriter(IPC.commandFile, true, false)
    if clearFile then
        clearFile:write("")
        clearFile:close()
    end

    local JSON = require("GameRL/JSON")
    local message = JSON.decode(content)
    print("[GameRL] Received: " .. tostring(message and message.Type or "nil"))
    return message
end

-- Check if there's data available
function IPC.available()
    if not IPC.connected then
        return false
    end

    local file = getFileReader(IPC.commandFile, true)
    if not file then
        return false
    end

    local line = file:readLine()
    file:close()

    return line ~= nil and line ~= ""
end

print("[GameRL] IPC module loaded (file-based)")
return IPC
