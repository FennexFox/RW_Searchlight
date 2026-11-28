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
    public sealed class GameplayLightingCheck : GameComponent
    {
        private const string SaveName = "searchlight-lighting";
        // Vanilla PowerNet can wait up to 200 simulation ticks to restart one consumer.
        private const int PowerRestartTicks = 220;
        private readonly string output;
        private readonly string runPhase;
        private readonly bool precise;
        private readonly bool requireCe;
        private readonly Queue<(string name, int ticks, Action action, Func<bool> ready)> steps = new();

        private Map map;
        private SearchlightLightingGame lighting;
        private IntVec3 origin;
        private Building_Searchlight lamp;
        private Building_Door door;
        private Building wall;
        private Pawn tracked;
        private Job diagonalWalk;
        private Pawn ceShooter;
        private Pawn ceTarget;
        private MethodInfo ceCombatGlow;
        private MethodInfo ceLightingShift;
        private object ceTracker;
        private int nextTick;
        private int nextFrame;
        private Action tickCheck;
        private int tickDeadline;
        private bool finished;
        private bool fixtureSaved;
        private byte expectedSavedLight;
        private string darkMidMesh;
        private readonly (float glow, float shift)[] ceUnaidedOn = new (float, float)[4];
        private readonly (float glow, float shift)[] ceUnaidedOff = new (float, float)[4];
        private readonly (float glow, float shift)[] ceGogglesOff = new (float, float)[4];
        private float previousHeading;
        private int intermediateDarkTicks;
        private int diagonalTicks;

        private SearchlightLightingMode RequestedMode => precise
            ? SearchlightLightingMode.PreciseField
            : SearchlightLightingMode.LegacyEndpoint;

        private IntVec3 TorchCell => East(22, -8);
        private static int Tick => Find.TickManager.TicksGame;

        public GameplayLightingCheck(Game game)
        {
            if (GenCommandLine.CommandLineArgPassed("ds-pawn-shadow-output"))
                return;
            if (!GenCommandLine.TryGetCommandLineArg("ds-lighting-mode", out string mode))
                return;
            precise = mode == "precise";
            GenCommandLine.TryGetCommandLineArg("ds-lighting-output", out output);
            GenCommandLine.TryGetCommandLineArg("ds-lighting-phase", out runPhase);
            requireCe = GenCommandLine.CommandLineArgPassed("ds-lighting-require-ce");
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref fixtureSaved, "fixtureSaved");
            Scribe_Values.Look(ref origin, "origin");
            Scribe_Values.Look(ref expectedSavedLight, "expectedSavedLight");
            Scribe_References.Look(ref lamp, "lamp");
        }

        public override void GameComponentUpdate()
        {
            if (output == null || finished || Find.CurrentMap == null || Current.ProgramState != ProgramState.Playing)
                return;
            try
            {
                if (map == null)
                {
                    Setup(Find.CurrentMap);
                    return;
                }
                if (tickCheck != null || Tick < nextTick || Time.frameCount < nextFrame || steps.Count == 0)
                    return;
                var ready = steps.Peek().ready;
                if (ready != null && !ready() && Tick < nextTick + PowerRestartTicks)
                {
                    nextFrame = Time.frameCount + 2;
                    return;
                }
                var step = steps.Dequeue();
                Record($"step={step.name}; tick={Tick}; sky={map.skyManager.CurSkyGlow:F4}");
                step.action();
                Schedule(step.ticks);
            }
            catch (Exception ex)
            {
                Finish("FAIL " + ex);
            }
        }

        public override void GameComponentTick()
        {
            if (tickCheck == null || finished)
                return;
            try
            {
                Check(Tick <= tickDeadline, "tracking phase finished within its bounded tick window");
                tickCheck();
            }
            catch (Exception ex)
            {
                Finish("FAIL " + ex);
            }
        }

        private void Setup(Map currentMap)
        {
            map = currentMap;
            var assembly = typeof(Building_Searchlight).Assembly;
            Record($"game={VersionControl.CurrentVersionString}; phase={runPhase}; mode={RequestedMode}; assemblyMvid={assembly.ManifestModule.ModuleVersionId:D}");
            var harmony = ModLister.GetModWithIdentifier("brrainz.harmony");
            Record("Harmony modVersion=" + harmony?.ModVersion);
            var ce = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CombatExtended");
            Check((ce != null) == requireCe, "loaded CE matches requested case");
            if (ce != null)
            {
                var type = ce.GetType("CombatExtended.LightingTracker", throwOnError: true);
                ceTracker = map.GetComponent(type);
                ceCombatGlow = type.GetMethod("CombatGlowAtFor");
                ceLightingShift = ce.GetType("CombatExtended.CE_Utility", throwOnError: true).GetMethod("GetLightingShift");
                Record("CE modVersion=" + ModLister.GetModWithIdentifier("ceteam.combatextended")?.ModVersion);
            }
            lighting = Current.Game.GetComponent<SearchlightLightingGame>();
            Check(lighting != null, "game has a searchlight lighting mode component");
            Check(fixtureSaved == (runPhase == "reload"), "runner opened the expected fresh or saved gameplay fixture");
            if (fixtureSaved)
            {
                ExcludeRandomBreakdown(lamp);
                PointCamera();
                Queue("reload", VerifyReload);
            }
            else
            {
                Check(lighting.CurrentMode == SearchlightLightingMode.LegacyEndpoint, "new game starts in legacy mode");
                lighting.SetMode(RequestedMode);
                Check(lighting.CurrentMode == RequestedMode, "requested gameplay lighting mode is active");
                origin = map.Center + new IntVec3(-45, 0, 0);
                ClearArea(new CellRect(origin.x - 13, origin.z - 15, 96, 52));
                map.weatherManager.TransitionTo(WeatherDefOf.Clear);
                map.weatherManager.curWeatherAge = (int)WeatherManager.TransitionTicks;
                SetHour(1);
                PointCamera();
                Queue("dark-baseline", () =>
                {
                    Check(map.skyManager.CurSkyGlow < 0.02f, "night fixture has low sky glow");
                    ExpectDark(East(15), "mid-beam baseline");
                    ExpectDark(East(30), "endpoint baseline");
                    darkMidMesh = LightingCheck.Mesh(map, East(15));
                    lamp = SpawnLamp(origin);
                }, ticks: PowerRestartTicks);
                // Precise mode now publishes only pawn-local samples. Its former
                // full-beam coverage and overlap suite is covered by PawnShadowHarness.
                if (!precise)
                {
                    ScheduleCoverage();
                    ScheduleBlockers();
                }
                ScheduleTracking();
                if (requireCe && !precise)
                    ScheduleCe();
                ScheduleModeAndSave();
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Fast;
            Schedule(30);
        }

        private void ScheduleCoverage()
        {
            foreach (int range in new[] { 5, 10, 30, 60 })
            foreach (int width in new[] { 6, 4, 2 })
            {
                Queue($"aim-{range}x{width}", () =>
                {
                    SetDiameter(width);
                    lamp.SetAimCell(East(range));
                }, ticks: 55);
                Queue($"coverage-{range}x{width}", () =>
                {
                    Check(AimSettled(range),
                        $"powered turret settled at requested range and east heading: expected range={range}, {LampState(lamp)}");
                    ExpectLit(East(range), "aim center");
                    IntVec3 mid = East(range / 2);
                    ExpectProvider(lamp, SearchlightLightingMode.LegacyEndpoint);
                    Check(lamp.SpotCell == East(range), "legacy spot reached the aim cell");
                    if (range >= 10)
                        ExpectDark(mid, "legacy mid-beam comparison");
                    Record($"coverage; range={range}; width={width}; center={Describe(East(range))}; mid={Describe(mid)}; outside={Describe(East(range / 2, 5))}; past={Describe(East(range + 1))}");
                }, ready: () => AimSettled(range) && lamp.SpotCell.IsValid);
            }
            Queue("back-to-default-width", () =>
            {
                SetDiameter(4);
                lamp.SetAimCell(East(30));
            });
            Queue("gameplay-mesh", () =>
            {
                bool meshChanged = LightingCheck.Mesh(map, East(15)) != darkMidMesh;
                Check(!meshChanged, "legacy endpoint lighting leaves the real mid-beam mesh unchanged");
                Record("mid-beam-mesh=" + LightingCheck.Mesh(map, East(15)));
            });
        }

        private void ScheduleBlockers()
        {
            Queue("place-wall-at-30", () =>
            {
                lamp.SetAimCell(East(60));
                wall = SpawnWall(East(30));
            });
            Queue("wall-clipping", () =>
            {
                ExpectDark(East(45), "behind wall");
                ExpectDark(East(60), "far target behind wall");
                Check(lamp.SpotCell == East(29), "legacy spot stops before wall");
                Record($"wall; before={Describe(East(29))}; blocker={Describe(East(30))}; behind={Describe(East(31))}");
                wall.Destroy();
            });
            Queue("wall-demolition", () =>
            {
                ExpectLit(East(60), "demolished wall let the beam reach its aim");
                door = (Building_Door)GenSpawn.Spawn(
                    ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Door"), ThingDefOf.Steel), East(30), map);
            });
            Queue("closed-door", () =>
            {
                Check(!door.Open, "door initially closed");
                ExpectDark(East(60), "closed door blocks the distant beam");
                door.StartManualOpenBy(null);
            }, ticks: 80);
            Queue("open-door", () =>
            {
                Check(door.Open, "door became open");
                ExpectLit(East(60), "open door transmits the beam");
                door.Destroy();
            });
        }

        private void ScheduleTracking()
        {
            Queue("set-north", () =>
            {
                SetDiameter(6);
                lamp.SetMode(SearchlightMode.Fixed);
                lamp.SetAimCell(East(0, 30));
                IsolateTrackingYard();
                tracked = SpawnPawn(East(30), Faction.OfAncientsHostile);
                tracked.equipment?.DestroyAllEquipment();
                Hold(tracked);
            }, ticks: 90);
            Queue("begin-turn-to-target", () =>
            {
                ExpectDark(tracked.Position, "target east stays dark while the beam faces north");
                lamp.SetMode(SearchlightMode.Automatic);
                lamp.SelectManualTarget(tracked);
                Check(lamp.LockedTarget == tracked, "manual lock acquired before rotation");
                previousHeading = lamp.AimHeading;
                StartTickCheck(CheckTurning, 100);
            });
            Queue("rotation-settled", () =>
            {
                Check(intermediateDarkTicks >= 20, "tracked rotation included intermediate dark ticks");
                ExpectLit(tracked.Position, "target only lights after beam reaches it");
                Record($"turning; intermediateDarkTicks={intermediateDarkTicks}; target={Describe(tracked.Position)}");
                diagonalWalk = JobMaker.MakeJob(JobDefOf.Goto, East(36, 6));
                diagonalWalk.playerForced = true;
                tracked.jobs.StartJob(diagonalWalk, JobCondition.InterruptForced);
                StartTickCheck(CheckDiagonalWalk, 800);
            });
            Queue("tracking-settled", () =>
            {
                Check(diagonalTicks > 0, "pawn walked diagonally with subcell aim samples");
                Check(lamp.LockedTarget == tracked, "tracking lock persisted throughout the diagonal walk");
                ExpectLit(tracked.Position, "settled target is lit after the diagonal walk");
                Record($"walking; interpolatedTicks={diagonalTicks}; finalCell={tracked.Position}; aim={lamp.AimHeading:F3},{lamp.AimRange:F3}");
                tracked.DeSpawn();
                GenSpawn.Spawn(tracked, East(30), map);
                Hold(tracked);
                lamp.SetMode(SearchlightMode.Fixed);
                lamp.SetAimCell(East(30));
            });
        }

        private void CheckTurning()
        {
            float turn = Mathf.DeltaAngle(previousHeading, lamp.AimHeading);
            float remaining = Mathf.Abs(Mathf.DeltaAngle(lamp.AimHeading, 90f));
            Check(turn >= -0.001f && turn <= 2.001f, "tracking keeps the 2 degree per-tick turn cap");
            CheckTrackingLock("rotation keeps the acquired target");
            previousHeading = lamp.AimHeading;
            if (remaining > 6f)
            {
                ExpectDark(tracked.Position, "tracking has not yet reached the target");
                intermediateDarkTicks++;
            }
            if (remaining < 0.01f)
                EndTickCheck();
        }

        private void CheckDiagonalWalk()
        {
            if (tracked.Position == East(36, 6))
            {
                Hold(tracked);
                EndTickCheck();
                return;
            }
            CheckTrackingLock("beam maintains a lock during the diagonal walk");
            Check(tracked.CurJob == diagonalWalk,
                $"the actual pawn stayed on the requested diagonal walk, tick={Tick}, cell={tracked.Position}, job={tracked.CurJobDef}");
            var path = tracked.pather;
            IntVec3 delta = path.nextCell - tracked.Position;
            float progress = path.MovePercentage;
            if (!path.Moving || delta.x == 0 || delta.z == 0 || progress <= 0.15f || progress >= 0.85f)
                return;

            Vector3 here = Vector3.Lerp(tracked.Position.ToVector3Shifted(), path.nextCell.ToVector3Shifted(), progress);
            Vector3 offset = here - lamp.Position.ToVector3Shifted();
            var expectedHeading = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            Check(Mathf.Abs(Mathf.DeltaAngle(lamp.AimHeading, expectedHeading)) < 0.3f &&
                Mathf.Abs(lamp.AimRange - offset.magnitude) < 0.2f, "aim follows the fractional diagonal pawn position");
            IntVec3 mid = lamp.Position + new IntVec3(Mathf.RoundToInt(offset.x * 0.55f), 0, Mathf.RoundToInt(offset.z * 0.55f));
            ExpectDark(mid, "pawn-only lighting keeps the empty mid-beam cell dark");
            diagonalTicks++;
        }

        private void CheckTrackingLock(string label)
        {
            if (lamp.LockedTarget == tracked)
                return;
            bool onMap = tracked.Spawned && tracked.Map == map;
            bool visible = onMap && !tracked.Fogged() && BeamField.Clear(map, lamp.Position, tracked.Position);
            throw new InvalidOperationException($"{label}. Tick={Tick}, position={tracked.Position}, " +
                $"spawned={onMap}, dead={tracked.Dead}, downed={tracked.Downed}, hostile={tracked.HostileTo(Faction.OfPlayer)}, " +
                $"visible={visible}, invisible={tracked.IsPsychologicallyInvisible()}, distance={tracked.Position.DistanceTo(lamp.Position):F2}, " +
                $"job={tracked.CurJobDef}, moving={tracked.pather.Moving}, lampOperating={lamp.IsOperating}, lock={lamp.LockedTarget}");
        }

        private void IsolateTrackingYard()
        {
            // The quicktest colony arrives with weapons and wildlife is randomized.
            // Keep its colonists safely out of the way while the real hostile pawn walks.
            int i = 0;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToArray())
            {
                pawn.DeSpawn();
                if (!pawn.IsColonist) continue;
                pawn.equipment?.DestroyAllEquipment();
                GenSpawn.Spawn(pawn, East(-10, 27 + i++), map);
                pawn.drafter.Drafted = true;
                pawn.drafter.FireAtWill = false;
                Hold(pawn);
            }
            Record($"tracking yard isolated; parkedColonists={i}");
        }

        private void ScheduleCe()
        {
            Queue("ce-fixture", () =>
            {
                ceShooter = SpawnPawn(East(-9), Faction.OfPlayer);
                ceTarget = SpawnPawn(CeTargets()[0], Faction.OfPlayer);
                ceShooter.apparel.DestroyAll();
                Hold(ceShooter);
                Hold(ceTarget);
            });
            Queue("ce-unaided-on", () =>
            {
                SampleCe("unaided-on", ceUnaidedOn);
                Flick(lamp, false);
            });
            Queue("ce-unaided-off", () =>
            {
                SampleCe("unaided-off", ceUnaidedOff);
                var def = DefDatabase<ThingDef>.GetNamed("CE_Apparel_AdvancedNightVision");
                ceShooter.apparel.Wear((Apparel)ThingMaker.MakeThing(def), false);
                Check(ceShooter.apparel.WornApparel.Any(apparel => apparel.def == def), "CE shooter actually equipped the goggles");
            });
            Queue("ce-goggles-off", () =>
            {
                SampleCe("goggles-off", ceGogglesOff);
                Flick(lamp, true);
            }, ticks: PowerRestartTicks);
            Queue("ce-goggles-on", () =>
            {
                var equippedOn = new (float glow, float shift)[4];
                SampleCe("goggles-on", equippedOn);
                Check(ceUnaidedOn[0].glow > ceUnaidedOff[0].glow &&
                    ceUnaidedOn[0].shift < ceUnaidedOff[0].shift, "CE uses lit center for the same unaided shooter and target");
                Check(ceUnaidedOn[1].glow > ceUnaidedOff[1].glow &&
                    ceUnaidedOn[1].shift < ceUnaidedOff[1].shift, "CE uses lit edge and neighborhood");
                Check(Near(ceUnaidedOn[2].glow, ceUnaidedOff[2].glow), "remote edge comparison is unaffected by the beam");
                for (int i = 0; i < equippedOn.Length; i++)
                {
                    Check(Near(equippedOn[i].glow, ceUnaidedOn[i].glow) &&
                        Near(ceGogglesOff[i].glow, ceUnaidedOff[i].glow), "goggles change CE's shooter penalty without changing combat glow");
                    float efficiencyFactor = ceGogglesOff[i].shift / ceUnaidedOff[i].shift;
                    Check(efficiencyFactor < 1f && Near(equippedOn[i].shift, ceUnaidedOn[i].shift * efficiencyFactor),
                        "CE applied the same night-vision factor once with the light on or off");
                }
            });
        }

        private IntVec3[] CeTargets() => new[] { East(30), East(30, 2), East(30, 5), East(30, 3) };

        private void SampleCe(string label, (float glow, float shift)[] samples)
        {
            var positions = CeTargets();
            for (int i = 0; i < positions.Length; i++)
            {
                if (ceTarget.Position != positions[i])
                {
                    ceTarget.DeSpawn();
                    GenSpawn.Spawn(ceTarget, positions[i], map);
                    Hold(ceTarget);
                }
                float glow = (float)ceCombatGlow.Invoke(ceTracker, new object[] { ceShooter.Position, ceTarget.Position });
                float shift = (float)ceLightingShift.Invoke(null, new object[] { ceShooter, glow });
                samples[i] = (glow, shift);
                Record($"ce={label}; probe={i}; shooter={ceShooter.thingIDNumber}; target={ceTarget.thingIDNumber}; cell={ceTarget.Position}; gameplay={Describe(ceTarget.Position)}; combatGlow={glow:F4}; lightingShift={shift:F4}");
            }
        }

        private void ScheduleModeAndSave()
        {
            Queue("alternate-mode", () =>
            {
                SetDiameter(4);
                lamp.SetMode(SearchlightMode.Fixed);
                lamp.SetAimCell(East(30));
                lighting.SetMode(precise ? SearchlightLightingMode.LegacyEndpoint : SearchlightLightingMode.PreciseField);
            });
            Queue("alternate-provider-live", () =>
            {
                var alternate = precise ? SearchlightLightingMode.LegacyEndpoint : SearchlightLightingMode.PreciseField;
                Check(lighting.CurrentMode == alternate, "transaction switched to the alternate mode");
                ExpectProvider(lamp, alternate);
                ExpectLit(East(30), "alternate provider lights the endpoint");
                ExpectDark(East(15), "empty mid-beam cell remains dark in both endpoint and pawn-local modes");
                Record($"alternateMode={alternate}; endpoint={Describe(East(30))}; mid={Describe(East(15))}");
                lighting.SetMode(RequestedMode);
                SetHour(12);
            });
            Queue("daylight-with-restored-provider", () =>
            {
                float sky = map.skyManager.CurSkyGlow;
                Check(sky > 0.55f && Near(map.glowGrid.GroundGlowAt(East(15)), sky), "active provider preserves natural daylight");
                Check(lighting.CurrentMode == RequestedMode, "transaction restored requested mode");
                ExpectProvider(lamp, RequestedMode);
                ExpectLit(East(30), "selected provider keeps the held pawn cell lit at noon");
                ExpectDark(East(15), "selected provider leaves empty mid-beam ground dark at noon");
                SetHour(1);
            });
            Queue("save", () =>
            {
                Check(map.skyManager.CurSkyGlow < 0.02f, "fixture returned to night");
                Check(lamp.IsOperating && lamp.AimRange == 30f, "saving an active fixed-aim searchlight");
                ExpectProvider(lamp, RequestedMode);
                ExpectLit(East(30), "saving the live provider");
                ExpectDark(East(15), "saving does not create illumination on empty mid-beam ground");
                expectedSavedLight = ColorAt(East(30)).r;
                fixtureSaved = true;
                Record($"save; mode={RequestedMode}; endpoint={Describe(East(30))}; mid={Describe(East(15))}");
                GameDataSaveLoader.SaveGame(SaveName);
                Check(new FileInfo(GenFilePaths.FilePathForSavedGame(SaveName)).Length > 0, "saved gameplay fixture");
                Finish("PASS " + runPhase);
            });
        }

        private void VerifyReload()
        {
            Check(lighting.CurrentMode == RequestedMode, "saved lighting mode reloaded without reapplying it");
            Check(lamp != null && lamp.Spawned && lamp.Position == origin && lamp.IsOperating &&
                lamp.CurrentMode == SearchlightMode.Fixed && lamp.AimRange == 30f, "saved lamp resumed powered fixed aim");
            ExpectProvider(lamp, RequestedMode);
            ExpectLit(East(30), "saved provider rebuilt light at the held pawn's beam cell");
            ExpectDark(East(15), "saved sparse provider leaves empty mid-beam ground dark");
            Check(ColorAt(East(30)).r == expectedSavedLight, "pawn-local or endpoint glow rebuilt after reload");
            Record($"reload; mode={lighting.CurrentMode}; endpoint={Describe(East(30))}; mid={Describe(East(15))}");
            lamp.Destroy();
            Queue("reload-despawn", () =>
            {
                ExpectDark(East(30), "despawning the loaded lamp removes its endpoint illumination");
                ExpectDark(East(15), "despawning the loaded lamp leaves empty mid-beam ground dark");
                Finish("PASS " + runPhase);
            });
        }

        private Building_Searchlight SpawnLamp(IntVec3 cell, bool installGenerator = true)
        {
            var turret = DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret");
            var source = (Building_Searchlight)GenSpawn.Spawn(turret, cell, map);
            ExcludeRandomBreakdown(source);
            if (installGenerator)
                GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"), cell + IntVec3.West, map);
            source.SetAlertMode(DetectionAlertMode.Off);
            source.SetMode(SearchlightMode.Fixed);
            return source;
        }

        private void ExcludeRandomBreakdown(Building_Searchlight source)
        {
            // Leave the real power network in control of the fixture lamps, but
            // keep the unrelated random breakdown roll out of lighting tests.
            var breakdown = source?.GetComp<CompBreakdownable>();
            if (breakdown != null)
                map.GetComponent<BreakdownManager>().Deregister(breakdown);
        }

        private static string LampState(Building_Searchlight source)
        {
            var power = source.GetComp<CompPowerTrader>();
            return $"actual range={source.AimRange:F3}, heading={source.AimHeading:F3}, " +
                $"spawned={source.Spawned}, operating={source.IsOperating}, powered={power.PowerOn}, " +
                $"connected={power.PowerNet != null}, flickedOn={source.GetComp<CompFlickable>().SwitchIsOn}, " +
                $"broken={source.GetComp<CompBreakdownable>().BrokenDown}";
        }

        private bool AimSettled(int range) => lamp.IsOperating && Near(lamp.AimRange, range) &&
            Mathf.Abs(Mathf.DeltaAngle(lamp.AimHeading, 90f)) < 0.01f;

        private Building SpawnWall(IntVec3 cell) =>
            (Building)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), cell, map);

        private Pawn SpawnPawn(IntVec3 position, Faction faction)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, faction);
            GenSpawn.Spawn(pawn, position, map);
            if (faction == Faction.OfPlayer)
                pawn.drafter.Drafted = true;
            return pawn;
        }

        private static void Hold(Pawn pawn)
        {
            pawn.pather.StopDead();
            var hold = JobMaker.MakeJob(JobDefOf.Wait);
            hold.expiryInterval = 500000;
            hold.playerForced = true;
            pawn.jobs.StartJob(hold, JobCondition.InterruptForced);
        }

        private void ClearArea(CellRect bounds)
        {
            bounds.ClipInsideMap(map);
            foreach (IntVec3 cell in bounds)
            {
                map.roofGrid.SetRoof(cell, null);
                if (cell.Fogged(map))
                    map.fogGrid.Unfog(cell);
                foreach (var thing in cell.GetThingList(map).ToArray())
                    if (thing is Plant || thing.def.passability == Traversability.Impassable ||
                        thing.TryGetComp<CompGlower>() != null)
                        thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
        }

        private void SetHour(int hour)
        {
            for (int i = 0; i < 24 && GenLocalDate.HourOfDay(map) != hour; i++)
                Find.TickManager.DebugSetTicksGame(Tick + 2500);
            Check(GenLocalDate.HourOfDay(map) == hour, "fixture reached requested local hour");
        }

        private static void SetDiameter(int width)
        {
            var mod = LoadedModManager.GetMod<SearchlightsMod>();
            var settings = mod.GetSettings<SearchlightSettings>();
            var field = typeof(SearchlightSettings).GetField("diameter", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(settings, (float)width);
            Check(Near(SearchlightSettings.Diameter, width), "fixture set the beam width");
        }

        private void PointCamera() => Find.CameraDriver.SetRootPosAndSize(East(25).ToVector3Shifted(), 30f);

        private void ExpectProvider(Building_Searchlight source, SearchlightLightingMode mode)
        {
            bool usesSpots = mode == SearchlightLightingMode.LegacyEndpoint;
            Check(source.SpotCell.IsValid == usesSpots, "lamp has the expected endpoint-provider state");
            int spots = map.listerThings.AllThings.Count(thing => thing.def.defName == "DS_LightSpot");
            Check(spots == (usesSpots ? 1 : 0), $"game has the expected number of endpoint lights, actual={spots}");
        }

        private static void Flick(Building_Searchlight source, bool on) =>
            source.GetComp<CompFlickable>().SwitchIsOn = on;

        private void ExpectDark(IntVec3 cell, string label)
        {
            var color = ColorAt(cell);
            Check(Near(Ground(cell), 0f) && color.r == 0 && color.g == 0 && color.b == 0,
                $"{label}: expected dark at {cell}, observed {Describe(cell)}");
        }

        private void ExpectLit(IntVec3 cell, string label) =>
            Check(Ground(cell) > 0f && ColorAt(cell).r > 0,
                $"{label}: expected light at {cell}, observed {Describe(cell)}");

        private IntVec3 East(int x, int z = 0) => origin + new IntVec3(x, 0, z);
        private float Ground(IntVec3 cell) => map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
        private Color32 ColorAt(IntVec3 cell) => map.glowGrid.VisualGlowAt(cell);
        private string Describe(IntVec3 cell) => $"{Ground(cell):F4}/{ColorAt(cell)}";

        private void Queue(string name, Action action, int ticks = 40, Func<bool> ready = null) =>
            steps.Enqueue((name, ticks, action, ready));

        private void Schedule(int ticks)
        {
            nextTick = Tick + ticks;
            nextFrame = Time.frameCount + 5;
        }

        private void StartTickCheck(Action action, int maxTicks)
        {
            tickCheck = action;
            tickDeadline = Tick + maxTicks;
        }

        private void EndTickCheck()
        {
            tickCheck = null;
            Schedule(30);
        }

        private void Record(string line)
        {
            File.AppendAllText(output, line + Environment.NewLine);
            Log.Message("SearchlightGameplayLightingHarness: " + line);
        }

        private void Finish(string result)
        {
            finished = true;
            Record(result);
            Application.Quit(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
