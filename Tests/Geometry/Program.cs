using System;
using System.Collections.Generic;
using DirectedSearchlights;

static class Program
{
    static int failures;

    static void Check(bool condition, string name)
    {
        if (condition) return;
        Console.Error.WriteLine("FAIL: " + name);
        failures++;
    }

    static float Trace(float dx, float dz, float limit, HashSet<(int, int)> blocked,
        out int lastX, out int lastZ)
    {
        return BeamGrid.Trace(0, 0, dx, dz, limit,
            (x, z) => Math.Abs(x) < 100 && Math.Abs(z) < 100 && !blocked.Contains((x, z)),
            out lastX, out lastZ);
    }

    static HashSet<(int, int)> ScanCandidates(BeamPose pose)
    {
        var cells = new HashSet<(int, int)>();
        for (int step = 0; step <= pose.ScanMajorSteps; step++)
        {
            pose.GetScanRowBounds(step, out bool xMajor, out int major, out int minMinor, out int maxMinor);
            for (int minor = minMinor; minor <= maxMinor; minor++)
                cells.Add(xMajor ? (major, minor) : (minor, major));
        }
        return cells;
    }

    static bool ScanContainsEveryCoveredCell(BeamPose pose, HashSet<(int, int)> candidates)
    {
        int radius = pose.ScanMajorSteps;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
            if (pose.TryGetLane(dx, dz, out _, out _) && !candidates.Contains((dx, dz))) return false;
        return true;
    }

    static void RunShadowGeometryTests()
    {
        var geometry = new BeamShadowGeometry();
        var pose = new BeamPose(0, 0, 0f, 10f, 4f);
        var multiCellCover = new[]
        {
            new BeamShadowGeometry.CoverCell(0, 2, 41, 0.5f),
            new BeamShadowGeometry.CoverCell(0, 3, 41, 0.5f)
        };
        Check(geometry.Transmission(pose, multiCellCover, 0f, 2.8f) == 1f,
            "destination inside one multi-cell blocker keeps full transmission");
        Check(Math.Abs(geometry.Transmission(pose, multiCellCover, 0f, 3.5f) - 0.5f) < 0.0001f,
            "one multi-cell blocker attenuates once after its farthest occupied cell");

        var diagonalPose = new BeamPose(0, 0, 45f, 10f, 4f);
        var tangentCover = new[] { new BeamShadowGeometry.CoverCell(0, 1, 52, 0f) };
        Check(geometry.Transmission(diagonalPose, tangentCover, 0f, 5f) == 1f,
            "a ray tangent to a cover-cell corner does not attenuate");

        Check(BeamShadowGeometry.PreferCover(0.7f, 4, 0.7f, 9) &&
              !BeamShadowGeometry.PreferCover(0.7f, 9, 0.7f, 4) &&
              !BeamShadowGeometry.PreferCover(0f, 1, 0f, 9),
            "same-cell strongest-cover ties use the lower thing ID and ignore zero cover");

        var mixedAlignedCover = new[]
        {
            new BeamShadowGeometry.CoverCell(0, 2, 61, 0.6f),
            new BeamShadowGeometry.CoverCell(0, 4, 72, 0.4f)
        };
        Check(Math.Abs(geometry.Transmission(pose, mixedAlignedCover, 0f, 5f) - 0.24f) < 0.0001f,
            "distinct aligned objects multiply their transmissions once each");

        var lowOpacityItem = new[] { new BeamShadowGeometry.CoverCell(0, 2, 83, 0.9f) };
        Check(Math.Abs(geometry.Transmission(pose, lowOpacityItem, 0f, 3f) - 0.9f) < 0.0001f,
            "a low-opacity aligned item causes proportionate attenuation");

        var widePose = new BeamPose(0, 0, 0f, 60f, 6f);
        var lateralEdgeItem = new[] { new BeamShadowGeometry.CoverCell(2, 41, 94, 0.25f) };
        Check(Math.Abs(geometry.Transmission(widePose, lateralEdgeItem, 1f, 60f) - 0.25f) < 0.0001f &&
              geometry.Transmission(widePose, lateralEdgeItem, 0f, 60f) == 1f,
            "an item at the beam's lateral edge affects only the edge lane");

        Check(BeamShadowGeometry.ApplyTransmission(255, 1f) == 255 &&
              BeamShadowGeometry.ApplyTransmission(255, 0.65f) == 23 &&
              BeamShadowGeometry.ApplyTransmission(255, 0.4225f) == 15,
            "saturated RGB is clipped before blocker transmission and rounded without byte overflow");

        Check(Math.Abs(BeamShadowGeometry.PawnOpacity(0f, 2f) - 0.7f) < 0.0001f &&
              Math.Abs(BeamShadowGeometry.PawnOpacity(0f, 0.5f) - 0.175f) < 0.0001f &&
              Math.Abs(BeamShadowGeometry.PawnOpacity(0f, 1f) - 0.35f) < 0.0001f &&
              BeamShadowGeometry.PawnOpacity(0f, 0f) == 0f &&
              BeamShadowGeometry.PawnOpacity(0f, -1f) == 0f &&
              Math.Abs(BeamShadowGeometry.PawnOpacity(0f, 4f) - 0.9f) < 0.0001f &&
              Math.Abs(BeamShadowGeometry.PawnOpacity(0.4f, 2f) - 0.4f) < 0.0001f,
            "pawn BodySize fallback clamps at zero, scales small values, caps at 0.9, and yields to explicit cover");
    }

