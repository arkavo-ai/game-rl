// Game-level actions for RimWorld GameRL - speed control, camera, etc.

using System;
using System.Collections.Generic;
using System.Linq;
using GameRL.Harmony.RPC;
using Verse;
using RimWorld;
using RimWorld.Planet;

namespace RimWorld.GameRL.Actions
{
    /// <summary>
    /// Game-level actions (speed control, camera, etc.)
    /// </summary>
    [GameRLComponent]
    public static class GameActions
    {
        /// <summary>
        /// Set game speed (0=paused, 1=normal, 2=fast, 3=superfast)
        /// </summary>
        [GameRLAction("SetSpeed", Description = "Set game speed (0=paused, 1=normal, 2=fast, 3=superfast)")]
        public static void SetSpeed([GameRLParam("Speed")] int speed)
        {
            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                throw new InvalidOperationException("SetSpeed: No TickManager available");
            }

            var timeSpeed = speed switch
            {
                0 => TimeSpeed.Paused,
                1 => TimeSpeed.Normal,
                2 => TimeSpeed.Fast,
                3 => TimeSpeed.Superfast,
                _ => TimeSpeed.Normal
            };

            tickManager.CurTimeSpeed = timeSpeed;
            Log.Message($"[GameRL] SetSpeed: Game speed set to {timeSpeed}");
        }

        /// <summary>
        /// Unpause the game and set to normal speed
        /// </summary>
        [GameRLAction("Unpause", Description = "Resume the game at normal speed")]
        public static void Unpause()
        {
            var tickManager = Find.TickManager;
            if (tickManager == null)
            {
                throw new InvalidOperationException("Unpause: No TickManager available");
            }

            tickManager.CurTimeSpeed = TimeSpeed.Normal;
            Log.Message("[GameRL] Unpause: Game resumed at normal speed");
        }

        /// <summary>
        /// Save current game state as a checkpoint for episode resets.
        /// Name defaults to "gamerl_checkpoint" if not provided.
        /// </summary>
        [GameRLAction("SaveCheckpoint", Description = "Save game state for episode reset")]
        public static void SaveCheckpoint([GameRLParam("Name")] string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "gamerl_checkpoint";

            GameDataSaveLoader.SaveGame(name);
            Log.Message($"[GameRL] SaveCheckpoint: Saved game as '{name}'");
        }

        /// <summary>
        /// Load a saved checkpoint to reset the episode.
        /// This is the primary mechanism for multi-episode training.
        /// </summary>
        /// <summary>
        /// Dismiss all pending letters (research finished, events, etc.) that block gameplay.
        /// </summary>
        [GameRLAction("DismissLetters", Description = "Dismiss all pending letter notifications (research done, events, etc.)")]
        public static void DismissLetters()
        {
            var letterStack = Find.LetterStack;
            if (letterStack == null)
            {
                throw new InvalidOperationException("DismissLetters: No LetterStack available");
            }

            var letters = letterStack.LettersListForReading.ToList();
            int count = letters.Count;
            foreach (var letter in letters)
            {
                letterStack.RemoveLetter(letter);
            }
            Log.Message($"[GameRL] DismissLetters: Dismissed {count} letters");
        }

        /// <summary>
        /// Dismiss ALL blocking dialogs and letters — faction naming, research completion,
        /// quest popups, message boxes, etc. Use this instead of DismissLetters when the
        /// game is stuck on a modal dialog.
        /// </summary>
        [GameRLAction("DismissAllDialogs", Description = "Dismiss all blocking dialogs AND letters (modal popups, research done, naming, etc.)")]
        public static void DismissAllDialogs()
        {
            int count = Patches.DialogDismissUtil.DismissAllDialogs();
            Log.Message($"[GameRL] DismissAllDialogs: Dismissed {count} dialogs/letters");
        }

        /// <summary>
        /// Set by LoadCheckpoint/NewColony to signal that ForceTicks must be skipped.
        /// Checked and cleared by HandleExecuteAction in Mod.cs.
        /// </summary>
        public static bool LoadRequested { get; set; }

