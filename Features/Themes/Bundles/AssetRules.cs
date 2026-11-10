using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Revenant_Theme_Studio.Features.Themes.Bundles
{
    /// <summary>
    /// The data-only rules every bundle file obeys (spec F1). The reader and the writer both use
    /// these, so RTS can never write a bundle it would refuse to read.
    /// </summary>
    public static class AssetRules
    {
        public const long MaxEntries = 1500;
        public const long MaxTotalBytes = 1L << 30;      // 1 GiB uncompressed
        public const long MaxFileBytes = 256L << 20;     // 256 MiB per file
        public const long MaxManifestBytes = 1L << 20;   // 1 MiB
        public const int MaxRatio = 100;                 // uncompressed : compressed, for large entries
        public const long RatioCheckFrom = 10L << 20;    // small files can compress hugely and harmlessly

        /// <summary>Asset types and the bytes each must start with.</summary>
        private static readonly Dictionary<string, Func<byte[], bool>> Magic = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"]  = b => StartsWith(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            [".jpg"]  = b => StartsWith(b, 0xFF, 0xD8, 0xFF),
            [".jpeg"] = b => StartsWith(b, 0xFF, 0xD8, 0xFF),
            [".bmp"]  = b => StartsWith(b, 0x42, 0x4D),
            [".ico"]  = b => StartsWith(b, 0x00, 0x00, 0x01, 0x00),
            [".cur"]  = b => StartsWith(b, 0x00, 0x00, 0x02, 0x00),
            [".ani"]  = b => Riff(b, "ACON"),
            [".wav"]  = b => Riff(b, "WAVE"),
        };

        /// <summary>Root-level files allowed besides assets.</summary>
        private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
            { "manifest.json", "LICENSE.txt" };

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static IReadOnlyCollection<string> AssetExtensions => Magic.Keys;

        /// <summary>
        /// Throws unless <paramref name="path"/> is a safe bundle-relative path: forward slashes, no
        /// "..", no drive or root, no stream colons, no device names, no trailing dots or spaces.
        /// </summary>
        public static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 200) throw Bad(path, "empty or too long");
            if (path.Contains('\\')) throw Bad(path, "backslash; bundle paths use forward slashes");
            if (path.StartsWith('/') || path.Contains(':')) throw Bad(path, "absolute path or stream name");
            foreach (var seg in path.Split('/'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") throw Bad(path, "empty, . or .. segment");
                if (seg.EndsWith('.') || seg.EndsWith(' ') || seg.StartsWith(' ')) throw Bad(path, "leading or trailing dot or space");
                if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw Bad(path, "invalid character");
                if (Reserved.Contains(Path.GetFileNameWithoutExtension(seg))) throw Bad(path, "device name");
            }
        }

        /// <summary>Throws unless the path is an allowed file type for its place in the bundle.</summary>
        public static void ValidateKind(string path)
        {
            if (!path.Contains('/') && RootFiles.Contains(path)) return;
            if (!Magic.ContainsKey(Path.GetExtension(path)))
                throw Bad(path, "file type not allowed in a theme (themes carry images, icons, cursors and sounds only)");
        }

        /// <summary>Throws unless the first bytes match what the extension promises.</summary>
        public static void ValidateContent(string path, byte[] head)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z') throw Bad(path, "is an executable");
                return;
            }
            if (!Magic.TryGetValue(ext, out var check) || !check(head))
                throw Bad(path, $"content is not a real {ext.TrimStart('.').ToUpperInvariant()} file");
        }

        // Allowed Windows locations a bundle may point at instead of carrying a file.
        private static readonly string[] SystemRoots =
            { @"Cursors\", @"Media\", @"Web\Wallpaper\", @"Resources\Themes\" };
        private static readonly Regex SystemDll = new(
            @"^System32\\(imageres|shell32)\.dll(,-?\d{1,6})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool LooksLikeSystemRef(string reference) =>
            reference.StartsWith("%SystemRoot%\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True if a "%SystemRoot%\..." reference points inside the allowed Windows folders, or at
        /// imageres.dll / shell32.dll with an optional ",index". Personal paths are never allowed.
        /// </summary>
        public static bool IsAllowedSystemRef(string reference)
        {
            if (!LooksLikeSystemRef(reference)) return false;
            string rest = reference["%SystemRoot%\\".Length..];
            if (rest.Contains("..") || rest.Contains('/') || rest.Contains(':') || rest.Contains('%')) return false;
            if (SystemDll.IsMatch(rest)) return true;
            return SystemRoots.Any(r => rest.StartsWith(r, StringComparison.OrdinalIgnoreCase) && rest.Length > r.Length)
                && Magic.ContainsKey(Path.GetExtension(rest));
        }

        private static bool StartsWith(byte[] b, params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(v => v);
        private static bool Riff(byte[] b, string form) =>
            b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == form[0] && b[9] == form[1] && b[10] == form[2] && b[11] == form[3];

        private static InvalidDataException Bad(string path, string why) => new($"Theme file \"{path}\" refused: {why}.");
    }
}
