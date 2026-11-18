using System.Linq;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private enum TrackingPhase
        {
            FixedAtFifteen, ExtendToSixty, FixedNorth, RotateToTarget, FollowPawn,
            ResumeSweep, AcquireOccluded, RetainLock, RankCandidates, ManualLock,
            LosePower, RestorePower, DestroyLamp
        }

        private const int TrackingStep = 13;
        private static readonly int[] PawnWalk = { 59, 58, 59, 60, 61 };
        private static TrackingPhase trackingPhase;
        private static float previousRange;
        private static float previousHeading;
        private static int darkSamples;
        private static int walkStep;
        private static Pawn nearerLowId;
        private static Pawn nearerHighId;
        private static Pawn farther;
        private static Thing trackingWall;
        private static IntVec3 lastSpot;

        private static void BeginTracking()
        {
            turret = SpawnLamp("DS_SearchlightTurret", origin);
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(East(15));
            for (int x = 58; x <= 61; x++)
                Find.CurrentMap.terrainGrid.SetTerrain(East(x), TerrainDefOf.Soil);
            SpawnTarget(East(60));
            NextTracking(TrackingPhase.FixedAtFifteen);
        }

        private static void TickTracking(int age)
        {
            Check(age <= 140, "timed out waiting for " + trackingPhase);
            nearerLowId?.pather.StopDead();
            nearerHighId?.pather.StopDead();
            farther?.pather.StopDead();
            switch (trackingPhase)
            {
                case TrackingPhase.FixedAtFifteen:
                    if (age < 90) break;
                    Check(turret.AimRange == 15f && turret.SpotCell == East(15), "fixed endpoint starts at 15 cells");
                    Check(GroundGlow(target.Position) <= 0.01f, "60-cell pawn starts dark");
                    turret.SelectManualTarget(target);
                    Check(turret.LockedTarget == null, "fixed mode rejects manual targeting");
                    turret.SetMode(SearchlightMode.Automatic);
                    previousRange = turret.AimRange;
                    NextTracking(TrackingPhase.ExtendToSixty);
                    break;
                case TrackingPhase.ExtendToSixty:
                    ExtendToSixty();
                    break;
                case TrackingPhase.FixedNorth:
                    if (age >= 90) BeginRotation();
                    break;
                case TrackingPhase.RotateToTarget:
                    RotateToTarget();
                    break;
                case TrackingPhase.FollowPawn:
                    FollowPawn();
                    break;
                case TrackingPhase.ResumeSweep:
                    if (age >= 90) ResumeSweep();
                    break;
                case TrackingPhase.AcquireOccluded:
                    if (age >= 35) AddVisibleCandidates();
                    break;
                case TrackingPhase.RetainLock:
                    if (age < 35) break;
                    Check(tower.LockedTarget == target, "tower retains a valid lock after visible candidates arrive");
                    tower.SelectManualTarget(null);
                    NextTracking(TrackingPhase.RankCandidates);
                    break;
                case TrackingPhase.RankCandidates:
                    if (age >= 35) RankCandidates();
                    break;
                case TrackingPhase.ManualLock:
                    if (age < 35) break;
                    Check(tower.LockedTarget == target, "valid manual selection persists across automatic acquisition polls");
                    lastSpot = tower.SpotCell;
                    Check(lastSpot.IsValid && GroundGlow(lastSpot) > 0.01f, "tower emits light before power loss");
                    tower.GetComp<CompPowerTrader>().PowerOn = false;
                    NextTracking(TrackingPhase.LosePower);
                    break;
                case TrackingPhase.LosePower:
                    if (age < 2) break;
                    Check(tower.GetComp<CompFlickable>().SwitchIsOn, "loss of power leaves the switch on");
                    Check(!tower.IsOperating && tower.LockedTarget == null && !tower.SpotCell.IsValid,
                        "power loss clears the lock and endpoint");
                    if (!SpotCleared("power loss")) break;
                    tower.GetComp<CompPowerTrader>().PowerOn = true;
                    NextTracking(TrackingPhase.RestorePower);
                    break;
                case TrackingPhase.RestorePower:
                    if (age < 35) break;
                    Check(tower.IsOperating && tower.LockedTarget == target && tower.SpotCell.IsValid,
                        "restoring power resumes tracking and illumination");
                    lastSpot = tower.SpotCell;
                    Check(GroundGlow(lastSpot) > 0.01f, "restored endpoint emits game light");
                    tower.Destroy(DestroyMode.Vanish);
                    NextTracking(TrackingPhase.DestroyLamp);
                    break;
                case TrackingPhase.DestroyLamp:
                    if (age < 2) break;
                    if (!SpotCleared("destroying the powered lamp")) break;
                    Write("power-and-destruction PASS; switch remained on during power loss");
                    EndTracking();
                    break;
            }
        }

        private static void ExtendToSixty()
        {
            Check(turret.AimRange >= previousRange - 0.001f && turret.AimRange <= previousRange + 0.601f,
                "live tracking extends range gradually each tick (" + previousRange + " -> " + turret.AimRange + ")");
            previousRange = turret.AimRange;
            if (turret.LockedTarget == null) return;
            Check(turret.LockedTarget == target, "automatic mode acquires the 60-cell pawn");
            if (turret.AimRange <= 56f)
            {
                Check(turret.SpotCell != target.Position && GroundGlow(target.Position) <= 0.01f,
                    "target remains dark while the endpoint is outside adjacent glow range");
                darkSamples++;
            }
            if (turret.AimRange < 59.99f) return;
            Check(darkSamples >= 40, "range extension includes many intermediate dark ticks");
            if (!TargetLit("range settled at 60")) return;
            Write("smooth-range PASS; from=15; to=60; intermediateDarkTicks=" + darkSamples);
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(origin + new IntVec3(0, 0, 60));
            NextTracking(TrackingPhase.FixedNorth);
        }

        private static void BeginRotation()
        {
            Check(Mathf.Abs(Mathf.DeltaAngle(turret.AimHeading, 0f)) < 0.01f && turret.AimRange == 60f,
                "rotation starts north at full range");
            Check(GroundGlow(target.Position) <= 0.01f, "east pawn starts dark when the beam faces north");
            turret.SetMode(SearchlightMode.Sweep);
            turret.SetAimCell(East(15));
            Pawn outsideArc = GenerateHostile();
            SpawnHostile(outsideArc, origin - new IntVec3(0, 0, 8));
            turret.SelectManualTarget(outsideArc);
            Check(turret.LockedTarget == null, "manual selection rejects a pawn outside the sweep arc");
            outsideArc.DeSpawn();
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == target, "valid manual selection locks before the beam turns");
            previousHeading = turret.AimHeading;
            darkSamples = 0;
            NextTracking(TrackingPhase.RotateToTarget);
        }

        private static void RotateToTarget()
        {
            float turn = Mathf.DeltaAngle(previousHeading, turret.AimHeading);
            float remaining = Mathf.Abs(Mathf.DeltaAngle(turret.AimHeading, 90f));
            Check(turn >= -0.001f && turn <= 2.001f && turret.AimRange == 60f,
                "beam turns gradually with the range already settled");
            Check(turret.LockedTarget == target, "target stays locked during the entire turn");
            previousHeading = turret.AimHeading;
            float halfAngle = Mathf.Atan2(2f, 60f) * Mathf.Rad2Deg;
            if (remaining > halfAngle + 0.001f)
                Check(turret.SpotCell != target.Position, "endpoint does not jump to a locked pawn outside the beam");
            // Near alignment, the adjacent endpoint can already cast radial glow on the pawn.
            if (remaining > 6f)
            {
                Check(GroundGlow(target.Position) <= 0.01f, "pawn stays dark beyond the endpoint's radial glow");
                darkSamples++;
            }
            if (remaining > 0.01f) return;
            Check(darkSamples >= 30, "rotation includes many intermediate dark ticks");
            if (!TargetLit("rotation settled east")) return;
            Write("smooth-rotation PASS; from=0; to=90; intermediateDarkTicks=" + darkSamples);
            WalkToNextCell();
        }

        private static void WalkToNextCell()
        {
            target.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, East(PawnWalk[walkStep])), JobCondition.InterruptForced);
            NextTracking(TrackingPhase.FollowPawn);
        }

        private static void FollowPawn()
        {
            int distance = PawnWalk[walkStep];
            if (target.Position != East(distance))
            {
                Check(turret.LockedTarget == target, "lock persists while pawn walks inside the boundary");
                return;
            }

            target.pather.StopDead();
            if (distance == 61)
            {
                if (turret.LockedTarget != null) return;
                Check(turret.SpotCell != target.Position, "61-cell pawn cannot become the endpoint");
                Write("moving-boundary PASS; same spawned pawn walked 60->59->58->59->60->61");
                NextTracking(TrackingPhase.ResumeSweep);
                return;
            }

            Check(turret.LockedTarget == target, "walking pawn at " + distance + " cells remains locked");
            if (turret.AimRange != distance) return;
            if (!TargetLit("walking pawn at " + distance)) return;
            walkStep++;
            WalkToNextCell();
        }

        private static void ResumeSweep()
        {
            float offset = Mathf.DeltaAngle(90f, turret.AimHeading);
            Check(turret.LockedTarget == null && turret.AimRange == 15f && offset > 2f && offset <= 45.01f,
                "losing the moving pawn returns to the configured sweep and range");
            Write("resume-sweep PASS; offset=" + offset + "; range=" + turret.AimRange);
            target.DeSpawn();
            turret.Destroy(DestroyMode.Vanish);
            lamps.Clear();
            tower = SpawnLamp("DS_SearchlightTower", origin);
            trackingWall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), East(6), Find.CurrentMap);
            SpawnTarget(East(14));
            Check(!BeamField.Clear(Find.CurrentMap, origin, target.Position), "tower's first target is occluded");
            NextTracking(TrackingPhase.AcquireOccluded);
        }

        private static void AddVisibleCandidates()
        {
            Check(tower.LockedTarget == target, "tower can acquire an outdoor occluded pawn");
            farther = GenerateHostile();
            nearerLowId = GenerateHostile();
            nearerHighId = GenerateHostile();
            SpawnHostile(farther, origin + new IntVec3(32, 0, 10));
            SpawnHostile(nearerHighId, origin + new IntVec3(24, 0, -6));
            SpawnHostile(nearerLowId, origin + new IntVec3(24, 0, 6));
            Check(farther.thingIDNumber < nearerLowId.thingIDNumber && nearerLowId.thingIDNumber < nearerHighId.thingIDNumber,
                "ID tie-break fixture uses ascending generation IDs");
            var scanOrder = Find.CurrentMap.mapPawns.AllPawnsSpawned.ToList();
            Check(scanOrder.IndexOf(nearerHighId) < scanOrder.IndexOf(nearerLowId),
                "higher ID is encountered first in the acquisition scan");
            Check(new[] { farther, nearerHighId, nearerLowId }.All(p => BeamField.Clear(Find.CurrentMap, origin, p.Position)),
                "three ranking alternatives have an unobstructed beam path");
            NextTracking(TrackingPhase.RetainLock);
        }

        private static void RankCandidates()
        {
            Check(tower.LockedTarget == nearerLowId,
                "fresh acquisition chooses visibility, then distance, then lower ID instead of scan order");
            tower.SelectManualTarget(target);
            Check(tower.LockedTarget == target, "manual selection can override with an outdoor occluded pawn");
            Find.CurrentMap.roofGrid.SetRoof(nearerLowId.Position, RoofDefOf.RoofConstructed);
            tower.SelectManualTarget(nearerLowId);
            Check(tower.LockedTarget == target, "tower manual selection rejects a roofed pawn and preserves the previous lock");
            Write("target-ranking PASS; retainedOccluded=" + target.thingIDNumber + "; selectedLowId=" + nearerLowId.thingIDNumber +
                "; equalDistanceHighId=" + nearerHighId.thingIDNumber + "; fartherId=" + farther.thingIDNumber);
            NextTracking(TrackingPhase.ManualLock);
        }

        private static void EndTracking()
        {
            Find.CurrentMap.roofGrid.SetRoof(nearerLowId.Position, null);
            foreach (Pawn pawn in new[] { target, nearerLowId, nearerHighId, farther }) pawn.DeSpawn();
            target = nearerLowId = nearerHighId = farther = null;
            trackingWall.Destroy(DestroyMode.Vanish);
            lamps.Clear();
            BeginPerformance();
        }

        private static bool TargetLit(string context)
        {
            Check(turret.SpotCell == target.Position, context + ": spot is on the pawn's current cell");
            Check(target.Position.GetThingList(Find.CurrentMap).Any(t => t.def.defName == "DS_LightSpot"),
                context + ": the actual glower is on the pawn's current cell");
            // The glow grid can update after the current batch of simulation ticks.
            return GroundGlow(target.Position) > 0.01f;
        }

        private static bool SpotCleared(string context)
        {
            Check(!Find.CurrentMap.listerThings.AllThings.Any(t => t.def.defName == "DS_LightSpot"), context + " removes the spawned glower");
            return GroundGlow(lastSpot) <= 0.01f;
        }

        private static IntVec3 East(int cells) => origin + new IntVec3(cells, 0, 0);
        private static float GroundGlow(IntVec3 cell) => Find.CurrentMap.glowGrid.GroundGlowAt(cell, ignoreSky: true);

        private static void NextTracking(TrackingPhase next)
        {
            trackingPhase = next;
            Next(TrackingStep);
        }
    }
}
