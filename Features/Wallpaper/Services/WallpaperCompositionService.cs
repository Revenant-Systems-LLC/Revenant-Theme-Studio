using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    /// <summary>
    /// A monitor's chosen crop region, in source-image pixel coordinates.
    /// Aspect ratio always matches the monitor's, so Fill never distorts.
    /// </summary>
    public class MonitorRegion
    {
        public MonitorProfile Monitor { get; set; } = new();
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class CompositionOutcome
    {
        public int AppliedCount { get; set; }
        public bool UpscaleUsed { get; set; }
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// The One Surface pipeline: crop each monitor's region out of the source image,
    /// AI-upscale any crop smaller than its monitor's native pixels, and apply each
    /// result to its monitor. Output files live in LocalAppData (stable paths — the
    /// temp cleanup never touches them), one per monitor, overwritten on each apply.
    /// </summary>
    public class WallpaperCompositionService
    {
        private readonly UpscalingService _upscaler;
        private readonly WallpaperEngineService _engine;
        private readonly string _outputDir;

        public WallpaperCompositionService(UpscalingService upscaler, WallpaperEngineService engine)
        {
            _upscaler = upscaler;
            _engine = engine;
            _outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevenantThemeStudio", "Wallpapers");
            Directory.CreateDirectory(_outputDir);
        }

        /// <summary>
        /// Default layout: replicate the physical monitor arrangement, scaled by
        /// <paramref name="coverage"/> (0..1, 1 = as large as fits) and centered on the image.
        /// </summary>
        /// <summary>Image pixels per desktop pixel at which the whole monitor
        /// arrangement exactly fits inside the image.</summary>
        public static double ComputeFitScale(int imageWidth, int imageHeight,
            IReadOnlyList<MonitorProfile> monitors)
        {
            if (monitors.Count == 0 || imageWidth < 1 || imageHeight < 1) return 1.0;

            int minX = monitors.Min(m => m.PositionX);
            int minY = monitors.Min(m => m.PositionY);
            int maxX = monitors.Max(m => m.PositionX + m.Width);
            int maxY = monitors.Max(m => m.PositionY + m.Height);
            double vW = Math.Max(1, maxX - minX);
            double vH = Math.Max(1, maxY - minY);

            return Math.Min(imageWidth / vW, imageHeight / vH);
        }

        public static List<MonitorRegion> BuildDefaultLayout(int imageWidth, int imageHeight,
            IReadOnlyList<MonitorProfile> monitors, double coverage = 1.0)
        {
            var regions = new List<MonitorRegion>();
            if (monitors.Count == 0 || imageWidth < 1 || imageHeight < 1) return regions;

            int minX = monitors.Min(m => m.PositionX);
            int minY = monitors.Min(m => m.PositionY);
            int maxX = monitors.Max(m => m.PositionX + m.Width);
            int maxY = monitors.Max(m => m.PositionY + m.Height);
            double vW = Math.Max(1, maxX - minX);
            double vH = Math.Max(1, maxY - minY);

            double fitScale = ComputeFitScale(imageWidth, imageHeight, monitors);
            double s = fitScale * Math.Clamp(coverage, 0.05, 1.0);

            double ox = (imageWidth - vW * s) / 2.0;
            double oy = (imageHeight - vH * s) / 2.0;

            foreach (var m in monitors)
            {
                regions.Add(new MonitorRegion
                {
                    Monitor = m,
                    X = ox + (m.PositionX - minX) * s,
                    Y = oy + (m.PositionY - minY) * s,
                    Width = m.Width * s,
                    Height = m.Height * s
                });
            }
            return regions;
        }

        public async Task<CompositionOutcome> ComposeAndApplyAsync(string imagePath,
            IReadOnlyList<MonitorRegion> regions, bool upscaleEnabled,
            CancellationToken ct = default, IProgress<WallpaperProgress>? progress = null)
        {
            var outcome = new CompositionOutcome();

            var source = await Task.Run(() => LoadFrozenBitmap(imagePath), ct);
            int imgW = source.PixelWidth;
            int imgH = source.PixelHeight;

            int done = 0;
            foreach (var region in regions)
            {
                ct.ThrowIfCancellationRequested();

                var mon = region.Monitor;
                progress?.Report(new WallpaperProgress(
                    $"Monitor {mon.MonitorId + 1}: cropping...",
                    (int)(done * 100.0 / regions.Count)));

                var rect = ClampRegion(region, imgW, imgH);
                var finalPath = Path.Combine(_outputDir, $"monitor{mon.MonitorId}.png");

                await Task.Run(async () =>
                {
                    var crop = new CroppedBitmap(source, rect);
                    crop.Freeze();

                    bool needsUpscale = upscaleEnabled &&
                        (rect.Width < mon.Width || rect.Height < mon.Height);

                    if (needsUpscale)
                    {
                        // The upscaler works on files; stage the crop in temp
                        // (rts_wallpaper_ prefix keeps it under daily cleanup).
                        var cropPath = Path.Combine(Path.GetTempPath(),
                            $"rts_wallpaper_crop_{Guid.NewGuid():N}.png");
                        SavePng(crop, cropPath);

                        try
                        {
                            var monProgress = new Progress<int>(pct => progress?.Report(
                                new WallpaperProgress(
                                    $"Monitor {mon.MonitorId + 1}: AI upscaling {pct}%",
                                    (int)((done + pct / 100.0) * 100.0 / regions.Count))));

                            var upscaled = await _upscaler.UpscaleImageAsync(
                                cropPath, mon.Width, mon.Height, ct, monProgress);
                            File.Copy(upscaled, finalPath, overwrite: true);
                            outcome.UpscaleUsed = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (FileNotFoundException)
                        {
                            outcome.Warnings.Add(
                                "AI model missing (Assets\\Models\\realesrgan-x4plus.onnx) — applied without upscale.");
                            File.Copy(cropPath, finalPath, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            outcome.Warnings.Add($"Upscale failed on monitor {mon.MonitorId + 1}: {ex.Message}");
                            File.Copy(cropPath, finalPath, overwrite: true);
                        }
                    }
                    else
                    {
                        SavePng(crop, finalPath);
                    }

                    _engine.ApplyWallpaper(
                        new WallpaperImage { Path = finalPath, Name = Path.GetFileName(finalPath) },
                        mon);
                }, ct);

                done++;
                outcome.AppliedCount++;
            }

            progress?.Report(new WallpaperProgress("Applied.", 100));
            return outcome;
        }

        private static Int32Rect ClampRegion(MonitorRegion region, int imgW, int imgH)
        {
            int x = (int)Math.Round(region.X);
            int y = (int)Math.Round(region.Y);
            int w = (int)Math.Round(region.Width);
            int h = (int)Math.Round(region.Height);

            w = Math.Clamp(w, 16, imgW);
            h = Math.Clamp(h, 16, imgH);
            x = Math.Clamp(x, 0, imgW - w);
            y = Math.Clamp(y, 0, imgH - h);

            return new Int32Rect(x, y, w, h);
        }

        private static BitmapSource LoadFrozenBitmap(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            if (image.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                return converted;
            }
            return image;
        }

        private static void SavePng(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
