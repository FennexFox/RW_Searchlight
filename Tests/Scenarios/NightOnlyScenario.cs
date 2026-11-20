using System;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using Verse;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private enum NightOnlyPhase { DefaultDaytime, EnabledDaytime, Dusk, BeforeDawn, Dawn, PowerLoss }

        private const int NightOnlyStep = 15;
        private static NightOnlyPhase nightOnlyPhase;
        private static IntVec3 nightOnlyAim;
        private static CompPowerTrader nightOnlyPower;
        private static float normalPowerOutput;

        private static void BeginNightOnly()
        {
            turret = SpawnLamp("DS_SearchlightTurret", origin);
            nightOnlyAim = origin + new IntVec3(12, 0, 0);
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(nightOnlyAim);
            nightOnlyPower = turret.GetComp<CompPowerTrader>();
            SetNightOnlyPhase(NightOnlyPhase.DefaultDaytime, 17);
        }

        private static void TickNightOnly()
        {
            switch (nightOnlyPhase)
            {
                case NightOnlyPhase.DefaultDaytime:
                    var toggle = turret.GetGizmos().OfType<Command_Toggle>()
                        .Single(g => g.defaultLabel == "DS_NightOnly".Translate());
                    Check(!toggle.isActive(), "Night Only starts disabled");
                    normalPowerOutput = nightOnlyPower.PowerOutput;
                    Check(normalPowerOutput < 0f, "default operation consumes power during the day");
                    CheckNightOnlyLight(true);
                    toggle.toggleAction();
                    Check(toggle.isActive(), "Night Only command enables the setting");
                    SetNightOnlyPhase(NightOnlyPhase.EnabledDaytime, 17);
                    break;
                case NightOnlyPhase.EnabledDaytime:
                    CheckDaytimeStandby();
                    SetNightOnlyPhase(NightOnlyPhase.Dusk, 18);
                    break;
                case NightOnlyPhase.Dusk:
                    CheckNightOnlyLight(true);
                    Check(nightOnlyPower.PowerOutput == normalPowerOutput, "normal power draw resumes at 18:00");
                    SetNightOnlyPhase(NightOnlyPhase.BeforeDawn, 5);
                    break;
                case NightOnlyPhase.BeforeDawn:
                    CheckNightOnlyLight(true);
                    SetNightOnlyPhase(NightOnlyPhase.Dawn, 6);
                    break;
                case NightOnlyPhase.Dawn:
                    CheckDaytimeStandby();
                    nightOnlyPower.PowerOn = false;
                    SetNightOnlyPhase(NightOnlyPhase.PowerLoss, 18);
                    break;
                case NightOnlyPhase.PowerLoss:
                    Check(!nightOnlyPower.PowerOn, "dusk does not restore lost power");
                    CheckNightOnlyLight(false);
                    Write("night-only PASS (default off, standby at 06:00, resumes at 18:00, respects power loss)");
                    Finish(true);
                    break;
            }
        }

        private static void CheckDaytimeStandby()
        {
            Check(nightOnlyPower.PowerOn && nightOnlyPower.PowerOutput == 0f,
                "daytime standby has supply available and does not draw power");
            CheckNightOnlyLight(false);
        }

        private static void CheckNightOnlyLight(bool operating)
        {
            string context = " at local hour " + GenLocalDate.HourOfDay(Find.CurrentMap);
            Check(turret.IsOperating == operating, "Night Only operation" + context);
            Check(turret.SpotCell == (operating ? nightOnlyAim : IntVec3.Invalid), "Night Only spot" + context);
            Check((GroundGlow(nightOnlyAim) > 0.01f) == operating, "Night Only ground glow" + context);
        }

        private static void SetNightOnlyPhase(NightOnlyPhase phase, int hour)
        {
            Map map = Find.CurrentMap;
            const int ticksPerHour = 2500;
            const int ticksPerDay = 24 * ticksPerHour;
            int ticksAhead = (hour * ticksPerHour - GenLocalDate.DayTick(map) + ticksPerDay) % ticksPerDay;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticksAhead);
            Check(GenLocalDate.HourOfDay(map) == hour, "test clock reaches local hour " + hour);
            nightOnlyPhase = phase;
            Next(NightOnlyStep);
        }
    }
}
