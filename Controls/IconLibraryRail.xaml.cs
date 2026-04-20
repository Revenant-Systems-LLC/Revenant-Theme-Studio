using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Revenant_Theme_Studio.ViewModels;

namespace Revenant_Theme_Studio.Controls
{
    /// <summary>
    /// Reusable per-tab icon picker. Drops onto any tab and binds to a
    /// <see cref="TabIconPickerViewModel"/> as its DataContext, giving that
    /// tab an independent IconFolder / IconList / SelectedIcon. Three live
    /// instances on MainViewModel keep Folder, Drive, and System tabs from
    /// stomping on each other's selection.
    /// </summary>
    public partial class IconLibraryRail : UserControl
    {
        public IconLibraryRail()
        {
            InitializeComponent();
        }

        private TabIconPickerViewModel? Vm => DataContext as TabIconPickerViewModel;

        private void BrowseIconFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Vm is null) return;
            var dialog = new OpenFolderDialog { Title = "Select Icon Library Folder" };
            if (dialog.ShowDialog() == true) Vm.IconFolder = dialog.FolderName;
        }

        private void BrowseSystemResource_Click(object sender, RoutedEventArgs e)
        {
            if (Vm is null) return;
            var dialog = new OpenFileDialog
            {
                Title = "Select System Icon Resource",
                Filter = "MUI Resource (*.mun;*.dll)|*.mun;*.dll|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                Vm.SystemResourcePath = dialog.FileName;
                Vm.ReloadIcons();
            }
        }
    }
}
