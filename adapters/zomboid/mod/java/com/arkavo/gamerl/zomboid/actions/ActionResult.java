package com.arkavo.gamerl.zomboid.actions;

/**
 * Result of an action execution.
 */
public class ActionResult {
    public final boolean success;
    public final String actionType;
    public final String message;
    public final String errorCode;

    private ActionResult(boolean success, String actionType, String message, String errorCode) {
        this.success = success;
        this.actionType = actionType;
        this.message = message;
        this.errorCode = errorCode;
    }

    public static ActionResult ok(String actionType, String message) {
        return new ActionResult(true, actionType, message, null);
    }

    public static ActionResult fail(String actionType, String errorCode, String message) {
        return new ActionResult(false, actionType, message, errorCode);
    }

    @Override
    public String toString() {
        if (success) {
            return "[OK] " + actionType + ": " + message;
        } else {
            return "[FAIL] " + actionType + " (" + errorCode + "): " + message;
        }
    }
}
