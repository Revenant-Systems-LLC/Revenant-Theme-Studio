using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Revenant_Theme_Studio.Features.Personalization.Services;
using Revenant_Theme_Studio.Features.Wallpaper;
using Revenant_Theme_Studio.Features.Wallpaper.Services;

namespace Revenant_Theme_Studio.Features.Personalization.ViewModels
{
    /// <summary>One monitor tile on the arrangement canvas.</summary>
    public class DisplayMonitorItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public MonitorProfile Profile { get; }
        public string Label { get; }

        public DisplayMonitorItemViewModel(MonitorProfile profile)
        {
            Profile = profile;
            Label = $"{profile.MonitorId + 1}";
        }

        // Canvas-space placement, set by the parent VM (virtual-desktop coords shifted positive).
        public double CanvasX { get; set; }
        public double CanvasY { get; set; }
        public double CanvasW { get; set; }
        public double CanvasH { get; set; }

        public string Summary => $"{Profile.Width} × {Profile.Height}";

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class DisplayViewModel : INotifyPropertyChanged, IDisposable
    {
        private const int RevertCountdownSeconds = 15;

        private readonly DisplayModeService _service = new();
        private readonly DisplayDetectionService _detection = new();
        private readonly DispatcherTimer _revertTimer;

        private DisplayMonitorItemViewModel? _selectedMonitor;
        private string? _selectedResolution;
        private int _selectedRate;
        private DisplayOrientation _selectedOrientation;
        private string _statusMessage = "Select a display, pick a mode, apply.";
        private string _scaleText = string.Empty;
        private bool _pendingRevert;
        private int _revertSecondsLeft;
        private string? _pendingDevice;
        private bool _suppressModeReload;

        private List<DisplayMode> _allModes = new();

        public ObservableCollection<DisplayMonitorItemViewModel> Monitors { get; } = new();
        public ObservableCollection<string> Resolutions { get; } = new();
        public ObservableCollection<int> Rates { get; } = new();
        public IReadOnlyList<DisplayOrientation> Orientations { get; } =
            new[] { DisplayOrientation.Landscape, DisplayOrientation.Portrait,
                    DisplayOrientation.LandscapeFlipped, DisplayOrientation.PortraitFlipped };

        // Canvas-space bounds of the whole arrangement (for the Viewbox host grid).
        public double ArrangementWidth { get; private set; }
        public double ArrangementHeight { get; private set; }

        public event Action? IdentifyRequested;

        public DisplayViewModel()
        {
            _revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _revertTimer.Tick += RevertTimer_Tick;
            RefreshMonitors();
        }

        // ── Bindables ────────────────────────────────────────────────────────

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string ScaleText
        {
            get => _scaleText;
            set { _scaleText = value; OnPropertyChanged(); }
        }

        public DisplayMonitorItemViewModel? SelectedMonitor
        {
            get => _selectedMonitor;
            set
            {
                if (_selectedMonitor != null) _selectedMonitor.IsSelected = false;
                _selectedMonitor = value;
                if (_selectedMonitor != null) _selectedMonitor.IsSelected = true;
                OnPropertyChanged();
                LoadModesForSelection();
            }
        }

        public string? SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                _selectedResolution = value;
                OnPropertyChanged();
                if (!_suppressModeReload) LoadRatesForResolution();
            }
        }

        public int SelectedRate
        {
            get => _selectedRate;
            set { _selectedRate = value; OnPropertyChanged(); }
        }

        public DisplayOrientation SelectedOrientation
        {
            get => _selectedOrientation;
            set { _selectedOrientation = value; OnPropertyChanged(); }
        }

        public bool PendingRevert
        {
            get => _pendingRevert;
            set { _pendingRevert = value; OnPropertyChanged(); }
        }

        public int RevertSecondsLeft
        {
            get => _revertSecondsLeft;
            set
            {
                _revertSecondsLeft = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RevertPrompt));
            }
        }

        public string RevertPrompt =>
            $"Keep these display settings? Reverting in {RevertSecondsLeft}s...";

        // ── Monitor enumeration + arrangement canvas ─────────────────────────

        public void RefreshMonitors()
        {
            var keepDevice = SelectedMonitor?.Profile.DeviceName;

            Monitors.Clear();
            var profiles = _detection.GetMonitorConfiguration();
            if (profiles.Count == 0) profiles.Add(_detection.GetPrimaryMonitor());

            int minX = profiles.Min(m => m.PositionX);
            int minY = profiles.Min(m => m.PositionY);
            ArrangementWidth = profiles.Max(m => m.PositionX + m.Width) - minX;
            ArrangementHeight = profiles.Max(m => m.PositionY + m.Height) - minY;
            OnPropertyChanged(nameof(ArrangementWidth));
            OnPropertyChanged(nameof(ArrangementHeight));

            foreach (var p in profiles)
            {
                Monitors.Add(new DisplayMonitorItemViewModel(p)
                {
                    CanvasX = p.PositionX - minX,
                    CanvasY = p.PositionY - minY,
                    CanvasW = p.Width,
                    CanvasH = p.Height
                });
            }

            SelectedMonitor = Monitors.FirstOrDefault(m => m.Profile.DeviceName == keepDevice)
                              ?? Monitors.FirstOrDefault();
        }

        private void LoadModesForSelection()
        {
            Resolutions.Clear();
            Rates.Clear();
            if (SelectedMonitor == null) return;

            var device = SelectedMonitor.Profile.DeviceName;
            _allModes = _service.GetModes(device);
            var current = _service.GetCurrentMode(device);

            _suppressModeReload = true;
            foreach (var res in _allModes.Select(m => m.Resolution).Distinct())
                Resolutions.Add(res);
            _suppressModeReload = false;

            SelectedResolution = current != null && Resolutions.Contains(current.Resolution)
                ? current.Resolution
                : Resolutions.FirstOrDefault();

            if (current != null && Rates.Contains(current.RefreshHz))
                SelectedRate = current.RefreshHz;

            SelectedOrientation = _service.GetCurrentOrientation(device);

            var center = SelectedMonitor.Profile;
            ScaleText = $"{DisplayModeService.GetScalePercent(
                center.PositionX + center.Width / 2,
                center.PositionY + center.Height / 2)}%";

            StatusMessage = current != null
                ? $"Display {SelectedMonitor.Label}: currently {current}"
                : $"Display {SelectedMonitor.Label}: current mode unknown";
        }

        private void LoadRatesForResolution()
        {
            Rates.Clear();
            if (SelectedResolution == null) return;

            var rates = _allModes
                .Where(m => m.Resolution == SelectedResolution)
                .Select(m => m.RefreshHz)
                .Distinct()
                .OrderByDescending(r => r)
                .ToList();

            foreach (var r in rates) Rates.Add(r);
            if (rates.Count > 0) SelectedRate = rates[0];
        }

        public void Identify() => IdentifyRequested?.Invoke();

        // ── Apply / keep / revert ────────────────────────────────────────────

        public void ApplySelectedMode()
        {
            if (SelectedMonitor == null || SelectedResolution == null)
            {
                StatusMessage = "Select a display and resolution first.";
                return;
            }
            if (PendingRevert)
            {
                StatusMessage = "Settle the pending change first (Keep or Revert).";
                return;
            }

            var mode = _allModes.FirstOrDefault(m =>
                m.Resolution == SelectedResolution && m.RefreshHz == SelectedRate);
            if (mode == null)
            {
                StatusMessage = "That resolution/rate combination isn't available.";
                return;
            }

            var device = SelectedMonitor.Profile.DeviceName;
            var result = _service.ApplyDynamic(device, mode, SelectedOrientation);

            if (!result.Success)
            {
                StatusMessage = result.Message;
                return;
            }

            _pendingDevice = device;
            RevertSecondsLeft = RevertCountdownSeconds;
            PendingRevert = true;
            _revertTimer.Start();
            StatusMessage = $"Applied {mode} — confirm to keep it.";
        }

        public void KeepChanges()
        {
            _revertTimer.Stop();
            PendingRevert = false;

            if (_pendingDevice != null)
            {
                var result = _service.Confirm(_pendingDevice);
                StatusMessage = result.Success
                    ? "Display settings saved."
                    : result.Message;
                _pendingDevice = null;
            }
            RefreshMonitors();
        }

        public void RevertNow()
        {
            _revertTimer.Stop();
            PendingRevert = false;

            if (_pendingDevice != null)
            {
                var result = _service.Revert(_pendingDevice);
                StatusMessage = result.Success ? "Reverted." : result.Message;
                _pendingDevice = null;
            }
            RefreshMonitors();
        }

        private void RevertTimer_Tick(object? sender, EventArgs e)
        {
            RevertSecondsLeft--;
            if (RevertSecondsLeft <= 0) RevertNow();
        }

        public void Dispose() => _revertTimer.Stop();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
