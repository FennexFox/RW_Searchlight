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

namespace SearchlightLightingHarness
{
    /// <summary>Small live-map fixture for pawn-only precise illumination and cover.</summary>
    public sealed class PawnShadowCheck : GameComponent
    {
        private const int PowerRestartTicks = 220;
        private const float SaturatedIntensity = 0.5f / 3.6f * 255f;
        private readonly Queue<(string name, int ticks, Action run)> steps = new();
        private readonly Dictionary<ThingDef, float> originalFill = new();
        private readonly Dictionary<RaceProperties, float> originalSize = new();
        private readonly string output;
        private readonly bool requireCe;
        private Map map;
        private IntVec3 origin;
        private Building_Searchlight lamp, secondLamp;
        private Building wall;
        private Pawn receiver, blocker, secondBlocker, mover;
        private Thing chunk, torch;
        private ThingDef chunkDef;
        private FieldInfo fillPercent, bodySize;
        private int nextTick, nextFrame, moveStart;
        private Action tickCheck;
        private int tickDeadline;
        private Color32 bare;
        private Color32 ambient;
        private Color32 shadowed;
        private Color32 secondOnly;
        private Color32 torchOnly;
        private bool finished;
        private static int Tick => Find.TickManager.TicksGame;
        private IntVec3 Target => East(40);

        public PawnShadowCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-pawn-shadow-output", out output);
            requireCe = GenCommandLine.CommandLineArgPassed("ds-lighting-require-ce");
        }

        public override void GameComponentUpdate()
        {
            if (string.IsNullOrEmpty(output) || finished || Find.CurrentMap == null || Current.ProgramState != ProgramState.Playing) return;
            try
            {
                if (tickCheck != null || Tick < nextTick || Time.frameCount < nextFrame) return;
                if (map == null) { Setup(Find.CurrentMap); return; }
                if (steps.Count == 0) { Finish("PASS fresh"); return; }
                var step = steps.Dequeue();
                Record($"step={step.name}; tick={Tick}");
                step.run();
                Schedule(step.ticks);
            }
            catch (Exception ex) { Finish("FAIL " + ex); }
        }

        public override void GameComponentTick()
        {
            if (tickCheck == null || finished) return;
            try { Check(Tick <= tickDeadline, "pawn movement completed within its tick window"); tickCheck(); }
            catch (Exception ex) { Finish("FAIL " + ex); }
        }

        private void Setup(Map current)
        {
            map = current;
            Record($"game={VersionControl.CurrentVersionString}; CE={requireCe}; assemblyMvid={typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId:D}");
            var ce = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CombatExtended");
            Check((ce != null) == requireCe, "loaded CE matches the requested fixture");
            var lighting = Current.Game.GetComponent<SearchlightLightingGame>();
            Check(lighting != null, "lighting game component exists");
            lighting.SetMode(SearchlightLightingMode.PreciseField);
            Check(lighting.CurrentMode == SearchlightLightingMode.PreciseField, "precise pawn lighting mode is active");
            fillPercent = typeof(ThingDef).GetField("fillPercent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bodySize = typeof(RaceProperties).GetField("baseBodySize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Check(fillPercent != null && bodySize != null, "fixture found mutable fill and pawn-size fields");

            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToArray()) pawn.DeSpawn();
            origin = map.Center + new IntVec3(-65, 0, 0);
            ClearArea(new CellRect(origin.x - 12, origin.z - 45, 94, 65));
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            map.weatherManager.curWeatherAge = (int)WeatherManager.TransitionTicks;
            SetHour(1);
            lamp = SpawnLamp(origin);
            lamp.SetAimCell(East(60));
            receiver = SpawnPawn(Target);
            Find.CameraDriver.SetRootPosAndSize(East(32).ToVector3Shifted(), 22f);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Fast;

            Queue("baseline", PowerRestartTicks, Baseline);
            Queue("ambient baseline", PowerRestartTicks, CaptureAmbient);
            Queue("restored baseline", PowerRestartTicks, RestoreBaseline);
            Queue("small pawn", 16, AddBlocker);
            Queue("small pawn shadow", 16, CheckSmallShadow);
            Queue("large pawn", 16, SetLargePawn);
            Queue("large pawn cap", 16, CheckLargeShadow);
            Queue("explicit 0.8", 16, () => SetFill(blocker.def, 0.8f));
            Queue("explicit 0.8 check", 16, CheckExplicitShadow);
            Queue("explicit 0.95", 16, () => SetFill(blocker.def, 0.95f));
            Queue("explicit 0.95 check", 16, CheckExplicitShadow);
            Queue("stacked pawns", 16, AddSecondBlocker);
            Queue("stacked pawns check", 16, CheckStackedShadow);
            Queue("coincident pawns", 16, AddCoincidentPawns);
            Queue("coincident pawns check", 16, CheckCoincidentPawns);
            Queue("chunk item", 16, AddChunk);
            Queue("chunk item check", 16, CheckChunkShadow);
            Queue("remove coincident chunk", 16, RemoveChunk);
            Queue("chunk removed check", 16, CheckChunkRemoved);
            Queue("opaque wall", 16, AddWall);
            Queue("opaque wall check", 16, CheckWall);
            Queue("native blocker", 16, SetupNativeMover);
            Queue("native movement", 1, StartNativeMove);
            Queue("mesh ready", 16, CheckAfterMove);
            Queue("second lamp", PowerRestartTicks, AddCrossingLamp);
            Queue("first lamp shadow", PowerRestartTicks, CaptureFirstLampShadow);
            Queue("second lamp only", PowerRestartTicks, CaptureSecondLamp);
            Queue("overlap", PowerRestartTicks, CheckLampOverlap);
            Queue("torch only", PowerRestartTicks, SetTorchBaseline);
            Queue("torch overlap", PowerRestartTicks, CheckTorchOverlap);
            Queue("mode switch", PowerRestartTicks, SwitchMode);
            Queue("mode restore", PowerRestartTicks, RestorePreciseMode);
            Queue("mode restore check", PowerRestartTicks, CheckModeRestore);
            Queue("cleanup", 16, Cleanup);
            Schedule(PowerRestartTicks);
        }

