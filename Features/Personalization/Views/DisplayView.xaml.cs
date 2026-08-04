using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Revenant_Theme_Studio.Features.Personalization.ViewModels;

namespace Revenant_Theme_Studio.Features.Personalization.Views
{
    public partial class DisplayView : UserControl
    {
        public DisplayView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is DisplayViewModel oldVm) oldVm.IdentifyRequested -= ShowIdentifyOverlays;
                if (e.NewValue is DisplayViewModel newVm) newVm.IdentifyRequested += ShowIdentifyOverlays;
            };
        }

        private DisplayViewModel? VM => DataContext as DisplayViewModel;

        private void MonitorTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: DisplayMonitorItemViewModel item })
                VM.SelectedMonitor = item;
        }

        private void Identify_Click(object sender, RoutedEventArgs e) => VM?.Identify();
        private void Rescan_Click(object sender, RoutedEventArgs e) => VM?.RefreshMonitors();
        private void Apply_Click(object sender, RoutedEventArgs e) => VM?.ApplySelectedMode();
        private void Keep_Click(object sender, RoutedEventArgs e) => VM?.KeepChanges();
        private void Revert_Click(object sender, RoutedEventArgs e) => VM?.RevertNow();

        /// <summary>Flash a big number on each physical monitor, Settings-style.</summary>
        private void ShowIdentifyOverlays()
        {
            if (VM == null) return;
            foreach (var monitor in VM.Monitors)
            {
                var overlay = new IdentifyOverlayWindow(monitor.Label,
                    monitor.Profile.PositionX, monitor.Profile.PositionY);
                overlay.Show();
            }
        }
    }

    /// <summary>
    /// Borderless, click-through-irrelevant flash window showing the monitor number.
    /// Positioned with SetWindowPos in raw physical pixels so per-monitor DPI
    /// differences can't drift it onto the wrong screen.
    /// </summary>
    internal sealed class IdentifyOverlayWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly int _px;
        private readonly int _py;

        public IdentifyOverlayWindow(string label, int physicalX, int physicalY)
        {
            _px = physicalX + 40;
            _py = physicalY + 40;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x14, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x96, 0x2B)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(34, 14, 34, 14),
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 96,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x96, 0x2B))
                }
            };

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hwnd, IntPtr.Zero, _px, _py, 0, 0,
                    SWP_NOZORDER | SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }
}
