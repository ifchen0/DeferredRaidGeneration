using System;
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
        public bool throttlePawns;
        public int throttleInterval = 4;
        public int throttleMinMapPawns = 100;
        public bool throttleWarWork;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref throttlePawns, "throttlePawns", false);
            Scribe_Values.Look(ref throttleInterval, "throttleInterval", 4);
            Scribe_Values.Look(ref throttleMinMapPawns, "throttleMinMapPawns", 100);
            Scribe_Values.Look(ref throttleWarWork, "throttleWarWork", false);
            throttleInterval = Mathf.Clamp(throttleInterval, 2, 8);
            throttleMinMapPawns = Mathf.Clamp(throttleMinMapPawns, 0, 300);
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

    public enum ThrottleKind { None, Idle, Work }

    /// <summary>
    /// On a crowded map (a large battle, an arena) most pawns are only walking or waiting, and many others lie downed.
    /// Their Pawn.Tick (job driver, stances, verbs, hediff and comp ticks) runs every tick although nothing they do needs it;
    /// the slower-rate TickInterval part is already scaled by the game itself. For such pawns the full Tick runs on one
    /// tick in N, at a scattered position (IsFullTick), so any periodic check inside Tick, hash-aligned or a plain
    /// TicksGame modulo in a mod, still fires about 1/N as often instead of never for some pawns.
    /// The ticks in between run what must keep its own pace or would visibly stutter: movement, TickRare on its usual
    /// schedule, ability cooldowns, the toil's end/fail checks and pre-tick actions (work sounds, effects, progress
    /// bars), the job mote, sounds and effecters. Pawns doing anything else keep the full rate, since their job progress
    /// is counted per tick. Hediff ticks are left to the full tick: vanilla Hediff.Tick is empty (injuries, bleeding and
    /// diseases advance in TickInterval), and running it on every skipped tick cost about half of what throttling saves;
    /// per-tick hediff logic added by mods runs at the reduced rate while a pawn is throttled.
    /// Optionally, while any map has an active threat to the player, colony pawns doing work-giver jobs that are not part
    /// of the fight are throttled too; their work progresses more slowly, which the player accepts for the duration.
    /// Their toil countdown is not compensated, so timed toils slow down by the same factor as per-tick work.
    /// A pawn that starts an attack or takes damage counts as engaged for EngagedTicks (unless downed); drafted colonists
    /// are never throttled. Engagement is not saved; after loading, pawns become engaged again on their next attack or hit.
    /// </summary>
    public static class BattleThrottle
    {
        private const int EngagedTicks = GenDate.TicksPerHour;
        private static readonly Dictionary<int, int> engagedUntil = new Dictionary<int, int>();
        private static Game engagedGame;
        private const int PruneInterval = GenDate.TicksPerDay;
        private static int prunedTick;
        private static readonly List<int> expired = new List<int>();

        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerAmbient =
            AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerAmbient");
        private static readonly AccessTools.FieldRef<Pawn, Sustainer> sustainerMoving =
            AccessTools.FieldRefAccess<Pawn, Sustainer>("sustainerMoving");
        private static readonly Func<JobDriver, Toil> curToil =
            AccessTools.MethodDelegate<Func<JobDriver, Toil>>(AccessTools.PropertyGetter(typeof(JobDriver), "CurToil"));
        private static readonly Func<JobDriver, bool> checkCurrentToilEndOrFail =
            AccessTools.MethodDelegate<Func<JobDriver, bool>>(AccessTools.Method(typeof(JobDriver), "CheckCurrentToilEndOrFail"));
        private static readonly AccessTools.FieldRef<JobDriver, bool> wantBeginNextToil =
            AccessTools.FieldRefAccess<JobDriver, bool>("wantBeginNextToil");
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, int> jobsGivenThisTick =
            AccessTools.FieldRefAccess<Pawn_JobTracker, int>("jobsGivenThisTick");
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, string> jobsGivenThisTickTextual =
            AccessTools.FieldRefAccess<Pawn_JobTracker, string>("jobsGivenThisTickTextual");

        private static HashSet<JobDef> simpleJobs;

        private const int ThreatCheckInterval = 60;
        private static int threatCheckedTick = -ThreatCheckInterval;
        private static bool anyThreat;
        private static WorkGiverDef rearmTurrets;

        // Dev-mode timing: pawn-ticks of qualifying pawns and the time their Pawn.Tick took, by kind and by full or
        // skipped tick, plus the whole game tick.
        private const int ReportInterval = 600;
        private static int reportTicks;
        private static long gameTickTime;
        private static readonly int[,] pawnTicks = new int[3, 2];
        private static readonly long[,] pawnTime = new long[3, 2];

        private static Game EnsureGame()
        {
            Game game = Current.Game;
            if (game != engagedGame)
            {
                engagedUntil.Clear();
                engagedGame = game;
                threatCheckedTick = -ThreatCheckInterval;
                prunedTick = 0;
            }
            int now = Find.TickManager.TicksGame;
            if (now - prunedTick >= PruneInterval || now < prunedTick)
            {
                // Pawns that died or left keep their entry; drop every expired one now and then.
                prunedTick = now;
                foreach (KeyValuePair<int, int> pair in engagedUntil)
                    if (pair.Value <= now)
                        expired.Add(pair.Key);
                foreach (int id in expired)
                    engagedUntil.Remove(id);
                expired.Clear();
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

        /// <summary>Whether this pawn's Pawn.Tick may run at the reduced rate, and why.</summary>
        public static ThrottleKind Qualifies(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Map.mapPawns.AllPawnsSpawnedCount < DeferredRaidGenerationMod.Settings.throttleMinMapPawns)
                return ThrottleKind.None;
            if (pawn.Downed)
                return ThrottleKind.Idle;
            if (pawn.Drafted)
                return ThrottleKind.None;
            EnsureGame();
            if (engagedUntil.TryGetValue(pawn.thingIDNumber, out int until))
            {
                if (Find.TickManager.TicksGame < until)
                    return ThrottleKind.None;
                engagedUntil.Remove(pawn.thingIDNumber);
            }
            Job job = pawn.CurJob;
            if (job == null)
                return ThrottleKind.Idle;
            if (simpleJobs == null)
                simpleJobs = new HashSet<JobDef>
                {
                    JobDefOf.Goto, JobDefOf.GotoWander, JobDefOf.Wait, JobDefOf.Wait_Wander,
                    JobDefOf.Wait_MaintainPosture, JobDefOf.Wait_Combat,
                };
            if (simpleJobs.Contains(job.def))
                return ThrottleKind.Idle;
            return IsThrottledWarWork(pawn, job) ? ThrottleKind.Work : ThrottleKind.None;
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

        /// <summary>
        /// One tick in each block of <paramref name="interval"/> ticks runs the full Pawn.Tick; which one is hashed from
        /// the block number and the pawn, so it has no period that a check inside Tick could line up against (a fixed or
        /// steadily moving position starves checks such as TicksGame % 60 for some pawns). Gaps average the interval and
        /// are at most twice it minus one.
        /// </summary>
        public static bool IsFullTick(Pawn pawn, int interval)
        {
            int now = Find.TickManager.TicksGame;
            int block = now / interval;
            uint x = unchecked((uint)block * 0x9E3779B1u ^ (uint)pawn.HashOffset());
            x ^= x >> 16;
            x = unchecked(x * 0x85EBCA6Bu);
            x ^= x >> 13;
            x = unchecked(x * 0xC2B2AE35u);
            x ^= x >> 16;
            return now - block * interval == (int)(x % (uint)interval);
        }

        /// <summary>
        /// What still runs on a skipped tick: everything a viewer would see stutter without, plus the parts that must keep
        /// their own pace. Work pawns get no toil countdown, so their timed toils slow down like their per-tick work.
        /// </summary>
        public static void LightTick(Pawn pawn, ThrottleKind kind)
        {
            if (pawn.stances.FullBodyBusy)
                pawn.stances.StanceTrackerTick();
            pawn.pather.PatherTick();
            if (!pawn.Spawned)
                return;
            if (pawn.IsHashIntervalTick(250))
                pawn.TickRare();
            pawn.abilities?.AbilitiesTick();
            if (!pawn.Spawned)
                return;
            Pawn_JobTracker jobs = pawn.jobs;
            if (jobs != null)
            {
                // JobTrackerTick resets the per-tick job count; without it, jobs started on skipped ticks add up.
                jobsGivenThisTick(jobs) = 0;
                jobsGivenThisTickTextual(jobs) = "";
                if (jobs.curDriver != null)
                    ToilTick(pawn, jobs.curDriver, kind == ThrottleKind.Idle);
            }
            if (!pawn.Spawned)
                return;
            Sustainer ambient = sustainerAmbient(pawn);
            if (ambient != null && !ambient.Ended)
                ambient.Maintain();
            Sustainer moving = sustainerMoving(pawn);
            if (moving != null && !moving.Ended && pawn.pather.Moving)
                moving.Maintain();
            pawn.Drawer.renderer.EffectersTick(false);
        }

        /// <summary>JobDriver.DriverTick without the toil's tickAction, which is the per-tick work being slowed.</summary>
        private static void ToilTick(Pawn pawn, JobDriver driver, bool countDown)
        {
            try
            {
                if (countDown)
                {
                    driver.ticksLeftThisToil--;
                    driver.debugTicksSpentThisToil++;
                }
                Toil toil = curToil(driver);
                if (toil == null || wantBeginNextToil(driver))
                    return;
                if (toil.defaultCompleteMode == ToilCompleteMode.Delay && driver.ticksLeftThisToil <= 0)
                    return;
                Job job = pawn.CurJob;
                List<Action> actions = toil.preTickActions;
                if (actions != null)
                {
                    // The full tick checks end and fail conditions first, and pre-tick actions may rely on them.
                    if (checkCurrentToilEndOrFail(driver))
                        return;
                    // Same guard as DriverTick: stop once an action changes the job or the toil.
                    for (int i = 0; i < actions.Count; i++)
                    {
                        actions[i]();
                        if (pawn.CurJob != job || pawn.jobs.curDriver != driver || curToil(driver) != toil)
                            return;
                    }
                }
                job?.mote?.Maintain();
            }
            catch (Exception e)
            {
                JobUtility.TryStartErrorRecoverJob(pawn, "Exception in reduced-rate JobDriver tick for pawn " + pawn.ToStringSafe(), e, driver);
            }
        }

        public static void RecordPawnTick(ThrottleKind kind, long elapsed, bool skipped)
        {
            pawnTicks[(int)kind, skipped ? 1 : 0]++;
            pawnTime[(int)kind, skipped ? 1 : 0] += elapsed;
        }

        public static void RecordGameTick(long elapsed)
        {
            gameTickTime += elapsed;
            if (++reportTicks < ReportInterval)
                return;
            long total = 0;
            foreach (long time in pawnTime)
                total += time;
            if (total > 0)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                var settings = DeferredRaidGenerationMod.Settings;
                var sb = new System.Text.StringBuilder($"[DRG] Battle throttle {(settings.throttlePawns ? $"on (1/{settings.throttleInterval})" : "off")}:");
                foreach (ThrottleKind kind in new[] { ThrottleKind.Idle, ThrottleKind.Work })
                {
                    int k = (int)kind, full = pawnTicks[k, 0], skipped = pawnTicks[k, 1];
                    if (full + skipped == 0)
                        continue;
                    sb.Append($" {kind.ToString().ToLower()} {(double)(full + skipped) / reportTicks:F1} pawns, " +
                              $"full tick {(full > 0 ? pawnTime[k, 0] * ms * 1000 / full : 0):F1} us, " +
                              $"skipped tick {(skipped > 0 ? pawnTime[k, 1] * ms * 1000 / skipped : 0):F1} us;");
                }
                sb.Append($" their Pawn.Tick {total * ms / reportTicks:F3} ms/tick; whole tick {gameTickTime * ms / reportTicks:F2} ms");
                Log.Message(sb.ToString());
            }
            reportTicks = 0;
            gameTickTime = 0;
            System.Array.Clear(pawnTicks, 0, pawnTicks.Length);
            System.Array.Clear(pawnTime, 0, pawnTime.Length);
        }
    }

    [HarmonyPatch(typeof(Pawn), "Tick")]
    public static class Patch_Pawn_Tick_BattleThrottle
    {
        public struct Timing
        {
            public long start;
            public ThrottleKind kind;
        }

        public static bool Prefix(Pawn __instance, out Timing __state)
        {
            __state = default;
            var settings = DeferredRaidGenerationMod.Settings;
            bool measure = Prefs.DevMode;
            if (!settings.throttlePawns && !measure)
                return true;
            ThrottleKind kind = BattleThrottle.Qualifies(__instance);
            if (kind == ThrottleKind.None)
                return true;
            long start = measure ? Stopwatch.GetTimestamp() : 0;
            if (!settings.throttlePawns || BattleThrottle.IsFullTick(__instance, settings.throttleInterval))
            {
                __state = new Timing { start = start, kind = kind };
                return true;
            }
            BattleThrottle.LightTick(__instance, kind);
            if (measure)
                BattleThrottle.RecordPawnTick(kind, Stopwatch.GetTimestamp() - start, skipped: true);
            return false;
        }

        public static void Postfix(Timing __state)
        {
            if (__state.start != 0)
                BattleThrottle.RecordPawnTick(__state.kind, Stopwatch.GetTimestamp() - __state.start, skipped: false);
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
