using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    public sealed class SearchlightSettings : ModSettings
    {
        private float diameter = 4f;
        public static float Diameter => SearchlightsMod.Settings?.diameter ?? 4f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref diameter, "beamDiameter", 4f);
            diameter = float.IsNaN(diameter) ? 4f : Mathf.Clamp(diameter, 2f, 6f);
        }

        internal void Draw(Rect bounds)
        {
            var listing = new Listing_Standard();
            listing.Begin(bounds);
            listing.Label("DS_BeamDiameter".Translate(diameter.ToString("0.0")));
            diameter = Mathf.Round(listing.Slider(diameter, 2f, 6f) * 10f) / 10f;
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
