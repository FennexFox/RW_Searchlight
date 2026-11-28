using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using DirectedSearchlights;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightLightingPerformanceHarness
{
    internal static class TickProbe
    {
        private static long[] samples = new long[4000];
        private static long started;
        private static int writeIndex;
        private static bool enabled;
        private static bool inTick;

        public static void Initialize(int capacity) { samples = new long[Math.Max(4000, capacity)]; }

        public static void Begin()
        {
            writeIndex = 0;
            enabled = true;
        }

        public static long[] End()
        {
            enabled = false;
            return samples.Take(writeIndex).ToArray();
        }

        public static void Stop() { enabled = false; }

        [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
        private static class DoSingleTickPatch
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                inTick = enabled;
                if (inTick) started = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!inTick) return;
                inTick = false;
                long elapsed = Stopwatch.GetTimestamp() - started;
                if (writeIndex < samples.Length) samples[writeIndex++] = elapsed;
            }
        }
    }

    internal static class PerformanceSeed
    {
        public static readonly string Value = ReadSeed();

        private static string ReadSeed()
        {
            GenCommandLine.TryGetCommandLineArg("ds-lighting-seed", out string seed);
            return string.IsNullOrWhiteSpace(seed) ? "SearchlightPerformance" : seed;
        }
    }

    [StaticConstructorOnStartup]
    internal static class PerformanceHarmonyBootstrap
    {
        static PerformanceHarmonyBootstrap()
        {
            new Harmony("searchlight.lightingperformance").PatchAll(typeof(PerformanceHarmonyBootstrap).Assembly);
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GenerateWorld))]
    internal static class FixedWorldSeedPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref string seedString) { seedString = PerformanceSeed.Value; }
    }

    [HarmonyPatch(typeof(GameInitData), nameof(GameInitData.ChooseRandomStartingTile))]
    internal static class FixedStartingTilePatch
    {
        [HarmonyPrefix]
        private static void Prefix() => Rand.PushState(GenText.StableStringHash(PerformanceSeed.Value));

        [HarmonyPostfix]
        private static void Postfix() => Rand.PopState();
    }

    public sealed class LightingPerformanceCheck : GameComponent
    {
        private enum Workload { Static, Rotating, MovingTarget, ToggledDoor, CrossingStationary, CrossingRotating, CrossingTracking }
        private enum Phase { Setup, Warmup, Sample, FinishSample, Cleanup, MemoryCheck, Finished }
        private const int DoorToggleInterval = 90;
        private const int DoorGlowSampleDelay = 60;
        private static readonly Workload[] Workloads = { Workload.Static, Workload.Rotating, Workload.MovingTarget, Workload.ToggledDoor };
        private static readonly int[] FullCounts = { 0, 10, 50 };
        private static readonly List<Building_Searchlight> Lamps = new List<Building_Searchlight>();
        private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo ThreadAllocatedBytes = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
        private static string resultPath;
        private static int warmupTicks = 600;
        private static int sampleTicks = 3600;
        private static int repeats = 3;
        private static int[] counts = FullCounts;
        private static Workload[] workloads = Workloads;
        private static int workloadIndex, countIndex, repeatIndex, modeIndex;
        private static int phaseTick, nextDoorToggle, nextDoorGlowSample;
        private static int nextWalkTurn;
        private static bool walkingRight = true;
        private static int walkerTransitions, walkerTransitionsAtSampleStart;
        private static IntVec3 lastWalkerPosition;
        private static readonly List<Pawn> crossingPawns = new List<Pawn>();
        private static readonly List<IntVec3> crossingLastPositions = new List<IntVec3>();
        private static readonly List<bool> crossingMovingPositiveZ = new List<bool>();
        private static readonly List<int> crossingPawnMoveCounts = new List<int>();
        private static readonly List<int> crossingPawnMoveCountsAtSampleStart = new List<int>();
        private static int crossingMoves, crossingMovesAtSampleStart;
        private static long meshPoseRebuildsAtSampleStart, shadowRebuildsAtSampleStart;
        private static long shadowInvalidationsAtSampleStart, shadowAffectedLampsAtSampleStart;
        private static bool shadowRebuildCounterSupported, shadowInvalidationCountersSupported;
        private static int rotationChanges, rotationChangesAtSampleStart;
        private static float lastRotationHeading;
        private static int doorToggleCycles, doorOpenLitSamples, doorClosedDarkSamples;
        private static int doorToggleCyclesAtSampleStart, doorOpenSamplesAtSampleStart, doorClosedSamplesAtSampleStart;
        private static IntVec3 walkerStartCell;
        private static float rotatingHeadingStart;
        private static float latestOpenGlow = float.NaN, latestClosedGlow = float.NaN;
        private static Phase phase = Phase.Setup;
        private static Map map;
        private static IntVec3 origin;
        private static Pawn walker;
        private static readonly List<Building_Door> Doors = new List<Building_Door>();
        private static long workingSetBefore;
        private static long gcBytesBefore;
        private static int gc0Before, gc1Before, gc2Before;
        private static object lightingGame;
        private static MethodInfo setMode;
        private static PropertyInfo currentMode;
        private static object lightingMap;
        private static object searchlightMap;
        private static Type lightingMapType;
        private static Type integrationType;
        private static PropertyInfo measureFlush;
        private static MethodInfo flushTicks;
        private static long flushTicksStart;
        private static long[] stageStart;
        private static int unloadCycle;
        private static long managedMemoryBefore;

        public LightingPerformanceCheck(Game game)
        {
            GenCommandLine.TryGetCommandLineArg("ds-lighting-performance-result", out resultPath);
            if (string.IsNullOrWhiteSpace(resultPath)) resultPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "lighting-performance.txt");
            ReadOptions();
            TickProbe.Initialize(sampleTicks);
            Write("harness-created; assemblyMvid=" + typeof(Building_Searchlight).Assembly.ManifestModule.ModuleVersionId.ToString("D") +
                "; warmupTicks=" + warmupTicks + "; sampleTicks=" + sampleTicks + "; repeats=" + repeats +
                "; counts=" + string.Join(",", counts) + "; workloads=" + string.Join(",", workloads));
        }

        public override void StartedNewGame() => Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;

        public override void GameComponentUpdate()
        {
            if (phase != Phase.Finished && Current.ProgramState == ProgramState.Playing &&
                Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast)
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        public override void GameComponentTick()
        {
            if (phase == Phase.Finished || Find.CurrentMap == null) return;
            try
            {
                int age = Find.TickManager.TicksGame - phaseTick;
                if (phase == Phase.Setup) Setup();
                else if (phase == Phase.Warmup && age >= warmupTicks) BeginSample();
                else if (phase == Phase.Sample && age >= sampleTicks) EndSample();
                else if (phase == Phase.FinishSample) FinishSample();
                else if (phase == Phase.Cleanup && age >= 30) AdvanceCase();
                else if (phase == Phase.MemoryCheck) RunMapUnloadCycle();
                if (phase == Phase.Warmup || phase == Phase.Sample) DriveWorkload();
            }
            catch (Exception ex) { Fail(ex); }
        }

        private static void ReadOptions()
        {
            string value;
            if (GenCommandLine.TryGetCommandLineArg("ds-lighting-warmup", out value) && int.TryParse(value, out int n)) warmupTicks = Math.Max(1, n);
            if (GenCommandLine.TryGetCommandLineArg("ds-lighting-sample", out value) && int.TryParse(value, out n)) sampleTicks = Math.Max(10, n);
            if (GenCommandLine.TryGetCommandLineArg("ds-lighting-repeats", out value) && int.TryParse(value, out n)) repeats = Math.Max(1, n);
            if (GenCommandLine.TryGetCommandLineArg("ds-lighting-counts", out value)) counts = ParseInts(value, FullCounts);
            if (GenCommandLine.TryGetCommandLineArg("ds-lighting-workloads", out value))
                workloads = value.Split(',').Select(s => (Workload)Enum.Parse(typeof(Workload), s, true)).ToArray();
        }

        private static int[] ParseInts(string text, int[] fallback) => text.Split(',').Select(s =>
        {
            if (!int.TryParse(s, out int n) || n < 0 || n > 100) throw new ArgumentException("Invalid lamp count: " + s);
            return n;
        }).Distinct().ToArray();

        private static void Setup()
        {
            map = Find.CurrentMap;
            origin = FindOpenFixture(map);
            Find.CameraDriver.SetRootPosAndSize(origin.ToVector3Shifted(), 30f);
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToArray())
            {
                pawn.jobs.StopAll();
                pawn.pather.StopDead();
                Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                wait.expiryInterval = 100000;
                pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
            }
            GameDataSaveLoader.SaveGame("lighting-performance-baseline");
            Write("baseline-save; file=" + GenFilePaths.FilePathForSavedGame("lighting-performance-baseline") +
                "; worldSeed=" + ReadWorldSeed() + "; startingTile=" + map.Tile);
            lightingGame = Current.Game.GetComponent<SearchlightLightingGame>();
            if (lightingGame == null) throw new InvalidOperationException("SearchlightLightingGame component is missing.");
            Type api = lightingGame.GetType();
            setMode = api.GetMethod("SetMode", AnyInstance);
            currentMode = api.GetProperty("CurrentMode", AnyInstance);
            if (setMode == null || currentMode == null) throw new MissingMemberException(api.FullName, "CurrentMode/SetMode");
            lightingMapType = typeof(Building_Searchlight).Assembly.GetType("DirectedSearchlights.BeamLightingMap", true);
            integrationType = typeof(Building_Searchlight).Assembly.GetType("DirectedSearchlights.LightingIntegration", true);
            measureFlush = integrationType.GetProperty("MeasureFlush", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            flushTicks = integrationType.GetMethod("FlushTicks", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            lightingMap = GetLightingDiagnostics();
            searchlightMap = GetSearchlightMapDiagnostics();
            if (lightingMap != null) SetDiagnosticCollection(true);
            TickProbe.Begin();
            TickProbe.End();
            BuildCase();
            StartMode(0);
        }

        private static IntVec3 FindOpenFixture(Map currentMap)
        {
            IntVec3 desired = currentMap.Center;
            for (int r = 0; r < 60; ++r)
            for (int dx = -r; dx <= r; ++dx)
            for (int dz = -r; dz <= r; ++dz)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                IntVec3 c = desired + new IntVec3(dx, 0, dz);
                if (!c.InBounds(currentMap) || !c.Walkable(currentMap) || c.Roofed(currentMap)) continue;
                bool open = true;
                IntVec3 movingPawnStart = c + new IntVec3(38, 0, 0);
                IntVec3 movingPawnLeft = c + new IntVec3(35, 0, 0);
                IntVec3 movingPawnRight = c + new IntVec3(40, 0, 0);
                if (!movingPawnStart.Walkable(currentMap) || !movingPawnLeft.Walkable(currentMap) || !movingPawnRight.Walkable(currentMap)) open = false;
                for (int lane = -24; lane <= 25 && open; lane++)
                {
                    IntVec3 start = c + new IntVec3(-12, 0, lane);
                    IntVec3 end = start + new IntVec3(60, 0, 0);
                    IntVec3 doorCell = start + new IntVec3(24, 0, 0);
                    IntVec3 doorSample = start + new IntVec3(30, 0, 0);
                    if (!start.InBounds(currentMap) || !end.InBounds(currentMap) ||
                        !start.Walkable(currentMap) || !doorCell.Walkable(currentMap) || !doorSample.Walkable(currentMap) ||
                        !BeamField.Clear(currentMap, start, end) ||
                        !BeamField.Clear(currentMap, start, movingPawnStart) ||
                        !BeamField.Clear(currentMap, start, movingPawnLeft) || !BeamField.Clear(currentMap, start, movingPawnRight)) open = false;
                }
                if (open) return c;
            }
            throw new InvalidOperationException("Could not find an open 120-cell test corridor.");
        }

        private static void BuildCase()
        {
            ClearCase();
            Workload workload = workloads[workloadIndex];
            int count = counts[countIndex];
            bool crossingWorkload = IsCrossingWorkload(workload);
            if (crossingWorkload && count > 0) BuildCrossingPawns(count);
            if (workload == Workload.MovingTarget && count > 0)
            {
                PawnKindDef walkerKind = DefDatabase<PawnKindDef>.GetNamed("Pirate", false) ?? PawnKindDefOf.Colonist;
                var request = new PawnGenerationRequest(walkerKind, Faction.OfAncientsHostile,
                    developmentalStages: DevelopmentalStage.Adult, mustBeCapableOfViolence: true);
                walker = PawnGenerator.GeneratePawn(request);
                GenSpawn.Spawn(walker, origin + new IntVec3(38, 0, 0), map);
                map.fogGrid.Unfog(walker.Position);
                walker.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, origin + new IntVec3(40, 0, 0)), JobCondition.InterruptForced);
                walkingRight = true;
                nextWalkTurn = Find.TickManager.TicksGame + 120;
                lastWalkerPosition = walker.Position;
                walkerTransitions = 0;
            }
            ThingDef lightDef = DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret", true);
            for (int i = 0; i < count; i++)
            {
                int lane = count <= 1 ? 0 : (int)Math.Round(i * 49.0 / (count - 1));
                IntVec3 p = origin + new IntVec3(-12, 0, -24 + lane);
                Building_Searchlight lamp = (Building_Searchlight)GenSpawn.Spawn(lightDef, p, map);
                var breakdown = lamp.GetComp<CompBreakdownable>();
                if (breakdown != null) map.GetComponent<BreakdownManager>().Deregister(breakdown);
                lamp.GetComp<CompPowerTrader>().PowerOn = true;
                lamp.SetMode(workload == Workload.Rotating || workload == Workload.MovingTarget || workload == Workload.CrossingRotating
                    ? SearchlightMode.Sweep : SearchlightMode.Fixed);
                int aimDistance = workload == Workload.ToggledDoor ? 30 : 60;
                lamp.SetAimCell(p + new IntVec3(aimDistance, 0, 0));
                if (workload == Workload.MovingTarget)
                {
                    lamp.SetMode(SearchlightMode.Automatic);
                    lamp.SelectManualTarget(walker);
                }
                else if (workload == Workload.CrossingTracking)
                {
                    lamp.SetMode(SearchlightMode.Automatic);
                    lamp.SelectManualTarget(crossingPawns[i]);
                }
                Lamps.Add(lamp);
                if (workload == Workload.ToggledDoor)
                {
                    ThingDef doorDef = DefDatabase<ThingDef>.GetNamed("Door", true);
                    Doors.Add((Building_Door)GenSpawn.Spawn(doorDef, p + new IntVec3(24, 0, 0), map, Rot4.North));
                }
            }
            if (workload == Workload.Rotating || workload == Workload.CrossingRotating)
                foreach (Building_Searchlight lamp in Lamps) lamp.SetSweepAngle(180f);
            rotationChanges = 0;
            if (Lamps.Count > 0) lastRotationHeading = Lamps[0].AimHeading;
            nextDoorToggle = Find.TickManager.TicksGame + DoorToggleInterval;
            nextDoorGlowSample = int.MaxValue;
            doorToggleCycles = doorOpenLitSamples = doorClosedDarkSamples = 0;
            latestOpenGlow = latestClosedGlow = float.NaN;
            Write("case; workload=" + workload + "; lamps=" + count + "; mode=" + ModeName(modeIndex) +
                "; mapId=" + map.uniqueID + "; mapSize=" + map.Size + "; origin=" + origin + "; tile=" + map.Tile);
        }

        private static void ClearCase()
        {
            foreach (Building_Searchlight lamp in Lamps.ToArray()) if (!lamp.Destroyed) lamp.Destroy(DestroyMode.Vanish);
            Lamps.Clear();
            if (walker != null && !walker.Destroyed) walker.Destroy(DestroyMode.Vanish);
            walker = null;
            foreach (Pawn pawn in crossingPawns.ToArray()) if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
            crossingPawns.Clear();
            crossingLastPositions.Clear();
            crossingMovingPositiveZ.Clear();
            crossingPawnMoveCounts.Clear();
            crossingPawnMoveCountsAtSampleStart.Clear();
            foreach (Building_Door door in Doors.ToArray()) if (!door.Destroyed) door.Destroy(DestroyMode.Vanish);
            Doors.Clear();
        }

        private static bool IsCrossingWorkload(Workload workload) =>
            workload == Workload.CrossingStationary || workload == Workload.CrossingRotating || workload == Workload.CrossingTracking;

        private static void BuildCrossingPawns(int count)
        {
            bool tracking = workloads[workloadIndex] == Workload.CrossingTracking;
            PawnKindDef walkerKind = tracking
                ? (DefDatabase<PawnKindDef>.GetNamed("Pirate", false) ?? PawnKindDefOf.Colonist)
                : PawnKindDefOf.Colonist;
            Faction faction = tracking ? Faction.OfAncientsHostile : Faction.OfPlayer;
            int seed = GenText.StableStringHash(PerformanceSeed.Value);
            for (int i = 0; i < count; i++)
            {
                int lane = LampLane(i, count);
                int crossingX = 12 + i * 2;
                IntVec3 negativeZ = origin + new IntVec3(crossingX, 0, -24 + lane - 4);
                IntVec3 positiveZ = origin + new IntVec3(crossingX, 0, -24 + lane + 4);
                bool movingPositiveZ = ((seed + i * 104729) & 1) == 0;
                Pawn pawn;
                Rand.PushState(unchecked(seed + i * 104729));
                try
                {
                    var request = new PawnGenerationRequest(walkerKind, faction,
                        developmentalStages: DevelopmentalStage.Adult, mustBeCapableOfViolence: true);
                    pawn = PawnGenerator.GeneratePawn(request);
                }
                finally { Rand.PopState(); }
                IntVec3 start = movingPositiveZ ? negativeZ : positiveZ;
                IntVec3 destination = movingPositiveZ ? positiveZ : negativeZ;
                bool clearCrossing = true;
                for (int dz = -4; dz <= 4; dz++)
                    if (!(origin + new IntVec3(crossingX, 0, -24 + lane + dz)).Walkable(map)) clearCrossing = false;
                if (!clearCrossing || !start.Walkable(map) || !destination.Walkable(map))
                    throw new InvalidOperationException("Crossing lane is not walkable: " + start + " -> " + destination);
                GenSpawn.Spawn(pawn, start, map);
                map.fogGrid.Unfog(start);
                pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, destination), JobCondition.InterruptForced);
                crossingPawns.Add(pawn);
                crossingLastPositions.Add(pawn.Position);
                crossingMovingPositiveZ.Add(movingPositiveZ);
                crossingPawnMoveCounts.Add(0);
            }
            crossingMoves = 0;
        }

        private static int LampLane(int index, int count) => count <= 1 ? 0 : (int)Math.Round(index * 49.0 / (count - 1));

        private static void StartMode(int index)
        {
            modeIndex = index;
            Type enumType = setMode.GetParameters().Single().ParameterType;
            object mode = Enum.Parse(enumType, ModeName(modeIndex));
            setMode.Invoke(lightingGame, new[] { mode });
            if (workloads[workloadIndex] == Workload.CrossingRotating)
            {
                foreach (Building_Searchlight lamp in Lamps)
                {
                    lamp.SetMode(SearchlightMode.Fixed);
                    lamp.SetMode(SearchlightMode.Sweep);
                    lamp.SetSweepAngle(180f);
                }
                rotationChanges = 0;
                if (Lamps.Count > 0) lastRotationHeading = Lamps[0].AimHeading;
            }
            if (lightingMap == null) lightingMap = GetLightingDiagnostics();
            if (lightingMap != null) SetDiagnosticCollection(true);
            Write("mode-set; mode=" + currentMode.GetValue(lightingGame));
            phase = Phase.Warmup;
            phaseTick = Find.TickManager.TicksGame;
        }

        private static string ModeName(int order)
        {
            bool preciseFirst = (repeatIndex & 1) != 0;
            bool precise = order == 0 ? preciseFirst : !preciseFirst;
            return precise ? "PreciseField" : "LegacyEndpoint";
        }

        private static void BeginSample()
        {
            ValidateCase();
            walkerStartCell = walker == null ? IntVec3.Invalid : walker.Position;
            walkerTransitionsAtSampleStart = walkerTransitions;
            crossingMovesAtSampleStart = crossingMoves;
            crossingPawnMoveCountsAtSampleStart.Clear();
            crossingPawnMoveCountsAtSampleStart.AddRange(crossingPawnMoveCounts);
            rotationChangesAtSampleStart = rotationChanges;
            rotatingHeadingStart = Lamps.Count == 0 ? 0f : Lamps[0].AimHeading;
            doorToggleCyclesAtSampleStart = doorToggleCycles;
            doorOpenSamplesAtSampleStart = doorOpenLitSamples;
            doorClosedSamplesAtSampleStart = doorClosedDarkSamples;
            TickProbe.Begin();
            CaptureGc(out gcBytesBefore, out gc0Before, out gc1Before, out gc2Before);
            workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
            stageStart = ReadCounters();
            flushTicksStart = ReadFlushTicks();
            meshPoseRebuildsAtSampleStart = SumLampCounter("MeshPoseCount", out _);
            shadowRebuildsAtSampleStart = SumLampCounter("ShadowInvalidationRebuildCount", out shadowRebuildCounterSupported);
            shadowInvalidationsAtSampleStart = ReadOptionalCounter(searchlightMap, "ShadowInvalidationNotifications", out shadowInvalidationCountersSupported);
            shadowAffectedLampsAtSampleStart = ReadOptionalCounter(searchlightMap, "ShadowInvalidatedLampCount", out bool affectedLampCounterSupported);
            shadowInvalidationCountersSupported &= affectedLampCounterSupported;
            if (measureFlush != null) measureFlush.SetValue(null, true);
            phase = Phase.Sample;
            phaseTick = Find.TickManager.TicksGame;
        }

        private static void EndSample()
        {
            TickProbe.Stop();
            if (measureFlush != null) measureFlush.SetValue(null, false);
            SetDiagnosticCollection(false);
            phase = Phase.FinishSample;
        }

        private static void FinishSample()
        {
            long[] raw = TickProbe.End();
            if (Lamps.Any(lamp => !lamp.IsOperating))
                throw new InvalidOperationException("A measured lamp stopped operating before the sample finished.");
            if (raw.Length != sampleTicks) throw new InvalidOperationException("Expected " + sampleTicks + " measured ticks; captured " + raw.Length + ".");
            double[] ms = raw.Select(t => t * 1000.0 / Stopwatch.Frequency).OrderBy(x => x).ToArray();
            long[] stageEnd = ReadCounters();
            CaptureGc(out long gcBytesAfter, out int gc0After, out int gc1After, out int gc2After);
            long workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
            long flushDelta = ReadFlushTicks() - flushTicksStart;
            long meshPoseRebuilds = SumLampCounter("MeshPoseCount", out _) - meshPoseRebuildsAtSampleStart;
            long shadowRebuilds = SumLampCounter("ShadowInvalidationRebuildCount", out _) - shadowRebuildsAtSampleStart;
            long shadowInvalidations = ReadOptionalCounter(searchlightMap, "ShadowInvalidationNotifications", out _)-shadowInvalidationsAtSampleStart;
            long shadowAffectedLamps = ReadOptionalCounter(searchlightMap, "ShadowInvalidatedLampCount", out _) - shadowAffectedLampsAtSampleStart;
            int pawnMoves = crossingMoves - crossingMovesAtSampleStart;
            int movingPawns = 0;
            for (int i = 0; i < crossingPawnMoveCounts.Count && i < crossingPawnMoveCountsAtSampleStart.Count; i++)
                if (crossingPawnMoveCounts[i] > crossingPawnMoveCountsAtSampleStart[i]) movingPawns++;
            Write("sample; mode=" + ModeName(modeIndex) + "; workload=" + workloads[workloadIndex] + "; lamps=" + counts[countIndex] +
                "; repeat=" + (repeatIndex + 1) + "; ticks=" + raw.Length + "; medianMs=" + Percentile(ms, 0.50).ToString("F5") +
                "; p95Ms=" + Percentile(ms, 0.95).ToString("F5") + "; meanMs=" + ms.Average().ToString("F5") +
                "; allocationMetric=" + (ThreadAllocatedBytes == null ? "managedHeapEstimate" : "threadAllocatedBytes") +
                "; allocationDeltaBytes=" + (gcBytesAfter - gcBytesBefore) + "; gcCollections=" + (gc0After-gc0Before) + "," + (gc1After-gc1Before) + "," + (gc2After-gc2Before) +
                "; workingSetDelta=" + (workingSetAfter-workingSetBefore) +
                "; generationMs=" + StageMs(stageStart, stageEnd, 1).ToString("F5") +
                "; aggregationMs=" + StageMs(stageStart, stageEnd, 3).ToString("F5") +
                "; invalidationQueueMs=" + StageMs(stageStart, stageEnd, 5).ToString("F5") +
                "; deferredFlushMs=" + (flushDelta * 1000.0 / Stopwatch.Frequency).ToString("F5") +
                "; generatedCells=" + StageDelta(stageStart, stageEnd, 6) + "; aggregatedCells=" + StageDelta(stageStart, stageEnd, 7) +
                "; invalidatedCells=" + StageDelta(stageStart, stageEnd, 8) +
                "; rotationHeadingStart=" + rotatingHeadingStart + "; rotationHeadingEnd=" + (Lamps.Count == 0 ? 0f : Lamps[0].AimHeading) +
                "; rotationTransitions=" + (rotationChanges - rotationChangesAtSampleStart) +
                "; movingStart=" + walkerStartCell + "; movingEnd=" + (walker == null ? "none" : walker.Position.ToString()) +
                "; movingCellTransitions=" + (walkerTransitions - walkerTransitionsAtSampleStart) +
                "; pawnCrossingMoves=" + pawnMoves +
                "; movingPawns=" + movingPawns +
                "; meshPoseRebuilds=" + meshPoseRebuilds +
                "; meshPoseRebuildsPerMove=" + RatioOrUnavailable(meshPoseRebuilds, pawnMoves) +
                "; shadowInvalidationRebuilds=" + (shadowRebuildCounterSupported ? shadowRebuilds.ToString() : "unsupported") +
                "; shadowInvalidationRebuildsPerMove=" + (shadowRebuildCounterSupported ? RatioOrUnavailable(shadowRebuilds, pawnMoves) : "unsupported") +
                "; shadowInvalidations=" + (shadowInvalidationCountersSupported ? shadowInvalidations.ToString() : "unsupported") +
                "; shadowAffectedLampsPerMove=" + (shadowInvalidationCountersSupported ? RatioOrUnavailable(shadowAffectedLamps, pawnMoves) : "unsupported"));
            int minimumRotationChanges = Math.Max(1, sampleTicks / 60);
            if ((workloads[workloadIndex] == Workload.Rotating || workloads[workloadIndex] == Workload.CrossingRotating) && Lamps.Count > 0 &&
                rotationChanges - rotationChangesAtSampleStart < minimumRotationChanges)
                throw new InvalidOperationException("Rotating fixture changed heading too few times: " + (rotationChanges - rotationChangesAtSampleStart));
            if (workloads[workloadIndex] == Workload.MovingTarget && Lamps.Count > 0 &&
                (walker == null || !walker.Spawned || walkerTransitions - walkerTransitionsAtSampleStart < 2 ||
                    Lamps.Any(l => l.LockedTarget != walker)))
                throw new InvalidOperationException("Moving target did not move and remain tracked; start=" + walkerStartCell +
                    "; end=" + (walker == null ? "null" : walker.Position.ToString()) + "; transitions=" + (walkerTransitions - walkerTransitionsAtSampleStart) +
                    "; locks=" + string.Join(" | ", Lamps.Select(l => l.LockedTarget?.thingIDNumber.ToString() ?? "none")));
            if (IsCrossingWorkload(workloads[workloadIndex]) && Lamps.Count > 0 &&
                (crossingPawns.Count != Lamps.Count || pawnMoves < Math.Max(1, crossingPawns.Count * sampleTicks / 1200) ||
                    movingPawns < (sampleTicks < 1200 ? 1 : crossingPawns.Count) || crossingPawns.Any(p => !p.Spawned) ||
                    (workloads[workloadIndex] == Workload.CrossingTracking && Lamps.Where((lamp, i) => lamp.LockedTarget != crossingPawns[i]).Any())))
                throw new InvalidOperationException("Seeded pawn crossings did not move through all lamp lanes or tracking locks were lost; moves=" +
                    pawnMoves + "; pawns=" + crossingPawns.Count + "; locks=" +
                    string.Join(" | ", Lamps.Select((l, i) => l.Position + ":" + (l.LockedTarget == null ? "none" : l.LockedTarget.thingIDNumber.ToString()) +
                        "/" + (i < crossingPawns.Count ? crossingPawns[i].thingIDNumber.ToString() : "missing"))));
            if (workloads[workloadIndex] == Workload.ToggledDoor && Lamps.Count > 0 &&
                (doorToggleCycles - doorToggleCyclesAtSampleStart < 10 ||
                    doorOpenLitSamples == doorOpenSamplesAtSampleStart || doorClosedDarkSamples == doorClosedSamplesAtSampleStart ||
                    latestOpenGlow <= 0.01f || latestClosedGlow > 0.01f))
                throw new InvalidOperationException("Door fixture failed behind-door illumination check; cycles=" +
                    (doorToggleCycles - doorToggleCyclesAtSampleStart) + "; openGlow=" + latestOpenGlow + "; closedGlow=" + latestClosedGlow);
            if (workloads[workloadIndex] == Workload.ToggledDoor)
                Write("door-activity; cycles=" + (doorToggleCycles - doorToggleCyclesAtSampleStart) +
                    "; openLitSamples=" + (doorOpenLitSamples - doorOpenSamplesAtSampleStart) +
                    "; closedDarkSamples=" + (doorClosedDarkSamples - doorClosedSamplesAtSampleStart) +
                    "; openGlow=" + latestOpenGlow + "; closedGlow=" + latestClosedGlow);
            phase = Phase.Cleanup;
            phaseTick = Find.TickManager.TicksGame;
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            int index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1);
            return sorted[index];
        }

        private static string RatioOrUnavailable(long numerator, int denominator) => denominator <= 0
            ? "unavailable" : ((double)numerator / denominator).ToString("F5", System.Globalization.CultureInfo.InvariantCulture);

        private static long SumLampCounter(string name, out bool supported)
        {
            supported = true;
            long total = 0;
            foreach (Building_Searchlight lamp in Lamps)
            {
                object beam = ReadMember(lamp, "beam");
                if (beam == null || beam is string) { supported = false; continue; }
                object value = ReadMember(beam, name);
                if (value == null || value is string) { supported = false; continue; }
                total += Convert.ToInt64(value);
            }
            return total;
        }

        private static long ReadOptionalCounter(object instance, string name, out bool supported)
        {
            object value = instance == null ? null : ReadMember(instance, name);
            supported = value != null && !(value is string);
            return supported ? Convert.ToInt64(value) : 0L;
        }

        private static void DriveWorkload()
        {
            Workload workload = workloads[workloadIndex];
            if ((workload == Workload.Rotating || workload == Workload.CrossingRotating) && Lamps.Count > 0)
            {
                float current = Lamps[0].AimHeading;
                if (Mathf.Abs(Mathf.DeltaAngle(lastRotationHeading, current)) > 0.01f) rotationChanges++;
                lastRotationHeading = current;
            }
            if (workload == Workload.ToggledDoor && Doors.Count > 0 && Find.TickManager.TicksGame >= nextDoorToggle)
            {
                foreach (Building_Door door in Doors)
                {
                    if (door.Open) typeof(Building_Door).GetMethod("DoorTryClose", AnyInstance).Invoke(door, null);
                    else door.StartManualOpenBy(null);
                }
                doorToggleCycles++;
                nextDoorGlowSample = Find.TickManager.TicksGame + DoorGlowSampleDelay;
                nextDoorToggle = Find.TickManager.TicksGame + DoorToggleInterval;
            }
            if (workload == Workload.ToggledDoor && Doors.Count > 0 && Find.TickManager.TicksGame >= nextDoorGlowSample)
            {
                IntVec3 sampleCell = Lamps[0].Position + new IntVec3(30, 0, 0);
                float glow = map.glowGrid.GroundGlowAt(sampleCell, ignoreSky: true);
                if (Doors[0].Open)
                {
                    latestOpenGlow = glow;
                    if (glow > 0.01f) doorOpenLitSamples++;
                }
                else
                {
                    latestClosedGlow = glow;
                    if (glow <= 0.01f) doorClosedDarkSamples++;
                }
                nextDoorGlowSample = int.MaxValue;
            }
            if (workload == Workload.MovingTarget && walker != null && Find.TickManager.TicksGame >= nextWalkTurn)
            {
                walkingRight = !walkingRight;
                IntVec3 destination = origin + new IntVec3(walkingRight ? 40 : 35, 0, 0);
                walker.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, destination), JobCondition.InterruptForced);
                nextWalkTurn = Find.TickManager.TicksGame + 120;
            }
            if (workload == Workload.MovingTarget && walker != null && walker.Position != lastWalkerPosition)
            {
                walkerTransitions++;
                lastWalkerPosition = walker.Position;
            }
            if (IsCrossingWorkload(workload)) DrivePawnCrossings();
        }

        private static void DrivePawnCrossings()
        {
            for (int i = 0; i < crossingPawns.Count; i++)
            {
                Pawn pawn = crossingPawns[i];
                if (pawn == null || !pawn.Spawned) continue;
                bool movingPositiveZ = crossingMovingPositiveZ[i];
                int lane = LampLane(i, crossingPawns.Count);
                int crossingX = 12 + i * 2;
                IntVec3 destination = origin + new IntVec3(crossingX, 0, -24 + lane + (movingPositiveZ ? 4 : -4));
                bool arrived = pawn.Position == destination;
                if (arrived)
                {
                    movingPositiveZ = !movingPositiveZ;
                    crossingMovingPositiveZ[i] = movingPositiveZ;
                    destination = origin + new IntVec3(crossingX, 0, -24 + lane + (movingPositiveZ ? 4 : -4));
                }
                Job currentJob = pawn.jobs.curJob;
                if (arrived || currentJob == null || currentJob.def != JobDefOf.Goto || currentJob.targetA.Cell != destination)
                {
                    pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, destination), JobCondition.InterruptForced);
                }
                if (pawn.Position != crossingLastPositions[i])
                {
                    crossingMoves++;
                    crossingPawnMoveCounts[i]++;
                    crossingLastPositions[i] = pawn.Position;
                }
            }
        }

        private static long[] ReadCounters()
        {
            string[] names = { "SampleGenerationCalls", "SampleGenerationTicks", "AggregateCalls", "AggregateTicks", "InvalidationCalls", "InvalidationTicks", "SampledCells", "AggregatedCells", "InvalidatedCells" };
            return lightingMap == null ? new long[names.Length] : names.Select(n => Convert.ToInt64(ReadMember(lightingMap, n) ?? 0L)).ToArray();
        }

        private static long StageDelta(long[] before, long[] after, int index) => after[index] - before[index];
        private static double StageMs(long[] before, long[] after, int index) => StageDelta(before, after, index) * 1000.0 / Stopwatch.Frequency;
        private static long ReadFlushTicks() => flushTicks == null ? 0 : Convert.ToInt64(flushTicks.Invoke(null, new object[] { map }));

        private static void ValidateCase()
        {
            if (Lamps.Count != counts[countIndex] || Lamps.Any(l => !l.IsOperating))
                throw new InvalidOperationException("Lamp fixture did not reach the requested powered state.");
            if (workloads[workloadIndex] == Workload.MovingTarget && Lamps.Count > 0 &&
                (walker == null || !walker.Spawned || walkerTransitions < 1 || Lamps.Any(l => l.LockedTarget != walker)))
                throw new InvalidOperationException("Moving target fixture did not move and acquire every lamp; pawn=" +
                    (walker == null ? "null" : walker.Position + "; faction=" + walker.Faction + "; hostile=" + walker.HostileTo(Faction.OfPlayer) + "; transitions=" + walkerTransitions) +
                    "; lampStates=" + string.Join(" | ", Lamps.Select(l => l.Position + ":operating=" + l.IsOperating + "; lock=" + l.LockedTarget + "; lastAcquire=" + l.LastAcquireTick)));
            if (IsCrossingWorkload(workloads[workloadIndex]) && Lamps.Count > 0 &&
                (crossingPawns.Count != Lamps.Count || crossingPawns.Any(p => !p.Spawned) ||
                    (workloads[workloadIndex] == Workload.CrossingTracking && Lamps.Where((lamp, i) => lamp.LockedTarget != crossingPawns[i]).Any())))
                throw new InvalidOperationException("Seeded pawn crossings did not move through all lamp lanes or tracking locks were lost; moves=" +
                    crossingMoves + "; pawns=" + crossingPawns.Count + "; locks=" +
                    string.Join(" | ", Lamps.Select((l, i) => l.Position + ":" + (l.LockedTarget == null ? "none" : l.LockedTarget.thingIDNumber.ToString()) +
                        "/" + (i < crossingPawns.Count ? crossingPawns[i].thingIDNumber.ToString() : "missing"))));
            if (workloads[workloadIndex] == Workload.ToggledDoor && Lamps.Count > 0 &&
                (Doors.Count != Lamps.Count || doorToggleCycles < 1))
                throw new InvalidOperationException("Door fixture did not toggle frequently during warmup.");
        }

        private static string ReadWorldSeed()
        {
            string actual = Find.World.info.seedString;
            if (!string.Equals(actual, PerformanceSeed.Value, StringComparison.Ordinal))
                throw new InvalidOperationException("World seed mismatch; expected " + PerformanceSeed.Value + ", got " + actual);
            return actual;
        }

        private static object ReadMember(object instance, string name)
        {
            PropertyInfo p = instance.GetType().GetProperty(name, AnyInstance);
            if (p != null) return p.GetValue(instance);
            FieldInfo f = instance.GetType().GetField(name, AnyInstance);
            return f == null ? "missing" : f.GetValue(instance);
        }

        private static void SetDiagnosticCollection(bool value)
        {
            PropertyInfo p = lightingMapType.GetProperty("DiagnosticsEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite) p.SetValue(null, value);
        }

        private static object GetLightingDiagnostics()
        {
            Type mapType = typeof(Building_Searchlight).Assembly.GetType("DirectedSearchlights.SearchlightMap", true);
            object searchlightMap = map.components.FirstOrDefault(c => mapType.IsInstanceOfType(c));
            PropertyInfo p = mapType.GetProperty("Lighting", AnyInstance);
            return searchlightMap == null || p == null ? null : p.GetValue(searchlightMap);
        }

        private static object GetSearchlightMapDiagnostics()
        {
            Type mapType = typeof(Building_Searchlight).Assembly.GetType("DirectedSearchlights.SearchlightMap", true);
            return map.components.FirstOrDefault(c => mapType.IsInstanceOfType(c));
        }

        private static void CaptureGc(out long bytes, out int g0, out int g1, out int g2)
        {
            bytes = ThreadAllocatedBytes == null ? GC.GetTotalMemory(false) : Convert.ToInt64(ThreadAllocatedBytes.Invoke(null, null));
            g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
        }

        private static void AdvanceCase()
        {
            if (modeIndex == 0) { StartMode(1); return; }
            ClearCase();
            if (++repeatIndex < repeats) { BuildCase(); StartMode(0); return; }
            repeatIndex = 0;
            if (++countIndex < counts.Length) { BuildCase(); StartMode(0); return; }
            countIndex = 0;
            if (++workloadIndex < workloads.Length) { BuildCase(); StartMode(0); return; }
            ClearCase();
            SetDiagnosticCollection(false);
            managedMemoryBefore = GC.GetTotalMemory(true);
            unloadCycle = 0;
            phase = Phase.MemoryCheck;
        }

        private static void RunMapUnloadCycle()
        {
            if (unloadCycle >= 3)
            {
                long retained = GC.GetTotalMemory(true) - managedMemoryBefore;
                long workingSet = Process.GetCurrentProcess().WorkingSet64;
                Write("map-unload-memory; cycles=" + unloadCycle + "; managedRetainedDelta=" + retained + "; workingSet=" + workingSet);
                if (retained > 256L * 1024L * 1024L)
                    throw new InvalidOperationException("Managed memory retained more than 256 MiB after three map unloads: " + retained);
                Write("PASS; elapsedTicks=" + Find.TickManager.TicksGame + "; mapId=" + map.uniqueID);
                phase = Phase.Finished;
                Application.Quit();
                return;
            }

            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
            PlanetTile tile = neighbors.First(t => !Find.World.Impassable(t) && Find.WorldObjects.MapParentAt(t) == null);
            Map away = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(70, 1, 70), WorldObjectDefOf.Site);
            Pawn resident = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(resident, away.Center, away);
            MapParent parent = away.Parent;
            Current.Game.DeinitAndRemoveMap(away, false);
            if (parent != null && !parent.Destroyed) parent.Destroy();
            unloadCycle++;
            Write("map-unload; cycle=" + unloadCycle + "; tile=" + tile);
        }

        private static void Write(string text)
        {
            string line = DateTime.UtcNow.ToString("O") + " " + text;
            File.AppendAllText(resultPath, line + Environment.NewLine);
            Log.Message("[SearchlightLightingPerformance] " + text);
        }

        private static void Fail(Exception ex)
        {
            try { Write("FAIL; " + ex); } catch { }
            phase = Phase.Finished;
            Application.Quit();
        }
    }
}
