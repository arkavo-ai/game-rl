// Map-level actions for RimWorld GameRL - designations, zones, blueprints

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameRL.Harmony.RPC;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorld.GameRL.State;

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
                throw new InvalidOperationException("DesignateHunt: TargetId not found. Use a ThingID from Entities.Animals (e.g., 'Deer123')");
            }

            var pawn = target as Pawn;
            if (pawn == null || !pawn.RaceProps.Animal)
            {
                throw new InvalidOperationException($"DesignateHunt: {target.LabelShort} ({target.ThingID}) is not an animal. Only animals can be hunted.");
            }

            var map = target.Map;
            if (map == null)
            {
                throw new InvalidOperationException($"DesignateHunt: {target.LabelShort} ({target.ThingID}) is not on the map");
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
                throw new InvalidOperationException("CancelHunt: TargetId not found. Use a ThingID from Entities.Animals");
            }

            var map = target.Map;
            if (map == null)
            {
                throw new InvalidOperationException($"CancelHunt: {target.LabelShort} ({target.ThingID}) is not on the map");
            }

            var designation = map.designationManager.DesignationOn(target, DesignationDefOf.Hunt);
            if (designation != null)
            {
                map.designationManager.RemoveDesignation(designation);
                Log.Message($"[GameRL] CancelHunt: Removed hunt designation from {target.LabelShort}");
            }
            else
            {
                throw new InvalidOperationException($"CancelHunt: {target.LabelShort} ({target.ThingID}) is not designated for hunting");
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
                throw new InvalidOperationException("Building parameter is required. Examples: Bed, ButcherSpot, Campfire, CookStove, SimpleResearchBench, Table2x2c, DiningChair, StandingLamp, Wall, Door");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("No map is currently loaded");
            }

            var buildingDef = DefDatabase<ThingDef>.GetNamed(buildingDefName, errorOnFail: false);
            if (buildingDef == null)
            {
                throw new InvalidOperationException($"Unknown building '{buildingDefName}'. Examples: Bed, ButcherSpot, Campfire, CookStove, SimpleResearchBench, Sandbags, Wall, Door");
            }

            // Get stuff (material) if specified
            ThingDef? stuffDef = null;
            if (buildingDef.MadeFromStuff)
            {
                if (!string.IsNullOrEmpty(stuffDefName))
                {
                    stuffDef = DefDatabase<ThingDef>.GetNamed(stuffDefName, errorOnFail: false);
                    if (stuffDef == null)
                    {
                        throw new InvalidOperationException($"Unknown stuff/material '{stuffDefName}'. Examples: WoodLog, BlocksSandstone, Steel");
                    }
                }
                else
                {
                    // Default to wood for stuff buildings
                    stuffDef = ThingDefOf.WoodLog;
                }
            }

            var pos = new IntVec3(x, 0, z);
            var rot = new Rot4(rotation);

            // Check if position is valid
            if (!pos.InBounds(map))
            {
                throw new InvalidOperationException($"Position ({x},{z}) is out of map bounds. Map size is {map.Size.x}x{map.Size.z}");
            }

            // Check if can place
            var canPlace = GenConstruct.CanPlaceBlueprintAt(buildingDef, pos, rot, map, false, null, null, stuffDef);
            if (!canPlace.Accepted)
            {
                throw new InvalidOperationException($"Cannot place {buildingDefName} at ({x},{z}). Reason: {canPlace.Reason ?? "blocked or invalid terrain"}");
            }

            // Always spawn buildings directly (instant build) since the blueprint→construction
            // pipeline has issues with colonists never picking up construction jobs.
            // Deduct material costs from available resources on the map.

            // Calculate and deduct material costs
            // Use listerThings to count ALL items (including forbidden) — matches ResourceExtractor
            //
            // IMPORTANT: validate ALL costs (stuff + every costList item) UP FRONT before
            // consuming/destroying anything. Otherwise a building that needs both stuff and
            // costList items could have its stuff destroyed before a later costList shortfall
            // throws — permanently losing materials with no building placed.
            int stuffNeeded = buildingDef.costStuffCount;
            if (stuffNeeded > 0 && stuffDef != null)
            {
                int available = CountAllOnMap(map, stuffDef);
                if (available < stuffNeeded)
                {
                    throw new InvalidOperationException($"Not enough {stuffDef.defName} to build {buildingDefName}. Need {stuffNeeded}, have {available}.");
                }
            }

            if (buildingDef.costList != null)
            {
                foreach (var cost in buildingDef.costList)
                {
                    int available = CountAllOnMap(map, cost.thingDef);
                    if (available < cost.count)
                    {
                        throw new InvalidOperationException($"Not enough {cost.thingDef.defName} to build {buildingDefName}. Need {cost.count}, have {available}.");
                    }
                }
            }

            // All checks passed — now safe to consume materials.
            if (stuffNeeded > 0 && stuffDef != null)
            {
                // Remove stuff materials from the map (unforbid before consuming)
                int remaining = stuffNeeded;
                foreach (var thing in map.listerThings.ThingsOfDef(stuffDef).ToList())
                {
                    if (remaining <= 0) break;
                    if (thing == null || thing.Destroyed || !thing.Spawned) continue;
                    thing.SetForbidden(false, false);  // Auto-unforbid consumed items
                    int take = System.Math.Min(thing.stackCount, remaining);
                    remaining -= take;
                    if (take >= thing.stackCount)
                        thing.Destroy();
                    else
                        thing.stackCount -= take;
                }
            }

            if (buildingDef.costList != null)
            {
                foreach (var cost in buildingDef.costList)
                {
                    int remaining = cost.count;
                    foreach (var thing in map.listerThings.ThingsOfDef(cost.thingDef).ToList())
                    {
                        if (remaining <= 0) break;
                        if (thing == null || thing.Destroyed || !thing.Spawned) continue;
                        thing.SetForbidden(false, false);  // Auto-unforbid consumed items
                        int take = System.Math.Min(thing.stackCount, remaining);
                        remaining -= take;
                        if (take >= thing.stackCount)
                            thing.Destroy();
                        else
                            thing.stackCount -= take;
                    }
                }
            }

            // Spawn the building directly
            var building = ThingMaker.MakeThing(buildingDef, stuffDef);
            building.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(building, pos, map, rot);
            Log.Message($"[GameRL] PlaceBlueprint: Built {buildingDefName} at ({x},{z})");
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
                throw new InvalidOperationException("CreateGrowingZone: No map is currently loaded");
            }

            var cells = new System.Collections.Generic.List<IntVec3>();
            for (int dx = 0; dx < width; dx++)
            {
                for (int dz = 0; dz < height; dz++)
                {
                    var cell = new IntVec3(x + dx, 0, z + dz);
                    if (cell.InBounds(map)
                        && cell.GetTerrain(map).fertility > 0
                        && cell.GetEdifice(map) == null
                        && map.zoneManager.ZoneAt(cell) == null)
                    {
                        cells.Add(cell);
                    }
                }
            }

            if (cells.Count == 0)
            {
                throw new InvalidOperationException($"CreateGrowingZone: No valid cells at ({x},{z}) with size {width}x{height}. Needs fertile soil with no buildings or existing zones.");
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
                    throw new InvalidOperationException($"CreateGrowingZone: Unknown plant '{plantDefName}'. Examples: Plant_Potato, Plant_Rice, Plant_Corn, Plant_Healroot, Plant_Cotton");
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
                throw new InvalidOperationException("CreateStockpile: No map is currently loaded");
            }

            var cells = new System.Collections.Generic.List<IntVec3>();
            for (int dx = 0; dx < width; dx++)
            {
                for (int dz = 0; dz < height; dz++)
                {
                    var cell = new IntVec3(x + dx, 0, z + dz);
                    if (cell.InBounds(map)
                        && cell.Standable(map)
                        && map.zoneManager.ZoneAt(cell) == null)
                    {
                        cells.Add(cell);
                    }
                }
            }

            if (cells.Count == 0)
            {
                throw new InvalidOperationException($"CreateStockpile: No valid cells at ({x},{z}) with size {width}x{height}. Needs standable terrain with no existing zones.");
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
                throw new InvalidOperationException("DesignateCutPlants: No map is currently loaded");
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
                throw new InvalidOperationException($"DesignateCutPlants: No trees found in radius {radius} around ({x},{z})");
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
                throw new InvalidOperationException("DesignateMine: No map is currently loaded");
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
                throw new InvalidOperationException($"DesignateMine: No mineable rocks found in radius {radius} around ({x},{z})");
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
                throw new InvalidOperationException("AddBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches like ButcherSpot, CookStove, etc.");
            }

            if (string.IsNullOrEmpty(recipeDefName))
            {
                throw new InvalidOperationException("AddBill: Recipe is required. Examples: ButcherCorpseFlesh, Make_MealSimple, Make_MealFine, Make_Pemmican");
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                throw new InvalidOperationException($"AddBill: {building.LabelShort} ({building.ThingID}) cannot accept bills. Use ListWorkbenches to find valid workbenches.");
            }

            var recipeDef = DefDatabase<RecipeDef>.GetNamed(recipeDefName, errorOnFail: false);
            if (recipeDef == null)
            {
                // List available recipes for this workbench
                var available = building.def.AllRecipes?.Take(5).Select(r => r.defName) ?? Enumerable.Empty<string>();
                throw new InvalidOperationException($"AddBill: Unknown recipe '{recipeDefName}'. Available at {building.LabelShort}: {string.Join(", ", available)}");
            }

            // Check if recipe can be done at this building
            if (!recipeDef.AvailableOnNow(building, null))
            {
                var available = building.def.AllRecipes?.Take(5).Select(r => r.defName) ?? Enumerable.Empty<string>();
                throw new InvalidOperationException($"AddBill: Recipe '{recipeDefName}' not available at {building.LabelShort}. Available: {string.Join(", ", available)}");
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
                throw new InvalidOperationException("ListWorkbenches: No map is currently loaded");
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
                throw new InvalidOperationException("ListWorkbenches: No workbenches found. Build a ButcherSpot, CookStove, or other production building first.");
            }
            else
            {
                Log.Message($"[GameRL] ListWorkbenches: Found {workbenches.Count} workbenches");
            }
            return result.ToString();
        }

        [GameRLAction("ListBuildables", Description = "List all available building defs with material requirements (for PlaceBlueprint)")]
        public static string ListBuildables()
        {
            var result = new System.Text.StringBuilder();

            var buildables = DefDatabase<ThingDef>.AllDefs
                .Where(def => def.category == ThingCategory.Building
                    && def.BuildableByPlayer
                    && def.designationCategory != null)
                .OrderBy(def => def.designationCategory?.defName ?? "")
                .ThenBy(def => def.defName)
                .ToList();

            int count = 0;
            foreach (var def in buildables)
            {
                // Skip if research prerequisites not met
                if (def.researchPrerequisites != null && def.researchPrerequisites.Count > 0)
                {
                    if (!def.researchPrerequisites.All(r => r.IsFinished))
                        continue;
                }

                var size = $"{def.size.x}x{def.size.z}";
                var costs = new List<string>();
                if (def.MadeFromStuff)
                    costs.Add($"Stuff:{def.costStuffCount}");
                if (def.costList != null)
                {
                    foreach (var cost in def.costList)
                        costs.Add($"{cost.thingDef.defName}:{cost.count}");
                }
                var costStr = costs.Count > 0 ? string.Join(" ", costs) : "free";
                result.AppendLine($"{def.defName}: {def.label} ({size}) [{costStr}]");
                count++;
            }

            if (count == 0)
                return "No buildable structures available (check research)";

            Log.Message($"[GameRL] ListBuildables: Found {count} buildable defs");
            return result.ToString();
        }

        /// <summary>
        /// Forbid an item so colonists won't interact with it (e.g., prevent drug consumption)
        /// </summary>
        [GameRLAction("Forbid", Description = "Forbid an item so colonists won't use it (e.g., drugs, tainted apparel)")]
        public static void Forbid([GameRLParam("ThingId")] Thing thing)
        {
            if (thing == null)
            {
                throw new InvalidOperationException("Forbid: ThingId not found. Use a ThingID from Entities.Items, Weapons, or Corpses");
            }

            if (!thing.def.HasComp(typeof(CompForbiddable)) && thing.def.category != ThingCategory.Item)
            {
                throw new InvalidOperationException($"Forbid: {thing.LabelShort} ({thing.ThingID}) cannot be forbidden");
            }

            thing.SetForbidden(true, false);
            Log.Message($"[GameRL] Forbid: Forbid {thing.LabelShort} ({thing.ThingID})");
        }

        /// <summary>
        /// Forbid all items of a specific type
        /// </summary>
        [GameRLAction("ForbidByType", Description = "Forbid all items of a specific type (e.g., Beer, SmokeleafJoint)")]
        public static void ForbidByType([GameRLParam("DefName")] string defName)
        {
            if (string.IsNullOrEmpty(defName))
            {
                throw new InvalidOperationException("ForbidByType: DefName is required. Examples: Beer, SmokeleafJoint, Flake, Yayo");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("ForbidByType: No map is currently loaded");
            }

            var def = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
            if (def == null)
            {
                throw new InvalidOperationException($"ForbidByType: Unknown item type '{defName}'");
            }

            int count = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(def))
            {
                if (!thing.IsForbidden(Faction.OfPlayer))
                {
                    thing.SetForbidden(true, false);
                    count++;
                }
            }

            if (count > 0)
            {
                Log.Message($"[GameRL] ForbidByType: Forbid {count} {defName}");
            }
            else
            {
                throw new InvalidOperationException($"ForbidByType: No unforbidden {defName} found on the map");
            }
        }

        /// <summary>
        /// List available recipes for a workbench
        /// </summary>
        [GameRLAction("ListRecipes", Description = "List all available recipes for a workbench (use before AddBill)")]
        public static string ListRecipes([GameRLParam("BuildingId")] Thing building)
        {
            if (building == null)
            {
                throw new InvalidOperationException("ListRecipes: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches");
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                throw new InvalidOperationException($"ListRecipes: {building.LabelShort} ({building.ThingID}) is not a workbench");
            }

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Recipes for {building.LabelShort} ({building.ThingID}):");

            var recipes = building.def.AllRecipes;
            if (recipes == null || recipes.Count == 0)
            {
                throw new InvalidOperationException($"ListRecipes: {building.LabelShort} has no recipes");
            }

            foreach (var recipe in recipes)
            {
                var available = recipe.AvailableOnNow(building, null) ? "" : " [UNAVAILABLE]";
                var ingredients = recipe.ingredients?.Select(i =>
                {
                    var filterSummary = i.filter?.Summary ?? "any";
                    return $"{filterSummary}x{i.GetBaseCount():F0}";
                }) ?? Enumerable.Empty<string>();
                var ingredientStr = ingredients.Any() ? string.Join(", ", ingredients) : "none";
                result.AppendLine($"  {recipe.defName}: {recipe.label} (needs: {ingredientStr}){available}");
            }

            Log.Message($"[GameRL] ListRecipes: Listed {recipes.Count} recipes for {building.LabelShort}");
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
                throw new InvalidOperationException("Unforbid: ThingId not found. Use a ThingID from ForbiddenItems in Resources or from Entities.Items");
            }

            // Use SetForbidden which handles the check internally
            // This works for items, corpses, and anything that can be forbidden
            if (!thing.def.HasComp(typeof(CompForbiddable)) && thing.def.category != ThingCategory.Item)
            {
                throw new InvalidOperationException($"Unforbid: {thing.LabelShort} ({thing.ThingID}) cannot be forbidden/unforbidden");
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
                throw new InvalidOperationException("UnforbidArea: No map is currently loaded");
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
                throw new InvalidOperationException($"UnforbidArea: No forbidden items found in radius {radius} around ({x},{z})");
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
                throw new InvalidOperationException("UnforbidByType: DefName is required. Examples: MealSurvivalPack, Steel, WoodLog, InsectJelly, MedicineHerbal");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("UnforbidByType: No map is currently loaded");
            }

            var def = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
            if (def == null)
            {
                throw new InvalidOperationException($"UnforbidByType: Unknown item type '{defName}'. Check ForbiddenItemCounts in Resources for valid DefNames.");
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
                throw new InvalidOperationException($"UnforbidByType: No forbidden {defName} found on the map");
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
                throw new InvalidOperationException("CancelBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches.");
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                throw new InvalidOperationException($"CancelBill: {building.LabelShort} ({building.ThingID}) is not a workbench. Use ListWorkbenches to find valid workbenches.");
            }

            if (billGiver.BillStack.Count == 0)
            {
                throw new InvalidOperationException($"CancelBill: {building.LabelShort} ({building.ThingID}) has no bills to cancel.");
            }

            if (billIndex < 0 || billIndex >= billGiver.BillStack.Count)
            {
                throw new InvalidOperationException($"CancelBill: Bill index {billIndex} out of range. Valid range: 0-{billGiver.BillStack.Count - 1}");
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
                throw new InvalidOperationException("ModifyBill: BuildingId not found. Use a ThingID from Entities.Buildings for workbenches.");
            }

            var billGiver = building as IBillGiver;
            if (billGiver == null)
            {
                throw new InvalidOperationException($"ModifyBill: {building.LabelShort} ({building.ThingID}) is not a workbench. Use ListWorkbenches to find valid workbenches.");
            }

            if (billGiver.BillStack.Count == 0)
            {
                throw new InvalidOperationException($"ModifyBill: {building.LabelShort} ({building.ThingID}) has no bills to modify. Use AddBill first.");
            }

            if (billIndex < 0 || billIndex >= billGiver.BillStack.Count)
            {
                throw new InvalidOperationException($"ModifyBill: Bill index {billIndex} out of range. Valid range: 0-{billGiver.BillStack.Count - 1}");
            }

            if (count == null && repeatForever == null)
            {
                throw new InvalidOperationException("ModifyBill: No changes specified. Provide Count (int) or RepeatForever (bool).");
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
                throw new InvalidOperationException($"ModifyBill: Bill {billIndex} is not a production bill and cannot be modified.");
            }
        }

        /// <summary>
        /// Request a full state observation instead of delta on the next sim_step.
        /// Use this if you lost sync (StateHash doesn't match) or want to refresh your view.
        /// </summary>
        [GameRLAction("RequestFullState", Description = "Request full state observation on next sim_step")]
        public static void RequestFullState()
        {
            var executor = GameRLMod.CommandExecutor;
            if (executor != null)
            {
                executor.ForceFullState = true;
                Log.Message("[GameRL] RequestFullState: Next observation will be full state");
            }
            else
            {
                throw new InvalidOperationException("RequestFullState: Command executor not initialized");
            }
        }

        /// <summary>
        /// Select a research project to work on
        /// </summary>
        [GameRLAction("SelectResearch", Description = "Select a research project. Use defNames from Research.Available in observation.")]
        public static void SelectResearch([GameRLParam("ProjectDefName")] string projectDefName)
        {
            if (string.IsNullOrEmpty(projectDefName))
            {
                throw new InvalidOperationException("SelectResearch: ProjectDefName is required");
            }

            var proj = DefDatabase<ResearchProjectDef>.GetNamed(projectDefName, errorOnFail: false);
            if (proj == null)
            {
                throw new InvalidOperationException($"SelectResearch: Unknown project '{projectDefName}'");
            }

            if (proj.IsFinished)
            {
                throw new InvalidOperationException($"SelectResearch: '{projectDefName}' is already completed");
            }

            if (proj.prerequisites != null)
            {
                foreach (var prereq in proj.prerequisites)
                {
                    if (!prereq.IsFinished)
                    {
                        throw new InvalidOperationException($"SelectResearch: Missing prerequisite: {prereq.label}");
                    }
                }
            }

            var manager = Find.ResearchManager;
            if (manager == null)
            {
                throw new InvalidOperationException("SelectResearch: No ResearchManager available");
            }

            var field = typeof(ResearchManager).GetField("currentProj",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(manager, proj);
                Log.Message($"[GameRL] SelectResearch: Now researching {proj.label}");
            }
            else
            {
                var prop = typeof(ResearchManager).GetProperty("CurrentProject");
                if (prop?.SetMethod != null)
                {
                    prop.SetValue(manager, proj);
                    Log.Message($"[GameRL] SelectResearch: Now researching {proj.label}");
                }
                else
                {
                    throw new InvalidOperationException("SelectResearch: Cannot set research project - API incompatible");
                }
            }
        }

        /// <summary>
        /// Delete a zone by label
        /// </summary>
        [GameRLAction("DeleteZone", Description = "Delete a zone by its label (from Zones observation)")]
        public static void DeleteZone([GameRLParam("ZoneLabel")] string zoneLabel)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("DeleteZone: No map loaded");
            }

            var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.label == zoneLabel);
            if (zone == null)
            {
                throw new InvalidOperationException($"DeleteZone: Zone '{zoneLabel}' not found");
            }

            zone.Delete();
            Log.Message($"[GameRL] DeleteZone: Deleted zone '{zoneLabel}'");
        }

        /// <summary>
        /// Set stockpile priority
        /// </summary>
        [GameRLAction("SetStockpilePriority", Description = "Set stockpile priority (1=low, 2=normal, 3=preferred, 4=important, 5=critical)")]
        public static void SetStockpilePriority(
            [GameRLParam("ZoneLabel")] string zoneLabel,
            [GameRLParam("Priority")] int priority)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("SetStockpilePriority: No map loaded");
            }

            var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.label == zoneLabel) as Zone_Stockpile;
            if (zone == null)
            {
                throw new InvalidOperationException($"SetStockpilePriority: Stockpile '{zoneLabel}' not found");
            }

            var storagePriority = priority switch
            {
                1 => StoragePriority.Low,
                2 => StoragePriority.Normal,
                3 => StoragePriority.Preferred,
                4 => StoragePriority.Important,
                5 => StoragePriority.Critical,
                _ => StoragePriority.Normal
            };

            zone.settings.Priority = storagePriority;
            Log.Message($"[GameRL] SetStockpilePriority: Set '{zoneLabel}' to {storagePriority}");
        }

        /// <summary>
        /// Set the plant type for a growing zone
        /// </summary>
        [GameRLAction("SetGrowingPlant", Description = "Change what plant a growing zone grows (e.g., Plant_Rice, Plant_Potato)")]
        public static void SetGrowingPlant(
            [GameRLParam("ZoneLabel")] string zoneLabel,
            [GameRLParam("PlantDefName")] string plantDefName)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("SetGrowingPlant: No map loaded");
            }

            var zone = map.zoneManager.AllZones.FirstOrDefault(z => z.label == zoneLabel) as Zone_Growing;
            if (zone == null)
            {
                throw new InvalidOperationException($"SetGrowingPlant: Growing zone '{zoneLabel}' not found");
            }

            var plantDef = DefDatabase<ThingDef>.GetNamed(plantDefName, errorOnFail: false);
            if (plantDef == null)
            {
                throw new InvalidOperationException($"SetGrowingPlant: Unknown plant '{plantDefName}'");
            }

            zone.SetPlantDefToGrow(plantDef);
            Log.Message($"[GameRL] SetGrowingPlant: '{zoneLabel}' now growing {plantDefName}");
        }

        /// <summary>
        /// Set prisoner interaction mode
        /// </summary>
        [GameRLAction("SetPrisonerInteraction", Description = "Set how to interact with a prisoner (AttemptRecruit, ReduceResistance, Release, Execution)")]
        public static void SetPrisonerInteraction(
            [GameRLParam("PrisonerId"), Resolve] Pawn prisoner,
            [GameRLParam("Mode")] string mode)
        {
            if (prisoner == null)
            {
                throw new InvalidOperationException("SetPrisonerInteraction: Prisoner not found");
            }

            if (!prisoner.IsPrisoner)
            {
                throw new InvalidOperationException($"SetPrisonerInteraction: {prisoner.LabelShort} is not a prisoner");
            }

            // Find the interaction mode def by name
            var modeDef = DefDatabase<PrisonerInteractionModeDef>.GetNamed(mode, errorOnFail: false);
            if (modeDef == null)
            {
                // Try common aliases
                modeDef = mode.ToLowerInvariant() switch
                {
                    "recruit" or "attemptrecruit" => PrisonerInteractionModeDefOf.AttemptRecruit,
                    "reduce" or "reduceresistance" => PrisonerInteractionModeDefOf.ReduceResistance,
                    "release" => PrisonerInteractionModeDefOf.Release,
                    "execution" or "execute" => PrisonerInteractionModeDefOf.Execution,
                    _ => null
                };
            }

            if (modeDef == null)
            {
                throw new InvalidOperationException($"SetPrisonerInteraction: Unknown mode '{mode}'. Use: AttemptRecruit, ReduceResistance, Release, Execution");
            }

            // Set via reflection for API compatibility
            try
            {
                var prop = typeof(Pawn_GuestTracker).GetProperty("interactionMode")
                    ?? typeof(Pawn_GuestTracker).GetProperty("ExclusiveInteractionMode");
                if (prop?.SetMethod != null)
                {
                    prop.SetValue(prisoner.guest, modeDef);
                    Log.Message($"[GameRL] SetPrisonerInteraction: {prisoner.LabelShort} set to {modeDef.defName}");
                }
                else
                {
                    throw new InvalidOperationException("SetPrisonerInteraction: Cannot set interaction mode - API incompatible");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SetPrisonerInteraction: {ex.Message}");
            }
        }

        /// <summary>
        /// Designate a wild animal for taming
        /// </summary>
        [GameRLAction("DesignateTame", Description = "Mark a wild animal for taming by a handler")]
        public static void DesignateTame([GameRLParam("TargetId")] Pawn animal)
        {
            if (animal == null || animal.Destroyed || !animal.Spawned)
            {
                throw new InvalidOperationException("DesignateTame: Animal not found");
            }
            if (!animal.RaceProps.Animal)
            {
                throw new InvalidOperationException($"DesignateTame: {animal.LabelShort} is not an animal");
            }
            if (animal.Faction == Faction.OfPlayer)
            {
                throw new InvalidOperationException($"DesignateTame: {animal.LabelShort} is already tamed");
            }

            var map = animal.Map;
            if (map == null) return;

            if (map.designationManager.DesignationOn(animal, DesignationDefOf.Tame) != null)
            {
                throw new InvalidOperationException($"DesignateTame: {animal.LabelShort} is already designated for taming");
            }

            map.designationManager.AddDesignation(new Designation(animal, DesignationDefOf.Tame));
            Log.Message($"[GameRL] DesignateTame: Marked {animal.LabelShort} ({animal.def.defName}) for taming");
        }

        /// <summary>
        /// Set training for a tamed animal
        /// </summary>
        [GameRLAction("SetAnimalTraining", Description = "Toggle a training type for a tamed animal (obedience, release, rescue, haul)")]
        public static void SetAnimalTraining(
            [GameRLParam("AnimalId")] Pawn animal,
            [GameRLParam("TrainingDef")] string trainingDefName,
            [GameRLParam("Enabled")] bool enabled = true)
        {
            if (animal == null || animal.Destroyed || !animal.Spawned)
            {
                throw new InvalidOperationException("SetAnimalTraining: Animal not found");
            }
            if (!animal.RaceProps.Animal || animal.Faction != Faction.OfPlayer)
            {
                throw new InvalidOperationException($"SetAnimalTraining: {animal.LabelShort} is not a tamed animal");
            }
            if (animal.training == null)
            {
                throw new InvalidOperationException($"SetAnimalTraining: {animal.LabelShort} cannot be trained");
            }

            var trainDef = DefDatabase<TrainableDef>.GetNamed(trainingDefName, errorOnFail: false);
            if (trainDef == null)
            {
                throw new InvalidOperationException($"SetAnimalTraining: Unknown training '{trainingDefName}'. Valid: Obedience, Release, Rescue, Haul");
            }

            try
            {
                animal.training.SetWantedRecursive(trainDef, enabled);
                Log.Message($"[GameRL] SetAnimalTraining: {animal.LabelShort} training '{trainingDefName}' set to {enabled}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SetAnimalTraining: {ex.Message}");
            }
        }

        /// <summary>
        /// Assign an animal to a specific area (zone restriction)
        /// </summary>
        [GameRLAction("SetAnimalArea", Description = "Restrict an animal to a named area (or 'Unrestricted')")]
        public static void SetAnimalArea(
            [GameRLParam("AnimalId")] Pawn animal,
            [GameRLParam("AreaLabel")] string areaLabel)
        {
            if (animal == null || animal.Destroyed || !animal.Spawned)
            {
                throw new InvalidOperationException("SetAnimalArea: Animal not found");
            }
            if (!animal.RaceProps.Animal || animal.Faction != Faction.OfPlayer)
            {
                throw new InvalidOperationException($"SetAnimalArea: {animal.LabelShort} is not a tamed animal");
            }

            if (string.IsNullOrEmpty(areaLabel) || areaLabel.ToLowerInvariant() == "unrestricted")
            {
                animal.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                Log.Message($"[GameRL] SetAnimalArea: {animal.LabelShort} set to unrestricted");
                return;
            }

            var map = animal.Map;
            if (map == null) return;

            var area = map.areaManager.AllAreas.FirstOrDefault(a => a.Label == areaLabel);
            if (area == null)
            {
                throw new InvalidOperationException($"SetAnimalArea: Area '{areaLabel}' not found");
            }

            animal.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            Log.Message($"[GameRL] SetAnimalArea: {animal.LabelShort} restricted to '{areaLabel}'");
        }

        /// <summary>
        /// Slaughter a tamed animal
        /// </summary>
        [GameRLAction("DesignateSlaughter", Description = "Mark a tamed animal for slaughter")]
        public static void DesignateSlaughter([GameRLParam("AnimalId")] Pawn animal)
        {
            if (animal == null || animal.Destroyed || !animal.Spawned)
            {
                throw new InvalidOperationException("DesignateSlaughter: Animal not found");
            }
            if (!animal.RaceProps.Animal || animal.Faction != Faction.OfPlayer)
            {
                throw new InvalidOperationException($"DesignateSlaughter: {animal.LabelShort} is not a tamed animal");
            }

            var map = animal.Map;
            if (map == null) return;

            if (map.designationManager.DesignationOn(animal, DesignationDefOf.Slaughter) != null)
            {
                throw new InvalidOperationException($"DesignateSlaughter: {animal.LabelShort} already designated for slaughter");
            }

            map.designationManager.AddDesignation(new Designation(animal, DesignationDefOf.Slaughter));
            Log.Message($"[GameRL] DesignateSlaughter: Marked {animal.LabelShort} for slaughter");
        }

        /// <summary>
        /// Designate a building for deconstruction to recover materials
        /// </summary>
        [GameRLAction("Deconstruct", Description = "Designate a building for deconstruction")]
        public static void Deconstruct([GameRLParam("BuildingId")] Building building)
        {
            if (building == null)
            {
                throw new InvalidOperationException("Deconstruct: BuildingId not found. Use a ThingID from Entities.Buildings");
            }

            var map = building.Map;
            if (map == null)
            {
                throw new InvalidOperationException($"Deconstruct: {building.LabelShort} ({building.ThingID}) is not on the map");
            }

            if (!building.DeconstructibleBy(Faction.OfPlayer))
            {
                throw new InvalidOperationException($"Deconstruct: {building.LabelShort} ({building.ThingID}) cannot be deconstructed by the player");
            }

            if (map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null)
            {
                Log.Message($"[GameRL] Deconstruct: {building.LabelShort} ({building.ThingID}) is already designated for deconstruction");
                return;
            }

            map.designationManager.AddDesignation(new Designation(building, DesignationDefOf.Deconstruct));
            Log.Message($"[GameRL] Deconstruct: Designated {building.LabelShort} ({building.ThingID}) for deconstruction");
        }

        /// <summary>
        /// Force a colonist to repair a damaged building
        /// </summary>
        [GameRLAction("DesignateRepair", Description = "Force a colonist to repair a damaged building")]
        public static void DesignateRepair(
            [GameRLParam("ColonistId")] Pawn pawn,
            [GameRLParam("BuildingId")] Building building)
        {
            if (pawn == null)
            {
                throw new InvalidOperationException("DesignateRepair: ColonistId not found. Use a ThingID from Entities.Colonists");
            }

            if (building == null)
            {
                throw new InvalidOperationException("DesignateRepair: BuildingId not found. Use a ThingID from Entities.Buildings");
            }

            if (pawn.Downed)
            {
                throw new InvalidOperationException($"DesignateRepair: {pawn.LabelShort} ({pawn.ThingID}) is downed and cannot repair");
            }

            if (building.HitPoints >= building.MaxHitPoints)
            {
                throw new InvalidOperationException($"DesignateRepair: {building.LabelShort} ({building.ThingID}) is not damaged ({building.HitPoints}/{building.MaxHitPoints} HP)");
            }

            var job = JobMaker.MakeJob(JobDefOf.Repair, building);
            pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            Log.Message($"[GameRL] DesignateRepair: {pawn.LabelShort} repairing {building.LabelShort} ({building.HitPoints}/{building.MaxHitPoints} HP)");
        }

        /// <summary>
        /// Designate natural stone floor for smoothing
        /// </summary>
        [GameRLAction("SmoothFloor", Description = "Designate natural stone floor for smoothing in an area")]
        public static void SmoothFloor(
            [GameRLParam("X")] int x,
            [GameRLParam("Y")] int z,
            [GameRLParam("Radius")] int radius = 3)
        {
            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("SmoothFloor: No map is currently loaded");
            }

            var center = new IntVec3(x, 0, z);
            if (!center.InBounds(map))
            {
                throw new InvalidOperationException($"SmoothFloor: Position ({x},{z}) is out of bounds");
            }

            int count = 0;
            foreach (var cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) continue;

                var terrain = cell.GetTerrain(map);
                if (terrain.smoothedTerrain != null)
                {
                    if (map.designationManager.DesignationAt(cell, DesignationDefOf.SmoothFloor) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.SmoothFloor));
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                Log.Message($"[GameRL] SmoothFloor: Marked {count} cells for smoothing in radius {radius} around ({x},{z})");
            }
            else
            {
                throw new InvalidOperationException($"SmoothFloor: No smoothable stone floor found in radius {radius} around ({x},{z})");
            }
        }

        /// <summary>
        /// Buy or sell items with an active trader
        /// </summary>
        [GameRLAction("Trade", Description = "Buy or sell items with an active trader")]
        public static void Trade(
            [GameRLParam("TraderId")] string traderId,
            [GameRLParam("ItemDefName")] string itemDefName,
            [GameRLParam("Count")] int count,
            [GameRLParam("IsBuy")] bool isBuy = true)
        {
            if (string.IsNullOrEmpty(traderId))
            {
                throw new InvalidOperationException("Trade: TraderId is required. Use trader name from ActiveTraders in observation");
            }

            if (string.IsNullOrEmpty(itemDefName))
            {
                throw new InvalidOperationException("Trade: ItemDefName is required (e.g., Steel, WoodLog, MedicineIndustrial)");
            }

            if (count <= 0)
            {
                throw new InvalidOperationException("Trade: Count must be positive");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                throw new InvalidOperationException("Trade: No map loaded");
            }

            // Find the trader - check visitor pawns first, then orbital
            ITrader? trader = null;
            try
            {
                // Visitor traders on map
                trader = map.mapPawns.AllPawnsSpawned
                    .FirstOrDefault(p => p != null && !p.Destroyed && p.Spawned
                        && p.TraderKind != null && p.Faction != Faction.OfPlayer
                        && (p.LabelShort == traderId || p.ThingID == traderId))
                    as ITrader;

                // Orbital traders
                if (trader == null)
                {
                    trader = map.passingShipManager?.passingShips?
                        .OfType<TradeShip>()
                        .FirstOrDefault(s => s.name == traderId || s.TraderName == traderId);
                }
            }
            catch { }

            if (trader == null)
            {
                throw new InvalidOperationException($"Trade: Trader '{traderId}' not found. Check ActiveTraders in observation.");
            }

            // Find best Social-skill colonist as negotiator
            Pawn? negotiator;
            try
            {
                negotiator = map.mapPawns.FreeColonists
                    .Where(p => !p.Downed && !p.InMentalState)
                    .OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0)
                    .FirstOrDefault();
            }
            catch
            {
                throw new InvalidOperationException("Trade: No available colonist to negotiate");
            }

            if (negotiator == null)
            {
                throw new InvalidOperationException("Trade: No available colonist to negotiate");
            }

            try
            {
                // Open trade session
                TradeSession.SetupWith(trader, negotiator, false);

                // Find the tradeable item
                var tradeable = TradeSession.deal.AllTradeables
                    .FirstOrDefault(t => t.ThingDef?.defName == itemDefName);

                if (tradeable == null)
                {
                    var available = TradeSession.deal.AllTradeables
                        .Where(t => t.CountHeldBy(isBuy ? Transactor.Trader : Transactor.Colony) > 0)
                        .Take(5)
                        .Select(t => t.ThingDef?.defName ?? "?");
                    TradeSession.Close();
                    var side = isBuy ? "trader" : "colony";
                    throw new InvalidOperationException($"Trade: Item '{itemDefName}' not available. {side} has: {string.Join(", ", available)}");
                }

                // Set trade amount (positive = buy from trader, negative = sell to trader)
                var available_count = isBuy
                    ? tradeable.CountHeldBy(Transactor.Trader)
                    : tradeable.CountHeldBy(Transactor.Colony);

                if (available_count <= 0)
                {
                    TradeSession.Close();
                    var side = isBuy ? "Trader doesn't have" : "Colony doesn't have";
                    throw new InvalidOperationException($"Trade: {side} any {itemDefName} to trade");
                }

                var actualCount = System.Math.Min(count, available_count);
                var adjustAmount = isBuy ? actualCount : -actualCount;
                tradeable.AdjustTo(adjustAmount);

                // Execute the trade
                if (TradeSession.deal.TryExecute(out bool actuallyTraded))
                {
                    var verb = isBuy ? "Bought" : "Sold";
                    Log.Message($"[GameRL] Trade: {verb} {actualCount} {itemDefName} via {negotiator.LabelShort}");
                }
                else
                {
                    throw new InvalidOperationException($"Trade: Trade execution failed (insufficient silver?)");
                }

                TradeSession.Close();
            }
            catch (Exception ex)
            {
                try { TradeSession.Close(); } catch { }
                throw new InvalidOperationException($"Trade: {ex.Message}");
            }
        }

        /// <summary>
        /// Count ALL items of a def on the map, including forbidden items.
        /// Matches ResourceExtractor's counting method for consistency.
        /// </summary>
        private static int CountAllOnMap(Map map, ThingDef def)
        {
            int total = 0;
            foreach (var thing in map.listerThings.ThingsOfDef(def))
            {
                if (thing != null && !thing.Destroyed && thing.Spawned)
                    total += thing.stackCount;
            }
            return total;
        }

        /// <summary>
        /// Unforbid all items on the map so colonists can access resources.
        /// Called after reset to prevent starvation from forbidden starting items.
        /// </summary>
        public static void UnforbidAllItems(Map map)
        {
            int count = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing != null && !thing.Destroyed && thing.Spawned
                    && thing.def.category == ThingCategory.Item
                    && thing is ThingWithComps twc)
                {
                    var comp = twc.GetComp<CompForbiddable>();
                    if (comp != null && comp.Forbidden)
                    {
                        twc.SetForbidden(false, false);
                        count++;
                    }
                }
            }
            if (count > 0)
                Log.Message($"[GameRL] Auto-unforbid: {count} items unforbidden");
        }
    }
}
