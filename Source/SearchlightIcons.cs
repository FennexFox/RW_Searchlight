using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    [StaticConstructorOnStartup]
    internal static class SearchlightIcons
    {
        private const string Folder = "UI/Commands/DirectedSearchlights/";

        internal static readonly Texture2D Fixed = Load("Fixed");
        internal static readonly Texture2D Sweep = Load("Sweep");
        internal static readonly Texture2D Automatic = Load("Automatic");
        internal static readonly Texture2D AdjustSweep = Load("AdjustSweep");
        internal static readonly Texture2D SelectTarget = Load("SelectTarget");
        internal static readonly Texture2D ClearTarget = Load("ClearTarget");
        internal static readonly Texture2D SetDirection = Load("SetDirection");
        internal static readonly Texture2D TargetSharing = Load("TargetSharing");
        internal static readonly Texture2D DetectionAlerts = Load("DetectionAlerts");

        private static Texture2D Load(string name) => ContentFinder<Texture2D>.Get(Folder + name);
    }
}
