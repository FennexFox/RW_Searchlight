using System;
using System.Collections.Generic;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private const int SolarStep = 17;
        private const int HorizonLead = 250;
        private static readonly int[] horizonTicks = new int[2]; // Sunset, then sunrise.
        private static int solarWindow = -1;
        private static int scansAtSampleStart;
        private static bool differsFromFixedSchedule;
        private static bool preciseLightingRequested;
        private static bool preciseSunriseClearChecked;
        private static int preciseStandbyStage;
        private static readonly Dictionary<int, Color32> preciseBaselineGlow = new Dictionary<int, Color32>();
        private static readonly List<int> precisePreviousFieldCells = new List<int>();
        private static MethodInfo climateTick;
        private static MethodInfo sampleTile;
        private static FieldInfo solarElevation;

        private static void BeginSolar()
        {
            preciseLightingRequested = GenCommandLine.CommandLineArgPassed("ds-scenario-precise-lighting");
            const string solarNamespace = "AtmosphereOverhaul.Simulation.Global.";
            Type solar = Type.GetType(solarNamespace + "GlobalSolarUtility, AtmosphereOverhaul", true);
            climateTick = solar.GetMethod("ClimateTickFromVanillaAbsTick");
            sampleTile = solar.GetMethod("TrySample");
            solarElevation = solar.Assembly.GetType(solarNamespace + "GlobalSolarSample", true)
                .GetField("SolarElevationRadians");
            Write("AO assembly=" + solar.Assembly.FullName + "; mvid=" + solar.Assembly.ManifestModule.ModuleVersionId);

            // Sample AO's solar elevation directly, independently of the game's patched glow getter.
            int start = Find.TickManager.TicksAbs + HorizonLead;
            int previous = start;
            bool wasNight = ElevationAt(start) <= 0f;
            for (int tick = start + 200; tick < start + 60 * 60000; tick += 200)
            {
                bool night = ElevationAt(tick) <= 0f;
                if (night != wasNight && (night || horizonTicks[0] > 0))
                {
                    int crossing = previous + 1;
                    while ((ElevationAt(crossing) <= 0f) == wasNight) crossing++;
                    if (night) horizonTicks[0] = crossing;
                    else
                    {
                        horizonTicks[1] = crossing;
                        // At least one crossing must distinguish the two scheduling rules.
                        if (horizonTicks[1] - horizonTicks[0] > 2 * HorizonLead &&
                            (SameFixedScheduleAcross(horizonTicks[0]) || SameFixedScheduleAcross(crossing))) break;
                        horizonTicks[0] = horizonTicks[1] = 0;
                    }
                }
                previous = tick;
                wasNight = night;
            }
            Check(horizonTicks[1] > 0, "found distinct AO sunset and sunrise windows on this map");

            Thing generator = null;
            if (preciseLightingRequested)
            {
                SearchlightLightingGame lighting = Current.Game.GetComponent<SearchlightLightingGame>();
                Check(lighting != null, "game owns a SearchlightLightingGame component");
                lighting.SetMode(SearchlightLightingMode.PreciseField);
                Check(lighting.CurrentMode == SearchlightLightingMode.PreciseField,
                    "precise per-cell lighting mode is active");
                generator = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"));
                generator.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(generator, origin + new IntVec3(-1, 0, 0), Find.CurrentMap);
                CapturePreciseBaseline();
                Write("precise-lighting mode=" + lighting.CurrentMode + "; powered glow source is in baseline");
            }

            turret = SpawnLamp("DS_SearchlightTurret", origin);
            turret.SetFaction(Faction.OfPlayer);
            turret.SetMode(SearchlightMode.Automatic);
            turret.SetAlertMode(DetectionAlertMode.Off);
            SpawnTarget(origin + new IntVec3(12, 0, 0));
            if (preciseLightingRequested) turret.SetAimCell(target.Position);
            if (!preciseLightingRequested)
            {
                generator = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"));
                generator.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(generator, origin + new IntVec3(-1, 0, 0), Find.CurrentMap);
            }
            nightOnlyPower = turret.GetComp<CompPowerTrader>();
            nightOnlyGeneratorPower = generator.TryGetComp<CompPowerTrader>();
            if (preciseLightingRequested) PowerModeCommand(turret, "DS_AlwaysOn");
            else PowerModeCommand(turret, "DS_AlwaysOn").action();
            Next(SolarStep);
        }

        private static bool SameFixedScheduleAcross(int tick)
        {
            int local = GenLocalDate.DayTick(Find.CurrentMap) + tick - Find.TickManager.TicksAbs;
            int before = ((local - HorizonLead) % 60000) / 2500;
            int after = ((local + HorizonLead) % 60000) / 2500;
            return (before >= 6 && before < 18) == (after >= 6 && after < 18);
        }

        private static float ElevationAt(int tick)
        {
            object climate = climateTick.Invoke(null, new object[] { (long)tick });
            object[] args = { Find.CurrentMap.Tile, climate, null };
            Check((bool)sampleTile.Invoke(null, args), "AO samples the current map tile");
            return (float)solarElevation.GetValue(args[2]);
        }

        private static void CapturePreciseBaseline()
        {
            Map map = Find.CurrentMap;
            map.glowGrid.GlowGridUpdate_First();
            preciseBaselineGlow.Clear();
            for (int z = -6; z <= 6; z++)
            for (int x = -1; x <= 16; x++)
            {
                IntVec3 cell = origin + new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) continue;
                int index = map.cellIndices.CellToIndex(cell);
                preciseBaselineGlow[index] = map.glowGrid.VisualGlowAt(index);
            }
            Check(preciseBaselineGlow.Count > 0, "precise field probe region is on the map");
            Write("precise-baseline; cells=" + preciseBaselineGlow.Count);
        }

        private static void CapturePreciseField()
        {
            Map map = Find.CurrentMap;
            precisePreviousFieldCells.Clear();
            int intermediateCells = 0;
            foreach (KeyValuePair<int, Color32> baseline in preciseBaselineGlow)
            {
                Color32 current = map.glowGrid.VisualGlowAt(baseline.Key);
                if (SameGlow(current, baseline.Value)) continue;
                precisePreviousFieldCells.Add(baseline.Key);
                IntVec3 cell = map.cellIndices.IndexToCell(baseline.Key);
                if (Math.Abs(cell.x - turret.Position.x) >= 4 &&
                    Math.Abs(cell.x - turret.Position.x) <= 10 &&
                    Math.Abs(cell.z - turret.Position.z) <= 3 &&
                    target != null && cell.DistanceTo(target.Position) > 3f)
                {
                    intermediateCells++;
                }
            }

            Check(precisePreviousFieldCells.Count > 0,
                "the active precise beam changes accumulated glow in its probe region");
            Check(intermediateCells > 0,
                "the precise field illuminates intermediate cells beyond the endpoint light radius");
        }

        private static void CheckPreciseFieldCleared(string context)
        {
            Map map = Find.CurrentMap;
            int remaining = 0;
            foreach (KeyValuePair<int, Color32> baseline in preciseBaselineGlow)
            {
                if (!SameGlow(map.glowGrid.VisualGlowAt(baseline.Key), baseline.Value)) remaining++;
            }

            Check(remaining == 0,
                context + " restores every previously sampled cell (remaining=" + remaining +
                ", priorCells=" + precisePreviousFieldCells.Count + ")");
        }

        private static bool SameGlow(Color32 first, Color32 second)
        {
            return first.r == second.r && first.g == second.g && first.b == second.b && first.a == second.a;
        }

        private static void TickSolar(int age)
        {
            if (solarWindow < 0)
            {
                if (preciseLightingRequested && preciseStandbyStage == 1)
                {
                    if (age < 2) return;
                    Check(Power(turret) && !turret.IsOperating && PowerOutputIs(0f),
                        "Activate on detection enters powered 0 W standby after its last target is removed");
                    Check(!turret.SpotCell.IsValid, "Activate on detection removes the endpoint spot in standby");
                    CheckPreciseFieldCleared("Activate on detection standby");
                    SpawnTarget(origin + new IntVec3(12, 0, 0));
                    PowerModeCommand(turret, "DS_EnemyOnly").action();
                    PowerModeCommand(turret, "DS_AlwaysOn").action();
                    PowerModeCommand(turret, "DS_NightOnly");
                    preciseStandbyStage = 2;
                    Write("precise-enemy-standby PASS; priorCells=" + precisePreviousFieldCells.Count +
                        "; draw=" + nightOnlyPower.PowerOutput);
                    StartSolarWindow(0);
                    return;
                }

                if (age < 90) return;
                Check(age < 2400, "AO fixture connects to the power network");
                if (!nightOnlyPower.PowerOn || nightOnlyPower.PowerNet == null) return;
                CheckPowerNetworkWired();
                if (preciseLightingRequested && preciseStandbyStage == 0)
                {
                    Check(turret.LockedTarget == target && turret.IsOperating,
                        "Always on beam is aimed at the hostile before the standby transition");
                    Check(nightOnlyPower.PowerOutput == -nightOnlyPower.Props.PowerConsumption,
                        "Always on beam draws its configured watts before standby");
                    CapturePreciseField();
                    Check(!turret.SpotCell.IsValid,
                        "precise field mode does not retain the legacy endpoint glower");
                    PowerModeCommand(turret, "DS_AlwaysOn").action();
                    PowerModeCommand(turret, "DS_NightOnly").action();
                    PowerModeCommand(turret, "DS_EnemyOnly");
                    target.DeSpawn();
                    target = null;
                    preciseStandbyStage = 1;
                    Next(SolarStep);
                    return;
                }
                StartSolarWindow(0);
                return;
            }

            if (preciseLightingRequested && solarWindow == 1 &&
                age == HorizonLead - 2)
            {
                Check(ElevationAt(Find.TickManager.TicksAbs) <= 0f && turret.IsOperating &&
                    turret.LockedTarget == target,
                    "Night Only still tracks the in-range enemy immediately before AO sunrise");
                CapturePreciseField();
            }
            if (preciseLightingRequested && solarWindow == 1 &&
                age >= HorizonLead + 2 && !preciseSunriseClearChecked)
            {
                Check(ElevationAt(Find.TickManager.TicksAbs) > 0f,
                    "AO solar elevation has crossed sunrise");
                Check(target != null && target.Spawned && target.Position.DistanceTo(turret.Position) <= 60f,
                    "hostile remains in range across AO sunrise");
                Check(Power(turret) && !turret.IsOperating && PowerOutputIs(0f),
                    "Night Only draws zero watts on the first update after AO sunrise");
                Check(!turret.SpotCell.IsValid && turret.LockedTarget == null,
                    "Night Only stops aim and removes its endpoint on the first update after AO sunrise");
                CheckPreciseFieldCleared("first update after AO sunrise");
                preciseSunriseClearChecked = true;
                Write("precise-sunrise-clear PASS; priorCells=" + precisePreviousFieldCells.Count +
                    "; watts=" + -nightOnlyPower.PowerOutput);
            }
            // The map ticks naturally across the horizon. Allow time for scans, aiming and glow-grid updates.
            if (age == 100 || age == HorizonLead + 100) scansAtSampleStart = turret.AcquisitionCount;
            if (age != 200 && age != HorizonLead + 200) return;
            bool after = age > HorizonLead;
            bool night = solarWindow == 0 ? after : !after;
            float elevation = ElevationAt(Find.TickManager.TicksAbs);
            int scans = turret.AcquisitionCount - scansAtSampleStart;
            float glow = GroundGlow(target.Position);
            int hour = GenLocalDate.HourOfDay(Find.CurrentMap);
            differsFromFixedSchedule |= night != (hour < 6 || hour >= 18);
            Write($"AO {(solarWindow == 0 ? "sunset" : "sunrise")} {(after ? "after" : "before")}; " +
                $"hour={hour}; elevationRadians={elevation:F6}; scans={scans}; watts={-nightOnlyPower.PowerOutput}; " +
                $"spot={turret.SpotCell}; target={target.Position}; artificialGlow={glow}");
            Check(night == (elevation <= 0f), "window straddles AO's solar horizon");
            Check((night ? scans > 0 : scans == 0) && turret.LockedTarget == (night ? target : null),
                "Night only gates scanning and target lock at the horizon");
            Check(nightOnlyPower.PowerOn && nightOnlyPower.PowerOutput == (night ? -nightOnlyPower.Props.PowerConsumption : 0f),
                "Night only gates power draw on the supplied network");
            if (preciseLightingRequested)
            {
                bool fieldGlow = GroundGlow(origin + new IntVec3(6, 0, 0)) > 0.01f;
                Check(turret.IsOperating == night && !turret.SpotCell.IsValid &&
                    (glow > 0.01f) == night && fieldGlow == night,
                    "Night Only gates the precise beam field and its artificial ground glow");
                if (solarWindow == 1 && !after && night) CapturePreciseField();
                if (solarWindow == 1 && after)
                    Check(preciseSunriseClearChecked, "precise field cleared on the first update after sunrise");
            }
            else
            {
                Check(turret.IsOperating == night && turret.SpotCell == (night ? target.Position : IntVec3.Invalid) &&
                    (glow > 0.01f) == night, "Night only gates the endpoint and its artificial ground glow");
            }
            if (!after) return;
            if (solarWindow == 0) StartSolarWindow(1);
            else
            {
                Check(differsFromFixedSchedule, "observed AO behavior differs from the vanilla fixed schedule");
                Finish(true);
            }
        }

        private static void StartSolarWindow(int index)
        {
            solarWindow = index;
            // Move the calendar epoch without skipping simulation ticks used by AO's background work.
            Find.TickManager.gameStartAbsTick = horizonTicks[index] - HorizonLead - Find.TickManager.TicksGame;
            Next(SolarStep);
        }
    }
}
