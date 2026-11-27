using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightVisualHarness
{
    internal sealed class PreciseVisualFixture
    {
        private static readonly string[] Names = { "18-tiles", "60-tiles", "60-tiles-tip", "partial-wall", "full-wall", "empty-lane", "pawn-shadow" };
        private static readonly int[] CameraCenters = { 9, 30, 57, 29, 24, 38, 38 };
        private static readonly float[] CameraSizes = { 10f, 27f, 8f, 15f, 16f, 12f, 12f };
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FieldInfo beamField = typeof(Building_Searchlight).GetField("beam", InstanceFields);
        private readonly FieldInfo meshField = typeof(BeamField).GetField("mesh", InstanceFields);
        private readonly MethodInfo sampleLight = typeof(BeamField).GetMethod("SampleLight", InstanceFields);
        private readonly Dictionary<int, byte> samples = new();
        private readonly Map map;
        private readonly Building_Searchlight lamp;
        private readonly IntVec3 origin;
        private readonly string output;
        private readonly Action<string> record;
        private readonly Action<string> finish;
        private readonly VisualLightMask mask;
        private readonly List<Thing> fixtureWalls = new();
        private int scene;
        private int readyTick;
        private int captureFrame;
        private bool masked;
        private string screenshot;

        private static int Tick => Find.TickManager.TicksGame;
        private int Range => scene == 0 ? 18 : 60;

        internal PreciseVisualFixture(Map map, Building_Searchlight lamp, IntVec3 origin, string output,
            Action<string> record, Action<string> finish)
        {
            this.map = map;
            this.lamp = lamp;
            this.origin = origin;
            this.output = output;
            this.record = record;
            this.finish = finish;
            mask = map.GetComponent<VisualLightMask>();
            Check(beamField != null && meshField != null && sampleLight != null && mask != null,
                "visual fixture found the beam mesh and its authoritative cell sampler");
            Check(Math.Abs(SearchlightSettings.Diameter - 4f) < 0.01f, "visual fixture runs with the default four-cell diameter");
            var mode = Current.Game.GetComponent<SearchlightLightingGame>();
            mode.SetMode(SearchlightLightingMode.PreciseField);
            Check(mode.CurrentMode == SearchlightLightingMode.PreciseField, "precise mode enabled");
            record("lighting=precise; Harmony=" + ModLister.GetModWithIdentifier("brrainz.harmony")?.ModVersion);
            readyTick = Tick + 55;
        }

        internal void Update()
        {
            if (screenshot != null)
            {
                if (Time.frameCount == captureFrame)
                {
                    if (!masked) VerifyScene();
                    ScreenCapture.CaptureScreenshot(screenshot);
                }
                if (File.Exists(screenshot) && new FileInfo(screenshot).Length > 0)
                    Advance();
                return;
            }

            if (Tick < readyTick || GenLocalDate.HourOfDay(map) != 1 || map.skyManager.CurSkyGlow > 0.15f ||
                !lamp.IsOperating || Math.Abs(lamp.AimRange - Range) > 0.01f ||
                Math.Abs(Mathf.DeltaAngle(lamp.AimHeading, 90f)) > 0.01f)
                return;

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.CameraDriver.SetRootPosAndSize(East(CameraCenters[scene]).ToVector3Shifted(), CameraSizes[scene]);
            Capture();
        }

        private void Capture()
        {
            screenshot = Path.Combine(output, Names[scene] + (masked ? "-mask.png" : ".png"));
            captureFrame = Time.frameCount + 8;
        }

        private void Advance()
        {
            if (!masked)
            {
                masked = true;
                mask.Samples = samples;
                Capture();
                return;
            }

            mask.Samples = null;
            masked = false;
            screenshot = null;
            scene++;
            if (scene == Names.Length)
            {
                finish("PASS");
                return;
            }

            if (scene == 1) lamp.SetAimCell(East(60));
            if (scene == 3) PlaceWall(1, 3);
            if (scene == 4) PlaceWall(-3, 0);
            if (scene == 5)
            {
                foreach (Thing wall in fixtureWalls) wall.Destroy();
                SpawnAdult(40);
            }
            if (scene == 6) SpawnAdult(36);
            readyTick = Tick + 25;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        private void SpawnAdult(int distance)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            pawn.ageTracker.AgeBiologicalTicks = 30L * GenDate.TicksPerYear;
            GenSpawn.Spawn(pawn, East(distance), map);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
            record($"probe pawn={pawn.ThingID}; cell={pawn.Position}; bodySize={pawn.BodySize}");
        }

        private void PlaceWall(int firstZ, int lastZ)
        {
            for (int z = firstZ; z <= lastZ; z++)
                fixtureWalls.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), East(30, z), map));
        }

        private void VerifyScene()
        {
            Check(!lamp.SpotCell.IsValid && !map.listerThings.AllThings.Any(thing => thing.def.defName == "DS_LightSpot"),
                "precise capture has no legacy endpoint glower");
            var beam = (BeamField)beamField.GetValue(lamp);
            sampleLight.Invoke(beam, new object[] { samples });
            var mesh = (Mesh)meshField.GetValue(beam);
            Check(mesh != null, "beam mesh uploaded before visual readback");
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            Check(vertices.Length > 0 && triangles.Length > 0, "uploaded beam geometry contains triangles");

            for (int x = 1; x <= Range + 3; x++)
            for (int z = -5; z <= 5; z++)
            {
                IntVec3 cell = East(x, z);
                byte expected = Intensity(cell);
                var raw = map.glowGrid.VisualGlowAt(cell);
                Check(raw.r == expected && raw.g == expected && raw.b == expected,
                    $"{Names[scene]} cell {cell} mask={expected}, live={raw}");
                if (x * x + z * z > Range * Range)
                    Check(expected == 0, $"cell {cell} beyond physical range is dark");
            }

            float meshEnd = vertices.Max(vertex => vertex.x) - (origin.x + 0.5f);
            var physicalCap = East(Range + 1);
            if (scene <= 4) Check(samples.Count == 0, "unoccupied vanilla beam has no gameplay light");
            if (scene <= 3)
            {
                Check(Covers(vertices, triangles, physicalCap) && meshEnd > Range + 1.5f,
                    "uploaded triangles reach beyond the physical focus into the rounded cap");
            }

            if (scene == 3)
            {
                Check(!Covers(vertices, triangles, East(45, 1)) && Covers(vertices, triangles, East(45, -1)),
                    "uploaded beam silhouette follows the partial obstruction");
            }
            if (scene == 4)
            {
                Check(meshEnd <= 29.51f && Covers(vertices, triangles, East(29)) && !Covers(vertices, triangles, East(30)),
                    "uploaded beam ends at the fully blocked wall's near face");
            }

            if (scene >= 5)
            {
                float own = map.glowGrid.GroundGlowAt(East(36), ignoreSky: true);
                float behind = map.glowGrid.GroundGlowAt(East(40), ignoreSky: true);
                float expectedBehind = scene == 6 ? 0.325f : 0.50f;
                Check(samples.Count == scene - 4 && Math.Abs(own - (scene == 6 ? 0.50f : 0f)) < 0.015f && Math.Abs(behind - expectedBehind) < 0.015f,
                    $"{Names[scene]} ground glow, caster={own}, shadow={behind}");
            }
            var probes = scene switch
            {
                0 => new[] { East(9), East(18), East(19), East(9, 3) },
                1 or 2 => new[] { East(30), East(59, 1), East(60), East(61) },
                3 => new[] { East(29), East(30, 1), East(45, 1), East(45, -1) },
                >= 5 => new[] { East(35), East(36), East(40), East(40, 1) },
                _ => new[] { East(29), East(30), East(31), East(34) }
            };
            int renderChecks = 0;
            foreach (var cell in probes)
            {
                if (!Find.CameraDriver.CurrentViewRect.Contains(cell)) continue;
                byte expectedMesh = ExpectedOverlayCenter(cell);
                Color32 actualMesh = ReadOverlayCenter(cell);
                Check(actualMesh.r == expectedMesh && actualMesh.g == expectedMesh && actualMesh.b == expectedMesh,
                    $"{Names[scene]} ground-overlay mesh at {cell} expected={expectedMesh}, uploaded={actualMesh}");
                float glow = map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
                record($"scene={Names[scene]}; cell={cell}; intensity={Intensity(cell)}; artificialGround={glow:F4}; overlayCenter={actualMesh}");
                renderChecks++;
            }
            Check(renderChecks >= 2, "camera includes multiple checked ground-overlay mesh samples");
            record($"scene={Names[scene]}; sampledCells={samples.Count}; uploadedBeamEnd={meshEnd:F3}; sky={map.skyManager.CurSkyGlow:F4}; pairedMask={Names[scene]}-mask.png");
        }

        private byte ExpectedOverlayCenter(IntVec3 cell)
        {
            // The live vanilla overlay averages eligible raw RGB at each shared
            // corner, then averages those four corner colors for its center.
            int total = 0;
            for (int z = 0; z <= 1; z++)
            for (int x = 0; x <= 1; x++)
            {
                int sum = 0;
                int count = 0;
                for (int nz = -1; nz <= 0; nz++)
                for (int nx = -1; nx <= 0; nx++)
                {
                    var neighbor = cell + new IntVec3(x + nx, 0, z + nz);
                    if (!neighbor.InBounds(map) || neighbor.GetEdifice(map)?.def.blockLight == true) continue;
                    sum += Intensity(neighbor);
                    count++;
                }
                if (count != 0) total += sum / count;
            }
            return (byte)(total / 4);
        }

        private Color32 ReadOverlayCenter(IntVec3 cell)
        {
            var section = map.mapDrawer.SectionAt(cell);
            var layer = section.GetLayer(typeof(SectionLayer_LightingOverlay));
            Check(layer != null && layer.Visible, "vanilla ground lighting layer is visible");
            var subMesh = layer.subMeshes.SingleOrDefault(candidate => candidate.material == MatBases.LightOverlay);
            Check(subMesh != null && subMesh.finalized && !subMesh.disabled, "vanilla ground overlay submesh is drawable");
            var colors = subMesh.mesh.colors32;
            var rect = section.CellRect;
            int cornerCount = (rect.Width + 1) * (rect.Height + 1);
            Check(colors.Length == cornerCount + rect.Area, "lighting mesh includes the expected corner and center colors");
            return colors[cornerCount + (cell.z - rect.minZ) * rect.Width + cell.x - rect.minX];
        }

        private static bool Covers(Vector3[] vertices, int[] triangles, IntVec3 cell)
        {
            var point = cell.ToVector3Shifted();
            for (int index = 0; index < triangles.Length; index += 3)
            {
                var a = vertices[triangles[index]];
                var b = vertices[triangles[index + 1]];
                var c = vertices[triangles[index + 2]];
                float area = Cross(a, b, c);
                if (Math.Abs(area) < 0.00001f) continue;
                float orientation = Math.Sign(area);
                if (orientation * Cross(a, b, point) >= -0.00001f &&
                    orientation * Cross(b, c, point) >= -0.00001f &&
                    orientation * Cross(c, a, point) >= -0.00001f)
                    return true;
            }
            return false;
        }

        private static float Cross(Vector3 a, Vector3 b, Vector3 point) =>
            (b.x - a.x) * (point.z - a.z) - (b.z - a.z) * (point.x - a.x);

        private byte Intensity(IntVec3 cell) =>
            samples.TryGetValue(map.cellIndices.CellToIndex(cell), out byte value) ? value : (byte)0;

        private IntVec3 East(int x, int z = 0) => origin + new IntVec3(x, 0, z);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    public sealed class VisualLightMask : MapComponent
    {
        private readonly List<IntVec3> cells = new();
        internal Dictionary<int, byte> Samples { private get; set; }

        public VisualLightMask(Map map) : base(map) { }

        public override void MapComponentDraw()
        {
            if (Samples == null || Find.CurrentMap != map) return;
            cells.Clear();
            foreach (var sample in Samples)
            {
                var cell = map.cellIndices.IndexToCell(sample.Key);
                cells.Add(cell);
                CellRenderer.RenderSpot(cell, 0.27f + 0.002f * sample.Value, 0.18f);
            }
            GenDraw.DrawFieldEdges(cells, new Color(0.12f, 0.90f, 0.78f, 0.85f));
        }
    }
}
