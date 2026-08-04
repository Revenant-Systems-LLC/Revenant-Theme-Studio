using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Revenant_Theme_Studio.Features.Wallpaper;
using Revenant_Theme_Studio.Features.Wallpaper.Services;
using Revenant_Theme_Studio.Features.Wallpaper.ViewModels;

namespace Revenant_Theme_Studio.Features.Wallpaper.Views
{
    public partial class WallpaperView : UserControl
    {
        private MonitorRegionViewModel? _dragRegion;
        private Point _dragOffset;

        public WallpaperView()
        {
            InitializeComponent();
        }

        private WallpaperViewModel? VM => DataContext as WallpaperViewModel;

        // ── Sources ──────────────────────────────────────────────────────────

        private void AddUrlSource_Click(object sender, RoutedEventArgs e) => VM?.AddUrlSource();

        private void NewSourceUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) VM?.AddUrlSource();
        }

        private void AddFolderSource_Click(object sender, RoutedEventArgs e)
        {
            if (VM == null) return;
            var dialog = new OpenFolderDialog { Title = "Select Wallpaper Folder" };
            if (dialog.ShowDialog() == true)
                VM.AddFolderSource(dialog.FolderName);
        }

        private void RemoveSource_Click(object sender, RoutedEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: SavedSource source })
                VM.RemoveSource(source);
        }

        private async void Fetch_Click(object sender, RoutedEventArgs e)
        {
            if (VM != null) await VM.FetchImagesAsync();
        }

        private async void Thumb_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: WallpaperImage image })
                await VM.SelectImageAsync(image);
        }

        // ── Monitor region drag ──────────────────────────────────────────────
        // PixelGrid lives in image-pixel space inside the Viewbox, so GetPosition
        // against it returns image-pixel coordinates directly — no scale math here.

        private void Region_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (VM == null ||
                sender is not FrameworkElement { DataContext: MonitorRegionViewModel region } fe)
                return;

            _dragRegion = region;
            var p = e.GetPosition(PixelGrid);
            _dragOffset = new Point(p.X - region.X, p.Y - region.Y);
            fe.CaptureMouse();
            e.Handled = true;
        }

        private void Region_MouseMove(object sender, MouseEventArgs e)
        {
            if (VM == null || _dragRegion == null ||
                sender is not FrameworkElement fe || !fe.IsMouseCaptured)
                return;

            var p = e.GetPosition(PixelGrid);
            _dragRegion.X = p.X - _dragOffset.X;
            _dragRegion.Y = p.Y - _dragOffset.Y;
            VM.ClampRegion(_dragRegion);
        }

        private void Region_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsMouseCaptured)
                fe.ReleaseMouseCapture();

            if (_dragRegion != null)
            {
                VM?.NotifyRegionMoved();
                _dragRegion = null;
            }
        }

        // ── Layout / apply ───────────────────────────────────────────────────

        private void ResetLayout_Click(object sender, RoutedEventArgs e) => VM?.ResetLayout();

        private async void ApplyLayout_Click(object sender, RoutedEventArgs e)
        {
            if (VM != null) await VM.ApplyLayoutAsync();
        }

        private void ApplySame_Click(object sender, RoutedEventArgs e) => VM?.ApplySameOnAll();

        // ── Per-monitor (independent) ────────────────────────────────────────

        private void MonitorSlot_Click(object sender, MouseButtonEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: MonitorSlotViewModel slot })
                VM.AssignSelectedToSlot(slot);
        }

        private void ClearSlot_Click(object sender, RoutedEventArgs e)
        {
            if (VM != null && sender is FrameworkElement { DataContext: MonitorSlotViewModel slot })
                VM.ClearSlot(slot);
        }

        private async void ApplyPerMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (VM != null) await VM.ApplyPerMonitorAsync();
        }

        private void Rotation_Click(object sender, RoutedEventArgs e) => VM?.ToggleRotation();

        private void Cancel_Click(object sender, RoutedEventArgs e) => VM?.Cancel();
    }
}
