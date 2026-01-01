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
        [GameRLAction("DesignateHunt", Description = "Mark an animal for hunting")]
        public static void DesignateHunt([GameRLParam("TargetId")] Thing target)
        {
            if (target == null)
            {
                Log.Warning("[GameRL] DesignateHunt: TargetId not found. Use a ThingID from Entities.Animals (e.g., 'Deer123')");
                return;
            }

            var pawn = target as Pawn;
            if (pawn == null || !pawn.RaceProps.Animal)
            {
                Log.Warning($"[GameRL] DesignateHunt: {target.LabelShort} ({target.ThingID}) is not an animal. Only animals can be hunted.");
                return;
            }

            var map = target.Map;
            if (map == null)
            {
                Log.Warning($"[GameRL] DesignateHunt: {target.LabelShort} ({target.ThingID}) is not on the map");
                return;
            }

            // Check if already designated
            if (map.designationManager.DesignationOn(target, DesignationDefOf.Hunt) != null)
            {
                Log.Message($"[GameRL] DesignateHunt: {target.LabelShort} ({target.ThingID}) is already designated for hunting");
                return;
            }

            map.designationManager.AddDesignation(new Designation(target, DesignationDefOf.Hunt));
            Log.Message($"[GameRL] DesignateHunt: Marked {target.LabelShort} for hunting");
        }

        /// <summary>
        /// Remove hunt designation from an animal
        /// </summary>
        [GameRLAction("CancelHunt", Description = "Remove hunting designation from an animal")]
        public static void CancelHunt([GameRLParam("TargetId")] Thing target)
        {
            if (target == null)
            {
                Log.Warning("[GameRL] CancelHunt: TargetId not found. Use a ThingID from Entities.Animals");
                return;
            }

            var map = target.Map;
            if (map == null)
            {
                Log.Warning($"[GameRL] CancelHunt: {target.LabelShort} ({target.ThingID}) is not on the map");
                return;
            }

            var designation = map.designationManager.DesignationOn(target, DesignationDefOf.Hunt);
            if (designation != null)
            {
                map.designationManager.RemoveDesignation(designation);
                Log.Message($"[GameRL] CancelHunt: Removed hunt designation from {target.LabelShort}");
            }
            else
            {
                Log.Warning($"[GameRL] CancelHunt: {target.LabelShort} ({target.ThingID}) is not designated for hunting");
            }
        }

        /// <summary>
        /// Place a building blueprint for construction
        /// </summary>
        [GameRLAction("PlaceBlueprint", Description = "Place a building blueprint for construction")]
        public static void PlaceBlueprint(
            [GameRLParam("Building")] string buildingDefName,
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Rotation")] int rotation = 0,
            [GameRLParam("Stuff")] string? stuffDefName = null)
        {
            if (string.IsNullOrEmpty(buildingDefName))
            {
                Log.Warning("[GameRL] PlaceBlueprint: Building parameter is required. Examples: Bed, ButcherSpot, Campfire, CookStove, ResearchBench, Table2x2c, DiningChair, StandingLamp");
                return;
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] PlaceBlueprint: No map is currently loaded");
                return;
            }

            var buildingDef = DefDatabase<ThingDef>.GetNamed(buildingDefName, errorOnFail: false);
            if (buildingDef == null)
            {
                Log.Warning($"[GameRL] PlaceBlueprint: Unknown building '{buildingDefName}'. Examples: Bed, ButcherSpot, Campfire, CookStove, ResearchBench, Sandbags, Wall, Door");
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
                Log.Warning($"[GameRL] PlaceBlueprint: Position ({x},{z}) is out of map bounds. Map size is {map.Size.x}x{map.Size.z}");
                return;
            }

            // Check if can place
            var canPlace = GenConstruct.CanPlaceBlueprintAt(buildingDef, pos, rot, map, false, null, null, stuffDef);
            if (!canPlace.Accepted)
            {
                Log.Warning($"[GameRL] PlaceBlueprint: Cannot place {buildingDefName} at ({x},{z}). Reason: {canPlace.Reason ?? "blocked or invalid terrain"}");
                return;
            }

            // Check if this is a "spot" type building with no construction cost - place instantly
            bool isInstantBuild = (buildingDef.costList == null || buildingDef.costList.Count == 0)
                && buildingDef.costStuffCount <= 0;

            if (isInstantBuild)
            {
                // Spawn the building directly (for ButcherSpot, Campfire, SleepingSpot, etc.)
                var building = ThingMaker.MakeThing(buildingDef, stuffDef);
                building.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(building, pos, map, rot);
                Log.Message($"[GameRL] PlaceBlueprint: Placed {buildingDefName} instantly at ({x},{z}) - no construction needed");
            }
            else
            {
                // Normal construction - create blueprint
                GenConstruct.PlaceBlueprintForBuild(buildingDef, pos, map, rot, Faction.OfPlayer, stuffDef);
                Log.Message($"[GameRL] PlaceBlueprint: Placed {buildingDefName} blueprint at ({x},{z}) - colonists will construct when resources available");
            }
        }

        /// <summary>
        /// Create a growing zone
        /// </summary>
        [GameRLAction("CreateGrowingZone", Description = "Create a growing zone for farming")]
        public static void CreateGrowingZone(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Width")] int width = 5,
            [GameRLParam("Height")] int height = 5,
            [GameRLParam("Plant")] string? plantDefName = null)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] CreateGrowingZone: No map is currently loaded");
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
                Log.Warning($"[GameRL] CreateGrowingZone: No fertile cells at ({x},{z}) with size {width}x{height}. Growing zones require fertile soil.");
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
                    Log.Message($"[GameRL] CreateGrowingZone: Created {cells.Count} cell zone for {plantDefName} at ({x},{z})");
                }
                else
                {
                    Log.Warning($"[GameRL] CreateGrowingZone: Unknown plant '{plantDefName}'. Examples: Plant_Potato, Plant_Rice, Plant_Corn, Plant_Healroot, Plant_Cotton");
                }
            }
            else
            {
                Log.Message($"[GameRL] CreateGrowingZone: Created {cells.Count} cell zone at ({x},{z})");
            }
        }

        /// <summary>
        /// Create a stockpile zone
        /// </summary>
        [GameRLAction("CreateStockpile", Description = "Create a stockpile zone for storage")]
        public static void CreateStockpile(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Width")] int width = 5,
            [GameRLParam("Height")] int height = 5)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] CreateStockpile: No map is currently loaded");
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
                Log.Warning($"[GameRL] CreateStockpile: No valid cells at ({x},{z}) with size {width}x{height}. Stockpiles need standable terrain (not walls, water, or impassable).");
                return;
            }

            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);

            foreach (var cell in cells)
            {
                zone.AddCell(cell);
            }

            Log.Message($"[GameRL] CreateStockpile: Created {cells.Count} cell stockpile at ({x},{z})");
        }

        /// <summary>
        /// Designate trees for cutting
        /// </summary>
        [GameRLAction("DesignateCutPlants", Description = "Designate plants/trees in an area for cutting")]
        public static void DesignateCutPlants(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Radius")] int radius = 5)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] DesignateCutPlants: No map is currently loaded");
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

            if (count > 0)
            {
                Log.Message($"[GameRL] DesignateCutPlants: Marked {count} trees for cutting in radius {radius} around ({x},{z})");
            }
            else
            {
                Log.Warning($"[GameRL] DesignateCutPlants: No trees found in radius {radius} around ({x},{z})");
            }
        }

        /// <summary>
        /// Designate area for mining
        /// </summary>
        [GameRLAction("DesignateMine", Description = "Designate rocks/ore in an area for mining")]
        public static void DesignateMine(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Radius")] int radius = 3)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] DesignateMine: No map is currently loaded");
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

            if (count > 0)
            {
                Log.Message($"[GameRL] DesignateMine: Marked {count} cells for mining in radius {radius} around ({x},{z})");
            }
            else
            {
                Log.Warning($"[GameRL] DesignateMine: No mineable rocks found in radius {radius} around ({x},{z})");
            }
        }

        /// <summary>
        /// Add a bill to a workbench (e.g., butcher creature at butcher spot)
        /// </summary>
        [GameRLAction("AddBill", Description = "Add a production bill to a workbench")]
        public static void AddBill(
            [GameRLParam("BuildingId")] Thing building,
            [GameRLParam("Recipe")] string recipeDefName,
            [GameRLParam("Count")] int count = -1)
        {
            if (building == null)
            {
                Log.Warning("[GameRL] AddBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches like ButcherSpot, CookStove, etc.");
                return;
            }

            if (string.IsNullOrEmpty(recipeDefName))
            {
                Log.Warning("[GameRL] AddBill: Recipe is required. Examples: ButcherCorpseFlesh, Make_MealSimple, Make_MealFine, Make_Pemmican");
                return;
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                Log.Warning($"[GameRL] AddBill: {building.LabelShort} ({building.ThingID}) cannot accept bills. Use ListWorkbenches to find valid workbenches.");
                return;
            }

            var recipeDef = DefDatabase<RecipeDef>.GetNamed(recipeDefName, errorOnFail: false);
            if (recipeDef == null)
            {
                // List available recipes for this workbench
                var available = building.def.AllRecipes?.Take(5).Select(r => r.defName) ?? Enumerable.Empty<string>();
                Log.Warning($"[GameRL] AddBill: Unknown recipe '{recipeDefName}'. Available at {building.LabelShort}: {string.Join(", ", available)}");
                return;
            }

            // Check if recipe can be done at this building
            if (!recipeDef.AvailableOnNow(building, null))
            {
                var available = building.def.AllRecipes?.Take(5).Select(r => r.defName) ?? Enumerable.Empty<string>();
                Log.Warning($"[GameRL] AddBill: Recipe '{recipeDefName}' not available at {building.LabelShort}. Available: {string.Join(", ", available)}");
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
            var countDesc = count > 0 ? $"x{count}" : "forever";
            Log.Message($"[GameRL] AddBill: Added {recipeDefName} ({countDesc}) to {building.LabelShort}");
        }

        /// <summary>
        /// List all buildings that can accept bills (workbenches, production spots)
        /// </summary>
        [GameRLAction("ListWorkbenches", Description = "List all workbenches and production buildings")]
        public static string ListWorkbenches()
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] ListWorkbenches: No map is currently loaded");
                return "No map loaded";
            }

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

            if (workbenches.Count == 0)
            {
                Log.Warning("[GameRL] ListWorkbenches: No workbenches found. Build a ButcherSpot, CookStove, or other production building first.");
            }
            else
            {
                Log.Message($"[GameRL] ListWorkbenches: Found {workbenches.Count} workbenches");
            }
            return result.ToString();
        }

        /// <summary>
        /// Unforbid an item so colonists can interact with it
        /// </summary>
        [GameRLAction("Unforbid", Description = "Unforbid an item so colonists can use it")]
        public static void Unforbid([GameRLParam("ThingId")] Thing thing)
        {
            if (thing == null)
            {
                Log.Warning("[GameRL] Unforbid: ThingId not found. Use a ThingID from ForbiddenItems in Resources or from Entities.Items");
                return;
            }

            // Use SetForbidden which handles the check internally
            // This works for items, corpses, and anything that can be forbidden
            if (!thing.def.HasComp(typeof(CompForbiddable)) && thing.def.category != ThingCategory.Item)
            {
                Log.Warning($"[GameRL] Unforbid: {thing.LabelShort} ({thing.ThingID}) cannot be forbidden/unforbidden");
                return;
            }

            thing.SetForbidden(false, false);
            Log.Message($"[GameRL] Unforbid: Unforbid {thing.LabelShort} ({thing.ThingID})");
        }

        /// <summary>
        /// Unforbid all items in a radius
        /// </summary>
        [GameRLAction("UnforbidArea", Description = "Unforbid all items in a radius")]
        public static void UnforbidArea(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Radius")] int radius = 10)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] UnforbidArea: No map is currently loaded");
                return;
            }

            var center = new IntVec3(x, 0, z);
            int count = 0;

            foreach (var cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) continue;

                var things = cell.GetThingList(map);
                foreach (var thing in things.ToList())
                {
                    if (thing.def.category == ThingCategory.Item && thing.IsForbidden(Faction.OfPlayer))
                    {
                        thing.SetForbidden(false, false);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                Log.Message($"[GameRL] UnforbidArea: Unforbid {count} items in radius {radius} around ({x},{z})");
            }
            else
            {
                Log.Warning($"[GameRL] UnforbidArea: No forbidden items found in radius {radius} around ({x},{z})");
            }
        }

        /// <summary>
        /// Unforbid all items of a specific type (e.g., MealSurvivalPack)
        /// </summary>
        [GameRLAction("UnforbidByType", Description = "Unforbid all items of a specific type (e.g., MealSurvivalPack)")]
        public static void UnforbidByType([GameRLParam("DefName")] string defName)
        {
            if (string.IsNullOrEmpty(defName))
            {
                Log.Warning("[GameRL] UnforbidByType: DefName is required. Examples: MealSurvivalPack, Steel, WoodLog, InsectJelly, MedicineHerbal");
                return;
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                Log.Warning("[GameRL] UnforbidByType: No map is currently loaded");
                return;
            }

            var def = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
            if (def == null)
            {
                Log.Warning($"[GameRL] UnforbidByType: Unknown item type '{defName}'. Check ForbiddenItemCounts in Resources for valid DefNames.");
                return;
            }

            int count = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(def))
            {
                if (thing.IsForbidden(Faction.OfPlayer))
                {
                    thing.SetForbidden(false, false);
                    count++;
                }
            }

            if (count > 0)
            {
                Log.Message($"[GameRL] UnforbidByType: Unforbid {count} {defName}");
            }
            else
            {
                Log.Warning($"[GameRL] UnforbidByType: No forbidden {defName} found on the map");
            }
        }

        /// <summary>
        /// Remove a bill from a workbench
        /// </summary>
        [GameRLAction("CancelBill", Description = "Remove a bill from a workbench")]
        public static void CancelBill(
            [GameRLParam("BuildingId")] Thing building,
            [GameRLParam("BillIndex")] int billIndex = 0)
        {
            if (building == null)
            {
                Log.Warning("[GameRL] CancelBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches.");
                return;
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                Log.Warning($"[GameRL] CancelBill: {building.LabelShort} ({building.ThingID}) is not a workbench. Use ListWorkbenches to find valid workbenches.");
                return;
            }

            if (billGiver.BillStack.Count == 0)
            {
                Log.Warning($"[GameRL] CancelBill: {building.LabelShort} ({building.ThingID}) has no bills to cancel.");
                return;
            }

            if (billIndex < 0 || billIndex >= billGiver.BillStack.Count)
            {
                Log.Warning($"[GameRL] CancelBill: Bill index {billIndex} out of range. Valid range: 0-{billGiver.BillStack.Count - 1}");
                return;
            }

            var bill = billGiver.BillStack[billIndex];
            var recipeName = bill.recipe?.defName ?? "unknown";
            billGiver.BillStack.Delete(bill);
            Log.Message($"[GameRL] CancelBill: Removed bill {billIndex} ({recipeName}) from {building.LabelShort}");
        }

        /// <summary>
        /// Modify a bill's repeat count or mode
        /// </summary>
        [GameRLAction("ModifyBill", Description = "Modify a bill's repeat count or mode")]
        public static void ModifyBill(
            [GameRLParam("BuildingId")] Thing building,
            [GameRLParam("BillIndex")] int billIndex = 0,
            [GameRLParam("Count")] int? count = null,
            [GameRLParam("RepeatForever")] bool? repeatForever = null)
        {
            if (building == null)
            {
                Log.Warning("[GameRL] ModifyBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches.");
                return;
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                Log.Warning($"[GameRL] ModifyBill: {building.LabelShort} ({building.ThingID}) is not a workbench. Use ListWorkbenches to find valid workbenches.");
                return;
            }

            if (billGiver.BillStack.Count == 0)
            {
                Log.Warning($"[GameRL] ModifyBill: {building.LabelShort} ({building.ThingID}) has no bills to modify. Use AddBill first.");
                return;
            }

            if (billIndex < 0 || billIndex >= billGiver.BillStack.Count)
            {
                Log.Warning($"[GameRL] ModifyBill: Bill index {billIndex} out of range. Valid range: 0-{billGiver.BillStack.Count - 1}");
                return;
            }

            if (count == null && repeatForever == null)
            {
                Log.Warning("[GameRL] ModifyBill: No changes specified. Provide Count (int) or RepeatForever (bool).");
                return;
            }

            var bill = billGiver.BillStack[billIndex];
            if (bill is Bill_Production prodBill)
            {
                var recipeName = bill.recipe?.defName ?? "unknown";
                if (repeatForever == true)
                {
                    prodBill.repeatMode = BillRepeatModeDefOf.Forever;
                    Log.Message($"[GameRL] ModifyBill: Set bill {billIndex} ({recipeName}) to repeat forever");
                }
                else if (count.HasValue)
                {
                    prodBill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                    prodBill.repeatCount = count.Value;
                    Log.Message($"[GameRL] ModifyBill: Set bill {billIndex} ({recipeName}) count to {count.Value}");
                }
            }
            else
            {
                Log.Warning($"[GameRL] ModifyBill: Bill {billIndex} is not a production bill and cannot be modified.");
            }
        }
    }
}
