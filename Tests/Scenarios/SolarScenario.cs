using System;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
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
        private static MethodInfo climateTick;
        private static MethodInfo sampleTile;
        private static FieldInfo solarElevation;

        private static void BeginSolar()
        {
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

            turret = SpawnLamp("DS_SearchlightTurret", origin);
            turret.SetFaction(Faction.OfPlayer);
            turret.SetMode(SearchlightMode.Automatic);
            turret.SetAlertMode(DetectionAlertMode.Off);
            SpawnTarget(origin + new IntVec3(12, 0, 0));
            var generator = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"));
            generator.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(generator, origin + new IntVec3(-1, 0, 0), Find.CurrentMap);
            nightOnlyPower = turret.GetComp<CompPowerTrader>();
            nightOnlyGeneratorPower = generator.TryGetComp<CompPowerTrader>();
            PowerModeCommand(turret, "DS_AlwaysOn").action();
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

        private static void TickSolar(int age)
        {
            if (solarWindow < 0)
            {
                if (age < 90) return;
                Check(age < 2400, "AO fixture connects to the power network");
                if (!nightOnlyPower.PowerOn || nightOnlyPower.PowerNet == null) return;
                CheckPowerNetworkWired();
                StartSolarWindow(0);
                return;
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
            Check(turret.IsOperating == night && turret.SpotCell == (night ? target.Position : IntVec3.Invalid) &&
                (glow > 0.01f) == night, "Night only gates the endpoint and its artificial ground glow");
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
