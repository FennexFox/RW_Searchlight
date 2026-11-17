using System;

namespace DirectedSearchlights
{
    // Pure grid traversal shared by aiming, mesh clipping and isolated tests.
    internal static class BeamGrid
    {
        internal static float HalfAngle(float range, float diameter)
        {
            if (range <= 0f) return 0f;
            diameter = Math.Max(2f, Math.Min(6f, diameter));
            return Math.Min(12f, (float)(Math.Atan2(diameter * 0.5f, range) * 180.0 / Math.PI));
        }

        // Match the cross-section at the illuminated cell, including clipped beams.
        internal static float SpotRadius(float range, float diameter, float spotDistance)
        {
            double slope = Math.Tan(HalfAngle(range, diameter) * Math.PI / 180.0);
            return Math.Max(0.5f, (float)(Math.Min(spotDistance, range) * slope));
        }

        internal static float GradientAlpha(float u, float v)
        {
            float lateral = Math.Abs(u * 2f - 1f);
            float radial = Math.Min(1f, v * 5f) * (1f - 0.5f * v);
            float alpha = radial * (1f - lateral * lateral);
            return Math.Max(0f, Math.Min(1f, alpha));
        }

        internal static float Trace(int originX, int originZ, float dx, float dz, float limit,
            Func<int, int, bool> transparent, out int lastX, out int lastZ)
        {
            int x = originX, z = originZ;
            lastX = x;
            lastZ = z;
            int stepX = dx > 0f ? 1 : -1;
            int stepZ = dz > 0f ? 1 : -1;
            float nextX = Math.Abs(dx) < 0.000001f ? float.PositiveInfinity : 0.5f / Math.Abs(dx);
            float nextZ = Math.Abs(dz) < 0.000001f ? float.PositiveInfinity : 0.5f / Math.Abs(dz);
            float deltaX = Math.Abs(dx) < 0.000001f ? float.PositiveInfinity : 1f / Math.Abs(dx);
            float deltaZ = Math.Abs(dz) < 0.000001f ? float.PositiveInfinity : 1f / Math.Abs(dz);
            while (true)
            {
                float t = Math.Min(nextX, nextZ);
                if (t > limit) return limit;
                if (Math.Abs(nextX - nextZ) < 0.00001f)
                {
                    if (!transparent(x + stepX, z) || !transparent(x, z + stepZ)
                        || !transparent(x + stepX, z + stepZ)) return t;
                    x += stepX; z += stepZ;
                    nextX += deltaX; nextZ += deltaZ;
                }
                else if (nextX < nextZ)
                {
                    x += stepX;
                    if (!transparent(x, z)) return t;
                    nextX += deltaX;
                }
                else
                {
                    z += stepZ;
                    if (!transparent(x, z)) return t;
                    nextZ += deltaZ;
                }
                lastX = x;
                lastZ = z;
            }
        }
    }
}
