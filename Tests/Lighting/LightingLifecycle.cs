using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightLightingHarness
{
    /// <summary>Two-map mode switching, map removal, and pre-mode-save migration checks.</summary>
    public sealed class LightingLifecycleCheck : GameComponent
    {
        private const string SaveName = "searchlight-lighting-lifecycle";
        private readonly string output;
        private readonly string phase;
        private int readyTick;
        private int stage;
        private int unloadCycle;
        private bool finished;
        private Map home;
        private Map away;
        private Pawn homePawn;
        private Pawn awayPawn;
        private Building_Searchlight homeLamp;
        private Building_Searchlight awayLamp;
        private IntVec3 homeOrigin;
        private IntVec3 awayOrigin;

        public LightingLifecycleCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-lifecycle-output", out output);
            GenCommandLine.TryGetCommandLineArg("ds-lifecycle-phase", out phase);
        }

        public override void GameComponentUpdate()
        {
            if (finished || string.IsNullOrEmpty(output) || Current.ProgramState != ProgramState.Playing ||
                Find.CurrentMap == null || (stage != 0 && Find.TickManager.TicksGame < readyTick))
                return;

            try
            {
                if (phase == "reload") VerifyMigration();
                else if (stage == 0) PrepareLifecycle();
                else RunLifecycle();
            }
            catch (Exception ex)
            {
                Finish("FAIL " + ex);
            }
        }

        private void PrepareLifecycle()
        {
            home = Find.CurrentMap;
            Record($"game={VersionControl.CurrentVersionString}; assemblyMvid={typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId:D}");
            var lighting = Current.Game.GetComponent<SearchlightLightingGame>();
            Check(lighting != null, "game component exists");
            Check(lighting.CurrentMode == SearchlightLightingMode.LegacyEndpoint, "fresh game defaults to legacy endpoint mode");
            homeOrigin = home.Center;
            ClearArea(home, homeOrigin, 26);
            home.weatherManager.TransitionTo(WeatherDefOf.Clear);
            homeLamp = SpawnLamp(home, homeOrigin);
            homePawn = SpawnHeldPawn(home, homeOrigin + new IntVec3(10, 0, 0));

            PrepareAwayMap();
        }

        private void PrepareAwayMap()
        {
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(home.Tile, neighbors);
            PlanetTile tile = neighbors.First(t => !Find.World.Impassable(t) && Find.WorldObjects.MapParentAt(t) == null);
            away = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(70, 1, 70), WorldObjectDefOf.Site);
            awayOrigin = away.Center;
            ClearArea(away, awayOrigin, 26);
            awayPawn = SpawnHeldPawn(away, awayOrigin + new IntVec3(10, 0, 0));
            awayLamp = SpawnLamp(away, awayOrigin);
            homeLamp.SetMode(SearchlightMode.Fixed);
            awayLamp.SetMode(SearchlightMode.Fixed);
            SetAim(homeLamp, homeOrigin);
            SetAim(awayLamp, awayOrigin);
            Current.Game.CurrentMap = away;
            stage = 1;
            readyTick = Find.TickManager.TicksGame + 100;
        }

        private void RunLifecycle()
        {
            var lighting = Current.Game.GetComponent<SearchlightLightingGame>();
            lighting.SetMode(SearchlightLightingMode.PreciseField);
            Check(lighting.CurrentMode == SearchlightLightingMode.PreciseField, "precise mode enabled with both maps populated");
            CheckPrecise(home, homeOrigin, homePawn, "home map while viewing away map");
            CheckPrecise(away, awayOrigin, awayPawn, "away map while viewing away map");

            Current.Game.CurrentMap = home;
            CheckPrecise(home, homeOrigin, homePawn, "home map after camera switch");
            CheckPrecise(away, awayOrigin, awayPawn, "away map after camera switch");
            lighting.SetMode(SearchlightLightingMode.LegacyEndpoint);
            CheckLegacy(home, homeOrigin, "home map after disabling precise mode");
            CheckLegacy(away, awayOrigin, "away map after disabling precise mode");

            Current.Game.CurrentMap = away;
            lighting.SetMode(SearchlightLightingMode.PreciseField);
            CheckPrecise(home, homeOrigin, homePawn, "home map after re-enabling precise mode from away view");
            CheckPrecise(away, awayOrigin, awayPawn, "away map after re-enabling precise mode");
            Current.Game.CurrentMap = home;

            System.WeakReference<Map> weakMap = RemoveAwayMap();
            CheckPrecise(home, homeOrigin, homePawn, "home lighting remains after away map removal");
            unloadCycle++;
            RecordCollectedMap(unloadCycle, weakMap);
            if (unloadCycle < 3)
            {
                PrepareAwayMap();
                return;
            }

            // This save contains the current mode component. The runner removes only that
            // component entry from the isolated XML before opening it in the reload phase.
            GameDataSaveLoader.SaveGame(SaveName);
            var savePath = GenFilePaths.FilePathForSavedGame(SaveName);
            Check(File.Exists(savePath) && new FileInfo(savePath).Length > 0, "saved isolated pre-migration fixture");
            Record("saved=" + savePath);
            Finish("PASS fresh");
        }

        private void VerifyMigration()
        {
            home = Find.CurrentMap;
            Record($"game={VersionControl.CurrentVersionString}; assemblyMvid={typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId:D}");
            var lighting = Current.Game.GetComponent<SearchlightLightingGame>();
            Check(lighting != null, "missing-component save receives the current game component");
            Check(lighting.CurrentMode == SearchlightLightingMode.LegacyEndpoint,
                "save without the lighting-mode component migrates to legacy endpoint mode");
            homeLamp = home.listerThings.AllThings.OfType<Building_Searchlight>().Single();
            IntVec3 origin = homeLamp.Position;
            IntVec3 middle = origin + new IntVec3(10, 0, 0);
            IntVec3 endpoint = origin + new IntVec3(20, 0, 0);
            homePawn = home.mapPawns.AllPawnsSpawned.FirstOrDefault(pawn => pawn.Position == middle);
            Check(homePawn != null, "held pawn persisted in the saved lifecycle fixture");
            Hold(homePawn);
            Check(homeLamp.SpotCell == endpoint, "legacy endpoint glower restored after loading the older save");
            Check(Artificial(home, middle) < 0.01f, "pawn-local precise sample is absent from the migrated save");
            Check(Artificial(home, endpoint) > 0.05f, "vanilla endpoint illumination remains after migration");
            Finish("PASS reload");
        }

        private static void CheckPrecise(Map map, IntVec3 origin, Pawn pawn, string label)
        {
            IntVec3 emptyLane = origin + new IntVec3(5, 0, 0);
            Check(pawn != null && pawn.Spawned && pawn.Map == map && pawn.Position == origin + new IntVec3(10, 0, 0),
                label + " retains its held pawn in the beam");
            Check(Artificial(map, pawn.Position) > 0.01f, label + " lights the pawn-local sample");
            Check(Artificial(map, emptyLane) < 0.01f, label + " leaves empty mid-beam ground dark");
        }

        private static void CheckLegacy(Map map, IntVec3 origin, string label)
        {
            Check(Artificial(map, origin + new IntVec3(10, 0, 0)) < 0.01f, label + " has no pawn-local precise sample");
            Check(Artificial(map, origin + new IntVec3(20, 0, 0)) > 0.05f, label + " retains endpoint illumination");
        }

        private System.WeakReference<Map> RemoveAwayMap()
        {
            Map removed = away;
            SearchlightMap controller = removed.GetComponent<SearchlightMap>();
            System.WeakReference<Map> weakMap = new System.WeakReference<Map>(removed);
            Check(awayLamp != null && awayLamp.Spawned && LightCount(controller) == 1,
                "away lamp remains registered when normal map removal begins");
            Check(HasAdapterMapState(removed), "active precise map has an adapter state before unloading");
            MapParent parent = removed.Parent;
            Current.Game.DeinitAndRemoveMap(removed, false);
            if (parent != null && !parent.Destroyed) parent.Destroy();
            Check(!Find.Maps.Contains(removed), "second map removed from the game");
            Check(LightingCount(controller) == 0, "removed map released its sparse lighting samples");
            Check(ShadowPawnCount(controller) == 0, "removed map released tracked pawn references");
            Check(LightCount(controller) == 0, "removed map released registered lamp references");
            Check(!HasAdapterMapState(removed), "removed map was forgotten by the glow-grid adapter");
            awayLamp = null;
            awayPawn = null;
            away = null;
            return weakMap;
        }

        private void RecordCollectedMap(int cycle, System.WeakReference<Map> weakMap)
        {
            long bytesBefore = GC.GetTotalMemory(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long bytesAfter = GC.GetTotalMemory(false);
            bool retained = weakMap.TryGetTarget(out _);
            Record($"map unload cycle={cycle}; managedBytesBeforeGc={bytesBefore}; managedBytesAfterGc={bytesAfter}; weakMapRetainedByGame={retained}");
        }

        private static bool HasAdapterMapState(Map map)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type adapter = typeof(Building_Searchlight).Assembly.GetType("DirectedSearchlights.LightingIntegration");
            MethodInfo method = adapter?.GetMethod("HasMapState", flags);
            if (method == null) throw new MissingMethodException("LightingIntegration.HasMapState(Map)");
            return (bool)method.Invoke(null, new object[] { map });
        }

        private static int LightingCount(SearchlightMap controller)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object mapLighting = typeof(SearchlightMap).GetField("lighting", flags)?.GetValue(controller);
            object aggregate = mapLighting?.GetType().GetField("aggregate", flags)?.GetValue(mapLighting);
            return aggregate is IDictionary dictionary ? dictionary.Count : 0;
        }

        private static int LightCount(SearchlightMap controller)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object lights = typeof(SearchlightMap).GetField("lights", flags)?.GetValue(controller);
            return lights is ICollection collection ? collection.Count : -1;
        }

        private static int ShadowPawnCount(SearchlightMap controller)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object pawns = typeof(SearchlightMap).GetField("shadowPawns", flags)?.GetValue(controller);
            return pawns is ICollection collection ? collection.Count : -1;
        }

        private static Building_Searchlight SpawnLamp(Map map, IntVec3 cell)
        {
            Building_Searchlight lamp = (Building_Searchlight)GenSpawn.Spawn(
                DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret"), cell, map);
            lamp.GetComp<CompPowerTrader>().PowerOn = true;
            Check(lamp.IsOperating, "lamp powered on " + map);
            return lamp;
        }

        private static Pawn SpawnHeldPawn(Map map, IntVec3 cell)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(pawn, cell, map);
            var wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 500000;
            wait.playerForced = true;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
            return pawn;
        }

        private static void Hold(Pawn pawn)
        {
            pawn.pather.StopDead();
            var wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 500000;
            wait.playerForced = true;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
        }

        private static void SetAim(Building_Searchlight lamp, IntVec3 origin) =>
            lamp.SetAimCell(origin + new IntVec3(20, 0, 0));

        private static void ClearArea(Map map, IntVec3 center, int radius)
        {
            CellRect rect = new CellRect(center.x - radius, center.z - radius, radius * 2 + 1, radius * 2 + 1);
            foreach (IntVec3 cell in rect.Cells.Where(c => c.InBounds(map)))
            {
                foreach (Thing thing in cell.GetThingList(map).ToList())
                    if (thing.def.category == ThingCategory.Building || thing is Plant)
                        thing.Destroy(DestroyMode.Vanish);
                if (cell.Roofed(map)) map.roofGrid.SetRoof(cell, null);
            }
        }

        private static float Artificial(Map map, IntVec3 cell) =>
            map.glowGrid.GroundGlowAt(cell, ignoreSky: true);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private void Record(string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.AppendAllText(output, line + Environment.NewLine);
            Log.Message("SearchlightLightingLifecycle: " + line);
        }

        private void Finish(string result)
        {
            finished = true;
            Record(result);
            Application.Quit(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }
    }
}
