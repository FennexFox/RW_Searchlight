using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    public enum AssignmentPolicy { Independent, Spread, Concentrate }
    public enum DetectionAlertMode { Off, Visual, VisualAndSound }

    internal static class CoordinationOptions
    {
        internal const string PolicyKey = "DS_Policy";
        internal const string AlertModeKey = "DS_AlertMode";

        internal static void Show<T>(string keyPrefix, Predicate<T> isSelected, Action<T> apply)
            where T : struct, Enum
        {
            var options = new List<FloatMenuOption>();
            foreach (T choice in Enum.GetValues(typeof(T)))
            {
                string label = (keyPrefix + choice).Translate();
                if (isSelected(choice)) label += " ✓";
                options.Add(new FloatMenuOption(label, () => apply(choice)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    public partial class Building_Searchlight
    {
        private IEnumerable<Gizmo> CoordinationGizmos()
        {
            yield return CreateCoordinationCommand("DS_AssignmentPolicy", CoordinationOptions.PolicyKey,
                SearchlightIcons.TargetSharing, light => light.Policy, (light, choice) => light.SetPolicy(choice));
            yield return CreateCoordinationCommand("DS_DetectionAlerts", CoordinationOptions.AlertModeKey,
                SearchlightIcons.DetectionAlerts, light => light.AlertMode, (light, choice) => light.SetAlertMode(choice));
        }

        private Gizmo CreateCoordinationCommand<T>(string key, string optionKey, Texture2D icon,
            Func<Building_Searchlight, T> current, Action<Building_Searchlight, T> change)
            where T : struct, Enum
        {
            return new Command_Action
            {
                defaultLabel = key.Translate(),
                defaultDesc = (key + "Desc").Translate(),
                icon = icon,
                alsoClickIfOtherInGroupClicked = false,
                action = () =>
                {
                    Building_Searchlight[] lights = Find.Selector.SelectedObjects
                        .OfType<Building_Searchlight>().ToArray();
                    CoordinationOptions.Show<T>(optionKey,
                        choice => lights.All(light => current(light).Equals(choice)),
                        choice =>
                        {
                            foreach (Building_Searchlight light in lights)
                                if (light.Spawned) change(light, choice);
                        });
                }
            };
        }
    }
}