        /// <summary>
        /// Programmatically generate a new colony for training.
        /// Uses RimWorld's internal game initialization to create a fresh colony
        /// with configurable parameters for diverse training scenarios.
        ///
        /// Scenario: Crashlanded (default), LostTribe, RichExplorer, Naked
        /// Storyteller: Cassandra (default), Phoebe, Randy
        /// Difficulty: Peaceful, Community, Adventure, Strive (default), Blood, Losing, Deathwish
        /// Biome: TemperateForest (default), BorealForest, TropicalRainforest, AridShrubland, Desert, Tundra, IceSheet
        /// MapSize: 200, 250 (default), 275, 300, 350
        /// </summary>
        [GameRLAction("NewColony", Description = "Generate a new colony programmatically for training. Params: Seed, Scenario, Storyteller, Difficulty, Biome, MapSize")]
        public static void NewColony(
            [GameRLParam("Seed")] string seed,
            [GameRLParam("Scenario")] string scenario,
            [GameRLParam("Storyteller")] string storyteller,
            [GameRLParam("Difficulty")] string difficulty,
            [GameRLParam("Biome")] string biome,
            [GameRLParam("MapSize")] int mapSize)
        {
            if (string.IsNullOrEmpty(seed))
                seed = Rand.Int.ToString();
            if (string.IsNullOrEmpty(scenario))
                scenario = "Crashlanded";
            if (string.IsNullOrEmpty(storyteller))
                storyteller = "Cassandra";
            if (string.IsNullOrEmpty(difficulty))
                difficulty = "Strive";
            if (string.IsNullOrEmpty(biome))
                biome = "TemperateForest";
            if (mapSize <= 0)
                mapSize = 250;

            Log.Message($"[GameRL] NewColony: seed={seed}, scenario={scenario}, storyteller={storyteller}, difficulty={difficulty}, biome={biome}, mapSize={mapSize}");

            // Signal that a game teardown is about to happen
            LoadRequested = true;

            // Queue long event so the game teardown/rebuild happens safely
            LongEventHandler.QueueLongEvent(delegate
            {
                try
                {
                    GenerateColony(seed, scenario, storyteller, difficulty, biome, mapSize);
                }
                catch (Exception ex)
                {
                    Log.Error($"[GameRL] NewColony failed: {ex}");
                }
            }, "GeneratingMap", doAsynchronously: true, exceptionHandler: null);
        }

        private static void GenerateColony(string seed, string scenarioName, string storytellerName, string difficultyName, string biomeName, int mapSize)
        {
            // 1. Resolve scenario
            var scenarioDef = ResolveScenario(scenarioName);
            if (scenarioDef == null)
                throw new InvalidOperationException($"NewColony: Unknown scenario '{scenarioName}'. Valid: Crashlanded, LostTribe, RichExplorer, Naked");

            // 2. Resolve storyteller
            var storytellerDef = ResolveStoryteller(storytellerName);
            if (storytellerDef == null)
                throw new InvalidOperationException($"NewColony: Unknown storyteller '{storytellerName}'. Valid: Cassandra, Phoebe, Randy");

            // 3. Resolve difficulty
            var difficultyDef = ResolveDifficulty(difficultyName);
            if (difficultyDef == null)
                throw new InvalidOperationException($"NewColony: Unknown difficulty '{difficultyName}'. Valid: Peaceful, Community, Adventure, Strive, Blood, Losing, Deathwish");

            // 4. Set up the new game
            Current.ProgramState = ProgramState.Entry;
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = scenarioDef.scenario;
            Current.Game.storyteller = new Storyteller(storytellerDef, difficultyDef);

            Find.Scenario.PreConfigure();

            // 5. Generate world
            Current.Game.World = WorldGenerator.GenerateWorld(
                0.3f,   // planet coverage (30%)
                seed,   // world seed
                OverallRainfall.Normal,
                OverallTemperature.Normal,
                OverallPopulation.Normal,
                LandmarkDensity.Normal
            );

            // 6. Find a tile matching the requested biome
            int tile = FindTileForBiome(biomeName);
            Current.Game.InitData.startingTile = tile;
            Current.Game.InitData.mapSize = mapSize;

            // 7. Configure starting pawns from the scenario
            Find.Scenario.PostIdeoChosen();
            Current.Game.InitData.PrepForMapGen();

            // 8. Generate the map and start the game
            Find.Scenario.PreMapGenerate();
            Current.Game.InitNewGame();

            // 9. Fix ideo-faction consistency — programmatic generation can leave
            // faction ideos unregistered with IdeoManager, causing tick errors like
            // "Faction X contains ideo Y which was removed!"
            RepairFactionIdeos();

            Log.Message($"[GameRL] NewColony: Colony generated successfully on tile {tile}");
        }

        private static ScenarioDef? ResolveScenario(string name)
        {
            // Try common aliases first
            var normalized = name.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            foreach (var def in DefDatabase<ScenarioDef>.AllDefs)
            {
                var defNorm = def.defName.ToLowerInvariant().Replace("_", "");
                if (defNorm == normalized || defNorm.Contains(normalized))
                    return def;
            }

            // Try label match
            foreach (var def in DefDatabase<ScenarioDef>.AllDefs)
            {
                if (def.label != null && def.label.ToLowerInvariant().Replace(" ", "").Contains(normalized))
                    return def;
            }

            return null;
        }

        private static StorytellerDef? ResolveStoryteller(string name)
        {
            var normalized = name.ToLowerInvariant().Replace(" ", "");
            foreach (var def in DefDatabase<StorytellerDef>.AllDefs)
            {
                var defNorm = def.defName.ToLowerInvariant().Replace("_", "");
                if (defNorm.Contains(normalized))
                    return def;
            }
            return null;
        }

