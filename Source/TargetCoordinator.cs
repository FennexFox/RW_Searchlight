using System;
using System.Collections.Generic;
using Verse;

namespace DirectedSearchlights
{
    internal sealed class TargetCoordinator
    {
        private const int MinimumHoldTicks = 120;

        private readonly Map map;
        private readonly Action<Building_Searchlight, Pawn, int> acquired;
        private readonly Dictionary<Building_Searchlight, Pawn> reservations = new Dictionary<Building_Searchlight, Pawn>();
        private readonly Dictionary<Pawn, int> occupancy = new Dictionary<Pawn, int>();
        private readonly Dictionary<Building_Searchlight, (Pawn pawn, int tick)> alternatives =
            new Dictionary<Building_Searchlight, (Pawn pawn, int tick)>();
        private readonly List<Building_Searchlight> dueLights = new List<Building_Searchlight>();
        private readonly List<Pawn> candidates = new List<Pawn>();
        private int lastBatchTick = -1;

        public TargetCoordinator(Map map, Action<Building_Searchlight, Pawn, int> acquired)
        {
            this.map = map;
            this.acquired = acquired;
        }

        public int ReservationCount => reservations.Count;
        public int ReservationsFor(Pawn pawn) => pawn != null && occupancy.TryGetValue(pawn, out int count) ? count : 0;
        public int CandidateSnapshotBuildCount { get; private set; }
        public int CandidateEvaluationCount { get; private set; }

        public void Tick(int tick, IReadOnlyList<Building_Searchlight> lights)
        {
            if (tick == lastBatchTick) return;
            lastBatchTick = tick;
            foreach (Building_Searchlight light in lights)
                if (light.ScanDue(tick)) dueLights.Add(light);
            if (dueLights.Count == 0) return;

            try
            {
                // Reconcile all owners before any due lamp can claim a target.
                foreach (Building_Searchlight light in lights) Reconcile(light, tick);
                bool hasSnapshot = false;
                foreach (Building_Searchlight light in dueLights)
                {
                    light.RecordScan(tick);
                    if (light.UsesFixedDetection && light.HasFixedEnemy) continue;
                    Pawn current = light.LockedTarget;
                    bool redistribute = current != null && light.Policy == AssignmentPolicy.Spread &&
                        !light.HasManualTarget && ReservationsFor(current) > 1;
                    if (!redistribute) ResetRedistribution(light);
                    if (current != null && !redistribute) continue;

                    if (!hasSnapshot)
                    {
                        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                            if (Building_Searchlight.IsDetectableHostile(pawn, map)) candidates.Add(pawn);
                        hasSnapshot = true;
                        CandidateSnapshotBuildCount++;
                    }

                    // Fixed lights detect a trigger without assigning or tracking that pawn.
                    if (light.UsesFixedDetection) light.RecordFixedEnemy(BestCandidate(light, false, null));
                    else if (redistribute) TryRedistribute(light, tick);
                    else ChangeLock(light, BestCandidate(light, false, null), tick);
                }
            }
            finally
            {
                dueLights.Clear();
                candidates.Clear();
            }
        }

        public void Reconcile(Building_Searchlight light, int tick)
        {
            ChangeLock(light, light.RetainedTarget(), tick);
        }

        public void ChangeLock(Building_Searchlight light, Pawn pawn, int tick)
        {
            light.ApplyLock(pawn, tick);
            reservations.TryGetValue(light, out Pawn previous);
            if (pawn == previous) return;

            ResetRedistribution(light);
            if (previous != null)
            {
                int count = occupancy[previous] - 1;
                if (count == 0) occupancy.Remove(previous);
                else occupancy[previous] = count;
                reservations.Remove(light);
            }
            if (pawn != null)
            {
                reservations.Add(light, pawn);
                occupancy[pawn] = ReservationsFor(pawn) + 1;
                acquired(light, pawn, tick);
            }
        }

        public void ResetRedistribution(Building_Searchlight light) => alternatives.Remove(light);

        public void Clear()
        {
            reservations.Clear();
            occupancy.Clear();
            alternatives.Clear();
            candidates.Clear();
            dueLights.Clear();
        }

        private void TryRedistribute(Building_Searchlight light, int tick)
        {
            alternatives.TryGetValue(light, out var previous);
            bool consecutive = previous.pawn != null && tick - previous.tick == Building_Searchlight.AcquireInterval;
            Pawn alternative = BestCandidate(light, true, consecutive ? previous.pawn : null);
            if (alternative == null) ResetRedistribution(light);
            else if (consecutive && alternative == previous.pawn && tick - light.LockSinceTick >= MinimumHoldTicks)
                ChangeLock(light, alternative, tick);
            else alternatives[light] = (alternative, tick);
        }

        private Pawn BestCandidate(Building_Searchlight light, bool freeDirectOnly, Pawn previousAlternative)
        {
            TargetCandidate best = default;
            int bestOccupancy = 0;
            bool usesReservations = !light.UsesFixedDetection && light.Policy == AssignmentPolicy.Spread;
            foreach (Pawn pawn in candidates)
            {
                int count = usesReservations ? ReservationsFor(pawn) : 0;
                if (freeDirectOnly && count > 0) continue;
                CandidateEvaluationCount++;
                if (!light.TryDescribeCandidate(pawn, out TargetCandidate candidate) || (freeDirectOnly && !candidate.Direct))
                    continue;
                if (pawn == previousAlternative) return pawn;
                if (best.Pawn != null && !candidate.Precedes(best, count, bestOccupancy)) continue;
                best = candidate;
                bestOccupancy = count;
            }
            return best.Pawn;
        }
    }
}
