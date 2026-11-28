using System;
using System.Collections.Generic;

namespace DirectedSearchlights
{
    internal sealed class BeamShadowGeometry
    {
        private const float GeometryTolerance = 0.00001f;
        private const float SaturatedIntensity = 0.5f / 3.6f * 255f;
        private readonly Dictionary<int, Hit> hitsByThing = new Dictionary<int, Hit>();
        private readonly List<Hit> orderedHits = new List<Hit>();

        internal static float PawnOpacity(float explicitCover, float bodySize)
            => explicitCover > 0f ? Math.Min(1f, explicitCover) : Math.Min(0.9f, 0.35f * Math.Max(0f, bodySize));

        internal static bool PreferCover(float candidateOpacity, int candidateThingId, float opacity, int thingId)
            => candidateOpacity > opacity || (candidateOpacity > 0f && candidateOpacity == opacity && candidateThingId < thingId);

        internal static byte ApplyTransmission(byte intensity, float transmission)
            => transmission >= 1f ? intensity : (byte)Math.Floor(Math.Min(SaturatedIntensity, intensity) * transmission + 0.5f);

        internal float Transmission(BeamPose pose, IReadOnlyList<CoverCell> cells, float lane, float depth)
        {
            CollectHits(pose, cells, lane);
            float transmission = 1f;
            foreach (Hit hit in hitsByThing.Values)
                if (depth > hit.Exit + GeometryTolerance)
                    transmission *= hit.Cell.Transmission;
            return transmission;
        }

        internal static void AddCornerLanes(BeamPose pose, IReadOnlyList<CoverCell> cells, List<float> lanes)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                CoverCell cell = cells[i];
                for (int dx = -1; dx <= 1; dx += 2)
                for (int dz = -1; dz <= 1; dz += 2)
                {
                    float x = cell.X + 0.5f * dx;
                    float z = cell.Z + 0.5f * dz;
                    float depth = x * pose.ForwardX + z * pose.ForwardZ - BeamPose.LensForward;
                    if (depth < 0f) continue;
                    float halfWidth = BeamPose.LensHalfWidth + depth * pose.Spread;
                    if (Math.Abs(halfWidth) <= GeometryTolerance) continue;
                    float lane = (x * pose.RightX + z * pose.RightZ) / halfWidth;
                    if (Math.Abs(lane) < 1f) lanes.Add(lane);
                }
            }
        }

        internal List<Hit> VisualHits(BeamPose pose, IReadOnlyList<CoverCell> cells, float leftLane, float rightLane)
        {
            CollectHits(pose, cells, 0.5f * (leftLane + rightLane));
            orderedHits.Clear();
            foreach (Hit hit in hitsByThing.Values) orderedHits.Add(hit);
            orderedHits.Sort(CompareHits);
            return orderedHits;
        }

        internal static float BoundaryExit(BeamPose pose, CoverCell cell, float lane)
        {
            // A strip approaches the corner from the interior. Even though
            // tangent rays transmit fully, that limiting exit ends the strip.
            var ray = new LaneRay(pose, lane);
            return ray.GetExit(cell, false, out float exit) ? exit : 0f;
        }

        private void CollectHits(BeamPose pose, IReadOnlyList<CoverCell> cells, float lane)
        {
            hitsByThing.Clear();
            var ray = new LaneRay(pose, lane);
            for (int i = 0; i < cells.Count; i++)
            {
                CoverCell cell = cells[i];
                if (!ray.GetExit(cell, true, out float exit)) continue;
                if (!hitsByThing.TryGetValue(cell.ThingId, out Hit hit) || exit > hit.Exit)
                    hitsByThing[cell.ThingId] = new Hit(cell, exit);
            }
        }

        private static int CompareHits(Hit left, Hit right) => left.Exit.CompareTo(right.Exit);

        internal readonly struct CoverCell
        {
            internal readonly int X;
            internal readonly int Z;
            internal readonly int ThingId;
            internal readonly float Transmission;

            internal CoverCell(int x, int z, int thingId, float transmission)
            {
                X = x;
                Z = z;
                ThingId = thingId;
                Transmission = transmission;
            }

            internal bool Equals(CoverCell other)
                => X == other.X && Z == other.Z && ThingId == other.ThingId && Transmission == other.Transmission;
        }

        internal readonly struct Hit
        {
            internal readonly CoverCell Cell;
            internal readonly float Exit;

            internal Hit(CoverCell cell, float exit)
            {
                Cell = cell;
                Exit = exit;
            }
        }

        private readonly struct LaneRay
        {
            private readonly float startX, startZ;
            private readonly float rateX, rateZ;

            internal LaneRay(BeamPose pose, float lane)
            {
                pose.GetLaneRay(lane, out startX, out startZ, out rateX, out rateZ);
            }

            internal bool GetExit(CoverCell cell, bool excludeTangencies, out float exit)
            {
                float entry = 0f;
                exit = float.PositiveInfinity;
                if (!IntersectAxis(startX, rateX, cell.X, excludeTangencies, ref entry, ref exit) ||
                    !IntersectAxis(startZ, rateZ, cell.Z, excludeTangencies, ref entry, ref exit))
                    return false;

                return excludeTangencies ? exit > entry + GeometryTolerance : exit >= entry - GeometryTolerance;
            }

            private static bool IntersectAxis(float start, float rate, int cell, bool excludeTangencies,
                ref float entry, ref float exit)
            {
                float min = cell - 0.5f - start;
                float max = cell + 0.5f - start;
                if (Math.Abs(rate) <= GeometryTolerance)
                    return excludeTangencies
                        ? min < -GeometryTolerance && max > GeometryTolerance
                        : min <= GeometryTolerance && max >= -GeometryTolerance;

                float a = min / rate;
                float b = max / rate;
                entry = Math.Max(entry, Math.Min(a, b));
                exit = Math.Min(exit, Math.Max(a, b));
                return true;
            }
        }
    }
}