        private static DifficultyDef? ResolveDifficulty(string name)
        {
            var normalized = name.ToLowerInvariant().Replace(" ", "");

            // Map common names to RimWorld defNames
            var aliases = new Dictionary<string, string>
            {
                { "peaceful", "Peaceful" },
                { "community", "MediumCommunity" },
                { "adventure", "MediumAdventure" },
                { "strive", "Rough" },
                { "blood", "Hard" },
                { "losing", "VeryHard" },
                { "deathwish", "ExtremelyHard" }
            };

            string? targetDefName = null;
            if (aliases.TryGetValue(normalized, out var mapped))
                targetDefName = mapped;

            foreach (var def in DefDatabase<DifficultyDef>.AllDefs)
            {
                if (targetDefName != null && def.defName == targetDefName)
                    return def;
                if (def.defName.ToLowerInvariant().Contains(normalized))
                    return def;
            }

            // Fallback to Rough (Strive to Survive)
            return DifficultyDefOf.Rough;
        }

        /// <summary>
        /// Ensures every ideo referenced by a faction is registered with the IdeoManager.
        /// Without this, IdeoManagerTick() may try to remove an ideo that a faction still
        /// references, producing "Faction X contains ideo Y which was removed!" errors.
        /// </summary>
        private static void RepairFactionIdeos()
        {
            var ideoManager = Find.IdeoManager;
            if (ideoManager == null)
                return;

            var registeredIdeos = new HashSet<Ideo>(ideoManager.IdeosListForReading);
            int repaired = 0;

            foreach (var faction in Find.FactionManager.AllFactions)
            {
                if (faction.ideos == null)
                    continue;

                foreach (var ideo in faction.ideos.AllIdeos)
                {
                    if (ideo != null && !registeredIdeos.Contains(ideo))
                    {
                        ideoManager.Add(ideo);
                        registeredIdeos.Add(ideo);
                        repaired++;
                        Log.Message($"[GameRL] RepairFactionIdeos: Re-registered ideo '{ideo.name}' for faction '{faction.Name}'");
                    }
                }
            }

            if (repaired > 0)
                Log.Message($"[GameRL] RepairFactionIdeos: Fixed {repaired} unregistered faction ideos");
        }

        private static int FindTileForBiome(string biomeName)
        {
            // In RimWorld 1.6, SurfaceTile doesn't expose biome directly.
            // Use TileFinder.RandomStartingTile() and sample multiple times
            // to find a tile in the preferred biome.
            var normalized = biomeName.ToLowerInvariant().Replace(" ", "").Replace("_", "");

            BiomeDef? targetBiome = null;
            foreach (var def in DefDatabase<BiomeDef>.AllDefs)
            {
                var defNorm = def.defName.ToLowerInvariant().Replace("_", "");
                if (defNorm == normalized || defNorm.Contains(normalized))
                {
                    targetBiome = def;
                    break;
                }
            }

            if (targetBiome != null)
            {
                // Sample random valid tiles and check if they match the biome
                // TileFinder gives us valid settlement tiles; we check biome via WorldGrid
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    int candidate = TileFinder.RandomStartingTile();
                    try
                    {
                        // Access biome through the surface tile's biome index
                        var surfTile = Find.WorldGrid[candidate];
                        // Use reflection to check biome if the field exists,
                        // otherwise check via string comparison on the tile
                        var biomeField = surfTile.GetType().GetField("biome") ??
                                         surfTile.GetType().GetField("Biome");
                        if (biomeField != null)
                        {
                            var tileBiome = biomeField.GetValue(surfTile) as BiomeDef;
                            if (tileBiome == targetBiome)
                                return candidate;
                        }
                        else
                        {
                            // If no biome field on SurfaceTile, try using the World
                            // to access biome data (RimWorld 1.6+ structure)
                            break;  // Biome matching not available, fall through
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
                Log.Warning($"[GameRL] Could not find tile for biome '{biomeName}', using random starting tile");
            }

            return TileFinder.RandomStartingTile();
        }

        /// <summary>
        /// Quit RimWorld to desktop. Used by training loops to restart the game process.
        /// </summary>
        [GameRLAction("QuitGame", Description = "Quit RimWorld to desktop for training loop restart")]
        public static void QuitGame()
        {
            Log.Message("[GameRL] QuitGame: Shutting down RimWorld");
            Root.Shutdown();
        }

        [GameRLAction("LoadCheckpoint", Description = "Load a saved game checkpoint to reset episode")]
        public static void LoadCheckpoint([GameRLParam("Name")] string name)
        {
            if (string.IsNullOrEmpty(name))
                name = "gamerl_checkpoint";

            var saveFiles = GenFilePaths.AllSavedGameFiles.ToList();
            var match = saveFiles.FirstOrDefault(f =>
                System.IO.Path.GetFileNameWithoutExtension(f.Name) == name);

            if (match == null)
            {
                throw new InvalidOperationException($"LoadCheckpoint: Save file '{name}' not found");
            }

            // Signal that a load is about to happen — ForceTicks must be skipped
            // because the game will tear down and reload, and ticking during teardown
            // causes a macOS fork-safety crash (Steam threads + fork = SIGABRT).
            LoadRequested = true;
            Log.Message($"[GameRL] LoadCheckpoint: Queuing load of '{name}'");
            GameDataSaveLoader.LoadGame(match);
        }
    }
}