        private void Baseline()
        {
            Check(lamp.IsOperating && Math.Abs(lamp.AimRange - 60f) < 0.05f, "searchlight reached the fixture aim");
            bare = ColorAt(Target);
            Check(IsLit(bare) && Ground(Target) > 0f, "directly reached receiver pawn is lit");
            IntVec3 support = East(40, 1), outside = East(40, 7);
            if (requireCe) Check(IsLit(ColorAt(support)) && IsDark(ColorAt(outside)), "CE lights only the in-beam 3x3 support cells");
            else Check(IsDark(ColorAt(support)) && Ground(support) < 0.001f, "vanilla precise mode leaves empty ground dark");
            Check(IsDark(ColorAt(outside)), "ground outside the directly reached pawn footprint is dark");
            Record($"baseline; target={bare}; support={ColorAt(support)}; outside={ColorAt(outside)}");
            Flick(lamp, false);
        }

        private void CaptureAmbient()
        {
            Check(!lamp.IsOperating, "searchlight is off while recording vanilla ambient RGB");
            ambient = ColorAt(Target);
            Record($"ambient; target={ambient}");
            Flick(lamp, true);
        }

        private void RestoreBaseline()
        {
            Check(lamp.IsOperating, "searchlight restarted after ambient sample");
            Color32 restored = ColorAt(Target);
            Check(IsLit(restored), "restored searchlight lights the receiver");
            bare = restored;
            Record($"baseline-restored; target={bare}; ambient={ambient}");
        }

        private void AddBlocker()
        {
            blocker = SpawnPawn(East(20));
            SetFill(blocker.def, 0f);
            SetSize(blocker, 1f);
        }

        private void CheckSmallShadow()
        {
            CheckTransmission(PawnTransmission(blocker), "one adult pawn attenuates a directly reached pawn");
            Check(IsLit(ColorAt(East(20))), "the blocker remains lit in its own cell");
        }

        private void SetLargePawn() => SetSize(blocker, 4f);

        private void CheckLargeShadow()
        {
            CheckTransmission(PawnTransmission(blocker), "size-based pawn opacity is capped at 0.9");
            Check(BeamShadowOpacity(1.5f, 1f) == 1f, "explicit pawn cover clamps at complete opacity");
            SetSize(blocker, 4f);
        }

        private void CheckExplicitShadow()
        {
            Check(blocker.def.Fillage == FillCategory.Partial && blocker.def.BaseBlockChance() > 0f,
                "positive partial fill supplies explicit cover for the pawn");
            float transmission = 1f - Math.Min(1f, blocker.def.BaseBlockChance());
            CheckTransmission(transmission, "explicit pawn cover overrides body-size fallback");
        }

