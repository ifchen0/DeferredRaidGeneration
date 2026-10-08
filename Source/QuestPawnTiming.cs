using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [HarmonyPatch(typeof(QuestPart_PawnsArrive), nameof(QuestPart_PawnsArrive.Notify_QuestSignalReceived))]
    public static class Patch_QuestPart_PawnsArrive_Timing
    {
        public static void Prefix(QuestPart_PawnsArrive __instance, Signal signal, out long __state)
        {
            __state = signal.tag == __instance.inSignal ? Stopwatch.GetTimestamp() : 0;
        }

        public static Exception Finalizer(Exception __exception, QuestPart_PawnsArrive __instance, long __state)
        {
            if (Prefs.DevMode && __state != 0)
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                Log.Message($"[DeferredRaidGeneration] Quest {__instance.quest?.root?.defName}: {__instance.pawns.Count} pawns arrived in {ms:F0} ms.");
            }
            return __exception;
        }
    }
}
