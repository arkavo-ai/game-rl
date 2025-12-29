// Map-level actions for RimWorld GameRL - designations, zones, blueprints

using System;
using System.Linq;
using GameRL.Harmony.RPC;
using Verse;
using RimWorld;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Map-level actions (designations, zones, construction)
    /// </summary>
    [GameRLComponent]
    public static class MapActions
    {
        /// <summary>
        /// Designate an animal for hunting
        /// </summary>
        [GameRLAction("designate_hunt", Description = "Mark an animal for hunting")]
        public static void DesignateHunt([GameRLParam("target_id")] Thing target)
        {
            if (target == null)
            {
                Log.Warning("[GameRL] designate_hunt: Target not found");
                return;
            }

            var pawn = target as Pawn;
            if (pawn == null || !pawn.RaceProps.Animal)
            {
                Log.Warning($"[GameRL] designate_hunt: {target.ThingID} is not an animal");
                return;
            }

            var map = target.Map;
            if (map == null)
            {
                Log.Warning("[GameRL] designate_hunt: Target has no map");
                return;
            }

            // Check if already designated
            if (map.designationManager.DesignationOn(target, DesignationDefOf.Hunt) != null)
            {
                Log.Message($"[GameRL] designate_hunt: {target.LabelShort} already designated for hunting");
                return;
            }

            map.designationManager.AddDesignation(new Designation(target, DesignationDefOf.Hunt));
            Log.Message($"[GameRL] designate_hunt: Marked {target.LabelShort} for hunting");
        }

        /// <summary>
        /// Remove hunt designation from an animal
        /// </summary>
        [GameRLAction("cancel_hunt", Description = "Remove hunting designation from an animal")]
        public static void CancelHunt([GameRLParam("target_id")] Thing target)
        {
            if (target == null)
            {
                Log.Warning("[GameRL] cancel_hunt: Target not found");
                return;
            }

            var map = target.Map;
            if (map == null) return;

            var designation = map.designationManager.DesignationOn(target, DesignationDefOf.Hunt);
            if (designation != null)
            {
                map.designationManager.RemoveDesignation(designation);
                Log.Message($"[GameRL] cancel_hunt: Removed hunt designation from {target.LabelShort}");
            }
        }

        /// <summary>
        /// Place a building blueprint for construction
        /// </summary>
        [GameRLAction("place_blueprint", Description = "Place a building blueprint for construction")]
        public static void PlaceBlueprint(
            [GameRLParam("building")] string buildingDefName,
            int x,
            int z,
            int rotation = 0,
            [GameRLParam("stuff")] string? stuffDefName = null)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] place_blueprint: No current map");
                return;
            }

            var buildingDef = DefDatabase<ThingDef>.GetNamed(buildingDefName, errorOnFail: false);
            if (buildingDef == null)
            {
                Log.Warning($"[GameRL] place_blueprint: Unknown building: {buildingDefName}");
                return;
            }

            // Get stuff (material) if specified
            ThingDef? stuffDef = null;
            if (!string.IsNullOrEmpty(stuffDefName))
            {
                stuffDef = DefDatabase<ThingDef>.GetNamed(stuffDefName, errorOnFail: false);
            }
            else if (buildingDef.MadeFromStuff)
            {
                // Default to wood for stuff buildings
                stuffDef = ThingDefOf.WoodLog;
            }

            var pos = new IntVec3(x, 0, z);
            var rot = new Rot4(rotation);

            // Check if position is valid
            if (!pos.InBounds(map))
            {
                Log.Warning($"[GameRL] place_blueprint: Position {pos} out of bounds");
                return;
            }

            // Check if can place
            if (!GenConstruct.CanPlaceBlueprintAt(buildingDef, pos, rot, map, false, null, null, stuffDef).Accepted)
            {
                Log.Warning($"[GameRL] place_blueprint: Cannot place {buildingDefName} at {pos}");
                return;
            }

            GenConstruct.PlaceBlueprintForBuild(buildingDef, pos, map, rot, Faction.OfPlayer, stuffDef);
            Log.Message($"[GameRL] place_blueprint: Placed {buildingDefName} blueprint at {pos}");
        }

        /// <summary>
        /// Create a growing zone
        /// </summary>
        [GameRLAction("create_growing_zone", Description = "Create a growing zone for farming")]
        public static void CreateGrowingZone(
            int x,
            int z,
            int width = 5,
            int height = 5,
            [GameRLParam("plant")] string? plantDefName = null)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] create_growing_zone: No current map");
                return;
            }

            var cells = new System.Collections.Generic.List<IntVec3>();
            for (int dx = 0; dx < width; dx++)
            {
                for (int dz = 0; dz < height; dz++)
                {
                    var cell = new IntVec3(x + dx, 0, z + dz);
                    if (cell.InBounds(map) && cell.GetTerrain(map).fertility > 0)
                    {
                        cells.Add(cell);
                    }
                }
            }

            if (cells.Count == 0)
            {
                Log.Warning($"[GameRL] create_growing_zone: No fertile cells at ({x},{z}) {width}x{height}");
                return;
            }

            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);

            foreach (var cell in cells)
            {
                zone.AddCell(cell);
            }

            // Set plant type if specified
            if (!string.IsNullOrEmpty(plantDefName))
            {
                var plantDef = DefDatabase<ThingDef>.GetNamed(plantDefName, errorOnFail: false);
                if (plantDef != null)
                {
                    zone.SetPlantDefToGrow(plantDef);
                    Log.Message($"[GameRL] create_growing_zone: Created {cells.Count} cell zone for {plantDefName}");
                }
                else
                {
                    Log.Warning($"[GameRL] create_growing_zone: Unknown plant: {plantDefName}");
                }
            }
            else
            {
                Log.Message($"[GameRL] create_growing_zone: Created {cells.Count} cell zone");
            }
        }

        /// <summary>
        /// Create a stockpile zone
        /// </summary>
        [GameRLAction("create_stockpile", Description = "Create a stockpile zone for storage")]
        public static void CreateStockpile(
            int x,
            int z,
            int width = 5,
            int height = 5)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] create_stockpile: No current map");
                return;
            }

            var cells = new System.Collections.Generic.List<IntVec3>();
            for (int dx = 0; dx < width; dx++)
            {
                for (int dz = 0; dz < height; dz++)
                {
                    var cell = new IntVec3(x + dx, 0, z + dz);
                    if (cell.InBounds(map) && cell.Standable(map))
                    {
                        cells.Add(cell);
                    }
                }
            }

            if (cells.Count == 0)
            {
                Log.Warning($"[GameRL] create_stockpile: No valid cells at ({x},{z}) {width}x{height}");
                return;
            }

            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);

            foreach (var cell in cells)
            {
                zone.AddCell(cell);
            }

            Log.Message($"[GameRL] create_stockpile: Created {cells.Count} cell stockpile");
        }

        /// <summary>
        /// Designate trees for cutting
        /// </summary>
        [GameRLAction("designate_cut_plants", Description = "Designate plants/trees in an area for cutting")]
        public static void DesignateCutPlants(
            int x,
            int z,
            int radius = 5)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] designate_cut_plants: No current map");
                return;
            }

            var center = new IntVec3(x, 0, z);
            int count = 0;

            foreach (var cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) continue;

                var plant = cell.GetPlant(map);
                if (plant != null && plant.def.plant.IsTree)
                {
                    if (map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                        count++;
                    }
                }
            }

            Log.Message($"[GameRL] designate_cut_plants: Marked {count} trees for cutting around ({x},{z})");
        }

        /// <summary>
        /// Designate area for mining
        /// </summary>
        [GameRLAction("designate_mine", Description = "Designate rocks/ore in an area for mining")]
        public static void DesignateMine(
            int x,
            int z,
            int radius = 3)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] designate_mine: No current map");
                return;
            }

            var center = new IntVec3(x, 0, z);
            int count = 0;

            foreach (var cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) continue;

                var building = cell.GetFirstMineable(map);
                if (building != null)
                {
                    if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.Mine));
                        count++;
                    }
                }
            }

            Log.Message($"[GameRL] designate_mine: Marked {count} cells for mining around ({x},{z})");
        }

        /// <summary>
        /// Add a bill to a workbench (e.g., butcher creature at butcher spot)
        /// </summary>
        [GameRLAction("add_bill", Description = "Add a production bill to a workbench")]
        public static void AddBill(
            [GameRLParam("building_id")] Thing building,
            [GameRLParam("recipe")] string recipeDefName,
            int count = -1)
        {
            if (building == null)
            {
                Log.Warning("[GameRL] add_bill: Building not found");
                return;
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                Log.Warning($"[GameRL] add_bill: {building.ThingID} cannot accept bills");
                return;
            }

            var recipeDef = DefDatabase<RecipeDef>.GetNamed(recipeDefName, errorOnFail: false);
            if (recipeDef == null)
            {
                Log.Warning($"[GameRL] add_bill: Unknown recipe: {recipeDefName}");
                return;
            }

            // Check if recipe can be done at this building
            if (!recipeDef.AvailableOnNow(building, null))
            {
                Log.Warning($"[GameRL] add_bill: Recipe {recipeDefName} not available at {building.LabelShort}");
                return;
            }

            var bill = BillUtility.MakeNewBill(recipeDef);
            if (bill is Bill_Production prodBill)
            {
                if (count > 0)
                {
                    prodBill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                    prodBill.repeatCount = count;
                }
                else
                {
                    prodBill.repeatMode = BillRepeatModeDefOf.Forever;
                }
            }

            billGiver.BillStack.AddBill(bill);
            Log.Message($"[GameRL] add_bill: Added {recipeDefName} to {building.LabelShort}");
        }

        /// <summary>
        /// List all buildings that can accept bills (workbenches, production spots)
        /// </summary>
        [GameRLAction("list_workbenches", Description = "List all workbenches and production buildings")]
        public static string ListWorkbenches()
        {
            var map = Find.CurrentMap;
            if (map == null) return "No map";

            var result = new System.Text.StringBuilder();
            var workbenches = map.listerBuildings.allBuildingsColonist
                .Where(b => b is IBillGiver)
                .ToList();

            foreach (var wb in workbenches)
            {
                var billGiver = wb as IBillGiver;
                var recipes = wb.def.AllRecipes?.Select(r => r.defName) ?? Enumerable.Empty<string>();
                result.AppendLine($"{wb.ThingID}: {wb.LabelShort} at ({wb.Position.x},{wb.Position.z})");
                result.AppendLine($"  Recipes: {string.Join(", ", recipes.Take(5))}...");
            }

            Log.Message($"[GameRL] list_workbenches: Found {workbenches.Count} workbenches");
            return result.ToString();
        }
    }
}
