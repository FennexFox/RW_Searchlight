using System;

namespace DirectedSearchlights
{
    // Pure grid traversal shared by aiming, mesh clipping and isolated tests.
    internal static class BeamGrid
    {
        internal const float FocusV = 0.75f;

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

        internal static float VisualRayLimit(float range, float capDepth, float u)
        {
            float lateral = u * 2f - 1f;
            return range + capDepth * (float)Math.Sqrt(Math.Max(0f, 1f - lateral * lateral));
        }

        // Keep the focus at one texture coordinate even when a wall shortens the ray.
        internal static float GradientV(float range, float visualLimit, float distance)
        {
            if (distance <= range) return FocusV * (distance / range);
            return FocusV + (1f - FocusV) * ((distance - range) / (visualLimit - range));
        }

        internal static float GradientAlpha(float u, float v, float aimDiameter)
        {
            float lateral = Math.Abs(u * 2f - 1f);
            float radial;
            if (v <= FocusV)
            {
                float distance = v / FocusV;
                radial = Math.Min(1f, 0.55f + distance * 2.25f) * (1f - 0.5f * distance);
            }
            else
            {
                float remaining = Math.Max(0f, (1f - v) / (1f - FocusV));
                radial = 0.5f * remaining * remaining * (3f - 2f * remaining);
            }
            // Use a softened inverse-area curve for the overlay. A six-cell
            // cross-section halves the alpha, preserving a visible center at
            // full spread. The rounded cap continues the aim point's fade.
            float diameter = aimDiameter * Math.Min(1f, v / FocusV);
            float spread = diameter / 6f;
            float alpha = radial * (1f - lateral * lateral) / (1f + spread * spread);
            return Math.Max(0f, Math.Min(1f, alpha));
        }

        internal static float Trace(int originX, int originZ, float dx, float dz, float limit,
            Func<int, int, bool> transparent, out int lastX, out int lastZ, float offsetX = 0f, float offsetZ = 0f)
        {
            int x = originX, z = originZ;
            lastX = x;
            lastZ = z;
            int stepX = dx > 0f ? 1 : -1;
            int stepZ = dz > 0f ? 1 : -1;
            float nextX = Math.Abs(dx) < 0.000001f ? float.PositiveInfinity : (0.5f - stepX * offsetX) / Math.Abs(dx);
            float nextZ = Math.Abs(dz) < 0.000001f ? float.PositiveInfinity : (0.5f - stepZ * offsetZ) / Math.Abs(dz);
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
