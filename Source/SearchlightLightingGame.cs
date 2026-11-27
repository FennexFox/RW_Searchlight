using System;
using Verse;

namespace DirectedSearchlights
{
    public enum SearchlightLightingMode { LegacyEndpoint, PreciseField }

    public sealed class SearchlightLightingGame : GameComponent
    {
        private SearchlightLightingMode mode;

        public SearchlightLightingGame(Game game) { }

        public SearchlightLightingMode CurrentMode => mode;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref mode, "searchlightLightingMode", SearchlightLightingMode.LegacyEndpoint);
            if (Scribe.mode != LoadSaveMode.LoadingVars) return;
            if (!Enum.IsDefined(typeof(SearchlightLightingMode), mode))
                mode = SearchlightLightingMode.LegacyEndpoint;
            if (mode == SearchlightLightingMode.PreciseField && !LightingIntegration.Available)
            {
                Log.Warning("[Directed Searchlights] " + "DS_PreciseUnavailable".Translate() + " " + LightingIntegration.Status);
                mode = SearchlightLightingMode.LegacyEndpoint;
            }
        }

        public void SetMode(SearchlightLightingMode next)
        {
            if (!Enum.IsDefined(typeof(SearchlightLightingMode), next))
                throw new ArgumentOutOfRangeException(nameof(next));
            if (mode == next) return;
            if (next == SearchlightLightingMode.PreciseField && !LightingIntegration.Available)
                throw new InvalidOperationException(LightingIntegration.Status);

            // Both passes run synchronously. Flush vanilla's accumulated colors at
            // the transaction boundary even when the player changes modes while paused.
            foreach (Map map in Find.Maps) map.GetComponent<SearchlightMap>().WithdrawLighting();
            mode = next;
            foreach (Map map in Find.Maps)
            {
                SearchlightMap searchlights = map.GetComponent<SearchlightMap>();
                searchlights.SetPreciseShadowTracking(next == SearchlightLightingMode.PreciseField);
                searchlights.RestoreLighting();
                map.glowGrid.GlowGridUpdate_First();
            }
            if (next == SearchlightLightingMode.LegacyEndpoint) LightingIntegration.Release();
        }
    }
}
