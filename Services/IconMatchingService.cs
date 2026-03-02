using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Revenant_Theme_Studio.Services
{
    public class IconMatchingService
    {
        private string[] _iconFolders = Array.Empty<string>();
        private List<string> _cachedIcons = new();
        private bool _dirty = true;

        public IconMatchingService(params string[] iconFolders)
        {
            SetIconFolders(iconFolders);
        }

        public void SetIconFolders(params string[] iconFolders)
        {
            _iconFolders = iconFolders
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _dirty = true;
        }

        public List<string> GetAllIcons()
        {
            if (!_dirty) return _cachedIcons;

            var results = new List<string>();

            foreach (var folder in _iconFolders)
            {
                if (!Directory.Exists(folder)) continue;

                results.AddRange(
                    Directory.GetFiles(folder, "*.ico", SearchOption.AllDirectories)
                );
            }

            _cachedIcons = results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _dirty = false;
            return _cachedIcons;
        }

        public string? FindBestMatch(string name, int maxDistance)
        {
            var all = GetAllIcons();
            if (all.Count == 0) return null;

            var needle = Normalize(name);

            // 1) exact
            var exact = all.FirstOrDefault(p => Normalize(Path.GetFileNameWithoutExtension(p)) == needle);
            if (exact != null) return exact;

            // 2) contains
            var contains = all.FirstOrDefault(p =>
            {
                var iconName = Normalize(Path.GetFileNameWithoutExtension(p));
                return iconName.Contains(needle) || needle.Contains(iconName);
            });
            if (contains != null) return contains;

            // 3) levenshtein best <= threshold
            (string path, int dist)? best = null;
            foreach (var p in all)
            {
                var iconName = Normalize(Path.GetFileNameWithoutExtension(p));
                var d = Levenshtein(needle, iconName);
                if (d <= maxDistance && (best == null || d < best.Value.dist))
                    best = (p, d);
            }

            return best?.path;
        }

        private static string Normalize(string s)
        {
            s = s.Trim().ToLowerInvariant();
            var chars = s.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var dp = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost
                    );
                }
            }

            return dp[a.Length, b.Length];
        }
    }
}