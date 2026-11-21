using System.Collections.Generic;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Verse;

namespace DirectedSearchlights
{
    /// <summary>Optional Harmony bridge for sparse per-cell accumulated glow.</summary>
    internal static class LightingIntegration
    {
        private const string HarmonyOwnerId = "directedsearchlights.lighting";
        private static readonly object InitializeLock = new object();
        private static readonly ConditionalWeakTable<GlowGrid, MapState> States =
            new ConditionalWeakTable<GlowGrid, MapState>();

        private static volatile bool initializationAttempted;
        private static volatile bool available;
        private static string status = "Not initialized.";
        private static MethodInfo unpatchMethod;

        internal static bool Available
        {
            get
            {
                EnsureInitialized();
                return available;
            }
        }

        internal static string Status
        {
            get
            {
                EnsureInitialized();
                return Volatile.Read(ref status);
            }
        }

        /// <summary>
        /// Sets one grayscale raw RGB channel value. Zero removes the entry.
        /// Accumulated-glow reads are O(1); changed cells are dirtied on the
        /// owning GlowGrid's update thread.
        /// </summary>
        internal static void SetCell(Map map, int index, byte intensity)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if ((uint)index >= (uint)map.cellIndices.NumGridCells) throw new ArgumentOutOfRangeException(nameof(index));

            EnsureInitialized();
            if (!available) throw new InvalidOperationException(Status);

            GlowGrid glowGrid = map.glowGrid;
            if (glowGrid == null) throw new InvalidOperationException("Map has no GlowGrid.");
            MapState state = States.GetValue(glowGrid, _ => new MapState(map));
            lock (state.SyncRoot)
            {
                bool hadPrevious = state.Intensities.TryGetValue(index, out byte previous);
                if (intensity == 0)
                {
                    if (!hadPrevious) return;
                    state.Intensities.TryRemove(index, out _);
                }
                else
                {
                    if (hadPrevious && previous == intensity) return;
                    state.Intensities[index] = intensity;
                }

                state.MarkDirty(index);
            }
        }

        /// <summary>Removes this map's contributions and dirties their cells.</summary>
        internal static void ClearMap(Map map)
        {
            if (map == null || map.glowGrid == null || !States.TryGetValue(map.glowGrid, out MapState state)) return;

            lock (state.SyncRoot)
            {
                foreach (KeyValuePair<int, byte> contribution in state.Intensities)
                {
                    state.MarkDirty(contribution.Key);
                }
                state.Intensities.Clear();
            }
        }

        private static void EnsureInitialized()
        {
            if (initializationAttempted) return;
            lock (InitializeLock)
            {
                if (initializationAttempted) return;
                initializationAttempted = true;

                try
                {
                    string gameVersion = VersionControl.CurrentVersionString ?? string.Empty;
                    bool is16 = gameVersion.StartsWith("1.6.", StringComparison.Ordinal);
                    bool is17 = gameVersion.StartsWith("1.7.", StringComparison.Ordinal);
                    if (!is16 && !is17)
                    {
                        SetStatus("Unavailable: RimWorld " + gameVersion + " has no verified lighting hook profile.");
                        return;
                    }

                    Assembly harmonyAssembly = FindHarmonyAssembly();
                    if (harmonyAssembly == null)
                    {
                        SetStatus("Unavailable: optional Harmony (0Harmony) is not loaded; vanilla lighting is unchanged.");
                        return;
                    }

                    Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", false);
                    Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", false);
                    if (harmonyType == null || harmonyMethodType == null)
                    {
                        SetStatus("Unavailable: the loaded 0Harmony assembly does not expose HarmonyLib.Harmony and HarmonyLib.HarmonyMethod.");
                        return;
                    }

                    Type glowGridType = typeof(Map).Assembly.GetType("Verse.GlowGrid", false);
                    MethodInfo getAccumulatedGlow = glowGridType?.GetMethod(
                        "GetAccumulatedGlowAt",
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(int), typeof(bool) },
                        null);
                    MethodInfo updateFirst = glowGridType?.GetMethod(
                        "GlowGridUpdate_First",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (getAccumulatedGlow == null || updateFirst == null)
                    {
                        SetStatus("Unavailable: RimWorld " + gameVersion + " does not match its verified GlowGrid signatures.");
                        return;
                    }

                    MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);
                    unpatchMethod = harmonyType.GetMethod(
                        "Unpatch",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(MethodBase), typeof(MethodInfo) },
                        null);
                    if (patchMethod == null || unpatchMethod == null)
                    {
                        SetStatus("Unavailable: loaded Harmony lacks compatible Patch/Unpatch methods.");
                        return;
                    }

                    object harmony = Activator.CreateInstance(harmonyType, new object[] { HarmonyOwnerId });
                    MethodInfo postfixMethod = typeof(LightingIntegration).GetMethod(
                        nameof(AccumulatedGlowPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                    MethodInfo prefixMethod = typeof(LightingIntegration).GetMethod(
                        nameof(GlowGridUpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                    object postfix = Activator.CreateInstance(harmonyMethodType, new object[] { postfixMethod });
                    object prefix = Activator.CreateInstance(harmonyMethodType, new object[] { prefixMethod });
                    try
                    {
                        InvokePatch(patchMethod, harmony, updateFirst, prefix, null);
                        InvokePatch(patchMethod, harmony, getAccumulatedGlow, null, postfix);
                    }
                    catch
                    {
                        TryUnpatch(harmony, getAccumulatedGlow, postfixMethod);
                        TryUnpatch(harmony, updateFirst, prefixMethod);
                        throw;
                    }

                    available = true;
                    SetStatus("Available: RimWorld " + (is16 ? "1.6" : "1.7") +
                        " hooks GlowGrid.GetAccumulatedGlowAt(Int32, Boolean); changed cells use DirtyCell(IntVec3).");
                }
                catch (Exception exception)
                {
                    SetStatus("Unavailable: could not install the optional GlowGrid hooks: " + Unwrap(exception).Message);
                }
            }
        }

        private static Assembly FindHarmonyAssembly()
        {
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (loaded[i].GetType("HarmonyLib.Harmony", false) != null) return loaded[i];
            }

            try
            {
                Assembly harmony = Assembly.Load("0Harmony");
                return harmony.GetType("HarmonyLib.Harmony", false) == null ? null : harmony;
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType)
        {
            MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "Patch") continue;
                ParameterInfo[] parameters = methods[i].GetParameters();
                if (parameters.Length == 5 && parameters[0].ParameterType == typeof(MethodBase) &&
                    parameters[1].ParameterType == harmonyMethodType && parameters[2].ParameterType == harmonyMethodType &&
                    parameters[3].ParameterType == harmonyMethodType && parameters[4].ParameterType == harmonyMethodType)
                {
                    return methods[i];
                }
            }
            return null;
        }

