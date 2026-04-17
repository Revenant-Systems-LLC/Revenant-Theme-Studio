using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using Revenant_Theme_Studio.Services;
using Revenant_Theme_Studio.ViewModels;

namespace Revenant_Theme_Studio
{
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainWindow()
        {
            InitializeComponent();
        }

        // ── Consent gate ─────────────────────────────────────────────────────
        private bool EnsureConsent()
        {
            if (ConsentService.Instance.HasConsented) return true;
            var dlg = new ConsentDialog { Owner = this };
            return dlg.ShowDialog() == true;
        }

        // ── Window lifecycle ─────────────────────────────────────────────────
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            ViewModel.ArchiveIconsToProgramData();
        }

        // ── Browse handlers ──────────────────────────────────────────────────
        private void BrowseIconFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Icon Library Folder" };
            if (dialog.ShowDialog() == true) ViewModel.IconFolder = dialog.FolderName;
        }

        private void BrowseTargetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder" };
            if (dialog.ShowDialog() == true) ViewModel.SelectedFolder = dialog.FolderName;
        }

        private void BrowseSystemResource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select System Icon Resource",
                Filter = "MUI Resource (*.mun;*.dll)|*.mun;*.dll|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.SystemResourcePath = dialog.FileName;
                ViewModel.ReloadIcons();
            }
        }

        private void BrowseScanRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Auto Match Root" };
            if (dialog.ShowDialog() == true) ViewModel.ScanRoot = dialog.FolderName;
        }

        // ── Action handlers ──────────────────────────────────────────────────
        private void ApplyFolderIcon_Click(object sender, RoutedEventArgs e)
            => ViewModel.ApplyFolderIcon();

        private void ApplyDriveIcon_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConsent()) return;
            ViewModel.ApplyDriveIcon();
        }

        private void ApplyShellIcon_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConsent()) return;
            ViewModel.ApplyShellIcon();
        }

        private void UndoLastChange_Click(object sender, RoutedEventArgs e)
            => ViewModel.UndoLastChange();

        private void RestoreShellDefault_Click(object sender, RoutedEventArgs e)
            => ViewModel.RestoreSelectedShellDefault();

        private void CancelAutoMatch_Click(object sender, RoutedEventArgs e)
            => ViewModel.CancelAutoMatch();

        private void UnlockPro_Click(object sender, RoutedEventArgs e)
        {
            var win = new LicenseKeyWindow { Owner = this };
            if (win.ShowDialog() == true)
                ViewModel.NotifyLicenseActivated();
        }

        private async void RunAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            var scanRoot = ViewModel.ScanRoot;
            if (string.IsNullOrWhiteSpace(scanRoot)) { await ViewModel.RunAutoMatchAsync(); return; }

            var result = MessageBox.Show(
                $"Scan root:\n{scanRoot}\n\nOnly strong matches will be applied. Unmatched folders are skipped.\n\nContinue?",
                "Run Auto Match", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                await ViewModel.RunAutoMatchAsync();
        }
    }
}
