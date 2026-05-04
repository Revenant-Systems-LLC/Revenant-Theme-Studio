using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
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

        public static async Task<List<string>> ExtractViaApi(string url)
        {
            var pattern = MatchBoard(url);
            if (pattern == null) return new List<string>();
            return await pattern.FetchImageUrls(url, _httpClient);
        }

        public static bool IsKnownBoard(string url)
        {
            try { return MatchBoard(url) != null; }
            catch { return false; }
        }
    }

    public abstract class BoardPattern
    {
        public abstract bool MatchesHost(string host);
        public abstract Task<List<string>> FetchImageUrls(string pageUrl, HttpClient client);

        protected static List<string> ParseJsonArray(string json, string imageUrlProperty)
        {
            var urls = new List<string>();
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
                    if (item.TryGetProperty(imageUrlProperty, out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            urls.Add(url);
                    }
                }
            }
            catch { }
            return urls;
        }
    }

    public class DanbooruPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("danbooru.donmai.us") || host.Contains("safebooru.donmai.us");

        public override async Task<List<string>> FetchImageUrls(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var apiUrl = $"{uri.Scheme}://{uri.Host}/posts.json?tags={Uri.EscapeDataString(tags)}&limit=20";

            var json = await client.GetStringAsync(apiUrl);
            return ParseJsonArray(json, "file_url");
        }
    }

    public class GelbooruPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("gelbooru.com") || host.Contains("safebooru.org");

        public override async Task<List<string>> FetchImageUrls(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var baseHost = uri.Host.Contains("safebooru") ? "safebooru.org" : "gelbooru.com";
            var apiUrl = $"https://{baseHost}/index.php?page=dapi&s=post&q=index&json=1&tags={Uri.EscapeDataString(tags)}&limit=20";

            var json = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);

            var urls = new List<string>();
            if (doc.RootElement.TryGetProperty("post", out var posts))
            {
                foreach (var post in posts.EnumerateArray())
                {
                    if (post.TryGetProperty("file_url", out var url))
                    {
                        var s = url.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) urls.Add(s);
                    }
                }
            }
            return urls;
        }
    }

    public class KonachanPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("konachan.com") || host.Contains("konachan.net") ||
            host.Contains("yande.re");

        public override async Task<List<string>> FetchImageUrls(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var tags = System.Web.HttpUtility.ParseQueryString(uri.Query)["tags"] ?? "";
            var apiUrl = $"{uri.Scheme}://{uri.Host}/post.json?tags={Uri.EscapeDataString(tags)}&limit=20";

            var json = await client.GetStringAsync(apiUrl);
            return ParseJsonArray(json, "file_url");
        }
    }

    public class WallhavenPattern : BoardPattern
    {
        public override bool MatchesHost(string host) =>
            host.Contains("wallhaven.cc");

        public override async Task<List<string>> FetchImageUrls(string pageUrl, HttpClient client)
        {
            var uri = new Uri(pageUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var q = query["q"] ?? "";
            var apiUrl = $"https://wallhaven.cc/api/v1/search?q={Uri.EscapeDataString(q)}&sorting=toplist&topRange=1M";

            var json = await client.GetStringAsync(apiUrl);
            return ParseJsonArray(json, "path");
        }
    }
}
