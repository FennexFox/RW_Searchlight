using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightOpticsHarness
{
    public sealed class OpticsCheck : GameComponent
    {
        private static string resultPath;
        private static int phase;
        private static int phaseTick;
        private static Building_Searchlight lamp;
        private static Pawn shooter;

        public OpticsCheck(Game game)
        {
            if (resultPath != null) return;
            GenCommandLine.TryGetCommandLineArg("ds-optics-result", out resultPath);
            if (string.IsNullOrEmpty(resultPath)) resultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "optics-results.txt");
            Write("created; assemblyMvid=" + typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
        }

        public override void StartedNewGame() { Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast; }

        public override void GameComponentTick()
        {
            if (phase == 99 || Find.CurrentMap == null) return;
            try
            {
                if (shooter != null && shooter.Spawned) shooter.pather.StopDead();
                if (phase == 0) Setup();
                else if (phase == 1 && Find.TickManager.TicksGame - phaseTick >= 120)
                {
                    Measure("unaided");
                    var def = DefDatabase<ThingDef>.GetNamed("CE_Apparel_AdvancedNightVision");
                    shooter.apparel.Wear((Apparel)ThingMaker.MakeThing(def), false);
                    Check(shooter.apparel.WornApparel.Any(a => a.def == def), "goggles equipped");
                    Next(2);
                }
                else if (phase == 2 && Find.TickManager.TicksGame - phaseTick >= 60)
                {
                    Measure("advanced-goggles");
                    Finish(true);
                }
            }
            catch (Exception ex) { Write("FAIL " + ex); Finish(false); }
        }

        private static void Setup()
        {
            var map = Find.CurrentMap;
            IntVec3 origin = map.Center + new IntVec3(-20, 0, 0);
            for (int z = -4; z <= 4; z++)
            for (int x = -4; x <= 25; x++)
            {
                IntVec3 cell = origin + new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) continue;
                map.roofGrid.SetRoof(cell, null);
                foreach (Thing thing in cell.GetThingList(map).ToList())
                    if (thing.def.passability == Traversability.Impassable || thing.TryGetComp<CompGlower>() != null)
                        thing.Destroy(DestroyMode.Vanish);
            }
            lamp = (Building_Searchlight)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret"), origin, map);
            lamp.GetComp<CompPowerTrader>().PowerOn = true;
            lamp.SetMode(SearchlightMode.Fixed);
            lamp.SetAimCell(origin + new IntVec3(15, 0, 0));
            shooter = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(shooter, origin + new IntVec3(-3, 0, 0), map);
            shooter.drafter.Drafted = true;
            shooter.jobs.StopAll();
            shooter.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != 1; i++)
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2500);
            Write("setup; lamp=" + lamp.Position + "; shooter=" + shooter.Position + "; hour=" + GenLocalDate.HourOfDay(map) + "; skyGlow=" + map.skyManager.CurSkyGlow);
            Next(1);
        }

        private static void Measure(string label)
        {
            var map = Find.CurrentMap;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "CombatExtended");
            var trackerType = assembly.GetType("CombatExtended.LightingTracker");
            var tracker = map.GetComponent(trackerType);
            var combatGlow = trackerType.GetMethod("CombatGlowAtFor");
            var shift = assembly.GetType("CombatExtended.CE_Utility").GetMethod("GetLightingShift");
            IntVec3 spot = lamp.SpotCell;
            Check(lamp.IsOperating && spot.IsValid, "single lamp illuminates");
            Check(map.listerThings.AllThings.Count(t => t.def.defName == "DS_SearchlightTurret" || t.def.defName == "DS_SearchlightTower") == 1, "exactly one searchlight");
            Check(map.listerThings.AllThings.Count(t => t.def.defName == "DS_LightSpot") == 1, "exactly one spot");
            var glower = map.listerThings.AllThings.First(t => t.def.defName == "DS_LightSpot").TryGetComp<CompGlower>();
            Write(label + "; tick=" + Find.TickManager.TicksGame + "; spot=" + spot + "; radius=" + glower.GlowRadius + "; skyGlow=" + map.skyManager.CurSkyGlow + "; goggles=" + string.Join(",", shooter.apparel.WornApparel.Select(a => a.def.defName).ToArray()));
            for (int d = 0; d <= 3; d++)
            {
                IntVec3 cell = spot + new IntVec3(d, 0, 0);
                float ground = map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
                float ce = Convert.ToSingle(combatGlow.Invoke(tracker, new object[] { shooter.Position, cell }));
                float penalty = Convert.ToSingle(shift.Invoke(null, new object[] { shooter, ce }));
                Write(label + "; offset=" + d + "; cell=" + cell + "; groundGlow=" + ground + "; combatGlow=" + ce + "; lightingShift=" + penalty);
            }
            for (int d = 1; d <= 3; d++)
            {
                IntVec3 cell = spot + new IntVec3(d, 0, d);
                float ground = map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
                float ce = Convert.ToSingle(combatGlow.Invoke(tracker, new object[] { shooter.Position, cell }));
                float penalty = Convert.ToSingle(shift.Invoke(null, new object[] { shooter, ce }));
                Write(label + "; diagonal=" + d + "; cell=" + cell + "; groundGlow=" + ground + "; combatGlow=" + ce + "; lightingShift=" + penalty);
            }
        }

        private static void Check(bool ok, string message) { if (!ok) throw new Exception("ASSERT: " + message); }
        private static void Next(int next) { phase = next; phaseTick = Find.TickManager.TicksGame; }
        private static void Write(string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.AppendAllText(resultPath, DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            Log.Message("SearchlightOpticsHarness: " + line);
        }
        private static void Finish(bool success)
        {
            phase = 99;
            Write((success ? "PASS" : "FAIL") + "; ticks=" + Find.TickManager.TicksGame);
            Application.Quit(success ? 0 : 1);
        }
    }
}
