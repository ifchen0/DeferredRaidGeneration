using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeferredRaidGeneration
{
    /// <summary>
    /// Removes the hitch when the camera first reaches a large group of pawns.
    ///
    /// Vanilla builds a pawn's render tree (graphics, materials, apparel nodes) only when the pawn is first drawn, and
    /// in this mod list that takes 2-5 ms per pawn, so a hundred raiders entering the view in one frame freeze the game.
    ///
    /// - Deferred groups build each pawn's render tree in the same step that generates it.
    /// - Everything else (caravans, small raids, loaded saves) is warmed in the background: spawned pawns on the
    ///   current map are built at about 1 ms per frame.
    ///
    /// Zoomed-out atlas baking was measured too (about 0.2 ms per frame baked) and is left to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    public static class PawnRenderWarmup
    {
        public const double WarmupBudgetMs = 1.0;
        private const double SpikeLogMs = 25.0;

        public static bool WarmupDisabled;

        private static readonly AccessTools.FieldRef<DynamicDrawManager, Map> mapRef =
            AccessTools.FieldRefAccess<DynamicDrawManager, Map>("map");

        private static readonly HashSet<Pawn> failed = new HashSet<Pawn>();
        private static int cursor;

        // Development-mode per-frame counters.
        public static bool InWarmup;
        public static int TreesBuilt;
        public static double TreesMs;
        public static int Bakes;
        public static double BakesMs;
        private static long drawStart;

        // Development-mode totals for background warmup, logged once a batch is finished.
        private static int warmedTotal;
        private static double warmedMsTotal;
        private static float warmStartTime;

        [DebugAction("Deferred Raid Generation", "Toggle render warmup (A/B test)", allowedGameStates = AllowedGameStates.Playing)]
        private static void ToggleWarmup()
        {
            WarmupDisabled = !WarmupDisabled;
            Messages.Message(WarmupDisabled ? "Render warmup: OFF (vanilla)." : "Render warmup: ON.", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Makes every pawn on the current map look never-drawn, as after a raid arrives.</summary>
        [DebugAction("Deferred Raid Generation", "Reset pawn graphics on map (test)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ResetGraphics()
        {
            int count = 0;
            foreach (Pawn pawn in Find.CurrentMap.mapPawns.AllPawnsSpawned)
            {
                pawn.Drawer.renderer.renderTree.SetDirty();
                GlobalTextureAtlasManager.TryMarkPawnFrameSetDirty(pawn);
                count++;
            }
            failed.Clear();
            Messages.Message($"Reset graphics of {count} pawns.", MessageTypeDefOf.NeutralEvent, false);
        }

        public static void Prefix()
        {
            TreesBuilt = 0;
            TreesMs = 0;
            Bakes = 0;
            BakesMs = 0;
            drawStart = Prefs.DevMode ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Postfix(DynamicDrawManager __instance)
        {
            if (drawStart != 0)
            {
                double ms = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
                if (ms >= SpikeLogMs)
                    Log.Message($"[DeferredRaidGeneration] Draw spike {ms:F1} ms: {TreesBuilt} render trees built ({TreesMs:F1} ms), "
                        + $"{Bakes} atlas frames baked ({BakesMs:F1} ms).");
            }
            Map map = mapRef(__instance);
            if (!WarmupDisabled && map == Find.CurrentMap)
                Warmup(map);
        }

        /// <summary>Builds the pawn's render tree now unless it is already built. Returns whether it did.</summary>
        public static bool Warm(Pawn pawn)
        {
            PawnRenderer renderer = pawn.Drawer?.renderer;
            if (WarmupDisabled || renderer == null || renderer.renderTree.Resolved || failed.Contains(pawn))
                return false;
            InWarmup = true;
            try
            {
                renderer.EnsureGraphicsInitialized();
            }
            catch (Exception e)
            {
                // Leave the pawn to vanilla, which will try again when it is drawn.
                failed.Add(pawn);
                if (Prefs.DevMode)
                    Log.Warning($"[DeferredRaidGeneration] Render warmup failed for {pawn}: {e.Message}");
            }
            finally
            {
                InWarmup = false;
            }
            return true;
        }

        private static void Warmup(Map map)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            int count = pawns.Count;
            if (count == 0)
                return;
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(WarmupBudgetMs * Stopwatch.Frequency / 1000.0);
            int warmed = 0;
            for (int i = 0; i < count; i++)
            {
                if (cursor >= count)
                    cursor = 0;
                Pawn pawn = pawns[cursor++];
                if (!Warm(pawn))
                    continue;
                warmed++;
                if (Stopwatch.GetTimestamp() - start >= budget)
                    break;
            }
            if (!Prefs.DevMode)
                return;
            if (warmed > 0)
            {
                if (warmedTotal == 0)
                    warmStartTime = Time.realtimeSinceStartup;
                warmedTotal += warmed;
                warmedMsTotal += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            else if (warmedTotal > 0)
            {
                Log.Message($"[DeferredRaidGeneration] Warmed {warmedTotal} render trees in the background: {warmedMsTotal:F0} ms total "
                    + $"({warmedMsTotal / warmedTotal:F2} ms each) over {Time.realtimeSinceStartup - warmStartTime:F1} s.");
                warmedTotal = 0;
                warmedMsTotal = 0;
            }
        }

    }

    /// <summary>Development-mode timing of render trees built while drawing.</summary>
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.EnsureGraphicsInitialized))]
    public static class Patch_EnsureGraphicsInitialized
    {
        public static void Prefix(PawnRenderer __instance, out long __state)
        {
            __state = Prefs.DevMode && !PawnRenderWarmup.InWarmup && !__instance.renderTree.Resolved ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Postfix(long __state)
        {
            if (__state == 0)
                return;
            PawnRenderWarmup.TreesBuilt++;
            PawnRenderWarmup.TreesMs += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>Development-mode timing of atlas frames rendered while drawing.</summary>
    [HarmonyPatch(typeof(PawnRenderer), "GetBlitMeshUpdatedFrame")]
    public static class Patch_GetBlitMeshUpdatedFrame
    {
        public static void Prefix(PawnTextureAtlasFrameSet frameSet, Rot4 rotation, PawnDrawMode drawMode, out long __state)
        {
            __state = Prefs.DevMode && frameSet.isDirty[frameSet.GetIndex(rotation, drawMode)] ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Postfix(long __state)
        {
            if (__state == 0)
                return;
            PawnRenderWarmup.Bakes++;
            PawnRenderWarmup.BakesMs += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
