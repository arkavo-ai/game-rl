package com.arkavo.gamerl.zomboid;

import org.json.JSONObject;
import org.json.JSONArray;

import java.util.Map;
import java.util.HashMap;

/**
 * Protocol message parsing and serialization.
 * Matches the wire format from game-bridge/src/protocol.rs
 */
public class Protocol {

    // === Message Classes ===

    public static class RegisterAgentMessage {
        public String agentId;
        public String agentType;
        public Map<String, Object> config;
    }

    public static class ExecuteActionMessage {
        public String agentId;
        public String actionType;
        public Map<String, Object> params;
        public int ticks;
    }

    public static class ResetMessage {
        public Long seed;
        public String scenario;
    }

    public static class ConfigureStreamsMessage {
        public String agentId;
        public String profile;
    }

    // === Parsing ===

    public static RegisterAgentMessage parseRegisterAgent(JSONObject msg) {
        RegisterAgentMessage result = new RegisterAgentMessage();
        result.agentId = msg.optString("AgentId");
        result.agentType = msg.optString("AgentType");
        result.config = jsonToMap(msg.optJSONObject("Config"));
        return result;
    }

    public static ExecuteActionMessage parseExecuteAction(JSONObject msg) {
        ExecuteActionMessage result = new ExecuteActionMessage();
        result.agentId = msg.optString("AgentId");
        result.ticks = msg.optInt("Ticks", 60);

        JSONObject action = msg.optJSONObject("Action");
        if (action != null) {
            // Handle Parameterized action format
            result.actionType = action.optString("action_type", action.optString("Type"));

            // Get params - they might be flattened or in a params object
            result.params = new HashMap<>();
            for (String key : action.keySet()) {
                if (!key.equals("action_type") && !key.equals("Type")) {
                    result.params.put(key, action.get(key));
                }
            }
        }
        return result;
    }

    public static ResetMessage parseReset(JSONObject msg) {
        ResetMessage result = new ResetMessage();
        if (msg.has("Seed") && !msg.isNull("Seed")) {
            result.seed = msg.optLong("Seed");
        }
        result.scenario = msg.optString("Scenario", null);
        return result;
    }

    public static ConfigureStreamsMessage parseConfigureStreams(JSONObject msg) {
        ConfigureStreamsMessage result = new ConfigureStreamsMessage();
        result.agentId = msg.optString("AgentId");
        result.profile = msg.optString("Profile");
        return result;
    }

    // === Serialization ===

    /**
     * Convert observation map to JSON.
     * Handles nested maps and arrays.
     */
    public static JSONObject observationToJson(Map<String, Object> observation) {
        JSONObject result = new JSONObject();
        result.put("Structured", mapToJson(observation));
        return result;
    }

    private static Object mapToJson(Map<String, Object> map) {
        if (map == null) return JSONObject.NULL;

        JSONObject result = new JSONObject();
        for (Map.Entry<String, Object> entry : map.entrySet()) {
            result.put(entry.getKey(), valueToJson(entry.getValue()));
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    private static Object valueToJson(Object value) {
        if (value == null) {
            return JSONObject.NULL;
        } else if (value instanceof Map) {
            return mapToJson((Map<String, Object>) value);
        } else if (value instanceof Iterable) {
            JSONArray arr = new JSONArray();
            for (Object item : (Iterable<?>) value) {
                arr.put(valueToJson(item));
            }
            return arr;
        } else if (value instanceof Object[]) {
            JSONArray arr = new JSONArray();
            for (Object item : (Object[]) value) {
                arr.put(valueToJson(item));
            }
            return arr;
        } else if (value instanceof float[]) {
            JSONArray arr = new JSONArray();
            for (float f : (float[]) value) {
                arr.put(f);
            }
            return arr;
        } else if (value instanceof double[]) {
            JSONArray arr = new JSONArray();
            for (double d : (double[]) value) {
                arr.put(d);
            }
            return arr;
        } else if (value instanceof int[]) {
            JSONArray arr = new JSONArray();
            for (int i : (int[]) value) {
                arr.put(i);
            }
            return arr;
        } else {
            return value;
        }
    }

    private static Map<String, Object> jsonToMap(JSONObject json) {
        if (json == null) {
            return new HashMap<>();
        }

        Map<String, Object> result = new HashMap<>();
        for (String key : json.keySet()) {
            Object value = json.get(key);
            if (value instanceof JSONObject) {
                result.put(key, jsonToMap((JSONObject) value));
            } else if (value instanceof JSONArray) {
                result.put(key, jsonArrayToList((JSONArray) value));
            } else if (value == JSONObject.NULL) {
                result.put(key, null);
            } else {
                result.put(key, value);
            }
        }
        return result;
    }

    private static java.util.List<Object> jsonArrayToList(JSONArray arr) {
        java.util.List<Object> result = new java.util.ArrayList<>();
        for (int i = 0; i < arr.length(); i++) {
            Object value = arr.get(i);
            if (value instanceof JSONObject) {
                result.add(jsonToMap((JSONObject) value));
            } else if (value instanceof JSONArray) {
                result.add(jsonArrayToList((JSONArray) value));
            } else if (value == JSONObject.NULL) {
                result.add(null);
            } else {
                result.add(value);
            }
        }
        return result;
    }
}
