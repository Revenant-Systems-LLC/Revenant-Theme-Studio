using System;
using System.IO;
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
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RevenantThemeStudio/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        public async Task<string> DownloadImageAsync(string url, CancellationToken ct = default)
        {
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".png";

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"rts_wallpaper_{Guid.NewGuid():N}{ext}");

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var file = File.Create(tempPath);
            await stream.CopyToAsync(file, ct);

            return tempPath;
        }
    }
}
