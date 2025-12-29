// Harmony patch for deterministic RNG seeding

using HarmonyLib;
using Verse;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Manages RNG seeding for deterministic episodes
    /// </summary>
    public static class RngManager
    {
        /// <summary>
        /// Seed to apply on next RNG initialization
        /// </summary>
        public static ulong? PendingSeed { get; set; }

        /// <summary>
        /// Whether a forced seed is active
        /// </summary>
        public static bool HasPendingSeed => PendingSeed.HasValue;

        /// <summary>
        /// Consume the pending seed and return it
        /// </summary>
        public static int ConsumeSeed()
        {
            if (!PendingSeed.HasValue)
                return 0;

            var seed = (int)(PendingSeed.Value & 0x7FFFFFFF);
            PendingSeed = null;
            Log.Message($"[GameRL] Consumed RNG seed: {seed}");
            return seed;
        }
    }

    /// <summary>
    /// Patch Rand.Seed setter to apply forced seeds
    /// </summary>
    [HarmonyPatch(typeof(Rand), nameof(Rand.Seed), MethodType.Setter)]
    public static class RandSeedPatch
    {
        static void Prefix(ref int value)
        {
            if (RngManager.HasPendingSeed)
            {
                value = RngManager.ConsumeSeed();
            }
        }
    }

    /// <summary>
    /// Patch game initialization to apply seed
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class GameInitPatch
    {
        static void Prefix()
        {
            if (RngManager.HasPendingSeed)
            {
                Log.Message("[GameRL] Applying seed before game initialization");
            }
        }
    }
}