    static bool BlockerBoundsContainEverySampleRay(BeamPose pose, HashSet<(int, int)> candidates)
    {
        foreach ((int x, int z) in candidates)
        {
            if (!pose.TryGetLane(x, z, out float lane, out _)) continue;
            if (!pose.GetSampleRay(x, z, lane, out float rayX, out float rayZ,
                out float rayLength, out float offsetX, out float offsetZ)) return false;
            bool allTouchedCellsAreRelevant = true;
            BeamGrid.Trace(0, 0, rayX, rayZ, rayLength, (cellX, cellZ) =>
            {
                if ((cellX != 0 || cellZ != 0) && !pose.IsPotentialBlockerCell(cellX, cellZ))
                    allTouchedCellsAreRelevant = false;
                return true;
            }, out _, out _, offsetX, offsetZ);
            if (!allTouchedCellsAreRelevant) return false;
        }
        return true;
    }

    static void Main()
    {
        int x, z;
        var clear = new HashSet<(int, int)>();
        Check(Math.Abs(Trace(0f, 1f, 5f, clear, out x, out z) - 5f) < 0.001f && x == 0 && z == 5,
            "north range and last traversed cell");
        Check(Math.Abs(Trace(1f, 0f, 5f, clear, out x, out z) - 5f) < 0.001f && x == 5 && z == 0,
            "east range and last traversed cell");
        var wall = new HashSet<(int, int)> { (0, 2) };
        Check(Math.Abs(Trace(0f, 1f, 5f, wall, out x, out z) - 1.5f) < 0.001f && z == 1,
            "wall clips at near face");
        float diagonal = (float)(1.0 / Math.Sqrt(2));
        var cornerWall = new HashSet<(int, int)> { (1, 0) };
        Check(Math.Abs(Trace(diagonal, diagonal, 5f, cornerWall, out x, out z) - (float)Math.Sqrt(0.5)) < 0.001f,
            "supercover blocks corner grazing");
        Check(Math.Abs(Trace(diagonal, diagonal, 5f, clear, out x, out z) - 5f) < 0.001f && x == 4 && z == 4,
            "clear diagonal traverses corners");
        float clipped = BeamGrid.Trace(0, 0, 1f, 0f, 5f, (cx, cz) => cx < 1,
            out x, out z, 0.27f, -0.1f);
        Check(Math.Abs(clipped - 0.23f) < 0.0001f && x == 0, "off-center aperture ray stops at the wall face");
        const float aimRange = 60f;
        const float capDepth = 2f;
        float visualLimit = BeamGrid.VisualRayLimit(aimRange, capDepth, 0.5f);
        float quarterLimit = BeamGrid.VisualRayLimit(aimRange, capDepth, 0.25f);
        Check(visualLimit == 62f && quarterLimit > aimRange && quarterLimit < visualLimit &&
              quarterLimit == BeamGrid.VisualRayLimit(aimRange, capDepth, 0.75f) &&
              BeamGrid.VisualRayLimit(aimRange, capDepth, 0f) == aimRange &&
              BeamGrid.VisualRayLimit(aimRange, capDepth, 1f) == aimRange,
            "rounded cap extends furthest at center and tapers symmetrically to the sides");
        float midpointV = BeamGrid.GradientV(aimRange, visualLimit, 30f);
        float focusV = BeamGrid.GradientV(aimRange, visualLimit, aimRange);
        float capMidpointV = BeamGrid.GradientV(aimRange, visualLimit, 61f);
        float endV = BeamGrid.GradientV(aimRange, visualLimit, visualLimit);
        const float narrowDiameter = 2f;
        const float wideDiameter = 6f;
        float narrowMid = BeamGrid.GradientAlpha(0.5f, midpointV, narrowDiameter);
        float wideMid = BeamGrid.GradientAlpha(0.5f, midpointV, wideDiameter);
        float narrowFocus = BeamGrid.GradientAlpha(0.5f, focusV, narrowDiameter);
        float wideFocus = BeamGrid.GradientAlpha(0.5f, focusV, wideDiameter);
        Check(narrowMid > wideMid && narrowFocus > wideFocus && wideMid > wideFocus && wideFocus == 0.25f,
            "a wider beam fades while keeping a visible center at maximum range");
        float localWidthFocus = BeamGrid.GradientAlpha(0.5f, focusV, wideDiameter * 0.5f);
        Check(Math.Abs(wideMid / 0.75f - localWidthFocus / 0.5f) < 0.001f,
            "equal local diameters receive the same spread attenuation");
        float shortFocusV = BeamGrid.GradientV(30f, 33f, 30f);
        Check(BeamGrid.GradientAlpha(0.5f, shortFocusV, wideDiameter) == wideFocus,
            "equal aim diameters have the same focus brightness at different ranges");
        Check(BeamGrid.GradientAlpha(0.5f, 0f, wideDiameter) > 0f &&
              BeamGrid.GradientAlpha(0.5f, focusV * 0.2f, narrowDiameter) <= 0.9f,
            "the overlay is visible at the lens and does not increase its peak brightness");
        Check(BeamGrid.GradientAlpha(0f, midpointV, wideDiameter) == 0f &&
              BeamGrid.GradientAlpha(1f, midpointV, wideDiameter) == 0f &&
              BeamGrid.GradientAlpha(0.25f, midpointV, wideDiameter) < wideMid,
            "gradient fades lateral edges");
        Check(BeamGrid.GradientAlpha(0.5f, capMidpointV, wideDiameter) == wideFocus * 0.5f &&
              BeamGrid.GradientAlpha(0.5f, endV, wideDiameter) == 0f,
            "cap fades to transparent after the aim point");
        float[] ranges = { 5f, 10f, 30f, 60f };
        float[] angles = { 24f, 22.620f, 7.628f, 3.818f };
        for (int i = 0; i < ranges.Length; i++)
        {
            float angle = BeamGrid.HalfAngle(ranges[i], 4f) * 2f;
            Check(Math.Abs(angle - angles[i]) < 0.01f, "default zoom at " + ranges[i]);
        }
        float slope = (float)Math.Tan(BeamGrid.HalfAngle(60f, 4f) * Math.PI / 180f);
        Check(Math.Abs(2f * 30f * slope - 2f) < 0.001f, "wall receives the existing focus at 30 tiles");
        var door = new HashSet<(int, int)> { (0, 59) };
        Trace(0f, 1f, 60f, door, out x, out z);
        Check(z == 58, "closed door blocks center line");
        door.Clear();
        Trace(0f, 1f, 60f, door, out x, out z);
        Check(z == 60, "opening door restores range, including tile 60");
        var capWall = new HashSet<(int, int)> { (0, 61) };
        float clippedCap = Trace(0f, 1f, visualLimit, capWall, out x, out z);
        Check(clippedCap == 60.5f && z == 60 &&
              BeamGrid.GradientV(aimRange, visualLimit, clippedCap) > focusV &&
              BeamGrid.GradientV(aimRange, visualLimit, clippedCap) < endV,
            "wall just beyond aim clips the visual cap at its near face");
        Trace(0f, 1f, visualLimit, clear, out x, out z);
        Check(z == 62, "only the visual ray extends beyond tile 60");
        Check(BeamGrid.HalfAngle(30f, 2f) < BeamGrid.HalfAngle(30f, 6f),
            "diameter changes footprint");
        var clip30 = new HashSet<(int, int)> { (0, 30) };
        Check(Math.Abs(BeamGrid.SpotRadius(60, 4, 60) - 2f) < 0.001f &&
              Math.Abs(BeamGrid.SpotRadius(60, 4, 30) - 1f) < 0.001f &&
              Math.Abs(BeamGrid.SpotRadius(60, 6, 60) - 3f) < 0.001f &&
              BeamGrid.SpotRadius(60, 2, 1) == 0.5f, "endpoint radius follows width and obstruction distance");
        Check(Math.Abs(Trace(0f, 1f, 60f, clip30, out x, out z) - 29.5f) < 0.001f,
            "range 60 beam clips at wall 30");

        var pose = new BeamPose(0, 0, 0f, 60f, 4f);
        Check(pose.TryGetSampleRay(0, 60, out float centerLane, out float centerDepth,
                  out float centerRayX, out float centerRayZ, out float centerRayLength,
                  out float centerOffsetX, out float centerOffsetZ) &&
              Math.Abs(centerLane) < 0.0001f && Math.Abs(centerDepth - pose.BodyLength) < 0.001f &&
              Math.Abs(centerRayLength - pose.BodyLength) < 0.001f && centerRayX == 0f && centerRayZ == 1f,
            "finite lens samples the central ray to the 60-cell focus");
        Check(!pose.TryGetSampleRay(0, 61, out _, out _, out _, out _, out _, out _, out _) &&
              !pose.TryGetSampleRay(1, 60, out _, out _, out _, out _, out _, out _, out _),
            "ground coverage stops at the radial aim range");
        Check(pose.TryGetSampleRay(1, 30, out float midLane, out _, out _, out _, out _, out _, out _) &&
              Math.Abs(midLane) < 1f &&
              !pose.TryGetSampleRay(2, 30, out _, out _, out _, out _, out _, out _, out _),
            "finite aperture contains the near edge and excludes cells outside its lane");
        Check(Math.Abs(pose.WidthAtDepth(pose.BodyLength) - 4f) < 0.001f,
            "default focus width remains four cells");

        var northBlocker = new HashSet<(int, int)> { (0, 2) };
        float lensWallDistance = BeamGrid.Trace(0, 0, centerRayX, centerRayZ, centerRayLength,
            (cx, cz) => !northBlocker.Contains((cx, cz)), out x, out z,
            centerOffsetX, centerOffsetZ);
        Check(Math.Abs(lensWallDistance - (1.5f - BeamPose.LensForward)) < 0.001f && z == 1,
            "cell-center illumination stops at a blocker face from the lens offset");
        northBlocker.Clear();
        float clearLensDistance = BeamGrid.Trace(0, 0, centerRayX, centerRayZ, centerRayLength,
            (cx, cz) => true, out x, out z, centerOffsetX, centerOffsetZ);
        Check(Math.Abs(clearLensDistance - centerRayLength) < 0.001f && z == 60,
            "an open door or clear lane transmits through the target center");

        var diagonalPose = new BeamPose(0, 0, 45f, 60f, 4f);
        Check(diagonalPose.TryGetSampleRay(2, 2, out float diagonalLane, out _,
                  out float diagonalRayX, out float diagonalRayZ, out float diagonalRayLength,
                  out float diagonalOffsetX, out float diagonalOffsetZ) && Math.Abs(diagonalLane) < 0.0001f,
            "fractional heading uses the same central lane geometry");
        var grazingWall = new HashSet<(int, int)> { (1, 0) };
        float diagonalWallDistance = BeamGrid.Trace(0, 0, diagonalRayX, diagonalRayZ, diagonalRayLength,
            (cx, cz) => !grazingWall.Contains((cx, cz)), out x, out z,
            diagonalOffsetX, diagonalOffsetZ);
        Check(diagonalWallDistance < diagonalRayLength && z == 0,
            "supercover sampling blocks diagonal corner grazing from the finite lens");
        float mapEdgeDistance = BeamGrid.Trace(0, 0, -1f, 0f, 5f,
            (cx, cz) => cx >= 0 && cz >= 0, out x, out z, -BeamPose.LensForward, 0f);
        Check(Math.Abs(mapEdgeDistance - (0.5f - BeamPose.LensForward)) < 0.001f && x == 0,
            "lens-offset tracing clips at the map edge");

        var narrowPose = new BeamPose(0, 0, 0f, 60f, 2f);
        var widePose = new BeamPose(0, 0, 0f, 60f, 6f);
        byte focusCenter = BeamLightField.IntensityAt(pose, 0f, pose.BodyLength);
        byte narrowCenter = BeamLightField.IntensityAt(narrowPose, 0f, narrowPose.BodyLength);
        byte wideCenter = BeamLightField.IntensityAt(widePose, 0f, widePose.BodyLength);
        byte edge = BeamLightField.IntensityAt(pose, 1f, pose.BodyLength);
        Check(focusCenter == 36 && narrowCenter >= focusCenter && focusCenter >= wideCenter,
            "default focus is calibrated to raw RGB 36 and narrower beams are no darker");
        Check(edge > 0 && edge < focusCenter,
            "the physical beam edge retains nonzero light intensity");

        float[] scanHeadings = { 0f, 22.5f, 45f, 67.5f, 90f, 112.5f, 135f, 157.5f,
            180f, 202.5f, 225f, 247.5f, 270f, 292.5f, 315f, 337.5f };
        float[] scanRanges = { 2f, 5f, 10f, 30f, 60f };
        float[] scanDiameters = { 2f, 4f, 6f };
        for (int h = 0; h < scanHeadings.Length; h++)
        for (int r = 0; r < scanRanges.Length; r++)
        for (int d = 0; d < scanDiameters.Length; d++)
        {
            var scanPose = new BeamPose(0, 0, scanHeadings[h], scanRanges[r], scanDiameters[d]);
            HashSet<(int, int)> candidates = ScanCandidates(scanPose);
            Check(ScanContainsEveryCoveredCell(scanPose, candidates),
                "dominant-axis scan bounds cover exhaustive footprint at heading " + scanHeadings[h] +
                ", range " + scanRanges[r] + ", diameter " + scanDiameters[d]);
        }
        var diagonalScanPose = new BeamPose(0, 0, 45f, 60f, 6f);
        int diagonalScanCount = ScanCandidates(diagonalScanPose).Count;
        int diagonalSquareCount = (2 * diagonalScanPose.ScanMajorSteps + 1) *
            (2 * diagonalScanPose.ScanMajorSteps + 1);
        Check(diagonalScanCount < diagonalSquareCount / 4,
            "dominant-axis scan avoids the large diagonal square bounding box");
        float[] blockerHeadings = { 0f, 22.5f, 45f, 67.5f, 90f, 135f, 225f, 315f };
        float[] blockerRanges = { 5f, 30f, 60f };
        for (int h = 0; h < blockerHeadings.Length; h++)
        for (int r = 0; r < blockerRanges.Length; r++)
        for (int d = 0; d < scanDiameters.Length; d++)
        {
            var blockerPose = new BeamPose(0, 0, blockerHeadings[h], blockerRanges[r], scanDiameters[d]);
            HashSet<(int, int)> candidates = ScanCandidates(blockerPose);
            Check(BlockerBoundsContainEverySampleRay(blockerPose, candidates),
                "renderer clipping bound contains every sampled ray at heading " + blockerHeadings[h] +
                ", range " + blockerRanges[r] + ", diameter " + scanDiameters[d]);
        }

        RunShadowGeometryTests();

        if (failures != 0) Environment.Exit(1);
        Console.WriteLine("Geometry checks passed.");
    }
}
