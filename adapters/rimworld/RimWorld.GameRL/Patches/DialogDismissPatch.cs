// Harmony patch to auto-dismiss blocking dialogs for RL agent operation
// Modal dialogs (naming, research completion, events) block gameplay and must be handled.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Null-safety prefix for LetterStack.RemoveLetter.
    /// GameEnder.GameEndTick can hold stale references to letters already removed by
    /// DismissAllDialogs, causing NullReferenceException when it calls RemoveLetter.
    /// This prefix skips the call if the letter is null or already removed.
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.RemoveLetter))]
    public static class LetterRemoveNullSafetyPatch
    {
        static bool Prefix(LetterStack __instance, Letter let)
        {
            if (let == null) return false;
            if (!__instance.LettersListForReading.Contains(let)) return false;
            return true;
        }
    }

    /// <summary>
    /// Auto-dismiss the faction/settlement naming dialog that appears after landing.
    /// Accepts the default randomized names so the RL agent can proceed.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_FactionDuringLanding), nameof(Dialog_FactionDuringLanding.DoWindowContents))]
    public static class FactionNamingDialogPatch
    {
        static void Postfix(Dialog_FactionDuringLanding __instance, Rect inRect)
        {
            // Always auto-dismiss — no static flag so this works across saves/sessions

            Log.Message("[GameRL] Auto-dismissing faction naming dialog");

            try
            {
                var traverse = Traverse.Create(__instance);

                string? factionName = null;
                string? settlementName = null;

                foreach (var fieldName in new[] { "curName", "factionName", "typedFactionName" })
                {
                    try
                    {
                        var val = traverse.Field(fieldName).GetValue<string>();
                        if (!string.IsNullOrEmpty(val)) { factionName = val; break; }
                    }
                    catch { }
                }

                foreach (var fieldName in new[] { "curSettlementName", "settlementName", "typedSettlementName" })
                {
                    try
                    {
                        var val = traverse.Field(fieldName).GetValue<string>();
                        if (!string.IsNullOrEmpty(val)) { settlementName = val; break; }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(factionName) && Faction.OfPlayer != null)
                {
                    Faction.OfPlayer.Name = factionName;
                    Log.Message($"[GameRL] Faction named: {factionName}");
                }

                if (!string.IsNullOrEmpty(settlementName))
                {
                    var map = Find.CurrentMap;
                    if (map != null)
                    {
                        var settlement = Find.WorldObjects.SettlementAt(map.Tile);
                        if (settlement != null)
                        {
                            settlement.Name = settlementName;
                            Log.Message($"[GameRL] Settlement named: {settlementName}");
                        }
                    }
                }

                __instance.Close(true);
                Log.Message("[GameRL] Faction naming dialog closed");
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] Failed to auto-dismiss naming dialog: {ex.Message}");
                try { __instance.Close(true); } catch { }
            }
        }

    }

    /// <summary>
    /// Utility to dismiss all open dialog windows from a GameRL action.
    /// Handles Dialog_MessageBox, research completion, quest popups, etc.
    /// </summary>
    public static class DialogDismissUtil
    {
        private static int _framesSinceLastCheck;
        private const int CheckIntervalFrames = 30; // ~0.5 seconds at 60fps

        /// <summary>
        /// List of dialog type names dismissed since last observation.
        /// Cleared when read so the agent sees each dismissal exactly once.
        /// </summary>
        public static List<string> RecentlyDismissed { get; } = new List<string>();

        /// <summary>
        /// Called from OnUpdate every frame to auto-dismiss blocking dialogs.
        /// Must run from OnUpdate (not OnTick) because modal dialogs block the tick loop.
        /// </summary>
        public static void FrameCheck()
        {
            _framesSinceLastCheck++;
            if (_framesSinceLastCheck < CheckIntervalFrames) return;
            _framesSinceLastCheck = 0;

            DismissAllDialogs();
        }

        /// <summary>
        /// Check if a window type is a blocking dialog that should be auto-dismissed.
        /// </summary>
        private static bool IsBlockingDialog(Window window)
        {
            if (window == null) return false;
            var typeName = window.GetType().Name;

            // Skip core UI windows that should never be closed
            if (typeName.StartsWith("MainTabWindow") ||
                typeName == "EditWindow_Log" ||
                typeName == "UIRoot_Play" ||
                typeName == "MapInterface")
                return false;

            // Close dialog-type windows that block gameplay
            return typeName.StartsWith("Dialog_") ||
                   typeName.Contains("MessageBox") ||
                   typeName.Contains("ChooseResearch") ||
                   window is Dialog_MessageBox;
        }

        /// <summary>
        /// Close all blocking dialog windows currently open.
        /// Returns the number of dialogs closed.
        /// </summary>
        public static int DismissAllDialogs()
        {
            int dismissed = 0;

            try
            {
                // Get all open windows
                var windows = Find.WindowStack?.Windows?.ToList();
                if (windows == null) return 0;

                foreach (var window in windows)
                {
                    if (!IsBlockingDialog(window)) continue;
                    var typeName = window.GetType().Name;

                    Log.Message($"[GameRL] Auto-dismissing dialog: {typeName}");
                    try
                    {
                        window.Close(true);
                        dismissed++;
                        RecentlyDismissed.Add(typeName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[GameRL] Failed to close {typeName}: {ex.Message}");
                    }
                }

                // Also dismiss letters
                var letterStack = Find.LetterStack;
                if (letterStack != null)
                {
                    var letters = letterStack.LettersListForReading.ToList();
                    foreach (var letter in letters)
                    {
                        letterStack.RemoveLetter(letter);
                        dismissed++;
                        RecentlyDismissed.Add($"Letter:{letter.Label}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] DismissAllDialogs error: {ex.Message}");
            }

            return dismissed;
        }

        /// <summary>
        /// Get and clear the list of recently dismissed dialogs (for observation).
        /// </summary>
        public static List<string> ConsumeRecentlyDismissed()
        {
            if (RecentlyDismissed.Count == 0) return new List<string>();
            var result = new List<string>(RecentlyDismissed);
            RecentlyDismissed.Clear();
            return result;
        }
    }
}
