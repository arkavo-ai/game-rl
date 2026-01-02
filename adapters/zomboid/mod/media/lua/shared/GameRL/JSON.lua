-- GameRL/JSON.lua
-- Minimal JSON encoder/decoder for Lua 5.1 (Kahlua compatible)

local JSON = {}

-- Encode a Lua value to JSON string
function JSON.encode(value)
    local t = type(value)

    if value == nil then
        return "null"
    elseif t == "boolean" then
        return value and "true" or "false"
    elseif t == "number" then
        if value ~= value then -- NaN
            return "null"
        elseif value == math.huge or value == -math.huge then
            return "null"
        else
            return tostring(value)
        end
    elseif t == "string" then
        return JSON.encodeString(value)
    elseif t == "table" then
        return JSON.encodeTable(value)
    else
        return "null"
    end
end

-- Encode a string with proper escaping
function JSON.encodeString(s)
    local escaped = s:gsub('[%z\1-\31\\"]', function(c)
        local byte = string.byte(c)
        if c == '"' then return '\\"'
        elseif c == '\\' then return '\\\\'
        elseif c == '\b' then return '\\b'
        elseif c == '\f' then return '\\f'
        elseif c == '\n' then return '\\n'
        elseif c == '\r' then return '\\r'
        elseif c == '\t' then return '\\t'
        else return string.format('\\u%04x', byte)
        end
    end)
    return '"' .. escaped .. '"'
end

-- Check if table is an array (empty table = object, not array)
local function isArray(t)
    local i = 0
    for _ in pairs(t) do
        i = i + 1
        if t[i] == nil then
            return false
        end
    end
    -- Empty table should be object {}, not array []
    return i > 0
end

-- Encode a table as JSON object or array
function JSON.encodeTable(t)
    if isArray(t) then
        -- Array
        local parts = {}
        for i, v in ipairs(t) do
            parts[i] = JSON.encode(v)
        end
        return "[" .. table.concat(parts, ",") .. "]"
    else
        -- Object
        local parts = {}
        for k, v in pairs(t) do
            if type(k) == "string" then
                table.insert(parts, JSON.encodeString(k) .. ":" .. JSON.encode(v))
            end
        end
        return "{" .. table.concat(parts, ",") .. "}"
    end
end

-- Decode a JSON string to Lua value
function JSON.decode(s)
    if not s or s == "" then
        return nil
    end

    local pos = 1

    local function skipWhitespace()
        while pos <= #s do
            local c = s:sub(pos, pos)
            if c == ' ' or c == '\t' or c == '\n' or c == '\r' then
                pos = pos + 1
            else
                break
            end
        end
    end

    local function peek()
        skipWhitespace()
        return s:sub(pos, pos)
    end

    local function consume(expected)
        skipWhitespace()
        if s:sub(pos, pos) ~= expected then
            error("Expected '" .. expected .. "' at position " .. pos)
        end
        pos = pos + 1
    end

    local parseValue -- forward declaration

    local function parseString()
        consume('"')
        local result = {}
        while pos <= #s do
            local c = s:sub(pos, pos)
            if c == '"' then
                pos = pos + 1
                return table.concat(result)
            elseif c == '\\' then
                pos = pos + 1
                local escape = s:sub(pos, pos)
                pos = pos + 1
                if escape == '"' then table.insert(result, '"')
                elseif escape == '\\' then table.insert(result, '\\')
                elseif escape == '/' then table.insert(result, '/')
                elseif escape == 'b' then table.insert(result, '\b')
                elseif escape == 'f' then table.insert(result, '\f')
                elseif escape == 'n' then table.insert(result, '\n')
                elseif escape == 'r' then table.insert(result, '\r')
                elseif escape == 't' then table.insert(result, '\t')
                elseif escape == 'u' then
                    local hex = s:sub(pos, pos + 3)
                    pos = pos + 4
                    local code = tonumber(hex, 16)
                    if code then
                        table.insert(result, string.char(code))
                    end
                end
            else
                table.insert(result, c)
                pos = pos + 1
            end
        end
        error("Unterminated string")
    end

    local function parseNumber()
        skipWhitespace()
        local start = pos
        -- Match number pattern
        if s:sub(pos, pos) == '-' then
            pos = pos + 1
        end
        while pos <= #s and s:sub(pos, pos):match('[0-9]') do
            pos = pos + 1
        end
        if pos <= #s and s:sub(pos, pos) == '.' then
            pos = pos + 1
            while pos <= #s and s:sub(pos, pos):match('[0-9]') do
                pos = pos + 1
            end
        end
        if pos <= #s and s:sub(pos, pos):lower() == 'e' then
            pos = pos + 1
            if pos <= #s and (s:sub(pos, pos) == '+' or s:sub(pos, pos) == '-') then
                pos = pos + 1
            end
            while pos <= #s and s:sub(pos, pos):match('[0-9]') do
                pos = pos + 1
            end
        end
        return tonumber(s:sub(start, pos - 1))
    end

    local function parseArray()
        consume('[')
        local result = {}
        skipWhitespace()
        if peek() == ']' then
            pos = pos + 1
            return result
        end
        while true do
            table.insert(result, parseValue())
            skipWhitespace()
            if peek() == ']' then
                pos = pos + 1
                return result
            end
            consume(',')
        end
    end

    local function parseObject()
        consume('{')
        local result = {}
        skipWhitespace()
        if peek() == '}' then
            pos = pos + 1
            return result
        end
        while true do
            skipWhitespace()
            local key = parseString()
            consume(':')
            result[key] = parseValue()
            skipWhitespace()
            if peek() == '}' then
                pos = pos + 1
                return result
            end
            consume(',')
        end
    end

    parseValue = function()
        skipWhitespace()
        local c = peek()
        if c == '"' then
            return parseString()
        elseif c == '{' then
            return parseObject()
        elseif c == '[' then
            return parseArray()
        elseif c == 't' then
            if s:sub(pos, pos + 3) == 'true' then
                pos = pos + 4
                return true
            end
        elseif c == 'f' then
            if s:sub(pos, pos + 4) == 'false' then
                pos = pos + 5
                return false
            end
        elseif c == 'n' then
            if s:sub(pos, pos + 3) == 'null' then
                pos = pos + 4
                return nil
            end
        elseif c == '-' or c:match('[0-9]') then
            return parseNumber()
        end
        error("Unexpected character '" .. c .. "' at position " .. pos)
    end

    local success, result = pcall(parseValue)
    if success then
        return result
    else
        print("[GameRL] JSON parse error: " .. tostring(result))
        return nil
    end
end

print("[GameRL] JSON module loaded")
return JSON
