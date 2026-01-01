package com.arkavo.gamerl.zomboid.actions;

import zombie.characters.IsoPlayer;
import zombie.iso.IsoWorld;
import zombie.iso.IsoCell;
import zombie.iso.IsoGridSquare;

import java.lang.reflect.Method;
import java.util.*;

/**
 * Dispatches actions to game.
 */
public class ActionDispatcher {

    private final Map<String, ActionHandler> actionHandlers = new HashMap<>();
    private ActionResult lastResult;

    public ActionDispatcher() {
        registerActions();
    }

    private void registerActions() {
        // Movement
        registerAction("Move", this::handleMove);
        registerAction("Sprint", this::handleSprint);
        registerAction("Sneak", this::handleSneak);
        registerAction("Wait", this::handleWait);

        // Combat
        registerAction("Attack", this::handleAttack);
        registerAction("Shove", this::handleShove);
        registerAction("Equip", this::handleEquip);

        // Interaction
        registerAction("PickUp", this::handlePickUp);
        registerAction("Drop", this::handleDrop);
        registerAction("UseItem", this::handleUseItem);

        // Survival
        registerAction("Eat", this::handleEat);
        registerAction("Drink", this::handleDrink);

        System.out.println("[GameRL] Registered " + actionHandlers.size() + " actions");
    }

    private void registerAction(String name, ActionHandler handler) {
        actionHandlers.put(name, handler);
    }

    public ActionResult dispatch(String actionType, Map<String, Object> params, String agentId) {
        ActionHandler handler = actionHandlers.get(actionType);
        if (handler == null) {
            lastResult = ActionResult.fail(actionType, "UNKNOWN_ACTION",
                "Unknown action: " + actionType + ". Available: " + actionHandlers.keySet());
            return lastResult;
        }

        try {
            // Get survivor from params or agent
            IsoPlayer survivor = resolveSurvivor(params, agentId);
            lastResult = handler.handle(survivor, params);
        } catch (Exception e) {
            lastResult = ActionResult.fail(actionType, "INTERNAL_ERROR", e.getMessage());
        }

        return lastResult;
    }

    private IsoPlayer resolveSurvivor(Map<String, Object> params, String agentId) {
        // Try SurvivorId from params first
        Object survivorId = params.get("SurvivorId");
        if (survivorId != null) {
            String id = survivorId.toString();
            if (id.startsWith("Player")) {
                try {
                    int idx = Integer.parseInt(id.replace("Player", ""));
                    if (idx >= 0 && idx < 4) {
                        return IsoPlayer.players[idx];
                    }
                } catch (NumberFormatException e) {
                    // ignore
                }
            }
        }

        // Default to first player
        return IsoPlayer.players[0];
    }

    public ActionResult getLastResult() {
        return lastResult;
    }

    public Map<String, Object> getActionSpace(String agentType) {
        Map<String, Object> space = new HashMap<>();
        space.put("type", "discrete_parameterized");

        List<Map<String, Object>> actions = new ArrayList<>();

        // Movement actions
        actions.add(actionDef("Wait", "Do nothing, advance simulation"));
        actions.add(actionDef("Move", "Move to target position",
            Map.of("X", intParam(), "Y", intParam(), "Z", intParam(0, 7))));
        actions.add(actionDef("Sprint", "Sprint to position",
            Map.of("X", intParam(), "Y", intParam())));
        actions.add(actionDef("Sneak", "Sneak to position",
            Map.of("X", intParam(), "Y", intParam())));

        // Combat actions
        actions.add(actionDef("Attack", "Attack target",
            Map.of("TargetId", stringParam())));
        actions.add(actionDef("Shove", "Push zombies away"));
        actions.add(actionDef("Equip", "Equip weapon",
            Map.of("ItemId", stringParam())));

        // Interaction actions
        actions.add(actionDef("PickUp", "Pick up item",
            Map.of("ItemId", stringParam())));
        actions.add(actionDef("Drop", "Drop item",
            Map.of("ItemId", stringParam())));
        actions.add(actionDef("UseItem", "Use item",
            Map.of("ItemId", stringParam())));

        // Survival actions
        actions.add(actionDef("Eat", "Eat food",
            Map.of("ItemId", stringParam())));
        actions.add(actionDef("Drink", "Drink beverage",
            Map.of("ItemId", stringParam())));

        space.put("actions", actions);
        return space;
    }

    private Map<String, Object> actionDef(String name, String description) {
        return actionDef(name, description, null);
    }

    private Map<String, Object> actionDef(String name, String description, Map<String, Object> params) {
        Map<String, Object> def = new HashMap<>();
        def.put("name", name);
        def.put("description", description);
        if (params != null) {
            def.put("params", params);
        }
        return def;
    }

    private Map<String, Object> intParam() {
        return Map.of("type", "int");
    }

    private Map<String, Object> intParam(int min, int max) {
        return Map.of("type", "int", "min", min, "max", max);
    }

