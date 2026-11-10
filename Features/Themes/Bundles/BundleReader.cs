using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Revenant_Theme_Studio.Features.Themes.Platform;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Features.Themes.Bundles
{
    /// <summary>A validated bundle: its manifest and where each of its files was staged.</summary>
    public sealed class LoadedBundle
    {
        public required ThemeBundle Manifest { get; init; }
        /// <summary>Bundle path (e.g. "icons/documents.ico") to the staged absolute path.</summary>
        public required IReadOnlyDictionary<string, string> Files { get; init; }

        /// <summary>The staged file for a bundle path, or the reference itself if it is an allowed system reference.</summary>
        public string Resolve(string reference) =>
            AssetRules.LooksLikeSystemRef(reference) ? reference
            : Files.TryGetValue(reference, out var p) ? p
            : throw new InvalidDataException($"\"{reference}\" is not in this theme.");
    }

    /// <summary>
    /// Reads .rtstheme bundles under the data-only rules (spec F1). Entries are read one by one,
    /// never with ExtractToDirectory; every size, name, type, hash and reference is checked before
    /// a theme can be applied. Files are written under their content hash, so names are always RTS's own.
    /// </summary>
    public static class BundleReader
    {
        /// <summary>Section keys this version understands and can apply.</summary>
        public static readonly IReadOnlySet<string> Understood =
            new HashSet<string>(StringComparer.Ordinal) { "icons", "wallpaper", "colors", "systemIcons", "shellOverrides" };

        public static ThemeBundle ReadManifest(Stream zip)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            return ParseManifest(archive);
        }

        /// <summary>Validates everything and stages each file into <paramref name="assetDir"/>.</summary>
        public static LoadedBundle Read(Stream zip, string assetDir)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = ParseManifest(archive);
            var listed = (manifest.Files ?? new()).ToDictionary(f => f.Path, StringComparer.Ordinal);
            bool v1 = manifest.Files is null;

            Directory.CreateDirectory(assetDir);
            var staged = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            foreach (var entry in archive.Entries)
            {
                string path = entry.FullName;
                if (path.EndsWith('/') && entry.Length == 0) continue;               // folder entry
                if (!seen.Add(path)) throw Bad($"\"{path}\" appears twice");
                if (path == "manifest.json") continue;
                AssetRules.ValidatePath(path);
                AssetRules.ValidateKind(path);

                if (entry.Length > AssetRules.MaxFileBytes) throw Bad($"\"{path}\" is larger than 256 MiB");
                total += entry.Length;
                if (total > AssetRules.MaxTotalBytes) throw Bad("theme is larger than 1 GiB unpacked");
                if (entry.Length >= AssetRules.RatioCheckFrom && entry.Length / Math.Max(entry.CompressedLength, 1) > AssetRules.MaxRatio)
                    throw Bad($"\"{path}\" is compressed suspiciously well");

                BundleFile? expected = null;
                if (!v1 && !listed.TryGetValue(path, out expected)) throw Bad($"\"{path}\" is not listed in the manifest");

                var (hash, bytes, stagedPath) = StageEntry(entry, path, assetDir);
                if (expected is not null)
                {
                    if (!hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) throw Bad($"\"{path}\" does not match its SHA-256");
                    if (bytes != expected.Bytes) throw Bad($"\"{path}\" is not the size the manifest says");
                }
                staged[path] = stagedPath;
            }

            foreach (var f in listed.Keys)
                if (!staged.ContainsKey(f)) throw Bad($"\"{f}\" is listed in the manifest but missing");

            CheckReferences(manifest, staged);
            return new LoadedBundle { Manifest = manifest, Files = staged };
        }

        private static (string hash, long bytes, string path) StageEntry(ZipArchiveEntry entry, string path, string assetDir)
        {
            string tmp = Path.Combine(assetDir, $".{Guid.NewGuid():N}.part");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            var head = new byte[16];
            int headLen = 0;
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        bytes += n;
                        // The header's size is not trusted: stop at the cap however the entry lies.
                        if (bytes > AssetRules.MaxFileBytes || bytes > entry.Length) throw Bad($"\"{path}\" unpacks larger than it claims");
                        if (headLen < head.Length) { int c = Math.Min(head.Length - headLen, n); Array.Copy(buffer, 0, head, headLen, c); headLen += c; }
                        sha.AppendData(buffer, 0, n);
                        output.Write(buffer, 0, n);
                    }
                }
                AssetRules.ValidateContent(path, head[..headLen]);

                string hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                string final = Path.Combine(assetDir, hash[..16] + Path.GetExtension(path).ToLowerInvariant());
                if (File.Exists(final)) File.Delete(tmp);              // same content already staged
                else File.Move(tmp, final);
                return (hash, bytes, final);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        private static ThemeBundle ParseManifest(ZipArchive archive)
        {
            if (archive.Entries.Count > AssetRules.MaxEntries) throw Bad("too many files");
            var entries = archive.Entries.Where(e => e.FullName == "manifest.json").ToList();
            if (entries.Count != 1) throw Bad("needs exactly one manifest.json at its root");
            var entry = entries[0];
            if (entry.Length > AssetRules.MaxManifestBytes) throw Bad("manifest.json is too large");

            byte[] json;
            using (var s = entry.Open())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[8192];
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > AssetRules.MaxManifestBytes) throw Bad("manifest.json is too large");
                }
                json = ms.ToArray();
            }

            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
                RejectUrls(doc.RootElement);

            ThemeBundle manifest;
            try { manifest = JsonSerializer.Deserialize<ThemeBundle>(json, Json.Options) ?? throw Bad("manifest.json is empty"); }
            catch (JsonException ex) { throw Bad($"manifest.json is not valid: {ex.Message}"); }

            if (manifest.Schema != ThemeBundle.SchemaV1 && manifest.Schema != ThemeBundle.SchemaV2)
                throw Bad($"unknown schema \"{manifest.Schema}\"");
            if (!ThemeId.IsValid(manifest.Id)) throw Bad("id must be 1 to 64 letters, digits, dots, dashes or underscores");
            if (string.IsNullOrWhiteSpace(manifest.Name)) throw Bad("has no name");
            if (Version.TryParse(manifest.MinRtsVersion, out var need) && need > ThemeEngineInfo.Version)
                throw Bad($"needs Revenant Theme Studio {manifest.MinRtsVersion} or newer");
            foreach (var key in manifest.Requires ?? new())
                if (!Understood.Contains(key)) throw Bad($"uses \"{key}\", which this version of Revenant Theme Studio can't apply yet");
            if (manifest.Files is not null)
            {
                if (manifest.Files.Count > AssetRules.MaxEntries) throw Bad("too many files");
                foreach (var f in manifest.Files)
                {
                    AssetRules.ValidatePath(f.Path);
                    if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw Bad($"\"{f.Path}\" has a malformed SHA-256");
                }
                if (manifest.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
                    throw Bad("lists a file twice");
            }

            UpgradeV1(manifest);
            return manifest;
        }

        /// <summary>Maps rts.theme/v1 systemIcons and shellOverrides into icons.targets.</summary>
        public static void UpgradeV1(ThemeBundle m)
        {
            var a = m.Assets;
            if (a.SystemIcons is null && a.ShellOverrides is null) { m.Schema = ThemeBundle.SchemaV2; return; }
            a.Icons ??= new IconSettings();
            foreach (var s in a.SystemIcons?.MunSwaps ?? new())
                a.Icons.Targets.Add(new IconTarget { Kind = "system", Group = s.IconGroup, File = s.File, SourceFingerprint = s.SourceFingerprint, Label = s.Label });
            foreach (var o in a.ShellOverrides ?? new())
                a.Icons.Targets.Add(new IconTarget
                {
                    Kind = o.Scope.ToLowerInvariant() switch { "system" => "shell", "drive" => "drive", _ => o.Scope.ToLowerInvariant() },
                    Match = o.Target, File = o.File, Label = o.Label,
                });
            a.SystemIcons = null;
            a.ShellOverrides = null;
            m.Schema = ThemeBundle.SchemaV2;
        }

        /// <summary>Every asset the manifest names must be in the bundle or be an allowed system reference.</summary>
        private static void CheckReferences(ThemeBundle m, IReadOnlyDictionary<string, string> staged)
        {
            void Check(string? reference, string what)
            {
                if (string.IsNullOrEmpty(reference)) return;
                if (AssetRules.LooksLikeSystemRef(reference))
                {
                    if (!AssetRules.IsAllowedSystemRef(reference)) throw Bad($"{what} points outside the allowed Windows folders: {reference}");
                    return;
                }
                if (!staged.ContainsKey(reference)) throw Bad($"{what} \"{reference}\" is not in the theme");
            }

            Check(m.Preview, "preview");
            foreach (var t in m.Assets.Icons?.Targets ?? new()) { Check(t.File, "icon"); Check(t.FullFile, "icon"); }
            var w = m.Assets.Wallpaper;
            if (w is not null)
            {
                Check(w.Static?.File, "wallpaper");
                foreach (var p in w.PerMonitor ?? new()) Check(p.File, "wallpaper");
                var seg = w.TimeOfDay?.Segments;
                foreach (var f in (seg?.Sunrise ?? new()).Concat(seg?.Day ?? new()).Concat(seg?.Sunset ?? new()).Concat(seg?.Night ?? new()))
                    Check(f, "wallpaper");
            }
        }

        private static void RejectUrls(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) RejectUrls(p.Value); break;
                case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) RejectUrls(i); break;
                case JsonValueKind.String:
                    var s = e.GetString() ?? "";
                    if (s.Contains("://") || s.StartsWith(@"\\")) throw Bad("contains a web or network address; themes are data only");
                    break;
            }
        }

        private static InvalidDataException Bad(string why) => new($"This theme was refused: {why}.");
    }

    public static class ThemeId
    {
        public static bool IsValid(string? id) =>
            !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            && !id.StartsWith('.') && !id.EndsWith('.');

        /// <summary>A valid id made from any name.</summary>
        public static string From(string name)
        {
            var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
            var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (id.Length > 64) id = id[..64].TrimEnd('-');
            return id.Length == 0 ? "theme-" + Guid.NewGuid().ToString("N")[..8] : id;
        }
    }

    public static class ThemeEngineInfo
    {
        /// <summary>The theme engine version bundles are checked against (minRtsVersion).</summary>
        public static readonly Version Version = new(2, 0, 0);
    }
}
