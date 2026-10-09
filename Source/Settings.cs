using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeferredRaidGeneration
{
    public enum FeatureState { Off, Active, Unavailable, Failed }

    /// <summary>
    /// One part of the mod that can be switched off. Its Harmony patch classes carry [HarmonyPatchCategory(Id)] and are
    /// applied at startup only when the feature is enabled (and its parent too), so a disabled feature leaves the game
    /// untouched; changes take effect after a restart.
    /// </summary>
    public sealed class Feature
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string Description;
        /// <summary>Enabled only while at least one of these is enabled; none = top level.</summary>
        public readonly string[] Parents;
        /// <summary>Patches that cannot be expressed as a category (other mods' methods).</summary>
        public Action<Harmony> ApplyManual;
        public Func<bool> IsAvailable = () => true;
        public string UnavailableText;

        public FeatureState State;
        public bool IsActive => State == FeatureState.Active;

        public Feature(string id, string label, string description, params string[] parents)
        {
            Id = id;
            Label = label;
            Description = description;
            Parents = parents;
        }
    }

    public static class Features
    {
        public const string RaidDeferral = "RaidDeferral";
        public const string QuestDeferral = "QuestDeferral";
        public const string AutosaveHold = "AutosaveHold";
        public const string ArrivalSmoothing = "ArrivalSmoothing";
        public const string StaggeredJoin = "StaggeredJoin";
        public const string RenderWarmup = "RenderWarmup";
        public const string ContainerCache = "ContainerCache";
        public const string ApparelCache = "ApparelCache";
        public const string StealCache = "StealCache";
        /// <summary>Development-mode timing patches; applied when development mode is on at startup.</summary>
        public const string Diagnostics = "Diagnostics";

        public static readonly List<Feature> All = new List<Feature>
        {
            new Feature(RaidDeferral, "Generate large raids over several seconds",
                "Enemy and friendly raids of 5 or more pawns (and Dynamic Diplomacy conquest armies) are generated one pawn at a time, " +
                "then the vanilla raid runs with those pawns. The raid arrives up to about 20 s later.")
            {
                ApplyManual = DynamicDiplomacyPatch.TryPatch,
            },
            new Feature(QuestDeferral, "Generate large beggar and refugee groups over several seconds",
                "Beggar and refugee quests with 5 or more pawns: the group is generated one pawn at a time, then the vanilla quest is created with them.")
            {
                IsAvailable = () => ModsConfig.IdeologyActive || ModsConfig.RoyaltyActive,
                UnavailableText = "(requires Ideology or Royalty)",
            },
            new Feature(AutosaveHold, "Autosave waits until pending groups are done",
                "An autosave that comes due while pawns are being generated waits for them (at most a minute) instead of finishing them in one go.",
                RaidDeferral, QuestDeferral, StaggeredJoin),
            new Feature(ArrivalSmoothing, "Lighter arrival of large groups",
                "Groups of 5 or more arriving from a deferred raid or a quest start looking for their first job spread over half a second, " +
                "and the spawn check for \"died\" thoughts only looks at pawns that remember one of them."),
            new Feature(StaggeredJoin, "Accepted refugees join one per frame",
                "After you accept, the first refugee joins at once and the others join and walk in one per frame.",
                ArrivalSmoothing),
            new Feature(RenderWarmup, "Build pawn graphics ahead of first draw",
                "Deferred pawns get their graphics built during generation; other pawns on the current map are prepared in the background at about 1 ms per frame."),
            new Feature(ContainerCache, "Scan map containers once per generated pawn or group",
                "For all pawn generation, the scan of every container on every map is done once per pawn or pawn group instead of dozens of times."),
            new Feature(ApparelCache, "Cache race-filtered starting apparel (Humanoid Alien Races)",
                "Starting apparel uses a list filtered once per race instead of copying and filtering the full apparel list for every pawn.")
            {
                ApplyManual = ApparelPairsCache.TryPatch,
                IsAvailable = () => AccessTools.TypeByName("AlienRace.HarmonyPatches") != null,
                UnavailableText = "(Humanoid Alien Races not loaded)",
            },
            new Feature(StealCache, "Cache item values in raider steal checks",
                "Raider steal checks compute each nearby item's market value once per check instead of once per raider."),
        };

        public static Feature Get(string id) => All.Find(f => f.Id == id);

        public static bool IsActive(string id) => Get(id)?.IsActive == true;

        // Read on hot paths, so kept as plain fields.
        public static bool RenderWarmupActive;
        public static bool StaggeredJoinActive;
        public static bool QuestDeferralActive;

        public static void Apply(Harmony harmony, DeferredRaidGenerationSettings settings)
        {
            foreach (Feature feature in All)
            {
                if (!feature.IsAvailable())
                {
                    feature.State = FeatureState.Unavailable;
                    continue;
                }
                if (!settings.IsEffectivelyEnabled(feature))
                    continue;
                try
                {
                    harmony.PatchCategory(feature.Id);
                    feature.ApplyManual?.Invoke(harmony);
                    feature.State = FeatureState.Active;
                }
                catch (Exception e)
                {
                    feature.State = FeatureState.Failed;
                    Log.Error($"[DeferredRaidGeneration] {feature.Label} failed to apply and was turned off: {e}");
                    try { harmony.UnpatchCategory(feature.Id); } catch { }
                }
            }
            RenderWarmupActive = IsActive(RenderWarmup);
            StaggeredJoinActive = IsActive(StaggeredJoin);
            QuestDeferralActive = IsActive(QuestDeferral);
            if (Prefs.DevMode)
                harmony.PatchCategory(Diagnostics);
        }
    }

    public class DeferredRaidGenerationSettings : ModSettings
    {
        // Only features switched off are stored; everything is on by default.
        private List<string> disabled = new List<string>();

        public bool IsEnabled(Feature feature) => !disabled.Contains(feature.Id);

        public void SetEnabled(Feature feature, bool on)
        {
            disabled.Remove(feature.Id);
            if (!on)
                disabled.Add(feature.Id);
        }

        /// <summary>Enabled, available, and (for a child) at least one parent effectively enabled.</summary>
        public bool IsEffectivelyEnabled(Feature feature)
        {
            if (!IsEnabled(feature) || !feature.IsAvailable())
                return false;
            return feature.Parents.Length == 0 || feature.Parents.Any(id => IsEffectivelyEnabled(Features.Get(id)));
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref disabled, "disabled", LookMode.Value);
            if (Scribe.mode != LoadSaveMode.Saving)
                disabled ??= new List<string>();
        }
    }

    public class DeferredRaidGenerationMod : Mod
    {
        public static DeferredRaidGenerationSettings Settings;
        private const float ChildIndent = 24f;
        private const float DescriptionIndent = 12f;
        private const float CheckboxColumn = 36f;
        private Vector2 scroll;
        private float viewHeight;

        public DeferredRaidGenerationMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DeferredRaidGenerationSettings>();
        }

        public override string SettingsCategory() => "Deferred Raid Generation";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(viewHeight, inRect.height));
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(view);
            listing.Label("Changes take effect after restarting the game. A feature that is off does not patch the game at all.");
            listing.GapLine();
            foreach (Feature feature in Features.All)
                DrawFeature(listing, feature);
            viewHeight = listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawFeature(Listing_Standard listing, Feature feature)
        {
            float indent = feature.Parents.Length > 0 ? ChildIndent : 0f;
            float width = listing.ColumnWidth;
            listing.Indent(indent);
            listing.ColumnWidth = width - indent;
            bool parentOn = feature.Parents.Length == 0 || feature.Parents.Any(id => Settings.IsEffectivelyEnabled(Features.Get(id)));
            if (feature.State == FeatureState.Unavailable)
            {
                GUI.color = Color.gray;
                listing.Label(feature.Label + "  " + feature.UnavailableText);
            }
            else
            {
                bool on = Settings.IsEnabled(feature);
                if (!parentOn)
                    GUI.color = Color.gray;
                listing.CheckboxLabeled(feature.Label, ref on, feature.Description);
                Settings.SetEnabled(feature, on);
                GUI.color = Color.white;
                if (feature.State == FeatureState.Failed)
                {
                    GUI.color = ColorLibrary.RedReadable;
                    listing.Label("Failed to apply; see the log.");
                }
                else if (Settings.IsEffectivelyEnabled(feature) != feature.IsActive)
                {
                    GUI.color = ColorLibrary.Gold;
                    listing.Label("Restart required.");
                }
            }
            // Indent only moves the start, so narrow the column too: keeps the text clear of the checkbox column.
            listing.ColumnWidth = width - indent - DescriptionIndent - CheckboxColumn;
            listing.Indent(DescriptionIndent);
            GUI.color = Color.gray;
            listing.Label(parentOn ? feature.Description : feature.Description + " (No effect while the features it depends on are off.)");
            GUI.color = Color.white;
            listing.Outdent(DescriptionIndent + indent);
            listing.ColumnWidth = width;
            listing.Gap(6f);
        }
    }
}
