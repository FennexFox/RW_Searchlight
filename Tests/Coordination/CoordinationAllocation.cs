using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace SearchlightCoordinationHarness
{
    public sealed partial class CoordinationCheck
    {
        private void BeginUniqueLocks()
        {
            for (int i = -1; i <= 1; i++)
            {
                Building_Searchlight lamp = Lamp(0, i * 5);
                Require(lamp.Policy == AssignmentPolicy.Spread, "new lamp defaults to Spread");
                Require(lamp.AlertMode == DetectionAlertMode.Visual, "new lamp defaults to Visual");
                lamp.SetAlertMode(DetectionAlertMode.Off);
                Hostile(20, i * 5);
            }
            int start = Now;
            After(2 * ScanInterval, () =>
            {
                Pawn[] assigned = lamps.Select(l => l.LockedTarget).ToArray();
                Require(assigned.All(pawns.Contains) && assigned.Distinct().Count() == 3,
                    "three Spread lamps settle on three different fixture pawns in two scans");
                Require(assigned.All(p => Reserved(Network, p) == 1), "exactly one reservation per uniquely locked pawn");
                Write("spread three-by-three; settledTicks=" + (lamps.Max(l => Count(l, "LockSinceTick")) - start) +
                    "; ids=" + string.Join(",", assigned.Select(p => p.thingIDNumber)));
                After(180, () =>
                {
                    Require(lamps.Select(l => l.LockedTarget).SequenceEqual(assigned), "valid unique locks stay stable");
                    Require(Alerts(Network, "NotificationsSent") == 0 && Alerts(Network, "ActiveMarkers") == 0,
                        "alert Off suppresses messages and markers with valid locks");
                    BeginRedistribution();
                });
            });
        }

        private void BeginRedistribution()
        {
            ClearFixtures();
            Pawn shared = Hostile(15, 0);
            Building_Searchlight manual = Lamp(0, -4);
            Building_Searchlight first = Lamp(0, 0);
            Building_Searchlight second = Lamp(0, 4);
            foreach (Building_Searchlight lamp in lamps) lamp.SetAlertMode(DetectionAlertMode.Off);
            manual.SelectManualTarget(shared);
            Require(Reserved(Network, shared) == 1, "valid manual order reserves immediately");
            After(ScanInterval, () =>
            {
                Require(Reserved(Network, shared) == 3, "three lamps can reserve one eligible pawn");
                int holdEnds = Math.Min(first.LastAcquireTick, second.LastAcquireTick) + 120;
                Pawn alternative = Hostile(15, 3);
                At(holdEnds - 1, () =>
                {
                    Require(first.LockedTarget == shared && second.LockedTarget == shared,
                        "eligible alternatives do not interrupt the 120-tick hold");
                    At(holdEnds, () => CheckRedistribution(manual, shared, alternative));
                });
            });
        }

        private void CheckRedistribution(Building_Searchlight manual, Pawn shared, Pawn alternative)
        {
            Require(manual.LockedTarget == shared && Reserved(Network, shared) == 2 && Reserved(Network, alternative) == 1,
                "stable alternative receives one shared automatic lock; manual order stays pinned");
            Building_Searchlight overlapping = lamps.Single(l => l != manual && l.LockedTarget == shared);
            overlapping.SelectManualTarget(shared);
            Require(Reserved(Network, shared) == 2, "manual collisions count without duplicate lamp reservations");
            Power(overlapping, false);
            After(1, () =>
            {
                Require(Reserved(Network, shared) == 1 && overlapping.LockedTarget == null, "power loss frees coverage immediately");
                Power(overlapping, true);
                After(ScanInterval, () =>
                {
                    Require(overlapping.LockedTarget == shared && Reserved(Network, shared) == 2,
                        "power restoration honors still-valid manual intent");
                    overlapping.Destroy(DestroyMode.Vanish);
                    Require(Reserved(Network, shared) == 1, "destroying a lamp frees its reservation immediately");
                    manual.SelectManualTarget(null);
                    Require(Read(manual, "manualTarget") == null && Reserved(Network, shared) == 0,
                        "releasing a manual order clears intent and frees its reservation");
                    manual.SelectManualTarget(shared);
                    Require(Reserved(Network, shared) == 1, "reacquiring a manual order restores its reservation");
                    shared.DeSpawn();
                    After(1, () =>
                    {
                        Require(Reserved(Network, shared) == 0 && manual.LockedTarget != shared,
                            "despawn invalidates a reservation without waiting for hold expiration");
                        Write("shared redistribution, manual collision/release, power and destruction passed");
                        BeginTwoScanStability();
                    });
                });
            });
        }

        private void BeginTwoScanStability()
        {
            ClearFixtures();
            Pawn shared = Hostile(15, 0);
            Lamp(0, 0).SetAlertMode(DetectionAlertMode.Off);
            Lamp(0, 4).SetAlertMode(DetectionAlertMode.Off);
            After(150, () =>
            {
                Require(Reserved(Network, shared) == 2, "both shared locks remained past minimum hold");
                Pawn free = Hostile(15, 3);
                After(ScanInterval, () =>
                {
                    Require(Reserved(Network, free) == 0, "one eligible scan cannot move a shared lock after the hold");
                    After(ScanInterval, () =>
                    {
                        Require(Reserved(Network, free) == 1 && Reserved(Network, shared) == 1,
                            "a second consecutive eligible scan moves exactly one shared lock");
                        Write("two consecutive scans required for a new alternative after the hold");
                        BeginVisibility();
                    });
                });
            });
        }

        private void BeginVisibility()
        {
            ClearFixtures();
            Building_Searchlight tower = Lamp(0, 0, tower: true);
            Building_Searchlight turret = Lamp(0, 8);
            tower.SetAlertMode(DetectionAlertMode.Off);
            turret.SetAlertMode(DetectionAlertMode.Off);
            Pawn reachable = Hostile(20, 8);
            Pawn blocked = Hostile(10, 0);
            Wall(5, 0);
            Wall(5, 4);
            turret.SelectManualTarget(reachable);
            After(ScanInterval, () =>
            {
                Require(BeamField.Clear(home, tower.Position, reachable.Position) && !BeamField.Clear(home, tower.Position, blocked.Position),
                    "visibility fixture has one direct and one blocked pawn");
                Require(tower.LockedTarget == reachable && Reserved(Network, blocked) == 0,
                    "tower prefers reserved direct target to closer unreserved blocked target");
                turret.SelectManualTarget(blocked);
                Require(turret.LockedTarget == reachable, "turret rejects a blocked manual target");
                tower.SelectManualTarget(blocked);
                Require(tower.LockedTarget == blocked && Reserved(Network, blocked) == 1, "tower can reserve an outdoor blocked manual target");
                home.roofGrid.SetRoof(blocked.Position, RoofDefOf.RoofConstructed);
                After(1, () =>
                {
                    Require(Reserved(Network, blocked) == 0 && tower.LockedTarget != blocked, "roof invalidates tower detection immediately");
                    home.roofGrid.SetRoof(blocked.Position, null);
                    Write("visibility precedes occupancy; roof invalidation passed");
                    BeginIndependentAndSweep();
                });
            });
        }

        private void BeginIndependentAndSweep()
        {
            ClearFixtures();
            Building_Searchlight lamp = Lamp(0, 0);
            lamp.SetPolicy(AssignmentPolicy.Independent);
            lamp.SetAlertMode(DetectionAlertMode.Off);
            Pawn smallId = Hostile(20, -3);
            Pawn bigId = Hostile(20, 3);
            Building_Searchlight holder = Lamp(0, -8);
            holder.SetAlertMode(DetectionAlertMode.Off);
            holder.SelectManualTarget(smallId);
            Require(Reserved(Network, smallId) == 1 && Reserved(Network, bigId) == 0,
                "fixture reserves only the smaller-ID pawn before Independent scans");
            Pawn atLimit = Hostile(60, 0);
            Pawn outside = Hostile(61, 0);
            Require(smallId.thingIDNumber < bigId.thingIDNumber, "tie fixture spawns smaller pawn ID first");
            After(180, () =>
            {
                Require(lamp.LockedTarget == smallId && lamp.SpotCell == smallId.Position,
                    "Independent preserves distance/ID ordering and endpoint illumination");
                lamp.SelectManualTarget(outside);
                Require(lamp.LockedTarget == smallId, "manual order rejects 61 cells");
                lamp.SetMode(SearchlightMode.Sweep);
                lamp.SetSweepHeading(90f);
                lamp.SetSweepAngle(10f);
                lamp.SelectManualTarget(smallId);
                Require(Read(lamp, "manualTarget") == null, "Sweep rejects manual designation outside current arc");
                After(150, () =>
                {
                    Require(lamp.LockedTarget == atLimit && lamp.SpotCell == atLimit.Position,
                        "Sweep rejects out-of-arc automatic candidates and accepts the visible 60-cell limit");
                    Require(Reserved(Network, outside) == 0 && Reserved(Network, atLimit) == 1,
                        "exact range boundaries are reflected in reservations");
                    Write("independent ids=" + smallId.thingIDNumber + "/" + bigId.thingIDNumber + "; sweep range 60/61 passed");
                    BeginPerformance(0);
                });
            });
        }

        private void BeginPerformance(int index)
        {
            ClearFixtures();
            int count = PerformanceCounts[index];
            for (int i = 0; i < count; i++)
                Lamp(-i / 10, i % 10 - 5).SetAlertMode(DetectionAlertMode.Off);
            for (int z = -6; z <= 6; z += 6) Hostile(24, z);
            const int sampleTicks = 120;
            int startTick = Now;
            int startSnapshots = Count(Network, "CandidateSnapshotBuildCount");
            int startEvaluations = Count(Network, "CandidateEvaluationCount");
            int startMessages = Alerts(Network, "NotificationsSent");
            var clock = Stopwatch.StartNew();
            int lastSnapshots = startSnapshots;
            Action sampleTick = null;
            sampleTick = () =>
            {
                int currentSnapshots = Count(Network, "CandidateSnapshotBuildCount");
                Require(currentSnapshots - lastSnapshots <= 1, "at most one candidate snapshot builds per game tick");
                lastSnapshots = currentSnapshots;
                if (Now < startTick + sampleTicks)
                {
                    After(1, sampleTick);
                    return;
                }

                int snapshots = currentSnapshots - startSnapshots;
                int evaluations = Count(Network, "CandidateEvaluationCount") - startEvaluations;
                Require(lamps.All(l => l.AcquisitionCount == sampleTicks / ScanInterval), "each lamp scans four times in 120 ticks");
                Require(snapshots > 0 && snapshots <= Math.Min(sampleTicks, count * sampleTicks / ScanInterval),
                    "at most one candidate snapshot per tick with acquisition work");
                Require(evaluations > 0 && evaluations <= count * pawns.Count * sampleTicks / ScanInterval,
                    "candidate evaluations scale with due scans and relevant hostiles");
                Require(Count(Network, "ReservationCount") == count, "every active lamp holds exactly one reservation");
                Require(lamps.Select(l => l.LockedTarget).Distinct().Count() == Math.Min(count, pawns.Count),
                    "more lamps than enemies share while covering all reachable enemies");
                int messages = Alerts(Network, "NotificationsSent") - startMessages;
                Require(messages == 0, "Off produces zero notifications throughout the sampled scan workload");
                Write("perf; lamps=" + count + "; ticks=" + sampleTicks + "; elapsedMs=" + clock.ElapsedMilliseconds +
                    "; scans=" + lamps.Sum(l => l.AcquisitionCount) + "; snapshots=" + snapshots + "; candidateEvaluations=" + evaluations +
                    "; settledTicks=" + (lamps.Max(l => Count(l, "LockSinceTick")) - startTick) + "; notifications=" + messages);
                Pawn[] assigned = lamps.Select(l => l.LockedTarget).ToArray();
                After(2 * ScanInterval, () =>
                {
                    Require(lamps.Select(l => l.LockedTarget).SequenceEqual(assigned), "existing shared locks remain stable after their hold");
                    if (index + 1 < PerformanceCounts.Length) BeginPerformance(index + 1);
                    else BeginMovingPerformance();
                });
            };
            After(1, sampleTick);
        }

        private void BeginMovingPerformance()
        {
            ClearFixtures();
            Pawn walker = Hostile(36, -8);
            for (int i = 0; i < 10; i++)
            {
                Building_Searchlight lamp = Lamp(0, i - 5);
                lamp.SetAlertMode(DetectionAlertMode.Off);
                lamp.SelectManualTarget(walker);
            }
            After(90, () =>
            {
                Require(lamps.All(l => l.LockedTarget == walker && l.SpotCell == walker.Position),
                    "ten lamps have settled on the target before it starts walking");
                // Other coordination fixtures stop their pawns every tick.
                pawns.Remove(walker);
                walker.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, Cell(48, 8)), JobCondition.InterruptForced);
                int BeamCount(string counter) => lamps.Sum(lamp => Count(Read(lamp, "beam"), counter));
                int startPoses = BeamCount("MeshPoseCount");
                int startScans = BeamCount("ObstacleScanCount");
                int startTraces = BeamCount("VisualRayTraceCount");
                IntVec3 startCell = walker.Position;
                After(120, () =>
                {
                    int poses = BeamCount("MeshPoseCount") - startPoses;
                    int scans = BeamCount("ObstacleScanCount") - startScans;
                    int traces = BeamCount("VisualRayTraceCount") - startTraces;
                    Write($"moving-perf; lamps=10; ticks=120; start={startCell}; end={walker.Position}; " +
                        $"meshPoses={poses}; obstacleScans={scans}; visualRayTraces={traces}");
                    Require(walker.Position.DistanceToSquared(startCell) >= 4 && lamps.All(l => l.LockedTarget == walker),
                        "the moving target stayed locked by all ten lamps");
                    Require(poses > 200 && scans < poses && traces == 0,
                        "fractional motion reuses the obstacle scan and requires no visual ray traces in open ground");
                    pawns.Add(walker);
                    BeginMapIsolation();
                });
            });
        }

        private void BeginMapIsolation()
        {
            ClearFixtures();
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(home.Tile, neighbors);
            PlanetTile tile = neighbors.First(t => !Find.World.Impassable(t) && Find.WorldObjects.MapParentAt(t) == null);
            Map away = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(70, 1, 70), WorldObjectDefOf.Site);
            ClearArena(away, away.Center, 5, 20, 6);
            // A temporary site automatically deinitializes when no colonist is present.
            Pawn awayColonist = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(awayColonist, away.Center + new IntVec3(-4, 0, -4), away);
            pawns.Add(awayColonist);
            Building_Searchlight homeLamp = Lamp(0, 0);
            Building_Searchlight awayLamp = Lamp(0, 0, map: away, at: away.Center);
            homeLamp.SetAlertMode(DetectionAlertMode.Off);
            Pawn homePawn = Hostile(12, 0);
            Pawn awayPawn = Hostile(0, 0, away, away.Center + new IntVec3(12, 0, 0));
            SearchlightMap awayNetwork = away.GetComponent<SearchlightMap>();
            Current.Game.CurrentMap = away;
            After(ScanInterval + BatchWindow, () =>
            {
                Write($"map isolation; home {homeLamp.LockedTarget}/{homePawn} operating={homeLamp.IsOperating} scans={homeLamp.AcquisitionCount}; " +
                    $"away {awayLamp.LockedTarget}/{awayPawn} operating={awayLamp.IsOperating} scans={awayLamp.AcquisitionCount}; " +
                    $"home hostile ids={string.Join(",", home.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).Select(p => p.thingIDNumber))}; " +
                    $"away hostile ids={string.Join(",", away.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)).Select(p => p.thingIDNumber))}");
                Require(homeLamp.LockedTarget == homePawn && awayLamp.LockedTarget == awayPawn,
                    "controllers coordinate their own map while the camera views the other");
                Require(Reserved(Network, awayPawn) == 0 && Reserved(awayNetwork, homePawn) == 0 &&
                    Reserved(Network, homePawn) == 1 && Reserved(awayNetwork, awayPawn) == 1, "reservations are isolated by map");
                Require(Alerts(Network, "NotificationsSent") == 0 && Alerts(awayNetwork, "NotificationsSent") == 1 &&
                    Alerts(awayNetwork, "ActiveMarkers") == 1, "the away-map alert and active marker stay in its controller");
                Message awayMessage = LatestDetectionMessage(awayNetwork);
                Require(awayMessage.lookTargets.targets.Single().Map == away, "the away notification points to the away map");
                Current.Game.CurrentMap = home;
                Require(homeLamp.LockedTarget == homePawn && awayLamp.LockedTarget == awayPawn, "camera switch preserves both independent assignments");
                homeLamp.Destroy(DestroyMode.Vanish);
                ParkPawn(homePawn);
                lamps.Remove(homeLamp);
                pawns.Remove(homePawn);
                Require(Reserved(awayNetwork, awayPawn) == 1, "away reservation remains active until map removal");
                MapParent parent = away.Parent;
                Current.Game.DeinitAndRemoveMap(away, false);
                if (!parent.Destroyed) parent.Destroy();
                Require(Count(awayNetwork, "ReservationCount") == 0 && Alerts(awayNetwork, "ActiveMarkers") == 0,
                    "leaving and removing the second map clears its transient state");
                Require(awayMessage.lookTargets.targets.All(t => t.Map != away), "removing the map also clears its notification location");
                Write("two-map view switching and removal cleared reservations, markers and notification locations");
                BeginDeduplication();
            });
        }
    }
}
