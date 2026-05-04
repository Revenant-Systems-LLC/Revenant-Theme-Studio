using System;
using System.Threading;
using System.Threading.Tasks;

namespace Revenant_Theme_Studio.Features.Wallpaper.Services
{
    public class WallpaperScheduler : IDisposable
    {
        private readonly WallpaperOrchestrator _orchestrator;
        private Timer? _timer;
        private CancellationTokenSource? _cts;
        private bool _running;
        private SavedSource? _activeSource;

        public bool IsRunning => _running;
        public int IntervalMinutes { get; private set; }

        public event Action<string>? StatusChanged;
        public event Action<string>? ErrorOccurred;

        public WallpaperScheduler(WallpaperOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public void Start(SavedSource source, int intervalMinutes)
        {
            if (intervalMinutes < 1) intervalMinutes = 1;

            Stop();

            _activeSource = source;
            IntervalMinutes = intervalMinutes;
            _running = true;
            _cts = new CancellationTokenSource();

            _ = RunOnceAsync(_cts.Token);

            _timer = new Timer(
                _ => _ = RunOnceAsync(_cts.Token),
                null,
                TimeSpan.FromMinutes(intervalMinutes),
                TimeSpan.FromMinutes(intervalMinutes));

            StatusChanged?.Invoke($"Scheduled every {intervalMinutes}min");
        }

        public void Stop()
        {
            _running = false;
            _cts?.Cancel();
            _timer?.Dispose();
            _timer = null;
            _cts?.Dispose();
            _cts = null;
            _activeSource = null;
        }

        public void UpdateInterval(int intervalMinutes)
        {
            if (intervalMinutes < 1) intervalMinutes = 1;
            IntervalMinutes = intervalMinutes;

            _timer?.Change(
                TimeSpan.FromMinutes(intervalMinutes),
                TimeSpan.FromMinutes(intervalMinutes));
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            if (_activeSource == null) return;

            try
            {
                StatusChanged?.Invoke("Fetching wallpaper...");

                var config = _orchestrator.GetConfig();
                var result = await _orchestrator.RunFullPipelineAsync(
                    _activeSource, config.UpscaleEnabled, ct);

                if (result.Success && result.ImagePath != null)
                {
                    _orchestrator.ApplyWallpaper(result.ImagePath);
                    StatusChanged?.Invoke($"Applied: {result.ImageName ?? "wallpaper"}");
                }
                else
                {
                    ErrorOccurred?.Invoke(result.Error ?? "Unknown error");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex.Message);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
