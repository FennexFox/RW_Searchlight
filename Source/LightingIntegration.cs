using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
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
        private static Action releaseHooks;
        internal static bool Failed => initializationAttempted && !available;
        internal static bool MissingHarmony { get; private set; }

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
            var state = GetState(map);
            lock (state) state.Set(index, intensity);
        }

        // The simulation owns the change buffer. Queries see a complete published pose.
        internal static void SetCells(Map map, Dictionary<int, byte> changes)
        {
            if (changes.Count == 0) return;
            var state = GetState(map);
            lock (state)
            {
                foreach (var entry in changes) state.Set(entry.Key, entry.Value);
            }
        }

        private static MapState GetState(Map map) => States.TryGetValue(map.glowGrid, out var state)
            ? state : AddState(map);

        private static MapState AddState(Map map) => States.GetValue(map.glowGrid, _ => new MapState(map));

        internal static void ClearMap(Map map)
        {
            if (map?.glowGrid == null || !States.TryGetValue(map.glowGrid, out var state)) return;
            lock (state)
            {
                foreach (int index in state.Intensities.Keys) state.Pending.Add(index);
                state.Intensities.Clear();
            }
        }

        internal static bool MeasureFlush { get; set; }
        internal static long FlushTicks(Map map) =>
            States.TryGetValue(map.glowGrid, out var state) ? state.FlushTicks : 0L;

        internal static bool HasMapState(Map map) => States.TryGetValue(map.glowGrid, out _);

        internal static void ForgetMap(Map map) => States.Remove(map.glowGrid);

        private static void EnsureInitialized()
        {
            if (initializationAttempted) return;
            lock (InitializeLock)
            {
                if (initializationAttempted) return;
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
                        MissingHarmony = true;
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

                    releaseHooks = () =>
                    {
                        unpatchMethod.Invoke(harmony, new object[] { getAccumulatedGlow, postfixMethod });
                        unpatchMethod.Invoke(harmony, new object[] { updateFirst, prefixMethod });
                    };
                    available = true;
                    SetStatus("Available: RimWorld " + (is16 ? "1.6" : "1.7") +
                        " hooks GlowGrid.GetAccumulatedGlowAt(Int32, Boolean); changed cells invalidate ground meshes and glow listeners.");
                }
                catch (Exception exception)
                {
                    SetStatus("Unavailable: could not install the optional GlowGrid hooks: " + Unwrap(exception).Message);
                }
                finally { initializationAttempted = true; }
            }
        }

        internal static void Release()
        {
            lock (InitializeLock)
            {
                if (!available) return;
                available = false;
                try
                {
                    releaseHooks();
                    initializationAttempted = false;
                }
                catch (Exception ex)
                {
                    SetStatus("Could not remove lighting hooks: " + Unwrap(ex).Message);
                    Log.Warning("[Directed Searchlights] " + status);
                }
                finally { releaseHooks = null; }
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
            if (!available || !States.TryGetValue((GlowGrid)__instance, out var state)) return;
            byte intensity;
            lock (state)
                if (!state.Intensities.TryGetValue(index, out intensity)) return;
            if (state.Map.terrainGrid.TerrainAt(index).exposesToVacuum) return;
            if (__result.r < intensity) __result.r = intensity;
            if (__result.g < intensity) __result.g = intensity;
            if (__result.b < intensity) __result.b = intensity;
        }

        private static void GlowGridUpdatePrefix(GlowGrid __instance)
        {
            if (!States.TryGetValue(__instance, out var state)) return;
            lock (state)
            {
                if (state.Pending.Count == 0 || state.Draining.Count != 0) return;
                (state.Draining, state.Pending) = (state.Pending, state.Draining);
            }
            long start = MeasureFlush ? Stopwatch.GetTimestamp() : 0L;
            try
            {
                var map = state.Map;
                foreach (int index in state.Draining)
                {
                    var cell = map.cellIndices.IndexToCell(index);
                    // Only our read-side overlay changed. Vanilla's flood buffers
                    // remain valid. Shared corner vertices can lie in another section.
                    map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.GroundGlow, true, false);
                    map.events.Notify_GlowChanged(cell);
                }
            }
            finally
            {
                state.Draining.Clear();
                if (start != 0L) state.FlushTicks += Stopwatch.GetTimestamp() - start;
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
            internal readonly Dictionary<int, byte> Intensities = new Dictionary<int, byte>();
            internal HashSet<int> Pending = new HashSet<int>();
            internal HashSet<int> Draining = new HashSet<int>();
            internal long FlushTicks;

            internal MapState(Map map) { Map = map; }

            internal void Set(int index, byte intensity)
            {
                Intensities.TryGetValue(index, out byte previous);
                if (intensity == previous) return;
                if (intensity == 0) Intensities.Remove(index);
                else Intensities[index] = intensity;
                Pending.Add(index);
            }
        }
    }
}
