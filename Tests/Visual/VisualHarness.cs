using System;
using System.IO;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;

namespace SearchlightVisualHarness
{
    public sealed class CaptureCheck : GameComponent
    {
        private readonly string output;
        private Building_Searchlight lamp;
        private IntVec3 origin;
        private int stage;
        private int frameToCapture;
        private string screenshotPath;

        public CaptureCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-visual-output", out output);
        }

        public override void GameComponentUpdate()
        {
            if (output == null || stage == 4 || Find.CurrentMap == null) return;
            try
            {
                if (lamp == null)
                {
                    SetUpMap();
                    return;
                }

                lamp.GetComp<CompPowerTrader>().PowerOn = true;
                if (screenshotPath != null)
                {
                    if (Time.frameCount == frameToCapture)
                        ScreenCapture.CaptureScreenshot(screenshotPath);
                    if (File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 0)
                        Advance();
                    return;
                }

                var map = Find.CurrentMap;
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
                foreach (Thing thing in cell.GetThingList(map).ToList())
                    if (thing is Plant || thing.def.passability == Traversability.Impassable ||
                        thing.TryGetComp<CompGlower>() != null)
                        thing.Destroy(DestroyMode.Vanish);
            }
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
            if (stage == 4)
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
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        private void Record(string line)
        {
            File.AppendAllLines(Path.Combine(output, "visual-results.txt"), new[] { line });
            Log.Message("SearchlightVisualHarness: " + line);
        }

        private void Exit(string result)
        {
            stage = 4;
            Record(result);
            Application.Quit();
        }
    }
}
