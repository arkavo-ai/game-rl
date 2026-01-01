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
                Log.Warning("[GameRL] Move: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (!pawn.Drafted)
            {
                Log.Warning($"[GameRL] Move: {pawn.LabelShort} ({pawn.ThingID}) is not drafted. Call Draft first.");
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
            if (pawn == null)
            {
                Log.Warning("[GameRL] Draft: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (pawn.drafter == null)
            {
                Log.Warning($"[GameRL] Draft: {pawn.LabelShort} ({pawn.ThingID}) cannot be drafted (no drafter component)");
                return;
            }

            pawn.drafter.Drafted = true;
            Log.Message($"[GameRL] Draft: {pawn.LabelShort} is now drafted");
        }

        /// <summary>
        /// Undraft a colonist to resume normal behavior
        /// </summary>
        [GameRLAction("Undraft", Description = "Undraft a colonist")]
        public static void Undraft([GameRLParam("ColonistId")] Pawn pawn)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] Undraft: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (pawn.drafter == null)
            {
                Log.Warning($"[GameRL] Undraft: {pawn.LabelShort} ({pawn.ThingID}) cannot be undrafted (no drafter component)");
                return;
            }

            pawn.drafter.Drafted = false;
            Log.Message($"[GameRL] Undraft: {pawn.LabelShort} is now undrafted");
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
                Log.Warning("[GameRL] SetWorkPriority: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (string.IsNullOrEmpty(workType))
            {
                Log.Warning("[GameRL] SetWorkPriority: WorkType is required. Valid types: Firefighter, Patient, Doctor, PatientBedRest, Childcare, BasicWorker, Warden, Handling, Cooking, Hunting, Construction, Growing, Mining, PlantCutting, Smithing, Tailoring, Art, Crafting, Hauling, Cleaning, Research");
                return;
            }

            var workDef = DefDatabase<WorkTypeDef>.GetNamed(workType, errorOnFail: false);
            if (workDef == null)
            {
                Log.Warning($"[GameRL] SetWorkPriority: Unknown WorkType '{workType}'. Valid types: Firefighter, Patient, Doctor, PatientBedRest, Childcare, BasicWorker, Warden, Handling, Cooking, Hunting, Construction, Growing, Mining, PlantCutting, Smithing, Tailoring, Art, Crafting, Hauling, Cleaning, Research");
                return;
            }

            if (pawn.workSettings == null)
            {
                Log.Warning($"[GameRL] SetWorkPriority: {pawn.LabelShort} ({pawn.ThingID}) has no work settings (might be incapable of work)");
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
            if (pawn == null)
            {
                Log.Warning("[GameRL] Attack: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (target == null)
            {
                Log.Warning("[GameRL] Attack: TargetId not found. Use a ThingID from Entities.Threats or Entities.Animals");
                return;
            }

            if (!pawn.Drafted)
            {
                Log.Warning($"[GameRL] Attack: {pawn.LabelShort} ({pawn.ThingID}) is not drafted. Call Draft first.");
                return;
            }

            var job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Attack: {pawn.LabelShort} attacking {target.LabelShort}");
        }

        /// <summary>
        /// Force a pawn to pick up an item
        /// </summary>
        [GameRLAction("Haul", Description = "Force a pawn to haul an item to a stockpile")]
        public static void Haul(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("ItemId")] Thing thing)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] Haul: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (thing == null)
            {
                Log.Warning("[GameRL] Haul: ItemId not found. Use a ThingID from Entities.Items or Resources");
                return;
            }

            var job = HaulAIUtility.HaulToStorageJob(pawn, thing, false);
            if (job != null)
            {
                pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
                Log.Message($"[GameRL] Haul: {pawn.LabelShort} hauling {thing.LabelShort} ({thing.ThingID})");
            }
            else
            {
                Log.Warning($"[GameRL] Haul: Cannot haul {thing.LabelShort} ({thing.ThingID}) - no valid stockpile or item is forbidden/unreachable");
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
            if (pawn == null)
            {
                Log.Warning("[GameRL] MoveToEntity: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (target == null)
            {
                Log.Warning("[GameRL] MoveToEntity: TargetId not found. Use a ThingID from any Entities category");
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
                    Log.Warning($"[GameRL] MoveToEntity: No reachable cell near {target.LabelShort} ({target.ThingID}) at position ({target.Position.x},{target.Position.z})");
                    return;
                }
            }

            var job = JobMaker.MakeJob(JobDefOf.Goto, targetCell);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] MoveToEntity: {pawn.LabelShort} moving to {target.LabelShort} at ({targetCell.x},{targetCell.z})");
        }

        /// <summary>
        /// Initiate social interaction with another pawn
        /// </summary>
        [GameRLAction("Chat", Description = "Initiate social interaction with target pawn")]
        public static void Chat(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Pawn target)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] Chat: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (target == null)
            {
                Log.Warning("[GameRL] Chat: TargetId not found. Use a ThingID from Entities.Colonists or Entities.Visitors");
                return;
            }

            if (pawn.Downed)
            {
                Log.Warning($"[GameRL] Chat: {pawn.LabelShort} ({pawn.ThingID}) is downed and cannot move");
                return;
            }

            if (target.Downed)
            {
                Log.Warning($"[GameRL] Chat: Target {target.LabelShort} ({target.ThingID}) is downed");
                return;
            }

            // Use GotoAndChitchat job to walk to and talk with target
            var job = JobMaker.MakeJob(JobDefOf.GotoAndBeSociallyActive, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Chat: {pawn.LabelShort} going to chat with {target.LabelShort}");
        }

        /// <summary>
        /// Equip a weapon
        /// </summary>
        [GameRLAction("Equip", Description = "Have a pawn equip a weapon")]
        public static void Equip(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("WeaponId")] Thing weapon)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] Equip: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (weapon == null)
            {
                Log.Warning("[GameRL] Equip: WeaponId not found. Use a ThingID from Entities.Weapons or Entities.Items");
                return;
            }

            if (!weapon.def.IsWeapon)
            {
                Log.Warning($"[GameRL] Equip: {weapon.LabelShort} ({weapon.ThingID}) is not a weapon. Only weapons can be equipped.");
                return;
            }

            // Create job to equip the weapon
            var job = JobMaker.MakeJob(JobDefOf.Equip, weapon);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Equip: {pawn.LabelShort} going to equip {weapon.LabelShort}");
        }

        /// <summary>
        /// Set medical care level for a pawn
        /// </summary>
        [GameRLAction("SetMedicalCare", Description = "Set medical care level for a pawn")]
        public static void SetMedicalCare(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("CareLevel")] string careLevel)
        {
            if (pawn == null)
            {
                Log.Warning("[GameRL] SetMedicalCare: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
                return;
            }

            if (string.IsNullOrEmpty(careLevel))
            {
                Log.Warning("[GameRL] SetMedicalCare: CareLevel is required. Valid levels: nocare, nomeds, herbal, normal, best");
                return;
            }

            if (pawn.playerSettings == null)
            {
                Log.Warning($"[GameRL] SetMedicalCare: {pawn.LabelShort} ({pawn.ThingID}) has no player settings (might be a non-colonist)");
                return;
            }

            MedicalCareCategory care;
            switch (careLevel.ToLower())
            {
                case "nocare":
                case "no_care":
                case "0":
                    care = MedicalCareCategory.NoCare;
                    break;
                case "nomeds":
                case "nomedication":
                case "no_medication":
                case "1":
                    care = MedicalCareCategory.NoMeds;
                    break;
                case "herbal":
                case "herbalonly":
                case "2":
                    care = MedicalCareCategory.HerbalOrWorse;
                    break;
                case "normal":
                case "industrial":
                case "3":
                    care = MedicalCareCategory.NormalOrWorse;
                    break;
                case "best":
                case "glitterworld":
                case "4":
                    care = MedicalCareCategory.Best;
                    break;
                default:
                    Log.Warning($"[GameRL] SetMedicalCare: Unknown CareLevel '{careLevel}'. Valid levels: nocare (0), nomeds (1), herbal (2), normal (3), best (4)");
                    return;
            }

            pawn.playerSettings.medCare = care;
            Log.Message($"[GameRL] SetMedicalCare: Set {pawn.LabelShort} to {care}");
        }
    }
}
