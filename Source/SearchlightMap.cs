using System.Collections.Generic;
using Verse;

namespace DirectedSearchlights
{
    public sealed class SearchlightMap : MapComponent
    {
        private readonly HashSet<Building_Searchlight> lights = new HashSet<Building_Searchlight>();

        public SearchlightMap(Map map) : base(map) { }

        public void Register(Building_Searchlight light) => lights.Add(light);
        public void Unregister(Building_Searchlight light) => lights.Remove(light);

        public override void MapComponentDraw()
        {
            if (Find.CurrentMap != map) return;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            foreach (Building_Searchlight light in lights)
                if (light.BeamVisibleIn(view)) light.DrawBeam();
        }

        public override void MapRemoved()
        {
            foreach (Building_Searchlight light in lights)
                light.DisposeRenderResources();
            lights.Clear();
        }
    }
}
