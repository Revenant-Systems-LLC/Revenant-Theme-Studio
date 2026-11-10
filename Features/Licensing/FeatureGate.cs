using System.Collections.Generic;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Licensing
{
    /// <summary>Every feature that can be free or paid. Add new features here.</summary>
    public enum Feature
    {
        // ── Icons (shipped in v1) ───────────────────────────────────────────
        SystemIconOverrides,
        AutoMatch,
        AutoMatchApply,
        UndoIconChange,
        GunmetalIconPack,
        SystemIconBrowser,
        MunEditor,

        // ── Themes (Phase 1) ─────────────────────────────────────────────────
        ThemeImport,
        ThemeApply,
        ThemeUndo,
        ThemeExport,
        ThemeCapture,
    }

    public enum Tier { Free, Pro }

    /// <summary>
    /// The one place that decides what is free and what needs Pro.
    ///
    /// To move a feature between free and paid, change its row in <see cref="Policy"/> and
    /// rebuild. Nothing else in the app checks the license directly; every gate calls
    /// <see cref="Allows"/>.
    /// </summary>
    public static class FeatureGate
    {
        private static readonly IReadOnlyDictionary<Feature, Tier> Policy = new Dictionary<Feature, Tier>
        {
            // Icons: same split as v1.
            [Feature.SystemIconOverrides] = Tier.Pro,
            [Feature.AutoMatch]           = Tier.Pro,
            [Feature.AutoMatchApply]      = Tier.Pro,
            [Feature.UndoIconChange]      = Tier.Pro,
            [Feature.GunmetalIconPack]    = Tier.Pro,
            [Feature.SystemIconBrowser]   = Tier.Pro,
            [Feature.MunEditor]           = Tier.Pro,

            // Themes: free while Phase 1 is built. Dave decides the split once everything exists.
            [Feature.ThemeImport]  = Tier.Free,
            [Feature.ThemeApply]   = Tier.Free,
            [Feature.ThemeUndo]    = Tier.Free,
            [Feature.ThemeExport]  = Tier.Free,
            [Feature.ThemeCapture] = Tier.Free,
        };

        /// <summary>True if the current license may use this feature. Unlisted features are Pro.</summary>
        public static bool Allows(Feature feature) =>
            TierOf(feature) == Tier.Free || LicenseService.Instance.IsPro;

        public static Tier TierOf(Feature feature) =>
            Policy.TryGetValue(feature, out var tier) ? tier : Tier.Pro;

        /// <summary>The standard message when a feature is locked.</summary>
        public static string LockedMessage(string what) => $"{what} requires RTS Pro.";
    }
}
