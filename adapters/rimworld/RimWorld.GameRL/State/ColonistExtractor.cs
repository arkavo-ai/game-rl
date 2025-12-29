// Extract colonist state from RimWorld

using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using MessagePack;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Colonist state for observations
    /// </summary>
    [MessagePackObject]
    public class ColonistState
    {
        [Key("id")]
        public string Id { get; set; } = "";

        [Key("name")]
        public string Name { get; set; } = "";

        [Key("position")]
        public float[] Position { get; set; } = new float[2];

        [Key("health")]
        public float Health { get; set; }

        [Key("mood")]
        public float Mood { get; set; }

        [Key("hunger")]
        public float Hunger { get; set; }

        [Key("rest")]
        public float Rest { get; set; }

        [Key("current_job")]
        public string? CurrentJob { get; set; }

        [Key("is_drafted")]
        public bool IsDrafted { get; set; }

        [Key("needs")]
        public Dictionary<string, float> Needs { get; set; } = new();

        [Key("skills")]
        public Dictionary<string, int> Skills { get; set; } = new();

        [Key("work_priorities")]
        public Dictionary<string, int> WorkPriorities { get; set; } = new();
    }

    /// <summary>
    /// Extracts colonist information from the game
    /// </summary>
    public static class ColonistExtractor
    {
        public static List<ColonistState> Extract(Map? map)
        {
            if (map == null)
                return new List<ColonistState>();

            return map.mapPawns.FreeColonists
                .Select(ExtractPawn)
                .ToList();
        }

        private static ColonistState ExtractPawn(Pawn pawn)
        {
            return new ColonistState
            {
                Id = pawn.ThingID,
                Name = pawn.Name?.ToStringFull ?? "Unknown",
                Position = new[] { (float)pawn.Position.x, (float)pawn.Position.z },
                Health = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Mood = pawn.needs?.mood?.CurLevelPercentage ?? 0.5f,
                Hunger = 1f - (pawn.needs?.food?.CurLevelPercentage ?? 1f),
                Rest = pawn.needs?.rest?.CurLevelPercentage ?? 1f,
                CurrentJob = pawn.CurJob?.def?.defName,
                IsDrafted = pawn.Drafted,
                Needs = ExtractNeeds(pawn),
                Skills = ExtractSkills(pawn),
                WorkPriorities = ExtractWorkPriorities(pawn)
            };
        }

        private static Dictionary<string, float> ExtractNeeds(Pawn pawn)
        {
            var needs = new Dictionary<string, float>();
            if (pawn.needs == null) return needs;

            foreach (var need in pawn.needs.AllNeeds)
            {
                needs[need.def.defName] = need.CurLevelPercentage;
            }
            return needs;
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
    }
}
