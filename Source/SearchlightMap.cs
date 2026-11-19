using System.Collections.Generic;
using Verse;

namespace DirectedSearchlights
{
    public sealed class SearchlightMap : MapComponent
    {
        private static readonly IComparer<Building_Searchlight> ById = Comparer<Building_Searchlight>.Create(
            (first, second) => first.thingIDNumber.CompareTo(second.thingIDNumber));
        private readonly List<Building_Searchlight> lights = new List<Building_Searchlight>();
        private readonly TargetCoordinator targets;
        private readonly DetectionAlerts alerts;
        private bool restoring;

        public SearchlightMap(Map map) : base(map)
        {
            alerts = new DetectionAlerts(map);
            targets = new TargetCoordinator(map, ReportAcquisition);
        }

        internal int ReservationCount => targets.ReservationCount;
        internal int ReservationsFor(Pawn pawn) => targets.ReservationsFor(pawn);
        internal int CandidateSnapshotBuildCount => targets.CandidateSnapshotBuildCount;
        internal int CandidateEvaluationCount => targets.CandidateEvaluationCount;

        public void Register(Building_Searchlight light, bool respawningAfterLoad)
        {
            int index = lights.BinarySearch(light, ById);
            if (index < 0) lights.Insert(~index, light);
            restoring |= respawningAfterLoad;
        }

        public void Unregister(Building_Searchlight light)
        {
            targets.ChangeLock(light, null, -1);
            targets.ResetRedistribution(light);
            lights.Remove(light);
        }

        public override void FinalizeInit()
        {
            if (!restoring) return;
            int tick = Find.TickManager.TicksGame;
            foreach (Building_Searchlight light in lights) targets.Reconcile(light, tick);
            alerts.Restored();
            restoring = false;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars) restoring = true;
            alerts.ExposeData();
        }

        public override void MapComponentTick() => alerts.Tick(Find.TickManager.TicksGame, lights);

        // MapComponentTick runs after normal thing ticks. The first lamp starts
        // the batch so all due lamps have their assignment before they aim.
        internal void BeforeLightTick(int tick) => targets.Tick(tick, lights);
        internal void Reconcile(Building_Searchlight light, int tick) => targets.Reconcile(light, tick);
        internal void ChangeLock(Building_Searchlight light, Pawn pawn, int tick) => targets.ChangeLock(light, pawn, tick);
        internal void ResetRedistribution(Building_Searchlight light) => targets.ResetRedistribution(light);

        private void ReportAcquisition(Building_Searchlight light, Pawn pawn, int tick)
        {
            if (!restoring) alerts.Acquired(light, pawn, tick);
        }

        public override void MapComponentDraw()
        {
            if (Find.CurrentMap != map) return;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            foreach (Building_Searchlight light in lights)
                if (light.BeamVisibleIn(view)) light.DrawBeam();
            alerts.Draw(lights);
        }

        public override void MapRemoved()
        {
            foreach (Building_Searchlight light in lights)
            {
                light.ApplyLock(null, -1);
                light.DisposeRenderResources();
            }
            lights.Clear();
            targets.Clear();
            alerts.Clear();
        }
    }
}
