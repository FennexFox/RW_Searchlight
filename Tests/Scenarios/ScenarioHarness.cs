using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightScenarioHarness
{
    // Loaded only in the isolated test profile created by Run-Scenarios.ps1.
    public sealed partial class ScenarioCheck : GameComponent
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly List<Building_Searchlight> lamps = new List<Building_Searchlight>();
        private static readonly List<Thing> blockers = new List<Thing>();
        private static string resultPath;
        private static int step;
        private static int stepTick;
        private static IntVec3 origin;
        private static Building_Searchlight turret;
        private static Building_Searchlight tower;
        private static Pawn target;
        private static Building_Door door;
        private static long zeroStart;
        private static long zeroElapsed;
        private static long tenStart;
        private static float automaticHeadingStart;

        public ScenarioCheck(Game game)
        {
            if (resultPath != null) return;
            GenCommandLine.TryGetCommandLineArg("ds-scenario-result", out resultPath);
            if (string.IsNullOrEmpty(resultPath))
                resultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "scenario-results.txt");
            Write("created; game=" + VersionControl.CurrentVersionString +
                "; assembly=" + typeof(Building_Searchlight).Assembly.GetName().Version +
                "; assemblyMvid=" + typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
        }

        public override void StartedNewGame()
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Write("new-game; map=" + Find.CurrentMap.Size);
        }

        public override void GameComponentTick()
        {
            if (step == 99 || Find.CurrentMap == null) return;
            try
            {
                // Keep the hostile pawn at a reproducible cell while the map and AI tick normally.
                if (target != null && target.Spawned && (step != TrackingStep || trackingPhase != TrackingPhase.FollowPawn))
                    target.pather.StopDead();
                int age = Find.TickManager.TicksGame - stepTick;
                switch (step)
                {
                    case 0: Setup(); break;
                    case 1: if (age >= 90) Fixed(); break;
                    case 2: if (age >= 90) Sweep(); break;
                    case 3: if (age >= 30) OutsideRange(); break;
                    case 4: if (age >= 90) InsideRange(); break;
                    case 5: if (age >= 90) LeaveRange(); break;
                    case 6: if (age >= 90) Wall(); break;
                    case 7: if (age >= 90) ClosedDoor(); break;
                    case 8: if (age >= 90) OpenDoor(); break;
                    case 9: if (age >= 35) TargetRoof(); break;
                    case 10: if (age >= 30) TowerRoof(); break;
                    case 11: if (age >= 180) ZeroLamps(); break;
                    case 12: if (age >= 180) TenLamps(); break;
                    case TrackingStep: TickTracking(age); break;
                    case SweepApiStep: if (age >= 1) TickSweepApi(age); break;
                    case NightOnlyStep: if (age >= 90) TickNightOnly(age); break;
                }
            }
            catch (Exception ex) { Fail("step=" + step + "; " + ex); }
        }

        private static void Setup()
        {
            Map map = Find.CurrentMap;
            origin = new IntVec3(Math.Max(25, Math.Min(map.Size.x - 75, map.Center.x - 55)), 0, map.Center.z);
            List<Pawn> ambientPawns = map.mapPawns.AllPawnsSpawned.ToList();
            foreach (Pawn pawn in ambientPawns)
                pawn.DeSpawn();
            for (int z = -12; z <= 12; z++)
            for (int x = -2; x <= 62; x++)
            {
                IntVec3 c = origin + new IntVec3(x, 0, z);
                if (!c.InBounds(map)) continue;
                map.roofGrid.SetRoof(c, null);
                foreach (Thing thing in c.GetThingList(map).ToList())
                    if (thing.def.passability == Traversability.Impassable) thing.Destroy(DestroyMode.Vanish);
            }
            turret = SpawnLamp("DS_SearchlightTurret", origin);
            tower = SpawnLamp("DS_SearchlightTower", origin + new IntVec3(0, 0, 8));
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(origin + new IntVec3(20, 0, 0));
            tower.SetMode(SearchlightMode.Fixed);
            tower.SetAimCell(tower.Position + new IntVec3(20, 0, 0));
            SpawnTarget(origin + new IntVec3(12, 0, 0));
            Write("setup; origin=" + origin + "; target=" + target.Position + "; ambientPawnsDespawned=" + ambientPawns.Count +
                "; policy=" + turret.Policy + "/" + tower.Policy + "; power=" + Power(turret) + "/" + Power(tower));
            Next(1);
        }

        private static void Fixed()
        {
            Check(turret.CurrentMode == SearchlightMode.Fixed, "fixed mode");
            Check(turret.LockedTarget == null, "fixed mode never locks");
            Check(turret.AcquisitionCount == 0, "fixed mode never polls targets");
            Check(turret.IsOperating && turret.SpotCell.IsValid, "fixed mode operating and illuminating");
            Write("fixed PASS; spot=" + turret.SpotCell + "; acquisitions=" + turret.AcquisitionCount);
            target.DeSpawn(); target = null;
            turret.SetMode(SearchlightMode.Sweep);
            turret.SetAimCell(origin + new IntVec3(20, 0, 0));
            Next(2);
        }

        private static void Sweep()
        {
            Check(turret.CurrentMode == SearchlightMode.Sweep, "sweep mode");
            Check(turret.AcquisitionCount >= 2, "sweep acquisition cadence");
            Check(turret.LockedTarget == null, "sweep without target");
            Check(Mathf.Abs(Mathf.DeltaAngle(90f, turret.AimHeading)) > 2f, "sweep moves heading");
            Write("sweep PASS; heading=" + turret.AimHeading + "; acquisitions=" + turret.AcquisitionCount);
            turret.SetMode(SearchlightMode.Automatic);
            automaticHeadingStart = turret.AimHeading;
            SpawnTarget(origin + new IntVec3(61, 0, 0));
            Next(3);
        }

        private static void OutsideRange()
        {
            Check(target.Position.x == origin.x + 61, "61-cell target stayed in place");
            Check(turret.CurrentMode == SearchlightMode.Automatic, "automatic mode");
            Check(turret.LockedTarget == null, "61-cell target excluded");
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == null, "manual targeting also rejects a 61-cell pawn");
            Write("automatic-scan; start=" + automaticHeadingStart + "; end=" + turret.AimHeading + "; operating=" + turret.IsOperating);
            Check(Mathf.Abs(Mathf.DeltaAngle(automaticHeadingStart, turret.AimHeading)) > 2f, "automatic mode scans without a target");
            Write("range61 PASS; acquisitions=" + turret.AcquisitionCount);
            target.DeSpawn(); target = null;
            SpawnTarget(origin + new IntVec3(60, 0, 0));
            Next(4);
        }

        private static void InsideRange()
        {
            Check(target.Position.x == origin.x + 60, "60-cell target stayed in place");
            Check(turret.LockedTarget == target, "60-cell target acquired");
            Check(turret.AimRange == 60f && turret.SpotCell == target.Position,
                $"60-cell beam reaches and illuminates the acquired pawn (aim={turret.AimRange:R} at {turret.AimHeading:R}, " +
                $"spot={turret.SpotCell}, target={target.Position}, origin={origin})");
            var spot = target.Position.GetThingList(Find.CurrentMap).Single(t => t.def.defName == "DS_LightSpot");
            Check(Math.Abs(spot.TryGetComp<CompGlower>().GlowRadius - 3f) < 0.01f, "four-tile beam has physical radius two plus grid origin offset");
            Write("range60 PASS; locked=" + turret.LockedTarget + "; acquisitions=" + turret.AcquisitionCount);
            target.DeSpawn();
            GenSpawn.Spawn(target, origin + new IntVec3(61, 0, 0), Find.CurrentMap);
            Next(5);
        }

        private static void LeaveRange()
        {
            Check(target.Position.x == origin.x + 61, "locked target moved beyond range");
            Check(turret.LockedTarget == null, "lock released when target leaves range");
            Write("leave-range PASS; lock=" + turret.LockedTarget);
            target.DeSpawn(); target = null;
            SpawnTarget(origin + new IntVec3(12, 0, 0));
            for (int z = 0; z <= 8; z++)
                blockers.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel),
                    origin + new IntVec3(5, 0, z), Find.CurrentMap));
            tower.SetMode(SearchlightMode.Automatic);
            Next(6);
        }

        private static void Wall()
        {
            Check(!BeamField.Clear(Find.CurrentMap, turret.Position, target.Position), "wall blocks low line of sight");
            Check(turret.LockedTarget == null, "turret cannot lock through wall");
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == null, "turret manual targeting also requires line of sight");
            Check(tower.LockedTarget == target, "tower locks through wall");
            Check(tower.SpotCell.IsValid && tower.SpotCell.x < origin.x + 5, "tower illuminates the near side of the wall");
            var spot = tower.SpotCell.GetThingList(Find.CurrentMap).Single(t => t.def.defName == "DS_LightSpot");
            float radius = spot.TryGetComp<CompGlower>().GlowRadius;
            Check(radius >= 1.5f && radius < 2.5f, "clipped beam has a narrower illumination footprint");
            Write("wall PASS; turretSpot=" + turret.SpotCell + "; towerSpot=" + tower.SpotCell);
            blockers[0].Destroy(DestroyMode.Vanish);
            blockers.RemoveAt(0);
            door = (Building_Door)GenSpawn.Spawn(ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Door"), ThingDefOf.Steel),
                origin + new IntVec3(5, 0, 0), Find.CurrentMap);
            Next(7);
        }

        private static void ClosedDoor()
        {
            Check(!door.Open, "door initially closed");
            Check(!BeamField.Clear(Find.CurrentMap, turret.Position, target.Position), "closed door occludes");
            Check(turret.LockedTarget == null, "closed door prevents lock");
            Write("closed-door PASS");
            door.StartManualOpenBy(null);
            Next(8);
        }

        private static void OpenDoor()
        {
            Check(door.Open, "door open");
            Check(BeamField.Clear(Find.CurrentMap, turret.Position, target.Position), "open door transmits line of sight");
            Check(turret.LockedTarget == target, "turret acquires through open door");
            Write("open-door PASS; turretLock=" + turret.LockedTarget);
            Find.CurrentMap.roofGrid.SetRoof(target.Position, RoofDefOf.RoofConstructed);
            Next(9);
        }

        private static void TargetRoof()
        {
            Check(door.Open && BeamField.Clear(Find.CurrentMap, turret.Position, target.Position), "door remains open for roof comparison");
            Check(tower.LockedTarget == null, "tower rejects roofed target");
            Check(turret.LockedTarget == target, "turret retains visible roofed target");
            Write("target-roof PASS; turret=" + turret.LockedTarget + "; tower=" + tower.LockedTarget);
            Find.CurrentMap.roofGrid.SetRoof(tower.Position, RoofDefOf.RoofConstructed);
            Next(10);
        }

        private static void TowerRoof()
        {
            Check(!tower.IsOperating, "roof disables tower");
            Check(!tower.SpotCell.IsValid, "roof clears tower spot");
            Check(turret.IsOperating, "roof does not disable separate turret");
            Write("tower-roof PASS");
            target.DeSpawn(); target = null;
            turret.Destroy(DestroyMode.Vanish);
            tower.Destroy(DestroyMode.Vanish);
            lamps.Clear();
            door.Destroy(DestroyMode.Vanish);
            foreach (Thing wall in blockers) if (!wall.Destroyed) wall.Destroy(DestroyMode.Vanish);
            BeginTracking();
        }

        private static void BeginPerformance()
        {
            zeroStart = Clock.ElapsedMilliseconds;
            Next(11);
        }

        private static void ZeroLamps()
        {
            zeroElapsed = Clock.ElapsedMilliseconds - zeroStart;
            Check(Find.CurrentMap.listerThings.AllThings.Count(t => t.def.defName == "DS_LightSpot") == 0, "zero lamps clear spots");
            Write("perf-zero; ticks=180; elapsedMs=" + zeroElapsed);
            for (int i = 0; i < 10; i++)
            {
                IntVec3 p = origin + new IntVec3((i % 5) * 4, 0, -10 + (i / 5) * 4);
                Building_Searchlight lamp = SpawnLamp("DS_SearchlightTurret", p);
                lamp.SetMode(SearchlightMode.Fixed);
                lamp.SetAimCell(p + new IntVec3(15, 0, 0));
            }
            tenStart = Clock.ElapsedMilliseconds;
            Next(12);
        }

        private static void TenLamps()
        {
            long elapsed = Clock.ElapsedMilliseconds - tenStart;
            Check(lamps.Count == 10 && lamps.All(l => l.IsOperating && l.SpotCell.IsValid), "ten lamps operate with spots");
            Write("perf-ten; ticks=180; elapsedMs=" + elapsed + "; zeroMs=" + zeroElapsed + "; active=" + lamps.Count);
            BeginSweepApi();
        }

        private static Building_Searchlight SpawnLamp(string defName, IntVec3 cell)
        {
            var lamp = (Building_Searchlight)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed(defName), cell, Find.CurrentMap);
            lamp.SetPolicy(AssignmentPolicy.Independent);
            Check(lamp.Policy == AssignmentPolicy.Independent, defName + " uses Independent policy");
            lamp.GetComp<CompPowerTrader>().PowerOn = true;
            Check(lamp.IsOperating, defName + " powered at " + cell);
            lamps.Add(lamp);
            return lamp;
        }

        private static void SpawnTarget(IntVec3 cell)
        {
            target = GenerateHostile();
            SpawnHostile(target, cell);
        }

        private static Pawn GenerateHostile()
        {
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamed("Pirate", false) ?? PawnKindDefOf.Colonist;
            return PawnGenerator.GeneratePawn(kind, Faction.OfAncientsHostile);
        }

        private static void SpawnHostile(Pawn pawn, IntVec3 cell)
        {
            GenSpawn.Spawn(pawn, cell, Find.CurrentMap);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
            Check(pawn.HostileTo(Faction.OfPlayer), "test pawn hostile");
        }

        private static bool Power(Building_Searchlight light) => light.GetComp<CompPowerTrader>().PowerOn;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
        private static void Next(int next) { step = next; stepTick = Find.TickManager.TicksGame; }
        private static void Write(string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.AppendAllText(resultPath, DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            Log.Message("SearchlightScenarioHarness: " + line);
        }
        private static void Fail(string message) { Write("FAIL " + message); Finish(false); }
        private static void Finish(bool success)
        {
            step = 99;
            Write((success ? "PASS" : "FAIL") + "; totalElapsedMs=" + Clock.ElapsedMilliseconds);
            Application.Quit(success ? 0 : 1);
        }
    }
}
