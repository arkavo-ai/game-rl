// Action result tracking for RL feedback

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Result of executing an action - provides feedback for RL agents
    /// </summary>
    public class ActionResult
    {
        /// <summary>
        /// Whether the action executed successfully
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Action type that was attempted
        /// </summary>
        public string ActionType { get; set; } = "";

        /// <summary>
        /// Human-readable message about the result
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// Error code if failed (for programmatic handling)
        /// </summary>
        public ActionErrorCode? ErrorCode { get; set; }

        public static ActionResult Ok(string actionType, string message = "")
        {
            return new ActionResult
            {
                Success = true,
                ActionType = actionType,
                Message = message
            };
        }

        public static ActionResult Fail(string actionType, ActionErrorCode code, string message)
        {
            return new ActionResult
            {
                Success = false,
                ActionType = actionType,
                ErrorCode = code,
                Message = message
            };
        }

        public static ActionResult NoOp()
        {
            return new ActionResult
            {
                Success = true,
                ActionType = "NoOp",
                Message = "No action taken"
            };
        }
    }

    /// <summary>
    /// Standard error codes for action failures
    /// </summary>
    public enum ActionErrorCode
    {
        /// <summary>Unknown action type</summary>
        UnknownAction,

        /// <summary>Target entity not found (invalid ID)</summary>
        TargetNotFound,

        /// <summary>Target is invalid for this action (wrong type, dead, etc.)</summary>
        InvalidTarget,

        /// <summary>Action preconditions not met (e.g., can't draft already drafted pawn)</summary>
        PreconditionFailed,

        /// <summary>No map loaded</summary>
        NoMap,

        /// <summary>Position out of bounds or blocked</summary>
        InvalidPosition,

        /// <summary>Missing required resources</summary>
        InsufficientResources,

        /// <summary>Action would have no effect</summary>
        NoEffect,

        /// <summary>Internal error during execution</summary>
        InternalError
    }
}
