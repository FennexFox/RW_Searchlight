using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightRuntimeHarness
{
    // This assembly is loaded only by the generated isolated test mod.
    public sealed class RuntimeCheck : GameComponent
    {
        private static string resultPath;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int phase;
        private static bool reloadRequested;
        private static bool screenshotRequested;
        private static IntVec3 origin;
        private static Pawn target;
        private static Pawn shooter;
        private static readonly List<Thing> lamps = new List<Thing>();
        private static readonly List<Thing> generators = new List<Thing>();
        private static Thing wall;
        private static int phaseTick;
        private static bool failed;
        private static int targetId;
        private static long perfStartMs;
        private static float ceOnGlow = float.NaN;
        private static float ceOnShift = float.NaN;
        private static float ceOffGlow = float.NaN;
        private static float ceOffShift = float.NaN;
        private static long lastStallLogMs;
        private static IntVec3 ceShooterAnchor = IntVec3.Invalid;
        private static IntVec3 ceTargetAnchor = IntVec3.Invalid;

        public RuntimeCheck(Game game)
        {
            if (resultPath == null)
            {
                GenCommandLine.TryGetCommandLineArg("ds-result", out resultPath);
                if (string.IsNullOrEmpty(resultPath)) resultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "runtime-results.txt");
                Write("harness-created; game=" + game + "; args=" + Environment.CommandLine);
            }
        }

        public override void StartedNewGame()
        {
            Write("new-game; map=" + Find.CurrentMap + "; elapsedMs=" + Clock.ElapsedMilliseconds);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        public override void LoadedGame()
        {
            Write("loaded-game; map=" + Find.CurrentMap + "; elapsedMs=" + Clock.ElapsedMilliseconds);
            if (reloadRequested)
            {
                var map = Find.CurrentMap;
                var found = map.listerThings.AllThings.Where(t => t.def.defName == "DS_SearchlightTurret" || t.def.defName == "DS_SearchlightTower").ToList();
                Write("reload-lamps=" + found.Count + "; snapshots=" + string.Join(" | ", found.Take(2).Select(Snapshot).ToArray()));
                Check(found.Count == 10, "save-load retained all ten lamps");
                Check(found.All(t => Convert.ToBoolean(Read(t, "IsOperating"))), "save-load retained power and operation");
                Check(found.Any(t => (Read(t, "LockedTarget") as Pawn)?.thingIDNumber == targetId), "save-load retained/reacquired target lock");
                Check(found.All(t => ((IntVec3)Read(t, "SpotCell")).IsValid), "save-load restored light spots");
                Finish(!failed);
            }
        }

        public override void GameComponentUpdate()
        {
            if (phase == 0 && Find.CurrentMap != null && Current.ProgramState == ProgramState.Playing)
            {
                try { Setup(); }
                catch (Exception ex) { Fail("setup-update: " + ex); }
            }
            if (phase > 0 && phase < 7 && Clock.ElapsedMilliseconds - lastStallLogMs > 15000)
            {
                lastStallLogMs = Clock.ElapsedMilliseconds;
                Write("progress; phase=" + phase + "; tick=" + Find.TickManager.TicksGame + "; speed=" + Find.TickManager.CurTimeSpeed + "; elapsedMs=" + Clock.ElapsedMilliseconds);
            }
            if (phase == 7 && !reloadRequested)
            {
                reloadRequested = true;
                if (GenCommandLine.CommandLineArgPassed("ds-no-reload"))
                {
                    Write("save-load skipped by command line after native graphics crash in prior run");
                    Finish(!failed);
                    return;
                }
                try
                {
                    Write("save-start; elapsedMs=" + Clock.ElapsedMilliseconds);
                    GameDataSaveLoader.SaveGame("searchlight-runtime");
                    Write("save-finished; bytes=" + new FileInfo(GenFilePaths.FilePathForSavedGame("searchlight-runtime")).Length);
                    GameDataSaveLoader.LoadGame("searchlight-runtime");
                }
                catch (Exception ex) { Fail("save-load: " + ex); }
            }
        }

        public override void GameComponentTick()
        {
            if (phase >= 7 || Find.CurrentMap == null) return;
            try
            {
                if (shooter != null && shooter.Spawned) shooter.pather.StopDead();
                if (phase == 0) Setup();
                int age = Find.TickManager.TicksGame - phaseTick;
                if (phase == 1 && age >= 180) Baseline();
                else if (phase == 2 && age >= 180) WallAndPower();
                else if (phase == 3 && age >= 600) Performance();
                else if (phase == 4 && age >= 360) CompleteSimulation();
                else if (phase == 5 && age >= 300) PowerOff();
                else if (phase == 6 && age >= 600) PowerRestored();
            }
            catch (Exception ex) { Fail("tick: " + ex); }
        }

        private static void Setup()
        {
            var map = Find.CurrentMap;
            Write("setup-begin; elapsedMs=" + Clock.ElapsedMilliseconds);
            origin = FindOpenFixture(map);
            Write("open-fixture; origin=" + origin + "; elapsedMs=" + Clock.ElapsedMilliseconds);
            var turret = DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret", false);
            var tower = DefDatabase<ThingDef>.GetNamed("DS_SearchlightTower", false);
            if (turret == null || tower == null) throw new Exception("Searchlight defs missing: turret=" + turret + ", tower=" + tower);
            var power = DefDatabase<ThingDef>.GetNamed("VanometricPowerCell", false);
            if (power == null) throw new Exception("VanometricPowerCell def missing");
            lamps.Add(GenSpawn.Spawn(turret, origin, map));
            lamps.Add(GenSpawn.Spawn(tower, origin + new IntVec3(0, 0, 8), map));
            generators.Add(GenSpawn.Spawn(power, origin + new IntVec3(-1, 0, 0), map));
            generators.Add(GenSpawn.Spawn(power, origin + new IntVec3(-1, 0, 8), map));
            var pawnKind = DefDatabase<PawnKindDef>.GetNamed("Pirate", false) ?? PawnKindDefOf.Colonist;
            target = PawnGenerator.GeneratePawn(pawnKind, Faction.OfAncientsHostile);
            targetId = target.thingIDNumber;
            GenSpawn.Spawn(target, origin + new IntVec3(12, 0, 0), map);
            target.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, origin + new IntVec3(18, 0, 0)), JobCondition.InterruptForced);
            shooter = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(shooter, origin + new IntVec3(-18, 0, 0), map);
            shooter.drafter.Drafted = true;
            shooter.jobs.StopAll();
            shooter.pather.StopDead();
            var shooterHold = JobMaker.MakeJob(JobDefOf.Wait);
            shooterHold.expiryInterval = 5000;
            shooter.jobs.StartJob(shooterHold, JobCondition.InterruptForced);
            var ticks = Find.TickManager.TicksGame;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 1; ++i)
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2500);
            Write("setup; origin=" + origin + "; target=" + target.Position + "; localHour=" + GenLocalDate.HourOfDay(map) + "; skyGlow=" + map.skyManager.CurSkyGlow + "; tickShift=" + (Find.TickManager.TicksGame - ticks) + "; power=" + PowerSnapshot());
            Advance(1);
        }

        private static IntVec3 FindOpenFixture(Map map)
        {
            var desired = map.Center + new IntVec3(20, 0, 20);
            for (int radius = 0; radius <= 75; ++radius)
            for (int dx = -radius; dx <= radius; ++dx)
            for (int dz = -radius; dz <= radius; ++dz)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
                var c = desired + new IntVec3(dx, 0, dz);
                if (OpenFixtureAt(map, c)) return c;
            }
            throw new Exception("Could not find fully open searchlight fixture near map center");
        }

        private static bool OpenFixtureAt(Map map, IntVec3 c)
        {
            var targetCell = c + new IntVec3(18, 0, 0);
            var shooterCell = c + new IntVec3(-18, 0, 0);
            if (!OpenCell(map, targetCell) || !OpenCell(map, shooterCell)) return false;
            var starts = new List<IntVec3> { c, c + new IntVec3(0, 0, 8) };
            for (int i = 0; i < 8; ++i)
                starts.Add(c + new IntVec3(-12 + (i % 4) * 4, 0, -12 + (i / 4) * 4));
            foreach (var start in starts)
            {
                if (!OpenCell(map, start) || !OpenCell(map, start + new IntVec3(-1, 0, 0)) ||
                    !OpenCell(map, start + new IntVec3(-1, 0, 1)) ||
                    !BeamField.Clear(map, start, targetCell)) return false;
            }
            return true;
        }

        private static bool OpenCell(Map map, IntVec3 c)
        {
            return c.InBounds(map) && c.Walkable(map) && !c.Roofed(map);
        }

        private static void Baseline()
        {
            Write("baseline; elapsedMs=" + Clock.ElapsedMilliseconds + "; target=" + target.Position + "; snapshots=" + string.Join(" | ", lamps.Select(Snapshot).ToArray()) + "; power=" + PowerSnapshot());
            Check(lamps.All(t => Convert.ToBoolean(Read(t, "IsOperating"))), "both original lamps powered");
            Check(lamps.All(t => (Read(t, "LockedTarget") as Pawn) == target), "both original lamps locked hostile pawn");
            Check(lamps.All(t => Convert.ToInt32(Read(t, "AcquisitionCount")) > 0), "both original lamps ran acquisition tick");
            Check(lamps.All(t => ((IntVec3)Read(t, "SpotCell")).IsValid), "both original lamps have light spots");
            Check(target.Position != origin + new IntVec3(12, 0, 0), "hostile pawn moved");
            var spot = (IntVec3)Read(lamps[0], "SpotCell");
            Write("illumination; targetGlow=" + Find.CurrentMap.glowGrid.GroundGlowAt(target.Position, ignoreSky: true) + "; spotGlow=" + Find.CurrentMap.glowGrid.GroundGlowAt(spot, ignoreSky: true) + "; spot=" + spot + "; target=" + target.Position);
            Check(Find.CurrentMap.glowGrid.GroundGlowAt(spot, ignoreSky: true) > 0.01f, "spot ground illuminated at night");
            Check(Find.CurrentMap.glowGrid.GroundGlowAt(target.Position, ignoreSky: true) > 0.01f, "target ground illuminated at night");
            var hold = JobMaker.MakeJob(JobDefOf.Wait);
            hold.expiryInterval = 3000;
            target.jobs.StartJob(hold, JobCondition.InterruptForced);
            var map = Find.CurrentMap;
            var wallDef = DefDatabase<ThingDef>.GetNamed("Wall", false);
            var midpoint = new IntVec3((origin.x + target.Position.x) / 2, 0, (origin.z + target.Position.z) / 2);
            wall = GenSpawn.Spawn(wallDef, midpoint, map);
            if (!screenshotRequested)
            {
                screenshotRequested = true;
                Find.CameraDriver.JumpToCurrentMapLoc(origin + new IntVec3(5, 0, 2));
                ScreenshotTaker.QueueSilentScreenshot();
            }
            Advance(2);
        }

        private static void WallAndPower()
        {
            Write("wall; elapsedMs=" + Clock.ElapsedMilliseconds + "; wall=" + wall.Position + "; target=" + target.Position + "; snapshots=" + string.Join(" | ", lamps.Select(Snapshot).ToArray()));
            Check((Read(lamps[0], "LockedTarget") as Pawn) != target, "turret loses lock on blocked pawn");
            Check((Read(lamps[1], "LockedTarget") as Pawn) == target, "tower retains lock through wall");
            wall.Destroy();
            var map = Find.CurrentMap;
            var turret = DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret", false);
            var power = DefDatabase<ThingDef>.GetNamed("VanometricPowerCell", false);
            for (int i = 0; i < 8; ++i)
            {
                var p = origin + new IntVec3(-12 + (i % 4) * 4, 0, -12 + (i / 4) * 4);
                lamps.Add(GenSpawn.Spawn(turret, p, map));
                generators.Add(GenSpawn.Spawn(power, p + new IntVec3(-1, 0, 0), map));
            }
            Advance(3);
        }

        private static void Performance()
        {
            int active = lamps.Count(t => Convert.ToBoolean(Read(t, "IsOperating") ?? false));
            int spots = Find.CurrentMap.listerThings.AllThings.Count(t => t.def.defName == "DS_LightSpot");
            Write("ten-lamps; elapsedMs=" + Clock.ElapsedMilliseconds + "; count=" + lamps.Count + "; active=" + active + "; spot-count=" + spots + "; states=" + string.Join(" | ", lamps.Select((t,i) => t.Position + ":" + PowerSnapshotOne(t) + "," + Read(t, "IsOperating") + "," + Read(t, "SpotCell") + ",generator=" + generators[i].Position + ",generatorSpawned=" + generators[i].Spawned).ToArray()));
            Check(active == 10, "all ten lamps powered for performance window");
            Check(spots == 10, "one light spot per active lamp");
            CaptureCE("on", out ceOnGlow, out ceOnShift);
            perfStartMs = Clock.ElapsedMilliseconds;
            Advance(4);
        }

        private static void CompleteSimulation()
        {
            Write("performance-window; elapsedMs=" + Clock.ElapsedMilliseconds + "; 360tickWallMs=" + (Clock.ElapsedMilliseconds - perfStartMs) + "; snapshots=" + string.Join(" | ", lamps.Take(2).Select(Snapshot).ToArray()));
            foreach (var lamp in lamps) (lamp as ThingWithComps).GetComp<CompFlickable>().SwitchIsOn = false;
            Advance(5);
        }

        private static void PowerOff()
        {
            Write("flick-off; snapshots=" + Snapshot(lamps[0]) + "; power=" + PowerSnapshotOne(lamps[0]) + "; flick=" + (lamps[0] as ThingWithComps).GetComp<CompFlickable>().SwitchIsOn + "; spots=" + Find.CurrentMap.listerThings.AllThings.Count(t => t.def.defName == "DS_LightSpot"));
            Check(lamps.All(t => !Convert.ToBoolean(Read(t, "IsOperating"))), "flick off stops all ten lamps");
            Check(lamps.All(t => !((IntVec3)Read(t, "SpotCell")).IsValid), "flick off removes all spots");
            CaptureCE("off", out ceOffGlow, out ceOffShift);
            foreach (var lamp in lamps) (lamp as ThingWithComps).GetComp<CompFlickable>().SwitchIsOn = true;
            Advance(6);
        }

        private static void PowerRestored()
        {
            Write("flick-on; snapshots=" + Snapshot(lamps[0]) + "; power=" + PowerSnapshotOne(lamps[0]) + "; flick=" + (lamps[0] as ThingWithComps).GetComp<CompFlickable>().SwitchIsOn);
            Check(lamps.All(t => Convert.ToBoolean(Read(t, "IsOperating"))), "flick on resumes all ten lamps");
            Check(lamps.All(t => ((IntVec3)Read(t, "SpotCell")).IsValid), "flick on restores all spots");
            float ceRestoredGlow, ceRestoredShift;
            if (CaptureCE("restored", out ceRestoredGlow, out ceRestoredShift))
            {
                Check(shooter.Position == ceShooterAnchor && target.Position == ceTargetAnchor, "CE shooter and target stayed fixed across OFF/ON readings");
                Check(ceOffGlow < ceOnGlow, "CE combat glow rises with searchlights");
                Check(ceOffShift > ceOnShift, "CE lighting penalty falls with searchlights");
                Check(ceRestoredGlow > ceOffGlow, "CE combat glow returns after flick on");
            }
            Advance(7);
        }

        private static bool CaptureCE(string label, out float glow, out float shift)
        {
            glow = shift = float.NaN;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CombatExtended");
            if (assembly == null) { Write("ce-" + label + "; assembly=absent"); return false; }
            if (!ceShooterAnchor.IsValid) { ceShooterAnchor = shooter.Position; ceTargetAnchor = target.Position; }
            else Check(shooter.Position == ceShooterAnchor && target.Position == ceTargetAnchor, "CE positions fixed at " + label);
            var trackerType = assembly.GetType("CombatExtended.LightingTracker");
            var tracker = Find.CurrentMap.components.FirstOrDefault(c => trackerType.IsInstanceOfType(c));
            if (tracker == null) throw new Exception("CE LightingTracker component missing");
            glow = Convert.ToSingle(trackerType.GetMethod("CombatGlowAtFor").Invoke(tracker, new object[] { shooter.Position, target.Position }));
            var utilityType = assembly.GetType("CombatExtended.CE_Utility");
            shift = Convert.ToSingle(utilityType.GetMethod("GetLightingShift").Invoke(null, new object[] { shooter, glow }));
            Write("ce-" + label + "; shooter=" + shooter.Position + "; target=" + target.Position + "; tick=" + Find.TickManager.TicksGame + "; combatGlow=" + glow + "; lightingShift=" + shift + "; skyGlow=" + Find.CurrentMap.skyManager.CurSkyGlow);
            return true;
        }

        private static string Snapshot(Thing thing)
        {
            return thing.def.defName + "@" + thing.Position + ":mode=" + Read(thing, "CurrentMode") + ",target=" + Read(thing, "LockedTarget") + ",heading=" + Read(thing, "AimHeading") + ",spot=" + Read(thing, "SpotCell") + ",operating=" + Read(thing, "IsOperating") + ",acquisitions=" + Read(thing, "AcquisitionCount") + ",glow=" + Find.CurrentMap.glowGrid.GroundGlowAt(thing.Position + new IntVec3(4, 0, 0), ignoreSky: true);
        }

        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return p != null ? p.GetValue(obj, null) : "missing-property";
        }

        private static string PowerSnapshot()
        {
            return string.Join("|", lamps.Select(PowerSnapshotOne).ToArray());
        }
        private static string PowerSnapshotOne(Thing t) => (t as ThingWithComps)?.GetComp<CompPowerTrader>()?.PowerOn.ToString() ?? "no-comp";

        private static void Advance(int next) { phase = next; phaseTick = Find.TickManager.TicksGame; }
        private static void Check(bool condition, string name)
        {
            Write((condition ? "CHECK PASS " : "CHECK FAIL ") + name);
            if (!condition) failed = true;
        }
        private static void Write(string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.AppendAllText(resultPath, DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            Log.Message("SearchlightRuntimeHarness: " + line);
        }
        private static void Fail(string message) { Write("FAIL " + message); Finish(false); }
        private static void Finish(bool success)
        {
            phase = 99;
            Write((success ? "PASS" : "FAIL") + "; totalElapsedMs=" + Clock.ElapsedMilliseconds);
            Application.Quit(success ? 0 : 1);
        }
    }
}
