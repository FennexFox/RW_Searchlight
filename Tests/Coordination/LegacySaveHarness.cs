using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightCoordinationHarness
{
    // Input is the ten-lamp vanilla Runtime save written by the pre-coordination package.
    public sealed class LegacySaveCheck : GameComponent
    {
        private readonly bool enabled;
        private int startTick;
        private Building_Searchlight[] lamps;
        private Pawn original;
        private Pawn newcomer;
        private bool finished;

        public LegacySaveCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-coordination-phase", out string phase);
            enabled = phase == "legacy";
        }

        public override void LoadedGame()
        {
            if (!enabled) return;
            try
            {
                Record("game=" + VersionControl.CurrentVersionString + "; assemblyMvid=" +
                    typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
                lamps = Find.CurrentMap.listerThings.AllThings.OfType<Building_Searchlight>().ToArray();
                Require(lamps.Length == 10, "expected the ten-lamp vanilla runtime fixture");
                Require(lamps.All(l => l.Policy == AssignmentPolicy.Independent &&
                    l.AlertMode == DetectionAlertMode.Visual), "all ten old lamps migrated to Independent + Visual");
                original = lamps.Select(l => l.LockedTarget).First(p => p != null);
                Map map = Find.CurrentMap;
                IntVec3 outside = map.AllCells.First(c => c.Walkable(map) &&
                    lamps.All(l => c.DistanceToSquared(l.Position) > 65 * 65));
                newcomer = SpawnHostile(outside, map);
                startTick = Find.TickManager.TicksGame;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
            catch (Exception ex) { Finish("FAIL " + ex); }
        }

        public override void GameComponentTick()
        {
            if (!enabled || finished || lamps == null) return;
            try
            {
                int age = Find.TickManager.TicksGame - startTick;
                if (age == 10)
                {
                    var sentry = lamps.First(l => l.IsOperating && l.LockedTarget == original);
                    newcomer.Position = original.Position;
                    sentry.SelectManualTarget(newcomer);
                    Require(sentry.LockedTarget == newcomer, "hostile outside coverage at load entered range and was acquired during the quiet window");
                }
                if (age == 71)
                {
                    Require(AlertCount("NotificationsSent") == 1 && AlertCount("PawnsNotified") == 1,
                        "only the hostile entering range generated a batched notification");
                    newcomer.DeSpawn();
                    Require(lamps.Any(l => l.LockedTarget == original), "original target lock restored");
                    // Trigger genuine reacquisitions after the quiet load window.
                    foreach (var lamp in lamps) lamp.SelectManualTarget(null);
                    Require(!lamps.Any(l => l.LockedTarget == original), "manual release clears every original target lock");
                }
                if (age == 160)
                {
                    Require(lamps.Any(l => l.LockedTarget == original), "original pawn reacquired");
                    Require(AlertCount("PawnsNotified") == 1, "restoring and reacquiring the original hostile produced no alert");
                    // The second hostile appears at the same cell. Clear the old UI message
                    // so the game's real-time message coalescing cannot hide this delivery.
                    Messages.Clear();
                    var sentry = lamps.First(l => l.IsOperating && l.LockedTarget == original);
                    Pawn later = SpawnHostile(original.Position, Find.CurrentMap);
                    sentry.SelectManualTarget(later);
                    Require(sentry.LockedTarget == later, "new hostile acquired after the quiet window");
                }
                if (age >= 230)
                {
                    Require(AlertCount("NotificationsSent") == 2 && AlertCount("PawnsNotified") == 2,
                        "one more message delivered after the quiet window");
                    Finish("PASS legacy");
                }
            }
            catch (Exception ex) { Finish("FAIL " + ex); }
        }

        private static Pawn SpawnHostile(IntVec3 cell, Map map)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfAncientsHostile);
            GenSpawn.Spawn(pawn, cell, map);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
            return pawn;
        }

        private static int AlertCount(string property)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var map = Find.CurrentMap.GetComponent<SearchlightMap>();
            var alerts = typeof(SearchlightMap).GetField("alerts", flags).GetValue(map);
            return (int)alerts.GetType().GetProperty(property, flags).GetValue(alerts);
        }

        private static void Require(bool valid, string description)
        {
            if (!valid) throw new InvalidOperationException(description);
            Record(description);
        }

        private static void Record(string line) =>
            File.AppendAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "legacy-results.txt"), line + Environment.NewLine);

        private void Finish(string line)
        {
            finished = true;
            Record(line);
            Application.Quit();
        }
    }
}
