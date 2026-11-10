using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.Formats
{
    /// <summary>
    /// A Windows .theme file: INI text (DOC: windows/win32/controls/themesfileformat-overview).
    /// Windows writes UTF-16 LE with a BOM and both "Key=value" and "Key =value"; the reader accepts
    /// UTF-16 LE/BE, UTF-8 with BOM and ANSI. Section and key lookups ignore case.
    /// </summary>
    public sealed class ThemeFile
    {
        public List<(string Name, List<(string Key, string Value)> Values)> Sections { get; } = new();

        public string? Get(string section, string key) =>
            Section(section)?.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        public List<(string Key, string Value)>? Section(string name) =>
            Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Values;

        public void Set(string section, string key, string value)
        {
            var values = Section(section);
            if (values is null) { values = new(); Sections.Add((section, values)); }
            int i = values.FindIndex(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) values[i] = (key, value); else values.Add((key, value));
        }

        public static ThemeFile Parse(byte[] bytes)
        {
            if (bytes.Length > 1 << 20) throw new InvalidDataException("This .theme file is too large.");
            if (bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z') throw new InvalidDataException("This is a program, not a theme.");
            string text = Decode(bytes);
            var file = new ThemeFile();
            List<(string, string)>? current = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string name = line[1..^1].Trim();
                    current = file.Section(name);
                    if (current is null) { current = new(); file.Sections.Add((name, current)); }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current is null) continue;
                current.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
            return file;
        }

        /// <summary>UTF-16 LE with BOM and CRLF, the way Windows writes theme files.</summary>
        public byte[] ToBytes()
        {
            var sb = new StringBuilder();
            foreach (var (name, values) in Sections)
            {
                sb.Append('[').Append(name).Append("]\r\n");
                foreach (var (k, v) in values) sb.Append(k).Append('=').Append(v).Append("\r\n");
                sb.Append("\r\n");
            }
            return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sb.ToString())).ToArray();
        }

        private static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(b);
        }
    }

    /// <summary>What an import produced: a bundle, the files to put in it, and what it left out.</summary>
    public sealed class ImportResult
    {
        public required ThemeBundle Manifest { get; init; }
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// Windows .theme to bundle (spec F3). Phase 1 brings over wallpaper, colors and desktop icons;
    /// cursors, sounds, classic colors, metrics and the screensaver are listed as not imported yet.
    /// Files outside the allowed Windows folders are copied in; nothing is ever run or fetched.
    /// </summary>
    public static class ThemeFileImporter
    {
        // WallpaperStyle (DOC) to fit. 0 is center, or tile when TileWallpaper=1.
        private static readonly Dictionary<string, string> Styles = new()
            { ["0"] = "center", ["2"] = "stretch", ["6"] = "fit", ["10"] = "fill", ["22"] = "span" };
        private static readonly string[] Positions = { "center", "tile", "stretch", "fit", "fill", "span" };

        /// <param name="themePath">The .theme file. Relative paths inside it resolve against its folder.</param>
        public static ImportResult Import(string themePath, string? fallbackName = null)
        {
            var theme = ThemeFile.Parse(File.ReadAllBytes(themePath));
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(themePath))!;
            string name = theme.Get("Theme", "DisplayName") is { Length: > 0 } dn && !dn.StartsWith('@')
                ? dn : fallbackName ?? Path.GetFileNameWithoutExtension(themePath);

            var result = new ImportResult
            {
                Manifest = new ThemeBundle
                {
                    Id = ThemeId.From(name), Name = name, Author = "Imported",
                    Origin = new BundleOrigin { Kind = "windowsTheme", Name = Path.GetFileName(themePath), ImportedUtc = DateTimeOffset.UtcNow.ToString("u") },
                },
            };
            var assets = result.Manifest.Assets;

            // ── wallpaper ──
            var desk = "Control Panel\\Desktop";
            var walls = new List<string>();
            if (theme.Get(desk, "MultimonBackgrounds") == "1")
                for (int i = 1; theme.Get(desk, $"Wallpaper{i}") is { } w; i++) walls.Add(w);
            if (walls.Count == 0 && theme.Get(desk, "Wallpaper") is { Length: > 0 } single) walls.Add(single);
            var refs = walls.Select((w, i) => Asset(result, baseDir, w, $"wallpapers/{i + 1}")).ToList();
            if (refs.Count > 0 && refs.All(r => r is not null))
            {
                string? fit = theme.Get(desk, "TileWallpaper") == "1" ? "tile"
                    : theme.Get(desk, "WallpaperStyle") is { } style && Styles.TryGetValue(style, out var f) ? f
                    : int.TryParse(theme.Get(desk, "PicturePosition"), out int pp) && pp is >= 0 and <= 5 ? Positions[pp] : null;
                assets.Wallpaper = refs.Count == 1
                    ? new WallpaperSettings { Kind = "static", Fit = fit, Static = new StaticWallpaper { File = refs[0]! } }
                    : new WallpaperSettings { Kind = "perMonitor", Fit = fit, PerMonitor = refs.Select((r, i) => new MonitorWallpaper { Match = new MonitorMatch { Ordinal = i }, File = r! }).ToList() };
            }
            else if (walls.Count > 0) result.Notes.Add("Wallpaper not imported: an image is missing or is not a supported type.");
            if (theme.Section("Slideshow") is not null) result.Notes.Add("Slideshow not imported yet: it comes in a later version.");

            // ── colors ──
            var vs = "VisualStyles";
            var colors = new ColorSettings();
            if (theme.Get(vs, "AppMode") is { } app) colors.AppMode = app.ToLowerInvariant() is "dark" or "light" ? app.ToLowerInvariant() : null;
            if (theme.Get(vs, "SystemMode") is { } sys) colors.SystemMode = sys.ToLowerInvariant() is "dark" or "light" ? sys.ToLowerInvariant() : null;
            if (theme.Get(vs, "Transparency") is { } tr) colors.Transparency = tr == "1";
            if (theme.Get(vs, "AutoColorization") == "1") colors.Accent = new AccentColors { Mode = "auto" };
            else if (ParseColorization(theme.Get(vs, "ColorizationColor")) is { } rgb) colors.Accent = new AccentColors { Mode = "fixed", Color = rgb };
            if (colors.AppMode is not null || colors.SystemMode is not null || colors.Transparency is not null || colors.Accent is not null)
                assets.Colors = colors;

            // ── desktop icons ──
            foreach (var (section, values) in theme.Sections)
            {
                if (!section.StartsWith("CLSID\\", StringComparison.OrdinalIgnoreCase) || !section.EndsWith("\\DefaultIcon", StringComparison.OrdinalIgnoreCase)) continue;
                string clsid = section.Split('\\')[1];
                if (ShellIconsSection.Find(clsid) is null) { result.Notes.Add($"Icon for {clsid} not imported: not a location RTS knows."); continue; }
                string? main = Value(values, "DefaultValue") ?? Value(values, "Empty");
                string? full = Value(values, "Full");
                if (string.IsNullOrEmpty(main)) continue;
                string slug = clsid.Trim('{', '}').ToLowerInvariant();
                var mainRef = Asset(result, baseDir, main, $"icons/{slug}");
                var fullRef = full is { Length: > 0 } ? Asset(result, baseDir, full, $"icons/{slug}-full") : null;
                if (mainRef is null) { result.Notes.Add($"Icon for {clsid} not imported: file missing or not an .ico."); continue; }
                (assets.Icons ??= new IconSettings()).Targets.Add(new IconTarget { Kind = "shell", Match = clsid, File = mainRef, FullFile = fullRef });
            }

            foreach (var (later, what) in new[] { ("Control Panel\\Cursors", "Cursors"), ("Control Panel\\Colors", "Classic colors"), ("Metrics", "Window metrics"), ("Sounds", "Sounds"), ("boot", "Screensaver") })
                if (theme.Section(later) is { Count: > 0 }) result.Notes.Add($"{what} not imported yet: coming in a later version.");
            if (theme.Sections.Any(s => s.Name.StartsWith("AppEvents", StringComparison.OrdinalIgnoreCase)) && !result.Notes.Any(n => n.StartsWith("Sounds")))
                result.Notes.Add("Sounds not imported yet: coming in a later version.");
            return result;
        }

        private static string? Value(List<(string Key, string Value)> values, string key) =>
            values.FirstOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>"0XC40078D4" or "0X3772CE" to "#RRGGBB" (alpha dropped).</summary>
        public static string? ParseColorization(string? v)
        {
            if (v is null) return null;
            var s = v.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (s.Length is < 1 or > 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return null;
            return "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A path from the theme as a bundle reference: allowed Windows locations stay system
        /// references; anything else is copied in if it exists and is an allowed type.
        /// </summary>
        private static string? Asset(ImportResult result, string baseDir, string path, string stem)
        {
            string trimmed = path.Trim().Trim('"');
            string? systemRef = ToSystemRef(trimmed);
            if (systemRef is not null && AssetRules.IsAllowedSystemRef(systemRef)) return systemRef;

            string full = Environment.ExpandEnvironmentVariables(trimmed);
            int comma = full.LastIndexOf(',');
            if (comma > 1 && int.TryParse(full[(comma + 1)..], out _)) full = full[..comma];   // "file.ico,0"
            if (!Path.IsPathRooted(full)) full = Path.Combine(baseDir, full);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!File.Exists(full) || !AssetRules.AssetExtensions.Contains(ext)) return null;

            string bundlePath = stem + ext;
            result.Files[bundlePath] = full;
            return bundlePath;
        }

        private static string? ToSystemRef(string path)
        {
            if (AssetRules.LooksLikeSystemRef(path)) return path;
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windir + "\\", StringComparison.OrdinalIgnoreCase) ? "%SystemRoot%" + path[windir.Length..] : null;
        }
    }

    /// <summary>
    /// Bundle to Windows .theme (spec F3, D9). Writes "&lt;name&gt;.theme" plus a "&lt;name&gt;" folder of
    /// assets into <c>outDir</c>. Every section Windows requires is present, with a fresh ThemeId.
    /// </summary>
    public static class ThemeFileExporter
    {
        private static readonly Dictionary<string, (string Style, string Tile)> Fit = new()
        {
            ["center"] = ("0", "0"), ["tile"] = ("0", "1"), ["stretch"] = ("2", "0"),
            ["fit"] = ("6", "0"), ["fill"] = ("10", "0"), ["span"] = ("22", "0"),
        };

        public static string Export(LoadedBundle bundle, string outDir)
        {
            var m = bundle.Manifest;
            string safe = string.Concat(m.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (safe.Length == 0) safe = m.Id;
            string assetDir = Path.Combine(outDir, safe);
            Directory.CreateDirectory(assetDir);

            string Copy(string reference, string sub)
            {
                string resolved = bundle.Resolve(reference);
                if (AssetRules.LooksLikeSystemRef(resolved)) return resolved;
                string dir = Path.Combine(assetDir, sub);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, Path.GetFileName(reference));
                File.Copy(resolved, dest, overwrite: true);
                return dest;
            }

            var t = new ThemeFile();
            t.Set("Theme", "DisplayName", m.Name);
            t.Set("Theme", "ThemeId", Guid.NewGuid().ToString("B").ToUpperInvariant());

            var w = m.Assets.Wallpaper;
            if (w?.Kind == "static" && w.Static is not null) t.Set("Control Panel\\Desktop", "Wallpaper", Copy(w.Static.File, "DesktopBackground"));
            else if (w?.Kind == "perMonitor" && w.PerMonitor is { Count: > 0 } pm)
            {
                t.Set("Control Panel\\Desktop", "Wallpaper", Copy(pm[0].File, "DesktopBackground"));
                t.Set("Control Panel\\Desktop", "MultimonBackgrounds", "1");
                foreach (var e in pm.Where(e => e.Match.Ordinal is not null).OrderBy(e => e.Match.Ordinal))
                    t.Set("Control Panel\\Desktop", $"Wallpaper{e.Match.Ordinal + 1}", Copy(e.File, "DesktopBackground"));
            }
            else if (w?.Kind == "timeOfDay" && w.TimeOfDay is not null)
                t.Set("Control Panel\\Desktop", "Wallpaper", Copy(w.TimeOfDay.Segments.Day[0], "DesktopBackground"));
            else t.Set("Control Panel\\Desktop", "Wallpaper", "");
            if (w?.Fit is { } fit && Fit.TryGetValue(fit.ToLowerInvariant(), out var s))
            {
                t.Set("Control Panel\\Desktop", "WallpaperStyle", s.Style);
                t.Set("Control Panel\\Desktop", "TileWallpaper", s.Tile);
            }

            // [VisualStyles] is required: Windows ignores a theme without it (DOC).
            t.Set("VisualStyles", "Path", @"%SystemRoot%\resources\themes\Aero\Aero.msstyles");
            t.Set("VisualStyles", "ColorStyle", "NormalColor");
            t.Set("VisualStyles", "Size", "NormalSize");
            var c = m.Assets.Colors;
            if (c?.Accent?.Mode == "auto") t.Set("VisualStyles", "AutoColorization", "1");
            else if (c?.Accent?.Color is { } hex)
            {
                var (r, g, b) = ColorsSection.Hex(hex, "accent");
                t.Set("VisualStyles", "AutoColorization", "0");
                t.Set("VisualStyles", "ColorizationColor", $"0XC4{r:X2}{g:X2}{b:X2}");
            }
            if (c?.Transparency is { } tr) t.Set("VisualStyles", "Transparency", tr ? "1" : "0");
            if (c?.AppMode is "light" or "dark") t.Set("VisualStyles", "AppMode", c.AppMode == "light" ? "Light" : "Dark");
            if (c?.SystemMode is "light" or "dark") t.Set("VisualStyles", "SystemMode", c.SystemMode == "light" ? "Light" : "Dark");

            foreach (var icon in m.Assets.Icons?.Targets.Where(x => x.Kind == "shell") ?? Enumerable.Empty<IconTarget>())
            {
                var spec = ShellIconsSection.Find(icon.Match ?? "");
                if (spec is null || icon.File is null) continue;
                string section = $@"CLSID\{spec.Clsid}\DefaultIcon";
                string main = Copy(icon.File, "Icons");
                if (spec.TwoState)
                {
                    t.Set(section, "Empty", main);
                    t.Set(section, "Full", icon.FullFile is null ? main : Copy(icon.FullFile, "Icons"));
                }
                else t.Set(section, "DefaultValue", main);
            }

            t.Set("MasterThemeSelector", "MTSM", LiveMtsm() ?? "DABJDKT");

            string themePath = Path.Combine(outDir, safe + ".theme");
            File.WriteAllBytes(themePath, t.ToBytes());
            return themePath;
        }

        /// <summary>MTSM as Windows writes it today (Windows 11 uses RJSPBS; the DOC page says DABJDKT).</summary>
        private static string? LiveMtsm()
        {
            try
            {
                string custom = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Themes", "Custom.theme");
                return File.Exists(custom) ? ThemeFile.Parse(File.ReadAllBytes(custom)).Get("MasterThemeSelector", "MTSM") : null;
            }
            catch { return null; }
        }
    }
}
