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
    /// <summary>Writes .rtstheme bundles under the same rules the reader enforces.</summary>
    public static class BundleWriter
    {
        /// <summary>
        /// Writes <paramref name="manifest"/> and <paramref name="files"/> (bundle path to source file)
        /// to <paramref name="outPath"/>. Fills in schema and files[] with real hashes. Atomic.
        /// </summary>
        public static void Write(ThemeBundle manifest, IReadOnlyDictionary<string, string> files, string outPath)
        {
            if (!ThemeId.IsValid(manifest.Id)) throw new InvalidDataException("Theme id is not valid.");
            BundleReader.UpgradeV1(manifest);
            manifest.Schema = ThemeBundle.CurrentSchema;

            var listed = new List<BundleFile>();
            foreach (var (path, source) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                AssetRules.ValidatePath(path);
                AssetRules.ValidateKind(path);
                var info = new FileInfo(source);
                if (info.Length > AssetRules.MaxFileBytes) throw new InvalidDataException($"\"{path}\" is larger than 256 MiB.");
                using var fs = info.OpenRead();
                var head = new byte[16];
                int got = fs.Read(head, 0, head.Length);
                AssetRules.ValidateContent(path, head[..got]);
                fs.Position = 0;
                listed.Add(new BundleFile
                {
                    Path = path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant(),
                    Bytes = info.Length,
                });
            }
            manifest.Files = listed;

            string tmp = outPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var m = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (var w = m.Open()) JsonSerializer.Serialize(w, manifest, Json.Options);
                    foreach (var (path, source) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                    {
                        // Images and sounds are already compressed; storing them is faster and no bigger.
                        var level = Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
                            ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                        zip.CreateEntryFromFile(source, path, level);
                    }
                }
                File.Move(tmp, outPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