        private void AddSecondBlocker()
        {
            SetFill(blocker.def, 0f);
            SetSize(blocker, 1f);
            blocker.Destroy(DestroyMode.Vanish);
            blocker = SpawnPawn(East(20));
            SetFill(blocker.def, 0f);
            SetSize(blocker, 1f);
            secondBlocker = SpawnPawn(East(25));
            SetSize(secondBlocker, 1f);
        }

        private void CheckStackedShadow() => CheckTransmission(PawnTransmission(blocker) * PawnTransmission(secondBlocker), "aligned blockers multiply once per occupied cell");

        private void AddCoincidentPawns()
        {
            blocker.Destroy(DestroyMode.Vanish);
            secondBlocker.Destroy(DestroyMode.Vanish);
            blocker = SpawnPawn(East(20));
            secondBlocker = SpawnPawn(East(20));
            foreach (Pawn pawn in new[] { blocker, secondBlocker }) { SetFill(pawn.def, 0f); SetSize(pawn, 1f); }
        }

        private void CheckCoincidentPawns()
        {
            CheckTransmission(PawnTransmission(blocker), "coincident pawns in one cell use strongest cover once");
            Check(IsLit(ColorAt(East(20))), "coincident blocker pawns remain lit in their shared cell");
        }

        private void AddChunk()
        {
            blocker.Destroy(DestroyMode.Vanish);
            secondBlocker.Destroy(DestroyMode.Vanish);
            chunkDef = DefDatabase<ThingDef>.GetNamed("ChunkGranite", false);
            Check(chunkDef != null && chunkDef.category == ThingCategory.Item, "vanilla granite chunk is an item");
            Check(Math.Abs(chunkDef.fillPercent - 0.5f) < 0.001f && chunkDef.Fillage == FillCategory.Partial,
                "vanilla granite chunk retains its 0.50 partial fill value");
            chunk = GenSpawn.Spawn(ThingMaker.MakeThing(chunkDef), East(20), map);
            blocker = SpawnPawn(East(20));
            SetFill(blocker.def, 0f);
            SetSize(blocker, 1f);
        }

        private void CheckChunkShadow()
        {
            float opacity = Math.Max(PawnOpacity(blocker), Math.Max(0.35f, Math.Min(1f, chunk.BaseBlockChance())));
            CheckTransmission(1f - opacity, "coincident pawn and chunk use strongest cover once");
        }

        private void RemoveChunk() => chunk.Destroy(DestroyMode.Vanish);

        private void CheckChunkRemoved()
        {
            CheckTransmission(PawnTransmission(blocker), "removing coincident chunk reveals only the pawn cover");
            blocker.Destroy(DestroyMode.Vanish);
        }

        private void AddWall() => wall = SpawnWall(East(20));

        private void CheckWall()
        {
            Check(IsDark(ColorAt(Target)), "opaque wall clips the beam before the receiver");
            wall.Destroy(DestroyMode.Vanish);
        }

        private void SetupNativeMover()
        {
            mover = SpawnPawn(East(20));
            SetFill(mover.def, 0f);
            SetSize(mover, 1f);
        }

        private void StartNativeMove()
        {
            CheckTransmission(PawnTransmission(mover), "native-movement pawn starts in the beam");
            moveStart = Tick;
            // Direct native Position change between simulation ticks. The next map tick
            // must withdraw the old-cell light and republish the receiver without respawn.
            mover.Position = East(21, 1);
            StartTickCheck(() =>
            {
                if (Tick > moveStart)
                {
                    Check(Tick == moveStart + 1 && mover.Position == East(21, 1), "native pawn Position change was observed on the next tick");
                    if (requireCe) Check(IsLit(ColorAt(East(20))), "old cell remains included in the moved pawn's in-beam CE support");
                    else Check(IsDark(ColorAt(East(20))), "old occupied cell no longer retains precise pawn light");
                    CheckTransmission(1f, "receiver brightened after blocker moved outside the beam");
                    Record($"move; method=direct Position setter, no despawn/respawn; from={East(20)}; to={mover.Position}; tick={Tick}");
                    EndTickCheck();
                }
            }, 500);
        }

        private void CheckAfterMove() { mover.Destroy(DestroyMode.Vanish); }

        private void AddCrossingLamp()
        {
            blocker = SpawnPawn(East(20));
            SetFill(blocker.def, 0f);
            SetSize(blocker, 1f);
        }

