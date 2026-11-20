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
                lamps = Find.CurrentMap.listerThings.AllThings.OfType<Building_Searchlight>().ToArray();
                Require(lamps.Length == 10, "expected the ten-lamp vanilla runtime fixture");
                Require(lamps.All(l => l.Policy == AssignmentPolicy.Independent &&
                    l.AlertMode == DetectionAlertMode.Visual), "all ten old lamps migrated to Independent + Visual");
                original = lamps.Select(l => l.LockedTarget).First(p => p != null);
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
                if (age == 45)
                {
                    Require(lamps.Any(l => l.LockedTarget == original), "original target lock restored");
                    // Trigger genuine reacquisitions after the quiet load window.
                    foreach (var lamp in lamps) lamp.SelectManualTarget(null);
                    Require(!lamps.Any(l => l.LockedTarget == original), "manual release clears every original target lock");
                }
                if (age == 160)
                {
                    Require(lamps.Any(l => l.LockedTarget == original), "original pawn reacquired");
                    Require(Notifications() == 0, "restoration and reacquisition produced zero messages");
                    var sentry = lamps.First(l => l.IsOperating && l.LockedTarget == original);
                    var cell = original.Position;
                    newcomer = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfAncientsHostile);
                    GenSpawn.Spawn(newcomer, cell, Find.CurrentMap);
                    newcomer.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
                    sentry.SelectManualTarget(newcomer);
                    Require(sentry.LockedTarget == newcomer, "new hostile acquired");
                }
                if (age >= 230)
                {
                    Require(Notifications() == 1, "one message for the new hostile");
                    Finish("PASS legacy");
                }
            }
            catch (Exception ex) { Finish("FAIL " + ex); }
        }

        private static int Notifications()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var map = Find.CurrentMap.GetComponent<SearchlightMap>();
            var alerts = typeof(SearchlightMap).GetField("alerts", flags).GetValue(map);
            return (int)alerts.GetType().GetProperty("NotificationsSent", flags).GetValue(alerts);
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
