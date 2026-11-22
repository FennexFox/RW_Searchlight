using System;
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

namespace SearchlightCoordinationHarness
{
    public sealed partial class CoordinationCheck : GameComponent
    {
        private const BindingFlags Diagnostics = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ScanInterval = 30;
        private const int BatchWindow = 60;
        private const int MarkerLifetime = 180;
        private const int AlertCooldown = 2500;
        private const int RequiredAbsence = 120;
        private static readonly int[] PerformanceCounts = { 1, 10, 50 };
        private readonly List<Building_Searchlight> lamps = new List<Building_Searchlight>();
        private readonly List<Pawn> pawns = new List<Pawn>();
        private readonly List<Thing> fixtures = new List<Thing>();
        private readonly string phase;
        private readonly string resultPath;
        private readonly bool preciseLightingRequested;
        private Map home;
        private IntVec3 origin;
        private int nextTick;
        private Action next;
        private bool started;
        private bool finished;
        private bool readyToSave;

        private static int Now => Find.TickManager.TicksGame;
        private SearchlightMap Network => home.GetComponent<SearchlightMap>();

        public CoordinationCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-coordination-phase", out phase);
            preciseLightingRequested = GenCommandLine.CommandLineArgPassed("ds-coordination-precise-lighting");
            if (phase != "fresh" && phase != "current") return;
            resultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, phase + "-results.txt");
            Write("harness created; assemblyMvid=" + typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D") +
                "; preciseLightingRequested=" + preciseLightingRequested);
        }

        public override void GameComponentUpdate()
        {
            if (resultPath == null || finished || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            try
            {
                if (!started)
                {
                    started = true;
                    home = Find.CurrentMap;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                    SearchlightLightingGame lighting = Current.Game.GetComponent<SearchlightLightingGame>();
                    Require(lighting != null, "game has a searchlight lighting mode component");
                    if (phase == "fresh")
                    {
                        if (preciseLightingRequested) lighting.SetMode(SearchlightLightingMode.PreciseField);
                        Require(lighting.CurrentMode == (preciseLightingRequested
                            ? SearchlightLightingMode.PreciseField
                            : SearchlightLightingMode.LegacyEndpoint), "fresh game uses the requested lighting mode");
                        Write("fresh lighting mode=" + lighting.CurrentMode);
                        origin = home.Center - new IntVec3(30, 0, 0);
                        ClearArena(home, origin, 5, 67, 20);
                        Write("fresh map=" + home.Size + "; origin=" + origin);
                        BeginUniqueLocks();
                    }
                    else
                    {
                        Require(lighting.CurrentMode == (preciseLightingRequested
                            ? SearchlightLightingMode.PreciseField
                            : SearchlightLightingMode.LegacyEndpoint), "current save restored its lighting mode without overriding it");
                        Write("current lighting mode=" + lighting.CurrentMode);
                        BeginCurrentReload();
                    }
                }
                if (readyToSave)
                {
                    GameDataSaveLoader.SaveGame("coordination");
                    Require(File.Exists(GenFilePaths.FilePathForSavedGame("coordination")), "current fixture save exists");
                    Finish(true);
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        public override void GameComponentTick()
        {
            if (!started || finished || next == null) return;
            try
            {
                foreach (Pawn pawn in pawns)
                    if (pawn.Spawned) pawn.pather.StopDead();
                if (Now < nextTick) return;
                Action run = next;
                next = null;
                run();
                Require(next != null || finished || readyToSave, "every fixture schedules its next assertion");
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void After(int ticks, Action run) => At(Now + ticks, run);

        private void At(int tick, Action run)
        {
            Require(tick > Now, "fixture deadlines get a future game tick");
            nextTick = tick;
            next = run;
        }

        private static void ClearArena(Map map, IntVec3 center, int west, int east, int halfHeight)
        {
            for (int x = -west; x <= east; x++)
            for (int z = -halfHeight; z <= halfHeight; z++)
            {
                IntVec3 cell = center + new IntVec3(x, 0, z);
                Require(cell.InBounds(map), "arena is inside generated map: " + cell);
                map.roofGrid.SetRoof(cell, null);
                foreach (Thing thing in cell.GetThingList(map).ToArray())
                    if (thing.def.passability == Traversability.Impassable || thing is Building)
                        thing.Destroy(DestroyMode.Vanish);
            }
            // Removing generated structures can eject their pawn occupants.
            foreach (var lord in map.lordManager.lords.ToArray()) map.lordManager.RemoveLord(lord);
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToArray()) ParkPawn(pawn);
            map.fogGrid.ClearAllFog();
        }

        private IntVec3 Cell(int x, int z) => origin + new IntVec3(x, 0, z);

        private static IntVec3 ProgressCell(Pawn pawn)
        {
            Vector3 position = Vector3.Lerp(pawn.Position.ToVector3Shifted(),
                pawn.pather.nextCell.ToVector3Shifted(), pawn.pather.MovePercentage);
            return new IntVec3(Mathf.FloorToInt(position.x), 0, Mathf.FloorToInt(position.z));
        }

        private Building_Searchlight Lamp(int x, int z, bool tower = false, Map map = null, IntVec3? at = null)
        {
            var def = DefDatabase<ThingDef>.GetNamed(tower ? "DS_SearchlightTower" : "DS_SearchlightTurret");
            var lamp = (Building_Searchlight)ThingMaker.MakeThing(def);
            lamp.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(lamp, at ?? Cell(x, z), map ?? home);
            Power(lamp, true);
            Require(lamp.IsOperating, "fixture lamp has power at " + lamp.Position);
            lamps.Add(lamp);
            return lamp;
        }

        private static void Power(Building_Searchlight lamp, bool enabled) => lamp.GetComp<CompPowerTrader>().PowerOn = enabled;

        private void RequireIlluminated(Building_Searchlight lamp, IntVec3 cell, string message)
        {
            if (!preciseLightingRequested)
            {
                Require(lamp.SpotCell == cell, message + "; endpoint=" + lamp.SpotCell + "; expected=" + cell);
                return;
            }

            float groundGlow = home.glowGrid.GroundGlowAt(cell, ignoreSky: true);
            Require(groundGlow > 0.001f, message + "; measured field ground glow=" + groundGlow.ToString("F4"));
        }

        private void RequireNotIlluminated(Building_Searchlight lamp, IntVec3 cell, string message)
        {
            if (!preciseLightingRequested)
            {
                Require(lamp.SpotCell != cell, message + "; endpoint=" + lamp.SpotCell + "; blocked=" + cell);
                return;
            }

            float groundGlow = home.glowGrid.GroundGlowAt(cell, ignoreSky: true);
            Require(groundGlow < 0.001f, message + "; measured field ground glow=" + groundGlow.ToString("F4"));
        }

        private Pawn Hostile(int x, int z, Map map = null, IntVec3? at = null)
        {
            var kind = DefDatabase<PawnKindDef>.GetNamed("Pirate", false) ?? PawnKindDefOf.Colonist;
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfAncientsHostile,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false));
            Spawn(pawn, at ?? Cell(x, z), map ?? home);
            pawns.Add(pawn);
            return pawn;
        }

        private static void Spawn(Pawn pawn, IntVec3 cell, Map map)
        {
            GenSpawn.Spawn(pawn, cell, map);
            Job wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 10000;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
            Require(pawn.HostileTo(Faction.OfPlayer) && !pawn.Downed && !pawn.Fogged(), "fixture pawn is a detectable hostile");
        }

        private void Wall(int x, int z)
        {
            fixtures.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), Cell(x, z), home));
        }

        private void ClearFixtures()
        {
            foreach (Building_Searchlight lamp in lamps)
                if (lamp.Spawned) lamp.Destroy(DestroyMode.Vanish);
            foreach (Pawn pawn in pawns) ParkPawn(pawn);
            foreach (Thing thing in fixtures)
                if (thing.Spawned) thing.Destroy(DestroyMode.Vanish);
            foreach (Pawn ambient in home.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).ToArray())
            {
                var lord = home.lordManager.LordOf(ambient);
                if (lord != null) home.lordManager.RemoveLord(lord);
                ParkPawn(ambient);
            }
            lamps.Clear();
            pawns.Clear();
            fixtures.Clear();
            Require(Count(Network, "ReservationCount") == 0, "clearing fixtures leaves zero reservations");
        }

        private static void ParkPawn(Pawn pawn)
        {
            if (pawn.Spawned) pawn.DeSpawn();
            if (!pawn.Destroyed && !pawn.Discarded && !pawn.IsWorldPawn())
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
        }

        private static int Count(object instance, string name) => (int)Read(instance, name);

        private static object Read(object instance, string name)
        {
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, Diagnostics);
            if (property != null) return property.GetValue(instance);
            FieldInfo field = type.GetField(name, Diagnostics);
            if (field != null) return field.GetValue(instance);
            throw new MissingMemberException(type.FullName, name);
        }

        private static int Reserved(SearchlightMap map, Pawn pawn)
        {
            MethodInfo method = typeof(SearchlightMap).GetMethod("ReservationsFor", Diagnostics);
            return (int)method.Invoke(map, new object[] { pawn });
        }

        private static int Alerts(SearchlightMap map, string counter) => Count(Read(map, "alerts"), counter);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("ASSERT: " + message);
        }

        private void Write(string line)
        {
            File.AppendAllText(resultPath, line + Environment.NewLine);
            Log.Message("SearchlightCoordinationHarness: " + line);
        }

        private void Fail(Exception error)
        {
            Write("tick=" + Now + "; " + error);
            Finish(false);
        }

        private void Finish(bool success)
        {
            finished = true;
            Write((success ? "PASS " : "FAIL ") + phase);
            Application.Quit(success ? 0 : 1);
        }
    }
}