        private void CaptureFirstLampShadow()
        {
            Record($"first-lamp-before-overlap; operating={lamp.IsOperating}; mode={lamp.CurrentMode}; heading={lamp.AimHeading:F1}; range={lamp.AimRange:F1}; receiver={receiver.Position}; blocker={blocker.Position}; target={ColorAt(Target)} ground={Ground(Target):F4}");
            Check(lamp.IsOperating, "primary searchlight remains powered before overlap sample");
            shadowed = ColorAt(Target);
            CheckTransmission(PawnTransmission(blocker), "single-lamp sample is shadowed before overlap");
            Flick(lamp, false);
            secondLamp = SpawnLamp(East(40, -42));
            secondLamp.SetAimCell(Target);
        }

        private void CaptureSecondLamp()
        {
            secondOnly = ColorAt(Target);
            Check(IsLit(secondOnly), "crossing searchlight lights the receiver independently");
            Flick(lamp, true);
        }

        private void CheckLampOverlap()
        {
            CheckMaxColor(ColorAt(Target), shadowed, secondOnly, "overlapping lamps use the stronger per-cell result");
            secondLamp.Destroy(DestroyMode.Vanish);
        }

        private void SetTorchBaseline()
        {
            CheckTransmission(PawnTransmission(blocker), "removing the crossing lamp reveals the pawn shadow");
            Flick(lamp, false);
            torch = GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("TorchLamp"), Target, map);
            torch.TryGetComp<CompRefuelable>().Refuel(100);
        }

        private void CheckTorchOverlap()
        {
            torchOnly = ColorAt(Target);
            Check(IsLit(torchOnly), "vanilla torch lights the receiver with the beam off");
            Flick(lamp, true);
            WaitForPower = true;
        }

        private bool WaitForPower;
        private void SwitchMode()
        {
            if (WaitForPower)
            {
                var combined = ColorAt(Target);
                CheckMaxColor(combined, torchOnly, shadowed, "torch contribution survives pawn attenuation by max composition");
                torch.Destroy(DestroyMode.Vanish);
                WaitForPower = false;
            }
            Current.Game.GetComponent<SearchlightLightingGame>().SetMode(SearchlightLightingMode.LegacyEndpoint);
            Check(lamp.SpotCell.IsValid, "legacy mode restores the endpoint provider");
        }

        private void RestorePreciseMode()
        {
            Current.Game.GetComponent<SearchlightLightingGame>().SetMode(SearchlightLightingMode.PreciseField);
            Check(!lamp.SpotCell.IsValid, "precise mode removes the legacy endpoint provider");
        }

        private void CheckModeRestore() => CheckTransmission(PawnTransmission(blocker), "precise pawn field returns after a mode switch");

        private void Cleanup()
        {
            if (secondLamp != null && !secondLamp.Destroyed) secondLamp.Destroy(DestroyMode.Vanish);
            if (torch != null && !torch.Destroyed) torch.Destroy(DestroyMode.Vanish);
            if (chunk != null && !chunk.Destroyed) chunk.Destroy(DestroyMode.Vanish);
            if (wall != null && !wall.Destroyed) wall.Destroy(DestroyMode.Vanish);
            foreach (Pawn pawn in new[] { receiver, blocker, secondBlocker, mover })
                if (pawn != null && !pawn.Destroyed) { if (pawn.Spawned) pawn.DeSpawn(); pawn.Destroy(DestroyMode.Vanish); }
        }

        private void CheckTransmission(float transmission, string label)
        {
            Color32 actual = ColorAt(Target);
            byte red = Expected(bare.r, ambient.r, transmission), green = Expected(bare.g, ambient.g, transmission), blue = Expected(bare.b, ambient.b, transmission);
            float expectedGround = Math.Min(0.5f, Math.Max(red, Math.Max(green, blue)) * (3.6f / 255f));
            Check(Math.Abs(actual.r - red) <= 1 && Math.Abs(actual.g - green) <= 1 && Math.Abs(actual.b - blue) <= 1,
                $"{label}: expected RGB({red},{green},{blue}), observed {Describe(Target)}");
            Check(Math.Abs(Ground(Target) - expectedGround) <= 0.01f,
                $"{label}: expected ground glow {expectedGround:F4}, observed {Ground(Target):F4}");
        }

        private static byte Expected(byte channel, byte ambientChannel, float transmission) =>
            Math.Max(ambientChannel, transmission >= 1f ? channel : (byte)Math.Floor(Math.Min(SaturatedIntensity, channel) * transmission + 0.5f));
        private static float PawnOpacity(Pawn pawn) => Math.Min(0.9f, 0.35f * Math.Max(0f, pawn.BodySize));
        private static float PawnTransmission(Pawn pawn) => 1f - PawnOpacity(pawn);
        private static bool IsLit(Color32 color) => color.r != 0 || color.g != 0 || color.b != 0;
        private static bool IsDark(Color32 color) => color.r == 0 && color.g == 0 && color.b == 0;
        private void CheckMaxColor(Color32 actual, Color32 first, Color32 second, string label)
        {
            Check(actual.r == Math.Max(first.r, second.r) && actual.g == Math.Max(first.g, second.g) && actual.b == Math.Max(first.b, second.b),
                $"{label}: expected RGB({Math.Max(first.r, second.r)},{Math.Max(first.g, second.g)},{Math.Max(first.b, second.b)}), observed {Describe(Target)}");
        }

        private float BeamShadowOpacity(float explicitCover, float size) =>
            (float)typeof(BeamField).Assembly.GetType("DirectedSearchlights.BeamShadowGeometry", true)
                .GetMethod("PawnOpacity", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { explicitCover, size });

        private void SetSize(Pawn pawn, float size)
        {
            if (!originalSize.ContainsKey(pawn.def.race)) originalSize[pawn.def.race] = (float)bodySize.GetValue(pawn.def.race);
            bodySize.SetValue(pawn.def.race, size);
        }

        private void SetFill(ThingDef def, float value)
        {
            if (!originalFill.ContainsKey(def)) originalFill[def] = (float)fillPercent.GetValue(def);
            fillPercent.SetValue(def, value);
        }

        private Pawn SpawnPawn(IntVec3 cell)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(pawn, cell, map);
            pawn.drafter.Drafted = true;
            var hold = JobMaker.MakeJob(JobDefOf.Wait); hold.expiryInterval = 500000; pawn.jobs.StartJob(hold, JobCondition.InterruptForced);
            return pawn;
        }

        private Building_Searchlight SpawnLamp(IntVec3 cell)
        {
            var source = (Building_Searchlight)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret"), cell, map);
            var breakdown = source.GetComp<CompBreakdownable>(); if (breakdown != null) map.GetComponent<BreakdownManager>().Deregister(breakdown);
            GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"), cell + IntVec3.West, map);
            source.SetAlertMode(DetectionAlertMode.Off); source.SetMode(SearchlightMode.Fixed); return source;
        }

        private Building SpawnWall(IntVec3 cell) => (Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), cell, map);
        private static void Flick(Building_Searchlight source, bool on) => source.GetComp<CompFlickable>().SwitchIsOn = on;
        private void ClearArea(CellRect rect)
        {
            rect.ClipInsideMap(map);
            foreach (IntVec3 cell in rect)
            {
                map.roofGrid.SetRoof(cell, null);
                foreach (Thing thing in cell.GetThingList(map).ToArray())
                    if (thing is Plant || thing.def.IsEdifice() || thing.def.category == ThingCategory.Item || thing.TryGetComp<CompGlower>() != null)
                        thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
        }

        private void SetHour(int hour)
        {
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != hour; i++) Find.TickManager.DebugSetTicksGame(Tick + 2500);
            Check(GenLocalDate.HourOfDay(map) == hour, "fixture reached local night");
        }

        private IntVec3 East(int x, int z = 0) => origin + new IntVec3(x, 0, z);
        private Color32 ColorAt(IntVec3 cell) => map.glowGrid.VisualGlowAt(cell);
        private float Ground(IntVec3 cell) => map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
        private string Describe(IntVec3 cell) => $"{cell} {Ground(cell):F4}/{ColorAt(cell)}";
        private void Queue(string name, int ticks, Action run) => steps.Enqueue((name, ticks, run));
        private void Schedule(int ticks) { nextTick = Tick + ticks; nextFrame = Time.frameCount + 5; }
        private void StartTickCheck(Action check, int maxTicks) { tickCheck = check; tickDeadline = Tick + maxTicks; }
        private void EndTickCheck() { tickCheck = null; Schedule(16); }
        private void Record(string line) { File.AppendAllText(output, line + Environment.NewLine); Log.Message("SearchlightPawnShadowHarness: " + line); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            foreach (var entry in originalSize) bodySize.SetValue(entry.Key, entry.Value);
            foreach (var entry in originalFill) fillPercent.SetValue(entry.Key, entry.Value);
            Record(result);
            Application.Quit(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
