using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DirectedSearchlights;
using RimWorld;
using Verse;

namespace SearchlightCoordinationHarness
{
    public sealed partial class CoordinationCheck
    {
        private const string SaveAlertTickFile = "saved-alert-tick.txt";

        private void BeginDeduplication()
        {
            ClearFixtures();
            Pawn target = Hostile(20, 0);
            for (int z = -5; z < 5; z++) Lamp(0, z);
            After(ScanInterval, () =>
            {
                Require(Reserved(Network, target) == 10, "ten lamps acquired the same target");
                int delivery = lamps.Min(l => l.LastAcquireTick) + BatchWindow;
                At(delivery - 1, () =>
                {
                    ExpectAlerts(0, 0, 0, 0);
                    At(delivery, () => CheckFirstDetection(target, delivery));
                });
            });
        }

        private void CheckFirstDetection(Pawn target, int delivery)
        {
            ExpectAlerts(1, 1, 0, 1);
            CheckMessage("DS_DetectedHostile".Translate(), target.Position);
            Require(!Find.TickManager.Paused, "a detection does not pause the game");
            At(delivery + MarkerLifetime - 1, () =>
            {
                ExpectAlerts(1, 1, 0, 1);
                After(1, () =>
                {
                    ExpectAlerts(1, 1, 0, 0);
                    foreach (Building_Searchlight lamp in lamps) Power(lamp, false);
                    After(1, () =>
                    {
                        Require(Reserved(Network, target) == 0, "power toggle frees all ten reservations");
                        foreach (Building_Searchlight lamp in lamps) Power(lamp, true);
                        After(ScanInterval, () => BeginPassiveDetection(target, delivery));
                    });
                });
            });
        }

        private void BeginPassiveDetection(Pawn target, int delivery)
        {
            Require(Reserved(Network, target) == 10, "all ten lights reacquired without repeating the notification");
            Pawn distractor = Hostile(19, 3);
            Building_Searchlight passive = lamps[0];
            passive.SetAlertMode(DetectionAlertMode.Off);
            passive.SelectManualTarget(distractor);
            foreach (Building_Searchlight lamp in lamps.Skip(1)) Power(lamp, false);
            At(delivery + AlertCooldown + 1, () =>
            {
                Require(passive.IsOperating && passive.LockedTarget == distractor && lamps.Skip(1).All(l => !l.IsOperating) &&
                    Reserved(Network, target) == 0, "only an Off lamp aiming at another pawn can still see the original target");
                distractor.DeSpawn();
                passive.SelectManualTarget(null);
                passive.SetAlertMode(DetectionAlertMode.Visual);
                foreach (Building_Searchlight lamp in lamps) Power(lamp, true);
                lamps[1].SelectManualTarget(target);
                After(BatchWindow + ScanInterval, () =>
                {
                    ExpectAlerts(1, 1, 0, 0);
                    Write("Off lamp locked to a different pawn preserves continuous detectability across cooldown");
                    CheckFixedPassiveCoverage(target);
                });
            });
        }

        private void CheckFixedPassiveCoverage(Pawn target)
        {
            Building_Searchlight passive = lamps[0];
            passive.SetMode(SearchlightMode.Fixed);
            passive.SetAlertMode(DetectionAlertMode.VisualAndSound);
            AdvancePowerMode(passive, "DS_AlwaysOn");
            AdvancePowerMode(passive, "DS_NightOnly");
            foreach (Building_Searchlight lamp in lamps.Skip(1)) Power(lamp, false);
            After(RequiredAbsence + ScanInterval, () =>
            {
                Require(passive.IsOperating && Reserved(Network, target) == 0,
                    "only the Fixed lamp sees the hostile, without reserving it");
                ExpectAlerts(1, 1, 0, 0);
                AdvancePowerMode(passive, "DS_EnemyOnly");
                passive.SetMode(SearchlightMode.Automatic);
                passive.SetAlertMode(DetectionAlertMode.Visual);
                passive.SelectManualTarget(target);
                foreach (Building_Searchlight lamp in lamps.Skip(1)) Power(lamp, true);
                After(BatchWindow + ScanInterval, () => CheckShortAbsence(target));
            });
        }

