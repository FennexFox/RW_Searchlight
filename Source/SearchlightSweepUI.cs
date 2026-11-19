using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    public partial class Building_Searchlight
    {
        private SweepEditor editor;
        private readonly List<IntVec3> reachable = new List<IntVec3>();
        private readonly List<IntVec3> sectorCells = new List<IntVec3>();
        private float nextVisibilityRefresh;

        private Gizmo CreateSweepArcCommand() => new Command_Action
        {
            defaultLabel = "DS_AdjustSweep".Translate(),
            defaultDesc = "DS_AdjustSweepDesc".Translate(),
            icon = SearchlightIcons.AdjustSweep,
            action = () =>
            {
                editor = new SweepEditor(this);
                Find.WindowStack.Add(editor);
            }
        };

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (!Spawned || mode != SearchlightMode.Sweep) return;
            float center = editor?.Heading ?? SweepHeading;
            float width = editor?.Width ?? SweepAngle;
            Vector3 origin = Position.ToVector3Shifted();
            origin.y = AltitudeLayer.MetaOverlays.AltitudeFor();
            Vector3 edge = origin + Vector3.forward.RotatedBy(center - width / 2f) * fixedRange;
            if (width < 360f) GenDraw.DrawLineBetween(origin, edge);
            int segments = Mathf.Max(1, Mathf.CeilToInt(width / 5f));
            for (int i = 1; i <= segments; i++)
            {
                float angle = center - width / 2f + width * i / segments;
                Vector3 next = origin + Vector3.forward.RotatedBy(angle) * fixedRange;
                GenDraw.DrawLineBetween(edge, next);
                edge = next;
            }
            if (width < 360f) GenDraw.DrawLineBetween(origin, edge);
            GenDraw.DrawLineBetween(origin, origin + Vector3.forward.RotatedBy(center) * fixedRange);
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                DrawReachableCells(center, width);
            else nextVisibilityRefresh = 0f;
        }

        private void DrawReachableCells(float center, float width)
        {
            List<IntVec3> cells = GetReachableCells(center, width);
            if (cells.Count > 0) GenDraw.DrawFieldEdges(cells, Color.green);
        }

        private List<IntVec3> GetReachableCells(float center, float width)
        {
            sectorCells.Clear();
            if (RequiresOpenSky && Position.Roofed(Map)) return sectorCells;
            // Visibility is independent of pointer movement. Refresh periodically for moving doors.
            if (Time.realtimeSinceStartup >= nextVisibilityRefresh)
            {
                nextVisibilityRefresh = Time.realtimeSinceStartup + 0.5f;
                reachable.Clear();
                int radius = (int)BeamRange;
                for (int x = -radius; x <= radius; x++)
                for (int z = -radius; z <= radius; z++)
                {
                    int d2 = x * x + z * z;
                    if (d2 == 0 || d2 > radius * radius) continue;
                    IntVec3 cell = Position + new IntVec3(x, 0, z);
                    if (cell.InBounds(Map) && !cell.Fogged(Map) && BeamField.Clear(Map, Position, cell))
                        reachable.Add(cell);
                }
            }
            foreach (IntVec3 cell in reachable)
                if (Mathf.Abs(Mathf.DeltaAngle(center, HeadingTo(cell))) <= width / 2f)
                    sectorCells.Add(cell);
            return sectorCells;
        }

        private sealed class SweepEditor : Window
        {
            private readonly Building_Searchlight light;
            private bool editWidth;
            private float anchorDistance;
            private float anchorWidth;
            public float Heading { get; private set; }
            public float Width { get; private set; }
            public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight);
            protected override float Margin => 0f;

            public SweepEditor(Building_Searchlight light)
            {
                this.light = light;
                Heading = light.SweepHeading;
                Width = light.SweepAngle;
                doWindowBackground = drawShadow = closeOnAccept = false;
                absorbInputAroundWindow = true;
                preventCameraMotion = false;
            }

            public override void DoWindowContents(Rect rect)
            {
                if (!light.Spawned || light.Map != Find.CurrentMap || light.mode != SearchlightMode.Sweep ||
                    !Find.Selector.IsSelected(light))
                {
                    Close();
                    return;
                }
                Vector3 delta = UI.MouseMapPosition() - light.Position.ToVector3Shifted();
                float distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
                if (distance > 0.1f)
                    Heading = Mathf.Repeat(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg, 360f);
                if (editWidth)
                    Width = Mathf.Clamp(anchorWidth + (distance - anchorDistance) * 6f, 1f, 360f);

                Rect hint = new Rect(12f, 12f, Mathf.Min(rect.width - 24f, 760f), 100f);
                Widgets.DrawWindowBackground(hint);
                Widgets.Label(hint.ContractedBy(6f), "DS_SweepEditorValues".Translate(
                    Heading.ToString("0"), Width.ToString("0")) + "\n" +
                    (editWidth ? "DS_SweepWidthHelp" : "DS_SweepEditorHelp").Translate());
                Event ev = Event.current;
                if (ev.type != EventType.MouseDown) return;
                if (ev.button == 2)
                {
                    editWidth = !editWidth;
                    anchorDistance = distance;
                    anchorWidth = Width;
                }
                else if (ev.button == 0 || ev.button == 1)
                {
                    if (ev.button == 0)
                    {
                        // Apply both fields together before revalidating the current target.
                        light.sweepHalfAngle = Width * 0.5f;
                        light.SetSweepHeading(Heading);
                    }
                    Close();
                }
                else return;
                ev.Use();
            }

            public override void PostClose()
            {
                base.PostClose();
                if (light.editor == this) light.editor = null;
            }
        }
    }
}
