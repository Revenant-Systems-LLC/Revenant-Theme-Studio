using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Themes.Formats
{
    /// <summary>.themepack: extract the cabinet, then import the .theme inside it.</summary>
    public static class ThemePackImporter
    {
        public static ImportResult Import(string packPath, string workDir)
        {
            var cab = Cabinet.Extract(packPath, workDir);
            var themes = cab.Extracted.Where(f => f.EndsWith(".theme", StringComparison.OrdinalIgnoreCase)).ToList();
            if (themes.Count != 1) throw new InvalidDataException("A .themepack must hold exactly one .theme file.");
            var result = ThemeFileImporter.Import(themes[0], Path.GetFileNameWithoutExtension(packPath));
            result.Manifest.Origin!.Name = Path.GetFileName(packPath);
            if (cab.Skipped.Count > 0) result.Notes.Add($"{cab.Skipped.Count} file(s) in the pack were left out (types a theme can't carry).");
            return result;
        }
    }

    /// <summary>
    /// WinDynamicDesktop .ddw (spec F7): a zip with one root .json and root-level images. Maps to
    /// a time-of-day wallpaper. The zip is read entry by entry under the bundle caps.
    /// </summary>
    public static class DdwImporter
    {
        private sealed class DdwJson
        {
            public string? DisplayName { get; set; }
            public string? ImageFilename { get; set; }
            public string? ImageCredits { get; set; }
            public int? DayHighlight { get; set; }
            public int? NightHighlight { get; set; }
            public int[]? SunriseImageList { get; set; }
            public int[]? DayImageList { get; set; }
            public int[]? SunsetImageList { get; set; }
            public int[]? NightImageList { get; set; }
        }

        public static ImportResult Import(string ddwPath, string workDir)
        {
            Directory.CreateDirectory(workDir);
            using var zip = ZipFile.OpenRead(ddwPath);
            if (zip.Entries.Count > AssetRules.MaxEntries) throw new InvalidDataException("This .ddw holds too many files.");
            var jsons = zip.Entries.Where(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
            if (jsons.Count != 1) throw new InvalidDataException("A .ddw must hold exactly one .json file at its root.");
            if (jsons[0].Length > AssetRules.MaxManifestBytes) throw new InvalidDataException("The .ddw's .json is too large.");

            DdwJson meta;
            using (var s = jsons[0].Open())
                meta = JsonSerializer.Deserialize<DdwJson>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new InvalidDataException("The .ddw's .json is empty.");
            if (meta.ImageFilename is not { Length: > 0 } pattern || !pattern.Contains('*'))
                throw new InvalidDataException("The .ddw's imageFilename must contain * for the image number.");
            if (meta.DayImageList is not { Length: > 0 } || meta.NightImageList is not { Length: > 0 })
                throw new InvalidDataException("The .ddw needs day and night images.");

            string id = Path.GetFileNameWithoutExtension(ddwPath);
            string name = meta.DisplayName is { Length: > 0 } dn ? dn : id.Replace('_', ' ');
            var result = new ImportResult
            {
                Manifest = new ThemeBundle
                {
                    Id = ThemeId.From(name), Name = name, Author = meta.ImageCredits ?? "Imported",
                    Origin = new BundleOrigin { Kind = "ddw", Name = Path.GetFileName(ddwPath), ImportedUtc = DateTimeOffset.UtcNow.ToString("u") },
                },
            };

            long total = 0;
            List<string>? Images(int[]? numbers)
            {
                if (numbers is not { Length: > 0 }) return null;
                var list = new List<string>();
                foreach (int n in numbers)
                {
                    string file = pattern.Replace("*", n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    string bundlePath = "wallpapers/" + file;
                    if (!result.Files.ContainsKey(bundlePath))
                    {
                        var entry = zip.GetEntry(file) ?? throw new InvalidDataException($"The .ddw is missing image \"{file}\".");
                        AssetRules.ValidatePath(bundlePath);
                        AssetRules.ValidateKind(bundlePath);
                        if (entry.Length > AssetRules.MaxFileBytes) throw new InvalidDataException($"\"{file}\" is larger than 256 MiB.");
                        total += entry.Length;
                        if (total > AssetRules.MaxTotalBytes) throw new InvalidDataException("This .ddw is larger than 1 GiB unpacked.");
                        string dest = Path.Combine(workDir, Guid.NewGuid().ToString("N") + Path.GetExtension(file));
                        using (var input = entry.Open())
                        using (var output = File.Create(dest))
                            CopyBounded(input, output, entry.Length);
                        result.Files[bundlePath] = dest;
                    }
                    list.Add(bundlePath);
                }
                return list;
            }

            var day = Images(meta.DayImageList)!;
            var sunrise = Images(meta.SunriseImageList);
            var sunset = Images(meta.SunsetImageList);
            // An empty sunrise/sunset list, or one equal to the day list, means the two-segment model.
            if (sunrise is not null && sunrise.SequenceEqual(day)) sunrise = null;
            if (sunset is not null && sunset.SequenceEqual(day)) sunset = null;

            result.Manifest.Assets.Wallpaper = new WallpaperSettings
            {
                Kind = "timeOfDay", Fit = "fill",
                TimeOfDay = new TimeOfDayWallpaper
                {
                    Segments = new TimeOfDaySegments { Sunrise = sunrise, Day = day, Sunset = sunset, Night = Images(meta.NightImageList)! },
                    Credits = meta.ImageCredits, DayHighlight = meta.DayHighlight, NightHighlight = meta.NightHighlight,
                },
            };
            result.Notes.Add("Time-of-day wallpaper uses fixed times for now (sunrise 6:00, day 8:00, sunset 18:00, night 20:00). Sun-based times come in a later version.");
            return result;
        }

        private static void CopyBounded(Stream input, Stream output, long declared)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > declared || total > AssetRules.MaxFileBytes) throw new InvalidDataException("An image unpacks larger than it claims.");
                output.Write(buffer, 0, n);
            }
        }
    }

    /// <summary>
    /// "Save my current look as a theme": the live wallpaper, colors and shell/drive icons as a
    /// bundle. Reads only; files RTS can't carry (e.g. extension-less transcodes) are noted, not copied.
    /// </summary>
    public static class ThemeCapture
    {
        private static readonly string[] Fits = { "center", "tile", "stretch", "fit", "fill", "span" };

        public static ImportResult Capture(string name, IRegistryStore reg, IWallpaperApi wallpaper)
        {
            var result = new ImportResult
            {
                Manifest = new ThemeBundle
                {
                    Id = ThemeId.From(name), Name = name, Author = Environment.UserName,
                    Origin = new BundleOrigin { Kind = "capture", ImportedUtc = DateTimeOffset.UtcNow.ToString("u") },
                },
            };
            var assets = result.Manifest.Assets;

            // ── wallpaper ──
            var monitors = wallpaper.Monitors();
            var walls = monitors.Select(m => wallpaper.GetWallpaper(m.Id)).ToList();
            string fit = Fits[Math.Clamp(wallpaper.GetPosition(), 0, 5)];
            uint bg = wallpaper.GetBackgroundColor();
            string bgHex = $"#{bg & 0xFF:X2}{(bg >> 8) & 0xFF:X2}{(bg >> 16) & 0xFF:X2}";
            var refs = walls.Select((w, i) => Copy(result, w, $"wallpapers/monitor-{i + 1}")).ToList();
            if (walls.Count > 0 && refs.All(r => r is not null))
            {
                assets.Wallpaper = walls.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? new WallpaperSettings { Kind = "static", Fit = fit, BackgroundColor = bgHex, Static = new StaticWallpaper { File = refs[0]! } }
                    : new WallpaperSettings { Kind = "perMonitor", Fit = fit, BackgroundColor = bgHex,
                        PerMonitor = refs.Select((r, i) => new MonitorWallpaper { Match = new MonitorMatch { Ordinal = i }, File = r! }).ToList() };
            }
            else if (walls.Count > 0) result.Notes.Add("Wallpaper not captured: one of the images can't be carried in a theme (missing, or not JPG/PNG/BMP).");

            // ── colors ──
            uint? D(string k, string n) => reg.Get(RegHive.CurrentUser, k, n) is { Number: { } v } ? (uint)v : null;
            var colors = new ColorSettings
            {
                AppMode = D(ColorsSection.Personalize, "AppsUseLightTheme") is { } a ? (a == 1 ? "light" : "dark") : null,
                SystemMode = D(ColorsSection.Personalize, "SystemUsesLightTheme") is { } s ? (s == 1 ? "light" : "dark") : null,
                Transparency = D(ColorsSection.Personalize, "EnableTransparency") is { } t ? t == 1 : null,
            };
            var accent = new AccentColors
            {
                OnStartTaskbar = D(ColorsSection.Personalize, "ColorPrevalence") is { } st ? st == 1 : null,
                OnTitleBars = D(ColorsSection.Dwm, "ColorPrevalence") is { } tb ? tb == 1 : null,
            };
            if (D(ColorsSection.Desktop, "AutoColorization") == 1) accent.Mode = "auto";
            else if (D(ColorsSection.Dwm, "AccentColor") is { } abgr)
            {
                accent.Color = Rgb(abgr);
                if (D(ColorsSection.Accent, "AccentColorMenu") is { } menu) accent.AccentMenu = Rgb(menu);
                if (D(ColorsSection.Accent, "StartColorMenu") is { } start) accent.StartMenu = Rgb(start);
                if (reg.Get(RegHive.CurrentUser, ColorsSection.Accent, "AccentPalette") is { Bytes: { Length: 32 } p })
                    accent.Palette = new AccentPalette { Source = "captured", Colors = Enumerable.Range(0, 8).Select(i => $"#{p[i * 4]:X2}{p[i * 4 + 1]:X2}{p[i * 4 + 2]:X2}{p[i * 4 + 3]:X2}").ToList() };
            }
            colors.Accent = accent;
            assets.Colors = colors;

            // ── shell and drive icons ──
            var icons = new IconSettings();
            foreach (var (label, spec) in new ShellIconService().Targets)
            {
                string key = $@"Software\Classes\CLSID\{spec.Clsid}\DefaultIcon";
                if (reg.Get(RegHive.CurrentUser, key, "")?.Text is not { Length: > 0 } value) continue;
                string slug = label.ToLowerInvariant().Replace(' ', '-');
                string? file = IconRef(result, value, $"icons/{slug}");
                if (file is null) { result.Notes.Add($"{label} icon not captured: {value}"); continue; }
                string? full = spec.TwoState && reg.Get(RegHive.CurrentUser, key, "Full")?.Text is { Length: > 0 } f ? IconRef(result, f, $"icons/{slug}-full") : null;
                icons.Targets.Add(new IconTarget { Kind = "shell", Match = label, File = file, FullFile = full });
            }
            const string drives = @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";
            for (char d = 'A'; d <= 'Z'; d++)
            {
                if (reg.Get(RegHive.CurrentUser, $@"{drives}\{d}\DefaultIcon", "")?.Text is not { Length: > 0 } value) continue;
                if (IconRef(result, value, $"icons/drive-{char.ToLowerInvariant(d)}") is { } file)
                    icons.Targets.Add(new IconTarget { Kind = "drive", Match = d.ToString(), File = file });
            }
            if (icons.Targets.Count > 0) assets.Icons = icons;
            return result;
        }

        private static string Rgb(uint abgr) => $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

        private static string? IconRef(ImportResult result, string value, string stem)
        {
            string v = value.Trim().Trim('"');
            if (AssetRules.IsAllowedSystemRef(v)) return v;
            int comma = v.LastIndexOf(',');
            if (comma > 1 && int.TryParse(v[(comma + 1)..], out _)) v = v[..comma];
            return Copy(result, Environment.ExpandEnvironmentVariables(v), stem);
        }

        private static string? Copy(ImportResult result, string path, string stem)
        {
            if (path.Length == 0 || !File.Exists(path)) return null;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!AssetRules.AssetExtensions.Contains(ext)) return null;
            string bundlePath = stem + ext;
            result.Files[bundlePath] = path;
            return bundlePath;
        }
    }
}
