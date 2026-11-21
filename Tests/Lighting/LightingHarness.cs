using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightLightingHarness
{
    public sealed class LightingCheck : GameComponent
    {
        private const byte FullIntensity = 64;
        private const byte LowIntensity = 20;
        private const string SaveName = "searchlight-lighting";
        private readonly string output;
        private readonly string runPhase;
        private readonly bool requireCe;

        private IntVec3 probe;
        private Pawn shooter;
        private Pawn target;
        private Thing torch;
        private string darkMesh;
        private string boundaryMesh;
        private string farMesh;
        private string torchMesh;
        private Color32 torchColor;
        private float torchGlow;
        private float darkCombatGlow;
        private float darkLightingShift;
        private bool fixtureSaved;
        private int stage;
        private int readyFrame;
        private int readyTick;

        private MethodInfo setCell;
        private MethodInfo clearMap;
        private MethodInfo combatGlowAt;
        private MethodInfo getLightingShift;
        private object ceTracker;

        private IntVec3 Neighbor => probe + IntVec3.East;
        private IntVec3 Far => probe + new IntVec3(3, 0, 0);
        private IntVec3 TorchCell => probe + new IntVec3(0, 0, -20);
        private IntVec3 Overlap => TorchCell + new IntVec3(0, 0, 8);

        public LightingCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-lighting-output", out output);
            GenCommandLine.TryGetCommandLineArg("ds-lighting-phase", out runPhase);
            requireCe = GenCommandLine.CommandLineArgPassed("ds-lighting-require-ce");
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref fixtureSaved, "fixtureSaved");
            Scribe_Values.Look(ref probe, "probe");
            Scribe_Values.Look(ref darkMesh, "darkMesh");
            Scribe_Values.Look(ref torchMesh, "torchMesh");
            Scribe_Values.Look(ref torchGlow, "torchGlow");
            Scribe_References.Look(ref torch, "torch");
            Scribe_References.Look(ref shooter, "shooter");
            Scribe_References.Look(ref target, "target");
        }

        public override void GameComponentUpdate()
        {
            var map = Find.CurrentMap;
            if (string.IsNullOrEmpty(output) || stage == -1 || map == null ||
                Current.ProgramState != ProgramState.Playing || Time.frameCount < readyFrame ||
                Find.TickManager.TicksGame < readyTick)
                return;

            try
            {
                if (stage == 0)
                    Setup(map);
                else if (runPhase == "reload")
                    VerifyReload(map);
                else
                    VerifyFresh(map);
            }
            catch (Exception ex)
            {
                Finish("FAIL " + ex);
            }
        }

        private void Setup(Map map)
        {
            var assembly = typeof(Building_Searchlight).Assembly;
            Record($"game={VersionControl.CurrentVersionString}; phase={runPhase}; assemblyMvid={assembly.ManifestModule.ModuleVersionId:D}");
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var harmony = loaded.FirstOrDefault(a => a.GetName().Name == "0Harmony");
            Check(harmony != null, "Harmony assembly is loaded");
            Record($"Harmony={harmony.FullName}; modVersion={ModLister.GetModWithIdentifier("brrainz.harmony")?.ModVersion}");
            var ce = loaded.FirstOrDefault(a => a.GetName().Name == "CombatExtended");
            Check((ce != null) == requireCe, "loaded CE matches requested case");
            if (ce != null)
            {
                Record($"CE={ce.FullName}; modVersion={ModLister.GetModWithIdentifier("ceteam.combatextended")?.ModVersion}");
                var trackerType = ce.GetType("CombatExtended.LightingTracker", throwOnError: true);
                ceTracker = map.GetComponent(trackerType);
                combatGlowAt = trackerType.GetMethod("CombatGlowAtFor");
                getLightingShift = ce.GetType("CombatExtended.CE_Utility", throwOnError: true).GetMethod("GetLightingShift");
                Check(ceTracker != null && combatGlowAt != null && getLightingShift != null, "CE lighting queries are present");
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var adapter = assembly.GetType("DirectedSearchlights.LightingIntegration", throwOnError: true);
            bool available = (bool)adapter.GetProperty("Available", flags).GetValue(null);
            var status = adapter.GetProperty("Status", flags).GetValue(null);
            Record($"adapterAvailable={available}; adapterStatus={status}");
            Check(available, "lighting adapter is available");
            setCell = adapter.GetMethod("SetCell", flags, null, new[] { typeof(Map), typeof(int), typeof(byte) }, null);
            clearMap = adapter.GetMethod("ClearMap", flags, null, new[] { typeof(Map) }, null);
            Check(setCell != null && clearMap != null, "lighting adapter test entry points exist");

            Check(fixtureSaved == (runPhase == "reload"), "runner opened the expected new game or saved fixture");
            if (!fixtureSaved)
            {
                // Place the probe on a section boundary, where the light overlay shares corners.
                probe = new IntVec3(map.Center.x / 17 * 17 - 1, 0, map.Center.z);
                foreach (var cell in new CellRect(probe.x - 18, probe.z - 32, 34, 47))
                {
                    map.roofGrid.SetRoof(cell, null);
                    foreach (var thing in cell.GetThingList(map).ToArray())
                        if (thing is Plant || thing.def.passability == Traversability.Impassable ||
                            thing.TryGetComp<CompGlower>() != null)
                            thing.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                }
                if (requireCe)
                {
                    shooter = SpawnPawn(map, probe + new IntVec3(-12, 0, 0));
                    target = SpawnPawn(map, probe);
                }
                map.weatherManager.TransitionTo(WeatherDefOf.Clear);
                map.weatherManager.curWeatherAge = (int)WeatherManager.TransitionTicks;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SetHour(map, 1);
            Find.CameraDriver.SetRootPosAndSize((probe + new IntVec3(0, 0, -8)).ToVector3Shifted(), 20f);
            Check(map.mapDrawer.SectionAt(probe) != map.mapDrawer.SectionAt(Neighbor), "probe and adjacent cell span sections");
            Record($"probe={probe}; adjacent={Neighbor}; far={Far}; torch={TorchCell}; overlap={Overlap}");
            Advance();
        }

        private void VerifyFresh(Map map)
        {
            switch (stage)
            {
                case 1:
                    Check(map.skyManager.CurSkyGlow < 0.02f, "night fixture reached low sky glow");
                    Measure(map, "dark", probe);
                    CheckDarkCell(map, probe);
                    CheckDarkCell(map, Neighbor);
                    CheckDarkCell(map, Far);
                    darkMesh = Mesh(map, probe);
                    boundaryMesh = Mesh(map, Neighbor);
                    farMesh = Mesh(map, Far);
                    if (requireCe)
                        (darkCombatGlow, darkLightingShift) = MeasureCe(map, "dark");
                    Publish(map, probe, FullIntensity);
                    break;
                case 2:
                    Measure(map, "added", probe);
                    Measure(map, "adjacent", Neighbor);
                    Measure(map, "remote", Far);
                    CheckLitProbe(map);
                    CheckDarkCell(map, Neighbor);
                    CheckDarkCell(map, Far);
                    Check(Mesh(map, Neighbor) != boundaryMesh, "adjacent section refreshed its interpolated corner colors");
                    Check(Mesh(map, Far) == farMesh, "remote section mesh sample retained its baseline");
                    if (requireCe)
                    {
                        var (combatGlow, lightingShift) = MeasureCe(map, "added");
                        Check(combatGlow > darkCombatGlow && combatGlow <= 0.501f, "CE combat glow rises within its nighttime cap");
                        Check(lightingShift < darkLightingShift, "CE lighting penalty falls for the same shooter and target");
                    }
                    Publish(map, probe, LowIntensity);
                    break;
                case 3:
                    Measure(map, "reduced", probe);
                    Check(Near(Artificial(map, probe), LowIntensity * 3.6f / 255f), "weaker contribution reduces gameplay glow below its cap");
                    Check(Mesh(map, probe) != darkMesh, "weaker contribution still changes overlay colors");
                    Publish(map, probe, 0);
                    break;
                case 4:
                    Measure(map, "removed", probe);
                    CheckDarkCell(map, probe);
                    Check(Mesh(map, probe) == darkMesh && Mesh(map, Neighbor) == boundaryMesh, "removal restored both section mesh samples");
                    if (requireCe)
                    {
                        var (combatGlow, lightingShift) = MeasureCe(map, "removed");
                        Check(Near(combatGlow, darkCombatGlow) && Near(lightingShift, darkLightingShift), "CE returned to its dark baseline");
                    }
                    torch = GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("TorchLamp"), TorchCell, map);
                    torch.TryGetComp<CompRefuelable>().Refuel(100);
                    break;
                case 5:
                    Measure(map, "torch-only", Overlap);
                    torchGlow = Artificial(map, Overlap);
                    torchColor = map.glowGrid.VisualGlowAt(Overlap);
                    torchMesh = Mesh(map, Overlap);
                    Check(torchGlow > 0f && torchColor.r > 0, "vanilla torch contributes to the overlap cell");
                    Publish(map, Overlap, FullIntensity);
                    break;
                case 6:
                    Measure(map, "torch-and-field", Overlap);
                    var merged = map.glowGrid.VisualGlowAt(Overlap);
                    Check(merged.r == Math.Max(torchColor.r, FullIntensity) &&
                        merged.g == Math.Max(torchColor.g, FullIntensity) &&
                        merged.b == Math.Max(torchColor.b, FullIntensity) && merged.a == torchColor.a,
                        "field merges by channel maximum and preserves the torch's alpha");
                    Check(Artificial(map, Overlap) >= torchGlow && Mesh(map, Overlap) != torchMesh,
                        "field and vanilla torch contribute to the live overlay");
                    clearMap.Invoke(null, new object[] { map });
                    SetHour(map, 12);
                    break;
                case 7:
                    Measure(map, "daylight-baseline", probe);
                    CheckDaylight(map);
                    CheckDarkCell(map, probe);
                    CheckTorch(map);
                    Publish(map, probe, FullIntensity);
                    break;
                case 8:
                    Measure(map, "daylight-and-field", probe);
                    CheckDaylight(map);
                    CheckLitProbe(map);
                    SetHour(map, 1);
                    break;
                case 9:
                    Measure(map, "before-save", probe);
                    Check(map.skyManager.CurSkyGlow < 0.02f, "fixture returned to night for the save");
                    CheckLitProbe(map);
                    CheckTorch(map);
                    fixtureSaved = true;
                    GameDataSaveLoader.SaveGame(SaveName);
                    var save = new FileInfo(GenFilePaths.FilePathForSavedGame(SaveName));
                    Check(save.Exists && save.Length > 0, "saved fixture while field contribution was active");
                    Record("save-bytes=" + save.Length);
                    Finish("PASS " + runPhase);
                    return;
            }
            Advance();
        }

        private void VerifyReload(Map map)
        {
            switch (stage)
            {
                case 1:
                    Measure(map, "reload-field-absent", probe);
                    CheckDarkCell(map, probe);
                    Check(Mesh(map, probe) == darkMesh, "transient field mesh was rebuilt from the dark baseline on load");
                    CheckTorch(map);
                    if (requireCe)
                        (darkCombatGlow, darkLightingShift) = MeasureCe(map, "reload-field-absent");
                    Publish(map, probe, FullIntensity);
                    break;
                case 2:
                    Measure(map, "reload-field-republished", probe);
                    CheckLitProbe(map);
                    CheckTorch(map);
                    if (requireCe)
                    {
                        var (combatGlow, lightingShift) = MeasureCe(map, "reload-field-republished");
                        Check(combatGlow > darkCombatGlow && lightingShift < darkLightingShift, "CE uses republished lighting on the loaded map");
                    }
                    clearMap.Invoke(null, new object[] { map });
                    break;
                case 3:
                    Measure(map, "reload-cleared", probe);
                    CheckDarkCell(map, probe);
                    Check(Mesh(map, probe) == darkMesh, "map clearing restored the loaded mesh");
                    CheckTorch(map);
                    Finish("PASS " + runPhase);
                    return;
            }
            Advance();
        }

        private void CheckTorch(Map map)
        {
            Check(torch != null && torch.Spawned && torch.Map == map, "vanilla torch is present");
            Measure(map, "torch-preserved", Overlap);
            Check(Near(Artificial(map, Overlap), torchGlow) && Mesh(map, Overlap) == torchMesh,
                "torch-only gameplay and mesh lighting retained their baseline");
        }

        private void CheckDaylight(Map map)
        {
            float sky = map.skyManager.CurSkyGlow;
            Check(sky > 0.55f && Near(map.glowGrid.GroundGlowAt(probe), sky), "outdoor gameplay glow preserves daylight");
        }

        private void CheckLitProbe(Map map)
        {
            Check(Near(Artificial(map, probe), 0.5f), "single-cell gameplay glow reaches the vanilla non-overlit cap");
            Check(Near(map.glowGrid.GroundGlowAt(probe, ignoreCavePlants: true, ignoreSky: true), 0.5f),
                "cave-plant-excluding gameplay query also sees the field");
            var raw = map.glowGrid.VisualGlowAt(probe);
            Check(raw.r == FullIntensity && raw.g == FullIntensity && raw.b == FullIntensity, "probe retains the exact field RGB");
            Check(Mesh(map, probe) != darkMesh, "live ground overlay mesh shows the field");
        }

        private static void CheckDarkCell(Map map, IntVec3 cell)
        {
            var raw = map.glowGrid.VisualGlowAt(cell);
            Check(Near(Artificial(map, cell), 0f) && raw.r == 0 && raw.g == 0 && raw.b == 0,
                $"cell {cell} has zero artificial gameplay and RGB glow");
        }

        private (float combatGlow, float lightingShift) MeasureCe(Map map, string label)
        {
            Check(shooter != null && target != null && shooter.Map == map && target.Map == map &&
                shooter.Position == probe + new IntVec3(-12, 0, 0) && target.Position == probe,
                "CE query used the same anchored shooter and target");
            float combatGlow = (float)combatGlowAt.Invoke(ceTracker, new object[] { shooter.Position, target.Position });
            float lightingShift = (float)getLightingShift.Invoke(null, new object[] { shooter, combatGlow });
            Record($"{label}; shooter={shooter.thingIDNumber}; target={target.thingIDNumber}; combatGlow={combatGlow:F4}; lightingShift={lightingShift:F4}");
            return (combatGlow, lightingShift);
        }

        private static Pawn SpawnPawn(Map map, IntVec3 cell)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(pawn, cell, map);
            pawn.drafter.Drafted = true;
            var hold = JobMaker.MakeJob(JobDefOf.Wait);
            hold.expiryInterval = 500000;
            pawn.jobs.StartJob(hold, JobCondition.InterruptForced);
            return pawn;
        }

        private static void SetHour(Map map, int hour)
        {
            var ticks = Find.TickManager;
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != hour; i++)
                ticks.DebugSetTicksGame(ticks.TicksGame + 2500);
            Check(GenLocalDate.HourOfDay(map) == hour, "fixture reached the requested local hour");
        }

        private void Publish(Map map, IntVec3 cell, byte intensity) =>
            setCell.Invoke(null, new object[] { map, map.cellIndices.CellToIndex(cell), intensity });

        private static float Artificial(Map map, IntVec3 cell) =>
            map.glowGrid.GroundGlowAt(cell, ignoreSky: true);

        private static string Mesh(Map map, IntVec3 cell)
        {
            // Read back the current rendered overlay's vertices without forcing regeneration.
            var section = map.mapDrawer.SectionAt(cell);
            var layer = (SectionLayer_LightingOverlay)section.GetLayer(typeof(SectionLayer_LightingOverlay));
            Check(layer != null && layer.Visible, "the active map is drawing its vanilla lighting overlay");
            var subMesh = layer.subMeshes.SingleOrDefault(mesh => mesh.material == MatBases.LightOverlay);
            Check(subMesh != null && subMesh.finalized && !subMesh.disabled, "the vanilla overlay submesh is ready to draw");
            var colors = subMesh.mesh.colors32;
            var rect = section.CellRect;
            int cornerStride = rect.Width + 1;
            int cornerCount = cornerStride * (rect.Height + 1);
            Check(colors.Length == cornerCount + rect.Area, "the mesh has colors for each corner and center vertex");
            int x = cell.x - rect.minX;
            int z = cell.z - rect.minZ;
            int bottomLeft = z * cornerStride + x;
            int topLeft = bottomLeft + cornerStride;
            int center = cornerCount + z * rect.Width + x;
            return $"BL={colors[bottomLeft]} TL={colors[topLeft]} TR={colors[topLeft + 1]} BR={colors[bottomLeft + 1]} Center={colors[center]}";
        }

        private void Measure(Map map, string label, IntVec3 cell) =>
            Record($"{label}; tick={Find.TickManager.TicksGame}; cell={cell}; sky={map.skyManager.CurSkyGlow:F4}; ground={Artificial(map, cell):F4}; " +
                $"groundWithSky={map.glowGrid.GroundGlowAt(cell):F4}; rgb={map.glowGrid.VisualGlowAt(cell)}; mesh={Mesh(map, cell)}");

        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private void Advance()
        {
            stage++;
            readyFrame = Time.frameCount + 30;
            // CE 16.7.4 caches ground glow for one TicksAbs value.
            readyTick = Find.TickManager.TicksGame + 12;
        }

        private void Record(string line)
        {
            File.AppendAllText(output, line + Environment.NewLine);
            Log.Message("SearchlightLightingHarness: " + line);
        }

        private void Finish(string result)
        {
            stage = -1;
            Record(result);
            Application.Quit(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }
    }
}
