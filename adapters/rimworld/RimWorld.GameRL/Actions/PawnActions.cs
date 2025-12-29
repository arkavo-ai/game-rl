// Pawn actions for RimWorld GameRL - using HarmonyRPC attributes

using System;
using GameRL.Harmony.RPC;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Pawn control actions accessible via HarmonyRPC
    /// </summary>
    [GameRLComponent]
    public static class PawnActions
    {
        /// <summary>
        /// Move a drafted pawn to a target position
        /// </summary>
        [GameRLAction("move", Description = "Move a drafted pawn to target coordinates")]
        public static void Move(
            [GameRLParam("colonist_id")] Pawn pawn,
            int x,
            int z)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] move: Pawn not found");
                return;
            }

            if (!pawn.Drafted)
            {
                Log.Warning($"[GameRL] move: Pawn {pawn.ThingID} is not drafted");
                return;
            }

            var target = new IntVec3(x, 0, z);
            var job = JobMaker.MakeJob(JobDefOf.Goto, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
        }

        /// <summary>
        /// Draft a colonist for direct control
        /// </summary>
        [GameRLAction("draft", Description = "Draft a colonist for direct control")]
        public static void Draft([GameRLParam("colonist_id")] Pawn pawn)
        {
            if (pawn?.drafter == null)
            {
                Log.Warning("[GameRL] draft: Pawn not found or cannot be drafted");
                return;
            }

            pawn.drafter.Drafted = true;
        }

        /// <summary>
        /// Undraft a colonist to resume normal behavior
        /// </summary>
        [GameRLAction("undraft", Description = "Undraft a colonist")]
        public static void Undraft([GameRLParam("colonist_id")] Pawn pawn)
        {
            if (pawn?.drafter == null)
            {
                Log.Warning("[GameRL] undraft: Pawn not found or cannot be undrafted");
                return;
            }

            pawn.drafter.Drafted = false;
        }

        /// <summary>
        /// Set work priority for a colonist
        /// </summary>
        [GameRLAction("set_work_priority", Description = "Set work priority for a colonist")]
        public static void SetWorkPriority(
            [GameRLParam("colonist_id")] Pawn pawn,
            [GameRLParam("work_type")] string workType,
            int priority)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] set_work_priority: Pawn not found");
                return;
            }

            var workDef = DefDatabase<WorkTypeDef>.GetNamed(workType, errorOnFail: false);
            if (workDef == null)
            {
                Log.Warning($"[GameRL] set_work_priority: Work type not found: {workType}");
                return;
            }

            if (pawn.workSettings == null)
            {
                Log.Warning($"[GameRL] set_work_priority: workSettings is null for {pawn.LabelShort}");
                return;
            }

            // Ensure manual priorities are enabled (required for SetPriority to work)
            if (!Current.Game.playSettings.useWorkPriorities)
            {
                Current.Game.playSettings.useWorkPriorities = true;
                Log.Message("[GameRL] Enabled manual work priorities");
            }

            var oldPriority = pawn.workSettings.GetPriority(workDef);
            pawn.workSettings.SetPriority(workDef, priority);
            var newPriority = pawn.workSettings.GetPriority(workDef);
            Log.Message($"[GameRL] set_work_priority: {pawn.LabelShort} {workType} {oldPriority} -> {newPriority}");
        }

        /// <summary>
        /// No-op action (advance time without doing anything)
        /// </summary>
        [GameRLAction("wait", Description = "Do nothing, just advance simulation")]
        public static void Wait()
        {
            // No-op
        }

        /// <summary>
        /// Force a pawn to attack a target
        /// </summary>
        [GameRLAction("attack", Description = "Force a drafted pawn to attack a target")]
        public static void Attack(
            [GameRLParam("colonist_id")] Pawn pawn,
            [GameRLParam("target_id")] Thing target)
        {
            if (pawn == null || target == null)
            {
                Log.Warning("[GameRL] attack: Pawn or target not found");
                return;
            }

            if (!pawn.Drafted)
            {
                Log.Warning($"[GameRL] attack: Pawn {pawn.ThingID} is not drafted");
                return;
            }

            var job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
        }

        /// <summary>
        /// Force a pawn to pick up an item
        /// </summary>
        [GameRLAction("haul", Description = "Force a pawn to haul an item to a stockpile")]
        public static void Haul(
            [GameRLParam("colonist_id")] Pawn pawn,
            [GameRLParam("thing_id")] Thing thing)
        {
            if (pawn == null || thing == null)
            {
                Log.Warning("[GameRL] haul: Pawn or thing not found");
                return;
            }

            var job = HaulAIUtility.HaulToStorageJob(pawn, thing, false);
            if (job != null)
            {
                pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            }
            else
            {
                Log.Warning($"[GameRL] haul: Could not create haul job for {thing.ThingID}");
            }
        }
    }
}
