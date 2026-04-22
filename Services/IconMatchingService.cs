using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Revenant_Theme_Studio.Models;

namespace Revenant_Theme_Studio.Services
{
    public class IconMatchingService
    {
        private static readonly string[] ReservedTokens = ["default", "thumb", "open", "front", "backdefault"];

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
                results.AddRange(Directory.GetFiles(folder, "*.ico", SearchOption.AllDirectories));
            }

            _cachedIcons = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _dirty = false;
            return _cachedIcons;
        }

        public MatchDecision FindBestMatch(string name, MatchStrictness strictness)
        {
            var all = GetCandidateIcons();
            if (all.Count == 0)
            {
                return new MatchDecision { Strength = MatchStrength.None, Reason = "No icon candidates available." };
            }

            var needle = Normalize(name);
            if (string.IsNullOrWhiteSpace(needle))
            {
                return new MatchDecision { Strength = MatchStrength.None, Reason = "Folder name is not matchable after normalization." };
            }

            var exact = all.FirstOrDefault(p => Normalize(Path.GetFileNameWithoutExtension(p)).Equals(needle, StringComparison.Ordinal));
            if (exact != null)
            {
                return new MatchDecision { Strength = MatchStrength.Strong, IconPath = exact, Reason = "Exact normalized match." };
            }

            var scored = all
                .Select(path =>
                {
                    var iconName = Normalize(Path.GetFileNameWithoutExtension(path));
                    var distance = Levenshtein(needle, iconName);
                    var maxLen = Math.Max(needle.Length, iconName.Length);
                    var normalizedDistance = maxLen == 0 ? 1.0 : (double)distance / maxLen;
                    return new { path, distance, normalizedDistance };
                })
                .OrderBy(x => x.normalizedDistance)
                .ThenBy(x => x.distance)
                .Take(2)
                .ToList();

            if (scored.Count == 0)
            {
                return new MatchDecision { Strength = MatchStrength.None, Reason = "No ranked candidates." };
            }

            var best = scored[0];
            var limit = strictness switch
            {
                MatchStrictness.Strict => 0.15,
                MatchStrictness.Balanced => 0.22,
                _ => 0.30
            };

            var lengthAwareAbsoluteLimit = Math.Max(1, needle.Length / 4);
            var withinSafeRange = best.normalizedDistance <= limit && best.distance <= lengthAwareAbsoluteLimit;
            if (!withinSafeRange)
            {
                return new MatchDecision
                {
                    Strength = MatchStrength.None,
                    Reason = $"Best candidate is too far from '{name}'.",
                    IconPath = best.path
                };
            }

            if (scored.Count > 1)
            {
                var second = scored[1];
                if (Math.Abs(second.normalizedDistance - best.normalizedDistance) < 0.03)
                {
                    return new MatchDecision
                    {
                        Strength = MatchStrength.Weak,
                        IconPath = best.path,
                        Reason = "Top candidates are too close, match is ambiguous."
                    };
                }
            }

            return new MatchDecision { Strength = MatchStrength.Strong, IconPath = best.path, Reason = "Best safe near-match." };
        }

        private List<string> GetCandidateIcons()
        {
            return GetAllIcons()
                .Where(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    return !ReservedTokens.Any(token => fileName.Contains(token, StringComparison.Ordinal));
                })
                .ToList();
        }

        private static string Normalize(string s)
        {
            s = s.Trim().ToLowerInvariant();
            var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
            return string.Join(" ", new string(chars)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !int.TryParse(part, out _)));
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) dp[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }
    }
}
