using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Revenant_Theme_Studio.Features.Personalization.Services;

namespace Revenant_Theme_Studio.Features.Personalization.ViewModels
{
    public class AccentSwatch
    {
        public Color Color { get; init; }
        public Brush Brush => new SolidColorBrush(Color);
        public string Hex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
    }

    /// <summary>
    /// Windows Settings > Personalization > Colors, live-apply. Every setter goes
    /// through the consent gate (the view wires it to the standard RTS dialog);
    /// a declined gate re-reads real state so the UI never lies.
    /// </summary>
    public class ColorsViewModel : INotifyPropertyChanged
    {
        // The Windows Settings accent palette, row-major as Settings lays it out.
        private static readonly string[] WindowsPalette =
        {
            "ffb900","ff8c00","f7630c","ca5010","da3b01","ef6950","d13438","ff4343",
            "e74856","e81123","ea005e","c30052","e3008c","bf0077","c239b3","9a0089",
            "0078d7","0063b1","8e8cd8","6b69d6","8764b8","744da9","b146c2","881798",
            "0099bc","2d7d9a","00b7c3","038387","00b294","018574","00cc6a","10893e",
            "7a7574","5d5a58","68768a","515c6b","567c73","486860","498205","107c10",
            "767676","4c4a48","69797e","4a5459","647c64","525e54","847545","7e735f"
        };

        private readonly WindowsPersonalizationService _service = new();

        private ColorMode _mode;
        private bool _transparency;
        private AccentSwatch? _selectedSwatch;
        private bool _accentOnStartTaskbar;
        private bool _accentOnTitleBars;
        private string _customHex = string.Empty;
        private string _statusMessage = "Changes apply live, same as Windows Settings.";
        private bool _loading;

        /// <summary>Wired by the view to the standard RTS consent dialog.</summary>
        public Func<bool>? ConsentGate { get; set; }

        public ObservableCollection<AccentSwatch> Swatches { get; } = new();

        public ColorsViewModel()
        {
            foreach (var hex in WindowsPalette)
                Swatches.Add(new AccentSwatch { Color = ParseHex(hex)!.Value });
            LoadFromSystem();
        }

        public void LoadFromSystem()
        {
            _loading = true;
            _mode = _service.GetColorMode();
            _transparency = _service.GetTransparency();
            _accentOnStartTaskbar = _service.GetAccentOnStartTaskbar();
            _accentOnTitleBars = _service.GetAccentOnTitleBars();

            var accent = _service.GetAccentColor();
            _selectedSwatch = Swatches.FirstOrDefault(s => s.Color == accent);
            _customHex = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
            _loading = false;

            OnPropertyChanged(string.Empty);
        }

        // ── Bindables (live apply) ───────────────────────────────────────────

        public IReadOnlyList<ColorMode> Modes { get; } = new[] { ColorMode.Light, ColorMode.Dark };

        public ColorMode Mode
        {
            get => _mode;
            set
            {
                if (_loading || value == _mode) { _mode = value; OnPropertyChanged(); return; }
                if (!Gate()) return;
                _mode = value;
                _service.SetColorMode(value);
                OnPropertyChanged();
                StatusMessage = $"Mode set to {value}.";
            }
        }

        public bool Transparency
        {
            get => _transparency;
            set
            {
                if (_loading || value == _transparency) { _transparency = value; OnPropertyChanged(); return; }
                if (!Gate()) return;
                _transparency = value;
                _service.SetTransparency(value);
                OnPropertyChanged();
                StatusMessage = $"Transparency {(value ? "on" : "off")}.";
            }
        }

        public AccentSwatch? SelectedSwatch
        {
            get => _selectedSwatch;
            set
            {
                if (_loading || value == null) { _selectedSwatch = value; OnPropertyChanged(); return; }
                if (!Gate()) return;
                _selectedSwatch = value;
                _service.SetAccentColor(value.Color);
                _customHex = value.Hex;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CustomHex));
                StatusMessage = $"Accent set to {value.Hex}.";
            }
        }

        public string CustomHex
        {
            get => _customHex;
            set { _customHex = value; OnPropertyChanged(); }
        }

        public void ApplyCustomHex()
        {
            var color = ParseHex(CustomHex);
            if (color == null)
            {
                StatusMessage = "Enter a color as #RRGGBB.";
                return;
            }
            if (!Gate()) return;

            _service.SetAccentColor(color.Value);
            _selectedSwatch = Swatches.FirstOrDefault(s => s.Color == color.Value);
            OnPropertyChanged(nameof(SelectedSwatch));
            StatusMessage = $"Accent set to #{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}.";
        }

        public bool AccentOnStartTaskbar
        {
            get => _accentOnStartTaskbar;
            set
            {
                if (_loading || value == _accentOnStartTaskbar) { _accentOnStartTaskbar = value; OnPropertyChanged(); return; }
                if (!Gate()) return;
                _accentOnStartTaskbar = value;
                _service.SetAccentOnStartTaskbar(value);
                OnPropertyChanged();
            }
        }

        public bool AccentOnTitleBars
        {
            get => _accentOnTitleBars;
            set
            {
                if (_loading || value == _accentOnTitleBars) { _accentOnTitleBars = value; OnPropertyChanged(); return; }
                if (!Gate()) return;
                _accentOnTitleBars = value;
                _service.SetAccentOnTitleBars(value);
                OnPropertyChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        private bool Gate()
        {
            if (ConsentGate == null || ConsentGate())
                return true;

            // Declined — reload truth so the UI snaps back.
            LoadFromSystem();
            StatusMessage = "Change cancelled — consent declined.";
            return false;
        }

        private static Color? ParseHex(string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6) return null;
            try
            {
                return Color.FromRgb(
                    Convert.ToByte(hex[..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
            }
            catch { return null; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
