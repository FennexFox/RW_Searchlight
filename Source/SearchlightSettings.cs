using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    public sealed class SearchlightSettings : ModSettings
    {
        private float diameter = 4f;
        private DetectionAlertMode defaultAlertMode = DetectionAlertMode.Visual;
        public static float Diameter => SearchlightsMod.Settings?.diameter ?? 4f;
        public static DetectionAlertMode DefaultAlertMode =>
            SearchlightsMod.Settings?.defaultAlertMode ?? DetectionAlertMode.Visual;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref diameter, "beamDiameter", 4f);
            Scribe_Values.Look(ref defaultAlertMode, "defaultAlertMode", DetectionAlertMode.Visual);
            diameter = float.IsNaN(diameter) ? 4f : Mathf.Clamp(diameter, 2f, 6f);
        }

        internal void Draw(Rect bounds)
        {
            var listing = new Listing_Standard();
            listing.Begin(bounds);
            var game = Current.Game?.GetComponent<SearchlightLightingGame>();
            if (game != null)
            {
                var mode = game.CurrentMode;
                if (!LightingIntegration.Failed)
                {
                    if (listing.ButtonTextLabeled("DS_LightingMode".Translate(), ("DS_" + mode).Translate()))
                    {
                        if (mode == SearchlightLightingMode.PreciseField)
                            game.SetMode(SearchlightLightingMode.LegacyEndpoint);
                        else if (LightingIntegration.Available)
                            game.SetMode(SearchlightLightingMode.PreciseField);
                        else Log.Warning("[Directed Searchlights] " + LightingIntegration.Status);
                    }
                }
                else
                {
                    listing.Label((LightingIntegration.MissingHarmony
                        ? "DS_LightingRequiresHarmony" : "DS_PreciseUnavailable").Translate());
                }
                listing.Gap();
            }
            listing.Label("DS_BeamDiameter".Translate(diameter.ToString("0.0")));
            diameter = Mathf.Round(listing.Slider(diameter, 2f, 6f) * 10f) / 10f;
            listing.Gap();
            if (listing.ButtonTextLabeled("DS_DefaultAlertMode".Translate(),
                (CoordinationOptions.AlertModeKey + defaultAlertMode).Translate()))
            {
                CoordinationOptions.Show<DetectionAlertMode>(CoordinationOptions.AlertModeKey,
                    choice => choice == defaultAlertMode, choice => defaultAlertMode = choice);
            }
            listing.End();
        }
    }

    public sealed class SearchlightsMod : Mod
    {
        internal static SearchlightSettings Settings { get; private set; }
        public SearchlightsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<SearchlightSettings>();
        }
        public override string SettingsCategory() => "Directed Searchlights";
        public override void DoSettingsWindowContents(Rect inRect) => Settings.Draw(inRect);
    }
}