    private Map<String, Object> stringParam() {
        return Map.of("type", "string");
    }

    // === Action Handlers ===

    private ActionResult handleMove(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Move", "INVALID_SURVIVOR", "Survivor not found");
        }

        int x = getInt(params, "X", (int) player.getX());
        int y = getInt(params, "Y", (int) player.getY());
        int z = getInt(params, "Z", (int) player.getZ());

        // Validate target
        IsoCell cell = IsoWorld.instance.CurrentCell;
        if (cell == null) {
            return ActionResult.fail("Move", "NO_CELL", "No active cell");
        }

        IsoGridSquare target = cell.getGridSquare(x, y, z);
        if (target == null || !target.isFree(false)) {
            return ActionResult.fail("Move", "INVALID_TARGET",
                "Cannot move to (" + x + "," + y + "," + z + ")");
        }

        // Initiate pathfinding
        try {
            player.setPathFindIndex(0);
            if (player.PathToLocation(x, y, z)) {
                return ActionResult.ok("Move", "Moving to (" + x + "," + y + "," + z + ")");
            } else {
                return ActionResult.fail("Move", "NO_PATH",
                    "No path to (" + x + "," + y + "," + z + ")");
            }
        } catch (Exception e) {
            return ActionResult.fail("Move", "PATH_ERROR", e.getMessage());
        }
    }

    private ActionResult handleSprint(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Sprint", "INVALID_SURVIVOR", "Survivor not found");
        }
        player.setSprinting(true);
        return handleMove(player, params);
    }

    private ActionResult handleSneak(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Sneak", "INVALID_SURVIVOR", "Survivor not found");
        }
        player.setSneaking(true);
        return handleMove(player, params);
    }

    private ActionResult handleWait(IsoPlayer player, Map<String, Object> params) {
        return ActionResult.ok("Wait", "Waiting");
    }

    private ActionResult handleAttack(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Attack", "INVALID_SURVIVOR", "Survivor not found");
        }
        String targetId = getString(params, "TargetId", null);
        if (targetId == null) {
            return ActionResult.fail("Attack", "NO_TARGET", "TargetId required");
        }
        // TODO: Resolve target and initiate attack
        return ActionResult.ok("Attack", "Attacking " + targetId);
    }

    private ActionResult handleShove(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Shove", "INVALID_SURVIVOR", "Survivor not found");
        }
        player.setDoShove(true);
        return ActionResult.ok("Shove", "Shoving");
    }

    private ActionResult handleEquip(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Equip", "INVALID_SURVIVOR", "Survivor not found");
        }
        String itemId = getString(params, "ItemId", null);
        if (itemId == null) {
            return ActionResult.fail("Equip", "NO_ITEM", "ItemId required");
        }
        // TODO: Find and equip item
        return ActionResult.ok("Equip", "Equipping " + itemId);
    }

    private ActionResult handlePickUp(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("PickUp", "INVALID_SURVIVOR", "Survivor not found");
        }
        String itemId = getString(params, "ItemId", null);
        if (itemId == null) {
            return ActionResult.fail("PickUp", "NO_ITEM", "ItemId required");
        }
        // TODO: Find and pick up item
        return ActionResult.ok("PickUp", "Picking up " + itemId);
    }

    private ActionResult handleDrop(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("Drop", "INVALID_SURVIVOR", "Survivor not found");
        }
        String itemId = getString(params, "ItemId", null);
        if (itemId == null) {
            return ActionResult.fail("Drop", "NO_ITEM", "ItemId required");
        }
        // TODO: Find and drop item
        return ActionResult.ok("Drop", "Dropping " + itemId);
    }

    private ActionResult handleUseItem(IsoPlayer player, Map<String, Object> params) {
        if (player == null) {
            return ActionResult.fail("UseItem", "INVALID_SURVIVOR", "Survivor not found");
        }
        String itemId = getString(params, "ItemId", null);
        if (itemId == null) {
            return ActionResult.fail("UseItem", "NO_ITEM", "ItemId required");
        }
        // TODO: Find and use item
        return ActionResult.ok("UseItem", "Using " + itemId);
    }

    private ActionResult handleEat(IsoPlayer player, Map<String, Object> params) {
        return handleUseItem(player, params);
    }

    private ActionResult handleDrink(IsoPlayer player, Map<String, Object> params) {
        return handleUseItem(player, params);
    }

    // === Helpers ===

    private int getInt(Map<String, Object> params, String key, int defaultValue) {
        Object value = params.get(key);
        if (value instanceof Number) {
            return ((Number) value).intValue();
        }
        return defaultValue;
    }

    private String getString(Map<String, Object> params, String key, String defaultValue) {
        Object value = params.get(key);
        if (value != null) {
            return value.toString();
        }
        return defaultValue;
    }

    @FunctionalInterface
    private interface ActionHandler {
        ActionResult handle(IsoPlayer player, Map<String, Object> params);
    }
}
