using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Revenant_Theme_Studio.Models
{
    /// <summary>
    /// Schema for a Revenant Theme Studio theme bundle (.rtstheme zip).
    ///
    /// The bundle is a zip with this layout:
    ///   manifest.json     ← serialized <see cref="ThemeBundle"/>
    ///   preview.png       ← marketplace thumbnail (optional in v1)
    ///   icons/            ← .ico files referenced by manifest
    ///   wallpapers/       ← reserved for v2
    ///   fonts/            ← reserved
    ///   sounds/           ← reserved
    ///   livewall/         ← reserved
    ///   ui/               ← reserved (the "go crazy" tier)
    ///   LICENSE.txt       ← optional
    ///   SIGNATURE         ← reserved for marketplace signing
    ///
    /// Design tenets — this is the contract that ships to the future
    /// marketplace, so once we cut v1 we don't reshape it casually:
    ///
    ///   1. Asset categories are namespaced under <see cref="ThemeAssets"/>
    ///      so future asset types slot in additively without breaking old
    ///      themes. A v1 icon-only theme stays valid forever.
    ///
    ///   2. <see cref="MinRtsVersion"/> lets a v3 theme refuse to install on
    ///      a v1 RTS rather than failing in obscure ways.
    ///
    ///   3. <see cref="MunSwap.SourceFingerprint"/> stores the SHA256 of the
    ///      original Microsoft icon at the time the swap was authored. When
    ///      we replay swaps against a freshly-updated .mun and the icon at
    ///      that resource ID has been renumbered or repurposed by Microsoft,
    ///      we surface a remap prompt instead of silently overwriting the
    ///      wrong icon.
    ///
    ///   4. Asset paths are relative to the bundle root, forward-slashed
    ///      (zip convention), and never escape the bundle (no "../").
    /// </summary>
    public class ThemeBundle
    {
        public const string CurrentSchema = "rts.theme/v1";

        /// <summary>Schema discriminator. Always "rts.theme/v1" for v1 bundles.</summary>
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = CurrentSchema;

        /// <summary>
        /// Stable globally-unique handle. Used by the future marketplace to
        /// track listings and updates across version bumps. Convention:
        /// kebab-case, "author-themename" (e.g. "revenant-bone").
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>Human-readable theme name shown in UI and storefront.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        /// <summary>Theme semver (e.g. "1.0.0"). Author-controlled.</summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Minimum RTS version that can install this theme. Newer themes
        /// using assets a given RTS doesn't understand should bump this so
        /// installation refuses cleanly instead of silently dropping assets.
        /// </summary>
        [JsonPropertyName("minRtsVersion")]
        public string MinRtsVersion { get; set; } = "1.0.0";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Path inside the bundle to the marketplace thumbnail.</summary>
        [JsonPropertyName("preview")]
        public string? Preview { get; set; }

        /// <summary>Optional path inside the bundle to a license file.</summary>
        [JsonPropertyName("license")]
        public string? License { get; set; }

        [JsonPropertyName("assets")]
        public ThemeAssets Assets { get; set; } = new();
    }

    /// <summary>
    /// Asset category catalog. Every category is optional; future asset
    /// types slot in as additional properties without breaking v1 themes.
    /// </summary>
    public class ThemeAssets
    {
        [JsonPropertyName("systemIcons")]
        public SystemIconAssets? SystemIcons { get; set; }

        // ── Reserved for future versions. Properties are deliberately
        //    commented out rather than declared-but-null so the JSON stays
        //    minimal in v1. When the wallpapers tier ships, we add the
        //    property + class in the same release.
        //
        // [JsonPropertyName("wallpapers")]    public WallpaperAssets?    Wallpapers    { get; set; }
        // [JsonPropertyName("fonts")]         public FontAssets?         Fonts         { get; set; }
        // [JsonPropertyName("sounds")]        public SoundAssets?        Sounds        { get; set; }
        // [JsonPropertyName("liveWallpapers")] public LiveWallpaperAssets? LiveWallpapers { get; set; }
        // [JsonPropertyName("ui")]            public UiAssets?           Ui            { get; set; }

        /// <summary>
        /// Per-target overrides for non-protected icons (folder/drive/system
        /// shell entries that don't require .mun editing). Mirrors the
        /// existing Apply* flows so a theme can ship both .mun swaps AND
        /// HKCU registry overrides as a single bundle.
        /// </summary>
        [JsonPropertyName("shellOverrides")]
        public List<ShellOverride>? ShellOverrides { get; set; }
    }

    /// <summary>
    /// Asset block for icons that live inside imageres.dll.mun (or, in
    /// principle, any other PE resource container — the schema doesn't
    /// hardcode the .mun filename).
    /// </summary>
    public class SystemIconAssets
    {
        /// <summary>
        /// Which PE resource container these swaps target. Defaults to
        /// imageres.dll.mun because it's the universal Windows icon source,
        /// but leaving it explicit lets future themes target shell32.dll.mun,
        /// ddores.dll.mun, etc.
        /// </summary>
        [JsonPropertyName("targetResource")]
        public string TargetResource { get; set; } = "imageres.dll.mun";

        [JsonPropertyName("munSwaps")]
        public List<MunSwap> MunSwaps { get; set; } = new();
    }

    /// <summary>
    /// One icon replacement inside a .mun file: "replace IconGroup N with
    /// the .ico at this bundle-relative path."
    /// </summary>
    public class MunSwap
    {
        /// <summary>The PE RT_GROUP_ICON resource ID being replaced.</summary>
        [JsonPropertyName("iconGroup")]
        public int IconGroup { get; set; }

        /// <summary>
        /// Bundle-relative path to the replacement .ico file. Forward
        /// slashes, no leading slash, no ".." segments.
        /// </summary>
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        /// <summary>
        /// SHA256 of the original Microsoft icon group at this resource ID
        /// when the swap was authored. On replay against a freshly-updated
        /// .mun, if the icon at IconGroup no longer matches this hash,
        /// Microsoft has likely renumbered or repurposed the slot — we
        /// prompt the user to remap rather than silently overwriting.
        /// Optional in v1 themes (older swaps just blind-overwrite).
        /// </summary>
        [JsonPropertyName("sourceFingerprint")]
        public string? SourceFingerprint { get; set; }

        /// <summary>
        /// Optional human label so the marketplace can show "Recycle Bin
        /// (Empty)" instead of "IconGroup 32".
        /// </summary>
        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }

    /// <summary>
    /// HKCU-scope shell override (folder/drive/system tab outputs). Lets a
    /// bundled theme apply non-.mun overrides as part of installation.
    /// </summary>
    public class ShellOverride
    {
        /// <summary>
        /// Target identifier — for system-icon scope this is the CLSID; for
        /// drive scope it's the drive letter; for folder scope it's an
        /// absolute path (only meaningful per-machine, so folder overrides
        /// in shipped themes are unusual but the schema permits them).
        /// </summary>
        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        /// <summary>"folder" | "drive" | "system"</summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;

        /// <summary>Bundle-relative path to the .ico file.</summary>
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }
}