        private static void InvokePatch(MethodInfo patchMethod, object harmony, MethodBase target, object prefix, object postfix)
        {
            patchMethod.Invoke(harmony, new object[] { target, prefix, postfix, null, null });
        }

        private static void TryUnpatch(object harmony, MethodBase target, MethodInfo patch)
        {
            try
            {
                unpatchMethod.Invoke(harmony, new object[] { target, patch });
            }
            catch
            {
                // Preserve the original installation error; the hook remains unavailable.
            }
        }

        private static void AccumulatedGlowPostfix(object __instance, int index, ref Color32 __result)
        {
            if (__instance == null || index < 0 ||
                !States.TryGetValue((GlowGrid)__instance, out MapState state) ||
                (uint)index >= (uint)state.Map.cellIndices.NumGridCells ||
                state.Map.terrainGrid.TerrainAt(index).exposesToVacuum ||
                !state.Intensities.TryGetValue(index, out byte intensity))
            {
                return;
            }

            // Max-combine raw RGB while preserving vanilla alpha and overlight.
            if (__result.r < intensity) __result.r = intensity;
            if (__result.g < intensity) __result.g = intensity;
            if (__result.b < intensity) __result.b = intensity;
        }

        private static void GlowGridUpdatePrefix(object __instance)
        {
            if (__instance == null || !States.TryGetValue((GlowGrid)__instance, out MapState state)) return;

            while (state.PendingDirty.TryDequeue(out int index))
            {
                state.PendingDirtySet.TryRemove(index, out _);
                if ((uint)index < (uint)state.Map.cellIndices.NumGridCells)
                {
                    state.Map.glowGrid.DirtyCell(state.Map.cellIndices.IndexToCell(index));
                }
            }
        }

        private static void SetStatus(string value)
        {
            Volatile.Write(ref status, value);
        }

        private static Exception Unwrap(Exception exception)
        {
            return exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
        }

        private sealed class MapState
        {
            internal readonly Map Map;
            internal readonly object SyncRoot = new object();
            internal readonly ConcurrentDictionary<int, byte> Intensities = new ConcurrentDictionary<int, byte>();
            internal readonly ConcurrentDictionary<int, byte> PendingDirtySet = new ConcurrentDictionary<int, byte>();
            internal readonly ConcurrentQueue<int> PendingDirty = new ConcurrentQueue<int>();

            internal MapState(Map map)
            {
                Map = map;
            }

            internal void MarkDirty(int index)
            {
                if (PendingDirtySet.TryAdd(index, 0)) PendingDirty.Enqueue(index);
            }
        }
    }
}
