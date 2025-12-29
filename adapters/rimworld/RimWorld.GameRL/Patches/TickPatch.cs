// Harmony patch for game tick synchronization

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Hook into the game tick loop to synchronize with GameRL
    /// </summary>
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class TickPatch
    {
        /// <summary>
        /// Called after each game tick
        /// </summary>
        static void Postfix()
        {
            GameRLMod.OnTick();
        }
    }
}
