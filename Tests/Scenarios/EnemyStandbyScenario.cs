using System;
using DirectedSearchlights;
using RimWorld;
using Verse;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private enum EnemyStandbyPhase { Waiting, Awake, Lost, CombinedDay }

        private const int EnemyStandbyStep = 16;
        private static EnemyStandbyPhase enemyStandbyPhase;
        private static IntVec3 enemyStandbyAim;
        private static float enemyStandbyPowerDraw;
        private static int automaticScansBeforeStandby;

        private static void BeginEnemyStandby()
        {
            SetEnemyStandbyHour(12);
            enemyStandbyAim = origin + new IntVec3(12, 0, 0);
            turret = SpawnLamp("DS_SearchlightTurret", origin);
            turret.SetMode(SearchlightMode.Fixed);
            turret.SetAimCell(enemyStandbyAim);
            turret.SetAlertMode(DetectionAlertMode.Off);
            enemyStandbyPowerDraw = -turret.GetComp<CompPowerTrader>().Props.PowerConsumption;
            Check(turret.Policy == AssignmentPolicy.Independent, "Enemy Only fixture uses Independent policy");
            Check(!NightOnlyToggle().isActive(), "new fixture starts with Night Only disabled");
            EnemyOnlyToggle().toggleAction();
            Check(EnemyOnlyToggle().isActive(), "Enemy Only command enables the setting");
            enemyStandbyPhase = EnemyStandbyPhase.Waiting;
            Next(EnemyStandbyStep);
        }

        private static void TickEnemyStandby(int age)
        {
            if (age < 90 && enemyStandbyPhase != EnemyStandbyPhase.Lost &&
                enemyStandbyPhase != EnemyStandbyPhase.CombinedDay) return;

            switch (enemyStandbyPhase)
            {
                case EnemyStandbyPhase.Waiting:
                    CheckEnemyStandbyDark("while waiting");
                    Check(turret.Policy == AssignmentPolicy.Independent,
                        "Enemy Only fixture uses Independent policy");
                    Check(turret.LockedTarget == null, "Enemy Only standby does not lock a target before wake");
                    if (turret.CurrentMode == SearchlightMode.Fixed)
                        Check(turret.AcquisitionCount >= 2,
                            "fixed Enemy Only scans while waiting without assigning a target");
                    else
                        Check(turret.AcquisitionCount >= automaticScansBeforeStandby + 2,
                            "Automatic Enemy Only scans while waiting without a target lock");
                    Write("enemy-only-wait PASS; mode=" + turret.CurrentMode + "; scans=" + turret.AcquisitionCount);
                    SpawnTarget(enemyStandbyAim);
                    enemyStandbyPhase = EnemyStandbyPhase.Awake;
                    Next(EnemyStandbyStep);
                    return;

                case EnemyStandbyPhase.Awake:
                    bool fixedMode = turret.CurrentMode == SearchlightMode.Fixed;
                    bool combined = NightOnlyToggle().isActive();
                    Check(Power(turret) && turret.IsOperating && PowerOutputIs(enemyStandbyPowerDraw),
                        "Enemy Only wakes and restores configured power after detection");
                    if (fixedMode)
                    {
                        Check(turret.LockedTarget == null,
                            "Fixed Enemy Only detects without locking or tracking the hostile");
                        Check(turret.SpotCell == enemyStandbyAim && GroundGlow(enemyStandbyAim) > 0.01f,
                            "Fixed Enemy Only lights its configured aim after detection");
                        Write("enemy-only-fixed-wake PASS; spot=" + turret.SpotCell);
                    }
                    else
                    {
                        Check(turret.LockedTarget == target && turret.SpotCell.IsValid &&
                            GroundGlow(target.Position) > 0.01f,
                            "Automatic Enemy Only wakes, tracks, and illuminates the hostile");
                        if (combined)
                            Check(EnemyOnlyToggle().isActive() && GenLocalDate.HourOfDay(Find.CurrentMap) == 18,
                                "Night Only and Enemy Only wake together at 18:00");
                        Write(combined ? "enemy-only-night-wake PASS" : "enemy-only-auto-wake PASS");
                    }
                    lastSpot = turret.SpotCell;
                    target.DeSpawn();
                    target = null;
                    enemyStandbyPhase = EnemyStandbyPhase.Lost;
                    Next(EnemyStandbyStep);
                    return;

                case EnemyStandbyPhase.Lost:
                    if (age < 2 || !CheckEnemyStandbyAsleep("target lost")) return;
                    Check(turret.LockedTarget == null, "Enemy Only releases target assignment after it disappears");
                    if (turret.CurrentMode == SearchlightMode.Fixed)
                    {
                        turret.SetMode(SearchlightMode.Automatic);
                        automaticScansBeforeStandby = turret.AcquisitionCount;
                        enemyStandbyPhase = EnemyStandbyPhase.Waiting;
                        Next(EnemyStandbyStep);
                    }
                    else if (!NightOnlyToggle().isActive())
                    {
                        NightOnlyToggle().toggleAction();
                        Check(NightOnlyToggle().isActive() && EnemyOnlyToggle().isActive(),
                            "Night Only and Enemy Only can both be enabled");
                        SpawnTarget(enemyStandbyAim);
                        enemyStandbyPhase = EnemyStandbyPhase.CombinedDay;
                        Next(EnemyStandbyStep);
                    }
                    else
                    {
                        Write("enemy-only PASS; fixed-detection; automatic-wake; lost-target-sleep; night-conjunction");
                        turret.Destroy(DestroyMode.Vanish);
                        lamps.Remove(turret);
                        lamps.Clear();
                        BeginNightOnly();
                    }
                    return;

                case EnemyStandbyPhase.CombinedDay:
                    if (age < 90) return;
                    Check(GenLocalDate.HourOfDay(Find.CurrentMap) == 12,
                        "combined standby is tested during the day");
                    CheckEnemyStandbyDark("with both settings on during daytime");
                    Check(turret.LockedTarget == null,
                        "combined daytime standby does not acquire its hostile");
                    SetEnemyStandbyHour(18);
                    enemyStandbyPhase = EnemyStandbyPhase.Awake;
                    Next(EnemyStandbyStep);
                    return;
            }
        }

        private static bool CheckEnemyStandbyAsleep(string context)
        {
            CheckEnemyStandbyOff(context + " after target loss");
            return SpotCleared("Enemy Only " + context);
        }

        private static void CheckEnemyStandbyDark(string context)
        {
            CheckEnemyStandbyOff(context);
            Check(GroundGlow(enemyStandbyAim) <= 0.01f,
                "Enemy Only ground light is clear " + context);
        }

        private static void CheckEnemyStandbyOff(string context)
        {
            Check(Power(turret) && !turret.IsOperating && PowerOutputIs(0f),
                "Enemy Only remains powered, dark, and at 0 W " + context);
            Check(!turret.SpotCell.IsValid, "Enemy Only clears its endpoint immediately " + context);
        }

        private static bool PowerOutputIs(float expected) =>
            Math.Abs(turret.GetComp<CompPowerTrader>().PowerOutput - expected) < 0.01f;

        private static void SetEnemyStandbyHour(int hour)
        {
            Map map = Find.CurrentMap;
            const int ticksPerHour = 2500;
            const int ticksPerDay = 24 * ticksPerHour;
            int ticksAhead = (hour * ticksPerHour - GenLocalDate.DayTick(map) + ticksPerDay) % ticksPerDay;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticksAhead);
            Check(GenLocalDate.HourOfDay(map) == hour, "test clock reaches local hour " + hour);
        }
    }
}
