// Harmony patches to capture important game events for push notifications

using HarmonyLib;
using Verse;
using RimWorld;
using RimWorld.GameRL.State;

namespace RimWorld.GameRL.Patches
{
    /// <summary>
    /// Capture pawn death events
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class PawnDeathPatch
    {
        static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            if (__instance?.Faction?.IsPlayer != true) return;

            var extractor = GameRLMod.StateExtractor;
            if (extractor == null) return;

            extractor.RecordEvent("ColonistDeath", 3, new
            {
                PawnName = __instance.LabelShort,
                Cause = dinfo?.Def?.defName ?? "Unknown",
                Position = new { __instance.Position.x, __instance.Position.z }
            });

            Log.Message($"[GameRL] Event: Colonist death - {__instance.LabelShort}");
        }
    }

    /// <summary>
    /// Capture raid/threat events
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class IncidentPatch
    {
        static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            if (!__result) return;

            var extractor = GameRLMod.StateExtractor;
            if (extractor == null) return;

            var defName = __instance.def?.defName ?? "Unknown";
            var severity = GetIncidentSeverity(defName);

            extractor.RecordEvent("Incident", severity, new
            {
                Type = defName,
                Label = __instance.def?.label ?? "Unknown",
                Points = parms.points,
                Faction = parms.faction?.Name ?? "None"
            });

            Log.Message($"[GameRL] Event: Incident - {defName}");
        }

        private static byte GetIncidentSeverity(string defName)
        {
            // High severity for hostile events
            if (defName.Contains("Raid") || defName.Contains("Infestation") ||
                defName.Contains("MechCluster") || defName.Contains("Manhunter"))
                return 3;

            // Medium severity for neutral/mixed events
            if (defName.Contains("Eclipse") || defName.Contains("SolarFlare") ||
                defName.Contains("ColdSnap") || defName.Contains("HeatWave"))
                return 2;

            // Low severity for positive/minor events
            return 1;
        }
    }

    /// <summary>
    /// Capture research completion
    /// </summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class ResearchCompletePatch
    {
        static void Postfix(ResearchProjectDef proj)
        {
            var extractor = GameRLMod.StateExtractor;
            if (extractor == null) return;

            extractor.RecordEvent("ResearchComplete", 1, new
            {
                Project = proj.defName,
                Label = proj.label
            });

            Log.Message($"[GameRL] Event: Research complete - {proj.label}");
        }
    }

    /// <summary>
    /// Capture construction completion
    /// </summary>
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class ConstructionCompletePatch
    {
        static void Postfix(Frame __instance, Pawn worker)
        {
            var extractor = GameRLMod.StateExtractor;
            if (extractor == null) return;

            var buildingDef = __instance.BuildDef?.defName ?? "Unknown";

            // Only record significant buildings
            if (!IsSignificantBuilding(buildingDef)) return;

            extractor.RecordEvent("ConstructionComplete", 1, new
            {
                Building = buildingDef,
                Label = __instance.BuildDef?.label ?? "Unknown",
                Position = new { __instance.Position.x, __instance.Position.z },
                Builder = worker?.LabelShort ?? "Unknown"
            });
        }

        private static bool IsSignificantBuilding(string defName)
        {
            // Record completion of significant structures
            return defName.Contains("Turret") ||
                   defName.Contains("Battery") ||
                   defName.Contains("Generator") ||
                   defName.Contains("Workbench") ||
                   defName.Contains("Bed") ||
                   defName.Contains("Door") ||
                   defName.Contains("Wall");
        }
    }

}
