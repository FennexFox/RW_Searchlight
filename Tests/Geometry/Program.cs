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
        Check(BeamGrid.GradientAlpha(0.5f, 0f) == 0f && BeamGrid.GradientAlpha(0.5f, 1f) >= 0.49f,
            "gradient fades source but keeps range edge visible");
        Check(BeamGrid.GradientAlpha(0f, 0.5f) == 0f && BeamGrid.GradientAlpha(1f, 0.5f) == 0f &&
            BeamGrid.GradientAlpha(0.5f, 0.5f) > 0.74f, "gradient fades lateral edges");
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
        Check(BeamGrid.HalfAngle(30f, 2f) < BeamGrid.HalfAngle(30f, 6f),
            "diameter changes footprint");
        var clip30 = new HashSet<(int, int)> { (0, 30) };
        Check(Math.Abs(BeamGrid.SpotRadius(60, 4, 60) - 2f) < 0.001f &&
              Math.Abs(BeamGrid.SpotRadius(60, 4, 30) - 1f) < 0.001f &&
              Math.Abs(BeamGrid.SpotRadius(60, 6, 60) - 3f) < 0.001f &&
              BeamGrid.SpotRadius(60, 2, 1) == 0.5f, "endpoint radius follows width and obstruction distance");
        Check(Math.Abs(Trace(0f, 1f, 60f, clip30, out x, out z) - 29.5f) < 0.001f,
            "range 60 beam clips at wall 30");
        if (failures != 0) Environment.Exit(1);
        Console.WriteLine("Geometry checks passed.");
    }
}
