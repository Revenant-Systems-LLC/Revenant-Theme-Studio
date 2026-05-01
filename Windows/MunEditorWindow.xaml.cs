using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Revenant_Theme_Studio.Models;
using Revenant_Theme_Studio.Services;
using Revenant_Theme_Studio.ViewModels;

namespace Revenant_Theme_Studio.Windows
{
    public partial class MunEditorWindow : Window
    {
        private readonly MunEditorViewModel _vm;

        public MunEditorWindow(string munPath)
        {
            InitializeComponent();
            _vm        = new MunEditorViewModel(munPath);
            DataContext = _vm;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.LoadAsync();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: MunIconEntry entry }) return;

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            var dlg = new SaveFileDialog
            {
                Title            = "Export icon as .ico",
                FileName         = entry.ExportFileName,
                DefaultExt       = ".ico",
                Filter           = "Icon files (*.ico)|*.ico",
                InitialDirectory = desktopPath
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllBytes(dlg.FileName, entry.OriginalIcoBytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed:\n{ex.Message}",
                    "Export error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: MunIconEntry entry }) return;

            var dlg = new OpenFileDialog
            {
                Title      = $"Replace {entry.DisplayName}",
                Filter     = "Icon and image files (*.ico;*.png)|*.ico;*.png|Icon files (*.ico)|*.ico|PNG files (*.png)|*.png",
                DefaultExt = ".ico"
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var result = IcoFileBuilder.ValidateAndNormalize(dlg.FileName);

                if (result.Warning != null)
                {
                    var proceed = MessageBox.Show(this,
                        $"{result.Warning}\n\nProceed?",
                        "Quality warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (proceed != MessageBoxResult.Yes) return;
                }

                entry.ReplacementBytes      = result.IcoBytes;
                entry.ReplacementSourcePath = dlg.FileName;
                entry.Thumbnail             = BuildThumbnail(result.IcoBytes);
            }
            catch (InvalidDataException ex)
            {
                MessageBox.Show(this, ex.Message,
                    "Invalid file", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Replace failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static BitmapSource? BuildThumbnail(byte[] icoBytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource     = new MemoryStream(icoBytes);
                bmp.DecodePixelWidth = 64;
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var staged = _vm.Entries
                .Where(x => x.HasReplacement)
                .Select(x => (x.GroupId, x.ReplacementBytes!))
                .ToList();

            if (staged.Count == 0) return;

            var confirm = MessageBox.Show(this,
                $"Apply {staged.Count} replacement(s) to imageres.dll.mun?\n\n" +
                "RTS will back up the original first. A UAC prompt will appear.",
                "Apply changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // Backup
            string backupPath;
            try
            {
                var backup = new MunBackupService();
                backupPath = backup.Backup(_vm.MunPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Backup failed:\n{ex.Message}",
                    "Backup error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Commit via elevated helper
            var client  = new MunWriterClient();
            var request = new MunCommitRequest(_vm.MunPath, staged);
            var (success, message) = client.Commit(request);

            if (!success)
            {
                MessageBox.Show(this, $"{message}\n\nBackup is safe at:\n{backupPath}",
                    "Apply failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Record to history
            var history = new ChangeHistoryService(new ManagedStorageService());
            history.Record(new Revenant_Theme_Studio.Models.ChangeRecord
            {
                BackupId      = Guid.NewGuid().ToString("N"),
                TargetType    = Revenant_Theme_Studio.Models.IconTargetType.MunFile,
                TargetPath    = _vm.MunPath,
                PreviousValue = backupPath,
                NewValue      = $"{staged.Count} group(s) replaced",
                Timestamp     = DateTimeOffset.Now
            });

            // Offer Explorer restart
            var restart = MessageBox.Show(this,
                "Changes applied.\n\nRestart Explorer now to see the new icons?",
                "Success", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (restart == MessageBoxResult.Yes)
                RestartExplorer();

            Close();
        }

        private static void RestartExplorer()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "taskkill", "/f /im explorer.exe")
                { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit();

                System.Diagnostics.Process.Start("explorer.exe");
            }
            catch { /* non-fatal */ }
        }
    }
}
