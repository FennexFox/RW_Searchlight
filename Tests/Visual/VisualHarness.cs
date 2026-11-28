using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightVisualHarness
{
    public sealed class CaptureCheck : GameComponent
    {
        private readonly string output;
        private readonly bool preciseLighting;
        private PreciseVisualFixture preciseFixture;
        private Building_Searchlight lamp;
        private IntVec3 origin;
        private int stage;
        private int frameToCapture;
        private string screenshotPath;
        private Building_Searchlight[] sentries;
        private Pawn[] hostiles;
        private int raidTick;

        public CaptureCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-visual-output", out output);
            preciseLighting = GenCommandLine.CommandLineArgPassed("ds-visual-precise");
            if (!string.IsNullOrEmpty(output))
                Record("assemblyMvid=" + typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
        }

        public override void GameComponentUpdate()
        {
            if (output == null || stage == 5 || Find.CurrentMap == null) return;
            try
            {
                if (lamp == null)
                {
                    SetUpMap();
                    return;
                }

                lamp.GetComp<CompPowerTrader>().PowerOn = true;
                if (preciseFixture != null)
                {
                    preciseFixture.Update();
                    return;
                }
                if (sentries != null)
                    foreach (var sentry in sentries)
                        sentry.GetComp<CompPowerTrader>().PowerOn = true;
                if (screenshotPath != null)
                {
                    if (Time.frameCount == frameToCapture)
                        ScreenCapture.CaptureScreenshot(screenshotPath);
                    if (File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 0)
                        Advance();
                    return;
                }

                var map = Find.CurrentMap;
                if (stage == 4)
                {
                    if (Find.TickManager.TicksGame - raidTick >= 100)
                    {
                        var locks = sentries.Select(s => s.LockedTarget).ToArray();
                        if (locks.Any(p => !hostiles.Contains(p)) || locks.Distinct().Count() != hostiles.Length)
                            throw new InvalidOperationException("Expected three separate hostile locks.");
                        Record("Spread target IDs: " + string.Join(", ", locks.Select(p => p.thingIDNumber)));
                        Frame("coordination.png", 9, 13f);
                    }
                    return;
                }
                int range = stage == 0 ? 18 : 60;
                if (GenLocalDate.HourOfDay(map) != 1 || map.skyManager.CurSkyGlow > 0.15f ||
                    !lamp.IsOperating || !lamp.SpotCell.IsValid || Math.Abs(lamp.AimRange - range) > 0.01f)
                    return;

                int spotDistance = lamp.SpotCell.x - origin.x;
                switch (stage)
                {
                    case 0 when spotDistance == 18: Frame("18-tiles.png", 9, 10f); break;
                    case 1 when spotDistance == 60: Frame("60-tiles.png", 30, 27f); break;
                    case 2 when spotDistance == 60: Frame("60-tiles-tip.png", 57, 8f); break;
                    case 3 when spotDistance > 24 && spotDistance < 30: Frame("wall.png", 24, 16f); break;
                }
            }
            catch (Exception ex)
            {
                Exit("FAIL " + ex);
            }
        }

        private void SetUpMap()
        {
            var map = Find.CurrentMap;
            origin = new IntVec3(map.Center.x - 35, 0, map.Center.z);
            for (int z = -14; z <= 14; z++)
            for (int x = -8; x <= 68; x++)
            {
                var cell = origin + new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) continue;
                map.roofGrid.SetRoof(cell, null);
                if (preciseLighting && cell.Fogged(map)) map.fogGrid.Unfog(cell);
                foreach (Thing thing in cell.GetThingList(map).ToList())
                    if ((preciseLighting && (thing is Pawn || thing.def.category == ThingCategory.Item ||
                        thing.def.category == ThingCategory.Building)) || thing is Plant || thing.def.passability == Traversability.Impassable ||
                        thing.TryGetComp<CompGlower>() != null)
                        thing.Destroy(DestroyMode.Vanish);
            }
            CheckObstacleCache(map);
            lamp = (Building_Searchlight)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret"), origin, map);
            lamp.GetComp<CompPowerTrader>().PowerOn = true;
            lamp.SetMode(SearchlightMode.Fixed);
            lamp.SetAimCell(origin + new IntVec3(18, 0, 0));
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            var ticks = Find.TickManager;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 1; i++)
                ticks.DebugSetTicksGame(ticks.TicksGame + 2500);
            ticks.CurTimeSpeed = TimeSpeed.Superfast;
            Find.UIRoot.screenshotMode.Active = true;
            Record($"setup; game={VersionControl.CurrentVersionString}; map={map.Size}; origin={origin}; diameter={SearchlightSettings.Diameter}");
            if (preciseLighting)
                preciseFixture = new PreciseVisualFixture(map, lamp, origin, output, Record, Exit);
        }

        private void CheckObstacleCache(Map map)
        {
            using var beam = new BeamField();
            var counter = typeof(BeamField).GetProperty("ObstacleScanCount", BindingFlags.Instance | BindingFlags.NonPublic);
            void Check(float heading, int expectedScans, int expectedDistance)
            {
                beam.Update(map, origin, heading, 18f, 4f);
                int scans = (int)counter.GetValue(beam);
                if (scans != expectedScans || beam.CenterCell.x - origin.x != expectedDistance)
                    throw new InvalidOperationException($"Obstacle cache at heading {heading}: scans={scans}, endpoint={beam.CenterCell}");
            }
            Check(90f, 1, 18);
            var steel = GenSpawn.Spawn(ThingDefOf.Steel, origin + new IntVec3(4, 0, 12), map);
            Check(90.05f, 1, 18);
            steel.Destroy();
            Check(90f, 1, 18);
            var wall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel),
                origin + new IntVec3(8, 0, 0), map);
            Check(90.05f, 2, 7);
            wall.Destroy();
            Check(90f, 3, 18);
            Record("Obstacle cache: item spawn/removal reused the footprint; wall spawn/removal refreshed it.");
        }

        private void Frame(string name, int center, float zoom)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.CameraDriver.SetRootPosAndSize((origin + new IntVec3(center, 0, 0)).ToVector3Shifted(), zoom);
            frameToCapture = Time.frameCount + 8;
            screenshotPath = Path.Combine(output, name);
            var map = Find.CurrentMap;
            var glow = map.glowGrid.GroundGlowAt(lamp.SpotCell, ignoreSky: true);
            Record($"{name}; aim={lamp.AimRange}; spot={lamp.SpotCell.x - origin.x}; skyGlow={map.skyManager.CurSkyGlow}; groundGlow={glow}");
        }

        private void Advance()
        {
            screenshotPath = null;
            stage++;
            if (stage == 5)
            {
                Exit("PASS");
                return;
            }
            if (stage == 1)
                lamp.SetAimCell(origin + new IntVec3(60, 0, 0));
            if (stage == 3)
                for (int z = -2; z <= 2; z++)
                    GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel),
                        origin + new IntVec3(30, 0, z), Find.CurrentMap);
            if (stage == 4) SetUpDetectionScene();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        private void SetUpDetectionScene()
        {
            var map = Find.CurrentMap;
            sentries = new Building_Searchlight[3];
            hostiles = new Pawn[3];
            for (int i = 0; i < 3; i++)
            {
                int z = (i - 1) * 6;
                var position = origin + new IntVec3(0, 0, z);
                var sentry = i == 1 ? lamp : (Building_Searchlight)GenSpawn.Spawn(lamp.def, position, map);
                sentries[i] = sentry;
                sentry.SetMode(SearchlightMode.Automatic);
                var cell = i == 1
                    ? origin + new IntVec3(18, 0, 0)
                    : origin + new IntVec3(24, 0, (i - 1) * 12);
                var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfAncientsHostile);
                GenSpawn.Spawn(pawn, cell, map);
                pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
                hostiles[i] = pawn;
            }
            raidTick = Find.TickManager.TicksGame;
            Find.UIRoot.screenshotMode.Active = false;
        }

        private void Record(string line)
        {
            File.AppendAllLines(Path.Combine(output, "visual-results.txt"), new[] { line });
            Log.Message("SearchlightVisualHarness: " + line);
        }

        private void Exit(string result)
        {
            stage = 5;
            Record(result);
            Application.Quit();
        }
    }
}
