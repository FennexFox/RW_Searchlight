using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace DirectedSearchlights
{
    internal sealed class DetectionAlerts
    {
        private const int CooldownTicks = 2500;
        private const int AbsenceTicks = 120;
        private const int BatchTicks = 60;
        private const int MarkerTicks = 180;
        private const int BaselineTicks = 30;

        private readonly Map map;
        private readonly Dictionary<int, DetectionRecord> records = new Dictionary<int, DetectionRecord>();
        private readonly List<int> expiredRecords = new List<int>();
        private readonly Dictionary<int, PendingDetection> pending = new Dictionary<int, PendingDetection>();
        private readonly HashSet<int> baselinePawns = new HashSet<int>();
        private readonly List<Message> liveMessages = new List<Message>();
        private List<DetectionRecord> savedRecords;
        private bool seedBaseline;
        private int baselineUntil = -1;
        private int batchUntil;

        internal DetectionAlerts(Map map) => this.map = map;

        internal int NotificationsSent { get; private set; }
        internal int PawnsNotified { get; private set; }
        internal int SoundsPlayed { get; private set; }
        internal int ActiveMarkers
        {
            get
            {
                int count = 0;
                foreach (DetectionRecord record in records.Values)
                    if (record.MarkerUntil >= 0) count++;
                return count;
            }
        }

        internal void Acquired(Building_Searchlight light, Pawn pawn, int tick)
        {
            if (light.Map != map || !light.CanDetect(pawn)) return;

            FlushIfDue(tick);
            int id = pawn.thingIDNumber;
            records.TryGetValue(id, out DetectionRecord existing);
            // A manual acquisition can arrive after this map's tick. End the
            // absence now, before another lamp samples the pawn on the next tick.
            existing?.ObservePresence(tick, light);
            if (seedBaseline || (tick < baselineUntil && baselinePawns.Contains(id)))
            {
                if (!records.ContainsKey(id)) records.Add(id, new DetectionRecord { Pawn = pawn });
                return;
            }

            if (light.AlertMode == DetectionAlertMode.Off) return;

            if (pending.TryGetValue(id, out PendingDetection detection))
            {
                if (!detection.Lights.Contains(light)) detection.Lights.Add(light);
                return;
            }

            if (existing != null && !existing.Ready(tick)) return;

            if (pending.Count == 0) batchUntil = tick + BatchTicks;
            pending.Add(id, new PendingDetection(pawn.Position, light));
            // Persist a conservative cooldown if the transient batch is lost to a save and reload.
            records[id] = new DetectionRecord { Pawn = pawn, LastAlert = batchUntil, Detector = light };
        }

        internal void Tick(int tick, IReadOnlyList<Building_Searchlight> lights)
        {
            if (seedBaseline) SeedBaseline(tick, lights);
            if (tick >= baselineUntil) baselinePawns.Clear();
            FlushIfDue(tick);

            foreach (KeyValuePair<int, DetectionRecord> entry in records)
            {
                DetectionRecord record = entry.Value;
                if (record.Pawn == null || record.Pawn.Destroyed || record.Pawn.Dead || record.Ready(tick))
                {
                    expiredRecords.Add(entry.Key);
                    continue;
                }

                // Detectability follows every eligible lamp, including lights tracking another pawn.
                bool markerActive = record.MarkerUntil > tick;
                Presence presence = Detect(record, lights, markerActive);
                if (presence != Presence.None)
                {
                    record.ObservePresence(tick, record.Detector);
                }
                else
                {
                    if (record.AbsentSince < 0) record.AbsentSince = tick;
                }

                if (!markerActive || presence != Presence.Alerting) record.MarkerUntil = -1;
            }

            foreach (int id in expiredRecords) records.Remove(id);
            expiredRecords.Clear();
        }

        internal void Draw(IReadOnlyList<Building_Searchlight> lights)
        {
            if (Find.CurrentMap != map) return;

            int tick = Find.TickManager.TicksGame;
            foreach (DetectionRecord record in records.Values)
            {
                if (record.MarkerUntil < 0) continue;
                if (tick >= record.MarkerUntil || Detect(record, lights, checkAlertMode: true) != Presence.Alerting)
                {
                    record.MarkerUntil = -1;
                    continue;
                }

                IntVec3 cell = record.Pawn.Position;
                if (Find.CameraDriver.CurrentViewRect.Contains(cell))
                    GenDraw.DrawCircleOutline(cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays), 0.65f, SimpleColor.Yellow);
            }
        }

        private Presence Detect(DetectionRecord record, IReadOnlyList<Building_Searchlight> lights, bool checkAlertMode = false)
        {
            Pawn pawn = record.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Map != map) return Presence.None;

            // Reuse the last detector as a hint, rechecking current eligibility
            // even while the game is paused. Other lamps may cover the same cell.
            var previous = record.Detector;
            Presence presence = Presence.None;
            if (previous != null && previous.CanDetect(pawn))
            {
                presence = previous.AlertMode == DetectionAlertMode.Off ? Presence.Passive : Presence.Alerting;
                if (!checkAlertMode || presence == Presence.Alerting) return presence;
            }
            else record.Detector = null;
            for (int i = 0; i < lights.Count; i++)
            {
                Building_Searchlight light = lights[i];
                if (light == previous || !light.CanDetect(pawn)) continue;
                record.Detector = light;
                presence = light.AlertMode == DetectionAlertMode.Off ? Presence.Passive : Presence.Alerting;
                if (!checkAlertMode || presence == Presence.Alerting) break;
            }

            return presence;
        }

        private void SeedBaseline(int tick, IReadOnlyList<Building_Searchlight> lights)
        {
            seedBaseline = false;
            if (tick >= baselineUntil || lights.Count == 0) return;

            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                int id = pawn.thingIDNumber;
                baselinePawns.Add(id);
                if (records.ContainsKey(id)) continue;
                var record = new DetectionRecord { Pawn = pawn };
                if (Detect(record, lights) != Presence.None) records.Add(id, record);
            }
        }

        private void FlushIfDue(int tick)
        {
            if (pending.Count == 0 || tick < batchUntil) return;

            var targets = new LookTargets();
            var notifiedIds = new List<int>(pending.Count);
            bool wantsSound = false;
            // Keep only the detection cells. Message hover and click never follow live pawn positions.
            foreach (KeyValuePair<int, PendingDetection> entry in pending)
            {
                DetectionAlertMode mode = DetectionAlertMode.Off;
                foreach (Building_Searchlight light in entry.Value.Lights)
                {
                    if (light.Map == map && light.AlertMode > mode) mode = light.AlertMode;
                }

                if (mode == DetectionAlertMode.Off)
                {
                    records.Remove(entry.Key);
                    continue;
                }

                targets.targets.Add(new GlobalTargetInfo(entry.Value.Cell, map));
                notifiedIds.Add(entry.Key);
                wantsSound |= mode == DetectionAlertMode.VisualAndSound;
            }

            pending.Clear();
            if (notifiedIds.Count == 0) return;

            string text = notifiedIds.Count == 1 ? "DS_DetectedHostile".Translate() : "DS_DetectedHostiles".Translate(notifiedIds.Count);
            var message = new Message(text, MessageTypeDefOf.SilentInput, targets);
            Messages.Message(message, historical: false);
            if (!Messages.IsLive(message)) return;

            liveMessages.RemoveAll(existing => !Messages.IsLive(existing));
            liveMessages.Add(message);
            NotificationsSent++;
            PawnsNotified += notifiedIds.Count;
            foreach (int id in notifiedIds)
            {
                if (!records.TryGetValue(id, out DetectionRecord record)) continue;
                record.LastAlert = tick;
                record.MarkerUntil = tick + MarkerTicks;
            }

            if (wantsSound && MessageTypeDefOf.NeutralEvent.sound is SoundDef sound)
            {
                sound.PlayOneShotOnCamera();
                SoundsPlayed++;
            }
        }

        internal void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                savedRecords = new List<DetectionRecord>();
                foreach (DetectionRecord record in records.Values)
                {
                    Pawn pawn = record.Pawn;
                    if (pawn != null && !pawn.Destroyed && !pawn.Dead &&
                        (pawn.Spawned || pawn.holdingOwner != null || pawn.IsWorldPawn()))
                        savedRecords.Add(record);
                }
            }

            Scribe_Collections.Look(ref savedRecords, "searchlightDetectionRecords", LookMode.Deep);
            Scribe_Values.Look(ref baselineUntil, "searchlightDetectionBaselineUntil", -1);
            if (Scribe.mode == LoadSaveMode.Saving) savedRecords = null;
        }

        internal void Restored()
        {
            if (savedRecords != null)
            {
                foreach (DetectionRecord record in savedRecords)
                    if (record?.Pawn != null && !record.Pawn.Destroyed && !record.Pawn.Dead)
                        records[record.Pawn.thingIDNumber] = record;
            }

            int tick = Find.TickManager.TicksGame;
            if (savedRecords == null) baselineUntil = tick + BaselineTicks + 1;
            seedBaseline = tick < baselineUntil;
            savedRecords = null;
        }

        internal void Clear()
        {
            records.Clear();
            expiredRecords.Clear();
            pending.Clear();
            baselinePawns.Clear();
            foreach (Message message in liveMessages) message.lookTargets?.Notify_MapRemoved(map);
            liveMessages.Clear();
            savedRecords = null;
            seedBaseline = false;
            baselineUntil = -1;
        }

        private enum Presence { None, Passive, Alerting }

        private sealed class PendingDetection
        {
            public readonly IntVec3 Cell;
            public readonly List<Building_Searchlight> Lights;

            public PendingDetection(IntVec3 cell, Building_Searchlight light)
            {
                Cell = cell;
                Lights = new List<Building_Searchlight> { light };
            }
        }

        private sealed class DetectionRecord : IExposable
        {
            public Pawn Pawn;
            public Building_Searchlight Detector;
            public int LastAlert = -1;
            public int AbsentSince = -1;
            public bool AbsenceSatisfied;
            public int MarkerUntil = -1;

            public bool FinishedAbsence(int tick) => AbsentSince >= 0 && tick - AbsentSince >= AbsenceTicks;

            public void ObservePresence(int tick, Building_Searchlight detector)
            {
                Detector = detector;
                AbsenceSatisfied |= FinishedAbsence(tick);
                AbsentSince = -1;
            }

            public bool Ready(int tick) => (AbsenceSatisfied || FinishedAbsence(tick)) &&
                (LastAlert < 0 || tick - LastAlert >= CooldownTicks);

            public void ExposeData()
            {
                Scribe_References.Look(ref Pawn, "pawn");
                Scribe_Values.Look(ref LastAlert, "lastAlert", -1);
                Scribe_Values.Look(ref AbsentSince, "absentSince", -1);
                Scribe_Values.Look(ref AbsenceSatisfied, "absenceSatisfied", false);
            }
        }
    }
}
