// Game-level actions for RimWorld GameRL - speed control, camera, etc.

using GameRL.Harmony.RPC;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Game-level actions (speed control, camera, etc.)
    /// </summary>
    [GameRLComponent]
    public static class GameActions
    {
        /// <summary>
        /// Set game speed (0=paused, 1=normal, 2=fast, 3=superfast)
        /// </summary>
        [GameRLAction("SetSpeed", Description = "Set game speed (0=paused, 1=normal, 2=fast, 3=superfast)")]
        public static void SetSpeed([GameRLParam("Speed")] int speed)
        {
            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                Log.Warning("[GameRL] SetSpeed: No TickManager available");
                return;
            }

            var timeSpeed = speed switch
            {
                0 => TimeSpeed.Paused,
                1 => TimeSpeed.Normal,
                2 => TimeSpeed.Fast,
                3 => TimeSpeed.Superfast,
                _ => TimeSpeed.Normal
            };

            tickManager.CurTimeSpeed = timeSpeed;
            Log.Message($"[GameRL] SetSpeed: Game speed set to {timeSpeed}");
        }

        /// <summary>
        /// Unpause the game and set to normal speed
        /// </summary>
        [GameRLAction("Unpause", Description = "Resume the game at normal speed")]
        public static void Unpause()
        {
            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                Log.Warning("[GameRL] Unpause: No TickManager available");
                return;
            }

            tickManager.CurTimeSpeed = TimeSpeed.Normal;
            Log.Message("[GameRL] Unpause: Game resumed at normal speed");
        }
    }
}
