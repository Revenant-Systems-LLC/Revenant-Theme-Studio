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

        // Canonical token -> every variant/abbreviation that should be treated as the
        // same word ("docs" == "documents", "pics" == "photos" == "images", ...).
        // Extend this table to teach the matcher a new synonym without touching any
        // scoring logic below.
        private static readonly Dictionary<string, string> SynonymCanon = BuildSynonymMap();

        private static Dictionary<string, string> BuildSynonymMap()
        {
            string[][] groups =
            [
                ["documents", "document", "doc", "docs"],
                ["image", "images", "img", "imgs", "pic", "pics", "picture", "pictures", "photo", "photos"],
                ["video", "videos", "vid", "vids"],
                ["downloads", "download", "dl", "dls"],
            ];

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var canonical = group[0];
                foreach (var variant in group)
                    map[variant] = canonical;
            }
            return map;
        }

        private string[] _iconFolders = Array.Empty<string>();
        private List<string> _cachedIcons = new();
        private bool _dirty = true;

        // Per-icon normalized name + token set, computed once per icon path and
        // reused across every FindBestMatch call. Invalidated whenever the icon
        // list itself changes (see GetAllIcons).
        private readonly Dictionary<string, IconNameInfo> _infoCache = new(StringComparer.OrdinalIgnoreCase);

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
            _infoCache.Clear();
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

            var needleInfo = BuildInfo(name);
            if (needleInfo.Tokens.Length == 0)
            {
                return new MatchDecision { Strength = MatchStrength.None, Reason = "Folder name is not matchable after normalization." };
            }

            var exact = all.FirstOrDefault(p => GetInfo(p).Normalized.Equals(needleInfo.Normalized, StringComparison.Ordinal));
            if (exact != null)
            {
                return new MatchDecision { Strength = MatchStrength.Strong, IconPath = exact, Reason = "Exact normalized match." };
            }

            var scored = all
                .Select(path => (path, result: Score(needleInfo, GetInfo(path))))
                .OrderByDescending(x => x.result.Value)
                .Take(2)
                .ToList();

            if (scored.Count == 0)
            {
                return new MatchDecision { Strength = MatchStrength.None, Reason = "No ranked candidates." };
            }

            var best = scored[0];

            // Higher is better now (0.0-1.0). Tiers, from the scoring functions below:
            //   0.90 full token containment (icon's words all present in the folder name)
            //   0.85 acronym match ("rts" <-> "revenant theme studio")
            //   0.80 reverse containment (folder's words all present in a longer icon name)
            //   0.00-0.75 partial token overlap (Dice coefficient, scaled down a tier)
            //   0.00-0.78 spelling similarity (Jaro-Winkler blended with Levenshtein), last resort
            // Strict only trusts the two strongest, unambiguous tiers (containment/acronym).
            // Balanced additionally trusts reverse-containment and close-spelling/typo matches.
            // Loose additionally trusts modest partial token overlap.
            var limit = strictness switch
            {
                MatchStrictness.Strict => 0.80,
                MatchStrictness.Balanced => 0.62,
                _ => 0.48
            };

            if (best.result.Value < limit)
            {
                return new MatchDecision
                {
                    Strength = MatchStrength.None,
                    Reason = $"Best candidate is too far from '{name}' ({best.result.Label}, score {best.result.Value:0.00}).",
                    IconPath = best.path
                };
            }

            if (scored.Count > 1)
            {
                var second = scored[1];
                // ~0.06 is about one tier-step wide (e.g. reverse-containment 0.80 vs full
                // containment 0.90 differ by 0.10 and should NOT read as ambiguous), so this
                // only fires when two candidates are genuinely tied within the same tier.
                if (best.result.Value - second.result.Value < 0.06)
                {
                    return new MatchDecision
                    {
                        Strength = MatchStrength.Weak,
                        IconPath = best.path,
                        Reason = "Top candidates are too close, match is ambiguous."
                    };
                }
            }

            return new MatchDecision
            {
                Strength = MatchStrength.Strong,
                IconPath = best.path,
                Reason = $"Best match via {best.result.Label} (score {best.result.Value:0.00})."
            };
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

        private IconNameInfo GetInfo(string iconPath)
        {
            if (_infoCache.TryGetValue(iconPath, out var info)) return info;
            info = BuildInfo(Path.GetFileNameWithoutExtension(iconPath));
            _infoCache[iconPath] = info;
            return info;
        }

        // ── Scoring ────────────────────────────────────────────────────────
        // Every sub-score is 0.0-1.0, higher is better. The final score is the
        // strongest signal that fires for a given pair — a hybrid instead of a
        // single distance metric, so a folder name that survives on tokens
        // (containment/acronym/overlap) is never punished for a length
        // mismatch the way a single normalized edit-distance was.

        private readonly record struct ScoreResult(double Value, string Label);

        private static ScoreResult Score(IconNameInfo needle, IconNameInfo candidate)
        {
            ScoreResult[] results =
            [
                new ScoreResult(ContainmentScore(needle.TokenSet, candidate.TokenSet), "token containment"),
                new ScoreResult(DiceScore(needle.TokenSet, candidate.TokenSet), "token overlap"),
                new ScoreResult(AcronymScore(needle, candidate), "acronym match"),
                new ScoreResult(CharScore(needle.Normalized, candidate.Normalized), "spelling similarity"),
            ];

            return results.OrderByDescending(r => r.Value).First();
        }

        // All of the icon's tokens present in the folder name (or vice versa). This is
        // what rescues "My Music Library" -> "music": candidate's {music} is a subset
        // of needle's {my, music, library}.
        private static double ContainmentScore(HashSet<string> needleTokens, HashSet<string> candidateTokens)
        {
            if (candidateTokens.IsSubsetOf(needleTokens)) return 0.90;
            if (needleTokens.IsSubsetOf(candidateTokens)) return 0.80;
            return 0.0;
        }

        // General-purpose partial overlap for names that share some but not all
        // tokens without either containing the other, e.g. "Old Documents Archive"
        // vs "Documents Backup" (shared token: documents).
        private static double DiceScore(HashSet<string> needleTokens, HashSet<string> candidateTokens)
        {
            if (needleTokens.Count == 0 || candidateTokens.Count == 0) return 0.0;
            var intersection = needleTokens.Count(candidateTokens.Contains);
            if (intersection == 0) return 0.0;
            var dice = 2.0 * intersection / (needleTokens.Count + candidateTokens.Count);
            // Scaled below the containment tier so partial overlap never outranks a
            // candidate that fully explains the folder name.
            return dice * 0.75;
        }

        // "rts" <-> "revenant theme studio": one side is a single token, the other is
        // multi-token, and that single token equals the multi-token side's initials.
        private static double AcronymScore(IconNameInfo needle, IconNameInfo candidate)
        {
            if (needle.Tokens.Length == 1 && candidate.Tokens.Length >= 2 && candidate.Acronym.Length >= 2
                && candidate.Acronym.Equals(needle.Tokens[0], StringComparison.Ordinal))
                return 0.85;
            if (candidate.Tokens.Length == 1 && needle.Tokens.Length >= 2 && needle.Acronym.Length >= 2
                && needle.Acronym.Equals(candidate.Tokens[0], StringComparison.Ordinal))
                return 0.85;
            return 0.0;
        }

        // Character-level similarity — the last resort, for names that share no
        // tokens at all (typos, near-identical spellings). Jaro-Winkler rewards a
        // shared prefix, which suits short icon names better than raw edit distance;
        // blended with normalized Levenshtein rather than relying on either alone.
        private static double CharScore(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0.0;
            var jw = JaroWinkler(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            var levSimilarity = maxLen == 0 ? 0.0 : 1.0 - (double)Levenshtein(a, b) / maxLen;
            var blended = (jw * 0.7) + (levSimilarity * 0.3);
            // Scaled below every token-aware tier so spelling closeness alone can't
            // outrank real token evidence, while still being able to clear Balanced/
            // Loose for a genuine typo that shares no tokens after canonicalization.
            return blended * 0.78;
        }

        // ── Normalization / tokenization ──────────────────────────────────

        private sealed class IconNameInfo
        {
            public required string Normalized { get; init; }
            public required string[] Tokens { get; init; }
            public required HashSet<string> TokenSet { get; init; }
            public required string Acronym { get; init; }
        }

        private static IconNameInfo BuildInfo(string raw)
        {
            var normalized = Normalize(raw);
            var rawTokens = normalized.Length == 0
                ? Array.Empty<string>()
                : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tokens = rawTokens.Select(Canonicalize).ToArray();
            var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
            var acronym = string.Concat(tokens.Where(t => t.Length > 0).Select(t => t[0]));
            return new IconNameInfo { Normalized = normalized, Tokens = tokens, TokenSet = tokenSet, Acronym = acronym };
        }

        // Synonym lookup first (handles irregular abbreviations the stemmer can't,
        // e.g. "docs" -> "documents"), then a cheap plural fold as a fallback so
        // ordinary plurals ("folders"/"folder", "screenshots"/"screenshot") converge
        // without needing an entry in the synonym table for every one of them.
        private static string Canonicalize(string token)
        {
            if (SynonymCanon.TryGetValue(token, out var canon)) return canon;
            var stemmed = Stem(token);
            return SynonymCanon.TryGetValue(stemmed, out var canonFromStem) ? canonFromStem : stemmed;
        }

        private static string Stem(string token)
        {
            if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
                return token[..^1];
            return token;
        }

        private static string Normalize(string s)
        {
            s = s.Trim().ToLowerInvariant();
            var chars = s.Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
            return string.Join(" ", new string(chars)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !int.TryParse(part, out _)));
        }

        // ── Character-distance primitives ─────────────────────────────────

        private static double JaroWinkler(string a, string b)
        {
            var jaro = Jaro(a, b);
            var maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
            var prefix = 0;
            for (var i = 0; i < maxPrefix; i++)
            {
                if (a[i] != b[i]) break;
                prefix++;
            }
            return jaro + prefix * 0.1 * (1 - jaro);
        }

        private static double Jaro(string a, string b)
        {
            if (a == b) return 1.0;
            var len1 = a.Length;
            var len2 = b.Length;
            if (len1 == 0 || len2 == 0) return 0.0;

            var matchDistance = Math.Max(0, Math.Max(len1, len2) / 2 - 1);
            var aMatches = new bool[len1];
            var bMatches = new bool[len2];
            var matches = 0;

            for (var i = 0; i < len1; i++)
            {
                var start = Math.Max(0, i - matchDistance);
                var end = Math.Min(i + matchDistance + 1, len2);
                for (var j = start; j < end; j++)
                {
                    if (bMatches[j] || a[i] != b[j]) continue;
                    aMatches[i] = true;
                    bMatches[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0) return 0.0;

            double transpositions = 0;
            var k = 0;
            for (var i = 0; i < len1; i++)
            {
                if (!aMatches[i]) continue;
                while (!bMatches[k]) k++;
                if (a[i] != b[k]) transpositions++;
                k++;
            }
            transpositions /= 2;

            return ((double)matches / len1 + (double)matches / len2 + (matches - transpositions) / matches) / 3.0;
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
