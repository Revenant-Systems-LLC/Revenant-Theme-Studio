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
                ViewModel.ReloadIcons();
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

        private void ReloadIconLibrary_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ReloadIcons();
        }

        private void BrowseTargetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Target Folder" };
            if (dialog.ShowDialog() == true)
                ViewModel.SelectedFolder = dialog.FolderName;
        }

        private void BrowseScanRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Scan Root" };
            if (dialog.ShowDialog() == true)
                ViewModel.ScanRoot = dialog.FolderName;
        }

        private void ApplyManual_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyManual();
        }

        private async void RunAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RunAutoMatchAsync();
        }

        private void CancelAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelAutoMatch();
        }

        private void IntroNavigate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tagValue })
                return;

            if (!int.TryParse(tagValue, out var tabIndex))
                return;

            MainTabs.SelectedIndex = tabIndex;
        }

        private void AcceptWarning_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.StatusMessage = "Warning accepted. Configure auto mode and run when ready.";
            MainTabs.SelectedIndex = 1;
        }

        private void DenyWarning_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.CancelAutoMatch();
            ViewModel.StatusMessage = "Auto/registry flow denied.";
            MainTabs.SelectedIndex = 0;
        }
    }
}
