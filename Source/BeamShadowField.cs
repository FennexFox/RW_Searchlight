using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using CoverCell = DirectedSearchlights.BeamShadowGeometry.CoverCell;

namespace DirectedSearchlights
{
    // The same occupied squares feed the visual strips and the cell-center sampler.
    internal sealed class BeamShadowField
    {
        private const float GeometryTolerance = 0.00001f;
        private List<CoverCell> cells = new List<CoverCell>();
        private List<CoverCell> nextCells = new List<CoverCell>();
        private BeamPose pose;

        internal bool HasCover => cells.Count != 0;
        internal IReadOnlyList<CoverCell> Cells => cells;

        internal static float Opacity(Thing thing)
        {
            ThingDef def = thing.def;
            if (thing is Pawn pawn)
            {
                float explicitCover = def.BaseBlockChance();
                return BeamShadowGeometry.PawnOpacity(explicitCover, pawn.BodySize);
            }

            switch (def.category)
            {
                case ThingCategory.Building:
                case ThingCategory.Plant:
                case ThingCategory.Item:
                    return Mathf.Clamp01(thing.BaseBlockChance());
                default:
                    return 0f;
            }
        }

        internal bool CouldAffect(IntVec3 cell)
            => CouldAffect(cell.x - pose.OriginX, cell.z - pose.OriginZ);

        internal bool Refresh(Map map, BeamPose newPose, bool enabled)
        {
            pose = newPose;
            nextCells.Clear();
            if (enabled && pose.IsValid) Capture(map);
            bool changed = nextCells.Count != cells.Count;
            for (int i = 0; !changed && i < cells.Count; i++)
                changed = !cells[i].Equals(nextCells[i]);
            List<CoverCell> previous = cells;
            cells = nextCells;
            nextCells = previous;
            return changed;
        }

        internal void Clear()
        {
            cells.Clear();
            nextCells.Clear();
            pose = default;
        }

        private void Capture(Map map)
        {
            // The cell-center scan ends at the focus. Cover can intersect the cap
            // and can touch the beam even when its own cell center lies outside.
            int steps = Mathf.CeilToInt(pose.Range + 2f * pose.CapDepth + 1f);
            float extendedWidth = Mathf.Abs(BeamPose.LensHalfWidth + (pose.BodyLength + pose.CapDepth) * pose.Spread);
            float halfSpan = (Mathf.Max(BeamPose.LensHalfWidth, extendedWidth) + 0.707107f) / pose.ScanMajorMagnitude;
            for (int i = 0; i <= steps; i++)
            {
                int major = i * pose.ScanMajorSign;
                float centerMinor = i * pose.ScanMinorComponent / pose.ScanMajorMagnitude;
                int minMinor = Mathf.CeilToInt(centerMinor - halfSpan);
                int maxMinor = Mathf.FloorToInt(centerMinor + halfSpan);
                for (int minor = minMinor; minor <= maxMinor; minor++)
                {
                    int x = pose.ScanXMajor ? major : minor;
                    int z = pose.ScanXMajor ? minor : major;
                    IntVec3 cell = new IntVec3(pose.OriginX + x, 0, pose.OriginZ + z);
                    if (!cell.InBounds(map) || !CouldAffect(x, z)) continue;
                    List<Thing> contents = cell.GetThingList(map);
                    if (contents.Count == 0 || !cell.CanBeSeenOverFast(map)) continue;
                    float opacity = 0f;
                    int thingId = int.MaxValue;
                    for (int j = 0; j < contents.Count; j++)
                    {
                        Thing thing = contents[j];
                        float cover = Opacity(thing);
                        if (BeamShadowGeometry.PreferCover(cover, thing.thingIDNumber, opacity, thingId))
                        {
                            opacity = cover;
                            thingId = thing.thingIDNumber;
                        }
                    }
                    if (opacity > 0f)
                        nextCells.Add(new CoverCell(x, z, thingId, 1f - opacity));
                }
            }
        }

        private bool CouldAffect(int x, int z)
        {
            if (!pose.IsValid || (x == 0 && z == 0)) return false;
            float depth = x * pose.ForwardX + z * pose.ForwardZ - BeamPose.LensForward;
            float cellExtent = 0.5f * (Mathf.Abs(pose.ForwardX) + Mathf.Abs(pose.ForwardZ));
            float near = Mathf.Max(0f, depth - cellExtent);
            float far = Mathf.Min(pose.BodyLength + pose.CapDepth, depth + cellExtent);
            if (near >= far) return false;

            float lateral = Mathf.Abs(x * pose.RightX + z * pose.RightZ);
            float width = Mathf.Max(Mathf.Abs(BeamPose.LensHalfWidth + near * pose.Spread),
                Mathf.Abs(BeamPose.LensHalfWidth + far * pose.Spread));
            return lateral <= width + cellExtent + GeometryTolerance;
        }
    }
}