        private void CheckShortAbsence(Pawn target)
        {
            ExpectAlerts(1, 1, 0, 0);
            Require(Reserved(Network, target) == 10, "all ten reservations restored without another detection after cooldown");
            IntVec3 detectionCell = target.Position;
            target.DeSpawn();
            // Absence starts when the map observes the despawn on the next tick.
            After(RequiredAbsence, () =>
            {
                Spawn(target, detectionCell, home);
                lamps[0].SelectManualTarget(target);
                After(1, () =>
                {
                    // A second detection must observe that the first lamp already ended the short absence.
                    lamps[1].SelectManualTarget(null);
                    lamps[1].SelectManualTarget(target);
                    After(BatchWindow + ScanInterval, () => CheckCompleteAbsence(target, detectionCell));
                });
            });
        }

        private void CheckCompleteAbsence(Pawn target, IntVec3 detectionCell)
        {
            ExpectAlerts(1, 1, 0, 0);
            // The cooldown uses game ticks, while message lifetime uses real time.
            // Start with a clear message stack so this rearmed alert gets its own message.
            Messages.Clear();
            target.DeSpawn();
            After(RequiredAbsence + 1, () =>
            {
                Spawn(target, detectionCell, home);
                lamps[0].SelectManualTarget(target);
                After(BatchWindow, () =>
                {
                    ExpectAlerts(2, 2, 0, 1);
                    Write("ten-lamp deduplication, no repeat after power/continuous tracking, 60/180/119/120-tick boundaries passed");
                    CheckFixedMarker(target);
                });
            });
        }

        private static void AdvancePowerMode(Building_Searchlight lamp, string currentMode)
        {
            string label = "DS_PowerMode".Translate(currentMode.Translate());
            lamp.GetGizmos().OfType<Command_Action>().Single(g => g.defaultLabel == label).action();
        }

        private void CheckFixedMarker(Pawn target)
        {
            Building_Searchlight passive = lamps[0];
            passive.SetMode(SearchlightMode.Fixed);
            AdvancePowerMode(passive, "DS_AlwaysOn");
            AdvancePowerMode(passive, "DS_NightOnly");
            foreach (Building_Searchlight lamp in lamps.Skip(1)) Power(lamp, false);
            After(1, () =>
            {
                ExpectAlerts(2, 2, 0, 0);
                AdvancePowerMode(passive, "DS_EnemyOnly");
                passive.SetMode(SearchlightMode.Automatic);
                foreach (Building_Searchlight lamp in lamps.Skip(1)) Power(lamp, true);
                BeginCoalescedDetection(target);
            });
        }

        private void BeginCoalescedDetection(Pawn previous)
        {
            Message priorMessage = LatestDetectionMessage(Network);
            IntVec3 detectionCell = previous.Position;
            previous.DeSpawn();
            After(1, () =>
            {
                Pawn replacement = Hostile(0, 0, at: detectionCell);
                lamps[0].SetAlertMode(DetectionAlertMode.VisualAndSound);
                lamps[0].SelectManualTarget(replacement);
                After(BatchWindow, () =>
                {
                    Require(Messages.IsLive(priorMessage) && ReferenceEquals(LatestDetectionMessage(Network), priorMessage),
                        "same-cell alert coalesced into the existing live notification");
                    ExpectAlerts(2, 3, 1, 1);
                    Write("coalesced same-cell alert retained pawn count, marker, and opt-in sound");
                    BeginSoundBatch();
                });
            });
        }

