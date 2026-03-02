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
                ViewModel.IconFolder = dialog.FolderName;
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
                ScanRootBox.Text = dialog.FolderName;
        }

        private void ApplyManual_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ApplyManual();
        }

        private void RunAutoMatch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RunAutoMatch(ScanRootBox.Text);
        }
    }
}
