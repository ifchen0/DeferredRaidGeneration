using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeferredRaidGeneration
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            var harmony = new Harmony("ifchen0.deferredraidgeneration");
            harmony.PatchAll();
            DynamicDiplomacyPatch.TryPatch(harmony);
        }
    }

    /// <summary>
    /// A group of pawns that is generated one at a time over several seconds, then handed to the code that would
    /// normally have generated them all at once.
    /// </summary>
    public abstract class PendingGeneration
    {
        public readonly List<Pawn> Generated = new List<Pawn>();
        public int Next;
        public int Steps;
        public double StepMsTotal;
        public double StepMsMax;
        public float StartTime = Time.realtimeSinceStartup;
        public bool Failed;

        public abstract int Count { get; }
        public abstract string Label { get; }
        public bool Done => Failed || Next >= Count;

        /// <summary>False when the target no longer exists; the pawns are then released unused.</summary>
        public abstract bool StillValid { get; }

        protected abstract Pawn GeneratePawn(int index);

        /// <summary>Runs the original code, which picks up the pre-generated pawns.</summary>
        public abstract void Execute();

        public void GenerateNext()
        {
            int index = Next++;
            try
            {
                Pawn pawn = GeneratePawn(index);
                if (pawn != null)
                    Generated.Add(pawn);
            }
            catch (Exception e)
            {
                // Fall back to generating the whole group normally.
                Log.Error($"[DeferredRaidGeneration] Pawn generation failed for {Label}, it will be generated normally: " + e);
                Failed = true;
            }
        }
    }

    /// <summary>A raid (enemy or friendly) whose composition has been decided by the vanilla raid code.</summary>
    public class PendingRaid : PendingGeneration
    {
        public IncidentWorker Worker;
        public IncidentParms Parms;
        public PawnGroupMakerParms GroupParms;
        public List<PawnGenOptionWithXenotype> Options;

        public override int Count => Options.Count;
        public override string Label => Worker.def.defName;
        /// <summary>
        /// Besides the map still existing, an enemy raid needs its faction to still qualify the way
        /// IncidentWorker_RaidEnemy.TryResolveRaidFaction checks it; otherwise the replay would silently switch to
        /// another faction and generate everything again. A faction that made peace meanwhile simply does not come.
        /// </summary>
        public override bool StillValid
        {
            get
            {
                if (!(Parms.target is Map map) || !Find.Maps.Contains(map))
                    return false;
                if (Worker is IncidentWorker_RaidEnemy
                    && !(Parms.faction != null && Parms.faction.HostileTo(Faction.OfPlayer) && (!Parms.faction.deactivated || Parms.forced)))
                    return false;
                return true;
            }
        }

        /// <summary>Same request PawnGroupKindWorker_Normal.GeneratePawns builds.</summary>
        protected override Pawn GeneratePawn(int index)
        {
            PawnGroupMakerParms parms = GroupParms;
            PawnGenOptionWithXenotype item = Options[index];
            bool allowFood = parms.raidStrategy == null || parms.raidStrategy.pawnsCanBringFood
                || (parms.faction != null && !parms.faction.HostileTo(Faction.OfPlayer));
            List<Pawn> generated = Generated;
            Predicate<Pawn> validator = parms.raidStrategy != null
                ? (Predicate<Pawn>)(p => parms.raidStrategy.Worker.CanUsePawn(parms.points, p, generated))
                : null;
            var request = new PawnGenerationRequest(item.Option.kind, parms.faction, PawnGenerationContext.NonPlayer, parms.tile,
                forceGenerateNewPawn: false, allowDead: false, parms.faction.deactivated, canGeneratePawnRelations: true,
                mustBeCapableOfViolence: true, 1f, forceAddFreeWarmLayerIfNeeded: false, allowGay: true, allowPregnant: true,
                allowFood, allowAddictions: true, parms.inhabitants, certainlyBeenInCryptosleep: false,
                forceRedressWorldPawnIfFormerColonist: false, worldPawnFactionDoesntMatter: false, 0f, 0f, null, 1f, null,
                validator, null, null, null, null, null, null, null, null, null, parms.ideo, forceNoIdeo: false,
                forceNoBackstory: false, forbidAnyTitle: false, forceDead: false, null, null, item.Xenotype);
            if (parms.raidAgeRestriction != null && parms.raidAgeRestriction.Worker.ShouldApplyToKind(item.Option.kind))
            {
                request.BiologicalAgeRange = parms.raidAgeRestriction.ageRange;
                request.AllowedDevelopmentalStages = parms.raidAgeRestriction.developmentStage;
            }
            if (item.Option.kind.pawnGroupDevelopmentStage.HasValue)
                request.AllowedDevelopmentalStages = item.Option.kind.pawnGroupDevelopmentStage.Value;
            if (!Find.Storyteller.difficulty.ChildRaidersAllowed && parms.faction != null && parms.faction.HostileTo(Faction.OfPlayer))
                request.AllowedDevelopmentalStages = DevelopmentalStage.Adult;
            return PawnGenerator.GeneratePawn(request);
        }

        public override void Execute()
        {
            DeferredRaids.ReplayingRaid = this;
            try
            {
                Worker.TryExecute(Parms);
            }
            finally
            {
                DeferredRaids.ReplayingRaid = null;
            }
        }
    }

    /// <summary>
    /// A Dynamic Diplomacy conquest arena (IncidentWorker_NPCConquest.InitArenaMap). Its pawns are generated with
    /// PawnGenerator.GeneratePawn(kind, faction) as DD does; the map itself is still generated when the arena starts.
    /// </summary>
    public class PendingArena : PendingGeneration
    {
        public MethodInfo InitArenaMap;
        public object[] Args;
        public List<KeyValuePair<PawnKindDef, Faction>> Kinds;

        public override int Count => Kinds.Count;
        public override string Label => "NPC conquest arena";
        public override bool StillValid => Args[0] is WorldObject parent && !parent.Destroyed;

        protected override Pawn GeneratePawn(int index) => PawnGenerator.GeneratePawn(Kinds[index].Key, Kinds[index].Value);

        public override void Execute()
        {
            DeferredRaids.ReplayingArena = this;
            try
            {
                InitArenaMap.Invoke(null, Args);
            }
            finally
            {
                DeferredRaids.ReplayingArena = null;
            }
        }
    }

    /// <summary>
    /// Large groups of pawns (enemy and friendly raids, Dynamic Diplomacy arenas) are split in two: when the event
    /// fires, what to generate is decided exactly as the original code would, then the pawns are generated one at a
    /// time (SecondsPerPawn each, at most MaxSeconds in total) while the game keeps running. When all pawns exist the original code runs again
    /// ("replay") and the pre-generated pawns are handed to it instead of generating new ones, so arrival, letter,
    /// lords and loot are all unchanged.
    /// </summary>
    public class DeferredRaids : GameComponent
    {
        public const int MinPawnsToDefer = 5;
        // Pawns are generated one at a time, SecondsPerPawn of real time apart, so a group is delayed in proportion to
        // its size, but by at most MaxSeconds. After a step that took t ms the next one also waits at least
        // IdleFactor * t ms, so a slow step is never followed directly by another one.
        private const float SecondsPerPawn = 0.25f;
        private const float MaxSeconds = 20f;
        private const float IdleFactor = 2f;

        private static readonly MethodInfo ResolveRaidPoints = AccessTools.Method(typeof(IncidentWorker_Raid), "ResolveRaidPoints");
        private static readonly MethodInfo TryResolveRaidFaction = AccessTools.Method(typeof(IncidentWorker_Raid), "TryResolveRaidFaction");
        private static readonly MethodInfo CloneMethod = AccessTools.Method(typeof(object), "MemberwiseClone");

        private readonly List<PendingGeneration> pending = new List<PendingGeneration>();
        private float nextStepTime;

        /// <summary>Set while a finished raid is being replayed through the vanilla incident code.</summary>
        public static PendingRaid ReplayingRaid;
        /// <summary>Set while a finished Dynamic Diplomacy arena is being replayed.</summary>
        public static PendingArena ReplayingArena;

        public static bool AnyReplaying => ReplayingRaid != null || ReplayingArena != null;

        // The lord sends every pawn to look for its first job in the same tick it is created. During a replay that
        // job search is postponed and spread over the next StaggerTicks ticks with a short wait job instead.
        private const int StaggerTicks = 30;
        public static HashSet<Pawn> ReplayPawns;
        public static readonly List<Pawn> Staggered = new List<Pawn>();

        /// <summary>Pawns whose memories mention any replayed pawn; built on first use during a replay.</summary>
        public static List<Pawn> DiedThoughtHolders;

        public static DeferredRaids Instance => Current.Game?.GetComponent<DeferredRaids>();

        /// <summary>Debug switch for A/B tests against vanilla behaviour; not saved.</summary>
        public static bool Disabled;

        [DebugAction("Deferred Raid Generation", "Toggle deferral (A/B test)", allowedGameStates = AllowedGameStates.Playing)]
        private static void ToggleDeferral()
        {
            Disabled = !Disabled;
            Messages.Message(Disabled ? "Deferred Raid Generation: OFF (vanilla generation)." : "Deferred Raid Generation: ON.",
                MessageTypeDefOf.NeutralEvent, false);
        }

        // Lists this mod has put into the static PawnGroupKindWorker.pawnsBeingGeneratedNow. They must not survive into
        // another game (load or new game while a group is pending), or that game would see the old game's pawns.
        private static readonly List<List<Pawn>> registered = new List<List<Pawn>>();

        public DeferredRaids(Game game)
        {
            foreach (List<Pawn> list in registered)
                PawnGroupKindWorker.pawnsBeingGeneratedNow.Remove(list);
            registered.Clear();
            ReplayingRaid = null;
            ReplayingArena = null;
            ReplayPawns = null;
            DiedThoughtHolders = null;
            Staggered.Clear();
            UnspawnedCache.Clear();
        }

        /// <summary>
        /// Decides whether this incident execution should be deferred, and if so queues it.
        /// Returns false to let vanilla run it immediately.
        /// </summary>
        public bool TryDefer(IncidentWorker worker, IncidentParms parms)
        {
            if (Disabled || AnyReplaying)
                return false;
            Type type = worker.GetType();
            if (type != typeof(IncidentWorker_RaidEnemy) && type != typeof(IncidentWorker_RaidFriendly))
                return false;
            if (!(parms.target is Map map) || parms.quest != null || !parms.questTag.NullOrEmpty() || parms.pawnKind != null
                || parms.pawnGroups != null || parms.controllerPawn != null)
                return false;
            if (worker.def.requireColonistsPresent && map.mapPawns.FreeColonistsSpawnedCount == 0)
                return false;

            // Vanilla re-rolls the child-raid restriction every time it runs, so a raid that is handed back to vanilla
            // must not keep the one rolled while planning, or the chance of a child raid would be rolled twice.
            RaidAgeRestrictionDef originalAgeRestriction = parms.raidAgeRestriction;
            PendingRaid raid = null;
            try
            {
                raid = Plan(worker, parms);
            }
            finally
            {
                if (raid == null || raid.Options.Count < MinPawnsToDefer)
                    parms.raidAgeRestriction = originalAgeRestriction;
            }
            if (raid == null || raid.Options.Count < MinPawnsToDefer)
                return false;

            Enqueue(raid);
            if (Prefs.DevMode)
                Log.Message($"[DeferredRaidGeneration] Deferred {worker.def.defName}: {raid.Count} pawns of {parms.faction}, {parms.raidStrategy?.defName}/{parms.raidArrivalMode?.defName}.");
            return true;
        }

        public void Enqueue(PendingGeneration generation)
        {
            pending.Add(generation);
            // Lets name and relation checks of other pawns being generated see these pawns, as they would in vanilla.
            PawnGroupKindWorker.pawnsBeingGeneratedNow.Add(generation.Generated);
            registered.Add(generation.Generated);
        }

        /// <summary>
        /// Resolves the raid the same way IncidentWorker_Raid.TryGenerateRaidInfo does up to pawn generation.
        /// The resolved values stay in parms, so the replay skips re-resolving them.
        /// </summary>
        private static PendingRaid Plan(IncidentWorker worker, IncidentParms parms)
        {
            var raidWorker = (IncidentWorker_Raid)worker;
            PawnGroupKindDef groupKind = parms.pawnGroupKind ?? PawnGroupKindDefOf.Combat;
            ResolveRaidPoints.Invoke(raidWorker, new object[] { parms });
            if (!(bool)TryResolveRaidFaction.Invoke(raidWorker, new object[] { parms }))
                return null;
            raidWorker.ResolveRaidStrategy(parms, groupKind);
            if (parms.raidArrivalMode == null && !raidWorker.TryResolveRaidArriveMode(parms))
                return null;
            raidWorker.ResolveRaidAgeRestriction(parms);

            // Strategies that generate their own threats (sieges with mortars, mech clusters, ...) are left to vanilla.
            Type strategyType = parms.raidStrategy.Worker.GetType();
            if (Declares(strategyType, nameof(RaidStrategyWorker.SpawnThreats), typeof(RaidStrategyWorker))
                || Declares(strategyType, nameof(RaidStrategyWorker.TryGenerateThreats), typeof(RaidStrategyWorker)))
                return null;

            // Vanilla fails the incident when no spawn center is found; check that now, since the caller is told the
            // raid succeeded as soon as it is queued. Done on a copy because some arrival modes cannot resolve twice
            // (EmergeFromWater returns false once spawnCenter is set); the replay resolves it again for the map as it
            // is then. CenterDrop may fall back to EdgeDrop, which also changes the points, so that change is kept.
            var probe = (IncidentParms)CloneMethod.Invoke(parms, null);
            if (!probe.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(probe))
                return null;
            parms.raidArrivalMode = probe.raidArrivalMode;

            PawnGroupMakerParms groupParms = IncidentParmsUtility.GetDefaultPawnGroupMakerParms(groupKind, parms);
            groupParms.points = IncidentWorker_Raid.AdjustedRaidPoints(parms.points, parms.raidArrivalMode, parms.raidStrategy,
                parms.faction, groupKind, parms.target, parms.raidAgeRestriction);
            if (parms.faction.def.pawnGroupMakers.NullOrEmpty()
                || !PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(groupParms, out PawnGroupMaker groupMaker, groupParms.ignoreGroupCommonality)
                || groupMaker.kindDef.Worker.GetType() != typeof(PawnGroupKindWorker_Normal)
                || !groupMaker.kindDef.Worker.CanGenerateFrom(groupParms, groupMaker))
                return null;

            return new PendingRaid
            {
                Worker = worker,
                Parms = parms,
                GroupParms = groupParms,
                Options = PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints(groupParms.points, groupMaker.options, groupParms).ToList(),
            };
        }

        private static bool Declares(Type type, string method, Type baseType)
        {
            for (Type t = type; t != null && t != baseType; t = t.BaseType)
                if (t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(m => m.Name == method))
                    return true;
            return false;
        }

        public override void GameComponentUpdate()
        {
            if (pending.Count == 0 || Time.realtimeSinceStartup < nextStepTime || LongEventHandler.AnyEventNowOrWaiting)
                return;
            PendingGeneration generation = pending[0];
            float stepStart = Time.realtimeSinceStartup;
            var watch = Stopwatch.StartNew();
            generation.GenerateNext();
            // Measured from the start of the step, so the step's own time is part of the interval and the total
            // stays close to MaxSeconds; a slow step still gets IdleFactor times its own duration of rest.
            float interval = Math.Min(SecondsPerPawn, MaxSeconds / Math.Max(1, generation.Count));
            nextStepTime = stepStart + Math.Max(interval, (float)watch.Elapsed.TotalSeconds * (1f + IdleFactor));
            double ms = watch.Elapsed.TotalMilliseconds;
            generation.Steps++;
            generation.StepMsTotal += ms;
            generation.StepMsMax = Math.Max(generation.StepMsMax, ms);
            if (generation.Done)
                Finish(generation);
        }

        private void Finish(PendingGeneration generation)
        {
            pending.Remove(generation);
            PawnGroupKindWorker.pawnsBeingGeneratedNow.Remove(generation.Generated);
            registered.Remove(generation.Generated);
            if (!generation.StillValid)
            {
                if (Prefs.DevMode)
                    Log.Message($"[DeferredRaidGeneration] Dropped {generation.Label}: its map or faction no longer qualifies.");
                ReleaseUnused(generation);
                return;
            }
            if (generation.Failed)
                ReleaseUnused(generation);
            if (Prefs.DevMode && generation.Steps > 0)
                Log.Message($"[DeferredRaidGeneration] Generated {generation.Generated.Count} pawns for {generation.Label} in {generation.Steps} steps over {Time.realtimeSinceStartup - generation.StartTime:F1} s; step avg {generation.StepMsTotal / generation.Steps:F1} ms, max {generation.StepMsMax:F1} ms.");
            ReplayPawns = new HashSet<Pawn>(generation.Generated);
            DiedThoughtHolders = null;
            Staggered.Clear();
            var watch = Stopwatch.StartNew();
            try
            {
                generation.Execute();
                StartStaggeredWaits();
            }
            catch (Exception e)
            {
                Log.Error($"[DeferredRaidGeneration] Exception while executing deferred {generation.Label}: " + e);
            }
            finally
            {
                ReplayPawns = null;
                DiedThoughtHolders = null;
                Staggered.Clear();
                ReleaseUnused(generation);
            }
            if (Prefs.DevMode)
                Log.Message($"[DeferredRaidGeneration] Executed {generation.Label} in {watch.Elapsed.TotalMilliseconds:F0} ms.");
        }

        private static void StartStaggeredWaits()
        {
            foreach (Pawn pawn in Staggered)
            {
                if (!pawn.Spawned || pawn.Dead || pawn.jobs == null || pawn.jobs.curJob != null)
                    continue;
                Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                wait.expiryInterval = Rand.RangeInclusive(1, StaggerTicks);
                pawn.jobs.StartJob(wait);
            }
        }

        /// <summary>Pawns that did not end up being used are handed to the world pawn system like any discarded pawn.</summary>
        private static void ReleaseUnused(PendingGeneration generation)
        {
            foreach (Pawn p in generation.Generated)
            {
                if (p.Spawned || p.Destroyed || p.Discarded || p.ParentHolder != null && !(p.ParentHolder is WorldPawns) || Find.WorldPawns.Contains(p))
                    continue;
                Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Decide);
            }
            generation.Generated.Clear();
        }

        /// <summary>Generates and executes everything still pending, synchronously (used before saving).</summary>
        public void FlushAll()
        {
            while (pending.Count > 0)
            {
                PendingGeneration generation = pending[0];
                while (!generation.Done)
                    generation.GenerateNext();
                Finish(generation);
            }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_IncidentWorker_TryExecute
    {
        public static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            DeferredRaids component = DeferredRaids.Instance;
            if (component == null)
                return true;
            try
            {
                if (!component.TryDefer(__instance, parms))
                    return true;
            }
            catch (Exception e)
            {
                Log.Error("[DeferredRaidGeneration] Could not defer raid, running it normally: " + e);
                return true;
            }
            __result = true;
            return false;
        }
    }

    /// <summary>During a replay, hands the pre-generated pawns to the group worker instead of generating new ones.</summary>
    [HarmonyPatch(typeof(PawnGroupKindWorker_Normal), "GeneratePawns",
        typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(List<Pawn>), typeof(bool))]
    public static class Patch_PawnGroupKindWorker_Normal_GeneratePawns
    {
        public static bool Prefix(PawnGroupMakerParms parms, List<Pawn> outPawns)
        {
            PendingRaid raid = DeferredRaids.ReplayingRaid;
            if (raid == null || raid.Generated.Count == 0 || parms.faction != raid.GroupParms.faction)
                return true;
            for (int i = 0; i < raid.Generated.Count; i++)
            {
                Pawn pawn = raid.Generated[i];
                if (parms.forceOneDowned && i == 0)
                {
                    pawn.health.forceDowned = true;
                    if (pawn.guest != null)
                        pawn.guest.Recruitable = true;
                    pawn.mindState.canFleeIndividual = false;
                }
                outPawns.Add(pawn);
            }
            raid.Generated.Clear();
            return false;
        }
    }

    /// <summary>
    /// RaidEnemy re-rolls the child-raid restriction every time it runs; the replay must keep the one decided when the
    /// raid was planned, since the pawns were generated for it.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), nameof(IncidentWorker_RaidEnemy.ResolveRaidAgeRestriction))]
    public static class Patch_IncidentWorker_RaidEnemy_ResolveRaidAgeRestriction
    {
        public static bool Prefix() => DeferredRaids.ReplayingRaid == null;
    }

    /// <summary>
    /// When the replayed group's lord interrupts a pawn's job to apply its duty, end the job without immediately
    /// searching for a new one; StartStaggeredWaits then gives the pawn a short wait so the searches are spread out.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_Pawn_JobTracker_EndCurrentJob
    {
        public static void Prefix(Pawn ___pawn, ref bool startNewJob)
        {
            if (startNewJob && DeferredRaids.ReplayPawns != null && DeferredRaids.ReplayPawns.Contains(___pawn))
            {
                startNewJob = false;
                DeferredRaids.Staggered.Add(___pawn);
            }
        }
    }

    /// <summary>
    /// Pawn generation lists every pawn in the world many times per pawn (unique names, relation candidates); for each
    /// map that includes walking every thing holder to find pawns inside containers. Nothing enters or leaves map
    /// containers while pawns are being generated, so inside any generation scope (one pawn, or a whole pawn group
    /// such as a caravan) that walk is done once per map and replayed into the vanilla result buffer. Applies to all
    /// pawn generation, deferred or not.
    /// </summary>
    public static class UnspawnedCache
    {
        private static int depth;
        private static readonly Dictionary<MapPawns, List<Pawn>> cache = new Dictionary<MapPawns, List<Pawn>>();

        public static bool Active => depth > 0;

        public static void Enter() => depth++;

        public static void Exit()
        {
            if (depth > 0)
                depth--;
            if (depth == 0)
                cache.Clear();
        }

        public static void Clear()
        {
            depth = 0;
            cache.Clear();
        }

        public static bool TryGet(MapPawns mapPawns, out List<Pawn> pawns) => cache.TryGetValue(mapPawns, out pawns);

        public static void Store(MapPawns mapPawns, List<Pawn> pawns) => cache[mapPawns] = new List<Pawn>(pawns);
    }

    [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), typeof(PawnGenerationRequest))]
    public static class Patch_PawnGenerator_GeneratePawn_Scope
    {
        public static void Prefix() => UnspawnedCache.Enter();

        public static Exception Finalizer(Exception __exception)
        {
            UnspawnedCache.Exit();
            return __exception;
        }
    }

    /// <summary>
    /// Keeps the cache across all pawns of one group (raid, caravan, quest group), not just within one pawn. In
    /// development mode, slow groups that were generated immediately (not deferred) are logged.
    /// </summary>
    [HarmonyPatch(typeof(PawnGroupKindWorker), nameof(PawnGroupKindWorker.GeneratePawns),
        typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(bool))]
    public static class Patch_PawnGroupKindWorker_GeneratePawns_Scope
    {
        private const double LogThresholdMs = 100;

        public static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            UnspawnedCache.Enter();
        }

        public static Exception Finalizer(Exception __exception, PawnGroupMakerParms parms, List<Pawn> __result, long __state)
        {
            UnspawnedCache.Exit();
            if (Prefs.DevMode && !DeferredRaids.AnyReplaying)
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (ms >= LogThresholdMs)
                    Log.Message($"[DeferredRaidGeneration] Generated {__result?.Count ?? 0} pawns ({parms?.groupKind?.defName}, not deferred) in {ms:F0} ms.");
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.AllPawnsUnspawned), MethodType.Getter)]
    public static class Patch_MapPawns_AllPawnsUnspawned
    {
        private static readonly AccessTools.FieldRef<MapPawns, List<Pawn>> resultBuffer =
            AccessTools.FieldRefAccess<MapPawns, List<Pawn>>("allPawnsUnspawnedResult");

        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result)
        {
            if (!UnspawnedCache.Active || !UnspawnedCache.TryGet(__instance, out List<Pawn> cached))
                return true;
            List<Pawn> buffer = resultBuffer(__instance);
            buffer.Clear();
            buffer.AddRange(cached);
            __result = buffer;
            return false;
        }

        public static void Postfix(MapPawns __instance, List<Pawn> __result, bool __runOriginal)
        {
            if (__runOriginal && UnspawnedCache.Active && __result != null)
                UnspawnedCache.Store(__instance, __result);
        }
    }

    /// <summary>
    /// Pawn.SpawnSetup calls RemoveDiedThoughts, which lists every living pawn in the world (including a walk of every
    /// container on every map) and scans their memories for "this pawn died" thoughts. For a replayed group the
    /// memories that mention any of its pawns are found once; each pawn then only checks those holders. The removal
    /// itself is vanilla.
    /// </summary>
    [HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility), nameof(PawnDiedOrDownedThoughtsUtility.RemoveDiedThoughts))]
    public static class Patch_PawnDiedOrDownedThoughtsUtility_RemoveDiedThoughts
    {
        public static bool Prefix(Pawn pawn)
        {
            HashSet<Pawn> group = DeferredRaids.ReplayPawns;
            if (group == null || !group.Contains(pawn))
                return true;
            if (DeferredRaids.DiedThoughtHolders == null)
            {
                var holders = new List<Pawn>();
                foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                {
                    List<Thought_Memory> memories = p.needs?.mood?.thoughts?.memories?.Memories;
                    if (memories != null && memories.Any(m => m.otherPawn != null && group.Contains(m.otherPawn)))
                        holders.Add(p);
                }
                DeferredRaids.DiedThoughtHolders = holders;
            }
            foreach (Pawn item in DeferredRaids.DiedThoughtHolders)
            {
                if (item == pawn || item.needs?.mood == null)
                    continue;
                MemoryThoughtHandler memories = item.needs.mood.thoughts.memories;
                memories.RemoveMemoriesOfDefWhereOtherPawnIs(ThoughtDefOf.KnowColonistDied, pawn);
                memories.RemoveMemoriesOfDefWhereOtherPawnIs(ThoughtDefOf.KnowPrisonerDiedInnocent, pawn);
                memories.RemoveMemoriesOfDefWhereOtherPawnIs(ThoughtDefOf.PawnWithGoodOpinionDied, pawn);
                memories.RemoveMemoriesOfDefWhereOtherPawnIs(ThoughtDefOf.PawnWithBadOpinionDied, pawn);
                if (ModsConfig.BiotechActive)
                    memories.RemoveMemoriesOfDefWhereOtherPawnIs(ThoughtDefOf.Stillbirth, pawn);
                List<PawnRelationDef> relations = DefDatabase<PawnRelationDef>.AllDefsListForReading;
                for (int i = 0; i < relations.Count; i++)
                {
                    ThoughtDef died = relations[i].GetGenderSpecificDiedThought(pawn);
                    if (died != null)
                        memories.RemoveMemoriesOfDefWhereOtherPawnIs(died, pawn);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Dynamic Diplomacy's full-simulation conquest generates an arena map and spawns both armies in one frame
    /// (IncidentWorker_NPCConquest.InitArenaMap). The armies' pawns are generated ahead of time; the map is still
    /// generated when the arena starts. Applied only when Dynamic Diplomacy is loaded.
    /// </summary>
    public static class DynamicDiplomacyPatch
    {
        private static MethodInfo initArenaMap;

        public static void TryPatch(Harmony harmony)
        {
            Type conquest = AccessTools.TypeByName("DynamicDiplomacy.IncidentWorker_NPCConquest");
            initArenaMap = conquest == null ? null : AccessTools.Method(conquest, "InitArenaMap");
            if (initArenaMap == null)
                return;
            harmony.Patch(initArenaMap, prefix: new HarmonyMethod(typeof(DynamicDiplomacyPatch), nameof(InitArenaMapPrefix)));
            harmony.Patch(AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn),
                    new[] { typeof(PawnKindDef), typeof(Faction), typeof(PlanetTile?) }),
                prefix: new HarmonyMethod(typeof(DynamicDiplomacyPatch), nameof(GeneratePawnPrefix)));
        }

        public static bool InitArenaMapPrefix(object[] __args, Faction baseAttacker, Faction baseDefender,
            List<PawnKindDef> lhs, List<PawnKindDef> rhs, bool silenced, Map existingMap)
        {
            DeferredRaids component = DeferredRaids.Instance;
            // The silenced calls re-create an arena for a player caravan arriving at it; leave those alone.
            if (component == null || DeferredRaids.Disabled || DeferredRaids.AnyReplaying || silenced || existingMap != null || lhs == null || rhs == null
                || lhs.Count + rhs.Count < DeferredRaids.MinPawnsToDefer)
                return true;
            var kinds = new List<KeyValuePair<PawnKindDef, Faction>>();
            kinds.AddRange(lhs.Select(k => new KeyValuePair<PawnKindDef, Faction>(k, baseAttacker)));
            kinds.AddRange(rhs.Select(k => new KeyValuePair<PawnKindDef, Faction>(k, baseDefender)));
            component.Enqueue(new PendingArena { InitArenaMap = initArenaMap, Args = (object[])__args.Clone(), Kinds = kinds });
            if (Prefs.DevMode)
                Log.Message($"[DeferredRaidGeneration] Deferred NPC conquest arena: {kinds.Count} pawns, {baseAttacker} vs {baseDefender}.");
            return false;
        }

        /// <summary>During an arena replay, DD's per-kind GeneratePawn calls take the matching pre-generated pawn.</summary>
        public static bool GeneratePawnPrefix(PawnKindDef kindDef, Faction faction, ref Pawn __result)
        {
            PendingArena arena = DeferredRaids.ReplayingArena;
            if (arena == null)
                return true;
            int index = arena.Generated.FindIndex(p => p.kindDef == kindDef && p.Faction == faction);
            if (index < 0)
                return true;
            __result = arena.Generated[index];
            arena.Generated.RemoveAt(index);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
    public static class Patch_GameDataSaveLoader_SaveGame
    {
        public static void Prefix()
        {
            try
            {
                DeferredRaids.Instance?.FlushAll();
            }
            catch (Exception e)
            {
                Log.Error("[DeferredRaidGeneration] Could not finish pending raids before saving: " + e);
            }
        }
    }
}
