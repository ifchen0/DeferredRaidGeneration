using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace DeferredRaidGeneration
{
    /// <summary>
    /// Quests such as Beggars and Hospitality_Refugee generate all their pawns inside QuestGen.Generate in one frame.
    /// In development mode, quest generations that created pawns and quest arrivals are logged with their duration;
    /// the debug action gives either quest on demand (deferred like a storyteller one).
    /// </summary>
    public static class QuestPawnTiming
    {
        internal static int pawnsGeneratedInQuest;

        private static readonly Dictionary<string, string> TestQuests = new Dictionary<string, string>
        {
            { "Beggars", "Ideology" },
            { "Hospitality_Refugee", "Royalty" },
        };

        [DebugAction("Deferred Raid Generation", "Pawn quest now (test)...", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GiveTestQuest()
        {
            var nodes = new List<DebugActionNode>();
            foreach (KeyValuePair<string, string> entry in TestQuests)
            {
                QuestScriptDef def = DefDatabase<QuestScriptDef>.GetNamedSilentFail(entry.Key);
                if (def == null)
                    continue;
                var node = new DebugActionNode(def.defName);
                // Lodger count is population x 0.25-0.75, so a population override gives larger groups on demand.
                foreach (int population in new[] { 0, 20, 40, 80 })
                {
                    int pop = population;
                    string label = pop == 0 ? "colony population" : $"population {pop}";
                    node.AddChild(new DebugActionNode(label, DebugActionType.Action, () =>
                    {
                        Map map = Find.CurrentMap;
                        if (!DeferredQuests.TryDefer(def, map, pop, () => Give(def, map, pop)))
                            Give(def, map, pop);
                    }));
                }
                nodes.Add(node);
            }
            return nodes;
        }

        private static void Give(QuestScriptDef def, Map map, int population)
        {
            var slate = new Slate();
            slate.Set("points", StorytellerUtility.DefaultThreatPointsNow(map));
            slate.Set("map", map);
            if (population > 0)
                slate.Set("population", population);
            Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(def, slate);
            if (!quest.hidden && def.sendAvailableLetter)
                QuestUtility.SendLetterQuestAvailable(quest);
        }
    }

    [HarmonyPatch(typeof(QuestGen), nameof(QuestGen.Generate))]
    public static class Patch_QuestGen_Generate_Timing
    {
        public static void Prefix(out long __state)
        {
            QuestPawnTiming.pawnsGeneratedInQuest = 0;
            __state = Stopwatch.GetTimestamp();
        }

        public static Exception Finalizer(Exception __exception, QuestScriptDef root, long __state)
        {
            int pawns = QuestPawnTiming.pawnsGeneratedInQuest;
            QuestPawnTiming.pawnsGeneratedInQuest = 0;
            PendingQuest replaying = DeferredQuests.Replaying;
            if (Prefs.DevMode && pawns > 0)
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (replaying == null)
                    Log.Message($"[DeferredRaidGeneration] Quest {root?.defName} generated {pawns} pawns (not deferred) in {ms:F0} ms.");
                else
                    Log.Message($"[DeferredRaidGeneration] Quest {root?.defName} replayed in {ms:F0} ms: {replaying.HandedOver} pre-generated pawns used, {pawns - replaying.HandedOver} generated now.");
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), typeof(PawnGenerationRequest))]
    public static class Patch_PawnGenerator_GeneratePawn_QuestCount
    {
        public static void Postfix()
        {
            if (QuestGen.Working)
                QuestPawnTiming.pawnsGeneratedInQuest++;
        }
    }

    /// <summary>
    /// Breakdown of a quest arrival: joining the player's faction, the arrival itself and the arrival letter.
    /// Development mode only; the Harmony owners of Pawn.SetFaction are listed once.
    /// </summary>
    public static class ArrivalBreakdown
    {
        public static bool Active;
        public static double SetFactionMs, ArriveMs, LetterMs;
        private static bool ownersLogged;

        public static double Since(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        public static void Reset()
        {
            SetFactionMs = ArriveMs = LetterMs = 0;
        }

        public static void LogOwnersOnce()
        {
            if (ownersLogged)
                return;
            ownersLogged = true;
            Patches info = Harmony.GetPatchInfo(AccessTools.Method(typeof(Pawn), nameof(Pawn.SetFaction)));
            if (info == null)
                return;
            string Owners(IEnumerable<Patch> patches) => string.Join(", ", patches.Select(p => p.owner).Distinct());
            Log.Message($"[DeferredRaidGeneration] Pawn.SetFaction patches: prefixes [{Owners(info.Prefixes)}], postfixes [{Owners(info.Postfixes)}], "
                + $"transpilers [{Owners(info.Transpilers)}], finalizers [{Owners(info.Finalizers)}].");
        }
    }

    [HarmonyPatch(typeof(QuestPart_PawnsArrive), nameof(QuestPart_PawnsArrive.Notify_QuestSignalReceived))]
    public static class Patch_QuestPart_PawnsArrive_Timing
    {
        public static void Prefix(QuestPart_PawnsArrive __instance, Signal signal, out long __state)
        {
            __state = signal.tag == __instance.inSignal && Prefs.DevMode ? Stopwatch.GetTimestamp() : 0;
            if (__state != 0)
            {
                ArrivalBreakdown.Reset();
                ArrivalBreakdown.Active = true;
            }
        }

        public static Exception Finalizer(Exception __exception, QuestPart_PawnsArrive __instance, long __state)
        {
            if (__state != 0)
            {
                ArrivalBreakdown.Active = false;
                double ms = ArrivalBreakdown.Since(__state);
                double other = ms - ArrivalBreakdown.SetFactionMs - ArrivalBreakdown.ArriveMs - ArrivalBreakdown.LetterMs;
                Log.Message($"[DeferredRaidGeneration] Quest {__instance.quest?.root?.defName}: {__instance.pawns.Count} pawns arrived in {ms:F0} ms "
                    + $"(join faction {ArrivalBreakdown.SetFactionMs:F0} ms, arrive {ArrivalBreakdown.ArriveMs:F0} ms, "
                    + $"letter relations {ArrivalBreakdown.LetterMs:F0} ms, other {other:F0} ms).");
                if (ArrivalBreakdown.SetFactionMs > 50)
                    ArrivalBreakdown.LogOwnersOnce();
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    public static class Patch_Pawn_SetFaction_Timing
    {
        public static void Prefix(out long __state) => __state = ArrivalBreakdown.Active ? Stopwatch.GetTimestamp() : 0;

        public static Exception Finalizer(Exception __exception, long __state)
        {
            if (__state != 0)
                ArrivalBreakdown.SetFactionMs += ArrivalBreakdown.Since(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    public static class Patch_PawnsArrivalModeWorker_Arrive_Timing
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in typeof(PawnsArrivalModeWorker).AllSubclassesNonAbstract())
            {
                MethodInfo method = type.GetMethod(nameof(PawnsArrivalModeWorker.Arrive),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (method != null && !method.IsAbstract)
                    yield return method;
            }
        }

        public static void Prefix(out long __state) => __state = ArrivalBreakdown.Active ? Stopwatch.GetTimestamp() : 0;

        public static Exception Finalizer(Exception __exception, long __state)
        {
            if (__state != 0)
                ArrivalBreakdown.ArriveMs += ArrivalBreakdown.Since(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    public static class Patch_PawnRelationUtility_SeenByPlayerLetter_Timing
    {
        public static IEnumerable<MethodBase> TargetMethods() =>
            typeof(PawnRelationUtility).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(m => m.Name == nameof(PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter));

        public static void Prefix(out long __state) => __state = ArrivalBreakdown.Active ? Stopwatch.GetTimestamp() : 0;

        public static Exception Finalizer(Exception __exception, long __state)
        {
            if (__state != 0)
                ArrivalBreakdown.LetterMs += ArrivalBreakdown.Since(__state);
            return __exception;
        }
    }

    /// <summary>Accepting a quest runs its arrival and everything else listening to the accept signal; logged when slow.</summary>
    [HarmonyPatch(typeof(Quest), nameof(Quest.Accept))]
    public static class Patch_Quest_Accept_Timing
    {
        public static void Prefix(out long __state) => __state = Prefs.DevMode ? Stopwatch.GetTimestamp() : 0;

        public static Exception Finalizer(Exception __exception, Quest __instance, long __state)
        {
            if (__state != 0)
            {
                double ms = ArrivalBreakdown.Since(__state);
                if (ms >= 50)
                    Log.Message($"[DeferredRaidGeneration] Quest {__instance.root?.defName} accepted in {ms:F0} ms.");
            }
            return __exception;
        }
    }
}
