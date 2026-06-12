// Harmony patch for processing commands even when paused
// TickManagerUpdate runs every frame regardless of pause state

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Hook into TickManagerUpdate which runs every frame (even when paused).
    /// This ensures ProcessCommands() is called so IPC messages are dequeued
    /// even when the game is paused.
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    public static class UpdatePatch
    {
        static void Postfix()
        {
            GameRLMod.OnUpdate();
        }
    }
}