        private void BeginSoundBatch()
        {
            ClearFixtures();
            Building_Searchlight tower = Lamp(0, 0, tower: true);
            Building_Searchlight second = Lamp(0, 8);
            second.SetMode(SearchlightMode.Fixed);
            second.SetAlertMode(DetectionAlertMode.VisualAndSound);
            Pawn blocked = Hostile(14, 0);
            Pawn direct = Hostile(14, 8);
            IntVec3 initialBlockedCell = blocked.Position;
            IntVec3 initialDirectCell = direct.Position;
            Wall(5, 0);
            tower.SelectManualTarget(blocked);
            Require(Reserved(Network, blocked) == 1 && tower.SpotCell != blocked.Position,
                "a tower reserves a blocked detection without illuminating the pawn");
            After(BatchWindow - 1, () =>
            {
                ExpectAlerts(2, 3, 1, 0);
                Require(second.AcquisitionCount == 0 && second.LockedTarget == null, "Fixed lamp did not detect eligible pawn");
                second.SetMode(SearchlightMode.Automatic);
                second.SelectManualTarget(direct);
                After(1, () =>
                {
                    ExpectAlerts(3, 5, 2, 2);
                    Message notification = CheckMessage("DS_DetectedHostiles".Translate(2), initialBlockedCell, initialDirectCell);
                    blocked.Position = Cell(16, 0);
                    Require(notification.lookTargets.targets.Any(t => t.Cell == initialBlockedCell) &&
                        notification.lookTargets.targets.All(t => t.Cell != blocked.Position), "notification remains at original detection cell as pawn moves");
                    home.fogGrid.Refog(CellRect.SingleCell(initialDirectCell));
                    After(1, () =>
                    {
                        Require(direct.Fogged() && Reserved(Network, direct) == 0, "fog immediately invalidates the reservation");
                        ExpectAlerts(3, 5, 2, 1);
                        tower.SetAlertMode(DetectionAlertMode.Off);
                        second.SetAlertMode(DetectionAlertMode.Off);
                        After(1, () =>
                        {
                            ExpectAlerts(3, 5, 2, 0);
                            home.fogGrid.Unfog(initialDirectCell);
                            Write("batch of two sends one real message with static cell targets and one opt-in sound; fog/Off clear markers");
                            BeginConcentratePolicy();
                        });
                    });
                });
            });
        }

        private void BeginConcentratePolicy()
        {
            ClearFixtures();
            Building_Searchlight reserved = Lamp(0, 0);
            Building_Searchlight concentrate = Lamp(0, 8);
            reserved.SetAlertMode(DetectionAlertMode.Off);
            concentrate.SetAlertMode(DetectionAlertMode.Off);
            concentrate.SetPolicy(AssignmentPolicy.Concentrate);
            Pawn nearerReserved = Hostile(18, 8);
            Pawn fartherFree = Hostile(28, 8);
            reserved.SelectManualTarget(nearerReserved);
            After(ScanInterval, () =>
            {
                Require(concentrate.LockedTarget == nearerReserved &&
                    Reserved(Network, nearerReserved) == 2 && Reserved(Network, fartherFree) == 0,
                    "Concentrate chooses the nearer pawn even when it already has a reservation");
                Write("Concentrate preferred the nearer reserved pawn over a farther free pawn");
                BeginSaveFixture();
            });
        }

