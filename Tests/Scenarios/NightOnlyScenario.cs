using System;
using System.IO;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private enum NightOnlyPhase
        {
            DefaultDaytime,
            EnabledDaytime,
            ReloadedDaytime,
            Dusk,
            BeforeDawn,
            Dawn,
            PowerLoss
        }

        private const int NightOnlyStep = 15;
        private const string NightOnlySave = "night-only";
        private static NightOnlyPhase nightOnlyPhase;
        private static IntVec3 nightOnlyAim;
        private static CompPowerTrader nightOnlyPower;
        private static CompPowerTrader nightOnlyGeneratorPower;
        private static Thing nightOnlyGenerator;
        private static float normalPowerOutput;
        private static bool nightOnlySavePending;
        private static int nightOnlyPowerReadyTick = -1;

        private static void BeginNightOnly()
        {
            Map map = Find.CurrentMap;
            var lamp = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DS_SearchlightTurret"));
            lamp.SetFaction(Faction.OfPlayer);
            turret = (Building_Searchlight)GenSpawn.Spawn(lamp, origin, map);
            turret.SetPolicy(AssignmentPolicy.Independent);
            turret.GetComp<CompFlickable>().SwitchIsOn = true;
            lamps.Add(turret);

            var generator = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("VanometricPowerCell"));
            generator.SetFaction(Faction.OfPlayer);
            nightOnlyGenerator = GenSpawn.Spawn(generator, origin + new IntVec3(-1, 0, 0), map);
            nightOnlyAim = origin + new IntVec3(12, 0, 0);
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(nightOnlyAim);
            nightOnlyPower = turret.GetComp<CompPowerTrader>();
            nightOnlyGeneratorPower = nightOnlyGenerator.TryGetComp<CompPowerTrader>();
            normalPowerOutput = -nightOnlyPower.Props.PowerConsumption;
            nightOnlyPowerReadyTick = -1;
            SetNightOnlyPhase(NightOnlyPhase.DefaultDaytime, 17);
        }

        public override void LoadedGame()
        {
            GenCommandLine.TryGetCommandLineArg("ds-scenario-phase", out string phase);
            if (phase != "current") return;
            try
            {
                Map map = Find.CurrentMap;
                var savedLamps = map.listerThings.AllThings.OfType<Building_Searchlight>();
                turret = savedLamps.Single(lamp => lamp.def.defName == "DS_SearchlightTurret");
                var detectionLamp = savedLamps.Single(lamp => lamp.def.defName == "DS_SearchlightTower");
                PowerModeCommand(detectionLamp, "DS_EnemyOnly");
                detectionLamp.Destroy(DestroyMode.Vanish);
                nightOnlyGenerator = map.listerThings.AllThings.Single(t => t.def.defName == "VanometricPowerCell");
                nightOnlyPower = turret.GetComp<CompPowerTrader>();
                nightOnlyGeneratorPower = nightOnlyGenerator.TryGetComp<CompPowerTrader>();
                normalPowerOutput = -nightOnlyPower.Props.PowerConsumption;
                nightOnlyPowerReadyTick = -1;
                nightOnlyAim = turret.Position + new IntVec3(12, 0, 0);
                nightOnlyPhase = NightOnlyPhase.ReloadedDaytime;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Next(NightOnlyStep);
                Write("night-only save loaded; hour=" + GenLocalDate.HourOfDay(map));
            }
            catch (Exception ex) { Fail("night-only load: " + ex); }
        }

        public override void GameComponentUpdate()
        {
            if (!nightOnlySavePending) return;
            nightOnlySavePending = false;
            try
            {
                GameDataSaveLoader.SaveGame(NightOnlySave);
                Check(File.Exists(GenFilePaths.FilePathForSavedGame(NightOnlySave)), "night-only fixture save exists");
                Write("night-only SAVE (enabled daytime standby on a live power network)");
                Application.Quit(0);
            }
            catch (Exception ex) { Fail("night-only save: " + ex); }
        }

        private static void TickNightOnly(int age)
        {
            switch (nightOnlyPhase)
            {
                case NightOnlyPhase.DefaultDaytime:
                    if (!WaitForNetworkPower(age, "fresh fixture", true)) return;
                    Check(nightOnlyPower.PowerOutput == normalPowerOutput,
                        "default operation consumes the configured " + -normalPowerOutput + " W during the day");
                    CheckNightOnlyLight(true);
                    PowerModeCommand(turret, "DS_AlwaysOn").action();
                    PowerModeCommand(turret, "DS_NightOnly");
                    var detectionLamp = SpawnLamp("DS_SearchlightTower", origin + new IntVec3(0, 0, -8));
                    PowerModeCommand(detectionLamp, "DS_AlwaysOn").action();
                    PowerModeCommand(detectionLamp, "DS_NightOnly").action();
                    PowerModeCommand(detectionLamp, "DS_EnemyOnly");
                    SetNightOnlyPhase(NightOnlyPhase.EnabledDaytime, 17);
                    break;
                case NightOnlyPhase.EnabledDaytime:
                    CheckDaytimeStandby();
                    nightOnlySavePending = true;
                    step = 99;
                    break;
                case NightOnlyPhase.ReloadedDaytime:
                    PowerModeCommand(turret, "DS_NightOnly");
                    Check(GenLocalDate.HourOfDay(Find.CurrentMap) == 17, "save/load preserves the daytime fixture hour");
                    if (!WaitForNetworkPower(age, "reloaded fixture", false)) return;
                    CheckDaytimeStandby();
                    SetNightOnlyPhase(NightOnlyPhase.Dusk, 18);
                    break;
                case NightOnlyPhase.Dusk:
                    CheckPowerNetworkWired();
                    Check(nightOnlyPower.PowerOn, "actual power network supplies the searchlight at dusk");
                    CheckNightOnlyLight(true);
                    Check(nightOnlyPower.PowerOutput == normalPowerOutput,
                        "configured " + -normalPowerOutput + " W power draw resumes at 18:00");
                    SetNightOnlyPhase(NightOnlyPhase.BeforeDawn, 5);
                    break;
                case NightOnlyPhase.BeforeDawn:
                    CheckNightOnlyLight(true);
                    SetNightOnlyPhase(NightOnlyPhase.Dawn, 6);
                    break;
                case NightOnlyPhase.Dawn:
                    CheckDaytimeStandby();
                    nightOnlyGenerator.Destroy(DestroyMode.Vanish);
                    SetNightOnlyPhase(NightOnlyPhase.PowerLoss, 18);
                    break;
                case NightOnlyPhase.PowerLoss:
                    Check(nightOnlyGenerator.Destroyed, "power source was removed from the fixture network");
                    if (nightOnlyPower.PowerOn && age < 2400) return;
                    Check(!nightOnlyPower.PowerOn, "dusk does not restore an unpowered network");
                    CheckNightOnlyLight(false);
                    Write("night-only PASS (live network, save/load standby, 06:00/18:00 transitions, source loss)");
                    Finish(true);
                    break;
            }
        }

        private static Command_Action PowerModeCommand(Building_Searchlight lamp, string expectedMode)
        {
            var labels = new[] { "DS_AlwaysOn", "DS_NightOnly", "DS_EnemyOnly" }
                .Select(key => "DS_PowerMode".Translate(key.Translate()).ToString()).ToArray();
            var command = lamp.GetGizmos().OfType<Command_Action>().Single(g => labels.Contains(g.defaultLabel));
            Check(command.defaultLabel == "DS_PowerMode".Translate(expectedMode.Translate()),
                lamp.def.defName + " shows power mode " + expectedMode);
            return command;
        }

        private static bool WaitForNetworkPower(int age, string context, bool waitForConfiguredSpot)
        {
            CheckPowerNetworkWired();
            if (nightOnlyPower.PowerOn)
            {
                if (nightOnlyPowerReadyTick < 0)
                {
                    Check(age < 2400, context + " energizes through RimWorld's network within one local hour");
                    nightOnlyPowerReadyTick = Find.TickManager.TicksGame;
                    Write("night-only network ready; context=" + context + "; waitTicks=" + age +
                        "; configuredWatts=" + -normalPowerOutput);
                }
                if (!waitForConfiguredSpot || turret.SpotCell == nightOnlyAim) return true;
                Check(age < 2400, context + " reaches its configured spot within the same local hour");
                return false;
            }
            Check(age < 2400, context + " energizes through RimWorld's network within one local hour");
            return false;
        }

        private static void CheckPowerNetworkWired()
        {
            Check(nightOnlyGeneratorPower != null && nightOnlyGeneratorPower.PowerOutput > 0f,
                "fixture generator is producing power");
            Check(nightOnlyPower.PowerNet != null && nightOnlyPower.PowerNet == nightOnlyGeneratorPower.PowerNet,
                "searchlight and generator share the actual power network");
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
