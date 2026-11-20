using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    public enum SearchlightMode
    {
        Fixed,
        Sweep,
        Automatic
    }

    public partial class Building_Searchlight : Building
    {
        private const float BeamRange = 60f;
        private const float DefaultSweepHalfAngle = 45f;
        private const float MinSweepHalfAngle = 0.5f;
        private const float MaxSweepHalfAngle = 180f;
        private const float SweepDegreesPerTick = 0.4f;
        internal const int AcquireInterval = 30;
        private const float TurnDegreesPerTick = 2f;
        // Effective height above the ground plane for the head's tilt axis.
        private const float AimHeightCells = 2f;

        private SearchlightMode mode = SearchlightMode.Automatic;
        private AssignmentPolicy policy = AssignmentPolicy.Spread;
        private DetectionAlertMode alertMode = SearchlightSettings.DefaultAlertMode;
        private float fixedHeading = float.NaN;
        private float fixedRange = BeamRange;
        private float sweepHalfAngle = DefaultSweepHalfAngle;
        private float aimHeading;
        private float aimRange = BeamRange;
        private float patrolHeading;
        private float sweepOffset;
        private int sweepDirection = 1;
        private Pawn manualTarget;
        private Pawn lockedTarget;
        private int lockSinceTick = -1;
        private Thing lightSpot;
        private BeamField beam;
        private SearchlightMap searchlights;
        private CompPowerTrader power;
        private CompFlickable flick;
        private bool nightOnly;
        private bool daylightPowerSuspended;
        private int lastAcquireTick = -1;
        private int acquisitionCount;
        private static Graphic headGraphic;

        public SearchlightMode CurrentMode => mode;
        public AssignmentPolicy Policy => policy;
        public DetectionAlertMode AlertMode => alertMode;
        public Pawn LockedTarget => lockedTarget;
        public float AimHeading => aimHeading;
        public float AimRange => aimRange;
        public float SweepAngle => SweepHalfAngle * 2f;
        public float SweepHeading => fixedHeading;
        public IntVec3 SpotCell => lightSpot != null && lightSpot.Spawned ? lightSpot.Position : IntVec3.Invalid;
        public bool IsOperating => Spawned && power != null && power.PowerOn &&
            (flick == null || flick.SwitchIsOn) && !InDaytimeStandby && (!RequiresOpenSky || !Position.Roofed(Map));
        private bool InDaytimeStandby => nightOnly && Spawned && GenLocalDate.HourOfDay(Map) is >= 6 and < 18;
        public int AcquisitionCount => acquisitionCount;
        public int LastAcquireTick => lastAcquireTick;
        internal int LockSinceTick => lockSinceTick;
        internal bool HasManualTarget => manualTarget != null;

        protected virtual bool DetectThroughWalls => false;
        protected virtual bool RequiresOpenSky => false;

        private float SweepHalfAngle => IsFinite(sweepHalfAngle) ?
            Mathf.Clamp(sweepHalfAngle, MinSweepHalfAngle, MaxSweepHalfAngle) : DefaultSweepHalfAngle;

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            power = GetComp<CompPowerTrader>();
            flick = GetComp<CompFlickable>();
            UpdateScheduledPower();
            if (float.IsNaN(fixedHeading))
                fixedHeading = Rotation.AsAngle;
            if (beam == null)
                beam = new BeamField();
            searchlights = map.GetComponent<SearchlightMap>();
            searchlights.Register(this, respawningAfterLoad);
            if (!respawningAfterLoad) RefreshBeamAndSpot();
        }

        public override void PostMapInit()
        {
            base.PostMapInit();
            RefreshBeamAndSpot();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            searchlights?.Unregister(this);
            searchlights = null;
            ClearVisuals();
            beam?.Dispose();
            beam = null;
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving && !IsFinite(sweepHalfAngle))
                sweepHalfAngle = DefaultSweepHalfAngle;
            Scribe_Values.Look(ref mode, "searchlightMode", SearchlightMode.Automatic);
            Scribe_Values.Look(ref nightOnly, "searchlightNightOnly", false);
            Scribe_Values.Look(ref policy, "searchlightAssignmentPolicy", AssignmentPolicy.Independent);
            Scribe_Values.Look(ref alertMode, "searchlightAlertMode", DetectionAlertMode.Visual);
            Scribe_Values.Look(ref fixedHeading, "searchlightFixedHeading", float.NaN);
            Scribe_Values.Look(ref fixedRange, "searchlightFixedRange", BeamRange);
            Scribe_Values.Look(ref sweepHalfAngle, "searchlightSweepHalfAngle", DefaultSweepHalfAngle);
            Scribe_Values.Look(ref aimHeading, "searchlightAimHeading", 0f);
            Scribe_Values.Look(ref aimRange, "searchlightAimRange", BeamRange);
            Scribe_Values.Look(ref patrolHeading, "searchlightPatrolHeading", fixedHeading);
            Scribe_Values.Look(ref sweepOffset, "searchlightSweepOffset", 0f);
            Scribe_Values.Look(ref sweepDirection, "searchlightSweepDirection", 1);
            Scribe_References.Look(ref manualTarget, "searchlightManualTarget");
            Scribe_References.Look(ref lockedTarget, "searchlightLockedTarget");
            Scribe_Values.Look(ref lockSinceTick, "searchlightLockSinceTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (!Enum.IsDefined(typeof(SearchlightMode), mode)) mode = SearchlightMode.Automatic;
                if (!Enum.IsDefined(typeof(AssignmentPolicy), policy)) policy = AssignmentPolicy.Independent;
                if (!Enum.IsDefined(typeof(DetectionAlertMode), alertMode)) alertMode = DetectionAlertMode.Visual;
                if (float.IsNaN(fixedHeading) || float.IsInfinity(fixedHeading))
                    fixedHeading = Rotation.AsAngle;
                if (float.IsNaN(patrolHeading) || float.IsInfinity(patrolHeading))
                    patrolHeading = fixedHeading;
                if (!IsFinite(sweepHalfAngle)) sweepHalfAngle = DefaultSweepHalfAngle;
                sweepHalfAngle = Mathf.Clamp(sweepHalfAngle, MinSweepHalfAngle, MaxSweepHalfAngle);
                if (!IsFinite(sweepOffset)) sweepOffset = 0f;
                sweepOffset = Mathf.Clamp(sweepOffset, -SweepHalfAngle, SweepHalfAngle);
                fixedRange = Mathf.Clamp(fixedRange, 1f, BeamRange);
                aimRange = Mathf.Clamp(aimRange, 0f, BeamRange);
                sweepDirection = sweepDirection < 0 ? -1 : 1;
                lastAcquireTick = -1;
            }
        }

        protected override void Tick()
        {
            base.Tick();
            UpdateScheduledPower();
            int tick = Find.TickManager.TicksGame;
            searchlights.BeforeLightTick(tick);
            searchlights.Reconcile(this, tick);
            if (!IsOperating)
            {
                ClearVisuals();
                return;
            }

            float desiredHeading;
            float desiredRange;
            if (lockedTarget != null)
            {
                desiredHeading = HeadingTo(lockedTarget.Position);
                desiredRange = Mathf.Min(BeamRange, Mathf.Sqrt(lockedTarget.Position.DistanceToSquared(Position)));
            }
            else if (mode == SearchlightMode.Sweep)
            {
                sweepOffset += SweepDegreesPerTick * sweepDirection;
                if (sweepOffset >= SweepHalfAngle) { sweepOffset = SweepHalfAngle; sweepDirection = -1; }
                else if (sweepOffset <= -SweepHalfAngle) { sweepOffset = -SweepHalfAngle; sweepDirection = 1; }
                desiredHeading = fixedHeading + sweepOffset;
                desiredRange = fixedRange;
            }
            else if (mode == SearchlightMode.Automatic)
            {
                patrolHeading = Mathf.Repeat(patrolHeading + SweepDegreesPerTick, 360f);
                desiredHeading = patrolHeading;
                desiredRange = fixedRange;
            }
            else { desiredHeading = fixedHeading; desiredRange = fixedRange; }

            aimHeading = Mathf.Repeat(Mathf.MoveTowardsAngle(aimHeading, desiredHeading, TurnDegreesPerTick), 360f);
            aimRange = TiltTowardsRange(aimRange, desiredRange);
            RefreshBeamAndSpot();
        }

        private static float TiltTowardsRange(float current, float target)
        {
            if (current == target) return target;
            float pitch = Mathf.Atan2(current, AimHeightCells);
            float targetPitch = Mathf.Atan2(target, AimHeightCells);
            float next = Mathf.MoveTowards(pitch, targetPitch, TurnDegreesPerTick * Mathf.Deg2Rad);
            return next == targetPitch ? target : AimHeightCells * Mathf.Tan(next);
        }

        internal bool ScanDue(int tick)
        {
            return mode != SearchlightMode.Fixed &&
                (tick + thingIDNumber) % AcquireInterval == 0 && IsOperating;
        }

        internal void RecordScan(int tick)
        {
            lastAcquireTick = tick;
            acquisitionCount++;
        }

        internal Pawn RetainedTarget()
        {
            if (mode == SearchlightMode.Fixed || !IsOperating) return null;
            if (manualTarget != null)
            {
                if (ValidTarget(manualTarget)) return manualTarget;
                if (lockedTarget == manualTarget)
                {
                    manualTarget = null;
                    return null;
                }
                manualTarget = null;
            }
            return ValidTarget(lockedTarget) ? lockedTarget : null;
        }

        internal void ApplyLock(Pawn pawn, int tick)
        {
            if (pawn == null) lockSinceTick = -1;
            else if (lockedTarget != pawn || lockSinceTick < 0 || lockSinceTick > tick) lockSinceTick = tick;
            lockedTarget = pawn;
        }

        private bool ValidTarget(Pawn pawn) => IsDetectableHostile(pawn, Map) && WithinDetectionArea(pawn.Position, out _);
        internal bool CanDetect(Pawn pawn) => mode != SearchlightMode.Fixed && IsOperating && ValidTarget(pawn);

        // The coordinator has already checked hostility and visibility state for this batch.
        internal bool TryDescribeCandidate(Pawn pawn, out TargetCandidate candidate)
        {
            candidate = default;
            if (!WithinDetectionArea(pawn.Position, out int distanceSquared)) return false;
            candidate = new TargetCandidate(pawn, distanceSquared,
                !DetectThroughWalls || BeamField.Clear(Map, Position, pawn.Position));
            return true;
        }

        private bool WithinDetectionArea(IntVec3 cell, out int distanceSquared)
        {
            int dx = cell.x - Position.x;
            int dz = cell.z - Position.z;
            distanceSquared = dx * dx + dz * dz;
            if (distanceSquared > BeamRange * BeamRange) return false;
            if (!IsWithinSweepArc(cell)) return false;
            if (DetectThroughWalls)
                return !cell.Roofed(Map);
            return BeamField.Clear(Map, Position, cell);
        }

        internal static bool IsDetectableHostile(Pawn pawn, Map map)
        {
            return pawn != null && !pawn.Destroyed && pawn.Spawned && pawn.Map == map && !pawn.Dead &&
                !pawn.Downed && !pawn.IsPsychologicallyInvisible() && !pawn.Fogged() &&
                pawn.HostileTo(Faction.OfPlayer);
        }

        private bool IsWithinSweepArc(IntVec3 cell)
        {
            return mode != SearchlightMode.Sweep ||
                (IsFinite(fixedHeading) &&
                    Mathf.Abs(Mathf.DeltaAngle(fixedHeading, HeadingTo(cell))) <= SweepHalfAngle);
        }

        private void ReleaseInvalidTargets()
        {
            searchlights?.Reconcile(this, Find.TickManager.TicksGame);
        }

        private float HeadingTo(IntVec3 cell)
        {
            return Mathf.Atan2(cell.x - Position.x, cell.z - Position.z) * Mathf.Rad2Deg;
        }

        private void RefreshBeamAndSpot()
        {
            if (!IsOperating || beam == null) { ClearVisuals(); return; }
            beam.Update(Map, Position, aimHeading, aimRange, SearchlightSettings.Diameter);
            IntVec3 cell = beam.CenterCell;
            if (lockedTarget != null && lockedTarget.Spawned && lockedTarget.Map == Map &&
                beam.Reaches(lockedTarget.Position))
                cell = lockedTarget.Position;
            if (!cell.IsValid) { RemoveSpot(); return; }
            if (lightSpot == null || lightSpot.Destroyed)
                lightSpot = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DS_LightSpot"));
            CompGlower glower = lightSpot.TryGetComp<CompGlower>();
            // RimWorld 1.6 starts the glow flood at distance 100 (one cell), even
            // at the source. Convert our physical radius to the grid's distance.
            float radius = 1f + BeamGrid.SpotRadius(aimRange, SearchlightSettings.Diameter, Position.DistanceTo(cell));
            if (lightSpot.Spawned)
            {
                if (lightSpot.Position == cell && Mathf.Abs(glower.GlowRadius - radius) < 0.02f) return;
                Map.glowGrid.DeRegisterGlower(glower);
                lightSpot.Position = cell;
                glower.GlowRadius = radius;
                Map.glowGrid.RegisterGlower(glower);
            }
            else
            {
                glower.GlowRadius = radius;
                GenSpawn.Spawn(lightSpot, cell, Map, WipeMode.Vanish);
            }
        }

        private void RemoveSpot()
        {
            if (lightSpot != null && !lightSpot.Destroyed)
                lightSpot.Destroy(DestroyMode.Vanish);
            lightSpot = null;
        }

        private void ClearVisuals()
        {
            RemoveSpot();
            beam?.Update(Map, Position, aimHeading, 0f, SearchlightSettings.Diameter);
        }

        internal void DrawBeam()
        {
            if (IsOperating) beam?.Draw();
        }

        internal bool BeamVisibleIn(CellRect view)
        {
            return IsOperating && beam != null && beam.VisibleIn(view);
        }

        internal void DisposeRenderResources()
        {
            beam?.Dispose();
            beam = null;
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (headGraphic == null)
                headGraphic = GraphicDatabase.Get<Graphic_Single>(
                    "Things/Building/Searchlight/SearchlightHead", ShaderDatabase.Cutout);
            headGraphic.Draw(drawLoc + Vector3.up * 0.03f, Rot4.North, this, aimHeading);
        }

        public void SelectManualTarget(Pawn pawn)
        {
            if (pawn != null && !CanDesignate(pawn)) return;
            manualTarget = pawn;
            SetImmediateLock(IsOperating ? pawn : null);
            lastAcquireTick = -1;
        }

        private bool CanDesignate(Pawn pawn) => mode != SearchlightMode.Fixed && ValidTarget(pawn);

        public void SetMode(SearchlightMode next)
        {
            mode = next;
            manualTarget = null;
            SetImmediateLock(null);
            if (next == SearchlightMode.Sweep) { sweepOffset = 0f; sweepDirection = 1; }
            lastAcquireTick = -1;
        }

        public void SetPolicy(AssignmentPolicy next)
        {
            if (policy == next || !Enum.IsDefined(typeof(AssignmentPolicy), next)) return;
            policy = next;
            searchlights?.ResetRedistribution(this);
        }

        public void SetAlertMode(DetectionAlertMode next)
        {
            if (Enum.IsDefined(typeof(DetectionAlertMode), next)) alertMode = next;
        }

        private void SetImmediateLock(Pawn pawn)
        {
            if (searchlights != null) searchlights.ChangeLock(this, pawn, Find.TickManager.TicksGame);
            else ApplyLock(null, -1);
        }

        protected override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (Spawned && (signal == CompPowerTrader.PowerTurnedOffSignal || signal == CompFlickable.FlickedOffSignal))
                SetImmediateLock(null);
        }

        public void SetAimCell(IntVec3 cell)
        {
            if (!Spawned || !cell.InBounds(Map) || cell == Position) return;
            fixedHeading = HeadingTo(cell);
            patrolHeading = fixedHeading;
            fixedRange = Mathf.Clamp(Mathf.Sqrt(cell.DistanceToSquared(Position)), 1f, BeamRange);
            ResetSweepPhase();
            ReleaseInvalidTargets();
        }

        public void SetSweepAngle(float fullAngle)
        {
            if (!IsFinite(fullAngle)) return;
            sweepHalfAngle = Mathf.Clamp(fullAngle * 0.5f, MinSweepHalfAngle, MaxSweepHalfAngle);
            if (!IsFinite(sweepOffset)) sweepOffset = 0f;
            sweepOffset = Mathf.Clamp(sweepOffset, -SweepHalfAngle, SweepHalfAngle);
            if (sweepOffset >= SweepHalfAngle) sweepDirection = -1;
            else if (sweepOffset <= -SweepHalfAngle) sweepDirection = 1;
            ReleaseInvalidTargets();
        }

        public void SetSweepHeading(float heading)
        {
            if (!IsFinite(heading)) return;
            fixedHeading = Mathf.Repeat(heading, 360f);
            patrolHeading = fixedHeading;
            ResetSweepPhase();
            ReleaseInvalidTargets();
        }

        private void ResetSweepPhase()
        {
            sweepOffset = 0f;
            sweepDirection = 1;
        }

        private void UpdateScheduledPower()
        {
            if (power == null) return;
            if (InDaytimeStandby)
            {
                power.PowerOutput = 0f;
                daylightPowerSuspended = true;
            }
            else if (daylightPowerSuspended)
            {
                power.SetUpPowerVars();
                daylightPowerSuspended = false;
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos()) yield return gizmo;
            yield return new Command_Toggle
            {
                defaultLabel = "DS_NightOnly".Translate(),
                defaultDesc = "DS_NightOnlyDesc".Translate(),
                icon = TexCommand.DesirePower,
                isActive = () => nightOnly,
                toggleAction = () =>
                {
                    nightOnly = !nightOnly;
                    UpdateScheduledPower();
                    ReleaseInvalidTargets();
                    RefreshBeamAndSpot();
                }
            };
            foreach (Gizmo gizmo in CoordinationGizmos()) yield return gizmo;
            if (mode == SearchlightMode.Sweep) yield return CreateSweepArcCommand();
            foreach (SearchlightMode choice in Enum.GetValues(typeof(SearchlightMode)))
            {
                SearchlightMode selected = choice;
                yield return new Command_Action
                {
                    defaultLabel = ("DS_Mode" + selected).Translate() + (mode == selected ? " ✓" : ""),
                    icon = selected == SearchlightMode.Fixed ? SearchlightIcons.Fixed :
                        selected == SearchlightMode.Sweep ? SearchlightIcons.Sweep : SearchlightIcons.Automatic,
                    action = () => SetMode(selected)
                };
            }
            if (mode != SearchlightMode.Fixed)
                yield return new Command_Target
            {
                defaultLabel = "DS_SelectTarget".Translate(),
                icon = SearchlightIcons.SelectTarget,
                targetingParams = new TargetingParameters
                {
                    canTargetPawns = true,
                    canTargetBuildings = false,
                    canTargetAnimals = true,
                    canTargetLocations = false,
                    validator = target => target.Thing is Pawn pawn && CanDesignate(pawn)
                },
                action = target => SelectManualTarget(target.Thing as Pawn)
            };
            if (manualTarget != null)
                yield return new Command_Action
                {
                    defaultLabel = "DS_ClearTarget".Translate(),
                    icon = SearchlightIcons.ClearTarget,
                    action = () => SelectManualTarget(null)
                };
            yield return new Command_Target
            {
                defaultLabel = "DS_SetDirection".Translate(),
                icon = SearchlightIcons.SetDirection,
                targetingParams = TargetingParameters.ForCell(),
                action = target =>
                {
                    SetAimCell(target.Cell);
                }
            };
        }
    }

    internal readonly struct TargetCandidate
    {
        public readonly Pawn Pawn;
        public readonly int DistanceSquared;
        public readonly bool Direct;

        public TargetCandidate(Pawn pawn, int distanceSquared, bool direct)
        {
            Pawn = pawn;
            DistanceSquared = distanceSquared;
            Direct = direct;
        }

        public bool Precedes(TargetCandidate other, int reservations, int otherReservations)
        {
            if (Direct != other.Direct) return Direct;
            if (reservations != otherReservations) return reservations < otherReservations;
            if (DistanceSquared != other.DistanceSquared) return DistanceSquared < other.DistanceSquared;
            return Pawn.thingIDNumber < other.Pawn.thingIDNumber;
        }
    }

    public class Building_SearchlightTower : Building_Searchlight
    {
        protected override bool DetectThroughWalls => true;
        protected override bool RequiresOpenSky => true;
    }
}
