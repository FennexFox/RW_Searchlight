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
        private CellRect reachedBounds;
        private bool hasReachedBounds;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<float> angles = new List<float>();
        private readonly List<int> triangles = new List<int>();

        public IntVec3 CenterCell { get; private set; } = IntVec3.Invalid;

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
            if (map == null || range <= 0f || !cell.InBounds(map) || cell == origin) return false;
            int dx = cell.x - origin.x;
            int dz = cell.z - origin.z;
            if (dx * dx + dz * dz > range * range + 0.001f) return false;
            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            return Mathf.Abs(Mathf.DeltaAngle(heading, bearing)) <= BeamGrid.HalfAngle(range, diameter) + 0.0001f
                && Clear(map, origin, cell);
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

        public void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (material != null) UnityEngine.Object.Destroy(material);
            mesh = null;
            material = null;
            hasReachedBounds = false;
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
        {
            if (map == null || !from.InBounds(map) || !to.InBounds(map)) return false;
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 0.0001f) return true;
            IntVec3 ignored;
            return Trace(map, from, dx / length, dz / length, length + 0.0001f, out ignored) >= length - 0.0001f;
        }

        private void Rebuild()
        {
            hasReachedBounds = false;
            CenterCell = IntVec3.Invalid;
            meshDirty = true;
            triangles.Clear();
            if (map == null || !origin.InBounds(map) || range <= 0f) return;

            float halfAngle = BeamGrid.HalfAngle(range, diameter);
            float overlayHeight = AltitudeLayer.MoteLow.AltitudeFor();
            float headingRadians = heading * Mathf.Deg2Rad;
            float forwardX = Mathf.Sin(headingRadians);
            float forwardZ = Mathf.Cos(headingRadians);
            float halfAngleRadians = halfAngle * Mathf.Deg2Rad;
            float slope = Mathf.Tan(halfAngleRadians);
            float capDepth = range * slope;
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
            Trace(map, origin, forwardX, forwardZ, range, out centerLast);
            if (centerLast != origin && centerLast.InBounds(map)) CenterCell = centerLast;
            reachedBounds = CellRect.FromLimits(minX, minZ, maxX, maxZ);
            hasReachedBounds = true;

            // Corner event rays preserve sharp occlusion transitions; the regular
            // rays bound visible curvature when no obstacle corner is nearby.
            angles.Clear();
            int stepsPerSide = Mathf.Max(12, Mathf.CeilToInt(halfAngle / AngularStep));
            for (int i = -stepsPerSide; i <= stepsPerSide; i++)
                angles.Add(halfAngle * ((float)i / stepsPerSide));
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                IntVec3 cell = new IntVec3(x, 0, z);
                float along = (x - origin.x) * forwardX + (z - origin.z) * forwardZ;
                float across = (x - origin.x) * forwardZ - (z - origin.z) * forwardX;
                if (along < -1f || Mathf.Abs(across) > Mathf.Max(0f, along) * slope + 1f) continue;
                if (!cell.InBounds(map) || cell.CanBeSeenOverFast(map)) continue;
                for (int ix = -1; ix <= 1; ix += 2)
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    float cornerX = x + ix * 0.5f - origin.x;
                    float cornerZ = z + iz * 0.5f - origin.z;
                    float relative = Mathf.DeltaAngle(heading, Mathf.Atan2(cornerX, cornerZ) * Mathf.Rad2Deg);
                    if (relative < -halfAngle || relative > halfAngle) continue;
                    angles.Add(Mathf.Max(-halfAngle, relative - CornerOffset));
                    angles.Add(relative);
                    angles.Add(Mathf.Min(halfAngle, relative + CornerOffset));
                }
            }
            angles.Sort();
            vertices.Clear();
            uvs.Clear();
            Vector3 lens = new Vector3(origin.x + 0.5f, overlayHeight, origin.z + 0.5f);
            float previous = float.NegativeInfinity;
            foreach (float relative in angles)
            {
                if (relative - previous < 0.0001f) continue;
                previous = relative;
                float radians = (heading + relative) * Mathf.Deg2Rad;
                float dx = Mathf.Sin(radians);
                float dz = Mathf.Cos(radians);
                float u = (relative + halfAngle) / (2f * halfAngle);
                float visualLimit = BeamGrid.VisualRayLimit(range, capDepth, u);
                IntVec3 ignored;
                float distance = Trace(map, origin, dx, dz, visualLimit, out ignored);
                float bodyDistance = Mathf.Min(range, distance);
                Vector3 direction = new Vector3(dx, 0f, dz);
                // Split at the aim distance so the cap fade leaves the body gradient intact.
                vertices.Add(lens);
                vertices.Add(lens + direction * bodyDistance);
                vertices.Add(lens + direction * distance);
                uvs.Add(new Vector2(u, 0f));
                uvs.Add(new Vector2(u, BeamGrid.GradientV(range, visualLimit, bodyDistance)));
                uvs.Add(new Vector2(u, BeamGrid.GradientV(range, visualLimit, distance)));
            }
            for (int a = 0; a + 5 < vertices.Count; a += 3)
            {
                // The near vertices coincide at the lens, so the body needs only one triangle.
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

        private static float Normalize(float angle)
        {
            angle %= 360f;
            return angle < 0f ? angle + 360f : angle;
        }

        private static float Trace(Map map, IntVec3 from, float dx, float dz, float limit, out IntVec3 last)
        {
            int x, z;
            float distance = BeamGrid.Trace(from.x, from.z, dx, dz, limit,
                (cellX, cellZ) => Transparent(map, cellX, cellZ), out x, out z);
            last = new IntVec3(x, 0, z);
            return distance;
        }

        private static bool Transparent(Map map, int x, int z)
        {
            IntVec3 cell = new IntVec3(x, 0, z);
            return cell.InBounds(map) && cell.CanBeSeenOverFast(map);
        }
    }
}
