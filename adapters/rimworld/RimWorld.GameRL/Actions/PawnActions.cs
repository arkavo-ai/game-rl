// Pawn actions for RimWorld GameRL - using HarmonyRPC attributes

using System;
using System.Collections.Generic;
using System.Linq;
using GameRL.Harmony.RPC;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorld.Planet;

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
                throw new InvalidOperationException("Move: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (!pawn.Drafted)
            {
                throw new InvalidOperationException($"Move: {pawn.LabelShort} ({pawn.ThingID}) is not drafted. Call Draft first.");
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
                throw new InvalidOperationException("Draft: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (pawn.drafter == null)
            {
                throw new InvalidOperationException($"Draft: {pawn.LabelShort} ({pawn.ThingID}) cannot be drafted (no drafter component)");
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
                throw new InvalidOperationException("Undraft: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (pawn.drafter == null)
            {
                throw new InvalidOperationException($"Undraft: {pawn.LabelShort} ({pawn.ThingID}) cannot be undrafted (no drafter component)");
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
                throw new InvalidOperationException("SetWorkPriority: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (string.IsNullOrEmpty(workType))
            {
                throw new InvalidOperationException("SetWorkPriority: WorkType is required. Valid types: Firefighter, Patient, Doctor, PatientBedRest, Childcare, BasicWorker, Warden, Handling, Cooking, Hunting, Construction, Growing, Mining, PlantCutting, Smithing, Tailoring, Art, Crafting, Hauling, Cleaning, Research");
            }

            var workDef = DefDatabase<WorkTypeDef>.GetNamed(workType, errorOnFail: false);
            if (workDef == null)
            {
                throw new InvalidOperationException($"SetWorkPriority: Unknown WorkType '{workType}'. Valid types: Firefighter, Patient, Doctor, PatientBedRest, Childcare, BasicWorker, Warden, Handling, Cooking, Hunting, Construction, Growing, Mining, PlantCutting, Smithing, Tailoring, Art, Crafting, Hauling, Cleaning, Research");
            }

            if (pawn.workSettings == null)
            {
                throw new InvalidOperationException($"SetWorkPriority: {pawn.LabelShort} ({pawn.ThingID}) has no work settings (might be incapable of work)");
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
        /// Force a pawn to attack a target
        /// </summary>
        [GameRLAction("Attack", Description = "Force a drafted pawn to attack a target")]
        public static void Attack(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Thing target)
        {
            if (pawn == null)
            {
                throw new InvalidOperationException("Attack: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (target == null)
            {
                throw new InvalidOperationException("Attack: TargetId not found. Use a ThingID from Entities.Threats or Entities.Animals");
            }

            if (!pawn.Drafted)
            {
                throw new InvalidOperationException($"Attack: {pawn.LabelShort} ({pawn.ThingID}) is not drafted. Call Draft first.");
            }

            // Use ranged attack if pawn has a ranged weapon, melee otherwise
            Job job;
            if (pawn.equipment?.Primary != null && pawn.equipment.Primary.def.IsRangedWeapon)
            {
                job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                Log.Message($"[GameRL] Attack: {pawn.LabelShort} shooting at {target.LabelShort} with {pawn.equipment.Primary.LabelShort}");
            }
            else
            {
                job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                Log.Message($"[GameRL] Attack: {pawn.LabelShort} melee attacking {target.LabelShort}");
            }
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
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
                throw new InvalidOperationException("Haul: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (thing == null)
            {
                throw new InvalidOperationException("Haul: ItemId not found. Use a ThingID from Entities.Items or Resources");
            }

            var job = HaulAIUtility.HaulToStorageJob(pawn, thing, false);
            if (job != null)
            {
                pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
                Log.Message($"[GameRL] Haul: {pawn.LabelShort} hauling {thing.LabelShort} ({thing.ThingID})");
            }
            else
            {
                throw new InvalidOperationException($"Haul: Cannot haul {thing.LabelShort} ({thing.ThingID}) - no valid stockpile or item is forbidden/unreachable");
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
                throw new InvalidOperationException("MoveToEntity: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (target == null)
            {
                throw new InvalidOperationException("MoveToEntity: TargetId not found. Use a ThingID from any Entities category");
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
                    throw new InvalidOperationException($"MoveToEntity: No reachable cell near {target.LabelShort} ({target.ThingID}) at position ({target.Position.x},{target.Position.z})");
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
                throw new InvalidOperationException("Chat: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (target == null)
            {
                throw new InvalidOperationException("Chat: TargetId not found. Use a ThingID from Entities.Colonists or Entities.Visitors");
            }

            if (pawn.Downed)
            {
                throw new InvalidOperationException($"Chat: {pawn.LabelShort} ({pawn.ThingID}) is downed and cannot move");
            }

            if (target.Downed)
            {
                throw new InvalidOperationException($"Chat: Target {target.LabelShort} ({target.ThingID}) is downed");
            }

            // Use GotoAndChitchat job to walk to and talk with target
            var job = JobMaker.MakeJob(JobDefOf.GotoAndBeSociallyActive, target);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Chat: {pawn.LabelShort} going to chat with {target.LabelShort}");
        }

        [GameRLAction("Arrest", Description = "Arrest a non-hostile pawn (visitor/wanderer). Requires drafted colonist. WARNING: causes ~80 faction goodwill penalty")]
        public static void Arrest(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("TargetId")] Pawn target)
        {
            if (pawn == null)
                throw new InvalidOperationException("Arrest: ColonistId not found. Use a ThingID from Entities.Colonists");
            if (target == null)
                throw new InvalidOperationException("Arrest: TargetId not found. Use a ThingID from Entities.Visitors");
            if (pawn.Downed)
                throw new InvalidOperationException($"Arrest: {pawn.LabelShort} ({pawn.ThingID}) is downed");
            if (!pawn.Drafted)
                throw new InvalidOperationException($"Arrest: {pawn.LabelShort} ({pawn.ThingID}) must be drafted first. Use Draft action");
            if (!target.RaceProps.Humanlike)
                throw new InvalidOperationException($"Arrest: Target {target.LabelShort} ({target.ThingID}) is not humanlike");
            if (target.Faction == Faction.OfPlayer)
                throw new InvalidOperationException($"Arrest: Cannot arrest own colonist {target.LabelShort}");
            if (target.HostileTo(Faction.OfPlayer))
                throw new InvalidOperationException($"Arrest: {target.LabelShort} is already hostile. Use Attack instead");
            if (target.IsPrisoner)
                throw new InvalidOperationException($"Arrest: {target.LabelShort} is already a prisoner. Use SetPrisonerInteraction");

            var bed = RestUtility.FindBedFor(target, pawn, checkSocialProperness: false, ignoreOtherReservations: false);
            if (bed == null)
                throw new InvalidOperationException("Arrest: No available prisoner bed. Build a Bed and set it for prisoners first");

            var job = JobMaker.MakeJob(JobDefOf.Arrest, target, bed);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Arrest: {pawn.LabelShort} arresting {target.LabelShort} (faction: {target.Faction?.Name ?? "none"})");
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
                throw new InvalidOperationException("Equip: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (weapon == null)
            {
                throw new InvalidOperationException("Equip: WeaponId not found. Use a ThingID from Entities.Weapons or Entities.Items");
            }

            if (!weapon.def.IsWeapon)
            {
                throw new InvalidOperationException($"Equip: {weapon.LabelShort} ({weapon.ThingID}) is not a weapon. Only weapons can be equipped.");
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
                throw new InvalidOperationException("SetMedicalCare: ColonistId not found. Use a ThingID from Entities.Colonists (e.g., 'Human123')");
            }

            if (string.IsNullOrEmpty(careLevel))
            {
                throw new InvalidOperationException("SetMedicalCare: CareLevel is required. Valid levels: nocare, nomeds, herbal, normal, best");
            }

            if (pawn.playerSettings == null)
            {
                throw new InvalidOperationException($"SetMedicalCare: {pawn.LabelShort} ({pawn.ThingID}) has no player settings (might be a non-colonist)");
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
                    throw new InvalidOperationException($"SetMedicalCare: Unknown CareLevel '{careLevel}'. Valid levels: nocare (0), nomeds (1), herbal (2), normal (3), best (4)");
            }

            pawn.playerSettings.medCare = care;
            Log.Message($"[GameRL] SetMedicalCare: Set {pawn.LabelShort} to {care}");
        }

        /// <summary>
        /// Rescue a downed pawn to a medical bed
        /// </summary>
        [GameRLAction("Rescue", Description = "Have a colonist rescue a downed pawn to a medical bed")]
        public static void Rescue(
            [GameRLParam("ColonistId")] Pawn rescuer,
            [GameRLParam("TargetId")] Pawn patient)
        {
            if (rescuer == null)
            {
                throw new InvalidOperationException("Rescue: ColonistId not found");
            }

            if (patient == null)
            {
                throw new InvalidOperationException("Rescue: TargetId not found");
            }

            if (rescuer.Downed)
            {
                throw new InvalidOperationException($"Rescue: {rescuer.LabelShort} is downed and cannot rescue");
            }

            if (!patient.Downed)
            {
                throw new InvalidOperationException($"Rescue: {patient.LabelShort} is not downed");
            }

            // Find a medical bed
            var bed = RestUtility.FindBedFor(patient, rescuer, checkSocialProperness: false);
            if (bed == null)
            {
                throw new InvalidOperationException($"Rescue: No available bed for {patient.LabelShort}");
            }

            var job = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
            rescuer.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Rescue: {rescuer.LabelShort} rescuing {patient.LabelShort}");
        }

        /// <summary>
        /// Prioritize tending a patient
        /// </summary>
        [GameRLAction("TendTo", Description = "Have a doctor tend to an injured/sick pawn")]
        public static void TendTo(
            [GameRLParam("ColonistId")] Pawn doctor,
            [GameRLParam("TargetId")] Pawn patient)
        {
            if (doctor == null)
            {
                throw new InvalidOperationException("TendTo: ColonistId (doctor) not found");
            }

            if (patient == null)
            {
                throw new InvalidOperationException("TendTo: TargetId (patient) not found");
            }

            if (doctor.Downed)
            {
                throw new InvalidOperationException($"TendTo: {doctor.LabelShort} is downed");
            }

            // Check patient has tending needs
            var hasTendable = patient.health?.hediffSet?.hediffs
                ?.Any(h => h.TendableNow()) ?? false;
            if (!hasTendable)
            {
                throw new InvalidOperationException($"TendTo: {patient.LabelShort} has no tendable conditions");
            }

            // Check if patient is already reserved by another doctor
            var map = doctor.Map ?? Find.CurrentMap;
            if (map != null)
            {
                var existingReserver = map.reservationManager.FirstRespectedReserver(patient, doctor);
                if (existingReserver != null && existingReserver != doctor)
                {
                    throw new InvalidOperationException($"TendTo: {patient.LabelShort} is already being tended by {existingReserver.LabelShort}. Wait for them to finish.");
                }
            }

            var job = JobMaker.MakeJob(JobDefOf.TendPatient, patient);
            doctor.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] TendTo: {doctor.LabelShort} tending {patient.LabelShort}");
        }

        /// <summary>
        /// Set a colonist's schedule for a specific hour
        /// </summary>
        [GameRLAction("SetSchedule", Description = "Set a colonist's schedule for a specific hour (Work, Sleep, Joy, Anything)")]
        public static void SetSchedule(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("Hour")] int hour,
            [GameRLParam("Assignment")] string assignment)
        {
            if (pawn == null)
            {
                throw new InvalidOperationException("SetSchedule: ColonistId not found. Use a ThingID from Entities.Colonists");
            }

            if (pawn.timetable == null)
            {
                throw new InvalidOperationException($"SetSchedule: {pawn.LabelShort} ({pawn.ThingID}) has no timetable");
            }

            if (hour < 0 || hour > 23)
            {
                throw new InvalidOperationException($"SetSchedule: Hour must be 0-23, got {hour}");
            }

            if (string.IsNullOrEmpty(assignment))
            {
                throw new InvalidOperationException("SetSchedule: Assignment is required. Valid: Work, Sleep, Joy, Anything");
            }

            TimeAssignmentDef? assignmentDef = assignment.ToLowerInvariant() switch
            {
                "work" => TimeAssignmentDefOf.Work,
                "sleep" => TimeAssignmentDefOf.Sleep,
                "joy" or "recreation" => TimeAssignmentDefOf.Joy,
                "anything" or "any" => TimeAssignmentDefOf.Anything,
                _ => null
            };

            if (assignmentDef == null)
            {
                throw new InvalidOperationException($"SetSchedule: Unknown assignment '{assignment}'. Valid: Work, Sleep, Joy, Anything");
            }

            pawn.timetable.SetAssignment(hour, assignmentDef);
            Log.Message($"[GameRL] SetSchedule: {pawn.LabelShort} hour {hour} set to {assignmentDef.defName}");
        }

        /// <summary>
        /// Assign a colonist to a specific bed
        /// </summary>
        [GameRLAction("AssignBed", Description = "Assign a colonist to a specific bed")]
        public static void AssignBed(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("BedId")] Building building)
        {
            if (pawn == null)
            {
                throw new InvalidOperationException("AssignBed: ColonistId not found. Use a ThingID from Entities.Colonists");
            }

            if (building == null)
            {
                throw new InvalidOperationException("AssignBed: BedId not found. Use a ThingID from Entities.Buildings for beds");
            }

            var bed = building as Building_Bed;
            if (bed == null)
            {
                throw new InvalidOperationException($"AssignBed: {building.LabelShort} ({building.ThingID}) is not a bed");
            }

            if (bed.ForPrisoners)
            {
                throw new InvalidOperationException($"AssignBed: {bed.LabelShort} ({bed.ThingID}) is a prisoner bed");
            }

            if (bed.Medical)
            {
                throw new InvalidOperationException($"AssignBed: {bed.LabelShort} ({bed.ThingID}) is a medical bed");
            }

            if (pawn.ownership == null)
            {
                throw new InvalidOperationException($"AssignBed: {pawn.LabelShort} ({pawn.ThingID}) has no ownership tracker");
            }

            pawn.ownership.ClaimBedIfNonMedical(bed);
            Log.Message($"[GameRL] AssignBed: {pawn.LabelShort} assigned to {bed.LabelShort} at ({bed.Position.x},{bed.Position.z})");
        }

        /// <summary>
        /// Schedule a surgery/operation on a pawn
        /// </summary>
        [GameRLAction("OperateSurgery", Description = "Schedule a surgery on a pawn (e.g., install peg leg, remove organ)")]
        public static void OperateSurgery(
            [GameRLParam("PatientId")] Pawn patient,
            [GameRLParam("Recipe")] string recipeDefName,
            [GameRLParam("BodyPart")] string? bodyPartLabel = null)
        {
            if (patient == null)
            {
                throw new InvalidOperationException("OperateSurgery: PatientId not found. Use a ThingID from Entities.Colonists");
            }

            if (string.IsNullOrEmpty(recipeDefName))
            {
                throw new InvalidOperationException("OperateSurgery: Recipe is required. Examples: InstallPegLeg, InstallDenture, RemoveBodyPart, ExciseCarcinoma");
            }

            var recipeDef = DefDatabase<RecipeDef>.GetNamed(recipeDefName, errorOnFail: false);
            if (recipeDef == null)
            {
                throw new InvalidOperationException($"OperateSurgery: Unknown recipe '{recipeDefName}'");
            }

            if (!recipeDef.IsSurgery)
            {
                throw new InvalidOperationException($"OperateSurgery: '{recipeDefName}' is not a surgery recipe");
            }

            if (patient.Dead || patient.Destroyed)
            {
                throw new InvalidOperationException($"OperateSurgery: {patient.LabelShort} is dead or destroyed");
            }

            // Find body part if specified
            BodyPartRecord? bodyPart = null;
            if (!string.IsNullOrEmpty(bodyPartLabel))
            {
                bodyPart = patient.RaceProps.body.AllParts
                    .FirstOrDefault(p => p.Label.Equals(bodyPartLabel, StringComparison.OrdinalIgnoreCase)
                                      || p.def.defName.Equals(bodyPartLabel, StringComparison.OrdinalIgnoreCase));
                if (bodyPart == null)
                {
                    var validParts = recipeDef.appliedOnFixedBodyParts?
                        .SelectMany(bpd => patient.RaceProps.body.AllParts.Where(p => p.def == bpd))
                        .Select(p => p.Label)
                        .Take(5) ?? Enumerable.Empty<string>();
                    throw new InvalidOperationException($"OperateSurgery: Body part '{bodyPartLabel}' not found. Valid parts for {recipeDefName}: {string.Join(", ", validParts)}");
                }
            }
            else if (recipeDef.appliedOnFixedBodyParts?.Count > 0)
            {
                // Auto-select first available body part
                bodyPart = recipeDef.appliedOnFixedBodyParts
                    .SelectMany(bpd => patient.RaceProps.body.AllParts.Where(p => p.def == bpd))
                    .FirstOrDefault();
            }

            var bill = new Bill_Medical(recipeDef, null);
            if (bodyPart != null)
            {
                bill.Part = bodyPart;
            }

            patient.BillStack.AddBill(bill);
            var partDesc = bodyPart != null ? $" on {bodyPart.Label}" : "";
            Log.Message($"[GameRL] OperateSurgery: Scheduled {recipeDefName}{partDesc} for {patient.LabelShort}");
        }

        /// <summary>
        /// Bury a corpse in an available grave
        /// </summary>
        [GameRLAction("Bury", Description = "Have a colonist bury a corpse in an available grave")]
        public static void Bury(
            [GameRLParam("ColonistId")] Pawn hauler,
            [GameRLParam("CorpseId")] Thing corpse)
        {
            if (hauler == null)
                throw new InvalidOperationException("Bury: ColonistId not found. Use a ThingID from Entities.Colonists");
            if (corpse == null)
                throw new InvalidOperationException("Bury: CorpseId not found. Use a ThingID from Entities.Corpses");

            if (corpse is not Corpse)
                throw new InvalidOperationException($"Bury: {corpse.LabelShort} ({corpse.ThingID}) is not a corpse");

            if (hauler.Downed)
                throw new InvalidOperationException($"Bury: {hauler.LabelShort} is downed and cannot haul");

            var map = hauler.Map ?? Find.CurrentMap;
            if (map == null)
                throw new InvalidOperationException("Bury: No map available");

            // Find an empty grave that accepts this corpse
            var grave = map.listerBuildings.allBuildingsColonist
                .OfType<Building_Grave>()
                .FirstOrDefault(g => !g.HasAnyContents && g.Accepts(corpse));

            if (grave == null)
                throw new InvalidOperationException("Bury: No empty grave available. Build a Grave first with PlaceBlueprint.");

            var job = JobMaker.MakeJob(JobDefOf.HaulToContainer, corpse, (Thing)grave);
            job.count = 1;
            hauler.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] Bury: {hauler.LabelShort} burying {corpse.LabelShort} in grave at ({grave.Position.x},{grave.Position.z})");
        }

        /// <summary>
        /// Open a container (cryptosleep casket, etc.)
        /// </summary>
        [GameRLAction("OpenCasket", Description = "Have a colonist open a cryptosleep casket or container")]
        public static void OpenCasket(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("BuildingId")] Building building)
        {
            if (pawn == null)
                throw new InvalidOperationException("OpenCasket: ColonistId not found. Use a ThingID from Entities.Colonists");
            if (building == null)
                throw new InvalidOperationException("OpenCasket: BuildingId not found. Use a ThingID from Entities.Buildings (e.g., AncientCryptosleepCasket123)");

            if (pawn.Downed)
                throw new InvalidOperationException($"OpenCasket: {pawn.LabelShort} is downed");

            if (building is not Building_Casket casket)
                throw new InvalidOperationException($"OpenCasket: {building.LabelShort} ({building.ThingID}) is not a casket/container");

            if (!casket.HasAnyContents)
                throw new InvalidOperationException($"OpenCasket: {building.LabelShort} ({building.ThingID}) is empty");

            var job = JobMaker.MakeJob(JobDefOf.Open, building);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] OpenCasket: {pawn.LabelShort} opening {building.LabelShort} at ({building.Position.x},{building.Position.z})");
        }

        /// <summary>
        /// Form a caravan with specified colonists
        /// </summary>
        [GameRLAction("FormCaravan", Description = "Form a caravan with specified colonists to travel on the world map")]
        public static void FormCaravan(
            [GameRLParam("ColonistIds")] string colonistIdsCommaSeparated,
            [GameRLParam("DestinationTile")] int destinationTile)
        {
            if (string.IsNullOrEmpty(colonistIdsCommaSeparated))
            {
                throw new InvalidOperationException("FormCaravan: ColonistIds is required. Comma-separated ThingIDs from Entities.Colonists");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("FormCaravan: No map loaded");
            }

            var ids = colonistIdsCommaSeparated.Split(',').Select(s => s.Trim()).ToList();
            var pawns = new List<Pawn>();

            foreach (var id in ids)
            {
                var pawn = map.mapPawns.FreeColonists.FirstOrDefault(p => p.ThingID == id);
                if (pawn == null)
                {
                    throw new InvalidOperationException($"FormCaravan: Colonist '{id}' not found");
                }
                if (pawn.Downed)
                {
                    throw new InvalidOperationException($"FormCaravan: {pawn.LabelShort} ({id}) is downed and cannot travel");
                }
                pawns.Add(pawn);
            }

            if (pawns.Count == 0)
            {
                throw new InvalidOperationException("FormCaravan: No valid colonists specified");
            }

            if (destinationTile < 0)
            {
                throw new InvalidOperationException($"FormCaravan: Invalid destination tile {destinationTile}");
            }

            try
            {
                // Use reflection to call StartFormingCaravan since the API signature varies
                var method = typeof(CaravanFormingUtility).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "StartFormingCaravan");

                if (method != null)
                {
                    var paramInfos = method.GetParameters();
                    // Build arguments dynamically based on the method signature
                    var args = new object[paramInfos.Length];
                    for (int i = 0; i < paramInfos.Length; i++)
                    {
                        var paramType = paramInfos[i].ParameterType;
                        if (paramType == typeof(List<Pawn>) || paramType.IsAssignableFrom(typeof(List<Pawn>)))
                            args[i] = pawns;
                        else if (paramType == typeof(Faction))
                            args[i] = Faction.OfPlayer;
                        else if (paramType == typeof(int))
                            args[i] = paramInfos[i].Name.Contains("dest") ? destinationTile : map.Tile;
                        else if (paramType == typeof(bool))
                            args[i] = false;
                        else
                            args[i] = paramInfos[i].HasDefaultValue ? paramInfos[i].DefaultValue! : null!;
                    }
                    method.Invoke(null, args);
                    Log.Message($"[GameRL] FormCaravan: Forming caravan with {pawns.Count} colonists to tile {destinationTile}");
                }
                else
                {
                    throw new InvalidOperationException("FormCaravan: CaravanFormingUtility.StartFormingCaravan not found");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"FormCaravan: Failed - {ex.Message}");
            }
        }
    }
}
