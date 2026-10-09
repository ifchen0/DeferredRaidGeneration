using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DeferredRaidGeneration
{
    public class DeferredRaidGenerationSettings : ModSettings
    {
        public bool throttlePawns = true;
        public int throttleInterval = 4;
        public int throttleMinMapPawns = 100;
        public bool throttleWarWork;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref throttlePawns, "throttlePawns", true);
            Scribe_Values.Look(ref throttleInterval, "throttleInterval", 4);
            Scribe_Values.Look(ref throttleMinMapPawns, "throttleMinMapPawns", 100);
            Scribe_Values.Look(ref throttleWarWork, "throttleWarWork", false);
        }
    }

    public class DeferredRaidGenerationMod : Mod
    {
        public static DeferredRaidGenerationSettings Settings;

        public DeferredRaidGenerationMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeferredRaidGenerationSettings>();
        }

        public override string SettingsCategory() => "Deferred Raid Generation";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("Run walking, waiting and downed pawns at a reduced tick rate on crowded maps", ref Settings.throttlePawns,
                "On a map with many pawns, pawns that are only walking, waiting or lying downed run their per-tick update " +
                "only once every few ticks. Movement, ability cooldowns, sounds and visual effects still update every tick. " +
                "Drafted colonists and pawns that attacked or were hurt in the last hour run at the normal rate.");
            list.CheckboxLabeled("  While any map has an active threat, also slow colony work that is not part of the fight",
                ref Settings.throttleWarWork,
                "Colony pawns doing work such as cooking, crafting, research, growing, hauling or cleaning also run at the " +
                "reduced rate while hostiles threaten any of your maps, so that work progresses more slowly until the fight " +
                "is over. Doctor, firefighting, construction and repair work, turret rearming, and jobs you ordered by hand " +
                "keep the normal rate.");
            list.Label($"Reduced rate: once every {Settings.throttleInterval} ticks");
            Settings.throttleInterval = Mathf.RoundToInt(list.Slider(Settings.throttleInterval, 2, 8));
            list.Label($"Only on maps with at least {Settings.throttleMinMapPawns} pawns");
            Settings.throttleMinMapPawns = Mathf.RoundToInt(list.Slider(Settings.throttleMinMapPawns, 0, 300) / 10f) * 10;
            if (Prefs.DevMode)
                list.Label("Dev mode: a timing line is logged every 600 ticks while any pawn qualifies.");
            list.End();
        }
    }

    /// <summary>
    /// On a crowded map (a large battle, an arena) most pawns are only walking or waiting, and many others lie downed.
    /// Their Pawn.Tick (job driver, stances, verbs, hediff and comp ticks) runs every tick although nothing they do needs it;
    /// the slower-rate TickInterval part is already scaled by the game itself. For such pawns the full Tick runs only on
    /// every Nth tick (aligned with the pawn's hash offset, so the game's own hash-interval checks inside it still fire,
    /// at a multiple of their interval); the ticks in between run only movement, toil countdown, the toil's pre-tick
    /// actions (work sounds, effects and progress bars, which must be maintained every tick), ability cooldowns, sounds
    /// and effecters. Pawns doing anything else keep the full rate, since their job progress is counted per tick.
    /// Optionally, while any map has an active threat to the player, colony pawns doing work-giver jobs that are not part
    /// of the fight are throttled too; their work progresses more slowly, which the player accepts for the duration.
    /// A pawn that starts an attack or takes damage counts as engaged for EngagedTicks (unless downed); drafted colonists
    /// are never throttled. Engagement is not saved; after loading, pawns become engaged again on their next attack or hit.
    /// </summary>
    public static class BattleThrottle
    {
        private const int EngagedTicks = GenDate.TicksPerHour;
        private static readonly Dictionary<int, int> engagedUntil = new Dictionary<int, int>();
        private static Game engagedGame;

        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerAmbient =
            AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerAmbient");
        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerMoving =
            AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerMoving");
        private static readonly System.Func<JobDriver, Toil> curToil =
            AccessTools.MethodDelegate<System.Func<JobDriver, Toil>>(AccessTools.PropertyGetter(typeof(JobDriver), "CurToil"));

        private static HashSet<JobDef> simpleJobs;

        private const int ThreatCheckInterval = 60;
        private static int threatCheckedTick = -ThreatCheckInterval;
        private static bool anyThreat;
        private static WorkGiverDef rearmTurrets;

        // Dev-mode timing: pawn-ticks of qualifying pawns and the time their Pawn.Tick took, plus the whole game tick.
        private const int ReportInterval = 600;
        private static int reportTicks;
        private static long gameTickTime;
        private static int qualifyingPawnTicks;
        private static int skippedPawnTicks;
        private static long qualifyingTime;

        private static Game EnsureGame()
        {
            Game game = Current.Game;
            if (game != engagedGame)
            {
                engagedUntil.Clear();
                engagedGame = game;
                threatCheckedTick = -ThreatCheckInterval;
            }
            return game;
        }

        public static void MarkEngaged(Pawn pawn)
        {
            if (pawn == null)
                return;
            EnsureGame();
            engagedUntil[pawn.thingIDNumber] = Find.TickManager.TicksGame + EngagedTicks;
        }

        /// <summary>True when this pawn's Pawn.Tick may run at the reduced rate.</summary>
        public static bool Qualifies(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Map.mapPawns.AllPawnsSpawnedCount < DeferredRaidGenerationMod.Settings.throttleMinMapPawns)
                return false;
            if (pawn.Downed)
                return true;
            if (pawn.Drafted)
                return false;
            EnsureGame();
            if (engagedUntil.TryGetValue(pawn.thingIDNumber, out int until))
            {
                if (Find.TickManager.TicksGame < until)
                    return false;
                engagedUntil.Remove(pawn.thingIDNumber);
            }
            Job job = pawn.CurJob;
            if (job == null)
                return true;
            if (simpleJobs == null)
                simpleJobs = new HashSet<JobDef>
                {
                    JobDefOf.Goto, JobDefOf.GotoWander, JobDefOf.Wait, JobDefOf.Wait_Wander,
                    JobDefOf.Wait_MaintainPosture, JobDefOf.Wait_Combat,
                };
            return simpleJobs.Contains(job.def) || IsThrottledWarWork(pawn, job);
        }

        /// <summary>Colony work that is not part of the fight, while any map has an active threat.</summary>
        private static bool IsThrottledWarWork(Pawn pawn, Job job)
        {
            WorkGiverDef giver = job.workGiverDef;
            if (!DeferredRaidGenerationMod.Settings.throttleWarWork || giver == null || job.playerForced || pawn.Faction == null || !pawn.Faction.IsPlayer)
                return false;
            WorkTypeDef type = giver.workType;
            if (type == WorkTypeDefOf.Doctor || type == WorkTypeDefOf.Firefighter || type == WorkTypeDefOf.Construction)
                return false;
            if (rearmTurrets == null)
                rearmTurrets = DefDatabase<WorkGiverDef>.GetNamedSilentFail("RearmTurrets");
            return giver != rearmTurrets && AnyThreat();
        }

        private static bool AnyThreat()
        {
            int now = Find.TickManager.TicksGame;
            if (now - threatCheckedTick < ThreatCheckInterval && now >= threatCheckedTick)
                return anyThreat;
            threatCheckedTick = now;
            anyThreat = false;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count && !anyThreat; i++)
                anyThreat = GenHostility.AnyHostileActiveThreatToPlayer(maps[i]);
            return anyThreat;
        }

        /// <summary>What still runs on a skipped tick: everything a viewer would see stutter without.</summary>
        public static void LightTick(Pawn pawn)
        {
            if (pawn.stances.FullBodyBusy)
                pawn.stances.StanceTrackerTick();
            pawn.pather.PatherTick();
            if (!pawn.Spawned)
                return;
            pawn.abilities?.AbilitiesTick();
            JobDriver driver = pawn.jobs?.curDriver;
            if (driver != null)
            {
                driver.ticksLeftThisToil--;
                driver.debugTicksSpentThisToil++;
                // Same guard as JobDriver.DriverTick: stop once an action changes the job or the toil.
                Toil toil = curToil(driver);
                List<System.Action> actions = toil?.preTickActions;
                if (actions != null)
                {
                    Job job = pawn.CurJob;
                    for (int i = 0; i < actions.Count; i++)
                    {
                        actions[i]();
                        if (pawn.CurJob != job || pawn.jobs.curDriver != driver || curToil(driver) != toil)
                            break;
                    }
                }
            }
            Sustainer ambient = sustainerAmbient(pawn);
            if (ambient != null && !ambient.Ended)
                ambient.Maintain();
            Sustainer moving = sustainerMoving(pawn);
            if (moving != null && !moving.Ended && pawn.pather.Moving)
                moving.Maintain();
            pawn.Drawer.renderer.EffectersTick(false);
        }

        public static void RecordPawnTick(long elapsed, bool skipped)
        {
            qualifyingPawnTicks++;
            qualifyingTime += elapsed;
            if (skipped)
                skippedPawnTicks++;
        }

        public static void RecordGameTick(long elapsed)
        {
            gameTickTime += elapsed;
            if (++reportTicks < ReportInterval)
                return;
            if (qualifyingPawnTicks > 0)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                var settings = DeferredRaidGenerationMod.Settings;
                Log.Message($"[DRG] Battle throttle {(settings.throttlePawns ? $"on (1/{settings.throttleInterval})" : "off")}: " +
                    $"{(double)qualifyingPawnTicks / reportTicks:F1} qualifying pawns, " +
                    $"their Pawn.Tick {qualifyingTime * ms / reportTicks:F3} ms/tick " +
                    $"({qualifyingTime * ms * 1000 / qualifyingPawnTicks:F1} us/pawn, {100.0 * skippedPawnTicks / qualifyingPawnTicks:F0}% skipped); " +
                    $"whole tick {gameTickTime * ms / reportTicks:F2} ms");
            }
            reportTicks = 0;
            gameTickTime = 0;
            qualifyingPawnTicks = 0;
            skippedPawnTicks = 0;
            qualifyingTime = 0;
        }
    }

    [HarmonyPatch(typeof(Pawn), "Tick")]
    public static class Patch_Pawn_Tick_BattleThrottle
    {
        public static bool Prefix(Pawn __instance, out long __state)
        {
            __state = 0;
            var settings = DeferredRaidGenerationMod.Settings;
            bool measure = Prefs.DevMode;
            if (!settings.throttlePawns && !measure)
                return true;
            if (!BattleThrottle.Qualifies(__instance))
                return true;
            long start = Stopwatch.GetTimestamp();
            if (!settings.throttlePawns || __instance.IsHashIntervalTick(settings.throttleInterval))
            {
                if (measure)
                    __state = start;
                return true;
            }
            BattleThrottle.LightTick(__instance);
            if (measure)
                BattleThrottle.RecordPawnTick(Stopwatch.GetTimestamp() - start, skipped: true);
            return false;
        }

        public static void Postfix(long __state)
        {
            if (__state != 0)
                BattleThrottle.RecordPawnTick(Stopwatch.GetTimestamp() - __state, skipped: false);
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class Patch_TickManager_DoSingleTick_BattleThrottle
    {
        public static void Prefix(out long __state) => __state = Prefs.DevMode ? Stopwatch.GetTimestamp() : 0;

        public static void Postfix(long __state)
        {
            if (__state != 0)
                BattleThrottle.RecordGameTick(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Pawn_PostApplyDamage_BattleThrottle
    {
        public static void Postfix(Pawn __instance, float totalDamageDealt)
        {
            if (totalDamageDealt > 0f)
                BattleThrottle.MarkEngaged(__instance);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryStartCastOn),
        new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class Patch_Verb_TryStartCastOn_BattleThrottle
    {
        public static void Postfix(Verb __instance, bool __result)
        {
            if (__result)
                BattleThrottle.MarkEngaged(__instance.CasterPawn);
        }
    }
}
