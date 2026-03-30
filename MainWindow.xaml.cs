using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Revenant_Theme_Studio.ViewModels;

namespace Revenant_Theme_Studio
{
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
            Closed += OnClosed;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            ViewModel.Dispose();
        }

        private void BrowseIconFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Icon Library Folder" };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.IconFolder = dialog.FolderName;
            }
        }

        private void BrowseTargetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder" };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.SelectedFolder = dialog.FolderName;
            }
        }

        private void BrowseWallpaperSource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Wallpaper Source Folder" };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.Wallpaper.LocalSourcePath = dialog.FolderName;
            }
        }

        private void ApplyFolderIcon_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyFolderIcon();
        private void UndoLastChange_Click(object sender, RoutedEventArgs e) => ViewModel.UndoLastChange();

        private void RefreshDisplays_Click(object sender, RoutedEventArgs e) => ViewModel.Wallpaper.RefreshDisplays();
        private void AssignWallpaperSource_Click(object sender, RoutedEventArgs e) => ViewModel.Wallpaper.AssignLocalFolderToSelectedSlot();
        private void RunRotationTick_Click(object sender, RoutedEventArgs e) => ViewModel.Wallpaper.RunRotationTick();

        private void DisplayThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb || thumb.DataContext is not DisplaySlotViewModel slot) return;

            var newX = Math.Max(0, slot.X + e.HorizontalChange);
            var newY = Math.Max(0, slot.Y + e.VerticalChange);
            ViewModel.Wallpaper.UpdateSlotPosition(slot.SlotId, newX, newY);
        }

        // New event handlers for Auto, Registry, and System Icons features

        private void BrowseScanRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Scan Root Folder" };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.ScanRoot = dialog.FolderName;
            }
        }

        private async void RunAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RunAutoMatchAsync();
        }

        private void CancelAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelAutoMatch();
        }

        private void ApplyDriveIcon_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyDriveIcon();
        }

        private void ApplyShellIcon_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyShellIcon();
        }

        private void RestoreShellIcon_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RestoreSelectedShellDefault();
        }
    }
}
