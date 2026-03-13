// Extract colonist state from RimWorld

using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// 2D position with explicit X/Y coordinates for LLM clarity
    /// </summary>
    public class Position2D
    {
        public int X { get; set; }
        public int Y { get; set; }

        public Position2D() { }
        public Position2D(int x, int y) { X = x; Y = y; }
    }

    /// <summary>
    /// Need state with value and human-readable status
    /// </summary>
    public class NeedState
    {
        public float Value { get; set; }
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// Colonist state for observations
    /// </summary>
    public class ColonistState
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public Position2D Position { get; set; } = new();

        public float Health { get; set; }

        public float Mood { get; set; }

        public float Hunger { get; set; }

        public float Rest { get; set; }

        public string? CurrentJob { get; set; }

        public bool IsDrafted { get; set; }

        public bool IsDowned { get; set; }

        public bool IsSleeping { get; set; }

        public string? MentalState { get; set; }

        public bool CanBeDrafted { get; set; }

        public string? Weapon { get; set; }

        public bool HasRangedWeapon { get; set; }

        public Dictionary<string, NeedState> Needs { get; set; } = new();

        public Dictionary<string, int> Skills { get; set; } = new();

        public Dictionary<string, int> WorkPriorities { get; set; } = new();

        public List<string> Traits { get; set; } = new();

        public List<HediffInfo> Injuries { get; set; } = new();

        public List<SocialRelation> Relations { get; set; } = new();

        public Dictionary<string, string> SkillPassions { get; set; } = new();

        public List<string> DisabledWorkTypes { get; set; } = new();

        public List<ApparelInfo> Apparel { get; set; } = new();

        /// <summary>
        /// Schedule assignments by hour (0-23). Values: Work, Sleep, Joy, Anything
        /// </summary>
        public Dictionary<int, string>? Schedule { get; set; }
    }

    /// <summary>
    /// Worn apparel with protection stats
    /// </summary>
    public class ApparelInfo
    {
        public string DefName { get; set; } = "";
        public string Label { get; set; } = "";
        public float HitPointsPercent { get; set; }
        public float InsulationCold { get; set; }
        public float InsulationHeat { get; set; }
        public float ArmorSharp { get; set; }
        public float ArmorBlunt { get; set; }
    }

    /// <summary>
    /// Social relation between two colonists
    /// </summary>
    public class SocialRelation
    {
        public string OtherPawnId { get; set; } = "";
        public string RelationType { get; set; } = "";
        public int Opinion { get; set; }
    }

    /// <summary>
    /// Health condition (injury, disease, implant, etc.)
    /// </summary>
    public class HediffInfo
    {
        public string DefName { get; set; } = "";
        public string Label { get; set; } = "";
        public string? BodyPart { get; set; }
        public float Severity { get; set; }
        public bool LifeThreatening { get; set; }
        public bool Bleeding { get; set; }
    }

    /// <summary>
    /// Extracts colonist information from the game
    /// </summary>
    public static class ColonistExtractor
    {
        public static List<ColonistState> Extract(Map? map, EntityIndex? entities = null)
        {
            if (map == null)
                return new List<ColonistState>();

            // Take snapshot to avoid collection modification during iteration
            List<Pawn> colonistSnapshot;
            try
            {
                colonistSnapshot = map.mapPawns.FreeColonists.ToList();
            }
            catch
            {
                return new List<ColonistState>();
            }

            var result = new List<ColonistState>();
            foreach (var pawn in colonistSnapshot)
            {
                if (pawn == null || pawn.Destroyed || !pawn.Spawned) continue;

                try
                {
                    result.Add(ExtractPawn(pawn));
                }
                catch
                {
                    // Skip pawns that throw during extraction
                }
            }
            return result;
        }

        private static ColonistState ExtractPawn(Pawn pawn)
        {
            return new ColonistState
            {
                Id = pawn.ThingID,
                Name = pawn.Name?.ToStringFull ?? "Unknown",
                Position = new Position2D { X = pawn.Position.x, Y = pawn.Position.z },
                Health = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Mood = pawn.needs?.mood?.CurLevelPercentage ?? 0.5f,
                Hunger = 1f - (pawn.needs?.food?.CurLevelPercentage ?? 1f),
                Rest = pawn.needs?.rest?.CurLevelPercentage ?? 1f,
                CurrentJob = pawn.CurJob?.def?.defName,
                IsDrafted = pawn.Drafted,
                IsDowned = pawn.Downed,
                IsSleeping = pawn.CurJob?.def == JobDefOf.LayDown,
                MentalState = pawn.MentalState?.def?.defName,
                CanBeDrafted = pawn.drafter != null && !pawn.Downed && !pawn.InMentalState,
                Weapon = pawn.equipment?.Primary?.def?.defName,
                HasRangedWeapon = pawn.equipment?.Primary?.def?.IsRangedWeapon ?? false,
                Needs = ExtractNeeds(pawn),
                Skills = ExtractSkills(pawn),
                WorkPriorities = ExtractWorkPriorities(pawn),
                Traits = ExtractTraits(pawn),
                Injuries = ExtractInjuries(pawn),
                Relations = ExtractRelations(pawn),
                SkillPassions = ExtractSkillPassions(pawn),
                DisabledWorkTypes = ExtractDisabledWorkTypes(pawn),
                Apparel = ExtractApparel(pawn),
                Schedule = ExtractSchedule(pawn)
            };
        }

        private static Dictionary<int, string>? ExtractSchedule(Pawn pawn)
        {
            if (pawn.timetable == null) return null;
            var schedule = new Dictionary<int, string>();
            for (int hour = 0; hour < 24; hour++)
            {
                var assignment = pawn.timetable.GetAssignment(hour);
                schedule[hour] = assignment?.defName ?? "Anything";
            }
            return schedule;
        }

        private static Dictionary<string, NeedState> ExtractNeeds(Pawn pawn)
        {
            var needs = new Dictionary<string, NeedState>();
            if (pawn.needs == null) return needs;

            foreach (var need in pawn.needs.AllNeeds)
            {
                var value = need.CurLevelPercentage;
                needs[need.def.defName] = new NeedState
                {
                    Value = value,
                    Status = GetNeedStatus(need.def.defName, value)
                };
            }
            return needs;
        }

        private static string GetNeedStatus(string needName, float value)
        {
            return needName switch
            {
                "Food" => value switch
                {
                    < 0.1f => "Starving",
                    < 0.3f => "Hungry",
                    < 0.8f => "Fed",
                    _ => "Full"
                },
                "Rest" => value switch
                {
                    < 0.1f => "Exhausted",
                    < 0.3f => "Tired",
                    < 0.8f => "Rested",
                    _ => "WellRested"
                },
                "Joy" => value switch
                {
                    < 0.15f => "Miserable",
                    < 0.35f => "Bored",
                    < 0.7f => "Content",
                    _ => "Happy"
                },
                "Mood" => value switch
                {
                    < 0.15f => "Breaking",
                    < 0.35f => "Stressed",
                    < 0.65f => "Content",
                    _ => "Happy"
                },
                _ => value switch
                {
                    < 0.25f => "Critical",
                    < 0.5f => "Low",
                    < 0.75f => "Moderate",
                    _ => "Good"
                }
            };
        }

        private static Dictionary<string, int> ExtractSkills(Pawn pawn)
        {
            var skills = new Dictionary<string, int>();
            if (pawn.skills == null) return skills;

            foreach (var skill in pawn.skills.skills)
            {
                skills[skill.def.defName] = skill.Level;
            }
            return skills;
        }

        private static List<string> ExtractTraits(Pawn pawn)
        {
            var traits = new List<string>();
            if (pawn.story?.traits == null) return traits;

            try
            {
                foreach (var trait in pawn.story.traits.allTraits)
                {
                    if (trait?.def != null)
                    {
                        traits.Add(trait.CurrentData?.label ?? trait.def.defName);
                    }
                }
            }
            catch
            {
                // Return partial results
            }
            return traits;
        }

        private static List<HediffInfo> ExtractInjuries(Pawn pawn)
        {
            var injuries = new List<HediffInfo>();
            if (pawn.health?.hediffSet == null) return injuries;

            try
            {
                foreach (var hediff in pawn.health.hediffSet.hediffs)
                {
                    if (hediff == null || !hediff.Visible) continue;

                    injuries.Add(new HediffInfo
                    {
                        DefName = hediff.def.defName,
                        Label = hediff.LabelCap,
                        BodyPart = hediff.Part?.Label,
                        Severity = hediff.Severity,
                        LifeThreatening = hediff.CurStage?.lifeThreatening ?? false,
                        Bleeding = hediff.Bleeding
                    });
                }
            }
            catch
            {
                // Return partial results
            }
            return injuries;
        }

        private static List<SocialRelation> ExtractRelations(Pawn pawn)
        {
            var relations = new List<SocialRelation>();
            if (pawn.relations == null) return relations;

            try
            {
                // Get direct relationships (spouse, child, lover, rival, etc.)
                foreach (var rel in pawn.relations.DirectRelations)
                {
                    if (rel?.otherPawn == null || rel.otherPawn.Destroyed) continue;

                    var opinion = 0;
                    try { opinion = pawn.relations.OpinionOf(rel.otherPawn); } catch { }

                    relations.Add(new SocialRelation
                    {
                        OtherPawnId = rel.otherPawn.ThingID,
                        RelationType = rel.def?.defName ?? "Unknown",
                        Opinion = opinion
                    });
                }
            }
            catch
            {
                // Return partial results
            }
            return relations;
        }

        private static Dictionary<string, int> ExtractWorkPriorities(Pawn pawn)
        {
            var priorities = new Dictionary<string, int>();
            if (pawn.workSettings == null) return priorities;

            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (!pawn.WorkTypeIsDisabled(workType))
                {
                    priorities[workType.defName] = pawn.workSettings.GetPriority(workType);
                }
            }
            return priorities;
        }

        private static Dictionary<string, string> ExtractSkillPassions(Pawn pawn)
        {
            var passions = new Dictionary<string, string>();
            if (pawn.skills == null) return passions;

            foreach (var skill in pawn.skills.skills)
            {
                passions[skill.def.defName] = skill.passion.ToString();
            }
            return passions;
        }

        private static List<string> ExtractDisabledWorkTypes(Pawn pawn)
        {
            var disabled = new List<string>();
            if (pawn.story == null) return disabled;

            try
            {
                foreach (var workType in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (pawn.WorkTypeIsDisabled(workType))
                    {
                        disabled.Add(workType.defName);
                    }
                }
            }
            catch { }
            return disabled;
        }

        private static List<ApparelInfo> ExtractApparel(Pawn pawn)
        {
            var apparel = new List<ApparelInfo>();
            if (pawn.apparel?.WornApparel == null) return apparel;

            try
            {
                foreach (var a in pawn.apparel.WornApparel)
                {
                    apparel.Add(new ApparelInfo
                    {
                        DefName = a.def.defName,
                        Label = a.LabelShort,
                        HitPointsPercent = (float)a.HitPoints / a.MaxHitPoints,
                        InsulationCold = a.GetStatValue(StatDefOf.Insulation_Cold),
                        InsulationHeat = a.GetStatValue(StatDefOf.Insulation_Heat),
                        ArmorSharp = a.GetStatValue(StatDefOf.ArmorRating_Sharp),
                        ArmorBlunt = a.GetStatValue(StatDefOf.ArmorRating_Blunt)
                    });
                }
            }
            catch { }
            return apparel;
        }
    }
}