        private void BeginSaveFixture()
        {
            ClearFixtures();
            Building_Searchlight manual = Lamp(0, 0);
            Building_Searchlight other = Lamp(0, 8);
            var generator = DefDatabase<ThingDef>.GetNamed("VanometricPowerCell");
            foreach (Building_Searchlight lamp in lamps)
            {
                Thing powerCell = ThingMaker.MakeThing(generator);
                powerCell.SetFaction(Faction.OfPlayer);
                fixtures.Add(GenSpawn.Spawn(powerCell, lamp.Position + new IntVec3(-1, 0, 0), home));
            }
            Pawn target = Hostile(18, 0);
            manual.SetAlertMode(DetectionAlertMode.VisualAndSound);
            other.SetPolicy(AssignmentPolicy.Concentrate);
            manual.SelectManualTarget(target);
            After(BatchWindow, () =>
            {
                Require(Reserved(Network, target) == 2 && other.LockedTarget == target, "Concentrate takes the best reachable pawn despite a manual reservation");
                ExpectAlerts(4, 6, 3, 1);
                File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, SaveAlertTickFile), Now.ToString(CultureInfo.InvariantCulture));
                Write("save fixture; alert tick=" + Now + "; pawn=" + target.thingIDNumber + "; reservations=" + Reserved(Network, target));
                readyToSave = true;
            });
        }

        private void BeginCurrentReload()
        {
            lamps.AddRange(home.listerThings.AllThings.OfType<Building_Searchlight>());
            pawns.AddRange(home.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer)));
            Require(lamps.Count == 2 && pawns.Count == 1, "current fixture reload retained both lamps and pawn");
            Building_Searchlight manual = lamps.Single(l => l.Policy == AssignmentPolicy.Spread);
            Building_Searchlight other = lamps.Single(l => l.Policy == AssignmentPolicy.Concentrate);
            Pawn target = pawns.Single();
            Require(manual.AlertMode == DetectionAlertMode.VisualAndSound && other.AlertMode == DetectionAlertMode.Visual,
                "current policy and per-lamp alert settings restored");
            Require(Read(manual, "manualTarget") == target, "current manual target reference restored");
            Require(lamps.All(l => l.IsOperating && l.LockedTarget == target) && Reserved(Network, target) == 2,
                "current reload rebuilt two valid reservations from the live lamps");
            ExpectAlerts(0, 0, 0, 0);
            int delivery = int.Parse(File.ReadAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, SaveAlertTickFile)), CultureInfo.InvariantCulture);
            After(BatchWindow + ScanInterval, () =>
            {
                ExpectAlerts(0, 0, 0, 0);
                IntVec3 cell = target.Position;
                target.DeSpawn();
                At(delivery + AlertCooldown - 1, () =>
                {
                    Spawn(target, cell, home);
                    manual.SelectManualTarget(target);
                    ExpectAlerts(0, 0, 0, 0);
                    After(1, () =>
                    {
                        manual.SelectManualTarget(null);
                        manual.SelectManualTarget(target);
                        // An early batch would already deliver at this tick.
                        After(BatchWindow - 1, () =>
                        {
                            ExpectAlerts(0, 0, 0, 0);
                            After(1, () =>
                            {
                                ExpectAlerts(1, 1, 1, 1);
                                Write("current save/load restores policies, manual references and reservations, clears markers, and respects ticks 2499/2500 of the persisted cooldown");
                                Finish(true);
                            });
                        });
                    });
                });
            });
        }

        private void ExpectAlerts(int messages, int targets, int sounds, int markers)
        {
            int actualMessages = Alerts(Network, "NotificationsSent");
            int actualTargets = Alerts(Network, "PawnsNotified");
            int actualSounds = Alerts(Network, "SoundsPlayed");
            int actualMarkers = Alerts(Network, "ActiveMarkers");
            Require(actualMessages == messages && actualTargets == targets && actualSounds == sounds && actualMarkers == markers,
                $"detection counts at {Now}: got {actualMessages}/{actualTargets}/{actualSounds}/{actualMarkers}, expected {messages}/{targets}/{sounds}/{markers}");
        }

        private Message CheckMessage(string text, params IntVec3[] cells)
        {
            Message message = LatestDetectionMessage(Network);
            Require(Messages.IsLive(message) && message.text == text && message.def == MessageTypeDefOf.SilentInput,
                "game UI received a neutral live detection notification with the localized wording");
            Require(message.lookTargets.targets.All(t => t.Map == home && !t.HasThing) &&
                cells.ToHashSet().SetEquals(message.lookTargets.targets.Select(t => t.Cell)),
                "notification targets are selectable snapshot cells on the correct map");
            return message;
        }

        private static Message LatestDetectionMessage(SearchlightMap network) =>
            ((List<Message>)Read(Read(network, "alerts"), "liveMessages")).Last();
    }
}
