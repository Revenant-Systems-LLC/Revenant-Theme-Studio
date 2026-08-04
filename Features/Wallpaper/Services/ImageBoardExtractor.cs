using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    /// <summary>
    /// One remote image candidate. FullUrl is the best-quality link; ThumbUrl feeds the
    /// results grid without pulling full-size bytes; FallbackUrl is tried when FullUrl 404s
    /// (Pinterest "originals" links are synthesized and occasionally have a different extension).
    /// </summary>
    public class RemoteImage
    {
        public string FullUrl { get; set; } = string.Empty;
        public string? ThumbUrl { get; set; }
        public string? FallbackUrl { get; set; }
    }

    public class ImageBoardExtractor
    {
        private static readonly HttpClient _httpClient;
        private static readonly List<BoardPattern> _patterns;

        static ImageBoardExtractor()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RevenantThemeStudio/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            _patterns = new List<BoardPattern>
            {
                new PinterestPattern(),
                new DanbooruPattern(),
                new GelbooruPattern(),
                new KonachanPattern(),
                new WallhavenPattern()
            };
        }

        public static BoardPattern? MatchBoard(string url)
        {
            var uri = new Uri(url);
            var host = uri.Host.ToLowerInvariant();
            return _patterns.FirstOrDefault(p => p.MatchesHost(host));
        }

        public static async Task<List<RemoteImage>> ExtractViaApi(string url)
        {
            var pattern = MatchBoard(url);
            if (pattern == null) return new List<RemoteImage>();
            return await pattern.FetchImages(url, _httpClient);
        }

        public static bool IsKnownBoard(string url)
        {
            try { return MatchBoard(url) != null; }
            catch { return false; }
        }

        /// <summary>
        /// Fetch a page presenting as a real browser. Pinterest (and a fair number of
        /// galleries) return 403 or an empty shell to non-browser user agents.
        /// </summary>
        internal static async Task<string> GetStringAsBrowser(HttpClient client, string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }
    }

    public abstract class BoardPattern
    {
        public abstract bool MatchesHost(string host);
        public abstract Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client);

        protected static string? GetString(JsonElement item, string prop) =>
            item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        protected static List<RemoteImage> ParseJsonArray(string json, string fullProp,
            string? thumbProp = null, string? fallbackProp = null)
        {
            var images = new List<RemoteImage>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var array = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("posts", out var posts) ? posts.EnumerateArray()
                    : root.TryGetProperty("data", out var data) ? data.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

                foreach (var item in array)
                {
                    var full = GetString(item, fullProp);
                    if (string.IsNullOrWhiteSpace(full)) continue;

                    images.Add(new RemoteImage
                    {
                        FullUrl = full!,
                        ThumbUrl = thumbProp == null ? null : GetString(item, thumbProp),
                        FallbackUrl = fallbackProp == null ? null : GetString(item, fallbackProp)
                    });
                }
            }
            catch { }
            return images;
        }
    }

    /// <summary>
    /// Pinterest. Board URLs (pinterest.com/{user}/{board}/) go through the public pidgets
    /// widget endpoint (no auth, JSON). Anything else — search pages, pin pages — falls back
    /// to mining i.pinimg.com URLs out of the server-rendered HTML, then the board RSS feed.
    /// Sized variants (236x/736x/...) are upgraded to /originals/ with the largest sized
    /// variant kept as the download fallback.
    /// </summary>
    public class PinterestPattern : BoardPattern
    {
        private const int OriginalsRank = int.MaxValue;
        private const int MaxResults = 60;

        private static readonly Regex PinImgUrl = new(
            @"https://i\.pinimg\.com/(?<size>originals|\d+x(?:\d+)?(?:_[A-Z]+)?)/(?<path>[A-Za-z0-9/_\-]+\.(?:jpg|jpeg|png|webp))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] ReservedSegments =
            { "search", "pin", "ideas", "today", "settings", "business", "resource", "about", "policies" };

        public override bool MatchesHost(string host) => host.Contains("pinterest.");

        public override async Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var segments = uri.AbsolutePath.Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            bool looksLikeBoard = segments.Length >= 2 &&
                !ReservedSegments.Contains(segments[0].ToLowerInvariant());

            if (looksLikeBoard)
            {
                var viaWidget = await TryPidgets(segments[0], segments[1], client);
                if (viaWidget.Count > 0) return viaWidget;
            }

            try
            {
                var html = await ImageBoardExtractor.GetStringAsBrowser(client, pageUrl);
                var mined = MinePinImageUrls(html);
                if (mined.Count > 0) return mined;
            }
            catch { }

            if (looksLikeBoard)
            {
                try
                {
                    var rss = await ImageBoardExtractor.GetStringAsBrowser(client,
                        $"https://www.pinterest.com/{segments[0]}/{segments[1]}.rss");
                    return MinePinImageUrls(rss);
                }
                catch { }
            }

            return new List<RemoteImage>();
        }

        private static async Task<List<RemoteImage>> TryPidgets(string user, string board, HttpClient client)
        {
            var images = new List<RemoteImage>();
            try
            {
                var url = $"https://widgets.pinterest.com/v3/pidgets/boards/" +
                          $"{Uri.EscapeDataString(user)}/{Uri.EscapeDataString(board)}/pins/";
                var json = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("pins", out var pins) ||
                    pins.ValueKind != JsonValueKind.Array)
                    return images;

                foreach (var pin in pins.EnumerateArray())
                {
                    if (!pin.TryGetProperty("images", out var imgs) ||
                        imgs.ValueKind != JsonValueKind.Object) continue;

                    string? sized = null;
                    foreach (var kv in imgs.EnumerateObject())
                    {
                        if (kv.Value.TryGetProperty("url", out var u))
                        {
                            sized = u.GetString();
                            if (!string.IsNullOrWhiteSpace(sized)) break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(sized)) continue;

                    images.Add(new RemoteImage
                    {
                        FullUrl = SwapSizeSegment(sized!, "originals"),
                        FallbackUrl = SwapSizeSegment(sized!, "736x"),
                        ThumbUrl = sized
                    });
                }
            }
            catch { }
            return images;
        }

        /// <summary>
        /// Mine i.pinimg.com URLs out of raw HTML/JSON/RSS text. Variants of the same image
        /// share the path after the size segment; group by it, prefer originals, keep the
        /// largest sized variant as fallback and the smallest as the grid thumbnail.
        /// Groups whose largest variant is under 236px are avatars/UI chrome — dropped.
        /// </summary>
        internal static List<RemoteImage> MinePinImageUrls(string text)
        {
            text = text.Replace("\\/", "/");

            var variants = new Dictionary<string, List<(int Rank, string Url)>>();
            foreach (Match m in PinImgUrl.Matches(text))
            {
                var path = m.Groups["path"].Value.ToLowerInvariant();
                var size = m.Groups["size"].Value.ToLowerInvariant();

                int rank = size.StartsWith("originals")
                    ? OriginalsRank
                    : int.TryParse(new string(size.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;

                if (!variants.TryGetValue(path, out var list))
                    variants[path] = list = new List<(int, string)>();
                if (!list.Any(v => v.Rank == rank))
                    list.Add((rank, m.Value));
            }

            var images = new List<RemoteImage>();
            foreach (var group in variants.Values)
            {
                var ordered = group.OrderBy(v => v.Rank).ToList();
                var largest = ordered[^1];
                if (largest.Rank < 236) continue;

                var smallest = ordered[0];
                string full = largest.Rank == OriginalsRank
                    ? largest.Url
                    : SwapSizeSegment(largest.Url, "originals");
                string? fallback = largest.Rank == OriginalsRank
                    ? (ordered.Count > 1 ? ordered[^2].Url : null)
                    : largest.Url;

                images.Add(new RemoteImage
                {
                    FullUrl = full,
                    FallbackUrl = fallback,
                    ThumbUrl = smallest.Rank == OriginalsRank
                        ? SwapSizeSegment(smallest.Url, "236x")
                        : smallest.Url
                });

                if (images.Count >= MaxResults) break;
            }
            return images;
        }

        private static string SwapSizeSegment(string url, string size) =>
            Regex.Replace(url, @"i\.pinimg\.com/(originals|\d+x(?:\d+)?(?:_[A-Z]+)?)/",
                $"i.pinimg.com/{size}/", RegexOptions.IgnoreCase);
    }

    public class DanbooruPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("danbooru.donmai.us") || host.Contains("safebooru.donmai.us");

        public override async Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var apiUrl = $"{uri.Scheme}://{uri.Host}/posts.json?tags={Uri.EscapeDataString(tags)}&limit=40";

            var json = await client.GetStringAsync(apiUrl);
            return ParseJsonArray(json, "file_url", "preview_file_url", "large_file_url");
        }
    }

    public class GelbooruPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("gelbooru.com") || host.Contains("safebooru.org");

        public override async Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var baseHost = uri.Host.Contains("safebooru") ? "safebooru.org" : "gelbooru.com";
            var apiUrl = $"https://{baseHost}/index.php?page=dapi&s=post&q=index&json=1" +
                         $"&tags={Uri.EscapeDataString(tags)}&limit=40";

            var json = await client.GetStringAsync(apiUrl);
            var images = new List<RemoteImage>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("post", out var posts) &&
                posts.ValueKind == JsonValueKind.Array)
            {
                foreach (var post in posts.EnumerateArray())
                {
                    var full = GetString(post, "file_url");
                    if (string.IsNullOrWhiteSpace(full)) continue;

                    images.Add(new RemoteImage
                    {
                        FullUrl = full!,
                        ThumbUrl = GetString(post, "preview_url"),
                        FallbackUrl = GetString(post, "sample_url")
                    });
                }
            }
            return images;
        }
    }

    public class KonachanPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("konachan.com") || host.Contains("konachan.net") ||
            host.Contains("yande.re");

        public override async Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var apiUrl = $"{uri.Scheme}://{uri.Host}/post.json?tags={Uri.EscapeDataString(tags)}&limit=40";

            var json = await client.GetStringAsync(apiUrl);
            return ParseJsonArray(json, "file_url", "preview_url", "sample_url");
        }
    }

    public class WallhavenPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("wallhaven.cc");

        public override async Task<List<RemoteImage>> FetchImages(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var q = query["q"] ?? "";
            var apiUrl = $"https://wallhaven.cc/api/v1/search?q={Uri.EscapeDataString(q)}" +
                         "&sorting=toplist&topRange=1M";

            var json = await client.GetStringAsync(apiUrl);
            var images = new List<RemoteImage>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var full = GetString(item, "path");
                    if (string.IsNullOrWhiteSpace(full)) continue;

                    string? thumb = null;
                    if (item.TryGetProperty("thumbs", out var thumbs))
                        thumb = GetString(thumbs, "small") ?? GetString(thumbs, "original");

                    images.Add(new RemoteImage { FullUrl = full!, ThumbUrl = thumb });
                }
            }
            return images;
        }
    }
}
