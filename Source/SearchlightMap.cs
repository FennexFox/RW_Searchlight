using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
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
        private SearchlightLightingGame lightingGame;
        private BeamLightingMap lighting;
        private bool restoring;
        private readonly bool usesAtmosphereOverhaul;
        private int lastSolarTick = int.MinValue;
        private bool sunAboveHorizon;
        private ThingListChangedCallbacks obstacleCallbacks;
        private int edificeRevision;
        private readonly Dictionary<Thing, CellRect> shadowThingRects = new Dictionary<Thing, CellRect>();
        private readonly List<Pawn> shadowPawns = new List<Pawn>();
        private readonly HashSet<Building_Searchlight> shadowInvalidatedLightsThisTick = new HashSet<Building_Searchlight>();
        private bool shadowThingsInitialized;
        private int lastShadowPollTick = int.MinValue;
        private int shadowDiagnosticsTick = int.MinValue;

        internal long ShadowInvalidationNotifications { get; private set; }
        internal long ShadowInvalidatedLampCount { get; private set; }
        internal long ShadowInvalidationRebuildCount => lights.Sum(light => (long)light.ShadowInvalidationRebuildCount);

        public SearchlightMap(Map map) : base(map)
        {
            usesAtmosphereOverhaul = LoadedModManager.RunningModsListForReading.Any(mod =>
                string.Equals(mod.PackageIdPlayerFacing, "acutus.AtmosphereOverhaul.release", StringComparison.OrdinalIgnoreCase));
            alerts = new DetectionAlerts(map);
            targets = new TargetCoordinator(map, ReportAcquisition);
        }

        internal bool IsDaylight
        {
            get
            {
                if (!usesAtmosphereOverhaul) return GenLocalDate.HourOfDay(map) is >= 6 and < 18;
                int tick = Find.TickManager.TicksAbs;
                if (lastSolarTick != tick)
                {
                    // AO maps sin(solar elevation) from [-0.05, 0.7] to celestial glow [0, 1].
                    // At the horizon that glow is 1/15, independent of weather and artificial light.
                    sunAboveHorizon = GenCelestial.CelestialSunGlow(map, tick) > 1f / 15f;
                    lastSolarTick = tick;
                }
                return sunAboveHorizon;
            }
        }

        internal int ReservationCount => targets.ReservationCount;
        internal bool PreciseLighting => (lightingGame ??= Current.Game.GetComponent<SearchlightLightingGame>())?.CurrentMode
            == SearchlightLightingMode.PreciseField;
        internal BeamLightingMap Lighting => lighting ??= new BeamLightingMap(map);
        internal void WithdrawField(Building_Searchlight light) => lighting?.Withdraw(light);

        internal void WithdrawLighting()
        {
            lighting?.Clear();
            foreach (Building_Searchlight light in lights) light.RemoveSpot();
        }

        internal void RestoreLighting()
        {
            foreach (Building_Searchlight light in lights) light.RefreshBeamAndSpot();
        }
        internal int ReservationsFor(Pawn pawn) => targets.ReservationsFor(pawn);
        internal int CandidateSnapshotBuildCount => targets.CandidateSnapshotBuildCount;
        internal int CandidateEvaluationCount => targets.CandidateEvaluationCount;

        internal int EdificeRevision
        {
            get
            {
                if (obstacleCallbacks == null)
                    EnsureThingCallbacks();
                return edificeRevision;
            }
        }

        private void EnsureThingCallbacks()
        {
            if (obstacleCallbacks != null) return;
            obstacleCallbacks = map.thingListChangedCallbacks;
            obstacleCallbacks.onThingAdded += ThingAdded;
            obstacleCallbacks.onThingRemoved += ThingRemoved;
        }

        private void ThingAdded(Thing thing)
        {
            EdificeChanged(thing);
            if (!PreciseLighting) return;
            if (!TrackShadowThing(thing)) return;
            InvalidateShadowRect(thing.OccupiedRect());
        }

        private void ThingRemoved(Thing thing)
        {
            EdificeChanged(thing);
            if (!PreciseLighting) return;
            if (shadowThingRects.TryGetValue(thing, out CellRect oldRect))
            {
                ForgetShadowThing(thing);
                InvalidateShadowRect(oldRect);
                return;
            }

            if (BeamShadowField.Opacity(thing) > 0f && thing.Position.InBounds(map))
                InvalidateShadowRect(thing.OccupiedRect());
        }

        private void EdificeChanged(Thing thing)
        {
            if (thing is Building && thing.def.IsEdifice()) edificeRevision++;
        }

        private bool TrackShadowThing(Thing thing)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned || thing.Map != map ||
                (thing is not Pawn && BeamShadowField.Opacity(thing) <= 0f)) return false;

            // Keep pawns whose current growth stage gives them zero opacity:
            // their cell moves must still be observed when their body size changes.
            if (thing is Pawn pawn && !shadowThingRects.ContainsKey(pawn)) shadowPawns.Add(pawn);
            shadowThingRects[thing] = thing.OccupiedRect();
            return true;
        }

        private void ForgetShadowThing(Thing thing)
        {
            shadowThingRects.Remove(thing);
            if (thing is Pawn pawn) shadowPawns.Remove(pawn);
        }

        private void InitializeShadowThings()
        {
            if (!PreciseLighting) return;
            EnsureThingCallbacks();
            if (shadowThingsInitialized) return;
            foreach (Thing thing in map.listerThings.AllThings) TrackShadowThing(thing);
            shadowThingsInitialized = true;
        }

        internal void SetPreciseShadowTracking(bool enabled)
        {
            lastShadowPollTick = int.MinValue;
            if (enabled)
            {
                shadowThingsInitialized = false;
                InitializeShadowThings();
                return;
            }

            shadowThingRects.Clear();
            shadowPawns.Clear();
            shadowInvalidatedLightsThisTick.Clear();
            shadowThingsInitialized = false;
        }

        private void PollShadowPawns()
        {
            for (int i = shadowPawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = shadowPawns[i];
                if (pawn.Destroyed || !pawn.Spawned || pawn.Map != map)
                {
                    if (shadowThingRects.TryGetValue(pawn, out CellRect previousRect))
                        InvalidateShadowRect(previousRect);
                    ForgetShadowThing(pawn);
                    continue;
                }

                CellRect currentRect = pawn.OccupiedRect();
                if (!shadowThingRects.TryGetValue(pawn, out CellRect oldRect))
                {
                    shadowThingRects[pawn] = currentRect;
                    InvalidateShadowRect(currentRect);
                    continue;
                }
                if (SameRect(oldRect, currentRect)) continue;

                shadowThingRects[pawn] = currentRect;
                InvalidateShadowRect(oldRect);
                InvalidateShadowRect(currentRect);
            }
        }

        private static bool SameRect(CellRect left, CellRect right)
            => left.minX == right.minX && left.minZ == right.minZ &&
                left.maxX == right.maxX && left.maxZ == right.maxZ;

        private void InvalidateShadowRect(CellRect rect)
        {
            int minX = Math.Max(0, rect.minX);
            int minZ = Math.Max(0, rect.minZ);
            int maxX = Math.Min(map.Size.x - 1, rect.maxX);
            int maxZ = Math.Min(map.Size.z - 1, rect.maxZ);
            if (minX > maxX || minZ > maxZ) return;
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
                InvalidateShadowCell(new IntVec3(x, 0, z));
        }

        private void InvalidateShadowCell(IntVec3 cell)
        {
            if (!cell.IsValid || !cell.InBounds(map) || !PreciseLighting) return;
            if (shadowDiagnosticsTick != Find.TickManager.TicksGame)
            {
                shadowDiagnosticsTick = Find.TickManager.TicksGame;
                shadowInvalidatedLightsThisTick.Clear();
            }

            ShadowInvalidationNotifications++;
            foreach (Building_Searchlight light in lights)
            {
                if (!light.InvalidateShadows(cell)) continue;
                if (shadowInvalidatedLightsThisTick.Add(light)) ShadowInvalidatedLampCount++;
            }
        }

        public void Register(Building_Searchlight light) => Register(light, false);

        internal void Register(Building_Searchlight light, bool respawningAfterLoad)
        {
            int index = lights.BinarySearch(light, ById);
            if (index < 0) lights.Insert(~index, light);
            restoring |= respawningAfterLoad;
        }

        public void Unregister(Building_Searchlight light)
        {
            WithdrawField(light);
            targets.ChangeLock(light, null, -1);
            targets.ResetRedistribution(light);
            lights.Remove(light);
        }

        public override void FinalizeInit()
        {
            if (PreciseLighting) InitializeShadowThings();
            if (!restoring) return;
            int tick = Find.TickManager.TicksGame;
            foreach (Building_Searchlight light in lights) targets.Reconcile(light, tick);
            alerts.Restored(lights);
            restoring = false;
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars) restoring = true;
            alerts.ExposeData();
        }

        public override void MapComponentTick() => alerts.Tick(Find.TickManager.TicksGame, lights);

        // Poll once before any lamp samples its beam. This also catches moves
        // made between simulation ticks, after the previous map post-tick.
        internal void BeforeLightTick(int tick)
        {
            if (lastShadowPollTick != tick)
            {
                lastShadowPollTick = tick;
                if (PreciseLighting)
                {
                    if (!shadowThingsInitialized) InitializeShadowThings();
                    PollShadowPawns();
                }
                else if (shadowThingsInitialized) SetPreciseShadowTracking(false);
            }
            targets.Tick(tick, lights);
        }

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
            WithdrawLighting();
            LightingIntegration.ForgetMap(map);
            if (obstacleCallbacks != null)
            {
                obstacleCallbacks.onThingAdded -= ThingAdded;
                obstacleCallbacks.onThingRemoved -= ThingRemoved;
                obstacleCallbacks = null;
            }
            foreach (Building_Searchlight light in lights)
            {
                light.ApplyLock(null, -1);
                light.DisposeRenderResources();
            }
            lights.Clear();
            targets.Clear();
            alerts.Clear();
            shadowThingRects.Clear();
            shadowPawns.Clear();
            shadowInvalidatedLightsThisTick.Clear();
            shadowThingsInitialized = false;
        }
    }
}
