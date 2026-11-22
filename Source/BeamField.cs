using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    // Simulation maintains the clipped mesh. Camera movement only changes its visibility.
    public sealed class BeamField : IDisposable
    {
        private const float AngularStep = 0.25f;
        private const float CornerOffset = 0.005f;
        private const int GradientStepsPerCell = 10;
        private static readonly Texture2D[] Gradients = new Texture2D[6 * GradientStepsPerCell + 1];

        private Mesh mesh;
        private bool meshDirty;
        private Material material;
        private int gradientDiameterStep;
        private Map map;
        private IntVec3 origin;
        private float heading;
        private float range;
        private float diameter;
        private int lastRebuildTick = int.MinValue;
        private CellRect obstacleBounds;
        private int cachedEdificeRevision;
        private readonly List<IntVec3> obstacles = new List<IntVec3>();
        private readonly List<IntVec3> relevantBlockers = new List<IntVec3>();
        private readonly List<IntVec3> previousRelevantBlockers = new List<IntVec3>();
        private readonly List<IntVec3> retainedRayEdifices = new List<IntVec3>();
        private Map retainedMap;
        private IntVec3 retainedFrom, retainedTo;
        private int retainedRevision;
        private bool hasObstacleCache;
        internal int ObstacleScanCount { get; private set; }
        internal int RetainedRayTraceCount { get; private set; }
        internal int VisualRayTraceCount { get; private set; }
        internal int MeshPoseCount { get; private set; }
        private CellRect reachedBounds;
        private bool hasReachedBounds;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<float> lanes = new List<float>();
        private readonly List<int> triangles = new List<int>();
        private readonly Func<int, int, bool> currentMapTransparency;
        private BeamPose pose;
        private BeamPose sampledPose;
        private Map sampledMap;
        private bool hasSampleSnapshot;
        private bool hasPotentialBlockingLane;

        public IntVec3 CenterCell { get; private set; } = IntVec3.Invalid;
        internal int Revision { get; private set; }
        internal int SampleRevision { get; private set; }

        public BeamField()
        {
            // Reused by every simulated cell ray; avoid a closure allocation per sample.
            currentMapTransparency = IsCurrentMapCellTransparent;
        }

        public void Update(Map newMap, IntVec3 newOrigin, float newHeading, float newRange, float newDiameter)
        {
            newHeading = Normalize(newHeading);
            newRange = Mathf.Max(0f, newRange);
            newDiameter = Mathf.Clamp(newDiameter, 2f, 6f);
            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if (map == newMap && origin == newOrigin && Mathf.Abs(Mathf.DeltaAngle(heading, newHeading)) < 0.01f
                && Mathf.Abs(range - newRange) < 0.01f && Mathf.Abs(diameter - newDiameter) < 0.01f)
            {
                if (tick == lastRebuildTick || (tick + origin.x * 7 + origin.z) % 15 != 0) return;
            }

            if (map != newMap || origin != newOrigin || newRange <= 0f) hasObstacleCache = false;
            map = newMap;
            origin = newOrigin;
            heading = newHeading;
            range = newRange;
            diameter = newDiameter;
            lastRebuildTick = tick;
            Rebuild();
        }

        // Test a cell on demand. The MVP has one endpoint, so tracing the entire
        // cone cell by cell every time a light turns would duplicate a lot of work.
        public bool Reaches(IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map) || !pose.IsValid ||
                !pose.TryGetSampleRay(cell.x, cell.z, out _, out _, out float rayX, out float rayZ,
                    out float rayLength, out float offsetX, out float offsetZ)) return false;
            if (obstacles.Count == 0) return true;
            float reached = BeamGrid.Trace(origin.x, origin.z, rayX, rayZ, rayLength,
                currentMapTransparency, out _, out _, offsetX, offsetZ);
            return reached >= rayLength - 0.0001f;
        }

        public bool VisibleIn(CellRect view) => hasReachedBounds && reachedBounds.Overlaps(view);

        public void Draw()
        {
            if (!hasReachedBounds || triangles.Count == 0) return;
            if (material == null)
                material = new Material(ShaderDatabase.TransparentPostLight)
                {
                    color = new Color(1f, 0.96f, 0.68f, 0.28f)
                };
            // RimWorld initializes loaded maps on its long-event thread. Unity
            // mesh and texture allocation/upload belong to the main-thread draw callback.
            if (meshDirty)
            {
                if (mesh == null) { mesh = new Mesh(); mesh.MarkDynamic(); }
                else mesh.Clear();
                mesh.SetVertices(vertices);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                material.mainTexture = GradientTexture(gradientDiameterStep);
                meshDirty = false;
            }
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
        }

        internal void SampleLight(Dictionary<int, byte> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            if (!hasReachedBounds || map == null || !pose.IsValid) return;

            bool traceForBlockers = hasPotentialBlockingLane;
            for (int forwardStep = 0; forwardStep <= pose.ScanMajorSteps; forwardStep++)
            {
                pose.GetScanRowBounds(forwardStep, out bool xMajor, out int relativeMajor,
                    out int minMinor, out int maxMinor);
                for (int relativeMinor = minMinor; relativeMinor <= maxMinor; relativeMinor++)
                {
                    int x = xMajor ? origin.x + relativeMajor : origin.x + relativeMinor;
                    int z = xMajor ? origin.z + relativeMinor : origin.z + relativeMajor;
                    if ((uint)x >= (uint)map.Size.x || (uint)z >= (uint)map.Size.z) continue;

                    float relativeX = x - origin.x;
                    float relativeZ = z - origin.z;
                    if (!pose.TryGetLane(relativeX, relativeZ, out float lane, out float depth)) continue;
                    if (traceForBlockers)
                    {
                        if (!pose.GetSampleRay(relativeX, relativeZ, lane, out float rayX, out float rayZ,
                            out float rayLength, out float offsetX, out float offsetZ)) continue;
                        float reached = BeamGrid.Trace(origin.x, origin.z, rayX, rayZ, rayLength,
                            currentMapTransparency, out _, out _, offsetX, offsetZ);
                        if (reached < rayLength - 0.0001f) continue;
                    }

                    IntVec3 cell = new IntVec3(x, 0, z);
                    destination[map.cellIndices.CellToIndex(cell)] = BeamLightField.IntensityAt(pose, lane, depth);
                }
            }
        }

        public void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (material != null) UnityEngine.Object.Destroy(material);
            mesh = null;
            material = null;
            map = null;
            hasObstacleCache = false;
            lastRebuildTick = int.MinValue;
            hasReachedBounds = false;
            pose = default;
            CenterCell = IntVec3.Invalid;
        }

        private static Texture2D GradientTexture(int diameterStep)
        {
            Texture2D gradient = Gradients[diameterStep];
            if (gradient != null) return gradient;
            float aimDiameter = (float)diameterStep / GradientStepsPerCell;
            gradient = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            gradient.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float alpha = BeamGrid.GradientAlpha((x + 0.5f) / 64f, (y + 0.5f) / 64f, aimDiameter);
                gradient.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            gradient.Apply(false, true);
            Gradients[diameterStep] = gradient;
            return gradient;
        }

        // Supercover traversal checks both touching cells at a grid corner. An open
        // door remains transparent because Verse's CanBeSeenOver owns that rule.
        public static bool Clear(Map map, IntVec3 from, IntVec3 to)
            => Clear(map, from, to, (x, z) => Transparent(map, x, z));

        internal bool ClearRetainedRay(Map map, IntVec3 from, IntVec3 to, int revision)
        {
            if (retainedMap == map && retainedFrom == from && retainedTo == to && retainedRevision == revision)
            {
                // Doors may change transparency between thing ticks without changing the edifice grid.
                foreach (IntVec3 cell in retainedRayEdifices)
                    if (!cell.CanBeSeenOverFast(map)) return false;
                return true;
            }
            return TraceRetainedRay(map, from, to, revision);
        }

        private bool TraceRetainedRay(Map map, IntVec3 from, IntVec3 to, int revision)
        {
            RetainedRayTraceCount++;
            retainedRayEdifices.Clear();
            bool clear = Clear(map, from, to, (x, z) =>
            {
                var cell = new IntVec3(x, 0, z);
                Building edifice = cell.GetEdifice(map);
                if (edifice == null) return true;
                retainedRayEdifices.Add(cell);
                return edifice.CanBeSeenOver();
            });
            retainedMap = clear ? map : null;
            retainedFrom = from;
            retainedTo = to;
            retainedRevision = revision;
            return clear;
        }

        private static bool Clear(Map map, IntVec3 from, IntVec3 to, Func<int, int, bool> transparent)
        {
            if (map == null || !from.InBounds(map) || !to.InBounds(map)) return false;
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 0.0001f) return true;
            return BeamGrid.Trace(from.x, from.z, dx / length, dz / length, length + 0.0001f,
                transparent, out _, out _) >= length - 0.0001f;
        }

        private void Rebuild()
        {
            unchecked { Revision++; }
            hasReachedBounds = false;
            CenterCell = IntVec3.Invalid;
            pose = default;
            relevantBlockers.Clear();
            hasPotentialBlockingLane = false;
            meshDirty = true;
            triangles.Clear();
            if (map == null || !origin.InBounds(map) || range <= 0f)
            {
                UpdateSampleRevision();
                return;
            }

            pose = new BeamPose(origin.x, origin.z, heading, range, diameter);
            float halfAngle = pose.HalfAngle;
            float overlayHeight = AltitudeLayer.MoteLow.AltitudeFor();
            float forwardX = pose.ForwardX;
            float forwardZ = pose.ForwardZ;
            float halfAngleRadians = halfAngle * Mathf.Deg2Rad;
            float capDepth = pose.CapDepth;
            float bodyLength = pose.BodyLength;
            if (bodyLength <= 0f) return;
            float spread = pose.Spread;
            // Short aims can hit the angle limit and be narrower than the configured diameter.
            gradientDiameterStep = Mathf.Clamp(Mathf.RoundToInt(2f * capDepth * GradientStepsPerCell),
                0, Gradients.Length - 1);
            // Enclose the full visual sector, including radial extrema between the sides.
            float boundsRayLength = (range + capDepth) / Mathf.Cos(halfAngleRadians);
            float leftRadians = (heading - halfAngle) * Mathf.Deg2Rad;
            float rightRadians = (heading + halfAngle) * Mathf.Deg2Rad;
            float leftX = origin.x + boundsRayLength * Mathf.Sin(leftRadians);
            float leftZ = origin.z + boundsRayLength * Mathf.Cos(leftRadians);
            float rightX = origin.x + boundsRayLength * Mathf.Sin(rightRadians);
            float rightZ = origin.z + boundsRayLength * Mathf.Cos(rightRadians);
            int minX = Mathf.FloorToInt(Mathf.Min(origin.x, Mathf.Min(leftX, rightX))) - 1;
            int maxX = Mathf.CeilToInt(Mathf.Max(origin.x, Mathf.Max(leftX, rightX))) + 1;
            int minZ = Mathf.FloorToInt(Mathf.Min(origin.z, Mathf.Min(leftZ, rightZ))) - 1;
            int maxZ = Mathf.CeilToInt(Mathf.Max(origin.z, Mathf.Max(leftZ, rightZ))) + 1;
            IntVec3 centerLast;
            pose.GetLaneRay(0f, out float centerOffsetX, out float centerOffsetZ, out float centerRayX, out float centerRayZ);
            Trace(origin, centerRayX, centerRayZ, bodyLength, out centerLast, centerOffsetX, centerOffsetZ);
            if (centerLast != origin && centerLast.InBounds(map)) CenterCell = centerLast;
            reachedBounds = CellRect.FromLimits(minX, minZ, maxX, maxZ);
            hasReachedBounds = true;

            // Corner event rays preserve sharp occlusion transitions; the regular
            // rays bound visible curvature when no obstacle corner is nearby.
            lanes.Clear();
            int stepsPerSide = Mathf.Max(12, Mathf.CeilToInt(halfAngle / AngularStep));
            for (int i = -stepsPerSide; i <= stepsPerSide; i++)
                lanes.Add((float)i / stepsPerSide);
            // Edifice membership includes natural rock. Door transparency is tested live below.
            int edificeRevision = map.GetComponent<SearchlightMap>().EdificeRevision;
            if (!hasObstacleCache || cachedEdificeRevision != edificeRevision
                || minX < obstacleBounds.minX || maxX > obstacleBounds.maxX
                || minZ < obstacleBounds.minZ || maxZ > obstacleBounds.maxZ)
            {
                obstacleBounds = reachedBounds.ExpandedBy(2);
                obstacles.Clear();
                for (int x = Mathf.Max(0, obstacleBounds.minX); x <= Mathf.Min(map.Size.x - 1, obstacleBounds.maxX); x++)
                for (int z = Mathf.Max(0, obstacleBounds.minZ); z <= Mathf.Min(map.Size.z - 1, obstacleBounds.maxZ); z++)
                {
                    IntVec3 cell = new IntVec3(x, 0, z);
                    if (cell != origin && cell.GetEdifice(map) != null) obstacles.Add(cell);
                }
                hasObstacleCache = true;
                cachedEdificeRevision = edificeRevision;
                ObstacleScanCount++;
            }

            // In unobstructed space every strip reaches its analytical rounded tip.
            // Keep the supercover trace for beams touching blockers or the map edge.
            // A sub-cell focus narrower than the lens can send converging strips
            // across the axis beyond the focus, outside the ordinary sector.
            bool needsClipping = spread < 0f || minX < 0 || minZ < 0 || maxX >= map.Size.x || maxZ >= map.Size.z;
            foreach (IntVec3 cell in obstacles)
            {
                int x = cell.x, z = cell.z;
                if (x < minX || x > maxX || z < minZ || z > maxZ) continue;
                if (!pose.IsPotentialBlockerCell(x - origin.x, z - origin.z)) continue;
                if (cell.CanBeSeenOverFast(map)) continue;
                needsClipping = true;
                relevantBlockers.Add(cell);
                for (int ix = -1; ix <= 1; ix += 2)
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    float cornerX = x + ix * 0.5f - origin.x;
                    float cornerZ = z + iz * 0.5f - origin.z;
                    float depth = cornerX * forwardX + cornerZ * forwardZ - BeamPose.LensForward;
                    if (depth <= 0f) continue;
                    float halfWidth = BeamPose.LensHalfWidth + depth * spread;
                    if (halfWidth <= 0f) continue;
                    float lane = (cornerX * pose.RightX + cornerZ * pose.RightZ) / halfWidth;
                    if (Mathf.Abs(lane) > 1f) continue;
                    float nudge = CornerOffset / halfAngle;
                    lanes.Add(Mathf.Max(-1f, lane - nudge));
                    lanes.Add(lane);
                    lanes.Add(Mathf.Min(1f, lane + nudge));
                }
            }
            hasPotentialBlockingLane = relevantBlockers.Count > 0 && needsClipping;
            UpdateSampleRevision();
            if (needsClipping) lanes.Sort();
            MeshPoseCount++;
            vertices.Clear();
            uvs.Clear();
            Vector3 lens = new Vector3(origin.x + 0.5f, overlayHeight, origin.z + 0.5f);
            float previous = float.NegativeInfinity;
            foreach (float lane in lanes)
            {
                if (lane - previous < 0.00001f) continue;
                previous = lane;
                pose.GetLaneRay(lane, out float offsetX, out float offsetZ, out float rayX, out float rayZ);
                Vector3 start = lens + new Vector3(offsetX, 0f, offsetZ);
                Vector3 ray = new Vector3(rayX, 0f, rayZ);
                float u = (lane + 1f) * 0.5f;
                float limit = BeamGrid.VisualRayLimit(bodyLength, capDepth, u);
                float depth = limit;
                if (needsClipping)
                {
                    float rayScale = ray.magnitude;
                    Vector3 direction = ray / rayScale;
                    float traveled = Trace(origin, direction.x, direction.z, limit * rayScale,
                        out _, offsetX, offsetZ);
                    depth = Mathf.Min(limit, traveled / rayScale);
                    VisualRayTraceCount++;
                }
                float bodyDepth = Mathf.Min(bodyLength, depth);
                vertices.Add(start);
                vertices.Add(start + ray * bodyDepth);
                vertices.Add(start + ray * depth);
                uvs.Add(new Vector2(u, 0f));
                uvs.Add(new Vector2(u, BeamGrid.GradientV(bodyLength, limit, bodyDepth)));
                uvs.Add(new Vector2(u, BeamGrid.GradientV(bodyLength, limit, depth)));
            }
            for (int a = 0; a + 5 < vertices.Count; a += 3)
            {
                // Join adjacent strips along the flat lens face.
                triangles.Add(a);
                triangles.Add(a + 4);
                triangles.Add(a + 3);
                triangles.Add(a);
                triangles.Add(a + 1);
                triangles.Add(a + 4);
                if (uvs[a + 2].y > BeamGrid.FocusV)
                {
                    triangles.Add(a + 1);
                    triangles.Add(a + 2);
                    triangles.Add(a + 5);
                }
                if (uvs[a + 5].y > BeamGrid.FocusV)
                {
                    triangles.Add(a + 1);
                    triangles.Add(a + 5);
                    triangles.Add(a + 4);
                }
            }
        }

        private void UpdateSampleRevision()
        {
            bool changed = !hasSampleSnapshot || !ReferenceEquals(sampledMap, map) ||
                !SameSamplePose(sampledPose, pose) || previousRelevantBlockers.Count != relevantBlockers.Count;
            if (!changed)
            {
                for (int i = 0; i < relevantBlockers.Count; i++)
                {
                    if (previousRelevantBlockers[i].Equals(relevantBlockers[i])) continue;
                    changed = true;
                    break;
                }
            }

            if (changed) unchecked { SampleRevision++; }
            sampledMap = map;
            sampledPose = pose;
            previousRelevantBlockers.Clear();
            previousRelevantBlockers.AddRange(relevantBlockers);
            hasSampleSnapshot = true;
        }

        private static bool SameSamplePose(BeamPose left, BeamPose right)
        {
            if (left.IsValid != right.IsValid) return false;
            if (!left.IsValid) return true;
            return left.OriginX == right.OriginX && left.OriginZ == right.OriginZ &&
                left.Heading == right.Heading && left.Range == right.Range && left.Diameter == right.Diameter;
        }

        private static float Normalize(float angle)
        {
            angle %= 360f;
            return angle < 0f ? angle + 360f : angle;
        }

        private float Trace(IntVec3 from, float dx, float dz, float limit, out IntVec3 last,
            float offsetX = 0f, float offsetZ = 0f)
        {
            int x, z;
            float distance = BeamGrid.Trace(from.x, from.z, dx, dz, limit,
                currentMapTransparency, out x, out z, offsetX, offsetZ);
            last = new IntVec3(x, 0, z);
            return distance;
        }

        private bool IsCurrentMapCellTransparent(int x, int z) => Transparent(map, x, z);

        private static bool Transparent(Map map, int x, int z)
        {
            IntVec3 cell = new IntVec3(x, 0, z);
            return cell.InBounds(map) && cell.CanBeSeenOverFast(map);
        }
    }
}
