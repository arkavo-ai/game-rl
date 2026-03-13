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
    /// Auto-dismiss the faction/settlement naming dialog that appears after landing.
    /// Accepts the default randomized names so the RL agent can proceed.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_FactionDuringLanding), nameof(Dialog_FactionDuringLanding.DoWindowContents))]
    public static class FactionNamingDialogPatch
    {
        private static bool _dismissed;

        static void Postfix(Dialog_FactionDuringLanding __instance, Rect inRect)
        {
            if (_dismissed) return;
            _dismissed = true;

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

        public static void Reset()
        {
            _dismissed = false;
        }
    }

    /// <summary>
    /// Utility to dismiss all open dialog windows from a GameRL action.
    /// Handles Dialog_MessageBox, research completion, quest popups, etc.
    /// </summary>
    public static class DialogDismissUtil
    {
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
                    // Skip the main game windows and toolbars
                    if (window == null) continue;
                    var typeName = window.GetType().Name;

                    // Skip core UI windows that should never be closed
                    if (typeName.StartsWith("MainTabWindow") ||
                        typeName == "EditWindow_Log" ||
                        typeName == "UIRoot_Play" ||
                        typeName == "MapInterface")
                        continue;

                    // Close dialog-type windows that block gameplay
                    if (typeName.StartsWith("Dialog_") ||
                        typeName.Contains("MessageBox") ||
                        typeName.Contains("ChooseResearch") ||
                        window is Dialog_MessageBox)
                    {
                        Log.Message($"[GameRL] Dismissing dialog: {typeName}");
                        try
                        {
                            window.Close(true);
                            dismissed++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[GameRL] Failed to close {typeName}: {ex.Message}");
                        }
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
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[GameRL] DismissAllDialogs error: {ex.Message}");
            }

            return dismissed;
        }
    }
}
