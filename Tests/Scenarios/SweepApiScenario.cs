using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DirectedSearchlights;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SearchlightScenarioHarness
{
    public sealed partial class ScenarioCheck
    {
        private enum SweepApiPhase
        {
            CustomThirty,
            WrapNorth,
            FullCircle,
            ReachableWall,
            ReachableClosedDoor,
            ReachableOpenDoor,
            GizmoWindow,
            GizmoClosed,
            NarrowLock,
            MoveOutsideArc,
            VerifyMoveOutsideArc,
            RotateLock
        }

        private const int SweepApiStep = 14;
        private static SweepApiPhase sweepApiPhase;
        private static float sweepMinOffset;
        private static float sweepMaxOffset;
        private static bool sweepSawPositiveEdge;
        private static bool sweepSawNegativeEdge;
        private static bool sweepSawNorth;
        private static readonly IntVec3 SweepInsideTargetOffset = new IntVec3(20, 0, 5);
        private static readonly IntVec3 SweepOutsideTargetOffset = new IntVec3(20, 0, 20);
        private static readonly IntVec3 PreviewCellOffset = new IntVec3(10, 0, 0);
        private static readonly IntVec3 PreviewBlockerOffset = new IntVec3(5, 0, 0);
        private static readonly MethodInfo ReachableCellsMethod = typeof(Building_Searchlight).GetMethod(
            "GetReachableCells", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo VisibilityRefreshField = typeof(Building_Searchlight).GetField(
            "nextVisibilityRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
        private static Thing previewWall;
        private static Building_Door previewDoor;
        private static float gizmoHeadingBefore;
        private static float gizmoAngleBefore;

        private static void SetVisibilityRefreshNow() => VisibilityRefreshField.SetValue(turret, 0f);

        private static List<IntVec3> ReadReachableCells(float heading, float width) =>
            (List<IntVec3>)ReachableCellsMethod.Invoke(turret, new object[] { heading, width });

        private static Window FindSweepEditorWindow() =>
            Find.WindowStack.Windows.FirstOrDefault(w => w.GetType().DeclaringType == typeof(Building_Searchlight));

        private static void BeginSweepApi()
        {
            foreach (Building_Searchlight lamp in lamps.ToList())
                if (!lamp.Destroyed) lamp.Destroy(DestroyMode.Vanish);
            lamps.Clear();

            turret = SpawnLamp("DS_SearchlightTurret", origin);
            turret.SetAimCell(origin + new IntVec3(0, 0, 20));
            turret.SetSweepHeading(0f);
            turret.SetSweepAngle(30f);
            Check(Mathf.Abs(turret.SweepAngle - 30f) < 0.01f, "custom sweep angle is applied");
            Check(Mathf.Abs(Mathf.DeltaAngle(turret.SweepHeading, 0f)) < 0.01f,
                "custom sweep heading is applied");

            // Boundary values are clamped, while non-finite input must not poison
            // the persisted/runtime sweep configuration.
            turret.SetSweepAngle(0f);
            Check(Mathf.Abs(turret.SweepAngle - 1f) < 0.01f, "sweep angle clamps to one degree");
            turret.SetSweepAngle(400f);
            Check(Mathf.Abs(turret.SweepAngle - 360f) < 0.01f, "sweep angle clamps to 360 degrees");
            turret.SetSweepAngle(float.NaN);
            Check(IsFinite(turret.SweepAngle), "NaN sweep angle is guarded");
            turret.SetSweepAngle(float.PositiveInfinity);
            Check(IsFinite(turret.SweepAngle), "infinite sweep angle is guarded");
            turret.SetSweepHeading(float.NaN);
            Check(IsFinite(turret.SweepHeading), "NaN sweep heading is guarded");
            turret.SetSweepHeading(float.NegativeInfinity);
            Check(IsFinite(turret.SweepHeading), "infinite sweep heading is guarded");

            turret.SetSweepHeading(0f);
            turret.SetSweepAngle(30f);
            turret.SetMode(SearchlightMode.Sweep);
            ResetSweepSamples();
            sweepApiPhase = SweepApiPhase.CustomThirty;
            Next(SweepApiStep);
        }

        private static void TickSweepApi(int age)
        {
            Check(age <= 1500, "timed out waiting for " + sweepApiPhase);
            switch (sweepApiPhase)
            {
                case SweepApiPhase.CustomThirty:
                    TickCustomThirty(age);
                    break;
                case SweepApiPhase.WrapNorth:
                    TickWrapNorth(age);
                    break;
                case SweepApiPhase.FullCircle:
                    TickFullCircle(age);
                    break;
                case SweepApiPhase.ReachableWall:
                    TickReachableWall(age);
                    break;
                case SweepApiPhase.ReachableClosedDoor:
                    TickReachableClosedDoor(age);
                    break;
                case SweepApiPhase.ReachableOpenDoor:
                    TickReachableOpenDoor(age);
                    break;
                case SweepApiPhase.GizmoWindow:
                    break;
                case SweepApiPhase.GizmoClosed:
                    TickGizmoClosed(age);
                    break;
                case SweepApiPhase.NarrowLock:
                    TickNarrowLock(age);
                    break;
                case SweepApiPhase.MoveOutsideArc:
                    TickMoveOutsideArc(age);
                    break;
                case SweepApiPhase.VerifyMoveOutsideArc:
                    TickVerifyMoveOutsideArc(age);
                    break;
                case SweepApiPhase.RotateLock:
                    TickRotateLock(age);
                    break;
            }
        }

        private static void TickCustomThirty(int age)
        {
            SampleSweep("30-degree sweep");
            if (age < 150 || !sweepSawPositiveEdge || !sweepSawNegativeEdge) return;
            Write("sweep-angle30 PASS; minOffset=" + sweepMinOffset + "; maxOffset=" + sweepMaxOffset);

            turret.SetSweepHeading(350f);
            turret.SetSweepAngle(30f);
            turret.SetMode(SearchlightMode.Sweep);
            ResetSweepSamples();
            sweepApiPhase = SweepApiPhase.WrapNorth;
            Next(SweepApiStep);
        }

        private static void TickWrapNorth(int age)
        {
            SampleSweep("north-wrapping sweep");
            sweepSawNorth |= Mathf.Abs(Mathf.DeltaAngle(0f, turret.AimHeading)) < 1.5f;
            if (age < 180 || !sweepSawNorth || !sweepSawPositiveEdge || !sweepSawNegativeEdge) return;
            Check(Mathf.Abs(Mathf.DeltaAngle(turret.SweepHeading, 350f)) < 0.01f,
                "sweep heading remains normalized across north");
            Write("sweep-wrap-north PASS; minOffset=" + sweepMinOffset + "; maxOffset=" + sweepMaxOffset);

            turret.SetSweepHeading(0f);
            turret.SetSweepAngle(360f);
            turret.SetMode(SearchlightMode.Sweep);
            ResetSweepSamples();
            sweepApiPhase = SweepApiPhase.FullCircle;
            Next(SweepApiStep);
        }

        private static void TickFullCircle(int age)
        {
            SampleSweep("360-degree sweep");
            if (age < 1350) return;
            Check(turret.SweepAngle == 360f, "360-degree sweep width is retained");
            Check(sweepMaxOffset > 160f && sweepMinOffset < -160f,
                "360-degree sweep traverses both sides of the full arc");
            Write("sweep-angle360 PASS; minOffset=" + sweepMinOffset + "; maxOffset=" + sweepMaxOffset);
            BeginLockChecks();
        }

        private static void BeginLockChecks()
        {
            turret.SetAimCell(origin + new IntVec3(20, 0, 0));
            turret.SetSweepHeading(90f);
            turret.SetSweepAngle(60f);
            turret.SetMode(SearchlightMode.Sweep);
            Check(ReachableCellsMethod != null && VisibilityRefreshField != null,
                "reachable-cell reflection hooks are available");
            SetVisibilityRefreshNow();
            Check(ReadReachableCells(90f, 60f).Contains(origin + PreviewCellOffset),
                "Ctrl preview includes a clear-path cell");
            previewWall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel),
                origin + PreviewBlockerOffset, Find.CurrentMap);
            sweepApiPhase = SweepApiPhase.ReachableWall;
            Next(SweepApiStep);
        }

        private static void TickReachableWall(int age)
        {
            if (age < 1) return;
            SetVisibilityRefreshNow();
            Check(!ReadReachableCells(90f, 60f).Contains(origin + PreviewCellOffset),
                "Ctrl preview excludes a cell behind a wall");
            previewWall.Destroy(DestroyMode.Vanish);
            previewWall = null;
            previewDoor = (Building_Door)GenSpawn.Spawn(
                ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Door"), ThingDefOf.Steel),
                origin + PreviewBlockerOffset, Find.CurrentMap);
            sweepApiPhase = SweepApiPhase.ReachableClosedDoor;
            Next(SweepApiStep);
        }

        private static void TickReachableClosedDoor(int age)
        {
            if (age < 1) return;
            Check(!previewDoor.Open, "preview door starts closed");
            SetVisibilityRefreshNow();
            Check(!ReadReachableCells(90f, 60f).Contains(origin + PreviewCellOffset),
                "Ctrl preview excludes a cell behind a closed door");
            previewDoor.StartManualOpenBy(null);
            sweepApiPhase = SweepApiPhase.ReachableOpenDoor;
            Next(SweepApiStep);
        }

        private static void TickReachableOpenDoor(int age)
        {
            if (age < 30 || !previewDoor.Open) return;
            SetVisibilityRefreshNow();
            Check(ReadReachableCells(90f, 60f).Contains(origin + PreviewCellOffset),
                "Ctrl preview restores a cell after the door opens");
            previewDoor.Destroy(DestroyMode.Vanish);
            previewDoor = null;
            BeginGizmoCheck();
        }

        private static void BeginGizmoCheck()
        {
            Find.Selector.Select(turret, playSound: false);
            List<Gizmo> gizmos = turret.GetGizmos().ToList();
            Command_Action adjust = gizmos.OfType<Command_Action>().FirstOrDefault(g =>
                g.defaultLabel == "DS_AdjustSweep".Translate() || g.defaultDesc == "DS_AdjustSweepDesc".Translate());
            Check(adjust != null, "Sweep mode gizmos include the sweep editor command");
            gizmoHeadingBefore = turret.SweepHeading;
            gizmoAngleBefore = turret.SweepAngle;
            adjust.action();
            Check(FindSweepEditorWindow() != null, "sweep editor command opens its real window");
            sweepApiPhase = SweepApiPhase.GizmoWindow;
            Next(SweepApiStep);
        }

        public override void GameComponentOnGUI()
        {
            if (step != SweepApiStep || sweepApiPhase != SweepApiPhase.GizmoWindow ||
                Event.current.type != EventType.Repaint) return;
            var original = new Event(Event.current);
            Vector3 cameraPosition = Find.Camera.transform.position;
            try
            {
                var editor = FindSweepEditorWindow();
                Check(editor != null, "sweep editor remains open while selected");
                AimTestPointerAt(0f, 10f);
                ClickEditor(editor, 2);
                AimTestPointerAt(0f, 15f);
                ClickEditor(editor, 1);
                Check(FindSweepEditorWindow() == null && turret.SweepHeading == gizmoHeadingBefore &&
                    turret.SweepAngle == gizmoAngleBefore, "right click cancels the edited sector");
                turret.GetGizmos().OfType<Command_Action>().Single(g => g.defaultLabel == "DS_AdjustSweep".Translate()).action();
                editor = FindSweepEditorWindow();
                AimTestPointerAt(0f, 10f);
                ClickEditor(editor, 2);
                AimTestPointerAt(0f, 15f);
                ClickEditor(editor, 0);
                Check(FindSweepEditorWindow() == null &&
                    Mathf.Abs(Mathf.DeltaAngle(turret.SweepHeading, 0f)) < 0.01f &&
                    Mathf.Abs(turret.SweepAngle - (gizmoAngleBefore + 30f)) < 0.01f,
                    "left click commits the preview heading and the adjusted width");
                turret.SetSweepHeading(gizmoHeadingBefore);
                turret.SetSweepAngle(gizmoAngleBefore);
                sweepApiPhase = SweepApiPhase.GizmoClosed;
                Next(SweepApiStep);
            }
            catch (Exception ex) { Fail("sweep editor input: " + ex); }
            finally { Find.Camera.transform.position = cameraPosition; Event.current = original; }
        }

        // Move only the isolated test camera so the map point under the pointer is deterministic.
        private static void AimTestPointerAt(float x, float z)
        {
            Vector3 delta = turret.Position.ToVector3Shifted() + new Vector3(x, 0f, z) - UI.MouseMapPosition();
            Find.Camera.transform.position += new Vector3(delta.x, 0f, delta.z);
        }

        private static void ClickEditor(Window editor, int button)
        {
            Event.current = new Event { type = EventType.MouseDown, button = button };
            editor.DoWindowContents(new Rect(0, 0, UI.screenWidth, UI.screenHeight));
            Check(Event.current.type == EventType.Used, "editor consumed mouse button " + button);
        }

        private static void TickGizmoClosed(int age)
        {
            if (age < 2) return;
            Check(FindSweepEditorWindow() == null, "closing the sweep editor removes the window");
            Check(Mathf.Abs(Mathf.DeltaAngle(turret.SweepHeading, gizmoHeadingBefore)) < 0.01f &&
                Mathf.Abs(turret.SweepAngle - gizmoAngleBefore) < 0.01f,
                "closing the sweep editor cancels unapplied changes");
            Write("sweep-preview-ui PASS; clear-wall-door-reopen=true; gizmo-open-close=true");
            SpawnTarget(origin + SweepInsideTargetOffset);
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == target, "target inside custom arc can be manually locked");
            sweepApiPhase = SweepApiPhase.NarrowLock;
            Next(SweepApiStep);
        }

        private static void TickNarrowLock(int age)
        {
            if (age < 2) return;
            Check(turret.LockedTarget == target, "manual lock remains while target is inside the arc");
            turret.SetSweepAngle(10f);
            Check(Mathf.Abs(turret.SweepAngle - 10f) < 0.01f, "sweep narrows to ten degrees");
            Check(turret.LockedTarget == null, "narrowing releases a lock outside the new arc");
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == null, "manual selection enforces the narrowed arc");

            turret.SetSweepAngle(60f);
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == target, "target can be locked again after widening the arc");
            sweepApiPhase = SweepApiPhase.MoveOutsideArc;
            Next(SweepApiStep);
        }

        private static void TickMoveOutsideArc(int age)
        {
            if (age < 2) return;
            Check(turret.LockedTarget == target, "target is locked before moving outside the configured arc");
            target.DeSpawn();
            GenSpawn.Spawn(target, origin + SweepOutsideTargetOffset, Find.CurrentMap);
            HoldSweepTarget();
            Check(target.Position == origin + SweepOutsideTargetOffset, "target moved outside the configured arc");
            // The lock is intentionally still present until the searchlight gets
            // its next Tick, so this distinguishes movement from tick validation.
            Check(turret.LockedTarget == target, "moving target keeps the stale lock until the next tick");
            sweepApiPhase = SweepApiPhase.VerifyMoveOutsideArc;
            Next(SweepApiStep);
        }

        private static void TickVerifyMoveOutsideArc(int age)
        {
            if (age < 2) return;
            Check(target.Position == origin + SweepOutsideTargetOffset, "moved target remains outside the configured arc");
            Check(turret.LockedTarget == null, "next tick releases a lock after target leaves the arc");
            Write("moving-arc-boundary PASS; next-tick-release=true");

            target.DeSpawn();
            GenSpawn.Spawn(target, origin + SweepInsideTargetOffset, Find.CurrentMap);
            HoldSweepTarget();
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == target, "target can be manually relocked after returning inside the arc");
            sweepApiPhase = SweepApiPhase.RotateLock;
            Next(SweepApiStep);
        }

        private static void TickRotateLock(int age)
        {
            if (age < 2) return;
            turret.SetSweepHeading(0f);
            Check(Mathf.Abs(Mathf.DeltaAngle(turret.SweepHeading, 0f)) < 0.01f,
                "sweep heading rotates to north");
            Check(turret.LockedTarget == null, "rotating releases a lock outside the new arc");
            turret.SelectManualTarget(target);
            Check(turret.LockedTarget == null, "manual selection enforces the rotated arc");
            Write("sweep-lock-boundary PASS; narrowing-and-rotation-release=true");

            target.DeSpawn();
            target = null;
            foreach (Building_Searchlight lamp in lamps.ToList())
                if (!lamp.Destroyed) lamp.Destroy(DestroyMode.Vanish);
            lamps.Clear();
            BeginEnemyStandby();
        }

        private static void HoldSweepTarget()
        {
            Job hold = JobMaker.MakeJob(JobDefOf.Wait);
            hold.expiryInterval = 10000;
            target.jobs.StartJob(hold, JobCondition.InterruptForced);
            target.pather.StopDead();
        }

        private static void ResetSweepSamples()
        {
            sweepMinOffset = float.MaxValue;
            sweepMaxOffset = float.MinValue;
            sweepSawPositiveEdge = false;
            sweepSawNegativeEdge = false;
            sweepSawNorth = false;
        }

        private static void SampleSweep(string context)
        {
            float offset = Mathf.DeltaAngle(turret.SweepHeading, turret.AimHeading);
            float halfAngle = turret.SweepAngle * 0.5f;
            Check(IsFinite(offset) && IsFinite(halfAngle), context + " has finite geometry");
            Check(Mathf.Abs(offset) <= halfAngle + 0.75f,
                context + " remains within its configured half-angle (offset=" + offset + ")");
            sweepMinOffset = Mathf.Min(sweepMinOffset, offset);
            sweepMaxOffset = Mathf.Max(sweepMaxOffset, offset);
            sweepSawPositiveEdge |= offset >= halfAngle - 1.0f;
            sweepSawNegativeEdge |= offset <= -halfAngle + 1.0f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
