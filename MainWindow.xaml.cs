using System.Windows;
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
            if (dialog.ShowDialog() == true)
            {
                ViewModel.ScanRoot = dialog.FolderName;
            }
        }

        private void ApplyFolderIcon_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyFolderIcon();
        private void ApplyDriveIcon_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyDriveIcon();
        private void ApplyShellIcon_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyShellIcon();
        private void UndoLastChange_Click(object sender, RoutedEventArgs e) => ViewModel.UndoLastChange();
        private void RestoreShellDefault_Click(object sender, RoutedEventArgs e) => ViewModel.RestoreSelectedShellDefault();

        private async void RunAutoMatch_Click(object sender, RoutedEventArgs e) => await ViewModel.RunAutoMatchAsync();
        private void CancelAutoMatch_Click(object sender, RoutedEventArgs e) => ViewModel.CancelAutoMatch();
    }
}
