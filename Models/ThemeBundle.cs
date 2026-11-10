using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revenant_Theme_Studio.Models
{
    /// <summary>
    /// The Revenant Theme Studio theme bundle (.rtstheme zip), schema rts.theme/v2.
    /// Spec: docs\specs\spec-phase1-theme-engine.md (F1) and docs\specs\README.md (section 1).
    ///
    ///   manifest.json   this object
    ///   preview.png     optional thumbnail
    ///   LICENSE.txt     optional
    ///   icons/ wallpapers/ lockscreen/ cursors/ sounds/   assets, listed in <see cref="Files"/>
    ///
    /// Rules the reader enforces (Features\Themes\Bundles\BundleReader):
    ///   1. Data only. Images, icons, cursors and sounds; never executables, scripts, HTML or SVG.
    ///   2. Every file in the zip is listed in <see cref="Files"/> with its SHA-256, and nothing else is in the zip.
    ///   3. Paths are bundle-relative, forward-slashed, and never escape the bundle.
    ///   4. A section missing from <see cref="ThemeAssets"/> is left untouched on apply.
    ///   5. rts.theme/v1 bundles still load: their systemIcons and shellOverrides map into icons.
    /// </summary>
    public class ThemeBundle
    {
        public const string SchemaV1 = "rts.theme/v1";
        public const string SchemaV2 = "rts.theme/v2";
        public const string CurrentSchema = SchemaV2;

        [JsonPropertyName("schema")] public string Schema { get; set; } = CurrentSchema;

        /// <summary>Stable handle, kebab-case "author-themename". Also the library folder name.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";

        /// <summary>Oldest RTS that can apply this bundle. Newer bundles refuse cleanly on older apps.</summary>
        [JsonPropertyName("minRtsVersion")] public string MinRtsVersion { get; set; } = "2.0.0";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("preview")] public string? Preview { get; set; }
        [JsonPropertyName("license")] public string? License { get; set; }

        /// <summary>Section keys a reader must understand. A reader that doesn't, refuses the bundle.</summary>
        [JsonPropertyName("requires")] public List<string>? Requires { get; set; }
        [JsonPropertyName("gate")] public Gate? Gate { get; set; }
        [JsonPropertyName("origin")] public BundleOrigin? Origin { get; set; }
        [JsonPropertyName("files")] public List<BundleFile>? Files { get; set; }

        [JsonPropertyName("assets")] public ThemeAssets Assets { get; set; } = new();
    }

    public class BundleFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
    }

    public class BundleOrigin
    {
        /// <summary>native, windowsTheme, ddw or capture.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "native";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("importedUtc")] public string? ImportedUtc { get; set; }
    }

    /// <summary>
    /// A Windows build gate. Builds are "26300" or "26300.9711" (numbers are accepted too).
    /// <see cref="FileVersion"/> gates system-icon edits on the .mun file's own version.
    /// </summary>
    public class Gate
    {
        [JsonPropertyName("minBuild")] [JsonConverter(typeof(BuildTextConverter))] public string? MinBuild { get; set; }
        [JsonPropertyName("maxBuild")] [JsonConverter(typeof(BuildTextConverter))] public string? MaxBuild { get; set; }
        [JsonPropertyName("fileVersion")] public List<string>? FileVersion { get; set; }
        /// <summary>skip (default) or fail.</summary>
        [JsonPropertyName("onUnsupported")] public string? OnUnsupported { get; set; }
    }

    /// <summary>
    /// Asset sections. Phase 1 applies icons, wallpaper and colors. Every other section
    /// (cursors, sounds, metrics, screensaver, lockScreen, tweaks, console, visualStyle, schedule)
    /// is kept verbatim in <see cref="Other"/> so re-exporting never loses it.
    /// </summary>
    public class ThemeAssets
    {
        [JsonPropertyName("icons")] public IconSettings? Icons { get; set; }
        [JsonPropertyName("wallpaper")] public WallpaperSettings? Wallpaper { get; set; }
        [JsonPropertyName("colors")] public ColorSettings? Colors { get; set; }

        /// <summary>v1 only. Mapped into <see cref="Icons"/> when read, never written.</summary>
        [JsonPropertyName("systemIcons")] public SystemIconAssets? SystemIcons { get; set; }
        /// <summary>v1 only. Mapped into <see cref="Icons"/> when read, never written.</summary>
        [JsonPropertyName("shellOverrides")] public List<ShellOverride>? ShellOverrides { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    // ── icons ────────────────────────────────────────────────────────────────

    public class IconSettings
    {
        [JsonPropertyName("gate")] public Gate? Gate { get; set; }
        [JsonPropertyName("targets")] public List<IconTarget> Targets { get; set; } = new();
    }

    /// <summary>
    /// One icon to change. Phase 1 applies kinds "shell" (a ShellIconService target by name or
    /// CLSID), "drive" (a drive letter) and "system" (an imageres.dll.mun icon group).
    /// </summary>
    public class IconTarget
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        /// <summary>Shell target name or CLSID, or a drive letter.</summary>
        [JsonPropertyName("match")] public string? Match { get; set; }
        /// <summary>Icon group ID, for kind "system".</summary>
        [JsonPropertyName("group")] public int? Group { get; set; }
        /// <summary>Bundle-relative .ico, or an allowed system reference such as "%SystemRoot%\System32\imageres.dll,-109".</summary>
        [JsonPropertyName("file")] public string? File { get; set; }
        /// <summary>Recycle Bin only: the icon when it has items.</summary>
        [JsonPropertyName("fullFile")] public string? FullFile { get; set; }
        [JsonPropertyName("sourceFingerprint")] public string? SourceFingerprint { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("gate")] public Gate? Gate { get; set; }
    }

    // ── wallpaper ────────────────────────────────────────────────────────────

    public class WallpaperSettings
    {
        /// <summary>static, perMonitor, timeOfDay or slideshow.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "static";
        /// <summary>center, tile, stretch, fit, fill or span.</summary>
        [JsonPropertyName("fit")] public string? Fit { get; set; }
        /// <summary>#RRGGBB shown around a centered or fitted image.</summary>
        [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; set; }
        [JsonPropertyName("static")] public StaticWallpaper? Static { get; set; }
        [JsonPropertyName("perMonitor")] public List<MonitorWallpaper>? PerMonitor { get; set; }
        [JsonPropertyName("timeOfDay")] public TimeOfDayWallpaper? TimeOfDay { get; set; }
        [JsonPropertyName("gate")] public Gate? Gate { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    public class StaticWallpaper
    {
        [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    }

    public class MonitorWallpaper
    {
        [JsonPropertyName("match")] public MonitorMatch Match { get; set; } = new();
        [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    }

    /// <summary>Which monitor. Device paths are machine-specific, so bundles never store them.</summary>
    public class MonitorMatch
    {
        /// <summary>0-based, in the order Windows lists monitors.</summary>
        [JsonPropertyName("ordinal")] public int? Ordinal { get; set; }
        /// <summary>primary, left, right, top or bottom.</summary>
        [JsonPropertyName("position")] public string? Position { get; set; }
    }

    public class TimeOfDayWallpaper
    {
        [JsonPropertyName("segments")] public TimeOfDaySegments Segments { get; set; } = new();
        [JsonPropertyName("credits")] public string? Credits { get; set; }
        [JsonPropertyName("dayHighlight")] public int? DayHighlight { get; set; }
        [JsonPropertyName("nightHighlight")] public int? NightHighlight { get; set; }
    }

    /// <summary>Ordered image lists per segment. Sunrise and sunset are optional (two-segment model).</summary>
    public class TimeOfDaySegments
    {
        [JsonPropertyName("sunrise")] public List<string>? Sunrise { get; set; }
        [JsonPropertyName("day")] public List<string> Day { get; set; } = new();
        [JsonPropertyName("sunset")] public List<string>? Sunset { get; set; }
        [JsonPropertyName("night")] public List<string> Night { get; set; } = new();
    }

    // ── colors ───────────────────────────────────────────────────────────────

    public class ColorSettings
    {
        /// <summary>light, dark or unchanged.</summary>
        [JsonPropertyName("appMode")] public string? AppMode { get; set; }
        [JsonPropertyName("systemMode")] public string? SystemMode { get; set; }
        [JsonPropertyName("transparency")] public bool? Transparency { get; set; }
        [JsonPropertyName("accent")] public AccentColors? Accent { get; set; }
        [JsonPropertyName("gate")] public Gate? Gate { get; set; }
        /// <summary>classic and other Phase 2 color settings, kept verbatim.</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    public class AccentColors
    {
        /// <summary>fixed (use <see cref="Color"/>) or auto (Windows picks from the wallpaper).</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "fixed";
        /// <summary>#RRGGBB.</summary>
        [JsonPropertyName("color")] public string? Color { get; set; }
        [JsonPropertyName("onStartTaskbar")] public bool? OnStartTaskbar { get; set; }
        [JsonPropertyName("onTitleBars")] public bool? OnTitleBars { get; set; }
        [JsonPropertyName("palette")] public AccentPalette? Palette { get; set; }
        /// <summary>Captured Start-menu shades (#RRGGBB). Without them the palette's entries 3 and 4 are used, else <see cref="Color"/>.</summary>
        [JsonPropertyName("accentMenu")] public string? AccentMenu { get; set; }
        [JsonPropertyName("startMenu")] public string? StartMenu { get; set; }
    }

    /// <summary>
    /// The 8-entry AccentPalette. "captured" palettes came from a real Windows install and replay
    /// exactly; Windows' own shade algorithm is undocumented, so RTS never invents one (spec D7).
    /// </summary>
    public class AccentPalette
    {
        [JsonPropertyName("source")] public string Source { get; set; } = "captured";
        /// <summary>8 colors, #RRGGBBAA in registry order.</summary>
        [JsonPropertyName("colors")] public List<string> Colors { get; set; } = new();
    }

    // ── v1 (read-only compatibility) ─────────────────────────────────────────

    public class SystemIconAssets
    {
        [JsonPropertyName("targetResource")] public string TargetResource { get; set; } = "imageres.dll.mun";
        [JsonPropertyName("munSwaps")] public List<MunSwap> MunSwaps { get; set; } = new();
    }

    public class MunSwap
    {
        [JsonPropertyName("iconGroup")] public int IconGroup { get; set; }
        [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
        [JsonPropertyName("sourceFingerprint")] public string? SourceFingerprint { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    public class ShellOverride
    {
        /// <summary>CLSID or target name ("system"), drive letter ("drive") or absolute path ("folder").</summary>
        [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;
        [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
        [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    /// <summary>Reads a build as a JSON string or number; writes it as a string.</summary>
    public sealed class BuildTextConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, System.Type t, JsonSerializerOptions o) =>
            reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.Number => reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.String => reader.GetString(),
                _ => throw new JsonException("A build must be a string or a number."),
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions o)
        {
            if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value);
        }
    }
}
