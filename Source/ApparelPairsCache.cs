using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace DeferredRaidGeneration
{
    /// <summary>
    /// Humanoid Alien Races filters race-restricted apparel out of PawnApparelGenerator.allApparelPairs before every
    /// GenerateStartingApparelFor call: it copies the whole list (thousands of apparel/stuff pairs with a large mod
    /// list), checks every pair, and removes them with a HashSet of ThingStuffPair, a struct without its own
    /// GetHashCode/Equals, so every lookup uses the slow reflection-based ValueType versions. Afterwards it appends
    /// the removed pairs again. Pawn generation calls this about twice per pawn.
    ///
    /// The result only depends on the pawn's race, so the filtered list is built once per race and swapped into
    /// allApparelPairs for the duration of the call; HAR's prefix and postfix are skipped while that happens.
    /// </summary>
    public static class ApparelPairsCache
    {
        public static bool Disabled;

        private static Func<ThingDef, ThingDef, bool> canWear;
        private static AccessTools.FieldRef<List<ThingStuffPair>> allPairs;
        private static readonly Dictionary<ThingDef, List<ThingStuffPair>> filtered = new Dictionary<ThingDef, List<ThingStuffPair>>();
        private static List<ThingStuffPair> source;
        private static int sourceCount;

        // Set while a filtered list is swapped in; HAR's prefix and postfix are skipped in that window.
        private static List<ThingStuffPair> swappedOut;

        // Development-mode timing of GenerateStartingApparelFor, logged every ReportEvery calls.
        private const int ReportEvery = 200;
        private static int calls;
        private static double msTotal;

        [DebugAction("Deferred Raid Generation", "Toggle apparel pair cache (A/B test)", allowedGameStates = AllowedGameStates.Playing)]
        private static void Toggle()
        {
            Disabled = !Disabled;
            calls = 0;
            msTotal = 0;
            Messages.Message(Disabled ? "Apparel pair cache: OFF (Humanoid Alien Races filtering)." : "Apparel pair cache: ON.",
                MessageTypeDefOf.NeutralEvent, false);
        }

        public static void TryPatch(Harmony harmony)
        {
            Type patches = AccessTools.TypeByName("AlienRace.HarmonyPatches");
            MethodInfo harPrefix = patches == null ? null : AccessTools.Method(patches, "GenerateStartingApparelForPrefix");
            MethodInfo harPostfix = patches == null ? null : AccessTools.Method(patches, "GenerateStartingApparelForPostfix");
            MethodInfo harCanWear = AccessTools.Method(AccessTools.TypeByName("AlienRace.RaceRestrictionSettings"), "CanWear",
                new[] { typeof(ThingDef), typeof(ThingDef) });
            if (harPrefix == null || harPostfix == null || harCanWear == null)
            {
                if (patches != null)
                    Log.Warning("[DeferredRaidGeneration] Apparel pair cache not applied: Humanoid Alien Races has changed.");
                return;
            }
            try
            {
                canWear = (Func<ThingDef, ThingDef, bool>)Delegate.CreateDelegate(typeof(Func<ThingDef, ThingDef, bool>), harCanWear);
                allPairs = AccessTools.StaticFieldRefAccess<List<ThingStuffPair>>(AccessTools.Field(typeof(PawnApparelGenerator), "allApparelPairs"));
                var skip = new HarmonyMethod(typeof(ApparelPairsCache), nameof(SkipWhileSwapped));
                harmony.Patch(harPrefix, prefix: skip);
                harmony.Patch(harPostfix, prefix: skip);
                harmony.Patch(AccessTools.Method(typeof(PawnApparelGenerator), nameof(PawnApparelGenerator.GenerateStartingApparelFor)),
                    prefix: new HarmonyMethod(typeof(ApparelPairsCache), nameof(Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ApparelPairsCache), nameof(Finalizer)));
            }
            catch (Exception e)
            {
                Log.Warning("[DeferredRaidGeneration] Apparel pair cache not applied: " + e.Message);
            }
        }

        public static bool SkipWhileSwapped() => swappedOut == null;

        public static void Prefix(Pawn pawn, out long __state)
        {
            __state = Prefs.DevMode ? Stopwatch.GetTimestamp() : 0;
            if (Disabled || swappedOut != null || pawn?.def == null)
                return;
            List<ThingStuffPair> current = allPairs();
            if (current == null)
                return;
            if (current != source || current.Count != sourceCount)
            {
                // The game rebuilt the list (or something else changed it): start over.
                filtered.Clear();
                source = current;
                sourceCount = current.Count;
            }
            if (!filtered.TryGetValue(pawn.def, out List<ThingStuffPair> list))
            {
                list = new List<ThingStuffPair>(current.Count);
                foreach (ThingStuffPair pair in current)
                {
                    if (canWear(pair.thing, pawn.def))
                        list.Add(pair);
                }
                filtered[pawn.def] = list;
            }
            swappedOut = current;
            allPairs() = list;
        }

        public static Exception Finalizer(Exception __exception, long __state)
        {
            if (swappedOut != null)
            {
                allPairs() = swappedOut;
                swappedOut = null;
            }
            if (__state != 0)
            {
                msTotal += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (++calls >= ReportEvery)
                {
                    Log.Message($"[DeferredRaidGeneration] Starting apparel ({(Disabled ? "cache off" : "cache on")}): {msTotal / calls:F2} ms per call over {calls} calls.");
                    calls = 0;
                    msTotal = 0;
                }
            }
            return __exception;
        }
    }
}
