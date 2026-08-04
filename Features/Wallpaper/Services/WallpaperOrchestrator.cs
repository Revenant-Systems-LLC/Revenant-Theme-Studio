using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class WallpaperOrchestrator : IDisposable
    {
        private readonly DisplayDetectionService _displayService;
        private readonly ImageDownloadService _downloadService;
        private readonly UpscalingService _upscalingService;
        private readonly WallpaperEngineService _wallpaperEngine;
        private readonly WallpaperConfigService _configService;

        public WallpaperOrchestrator()
        {
            _displayService = new DisplayDetectionService();
            _downloadService = new ImageDownloadService();
            _upscalingService = new UpscalingService();
            _wallpaperEngine = new WallpaperEngineService();
            _configService = new WallpaperConfigService();
        }

        public async Task<WallpaperResult> FetchFromSourceAsync(SavedSource source,
            CancellationToken ct = default, IProgress<WallpaperProgress>? progress = null)
        {
            try
            {
                progress?.Report(new WallpaperProgress("Fetching page...", 0));

                var webSource = new WebImageSource
                {
                    SourcePath = source.Url,
                    Rule = new ExtractionRule
                    {
                        Mode = source.SelectionMode,
                        Position = source.SelectionPosition
                    }
                };

                var selected = await webSource.GetSelectedImage();
                if (selected == null)
                    return WallpaperResult.Fail("No images found on the page.");

                progress?.Report(new WallpaperProgress("Downloading image...", 15));
                var localPath = await _downloadService.DownloadImageAsync(selected, ct);

                progress?.Report(new WallpaperProgress("Image downloaded.", 30));

                return WallpaperResult.Ok(localPath, selected.Name);
            }
            catch (OperationCanceledException)
            {
                return WallpaperResult.Fail("Operation cancelled.");
            }
            catch (Exception ex)
            {
                return WallpaperResult.Fail($"Fetch failed: {ex.Message}");
            }
        }

        public async Task<WallpaperResult> UpscaleIfNeededAsync(string imagePath,
            CancellationToken ct = default, IProgress<WallpaperProgress>? progress = null)
        {
            try
            {
                var monitors = _displayService.GetMonitorConfiguration();
                if (monitors.Count == 0)
                {
                    var primary = _displayService.GetPrimaryMonitor();
                    monitors.Add(primary);
                }

                int targetW = monitors.Max(m => m.PositionX + m.Width);
                int targetH = monitors.Max(m => m.PositionY + m.Height);

                var upscaleProgress = new Progress<int>(pct =>
                    progress?.Report(new WallpaperProgress($"Upscaling... {pct}%", 30 + (int)(pct * 0.6))));

                progress?.Report(new WallpaperProgress("Starting upscale...", 30));
                var upscaledPath = await _upscalingService.UpscaleImageAsync(
                    imagePath, targetW, targetH, ct, upscaleProgress);

                progress?.Report(new WallpaperProgress("Upscale complete.", 90));
                return WallpaperResult.Ok(upscaledPath);
            }
            catch (FileNotFoundException)
            {
                return WallpaperResult.Ok(imagePath);
            }
            catch (OperationCanceledException)
            {
                return WallpaperResult.Fail("Upscale cancelled.");
            }
            catch (Exception ex)
            {
                return WallpaperResult.Fail($"Upscale failed: {ex.Message}");
            }
        }

        public void ApplyWallpaper(string imagePath)
        {
            var image = new WallpaperImage { Path = imagePath, Name = Path.GetFileName(imagePath) };
            _wallpaperEngine.ApplyToAllMonitors(image);
        }

        public void ApplyWallpaperToMonitor(string imagePath, MonitorProfile monitor)
        {
            var image = new WallpaperImage { Path = imagePath, Name = Path.GetFileName(imagePath) };
            _wallpaperEngine.ApplyWallpaper(image, monitor);
        }

        public async Task<WallpaperResult> RunFullPipelineAsync(SavedSource source,
            bool upscale = true, CancellationToken ct = default,
            IProgress<WallpaperProgress>? progress = null)
        {
            var fetchResult = await FetchFromSourceAsync(source, ct, progress);
            if (!fetchResult.Success) return fetchResult;

            string finalPath = fetchResult.ImagePath!;

            if (upscale)
            {
                var upscaleResult = await UpscaleIfNeededAsync(finalPath, ct, progress);
                if (!upscaleResult.Success) return upscaleResult;
                finalPath = upscaleResult.ImagePath!;
            }

            progress?.Report(new WallpaperProgress("Ready to apply.", 95));
            return WallpaperResult.Ok(finalPath);
        }

        public WallpaperConfig GetConfig() => _configService.Load();
        public void SaveConfig(WallpaperConfig config) => _configService.Save(config);

        // Shared instances so the One Surface composition path reuses the same
        // ONNX session and COM engine instead of loading its own.
        public UpscalingService Upscaler => _upscalingService;
        public WallpaperEngineService Engine => _wallpaperEngine;
        public DisplayDetectionService Displays => _displayService;
        public ImageDownloadService Downloader => _downloadService;

        public void Dispose()
        {
            _upscalingService.Dispose();
        }
    }

    public class WallpaperResult
    {
        public bool Success { get; init; }
        public string? ImagePath { get; init; }
        public string? ImageName { get; init; }
        public string? Error { get; init; }

        public static WallpaperResult Ok(string path, string? name = null) =>
            new() { Success = true, ImagePath = path, ImageName = name };
        public static WallpaperResult Fail(string error) =>
            new() { Success = false, Error = error };
    }

    public class WallpaperProgress
    {
        public string Status { get; }
        public int Percent { get; }

        public WallpaperProgress(string status, int percent)
        {
            Status = status;
            Percent = percent;
        }
    }
}
