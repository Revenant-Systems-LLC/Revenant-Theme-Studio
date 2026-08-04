using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class ImageDownloadService
    {
        private static readonly HttpClient _httpClient;

        static ImageDownloadService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        /// <summary>
        /// Download the image, walking the candidate chain: full-res, fallback, thumbnail.
        /// Pinterest /originals/ links are synthesized and occasionally 404 — the sized
        /// variant then still delivers a usable wallpaper.
        /// </summary>
        public async Task<string> DownloadImageAsync(WallpaperImage image, CancellationToken ct = default)
        {
            var candidates = new[] { image.Path, image.FallbackPath, image.ThumbUrl }
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct()
                .ToList();

            Exception? lastError = null;
            foreach (var url in candidates)
            {
                // Local files (folder sources) need no download.
                if (File.Exists(url)) return url;

                try
                {
                    return await DownloadImageAsync(url, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }

            throw lastError ?? new InvalidOperationException("No downloadable URL on this image.");
        }

        public async Task<string> DownloadImageAsync(string url, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Some CDNs 403 unknown user agents; present as a browser.
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

            var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext))
                ext = ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"rts_wallpaper_{Guid.NewGuid():N}{ext}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var file = File.Create(tempPath);
            await stream.CopyToAsync(file, ct);

            return tempPath;
        }

        private static string ExtensionFromContentType(string? mediaType) => mediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            _ => ".png"
        };
    }
}
