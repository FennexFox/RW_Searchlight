using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace DirectedSearchlights
{
    /// <summary>
    /// Keeps the maximum per-cell reveal from the beams on one map. Only cells
    /// whose sampled intensity changed are recomputed.
    /// </summary>
    internal sealed class BeamLightingMap
    {
        private static volatile bool diagnosticsEnabled;

        private readonly Map map;
        private readonly Dictionary<Building_Searchlight, LampSample> lamps =
            new Dictionary<Building_Searchlight, LampSample>(LampReferenceComparer.Instance);
        private readonly Dictionary<int, byte> aggregate = new Dictionary<int, byte>();
        private readonly List<int> changedCells = new List<int>();
        private readonly Dictionary<int, byte> pendingUpdates = new Dictionary<int, byte>();

        private long sampleGenerationCalls;
        private long sampleGenerationTicks;
        private long aggregateCalls;
        private long aggregateTicks;
        private long invalidationCalls;
        private long invalidationTicks;
        private long sampledCells;
        private long aggregatedCells;
        private long invalidatedCells;

        internal BeamLightingMap(Map map)
        {
            this.map = map ?? throw new ArgumentNullException(nameof(map));
        }

        /// <summary>Enables Stopwatch timing and work counters for the performance harness.</summary>
        internal static bool DiagnosticsEnabled
        {
            get => diagnosticsEnabled;
            set => diagnosticsEnabled = value;
        }

        internal long SampleGenerationCalls => sampleGenerationCalls;
        internal long SampleGenerationTicks => sampleGenerationTicks;
        internal long AggregateCalls => aggregateCalls;
        internal long AggregateTicks => aggregateTicks;
        internal long InvalidationCalls => invalidationCalls;
        internal long InvalidationTicks => invalidationTicks;
        internal long SampledCells => sampledCells;
        internal long AggregatedCells => aggregatedCells;
        internal long InvalidatedCells => invalidatedCells;

        internal void Publish(Building_Searchlight lamp, BeamField beam)
        {
            if (lamp == null) return;
            if (beam == null || !lamp.Spawned || lamp.Map != map)
            {
                Withdraw(lamp);
                return;
            }

            if (!lamps.TryGetValue(lamp, out LampSample sample))
            {
                sample = new LampSample();
                lamps.Add(lamp, sample);
            }

            int revision = beam.Revision;
            if (sample.HasRevision && sample.Revision == revision && ReferenceEquals(sample.Beam, beam)) return;

            Dictionary<int, byte> oldCells = sample.Cells;
            Dictionary<int, byte> newCells = sample.NextCells;
            newCells.Clear();

            bool timed = diagnosticsEnabled;
            long started = timed ? Stopwatch.GetTimestamp() : 0L;
            beam.SampleLight(newCells);
            if (timed)
            {
                sampleGenerationCalls++;
                sampleGenerationTicks += Stopwatch.GetTimestamp() - started;
                sampledCells += newCells.Count;
            }

            RecomputeChangedCells(sample, oldCells, newCells, true);

            sample.Cells = newCells;
            sample.NextCells = oldCells;
            sample.NextCells.Clear();
            sample.Beam = beam;
            sample.Revision = revision;
            sample.HasRevision = true;
        }

        internal void Withdraw(Building_Searchlight lamp)
        {
            if (lamp == null || !lamps.TryGetValue(lamp, out LampSample sample)) return;

            Dictionary<int, byte> oldCells = sample.Cells;
            Dictionary<int, byte> emptyCells = sample.NextCells;
            emptyCells.Clear();
            lamps.Remove(lamp);
            RecomputeChangedCells(sample, oldCells, emptyCells, false);
        }

        /// <summary>Invalidates this map's sparse reveal and releases retained lamp samples.</summary>
        internal void Clear()
        {
            bool timed = diagnosticsEnabled;
            long started = timed ? Stopwatch.GetTimestamp() : 0L;
            int clearedCells = aggregate.Count;
            LightingIntegration.ClearMap(map);
            if (timed)
            {
                invalidationCalls++;
                invalidatedCells += clearedCells;
                invalidationTicks += Stopwatch.GetTimestamp() - started;
            }

            lamps.Clear();
            aggregate.Clear();
            changedCells.Clear();
            pendingUpdates.Clear();
        }

        private void RecomputeChangedCells(LampSample changed, Dictionary<int, byte> oldCells,
            Dictionary<int, byte> newCells, bool changedRemainsActive)
        {
            bool timed = diagnosticsEnabled;
            long started = timed ? Stopwatch.GetTimestamp() : 0L;
            changedCells.Clear();
            foreach (KeyValuePair<int, byte> oldCell in oldCells)
            {
                if (!newCells.TryGetValue(oldCell.Key, out byte newIntensity) || newIntensity != oldCell.Value)
                    changedCells.Add(oldCell.Key);
            }
            foreach (KeyValuePair<int, byte> newCell in newCells)
            {
                if (!oldCells.ContainsKey(newCell.Key)) changedCells.Add(newCell.Key);
            }

            pendingUpdates.Clear();

            for (int i = 0; i < changedCells.Count; i++)
            {
                int index = changedCells[i];
                bool hadOld = oldCells.TryGetValue(index, out byte oldIntensity);
                bool hasNew = newCells.TryGetValue(index, out byte newIntensity);
                if (!hasNew) newIntensity = 0;

                bool hasAggregate = aggregate.TryGetValue(index, out byte currentMaximum);
                if (!hasAggregate) currentMaximum = 0;
                byte maximum = currentMaximum;

                if (newIntensity > currentMaximum)
                {
                    // The cached maximum proves no other beam needs to be read.
                    maximum = newIntensity;
                }
                else if (hadOld && oldIntensity >= currentMaximum && newIntensity < currentMaximum)
                {
                    // Only a decrease of a current maximum can reveal a lower
                    // contributor. The common single-beam path needs no scan.
                    if (lamps.Count == 0) maximum = 0;
                    else if (changedRemainsActive && lamps.Count == 1) maximum = newIntensity;
                    else maximum = FindMaximum(index, changed, newCells);
                }

                if (maximum != currentMaximum || (maximum == 0 && hasAggregate))
                    pendingUpdates[index] = maximum;
            }

            if (timed)
            {
                aggregateCalls++;
                aggregatedCells += changedCells.Count;
                aggregateTicks += Stopwatch.GetTimestamp() - started;
            }

            if (pendingUpdates.Count != 0)
            {
                timed = diagnosticsEnabled;
                started = timed ? Stopwatch.GetTimestamp() : 0L;
                LightingIntegration.SetCells(map, pendingUpdates);
                if (timed)
                {
                    invalidationCalls++;
                    invalidatedCells += pendingUpdates.Count;
                    invalidationTicks += Stopwatch.GetTimestamp() - started;
                }

                foreach (KeyValuePair<int, byte> update in pendingUpdates)
                {
                    if (update.Value == 0) aggregate.Remove(update.Key);
                    else aggregate[update.Key] = update.Value;
                }
            }

            changedCells.Clear();
            pendingUpdates.Clear();
        }

        private byte FindMaximum(int index, LampSample changed, Dictionary<int, byte> newCells)
        {
            byte maximum = 0;
            foreach (LampSample candidate in lamps.Values)
            {
                Dictionary<int, byte> cells = ReferenceEquals(candidate, changed) ? newCells : candidate.Cells;
                if (cells.TryGetValue(index, out byte intensity) && intensity > maximum)
                    maximum = intensity;
            }
            return maximum;
        }

        private sealed class LampSample
        {
            internal Dictionary<int, byte> Cells = new Dictionary<int, byte>();
            internal Dictionary<int, byte> NextCells = new Dictionary<int, byte>();
            internal BeamField Beam;
            internal int Revision;
            internal bool HasRevision;
        }

        private sealed class LampReferenceComparer : IEqualityComparer<Building_Searchlight>
        {
            internal static readonly LampReferenceComparer Instance = new LampReferenceComparer();

            public bool Equals(Building_Searchlight left, Building_Searchlight right) => ReferenceEquals(left, right);
            public int GetHashCode(Building_Searchlight lamp) => RuntimeHelpers.GetHashCode(lamp);
        }
    }
}
