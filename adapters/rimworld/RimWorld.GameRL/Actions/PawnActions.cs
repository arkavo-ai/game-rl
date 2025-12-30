// Pawn actions for RimWorld GameRL - using HarmonyRPC attributes

using System;
using System.Linq;
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
        [GameRLAction("Move", Description = "Move a drafted pawn to target coordinates")]
        public static void Move(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z)
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
        [GameRLAction("Draft", Description = "Draft a colonist for direct control")]
        public static void Draft([GameRLParam("ColonistId")] Pawn pawn)
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
        [GameRLAction("Undraft", Description = "Undraft a colonist")]
        public static void Undraft([GameRLParam("ColonistId")] Pawn pawn)
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
        [GameRLAction("SetWorkPriority", Description = "Set work priority for a colonist")]
        public static void SetWorkPriority(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("WorkType")] string workType,
            [GameRLParam("Priority")] int priority)
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
        [GameRLAction("Wait", Description = "Do nothing, just advance simulation")]
        public static void Wait()
        {
            // No-op
        }

        /// <summary>
        /// Force a pawn to attack a target
        /// </summary>
        [GameRLAction("Attack", Description = "Force a drafted pawn to attack a target")]
        public static void Attack(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Thing target)
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
        [GameRLAction("Haul", Description = "Force a pawn to haul an item to a stockpile")]
        public static void Haul(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("ThingId")] Thing thing)
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

        /// <summary>
        /// Move a pawn toward a target entity
        /// </summary>
        [GameRLAction("MoveToEntity", Description = "Move a pawn toward a target entity")]
        public static void MoveToEntity(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Thing target,
            [GameRLParam("Distance")] int distance = 1)
        {
            if (pawn == null || target == null)
            {
                Log.Warning("[GameRL] move_to_entity: Pawn or target not found");
                return;
            }

            // Find a cell adjacent to the target at the specified distance
            var targetCell = target.Position;
            if (distance > 0)
            {
                // Find walkable cell near target
                var cells = GenRadial.RadialCellsAround(target.Position, distance, true)
                    .Where(c => c.Standable(pawn.Map) && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly));

                targetCell = cells.FirstOrDefault();
                if (targetCell == default)
                {
                    Log.Warning($"[GameRL] move_to_entity: No reachable cell near {target.ThingID}");
                    return;
                }
            }

            var job = JobMaker.MakeJob(JobDefOf.Goto, targetCell);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] move_to_entity: {pawn.LabelShort} moving to {target.LabelShort} at {targetCell}");
        }

        /// <summary>
        /// Initiate social interaction with another pawn
        /// </summary>
        [GameRLAction("Chat", Description = "Initiate social interaction with target pawn")]
        public static void Chat(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Pawn target)
        {
            if (pawn == null || target == null)
            {
                Log.Warning("[GameRL] chat: Pawn or target not found");
                return;
            }

            if (pawn.Downed || target.Downed)
            {
                Log.Warning("[GameRL] chat: Cannot chat with downed pawn");
                return;
            }

            // Use GotoAndChitchat job to walk to and talk with target
            var job = JobMaker.MakeJob(JobDefOf.GotoAndBeSociallyActive, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] chat: {pawn.LabelShort} going to chat with {target.LabelShort}");
        }

        /// <summary>
        /// Equip a weapon
        /// </summary>
        [GameRLAction("Equip", Description = "Have a pawn equip a weapon")]
        public static void Equip(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("WeaponId")] Thing weapon)
        {
            if (pawn == null || weapon == null)
            {
                Log.Warning("[GameRL] equip: Pawn or weapon not found");
                return;
            }

            if (!weapon.def.IsWeapon)
            {
                Log.Warning($"[GameRL] equip: {weapon.ThingID} is not a weapon");
                return;
            }

            // Create job to equip the weapon
            var job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] equip: {pawn.LabelShort} going to equip {weapon.LabelShort}");
        }
    }
}
