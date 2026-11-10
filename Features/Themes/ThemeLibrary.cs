using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Revenant_Theme_Studio.Features.Themes.Bundles;
using Revenant_Theme_Studio.Features.Themes.Engine;
using Revenant_Theme_Studio.Features.Themes.Formats;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Features.Themes.Sections;
using Revenant_Theme_Studio.Features.Wallpaper.Services;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;

namespace Revenant_Theme_Studio.Features.Themes
{
    public sealed class LibraryEntry
    {
        public required string Folder { get; init; }
        public required ThemeBundle Manifest { get; init; }
        public required string BundlePath { get; init; }
        public string? PreviewPath { get; init; }

        /// <summary>What the theme changes, for the card ("Wallpaper · Colors · 12 icons").</summary>
        public string Summary
        {
            get
            {
                var a = Manifest.Assets;
                var parts = new List<string>();
                if (a.Wallpaper is not null) parts.Add(a.Wallpaper.Kind == "timeOfDay" ? "Time-of-day wallpaper" : "Wallpaper");
                if (a.Colors is not null) parts.Add("Colors");
                if (a.Icons is { Targets.Count: > 0 } i) parts.Add($"{i.Targets.Count} icon{(i.Targets.Count == 1 ? "" : "s")}");
                if (a.Other is { Count: > 0 } o) parts.AddRange(o.Keys.Select(k => char.ToUpperInvariant(k[0]) + k[1..]));
                return parts.Count == 0 ? "Empty theme" : string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Installed themes. Each lives in Themes\&lt;id&gt;-&lt;hash&gt;\theme.rtstheme; its files are staged under
    /// Assets\&lt;same&gt;\. Every import, whatever its format, becomes a validated .rtstheme first.
    /// Assets are never deleted with a theme: an applied theme's wallpaper and icons point at them.
    /// </summary>
    public sealed class ThemeLibrary
    {
        public string Root { get; }
        private string Themes => Path.Combine(Root, "Themes");
        private string Assets => Path.Combine(Root, "Assets");
        private string Work => Path.Combine(Root, "Imports");

        public ThemeLibrary(string root)
        {
            Root = root;
            Directory.CreateDirectory(Themes);
            Directory.CreateDirectory(Assets);
        }

        public static ThemeLibrary Default() => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revenant Theme Studio"));

        public static readonly string[] Extensions = { ".rtstheme", ".theme", ".themepack", ".deskthemepack", ".ddw" };

        public IReadOnlyList<LibraryEntry> List()
        {
            var list = new List<LibraryEntry>();
            foreach (var dir in Directory.EnumerateDirectories(Themes))
            {
                string bundle = Path.Combine(dir, "theme.rtstheme");
                if (!File.Exists(bundle)) continue;
                try
                {
                    ThemeBundle m;
                    using (var fs = File.OpenRead(bundle)) m = BundleReader.ReadManifest(fs);
                    string folder = Path.GetFileName(dir);
                    var w = m.Assets.Wallpaper;
                    string? firstWall = w?.Static?.File ?? w?.PerMonitor?.FirstOrDefault()?.File ?? w?.TimeOfDay?.Segments.Day.FirstOrDefault();
                    string? preview = Staged(m, folder, m.Preview) ?? Staged(m, folder, firstWall);
                    list.Add(new LibraryEntry { Folder = folder, Manifest = m, BundlePath = bundle, PreviewPath = preview });
                }
                catch (Exception) { /* a damaged entry is skipped, never deleted */ }
            }
            return list.OrderBy(e => e.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Where a bundle file was staged (or a system reference, expanded), if it is an image on disk.</summary>
        private string? Staged(ThemeBundle m, string folder, string? reference)
        {
            if (reference is null) return null;
            if (AssetRules.LooksLikeSystemRef(reference))
            {
                string expanded = Environment.ExpandEnvironmentVariables(reference);
                return File.Exists(expanded) ? expanded : null;
            }
            var file = m.Files?.FirstOrDefault(f => f.Path == reference);
            if (file is null) return null;
            string staged = Path.Combine(Assets, folder, file.Sha256[..16] + Path.GetExtension(reference).ToLowerInvariant());
            return File.Exists(staged) ? staged : null;
        }

        /// <summary>Imports any supported file. Returns the entry and notes on anything left out.</summary>
        public (LibraryEntry Entry, IReadOnlyList<string> Notes) Import(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string work = Path.Combine(Work, Guid.NewGuid().ToString("N"));
            try
            {
                if (ext == ".rtstheme") return (AddBundle(path), Array.Empty<string>());
                ImportResult result = ext switch
                {
                    ".theme" => ThemeFileImporter.Import(path),
                    ".themepack" or ".deskthemepack" => ThemePackImporter.Import(path, work),
                    ".ddw" => DdwImporter.Import(path, work),
                    _ => throw new InvalidDataException($"{ext} files aren't themes Revenant Theme Studio can import."),
                };
                return (Add(result), result.Notes);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        /// <summary>Writes an import or capture as a bundle and adds it.</summary>
        public LibraryEntry Add(ImportResult result)
        {
            Directory.CreateDirectory(Work);
            string tmp = Path.Combine(Work, Guid.NewGuid().ToString("N") + ".rtstheme");
            try
            {
                BundleWriter.Write(result.Manifest, result.Files, tmp);
                return AddBundle(tmp);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        private LibraryEntry AddBundle(string bundlePath)
        {
            string hash;
            using (var fs = File.OpenRead(bundlePath)) hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant()[..8];
            ThemeBundle manifest;
            using (var fs = File.OpenRead(bundlePath)) manifest = BundleReader.ReadManifest(fs);
            string folder = $"{manifest.Id}-{hash}";

            // Full validation and staging before it joins the library.
            using (var fs = File.OpenRead(bundlePath)) BundleReader.Read(fs, Path.Combine(Assets, folder));
            string dest = Path.Combine(Themes, folder);
            Directory.CreateDirectory(dest);
            File.Copy(bundlePath, Path.Combine(dest, "theme.rtstheme"), overwrite: true);
            return List().First(e => e.Folder == folder);
        }

        /// <summary>Re-validates the stored bundle and returns it with its staged files.</summary>
        public LoadedBundle Load(LibraryEntry entry)
        {
            using var fs = File.OpenRead(entry.BundlePath);
            return BundleReader.Read(fs, Path.Combine(Assets, entry.Folder));
        }

        /// <summary>Removes a theme from the list. Its staged files stay: an applied theme may still use them.</summary>
        public void Remove(LibraryEntry entry)
        {
            string dir = Path.Combine(Themes, entry.Folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        public LibraryEntry? Find(string bundleId) =>
            List().FirstOrDefault(e => e.Manifest.Id == bundleId);
    }

    /// <summary>The one place the real engine is assembled.</summary>
    public static class ThemeEngineFactory
    {
        public static ThemeApplyEngine Create(TransactionStore? store = null) => new(
            new IThemeSection[]
            {
                new WallpaperSection(new WallpaperEngineService()),
                new ColorsSection(),
                new ShellIconsSection(),
                new SystemIconsSection(new MunWriterClient()),
            },
            new RegistryStore(), new ShellBroadcaster(), store ?? TransactionStore.Default(), WindowsBuild.Current);
    }
}
