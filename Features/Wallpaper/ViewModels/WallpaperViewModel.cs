using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Revenant_Theme_Studio.Features.Wallpaper;
using Revenant_Theme_Studio.Features.Wallpaper.Services;

namespace Revenant_Theme_Studio.Features.Wallpaper.ViewModels
{
    /// <summary>
    /// One draggable monitor rectangle on the layout canvas, in source-image pixel
    /// coordinates. The view multiplies by its display scale to render.
    /// </summary>
    public class MonitorRegionViewModel : INotifyPropertyChanged
    {
        private double _x, _y, _width, _height;

        public MonitorProfile Monitor { get; }
        public string Label { get; }

        public MonitorRegionViewModel(MonitorProfile monitor)
        {
            Monitor = monitor;
            Label = $"{monitor.MonitorId + 1}";
        }

        public string Tooltip => $"Monitor {Monitor.MonitorId + 1} — {Monitor.Width}×{Monitor.Height}";

        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }
        public double Width { get => _width; set { _width = value; OnPropertyChanged(); } }
        public double Height { get => _height; set { _height = value; OnPropertyChanged(); } }

        public MonitorRegion ToRegion() => new()
        {
            Monitor = Monitor,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One monitor as an independent wallpaper slot. Each slot owns its own image,
    /// which is what lets different monitors show different wallpapers.
    /// </summary>
    public class MonitorSlotViewModel : INotifyPropertyChanged
    {
        private string? _imagePath;
        private BitmapImage? _thumbnail;

        public MonitorProfile Monitor { get; }
        public string Label { get; }
        public string Header { get; }

        public MonitorSlotViewModel(MonitorProfile monitor)
        {
            Monitor = monitor;
            Label = $"Monitor {monitor.MonitorId + 1}";
            Header = $"{monitor.Width}×{monitor.Height}";
        }

        public string? ImagePath
        {
            get => _imagePath;
            set
            {
                _imagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(SlotStatus));
            }
        }

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        public bool HasImage => !string.IsNullOrEmpty(_imagePath);

        public string SlotStatus =>
            HasImage ? (Path.GetFileName(_imagePath) ?? "assigned") : "click to assign";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class WallpaperViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly WallpaperOrchestrator _orchestrator;
        private readonly WallpaperCompositionService _composition;
        private readonly WallpaperScheduler _scheduler;
        private readonly List<MonitorProfile> _monitors;

        private WallpaperConfig _config;
        private CancellationTokenSource? _cts;

        private string _statusMessage = "Add a source, fetch, pick an image.";
        private string _newSourceName = string.Empty;
        private string _newSourceUrl = string.Empty;
        private SavedSource? _selectedSource;
        private WallpaperImage? _selectedImage;
        private BitmapImage? _previewBitmap;
        private string? _previewImagePath;
        private bool _isBusy;
        private int _progressPercent;
        private double _coverageScale = 1.0;
        private int _rotationMinutes = 5;
        private bool _rotationRunning;
        private bool _upscaleEnabled;

        public ObservableCollection<SavedSource> Sources { get; } = new();
        public ObservableCollection<WallpaperImage> Results { get; } = new();
        public ObservableCollection<MonitorRegionViewModel> Regions { get; } = new();

        /// <summary>Independent mode: one slot per monitor, each with its own image.</summary>
        public ObservableCollection<MonitorSlotViewModel> MonitorSlots { get; } = new();

        public IReadOnlyList<MonitorProfile> Monitors => _monitors;
        public string MonitorSummary { get; }

        public WallpaperViewModel()
        {
            _orchestrator = new WallpaperOrchestrator();
            _composition = new WallpaperCompositionService(_orchestrator.Upscaler, _orchestrator.Engine);
            _scheduler = new WallpaperScheduler(_orchestrator);
            _monitors = _orchestrator.Displays.GetMonitorConfiguration();
            if (_monitors.Count == 0)
                _monitors.Add(_orchestrator.Displays.GetPrimaryMonitor());

            MonitorSummary = $"{_monitors.Count} monitor{(_monitors.Count == 1 ? "" : "s")} detected — " +
                string.Join(", ", _monitors.Select(m => $"{m.Width}×{m.Height}"));

            foreach (var m in _monitors)
                MonitorSlots.Add(new MonitorSlotViewModel(m));

            _config = _orchestrator.GetConfig();
            _upscaleEnabled = _config.UpscaleEnabled;
            if (_config.RefreshIntervalMinutes > 0)
                _rotationMinutes = _config.RefreshIntervalMinutes;
            foreach (var s in _config.Sources)
                Sources.Add(s);

            // Selection isn't persisted, only the list is. Without this, every launch
            // starts with nothing selected and Fetch silently bails on "Select a source first",
            // which reads as "fetch is broken".
            SelectedSource = Sources.FirstOrDefault();

            _scheduler.StatusChanged += msg => StatusMessage = $"Rotation: {msg}";
            _scheduler.ErrorOccurred += msg => StatusMessage = $"Rotation error: {msg}";
        }

        // ── Bindable state ───────────────────────────────────────────────────

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string NewSourceName
        {
            get => _newSourceName;
            set { _newSourceName = value; OnPropertyChanged(); }
        }

        public string NewSourceUrl
        {
            get => _newSourceUrl;
            set { _newSourceUrl = value; OnPropertyChanged(); }
        }

        public SavedSource? SelectedSource
        {
            get => _selectedSource;
            set { _selectedSource = value; OnPropertyChanged(); }
        }

        public WallpaperImage? SelectedImage
        {
            get => _selectedImage;
            set { _selectedImage = value; OnPropertyChanged(); }
        }

        /// <summary>Decoded preview shown on the layout canvas.</summary>
        public BitmapImage? PreviewBitmap
        {
            get => _previewBitmap;
            private set
            {
                _previewBitmap = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ImagePixelWidth));
                OnPropertyChanged(nameof(ImagePixelHeight));
                OnPropertyChanged(nameof(OverlayFontSize));
                OnPropertyChanged(nameof(OverlaySubFontSize));
                OnPropertyChanged(nameof(OverlayStrokeThickness));
            }
        }

        public bool HasPreview => _previewBitmap != null;
        public string? PreviewImagePath => _previewImagePath;
        public int ImagePixelWidth => _previewBitmap?.PixelWidth ?? 0;
        public int ImagePixelHeight => _previewBitmap?.PixelHeight ?? 0;

        // The layout canvas renders in image-pixel space inside a Viewbox, so overlay
        // chrome (labels, borders) must scale with the image or it vanishes on 4K+ sources.
        public double OverlayFontSize => Math.Clamp(ImagePixelWidth / 45.0, 14, 120);
        public double OverlaySubFontSize => OverlayFontSize / 2.2;
        public System.Windows.Thickness OverlayStrokeThickness =>
            new(Math.Max(2, ImagePixelWidth / 400.0));

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); }
        }

        public bool IsIdle => !_isBusy;

        public int ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = value; OnPropertyChanged(); }
        }

        public bool UpscaleEnabled
        {
            get => _upscaleEnabled;
            set
            {
                _upscaleEnabled = value;
                OnPropertyChanged();
                _config.UpscaleEnabled = value;
                _orchestrator.SaveConfig(_config);
            }
        }

        /// <summary>0.05..1.0 — how much of the image the monitor arrangement covers.
        /// Changing it rescales every region about its own center.</summary>
        public double CoverageScale
        {
            get => _coverageScale;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 1.0);
                if (Math.Abs(clamped - _coverageScale) < 0.0001) return;

                var previous = _coverageScale;
                _coverageScale = clamped;
                OnPropertyChanged();
                RescaleRegions(previous, clamped);
                OnPropertyChanged(nameof(QualityHint));
            }
        }

        /// <summary>Tells the user whether the current layout will need AI upscaling.</summary>
        public string QualityHint
        {
            get
            {
                if (!HasPreview || Regions.Count == 0) return string.Empty;
                bool anySmall = Regions.Any(r => r.Width < r.Monitor.Width - 0.5);
                return anySmall
                    ? (UpscaleEnabled ? "Regions below native res — AI upscale will engage."
                                      : "Regions below native res — enable upscaling or expect softness.")
                    : "All regions at or above native res — pixel-perfect.";
            }
        }

        public int RotationMinutes
        {
            get => _rotationMinutes;
            set
            {
                _rotationMinutes = Math.Max(1, value);
                OnPropertyChanged();
                if (_rotationRunning) _scheduler.UpdateInterval(_rotationMinutes);
                _config.RefreshIntervalMinutes = _rotationMinutes;
                _orchestrator.SaveConfig(_config);
            }
        }

        public bool RotationRunning
        {
            get => _rotationRunning;
            private set { _rotationRunning = value; OnPropertyChanged(); }
        }

        // ── Sources ──────────────────────────────────────────────────────────

        public void AddUrlSource()
        {
            var url = NewSourceUrl.Trim();
            if (string.IsNullOrWhiteSpace(url)) { StatusMessage = "Enter a gallery or board URL."; return; }
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { StatusMessage = "That URL doesn't parse."; return; }

            var name = string.IsNullOrWhiteSpace(NewSourceName)
                ? new Uri(url).Host.Replace("www.", "")
                : NewSourceName.Trim();

            var source = new SavedSource
            {
                Name = name,
                Url = url,
                Type = ImageBoardExtractor.IsKnownBoard(url) ? SourceType.ImageBoard : SourceType.WebSource,
                SelectionMode = SelectionMode.Random
            };

            Sources.Add(source);
            SelectedSource = source;
            PersistSources();
            NewSourceName = string.Empty;
            NewSourceUrl = string.Empty;
            StatusMessage = $"Source added: {name}";
        }

        public void AddFolderSource(string folderPath)
        {
            if (!Directory.Exists(folderPath)) { StatusMessage = "Folder not found."; return; }

            var source = new SavedSource
            {
                Name = Path.GetFileName(folderPath.TrimEnd('\\', '/')),
                Url = folderPath,
                Type = SourceType.LocalFolder,
                SelectionMode = SelectionMode.Random
            };

            Sources.Add(source);
            SelectedSource = source;
            PersistSources();
            StatusMessage = $"Folder source added: {source.Name}";
        }

        public void RemoveSource(SavedSource source)
        {
            Sources.Remove(source);
            if (SelectedSource == source) SelectedSource = null;
            PersistSources();
        }

        private void PersistSources()
        {
            _config.Sources = Sources.ToList();
            _orchestrator.SaveConfig(_config);
        }

        // ── Fetch + select ───────────────────────────────────────────────────

        public async Task FetchImagesAsync()
        {
            if (SelectedSource == null) { StatusMessage = "Select a source first."; return; }
            if (IsBusy) return;

            IsBusy = true;
            Results.Clear();
            StatusMessage = $"Fetching from {SelectedSource.Name}...";

            try
            {
                WallpaperSource source = SelectedSource.Type == SourceType.LocalFolder
                    ? new LocalFolderSource { SourcePath = SelectedSource.Url }
                    : new WebImageSource { SourcePath = SelectedSource.Url };

                var images = await source.GetAvailableImages();
                foreach (var img in images)
                    Results.Add(img);

                StatusMessage = images.Count == 0
                    ? "No images found. Pinterest search pages often need login — board URLs (pinterest.com/user/board) work best."
                    : $"{images.Count} images. Click one to load it onto the canvas.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Fetch failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task SelectImageAsync(WallpaperImage image)
        {
            if (IsBusy) return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            SelectedImage = image;
            StatusMessage = $"Downloading {image.Name}...";

            try
            {
                var path = await _orchestrator.Downloader.DownloadImageAsync(image, _cts.Token);
                _previewImagePath = path;

                var bmp = await Task.Run(() =>
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(path, UriKind.Absolute);
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    return b;
                }, _cts.Token);

                PreviewBitmap = bmp;
                ResetLayout();
                StatusMessage = $"{image.Name} — {bmp.PixelWidth}×{bmp.PixelHeight}. Drag your monitors into place.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Download cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Download failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                _cts = null;
            }
        }

        // ── Layout ───────────────────────────────────────────────────────────

        /// <summary>Rebuild regions to the default arrangement at the current coverage.</summary>
        public void ResetLayout()
        {
            Regions.Clear();
            if (!HasPreview) return;

            var regions = WallpaperCompositionService.BuildDefaultLayout(
                ImagePixelWidth, ImagePixelHeight, _monitors, CoverageScale);

            foreach (var r in regions)
            {
                var vm = new MonitorRegionViewModel(r.Monitor)
                {
                    X = r.X, Y = r.Y, Width = r.Width, Height = r.Height
                };
                Regions.Add(vm);
            }
            OnPropertyChanged(nameof(QualityHint));
        }

        private void RescaleRegions(double oldCoverage, double newCoverage)
        {
            if (!HasPreview || Regions.Count == 0 || oldCoverage <= 0) return;

            double ratio = newCoverage / oldCoverage;
            foreach (var r in Regions)
            {
                double cx = r.X + r.Width / 2.0;
                double cy = r.Y + r.Height / 2.0;
                r.Width *= ratio;
                r.Height *= ratio;
                r.X = cx - r.Width / 2.0;
                r.Y = cy - r.Height / 2.0;
                ClampRegion(r);
            }
        }

        /// <summary>Keep a region fully inside the image. Called by the view during drag.</summary>
        public void ClampRegion(MonitorRegionViewModel region)
        {
            if (!HasPreview) return;
            region.X = Math.Clamp(region.X, 0, Math.Max(0, ImagePixelWidth - region.Width));
            region.Y = Math.Clamp(region.Y, 0, Math.Max(0, ImagePixelHeight - region.Height));
        }

        public void NotifyRegionMoved() => OnPropertyChanged(nameof(QualityHint));

        // ── Apply ────────────────────────────────────────────────────────────

        public async Task ApplyLayoutAsync()
        {
            if (_previewImagePath == null) { StatusMessage = "Pick an image first."; return; }
            if (Regions.Count == 0) { StatusMessage = "No monitor regions to apply."; return; }
            if (IsBusy) return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<WallpaperProgress>(p =>
            {
                StatusMessage = p.Status;
                ProgressPercent = p.Percent;
            });

            try
            {
                var outcome = await _composition.ComposeAndApplyAsync(
                    _previewImagePath,
                    Regions.Select(r => r.ToRegion()).ToList(),
                    UpscaleEnabled, _cts.Token, progress);

                StatusMessage = outcome.Warnings.Count > 0
                    ? $"Applied to {outcome.AppliedCount} monitors. {outcome.Warnings[0]}"
                    : $"Applied to {outcome.AppliedCount} monitors" +
                      (outcome.UpscaleUsed ? " (AI upscaled)." : ".");
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Apply cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Apply failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                ProgressPercent = 0;
                _cts = null;
            }
        }

        /// <summary>Classic mode: same image, Fill, on every monitor.</summary>
        public void ApplySameOnAll()
        {
            if (_previewImagePath == null) { StatusMessage = "Pick an image first."; return; }

            try
            {
                _orchestrator.ApplyWallpaper(_previewImagePath);
                StatusMessage = "Applied to all monitors (same image).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Apply failed: {ex.Message}";
            }
        }

        // ── Independent mode: a different wallpaper on each monitor ──────────

        public bool HasAnyAssignment => MonitorSlots.Any(s => s.HasImage);

        /// <summary>Pin the currently loaded image to one monitor. Repeat with other
        /// images to give each screen its own wallpaper.</summary>
        public void AssignSelectedToSlot(MonitorSlotViewModel slot)
        {
            if (_previewImagePath == null)
            {
                StatusMessage = "Pick an image first, then click a monitor to assign it.";
                return;
            }

            slot.ImagePath = _previewImagePath;
            slot.Thumbnail = PreviewBitmap;
            OnPropertyChanged(nameof(HasAnyAssignment));
            StatusMessage = $"{slot.Label} ← {Path.GetFileName(_previewImagePath)}. " +
                            "Assign the others, then Apply Per-Monitor.";
        }

        public void ClearSlot(MonitorSlotViewModel slot)
        {
            slot.ImagePath = null;
            slot.Thumbnail = null;
            OnPropertyChanged(nameof(HasAnyAssignment));
            StatusMessage = $"{slot.Label} cleared.";
        }

        /// <summary>Apply each slot's own image to its own monitor. Unassigned monitors
        /// are left exactly as they are.</summary>
        public async Task ApplyPerMonitorAsync()
        {
            var assigned = MonitorSlots.Where(s => s.HasImage).ToList();
            if (assigned.Count == 0)
            {
                StatusMessage = "No monitors assigned. Click an image, then click a monitor slot.";
                return;
            }
            if (IsBusy) return;

            IsBusy = true;
            _cts = new CancellationTokenSource();
            int applied = 0;
            bool upscaled = false;

            try
            {
                for (int i = 0; i < assigned.Count; i++)
                {
                    var slot = assigned[i];
                    _cts.Token.ThrowIfCancellationRequested();

                    ProgressPercent = (int)(i / (double)assigned.Count * 100);
                    StatusMessage = $"Applying to {slot.Label}...";

                    var path = slot.ImagePath!;
                    if (UpscaleEnabled)
                    {
                        var better = await TryUpscaleForMonitorAsync(path, slot.Monitor, _cts.Token);
                        if (!string.Equals(better, path, StringComparison.OrdinalIgnoreCase))
                        {
                            path = better;
                            upscaled = true;
                        }
                    }

                    _orchestrator.ApplyWallpaperToMonitor(path, slot.Monitor);
                    applied++;
                }

                StatusMessage = $"Applied {applied} monitor{(applied == 1 ? "" : "s")} independently" +
                                (upscaled ? " (AI upscaled)." : ".");
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Apply cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Apply failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                ProgressPercent = 0;
                _cts = null;
            }
        }

        /// <summary>Upscale to this monitor's native res. Never fatal: if the ONNX model is
        /// absent or the upscale fails, fall back to the original image.</summary>
        private async Task<string> TryUpscaleForMonitorAsync(
            string path, MonitorProfile monitor, CancellationToken ct)
        {
            try
            {
                var inner = new Progress<int>(pct => ProgressPercent = pct);
                return await _orchestrator.Upscaler.UpscaleImageAsync(
                    path, monitor.Width, monitor.Height, ct, inner);
            }
            catch (OperationCanceledException) { throw; }
            catch { return path; }
        }

        public void Cancel() => _cts?.Cancel();

        // ── Rotation ─────────────────────────────────────────────────────────

        public void ToggleRotation()
        {
            if (RotationRunning)
            {
                _scheduler.Stop();
                RotationRunning = false;
                StatusMessage = "Rotation stopped.";
                return;
            }

            if (SelectedSource == null) { StatusMessage = "Select a source to rotate from."; return; }
            if (SelectedSource.Type == SourceType.LocalFolder)
            {
                StatusMessage = "Rotation currently supports web sources — pick a URL source.";
                return;
            }

            _scheduler.Start(SelectedSource, RotationMinutes);
            RotationRunning = true;
        }

        public void Dispose()
        {
            _scheduler.Dispose();
            _orchestrator.Dispose();
            _cts?.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
