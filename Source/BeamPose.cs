using System;

namespace DirectedSearchlights
{
    /// <summary>
    /// A finite-aperture ground beam pose. Cell-center coverage and mesh lane
    /// construction use the same lens plane, focus plane, and lane equation.
    /// </summary>
    internal readonly struct BeamPose
    {
        internal const float LensHalfWidth = 13f / 128f;
        internal const float LensForward = 35f / 128f;
        private const float RangeTolerance = 0.001f;
        private const float LaneTolerance = 0.0001f;

        internal readonly int OriginX;
        internal readonly int OriginZ;
        internal readonly float Heading;
        internal readonly float Range;
        internal readonly float Diameter;
        internal readonly float HalfAngle;
        internal readonly float ForwardX;
        internal readonly float ForwardZ;
        internal readonly float RightX;
        internal readonly float RightZ;
        internal readonly float CapDepth;
        internal readonly float BodyLength;
        internal readonly float Spread;
        internal readonly bool IsValid;

        internal BeamPose(int originX, int originZ, float heading, float range, float diameter)
        {
            OriginX = originX;
            OriginZ = originZ;
            Heading = Normalize(heading);
            Range = Math.Max(0f, range);
            Diameter = Math.Max(2f, Math.Min(6f, diameter));
            HalfAngle = BeamGrid.HalfAngle(Range, Diameter);

            double radians = Heading * Math.PI / 180.0;
            ForwardX = (float)Math.Sin(radians);
            ForwardZ = (float)Math.Cos(radians);
            RightX = ForwardZ;
            RightZ = -ForwardX;

            CapDepth = Range * (float)Math.Tan(HalfAngle * Math.PI / 180.0);
            BodyLength = Range - LensForward;
            IsValid = Range > 0f && BodyLength > 0f;
            Spread = BodyLength > 0f ? (CapDepth - LensHalfWidth) / BodyLength : 0f;
        }

        internal float WidthAtDepth(float depth)
        {
            float clampedDepth = Math.Max(0f, Math.Min(BodyLength, depth));
            return 2f * (LensHalfWidth + clampedDepth * Spread);
        }

        /// <summary>Return the physical lane and forward depth for a point center-relative to the building.</summary>
        internal bool TryGetLane(float relativeX, float relativeZ, out float lane, out float depth)
        {
            lane = 0f;
            depth = 0f;
            if (!IsValid || relativeX * relativeX + relativeZ * relativeZ > Range * Range + RangeTolerance)
                return false;

            float forwardDepth = relativeX * ForwardX + relativeZ * ForwardZ;
            depth = forwardDepth - LensForward;
            if (depth < -LaneTolerance || depth > BodyLength + LaneTolerance) return false;
            if (depth < 0f) depth = 0f;

            float halfWidth = LensHalfWidth + depth * Spread;
            if (halfWidth <= 0.00001f) return false;
            float lateral = relativeX * RightX + relativeZ * RightZ;
            lane = lateral / halfWidth;
            return Math.Abs(lane) <= 1f + LaneTolerance;
        }

        /// <summary>
        /// Resolve the ray from the finite lens to a cell center. The returned
        /// offsets are relative to the building center for BeamGrid.Trace.
        /// </summary>
        internal bool TryGetSampleRay(int cellX, int cellZ, out float lane, out float depth,
            out float rayX, out float rayZ, out float rayLength, out float offsetX, out float offsetZ)
        {
            lane = depth = rayX = rayZ = rayLength = offsetX = offsetZ = 0f;
            float relativeX = cellX - OriginX;
            float relativeZ = cellZ - OriginZ;
            if (!TryGetLane(relativeX, relativeZ, out lane, out depth)) return false;

            GetLaneRay(lane, out offsetX, out offsetZ, out _, out _);
            float toCellX = relativeX - offsetX;
            float toCellZ = relativeZ - offsetZ;
            rayLength = (float)Math.Sqrt(toCellX * toCellX + toCellZ * toCellZ);
            if (rayLength <= 0.00001f) return false;
            rayX = toCellX / rayLength;
            rayZ = toCellZ / rayLength;
            return true;
        }

        /// <summary>Build the exact lane origin and direction used by the rendered mesh.</summary>
        internal void GetLaneRay(float lane, out float offsetX, out float offsetZ, out float rayX, out float rayZ)
        {
            offsetX = ForwardX * LensForward + RightX * lane * LensHalfWidth;
            offsetZ = ForwardZ * LensForward + RightZ * lane * LensHalfWidth;
            rayX = ForwardX + RightX * lane * Spread;
            rayZ = ForwardZ + RightZ * lane * Spread;
        }

        private static float Normalize(float angle)
        {
            angle %= 360f;
            return angle < 0f ? angle + 360f : angle;
        }
    }
}
