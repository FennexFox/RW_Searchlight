using System;

namespace DirectedSearchlights
{
    /// <summary>Gameplay intensity profile for the shared finite-lens footprint.</summary>
    internal static class BeamLightField
    {
        internal const byte DefaultFocusCenterIntensity = 36;
        private const float ReferenceWidth = 4f;
        private const float WidthAttenuationScale = 6f;
        private const float EdgeMinimum = 0.2f;
        private const float EdgeVariation = 1f - EdgeMinimum;

        /// <summary>
        /// Convert geometric lane coverage to raw grayscale RGB. At a four-cell
        /// focus the center is calibrated to byte 36; width attenuation is
        /// normalized to that reference and the aperture edge stays nonzero.
        /// </summary>
        internal static byte IntensityAt(BeamPose pose, float lane, float depth)
        {
            float width = pose.WidthAtDepth(depth);
            float reference = 1f + Square(ReferenceWidth / WidthAttenuationScale);
            float widthFactor = reference / (1f + Square(width / WidthAttenuationScale));
            float clampedLane = Math.Max(-1f, Math.Min(1f, lane));
            float edgeFactor = EdgeMinimum + EdgeVariation * (1f - clampedLane * clampedLane);
            float intensity = DefaultFocusCenterIntensity * widthFactor * edgeFactor;
            int rounded = (int)Math.Floor(intensity + 0.5f);
            return (byte)Math.Max(1, Math.Min(255, rounded));
        }

        private static float Square(float value) => value * value;
    }
}
